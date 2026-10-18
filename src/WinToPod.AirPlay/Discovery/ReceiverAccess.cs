// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace WinToPod.AirPlay.Discovery;

public enum CredentialRequirement { Unknown, NoneAdvertised, Password, PinPairing }

// Unauthenticated hints, never proof of occupancy or authority to start playback.
// igl is a dynamic playback-group role; tight-sync leadership is status bit 13.
public static class ReceiverAccess
{
    public static ulong? StatusFlags(IReadOnlyDictionary<string, string?> fields)
    {
        foreach (var key in new[] { "flags", "sf" })
            if (fields.TryGetValue(key, out var value) && TryHex(value, out var flags)) return flags;
        return null;
    }

    public static bool IsTightSyncLeader(AirPlayReceiver receiver)
        => StatusFlags(receiver.Properties) is { } flags && (flags & (1UL << 13)) != 0;

    public static CredentialRequirement Credentials(IReadOnlyDictionary<string, string?> fields, Dictionary<string, object?>? info = null)
    {
        // Live /info overrides cached flags. pw is consulted only if not contradicted by live flags.
        var live = info is not null && info.TryGetValue("statusFlags", out var status) ? Integer(status) : null;
        var flags = live ?? StatusFlags(fields);
        if (flags is { } bits && (bits & 8) != 0) return CredentialRequirement.PinPairing;
        if (flags is { } mask && (mask & 128) != 0) return CredentialRequirement.Password;
        if (live is not null) return CredentialRequirement.NoneAdvertised;
        if (fields.TryGetValue("pw", out var pw))
        {
            if (pw is not null && (pw.Equals("true", StringComparison.OrdinalIgnoreCase) || pw == "1")) return CredentialRequirement.Password;
            if (pw is not null && (pw.Equals("false", StringComparison.OrdinalIgnoreCase) || pw == "0")) return CredentialRequirement.NoneAdvertised;
        }
        return flags is null ? CredentialRequirement.Unknown : CredentialRequirement.NoneAdvertised;
    }

    public static ulong? Integer(object? value) => value switch
    {
        ulong n => n, long n when n >= 0 => (ulong)n, uint n => n, int n when n >= 0 => (ulong)n, _ => null
    };

    private static bool TryHex(string? text, out ulong bits)
    {
        bits = 0; if (string.IsNullOrWhiteSpace(text)) return false;
        var word = text.AsSpan().Trim();
        if (word.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) word = word[2..];
        return ulong.TryParse(word, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bits);
    }
}
