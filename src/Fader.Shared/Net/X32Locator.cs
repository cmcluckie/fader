using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Fader.Shared.Net;

/// <summary>
/// Finds the X32 without the user having to type an IP every time.
///
/// Startup order (see <see cref="ResolveCore"/>):
///   1. the last address that answered  (remembered across runs)
///   2. the configured / hand-typed address, if given
///   3. a broadcast search of the LAN, the way X32-Edit finds consoles
///
/// Whatever answers is saved as the new "last", so a hand-typed address that
/// works becomes the remembered one - manual entry and auto-discovery reconcile
/// instead of fighting. All probing is a real /info round-trip: an address only
/// counts as the console if something at it replies to OSC on the X32 port.
/// </summary>
public static class X32Locator
{
    public const int DefaultPort = 10023;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Resolve the console address using the store for memory, a config seed as
    /// the fallback, and a live broadcast search as the last resort. Returns null
    /// only if nothing on the network answers at all.
    /// </summary>
    public static async Task<IPAddress?> ResolveAsync(
        X32AddressStore store,
        string? configuredAddress,
        int port = DefaultPort,
        CancellationToken token = default)
    {
        var resolved = await ResolveCore(
            store.LoadLast(),
            configuredAddress,
            ip => ProbeAsync(ip, port, ProbeTimeout, token),
            () => DiscoverAsync(port, DiscoverTimeout, token));

        if (resolved is not null)
        {
            store.SaveLast(resolved.ToString());
        }

        return resolved;
    }

    /// <summary>
    /// The pure decision, with probing and discovery injected so it can be
    /// tested without a network. Tries last-known, then the seed, then discovery,
    /// skipping anything that doesn't parse and returning the first that answers.
    /// </summary>
    public static async Task<IPAddress?> ResolveCore(
        string? lastKnown,
        string? seed,
        Func<IPAddress, Task<bool>> probe,
        Func<Task<IPAddress?>> discover)
    {
        foreach (var candidate in new[] { lastKnown, seed })
        {
            if (IPAddress.TryParse(candidate, out var ip) && await probe(ip))
            {
                return ip;
            }
        }

        return await discover();
    }

    /// <summary>Send /info to one address and report whether it replies in time.</summary>
    public static async Task<bool> ProbeAsync(
        IPAddress address, int port, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            using var udp = new UdpClient(0);
            var info = new OscMessage("/info").ToBytes();
            await udp.SendAsync(info, info.Length, new IPEndPoint(address, port));

            var reply = await ReceiveFromAsync(udp, address, timeout, token);
            return reply is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Broadcast /info across every up IPv4 interface and return the first
    /// address that answers. This is how X32-Edit locates a console: the reply
    /// comes back by unicast, so the responder's source address is the console.
    /// </summary>
    public static async Task<IPAddress?> DiscoverAsync(
        int port, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            using var udp = new UdpClient(0) { EnableBroadcast = true };
            var info = new OscMessage("/info").ToBytes();

            foreach (var target in BroadcastTargets())
            {
                try
                {
                    await udp.SendAsync(info, info.Length, new IPEndPoint(target, port));
                }
                catch
                {
                    // A single interface refusing broadcast (e.g. a down VPN
                    // adapter) must not abort the search on the others.
                }
            }

            // Accept a reply from anyone - the source address is the console.
            var reply = await ReceiveFromAsync(udp, expected: null, timeout, token);
            return reply?.Address;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>255.255.255.255 plus each interface's directed broadcast.</summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        yield return IPAddress.Broadcast;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork ||
                    ua.IPv4Mask is null)
                {
                    continue;
                }

                var directed = DirectedBroadcast(ua.Address, ua.IPv4Mask);
                if (directed is not null)
                {
                    yield return directed;
                }
            }
        }
    }

    private static IPAddress? DirectedBroadcast(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4)
        {
            return null;
        }

        var b = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            b[i] = (byte)(a[i] | ~m[i]);
        }
        return new IPAddress(b);
    }

    /// <summary>
    /// Wait for a UDP reply, optionally requiring it to come from a specific
    /// address, honouring both a timeout and the cancellation token.
    /// </summary>
    private static async Task<IPEndPoint?> ReceiveFromAsync(
        UdpClient udp, IPAddress? expected, TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(cts.Token);
                if (expected is null || result.RemoteEndPoint.Address.Equals(expected))
                {
                    return result.RemoteEndPoint;
                }
                // Otherwise keep waiting - a stray packet from something else.
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out or cancelled - no qualifying reply.
        }

        return null;
    }
}
