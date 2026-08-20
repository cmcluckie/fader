using System.Diagnostics;
using System.Text;

namespace Fader.Bridge.Feedback;

/// <summary>
/// Ties the feedback engine to the C#-owned concerns (§4): supervises the engine,
/// persists locked notches and replays them on every (re)start so they survive a
/// restart (§8), and logs detections to CSV. The tray drives this; it does not
/// touch the engine client directly.
/// </summary>
public sealed class FeedbackController : IAsyncDisposable
{
    private const int Channels = 2;

    private readonly EngineSupervisor _supervisor;
    private readonly FkNotchStore _store;
    private readonly FkEventLog _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private readonly FkNotch[][] _latest = { Array.Empty<FkNotch>(), Array.Empty<FkNotch>() };

    private readonly List<string> _devices = new();
    private FkMode _mode = FkMode.Assist;   // §8: default assist
    private string _savedSignature = "";
    private string? _currentDevice;
    private string? _selectedDevice;
    private volatile bool _pendingReplay;
    // Persistence is gated off from a (re)start until the replay is applied, so a
    // fresh engine's empty notch frames cannot clobber the stored locked filters.
    private volatile bool _allowPersist;

    public FeedbackController(string enginePath, string dataDir, string? device = null)
    {
        _supervisor = new EngineSupervisor(enginePath, device);
        _store = new FkNotchStore(Path.Combine(dataDir, "notches.json"));
        _log = new FkEventLog(Path.Combine(dataDir, "logs"));

        _savedSignature = Signature(_store.Load());

        _supervisor.EngineStarted += () => { _pendingReplay = true; _allowPersist = false; };
        _supervisor.EngineOkChanged += OnEngineOk;
        _supervisor.Log += m => Log?.Invoke(m);
        _supervisor.Client.NotchesReceived += OnNotches;
        _supervisor.Client.DetectionReceived += OnDetection;
        _supervisor.Client.SpectrumReceived += s => SpectrumChanged?.Invoke(s);
        _supervisor.Client.DeviceListed += OnDeviceListed;
        _supervisor.Client.AudioStateReceived += OnAudioState;
    }

    public bool EngineOk => _supervisor.EngineOk;
    public FkMode Mode => _mode;
    public string LogPath => _log.Path;
    public string StorePath => _store.Path;

    public event Action<bool>? EngineOkChanged;
    public event Action<string>? Log;

    /// <summary>Latest notch state for a channel (for a display, and for tests).</summary>
    public event Action<int, FkNotch[]>? NotchesChanged;

    /// <summary>Latest engine spectrum frame for a channel (for a display).</summary>
    public event Action<FkSpectrum>? SpectrumChanged;

    /// <summary>A detection fired (for a display; also logged internally).</summary>
    public event Action<FkDetection>? DetectionReceived;

    /// <summary>The available input devices or the current selection changed.</summary>
    public event Action? DevicesChanged;

    /// <summary>Input devices the engine reported (Core Audio).</summary>
    public IReadOnlyList<string> Devices { get { lock (_lock) { return _devices.ToArray(); } } }

    /// <summary>The device the engine is currently running on.</summary>
    public string? CurrentDevice => _currentDevice;

    /// <summary>Switch the engine's audio device; remembered and re-applied on restart.</summary>
    public void SetDevice(string device)
    {
        _selectedDevice = device;
        _supervisor.Client.SetAudio(device, 48000, 64);
    }

    public void Start(CancellationToken token = default) => _supervisor.Start(token);

    /// <summary>Hand-place a locked notch (the plugin's click-to-place). It persists and replays.</summary>
    public void PlaceManualNotch(int channel, float hz, float depthDb) =>
        _supervisor.Client.PlaceNotch(channel, hz, depthDb);

    // ---- tray-facing control ------------------------------------------------
    public void SetMode(FkMode mode)
    {
        _mode = mode;
        _supervisor.Client.SetMode(mode);
    }

    /// <summary>Lock every active notch on both channels (end of a ring-out pass).</summary>
    public void LockAll()
    {
        for (var ch = 0; ch < Channels; ch++) _supervisor.Client.LockAll(ch);
    }

    public void ClearAll(bool includeLocked)
    {
        for (var ch = 0; ch < Channels; ch++) _supervisor.Client.Clear(ch, includeLocked);
    }

    // ---- engine lifecycle ---------------------------------------------------
    // Replay once the engine is actually up (first engineOk after a start), not
    // on process spawn - a packet sent before the engine binds its port is lost.
    private void OnEngineOk(bool ok)
    {
        if (ok)
        {
            _supervisor.Client.ListDevices();   // repopulate the picker whenever it comes up
        }

        if (ok && _pendingReplay)
        {
            _pendingReplay = false;
            if (_selectedDevice is { } device)
            {
                _supervisor.Client.SetAudio(device, 48000, 64);   // re-apply the chosen device
            }
            var toReplay = _store.Load();   // intact - persistence was gated off until now
            if (toReplay.Count > 0)
            {
                Log?.Invoke($"replaying {toReplay.Count} locked notch(es)");
            }
            foreach (var n in toReplay)
            {
                _supervisor.Client.PlaceNotch(n.Channel, n.Hz, n.DepthDb);
            }
            _supervisor.Client.SetMode(_mode);

            // Re-enable persistence once the engine has had time to reflect the
            // replay, so the first saved frame carries the replayed notches.
            _ = Task.Delay(800).ContinueWith(_ => _allowPersist = true, TaskScheduler.Default);
        }
        EngineOkChanged?.Invoke(ok);
    }

    private void OnNotches(int ch, FkNotch[] notches)
    {
        if (ch < 0 || ch >= Channels)
        {
            return;
        }

        NotchesChanged?.Invoke(ch, notches);

        IReadOnlyList<StoredNotch> locked;
        lock (_lock)
        {
            _latest[ch] = notches;                  // always track for display
            if (!_allowPersist)
            {
                return;                             // startup/replay window: don't save
            }
            locked = CollectLocked();
            var sig = Signature(locked);
            if (sig == _savedSignature)
            {
                return;   // nothing changed; don't churn the file at 10 Hz
            }
            _savedSignature = sig;
        }

        _store.Save(locked);
    }

    private void OnDetection(FkDetection d)
    {
        _log.Write(d, _mode == FkMode.Auto, _clock.Elapsed.TotalSeconds);
        DetectionReceived?.Invoke(d);
    }

    private void OnDeviceListed(string name)
    {
        bool added;
        lock (_lock)
        {
            added = !_devices.Contains(name);
            if (added) _devices.Add(name);
        }
        if (added) DevicesChanged?.Invoke();
    }

    private void OnAudioState(FkAudioState state)
    {
        _currentDevice = state.Device;
        DevicesChanged?.Invoke();
    }

    private List<StoredNotch> CollectLocked()
    {
        var list = new List<StoredNotch>();
        for (var ch = 0; ch < Channels; ch++)
        {
            foreach (var n in _latest[ch])
            {
                if (n.Active && n.Locked)
                {
                    list.Add(new StoredNotch(ch, n.FreqHz, n.TargetDb, n.Manual));
                }
            }
        }
        return list;
    }

    private static string Signature(IReadOnlyList<StoredNotch> notches)
    {
        var sb = new StringBuilder();
        foreach (var n in notches.OrderBy(x => x.Channel).ThenBy(x => x.Hz))
        {
            sb.Append(n.Channel).Append(':').Append((int) n.Hz).Append(':')
              .Append((int) (n.DepthDb * 10)).Append(';');
        }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _supervisor.DisposeAsync();
        _log.Dispose();
    }
}
