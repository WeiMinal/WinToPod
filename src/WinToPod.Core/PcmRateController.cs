// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.Core;

// Queue feedback, not a hardware-clock estimator. Called once per output block.
public sealed class PcmRateController
{
    public const int SampleRate = 44100, TargetFrames = 4410;
    public const double MaximumPpm = 1000, SlewPpmPerSecond = 20;
    private double _filtered, _integral;
    public double CorrectionPpm { get; private set; }
    public double Observe(int bufferedFrames, int outputFrames)
    {
        if (bufferedFrames < 0 || outputFrames <= 0) throw new ArgumentOutOfRangeException();
        var seconds = outputFrames / (double)SampleRate;
        var error = (bufferedFrames - TargetFrames) / (double)SampleRate;
        _filtered += (1 - Math.Exp(-seconds)) * (error - _filtered);
        _integral = Math.Clamp(_integral + _filtered * seconds * 100, -MaximumPpm, MaximumPpm);
        var desired = Math.Clamp(_filtered * 20000 + _integral, -MaximumPpm, MaximumPpm);
        CorrectionPpm += Math.Clamp(desired - CorrectionPpm, -SlewPpmPerSecond * seconds, SlewPpmPerSecond * seconds);
        return 1 + CorrectionPpm / 1e6;
    }
    public void Reset() { _filtered = _integral = CorrectionPpm = 0; }
}
