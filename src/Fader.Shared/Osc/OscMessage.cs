using System.Buffers.Binary;
using System.Text;

namespace Fader.Shared;

/// <summary>
/// Minimal hand-rolled OSC 1.0 encoder/decoder - just the subset the X32 speaks
/// (string address, comma type-tag, and int/float/string/blob arguments).
/// Everything is big-endian and padded to a 4-byte boundary.
/// Dependency-free and shared by the bridge and the OscPing diagnostic.
/// </summary>
public sealed record OscMessage(string Address, params object[] Arguments)
{
    public byte[] ToBytes()
    {
        var buffer = new List<byte>(64);

        WritePaddedString(buffer, Address);

        var tags = new StringBuilder(",");
        foreach (var arg in Arguments)
        {
            tags.Append(arg switch
            {
                int => 'i',
                float => 'f',
                double => 'f',   // X32 has no float64 - narrow it
                string => 's',
                byte[] => 'b',
                _ => throw new NotSupportedException(
                    $"OSC argument type {arg.GetType().Name} is not supported."),
            });
        }
        WritePaddedString(buffer, tags.ToString());

        foreach (var arg in Arguments)
        {
            switch (arg)
            {
                case int i:
                    WriteInt32(buffer, i);
                    break;
                case float f:
                    WriteFloat32(buffer, f);
                    break;
                case double d:
                    WriteFloat32(buffer, (float)d);
                    break;
                case string s:
                    WritePaddedString(buffer, s);
                    break;
                case byte[] blob:
                    WriteInt32(buffer, blob.Length);
                    buffer.AddRange(blob);
                    Pad(buffer);
                    break;
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Parse an OSC packet. Returns null rather than throwing on malformed input -
    /// a diagnostic should report junk on the wire, not die on it.
    /// </summary>
    public static OscMessage? Parse(byte[] data, int length)
    {
        try
        {
            var offset = 0;
            var address = ReadPaddedString(data, length, ref offset);
            if (address is null || !address.StartsWith('/'))
            {
                return null;
            }

            var tags = ReadPaddedString(data, length, ref offset);
            if (tags is null || !tags.StartsWith(','))
            {
                // Some devices reply with no type-tag string at all.
                return new OscMessage(address);
            }

            var args = new List<object>();
            foreach (var tag in tags[1..])
            {
                switch (tag)
                {
                    case 'i':
                        if (offset + 4 > length) return null;
                        args.Add(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset)));
                        offset += 4;
                        break;

                    case 'f':
                        if (offset + 4 > length) return null;
                        args.Add(BitConverter.Int32BitsToSingle(
                            BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset))));
                        offset += 4;
                        break;

                    case 's':
                        var s = ReadPaddedString(data, length, ref offset);
                        if (s is null) return null;
                        args.Add(s);
                        break;

                    case 'b':
                        if (offset + 4 > length) return null;
                        var blobLength = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
                        offset += 4;
                        if (blobLength < 0 || offset + blobLength > length) return null;
                        args.Add(data[offset..(offset + blobLength)]);
                        offset += (blobLength + 3) & ~3;
                        break;

                    default:
                        return new OscMessage(address, args.ToArray());
                }
            }

            return new OscMessage(address, args.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a UDP payload that may be a single message or an OSC bundle.
    /// The X32 mostly sends plain messages, but scene/snapshot recalls can
    /// arrive bundled - unpacking them is what keeps the surface in sync
    /// after a recall.
    /// </summary>
    public static IReadOnlyList<OscMessage> ParsePacket(byte[] data, int length)
    {
        if (length >= 8 && data[0] == '#')
        {
            var header = Encoding.ASCII.GetString(data, 0, 7);
            if (header == "#bundle")
            {
                var messages = new List<OscMessage>();
                var offset = 16; // 8-byte "#bundle\0" + 8-byte time tag

                while (offset + 4 <= length)
                {
                    var size = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
                    offset += 4;
                    if (size < 0 || offset + size > length)
                    {
                        break; // truncated or malformed element
                    }

                    // Elements may themselves be bundles.
                    messages.AddRange(ParsePacket(data[offset..(offset + size)], size));
                    offset += size;
                }

                return messages;
            }
        }

        var single = Parse(data, length);
        return single is null ? Array.Empty<OscMessage>() : new[] { single };
    }

    public override string ToString()
    {
        if (Arguments.Length == 0)
        {
            return Address;
        }

        var rendered = Arguments.Select(a => a switch
        {
            float f => f.ToString("0.####"),
            string s => $"\"{s}\"",
            byte[] b => $"<{b.Length} bytes>",
            _ => a.ToString() ?? "?",
        });

        return $"{Address}  {string.Join("  ", rendered)}";
    }

    private static void WritePaddedString(List<byte> buffer, string value)
    {
        buffer.AddRange(Encoding.ASCII.GetBytes(value));
        buffer.Add(0);
        Pad(buffer);
    }

    private static void WriteInt32(List<byte> buffer, int value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(tmp, value);
        buffer.AddRange(tmp);
    }

    private static void WriteFloat32(List<byte> buffer, float value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(tmp, BitConverter.SingleToInt32Bits(value));
        buffer.AddRange(tmp);
    }

    private static void Pad(List<byte> buffer)
    {
        while (buffer.Count % 4 != 0)
        {
            buffer.Add(0);
        }
    }

    private static string? ReadPaddedString(byte[] data, int length, ref int offset)
    {
        var start = offset;
        while (offset < length && data[offset] != 0)
        {
            offset++;
        }

        if (offset >= length)
        {
            return null; // unterminated
        }

        var value = Encoding.ASCII.GetString(data, start, offset - start);
        offset = (offset + 4) & ~3; // step past the null, then round up to 4
        return value;
    }
}
