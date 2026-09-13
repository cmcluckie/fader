using System.Net;

using Commons.Music.Midi;
using Fader.Bridge;
using Fader.Bridge.Midi;
using Fader.Bridge.Osc;
using Fader.Shared;
using Fader.Shared.Net;

namespace Fader.Bridge.App;

/// <summary>Immutable snapshot of what the tray menu needs to render.</summary>
public sealed record BridgeStatus
{
    /// <summary>The user wants the bridge on (Start pressed).</summary>
    public bool Active { get; init; }

    /// <summary>The FaderPort is open and the bridge is actually running.</summary>
    public bool Bridging { get; init; }

    public bool X32Reachable { get; init; }
    public string MidiPort { get; init; } = "—";
    public string X32Endpoint { get; init; } = "—";
    public string? Error { get; init; }
}

/// <summary>
/// Supervises the bridge so it survives either device being absent. Every 5s it
/// re-checks the FaderPort: if it is missing it waits and retries; when it
/// appears it opens it and starts bridging; if it is unplugged mid-run it tears
/// down and goes back to waiting. The X32 needs no such reconnect - OSC is
/// connectionless - so it is simply probed with /info each tick, and the
/// bridge's own keepalive and periodic resync repopulate state when it returns.
/// </summary>
public sealed class BridgeController : IAsyncDisposable
{
    private readonly string _configPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BridgeConfig? _config;
    private readonly X32AddressStore _addressStore = new();
    private IPAddress? _resolvedAddress;  // cached once the console has answered
    private IMidiAccess? _midiAccess;     // shared: presence checks + opening
    private FaderPortDevice? _surface;
    private X32Client? _client;
    private BridgeHost? _host;
    private NowPlaying? _nowPlaying;
    private CancellationTokenSource? _sessionCts;  // one bridging session
    private Timer? _supervisor;

    private bool _marqueeEnabled = true;

    private volatile bool _active;
    private bool _bridging;
    private long _lastInfoTicks;
    private string _midiPort = "—";
    private string _endpoint = "—";
    private string? _error;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    // Reachability tolerates one missed reply at the 5s probe cadence.
    private const long ReachableTtlMs = 11_000;

    public BridgeController(string configPath) => _configPath = configPath;

    public event Action<BridgeStatus>? StatusChanged;

    public bool Active => _active;

    public bool MarqueeEnabled => _marqueeEnabled;

    /// <summary>Turn the now-playing marquee on the scribble strips on or off.</summary>
    public void SetMarqueeEnabled(bool on)
    {
        _marqueeEnabled = on;
        _nowPlaying?.SetEnabled(on);
    }

