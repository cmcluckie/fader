using System.Net;
using System.Net.Sockets;
using Fader.Shared;
using FeedbackFader;

// Measures ASG - added stable gain - by ringing the room on purpose.
//
//   dotnet run --project diagnostics/AutoRingOut -- <x32-ip> [options]
//
// For each run it raises the X32 main fader a step at a time, waits, and watches
// the CONSOLE'S OWN RTA for a sustained narrow peak. The moment it sees one it
// records the level and the frequency and pulls the fader straight back down.
// Half the runs are with the guard bypassed, half with it guarding; the median
// difference between the two is ASG, the only number that says whether any of
// this works.
//
// The detector is the console's RTA and not the engine on purpose: with the
// guard bypassed the engine stops analysing, so it cannot referee its own test.
//
// THIS DELIBERATELY DRIVES A ROOM INTO FEEDBACK. It never exceeds --ceiling, it
// drops the fader on a ring, on Ctrl-C, and on any exception - but it is still a
// loud test, and it belongs in an empty room with the monitors' limits in mind.
//
// The room must be quiet: music or talking looks like signal to the RTA and will
// be called a ring. The tool refuses to start if the room is not quiet.

const string X32MainFader = "/main/st/mix/fader";

// --taper: prove the dB <-> console mapping on its own, before it moves a fader.
if (args.Contains("--taper"))
{
    Console.WriteLine("  dB     -> float -> dB   (round trip through the X32 taper)");
    foreach (var db in new[] { -90f, -60f, -30f, -24f, -20f, -10f, -9f, -4f, -2f, 0f, 5f, 10f })
    {
        var f = X32Level.ToFloat(db);
        Console.WriteLine($"  {db,6:0.0} -> {f:0.0000} -> {X32Level.ToDb(f),6:0.0}   {(Math.Abs(X32Level.ToDb(f) - db) < 0.05f ? "ok" : "MISMATCH")}");
    }
    return 0;
}

var ipText = args.FirstOrDefault(a => !a.StartsWith('-'));
if (ipText is null || !IPAddress.TryParse(ipText, out var console))
{
    Console.Error.WriteLine("usage: AutoRingOut <x32-ip> [--start -24] [--ceiling -2] [--step 1] " +
                            "[--dwell 2.5] [--runs 3] [--prominence 25] [--level -85] [--sustain 1.2] [--keep-notches] [--yes]");
    return 2;
}

// --probe: ask the console where its main fader is, and say so in dB. Compare
// that with the number on the console's screen before trusting anything below.
if (args.Contains("--probe"))
{
    using var probeUdp = new UdpClient(0) { Client = { ReceiveTimeout = 2000 } };
    var q = new OscMessage(X32MainFader).ToBytes();
    probeUdp.Send(q, q.Length, new IPEndPoint(console, 10023));
    try
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        var reply = OscMessage.Parse(probeUdp.Receive(ref from), 1024);
        if (reply?.Arguments.FirstOrDefault() is float f)
            Console.WriteLine($"main fader = {f:0.0000} = {X32Level.ToDb(f):0.0} dB   (does that match the console screen?)");
        else Console.WriteLine($"unexpected reply: {reply?.Address}");
    }
    catch (SocketException) { Console.Error.WriteLine("no reply from the console."); return 1; }
    return 0;
}

float Opt(string name, float fallback)
{
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], out var v) ? v : fallback;
}
bool Flag(string name) => args.Contains("--" + name);

var startDb      = Opt("start", -24f);
var ceilingDb    = Opt("ceiling", -2f);
var stepDb       = Opt("step", 1f);
var dwellSec     = Opt("dwell", 2.5f);
var runs         = (int) Opt("runs", 3f);
var promDb       = Opt("prominence", 25f);     // measured: 8-11 dB in silence, 40-70 dB ringing
var sustainSec   = Opt("sustain", 1.2f);       // how long it must stand there
var minLevelDb   = Opt("level", -85f);         // measured: silence peaks -119 dB, rings -54 to -77
var keepNotches  = Flag("keep-notches");

