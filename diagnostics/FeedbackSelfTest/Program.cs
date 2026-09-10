using FeedbackFader;

namespace Fader.Diagnostics.FeedbackSelfTest;

/// <summary>
/// Feedback Fader's offline self-test: the parts of the host side that are pure
/// enough to check without an engine, a console, or a room - persistence, the
/// CSV logs, the X32 GEQ/RTA codecs, and engine↔RTA correlation.
///
/// These checks used to live in the bridge's self-test, back when both products
/// were one binary. Nothing about them was ever about MIDI.
///
///   dotnet run --project diagnostics/FeedbackSelfTest
///
/// For the checks that need the real engine process, see FkPing.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Console.WriteLine("Feedback Fader self-test");
        Console.WriteLine("========================\n");

        Persistence();
        RtaAndGeq();
        Correlation();

        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Persistence()
    {
        Section("Persistence + logging");

        var dir = Path.Combine(Path.GetTempPath(), "fk-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FkNotchStore(Path.Combine(dir, "notches.json"));
            Check("empty store loads as empty", store.Load().Count == 0);

            store.Save(new List<StoredNotch> { new(0, 1200f, -6f, true), new(1, 3400f, -9f, false) });
            var loaded = store.Load();
            Check("store round-trips locked notches",
                loaded.Count == 2
                && loaded[0] is { Channel: 0, Manual: true } && Math.Abs(loaded[0].Hz - 1200f) < 0.01f
                && loaded[1] is { Channel: 1, Manual: false } && Math.Abs(loaded[1].DepthDb + 9f) < 0.01f);

            var corrupt = Path.Combine(dir, "corrupt.json");
            File.WriteAllText(corrupt, "{ not json ]");
            Check("corrupt store loads clean rather than throwing",
                new FkNotchStore(corrupt).Load().Count == 0);

            var log = new FkEventLog(Path.Combine(dir, "logs"), stamp: "test");
            log.Write(new FkDetection(0, 1234.56f, -20.5f), applied: true, seconds: 12.345);
            log.Write(new FkDetection(1, 800.0f, -30.0f), applied: false, seconds: 15.0);
            log.Dispose();

            var lines = File.ReadAllLines(log.Path);
            Check("csv header matches the plugin's",
                lines.Length >= 1 && lines[0] == "seconds,channel,frequency_hz,level_db,applied");
            Check("csv logs a LEAD detection as applied",
                lines.Length >= 2 && lines[1] == "12.345,LEAD,1234.56,-20.50,1");
            Check("csv logs a BGV detection as flagged-only",
                lines.Length >= 3 && lines[2] == "15.000,BGV,800.00,-30.00,0");

            var audio = new FkAudioStore(Path.Combine(dir, "audio.json"));
            Check("empty audio store has no inputs", audio.Load().Inputs.Length == 0);
            audio.Save(new AudioSelection("Universal Audio Thunderbolt", new[] { 2, 3, 6 }));
            var reloaded = audio.Load();
            Check("audio store round-trips device + enabled inputs",
                reloaded.Device == "Universal Audio Thunderbolt"
                && reloaded.Inputs.SequenceEqual(new[] { 2, 3, 6 }));

            // The console address is Feedback Fader's own setting since the split;
            // it used to be read out of the bridge's config.json.
            audio.Save(audio.Load() with { ConsoleAddress = "192.168.9.113" });
            Check("audio store round-trips the console address",
                audio.Load().ConsoleAddress == "192.168.9.113"
                && audio.Load().Device == "Universal Audio Thunderbolt");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    private static void RtaAndGeq()
    {
        Section("X32 RTA + GEQ (verified paths)");

        // GEQ par <-> dB (0.5 = flat, +/-15 dB).
        Check("GEQ 0.5 par is 0 dB", Math.Abs(X32Geq.ParToDb(0.5f)) < 0.001f);
        Check("GEQ -6 dB is 0.3 par", Math.Abs(X32Geq.DbToPar(-6f) - 0.3f) < 0.001f);
        Check("GEQ dB round-trips", Math.Abs(X32Geq.ParToDb(X32Geq.DbToPar(-9f)) + 9f) < 0.001f);
        Check("GEQ clamps to +/-15 dB", X32Geq.DbToPar(-30f) == 0f && X32Geq.DbToPar(30f) == 1f);

        Check("1000 Hz maps to band 18 (1 kHz)", X32Geq.NearestBand(1000f) == 18);
        Check("2100 Hz maps to band 21 (2 kHz)", X32Geq.NearestBand(2100f) == 21);
        Check("band address is /fx/5/par/07", X32Geq.Band(5, 7) == "/fx/5/par/07");
        Check("GEQ has 31 bands", X32Geq.BandHz.Length == 31);

        // RTA blob: int32 word-count, then 100 little-endian int16 (dB = v/256).
        var blob = new byte[4 + 100 * 2];
        BitConverter.GetBytes(50).CopyTo(blob, 0);
        BitConverter.GetBytes((short)(-20 * 256)).CopyTo(blob, 4 + 50 * 2);
        var frame = X32Rta.Decode(blob);
        Check("RTA decodes 100 bands", frame is { Length: 100 });
        Check("RTA scales dB = v/256", frame is not null && Math.Abs(frame[50] + 20f) < 0.01f);
        Check("RTA short blob rejected", X32Rta.Decode(new byte[8]) is null);

        Check("RTA band 0 is ~20 Hz", Math.Abs(X32Rta.BandHz(0) - 20f) < 0.5f);
        Check("RTA band 99 is ~20 kHz", Math.Abs(X32Rta.BandHz(99) - 20000f) < 50f);
        Check("RTA band centres ascend",
            X32Rta.BandHz(10) < X32Rta.BandHz(50) && X32Rta.BandHz(50) < X32Rta.BandHz(90));
    }

    private static void Correlation()
    {
        Section("Engine ↔ RTA correlation");

        // Correlation: an engine detection corroborated by an RTA peak.
        var corr = new Correlator(tolFraction: 0.06f, rtaThresholdDb: -50f);
        var floor = Enumerable.Repeat(X32Rta.FloorDb, X32Rta.BandCount).ToArray();
        Check("no RTA peak -> no correlation",
            corr.Match(new FkDetection(0, 1200f, -20f), floor) is null);

        var band = Correlator.NearestRtaBand(1200f);
        var hit = (float[]) floor.Clone();
        hit[band] = -18f;
        var match = corr.Match(new FkDetection(0, 1200f, -20f), hit);
        Check("engine + RTA at same freq -> correlated",
            match is not null && match.Channel == 0 && Math.Abs(match.RtaHz - 1200f) < 1200f * 0.06f);

        var elsewhere = (float[]) floor.Clone();
        elsewhere[Correlator.NearestRtaBand(500f)] = -18f;
        Check("RTA peak at a different freq -> no correlation",
            corr.Match(new FkDetection(0, 1200f, -20f), elsewhere) is null);

        var quiet = (float[]) floor.Clone();
        quiet[band] = -55f;
        Check("RTA peak below threshold -> no correlation",
            corr.Match(new FkDetection(0, 1200f, -20f), quiet) is null);

        Check("NearestRtaBand(1000) is ~1000 Hz",
            Math.Abs(X32Rta.BandHz(Correlator.NearestRtaBand(1000f)) - 1000f) < 40f);
    }

    // ------------------------------------------------------------------ output

    private static void Section(string title)
    {
        Console.WriteLine($"\n{title}");
        Console.WriteLine(new string('-', title.Length));
    }

    private static void Check(string description, bool ok)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  PASS  {description}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  FAIL  {description}");
        }
    }
}
