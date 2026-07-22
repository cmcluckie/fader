using System.Net;
using System.Net.Sockets;
using Fader.Bridge.Osc;

namespace Fader.Diagnostics.BridgeSelfTest;

/// <summary>
/// Stands in for an X32 Rack on a loopback UDP port: holds channel state,
/// answers queries, and pushes unsolicited updates the way /xremote does.
/// </summary>
public sealed class MockConsole : IDisposable
{
    private readonly UdpClient _udp;
    private readonly Dictionary<string, object> _state = new();
    private readonly List<OscMessage> _received = new();
    private readonly object _lock = new();
    private IPEndPoint? _client;
    private CancellationTokenSource? _cts;

    public MockConsole()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        for (var ch = 1; ch <= 32; ch++)
        {
            _state[X32Address.Fader(ch)] = 0.0f;
            _state[X32Address.MixOn(ch)] = 1;      // 1 = unmuted
            _state[X32Address.Solo(ch)] = 0;
            _state[X32Address.Name(ch)] = $"Vox{ch}";
        }
    }

    public int Port { get; }

    public IReadOnlyList<OscMessage> Received
    {
        get { lock (_lock) { return _received.ToList(); } }
    }

    public object? Get(string address)
    {
        lock (_lock) { return _state.GetValueOrDefault(address); }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udp.ReceiveAsync(_cts.Token);
                    _client = result.RemoteEndPoint;
                    Handle(result.Buffer);
                }
                catch (OperationCanceledException) { return; }
                catch { /* keep serving */ }
            }
        });
    }

    private void Handle(byte[] buffer)
    {
        foreach (var msg in OscMessage.ParsePacket(buffer, buffer.Length))
        {
            lock (_lock)
            {
                _received.Add(msg);
            }

            if (msg.Address == "/xremote")
            {
                continue;
            }

            if (msg.Arguments.Length == 0)
            {
                // Query: reply with current value.
                object? value;
                lock (_lock) { value = _state.GetValueOrDefault(msg.Address); }
                if (value is not null)
                {
                    Send(new OscMessage(msg.Address, value));
                }
            }
            else
            {
                // Set: store it. A real X32 does not acknowledge.
                lock (_lock) { _state[msg.Address] = msg.Arguments[0]; }
            }
        }
    }

    /// <summary>
    /// Change state WITHOUT broadcasting - the scene-recall case. The X32 does
    /// not reliably push every parameter after a recall, which is exactly the
    /// situation a push-only bridge gets silently wrong.
    /// </summary>
    public void SetSilently(string address, object value)
    {
        lock (_lock) { _state[address] = value; }
    }

    /// <summary>Push an unsolicited change, as the console does under /xremote.</summary>
    public void Push(string address, object value)
    {
        lock (_lock) { _state[address] = value; }
        Send(new OscMessage(address, value));
    }

    private void Send(OscMessage message)
    {
        if (_client is null)
        {
            return;
        }

        var bytes = message.ToBytes();
        _udp.Send(bytes, bytes.Length, _client);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp.Dispose();
    }
}