// --watch: show what the console's RTA is actually reporting, without moving a
// fader. The first automated run fired at 7 kHz in a quiet room, so the
// thresholds get set from what this prints, not from a guess.
if (args.Contains("--watch"))
{
    await using var w = new X32Rta(console);
    var seen = 0;
    w.FrameReceived += frame =>
    {
        seen++;
        if (seen % 3 != 0) return;                       // ~2 Hz of print
        var sorted = frame.OrderBy(x => x).ToArray();
        var med = sorted[sorted.Length / 2];
        var idx = Enumerable.Range(0, frame.Length).OrderByDescending(i => frame[i]).Take(3).ToArray();
        var parts = idx.Select(i =>
        {
            var lo = Math.Max(0, i - 10); var hi = Math.Min(frame.Length - 1, i + 10);
            var local = Enumerable.Range(lo, hi - lo + 1).Where(j => Math.Abs(j - i) > 2)
                                  .Select(j => frame[j]).OrderBy(x => x).ToArray();
            var localMed = local.Length > 0 ? local[local.Length / 2] : med;
            return $"{X32Rta.BandHz(i),7:F0}Hz {frame[i],6:F1}dB  +{frame[i] - med,4:F1} global  +{frame[i] - localMed,4:F1} local";
        });
        Console.WriteLine($"  floor {med,6:F1} dB | " + string.Join(" | ", parts));
    };
    var loudest = (Db: -999f, Hz: 0f, Prom: 0f);
    w.FrameReceived += frame =>
    {
        var top = 0;
        for (var i = 1; i < frame.Length; i++) if (frame[i] > frame[top]) top = i;
        if (frame[top] <= loudest.Db) return;
        var lo = Math.Max(0, top - 10); var hi = Math.Min(frame.Length - 1, top + 10);
        var local = Enumerable.Range(lo, hi - lo + 1).Where(j => Math.Abs(j - top) > 2)
                              .Select(j => frame[j]).OrderBy(x => x).ToArray();
        loudest = (frame[top], X32Rta.BandHz(top), frame[top] - (local.Length > 0 ? local[local.Length / 2] : frame.Min()));
    };
    var secs = (int) Opt("seconds", 60f);
    w.Start();
    Console.WriteLine($"watching the RTA for {secs} s - RAISE THE FADER UNTIL IT RINGS, hold it, then pull it down\n");
    await Task.Delay(secs * 1000);
    Console.WriteLine($"\n{seen} frames ({seen / (float) secs:F1}/s)");
    Console.WriteLine($"LOUDEST seen: {loudest.Hz:F0} Hz at {loudest.Db:F1} dB, +{loudest.Prom:F1} above neighbours");
    return 0;
}


Console.WriteLine("Auto ring-out - ASG measurement");
Console.WriteLine("===============================\n");
Console.WriteLine($"console   : {console}:10023   main fader {startDb:0} dB -> ceiling {ceilingDb:0} dB in {stepDb:0} dB steps");
Console.WriteLine($"ring test : louder than {minLevelDb:0} dB, {promDb:0} dB above its neighbours, held {sustainSec:0.0} s");
Console.WriteLine($"runs      : {runs} bypassed + {runs} guarding, {dwellSec:0.0} s per step");
Console.WriteLine($"notches   : {(keepNotches ? "kept between runs" : "cleared before each guarded run")}\n");

if (!Flag("yes"))
{
    Console.Write("This will make the room feed back, repeatedly. Type GO to continue: ");
    if (Console.ReadLine()?.Trim() != "GO") { Console.WriteLine("aborted."); return 1; }
    Console.WriteLine();
}

using var cts = new CancellationTokenSource();
using var faderUdp = new UdpClient(0);
using var engineUdp = new UdpClient(0);
using var readUdp = new UdpClient(0) { Client = { ReceiveTimeout = 400 } };
var consoleEp = new IPEndPoint(console, 10023);
var engineEp = new IPEndPoint(IPAddress.Loopback, FkEngineClient.EnginePort);

void SetFader(float db)
{
    var msg = new OscMessage(X32MainFader, X32Level.ToFloat(db)).ToBytes();
    faderUdp.Send(msg, msg.Length, consoleEp);
}

// Where the fader ACTUALLY is. The operator keeps a hand on it and pulls it down
// when the feedback gets ugly - which would quietly inflate every guarded run, so
// each step checks the console rather than trusting what it was told to set.
float? ReadFader()
{
    try
    {
        var q = new OscMessage(X32MainFader).ToBytes();
        readUdp.Send(q, q.Length, consoleEp);
        var from = new IPEndPoint(IPAddress.Any, 0);
        var reply = OscMessage.Parse(readUdp.Receive(ref from), 1024);
        return reply?.Arguments.FirstOrDefault() is float f ? X32Level.ToDb(f) : null;
    }
    catch (SocketException) { return null; }
}

// Straight to the engine's control port. FkEngineClient binds the telemetry port,
// which the running app already owns, so this sends raw rather than borrowing it.
void SetBypass(bool on)
{
    var msg = new OscMessage("/fk/bypass", on ? 1 : 0).ToBytes();
    engineUdp.Send(msg, msg.Length, engineEp);
}
void ClearNotches()
{
    foreach (var ch in new[] { 0, 1 })
    {
        var msg = new OscMessage("/fk/clear", ch, 1).ToBytes();
        engineUdp.Send(msg, msg.Length, engineEp);
    }
}

