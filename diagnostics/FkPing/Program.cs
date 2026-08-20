using Fader.Bridge.Feedback;

// Smoke test for the C# feedback-engine plumbing: spawn the engine via the
// supervisor, confirm telemetry flows, and round-trip a control command.
//
//   dotnet run --project diagnostics/FkPing -- [path-to-fk-engine]

Console.WriteLine("FeedbackKiller engine - supervisor smoke test");
Console.WriteLine("=============================================\n");

var binary = args.ElementAtOrDefault(0)
             ?? Path.Combine("engine", "build", "fk-engine_artefacts", "Release", "fk-engine");
binary = Path.GetFullPath(binary);

if (!File.Exists(binary))
{
    Console.Error.WriteLine($"engine binary not found: {binary}");
    Console.Error.WriteLine("build it first:  (cd engine && cmake -B build -G \"Unix Makefiles\" && cmake --build build)");
    return 1;
}

Console.WriteLine($"engine   : {binary}\n");

await using var supervisor = new EngineSupervisor(binary);
supervisor.Log += m => Console.WriteLine($"  {m}");
supervisor.EngineOkChanged += ok => Console.WriteLine($"  >>> engineOk = {ok}");

var client = supervisor.Client;

var statusCount = 0;
FkStatus? lastStatus = null;
client.StatusReceived += s => { lastStatus = s; statusCount++; };

var detections = 0;
client.DetectionReceived += d =>
{
    detections++;
    Console.WriteLine($"  DETECT ch{d.Channel} {d.Hz,7:F1} Hz  {d.LevelDb,6:F1} dB");
};

var notchFrames = 0;
var spectrumFrames = 0;
FkNotch[] lastNotches = [];
client.NotchesReceived += (ch, n) => { if (ch == 0) { notchFrames++; lastNotches = n; } };
client.SpectrumReceived += s => { if (s.Channel == 0) spectrumFrames++; };

supervisor.Start();

Console.WriteLine("\n[1] letting it run 4 s (expect engineOk, notch + spectrum frames)...");
await Task.Delay(4000);
Console.WriteLine($"    status frames : {statusCount}   engineOk={lastStatus?.EngineOk}  cpu={lastStatus?.CpuLoad:F3}");
Console.WriteLine($"    notch frames  : {notchFrames} (ch0)");
Console.WriteLine($"    spectrum frames: {spectrumFrames} (ch0)");

Console.WriteLine("\n[2] round-trip: place a locked -6 dB notch at 1200 Hz on ch0...");
client.SetMode(FkMode.Auto);      // manual placement works in any mode, but be explicit
client.PlaceNotch(0, 1200f, -6f);
await Task.Delay(1200);

var active = lastNotches.Where(n => n.Active).ToArray();
Console.WriteLine($"    active notches ch0: {active.Length}");
foreach (var n in active)
    Console.WriteLine($"      {n.FreqHz,7:F0} Hz  cur {n.CurrentDb,6:F2}  target {n.TargetDb,6:F2}  locked={n.Locked} manual={n.Manual}");

var ok = statusCount > 0 && notchFrames > 0 && spectrumFrames > 0
         && (lastStatus?.EngineOk ?? false)
         && active.Any(n => Math.Abs(n.FreqHz - 1200f) < 5f && n.Locked);

Console.WriteLine("\n[3] stopping (clean SIGTERM via supervisor dispose)...");
Console.WriteLine(ok ? "\nPHASE 1 PLUMBING OK" : "\nSOMETHING MISSING - see counts above");
return ok ? 0 : 1;
