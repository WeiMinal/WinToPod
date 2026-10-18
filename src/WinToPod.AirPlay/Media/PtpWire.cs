// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace WinToPod.AirPlay.Media;

// Independent gPTP wire codec. Field sources and Apple extensions are recorded
// in docs/PROTOCOL.md. This is not a complete IEEE-1588 implementation.
public static class PtpWire
{
    public const int HeaderSize = 34;
    public static byte[] Header(byte type, int size, ulong clockId, ushort sequence, ushort flags = 0x0408, sbyte interval = -3)
    {
        var result = new byte[size]; result[0] = (byte)(0x10 | type); result[1] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), checked((ushort)size));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(20), clockId);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(30), sequence);
        result[32] = type switch { 1 => 1, 9 => 3, 2 or 3 or 10 or 12 => 5, _ => 0 };
        result[33] = unchecked((byte)interval); return result;
    }

    public static bool IsValid(ReadOnlySpan<byte> data)
    {
        if (data.Length is < HeaderSize or > 1500 || data[0] >> 4 != 1 || (data[1] & 15) != 2 || data[4] != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(data[2..]) != data.Length) return false;
        var minimum = (data[0] & 15) switch { 0 or 1 or 8 => 44, 2 or 3 or 9 or 10 => 54, 11 => 64, 12 => 44, _ => int.MaxValue };
        return data.Length >= minimum && BinaryPrimitives.ReadUInt16BigEndian(data[28..]) != 0;
    }

    public static void Timestamp(Span<byte> output, long nanoseconds)
    {
        if (nanoseconds < 0 || output.Length < 10) throw new ArgumentOutOfRangeException(nameof(nanoseconds));
        var seconds = (ulong)(nanoseconds / 1_000_000_000L);
        BinaryPrimitives.WriteUInt16BigEndian(output, (ushort)(seconds >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(output[2..], (uint)seconds);
        BinaryPrimitives.WriteUInt32BigEndian(output[6..], (uint)(nanoseconds % 1_000_000_000L));
    }
    public static long ReadTimestamp(ReadOnlySpan<byte> input)
    {
        if (input.Length < 10) throw new InvalidDataException("Truncated PTP timestamp.");
        var seconds = ((ulong)BinaryPrimitives.ReadUInt16BigEndian(input) << 32) | BinaryPrimitives.ReadUInt32BigEndian(input[2..]);
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(input[6..]);
        if (fraction >= 1_000_000_000 || seconds > (ulong)(long.MaxValue / 1_000_000_000L)) throw new InvalidDataException("PTP timestamp out of range.");
        var ns = (Int128)seconds * 1_000_000_000 + fraction;
        if (ns > long.MaxValue) throw new InvalidDataException("PTP timestamp out of range.");
        return (long)ns;
    }

    public static byte[] Announce(ulong clockId, ushort sequence)
    {
        var data = Header(11, 76, clockId, sequence, interval: 0);
        data[47] = 128; data[48] = 6; data[49] = 0x21; data[50] = 0x43; data[51] = 0x6a; data[52] = 128;
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(53), clockId); data[63] = 0x20;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(64), 8); BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(66), 8);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(68), clockId); return data;
    }
    public static byte[] Sync(ulong clockId, ushort sequence) => Header(0, 44, clockId, sequence, 0x0608);
    public static byte[] FollowUp(ulong clockId, ushort sequence, long sentNanoseconds)
    {
        var data = Header(8, 96, clockId, sequence); Timestamp(data.AsSpan(34), sentNanoseconds);
        // 802.1AS Follow_Up information, followed by Apple's clock identity TLV.
        new byte[] { 0, 3, 0, 28, 0, 0x80, 0xc2, 0, 0, 1 }.CopyTo(data, 44);
        new byte[] { 0, 3, 0, 16, 0, 0x0d, 0x93, 0, 0, 4 }.CopyTo(data, 76);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(86), clockId); return data;
    }
    public static byte[] DelayReply(ReadOnlySpan<byte> request, ulong clockId, long timestamp, bool followUp = false)
    {
        if (!IsValid(request) || (request[0] & 15) is not (1 or 2)) throw new InvalidDataException("Invalid PTP delay request.");
        var peerDelay = (request[0] & 15) == 2;
        var type = peerDelay ? followUp ? (byte)10 : (byte)3 : (byte)9;
        var data = Header(type, 54, clockId, BinaryPrimitives.ReadUInt16BigEndian(request[30..]), peerDelay && !followUp ? (ushort)0x0608 : (ushort)0x0408);
        request.Slice(8, 8).CopyTo(data.AsSpan(8)); Timestamp(data.AsSpan(34), timestamp);
        request.Slice(20, 10).CopyTo(data.AsSpan(44)); return data;
    }
    public static IEnumerable<byte[]> Grants(byte[] request, ulong clockId)
    {
        if (!IsValid(request) || (request[0] & 15) != 12) yield break;
        var target = BinaryPrimitives.ReadUInt64BigEndian(request.AsSpan(34));
        if (target != clockId && target != ulong.MaxValue) yield break;
        var port = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(42));
        if (port != 1 && port != ushort.MaxValue) yield break;
        for (var offset = 44; offset + 4 <= request.Length;)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(offset + 2));
            if ((length & 1) != 0 || length > request.Length - offset - 4) yield break;
            if (type == 4 && length == 6 && (request[offset + 4] >> 4) is 0 or 9 or 11)
            {
                var response = Header(12, 56, clockId, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(30)));
                request.AsSpan(20, 10).CopyTo(response.AsSpan(34));
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(44), 5); response[47] = 8;
                response[48] = request[offset + 4]; response[49] = request[offset + 5];
                var duration = Math.Min(60u, BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(offset + 6)));
                BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(50), duration); response[54] = 1;
                yield return response;
            }
            offset += length + 4;
        }
    }
}
