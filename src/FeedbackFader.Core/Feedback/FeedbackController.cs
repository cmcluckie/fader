using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Fader.Shared;

namespace FeedbackFader;

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
    private float _harmBudget;      // 0 = no ceiling, which is how it shipped
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
    private string? _consoleAddress;
    private int[] _deskChannels = Array.Empty<int>();     // X32 channels fed through the engine
    private int[] _bypassChannels = Array.Empty<int>();   // their muted spares straight from the Console, pairwise
    private int _deskBusy;                                // one desk read or swap at a time
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
        // Before 2026-10-09 an input without a saved return mirrored its own index.
        // Keep that for inputs saved by those versions, explicitly, so an upgrade
        // changes nothing on a rig that relied on it; only inputs armed from now
        // on start with no return.
        foreach (var index in _enabled) _returns.TryAdd(index, index);
        _minHz = audio.MinHz > 0 ? audio.MinHz : 200f;
        _maxHz = audio.MaxHz > 0 ? audio.MaxHz : 16000f;
        _floorDb = audio.FloorDb < 0 ? audio.FloorDb : -88f;
        _maxCutDb = audio.MaxCutDb < 0 ? audio.MaxCutDb : -24f;
        _harmBudget = Math.Max(0f, audio.HarmBudget);
        _attack = Math.Clamp(audio.Attack, 0, 2);
        _floorAuto = audio.FloorAuto;
        _consoleAddress = audio.ConsoleAddress;
        var desk = audio.DeskChannels ?? Array.Empty<int>();
        var bypass = audio.BypassChannels ?? Array.Empty<int>();
        if (desk.Length == bypass.Length && !desk.Intersect(bypass).Any()) { _deskChannels = desk; _bypassChannels = bypass; }

        _supervisor = new EngineSupervisor(enginePath, device ?? audio.Device);
        _store = new FkNotchStore(Path.Combine(dataDir, "notches.json"));
        _log = new FkEventLog(Path.Combine(dataDir, "logs"));

        _savedSignature = Signature(_store.Load());

        _supervisor.EngineStarted += () => { _pendingReplay = true; _allowPersist = false; };
        _supervisor.EngineOkChanged += OnEngineOk;
        _supervisor.Log += m => Log?.Invoke(m);
        _supervisor.Client.StatusReceived += s => StatusChanged?.Invoke(s);
        _supervisor.Client.ContextReceived += c => { Context = c; ContextChanged?.Invoke(c); };
        _supervisor.Client.TrackReceived += OnTrack;
        _supervisor.Client.NotInLoopReceived += n => { NotInLoop = n; NotInLoopChanged?.Invoke(n); };
        _supervisor.Client.RescueReceived += (ch, depth, hz, level, why) =>
        {
            _log.WriteRescue(_clock.Elapsed.TotalSeconds, ch, depth, hz, level, why);
            RescueDucked?.Invoke(ch, depth, hz);
        };
        _supervisor.Client.NotchesReceived += OnNotches;
        _supervisor.Client.DetectionReceived += OnDetection;
        _supervisor.Client.RejectionReceived += OnRejection;
        _supervisor.Client.SpectrumReceived += OnSpectrum;
        _supervisor.Client.DeviceListed += OnDeviceListed;
        _supervisor.Client.ChannelListed += OnChannelListed;
        _supervisor.Client.OutChannelListed += OnOutChannelListed;
        _supervisor.Client.AudioStateReceived += OnAudioState;
        _supervisor.Client.LevelsReceived += OnLevels;
    }

    public bool EngineOk => _supervisor.EngineOk;

    /// <summary>The engine process is up, even if no audio device is running yet.</summary>
    public bool ProcessAlive => _supervisor.ProcessAlive;

    /// <summary>The running engine's build stamp, or null before it has said.</summary>
    public string? EngineBuild => _supervisor.EngineBuild;

    // ---- a level on every device input ------------------------------------
    // The engine opens every input the device has and reports a peak per input
    // ten times a second, armed or not. Kept here as the last peak and a peak
    // held for two seconds, both in dBFS, for the Levels tab: sing, and the
    // input that moves is the microphone.
    private readonly object _levelLock = new();
    private float[] _levelDb = Array.Empty<float>();
    private float[] _holdDb = Array.Empty<float>();
    private long[] _holdAt = Array.Empty<long>();

    private void OnLevels(float[] peaks)
    {
        var now = Environment.TickCount64;
        lock (_levelLock)
        {
            if (_levelDb.Length != peaks.Length)
            {
                _levelDb = new float[peaks.Length];
                _holdDb = Enumerable.Repeat(-120f, peaks.Length).ToArray();
                _holdAt = new long[peaks.Length];
            }
            for (var c = 0; c < peaks.Length; c++)
            {
                var db = peaks[c] > 1e-6f ? 20f * MathF.Log10(peaks[c]) : -120f;
                _levelDb[c] = db;
                if (db >= _holdDb[c] || now - _holdAt[c] > 2000) { _holdDb[c] = db; _holdAt[c] = now; }
            }
        }
    }

    /// <summary>Per device input: the peak of the last report and the peak held for two seconds, in dBFS. Empty until the engine reports.</summary>
    public (float LevelDb, float HoldDb)[] InputLevels
    {
        get
        {
            lock (_levelLock)
            {
                var r = new (float, float)[_levelDb.Length];
                for (var c = 0; c < r.Length; c++) r[c] = (_levelDb[c], _holdDb[c]);
                return r;
            }
        }
    }
    public string LogPath => _log.Path;

    public event Action<bool>? EngineOkChanged;
    public event Action<string>? Log;
    public event Action<int, FkNotch[]>? NotchesChanged;      // slot, notches
    public event Action<FkSpectrum>? SpectrumChanged;         // slot in .Channel
    public event Action<IReadOnlyList<FkTrack>>? TracksChanged;   // rings being followed across hops
    /// <summary>Filters cutting at maximum while their ring gets louder: a wiring fault, not a feedback one.</summary>
    public int NotInLoop { get; private set; }
    public event Action<int>? NotInLoopChanged;
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

    /// <summary>
    /// The engine sends every track slot, empty ones included, so a slot that has
    /// gone quiet clears rather than leaving its last hot reading on the display.
    /// </summary>
    private readonly Dictionary<(int, int), FkTrack> _trackSlots = new();

    private void OnTrack(FkTrack t)
    {
        List<FkTrack> live;
        lock (_trackSlots)
        {
            if (t.Hz > 0f) _trackSlots[(t.Channel, t.Index)] = t;
            else _trackSlots.Remove((t.Channel, t.Index));
            live = _trackSlots.Values.ToList();
        }
        TracksChanged?.Invoke(live);
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
        // confirm frames, first-strike depth.
        // "Fast" used to act on four frames instead of six. Measured on the 17
        // real howls in the replay fixtures, 2026-10-03: the two frames bought
        // 10 ms of latency and cost two extra hits on the singer's harmonics.
        // The first strike at the dial is the part of "fast" that works.
        var (frames, firstCut) = _attack switch
        {
            0 => (10f, -6f),    // gentle: more evidence, ease in
            2 => (6f, -18f),    // fast: hit hard, at the dial, from the first frame
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

    /// <summary>
    /// How much the filter set may cost the listener, in ear-weighted dB-ERB.
    /// 0 means no ceiling.
    ///
    /// Depth in dB was never the right unit: the ear charges for spectral AREA,
    /// and a 100 Hz notch spans 1.4 ear-bandwidths at 300 Hz but 0.12 at 8 kHz.
    /// Measured on 135 s of singing with a ring building at 20 dB/s, a budget of
    /// 30 killed the ring as completely as no budget at all while removing 30
    /// units of voice instead of 209.
    /// </summary>
    public float HarmBudget => _harmBudget;

    public void SetHarmBudget(float budget)
    {
        _harmBudget = Math.Clamp(budget, 0f, 400f);
        _supervisor.Client.SetParam("harmBudget", _harmBudget);
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

    // ---- the mixing console -------------------------------------------------
    /// <summary>
    /// Where the X32 lives, as typed - the RTA overlay and the signal-path check
    /// are the only two things that need it, and both degrade quietly without it.
    /// Feedback Fader's own setting since the split; it used to be read out of
    /// the bridge's config.json.
    /// </summary>
    public string? ConsoleAddress
    {
        get { lock (_lock) { return _consoleAddress; } }
    }

    /// <summary>Returns true if the text parsed; a bad address is not persisted.</summary>
    public bool SetConsoleAddress(string? text)
    {
        var trimmed = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (trimmed is not null && !System.Net.IPAddress.TryParse(trimmed, out _)) return false;

        lock (_lock) { _consoleAddress = trimmed; }
        SaveAudio();
        ConsoleAddressChanged?.Invoke();
        return true;
    }

    public event Action? ConsoleAddressChanged;

    // ---- the desk bypass: the one X32 write ----------------------------------
    /// <summary>
    /// The X32 channels the guarded microphones land on (through the engine), and
    /// the muted spares carrying the same microphones straight from the Console,
    /// pairwise. Empty until Chris fills them in, and the feature stays hidden
    /// until then. README, Rule Zero: the mute swap is the only thing this app
    /// ever writes on the desk, and only from a hold on Show.
    /// </summary>
    public IReadOnlyList<int> DeskChannels { get { lock (_lock) { return _deskChannels; } } }
    public IReadOnlyList<int> BypassChannels { get { lock (_lock) { return _bypassChannels; } } }

    public bool DeskBypassConfigured => TryDeskSetup(out _, out _, out _);

    /// <summary>"1 2" or "1,2" to [1, 2]; null when anything in it is not a channel 1..32, or repeats.</summary>
    public static int[]? ParseChannels(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();
        var list = new List<int>();
        foreach (var part in text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out var ch) || ch < 1 || ch > 32 || list.Contains(ch)) return null;
            list.Add(ch);
        }
        return list.ToArray();
    }

    /// <summary>
    /// Returns true if both lists parsed, match in length and share no channel;
    /// nothing is kept otherwise. Two empty lists switch the feature off.
    /// </summary>
    public bool SetDeskBypassChannels(string? deskText, string? bypassText)
    {
        var desk = ParseChannels(deskText);
        var bypass = ParseChannels(bypassText);
        if (desk is null || bypass is null || desk.Length != bypass.Length || desk.Intersect(bypass).Any()) return false;
        lock (_lock) { _deskChannels = desk; _bypassChannels = bypass; }
        DeskBypassState = DeskState.Unknown;
        SaveAudio();
        DeskBypassChanged?.Invoke();
        return true;
    }

    /// <summary>What the desk's mutes said when last read; never assumed from what was sent.</summary>
    public DeskState DeskBypassState { get; private set; } = DeskState.Unknown;

    /// <summary>One line for the operator about the last read or swap.</summary>
    public string DeskMessage { get; private set; } = "";

    public event Action? DeskBypassChanged;

    /// <summary>Read the mutes (reading is always allowed) and say which path is live. Writes nothing.</summary>
    public async Task RefreshDeskStateAsync()
    {
        if (!TryDeskSetup(out var ip, out var desk, out var bypass)) return;
        if (Interlocked.Exchange(ref _deskBusy, 1) == 1) return;
        try
        {
            using var x32 = new X32Mutes(ip, DeskPort);
            await ReadDeskStateAsync(x32, desk, bypass);
        }
        finally
        {
            Volatile.Write(ref _deskBusy, 0);
            DeskBypassChanged?.Invoke();
        }
    }

    /// <summary>
    /// THE X32 write. To the desk: open the bypass channels, then mute the guarded
    /// ones - a few milliseconds of both beats any gap. Back: open the guarded
    /// channels, then mute the spares. Every write is read back and logged, and
    /// the state shown afterwards is what the desk reported, not what was sent.
    /// Called from a one-second hold on Show and from nowhere else: not on engine
    /// death, not at start-up, not at quit.
    /// </summary>
    public async Task<bool> SwapToDeskAsync(bool toDesk)
    {
        if (!TryDeskSetup(out var ip, out var desk, out var bypass))
        {
            DeskMessage = "desk bypass is not set up: the console address and both channel lists live in Setup";
            DeskBypassChanged?.Invoke();
            return false;
        }
        if (Interlocked.Exchange(ref _deskBusy, 1) == 1) return false;
        try
        {
            var plan = SwapPlan(toDesk, desk, bypass);
            Log?.Invoke($"desk swap {(toDesk ? "to the desk" : "back to the guard")} on {ip}: "
                        + string.Join(", ", plan.Select(p => $"{(p.Open ? "open" : "mute")} Ch {p.Channel}")));
            using var x32 = new X32Mutes(ip, DeskPort);
            var failed = new List<string>();
            foreach (var (ch, open) in plan) await WriteMuteAsync(x32, ch, open, failed);
            var state = await ReadDeskStateAsync(x32, desk, bypass);
            if (failed.Count > 0)
                DeskMessage = $"desk: {string.Join(", ", failed)} did not take; look at the desk. {DeskMessage}";
            return failed.Count == 0 && state == (toDesk ? DeskState.Bypassed : DeskState.Guard);
        }
        finally
        {
            Volatile.Write(ref _deskBusy, 0);
            DeskBypassChanged?.Invoke();
        }
    }

    /// <summary>
    /// The writes a swap makes, in order. Whatever is about to carry the vocal
    /// opens first; whatever carried it mutes second. Pure, so the self-test can
    /// hold the order to account without a desk.
    /// </summary>
    public static (int Channel, bool Open)[] SwapPlan(bool toDesk, int[] desk, int[] bypass)
    {
        var open = toDesk ? bypass : desk;
        var mute = toDesk ? desk : bypass;
        return open.Select(ch => (ch, true)).Concat(mute.Select(ch => (ch, false))).ToArray();
    }

    /// <summary>Where the desk listens. The self-test points this at a fake X32 on loopback.</summary>
    public int DeskPort { get; set; } = 10023;

    private async Task WriteMuteAsync(X32Mutes x32, int ch, bool open, List<string> failed)
    {
        var ok = await x32.SetOpenAsync(ch, open);
        Log?.Invoke($"desk write {X32Mutes.Address(ch)} <- {(open ? 1 : 0)}: {(ok ? "read back as sent" : "DID NOT TAKE")}");
        if (!ok) failed.Add($"Ch {ch} {(open ? "open" : "mute")}");
    }

    private async Task<DeskState> ReadDeskStateAsync(X32Mutes x32, int[] desk, int[] bypass)
    {
        var deskOpen = new List<bool?>();
        var bypassOpen = new List<bool?>();
        foreach (var ch in desk) deskOpen.Add(await x32.ReadOpenAsync(ch));
        foreach (var ch in bypass) bypassOpen.Add(await x32.ReadOpenAsync(ch));

        DeskState state;
        if (deskOpen.Concat(bypassOpen).Any(v => v is null))
        {
            state = DeskState.Unknown;
            DeskMessage = "desk: no answer from the console; the mutes are whatever they were";
        }
        else if (deskOpen.All(v => v == true) && bypassOpen.All(v => v == false))
        {
            state = DeskState.Guard;
            DeskMessage = $"desk: guard path live. Ch {Join(desk)} open, Ch {Join(bypass)} muted";
        }
        else if (bypassOpen.All(v => v == true) && deskOpen.All(v => v == false))
        {
            state = DeskState.Bypassed;
            DeskMessage = $"desk: BYPASSED. Ch {Join(bypass)} open, Ch {Join(desk)} muted; the guard is out of the path";
        }
        else
        {
            var openNow = desk.Concat(bypass).Zip(deskOpen.Concat(bypassOpen)).Where(p => p.Second == true).Select(p => p.First);
            state = DeskState.Mixed;
            DeskMessage = $"desk: mixed. Open right now: Ch {string.Join(" ", openNow)}; look at the desk";
        }
        DeskBypassState = state;
        Log?.Invoke(DeskMessage);
        return state;
    }

    private static string Join(IEnumerable<int> channels) => string.Join("/", channels);

    private bool TryDeskSetup(out IPAddress ip, out int[] desk, out int[] bypass)
    {
        string? address;
        lock (_lock) { address = _consoleAddress; desk = _deskChannels; bypass = _bypassChannels; }
        if (desk.Length == 0 || desk.Length != bypass.Length || !IPAddress.TryParse(address, out var parsed) || parsed is null)
        {
            ip = IPAddress.None;
            return false;
        }
        ip = parsed;
        return true;
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
            if (on && !has && _enabled.Count < MaxChans) { _enabled.Add(channel); _returns.TryAdd(channel, NoReturn); }
            else if (!on && has) _enabled.Remove(channel);
            else return;
            _enabled.Sort();
        }
        PushRouting();
        SaveAudio();
        ChannelsChanged?.Invoke();
    }

    /// <summary>The return value that means "listen only": analyse and log, write nothing anywhere.</summary>
    public const int NoReturn = -1;

    /// <summary>
    /// Where an armed input's processed audio is returned, or <see cref="NoReturn"/>.
    /// On an insert rig this is the output that feeds the console - e.g. mic on
    /// ANALOG 5, return on ADAT 3. A newly armed input has no return until one
    /// is chosen: arming a microphone must never, by itself, put it on an output
    /// (an Apollo's first outputs are its monitors). Inputs saved by older
    /// versions without a return keep the mirror they always had.
    /// </summary>
    public int ReturnFor(int channel)
    {
        lock (_lock) { return _returns.GetValueOrDefault(channel, NoReturn); }
    }

    /// <summary>True when at least one armed input is written back to an output - the engine is in the audio path.</summary>
    public bool InPath
    {
        get { lock (_lock) { return _enabled.Any(i => _returns.GetValueOrDefault(i, NoReturn) >= 0); } }
    }

    /// <summary>How many armed inputs have a Return, and so are actually in the audio path.</summary>
    public int InPathCount
    {
        get { lock (_lock) { return _enabled.Count(i => _returns.GetValueOrDefault(i, NoReturn) >= 0); } }
    }

    public void SetReturn(int channel, int output)
    {
        lock (_lock) { _returns[channel] = output; }
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
            outputs = inputs.Select(i => _returns.GetValueOrDefault(i, NoReturn)).ToArray();
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
        int[] desk, bypass;
        lock (_lock)
        {
            names = _names.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            returns = _returns.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            desk = _deskChannels;
            bypass = _bypassChannels;
        }
        _audioStore.Save(new AudioSelection(_selectedDevice, EnabledSnapshot(), Enabled: true,
            Names: names, Returns: returns, MinHz: _minHz, MaxHz: _maxHz, FloorAuto: _floorAuto,
            FloorDb: _floorDb, Attack: _attack, MaxCutDb: _maxCutDb, HarmBudget: _harmBudget,
            ConsoleAddress: _consoleAddress, DeskChannels: desk, BypassChannels: bypass));
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

        // Record the audio too, pre-notch and post-notch. Asking the singer
        // whether the low end hollowed out is asking them to perform and audit at
        // the same time; this keeps the evidence so the listening can happen
        // afterwards. ~17 MB a minute.
        if (on)
        {
            RecordingPath = Path.Combine(_dataDir, "logs",
                $"audio-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            _supervisor.Client.SetRecord(RecordingPath);
        }
        else
        {
            _supervisor.Client.SetRecord("");
        }

        Log?.Invoke(on ? $"capture on: {Path.GetFileName(_log.CapturePath)} + audio" : "capture off");
        CaptureChanged?.Invoke(on);
    }

    /// <summary>Where the last (or current) flight recording is being written.</summary>
    public string? RecordingPath { get; private set; }

    public event Action<bool>? CaptureChanged;

    /// <summary>What the mic is hearing right now: level, note, voice or music.</summary>
    public FkContext Context { get; private set; } = new(-120f, 0f, 0, 0f);
    public event Action<FkContext>? ContextChanged;

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
                attack = _attack, maxCutDb = _maxCutDb, harmBudget = _harmBudget,
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
                     worstDb, worstHz, Context);
    }

    /// <summary>
    /// Near-misses, with the gate that stopped each one. This is the log that was
    /// missing: "it did not catch that" used to be answerable only by simulating
    /// the detector against a downsampled copy of the spectrum.
    /// </summary>
    /// <summary>The rescue duck acted: channel, depth (dB), the frequency it acted for.</summary>
    public event Action<int, float, float>? RescueDucked;

    private void OnRejection(FkRejection r)
    {
        _log.WriteReject(_clock.Elapsed.TotalSeconds, r.Channel, r);
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
            _supervisor.Client.SetParam("harmBudget", _harmBudget);
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

        // What we were actually DOING about this frequency at the moment we
        // caught it. The eq log records the deepest point in the band, which
        // is a different question and misled me: during a runaway at 7107 Hz
        // it reported -24.2 dB, and that cut turned out to be at 7544 Hz - two
        // bandwidths away, so the ring itself got almost nothing. "Detected"
        // and "suppressed" are separate facts and only one of them was written
        // down. Computed for every detection now, not only with capture on.
        // (An ESTIMATE: the notch table arrives at 10 Hz and carries no Q, so
        // this assumes a width. The recording's two channels are the truth.)
        FkNotch[] live;
        lock (_lock) { live = _latest[d.Channel] ?? Array.Empty<FkNotch>(); }
        var active = live.Where(n => n.Active && n.CurrentDb < -0.1f)
                         .Select(n => (n.FreqHz, n.CurrentDb))
                         .ToArray();
        var cutHere = NotchResponse.SumDb(active, d.Hz);
        var nearest = active.Length == 0 ? 0f
                    : active.OrderBy(n => Math.Abs(n.FreqHz - d.Hz)).First().FreqHz;

        // With capture on, detection keeps running while the guard is off, so the
        // guard column is what makes an on/off session comparable.
        if (CaptureEnabled)
            _log.WriteCapture(_clock.Elapsed.TotalSeconds, d.Channel, ! IsBypassed, d, cutHere, nearest);

        // enabled channels always cut, so a detection on an active slot was applied
        _log.Write(d, applied: ! IsBypassed, _clock.Elapsed.TotalSeconds, cutHere, nearest);
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

    // The device's channel names go to the app log once per listing, so "which
    // index is Mic 1" can be answered from the log instead of a screenshot.
    private void OnChannelListed(int index, string name)
    {
        bool changed;
        lock (_lock) { changed = !_channels.TryGetValue(index, out var old) || old != name; _channels[index] = name; }
        if (changed) Log?.Invoke($"input {index}: {name}");
        ChannelsChanged?.Invoke();
    }

    private void OnOutChannelListed(int index, string name)
    {
        bool changed;
        lock (_lock) { changed = !_outChannels.TryGetValue(index, out var old) || old != name; _outChannels[index] = name; }
        if (changed) Log?.Invoke($"output {index}: {name}");
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
