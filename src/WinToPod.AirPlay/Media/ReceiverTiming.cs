// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.AirPlay.Discovery;

namespace WinToPod.AirPlay.Media;

public static class ReceiverTiming
{
    public static bool UsesPtp(IReadOnlyDictionary<string, string?> fields, Dictionary<string, object?>? info)
    {
        if (info?.TryGetValue("features", out var value) == true && ReceiverAccess.Integer(value) is { } bits)
            return (bits & (1UL << 41)) != 0;
        return fields.TryGetValue("features", out var text) && AirPlayFeatures.TryParse(text, out var features) && features.HasBit(41);
    }
    public static bool FollowsReceiver(IReadOnlyDictionary<string, string?> fields, Dictionary<string, object?>? info)
    {
        var model = info?.GetValueOrDefault("model") as string ?? fields.GetValueOrDefault("model");
        var os = info?.GetValueOrDefault("osVersion") as string ?? fields.GetValueOrDefault("osvers");
        // Current compatibility rule, based on measured standalone reports.
        // Pair mode still uses a shared sender clock; this does not implement
        // macOS-style BMCA/receiver-GM following. See the native-pair research.
        return model?.StartsWith("AudioAccessory", StringComparison.Ordinal) == true &&
            int.TryParse(os?.Split('.')[0], out var major) && major >= 27 && fields.GetValueOrDefault("igl") == "1" &&
            string.IsNullOrEmpty(fields.GetValueOrDefault("tsid")) && string.IsNullOrEmpty(fields.GetValueOrDefault("pgid"));
    }
}
