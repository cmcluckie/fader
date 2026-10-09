using System.Net;
using System.Net.Sockets;
using Fader.Shared;

namespace FeedbackFader;

/// <summary>
/// The one thing Feedback Fader is allowed to write on the X32: channel mutes,
/// and only when Chris clicks. Rule Zero (README) says the desk is production
/// and locked; this class exists so that the single permitted write is one
/// small, auditable place rather than a line somewhere in a view.
///
/// <c>/ch/NN/mix/on</c> is the X32's channel on-switch: 1 = open, 0 = muted.
/// Sent bare it is a query and the desk answers with the value; sent with an
/// int it is a set, which the desk does not acknowledge, so every set is read
/// back and a channel that did not take the value is reported as a failure,
/// never assumed.
///
/// Verified against nothing yet: the address and the 0/1 meaning are from the
/// X32 OSC protocol and the September read/write checks of other paths. The
/// first real use will be Chris's, on his go, with a backup taken first.
/// </summary>
public sealed class X32Mutes : IDisposable
{
    private readonly IPEndPoint _console;
    private readonly UdpClient _udp;

    public X32Mutes(IPAddress address, int port = 10023)
    {
        _console = new IPEndPoint(address, port);
        _udp = new UdpClient(0);
    }

    public static string Address(int channel) => $"/ch/{channel:D2}/mix/on";

    /// <summary>Is the channel open? Null when the desk did not answer.</summary>
    public async Task<bool?> ReadOpenAsync(int channel, CancellationToken token = default)
    {
        var address = Address(channel);
        try
        {
            var request = new OscMessage(address).ToBytes();
            await _udp.SendAsync(request, request.Length, _console);
            return await AwaitReplyAsync(address, TimeSpan.FromSeconds(1), token);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Open or mute one channel, then read it back. Returns true only when the
    /// desk reports the channel in the asked-for state.
    /// </summary>
    public async Task<bool> SetOpenAsync(int channel, bool open, CancellationToken token = default)
    {
        var address = Address(channel);
        try
        {
            var set = new OscMessage(address, open ? 1 : 0).ToBytes();
            await _udp.SendAsync(set, set.Length, _console);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(50, token);
                var state = await ReadOpenAsync(channel, token);
                if (state == open) return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Wait for the reply to THIS address; anything else that arrives on the
    // socket (a late reply to an earlier query) is discarded, not misread.
    private async Task<bool?> AwaitReplyAsync(string address, TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        while (true)
        {
            var reply = await _udp.ReceiveAsync(cts.Token);
            foreach (var m in OscMessage.ParsePacket(reply.Buffer, reply.Buffer.Length))
            {
                if (m.Address != address) continue;
                if (m.Arguments is [int on]) return on != 0;
                if (m.Arguments is [float onF]) return onF != 0f;
            }
        }
    }

    public void Dispose() => _udp.Dispose();
}
