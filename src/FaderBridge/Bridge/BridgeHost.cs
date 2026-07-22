using Fader.Bridge.Midi;
using Fader.Bridge.Osc;

namespace Fader.Bridge;

/// <summary>
/// Wires the FaderPort to the X32 in both directions and owns the shared state
/// they disagree about: the bank window, fader-touch state, and the last value
/// this bridge sent (used to ignore the console's echo of our own change).
/// </summary>
public sealed class BridgeHost
{
    private readonly BridgeConfig _config;
    private readonly IControlSurface _surface;
    private readonly X32Client _console;
    private readonly object _stateLock = new();

    private readonly bool[] _touched;
    private readonly float[] _lastKnownFader;   // latest console value per strip
    private readonly long[] _lastSentTicks;     // when we last pushed that strip to the X32
    private int _bankOffset;                    // 0-based index of the leftmost channel

    public BridgeHost(BridgeConfig config, IControlSurface surface, X32Client console)
    {
        _config = config;
        _surface = surface;
        _console = console;

        _touched = new bool[config.StripCount];
        _lastKnownFader = new float[config.StripCount];
        _lastSentTicks = new long[config.StripCount];
    }

    public event Action<string>? Log;

    /// <summary>Console channel (1-based) currently under a given strip.</summary>
    private int ChannelFor(int strip) => _bankOffset + strip + 1;

    /// <summary>Strip showing a given console channel, or -1 if it is off-bank.</summary>
    private int StripFor(int channel)
    {
        var strip = channel - 1 - _bankOffset;
        return strip >= 0 && strip < _config.StripCount ? strip : -1;
    }

    public void Start(CancellationToken token = default)
    {
        _surface.FaderMoved += OnFaderMoved;
        _surface.FaderTouched += OnFaderTouched;
        _surface.ButtonChanged += OnButtonChanged;
        _console.MessageReceived += OnConsoleMessage;

        RefreshBank();

        if (_config.ResyncSeconds > 0)
        {
            _ = Task.Run(() => ResyncLoopAsync(token), token);
        }
    }

