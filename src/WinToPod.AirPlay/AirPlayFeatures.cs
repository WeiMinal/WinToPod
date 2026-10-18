// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace WinToPod.AirPlay;

// Source: public AirPlay service-discovery description. No device-compatibility claim.
public readonly record struct AirPlayFeatures(ulong Bits)
{
    public bool HasBit(int bit)
    {
        if (bit is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(bit));
        return (Bits & (1UL << bit)) != 0;
    }

    public static bool TryParse(string? text, out AirPlayFeatures features)
    {
        features = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var words = text.Split(',');
        if (words.Length is < 1 or > 2 || !TryWord(words[0], out var low)) return false;
        uint high = 0;
        if (words.Length == 2 && !TryWord(words[1], out high)) return false;
        features = new((ulong)low | ((ulong)high << 32));
        return true;
    }

    private static bool TryWord(string text, out uint value)
    {
        var word = text.AsSpan().Trim();
        if (word.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) word = word[2..];
        return uint.TryParse(word, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}
