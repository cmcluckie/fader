using System.Diagnostics;
using System.Text;

namespace Fader.Bridge.Feedback;

/// <summary>
/// Ties the feedback engine to the C#-owned concerns (§4): supervises the engine,
/// owns which input channels are enabled, persists locked notches (keyed by
/// physical channel so they survive re-ordering) and replays everything on each
/// (re)start, and logs detections.
///
/// The engine works in slots 0..N-1 (one per checked channel, ascending); this
/// class maps slot &lt;-&gt; physical channel so callers and storage speak in
/// physical channels.
/// </summary>
public sealed class FeedbackController : IAsyncDisposable
{
    private const int MaxChans = 8;   // must match the engine's kMaxInputs

    private readonly EngineSupervisor _supervisor;
    private readonly FkNotchStore _store;
    private readonly FkAudioStore _audioStore;
    private readonly FkEventLog _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private readonly FkNotch[][] _latest = Enumerable.Range(0, MaxChans).Select(_ => Array.Empty<FkNotch>()).ToArray();

    private readonly List<string> _devices = new();
    private readonly SortedDictionary<int, string> _channels = new();
    private readonly List<int> _enabled = new();          // physical indices, sorted

    private string _savedSignature = "";
    private string? _currentDevice;
    private string? _selectedDevice;
    private volatile bool _pendingReplay;
    private volatile bool _allowPersist;

    public FeedbackController(string enginePath, string dataDir, string? device = null)
    {
        _audioStore = new FkAudioStore(Path.Combine(dataDir, "audio.json"));
        var audio = _audioStore.Load();
        _selectedDevice = audio.Device;
        _enabled.AddRange((audio.Inputs ?? Array.Empty<int>()).Distinct().OrderBy(x => x).Take(MaxChans));

        _supervisor = new EngineSupervisor(enginePath, device ?? audio.Device);
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
        _supervisor.Client.ChannelListed += OnChannelListed;
        _supervisor.Client.AudioStateReceived += OnAudioState;
    }

    public bool EngineOk => _supervisor.EngineOk;
    public string LogPath => _log.Path;

    public event Action<bool>? EngineOkChanged;
    public event Action<string>? Log;
    public event Action<int, FkNotch[]>? NotchesChanged;      // slot, notches
    public event Action<FkSpectrum>? SpectrumChanged;         // slot in .Channel
    public event Action<FkDetection>? DetectionReceived;      // slot in .Channel
    public event Action? DevicesChanged;
    public event Action? ChannelsChanged;                     // channel list or enabled set changed

    // ---- devices ------------------------------------------------------------
    public IReadOnlyList<string> Devices { get { lock (_lock) { return _devices.ToArray(); } } }
    public string? CurrentDevice => _currentDevice;

    public void SetDevice(string device)
    {
        lock (_lock) { _selectedDevice = device; _enabled.Clear(); }   // new device -> new channel space
        _supervisor.Client.SetAudio(device, 48000, 64);
        _supervisor.Client.SetInputs(EnabledSnapshot());
        SaveAudio();
        ChannelsChanged?.Invoke();
    }

    // ---- channels + enable/disable -----------------------------------------
    /// <summary>Every input channel the current device exposes (index, name).</summary>
    public IReadOnlyList<(int Index, string Name)> InputChannels
    {
        get { lock (_lock) { return _channels.Select(kv => (kv.Key, kv.Value)).ToArray(); } }
    }

    /// <summary>Enabled physical channel indices (ascending), one engine slot each.</summary>
    public IReadOnlyList<int> EnabledInputs => EnabledSnapshot();

    public bool IsInputEnabled(int channel) { lock (_lock) { return _enabled.Contains(channel); } }

    public string ChannelName(int channel) { lock (_lock) { return _channels.GetValueOrDefault(channel, $"Ch {channel + 1}"); } }

    /// <summary>Check or uncheck a physical channel for feedback (monitor + cut).</summary>
    public void SetInputEnabled(int channel, bool on)
    {
        lock (_lock)
        {
            var has = _enabled.Contains(channel);
            if (on && !has && _enabled.Count < MaxChans) _enabled.Add(channel);
            else if (!on && has) _enabled.Remove(channel);
            else return;
            _enabled.Sort();
        }
        _supervisor.Client.SetInputs(EnabledSnapshot());
        SaveAudio();
        ChannelsChanged?.Invoke();
    }

