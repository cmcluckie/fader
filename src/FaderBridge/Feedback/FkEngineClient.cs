using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Fader.Bridge.Osc;

namespace Fader.Bridge.Feedback;

/// <summary>
/// Loopback OSC client for the feedback engine (see docs/fk-osc-interface.md).
/// Sends control to the engine on 10024 and listens for telemetry on 10025,
/// reusing the bridge's own <see cref="OscMessage"/> codec.
///
/// Telemetry blobs are the engine's native memory layout (little-endian on
/// Apple Silicon, where both ends run); if the engine were ever built
/// big-endian this decode would need to follow.
/// </summary>
public sealed class FkEngineClient : IAsyncDisposable
{
    public const int EnginePort = 10024;
    public const int TelemetryPort = 10025;

    private readonly UdpClient _udp;
    private readonly IPEndPoint _engine = new(IPAddress.Loopback, EnginePort);
    private CancellationTokenSource? _cts;
    private Task? _receive;

    public FkEngineClient()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, TelemetryPort));
    }

    public event Action<FkStatus>? StatusReceived;
    public event Action<FkDetection>? DetectionReceived;
    public event Action<int, FkNotch[]>? NotchesReceived;
    public event Action<FkSpectrum>? SpectrumReceived;
    public event Action<FkAudioState>? AudioStateReceived;
    public event Action<string>? DeviceListed;
    public event Action<string>? Error;

    public void Start(CancellationToken token)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _receive = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
    }

    // ---- control (app -> engine) -------------------------------------------
    public void SetMode(FkMode mode)                 => Send(new OscMessage("/fk/mode", (int) mode));
    public void Subscribe(FkTelemetry mask)          => Send(new OscMessage("/fk/subscribe", (int) mask));
    public void Ping()                               => Send(new OscMessage("/fk/ping"));
    public void SetParam(string name, float value)   => Send(new OscMessage("/fk/param", name, value));
    public void PlaceNotch(int ch, float hz, float depthDb) => Send(new OscMessage("/fk/notch/place", ch, hz, depthDb));
    public void RemoveNotch(int ch, int slot)        => Send(new OscMessage("/fk/notch/remove", ch, slot));
    public void LockNotch(int ch, int slot, bool on) => Send(new OscMessage("/fk/notch/lock", ch, slot, on ? 1 : 0));
    public void Clear(int ch, bool includeLocked)    => Send(new OscMessage("/fk/clear", ch, includeLocked ? 1 : 0));
    public void LockAll(int ch)                      => Send(new OscMessage("/fk/lockall", ch));
    public void SetAudio(string device, int sampleRate, int bufferSize)
        => Send(new OscMessage("/fk/audio", device, sampleRate, bufferSize));
    public void ListDevices()                        => Send(new OscMessage("/fk/listdevices"));

    private void Send(OscMessage message)
    {
        try
        {
            var bytes = message.ToBytes();
            _udp.Send(bytes, bytes.Length, _engine);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"send {message.Address}: {ex.Message}");
        }
    }

    // ---- telemetry (engine -> app) -----------------------------------------
    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(token);
                foreach (var message in OscMessage.ParsePacket(result.Buffer, result.Buffer.Length))
                {
                    Dispatch(message);
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

    private void Dispatch(OscMessage m)
    {
        switch (m.Address)
        {
            case "/fk/status" when m.Arguments is [int ok, float cpu]:
                StatusReceived?.Invoke(new FkStatus(ok != 0, cpu));
                break;

            case "/fk/event" when m.Arguments is [int ch, float hz, float lvl]:
                DetectionReceived?.Invoke(new FkDetection(ch, hz, lvl));
                break;

            case "/fk/notches" when m.Arguments is [int ch, byte[] blob]:
                NotchesReceived?.Invoke(ch, DecodeNotches(blob));
                break;

            case "/fk/spectrum" when m.Arguments is [int ch, byte[] blob]:
                if (DecodeSpectrum(ch, blob) is { } s) SpectrumReceived?.Invoke(s);
                break;

            case "/fk/audio/state" when m.Arguments is [string dev, int sr, int buf, int running]:
                AudioStateReceived?.Invoke(new FkAudioState(dev, sr, buf, running != 0));
                break;

            case "/fk/device" when m.Arguments is [string name]:
                DeviceListed?.Invoke(name);
                break;
        }
    }

    // 16 bytes/slot: u8 active, u8 locked, u8 manual, u8 pad, f32 freq, f32 currentDb, f32 targetDb
    private static FkNotch[] DecodeNotches(byte[] blob)
    {
        const int rec = 16;
        var count = blob.Length / rec;
        var notches = new FkNotch[count];
        for (var i = 0; i < count; i++)
        {
            var o = i * rec;
            notches[i] = new FkNotch(
                Active: blob[o] != 0,
                Locked: blob[o + 1] != 0,
                Manual: blob[o + 2] != 0,
                FreqHz: BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(o + 4)),
                CurrentDb: BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(o + 8)),
                TargetDb: BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(o + 12)));
        }
        return notches;
    }

    // u16 binCount, f32 hzPerBin, f32[binCount] magnitudes
    private static FkSpectrum? DecodeSpectrum(int ch, byte[] blob)
    {
        if (blob.Length < 6) return null;
        var bins = BinaryPrimitives.ReadUInt16LittleEndian(blob);
        var hzPerBin = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(2));
        if (blob.Length < 6 + bins * 4) return null;

        var mags = new float[bins];
        for (var i = 0; i < bins; i++)
            mags[i] = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(6 + i * 4));
        return new FkSpectrum(ch, hzPerBin, mags);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_receive is not null)
        {
            try { await _receive; } catch { /* shutting down */ }
        }

        _cts?.Dispose();
        _udp.Dispose();
    }
}
