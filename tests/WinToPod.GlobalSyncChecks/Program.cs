// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.GlobalSyncLab;
using System.Text.Json;

var checks = new List<string>();
void Require(bool condition) { if (!condition) throw new Exception("Delay queue assertion failed."); }
using (var queue = new DelayQueue<Frame>(80))
{
    var first = new Frame(); var second = new Frame();
    queue.Push(0, first); queue.Push(67, second);
    Require(queue.TakeDue(1999, 2000) is null);
    var due = queue.TakeDue(2000, 2000);
    Require(due?.Value == first && !first.Disposed && queue.Count == 1);
    due?.Value.Dispose();
    checks.Add("prebuffer does not present early; exact timestamp boundary transfers ownership");
    queue.Clear(); Require(second.Disposed);
    queue.Push(1, new Frame()); // Clear resets timestamp generation.
    checks.Add("reset releases pending frames and resets timeline");
}
using (var queue = new DelayQueue<Frame>(2))
{
    var a = new Frame(); var b = new Frame(); var c = new Frame();
    queue.Push(1, a); queue.Push(2, b); queue.Push(3, c);
    Require(a.Disposed && queue.Count == 2);
    var due = queue.TakeDue(100, 0);
    Require(b.Disposed && due?.Value == c && !c.Disposed && queue.Count == 0);
    c.Dispose();
    checks.Add("bounded memory and slow consumer discard obsolete frames");
    var invalid = new Frame();
    try { queue.Push(2, invalid); throw new Exception("Accepted non-monotonic timestamp."); }
    catch (ArgumentOutOfRangeException) { Require(invalid.Disposed); }
    checks.Add("non-monotonic producer is rejected without leaking frame");
}
using (var queue = new DelayQueue<Frame>(80))
{
    for (var time = 0; time <= 6000; time += 67)
    {
        queue.Push(time, new Frame());
        var due = queue.TakeDue(time, 5000);
        if (due is { } frame) { Require(time - frame.At >= 5000 && time - frame.At < 5067); frame.Value.Dispose(); }
        Require(queue.Count <= 80);
    }
    checks.Add("maximum supported delay stays bounded with 67ms frame pacing");
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks, desktop_capture_tested = false, acoustic_sync_tested = false }));
sealed class Frame : IDisposable { public bool Disposed; public void Dispose() { if (Disposed) throw new Exception("Double dispose."); Disposed = true; } }
