// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WinToPod.Core;

// A fresh key per export prevents matching receiver tokens across diagnostics.
// This is a best-effort text filter, not a promise to anonymize arbitrary data.
public sealed partial class DiagnosticRedactor(IEnumerable<string> labels, IEnumerable<string> paths) : IDisposable
{
    private readonly HMACSHA256 _identifiers = new();
    private readonly string[] _labels = labels.Where(s => s.Length > 1).Distinct().OrderByDescending(s => s.Length).ToArray();
    private readonly string[] _paths = paths.Where(s => s.Length > 1).Distinct().OrderByDescending(s => s.Length).ToArray();
    public string Token(string value) => Convert.ToHexString(_identifiers.ComputeHash(Encoding.UTF8.GetBytes(value)))[..12];
    public string Redact(string text)
    {
        text = WindowsPath().Replace(text, "<path>");
        foreach (var path in _paths) text = text.Replace(path, "<path>", StringComparison.OrdinalIgnoreCase);
        text = IPv4().Replace(text, "<IPv4>");
        text = Mac().Replace(text, "<device-id>");
        text = IPv6Candidate().Replace(text, m => IPAddress.TryParse(m.Value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? "<IPv6>" : m.Value);
        foreach (var label in _labels) text = text.Replace(label, "<private>", StringComparison.OrdinalIgnoreCase);
        return text;
    }
    public void Dispose() => _identifiers.Dispose();
    [GeneratedRegex(@"(?i)\b[a-z]:\\[^\r\n\""<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IPv4();
    [GeneratedRegex(@"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Mac();
    [GeneratedRegex(@"(?i)(?<![\w:])(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}(?:%[0-9a-z_.-]+)?(?![\w:])", RegexOptions.CultureInvariant)]
    private static partial Regex IPv6Candidate();
}
