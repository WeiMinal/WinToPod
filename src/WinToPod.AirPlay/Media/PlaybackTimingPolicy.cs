// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.AirPlay.Media;

// V1.2 prototype, explicitly selected by tests until capture-rate compensation
// and device validation are complete. Never changes PTP readiness or ownership.
public enum PlaybackTimingMode { Legacy, ClockAligned }

internal static class PlaybackTimingPolicy
{
    public static TimeSpan RecoveryBudget(uint latencySamples)
        => TimeSpan.FromSeconds(Math.Clamp(latencySamples / 44100d / 2, .05, 1));

    public static TimeSpan Elapsed((ulong ClockId, long TimeNanoseconds) origin,
        (ulong ClockId, long TimeNanoseconds) current)
    {
        if (current.ClockId != origin.ClockId || current.TimeNanoseconds < origin.TimeNanoseconds)
            throw new IOException("音箱时钟来源或时间线变化，已停止发送。");
        return TimeSpan.FromTicks((current.TimeNanoseconds - origin.TimeNanoseconds) / 100);
    }

    public static void CheckLateness(TimeSpan remaining, uint latencySamples)
    {
        if (remaining < -RecoveryBudget(latencySamples))
            throw new IOException("发送落后超出协商缓冲的恢复预算，已停止发送，不自动重新连接。");
    }
}
