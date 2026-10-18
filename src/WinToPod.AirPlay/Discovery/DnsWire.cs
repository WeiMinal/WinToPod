// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Net;
using System.Text;

namespace WinToPod.AirPlay.Discovery;

public enum DnsType : ushort { A = 1, Ptr = 12, Txt = 16, Aaaa = 28, Srv = 33 }
public sealed record DnsQuestion(string Name, DnsType Type);
public sealed record DnsRecord(string Name, DnsType Type, uint Ttl, bool Flush, string DataKey,
    string? Target = null, ushort Port = 0, IPAddress? Address = null,
    IReadOnlyDictionary<string, string?>? Text = null, ushort Priority = 0);
public sealed record DnsPacket(ushort Id, bool Response, bool Truncated, DnsRecord[] Records);

// Written from RFC 1035/6762/6763; no sender implementation is used as a source.
public static class DnsWire
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static DnsPacket Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12) throw new InvalidDataException("Incomplete DNS header.");
        var flags = U16(packet, 2);
        if ((flags & 0x780f) != 0) throw new InvalidDataException("Unsupported DNS opcode/error.");
        var questions = U16(packet, 4);
        var records = (int)U16(packet, 6) + U16(packet, 8) + U16(packet, 10);
        if (questions > 256 || records > 512) throw new InvalidDataException("DNS record limit exceeded.");
        var offset = 12;
        for (var i = 0; i < questions; i++) { ReadName(packet, ref offset); Require(packet, offset, 4); offset += 4; }
        var parsed = new List<DnsRecord>();
        for (var i = 0; i < records; i++)
        {
            var name = ReadName(packet, ref offset); Require(packet, offset, 10);
            var type = (DnsType)U16(packet, offset); var cls = U16(packet, offset + 2);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(offset + 4)..]);
            var length = U16(packet, offset + 8); offset += 10;
            Require(packet, offset, length); var end = offset + length;
            if ((cls & 0x7fff) != 1) { offset = end; continue; }
            var flush = (cls & 0x8000) != 0;
            DnsRecord? record = null;
            switch (type)
            {
                case DnsType.Ptr:
                    var target = ReadName(packet, ref offset);
                    record = new(name, type, ttl, flush, target.ToLowerInvariant(), Target: target);
                    break;
                case DnsType.Srv:
                    if (length < 7) throw new InvalidDataException("Incomplete SRV.");
                    var priority = U16(packet, offset); var weight = U16(packet, offset + 2);
                    var port = U16(packet, offset + 4); offset += 6;
                    var host = ReadName(packet, ref offset);
                    record = new(name, type, ttl, flush, $"{priority}/{weight}/{port}/{host.ToLowerInvariant()}",
                        Target: host, Port: port, Priority: priority);
                    break;
                case DnsType.Txt:
                    var raw = packet.Slice(offset, length);
                    record = new(name, type, ttl, flush, Convert.ToHexString(raw), Text: ParseText(raw));
                    offset = end; break;
                case DnsType.A when length == 4:
                case DnsType.Aaaa when length == 16:
                    var address = new IPAddress(packet.Slice(offset, length));
                    record = new(name, type, ttl, flush, address.ToString(), Address: address);
                    offset = end; break;
                case DnsType.A:
                case DnsType.Aaaa: throw new InvalidDataException("Invalid address record length.");
                default: offset = end; break;
            }
            if (offset != end) throw new InvalidDataException("RDATA name exceeds its declared length.");
            if (record is not null) parsed.Add(record);
        }
        return new(U16(packet, 0), (flags & 0x8000) != 0, (flags & 0x0200) != 0, parsed.ToArray());
    }

    public static byte[] Query(ushort id, IEnumerable<DnsQuestion> questions)
    {
        var entries = questions.Distinct().ToArray();
        if (entries.Length is < 1 or > 256) throw new ArgumentException("Invalid question count.");
        using var output = new MemoryStream(); output.Write(new byte[12]);
        foreach (var q in entries) { output.Write(EncodeName(q.Name)); Write16(output, (ushort)q.Type); Write16(output, 1); }
        if (output.Length > 1400) throw new ArgumentException("Split DNS queries into smaller packets.");
        var result = output.ToArray(); BinaryPrimitives.WriteUInt16BigEndian(result, id);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)entries.Length); return result;
    }

    public static byte[] EncodeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var result = new MemoryStream();
        foreach (var label in (name.EndsWith('.') ? name[..^1] : name).Split('.'))
        {
            using var bytes = new MemoryStream();
            for (var i = 0; i < label.Length;)
            {
                if (label[i] == '\\')
                {
                    if (i + 3 >= label.Length || label.AsSpan(i + 1, 3).ContainsAnyExceptInRange('0', '9') ||
                        !int.TryParse(label.AsSpan(i + 1, 3), out var b) || b is < 0 or > 255)
                        throw new ArgumentException("DNS escapes must contain three decimal digits.");
                    bytes.WriteByte((byte)b); i += 4;
                }
                else
                {
                    var next = label.IndexOf('\\', i); if (next < 0) next = label.Length;
                    bytes.Write(Utf8.GetBytes(label[i..next])); i = next;
                }
            }
            if (bytes.Length is < 1 or > 63) throw new ArgumentException("Invalid DNS label length.");
            result.WriteByte((byte)bytes.Length); bytes.Position = 0; bytes.CopyTo(result);
        }
        result.WriteByte(0);
        if (result.Length > 255) throw new ArgumentException("DNS name is too long.");
        return result.ToArray();
    }

    private static string ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var labels = new List<string>(); var at = offset; var jumped = false; var expanded = 1;
        for (var steps = 0; steps < 128; steps++)
        {
            Require(packet, at, 1); var length = packet[at++];
            if ((length & 0xc0) == 0xc0)
            {
                Require(packet, at, 1); var pointer = ((length & 0x3f) << 8) | packet[at++];
                if (pointer < 12 || pointer >= at - 2) throw new InvalidDataException("Invalid DNS compression pointer.");
                if (!jumped) offset = at; jumped = true; at = pointer; continue;
            }
            if ((length & 0xc0) != 0) throw new InvalidDataException("Unsupported DNS label encoding.");
            if (length == 0) { if (!jumped) offset = at; return string.Join('.', labels) + "."; }
            expanded += 1 + length; if (expanded > 255) throw new InvalidDataException("DNS name is too long.");
            Require(packet, at, length);
            try
            {
                var label = Utf8.GetString(packet.Slice(at, length));
                var escaped = new StringBuilder();
                foreach (var c in label)
                    if (c is '.' or '\\' || c < 32 || c == 127) escaped.Append('\\').Append(((int)c).ToString("D3"));
                    else escaped.Append(c);
                labels.Add(escaped.ToString());
            }
            catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid UTF-8 DNS label.", e); }
            at += length;
        }
        throw new InvalidDataException("DNS compression traversal limit exceeded.");
    }

    private static IReadOnlyDictionary<string, string?> ParseText(ReadOnlySpan<byte> raw)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset < raw.Length;)
        {
            var length = raw[offset++]; Require(raw, offset, length);
            var entry = raw.Slice(offset, length); offset += length;
            var equals = entry.IndexOf((byte)'='); var key = equals < 0 ? entry : entry[..equals];
            if (key.IsEmpty || key.ContainsAnyExceptInRange((byte)0x20, (byte)0x7e)) continue;
            var keyText = Encoding.ASCII.GetString(key);
            // Keys are ASCII; opaque non-UTF8 values remain visible as replacement characters.
            fields.TryAdd(keyText, equals < 0 ? null : Encoding.UTF8.GetString(entry[(equals + 1)..]));
        }
        return new ReadOnlyDictionary<string, string?>(fields);
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) { Require(bytes, offset, 2); return BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]); }
    private static void Require(ReadOnlySpan<byte> bytes, int offset, int size)
    { if (offset < 0 || size < 0 || offset > bytes.Length - size) throw new InvalidDataException("Truncated DNS field."); }
    private static void Write16(Stream output, ushort value)
    { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); output.Write(bytes); }
}
