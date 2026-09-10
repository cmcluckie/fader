using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Fader.Shared;

namespace FeedbackFader;

/// <summary>
/// One-shot read of the X32's 32 channel meters.
///
/// Unlike <see cref="X32Rta"/> this needs no subscription: sending
/// <c>/meters ,s "/meters/1"</c> returns a single blob immediately, which is
/// exactly right for a check that runs for two seconds and stops. Verified
/// against an X32 Rack (firmware 2.07): the blob is a little-endian int32 count
/// (96) followed by that many little-endian floats, 0..1 linear, of which the
/// first 32 are the input channels.
/// </summary>
public sealed class X32ChannelMeters : IDisposable
{
    public const int ChannelCount = 32;

    private readonly IPEndPoint _console;
    private readonly UdpClient _udp;

    public X32ChannelMeters(IPAddress address, int port = 10023)
    {
        _console = new IPEndPoint(address, port);
        _udp = new UdpClient(0);
    }

    /// <summary>
    /// Read the channel meters once. Returns null if the console does not reply -
    /// which is itself an answer worth reporting, not an error to swallow.
    /// </summary>
    public async Task<float[]?> ReadAsync(CancellationToken token = default)
    {
        try
        {
            var request = new OscMessage("/meters", "/meters/1").ToBytes();
            await _udp.SendAsync(request, request.Length, _console);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var reply = await _udp.ReceiveAsync(timeout.Token);
            return Decode(reply.Buffer);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Average of several reads, so one noisy frame can't decide the answer.</summary>
    public async Task<float[]?> ReadAveragedAsync(int reads, CancellationToken token = default)
    {
        var sum = new float[ChannelCount];
        var got = 0;
        for (var i = 0; i < reads; i++)
        {
            var frame = await ReadAsync(token);
            if (frame is null) continue;
            for (var c = 0; c < ChannelCount; c++) sum[c] += frame[c];
            got++;
            await Task.Delay(60, token);
        }
        if (got == 0) return null;
        for (var c = 0; c < ChannelCount; c++) sum[c] /= got;
        return sum;
    }

    private static float[]? Decode(byte[] packet)
    {
        foreach (var m in OscMessage.ParsePacket(packet, packet.Length))
        {
            if (m.Arguments is not [byte[] blob] || blob.Length < 4) continue;
            var count = BinaryPrimitives.ReadInt32LittleEndian(blob);
            if (count < ChannelCount || blob.Length < 4 + count * 4) continue;

            var levels = new float[ChannelCount];
            for (var c = 0; c < ChannelCount; c++)
                levels[c] = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(4 + c * 4));
            return levels;
        }
        return null;
    }

    public void Dispose() => _udp.Dispose();
}
