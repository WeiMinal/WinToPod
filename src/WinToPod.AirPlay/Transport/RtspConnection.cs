// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WinToPod.AirPlay.Pairing;

namespace WinToPod.AirPlay.Transport;

public sealed record RtspResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public void RequirePairingSuccess(string stage)
    {
        Dictionary<byte, byte[]>? fields = null;
        try { fields = Tlv8.Decode(Body); } catch (InvalidDataException) { }
        fields?.ThrowIfError(Status);
        RequireSuccess(stage);
    }
    public void RequireSuccess(string stage)
    {
        if (Status == 401 && Headers.TryGetValue("WWW-Authenticate", out var challenge) && challenge.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
            throw new PasswordRequiredException();
        if (Status != 200) throw new RtspRejectedException(stage, Status);
    }
}
public sealed class RtspRejectedException(string stage, int status) : IOException($"{stage}被音箱拒绝（RTSP {status}）。连接已停止，不自动重试。")
{ public int Status { get; } = status; public string Stage { get; } = stage; }

public sealed class RtspConnection : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private Stream _stream;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private int _sequence;
    private bool _failed;
    private bool _controlAuthenticated;
    private bool _playbackEstablished;
    private Func<CancellationToken, Task<string?>>? _playbackPassword;
    private DigestChallenge.DigestAuthorization? _digest;
    private IPlaybackCredentialStore? _playbackCredentials;
    private string? _receiverId;
    private readonly Action<string>? _diagnostic;
    public string? Session { get; private set; }
    public string ClientId { get; private set; } = CreateClientId();
    public string PairingClientId { get; private set; } = Guid.NewGuid().ToString("D").ToUpperInvariant();
    private static string CreateClientId()
    {
        var identity = RandomNumberGenerator.GetBytes(8);
        identity[0] = (byte)((identity[0] | 2) & 0xfe); // Local, unicast MAC prefix used by session/PTP identity.
        return Convert.ToHexString(identity);
    }
    public IPAddress LocalAddress => ((IPEndPoint)_tcp.Client.LocalEndPoint!).Address;
    public IPAddress RemoteAddress => ((IPEndPoint)_tcp.Client.RemoteEndPoint!).Address;
    private RtspConnection(TcpClient tcp, Action<string>? diagnostic) { _tcp = tcp; _stream = tcp.GetStream(); _diagnostic = diagnostic; }

    public static async Task<RtspConnection> ConnectAsync(IPAddress remote, int port, IPAddress local, CancellationToken cancellation, Action<string>? diagnostic = null)
    {
        var tcp = new TcpClient(remote.AddressFamily) { NoDelay = true };
        try
        {
            tcp.Client.Bind(new IPEndPoint(local, 0));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync(remote, port, timeout.Token).ConfigureAwait(false); return new(tcp, diagnostic);
        }
        catch { tcp.Dispose(); throw; }
    }
    internal void UseClientIdentity(byte[] identity) => ClientId = Convert.ToHexString(identity);
    internal void UsePairingIdentity(string identity) => PairingClientId = Guid.Parse(identity).ToString("D").ToUpperInvariant();
    internal void UsePlaybackPasswordProvider(Func<CancellationToken, Task<string?>> provider) => _playbackPassword = provider;
    internal void UsePlaybackCredentials(string receiverId, IPlaybackCredentialStore? store) { _receiverId = receiverId; _playbackCredentials = store; }

    public async Task<byte[]> AuthenticateTransientAsync(CancellationToken cancellation)
    {
        using var srp = new SrpClient();
        var headers = new Dictionary<string, string>
        { ["X-Apple-HKP"] = "4", ["X-Apple-Client-Name"] = "WinToPod", ["X-Apple-Client-ID"] = PairingClientId };
        var m2 = await RequestAsync("POST", "/pair-setup", Tlv8.Encode((Tlv8.State, [1]), (Tlv8.Method, [0]), (Tlv8.Flags, [0x10])), AirPlayRequestProfile.PairingContentType, headers, cancellation).ConfigureAwait(false);
        m2.RequirePairingSuccess("会话鉴权M1"); var fields = Tlv8.Decode(m2.Body); fields.RequireState(2);
        var proof = srp.ComputeProof(fields.Require(Tlv8.Salt, 8, 64), fields.Require(Tlv8.PublicKey, 1, 384), "3939");
        var m4 = await RequestAsync("POST", "/pair-setup", Tlv8.Encode((Tlv8.State, [3]), (Tlv8.PublicKey, srp.PublicKey), (Tlv8.Proof, proof)), AirPlayRequestProfile.PairingContentType, headers, cancellation).ConfigureAwait(false);
        m4.RequirePairingSuccess("会话鉴权M3"); var verified = Tlv8.Decode(m4.Body); verified.RequireState(4);
        var secret = srp.VerifyServer(verified.Require(Tlv8.Proof, 64, 64));
        _stream = new EncryptedControlStream(_stream, secret); _controlAuthenticated = true; return secret;
    }

    public async Task<byte[]> AuthenticatePairedAsync(PairingCredentials credentials, CancellationToken cancellation)
    {
        var secret = await HapPairingClient.VerifyAsync(this, credentials, cancellation).ConfigureAwait(false);
        _stream = new EncryptedControlStream(_stream, secret); _controlAuthenticated = true; return secret;
    }

    public async Task<RtspResponse> RequestAsync(string method, string path, byte[]? body = null, string? contentType = null,
        IReadOnlyDictionary<string, string>? extra = null, CancellationToken cancellation = default)
    {
        await _serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_failed) throw new IOException("RTSP connection is closed.");
            var playback = _controlAuthenticated && (path.StartsWith("rtsp://", StringComparison.Ordinal) || path == "/feedback");
            var response = await SendAsync(method, path, body, contentType, extra, playback ? _digest : null, cancellation).ConfigureAwait(false);
            if (playback && response.Status == 401 && response.Headers.TryGetValue("WWW-Authenticate", out var challenge) &&
                challenge.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
            {
                // One challenge response in the existing encrypted session. Never
                // reconnect, re-pair, change protocols, or loop after a refusal.
                if (_digest is not null || _playbackEstablished || method != "SETUP") throw new PlaybackAuthenticationException("音箱拒绝了播放密码验证。已停止本次连接，请核对家庭AirPlay密码或导出诊断。");
                var parsed = DigestChallenge.Parse(challenge);
                _diagnostic?.Invoke($"播放会话要求额外密码验证：{parsed}；保留已完成的配对，仅回应本次挑战一次。");
                using var remembered = _receiverId is null ? null : _playbackCredentials?.Load(_receiverId, parsed.Scope);
                PlaybackCredential? created = null;
                try
                {
                    if (remembered is not null) _diagnostic?.Invoke("正在使用此音箱已保存的播放凭据；不重新配对，不询问密码。");
                    else
                    {
                        if (_playbackPassword is null) throw new PlaybackAuthenticationException("音箱已通过配对，但播放会话仍需要家庭AirPlay密码。");
                        var password = await _playbackPassword(cancellation).ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                        if (string.IsNullOrEmpty(password)) throw new OperationCanceledException("用户取消播放密码输入。", cancellation);
                        try { created = parsed.CreateCredential(password); } finally { password = null; }
                    }
                    _digest = parsed.Authorize(remembered ?? created!);
                response = await SendAsync(method, path, body, contentType, extra, _digest, cancellation).ConfigureAwait(false);
                    if (response.Status == 401)
                    {
                        if (_receiverId is not null && remembered is not null) _playbackCredentials?.Forget(_receiverId, parsed.Scope);
                        throw new PlaybackAuthenticationException(remembered is null ? "音箱拒绝了播放密码验证。已停止本次连接，请核对家庭AirPlay密码或导出诊断。" : "已保存的播放凭据被音箱拒绝。本次已停止，下次手动连接可重新输入密码；配对身份保留。");
                    }
                    if (response.Status == 200 && _receiverId is not null && created is not null)
                    {
                        try { _playbackCredentials?.Save(_receiverId, created); }
                        catch (IOException) { _diagnostic?.Invoke("播放凭据无法持久保存；本次连接继续，后续可能需要重新输入。请检查本机保存权限。"); }
                    }
                }
                finally { created?.Dispose(); }
            }
            if (playback && method == "SETUP" && response.Status == 200) _playbackEstablished = true;
            return response;
        }
        catch { _failed = true; _tcp.Dispose(); throw; }
        finally { _serial.Release(); }
    }

    private async Task<RtspResponse> SendAsync(string method, string path, byte[]? body, string? contentType,
        IReadOnlyDictionary<string, string>? extra, DigestChallenge.DigestAuthorization? digest, CancellationToken cancellation)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var sequence = ++_sequence;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["CSeq"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), ["User-Agent"] = AirPlayRequestProfile.UserAgent, ["DACP-ID"] = ClientId,
                ["Client-Instance"] = ClientId, ["Active-Remote"] = "1" };
            if (Session is not null) headers["Session"] = Session;
            if (body is not null) headers["Content-Length"] = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (contentType is not null) headers["Content-Type"] = contentType;
            if (extra is not null) foreach (var pair in extra) headers[pair.Key] = pair.Value;
            if (digest is not null) headers["Authorization"] = digest.CreateHeader(method, path);
            if (method.Any(char.IsWhiteSpace) || path.Any(c => c is '\r' or '\n') || headers.Any(h => h.Key.Any(c => c is '\r' or '\n' or ':') || h.Value.Any(c => c is '\r' or '\n')))
                throw new ArgumentException("Invalid RTSP request field.");
            var text = new StringBuilder().Append(method).Append(' ').Append(path).Append(" RTSP/1.0\r\n");
            foreach (var h in headers) text.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n"); text.Append("\r\n");
            await _stream.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), timeout.Token).ConfigureAwait(false);
            if (body is not null) await _stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
            await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            var response = await ReadResponseAsync(_stream, timeout.Token).ConfigureAwait(false);
            if (response.Headers.TryGetValue("CSeq", out var received) && received != headers["CSeq"])
                throw new InvalidDataException("Mismatched RTSP response CSeq.");
            if (response.Headers.TryGetValue("Session", out var session)) Session = session.Split(';')[0].Trim();
            _diagnostic?.Invoke(RtspDiagnostics.Describe(method, path, contentType, body?.Length ?? 0, response, headers.GetValueOrDefault("X-Apple-HKP")) + (digest is null ? "" : "; playbackAuth=Digest"));
            return response;
        }
        catch (OperationCanceledException e) when (!cancellation.IsCancellationRequested)
        { _failed = true; _tcp.Dispose(); throw new TimeoutException($"{method}请求等待10秒未响应。", e); }
    }

    public static async Task<RtspResponse> ReadResponseAsync(Stream stream, CancellationToken cancellation)
    {
        using var header = new MemoryStream(); var one = new byte[1]; var end = 0;
        while (end != 4)
        {
            if (header.Length >= 16384) throw new InvalidDataException("RTSP header exceeds limit.");
            await stream.ReadExactlyAsync(one, cancellation).ConfigureAwait(false); header.WriteByte(one[0]);
            end = (end, one[0]) switch { (0, 13) => 1, (1, 10) => 2, (2, 13) => 3, (3, 10) => 4, (_, 13) => 1, _ => 0 };
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"); var statusLine = lines[0].Split(' ', 3);
        if (statusLine.Length < 2 || statusLine[0] is not ("RTSP/1.0" or "HTTP/1.1" or "HTTP/1.0") || !int.TryParse(statusLine[1], out var status) || status is < 100 or > 599)
            throw new InvalidDataException("Invalid RTSP status line.");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(l => l.Length != 0))
        {
            var colon = line.IndexOf(':'); if (colon <= 0 || !fields.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim()))
                throw new InvalidDataException("Invalid or duplicate RTSP header.");
        }
        if (fields.ContainsKey("Transfer-Encoding")) throw new InvalidDataException("Unsupported chunked RTSP response.");
        var length = 0;
        if (fields.TryGetValue("Content-Length", out var contentLength) && (!int.TryParse(contentLength, out length) || length is < 0 or > 2 * 1024 * 1024))
            throw new InvalidDataException("Invalid RTSP body length.");
        var body = new byte[length]; await stream.ReadExactlyAsync(body, cancellation).ConfigureAwait(false);
        return new(status, fields, body);
    }

    public ValueTask DisposeAsync()
    { _failed = true; _playbackPassword = null; _digest?.Dispose(); _digest = null; _stream.Dispose(); _tcp.Dispose(); return ValueTask.CompletedTask; }
}
