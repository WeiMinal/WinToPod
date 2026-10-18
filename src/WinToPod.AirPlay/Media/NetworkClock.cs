// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;

namespace WinToPod.AirPlay.Media;

public sealed class NetworkClock
{
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _utc = DateTimeOffset.UtcNow;
    public DateTimeOffset Now => _utc + Stopwatch.GetElapsedTime(_start);
    public ulong NtpNow => ToNtp(Now);
    public long UnixNanoseconds => checked((Now - DateTimeOffset.UnixEpoch).Ticks * 100);
    public static ulong ToNtp(DateTimeOffset utc)
    {
        var ticks = (utc - new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero)).Ticks;
        return unchecked(((ulong)(ticks / TimeSpan.TicksPerSecond) << 32) | (ulong)((ticks % TimeSpan.TicksPerSecond) * (1L << 32) / TimeSpan.TicksPerSecond));
    }
    public static byte[] TimingReply(ReadOnlySpan<byte> query, ulong received, ulong sent)
    {
        if (query.Length != 32 || (query[1] & 0x7f) != 82) throw new InvalidDataException("Invalid timing query.");
        var reply = query.ToArray(); reply[0] = 0x80; reply[1] = 0xd3; query.Slice(24, 8).CopyTo(reply.AsSpan(8));
        BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(16), received); BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(24), sent); return reply;
    }
    public static byte[] Sync(uint nextTimestamp, uint latencySamples, ulong ntp, bool first)
    {
        var packet = new byte[20]; packet[0] = first ? (byte)0x90 : (byte)0x80; packet[1] = 0xd4; packet[3] = 7;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), unchecked(nextTimestamp - latencySamples));
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), ntp); BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), nextTimestamp); return packet;
    }
    public static byte[] PtpSync(uint renderPosition, long masterNanoseconds, ulong clockId, bool first)
    {
        var packet = new byte[28]; packet[0] = first ? (byte)0x90 : (byte)0x80; packet[1] = 0xd7; packet[3] = 6;
        // Receiver interpretation: field1 - 11035 renders at masterNanoseconds.
        var field1 = unchecked(renderPosition + 11035);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), field1);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), checked((ulong)masterNanoseconds));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), unchecked(field1 + 77175));
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(20), clockId); return packet;
    }
}
