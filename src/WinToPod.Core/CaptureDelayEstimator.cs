// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.Core;
public sealed class CaptureDelayEstimator
{
    private double _smoothed; private bool _hasValue;
    public int DelayMs => (int)Math.Round(_smoothed / 10) * 10;
    public void Observe(int bufferedFrames, double sinceCaptureMs)
    {
        if (bufferedFrames <= 0 || !double.IsFinite(sinceCaptureMs) || sinceCaptureMs < 0) return;
        var age = Math.Clamp(bufferedFrames * 1000d / 44100 + sinceCaptureMs, 0, 2000);
        _smoothed = _hasValue ? _smoothed + .02 * (age - _smoothed) : age; _hasValue = true;
    }
}
