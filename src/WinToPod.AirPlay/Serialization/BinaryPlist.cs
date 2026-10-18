// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace WinToPod.AirPlay.Serialization;

// Independent bplist00 object-table codec. Bounded, no external entity expansion.
public static class BinaryPlist
{
    private sealed record Node(byte Marker, byte[] Data, int[] References, int Count);
    private static readonly UnicodeEncoding Utf16 = new(true, false, true);

    public static byte[] Encode(object? value)
    {
        var nodes = new List<Node>();
        int Add(object? item, int depth)
        {
            if (depth > 32 || nodes.Count > 8192) throw new ArgumentException("Plist limit exceeded.");
            Node node;
            if (item is null) node = new(0, [], [], 0);
            else if (item is bool boolean) node = new(boolean ? (byte)9 : (byte)8, [], [], 0);
            else if (item is byte[] bytes) node = new(0x40, bytes, [], bytes.Length);
            else if (item is string text)
            {
                var ascii = text.All(c => c < 128);
                node = new(ascii ? (byte)0x50 : (byte)0x60, ascii ? Encoding.ASCII.GetBytes(text) : Utf16.GetBytes(text), [], text.Length);
            }
            else if (item is double real) { var data = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(data, real); node = new(0x23, data, [], 0); }
            else if (item is int or uint or long or ulong or ushort or byte)
            { var data = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(data, Convert.ToUInt64(item)); node = new(0x13, data, [], 0); }
            else if (item is IDictionary<string, object?> dict)
            {
                var keys = dict.Keys.Select(k => Add(k, depth + 1)).ToArray();
                var values = dict.Values.Select(v => Add(v, depth + 1)).ToArray();
                node = new(0xd0, [], keys.Concat(values).ToArray(), dict.Count);
            }
            else if (item is IEnumerable<object?> array)
            { var references = array.Select(v => Add(v, depth + 1)).ToArray(); node = new(0xa0, [], references, references.Length); }
            else throw new ArgumentException("Unsupported plist value.");
            nodes.Add(node); return nodes.Count - 1;
        }
        var root = Add(value, 0); var refSize = Width((ulong)nodes.Count);
        using var result = new MemoryStream(); result.Write("bplist00"u8); var offsets = new List<ulong>();
        foreach (var node in nodes)
        {
            offsets.Add((ulong)result.Position);
            if (node.Marker is 0 or 8 or 9 or 0x13 or 0x23) result.WriteByte(node.Marker);
            else WriteLength(result, node.Marker, node.Count);
            result.Write(node.Data); foreach (var reference in node.References) WriteInteger(result, (ulong)reference, refSize);
        }
        var table = (ulong)result.Position; var offsetSize = Width(table);
        foreach (var offset in offsets) WriteInteger(result, offset, offsetSize);
        result.Write(new byte[6]); result.WriteByte((byte)offsetSize); result.WriteByte((byte)refSize);
        WriteInteger(result, (ulong)nodes.Count, 8); WriteInteger(result, (ulong)root, 8); WriteInteger(result, table, 8);
        if (result.Length > 2 * 1024 * 1024) throw new ArgumentException("Plist exceeds size limit.");
        return result.ToArray();
    }

