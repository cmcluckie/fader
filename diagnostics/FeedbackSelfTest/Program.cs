using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Fader.Shared;
using FeedbackFader;

namespace Fader.Diagnostics.FeedbackSelfTest;

/// <summary>
/// Feedback Fader's offline self-test: the parts of the host side that are pure
/// enough to check without an engine, a console, or a room - persistence, the
/// CSV logs, the X32 GEQ/RTA codecs, engine↔RTA correlation, and the desk
/// bypass against a fake X32 on loopback (the real one is production: Rule Zero).
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

    private static async Task<int> Main()
    {
        Console.WriteLine("Feedback Fader self-test");
        Console.WriteLine("========================\n");

        Persistence();
        RtaAndGeq();
        Correlation();
        await DeskBypass();

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

            // Ten columns since the capture work: detected and suppressed are
            // separate facts, and the gate, the refusal, the cut at that frequency,
            // the nearest filter and the age all travel with the row.
            var lines = File.ReadAllLines(log.Path);
            Check("csv header is the ten-column detection row",
                lines.Length >= 1 && lines[0] == "seconds,channel,frequency_hz,level_db,applied,gate,filter,cut_here_db,nearest_notch_hz,age_ms");
            Check("csv logs a LEAD detection as applied",
                lines.Length >= 2 && lines[1].StartsWith("12.345,LEAD,1234.56,-20.50,1,") && lines[1].Split(',').Length == 10);
            Check("csv logs a BGV detection as flagged-only",
                lines.Length >= 3 && lines[2].StartsWith("15.000,BGV,800.00,-30.00,0,") && lines[2].Split(',').Length == 10);

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

        // The engine's levels blob: uint16 count, then count little-endian floats.
        var lv = new byte[2 + 3 * 4];
        BitConverter.GetBytes((ushort) 3).CopyTo(lv, 0);
        BitConverter.GetBytes(0.5f).CopyTo(lv, 2);
        BitConverter.GetBytes(0.0f).CopyTo(lv, 6);
        BitConverter.GetBytes(1.0f).CopyTo(lv, 10);
        var decoded = FkEngineClient.DecodeLevels(lv);
        Check("levels blob decodes three inputs", decoded is { Length: 3 } && Math.Abs(decoded[0] - 0.5f) < 1e-6f && decoded[1] == 0f && decoded[2] == 1f);
        Check("levels short blob rejected", FkEngineClient.DecodeLevels(new byte[] { 3, 0, 1 }) is null);
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

    /// <summary>
    /// The one X32 write the app has, proven on a desk that cannot be hurt. The
    /// fake answers a bare /ch/NN/mix/on with the value and takes an int as a
    /// set, as the X32 does; it can also be told to ignore sets, which is what
    /// "the read-back must decide" is for.
    /// </summary>
    private static async Task DeskBypass()
    {
        Section("Desk bypass (fake X32 on loopback)");

        Check("mute address is /ch/01/mix/on", X32Mutes.Address(1) == "/ch/01/mix/on");
        Check("mute address pads to two digits", X32Mutes.Address(12) == "/ch/12/mix/on");

        Check("channels parse space-separated", FeedbackController.ParseChannels("1 2") is [1, 2]);
        Check("channels parse comma-separated", FeedbackController.ParseChannels("11, 12") is [11, 12]);
        Check("blank parses as none", FeedbackController.ParseChannels("  ") is { Length: 0 });
        Check("channel 0 rejected", FeedbackController.ParseChannels("0 2") is null);
        Check("channel 33 rejected", FeedbackController.ParseChannels("33") is null);
        Check("a repeated channel rejected", FeedbackController.ParseChannels("1 1") is null);
        Check("words rejected", FeedbackController.ParseChannels("lead") is null);

        var toDesk = FeedbackController.SwapPlan(true, new[] { 1, 2 }, new[] { 11, 12 });
        Check("to the desk: spares open before the guarded channels mute",
            toDesk is [(11, true), (12, true), (1, false), (2, false)]);
        var back = FeedbackController.SwapPlan(false, new[] { 1, 2 }, new[] { 11, 12 });
        Check("back to the guard: guarded channels open before the spares mute",
            back is [(1, true), (2, true), (11, false), (12, false)]);

        using var desk = new FakeDesk();
        using var mutes = new X32Mutes(IPAddress.Loopback, desk.Port);

        Check("reads an open channel", await mutes.ReadOpenAsync(1) == true);
        Check("reads a muted channel", await mutes.ReadOpenAsync(11) == false);

        desk.NoiseBeforeReply = true;
        Check("an unrelated reply on the socket is not mistaken for the answer", await mutes.ReadOpenAsync(11) == false);
        desk.NoiseBeforeReply = false;

        Check("a mute is written and read back", await mutes.SetOpenAsync(1, false) && desk.On[1] == 0);
        Check("an open is written and read back", await mutes.SetOpenAsync(1, true) && desk.On[1] == 1);

        desk.IgnoreSets = true;
        Check("a set the desk did not take is reported, not assumed", !await mutes.SetOpenAsync(2, false) && desk.On[2] == 1);
        desk.IgnoreSets = false;

        desk.Sets.Clear();
        foreach (var (ch, open) in toDesk) await mutes.SetOpenAsync(ch, open);
        Check("the swap leaves the spares open and the guarded channels muted",
            desk.On[11] == 1 && desk.On[12] == 1 && desk.On[1] == 0 && desk.On[2] == 0);
        Check("the desk saw the writes in the planned order",
            desk.Sets.Select(s => (s.Channel, s.Value == 1)).SequenceEqual(toDesk));
        Check("nothing but the four named channels was written",
            desk.Sets.All(s => s.Channel is 1 or 2 or 11 or 12));

        foreach (var (ch, open) in back) await mutes.SetOpenAsync(ch, open);
        Check("the swap back restores every mute", desk.On[1] == 1 && desk.On[2] == 1 && desk.On[11] == 0 && desk.On[12] == 0);

        using var nobody = new X32Mutes(IPAddress.Loopback, FreePort());
        Check("no desk answers -> unknown, within a second", await nobody.ReadOpenAsync(1) is null);
    }

    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint) probe.Client.LocalEndPoint!).Port;
    }

    /// <summary>Just enough X32 to answer /ch/NN/mix/on: channels 1-10 open, the rest muted.</summary>
    private sealed class FakeDesk : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();
        private static readonly Regex Mute = new(@"^/ch/(\d\d)/mix/on$");

        public readonly Dictionary<int, int> On = new();
        public readonly List<(int Channel, int Value)> Sets = new();
        public bool IgnoreSets;
        public bool NoiseBeforeReply;

        public FakeDesk()
        {
            for (var c = 1; c <= 32; c++) On[c] = c <= 10 ? 1 : 0;
            _ = Task.Run(Loop);
        }

        public int Port => ((IPEndPoint) _udp.Client.LocalEndPoint!).Port;

        private async Task Loop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var r = await _udp.ReceiveAsync(_cts.Token);
                    foreach (var m in OscMessage.ParsePacket(r.Buffer, r.Buffer.Length))
                    {
                        var match = Mute.Match(m.Address);
                        if (!match.Success) continue;
                        var ch = int.Parse(match.Groups[1].Value);
                        if (m.Arguments is [int v])
                        {
                            lock (Sets) Sets.Add((ch, v));
                            if (!IgnoreSets) On[ch] = v;
                            continue;   // the X32 does not acknowledge a set
                        }
                        if (NoiseBeforeReply)
                        {
                            var noise = new OscMessage("/ch/06/mix/on", 1).ToBytes();
                            await _udp.SendAsync(noise, noise.Length, r.RemoteEndPoint);
                        }
                        var reply = new OscMessage(m.Address, On[ch]).ToBytes();
                        await _udp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Dispose();
        }
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
