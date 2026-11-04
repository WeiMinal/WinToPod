// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.AirPlay.Media;

internal static class V12TimingChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("V1.2｜新型号按能力处理，27.2不自动授予功能", () =>
        {
            var fields = new Dictionary<string, string?> { ["model"] = "AudioAccessory999,1", ["osvers"] = "27.2" };
            Base01Checks.True(!ReceiverCapabilities.Read(fields, null).Known);
            fields["features"] = "0x38000,0x200";
            var capability = ReceiverCapabilities.Read(fields, null);
            Base01Checks.True(capability is { Known: true, Ptp: true, TrackText: true, Artwork: true, Progress: true });
            Base01Checks.True(ReceiverCapabilities.Read(fields, new() { ["features"] = 0UL }) == new ReceiverCapabilities(true, false, false, false, false));
            Base01Checks.True(!capability.DiagnosticFields.Contains("999") && !capability.DiagnosticFields.Contains("27.2"));
            return Task.CompletedTask;
        });
        foreach (var (stall, latency, stop) in new[] { (.4, 88200U, false), (.6, 88200U, false), (1.1, 88200U, true), (.2, 11025U, true) })
            yield return ($"V1.2｜卡顿{stall:F1}秒、缓冲{latency}样本", async () =>
            {
                await using var f = await Base01LoopbackFixture.OpenAsync(latency: latency, mode: PlaybackTimingMode.ClockAligned);
                var reads = 0; IOException? failure = null;
                try
                {
                    await f.Session.RunMembersAsync([f.Session], (_, _) =>
                    {
                        if (++reads == 20) f.Time.Advance(TimeSpan.FromSeconds(stall));
                        return ValueTask.CompletedTask;
                    }, TimeSpan.FromSeconds(2), default);
                }
                catch (IOException e) { failure = e; }
                Base01Checks.True((failure is not null) == stop);
                if (stop) Base01Checks.True(failure!.Message.Contains("恢复预算") && f.Session.PacketsSent == 19);
                else Base01Checks.True(f.Session.PacketsSent > 250);
                await f.DrainAsync(); Base01Checks.True(f.ReceivedPackets == f.Session.PacketsSent);
            });
        yield return ("V1.2｜对齐模式仍在PTP过期时停止", async () =>
        {
            await using var f = await Base01LoopbackFixture.OpenAsync(ptp: true, mode: PlaybackTimingMode.ClockAligned);
            f.Time.Advanced = null;
            IOException? failure = null;
            try { await f.Session.RunMembersAsync([f.Session], (_, _) => ValueTask.CompletedTask, TimeSpan.FromSeconds(4), default); }
            catch (IOException e) { failure = e; }
            Base01Checks.True(failure is not null && failure.Message.Contains("PTP"));
        });
        yield return ("V1.2｜恢复预算严格边界与时钟来源变更", () =>
        {
            foreach (var latency in new[] { 11025U, 88200U, 441000U })
            {
                var budget = PlaybackTimingPolicy.RecoveryBudget(latency);
                PlaybackTimingPolicy.CheckLateness(-budget, latency);
                var rejected = false;
                try { PlaybackTimingPolicy.CheckLateness(-budget - TimeSpan.FromTicks(1), latency); }
                catch (IOException) { rejected = true; }
                Base01Checks.True(rejected);
            }
            foreach (var current in new[] { (2UL, 100L), (1UL, 99L) })
            {
                var rejected = false;
                try { PlaybackTimingPolicy.Elapsed((1, 100), current); }
                catch (IOException) { rejected = true; }
                Base01Checks.True(rejected);
            }
            return Task.CompletedTask;
        });
        yield return ("V1.2｜双向时钟偏差真实发送循环短程回归", async () =>
        {
            await MeasureAsync(100, 120, 60); await MeasureAsync(-100, 120, 60);
        });
    }

    private static async Task MeasureAsync(int ppm, int seconds, int interval)
    {
        await using var f = await Base01LoopbackFixture.OpenAsync(ptp: true, ppm: ppm, mode: PlaybackTimingMode.ClockAligned);
        var next = interval; var rows = 0; var maximum = 0;
        f.OnPtpSync = (rtp, render, ns) =>
        {
            var delta = unchecked((int)(rtp - render - 88200)); maximum = Math.Max(maximum, Math.Abs(delta));
            Base01Checks.True(Math.Abs(delta) <= 352 + 45);
            if (ns < next * 1_000_000_000L || next > seconds) return;
            Console.WriteLine(FormattableString.Invariant($"| {ppm} | {next / 3600d:F3} | {delta} | {delta / 44.1:F3} |"));
            rows++; next += interval;
        };
        await f.Session.RunMembersAsync([f.Session], (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromSeconds(seconds * (1 + ppm / 1e6)), default);
        await f.DrainAsync();
        Base01Checks.True(rows == seconds / interval && f.ReceivedPackets == f.Session.PacketsSent);
        Console.WriteLine($"V12_TIMING ppm={ppm}; max_samples={maximum}; packets={f.ReceivedPackets}; real_homepod=false; capture_rate_compensated=false");
    }

    public static async Task<int> DriftAsync()
    {
        Console.WriteLine("| ppm | 虚拟小时 | RTP−Sync−缓冲样本差 | 毫秒 |\n| ---: | ---: | ---: | ---: |");
        foreach (var ppm in new[] { 100, 50, 0, -50, -100 }) await MeasureAsync(ppm, 8 * 3600, 3600);
        return 0;
    }
}
