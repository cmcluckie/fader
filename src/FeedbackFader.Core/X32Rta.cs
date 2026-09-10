using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

using Fader.Shared;
namespace FeedbackFader;

/// <summary>
/// Reads the X32's 100-band RTA over OSC. Verified against a real X32 Rack
/// (firmware 2.07): the RTA arrives from <c>/batchsubscribe &lt;alias&gt;
/// /meters/15 0 0 &lt;factor&gt;</c> as a blob of one int32 word-count (50) then
/// 100 little-endian int16 band levels, where <c>dB = value / 256</c> (a silent
/// console reads -32768 = -128 dB on every band).
///
/// Like every X32 subscription it expires; it is renewed well inside 10 s.
///
/// The band-centre frequencies are modelled as a log-uniform 20 Hz - 20 kHz
/// spread (~1/10 octave); the exact centres should be confirmed with a known
/// tone at the rig (see the deliverable-5 list).
/// </summary>
public sealed class X32Rta : IAsyncDisposable
{
    public const string MeterPath = "/meters/15";
    // The batchsubscribe alias becomes the reply's OSC address, so it must start
    // with '/' or OscMessage.Parse rejects it (the X32 echoes the alias verbatim).
    public const string Alias = "/rta";
    public const int BandCount = 100;
    public const float FloorDb = -128f;

    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(8);

    private readonly IPEndPoint _console;
    private readonly UdpClient _udp;
    private readonly object _lock = new();
    private float[] _latest = Enumerable.Repeat(FloorDb, BandCount).ToArray();

    private CancellationTokenSource? _cts;
    private Task? _receive;
    private Task? _renew;

    public X32Rta(IPAddress address, int port = 10023)
    {
        _console = new IPEndPoint(address, port);
        _udp = new UdpClient(0);
    }

    public event Action<float[]>? FrameReceived;
    public event Action<string>? Error;

    public void Start(CancellationToken token = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Subscribe();
        _receive = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        _renew = Task.Run(() => RenewLoopAsync(_cts.Token), _cts.Token);
    }

    /// <summary>Latest RTA frame, one dBFS value per band (copy).</summary>
    public float[] Latest
    {
        get { lock (_lock) { return (float[]) _latest.Clone(); } }
    }

    /// <summary>The strongest band above <paramref name="thresholdDb"/>, or null.</summary>
    public (int Band, float FreqHz, float Db)? Peak(float thresholdDb = -60f)
    {
        var frame = Latest;
        var best = -1;
        var bestDb = thresholdDb;
        for (var i = 0; i < frame.Length; i++)
        {
            if (frame[i] > bestDb)
            {
                bestDb = frame[i];
                best = i;
            }
        }
        return best < 0 ? null : (best, BandHz(best), bestDb);
    }

    /// <summary>Approximate centre frequency of RTA band <paramref name="band"/> (0..99).</summary>
    public static float BandHz(int band) => 20f * MathF.Pow(1000f, band / (float) (BandCount - 1));

    private void Subscribe()
    {
        // alias, meter path, p1, p2, time factor (4 => ~fast). Renewed on a timer.
        Send(new OscMessage("/batchsubscribe", Alias, MeterPath, 0, 0, 4));
    }

    private void Send(OscMessage message)
    {
        try
        {
            var bytes = message.ToBytes();
            _udp.Send(bytes, bytes.Length, _console);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"send {message.Address}: {ex.Message}");
        }
    }

    private async Task RenewLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RenewInterval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Subscribe();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(token);
                var message = OscMessage.Parse(result.Buffer, result.Buffer.Length);
                if (message?.Arguments is [byte[] blob] && Decode(blob) is { } frame)
                {
                    lock (_lock) { _latest = frame; }
                    FrameReceived?.Invoke(frame);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Error?.Invoke($"receive: {ex.Message}");
            }
        }
    }

    /// <summary>Blob: int32 word-count, then 100 little-endian int16 levels (dB = v/256).</summary>
    public static float[]? Decode(byte[] blob)
    {
        if (blob.Length < 4 + BandCount * 2)
        {
            return null;
        }
        var frame = new float[BandCount];
        for (var i = 0; i < BandCount; i++)
        {
            var raw = BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(4 + i * 2));
            frame[i] = raw / 256f;
        }
        return frame;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }
        foreach (var task in new[] { _receive, _renew })
        {
            if (task is not null)
            {
                try { await task; } catch { /* shutting down */ }
            }
        }
        _cts?.Dispose();
        _udp.Dispose();
    }
}
