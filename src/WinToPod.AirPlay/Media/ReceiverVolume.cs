// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.AirPlay.Media;

public enum ReceiverVolumeCurve { Native, DesktopCalibrated }

// A control mapping only: PCM remains untouched. The calibrated desktop curve
// uses two approximate listening comparisons, not an Apple-published curve.
public static class ReceiverVolume
{
    public static double Decibels(int percent, ReceiverVolumeCurve curve = ReceiverVolumeCurve.Native)
    {
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        if (!Enum.IsDefined(curve)) throw new ArgumentOutOfRangeException(nameof(curve));
        if (percent == 0) return -144;
        var position = percent / 100d;
        return -30 + 30 * Position(position, curve);
    }

    private static double Position(double x, ReceiverVolumeCurve curve) => curve == ReceiverVolumeCurve.Native
        ? x : (4 * x * x * x + 3 * x * x + 8 * x) / 15;

    public static int Percent(double decibels, ReceiverVolumeCurve curve = ReceiverVolumeCurve.Native)
    {
        if (!double.IsFinite(decibels)) throw new ArgumentOutOfRangeException(nameof(decibels));
        if (!Enum.IsDefined(curve)) throw new ArgumentOutOfRangeException(nameof(curve));
        if (decibels <= -30) return 0;
        if (decibels >= 0) return 100;
        var target = (decibels + 30) / 30;
        if (curve == ReceiverVolumeCurve.Native) return Math.Clamp((int)Math.Round(target * 100), 0, 100);
        // Invert the same smooth, strictly increasing curve for external volume
        // changes. Never reapply it to readback or the volume will drift.
        var low = 0d; var high = 1d;
        for (var i = 0; i < 40; i++)
        {
            var middle = (low + high) / 2;
            if (Position(middle, curve) < target) low = middle; else high = middle;
        }
        return Math.Clamp((int)Math.Round((low + high) * 50), 1, 100);
    }
}