// Whatever happens - a ring, Ctrl-C, an exception - the fader goes back down and
// the guard goes back on. This is the only part of this program that must never
// fail to run.
void Safe()
{
    try { SetFader(startDb); } catch { /* best effort */ }
    try { SetBypass(false); } catch { /* best effort */ }
}
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Safe(); cts.Cancel(); Console.WriteLine("\n  interrupted - fader down, guard back on"); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => Safe();

await using var rta = new X32Rta(console);
var detector = new RingWatch(promDb, sustainSec, minLevelDb);
var frames = 0;
rta.FrameReceived += f => { frames++; detector.Push(f); };
rta.Error += m => Console.WriteLine($"  [rta] {m}");
rta.Start(cts.Token);

SetFader(startDb);
await Task.Delay(1500, cts.Token);
if (frames == 0)
{
    Console.Error.WriteLine($"No RTA frames from {console}. Is the console reachable, and is /meters/15 allowed?");
    Safe();
    return 1;
}

// A quiet room is a precondition, not a nicety: music reads as a ring.
detector.Reset();
await Task.Delay(2000, cts.Token);
if (detector.Current is { } noise)
{
    Console.Error.WriteLine($"The room is not quiet - {noise.FreqHz:F0} Hz is already at {noise.LevelDb:F0} dB, " +
                            $"{noise.ProminenceDb:F0} dB above its neighbours. Stop the music and try again.");
    Safe();
    return 1;
}
Console.WriteLine($"room is quiet, {frames} RTA frames in 3.5 s - starting\n");

// --sweep: raise the fader step by step and simply REPORT what the RTA sees.
// No decision, no threshold - this is how the thresholds get calibrated, since
// guessing them produced a false ring at -24 and then no ring at all by -6.
if (args.Contains("--sweep"))
{
    Console.WriteLine("fader   peak band      level   +neighbours   (guard bypassed throughout)");
    SetBypass(true);
    try
    {
        for (var db = startDb; db <= ceilingDb + 0.001f && !cts.IsCancellationRequested; db += stepDb)
        {
            SetFader(db);
            await Task.Delay((int)(dwellSec * 1000), cts.Token);
            var frame = rta.Latest;
            var top = 0;
            for (var i = 1; i < frame.Length; i++) if (frame[i] > frame[top]) top = i;
            var lo = Math.Max(0, top - 10); var hi = Math.Min(frame.Length - 1, top + 10);
            var local = Enumerable.Range(lo, hi - lo + 1).Where(j => Math.Abs(j - top) > 2)
                                  .Select(j => frame[j]).OrderBy(x => x).ToArray();
            var reference = local.Length > 0 ? local[local.Length / 2] : frame.Min();
            Console.WriteLine($"{db,5:0} dB  {X32Rta.BandHz(top),7:F0} Hz  {frame[top],7:F1} dB   +{frame[top] - reference,5:F1} dB");
        }
    }
    finally { Safe(); }
    Console.WriteLine("\nfader back to start, guard back on");
    return 0;
}

var results = new List<(bool Guarded, float OnsetDb, float Hz)>();
try
{
    for (var run = 0; run < runs * 2 && !cts.IsCancellationRequested; run++)
    {
        var guarded = run % 2 == 1;                 // alternate, so drift hits both equally
        SetBypass(!guarded);
        if (guarded && !keepNotches) ClearNotches();

        SetFader(startDb);
        await Task.Delay(1200, cts.Token);
        detector.Reset();

        Console.Write($"run {run + 1,2}  {(guarded ? "GUARD ON " : "bypassed ")}");
        var onset = float.NaN;
        var hz = 0f; var level = 0f; var prom = 0f;
        var handMoved = false;

        for (var db = startDb; db <= ceilingDb + 0.001f && !cts.IsCancellationRequested; db += stepDb)
        {
            SetFader(db);
            Console.Write($"{db:0} ");
            var until = DateTime.UtcNow.AddSeconds(dwellSec);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(50, cts.Token);
                if (detector.Current is { } ring)
                {
                    onset = db; hz = ring.FreqHz; level = ring.LevelDb; prom = ring.ProminenceDb;
                    break;
                }
            }
            if (!float.IsNaN(onset)) break;

            if (ReadFader() is { } actual && actual < db - 0.5f)
            {
                handMoved = true;                       // the fader was pulled down under us
                break;
            }
        }

        SetFader(startDb);                          // down first, report second
        if (handMoved)
            Console.WriteLine("-> DISCARDED: the fader was moved by hand during this run");
        else if (float.IsNaN(onset))
            Console.WriteLine($"-> no ring by {ceilingDb:0} dB");
        else
        {
            Console.WriteLine($"-> RING at {onset:0} dB, {hz:F0} Hz ({level:F0} dB, +{prom:F0} over neighbours)");
            results.Add((guarded, onset, hz));
        }

        detector.Reset();
        await Task.Delay(3000, cts.Token);          // let the room and the guard settle
    }
}
catch (OperationCanceledException) { /* interrupted; Safe() already ran */ }
finally { Safe(); }

