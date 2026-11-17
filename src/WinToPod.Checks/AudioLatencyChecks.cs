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
        yield return ("音频延迟｜WinPods独立PTP报文向量含回绕与首包逐字节一致", () =>
        {
            // WinPods 5d6d291, crates/airplay/tests/fixtures/anchors.json (MIT).
            // Copyright (c) 2026 Ziyang Huang; license: licenses/WinPods-MIT.txt.
            Base01Checks.True(Convert.ToHexString(NetworkClock.PtpSync(unchecked((uint)(4410UL - 4410UL)), 48766432109L, 192172642282561006UL, true, 4410U)).Equals("90d70006000000000000000b5ab4b36d0000113a02aabbfffeccddee", StringComparison.OrdinalIgnoreCase));
            Base01Checks.True(Convert.ToHexString(NetworkClock.PtpSync(unchecked((uint)(48510UL - 4410UL)), 49766432109L, 192172642282561006UL, false, 4410U)).Equals("80d700060000ac440000000b964f7d6d0000bd7e02aabbfffeccddee", StringComparison.OrdinalIgnoreCase));
            Base01Checks.True(Convert.ToHexString(NetworkClock.PtpSync(unchecked((uint)(92610UL - 4410UL)), 50766432109L, 192172642282561006UL, false, 4410U)).Equals("80d70006000158880000000bd1ea476d000169c202aabbfffeccddee", StringComparison.OrdinalIgnoreCase));
            Base01Checks.True(Convert.ToHexString(NetworkClock.PtpSync(unchecked((uint)(92612UL - 4410UL)), 51766432109L, 192172642282561006UL, false, 4410U)).Equals("80d700060001588a0000000c0d85116d000169c402aabbfffeccddee", StringComparison.OrdinalIgnoreCase));
            Base01Checks.True(Convert.ToHexString(NetworkClock.PtpSync(unchecked((uint)(3000UL - 4410UL)), 52766432109L, 192172642282561006UL, false, 4410U)).Equals("80d70006fffffa7e0000000c491fdb6d00000bb802aabbfffeccddee", StringComparison.OrdinalIgnoreCase));
            Base01Checks.True(Convert.ToHexString(NetworkClock.PtpSync(unchecked((uint)(4294968296UL - 4410UL)), 53766432109L, 192172642282561006UL, false, 4410U)).Equals("80d70006fffff2ae0000000c84baa56d000003e802aabbfffeccddee", StringComparison.OrdinalIgnoreCase));
            Base01Checks.True(Convert.ToHexString(NetworkClock.PtpSync(unchecked((uint)(4294967396UL - 4410UL)), 54766432109L, 192172642282561006UL, false, 4410U)).Equals("80d70006ffffef2a0000000cc0556f6d0000006402aabbfffeccddee", StringComparison.OrdinalIgnoreCase));
            Base01Checks.True(Convert.ToHexString(NetworkClock.PtpSync(unchecked((uint)(12884901895UL - 88200UL)), 55766432109L, 192172642282561006UL, false, 88200U)).Equals("80d70006fffea77f0000000cfbf0396d0000000702aabbfffeccddee", StringComparison.OrdinalIgnoreCase));
            Base01Checks.True(Convert.ToHexString(NetworkClock.PtpSync(unchecked((uint)(4410UL - 4410UL)), 56766432109L, 192172642282561006UL, true, 4410U)).Equals("90d70006000000000000000d378b036d0000113a02aabbfffeccddee", StringComparison.OrdinalIgnoreCase));
            Base01Checks.True(Convert.ToHexString(NetworkClock.PtpSync(unchecked((uint)(4294967295UL - 0UL)), 57766432109L, 17485995211385265646UL, false, 0U)).Equals("80d70006ffffffff0000000d7325cd6dfffffffff2aabbfffeccddee", StringComparison.OrdinalIgnoreCase));
            Base01Checks.True(Convert.ToHexString(NetworkClock.PtpSync(unchecked((uint)(7930UL - 4410UL)), 58766432109L, 192172642282561006UL, false, 4410U)).Equals("80d7000600000dc00000000daec0976d00001efa02aabbfffeccddee", StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        });
        yield return ("音频延迟｜标准与600ms实验目标分别受接收范围约束", () =>
        {
            Base01Checks.True(Read(11025, 88200, AudioLatencyMode.Standard).SelectedFrames == 88200);
            Base01Checks.True(Read(11025, 88200).SelectedFrames == 26460);
            Base01Checks.True(Read(44100, 88200).SelectedFrames == 44100);
            Base01Checks.True(Read(11025, 22050).SelectedFrames == 22050);
            Base01Checks.True(Read(132300, 176400).SelectedFrames == 132300);
            return Task.CompletedTask;
        });
        yield return ("音频延迟｜缺少范围不静默退回两秒", () =>
        {
            foreach (var report in new[] { Read(null, null), Read(11025, null), Read(null, 88200) })
                Base01Checks.True(report.SelectedFrames == 26460 && report.Decision == "receiver-window-incomplete-requested-target");
            Base01Checks.True(Read(null, 44100).SelectedFrames == 26460);
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
            Base01Checks.True(pair.All(r => r.SelectedFrames == 26460));
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
