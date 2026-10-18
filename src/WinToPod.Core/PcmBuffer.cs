// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.Core;

// Bounded S16LE stereo ring. Overflow drops oldest whole frames, never grows latency forever.
public sealed class PcmBuffer
{
    private readonly byte[] _buffer; private int _start, _count; private long _dropped, _missing;
    private readonly object _gate = new();
    public PcmBuffer(int capacityBytes = 44100 * 4)
    { if (capacityBytes < 1408 || capacityBytes % 4 != 0) throw new ArgumentException("Whole stereo frames required."); _buffer = new byte[capacityBytes]; }
    public long DroppedFrames { get { lock (_gate) return _dropped; } }
    public long MissingFrames { get { lock (_gate) return _missing; } }
    public int AvailableBytes { get { lock (_gate) return _count; } }
    public void Write(ReadOnlySpan<byte> source)
    {
        if (source.Length % 4 != 0) throw new ArgumentException("Whole stereo frames required.");
        lock (_gate)
        {
            if (source.Length > _buffer.Length) { _dropped += (source.Length - _buffer.Length) / 4; source = source[^_buffer.Length..]; }
            var remove = Math.Max(0, _count + source.Length - _buffer.Length);
            _start = (_start + remove) % _buffer.Length; _count -= remove; _dropped += remove / 4;
            var at = (_start + _count) % _buffer.Length; var first = Math.Min(source.Length, _buffer.Length - at);
            source[..first].CopyTo(_buffer.AsSpan(at)); source[first..].CopyTo(_buffer); _count += source.Length;
        }
    }
    public void Read(Span<byte> destination)
    {
        if (destination.Length % 4 != 0) throw new ArgumentException("Whole stereo frames required.");
        lock (_gate)
        {
            destination.Clear(); var count = Math.Min(destination.Length, _count); var first = Math.Min(count, _buffer.Length - _start);
            _buffer.AsSpan(_start, first).CopyTo(destination); _buffer.AsSpan(0, count - first).CopyTo(destination[first..]);
            _start = (_start + count) % _buffer.Length; _count -= count; _missing += (destination.Length - count) / 4;
        }
    }
    public void Clear() { lock (_gate) { Array.Clear(_buffer); _start = _count = 0; } }
}
