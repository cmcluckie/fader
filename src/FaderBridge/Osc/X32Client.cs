using System.Net;
using System.Net.Sockets;

namespace Fader.Bridge.Osc;

/// <summary>
/// UDP transport to the X32. Owns a single socket for both directions - the
/// console replies to the source port of whatever packet it received, so send
/// and receive must share it.
/// </summary>
public sealed class X32Client : IAsyncDisposable
{
    private readonly IPEndPoint _target;
    private readonly TimeSpan _keepaliveInterval;
    private readonly UdpClient _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _keepaliveLoop;

    public X32Client(IPAddress address, int port, TimeSpan keepaliveInterval)
    {
        _target = new IPEndPoint(address, port);
        _keepaliveInterval = keepaliveInterval;
        _udp = new UdpClient(0);
    }

    public event Action<OscMessage>? MessageReceived;
    public event Action<string>? Error;

    public IPEndPoint Target => _target;
    public int LocalPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public void Start(CancellationToken token)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        _keepaliveLoop = Task.Run(() => KeepaliveLoopAsync(_cts.Token), _cts.Token);
    }

    public void Send(OscMessage message)
    {
        try
        {
            var bytes = message.ToBytes();
            _udp.Send(bytes, bytes.Length, _target);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"send failed ({message.Address}): {ex.Message}");
        }
    }

    /// <summary>Query a parameter's current value (an address with no arguments).</summary>
    public void Query(string address) => Send(new OscMessage(address));

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(token);
                foreach (var message in OscMessage.ParsePacket(result.Buffer, result.Buffer.Length))
                {
                    MessageReceived?.Invoke(message);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // A transient ICMP port-unreachable surfaces here; keep listening.
                Error?.Invoke($"receive: {ex.SocketErrorCode}");
            }
            catch (Exception ex)
            {
                Error?.Invoke($"receive: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The X32 stops broadcasting parameter changes ~10 s after the last
    /// /xremote, so this must keep running for the whole session. Losing it is
    /// the classic "feedback worked for ten seconds then died" failure.
    /// </summary>
    private async Task KeepaliveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Send(new OscMessage("/xremote"));
            try
            {
                await Task.Delay(_keepaliveInterval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        foreach (var task in new[] { _receiveLoop, _keepaliveLoop })
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
