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
    private readonly Dictionary<int, string> _names = new();   // physical index -> user label
    private readonly Dictionary<int, int> _returns = new();    // physical input -> return output
    private readonly SortedDictionary<int, string> _outChannels = new();

    private float _minHz = 200f, _maxHz = 16000f;
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
        foreach (var (key, value) in audio.Names ?? new Dictionary<string, string>())
        {
            if (int.TryParse(key, out var index)) _names[index] = value;
        }
        foreach (var (key, value) in audio.Returns ?? new Dictionary<string, int>())
        {
            if (int.TryParse(key, out var index)) _returns[index] = value;
        }
        _minHz = audio.MinHz > 0 ? audio.MinHz : 200f;
        _maxHz = audio.MaxHz > 0 ? audio.MaxHz : 16000f;

        _supervisor = new EngineSupervisor(enginePath, device ?? audio.Device);
        _store = new FkNotchStore(Path.Combine(dataDir, "notches.json"));
        _log = new FkEventLog(Path.Combine(dataDir, "logs"));

        _savedSignature = Signature(_store.Load());

        _supervisor.EngineStarted += () => { _pendingReplay = true; _allowPersist = false; };
        _supervisor.EngineOkChanged += OnEngineOk;
        _supervisor.Log += m => Log?.Invoke(m);
        _supervisor.Client.StatusReceived += s => StatusChanged?.Invoke(s);
        _supervisor.Client.NotchesReceived += OnNotches;
        _supervisor.Client.DetectionReceived += OnDetection;
        _supervisor.Client.SpectrumReceived += s => SpectrumChanged?.Invoke(s);
        _supervisor.Client.DeviceListed += OnDeviceListed;
        _supervisor.Client.ChannelListed += OnChannelListed;
        _supervisor.Client.OutChannelListed += OnOutChannelListed;
        _supervisor.Client.AudioStateReceived += OnAudioState;
    }

    public bool EngineOk => _supervisor.EngineOk;
    public string LogPath => _log.Path;

    public event Action<bool>? EngineOkChanged;
    public event Action<string>? Log;
    public event Action<int, FkNotch[]>? NotchesChanged;      // slot, notches
    public event Action<FkSpectrum>? SpectrumChanged;         // slot in .Channel
    public event Action<FkDetection>? DetectionReceived;      // slot in .Channel
    public event Action<FkStatus>? StatusChanged;             // engine running + CPU load
    public event Action? SearchRangeChanged;

    /// <summary>
    /// The band the detector watches. Narrowing the low edge is the direct fix for
    /// a voice being notched: below roughly 1 kHz a sung note looks exactly like a
    /// ring, and vocal feedback almost always lives above it.
    /// </summary>
    public float MinHz => _minHz;
    public float MaxHz => _maxHz;

    public void SetSearchRange(float minHz, float maxHz)
    {
        minHz = Math.Clamp(minHz, 40f, 8000f);
        maxHz = Math.Clamp(maxHz, Math.Max(minHz * 1.5f, 1000f), 18000f);
        if (Math.Abs(minHz - _minHz) < 0.5f && Math.Abs(maxHz - _maxHz) < 0.5f) return;
        _minHz = minHz;
        _maxHz = maxHz;
        PushSearchRange();
        SaveAudio();
        SearchRangeChanged?.Invoke();
    }

    private void PushSearchRange()
    {
        _supervisor.Client.SetParam("minFreq", _minHz);
        _supervisor.Client.SetParam("maxFreq", _maxHz);
    }
    public event Action? DevicesChanged;
    public event Action? ChannelsChanged;                     // channel list or enabled set changed

    // ---- devices ------------------------------------------------------------
    public IReadOnlyList<string> Devices { get { lock (_lock) { return _devices.ToArray(); } } }
    public string? CurrentDevice => _currentDevice;

    public void SetDevice(string device)
    {
        // A new device is a new channel space: arms and labels no longer refer to
        // the same physical inputs, so both are cleared rather than silently wrong.
        lock (_lock) { _selectedDevice = device; _enabled.Clear(); _names.Clear(); _returns.Clear(); _outChannels.Clear(); }
        _supervisor.Client.SetAudio(device, 48000, 64);
        PushRouting();
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

    /// <summary>
    /// What to call this input. A name the user gave it wins over the interface's own
    /// channel name: on stage you look for "Lead", not "Input 4 (Thunderbolt)".
    /// </summary>
    public string ChannelName(int channel)
    {
        lock (_lock)
        {
            if (_names.TryGetValue(channel, out var custom) && !string.IsNullOrWhiteSpace(custom)) return custom;
            return _channels.GetValueOrDefault(channel, $"Ch {channel + 1}");
        }
    }

    /// <summary>The interface's own name for the input, ignoring any user label.</summary>
    public string HardwareName(int channel)
    {
        lock (_lock) { return _channels.GetValueOrDefault(channel, $"Ch {channel + 1}"); }
    }

    public bool HasCustomName(int channel)
    {
        lock (_lock) { return _names.ContainsKey(channel); }
    }

    /// <summary>Rename an input. Blank clears the label and falls back to the hardware name.</summary>
    public void SetChannelName(int channel, string? name)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(name)) _names.Remove(channel);
            else _names[channel] = name.Trim();
        }
        SaveAudio();
        ChannelsChanged?.Invoke();
    }

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
        PushRouting();
        SaveAudio();
        ChannelsChanged?.Invoke();
    }

    /// <summary>
    /// Where an armed input's processed audio is returned. Defaults to the same
    /// index as the input (the plain passthrough case); on an insert rig this is
    /// the output that feeds the console - e.g. mic on ANALOG 5, return on ADAT 3.
    /// </summary>
    public int ReturnFor(int channel)
    {
        lock (_lock) { return _returns.GetValueOrDefault(channel, channel); }
    }

    public void SetReturn(int channel, int output)
    {
        lock (_lock)
        {
            if (output == channel) _returns.Remove(channel);   // default: mirror
            else _returns[channel] = output;
        }
        PushRouting();
        SaveAudio();
        ChannelsChanged?.Invoke();
    }

    /// <summary>Every output channel the current device exposes (index, name).</summary>
    public IReadOnlyList<(int Index, string Name)> OutputChannels
    {
        get { lock (_lock) { return _outChannels.Select(kv => (kv.Key, kv.Value)).ToArray(); } }
    }

    /// <summary>Send the armed inputs and their parallel returns to the engine.</summary>
    private void PushRouting()
    {
        int[] inputs; int[] outputs;
        lock (_lock)
        {
            inputs = _enabled.ToArray();
            outputs = inputs.Select(i => _returns.GetValueOrDefault(i, i)).ToArray();
        }
        _supervisor.Client.SetInputs(inputs);
        _supervisor.Client.SetOutputs(outputs);
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
    private void SaveAudio()
    {
        Dictionary<string, string> names;
        Dictionary<string, int> returns;
        lock (_lock)
        {
            names = _names.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            returns = _returns.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        }
        _audioStore.Save(new AudioSelection(_selectedDevice, EnabledSnapshot(), Enabled: true,
            Names: names, Returns: returns, MinHz: _minHz, MaxHz: _maxHz));
    }

    // ---- lifecycle + notch ops ---------------------------------------------
    public void Start(CancellationToken token = default) => _supervisor.Start(token);

    /// <summary>Hand-place a locked notch on an engine slot (used by tests).</summary>
    public void PlaceManualNotch(int slot, float hz, float depthDb) =>
        _supervisor.Client.PlaceNotch(slot, hz, depthDb);

    /// <summary>
    /// Show-mode bypass: audio passes clean while every notch keeps its state, so
    /// un-bypassing restores the guard exactly as it was. Replayed on engine restart.
    /// </summary>
    public bool IsBypassed { get; private set; }

    public void SetBypass(bool on)
    {
        IsBypassed = on;
        _supervisor.Client.SetBypass(on);
        BypassChanged?.Invoke(on);
    }

    public event Action<bool>? BypassChanged;

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
            PushRouting();
            PushSearchRange();
            if (IsBypassed) _supervisor.Client.SetBypass(true);   // a fresh engine starts un-bypassed

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

    private void OnOutChannelListed(int index, string name)
    {
        lock (_lock) { _outChannels[index] = name; }
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
