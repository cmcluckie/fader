using System.Text;

namespace Fader.Bridge.Midi;

/// <summary>
/// MCU note assignments and message construction for the FaderPort 8 in
/// Mackie Control mode. Note numbers are the documented MCU defaults - if
/// diagnostic #1 shows different numbers on your unit, correct them here.
/// </summary>
public static class McuProtocol
{
    // Per-strip button banks. Add the strip index (0-7) to the base note.
    public const int RecBase = 0;
    public const int SoloBase = 8;
    public const int MuteBase = 16;
    public const int SelectBase = 24;
    public const int VPotBase = 32;
    public const int TouchBase = 104;

    // Transport
    public const int Rewind = 91;
    public const int FastForward = 92;
    public const int Stop = 93;
    public const int Play = 94;
    public const int Record = 95;

    // Bank / navigation
    public const int BankLeft = 46;
    public const int BankRight = 47;
    public const int ChannelLeft = 48;
    public const int ChannelRight = 49;

    /// <summary>Characters per scribble-strip cell in the MCU display protocol.</summary>
    public const int ScribbleCellWidth = 7;

    /// <summary>Byte offset where the scribble strip's lower row begins.</summary>
    private const int LowerRowOffset = 56;

    public static bool TryGetStrip(int note, int baseNote, int stripCount, out int strip)
    {
        strip = note - baseNote;
        return strip >= 0 && strip < stripCount;
    }

    public static bool IsFaderTouch(int note) => note is >= TouchBase and < TouchBase + 8;

    /// <summary>Pitch-bend message driving one motorised fader. Value is 14-bit (0-16383).</summary>
    public static byte[] FaderPosition(int strip, int value14)
    {
        var clamped = Math.Clamp(value14, 0, 16383);
        return new[]
        {
            (byte)(0xE0 | (strip & 0x0F)),
            (byte)(clamped & 0x7F),        // LSB
            (byte)((clamped >> 7) & 0x7F), // MSB
        };
    }

    /// <summary>
    /// Button LED state. MCU lamps are driven by echoing a note-on back to the
    /// surface: velocity 127 lit, 0 dark. (1-63 is "flashing" on some surfaces.)
    /// </summary>
    public static byte[] Led(int note, bool on) =>
        new[] { (byte)0x90, (byte)note, (byte)(on ? 127 : 0) };

    /// <summary>
    /// MCU scribble-strip write:  F0 00 00 66 14 12 &lt;offset&gt; &lt;7-bit ASCII&gt; F7
    /// Row 0 is the upper line, row 1 the lower; each strip owns 7 characters.
    /// </summary>
    public static byte[] ScribbleText(int strip, int row, string text)
    {
        var offset = (row == 0 ? 0 : LowerRowOffset) + strip * ScribbleCellWidth;
        var cell = Fit(text, ScribbleCellWidth);

        var message = new List<byte>(8 + ScribbleCellWidth)
        {
            0xF0, 0x00, 0x00, 0x66, 0x14, 0x12, (byte)offset,
        };

        // Strip anything outside printable 7-bit ASCII - the display shows
        // garbage for high bytes, and X32 channel names can contain anything.
        foreach (var c in cell)
        {
            message.Add((byte)(c is >= ' ' and <= '~' ? c : ' '));
        }

        message.Add(0xF7);
        return message.ToArray();
    }

    private static string Fit(string text, int width)
    {
        text ??= string.Empty;
        return text.Length >= width
            ? text[..width]
            : text.PadRight(width);
    }

    /// <summary>Human-readable label for a note, used in logging.</summary>
    public static string Describe(int note)
    {
        if (TryGetStrip(note, RecBase, 8, out var s)) return $"Rec {s + 1}";
        if (TryGetStrip(note, SoloBase, 8, out s)) return $"Solo {s + 1}";
        if (TryGetStrip(note, MuteBase, 8, out s)) return $"Mute {s + 1}";
        if (TryGetStrip(note, SelectBase, 8, out s)) return $"Select {s + 1}";
        if (TryGetStrip(note, VPotBase, 8, out s)) return $"V-Pot {s + 1}";
        if (TryGetStrip(note, TouchBase, 8, out s)) return $"Touch {s + 1}";

        return Globals.TryGetValue(note, out var name)
            ? name
            : $"note {note} (unmapped)";
    }

    /// <summary>
    /// Standard MCU global buttons. Confirmed present on the FaderPort 8:
    /// pressing its modifier and utility keys produces exactly these numbers,
    /// so the surface follows the MCU map faithfully rather than a PreSonus
    /// variant. Named here so diagnostic output is readable and so anything
    /// genuinely unknown still stands out as "(unmapped)".
    /// </summary>
    private static readonly Dictionary<int, string> Globals = new()
    {
        [40] = "Assign Track",
        [41] = "Assign Send",
        [42] = "Assign Pan/Surround",
        [43] = "Assign Plug-in",
        [44] = "Assign EQ",
        [45] = "Assign Instrument",
        [BankLeft] = "Bank Left",
        [BankRight] = "Bank Right",
        [ChannelLeft] = "Channel Left",
        [ChannelRight] = "Channel Right",
        [50] = "Flip",
        [51] = "Global View",
        [52] = "Name/Value",
        [53] = "SMPTE/Beats",
        [54] = "F1", [55] = "F2", [56] = "F3", [57] = "F4",
        [58] = "F5", [59] = "F6", [60] = "F7", [61] = "F8",
        [62] = "View: MIDI Tracks",
        [63] = "View: Inputs",
        [64] = "View: Audio Tracks",
        [65] = "View: Audio Instrument",
        [66] = "View: Aux",
        [67] = "View: Busses",
        [68] = "View: Outputs",
        [69] = "View: User",
        [70] = "Shift",
        [71] = "Option",
        [72] = "Control",
        [73] = "Cmd/Alt",
        [74] = "Automation: Read/Off",
        [75] = "Automation: Write",
        [76] = "Automation: Trim",
        [77] = "Automation: Touch",
        [78] = "Automation: Latch",
        [79] = "Group",
        [80] = "Save",
        [81] = "Undo",
        [82] = "Cancel",
        [83] = "Enter",
        [84] = "Marker",
        [85] = "Nudge",
        [86] = "Cycle",
        [87] = "Drop",
        [88] = "Replace",
        [89] = "Click",
        [90] = "Solo (rude)",
        [Rewind] = "Rewind",
        [FastForward] = "Fast-Forward",
        [Stop] = "Stop",
        [Play] = "Play",
        [Record] = "Record",
        [96] = "Cursor Up",
        [97] = "Cursor Down",
        [98] = "Cursor Left",
        [99] = "Cursor Right",
        [100] = "Zoom",
        [101] = "Scrub",
    };

    /// <summary>
    /// MCU carries the master fader on pitch-bend channel 8, one past the eight
    /// strips. The FaderPort emits it, so the bridge must recognise it rather
    /// than silently treating it as a ninth strip.
    /// </summary>
    public const int MasterFaderChannel = 8;

    /// <summary>Decodes an MCU scribble-strip SysEx back to text, for logging/tests.</summary>
    public static string? TryDecodeScribble(byte[] sysex)
    {
        if (sysex.Length < 9 ||
            sysex[1] != 0x00 || sysex[2] != 0x00 || sysex[3] != 0x66 || sysex[5] != 0x12)
        {
            return null;
        }

        return Encoding.ASCII.GetString(sysex, 7, sysex.Length - 8);
    }
}
