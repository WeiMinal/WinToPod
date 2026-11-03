// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.AirPlay.Media;

public readonly record struct SendScheduleSnapshot(long Observations, long LastLatenessTicks, long MaxLatenessTicks,
    long Over50Ms, long Over100Ms, long Over250Ms)
{
    internal string DiagnosticFields => FormattableString.Invariant(
        $"观测={Observations}；本次落后ms={LastLatenessTicks / (double)TimeSpan.TicksPerMillisecond:F3}；最大落后ms={MaxLatenessTicks / (double)TimeSpan.TicksPerMillisecond:F3}；落后>50ms={Over50Ms}；落后>100ms={Over100Ms}；落后>250ms={Over250Ms}");
}

internal sealed class SendScheduleDiagnostics
{
    private readonly object _gate = new();
    private SendScheduleSnapshot _snapshot;
    public SendScheduleSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public void Observe(TimeSpan remaining)
    {
        var late = remaining < TimeSpan.Zero ? -remaining.Ticks : 0;
        lock (_gate)
        {
            var previous = _snapshot;
            _snapshot = new(previous.Observations + 1, late, Math.Max(previous.MaxLatenessTicks, late),
                previous.Over50Ms + (late > 50 * TimeSpan.TicksPerMillisecond ? 1 : 0),
                previous.Over100Ms + (late > 100 * TimeSpan.TicksPerMillisecond ? 1 : 0),
                previous.Over250Ms + (late > 250 * TimeSpan.TicksPerMillisecond ? 1 : 0));
        }
    }
}
