// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace WinToPod.AirPlay.Media;

// Keeps the media clock separate from the host clock. Never sets Windows time.
// Software receive timestamps give a bounded estimate, not hardware PTP accuracy.
public sealed class PtpClockTracker(ulong ownClockId, bool followReceiver)
{
    private readonly object _gate = new();
    private readonly Dictionary<ushort, (long Received, long Correction)> _pending = [];
    private readonly Dictionary<(ulong Clock, ushort Port, int Type), ushort> _probeSequences = [];
    private ulong _source, _master;
    private ushort _sourcePort;
    private long _offset, _first, _last;
    private int _samples;
    public bool FollowReceiver => followReceiver;
    public void Observe(ReadOnlySpan<byte> packet, long receivedNanoseconds)
    {
        if (!PtpWire.IsValid(packet)) return;
        lock (_gate)
        {
            var type = packet[0] & 15;
            var source = BinaryPrimitives.ReadUInt64BigEndian(packet[20..]);
            if (source == ownClockId) return;
            if (!followReceiver)
            {
                if (type is 1 or 2)
                {
                    var key = (source, BinaryPrimitives.ReadUInt16BigEndian(packet[28..]), type);
                    var probeSequence = BinaryPrimitives.ReadUInt16BigEndian(packet[30..]);
                    if (_probeSequences.TryGetValue(key, out var previous) && unchecked((ushort)(probeSequence - previous)) is 0 or > 32767) return;
                    if (_probeSequences.Count >= 8 && !_probeSequences.ContainsKey(key)) return;
                    _probeSequences[key] = probeSequence; Count(receivedNanoseconds);
                }
                return;
            }
            if (type == 11)
            {
                var master = BinaryPrimitives.ReadUInt64BigEndian(packet[53..]);
                if (master == 0 || master == ownClockId) return;
                var port = BinaryPrimitives.ReadUInt16BigEndian(packet[28..]);
                if (_master != master || _source != source || _sourcePort != port)
                { _master = master; _source = source; _sourcePort = port; _samples = 0; _pending.Clear(); }
            }
            if (_source != source || BinaryPrimitives.ReadUInt16BigEndian(packet[28..]) != _sourcePort || _master == 0) return;
            var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet[30..]);
            var correction = BinaryPrimitives.ReadInt64BigEndian(packet[8..]) / 65536;
            if (type == 0 && (packet[6] & 2) != 0)
            {
                if (_pending.Count >= 16) _pending.Clear();
                _pending[sequence] = (receivedNanoseconds, correction);
            }
            else if (type == 0) Sample(packet[34..], receivedNanoseconds, correction);
            else if (type == 8 && _pending.Remove(sequence, out var sync) && receivedNanoseconds >= sync.Received && receivedNanoseconds - sync.Received < 1_000_000_000)
                Sample(packet[34..], sync.Received, checked(sync.Correction + correction));
        }
    }
    private void Sample(ReadOnlySpan<byte> timestamp, long received, long correction)
    {
        long peer;
        try { peer = PtpWire.ReadTimestamp(timestamp); } catch (InvalidDataException) { return; }
        var offset = (Int128)peer + correction - received;
        if (offset < long.MinValue || offset > long.MaxValue) return;
        // A change in epoch/master or a discontinuity invalidates readiness.
        // It must never silently move an already announced audio timeline.
        var delta = offset - _offset;
        if (_samples >= 8 && Int128.Abs(delta) > 5_000_000) return;
        if (_samples == 0 || Int128.Abs(delta) > 5_000_000)
        { _offset = (long)offset; _samples = 0; }
        else _offset = (long)((Int128)_offset + delta / 8);
        Count(received);
    }
    private void Count(long received)
    {
        if (_samples == 0 || received <= _last || received - _last > 1_000_000_000) { _first = received; _samples = 0; }
        _last = received; _samples++;
    }
    public (bool Ready, int Samples, ulong ClockId, long TimeNanoseconds) Snapshot(long localNanoseconds)
    {
        lock (_gate)
        {
            var fresh = _samples >= 8 && localNanoseconds >= _last && localNanoseconds - _last < 2_000_000_000 && _last - _first >= 2_300_000_000;
            var time = (Int128)localNanoseconds + (followReceiver ? _offset : 0);
            return (fresh && time >= 0 && time <= long.MaxValue, _samples, followReceiver ? _master : ownClockId,
                time >= 0 && time <= long.MaxValue ? (long)time : 0);
        }
    }
}
