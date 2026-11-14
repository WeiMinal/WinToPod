// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.GlobalSyncLab;

// Ownership transfers on Push and TakeDue; all superseded/capped frames are disposed.
// Monotonic milliseconds only. No screen or media data is persisted.
internal sealed class DelayQueue<T>(int capacity) : IDisposable where T : class, IDisposable
{
    private readonly Queue<(long At, T Value)> _frames = new();
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private long _last = -1;
    public int Count => _frames.Count;
    public void Push(long at, T value)
    {
        if (at < 0 || at < _last) { value.Dispose(); throw new ArgumentOutOfRangeException(nameof(at)); }
        _last = at;
        while (_frames.Count >= _capacity) _frames.Dequeue().Value.Dispose();
        _frames.Enqueue((at, value));
    }
    public (long At, T Value)? TakeDue(long now, int delayMs)
    {
        if (delayMs is < 0 or > 5000) throw new ArgumentOutOfRangeException(nameof(delayMs));
        (long At, T Value)? selected = null;
        while (_frames.TryPeek(out var next) && next.At <= now - delayMs)
        {
            selected?.Value.Dispose();
            selected = _frames.Dequeue();
        }
        return selected;
    }
    public void Clear()
    {
        while (_frames.TryDequeue(out var frame)) frame.Value.Dispose();
        _last = -1;
    }
    public void Dispose() => Clear();
}
