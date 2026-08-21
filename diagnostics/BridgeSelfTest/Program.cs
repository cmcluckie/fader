using System.Net;
using Fader.Bridge;
using Fader.Bridge.Feedback;
using Fader.Bridge.Midi;
using Fader.Bridge.Osc;

namespace Fader.Diagnostics.BridgeSelfTest;

/// <summary>
/// Exercises the real BridgeHost against a mock console over a real UDP socket,
/// with a fake control surface in place of the FaderPort. Covers the behaviours
/// that are easy to get subtly wrong and hard to spot by eye: fader-touch
/// gating, the inverted mute sense, bank windowing, and scaling round-trips.
///
///   dotnet run --project diagnostics/BridgeSelfTest
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        Console.WriteLine("Bridge self-test");
        Console.WriteLine("================\n");

        ScalingRoundTrip();
        OscEncodingIsSpecCorrect();
        ScribbleSysExLayout();
        FeedbackPersistence();
        RtaAndGeq();
        await BridgeBehaviour();
        await SilentSceneRecallRecovery();

        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- pure

    private static void ScalingRoundTrip()
    {
        Section("Fader scaling");

        Check("MCU 0 -> 0.0", FaderScaling.McuToX32(0) == 0f);
        Check("MCU 16383 -> 1.0", FaderScaling.McuToX32(16383) == 1f);
        Check("X32 0.75 -> MCU 12287", FaderScaling.X32ToMcu(0.75f) == 12287);

        // The inverse must be exact, or values creep every time they round-trip
        // console -> bridge -> motor -> console.
        var stable = true;
        for (var mcu = 0; mcu <= 16383; mcu += 97)
        {
            if (FaderScaling.X32ToMcu(FaderScaling.McuToX32(mcu)) != mcu)
            {
                stable = false;
                break;
            }
        }
        Check("round-trip is exact across the whole range (no creep)", stable);

        Check("out-of-range clamps", FaderScaling.X32ToMcu(2f) == 16383 &&
                                     FaderScaling.X32ToMcu(-1f) == 0);
    }

    private static void OscEncodingIsSpecCorrect()
    {
        Section("OSC encoding");

        var bytes = new OscMessage("/ch/01/mix/fader", 0.75f).ToBytes();
        Check("4-byte aligned", bytes.Length % 4 == 0);
        Check("expected length 28", bytes.Length == 28);
        Check("float is big-endian 3F400000",
            Convert.ToHexString(bytes[^4..]) == "3F400000");
        Check("typetag \",f\" padded",
            Convert.ToHexString(bytes[20..24]) == "2C660000");

        var parsed = OscMessage.Parse(bytes, bytes.Length);
        Check("round-trips", parsed?.Address == "/ch/01/mix/fader" &&
                             parsed.Arguments.FirstOrDefault() is 0.75f);

        // An int argument must not be silently encoded as a float - the mute
        // path depends on the distinction.
        var intMsg = new OscMessage("/ch/01/mix/on", 0).ToBytes();
        var intParsed = OscMessage.Parse(intMsg, intMsg.Length);
        Check("int stays an int", intParsed?.Arguments.FirstOrDefault() is 0);

        Check("malformed input returns null rather than throwing",
            OscMessage.Parse(new byte[] { 0x01, 0x02, 0x03 }, 3) is null);

        Check("channel address parses",
            X32Address.TryParseChannel("/ch/07/mix/fader", out var ch, out var p)
            && ch == 7 && p == "mix/fader");

        Check("non-channel address rejected",
            !X32Address.TryParseChannel("/-stat/solosw/01", out _, out _));
    }

    private static void ScribbleSysExLayout()
    {
        Section("MCU scribble strip");

        var sysex = McuProtocol.ScribbleText(0, 0, "Kick");
        Check("starts F0 00 00 66 14 12",
            Convert.ToHexString(sysex[..6]) == "F000006614 12".Replace(" ", ""));
        Check("terminated F7", sysex[^1] == 0xF7);
        Check("strip 0 upper row is offset 0", sysex[6] == 0);
        Check("padded to 7 chars", sysex.Length == 8 + 7);

        Check("strip 3 upper row is offset 21", McuProtocol.ScribbleText(3, 0, "x")[6] == 21);
        Check("strip 0 lower row is offset 56", McuProtocol.ScribbleText(0, 1, "x")[6] == 56);

        Check("over-long text is truncated, not overflowed",
            McuProtocol.ScribbleText(0, 0, "Overlong name").Length == 15);

        // X32 channel names are free text; high bytes would corrupt the display.
        var accented = McuProtocol.ScribbleText(0, 0, "Café");
        Check("non-ASCII replaced with space", accented.Skip(7).Take(7).All(b => b is >= 0x20 and <= 0x7E));

        Check("decodes back", McuProtocol.TryDecodeScribble(sysex)?.Trim() == "Kick");

        // Whole-row write (used by the now-playing marquee): one SysEx, 56 chars.
        var line = McuProtocol.ScribbleLine(0, "Hello");
        Check("scribble line header + offset 0",
            Convert.ToHexString(line[..7]) == "F0000066141200");
        Check("scribble line is 56 chars wide", line.Length == 8 + McuProtocol.RowWidth);
        Check("scribble line row 1 is offset 56", McuProtocol.ScribbleLine(1, "x")[6] == 56);
    }

    private static void FeedbackPersistence()
    {
        Section("Feedback persistence + logging");

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

        // Headamp (§7 trap): local input source-1 = headamp index.
        Check("ch source 3 -> headamp index 2", X32Headamp.HeadampForSource(3) == 2);
        Check("headamp address is /headamp/002/gain", X32Headamp.Gain(2) == "/headamp/002/gain");
        Check("non-local source has no local headamp", X32Headamp.HeadampForSource(40) == -1);
        Check("headamp 0.5833 par is ~+30 dB", Math.Abs(X32Headamp.ParToDb(0.5833f) - 30f) < 0.1f);
        Check("headamp dB round-trips", Math.Abs(X32Headamp.ParToDb(X32Headamp.DbToPar(24f)) - 24f) < 0.01f);

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

    // ------------------------------------------------------------ integration

    private static async Task BridgeBehaviour()
    {
        Section("Bridge behaviour (real UDP, real BridgeHost)");

        using var mock = new MockConsole();
        mock.Start();

        var config = new BridgeConfig
        {
            X32IpAddress = "127.0.0.1",
            X32Port = mock.Port,
            StripCount = 8,
            X32ChannelCount = 32,
            EchoSuppressionMs = 150,
        };

        var surface = new FakeSurface();
        await using var client = new X32Client(
            IPAddress.Loopback, mock.Port, TimeSpan.FromSeconds(9));
        client.Start(CancellationToken.None);

        var bridge = new BridgeHost(config, surface, client);
        bridge.Start();
        await Settle();

        // --- startup ---------------------------------------------------------
        Check("queries the console on startup",
            mock.Received.Any(m => m.Address == X32Address.Fader(1) && m.Arguments.Length == 0));
        Check("keepalive /xremote sent",
            mock.Received.Any(m => m.Address == "/xremote"));
        Check("channel name reaches the scribble strip",
            surface.Scribbles.GetValueOrDefault((0, 0)) == "Vox1");

        // --- surface -> console ---------------------------------------------
        surface.MoveFader(2, 12287);
        await Settle();
        Check("fader move reaches the console as a float",
            mock.Get(X32Address.Fader(3)) is float f && Math.Abs(f - 0.75f) < 0.001f);

        // The master encoder (channel-8 pitch bend) is inert: it must not route
        // its own value nor flip layers - the Record button does that.
        surface.MoveFader(McuProtocol.MasterFaderChannel, 12287);
        await Settle();
        surface.MoveFader(0, 8000);
        await Settle();
        Check("the master encoder stays inert - strip 1 still drives its channel, not the main bus",
            mock.Get(X32Address.MainFader) is null &&
            mock.Get(X32Address.Fader(1)) is float);

        // Record flips the eight faders to the Master layer: strip 0 = main LR,
        // strips 1-7 = mix buses 1-7, and the Record lamp lights.
        surface.PressButton(McuProtocol.Record);
        await Settle();
        Check("Record lights its lamp when entering the Master layer",
            surface.Leds.GetValueOrDefault(McuProtocol.Record) is true);

        surface.MoveFader(0, 16383);
        await Settle();
        Check("on the Master layer, strip 1 drives the main LR bus",
            mock.Get(X32Address.MainFader) is 1f);

        surface.MoveFader(2, 12287);
        await Settle();
        Check("on the Master layer, strip 3 drives mix bus 2",
            mock.Get(X32Address.BusFader(2)) is float b && Math.Abs(b - 0.75f) < 0.001f);

        surface.ClearLog();
        mock.Push(X32Address.BusFader(3), 0.4f);
        await Settle();
        Check("a bus change from the console drives its strip's motor",
            surface.MotorPositions.GetValueOrDefault(3) == FaderScaling.X32ToMcu(0.4f));

        // Record again toggles back to the channel layer and darkens the lamp.
        surface.PressButton(McuProtocol.Record);
        await Settle();
        Check("Record darkens its lamp when leaving the Master layer",
            surface.Leds.GetValueOrDefault(McuProtocol.Record) is false);

        surface.MoveFader(1, 0);
        await Settle();
        Check("back on the Channel layer, strip 2 drives its channel again, not a bus",
            mock.Get(X32Address.Fader(2)) is 0f);

        // --- transport buttons -> media commands -----------------------------
        TransportCommand? media = null;
        bridge.Transport += c => media = c;

        surface.PressButton(McuProtocol.FastForward);
        await Settle();
        Check("Fast-Forward raises a Next command", media == TransportCommand.Next);

        surface.PressButton(McuProtocol.Rewind);
        await Settle();
        Check("Rewind raises a Previous command", media == TransportCommand.Previous);

        surface.PressButton(McuProtocol.Play);
        await Settle();
        Check("Play raises a Play/Pause command", media == TransportCommand.PlayPause);

        // --- display override (a marquee owns the scribble strips) -----------
        // Channel 5 (strip 4 at bank offset 0); its name is not asserted elsewhere.
        bridge.SetDisplayOverride(true);
        mock.Push(X32Address.Name(5), "OVERRIDDEN");
        await Settle();
        Check("bridge withholds its labels while overridden",
            surface.Scribbles.GetValueOrDefault((4, 0)) != "OVERRIDDEN");

        bridge.SetDisplayOverride(false);
        await Settle();
        Check("releasing the override repaints the labels",
            surface.Scribbles.GetValueOrDefault((4, 0)) == "OVERRIDDEN");


        // --- console -> surface ---------------------------------------------
        surface.ClearLog();
        mock.Push(X32Address.Fader(1), 0.5f);
        await Settle();
        Check("console change drives the motor",
            surface.MotorPositions.GetValueOrDefault(0) == FaderScaling.X32ToMcu(0.5f));

        // --- touch gating: the headline behaviour ----------------------------
        surface.TouchFader(0, true);
        surface.ClearLog();
        mock.Push(X32Address.Fader(1), 0.9f);
        await Settle();
        Check("motor is NOT driven while the fader is touched",
            surface.MotorWritesFor(0) == 0);

        surface.TouchFader(0, false);
        await Settle();
        Check("motor resyncs to the console value on touch release",
            surface.MotorPositions.GetValueOrDefault(0) == FaderScaling.X32ToMcu(0.9f));

        // --- echo suppression -------------------------------------------------
        surface.ClearLog();
        surface.MoveFader(4, 8000);
        await Settle();
        mock.Push(X32Address.Fader(5), FaderScaling.McuToX32(8000));
        await Settle();
        Check("console echo of our own move does not re-drive the motor",
            surface.MotorWritesFor(4) == 0);

        // --- mute, with its inverted sense -----------------------------------
        mock.Push(X32Address.MixOn(1), 0);
        await Settle();
        Check("mix/on 0 (muted) LIGHTS the mute lamp",
            surface.Leds.GetValueOrDefault(McuProtocol.MuteBase) is true);

        mock.Push(X32Address.MixOn(1), 1);
        await Settle();
        Check("mix/on 1 (unmuted) darkens the mute lamp",
            surface.Leds.GetValueOrDefault(McuProtocol.MuteBase) is false);

        surface.PressButton(McuProtocol.MuteBase);
        await Settle();
        Check("pressing mute toggles the console to muted",
            mock.Get(X32Address.MixOn(1)) is 0);

        surface.PressButton(McuProtocol.MuteBase);
        await Settle();
        Check("pressing mute again unmutes",
            mock.Get(X32Address.MixOn(1)) is 1);

        // --- select ----------------------------------------------------------
        surface.PressButton(McuProtocol.SelectBase + 2);
        await Settle();
        Check("select sends a 0-based channel index",
            mock.Get(X32Address.SelectedIndex) is 2);

        // --- banking ----------------------------------------------------------
        surface.PressButton(McuProtocol.BankRight);
        await Settle();
        Check("bank right re-queries the new window",
            mock.Received.Any(m => m.Address == X32Address.Fader(9) && m.Arguments.Length == 0));
        Check("scribble strip follows the bank",
            surface.Scribbles.GetValueOrDefault((0, 0)) == "Vox9");

        surface.MoveFader(0, 16383);
        await Settle();
        Check("after banking, strip 1 drives console channel 9",
            mock.Get(X32Address.Fader(9)) is 1f);

        surface.ClearLog();
        mock.Push(X32Address.Fader(1), 0.25f);
        await Settle();
        Check("off-bank channel does not move any motor",
            surface.MotorWriteLog.Count == 0);

        surface.PressButton(McuProtocol.ChannelLeft);
        await Settle();
        Check("channel left shifts the window by one",
            surface.Scribbles.GetValueOrDefault((0, 0)) == "Vox8");

        // Bank navigation must not run off either end of the console.
        for (var i = 0; i < 10; i++)
        {
            surface.PressButton(McuProtocol.BankLeft);
        }
        await Settle();
        Check("bank clamps at the low end",
            surface.Scribbles.GetValueOrDefault((0, 0)) == "Vox1");

        for (var i = 0; i < 10; i++)
        {
            surface.PressButton(McuProtocol.BankRight);
        }
        await Settle();
        Check("bank clamps at the high end (last window is 25-32)",
            surface.Scribbles.GetValueOrDefault((0, 0)) == "Vox25" &&
            surface.Scribbles.GetValueOrDefault((7, 0)) == "Vox32");

        // --- select LEDs -----------------------------------------------------
        Check("selected channel is queried at startup, not only on press",
            mock.Received.Any(m => m.Address == X32Address.SelectedIndex &&
                                   m.Arguments.Length == 0));
    }

    /// <summary>
    /// A scene recall is the case the console does NOT reliably push. This
    /// mutates console state silently - no /xremote broadcast at all - and
    /// checks the periodic resync notices.
    /// </summary>
    private static async Task SilentSceneRecallRecovery()
    {
        Section("Scene-recall recovery (console changes without broadcasting)");

        using var mock = new MockConsole();
        mock.Start();

        var config = new BridgeConfig
        {
            X32IpAddress = "127.0.0.1",
            X32Port = mock.Port,
            StripCount = 8,
            X32ChannelCount = 32,
            ResyncSeconds = 0.4,   // shortened so the test isn't slow
        };

        var surface = new FakeSurface();
        await using var client = new X32Client(
            IPAddress.Loopback, mock.Port, TimeSpan.FromSeconds(9));
        client.Start(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var bridge = new BridgeHost(config, surface, client);
        bridge.Start(cts.Token);
        await Settle();

        // Change state the way a scene recall does: silently, no push.
        mock.SetSilently(X32Address.Fader(1), 0.8f);
        mock.SetSilently(X32Address.MixOn(2), 0);
        mock.SetSilently(X32Address.Name(3), "Snare");

        Check("surface is stale immediately after a silent recall",
            surface.MotorPositions.GetValueOrDefault(0) != FaderScaling.X32ToMcu(0.8f));

        await Task.Delay(1200); // let at least one resync land

        Check("resync recovers the fader position",
            surface.MotorPositions.GetValueOrDefault(0) == FaderScaling.X32ToMcu(0.8f));
        Check("resync recovers the mute lamp",
            surface.Leds.GetValueOrDefault(McuProtocol.MuteBase + 1) is true);
        Check("resync recovers the scribble strip",
            surface.Scribbles.GetValueOrDefault((2, 0)) == "Snare");

        // The resync must not stomp a fader the user is holding.
        surface.TouchFader(4, true);
        mock.SetSilently(X32Address.Fader(5), 0.1f);
        surface.ClearLog();
        await Task.Delay(1200);
        Check("resync does not fight a touched fader",
            surface.MotorWritesFor(4) == 0);

        cts.Cancel();
    }

    private static async Task Settle() => await Task.Delay(120);

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
