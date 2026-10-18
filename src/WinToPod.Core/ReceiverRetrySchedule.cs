// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.Core;

// Device-requested backoff belongs to the rejecting physical receiver, not a
// speaker group, a playback/test mode or the duration of a successful session.
public sealed class ReceiverRetrySchedule(Dictionary<string, DateTimeOffset> deadlines)
{
    public void Record(string receiverId, ulong seconds, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(receiverId)) throw new ArgumentException("Receiver ID required.");
        var maximum = (DateTimeOffset.MaxValue - now).TotalSeconds;
        var until = seconds >= maximum ? DateTimeOffset.MaxValue : now.AddSeconds(seconds);
        if (!deadlines.TryGetValue(receiverId, out var existing) || until > existing) deadlines[receiverId] = until;
    }
    public long Remaining(IEnumerable<string> receiverIds, DateTimeOffset now)
    {
        long maximum = 0;
        foreach (var id in receiverIds)
            if (deadlines.TryGetValue(id, out var until)) maximum = Math.Max(maximum, (long)Math.Ceiling(Math.Max(0, (until - now).TotalSeconds)));
        return maximum;
    }
}