    /// <summary>
    /// Periodically re-pull the bank. The X32 does not reliably broadcast every
    /// parameter on a scene recall, and UDP can drop an update, so relying only
    /// on /xremote pushes leaves the surface quietly wrong. Suppressed writes
    /// (touched faders, our own echoes) mean this is safe to run continuously.
    /// </summary>
    private async Task ResyncLoopAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(_config.ResyncSeconds);

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RefreshBank();
        }
    }

    // ---------------------------------------------------------------- surface

    private void OnFaderMoved(int strip, int value14)
    {
        if (strip == McuProtocol.MasterFaderChannel)
        {
            // MCU puts the master fader on channel 8. The FaderPort emits it;
            // what it is physically wired to is still unconfirmed, so it is
            // logged rather than routed to /main/st/mix/fader. Mapping an
            // unidentified control onto the main LR bus is not a guess worth
            // making silently.
            Log?.Invoke($"master fader (MCU ch 8) = {value14} - not mapped");
            return;
        }

        if (strip >= _config.StripCount)
        {
            return;
        }

        var level = FaderScaling.McuToX32(value14);

        lock (_stateLock)
        {
            _lastKnownFader[strip] = level;
            _lastSentTicks[strip] = Environment.TickCount64;
        }

        _console.Send(new OscMessage(X32Address.Fader(ChannelFor(strip)), level));
    }

    private void OnFaderTouched(int strip, bool touched)
    {
        if (strip >= _config.StripCount)
        {
            return;
        }

        lock (_stateLock)
        {
            _touched[strip] = touched;
        }

        // On release, snap the motor to whatever the console actually holds. The
        // two can have drifted apart if the console moved while the fader was
        // under a finger and its updates were being suppressed.
        if (!touched)
        {
            float level;
            lock (_stateLock)
            {
                level = _lastKnownFader[strip];
            }
            _surface.SetFaderPosition(strip, FaderScaling.X32ToMcu(level));
        }
    }

    private void OnButtonChanged(int note, bool pressed)
    {
        if (!pressed)
        {
            return; // act on press; MCU sends a matching release we don't need
        }

        var strips = _config.StripCount;

        if (McuProtocol.TryGetStrip(note, McuProtocol.MuteBase, strips, out var strip))
        {
            // Inverted sense: /mix/on 0 = muted. Query and let the console's
            // reply drive the LED, so the surface always reflects real state.
            ToggleMute(strip);
            return;
        }

        if (McuProtocol.TryGetStrip(note, McuProtocol.SoloBase, strips, out strip))
        {
            ToggleSolo(strip);
            return;
        }

        if (McuProtocol.TryGetStrip(note, McuProtocol.SelectBase, strips, out strip))
        {
            _console.Send(new OscMessage(X32Address.SelectedIndex, ChannelFor(strip) - 1));
            return;
        }

        switch (note)
        {
            case McuProtocol.BankLeft:
                ShiftBank(-_config.StripCount);
                break;
            case McuProtocol.BankRight:
                ShiftBank(_config.StripCount);
                break;
            case McuProtocol.ChannelLeft:
                ShiftBank(-1);
                break;
            case McuProtocol.ChannelRight:
                ShiftBank(1);
                break;

            case McuProtocol.Play:
            case McuProtocol.Stop:
            case McuProtocol.Record:
            case McuProtocol.Rewind:
            case McuProtocol.FastForward:
                // Deliberately not mapped. An X32 Rack has no transport of its
                // own - the only candidate is the USB recorder, and its OSC
                // paths differ between firmware versions. Tell me what you want
                // these to do and they're a few lines each. See README.
                Log?.Invoke($"transport: {McuProtocol.Describe(note)} (not mapped - see README)");
                break;

            default:
                Log?.Invoke($"unhandled button: {McuProtocol.Describe(note)}");
                break;
        }
    }

    private void ToggleMute(int strip)
    {
        var channel = ChannelFor(strip);
        _pendingToggles[X32Address.MixOn(channel)] = true;
        _console.Query(X32Address.MixOn(channel));
    }

    private void ToggleSolo(int strip)
    {
        var channel = ChannelFor(strip);
        _pendingToggles[X32Address.Solo(channel)] = true;
        _console.Query(X32Address.Solo(channel));
    }

    /// <summary>
    /// Addresses awaiting a query reply that should be flipped rather than
    /// merely displayed. The X32 has no "toggle" verb, so a press becomes
    /// read-then-write; this marks which replies are part of that round trip.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool>
        _pendingToggles = new();

    private void ShiftBank(int delta)
    {
        lock (_stateLock)
        {
            var max = _config.X32ChannelCount - _config.StripCount;
            var next = Math.Clamp(_bankOffset + delta, 0, max);
            if (next == _bankOffset)
            {
                return;
            }
            _bankOffset = next;
        }

        Log?.Invoke($"bank -> channels {_bankOffset + 1}-{_bankOffset + _config.StripCount}");
        RefreshBank();
    }

    /// <summary>
    /// Pull every parameter the surface displays for the current window. Also
    /// how the surface recovers after a scene recall.
    /// </summary>
    private void RefreshBank()
    {
        // Which channel the console has selected, so the Select lamps are right
        // from the first second rather than only after someone presses one.
        _console.Query(X32Address.SelectedIndex);

        for (var strip = 0; strip < _config.StripCount; strip++)
        {
            var channel = ChannelFor(strip);
            _console.Query(X32Address.Fader(channel));
            _console.Query(X32Address.MixOn(channel));
            _console.Query(X32Address.Name(channel));
            _console.Query(X32Address.Solo(channel));

            if (_config.ShowChannelNumbers)
            {
                _surface.SetScribble(strip, 1, $"Ch {channel}");
            }
        }
    }

    // ---------------------------------------------------------------- console

    private void OnConsoleMessage(OscMessage message)
    {
        if (message.Address == X32Address.SelectedIndex)
        {
            if (message.Arguments.FirstOrDefault() is int index)
            {
                UpdateSelectLeds(index + 1);
            }
            return;
        }

        if (message.Address.StartsWith("/-stat/solosw/", StringComparison.Ordinal))
        {
            HandleSolo(message);
            return;
        }

        if (!X32Address.TryParseChannel(message.Address, out var channel, out var parameter))
        {
            return;
        }

        var strip = StripFor(channel);
        if (strip < 0)
        {
            return; // channel is outside the current bank window
        }

        switch (parameter)
        {
            case "mix/fader" when message.Arguments.FirstOrDefault() is float level:
                HandleFader(strip, level);
                break;

            case "mix/on" when message.Arguments.FirstOrDefault() is int on:
                HandleMixOn(channel, strip, on);
                break;

            case "config/name" when message.Arguments.FirstOrDefault() is string name:
                // A blank name means the console is using its default label.
                _surface.SetScribble(strip, 0,
                    string.IsNullOrWhiteSpace(name) ? $"Ch {channel}" : name);
                break;
        }
    }

    private void HandleFader(int strip, float level)
    {
        bool suppress;
        lock (_stateLock)
        {
            _lastKnownFader[strip] = level;

            // Two reasons to leave the motor alone: the user's hand is on it, or
            // this is the console echoing back a move we just sent.
            var sinceSent = Environment.TickCount64 - _lastSentTicks[strip];
            suppress = _touched[strip] || sinceSent < _config.EchoSuppressionMs;
        }

        if (!suppress)
        {
            _surface.SetFaderPosition(strip, FaderScaling.X32ToMcu(level));
        }
    }

    private void HandleMixOn(int channel, int strip, int on)
    {
        var address = X32Address.MixOn(channel);

        if (_pendingToggles.TryRemove(address, out _))
        {
            // Second half of a mute press: write back the opposite.
            var flipped = on == 0 ? 1 : 0;
            _console.Send(new OscMessage(address, flipped));
            _surface.SetLed(McuProtocol.MuteBase + strip, flipped == 0);
            return;
        }

        // Inverted sense: mix/on 0 means muted, which is when the lamp lights.
        _surface.SetLed(McuProtocol.MuteBase + strip, on == 0);
    }

    private void HandleSolo(OscMessage message)
    {
        var tail = message.Address[^2..];
        if (!int.TryParse(tail, out var channel) ||
            message.Arguments.FirstOrDefault() is not int state)
        {
            return;
        }

        if (_pendingToggles.TryRemove(message.Address, out _))
        {
            var flipped = state == 0 ? 1 : 0;
            _console.Send(new OscMessage(message.Address, flipped));
            state = flipped;
        }

        var strip = StripFor(channel);
        if (strip >= 0)
        {
            _surface.SetLed(McuProtocol.SoloBase + strip, state != 0);
        }
    }

    private void UpdateSelectLeds(int selectedChannel)
    {
        for (var strip = 0; strip < _config.StripCount; strip++)
        {
            _surface.SetLed(McuProtocol.SelectBase + strip, ChannelFor(strip) == selectedChannel);
        }
    }
}