    private void Publish() => StatusChanged?.Invoke(new BridgeStatus
    {
        Active = _active,
        Bridging = _bridging,
        X32Reachable = _bridging && Environment.TickCount64 - Interlocked.Read(ref _lastInfoTicks) < ReachableTtlMs,
        MidiPort = _midiPort,
        X32Endpoint = _endpoint,
        Error = _error,
    });

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_active)
            {
                return;
            }

            _error = null;

            try
            {
                _config = BridgeConfig.Load(_configPath);
            }
            catch (Exception ex)
            {
                _error = $"Config: {ex.Message}";
                Publish();
                return;
            }

            _endpoint = string.IsNullOrWhiteSpace(_config.X32IpAddress)
                ? "searching for X32…"
                : $"{_config.X32IpAddress}:{_config.X32Port}";

            try
            {
                _midiAccess = MidiBackend.Create(out _);
            }
            catch (Exception ex)
            {
                _error = $"MIDI backend: {ex.Message}";
                Publish();
                return;
            }

            _active = true;

            await TryConnectAsync();                 // connect immediately if possible
            _supervisor = new Timer(_ => _ = TickAsync(), null, Interval, Interval);
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        _active = false;    // set first so an in-flight tick bails out

        // Dispose the timer outside the gate: its callback returns synchronously
        // (it only kicks off TickAsync), so this cannot deadlock on the gate.
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync();
            _supervisor = null;
        }

        await _gate.WaitAsync();
        try
        {
            await TeardownSessionAsync();
            (_midiAccess as IDisposable)?.Dispose();
            _midiAccess = null;
            _config = null;
            _midiPort = "—";
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The 5s heartbeat. Runs under the gate; skips if one is in flight.</summary>
    private async Task TickAsync()
    {
        if (!await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (!_active)
            {
                return;
            }

            if (!_bridging)
            {
                await TryConnectAsync();
            }
            else if (!SurfacePresent())
            {
                // FaderPort was unplugged or powered off while running.
                await TeardownSessionAsync();
                _midiPort = "waiting for FaderPort…";
            }

            if (_bridging)
            {
                try { _client?.Send(new OscMessage("/info")); }
                catch { /* socket may be mid-teardown; next tick reflects it */ }
            }

            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Open the FaderPort and start a bridging session, if it is present.</summary>
    private async Task TryConnectAsync()
    {
        if (_config is null || _midiAccess is null)
        {
            return;
        }

        if (!SurfacePresent())
        {
            _midiPort = "waiting for FaderPort…";
            return;
        }

        try
        {
            var surface = new FaderPortDevice();
            await surface.OpenAsync(_config.MidiPortName, _midiAccess);

            // Find the console: last address that answered, else the configured
            // hint, else a LAN search. Cached, so the search only runs until it
            // first answers; a lost console clears the cache (see TickAsync).
            _resolvedAddress ??= await X32Locator.ResolveAsync(
                _addressStore, _config.X32IpAddress, _config.X32Port);

            if (_resolvedAddress is null)
            {
                _endpoint = "searching for X32…";
                return;   // the supervisor tick will try again
            }

            _endpoint = $"{_resolvedAddress}:{_config.X32Port}";

            var session = new CancellationTokenSource();
            var client = new X32Client(
                _resolvedAddress, _config.X32Port,
                TimeSpan.FromSeconds(_config.KeepaliveSeconds));
            client.MessageReceived += OnConsoleMessage;
            client.Start(session.Token);

            var host = new BridgeHost(_config, surface, client);
            host.Transport += MediaKeys.Handle;   // transport buttons -> music player
            host.Start(session.Token);

            var nowPlaying = new NowPlaying(surface, host);
            nowPlaying.SetEnabled(_marqueeEnabled);
            nowPlaying.Start();

            _surface = surface;
            _client = client;
            _host = host;
            _nowPlaying = nowPlaying;
            _sessionCts = session;
            _midiPort = surface.InputName;
            Interlocked.Exchange(ref _lastInfoTicks, 0);
            _error = null;
            _bridging = true;
        }
        catch (Exception ex)
        {
            // Opening raced a disconnect, or the name is wrong. Stay in waiting;
            // the next tick retries.
            _error = $"MIDI: {ex.Message}";
            _midiPort = "waiting for FaderPort…";
            await TeardownSessionAsync();
        }
    }

    private async Task TeardownSessionAsync()
    {
        _bridging = false;

        // Stop the marquee before the surface it writes to goes away.
        _nowPlaying?.Dispose();
        _nowPlaying = null;

        _sessionCts?.Cancel();

        if (_client is not null)
        {
            _client.MessageReceived -= OnConsoleMessage;
        }

        try { _surface?.Reset(_config?.StripCount ?? 8); }
        catch { /* the surface may already be gone; best effort */ }

        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_surface is not null)
        {
            await _surface.DisposeAsync();
        }

        _sessionCts?.Dispose();
        _surface = null;
        _client = null;
        _host = null;
        _sessionCts = null;
    }

    /// <summary>Is the configured FaderPort currently enumerated on both ends?</summary>
    private bool SurfacePresent()
    {
        if (_config is null || _midiAccess is null)
        {
            return false;
        }

        var filter = _config.MidiPortName;
        return Matches(_midiAccess.Inputs) && Matches(_midiAccess.Outputs);

        bool Matches(IEnumerable<IMidiPortDetails> ports)
        {
            var list = ports.ToList();
            // Exact match wins; otherwise an unambiguous substring - the same
            // rule FaderPortDevice.OpenAsync uses to pick the port.
            if (list.Any(p => string.Equals(p.Name, filter, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return list.Count(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) == 1;
        }
    }

    private void OnConsoleMessage(OscMessage message)
    {
        if (message.Address == "/info")
        {
            Interlocked.Exchange(ref _lastInfoTicks, Environment.TickCount64);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
