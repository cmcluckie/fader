using System.Net;
using System.Net.Sockets;
using Fader.Bridge.Osc;

// Verifies the X32 RTA + GEQ paths against a live console, read-only.
//
//   dotnet run --project diagnostics/RingOut -- <x32-ip> [geqSlot]
//
// It subscribes to the RTA and reports the strongest band mapped to a GEQ band,
// and reads a few GEQ bands to confirm they are flat. It does NOT change the
// console. Actually cutting bands during a ring-out affects the mains and needs
// the PA up, so that stays a rig-side action (see the README ring-out workflow).

Console.WriteLine("X32 ring-out path check (read-only)");
Console.WriteLine("===================================\n");

var ip = args.ElementAtOrDefault(0) ?? "192.168.9.113";
var geqSlot = int.TryParse(args.ElementAtOrDefault(1), out var s) ? s : 5;
if (!IPAddress.TryParse(ip, out var address))
{
    Console.Error.WriteLine($"\"{ip}\" is not a valid IP address.");
    return 1;
}
Console.WriteLine($"console : {ip}   GEQ slot: {geqSlot}\n");

// ---- RTA -------------------------------------------------------------------
await using var rta = new X32Rta(address);
var frames = 0;
rta.FrameReceived += _ => frames++;
rta.Error += m => Console.WriteLine($"  [rta] {m}");
rta.Start();

Console.WriteLine("[1] subscribing to /meters/15 for 2.5 s ...");
await Task.Delay(2500);

var peakAny = rta.Peak(X32Rta.FloorDb - 1f);     // strongest band regardless of level
Console.WriteLine($"    frames received : {frames}");
if (peakAny is { } p)
{
    var geqBand = X32Geq.NearestBand(p.FreqHz);
    Console.WriteLine($"    strongest band  : #{p.Band}  ~{p.FreqHz,7:F0} Hz  {p.Db,6:F1} dB");
    Console.WriteLine($"    -> nearest GEQ band {geqBand} (~{X32Geq.BandHz[geqBand - 1]:F0} Hz), address {X32Geq.Band(geqSlot, geqBand)}");
}
var ring = rta.Peak(-60f);
Console.WriteLine(ring is null
    ? "    no ring above -60 dB (expected in silence; push the mains to see real peaks)"
    : $"    RING above -60 dB at ~{ring.Value.FreqHz:F0} Hz");

// ---- GEQ read --------------------------------------------------------------
Console.WriteLine($"\n[2] reading GEQ slot {geqSlot} bands (expect ~0.5 = 0 dB, flat) ...");
using var udp = new UdpClient(0) { Client = { ReceiveTimeout = 1500 } };
var target = new IPEndPoint(address, 10023);
var geqOk = true;
foreach (var band in new[] { 1, 16, 31 })
{
    var value = QueryFloat(udp, target, X32Geq.Band(geqSlot, band));
    if (value is { } par)
    {
        Console.WriteLine($"    band {band,2}: par {par:F3}  = {X32Geq.ParToDb(par),5:F1} dB");
    }
    else
    {
        Console.WriteLine($"    band {band,2}: no reply");
        geqOk = false;
    }
}

var ok = frames > 0 && geqOk;
Console.WriteLine(ok
    ? "\nRTA streaming and GEQ readable — ring-out path verified (read-only)."
    : "\nSomething did not respond — check the IP and that the GEQ is on that slot.");
return ok ? 0 : 1;

static float? QueryFloat(UdpClient udp, IPEndPoint target, string address)
{
    try
    {
        var msg = new OscMessage(address).ToBytes();
        udp.Send(msg, msg.Length, target);
        var from = new IPEndPoint(IPAddress.Any, 0);
        var reply = udp.Receive(ref from);
        var parsed = OscMessage.Parse(reply, reply.Length);
        return parsed?.Arguments is [float f] ? f : null;
    }
    catch (SocketException)
    {
        return null;
    }
}
