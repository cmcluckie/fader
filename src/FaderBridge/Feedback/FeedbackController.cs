using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Fader.Bridge.Osc;

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
    private readonly string _dataDir;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private readonly FkNotch[][] _latest = Enumerable.Range(0, MaxChans).Select(_ => Array.Empty<FkNotch>()).ToArray();

    private readonly List<string> _devices = new();
    private readonly SortedDictionary<int, string> _channels = new();
    private readonly List<int> _enabled = new();          // physical indices, sorted
    private readonly Dictionary<int, string> _names = new();   // physical index -> user label
    private readonly Dictionary<int, int> _returns = new();    // physical input -> return output
    private readonly SortedDictionary<int, string> _outChannels = new();

    private float _minHz = 200f, _maxHz = 16000f, _floorDb = -88f, _maxCutDb = -24f;
    private int _attack = 1;
    private bool _floorAuto = true;
    private double _floorEstimate = -88;
    private DateTime _lastFloorPush = DateTime.MinValue;

    // A rolling few seconds of spectra, so "that was feedback" can be answered
    // with the frames that led up to it rather than from memory.
    private readonly Queue<(double T, int Slot, float HzPerBin, float[] Mags)> _recent = new();
    private const int RecentFrames = 240;   // ~6 s at the telemetry rate
    private string _savedSignature = "";
    private string? _currentDevice;
    private string? _selectedDevice;
    private volatile bool _pendingReplay;
    private volatile bool _allowPersist;

    public FeedbackController(string enginePath, string dataDir, string? device = null)
    {
        _dataDir = dataDir;
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
        _floorDb = audio.FloorDb < 0 ? audio.FloorDb : -88f;
        _maxCutDb = audio.MaxCutDb < 0 ? audio.MaxCutDb : -24f;
        _attack = Math.Clamp(audio.Attack, 0, 2);
        _floorAuto = audio.FloorAuto;

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
        _supervisor.Client.RejectionReceived += OnRejection;
        _supervisor.Client.SpectrumReceived += OnSpectrum;
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

    /// <summary>Ignore anything quieter than this - the lid on the "cut inside this box" rule.</summary>
    public float FloorDb => _floorDb;

    /// <summary>
    /// Whether the floor tracks the room instead of being set by hand.
    ///
    /// This control broke suppression twice in one session, in opposite
    /// directions: dragged to the bottom it chased the noise floor and thrashed
    /// the filter pool, and raised too far it could not see a ring until the ring
    /// was already loud. It is not something anyone should have to judge by eye -
    /// the right answer is "just above whatever this room's floor happens to be",
    /// and the spectrum already says what that is.
    /// </summary>
    public bool FloorAuto => _floorAuto;

    public void SetFloorAuto(bool on)
    {
        _floorAuto = on;
        SaveAudio();
        SearchRangeChanged?.Invoke();
    }

    /// <summary>Track the room: sit the floor a fixed margin above the measured noise.</summary>
    private void OnSpectrum(FkSpectrum s)
    {
        SpectrumChanged?.Invoke(s);

        lock (_recent)
        {
            _recent.Enqueue((_clock.Elapsed.TotalSeconds, s.Channel, s.HzPerBin, s.Magnitudes));
            while (_recent.Count > RecentFrames) _recent.Dequeue();
        }
        if (!_floorAuto || s.Magnitudes.Length == 0) return;

        var lo = (int) (_minHz / Math.Max(1f, s.HzPerBin));
        var hi = Math.Min(s.Magnitudes.Length - 1, (int) (_maxHz / Math.Max(1f, s.HzPerBin)));
        if (hi - lo < 8) return;

        // The median of the watched band IS the noise floor: a ring is one narrow
        // spike among hundreds of bins and cannot move the middle of the set.
        var band = new float[hi - lo + 1];
        Array.Copy(s.Magnitudes, lo, band, 0, band.Length);
        Array.Sort(band);
        var median = band[band.Length / 2];

        // Sit just clear of the noise, not comfortably above it. The floor's only
        // job is to skip bins that are pure noise; prominence, stability and
        // growth do the actual selecting. Every dB of extra margin here is a dB
        // of ring you cannot see until it is louder - measured at the rig, a
        // raised floor took detections from 263 to 5 and suppression stopped.
        _floorEstimate += (median + 4.0 - _floorEstimate) * 0.05;

        if ((DateTime.UtcNow - _lastFloorPush).TotalSeconds < 2) return;

        var db = (float) Math.Clamp(_floorEstimate, -95.0, -60.0);
        if (Math.Abs(db - _floorDb) < 1.5f) return;

        _lastFloorPush = DateTime.UtcNow;   // only once we actually push
        _floorDb = db;
        _supervisor.Client.SetParam("floorDb", _floorDb);
        SearchRangeChanged?.Invoke();
    }

    public void SetFloor(float db)
    {
        _floorAuto = false;                 // touching it by hand takes it off auto
        db = Math.Clamp(db, -95f, -55f);    // higher than this and the detector is blind
        if (Math.Abs(db - _floorDb) < 0.5f) return;
        _floorDb = db;
        _supervisor.Client.SetParam("floorDb", _floorDb);
        SaveAudio();
        SearchRangeChanged?.Invoke();
    }

    /// <summary>
    /// How eagerly it reacts, 0 gentle / 1 normal / 2 fast. One control moving the
    /// several parameters that together make up "attack" - you think "react
    /// quicker", not "set persistFrames to 4".
    /// </summary>
    public int Attack => _attack;

    public void SetAttack(int level)
    {
        _attack = Math.Clamp(level, 0, 2);
        PushAttack();
        SaveAudio();
    }

    private void PushAttack()
    {
        // confirm frames, first-strike depth
        var (frames, firstCut) = _attack switch
        {
            0 => (10f, -6f),    // gentle: more evidence, ease in
            2 => (4f, -18f),    // fast: act on less, hit hard
            _ => (6f, -12f),    // normal
        };
        _supervisor.Client.SetParam("persistFrames", frames);
        _supervisor.Client.SetParam("initialCut", firstCut);
    }

    /// <summary>How deep a single notch may go - the tone-versus-safety trade.</summary>
    public float MaxCutDb => _maxCutDb;

    public void SetMaxCut(float db)
    {
        _maxCutDb = Math.Clamp(db, -36f, -9f);
        _supervisor.Client.SetParam("maxCutDb", _maxCutDb);
        SaveAudio();
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
            Names: names, Returns: returns, MinHz: _minHz, MaxHz: _maxHz, FloorAuto: _floorAuto,
            FloorDb: _floorDb, Attack: _attack, MaxCutDb: _maxCutDb));
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

    /// <summary>
    /// Diagnostic capture: per-detection measurements to a CSV, and detection kept
    /// running while the guard is bypassed so guard-on and guard-off can be
    /// compared. Off by default and never persisted on - it is for answering a
    /// question, not for running through every gig.
    /// </summary>
    public bool CaptureEnabled { get; private set; }

    public void SetCapture(bool on)
    {
        if (on == CaptureEnabled) return;
        CaptureEnabled = on;
        _log.SetCapture(on);
        _supervisor.Client.SetAnalysis(on);
        Log?.Invoke(on ? $"capture on: {Path.GetFileName(_log.CapturePath)}" : "capture off");
        CaptureChanged?.Invoke(on);
    }

    public event Action<bool>? CaptureChanged;

    public void SetBypass(bool on)
    {
        IsBypassed = on;
        _supervisor.Client.SetBypass(on);
        BypassChanged?.Invoke(on);
    }

    public event Action<bool>? BypassChanged;

    /// <summary>
    /// Does this app's output actually reach the console?
    ///
    /// Three times now a Console mute has silently taken the app out of the
    /// signal path: detection keeps running, filters keep deploying, the UI looks
    /// healthy, and nothing is cut, because the raw mic is reaching the PA by
    /// another route. Nothing in the app can see that - but the desk can. Put a
    /// tone on the return, read the X32's own channel meters, and let the console
    /// answer the question.
    /// </summary>
    public async Task<PathCheckResult> CheckSignalPathAsync(IPAddress console, CancellationToken token = default)
    {
        if (!EngineOk) return new PathCheckResult(false, -1, 0f, "The engine isn't running.");
        if (EnabledInputs.Count == 0) return new PathCheckResult(false, -1, 0f, "No channels are armed.");

        using var meters = new X32ChannelMeters(console);

        var quiet = await meters.ReadAveragedAsync(4, token);
        if (quiet is null)
            return new PathCheckResult(false, -1, 0f, $"No reply from the X32 at {console}.");

        try
        {
            _supervisor.Client.SetTestTone(1000f);
            await Task.Delay(700, token);
            var loud = await meters.ReadAveragedAsync(4, token);
            if (loud is null)
                return new PathCheckResult(false, -1, 0f, $"No reply from the X32 at {console}.");

            var best = -1;
            var rise = 0f;
            for (var c = 0; c < X32ChannelMeters.ChannelCount; c++)
            {
                var d = loud[c] - quiet[c];
                if (d > rise) { rise = d; best = c; }
            }

            // 0.02 on the X32's 0..1 linear scale is comfortably above frame noise
            // and far below anything you would call a signal.
            return rise > 0.02f
                ? new PathCheckResult(true, best + 1, rise,
                    $"Reached the desk on channel {best + 1}. The app is in the signal path.")
                : new PathCheckResult(false, -1, rise,
                    "The tone never arrived. Your audio is not reaching the desk - " +
                    "check that the return channel is unmuted and up in Console.");
        }
        finally
        {
            _supervisor.Client.SetTestTone(0f);   // never leave a tone in the PA
        }
    }

    /// <summary>
    /// Mark "that was feedback" and dump the seconds leading up to it.
    ///
    /// The point is the ones your ear catches BEFORE the detector does: you are
    /// not annotating a dataset afterwards, you are rehearsing and hitting a
    /// button when it fires. Each file holds the spectral frames, the detections
    /// that did or did not happen, and the settings in force - which is enough to
    /// answer "why was that one slow" with data instead of recollection, and
    /// enough to train something later if it is ever worth it.
    /// </summary>
    public string? MarkFeedback(string note = "")
    {
        (double T, int Slot, float HzPerBin, float[] Mags)[] frames;
        lock (_recent) { frames = _recent.ToArray(); }
        if (frames.Length == 0) return null;

        var now = _clock.Elapsed.TotalSeconds;
        var dir = Path.Combine(_dataDir, "labels");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"label-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        var payload = new
        {
            marked_at_seconds = Math.Round(now, 3),
            note,
            settings = new
            {
                minHz = _minHz, maxHz = _maxHz, floorDb = _floorDb, floorAuto = _floorAuto,
                attack = _attack, maxCutDb = _maxCutDb,
            },
            // what the detector had already decided, for comparison with the ear
            recent_detections = _recentDetections.ToArray(),
            recent_rejections = _recentRejections.ToArray(),
            recent_notches = NotchHistory(),
            frames = frames.Select(f => new
            {
                t = Math.Round(f.T - now, 3),          // negative: seconds before the press
                slot = f.Slot,
                hzPerBin = f.HzPerBin,
                db = f.Mags.Select(m => (float) Math.Round(m, 1)).ToArray(),
            }).ToArray(),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload));
        Log?.Invoke($"labelled: {Path.GetFileName(path)} ({frames.Length} frames)");
        return path;
    }

    private object[] NotchHistory()
    {
        lock (_recentNotches) return _recentNotches.ToArray();
    }

    private readonly Queue<object> _recentDetections = new();
    private readonly Queue<object> _recentRejections = new();
    private readonly Queue<object> _recentNotches = new();
    private readonly double[] _lastNotchSample = new double[MaxChans];

    /// <summary>
    /// What the filters were actually doing, sampled ~10x/s.
    ///
    /// Without this a label can show a ring sitting at -48 dB for six seconds and
    /// still not say whether a notch was on it. That is the difference between
    /// "never detected it" and "detected it and the cut was not enough" - opposite
    /// fixes, and the spectrum alone cannot tell them apart. Only active filters
    /// are kept; an idle bank is not worth the bytes.
    /// </summary>
    private void RecordNotches(int slot, FkNotch[] notches)
    {
        var now = _clock.Elapsed.TotalSeconds;
        lock (_recentNotches)
        {
            if (now - _lastNotchSample[slot] < 0.1) return;
            _lastNotchSample[slot] = now;

            foreach (var n in notches)
            {
                if (!n.Active) continue;
                _recentNotches.Enqueue(new
                {
                    t = Math.Round(now, 3),
                    slot,
                    hz = Math.Round(n.FreqHz, 1),
                    cut = Math.Round(n.CurrentDb, 1),
                    target = Math.Round(n.TargetDb, 1),
                    locked = n.Locked,
                });
            }
            while (_recentNotches.Count > 1500) _recentNotches.Dequeue();
        }

        LogEq(slot, notches, now);
    }

    private readonly double[] _lastEqLog = new double[MaxChans];

    /// <summary>
    /// Record the summed response of the live filters once a second, with the
    /// bypass state beside it.
    ///
    /// The detection log answers "what did it catch". Nothing answered "what is it
    /// costing", and that is the question behind every report of muffling: the
    /// filters are individually correct and collectively a high-cut, and no log
    /// carried the collective number. With bypass in the same row, an on/off
    /// comparison becomes a subtraction instead of an argument about memory.
    /// </summary>
    private void LogEq(int slot, FkNotch[] notches, double now)
    {
        if (now - _lastEqLog[slot] < 1.0) return;
        _lastEqLog[slot] = now;

        var live = notches.Where(n => n.Active && n.CurrentDb < -0.1f)
                          .Select(n => (n.FreqHz, n.CurrentDb))
                          .ToArray();

        var worstDb = 0.0;
        var worstHz = 0.0;
        for (var i = 0; i < 60; i++)
        {
            var f = 1000.0 * Math.Pow(16.0, i / 59.0);      // 1 kHz .. 16 kHz
            var v = NotchResponse.SumDb(live, f);
            if (v < worstDb) { worstDb = v; worstHz = f; }
        }

        _log.WriteEq(now, slot, IsBypassed, live.Length,
                     NotchResponse.AverageDb(live, 1000, 4000),
                     NotchResponse.AverageDb(live, 4000, 16000),
                     worstDb, worstHz);
    }

    /// <summary>
    /// Near-misses, with the gate that stopped each one. This is the log that was
    /// missing: "it did not catch that" used to be answerable only by simulating
    /// the detector against a downsampled copy of the spectrum.
    /// </summary>
    private void OnRejection(FkRejection r)
    {
        lock (_recentRejections)
        {
            _recentRejections.Enqueue(new
            {
                t = Math.Round(_clock.Elapsed.TotalSeconds, 3),
                hz = Math.Round(r.Hz, 1),
                db = Math.Round(r.LevelDb, 1),
                why = r.Why,
                frames = r.Frames,
                slot = r.Channel,
            });
            while (_recentRejections.Count > 400) _recentRejections.Dequeue();
        }
    }

    /// <summary>Lock or unlock one filter, by its slot index within the channel.</summary>
    public void LockNotch(int slot, int index, bool on)
    {
        if (slot < 0 || slot >= MaxChans) return;
        _supervisor.Client.LockNotch(slot, index, on);
    }

    /// <summary>Remove one filter outright - the surgical alternative to Panic.</summary>
    public void RemoveNotch(int slot, int index)
    {
        if (slot < 0 || slot >= MaxChans) return;
        _supervisor.Client.RemoveNotch(slot, index);
    }

    /// <summary>The filters currently deployed on a slot (active ones only).</summary>
    public IReadOnlyList<(int Index, FkNotch Notch)> ActiveNotches(int slot)
    {
        if (slot < 0 || slot >= MaxChans) return Array.Empty<(int, FkNotch)>();
        lock (_lock)
        {
            var list = new List<(int, FkNotch)>();
            var latest = _latest[slot];
            for (var i = 0; i < latest.Length; i++)
            {
                if (latest[i].Active) list.Add((i, latest[i]));
            }
            return list;
        }
    }

    /// <summary>
    /// Release every held filter. The way back from a ring-out that locked in
    /// something you did not want - locked notches are otherwise permanent.
    /// </summary>
    public void UnlockAll()
    {
        for (var slot = 0; slot < MaxChans; slot++)
        {
            int count;
            lock (_lock) { count = _latest[slot].Length; }
            for (var i = 0; i < count; i++) _supervisor.Client.LockNotch(slot, i, false);
        }
    }

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
            PushAttack();
            _supervisor.Client.SetParam("floorDb", _floorDb);
            _supervisor.Client.SetParam("maxCutDb", _maxCutDb);
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
        RecordNotches(slot, notches);

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
        lock (_recentDetections)
        {
            _recentDetections.Enqueue(new
            {
                t = Math.Round(_clock.Elapsed.TotalSeconds, 3),
                hz = Math.Round(d.Hz, 1),
                db = Math.Round(d.LevelDb, 1),
                age_ms = Math.Round(d.AgeMs, 0),
                width_hz = Math.Round(d.WidthHz, 1),
                slot = d.Channel,
            });
            while (_recentDetections.Count > 200) _recentDetections.Dequeue();
        }

        // With capture on, detection keeps running while the guard is off, so the
        // guard column is what makes an on/off session comparable.
        if (CaptureEnabled)
        {
            _log.WriteCapture(_clock.Elapsed.TotalSeconds, d.Channel, ! IsBypassed, d);
        }

        // enabled channels always cut, so a detection on an active slot was applied
        _log.Write(d, applied: ! IsBypassed, _clock.Elapsed.TotalSeconds);
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
