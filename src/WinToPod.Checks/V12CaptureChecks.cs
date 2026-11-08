// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using WinToPod.Core;
using WinToPod.AirPlay.Media;
internal static class V12CaptureChecks
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("V1.2｜采集闭环五档偏差8小时（控制器模型）", () =>
        {
            foreach (var ppm in new[] { -100, -50, 0, 50, 100 })
            {
                var rate = new PcmRateController(); double queue = 4410, previous = 0; var hour = 1;
                for (var i = 0; i * 352d / 44100 < 28800; i++)
                {
                    var ratio = rate.Observe((int)Math.Round(queue), 352);
                    Require(Math.Abs(rate.CorrectionPpm) <= 1000 && Math.Abs(rate.CorrectionPpm - previous) <= 20 * 352d / 44100 + 1e-9, "Rate limit/slew failed."); previous = rate.CorrectionPpm;
                    queue += 352 * (1 + ppm / 1e6 - ratio);
                    Require(queue > 352 && queue < 8820, "Queue diverged.");
                    if (i * 352d / 44100 >= hour * 3600 - 352d / 44100)
                    {
                        Require(Math.Abs(queue - 4410) < 2 && Math.Abs(rate.CorrectionPpm - ppm) < 2, "Rate did not converge.");
                        Console.WriteLine(FormattableString.Invariant($"V12_CAPTURE_MODEL ppm={ppm}; hour={hour++}; buffered={queue:F3}; correction_ppm={rate.CorrectionPpm:F3}; waveform=false"));
                    }
                }
                Require(hour == 9, "Missing hourly observations."); rate.Reset(); Require(rate.CorrectionPpm == 0, "Reset failed.");
            }
            return Task.CompletedTask;
        });
        yield return ("V1.2｜重采样静音、直流与声道", () =>
        {
            foreach (var level in new short[] { 0, 1, 12000, 32767 })
            {
                var b = new ClockAlignedPcmBuffer(); var data = Constant(6000, level); var output = new byte[1408]; b.Write(data);
                for (var block = 0; block < 200; block++)
                {
                    b.Write(data.AsSpan(0, 1408)); b.Read(output);
                    if (block == 0) continue;
                    for (var at = 0; at < output.Length; at += 4)
                    {
                        Require(Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(at)) - level) <= 1, "DC gain changed.");
                        Require(Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(at + 2)) + level) <= 1, "Stereo phase changed.");
                    }
                }
                Require(b.Snapshot.ClippedSamples == 0, "DC clips.");
            }
            return Task.CompletedTask;
        });
        yield return ("V1.2｜重采样100Hz至20kHz误差", () =>
        {
            foreach (var hz in new[] { 100, 1000, 10000, 18000, 20000 }) Tone(hz);
            return Task.CompletedTask;
        });
        yield return ("V1.2｜采集断供恢复、溢出与无分配", () =>
        {
            var b = new ClockAlignedPcmBuffer(); var output = new byte[1408]; b.Write(Constant(5000, 10000));
            for (var i = 0; i < 50; i++) b.Read(output);
            Require(output.All(v => v == 0) && b.Snapshot.RebufferCount == 1 && b.Snapshot.CorrectionPpm == 0, "Idle retained data.");
            b.Write(Constant(2000, -4000)); b.Read(output); Require(output.All(v => v == 0), "Early resume.");
            b.Write(Constant(3000, -4000)); b.Read(output); b.Read(output);
            Require(BinaryPrimitives.ReadInt16LittleEndian(output) == -4000, "Stale audio replayed.");
            b.Write(new byte[100000 * 4]); Require(b.Snapshot.Buffer.DroppedFrames > 0 && b.Snapshot.Buffer.BufferedFrames <= 44100, "Overflow unbounded.");
            b.Clear(); b.Write(new byte[5000 * 4]); var input = new byte[1408];
            for (var i = 0; i < 100; i++) { b.Write(input); b.Read(output); }
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++) { b.Write(input); b.Read(output); _ = b.Snapshot; }
            Require(GC.GetAllocatedBytesForCurrentThread() == allocated, "Hot path allocates.");
            return Task.CompletedTask;
        });
        yield return ("V1.2｜采集补偿与真实发送循环双向偏差", async () =>
        {
            foreach (var ppm in new[] { -100, 100 })
            {
                await using var f = await Base01LoopbackFixture.OpenAsync(ptp: true, ppm: ppm, mode: PlaybackTimingMode.ClockAligned);
                var b = new ClockAlignedPcmBuffer(); b.Write(Constant(5000, 1000)); var input = Constant(44100, 1000); long written = 0;
                await f.Session.RunMembersAsync([f.Session], (memory, _) =>
                {
                    var expected = (long)(f.Time.GetTimestamp() * 44100d / f.Time.Frequency);
                    var count = checked((int)(expected - written)); b.Write(input.AsSpan(0, count * 4)); written = expected; b.Read(memory.Span);
                    return ValueTask.CompletedTask;
                }, TimeSpan.FromSeconds(30), default);
                await f.DrainAsync();
                Require(b.Snapshot is { RebufferCount: 0, Buffer.DroppedFrames: 0, Buffer.MissingFrames: 0 }, "Capture lost frames.");
                Require(f.ReceivedPackets == f.Session.PacketsSent, "RTP loss.");
            }
        });
    }
    private static byte[] Constant(int frames, short value)
    {
        var result = new byte[frames * 4];
        for (var i = 0; i < result.Length; i += 4) { BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i), value); BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i + 2), (short)-value); }
        return result;
    }
    private static void Tone(int hz)
    {
        var b = new ClockAlignedPcmBuffer(); long generated = 0; double position = 0, error = 0, reference = 0;
        var input = new byte[24000]; var output = new byte[1408];
        void Supply(int frames)
        {
            for (var i = 0; i < frames; i++, generated++)
            {
                var value = (short)Math.Round(8000 * Math.Sin(2 * Math.PI * hz * generated / 44100));
                BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(i * 4), value); BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(i * 4 + 2), (short)-value);
            }
            b.Write(input.AsSpan(0, frames * 4));
        }
        Supply(6000);
        for (var block = 0; block < 400; block++)
        {
            if (block > 0) Supply(352); b.Read(output); var ratio = 1 + b.Snapshot.CorrectionPpm / 1e6;
            for (var i = 0; i < 352; i++, position += ratio)
            {
                if (block < 10) continue;
                var expected = 8000 * Math.Sin(2 * Math.PI * hz * position / 44100);
                var actual = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i * 4));
                Require(Math.Abs(actual + BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i * 4 + 2))) <= 1, "Channel phase differs.");
                error += Math.Pow(actual - expected, 2); reference += expected * expected;
            }
        }
        var db = 10 * Math.Log10(error / reference);
        Console.WriteLine(FormattableString.Invariant($"V12_CAPTURE_TONE hz={hz}; error_db={db:F3}; listening_test=false"));
        Require(db < -60 && b.Snapshot.ClippedSamples == 0, "Waveform error exceeds -60dB.");
    }
}
