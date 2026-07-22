using System.Net;
using System.Net.Sockets;
using Fader.Bridge.Osc;

namespace Fader.Diagnostics.OscPing;

/// <summary>
/// Diagnostic #2: confirm the UDP/OSC path to the X32 Rack, then move channel 1's fader.
///
///   dotnet run --project diagnostics/OscPing -- 192.168.1.42
///   dotnet run --project diagnostics/OscPing -- 192.168.1.42 0.5
///
/// Runs four steps, each one proving something specific:
///   1. /info                  - the console is reachable and is an X32 (round-trip)
///   2. /ch/01/mix/fader       - query the current value
///   3. /ch/01/mix/fader ,f x  - set it (watch the physical fader move)
///   4. /ch/01/mix/fader       - query again to confirm the set landed
/// </summary>
internal static class Program
{
    private const int X32Port = 10023;
    private const string PlaceholderIp = "192.168.1.100";

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("X32 Rack - OSC path check");
        Console.WriteLine("=========================\n");

        var host = args.ElementAtOrDefault(0) ?? PlaceholderIp;
        if (args.Length == 0)
        {
            Console.WriteLine($"No IP given, using placeholder {PlaceholderIp}.");
            Console.WriteLine("If that is not your console, pass the real address:");
            Console.WriteLine("  dotnet run --project diagnostics/OscPing -- <x32-ip>\n");
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            Console.Error.WriteLine($"\"{host}\" is not a valid IP address.");
            return 1;
        }

        var level = 0.75f; // ~0 dB on the X32 fader taper
        if (args.ElementAtOrDefault(1) is { } levelArg &&
            !float.TryParse(levelArg, out level))
        {
            Console.Error.WriteLine($"\"{levelArg}\" is not a valid fader level (0.0 - 1.0).");
            return 1;
        }
        level = Math.Clamp(level, 0f, 1f);

        var target = new IPEndPoint(address, X32Port);

        // One socket for both directions: the X32 replies to the source port of
        // whatever packet it received, so send and receive must share it.
        using var udp = new UdpClient(0);
        udp.Client.ReceiveTimeout = 2000;
        Console.WriteLine($"Target       : {target}");
        Console.WriteLine($"Local port   : {((IPEndPoint)udp.Client.LocalEndPoint!).Port}\n");

        var reachable = await Step(udp, target, "1. Console identity",
            new OscMessage("/info"), expectReply: true);

        if (!reachable)
        {
            Console.Error.WriteLine("""

                No reply from the console. Check, in order:
                  - the IP is right (X32: Setup > Network on the console screen)
                  - the Mac and the X32 are on the same subnet
                  - you can reach it at all:  ping <x32-ip>
                  - no firewall is blocking outbound UDP 10023
                    (System Settings > Network > Firewall)
                  - nothing else already owns the console's OSC session
                    (X32-Edit / M32-Edit open on another machine)
                """);
            return 1;
        }

        await Step(udp, target, "2. Read current fader",
            new OscMessage("/ch/01/mix/fader"), expectReply: true);

        Console.WriteLine($"\n>>> Watch channel 1 on the console - setting fader to {level:F2}");
        await Step(udp, target, "3. Set fader",
            new OscMessage("/ch/01/mix/fader", level), expectReply: false);

        await Task.Delay(250);

        await Step(udp, target, "4. Read it back",
            new OscMessage("/ch/01/mix/fader"), expectReply: true);

        Console.WriteLine($"""

            If step 4 reported ~{level:F2}, the OSC path is good.

            Note for the bridge: X32 fader floats are not linear in dB.
            0.0 = -inf, ~0.75 = 0 dB, 1.0 = +10 dB. FaderScaling.McuToX32 below is
            the placeholder linear map - it is the one knob to tune once the
            faders physically track the console.
            """);

        return 0;
    }

    private static async Task<bool> Step(
        UdpClient udp, IPEndPoint target, string label, OscMessage message, bool expectReply)
    {
        var bytes = message.ToBytes();
        Console.WriteLine($"{label}");
        Console.WriteLine($"   -> {message}   ({bytes.Length} bytes)");

        await udp.SendAsync(bytes, bytes.Length, target);

        if (!expectReply)
        {
            return true;
        }

        try
        {
            var receive = udp.ReceiveAsync();
            var completed = await Task.WhenAny(receive, Task.Delay(2000));
            if (completed != receive)
            {
                Console.WriteLine("   <- (no reply within 2s)\n");
                return false;
            }

            var result = await receive;
            var reply = OscMessage.Parse(result.Buffer, result.Buffer.Length);
            Console.WriteLine(reply is null
                ? $"   <- unparseable, {result.Buffer.Length} bytes\n"
                : $"   <- {reply}\n");
            return true;
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"   <- socket error: {ex.SocketErrorCode}\n");
            return false;
        }
    }
}

/// <summary>
/// Placeholder fader mapping, promoted to its own type because it is the part
/// most likely to need tuning once both ends are talking.
/// </summary>
public static class FaderScaling
{
    /// <summary>MCU 14-bit fader position (0-16383) to X32 fader float (0.0-1.0).</summary>
    public static float McuToX32(int mcu)
    {
        // TUNE HERE. Straight linear for now: the FaderPort's physical taper and
        // the X32's fader float are both roughly "position", so this tracks
        // acceptably, but the dB curves differ. If the FaderPort feels wrong
        // against the console's dB readout, shape this (X32: 0.75 = 0 dB) rather
        // than adjusting anything downstream.
        return Math.Clamp(mcu / 16383f, 0f, 1f);
    }

    /// <summary>X32 fader float (0.0-1.0) back to an MCU 14-bit motor position.</summary>
    public static int X32ToMcu(float x32)
        => (int)Math.Round(Math.Clamp(x32, 0f, 1f) * 16383f);
}
