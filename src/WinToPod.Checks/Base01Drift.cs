// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

internal static class Base01Drift
{
    internal readonly record struct Row(int Ppm, int TargetSeconds, double PtpSeconds, int Samples,
        double Milliseconds, double BufferPercent, double TheorySamples, double ErrorSamples);

    public static async Task<List<Row>> MeasureAsync(int ppm, int seconds, int interval)
    {
        await using var f = await Base01LoopbackFixture.OpenAsync(ptp: true, ppm: ppm);
        var rows = new List<Row>(); var next = interval;
        f.OnPtpSync = (rtp, render, nanoseconds) =>
        {
            if (nanoseconds < next * 1_000_000_000L || next > seconds) return;
            var elapsed = nanoseconds / 1e9;
            // Positive = send position advances faster than the render timeline.
            // Remove the negotiated lead before comparing the two packet fields.
            var delta = unchecked((int)(rtp - render - 88200));
            var theory = elapsed * ppm / 1e6 * 44100;
            // One 352-sample packet plus 1ms for the existing offset low-pass lag
            // and timestamp truncation. This validates the measuring fixture,
            // not a requirement that drift must remain below a chosen limit.
            Base01Checks.True(Math.Abs(delta - theory) <= 352 + 44.1);
            var row = new Row(ppm, next, elapsed, delta, delta * 1000d / 44100,
                delta * 100d / 88200, theory, delta - theory);
            rows.Add(row); next += interval;
        };
        // Duration is measured by the sender clock; advance at least `seconds`
        // on the peer clock too. Production's existing buffer tail remains intact.
        await f.Session.RunMembersAsync([f.Session], (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromSeconds(seconds * (1 + ppm / 1e6)), default);
        await f.DrainAsync();
        Base01Checks.True(rows.Count == seconds / interval && f.ReceivedPackets == f.Session.PacketsSent && f.SyncPackets > 0);
        Console.Error.WriteLine(FormattableString.Invariant($"BASE-01｜真实循环漂移：ppm={ppm}；虚拟秒={seconds}；RTP包={f.ReceivedPackets}；Sync包={f.SyncPackets}；最大落后ms={f.Session.SendSchedule.MaxLatenessTicks / 10000d:F3}"));
        return rows;
    }
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("BASE-01｜漂移量化：真实RunMembersAsync/ALAC/加密/UDP；PTP输入为假时钟直接驱动Tracker；未经过网络PTP或真机。缓冲=88200样本/2000ms。");
        Console.WriteLine("| ppm | 小时 | PTP实测秒 | RTP−Sync−缓冲（样本） | 毫秒 | 占缓冲% | 与理论差（样本） |");
        Console.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        var all = new List<Row>();
        foreach (var ppm in new[] { 100, 50, 0, -50, -100 })
        {
            var rows = await MeasureAsync(ppm, 8 * 3600, 3600); all.AddRange(rows);
            foreach (var row in rows)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {row.Ppm} | {row.TargetSeconds / 3600} | {row.PtpSeconds:F6} | {row.Samples} | {row.Milliseconds:F3} | {row.BufferPercent:F3} | {row.ErrorSamples:F3} |"));
        }
        Console.WriteLine("BASE01_DRIFT_JSON " + JsonSerializer.Serialize(all));
        Console.WriteLine("BASE-01｜漂移装置校验 5/5；real_homepod=false；model_only=false"); return 0;
    }
}
