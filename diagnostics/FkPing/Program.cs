using Fader.Bridge.Feedback;

// Smoke tests for the C# feedback side against the real engine:
//   A. plumbing        - spawn via the supervisor, telemetry + a control round-trip
//   B. persistence     - a locked notch survives an engine restart (Phase 2, §8)
//
//   dotnet run --project diagnostics/FkPing -- [path-to-fk-engine]

Console.WriteLine("FeedbackKiller - C# side smoke test");
Console.WriteLine("===================================\n");

var binary = args.ElementAtOrDefault(0)
             ?? Path.Combine("engine", "build", "fk-engine_artefacts", "Release", "fk-engine");
binary = Path.GetFullPath(binary);

if (!File.Exists(binary))
{
    Console.Error.WriteLine($"engine binary not found: {binary}");
    Console.Error.WriteLine("build it:  (cd engine && cmake -B build -G \"Unix Makefiles\" && cmake --build build)");
    return 1;
}
Console.WriteLine($"engine : {binary}\n");

var plumbingOk = await Plumbing(binary);
var persistOk = await Persistence(binary);

Console.WriteLine(plumbingOk && persistOk ? "\nALL OK" : "\nSOMETHING FAILED");
return plumbingOk && persistOk ? 0 : 1;

// --------------------------------------------------------------------------- A
static async Task<bool> Plumbing(string binary)
{
    Console.WriteLine("[A] plumbing");
    var supervisor = new EngineSupervisor(binary);
    var status = 0; var notchFrames = 0; var spectrumFrames = 0;
    FkNotch[] last = [];
    supervisor.Client.StatusReceived += _ => status++;
    supervisor.Client.NotchesReceived += (ch, n) => { if (ch == 0) { notchFrames++; last = n; } };
    supervisor.Client.SpectrumReceived += s => { if (s.Channel == 0) spectrumFrames++; };

    supervisor.Start();
    await Task.Delay(4000);
    supervisor.Client.PlaceNotch(0, 1200f, -6f);
    await Task.Delay(1200);

    var placed = last.FirstOrDefault(n => n.Active && Math.Abs(n.FreqHz - 1200f) < 5f);
    var ok = status > 0 && notchFrames > 0 && spectrumFrames > 0 && supervisor.EngineOk && placed.Active && placed.Locked;
    Console.WriteLine($"    status={status} notches={notchFrames} spectrum={spectrumFrames} engineOk={supervisor.EngineOk}");
    Console.WriteLine($"    placed 1200 Hz notch: active={placed.Active} locked={placed.Locked}");
    Console.WriteLine($"    => {(ok ? "OK" : "FAIL")}\n");

    await supervisor.DisposeAsync();
    await Task.Delay(500);   // let the engine's port free before scenario B
    return ok;
}

// --------------------------------------------------------------------------- B
static async Task<bool> Persistence(string binary)
{
    Console.WriteLine("[B] persistence across restart");
    var dir = Path.Combine(Path.GetTempPath(), "fkping-" + Guid.NewGuid().ToString("N"));

    try
    {
        // First session: place a locked notch, confirm it is persisted.
        var c1 = new FeedbackController(binary, dir);
        c1.Start();
        if (!await WaitFor(() => c1.EngineOk, 6000)) { Console.WriteLine("    engine never came up"); await c1.DisposeAsync(); return false; }

        c1.PlaceManualNotch(0, 1500f, -8f);
        await Task.Delay(1200);
        var stored = new FkNotchStore(Path.Combine(dir, "notches.json")).Load();
        var persisted = stored.Any(n => n.Channel == 0 && Math.Abs(n.Hz - 1500f) < 5f);
        Console.WriteLine($"    session 1: placed 1500 Hz, persisted={persisted} (store has {stored.Count})");
        await c1.DisposeAsync();
        await Task.Delay(500);

        // Second session, same data dir: the notch must be replayed to the fresh engine.
        var c2 = new FeedbackController(binary, dir);
        FkNotch[] ch0 = [];
        var frames = 0;
        c2.NotchesChanged += (ch, n) => { if (ch == 0) { ch0 = n; frames++; } };
        c2.Log += m => Console.WriteLine($"      [c2] {m}");
        c2.Start();
        var up = await WaitFor(() => c2.EngineOk, 8000);
        Console.WriteLine($"    session 2: engineOk={up}");
        var replayed = await WaitFor(() => ch0.Any(n => n.Active && n.Locked && Math.Abs(n.FreqHz - 1500f) < 5f), 5000);
        var anyActive = ch0.Count(n => n.Active);
        Console.WriteLine($"    session 2: notch frames={frames}, active notches now={anyActive}, 1500 Hz replayed={replayed}");
        await c2.DisposeAsync();
        await Task.Delay(300);

        var ok = persisted && replayed;
        Console.WriteLine($"    => {(ok ? "OK" : "FAIL")}");
        return ok;
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
    }
}

static async Task<bool> WaitFor(Func<bool> cond, int timeoutMs)
{
    var start = Environment.TickCount64;
    while (Environment.TickCount64 - start < timeoutMs)
    {
        if (cond()) return true;
        await Task.Delay(150);
    }
    return cond();
}