    /// <summary>Physical channel driving an engine slot, or -1.</summary>
    public int PhysicalForSlot(int slot)
    {
        lock (_lock) { return slot >= 0 && slot < _enabled.Count ? _enabled[slot] : -1; }
    }

    private int SlotForPhysical(int channel)
    {
        lock (_lock) { return _enabled.IndexOf(channel); }
    }

    private int[] EnabledSnapshot() { lock (_lock) { return _enabled.ToArray(); } }

    // The controller only exists while feedback is enabled, so persist Enabled=true
    // whenever it saves; the app writes Enabled=false when it is switched off.
    private void SaveAudio() => _audioStore.Save(new AudioSelection(_selectedDevice, EnabledSnapshot(), Enabled: true));

    // ---- lifecycle + notch ops ---------------------------------------------
    public void Start(CancellationToken token = default) => _supervisor.Start(token);

    /// <summary>Hand-place a locked notch on an engine slot (used by tests).</summary>
    public void PlaceManualNotch(int slot, float hz, float depthDb) =>
        _supervisor.Client.PlaceNotch(slot, hz, depthDb);

    public void LockAll()
    {
        for (var slot = 0; slot < MaxChans; slot++) _supervisor.Client.LockAll(slot);
    }

    public void ClearAll(bool includeLocked)
    {
        for (var slot = 0; slot < MaxChans; slot++) _supervisor.Client.Clear(slot, includeLocked);
    }

    private void OnEngineOk(bool ok)
    {
        if (ok)
        {
            _supervisor.Client.ListDevices();
        }

        if (ok && _pendingReplay)
        {
            _pendingReplay = false;
            if (_selectedDevice is { } device)
            {
                _supervisor.Client.SetAudio(device, 48000, 64);
            }
            _supervisor.Client.SetInputs(EnabledSnapshot());

            // Replay locked notches, mapping their physical channel to its slot.
            var toReplay = _store.Load();
            var replayed = 0;
            foreach (var n in toReplay)
            {
                var slot = SlotForPhysical(n.Channel);
                if (slot >= 0) { _supervisor.Client.PlaceNotch(slot, n.Hz, n.DepthDb); replayed++; }
            }
            if (replayed > 0) Log?.Invoke($"replaying {replayed} locked notch(es)");

            _ = Task.Delay(800).ContinueWith(_ => _allowPersist = true, TaskScheduler.Default);
        }
        EngineOkChanged?.Invoke(ok);
    }

    private void OnNotches(int slot, FkNotch[] notches)
    {
        if (slot < 0 || slot >= MaxChans)
        {
            return;
        }

        NotchesChanged?.Invoke(slot, notches);

        IReadOnlyList<StoredNotch> locked;
        lock (_lock)
        {
            _latest[slot] = notches;
            if (!_allowPersist)
            {
                return;
            }
            locked = CollectLocked();
            var sig = Signature(locked);
            if (sig == _savedSignature)
            {
                return;
            }
            _savedSignature = sig;
        }

        _store.Save(locked);
    }

    private void OnDetection(FkDetection d)
    {
        // enabled channels always cut, so a detection on an active slot was applied
        _log.Write(d, applied: true, _clock.Elapsed.TotalSeconds);
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

    private void OnChannelListed(int index, string name)
    {
        lock (_lock) { _channels[index] = name; }
        ChannelsChanged?.Invoke();
    }

    private void OnAudioState(FkAudioState state)
    {
        var deviceChanged = state.Device != _currentDevice;
        _currentDevice = state.Device;
        if (deviceChanged)
        {
            lock (_lock) { _channels.Clear(); }
            ChannelsChanged?.Invoke();
        }
        DevicesChanged?.Invoke();
    }

    // Locked notches, keyed by physical channel (via the slot mapping) so they
    // are stable when the enabled set is re-ordered.
    private List<StoredNotch> CollectLocked()
    {
        var list = new List<StoredNotch>();
        for (var slot = 0; slot < MaxChans; slot++)
        {
            var physical = slot < _enabled.Count ? _enabled[slot] : -1;
            if (physical < 0) continue;
            foreach (var n in _latest[slot])
            {
                if (n.Active && n.Locked)
                {
                    list.Add(new StoredNotch(physical, n.FreqHz, n.TargetDb, n.Manual));
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
