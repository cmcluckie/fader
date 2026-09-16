using System.Diagnostics;

namespace FeedbackFader;

/// <summary>
/// Owns the lifecycle of the headless feedback engine: launches it, keeps a
/// telemetry subscription renewed and pings it for liveness, and restarts it
/// with backoff if it dies. Per the ADR the engine is stateless across restarts,
/// so callers replay locked notches and settings through <see cref="Client"/>
/// after each (re)start via the <see cref="EngineStarted"/> hook.
/// </summary>
public sealed class EngineSupervisor : IAsyncDisposable
{
    private readonly string _binaryPath;
    private readonly string _device;
    private readonly int _sampleRate;
    private readonly int _bufferSize;
    private readonly FkEngineClient _client = new();
    private readonly object _procLock = new();

    private Process? _proc;
    private Timer? _health;
    private CancellationTokenSource? _cts;
    private long _lastStatusTicks;
    private int _restarts;
    private volatile bool _stopping;

    // Renew well under the 5 s telemetry timeout; a 6 s status gap means dead.
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(2);
    private const long StatusTtlMs = 6000;

    public EngineSupervisor(string binaryPath, string? device = null, int sampleRate = 48000, int bufferSize = 64)
    {
        _binaryPath = binaryPath;
        _device = device ?? "";
        _sampleRate = sampleRate;
        _bufferSize = bufferSize;
    }

    public FkEngineClient Client => _client;

    /// <summary>True when the engine is running audio (telemetry reports it live).</summary>
    public bool EngineOk { get; private set; }

    /// <summary>
    /// True when the engine PROCESS is up, regardless of whether audio is running.
    /// Lets the UI tell "engine crashed" apart from "engine fine, no interface
    /// selected yet" - two states EngineOk alone collapses into one.
    /// </summary>
    public bool ProcessAlive
    {
        get { lock (_procLock) { return _proc is not null && !_proc.HasExited; } }
    }

    public event Action<string>? Log;
    public event Action<bool>? EngineOkChanged;

    /// <summary>Raised after each (re)start so the caller can replay notches/mode/params.</summary>
    public event Action? EngineStarted;

    public void Start(CancellationToken token = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _client.StatusReceived += OnStatus;
        _client.Error += m => Log?.Invoke($"[fk-osc] {m}");
        _client.Start(_cts.Token);

        KillStrayEngines();   // clear any orphan left by a crash of an older build
        Spawn();
        _health = new Timer(_ => Health(), null, HealthInterval, HealthInterval);
    }

    private void OnStatus(FkStatus status)
    {
        Interlocked.Exchange(ref _lastStatusTicks, Environment.TickCount64);
        if (status.EngineOk)
        {
            _restarts = 0;   // sustained health clears the backoff
        }
        if (status.EngineOk != EngineOk)
        {
            EngineOk = status.EngineOk;
            EngineOkChanged?.Invoke(EngineOk);
        }
    }

    private void Spawn()
    {
        lock (_procLock)
        {
            if (_stopping)
            {
                return;
            }

            try
            {
                var psi = new ProcessStartInfo(_binaryPath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // Held open for the engine's lifetime as a dead-man's switch:
                    // if this app dies, the OS closes the pipe, the engine reads
                    // EOF and exits, so it can never be orphaned. See EngineMain.
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                };
                psi.Environment["FK_PARENT_WATCH"] = "1";
                psi.ArgumentList.Add(_device);                 // "" = default Core Audio device
                psi.ArgumentList.Add(_sampleRate.ToString());
                psi.ArgumentList.Add(_bufferSize.ToString());

                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) => { if (e.Data is { } d) Log?.Invoke($"[engine] {d}"); };
                p.ErrorDataReceived  += (_, e) => { if (e.Data is { } d) Log?.Invoke($"[engine] {d}"); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _proc = p;
                Interlocked.Exchange(ref _lastStatusTicks, Environment.TickCount64);  // grace window
                Log?.Invoke($"engine launched (pid {p.Id})");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"engine launch failed: {ex.Message}");
                return;
            }
        }

        // Subscribe to telemetry; level-triggered, so it self-heals on drops.
        // The controller applies the device, inputs, and notches on engineOk.
        _client.Subscribe(FkTelemetry.All);
        EngineStarted?.Invoke();
    }

    private void Health()
    {
        if (_stopping)
        {
            return;
        }

        // Renew the subscription (< 5 s) and probe liveness.
        _client.Subscribe(FkTelemetry.All);
        _client.Ping();

        var procDead = false;
        lock (_procLock)
        {
            procDead = _proc is null || _proc.HasExited;
        }

        var since = Environment.TickCount64 - Interlocked.Read(ref _lastStatusTicks);
        if (procDead || since > StatusTtlMs)
        {
            Log?.Invoke(procDead ? "engine process gone; restarting" : $"engine silent {since} ms; restarting");
            if (EngineOk) { EngineOk = false; EngineOkChanged?.Invoke(false); }
            Restart();
        }
    }

    private void Restart()
    {
        KillProc();

        var backoffMs = (int) Math.Min(30_000, 1000 * Math.Pow(2, Math.Min(_restarts, 5)));
        _restarts++;
        Log?.Invoke($"restart in {backoffMs} ms (attempt {_restarts})");

        _ = Task.Delay(backoffMs).ContinueWith(_ =>
        {
            if (!_stopping) Spawn();
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Kill any fk-engine process still running before we spawn ours. Only this
    /// one app should ever run, and it owns the single engine it manages, so any
    /// engine already alive at startup is an orphan from a previous crash - and
    /// an orphan holding the audio device would block the engine we are about to
    /// start. The dead-man's-switch (FK_PARENT_WATCH) prevents new orphans; this
    /// sweeps up ones left by builds that predate it.
    /// </summary>
    private void KillStrayEngines()
    {
        var name = Path.GetFileNameWithoutExtension(_binaryPath);
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                Log?.Invoke($"killing stray engine pid {p.Id}");
                p.Kill();
                p.WaitForExit(1000);
            }
            catch { /* gone already, or not ours to kill */ }
            finally { p.Dispose(); }
        }
    }

    private void KillProc()
    {
        Process? p;
        lock (_procLock)
        {
            p = _proc;
            _proc = null;
        }
        if (p is null)
        {
            return;
        }

        try
        {
            if (!p.HasExited)
            {
                // Ask first, on every platform: on /fk/quit the engine closes the
                // audio device and exits. Windows has no SIGTERM to fall back on,
                // and a hard kill skips closing the device.
                _client.Quit();

                // An engine too wedged to read OSC can still take a signal.
                if (!p.WaitForExit(800) && !OperatingSystem.IsWindows())
                {
                    try { using var k = Process.Start("/bin/kill", $"-TERM {p.Id}"); k?.WaitForExit(300); }
                    catch { /* fall through to Kill */ }
                    p.WaitForExit(500);
                }

                if (!p.HasExited)
                {
                    p.Kill();
                }
            }
        }
        catch { /* already gone */ }
        finally { p.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;

        if (_health is not null)
        {
            await _health.DisposeAsync();
            _health = null;
        }

        KillProc();

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }
        await _client.DisposeAsync();
        _cts?.Dispose();
    }
}
