// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WinToPod.AirPlay.Transport;

// Independent RFC 7616 / legacy RFC 2069 implementation. AirPlay uses an empty
// username. State belongs only to one authenticated RTSP connection; no redirects,
// cross-receiver credential sharing, or password/Authorization logging.
public sealed class DigestChallenge
{
    private readonly Dictionary<string, string> _fields;
    public string Algorithm { get; }
    public bool UsesQop { get; }
    public string BaseAlgorithm => Algorithm.StartsWith("SHA-256", StringComparison.Ordinal) ? "SHA-256" : "MD5";
    public string Scope => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("WinToPod Digest\n\n" + _fields["realm"] + "\n" + BaseAlgorithm)));
    private DigestChallenge(Dictionary<string, string> fields)
    {
        _fields = fields;
        if (!fields.TryGetValue("realm", out _) || !fields.TryGetValue("nonce", out var nonce) || nonce.Length == 0)
            throw new PlaybackAuthenticationException("音箱的播放密码验证缺少必要信息。请导出诊断。");
        Algorithm = fields.GetValueOrDefault("algorithm", "MD5").ToUpperInvariant();
        if (Algorithm is not ("MD5" or "MD5-SESS" or "SHA-256" or "SHA-256-SESS"))
            throw new PlaybackAuthenticationException("音箱要求的播放密码算法暂未支持。请导出诊断。");
        if (fields.TryGetValue("qop", out var qop))
        {
            if (!qop.Split(',').Any(x => x.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase)))
                throw new PlaybackAuthenticationException("音箱要求的播放密码保护方式暂未支持。请导出诊断。");
            UsesQop = true;
        }
        if (fields.TryGetValue("charset", out var charset) && !charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ||
            fields.GetValueOrDefault("userhash", "false").Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new PlaybackAuthenticationException("音箱要求的播放密码编码暂未支持。请导出诊断。");
    }

    public static DigestChallenge Parse(string header)
    {
        if (header.Length > 4096 || header.Any(c => c < 32 && c != '\t' || c == 127) ||
            !header.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
            throw new PlaybackAuthenticationException("音箱返回的播放密码验证格式无效。请导出诊断。");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var at = 7;
        while (at < header.Length)
        {
            while (at < header.Length && char.IsWhiteSpace(header[at])) at++;
            var start = at;
            while (at < header.Length && (char.IsAsciiLetterOrDigit(header[at]) || header[at] is '-' or '_')) at++;
            var key = header[start..at];
            while (at < header.Length && char.IsWhiteSpace(header[at])) at++;
            if (key.Length == 0 || at >= header.Length || header[at++] != '=') throw Malformed();
            while (at < header.Length && char.IsWhiteSpace(header[at])) at++;
            string value;
            if (at < header.Length && header[at] == '"')
            {
                at++; var text = new StringBuilder(); var closed = false;
                while (at < header.Length)
                {
                    var c = header[at++];
                    if (c == '"') { closed = true; break; }
                    if (c == '\\') { if (at == header.Length) throw Malformed(); c = header[at++]; }
                    text.Append(c);
                }
                if (!closed) throw Malformed();
                value = text.ToString();
            }
            else
            {
                start = at;
                while (at < header.Length && header[at] != ',') at++;
                value = header[start..at].Trim();
                if (value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\')) throw Malformed();
            }
            if (value.Length > 1024 || fields.Count >= 32 || !fields.TryAdd(key, value)) throw Malformed();
            while (at < header.Length && char.IsWhiteSpace(header[at])) at++;
            if (at == header.Length) break;
            if (header[at++] != ',' || at == header.Length) throw Malformed();
        }
        return new(fields);
    }
    private static PlaybackAuthenticationException Malformed() => new("音箱返回的播放密码验证格式无效。请导出诊断。");
    public PlaybackCredential CreateCredential(string password)
    {
        var input = Encoding.UTF8.GetBytes(":" + _fields["realm"] + ":" + password);
        byte[]? secret = null;
        try { secret = BaseAlgorithm == "MD5" ? MD5.HashData(input) : SHA256.HashData(input); return new(Scope, BaseAlgorithm, secret); }
        finally { CryptographicOperations.ZeroMemory(input); if (secret is not null) CryptographicOperations.ZeroMemory(secret); }
    }
    public DigestAuthorization Authorize(string password) { using var credential = CreateCredential(password); return Authorize(credential); }
    public DigestAuthorization Authorize(PlaybackCredential credential)
    {
        if (credential.Scope != Scope || credential.Algorithm != BaseAlgorithm) throw new ArgumentException("Playback credential scope mismatch.");
        return new(this, credential);
    }
    public override string ToString() => $"Digest {Algorithm}; qop={(UsesQop ? "auth" : "none")}";

    public sealed class DigestAuthorization : IDisposable
    {
        private readonly DigestChallenge _challenge;
        private readonly string _cnonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        private byte[] _ha1;
        private uint _count;
        private bool _disposed;
        internal DigestAuthorization(DigestChallenge challenge, PlaybackCredential credential)
        {
            _challenge = challenge;
            _ha1 = credential.CopySecret();
            if (challenge.Algorithm.EndsWith("-SESS", StringComparison.Ordinal))
            {
                var first = _ha1;
                _ha1 = Hash(Hex(first) + ":" + challenge._fields["nonce"] + ":" + _cnonce);
                CryptographicOperations.ZeroMemory(first);
            }
        }
        private byte[] Hash(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            try { return _challenge.Algorithm.StartsWith("SHA-256", StringComparison.Ordinal) ? SHA256.HashData(bytes) : MD5.HashData(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
        private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        public string CreateHeader(string method, string uri)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (method.Length == 0 || method.Any(char.IsWhiteSpace) || uri.Any(c => c < 32 || c == 127)) throw new ArgumentException("Invalid Digest request.");
            var fields = _challenge._fields; var nc = checked(++_count).ToString("x8", CultureInfo.InvariantCulture);
            var ha2 = Hash(method + ":" + uri);
            byte[] response;
            try { response = Hash(Hex(_ha1) + ":" + fields["nonce"] + ":" + (_challenge.UsesQop ? nc + ":" + _cnonce + ":auth:" : "") + Hex(ha2)); }
            finally { CryptographicOperations.ZeroMemory(ha2); }
            string header;
            try { header = "Digest username=\"\", realm=" + Quote(fields["realm"]) + ", nonce=" + Quote(fields["nonce"]) + ", uri=" + Quote(uri) + ", response=" + Quote(Hex(response)); }
            finally { CryptographicOperations.ZeroMemory(response); }
            if (fields.ContainsKey("algorithm")) header += ", algorithm=" + _challenge.Algorithm;
            if (fields.TryGetValue("opaque", out var opaque)) header += ", opaque=" + Quote(opaque);
            if (_challenge.UsesQop) header += ", qop=auth, nc=" + nc + ", cnonce=" + Quote(_cnonce);
            else if (_challenge.Algorithm.EndsWith("-SESS", StringComparison.Ordinal)) header += ", cnonce=" + Quote(_cnonce);
            return header;
        }
        public override string ToString() => "DigestAuthorization [redacted]";
        public void Dispose() { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(_ha1); }
    }
}

public sealed class PlaybackAuthenticationException(string message) : IOException(message);
