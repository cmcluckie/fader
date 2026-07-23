using Fader.Bridge;
using Fader.Bridge.Midi;
using Fader.Bridge.Osc;

namespace Fader.MenuBar;

/// <summary>Immutable snapshot of what the tray menu needs to render.</summary>
public sealed record BridgeStatus
{
    public bool Running { get; init; }
    public bool X32Reachable { get; init; }
    public string MidiPort { get; init; } = "—";
    public string X32Endpoint { get; init; } = "—";
    public string? Error { get; init; }
}

/// <summary>
/// Owns the lifecycle of the real bridge (surface + console + host) so the tray
/// can start and stop it, and polls the console with /info so the menu can show
/// whether it is actually reachable rather than merely "running".
/// </summary>
public sealed class BridgeController : IAsyncDisposable
{
    private readonly string _configPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private FaderPortDevice? _surface;
    private X32Client? _client;
    private BridgeHost? _host;
    private CancellationTokenSource? _cts;
    private Timer? _probe;

    private int _stripCount = 8;
    private long _lastInfoTicks;

    // Reachability is declared lost if no /info reply lands within this window.
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(3);
    private static readonly long ReachableTtlMs = 7000;

    public BridgeController(string configPath) => _configPath = configPath;

    public event Action<BridgeStatus>? StatusChanged;

    public bool Running { get; private set; }

    private string _midiPort = "—";
    private string _endpoint = "—";
    private string? _error;

    private void Publish() => StatusChanged?.Invoke(new BridgeStatus
    {
        Running = Running,
        X32Reachable = Running && Environment.TickCount64 - _lastInfoTicks < ReachableTtlMs,
        MidiPort = _midiPort,
        X32Endpoint = _endpoint,
        Error = _error,
    });

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Running)
            {
                return;
            }

            _error = null;

            BridgeConfig config;
            try
            {
                config = BridgeConfig.Load(_configPath);
            }
            catch (Exception ex)
            {
                _error = $"Config: {ex.Message}";
                Publish();
                return;
            }

            _stripCount = config.StripCount;
            _endpoint = $"{config.X32IpAddress}:{config.X32Port}";

            var surface = new FaderPortDevice();
            try
            {
                await surface.OpenAsync(config.MidiPortName);
            }
            catch (Exception ex)
            {
                _error = $"MIDI: {ex.Message}";
                _midiPort = "not found";
                await surface.DisposeAsync();
                Publish();
                return;
            }

            _midiPort = surface.InputName;

            var cts = new CancellationTokenSource();
            var client = new X32Client(
                config.ResolvedAddress, config.X32Port,
                TimeSpan.FromSeconds(config.KeepaliveSeconds));
            client.MessageReceived += OnConsoleMessage;
            client.Start(cts.Token);

            var host = new BridgeHost(config, surface, client);
            host.Start(cts.Token);

            _surface = surface;
            _client = client;
            _host = host;
            _cts = cts;
            _lastInfoTicks = 0;
            Running = true;

            _probe = new Timer(_ => Probe(), null, TimeSpan.Zero, ProbeInterval);
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!Running)
            {
                return;
            }

            Running = false;

            if (_probe is not null)
            {
                await _probe.DisposeAsync();
                _probe = null;
            }

            _cts?.Cancel();

            if (_client is not null)
            {
                _client.MessageReceived -= OnConsoleMessage;
            }

            // Blank the surface so it does not freeze on the last-known state.
            try { _surface?.Reset(_stripCount); }
            catch { /* best effort on the way down */ }

            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            if (_surface is not null)
            {
                await _surface.DisposeAsync();
            }

            _cts?.Dispose();
            _client = null;
            _surface = null;
            _host = null;
            _cts = null;
            _midiPort = "—";
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnConsoleMessage(OscMessage message)
    {
        // Any reply proves the path, but /info is the one we solicit and it is
        // never suppressed or bank-scoped, so it is the cleanest liveness signal.
        if (message.Address == "/info")
        {
            _lastInfoTicks = Environment.TickCount64;
        }
    }

    private void Probe()
    {
        try
        {
            _client?.Send(new OscMessage("/info"));
        }
        catch
        {
            // Socket may be tearing down; the next Publish will reflect it.
        }

        Publish();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
