// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.AirPlay.Discovery;

namespace WinToPod.AirPlay.Media;

public enum AudioLatencyMode { Standard, ReducedExperimental }

// These are sender scheduling values and receiver-reported limits, NOT measured
// acoustic latency. Omitted response limits are not a request for a 2-second lead.
public sealed record AudioLatencyReport(AudioLatencyMode Mode, uint RequestedFrames,
    uint? ReceiverMinimumFrames, uint? ReceiverMaximumFrames, uint SelectedFrames,
    uint? ArrivalToRenderMs, string Decision)
{
    public double SenderLeadMs => SelectedFrames * 1000d / 44100;
}

internal static class AudioLatencyPolicy
{
    internal const uint StandardFrames = 88200, ReducedFrames = 26460, FloorFrames = 11025, CeilingFrames = 441000;

    public static AudioLatencyReport Read(AudioLatencyMode mode, IReadOnlyDictionary<string, object?> response)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        uint? ReadValue(string key, uint ceiling)
        {
            if (!response.TryGetValue(key, out var value)) return null;
            if (ReceiverAccess.Integer(value) is not { } number || number > ceiling)
                throw new InvalidDataException("音箱返回无效的音频延迟字段：" + key);
            return (uint)number;
        }
        var min = ReadValue("latencyMin", CeilingFrames);
        var max = ReadValue("latencyMax", CeilingFrames);
        var render = ReadValue("arrivalToRenderLatencyMs", 10000);
        var report = new AudioLatencyReport(mode, mode == AudioLatencyMode.Standard ? StandardFrames : ReducedFrames,
            min, max, 0, render, "");
        return SelectGroup([report])[0];
    }

    public static AudioLatencyReport[] SelectGroup(IReadOnlyList<AudioLatencyReport> reports)
    {
        if (reports.Count == 0 || reports.Select(r => r.Mode).Distinct().Count() != 1)
            throw new ArgumentException("音箱必须使用同一缓冲策略。", nameof(reports));
        var lower = Math.Max(FloorFrames, reports.Max(r => r.ReceiverMinimumFrames ?? FloorFrames));
        var upper = reports.Min(r => r.ReceiverMaximumFrames ?? CeilingFrames);
        if (lower > upper) throw new InvalidDataException("音箱音频缓冲范围不一致，已停止播放准备；不会向组合发送不同时间轴。");
        var reduced = reports[0].Mode == AudioLatencyMode.ReducedExperimental;
        var complete = reports.All(r => r.ReceiverMinimumFrames.HasValue && r.ReceiverMaximumFrames.HasValue);
        var target = reduced ? ReducedFrames : StandardFrames;
        var selected = Math.Clamp(target, lower, upper);
        var decision = reduced && !complete ? "receiver-window-incomplete-requested-target"
            : selected != target ? "clamped-to-common-receiver-window" : reduced ? "reduced-experiment" : "standard";
        return reports.Select(r => r with { SelectedFrames = selected, Decision = decision }).ToArray();
    }
}
