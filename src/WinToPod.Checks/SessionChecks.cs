// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinToPod.AirPlay;
using WinToPod.AirPlay.Media;
using WinToPod.AirPlay.Pairing;
using WinToPod.AirPlay.Serialization;
using WinToPod.AirPlay.Transport;

internal static class SessionChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return Check("TLV拒绝：保留接收端等待时间，不误判为密码或丢失成员上下文", () =>
        {
            var fields = Tlv8.Decode(Tlv8.Encode((Tlv8.State, [4]), (Tlv8.Error, [2]), (Tlv8.RetryDelay, [4])));
            try { fields.RequireState(4); throw new Exception("Expected rejection."); }
            catch (PairingRejectedException e)
            {
                True(e.Code == 2 && e.RetryDelaySeconds == 4);
                var member = new NativeMemberAuthenticationException(2, e);
                True(member.Member == 2 && ReferenceEquals(member.GetBaseException(), e));
            }
            var large = Tlv8.Decode(Tlv8.Encode((Tlv8.State, [2]), (Tlv8.Error, [3]), (Tlv8.RetryDelay, [0, 1])));
            try { large.RequireState(2); throw new Exception("Expected rejection."); }
            catch (PairingRejectedException e) { True(e.RetryDelaySeconds == 256); }
        });
        yield return Check("鉴权分类：明确Digest密码挑战才请求密码，403访问拒绝不误判", () =>
        {
            Throws<PasswordRequiredException>(() => new RtspResponse(401, new Dictionary<string, string> { ["WWW-Authenticate"] = "Digest realm=AirPlay" }, []).RequireSuccess("鉴权"));
            Throws<RtspRejectedException>(() => new RtspResponse(403, new Dictionary<string, string>(), []).RequireSuccess("鉴权"));
            Throws<RtspRejectedException>(() => new RtspResponse(401, new Dictionary<string, string>(), []).RequireSuccess("鉴权"));
        });
        yield return Check("TLV8：384字节SRP公钥跨255片段重组，保留零长度字段", () =>
        {
            var value = Enumerable.Range(0, 384).Select(i => (byte)i).ToArray();
            var fields = Tlv8.Decode(Tlv8.Encode((Tlv8.State, [2]), (Tlv8.PublicKey, value), (12, [])));
            True(fields.Require(Tlv8.PublicKey).SequenceEqual(value) && fields[12].Length == 0); fields.RequireState(2);
            Throws<InvalidDataException>(() => Tlv8.Decode([1, 2, 3]));
            Throws<InvalidDataException>(() => Tlv8.Decode([1, 1, 3, 1, 1, 4]));
            Throws<PairingRejectedException>(() => Tlv8.Decode([6, 1, 2, 7, 1, 2]).RequireState(2));
        });
        yield return Check("SRP：SHA512/3072位固定答案匹配独立Python srptools，包含前导零salt", () =>
        {
            foreach (var fixture in new[] { "srp-sha512.json", "srp-short-a.json", "srp-short-b.json" })
            {
                using var json = JsonDocument.Parse(Fixture(fixture)); var r = json.RootElement;
                byte[] B(string key) => Convert.FromHexString(r.GetProperty(key).GetString()!);
                using var srp = new SrpClient(B("a")); True(srp.PublicKey.SequenceEqual(B("A")));
                True(srp.ComputeProof(B("salt"), B("B"), "3939").SequenceEqual(B("M1")));
                True(srp.VerifyServer(B("M2")).SequenceEqual(B("K")));
            }
        });
        yield return Check("SRP：非法服务端公钥/证明失败关闭会话", () =>
        {
            using var json = JsonDocument.Parse(Fixture("srp-sha512.json")); var r = json.RootElement;
            byte[] B(string key) => Convert.FromHexString(r.GetProperty(key).GetString()!);
            using var srp = new SrpClient(); Throws<CryptographicException>(() => srp.ComputeProof(new byte[16], [0], "3939"));
            using var wrong = new SrpClient(B("a")); wrong.ComputeProof(B("salt"), B("B"), "3939");
            Throws<CryptographicException>(() => wrong.VerifyServer(new byte[64]));
            Throws<ObjectDisposedException>(() => wrong.VerifyServer(B("M2")));
        });
        yield return Check("二进制plist：解析独立Python plistlib生成的嵌套、中文、密钥和浮点", () =>
        {
            var r = BinaryPlist.Dictionary(Fixture("python-plist.bplist")); True((string)r["name"]! == "客厅" && (bool)r["ready"]!);
            True((double)r["volume"]! == -24);
            var stream = (Dictionary<string, object?>)((object?[])r["streams"]!)[0]!;
            True((ulong)stream["dataPort"]! == 7001 && ((byte[])stream["shk"]!).SequenceEqual(Enumerable.Range(0, 32).Select(i => (byte)i)));
            True(BinaryPlist.Dictionary(BinaryPlist.Encode(r)).Count == 5);
        });
        yield return Check("二进制plist：截断、恶意对象数与循环引用被拒绝", () =>
        {
            var original = Fixture("python-plist.bplist");
            Throws<InvalidDataException>(() => BinaryPlist.Decode(original[..^1]));
            var bad = (byte[])original.Clone(); bad.AsSpan(bad.Length - 24, 8).Fill(0xff);
            Throws<InvalidDataException>(() => BinaryPlist.Decode(bad));
            var emptyArray = BinaryPlist.Encode(new object?[] { null });
            // Find the root array at its serialized object offset and point it to itself.
            var table = (int)BinaryPrimitives.ReadUInt64BigEndian(emptyArray.AsSpan(emptyArray.Length - 8));
            var rootIndex = (int)BinaryPrimitives.ReadUInt64BigEndian(emptyArray.AsSpan(emptyArray.Length - 16));
            var offset = emptyArray[table + rootIndex]; emptyArray[offset + 1] = (byte)rootIndex;
            Throws<InvalidDataException>(() => BinaryPlist.Decode(emptyArray));
        });
        yield return Check("ChaCha：托管回退与系统原语输出一致，篡改拒绝", () =>
        {
            var key = new byte[32]; var nonce = new byte[12]; var plain = Encoding.UTF8.GetBytes("WinToPod测试"); var aad = new byte[] { 1, 2 };
            var a = new byte[plain.Length]; var b = new byte[plain.Length]; var ta = new byte[16]; var tb = new byte[16];
            using var native = new AeadCipher(key); using var managed = new AeadCipher(key, true);
            native.Encrypt(nonce, plain, a, ta, aad); managed.Encrypt(nonce, plain, b, tb, aad);
            True(a.SequenceEqual(b) && ta.SequenceEqual(tb)); var output = new byte[plain.Length]; managed.Decrypt(nonce, b, tb, output, aad); True(output.SequenceEqual(plain));
            tb[0] ^= 1; Throws<CryptographicException>(() => managed.Decrypt(nonce, b, tb, output, aad)); True(output.All(x => x == 0));
        });
        yield return Check("ALAC：明确352帧数量、逐位样本和END标签", () =>
        {
            var pcm = new byte[1408]; BinaryPrimitives.WriteInt16LittleEndian(pcm, -1234); BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2), 2345);
            var frame = AlacVerbatim.Encode(pcm); var bit = 0;
            uint Read(int width) { uint v = 0; for (var i = 0; i < width; i++, bit++) v = (v << 1) | (uint)((frame[bit / 8] >> (7 - bit % 8)) & 1); return v; }
            True(Read(3) == 1 && Read(4) == 0 && Read(12) == 0 && Read(1) == 1 && Read(2) == 0 && Read(1) == 1 && Read(32) == 352);
            True(unchecked((short)Read(16)) == -1234 && Read(16) == 2345);
            for (var i = 2; i < 704; i++) True(Read(16) == 0); True(Read(3) == 7);
            Throws<ArgumentException>(() => AlacVerbatim.Encode(new byte[3]));
            var output = Environment.GetEnvironmentVariable("WINTOPOD_CHECK_OUTPUT");
            if (!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); File.WriteAllBytes(Path.Combine(output, "frame.alac"), frame); File.WriteAllBytes(Path.Combine(output, "frame.pcm"), pcm); }
        });
        yield return Check("RTP：加密布局tag在nonce前，AAD绑定时间戳/SSRC，16位序号回绕不复用nonce", () =>
        {
            var key = new byte[32]; var frame = AlacVerbatim.Encode(new byte[1408]); using var sender = new AudioPacketCipher(key, 65535);
            var a = sender.Encrypt(frame, 65535, 10, 20, true); var b = sender.Encrypt(frame, 0, 362, 20, false);
            True(a[1] == 0xe0 && b[1] == 0x60 && BinaryPrimitives.ReadUInt64LittleEndian(a.AsSpan(a.Length - 8)) == 65535 && BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(b.Length - 8)) == 65536);
            using var cipher = new AeadCipher(key); var nonce = new byte[12]; b.AsSpan(b.Length - 8).CopyTo(nonce.AsSpan(4)); var plain = new byte[frame.Length];
            cipher.Decrypt(nonce, b.AsSpan(12, frame.Length), b.AsSpan(12 + frame.Length, 16), plain, b.AsSpan(4, 8)); True(plain.SequenceEqual(frame));
            b[4] ^= 1; Throws<CryptographicException>(() => cipher.Decrypt(nonce, b.AsSpan(12, frame.Length), b.AsSpan(12 + frame.Length, 16), plain, b.AsSpan(4, 8)));
            using var exhausted = new AudioPacketCipher(key, ulong.MaxValue); exhausted.Encrypt(frame, 0, 0, 0, true); Throws<InvalidOperationException>(() => exhausted.Encrypt(frame, 1, 1, 0, false));
        });
        yield return Check("NTP：1900历元、请求时间回显和同步样本锚点", () =>
        {
            True(NetworkClock.ToNtp(new DateTimeOffset(1900, 1, 1, 0, 0, 1, TimeSpan.Zero)) == 1UL << 32);
            var query = new byte[32]; query[0] = 0x80; query[1] = 0xd2; query[3] = 7; query[31] = 42;
            var reply = NetworkClock.TimingReply(query, 123, 456); True(reply[1] == 0xd3 && reply[15] == 42);
            True(BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(16)) == 123 && BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(24)) == 456);
            var sync = NetworkClock.Sync(100000, 88200, 789, true); True(sync[0] == 0x90 && BinaryPrimitives.ReadUInt32BigEndian(sync.AsSpan(4)) == 11800);
        });
        yield return ("RTSP：分段读取、大小限制、重复Content-Length与错帧拒绝", async () =>
        {
            var valid = Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nCSeq: 2\r\nContent-Length: 4\r\n\r\ntest");
            var parsed = await RtspConnection.ReadResponseAsync(new FragmentedStream(valid), default);
            True(parsed.Status == 200 && Encoding.ASCII.GetString(parsed.Body) == "test");
            foreach (var text in new[] { "RTSP/1.0 200 OK\r\nContent-Length: -1\r\n\r\n", "RTSP/1.0 200 OK\r\nContent-Length: 0\r\nContent-Length: 0\r\n\r\n", "RTSP/1.0 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" })
                try { await RtspConnection.ReadResponseAsync(new MemoryStream(Encoding.ASCII.GetBytes(text)), default); throw new Exception("Expected rejection."); } catch (InvalidDataException) { }
        });
        yield return Check("拒绝诊断：保留403与挑战种类，不输出任意头、nonce或正文", () =>
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "private-device-name", ["Server"] = "192.168.99.1 secret-name",
                ["WWW-Authenticate"] = "Digest realm=private-home, nonce=never-log-this", ["X-Private"] = "secret-value"
            };
            var text = RtspDiagnostics.Describe("POST", "/pair-setup", "application/octet-stream", 9,
                new(403, headers, Encoding.UTF8.GetBytes("private body 192.168.99.1")));
            True(text.Contains("→ 403") && text.Contains("challenge=Digest") && text.Contains("tlv=unrecognised") && text.Contains("responseType=other"));
            foreach (var secret in new[] { "private", "192.168", "never-log", "secret" }) True(!text.Contains(secret));
        });
        yield return Check("拒绝诊断：只读取TLV状态错误等待时间，不输出密钥或证明", () =>
        {
            var body = Tlv8.Encode((Tlv8.State, [2]), (Tlv8.Error, [6]), (Tlv8.RetryDelay, [60]),
                (Tlv8.Flags, [0x10]), (Tlv8.Salt, Encoding.ASCII.GetBytes("secret-salt")), (Tlv8.PublicKey, Encoding.ASCII.GetBytes("secret-key")), (Tlv8.Proof, Encoding.ASCII.GetBytes("secret-proof")));
            var text = RtspDiagnostics.Describe("POST", "/pair-setup", "application/octet-stream", 9,
                new(403, new Dictionary<string, string> { ["Server"] = "AirTunes/980.77.2" }, body));
            True(text.Contains("server=AirTunes/980.77.2") && text.Contains("tlvState=2") && text.Contains("tlvError=6") && text.Contains("retryDelay=60"));
            True(text.Contains("saltBytes=11") && text.Contains("pairingFlags=0x10"));
            True(!text.Contains("secret") && !text.Contains(Convert.ToHexString(body)));
        });
        yield return Check("info诊断：仅记录数值能力，不记录设备名称地址和标识", () =>
        {
            var body = BinaryPlist.Encode(new Dictionary<string, object?> { ["statusFlags"] = 0xbac04,
                ["features"] = 123UL, ["name"] = "private-home", ["deviceID"] = "private-id", ["ip"] = "192.168.99.1" });
            var text = RtspDiagnostics.Describe("GET", "/info", null, 0, new(200, new Dictionary<string, string>(), body));
            True(text.Contains("statusFlags=0xBAC04") && text.Contains("features=0x7B") && !text.Contains("private") && !text.Contains("192.168"));
            var url = RtspDiagnostics.Describe("SETUP", "rtsp://192.168.99.1/private", null, 0, new(200, new Dictionary<string, string>(), []));
            True(url.StartsWith("RTSP SETUP session") && !url.Contains("192.168") && !url.Contains("private"));
        });
        yield return Check("验证启动回复：200内嵌拒绝与403均保留设备冷却，不能继续SRP", () =>
        {
            foreach (var status in new[] { 200, 403 })
            {
                var response = new RtspResponse(status, new Dictionary<string, string>(), Tlv8.Encode((Tlv8.State, [2]), (Tlv8.Error, [2]), (Tlv8.RetryDelay, [0, 1])));
                try { response.RequirePairingSuccess("test"); throw new Exception("Backoff ignored."); }
                catch (PairingRejectedException e) { True(e.RetryDelaySeconds == 256 && e.ResponseStatus == status); }
            }
        });
        yield return Check("403分类：拒绝本身不代表密码错误或访问策略", () =>
        {
            try { new RtspResponse(403, new Dictionary<string, string>(), []).RequireSuccess("会话鉴权M1"); throw new Exception("Expected rejection."); }
            catch (RtspRejectedException e) { True(e.Status == 403 && e.Stage == "会话鉴权M1"); }
        });
    }
    private static byte[] Fixture(string name)
    { using var stream = typeof(SessionChecks).Assembly.GetManifestResourceStream("WinToPod.Checks.Fixtures." + name) ?? throw new Exception("Missing fixture " + name); using var result = new MemoryStream(); stream.CopyTo(result); return result.ToArray(); }
    private static (string, Func<Task>) Check(string name, Action run) => (name, () => { run(); return Task.CompletedTask; });
    private static void True(bool value) { if (!value) throw new Exception("Session check failed."); }
    private static void Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken); }
}
