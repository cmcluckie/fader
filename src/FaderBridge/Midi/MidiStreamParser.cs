namespace Fader.Bridge.Midi;

/// <summary>
/// Walks a raw MIDI byte stream and raises one event per complete message.
/// CoreMIDI can deliver several messages in a single callback and SysEx can
/// straddle callbacks, so a little state is kept between feeds.
/// </summary>
public sealed class MidiStreamParser
{
    private readonly List<byte> _sysex = new();
    private bool _inSysex;
    private byte _runningStatus;

    public event Action<int, int, int>? NoteOn;          // channel, note, velocity
    public event Action<int, int>? NoteOff;              // channel, note
    public event Action<int, int>? PitchBend;            // channel, 14-bit value
    public event Action<int, int, int>? ControlChange;   // channel, cc, value
    public event Action<byte[]>? SysEx;
    public event Action<byte, int, int>? Other;          // status, d1, d2

    public void Feed(byte[] data, int start, int length)
    {
        var i = start;
        var end = start + length;

        while (i < end)
        {
            var b = data[i];

            if (_inSysex)
            {
                _sysex.Add(b);
                if (b == 0xF7)
                {
                    _inSysex = false;
                    SysEx?.Invoke(_sysex.ToArray());
                    _sysex.Clear();
                }
                i++;
                continue;
            }

            if (b == 0xF0)
            {
                _inSysex = true;
                _sysex.Clear();
                _sysex.Add(b);
                i++;
                continue;
            }

            // Realtime messages (0xF8-0xFF) can be interleaved anywhere, including
            // mid-message. Drop them - MCU carries nothing useful in them.
            if (b >= 0xF8)
            {
                i++;
                continue;
            }

            if (b >= 0x80)
            {
                _runningStatus = b;
                i++;
            }
            else if (_runningStatus == 0)
            {
                i++; // stray data byte with no preceding status
                continue;
            }

            var status = _runningStatus;
            var needed = DataBytesFor(status);
            if (i + needed > end)
            {
                return; // split across callbacks; wait for the remainder
            }

            var d1 = needed > 0 ? data[i] : (byte)0;
            var d2 = needed > 1 ? data[i + 1] : (byte)0;
            i += needed;

            Dispatch(status, d1, d2);
        }
    }

    private void Dispatch(byte status, byte d1, byte d2)
    {
        var channel = status & 0x0F;

        switch (status & 0xF0)
        {
            case 0xE0:
                PitchBend?.Invoke(channel, d1 | (d2 << 7)); // LSB then MSB
                break;

            // MCU signals button release as note-on with velocity 0.
            case 0x90 when d2 > 0:
                NoteOn?.Invoke(channel, d1, d2);
                break;

            case 0x90:
            case 0x80:
                NoteOff?.Invoke(channel, d1);
                break;

            case 0xB0:
                ControlChange?.Invoke(channel, d1, d2);
                break;

            default:
                Other?.Invoke(status, d1, d2);
                break;
        }
    }

    public static int DataBytesFor(byte status) => (status & 0xF0) switch
    {
        0xC0 or 0xD0 => 1,
        0xF0 => status switch { 0xF1 or 0xF3 => 1, 0xF2 => 2, _ => 0 },
        _ => 2,
    };
}
