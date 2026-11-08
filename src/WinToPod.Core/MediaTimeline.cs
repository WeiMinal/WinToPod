// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.Core;

public static class MediaTimeline
{
    // SMTC position is a timestamped snapshot, often updated only on seek/pause.
    // Extrapolate only while the caller has just confirmed Playing.
    public static TimeSpan? Position(TimeSpan position, TimeSpan duration, DateTimeOffset updated,
        DateTimeOffset now, double? rate)
    {
        var age = (now - updated).TotalSeconds;
        if (duration <= TimeSpan.Zero || position < TimeSpan.Zero || position > duration ||
            age < 0 || age > 86400 || rate is not { } speed || !double.IsFinite(speed) || speed is <= 0 or > 4)
            return null;
        return TimeSpan.FromSeconds(Math.Min(duration.TotalSeconds, position.TotalSeconds + age * speed));
    }
}
