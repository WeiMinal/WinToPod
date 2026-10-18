// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace WinToPod.Core;

// Replaces PCM inside an existing stream. The caller keeps draining its live
// capture on every read, including during the test; no stale audio accumulates.
public sealed class SessionSoundTest
{
    private const int Rate = 44100, SegmentFrames = Rate * 3 / 2, ToneFrames = SegmentFrames * 3;
    private readonly object _gate = new();
    private long _frames, _end;
    private int _latency;
    private bool _active, _cancelled;

    public bool Start(int latencyFrames)
    {
        if (latencyFrames is < 0 or > Rate * 10) throw new ArgumentOutOfRangeException(nameof(latencyFrames));
        lock (_gate)
        {
            if (_active) return false;
            _frames = 0; _end = ToneFrames; _latency = latencyFrames; _cancelled = false; _active = true; return true;
        }
    }
    public void Stop()
    { lock (_gate) { if (!_active || _cancelled) return; _end = Math.Min(_frames, ToneFrames); _cancelled = true; } }
    public void Reset() { lock (_gate) _active = false; }
    public (bool Active, bool CanStop, string Description) Status
    {
        get
        {
            lock (_gate)
            {
                if (!_active) return (false, false, "");
                var audible = _frames - _latency;
                var description = _cancelled ? "正在恢复电脑声音……" : audible < 0 ? "测试即将开始，正在经过音箱缓冲……" :
                    audible >= ToneFrames ? "正在恢复电脑声音……" : audible < SegmentFrames ? "测试声音：左声道（低音）" :
                    audible < SegmentFrames * 2 ? "测试声音：右声道（高音）" : "测试声音：左右一起";
                return (true, !_cancelled && _frames < ToneFrames, description);
            }
        }
    }
    public void Apply(Span<byte> livePcm)
    {
        if (livePcm.Length % 4 != 0) throw new ArgumentException("Whole stereo frames required.");
        lock (_gate)
        {
            if (!_active) return;
            for (var at = 0; at < livePcm.Length; at += 4, _frames++)
            {
                if (_frames >= _end) continue;
                var segment = (int)(_frames / SegmentFrames);
                var local = (_frames % SegmentFrames) / (double)Rate;
                var envelope = Math.Min(1, Math.Min(local / .02, (1.5 - local) / .02));
                var time = _frames / (double)Rate;
                var left = segment is 0 or 2 ? (short)(Math.Sin(2 * Math.PI * 440 * time) * .12 * envelope * short.MaxValue) : (short)0;
                var right = segment is 1 or 2 ? (short)(Math.Sin(2 * Math.PI * 660 * time) * .12 * envelope * short.MaxValue) : (short)0;
                BinaryPrimitives.WriteInt16LittleEndian(livePcm[at..], left);
                BinaryPrimitives.WriteInt16LittleEndian(livePcm[(at + 2)..], right);
            }
            if (_frames >= _end + _latency) _active = false;
        }
    }
}
