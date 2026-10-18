// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text.RegularExpressions;
using WinToPod.AirPlay.Discovery;
using WinToPod.AirPlay.Pairing;
using WinToPod.AirPlay.Serialization;

namespace WinToPod.AirPlay.Transport;

// Allowlist, not a raw wire dump. No arbitrary header values, response text,
// identifiers, SRP material, passwords, IP addresses or audio enter the log.
public static partial class RtspDiagnostics
{
    public static string Describe(string method, string path, string? contentType, int requestBytes, RtspResponse response, string? requestHkp = null)
    {
        var operation = method is "GET" or "POST" or "SETUP" or "SETPEERS" or "RECORD" or "GET_PARAMETER" or "SET_PARAMETER" or "TEARDOWN" ? method : "OTHER";
        var endpoint = path is "/info" or "/pair-pin-start" or "/pair-setup" or "/pair-verify" or "/feedback" ? path : "session";
        var parts = new List<string>
        {
            $"RTSP {operation} {endpoint} → {response.Status}",
            $"requestType={MediaType(contentType)}", $"requestBytes={requestBytes}",
            $"responseType={MediaType(response.Headers.GetValueOrDefault("Content-Type"))}", $"responseBytes={response.Body.Length}"
        };
        if (response.Headers.TryGetValue("Server", out var server)) parts.Add("server=" + (ServerVersion().IsMatch(server) ? server : "other"));
        if (response.Headers.TryGetValue("WWW-Authenticate", out var challenge))
            parts.Add("challenge=" + (challenge.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase) ? "Digest" : "other"));
        if (path is "/pair-setup" or "/pair-pin-start" or "/pair-verify")
        {
            parts.Add("HKP=" + (requestHkp is "3" or "4" ? requestHkp : "unknown"));
            if (response.Body.Length == 0 && path == "/pair-pin-start") return string.Join("; ", parts);
            try
            {
                var tlv = Tlv8.Decode(response.Body);
                foreach (var (tag, name) in new[] { (Tlv8.Salt, "saltBytes"), (Tlv8.PublicKey, "publicKeyBytes"), (Tlv8.Proof, "proofBytes"), (Tlv8.EncryptedData, "encryptedBytes") })
                    if (tlv.TryGetValue(tag, out var material)) parts.Add($"{name}={material.Length}");
                if (tlv.TryGetValue(Tlv8.Flags, out var flags) && flags.Length is >= 1 and <= 4)
                {
                    Span<byte> paddedFlags = stackalloc byte[4]; paddedFlags.Clear(); flags.CopyTo(paddedFlags);
                    parts.Add("pairingFlags=0x" + BinaryPrimitives.ReadUInt32LittleEndian(paddedFlags).ToString("X"));
                }
                foreach (var (tag, name) in new[] { (Tlv8.State, "tlvState"), (Tlv8.Error, "tlvError") })
                    if (tlv.TryGetValue(tag, out var value) && value.Length == 1) parts.Add($"{name}={value[0]}");
                if (tlv.TryGetValue(Tlv8.RetryDelay, out var delay) && delay.Length is >= 1 and <= 4)
                {
                    Span<byte> padded = stackalloc byte[4]; padded.Clear(); delay.CopyTo(padded);
                    parts.Add("retryDelay=" + BinaryPrimitives.ReadUInt32LittleEndian(padded));
                }
            }
            catch (InvalidDataException) { parts.Add("tlv=unrecognised"); }
        }
        if (path == "/info" && response.Status == 200 && response.Body.Length != 0)
        {
            try
            {
                var info = BinaryPlist.Dictionary(response.Body);
                foreach (var key in new[] { "statusFlags", "features" })
                    if (info.TryGetValue(key, out var value) && ReceiverAccess.Integer(value) is { } number) parts.Add($"{key}=0x{number:X}");
            }
            catch (InvalidDataException) { parts.Add("info=unrecognised"); }
        }
        return string.Join("; ", parts);
    }

    private static string MediaType(string? value) => value?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        null => "none", "application/octet-stream" => "application/octet-stream",
        "application/pairing+tlv8" => "application/pairing+tlv8", "application/x-apple-binary-plist" => "application/x-apple-binary-plist",
        "text/parameters" => "text/parameters", _ => "other"
    };

    [GeneratedRegex(@"\AAirTunes/[0-9]{1,4}(?:\.[0-9]{1,4}){0,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ServerVersion();
}