Console.WriteLine();
var off = results.Where(r => !r.Guarded).Select(r => r.OnsetDb).OrderBy(x => x).ToList();
var on  = results.Where(r =>  r.Guarded).Select(r => r.OnsetDb).OrderBy(x => x).ToList();
static float Median(List<float> v) => v.Count == 0 ? float.NaN
    : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2f;

Console.WriteLine($"bypassed : {(off.Count == 0 ? "no rings" : string.Join(", ", off.Select(x => $"{x:0}")) + $"  median {Median(off):0.0} dB")}");
Console.WriteLine($"guarding : {(on.Count == 0 ? "no rings" : string.Join(", ", on.Select(x => $"{x:0}")) + $"  median {Median(on):0.0} dB")}");

foreach (var g in results.GroupBy(r => r.Guarded))
    Console.WriteLine($"{(g.Key ? "guarded" : "bypassed"),-9} ring frequencies: {string.Join(", ", g.Select(r => $"{r.Hz:F0} Hz"))}");

if (off.Count > 0 && on.Count > 0)
{
    Console.WriteLine($"\nASG = {Median(on) - Median(off):0.0} dB   (guarding - bypassed, medians)");
    if (off.Count < 3 || on.Count < 3)
        Console.WriteLine("  few runs: treat the spread, not the median, as the result.");
}
else Console.WriteLine("\nASG not measurable: one side never rang. Raise --ceiling, or lower --start.");

return 0;

// ---------------------------------------------------------------------------

/// <summary>
/// A ring is a narrow peak that stands well above the rest of the band and stays
/// there. Music and speech move; a ring does not - which is the whole difference
/// between this and a level meter.
/// </summary>
sealed class RingWatch(float promDb, float sustainSec, float minLevelDb)
{
    public readonly record struct Ring(float FreqHz, float ProminenceDb, float LevelDb);

    private int _band = -1;
    private DateTime _since = DateTime.MaxValue;
    private Ring? _current;

    public Ring? Current => _current;

    public void Reset() { _band = -1; _since = DateTime.MaxValue; _current = null; }

    public void Push(float[] frame)
    {
        var top = 0;
        for (var i = 1; i < frame.Length; i++) if (frame[i] > frame[top]) top = i;

        // Prominence against the NEIGHBOURHOOD, not the whole band. Measured in
        // this room: hiss in silence is 15-30 dB above the global median but only
        // 5-11 dB above its own neighbours, so the global test fired on nothing.
        var lo = Math.Max(0, top - 10);
        var hi = Math.Min(frame.Length - 1, top + 10);
        var local = Enumerable.Range(lo, hi - lo + 1)
                              .Where(j => Math.Abs(j - top) > 2)
                              .Select(j => frame[j]).OrderBy(x => x).ToArray();
        var reference = local.Length > 0 ? local[local.Length / 2] : frame.Min();
        var prominence = frame[top] - reference;

        // And an absolute floor: the room's own noise is around -90 dB here, and
        // no amount of prominence makes that feedback.
        if (frame[top] < minLevelDb || prominence < promDb || (_band >= 0 && Math.Abs(top - _band) > 1))
        {
            Reset();
            return;
        }

        if (_band < 0) { _band = top; _since = DateTime.UtcNow; return; }
        _band = top;
        if ((DateTime.UtcNow - _since).TotalSeconds >= sustainSec)
            _current = new Ring(X32Rta.BandHz(top), prominence, frame[top]);
    }
}

/// <summary>
/// The X32's fader taper: four straight lines in dB, 0.75 = 0 dB, 1.0 = +10 dB.
/// Needed because this tool sets levels in dB, and the console speaks 0..1.
/// </summary>
static class X32Level
{
    public static float ToFloat(float db) => db switch
    {
        >= 0f   => Math.Min(1f, db / 40f + 0.75f),
        >= -10f => db / 40f + 0.75f,
        >= -30f => db / 80f + 0.625f,
        >= -60f => db / 160f + 0.4375f,
        _       => Math.Max(0f, db / 480f + 0.1875f),
    };

    public static float ToDb(float f) => f switch
    {
        >= 0.5f    => f * 40f - 30f,
        >= 0.25f   => f * 80f - 50f,
        >= 0.0625f => f * 160f - 70f,
        _          => f * 480f - 90f,
    };
}