    public static object? Decode(byte[] bytes)
    {
        if (bytes.Length is < 40 or > 2 * 1024 * 1024 || !bytes.AsSpan(0, 8).SequenceEqual("bplist00"u8))
            throw new InvalidDataException("Invalid binary plist header/size.");
        var trailer = bytes.Length - 32; var offsetSize = bytes[trailer + 6]; var refSize = bytes[trailer + 7];
        var count = ReadInteger(bytes, trailer + 8, 8); var root = ReadInteger(bytes, trailer + 16, 8); var table = ReadInteger(bytes, trailer + 24, 8);
        if (offsetSize is < 1 or > 8 || refSize is < 1 or > 8 || count is < 1 or > 8192 || root >= count || table < 8 || table >= (ulong)trailer || count * offsetSize > (ulong)trailer - table)
            throw new InvalidDataException("Invalid plist trailer.");
        var active = new HashSet<ulong>(); var decoded = new Dictionary<ulong, object?>();
        object? Get(ulong index, int depth)
        {
            if (index >= count || depth > 32) throw new InvalidDataException("Plist reference/depth limit.");
            if (decoded.TryGetValue(index, out var cached)) return cached;
            if (!active.Add(index)) throw new InvalidDataException("Cyclic plist reference.");
            var location = ReadInteger(bytes, checked((int)table + (int)index * offsetSize), offsetSize);
            if (location < 8 || location >= table) throw new InvalidDataException("Invalid plist object offset.");
            var at = (int)location; var marker = bytes[at++]; var kind = marker >> 4; var low = marker & 15;
            ulong Length()
            {
                if (low < 15) return (ulong)low;
                Need(at, 1); var lengthTag = bytes[at++];
                if ((lengthTag >> 4) != 1 || (lengthTag & 15) > 3) throw new InvalidDataException("Invalid plist length.");
                var size = 1 << (lengthTag & 15); Need(at, size); var length = ReadInteger(bytes, at, size); at += size; return length;
            }
            void Need(int start, int size)
            { if (size < 0 || start < 8 || start > (int)table - size) throw new InvalidDataException("Truncated plist object."); }
            int BoundedLength(ulong value)
            { if (value > 2 * 1024 * 1024) throw new InvalidDataException("Plist length limit."); return (int)value; }
            object? value;
            switch (kind)
            {
                case 0: value = low switch { 0 => null, 8 => false, 9 => true, _ => throw new InvalidDataException("Unsupported plist singleton.") }; break;
                case 1:
                    if (low > 3) throw new InvalidDataException("Plist integer too large.");
                    var size = 1 << low; Need(at, size); value = ReadInteger(bytes, at, size); break;
                case 2:
                    if (low is not (2 or 3)) throw new InvalidDataException("Unsupported plist real.");
                    Need(at, 1 << low); value = low == 3 ? BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(at, 8)) : (double)BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(at, 4)); break;
                case 3:
                    if (low != 3) throw new InvalidDataException("Unsupported plist date.");
                    Need(at, 8); value = BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(at, 8)); break;
                case 4: var dataLength = BoundedLength(Length()); Need(at, dataLength); value = bytes.AsSpan(at, dataLength).ToArray(); break;
                case 5: var asciiLength = BoundedLength(Length()); Need(at, asciiLength); value = Encoding.ASCII.GetString(bytes, at, asciiLength); break;
                case 6:
                    var chars = BoundedLength(Length()); var byteCount = checked(chars * 2); Need(at, byteCount);
                    try { value = Utf16.GetString(bytes, at, byteCount); }
                    catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid plist UTF-16.", e); } break;
                case 8: Need(at, low + 1); value = ReadInteger(bytes, at, low + 1); break;
                case 0xa:
                case 0xd:
                    var length = BoundedLength(Length()); if (length > 8192) throw new InvalidDataException("Plist container limit.");
                    Need(at, checked(length * refSize * (kind == 0xd ? 2 : 1)));
                    var entries = new object?[length];
                    for (var i = 0; i < length; i++) entries[i] = Get(ReadInteger(bytes, at + i * refSize, refSize), depth + 1);
                    if (kind == 0xa) value = entries;
                    else
                    {
                        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                        for (var i = 0; i < length; i++)
                        {
                            if (entries[i] is not string key || !dict.TryAdd(key, Get(ReadInteger(bytes, at + (length + i) * refSize, refSize), depth + 1)))
                                throw new InvalidDataException("Invalid/duplicate plist dictionary key.");
                        }
                        value = dict;
                    }
                    break;
                default: throw new InvalidDataException("Unsupported plist object type.");
            }
            active.Remove(index); decoded[index] = value; return value;
        }
        return Get(root, 0);
    }

    public static Dictionary<string, object?> Dictionary(byte[] bytes)
        => Decode(bytes) as Dictionary<string, object?> ?? throw new InvalidDataException("Expected plist dictionary.");
    private static int Width(ulong value) => value <= byte.MaxValue ? 1 : value <= ushort.MaxValue ? 2 : value <= uint.MaxValue ? 4 : 8;
    private static void WriteLength(Stream output, byte kind, int count)
    { if (count < 15) output.WriteByte((byte)(kind | count)); else { output.WriteByte((byte)(kind | 15)); var size = Width((ulong)count); output.WriteByte((byte)(0x10 | (size == 1 ? 0 : size == 2 ? 1 : size == 4 ? 2 : 3))); WriteInteger(output, (ulong)count, size); } }
    private static void WriteInteger(Stream output, ulong value, int size)
    { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, value); output.Write(bytes[(8 - size)..]); }
    private static ulong ReadInteger(byte[] bytes, int offset, int size)
    {
        if (size is < 1 or > 8 || offset < 0 || offset > bytes.Length - size) throw new InvalidDataException("Invalid plist integer field.");
        ulong value = 0; for (var i = 0; i < size; i++) value = (value << 8) | bytes[offset + i]; return value;
    }
}
