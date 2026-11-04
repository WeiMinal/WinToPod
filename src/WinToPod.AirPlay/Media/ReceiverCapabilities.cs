// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.AirPlay.Discovery;

namespace WinToPod.AirPlay.Media;

// Transport capabilities, not an assertion of audible playback or Sound Glow.
// Unknown/new hardware uses its advertised bits, never a marketing-name allowlist.
public readonly record struct ReceiverCapabilities(bool Known, bool Ptp, bool TrackText, bool Artwork, bool Progress)
{
    public static ReceiverCapabilities Read(IReadOnlyDictionary<string, string?> fields, Dictionary<string, object?>? info)
    {
        ulong bits;
        if (info?.TryGetValue("features", out var value) == true && ReceiverAccess.Integer(value) is { } number) bits = number;
        else if (AirPlayFeatures.TryParse(fields.GetValueOrDefault("features"), out var features)) bits = features.Bits;
        else return default;
        return new(true, (bits & (1UL << 41)) != 0, (bits & (1UL << 17)) != 0,
            (bits & (1UL << 15)) != 0, (bits & (1UL << 16)) != 0);
    }

    public string DiagnosticFields => $"能力已知={(Known ? 1 : 0)}；PTP={(Ptp ? 1 : 0)}；曲目文本={(TrackText ? 1 : 0)}；封面={(Artwork ? 1 : 0)}；进度={(Progress ? 1 : 0)}";
}
