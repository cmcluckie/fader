using Fader.Bridge.Midi;
using Fader.Bridge.Osc;

namespace Fader.Bridge;

/// <summary>
/// A media-transport action from the FaderPort's transport buttons. The X32 has
/// no transport, so these drive the host's music player instead; the bridge only
/// names the intent and leaves the platform-specific action to whoever listens.
/// </summary>
public enum TransportCommand { PlayPause, Stop, Next, Previous }

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
    private Layer _layer = Layer.Channels;      // what the eight faders currently drive
    private volatile bool _displayOverride;     // something else owns the scribble strips

    /// <summary>
    /// Which set of console controls the eight physical strips drive. The
    /// FaderPort's Session Navigator has no host-visible button, but its encoder
    /// betrays the mode: in Channel mode it emits notes 48/49, in Master mode it
    /// emits channel-8 pitch bend. The bridge watches for those and flips here.
    /// </summary>
    private enum Layer { Channels, Master }

    /// <summary>In the Master layer, strip 0 is the main LR bus; strips 1-7 are mix buses 1-7.</summary>
    private const int MasterStrip = 0;

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

    /// <summary>Raised when a transport button is pressed, for the host to act on.</summary>
    public event Action<TransportCommand>? Transport;

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

        RefreshLayer();

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

            RefreshLayer();
        }
    }

    // ---------------------------------------------------------------- surface

    private void OnFaderMoved(int strip, int value14)
    {
        if (strip == McuProtocol.MasterFaderChannel)
        {
            // The Session Navigator encoder (channel-8 pitch bend) is inert: its
            // value is useless and the layer is toggled explicitly by the Record
            // button, so nothing to do here.
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

        _console.Send(new OscMessage(FaderAddressFor(strip), level));
    }

    /// <summary>The console fader address a physical strip drives in the current layer.</summary>
    private string FaderAddressFor(int strip) => _layer == Layer.Master
        ? (strip == MasterStrip ? X32Address.MainFader : X32Address.BusFader(strip))
        : X32Address.Fader(ChannelFor(strip));

    /// <summary>Switch which controls the eight faders drive, and re-pull their values.</summary>
    private void EnterLayer(Layer layer)
    {
        lock (_stateLock)
        {
            if (_layer == layer)
            {
                return;
            }
            _layer = layer;
        }

        // The Record lamp shows which layer is live: lit = Master, dark = Channels.
        _surface.SetLed(McuProtocol.Record, layer == Layer.Master);

        Log?.Invoke(layer == Layer.Master
            ? "layer -> Master (strip 1 = Main, strips 2-8 = Bus 1-7)"
            : $"layer -> Channels {_bankOffset + 1}-{_bankOffset + _config.StripCount}");
        RefreshLayer();
    }

    /// <summary>
    /// While an override is active - e.g. a now-playing marquee owns the scribble
    /// strips - the bridge stops writing its own labels. Turning it off re-pulls
    /// the current layer so the labels come straight back.
    /// </summary>
    public void SetDisplayOverride(bool on)
    {
        _displayOverride = on;
        if (!on)
        {
            RefreshLayer();
        }
    }

    /// <summary>Gated scribble write - skipped while a display override is active.</summary>
    private void Scribble(int strip, int row, string text)
    {
        if (_displayOverride)
        {
            return;
        }
        _surface.SetScribble(strip, row, text);
    }

    /// <summary>Flip between the Channel and Master layers (the Record button).</summary>
    private void ToggleLayer()
    {
        Layer target;
        lock (_stateLock)
        {
            target = _layer == Layer.Master ? Layer.Channels : Layer.Master;
        }
        EnterLayer(target);
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

        // Mute/Solo/Select act on channels only; in the Master layer they would
        // mis-address a bus, so they are ignored there (they fall to default).
        if (_layer == Layer.Channels)
        {
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

            // Transport drives the host's music player (the X32 has no transport).
            // Rewind/Fast-Forward act as previous/next track.
            case McuProtocol.FastForward:
                Transport?.Invoke(TransportCommand.Next);
                break;
            case McuProtocol.Rewind:
                Transport?.Invoke(TransportCommand.Previous);
                break;
            case McuProtocol.Play:
                Transport?.Invoke(TransportCommand.PlayPause);
                break;
            case McuProtocol.Stop:
                Transport?.Invoke(TransportCommand.Stop);
                break;
            // Record is repurposed as the Master/Channel layer toggle - the
            // Session Navigator's own Master button sends no MIDI, so a real
            // button gives an instant, lamp-lit switch instead.
            case McuProtocol.Record:
                ToggleLayer();
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
        RefreshLayer();
    }

    /// <summary>Re-pull whichever layer the eight faders currently show.</summary>
    private void RefreshLayer()
    {
        Layer layer;
        lock (_stateLock)
        {
            layer = _layer;
        }

        if (layer == Layer.Master)
        {
            RefreshMaster();
        }
        else
        {
            RefreshBank();
        }
    }

    /// <summary>
    /// Master layer: strip 0 is the main LR bus, strips 1-7 are mix buses 1-7.
    /// Queries their levels and names; the replies drive the motors and the
    /// upper scribble row, exactly as the channel layer does.
    /// </summary>
    private void RefreshMaster()
    {
        _console.Query(X32Address.MainFader);
        _console.Query(X32Address.MainName);
        Scribble(MasterStrip, 1, "Main LR");

        for (var strip = 1; strip < _config.StripCount; strip++)
        {
            _console.Query(X32Address.BusFader(strip));
            _console.Query(X32Address.BusName(strip));
            Scribble(strip, 1, $"Bus {strip}");
        }
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
                Scribble(strip, 1, $"Ch {channel}");
            }
        }
    }

    // ---------------------------------------------------------------- console

    private void OnConsoleMessage(OscMessage message)
    {
        Layer layer;
        lock (_stateLock)
        {
            layer = _layer;
        }

        // Select lamps and solo are channel-layer concepts.
        if (message.Address == X32Address.SelectedIndex)
        {
            if (layer == Layer.Channels && message.Arguments.FirstOrDefault() is int index)
            {
                UpdateSelectLeds(index + 1);
            }
            return;
        }

        if (message.Address.StartsWith("/-stat/solosw/", StringComparison.Ordinal))
        {
            if (layer == Layer.Channels)
            {
                HandleSolo(message);
            }
            return;
        }

        if (layer == Layer.Master)
        {
            HandleMasterMessage(message);
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
                Scribble(strip, 0,
                    string.IsNullOrWhiteSpace(name) ? $"Ch {channel}" : name);
                break;
        }
    }

    /// <summary>
    /// Console replies while the eight faders show the Master layer: main LR on
    /// strip 0, mix buses 1-7 on strips 1-7. Only faders and names drive the
    /// surface here - mute/solo/select stay channel-only.
    /// </summary>
    private void HandleMasterMessage(OscMessage message)
    {
        if (message.Address == X32Address.MainFader)
        {
            if (message.Arguments.FirstOrDefault() is float level)
            {
                HandleFader(MasterStrip, level);
            }
            return;
        }

        if (message.Address == X32Address.MainName)
        {
            var name = message.Arguments.FirstOrDefault() as string;
            Scribble(MasterStrip, 0, string.IsNullOrWhiteSpace(name) ? "Main LR" : name);
            return;
        }

        if (!X32Address.TryParseBus(message.Address, out var bus, out var parameter))
        {
            return;
        }

        // Bus N is shown on strip N (1-7); higher buses are off this layer.
        var strip = bus;
        if (strip < 1 || strip >= _config.StripCount)
        {
            return;
        }

        switch (parameter)
        {
            case "mix/fader" when message.Arguments.FirstOrDefault() is float level:
                HandleFader(strip, level);
                break;

            case "config/name" when message.Arguments.FirstOrDefault() is string name:
                Scribble(strip, 0,
                    string.IsNullOrWhiteSpace(name) ? $"Bus {bus}" : name);
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
