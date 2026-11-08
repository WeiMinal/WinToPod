// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
namespace WinToPod.Core;

public readonly record struct CaptureRateSnapshot(PcmBufferSnapshot Buffer, double CorrectionPpm,
    long RebufferCount, long ClippedSamples, long OutputFrames, bool Primed);

// Bounded stereo queue with a shared phase for both channels. One lock protects
// capture, consumption and snapshots. Narrow ±1000ppm correction only.
public sealed class ClockAlignedPcmBuffer
{
    private const int Taps = 64, Half = 32, Phases = 4096, FadeFrames = 64;
    private static readonly float[] Coefficients = BuildCoefficients();
    private readonly object _gate = new();
    private readonly short[] _samples = new short[44100 * 2];
    private readonly PcmRateController _rate = new();
    private int _start, _count = Half - 1, _fade;
    private double _position = Half - 1;
    private bool _primed;
    private short _lastLeft, _lastRight;
    private long _dropped, _missing, _rebuffer, _clipped, _output;
    private int Capacity => _samples.Length / 2;
    private int Queued => Math.Max(0, _count - (int)_position);
    public CaptureRateSnapshot Snapshot
    {
        get { lock (_gate) return new(new(_dropped, _missing, Queued, Capacity), _rate.CorrectionPpm, _rebuffer, _clipped, _output, _primed); }
    }
    public void Write(ReadOnlySpan<byte> source)
    {
        if (source.Length % 4 != 0) throw new ArgumentException("Whole stereo frames required.", nameof(source));
        lock (_gate)
        {
            if (_count + source.Length / 4 > Capacity)
            {
                _dropped += Queued; ResetCore(); _rebuffer++;
                var excess = Math.Max(0, source.Length / 4 - (Capacity - _count));
                _dropped += excess; source = source[(excess * 4)..];
            }
            var at = (_start + _count) % Capacity;
            for (var i = 0; i < source.Length; i += 4)
            {
                _samples[at * 2] = BinaryPrimitives.ReadInt16LittleEndian(source[i..]);
                _samples[at * 2 + 1] = BinaryPrimitives.ReadInt16LittleEndian(source[(i + 2)..]);
                if (++at == Capacity) at = 0;
            }
            _count += source.Length / 4;
        }
    }
    public void Read(Span<byte> destination)
    {
        if (destination.Length % 4 != 0) throw new ArgumentException("Whole stereo frames required.", nameof(destination));
        if (destination.IsEmpty) return;
        lock (_gate)
        {
            var frames = destination.Length / 4; _output += frames; destination.Clear();
            if (!_primed)
            {
                if (Queued < PcmRateController.TargetFrames) { _missing += frames; return; }
                _primed = true; _fade = FadeFrames;
            }
            var ratio = _rate.Observe(Queued, frames); var produced = 0;
            for (; produced < frames; produced++)
            {
                var center = (int)_position;
                var phase = (int)Math.Round((_position - center) * Phases);
                if (phase == Phases) { center++; phase = 0; }
                if (center + Half >= _count) break;
                var at = (_start + center - (Half - 1)) % Capacity;
                double left = 0, right = 0;
                var weights = Coefficients.AsSpan(phase * Taps, Taps);
                for (var tap = 0; tap < Taps; tap++)
                {
                    left += _samples[at * 2] * weights[tap]; right += _samples[at * 2 + 1] * weights[tap];
                    if (++at == Capacity) at = 0;
                }
                if (_fade > 0) { var gain = (FadeFrames - --_fade) / (double)FadeFrames; left *= gain; right *= gain; }
                _lastLeft = Quantize(left); _lastRight = Quantize(right);
                Put(destination[(produced * 4)..], _lastLeft, _lastRight); _position += ratio;
            }
            var retired = Math.Max(0, (int)_position - (Half - 1));
            _start = (_start + retired) % Capacity; _count -= retired; _position -= retired;
            if (produced < frames)
            {
                var fade = Math.Min(FadeFrames, frames - produced);
                for (var i = 0; i < fade; i++) Put(destination[((produced + i) * 4)..],
                    (short)(_lastLeft * (fade - i - 1) / fade), (short)(_lastRight * (fade - i - 1) / fade));
                _missing += frames - produced; _dropped += Queued; _rebuffer++; ResetCore();
            }
        }
    }
    public void Clear() { lock (_gate) { _dropped += Queued; if (_primed) _rebuffer++; ResetCore(); } }
    private void ResetCore()
    {
        Array.Clear(_samples); _start = 0; _count = Half - 1; _position = Half - 1;
        _primed = false; _fade = 0; _lastLeft = _lastRight = 0; _rate.Reset();
    }
    private short Quantize(double value)
    {
        var rounded = Math.Round(value);
        if (rounded < short.MinValue || rounded > short.MaxValue) _clipped++;
        return (short)Math.Clamp(rounded, short.MinValue, short.MaxValue);
    }
    private static void Put(Span<byte> frame, short left, short right)
    {
        BinaryPrimitives.WriteInt16LittleEndian(frame, left); BinaryPrimitives.WriteInt16LittleEndian(frame[2..], right);
    }
    private static float[] BuildCoefficients()
    {
        // Unity-DC Blackman-windowed sinc. Immutable table shared by all sources.
        var result = new float[Phases * Taps];
        for (var phase = 0; phase < Phases; phase++)
        {
            double sum = 0;
            for (var tap = 0; tap < Taps; tap++)
            {
                var x = tap - (Half - 1) - phase / (double)Phases;
                var sinc = x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                var window = .42 + .5 * Math.Cos(Math.PI * x / Half) + .08 * Math.Cos(2 * Math.PI * x / Half);
                var weight = sinc * window; result[phase * Taps + tap] = (float)weight; sum += weight;
            }
            for (var tap = 0; tap < Taps; tap++) result[phase * Taps + tap] /= (float)sum;
        }
        return result;
    }
}
