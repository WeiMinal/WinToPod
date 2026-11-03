// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using WinToPod.AirPlay.Media;
using WinToPod.Core;

internal static class Base01Checks
{
    private const long Start = 10_000_000_000;
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return Check("0.5秒有效样本间隔保持就绪", () =>
        {
            var tracker = Ready(); var last = Start + 3_500_000_000;
            Probe(tracker, 8, last + 500_000_000);
            var state = tracker.Snapshot(last + 500_000_000); var d = tracker.Diagnostics(last + 500_000_000);
            True(state.Ready && state.Samples == 9 && d.SampleResets == 0 && d.ReadinessLosses == 0);
            True(d.IntervalLe500Ms == 8 && d.MaxIntervalNanoseconds == 500_000_000);
        });
        foreach (var gap in new[] { 1_200_000_000L, 3_000_000_000L })
            yield return Check($"{gap / 1e9:F1}秒间隔清零后重新累计", () =>
            {
                // 特性固定，后续策略变更需同步修改。
                var tracker = Ready(); var now = Start + 3_500_000_000 + gap;
                Probe(tracker, 8, now);
                var state = tracker.Snapshot(now); var d = tracker.Diagnostics(now);
                True(!state.Ready && state.Samples == 1 && d.SampleResets == 1 && d.GapResets == 1 && d.ReadinessLosses == 1);
                for (ushort i = 1; i <= 6; i++) Probe(tracker, (ushort)(8 + i), now + i * 500_000_000L);
                True(!tracker.Snapshot(now + 3_000_000_000).Ready);
                Probe(tracker, 15, now + 3_500_000_000);
                True(tracker.Snapshot(now + 3_500_000_000).Ready);
                Console.WriteLine("BASE-01 注入间隔：" + tracker.Diagnostics(now + 3_500_000_000).DiagnosticFields);
            });
        yield return Check("间隔恰好1秒不清零，新鲜度恰好2秒过期", () =>
        {
            // 特性固定，后续策略变更需同步修改。严格大于/小于边界不改。
            var tracker = Ready(); var last = Start + 4_500_000_000;
            Probe(tracker, 8, last); True(tracker.Snapshot(last).Ready);
            True(tracker.Snapshot(last + 1_999_999_999).Ready);
            True(!tracker.Snapshot(last + 2_000_000_000).Ready);
            True(!tracker.Snapshot(last + 3_000_000_000).Ready);
            var d = tracker.Diagnostics(last + 3_000_000_000);
            True(d.SampleResets == 0 && d.ReadinessLosses == 1 && d.SampleAgeNanoseconds == 3_000_000_000);
        });
        yield return Check("8个样本仍须满足2.3秒窗口", () =>
        {
            var tracker = new PtpClockTracker(66, false);
            for (ushort i = 0; i < 8; i++) Probe(tracker, i, Start + i * 300_000_000L);
            True(!tracker.Snapshot(Start + 2_100_000_000).Ready);
            Probe(tracker, 8, Start + 2_300_000_000);
            True(tracker.Snapshot(Start + 2_300_000_000).Ready);
        });
        yield return Check("8ms离群及持续突变的新增计数，不清除旧样本", () =>
        {
            // Existing PtpChecks already fixes readiness after isolated/persistent
            // jumps. This case adds exact rejection/loss counters and the 8ms input.
            var tracker = FollowReady(); var last = Start + 3_500_000_000;
            Base01LoopbackFixture.Observe(tracker, 8, last + 250_000_000, last + 258_000_000);
            var d = tracker.Diagnostics(last + 250_000_000);
            True(d.RejectedOffsetSamples == 1 && d.MaxOffsetJumpNanoseconds == 8_000_000 && d.SampleResets == 0);
            True(tracker.Snapshot(last + 250_000_000).TimeNanoseconds == last + 250_000_000);
            for (ushort i = 2; i <= 10; i++)
                Base01LoopbackFixture.Observe(tracker, (ushort)(7 + i), last + i * 250_000_000L, last + i * 250_000_000L + 8_000_000);
            d = tracker.Diagnostics(last + 2_500_000_000);
            True(d.RejectedOffsetSamples == 10 && d.ReadinessLosses == 1 && d.SampleResets == 0 && tracker.Snapshot(last + 2_500_000_000).Samples == 8);
            Console.WriteLine("BASE-01 注入突变：" + d.DiagnosticFields);
        });
        yield return Check("偏移差恰好5ms接受，大于5ms拒绝", () =>
        {
            // 特性固定，后续策略变更需同步修改。
            foreach (var jump in new[] { 5_000_000L, 5_000_001L })
            {
                var tracker = FollowReady(); var now = Start + 4_000_000_000;
                Base01LoopbackFixture.Observe(tracker, 8, now, now + jump);
                True(tracker.Diagnostics(now).RejectedOffsetSamples == (jump == 5_000_000 ? 0 : 1));
                True(tracker.Snapshot(now).Samples == (jump == 5_000_000 ? 9 : 8));
            }
        });
        yield return Check("到包间隔六桶的闭区间边界与无效包隔离", () =>
        {
            var tracker = new PtpClockTracker(66, false); long now = Start; ushort sequence = 0;
            Probe(tracker, sequence++, now);
            foreach (var gap in new[] { 150, 250, 500, 1000, 2000, 2001 }) { now += gap * 1_000_000L; Probe(tracker, sequence++, now); }
            var before = tracker.Diagnostics(now);
            tracker.Observe([], now + 1); tracker.Observe(PtpWire.Header(1, 44, 66, 0), now + 2);
            True(tracker.Diagnostics(now) == before);
            True(before.IntervalLe150Ms == 1 && before.IntervalLe250Ms == 1 && before.IntervalLe500Ms == 1 &&
                before.IntervalLe1000Ms == 1 && before.IntervalLe2000Ms == 1 && before.IntervalGt2000Ms == 1 && before.MaxIntervalNanoseconds == 2_001_000_000);
        });
        yield return Check("缓冲快照与原计数一致且保持只读值语义", () =>
        {
            var buffer = new PcmBuffer(1408); buffer.Write(new byte[2816]); buffer.Read(new byte[1412]);
            var snapshot = buffer.Snapshot;
            True(snapshot.DroppedFrames == buffer.DroppedFrames && snapshot.MissingFrames == buffer.MissingFrames &&
                snapshot.BufferedFrames * 4 == buffer.AvailableBytes && snapshot.CapacityFrames == 352);
            buffer.Write(new byte[1408]); buffer.Clear();
            True(snapshot == buffer.Snapshot && snapshot.DroppedFrames == 352 && snapshot.MissingFrames == 1);
        });
        yield return ("BASE-01｜默认时间源沿用Stopwatch舍入及取消，网络时钟可注入", async () =>
        {
            var system = MediaTimeSource.System;
            True(system.Frequency == Stopwatch.Frequency);
            foreach (var delta in new[] { -123456789L, 0, 1, 999999, 123456789, long.MaxValue / 100 })
                True(system.GetElapsedTime(42, 42 + delta) == Stopwatch.GetElapsedTime(42, 42 + delta));
            await system.DelayAsync(TimeSpan.Zero, default);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Throws<OperationCanceledException>(() => system.DelayAsync(TimeSpan.FromSeconds(1), cancel.Token));
            var time = new Base01TimeSource(); var clock = new NetworkClock(time);
            time.Advance(TimeSpan.FromMilliseconds(500));
            True(clock.UnixNanoseconds == 100_500_000_000 && clock.NtpNow == ((2208988900UL << 32) | 0x80000000));
        });
        yield return ("BASE-01｜PTP Read在2秒过期立即停止，没有缓冲期", async () =>
        {
            // 特性固定，后续策略变更需同步修改。
            await using var f = await Base01LoopbackFixture.OpenAsync(ptp: true);
            True(f.Ptp!.Read().TimeNanoseconds == 100_000_000_000);
            f.Time.Advanced = null; f.Time.Advance(TimeSpan.FromSeconds(2));
            var error = await Throws<IOException>(() => { f.Ptp.Read(); return Task.CompletedTask; });
            True(error.GetType() == typeof(IOException) && error.Message == "PTP时钟同步丢失，已停止发送，不自动重新连接。");
            True(f.Ptp.Diagnostics.ReadinessLosses == 1 && f.Ptp.Diagnostics.SampleAgeNanoseconds == 2_000_000_000);
            True(f.Trace.Any(s => s.Contains("stage=clock")) && f.Trace.Any(s => s.Contains("样本年龄ms=2000.000")));
            Base01Diagnostics.Write(f.Trace);
        });
        yield return ("BASE-01｜发送卡顿0.4秒继续，回环收到完整RTP", () => StallAsync(.4, false));
        yield return ("BASE-01｜发送卡顿0.6秒终止，异常与诊断保留", () => StallAsync(.6, true));
        yield return ("BASE-01｜发送保护恰好500ms不触发，多1tick触发", async () =>
        {
            // 特性固定，后续策略变更需同步修改。
            var frame = TimeSpan.FromSeconds(352 / 44100d);
            await StallAsync((frame + TimeSpan.FromMilliseconds(500)).TotalSeconds, false);
            await StallAsync((frame + TimeSpan.FromMilliseconds(500) + TimeSpan.FromTicks(1)).TotalSeconds, true);
        });
        yield return ("BASE-01｜漂移量化装置短程通过真实循环及UDP校验", async () =>
        {
            var rows = await Base01Drift.MeasureAsync(1000, 60, 30);
            True(rows.Count == 2);
        });
        yield return Check("PTP与发送统计热路径不逐包分配", () =>
        {
            var tracker = new PtpClockTracker(66, false); var schedule = new SendScheduleDiagnostics();
            var packet = PtpWire.Header(1, 44, 55, 0);
            void Observe(int i)
            {
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(30), (ushort)i);
                tracker.Observe(packet, Start + i * 125_000_000L); schedule.Observe(TimeSpan.FromMilliseconds(-60));
            }
            for (var i = 0; i < 100; i++) Observe(i);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 100; i < 1100; i++) Observe(i);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            True(allocated == 0 && schedule.Snapshot.Over50Ms == 1100 && schedule.Snapshot.Over100Ms == 0);
        });
    }
    private static async Task StallAsync(double seconds, bool shouldStop)
    {
        foreach (var latency in new[] { 11025U, 88200U })
        {
            await using var f = await Base01LoopbackFixture.OpenAsync(latency: latency);
            var reads = 0;
            Task Run() => f.Session.RunMembersAsync([f.Session], (_, _) =>
            {
                if (reads++ == 0) f.Time.Advance(TimeSpan.FromSeconds(seconds));
                return ValueTask.CompletedTask;
            }, TimeSpan.FromSeconds(1), default);
            if (shouldStop)
            {
                var error = await Throws<IOException>(Run);
                True(error.GetType() == typeof(IOException) && error.Message == "发送线程落后超过500ms，已停止，避免持续堆积延迟。");
                True(f.Trace.Any(s => s.Contains("会话故障：stage=control; kind=protocol; exception=IOException")));
                True(f.Session.PacketsSent == 1);
            }
            else { await Run(); True(f.Session.PacketsSent > 100); }
            await f.DrainAsync(); True(f.ReceivedPackets == f.Session.PacketsSent);
            var expected = TimeSpan.FromSeconds(seconds) - TimeSpan.FromSeconds(352 / 44100d);
            True(f.Session.SendSchedule.MaxLatenessTicks == expected.Ticks && f.Session.SendSchedule.Over250Ms > 0);
            True(f.Trace.Any(s => s.StartsWith("发送结束：", StringComparison.Ordinal)));
            Base01Diagnostics.Write(f.Trace);
        }
    }
    private static PtpClockTracker Ready()
    {
        var tracker = new PtpClockTracker(66, false);
        for (ushort i = 0; i < 8; i++) Probe(tracker, i, Start + i * 500_000_000L);
        True(tracker.Snapshot(Start + 3_500_000_000).Ready); return tracker;
    }
    private static PtpClockTracker FollowReady()
    {
        var tracker = new PtpClockTracker(66, true); tracker.Observe(PtpWire.Announce(55, 0), Start);
        for (ushort i = 0; i < 8; i++) Base01LoopbackFixture.Observe(tracker, i, Start + i * 500_000_000L, Start + i * 500_000_000L);
        True(tracker.Snapshot(Start + 3_500_000_000).Ready); return tracker;
    }
    private static void Probe(PtpClockTracker tracker, ushort sequence, long now) => tracker.Observe(PtpWire.Header(1, 44, 55, sequence), now);
    private static (string, Func<Task>) Check(string name, Action run) => ("BASE-01｜" + name, () => { run(); return Task.CompletedTask; });
    internal static void True(bool value) { if (!value) throw new Exception("BASE-01 特性固定/测试装置校验失败。"); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
}
