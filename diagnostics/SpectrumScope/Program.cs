using System.Text;
using FeedbackFader;

// Phase 4 display: the engine's per-mic spectrum and the X32's RTA on one shared
// log-frequency axis, with detections and engine/RTA correlations marked. The
// GUI window would draw the same two arrays; this is the terminal form, so the
// data path and the correlation are actually verifiable.
//
//   dotnet run --project diagnostics/SpectrumScope -- [x32-ip] [path-to-fk-engine]

const int Cols = 60;
const string Shades = " .:-=+*#%@";

Console.WriteLine("Spectrum scope — engine + X32 RTA, correlated");
Console.WriteLine("=============================================\n");

var ip = args.ElementAtOrDefault(0) ?? "192.168.9.113";
var binary = Path.GetFullPath(args.ElementAtOrDefault(1)
             ?? Path.Combine("engine", "build", "fk-engine_artefacts", "Release", "fk-engine"));

// --- [1] synthetic demo: prove the render + the correlation, independent of signal ---
Console.WriteLine("[1] render + correlation demo (synthetic)");
var demo = Enumerable.Repeat(X32Rta.FloorDb, X32Rta.BandCount).ToArray();
var demoBand = Correlator.NearestRtaBand(1000f);
demo[demoBand] = -14f;                         // a fake 1 kHz ring in the RTA
Console.WriteLine("    RTA   " + Bars(demo, Cols) + "  <- a peak at 1 kHz");
Console.WriteLine("          " + Ruler(Cols));
var demoMatch = new Correlator().Match(new FkDetection(0, 1000f, -22f), demo);
Console.WriteLine(demoMatch is null
    ? "    (no correlation?!)"
    : $"    CORRELATED: engine 1000 Hz -22 dB  <->  RTA band {demoMatch.RtaBand} ~{demoMatch.RtaHz:F0} Hz {demoMatch.RtaDb:F0} dB\n");

// --- [2] live: engine spectrum + console RTA on the same axis --------------------------
Console.WriteLine("[2] live (engine on the default device + console RTA)");
if (!File.Exists(binary))
{
    Console.WriteLine($"    engine not built at {binary} — skipping the live half.");
    return 0;
}

await using var supervisor = new EngineSupervisor(binary);
FkSpectrum? latestSpectrum = null;
var detections = new List<FkDetection>();
supervisor.Client.SpectrumReceived += s => { if (s.Channel == 0) latestSpectrum = s; };
supervisor.Client.DetectionReceived += detections.Add;
supervisor.Start();

await using var rta = new X32Rta(System.Net.IPAddress.Parse(ip));
var rtaFrames = 0;
rta.FrameReceived += _ => rtaFrames++;
rta.Start();

await Task.Delay(2500);

var rtaFrame = rta.Latest;
Console.WriteLine($"    engineOk={supervisor.EngineOk}  RTA frames={rtaFrames}");
if (latestSpectrum is { } spec)
{
    Console.WriteLine("    ENGINE " + Bars(ToLogAxis(spec), Cols) + "  (LEAD, resampled to log)");
}
Console.WriteLine("    RTA    " + Bars(rtaFrame, Cols));
Console.WriteLine("           " + Ruler(Cols));
Console.WriteLine("    (both at floor in silence — bars fill in when the mic/RTA see signal)");

var correlator = new Correlator();
var correlated = detections.Select(d => correlator.Match(d, rtaFrame)).Where(m => m is not null).ToList();
Console.WriteLine($"\n    engine detections: {detections.Count}   correlated with RTA: {correlated.Count}");
foreach (var m in correlated)
    Console.WriteLine($"      CORRELATED ch{m!.Channel} {m.EngineHz:F0} Hz <-> RTA {m.RtaHz:F0} Hz {m.RtaDb:F0} dB");

return 0;

// ---------------------------------------------------------------------------------------
static string Bars(float[] data, int cols, float minDb = -100f, float maxDb = 0f)
{
    var sb = new StringBuilder(cols);
    for (var c = 0; c < cols; c++)
    {
        var lo = c * data.Length / cols;
        var hi = Math.Max(lo + 1, (c + 1) * data.Length / cols);
        var peak = float.NegativeInfinity;
        for (var i = lo; i < hi && i < data.Length; i++) peak = MathF.Max(peak, data[i]);
        var t = Math.Clamp((peak - minDb) / (maxDb - minDb), 0f, 1f);
        sb.Append(Shades[(int) (t * (Shades.Length - 1))]);
    }
    return sb.ToString();
}

// Resample the engine's linear-frequency spectrum onto the RTA's 100 log bands.
static float[] ToLogAxis(FkSpectrum spec)
{
    var outp = new float[X32Rta.BandCount];
    for (var i = 0; i < X32Rta.BandCount; i++)
    {
        var bin = (int) MathF.Round(X32Rta.BandHz(i) / MathF.Max(1f, spec.HzPerBin));
        outp[i] = bin >= 0 && bin < spec.Magnitudes.Length ? spec.Magnitudes[bin] : -120f;
    }
    return outp;
}

// A ruler marking 100 Hz / 1 kHz / 10 kHz on the log axis.
static string Ruler(int cols)
{
    var row = new char[cols];
    Array.Fill(row, ' ');
    foreach (var (hz, label) in new[] { (100f, "100"), (1000f, "1k"), (10000f, "10k") })
    {
        var col = Correlator.NearestRtaBand(hz) * cols / X32Rta.BandCount;
        for (var k = 0; k < label.Length && col + k < cols; k++) row[col + k] = label[k];
    }
    return new string(row);
}
