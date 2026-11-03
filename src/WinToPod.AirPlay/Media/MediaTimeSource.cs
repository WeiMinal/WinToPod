// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace WinToPod.AirPlay.Media;

// Internal seam: no UI setting, protocol negotiation or clock servo policy.
// Production forwards directly to the original APIs, including elapsed-time rounding.
internal abstract class MediaTimeSource
{
    public static MediaTimeSource System { get; } = new SystemMediaTimeSource();
    public abstract long GetTimestamp();
    public abstract long Frequency { get; }
    public abstract DateTimeOffset UtcNow { get; }
    public abstract TimeSpan GetElapsedTime(long start, long end);
    public TimeSpan GetElapsedTime(long start) => GetElapsedTime(start, GetTimestamp());
    public abstract Task DelayAsync(TimeSpan delay, CancellationToken cancellation);

    private sealed class SystemMediaTimeSource : MediaTimeSource
    {
        public override long GetTimestamp() => Stopwatch.GetTimestamp();
        public override long Frequency => Stopwatch.Frequency;
        public override DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public override TimeSpan GetElapsedTime(long start, long end) => Stopwatch.GetElapsedTime(start, end);
        public override Task DelayAsync(TimeSpan delay, CancellationToken cancellation) => Task.Delay(delay, cancellation);
    }
}
