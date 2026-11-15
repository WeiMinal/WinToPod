// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.AirPlay.Media;
using WinToPod.AirPlay.Serialization;
using WinToPod.Core;

internal static class AudioLatencyChecks
{
    private static AudioLatencyReport Read(uint? min, uint? max, AudioLatencyMode mode = AudioLatencyMode.ReducedExperimental)
    {
        var data = new Dictionary<string, object?>();
        if (min.HasValue) data["latencyMin"] = min.Value;
        if (max.HasValue) data["latencyMax"] = max.Value;
        return AudioLatencyPolicy.Read(mode, data);
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Invalid receiver window was accepted.");
    }
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("音频延迟｜标准与600ms实验目标分别受接收范围约束", () =>
        {
            Base01Checks.True(Read(11025, 88200, AudioLatencyMode.Standard).SelectedFrames == 88200);
            Base01Checks.True(Read(11025, 88200).SelectedFrames == 26460);
            Base01Checks.True(Read(44100, 88200).SelectedFrames == 44100);
            Base01Checks.True(Read(11025, 22050).SelectedFrames == 22050);
            Base01Checks.True(Read(132300, 176400).SelectedFrames == 132300);
            return Task.CompletedTask;
        });
        yield return ("音频延迟｜缺少范围不擅自降缓冲", () =>
        {
            foreach (var report in new[] { Read(null, null), Read(11025, null), Read(null, 88200) })
                Base01Checks.True(report.SelectedFrames == 88200 && report.Decision == "receiver-window-incomplete-standard-target");
            Base01Checks.True(Read(null, 44100).SelectedFrames == 44100);
            return Task.CompletedTask;
        });
        yield return ("音频延迟｜矛盾越界及非整数返回拒绝", () =>
        {
            Reject(() => Read(88200, 44100)); Reject(() => Read(0, 0)); Reject(() => Read(441001, null));
            foreach (object? invalid in new object?[] { -1, "11025", 1.5, true, null, ulong.MaxValue })
                Reject(() => AudioLatencyPolicy.Read(AudioLatencyMode.Standard, new Dictionary<string, object?> { ["latencyMin"] = invalid }));
            return Task.CompletedTask;
        });
        yield return ("音频延迟｜原生对采用公共范围及共同时间轴", () =>
        {
            var pair = AudioLatencyPolicy.SelectGroup([Read(11025, 88200), Read(44100, 66150)]);
            Base01Checks.True(pair.All(r => r.SelectedFrames == 44100));
            pair = AudioLatencyPolicy.SelectGroup([Read(11025, 88200), Read(null, null)]);
            Base01Checks.True(pair.All(r => r.SelectedFrames == 88200));
            Reject(() => AudioLatencyPolicy.SelectGroup([Read(11025, 22050), Read(44100, 88200)]));
            return Task.CompletedTask;
        });
        yield return ("音频延迟｜保留接收端渲染报告且不冒充测量", () =>
        {
            var report = AudioLatencyPolicy.Read(AudioLatencyMode.ReducedExperimental,
                new Dictionary<string, object?> { ["latencyMin"] = 11025UL, ["latencyMax"] = 88200UL, ["arrivalToRenderLatencyMs"] = 100UL });
            Base01Checks.True(report.ArrivalToRenderMs == 100 && report.SenderLeadMs == 600 && report.RequestedFrames == 26460);
            return Task.CompletedTask;
        });
        yield return ("音频延迟｜采集队列估算独立保留", () =>
        {
            var estimator = new CaptureDelayEstimator(); estimator.Observe(4410, 0);
            Base01Checks.True(estimator.DelayMs == 100);
            estimator.Observe(2205, 50); Base01Checks.True(estimator.DelayMs == 100);
            estimator.Observe(0, 0); estimator.Observe(4410, double.NaN);
            Base01Checks.True(estimator.DelayMs == 100);
            return Task.CompletedTask;
        });
        yield return ("音频延迟｜600ms选择实际进入PTP同步报文且禁止中途改锚", async () =>
        {
            await using var fixture = await Base01LoopbackFixture.OpenAsync(ptp: true, mode: PlaybackTimingMode.ClockAligned);
            fixture.Session.ApplyLatency(Read(11025, 88200));
            var observed = 0;
            fixture.OnPtpSync = (_, position, ns) =>
            {
                var expected = unchecked(Base01LoopbackFixture.InitialTimestamp - 26460U + (uint)((Int128)ns * 44100 / 1_000_000_000));
                Base01Checks.True(position == expected); observed++;
            };
            await fixture.Session.RunMembersAsync([fixture.Session], (_, _) => ValueTask.CompletedTask, TimeSpan.FromSeconds(2), default);
            await fixture.DrainAsync(); Base01Checks.True(observed >= 2 && fixture.ReceivedPackets == fixture.Session.PacketsSent);
            try { fixture.Session.ApplyLatency(Read(44100, 88200)); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Live timeline was changed.");
        });
    }
}
