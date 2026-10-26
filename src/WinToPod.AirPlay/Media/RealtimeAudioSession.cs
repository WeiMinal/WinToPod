// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WinToPod.AirPlay.Serialization;
using WinToPod.AirPlay.Transport;
using WinToPod.AirPlay.Discovery;
using WinToPod.AirPlay.Pairing;

namespace WinToPod.AirPlay.Media;

public sealed record AudioSessionOptions(IPAddress RemoteAddress, int Port, IPAddress LocalAddress,
    string? Password, int Volume, IPAddress[]? TimingPeers = null, IReadOnlyDictionary<string, string?>? AdvertisedProperties = null,
    PtpTestPorts? LoopbackTestPorts = null, string? SenderIdentity = null, string? ReceiverId = null,
    ReceiverVolumeCurve VolumeCurve = ReceiverVolumeCurve.Native);

// Only the independent loopback harness may relocate privileged UDP ports.
// Desktop calls never set this; actual AirPlay always uses 319 and 320.
public sealed record PtpTestPorts(int Event, int General);

// Experimental native AP2 realtime ALAC with PTP or NTP. No legacy AirPlay-1 fallback,
// no volume command before explicit acquisition, no automatic retry/takeover.
public sealed class RealtimeAudioSession : IAsyncDisposable
{
    private RtspConnection _rtsp;
    private readonly AudioSessionOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NetworkClock _clock = new();
    private readonly UdpClient _timing, _control, _data;
    private readonly object _packetsLock = new();
    private readonly Dictionary<ushort, byte[]> _recent = [];
    private readonly Queue<ushort> _recentOrder = [];
    private readonly List<Task> _background = [];
    private TcpClient? _event;
    private EncryptedControlStream? _eventStream;
    private PtpClockEngine? _ptp;
    private PlaybackCohort? _cohort;
    private readonly Action<string>? _diagnostic;
    private AudioPacketCipher? _cipher;
    private byte[]? _authenticationSecret;
    private string? _sessionPassword;
    private string _url = "";
    private IPEndPoint? _dataEndpoint, _controlEndpoint;
    private uint _timestamp, _ssrc;
    private ushort _sequence;
    private uint _latency = 88200;
    private bool _configured, _disposed, _audioStarted, _volumeConfirmed;
    private volatile bool _pcmMuted;
    private double? _muteReadbackDb;
    private Exception? _backgroundFailure;
    private long _packetCount, _resendCount;
    public long PacketsSent => Interlocked.Read(ref _packetCount);
    public long Retransmits => Interlocked.Read(ref _resendCount);
    public int? LastConfirmedVolume { get; private set; }
    public Dictionary<string, object?>? DeviceInfo { get; private set; }

    private RealtimeAudioSession(RtspConnection rtsp, AudioSessionOptions options, Action<string>? diagnostic, PlaybackCohort? cohort)
    {
        _rtsp = rtsp; _options = options; _diagnostic = diagnostic; _cohort = cohort;
        _timing = Bound(options.LocalAddress); _control = Bound(options.LocalAddress); _data = Bound(options.LocalAddress);
    }

    public static async Task<RealtimeAudioSession> OpenAsync(AudioSessionOptions options, Action<string>? progress, CancellationToken cancellation, Action<string>? diagnostic = null,
        Func<ReceiverPasswordRequest, CancellationToken, Task<string?>>? requestPassword = null, IReceiverPairingStore? pairingStore = null, IPlaybackCredentialStore? playbackCredentials = null)
    {
        using var memory = pairingStore is null ? new MemoryReceiverPairingStore() : null;
        return await OpenMemberAsync(options, progress, cancellation, diagnostic, null, requestPassword: requestPassword, pairingStore: pairingStore ?? memory!, playbackCredentials: playbackCredentials).ConfigureAwait(false);
    }

    internal static async Task<RealtimeAudioSession> OpenMemberAsync(AudioSessionOptions options, Action<string>? progress, CancellationToken cancellation, Action<string>? diagnostic, PlaybackCohort? cohort, bool authenticateOnly = false,
        Func<ReceiverPasswordRequest, CancellationToken, Task<string?>>? requestPassword = null, IReceiverPairingStore? pairingStore = null, IPlaybackCredentialStore? playbackCredentials = null)
    {
        if (options.Volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.SenderIdentity is { } sender && !Guid.TryParse(sender, out _)) throw new ArgumentException("Invalid sender identity.", nameof(options));
        diagnostic?.Invoke($"AirPlay请求配置：{AirPlayRequestProfile.Revision}; User-Agent={AirPlayRequestProfile.UserAgent}; pairingType={AirPlayRequestProfile.PairingContentType}");
        var rtsp = await RtspConnection.ConnectAsync(options.RemoteAddress, options.Port, options.LocalAddress, cancellation, diagnostic).ConfigureAwait(false);
        if (cohort is not null) rtsp.UseClientIdentity(cohort.Identity);
        rtsp.UsePairingIdentity(options.SenderIdentity ?? cohort?.SenderIdentity ?? rtsp.PairingClientId);
        RealtimeAudioSession? session = null;
        try
        {
            session = new(rtsp, options, diagnostic, cohort);
            await session.AuthenticateAsync(progress, cancellation, requestPassword, pairingStore ?? throw new ArgumentNullException(nameof(pairingStore)), playbackCredentials).ConfigureAwait(false);
            if (!authenticateOnly) await session.ConfigurePreparedAsync(progress, cancellation).ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (session is not null) await session.CloseAsync(false).ConfigureAwait(false); else await rtsp.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task AuthenticateAsync(Action<string>? progress, CancellationToken cancellation,
        Func<ReceiverPasswordRequest, CancellationToken, Task<string?>>? requestPassword, IReceiverPairingStore pairingStore, IPlaybackCredentialStore? playbackCredentials)
    {
        progress?.Invoke("正在读取设备信息");
        var info = await _rtsp.RequestAsync("GET", "/info", cancellation: cancellation).ConfigureAwait(false);
        // Some receivers only reveal info after authentication; do not turn this into an occupancy probe.
        if (info.Status == 200 && info.Body.Length != 0) DeviceInfo = BinaryPlist.Dictionary(info.Body);
        var credentials = ReceiverAccess.Credentials(_options.AdvertisedProperties ?? new Dictionary<string, string?>(), DeviceInfo);
        if (credentials == CredentialRequirement.PinPairing)
            throw new IOException("设备要求屏幕验证码配对，当前版本尚未支持这种方式。此处无需填写Apple ID密码；HomePod请检查家庭App中的扬声器访问设置。");
        var receiverId = _options.ReceiverId ?? _options.RemoteAddress.ToString();
        _sessionPassword = _options.Password;
        void SetPlaybackPasswordProvider()
        {
            _rtsp.UsePlaybackCredentials(receiverId, playbackCredentials);
            _rtsp.UsePlaybackPasswordProvider(async token =>
        {
            if (!string.IsNullOrEmpty(_sessionPassword)) return _sessionPassword;
            if (requestPassword is null) return null;
            progress?.Invoke("音箱已配对，播放仍需AirPlay密码……");
            _sessionPassword = await requestPassword(new(_options.RemoteAddress, ForPlayback: true), token).ConfigureAwait(false);
            return _sessionPassword;
        });
        }
        SetPlaybackPasswordProvider();
        using var existing = pairingStore.Load(receiverId, _rtsp.PairingClientId);
        if (existing is not null)
        {
            progress?.Invoke("正在验证已保存的配对身份……");
            _diagnostic?.Invoke("鉴权模式：已保存配对的HKP3身份验证；不发送SRP口令证明，不在失败后自动重新配对。");
            _authenticationSecret = await _rtsp.AuthenticatePairedAsync(existing, cancellation).ConfigureAwait(false);
            _diagnostic?.Invoke("成员鉴权已完成；尚未SETUP、启动PTP或发送音量/音频。"); return;
        }
        var password = _options.Password;
        // Each receiver decides independently. In a real native pair the leader
        // accepted transient's fixed secret while its sibling required the Home
        // password. Never guess the sibling's secret or broadcast its answer.
        if (credentials == CredentialRequirement.Password && string.IsNullOrEmpty(password))
        {
            if (requestPassword is null) throw new PasswordRequiredException();
            progress?.Invoke("音箱需要AirPlay密码，等待输入……");
            _diagnostic?.Invoke("此成员声明需要AirPlay密码；先请求用户输入，尚未启动配对或发送SRP证明。");
            password = await requestPassword(new(_options.RemoteAddress), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(password)) throw new OperationCanceledException("用户取消输入AirPlay密码。", cancellation);
        }
        progress?.Invoke("正在进行会话鉴权");
        _sessionPassword = password;
        byte[] secret;
        try
        {
            if (!string.IsNullOrEmpty(password))
            {
                progress?.Invoke("正在完成首次配对……");
                _diagnostic?.Invoke("鉴权模式：HKP3完整配对M1—M6，随后pair-verify；不使用HKP4密码瞬态路径。");
                var seed = pairingStore.GetControllerSecret(_rtsp.PairingClientId);
                try
                {
                    using var paired = await HapPairingClient.SetupAsync(_rtsp, password, seed, cancellation).ConfigureAwait(false);
                    pairingStore.Save(receiverId, paired);
                    _diagnostic?.Invoke("完整配对已验证并交付凭据存储；关闭配对连接，另建连接验证身份。此为配对后续步骤，不是失败重试。");
                    var identity = Convert.FromHexString(_rtsp.ClientId); var pairingId = _rtsp.PairingClientId;
                    await _rtsp.DisposeAsync().ConfigureAwait(false);
                    _rtsp = await RtspConnection.ConnectAsync(_options.RemoteAddress, _options.Port, _options.LocalAddress, cancellation, _diagnostic).ConfigureAwait(false);
                    _rtsp.UseClientIdentity(identity); _rtsp.UsePairingIdentity(pairingId);
                    SetPlaybackPasswordProvider();
                    secret = await _rtsp.AuthenticatePairedAsync(paired, cancellation).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(seed); }
            }
            else secret = await _rtsp.AuthenticateTransientAsync(cancellation).ConfigureAwait(false);
        }
        catch (PasswordRequiredException e) when (!e.Rejected && !string.IsNullOrEmpty(password))
        { throw new IOException("输入密码后，设备仍要求另一种鉴权方式，当前版本尚未兼容。已停止连接，不会反复要求输入同一密码。", e); }
        finally { password = null; }
        _authenticationSecret = secret;
        _diagnostic?.Invoke("成员鉴权已完成；尚未SETUP、启动PTP或发送音量/音频。");
    }

    internal async Task ConfigurePreparedAsync(Action<string>? progress, CancellationToken cancellation)
    {
        var secret = _authenticationSecret ?? throw new InvalidOperationException("Member has not authenticated or is already configured.");
        _authenticationSecret = null;
        try
        {
            var fields = _options.AdvertisedProperties ?? new Dictionary<string, string?>();
            var usePtp = ReceiverTiming.UsesPtp(fields, DeviceInfo);
            var follow = usePtp && ReceiverTiming.FollowsReceiver(fields, DeviceInfo);
            var identity = _cohort?.Identity ?? Convert.FromHexString(_rtsp.ClientId);
            var clockId = BinaryPrimitives.ReadUInt64BigEndian(identity);
            _diagnostic?.Invoke($"时钟选择：{(usePtp ? "PTP" : "NTP")}；来源=设备能力；followReceiver={follow}；不自动切换协议重试。");
            if (usePtp)
            {
                progress?.Invoke("正在准备音箱同步时钟");
                _ptp = _cohort is null ? new(_options.LocalAddress, _options.RemoteAddress, _options.TimingPeers ?? [], clockId, follow, _diagnostic, Fail, _options.LoopbackTestPorts) : _cohort.GetClock(_diagnostic);
            }
            else if (_cohort is not null) throw new IOException("组合成员没有提供PTP能力，已停止整组连接。");
            else _background.Add(TimingLoopAsync());
            _background.Add(ControlLoopAsync());
            _timestamp = _cohort?.InitialTimestamp ?? RandomUInt32(); var streamId = RandomUInt32(); _ssrc = usePtp ? 0 : streamId;
            _sequence = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
            _cipher = new AudioPacketCipher(secret[..32], _sequence);
            var sessionId = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)).ToString(CultureInfo.InvariantCulture);
            _url = $"rtsp://{_options.RemoteAddress}/{sessionId}";
            var mac = string.Join(':', identity.Take(6).Select(x => x.ToString("X2")));
            var groupId = _cohort?.GroupId ?? Guid.NewGuid().ToString().ToUpperInvariant();
            var setup = new Dictionary<string, object?>
            {
                ["deviceID"] = mac, ["macAddress"] = mac, ["name"] = "WinToPod", ["model"] = "WinToPod",
                ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(), ["timingProtocol"] = usePtp ? "PTP" : "NTP",
                ["isMultiSelectAirPlay"] = _cohort is not null, ["osName"] = "Windows", ["sourceVersion"] = "377.25.01"
            };
            if (usePtp)
            {
                var peer = new Dictionary<string, object?>
                {
                    ["ID"] = groupId, ["DeviceType"] = 0, ["ClockID"] = clockId,
                    ["Addresses"] = new object?[] { _options.LocalAddress.ToString() }, ["SupportsClockPortMatchingOverride"] = false
                };
                setup["groupUUID"] = groupId; setup["groupContainsGroupLeader"] = false;
                setup["timingPeerInfo"] = peer; setup["timingPeerList"] = new object?[] { peer };
            }
            else setup["timingPort"] = Port(_timing);
            progress?.Invoke("正在协商音频会话（SETUP，会话阶段）");
            var session = await PlistAsync("SETUP", setup, cancellation).ConfigureAwait(false);
            _configured = true;
            if (TryPort(session, "eventPort") is { } eventPort)
            {
                progress?.Invoke("正在连接音箱事件通道");
                _event = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                _event.Client.Bind(new IPEndPoint(_options.LocalAddress, 0));
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(8));
                await _event.ConnectAsync(_options.RemoteAddress, eventPort, deadline.Token).ConfigureAwait(false);
                _eventStream = new EncryptedControlStream(_event.GetStream(), secret, eventChannel: true);
                _background.Add(EventLoopAsync());
            }
            var recordHeaders = new Dictionary<string, string>
            { ["Range"] = "npt=0-", ["RTP-Info"] = $"seq={_sequence};rtptime={_timestamp}" };
            progress?.Invoke("正在确认播放会话（RECORD）");
            var record = await _rtsp.RequestAsync("RECORD", _url, extra: recordHeaders, cancellation: cancellation).ConfigureAwait(false);
            record.RequireSuccess("RECORD");
            var stream = new Dictionary<string, object?>
            {
                ["type"] = 96, ["ct"] = 2, ["audioFormat"] = 0x40000, ["sr"] = 44100, ["spf"] = 352,
                ["audioMode"] = "default", ["shk"] = secret[..32], ["isMedia"] = true,
                ["controlPort"] = Port(_control), ["dataPort"] = Port(_data),
                ["streamConnectionID"] = (ulong)streamId, ["latencyMin"] = 11025, ["latencyMax"] = 88200,
                ["supportsDynamicStreamID"] = false
            };
            progress?.Invoke("正在协商音频流（SETUP，流阶段；尚未发声）");
            var negotiated = await PlistAsync("SETUP", new Dictionary<string, object?> { ["streams"] = new object?[] { stream } }, cancellation).ConfigureAwait(false);
            if (!negotiated.TryGetValue("streams", out var values) || values is not object?[] streams || streams.Length != 1 || streams[0] is not Dictionary<string, object?> received)
                throw new InvalidDataException("音箱没有返回有效的音频流端口。");
            _dataEndpoint = new(_options.RemoteAddress, TryPort(received, "dataPort") ?? throw new InvalidDataException("缺少音频dataPort。"));
            _controlEndpoint = new(_options.RemoteAddress, TryPort(received, "controlPort") ?? throw new InvalidDataException("缺少controlPort。"));
            if (received.TryGetValue("latencyMin", out var minimum) && minimum is ulong min && min <= 441000)
                _latency = Math.Max(_latency, (uint)min);
            if (received.TryGetValue("latencyMax", out var maximum) && maximum is ulong max && max >= 11025 && max <= 441000)
                _latency = Math.Min(_latency, (uint)max);
            if (_ptp is not null)
            {
                progress?.Invoke("正在确认音箱同步时钟");
                var peers = (_options.TimingPeers ?? []).Append(_options.RemoteAddress).Append(_options.LocalAddress).Distinct().Select(x => (object?)x.ToString()).ToArray();
                var response = await _rtsp.RequestAsync("SETPEERS", _url, BinaryPlist.Encode(peers), "application/x-apple-binary-plist", cancellation: cancellation).ConfigureAwait(false);
                response.RequireSuccess("设置时钟节点");
                _background.Add(FeedbackLoopAsync());
                if (_cohort is null) { await _ptp.WaitUntilReadyAsync(cancellation).ConfigureAwait(false); _ptp.Read(); }
            }
            if (_cohort is not null) return; // all members must finish SETPEERS before volume/audio
            progress?.Invoke("正在恢复并确认音箱音量");
            if (!await SetVolumeAndConfirmAsync(_options.Volume, cancellation).ConfigureAwait(false))
                throw new IOException("音箱音量未得到回读确认；保持不发声并停止连接。不会把未确认值记入历史。");
            if (_ptp is null) _background.Add(FeedbackLoopAsync());
            progress?.Invoke("会话已就绪；数据发送不代表已经可听");
        }
        finally { _sessionPassword = null; CryptographicOperations.ZeroMemory(secret); }
    }

    internal void UseSingleFallback()
    {
        if (_authenticationSecret is null || _configured || _ptp is not null || _audioStarted)
            throw new InvalidOperationException("Only an authenticated, unconfigured member can fall back.");
        _cohort = null;
    }

    public async Task<bool> SetVolumeAndConfirmAsync(int percent, CancellationToken cancellation)
    {
        if (_disposed || _lifetime.IsCancellationRequested) return false;
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        try
        {
        EnsureOwned(); _ptp?.Read();
        if (percent == 0) _pcmMuted = true;
        var db = ReceiverVolume.Decibels(percent, _options.VolumeCurve);
        var set = await _rtsp.RequestAsync("SET_PARAMETER", _url, Encoding.ASCII.GetBytes("volume: " + db.ToString("F6", CultureInfo.InvariantCulture) + "\r\n"), "text/parameters", cancellation: cancellation).ConfigureAwait(false);
        set.RequireSuccess("设置音量");
        var result = await VolumeConfirmation.ReadAsync(db, percent == 0, ReadVolumeDbAsync, cancellation,
            (attempt, actual, confirmed) => _diagnostic?.Invoke($"音量确认：requested={db:F3}dB; readback={actual:F3}dB; mute={percent == 0}; accepted={confirmed}; attempt={attempt}/4。")).ConfigureAwait(false);
        EnsureOwned(); _ptp?.Read();
        if (result.Confirmed)
        {
            _volumeConfirmed = true;
            LastConfirmedVolume = percent; _pcmMuted = percent == 0; _muteReadbackDb = percent == 0 ? result.ActualDecibels : null;
        }
        else _diagnostic?.Invoke("音量暂未确认：有限次读回仍不匹配；保留上次确认状态，不判定音箱失联，不重复设置音量。");
        return result.Confirmed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception e) { _volumeConfirmed = false; Fail(e); throw; }
    }

    private async Task<double> ReadVolumeDbAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); EnsureOwned(); _ptp?.Read();
        var get = await _rtsp.RequestAsync("GET_PARAMETER", _url, "volume\r\n"u8.ToArray(), "text/parameters", cancellation: cancellation).ConfigureAwait(false);
        get.RequireSuccess("读取音量");
        var line = Encoding.ASCII.GetString(get.Body).Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("volume:", StringComparison.OrdinalIgnoreCase));
        if (line is null || !double.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var db) ||
            !double.IsFinite(db) || db is < -144.25 or > .25) throw new InvalidDataException("音箱返回了无效音量。");
        EnsureOwned(); return db;
    }

    public static double VolumeDb(int percent)
        => ReceiverVolume.Decibels(percent);

    public async Task RunAsync(Func<Memory<byte>, CancellationToken, ValueTask> readPcm, TimeSpan? duration, CancellationToken cancellation)
        => await RunMembersAsync([this], readPcm, duration, cancellation).ConfigureAwait(false);

    internal void ConfirmGroupVolume(int volume) { _volumeConfirmed = true; LastConfirmedVolume = volume; }
    internal uint Latency => _latency;
    internal void EnsureOwned()
    {
        _cohort?.ThrowIfFailed();
        if (_disposed || _lifetime.IsCancellationRequested) throw new IOException("音箱会话已经结束，停止音量和音频控制。", _backgroundFailure);
    }
    public async Task<int> ReadVolumeAsync(CancellationToken cancellation)
    {
        EnsureOwned();
        try
        {
            var db = await ReadVolumeDbAsync(cancellation).ConfigureAwait(false);
            if (_pcmMuted && (db <= -29.75 || _muteReadbackDb is { } prior && Math.Abs(db - prior) <= .25)) return 0;
            _pcmMuted = db <= -30; _muteReadbackDb = null;
            if (!_pcmMuted && LastConfirmedVolume is int confirmed && confirmed > 0 &&
                Math.Abs(db - ReceiverVolume.Decibels(confirmed, _options.VolumeCurve)) <= .25) return confirmed;
            return ReceiverVolume.Percent(db, _options.VolumeCurve);
        }
        catch (Exception e) { Fail(e, "volume"); throw _backgroundFailure ?? e; }
    }

    internal async Task RunMembersAsync(IReadOnlyList<RealtimeAudioSession> members, Func<Memory<byte>, CancellationToken, ValueTask> readPcm, TimeSpan? duration, CancellationToken cancellation)
    {
        foreach (var member in members)
        {
            member.EnsureOwned();
            if (!member._configured || !member._volumeConfirmed || member._audioStarted) throw new InvalidOperationException("Audio session is not ready.");
            member._audioStarted = true;
        }
        _latency = members.Max(m => m.Latency);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellation, _cohort?.Cancellation ?? CancellationToken.None);
        var pcm = new byte[352 * 4]; var start = Stopwatch.GetTimestamp(); long frames = 0; var first = true; var nextSync = TimeSpan.Zero;
        var initialTimestamp = _timestamp;
        var anchor = _ptp?.Read();
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var elapsed = Stopwatch.GetElapsedTime(start);
                if (duration is { } limit && elapsed >= limit + TimeSpan.FromSeconds(_latency / 44100d + 0.3)) return;
                pcm.AsSpan().Clear();
                if (duration is null || elapsed < duration) await readPcm(pcm, linked.Token).ConfigureAwait(false);
                if (_pcmMuted) pcm.AsSpan().Clear();
                foreach (var member in members) { member.EnsureOwned(); if (!member._volumeConfirmed) throw new IOException("Volume confirmation lost."); }
                var ptpTime = _ptp?.Read();
                if (elapsed >= nextSync)
                {
                    byte[] sync;
                    if (ptpTime is { } ptp && anchor is { } origin)
                    {
                        if (ptp.ClockId != origin.ClockId) throw new IOException("音箱时钟来源变化，已停止发送。");
                        // Frozen mapping: never derive render position from the
                        // moving send head or re-anchor a live stream on a tick.
                        var elapsedSamples = (long)(((Int128)ptp.TimeNanoseconds - origin.TimeNanoseconds) * 44100 / 1_000_000_000);
                        var position = unchecked(initialTimestamp - _latency + (uint)elapsedSamples);
                        sync = NetworkClock.PtpSync(position, ptp.TimeNanoseconds, ptp.ClockId, first);
                    }
                    else sync = NetworkClock.Sync(_timestamp, _latency, _clock.NtpNow, first);
                    foreach (var member in members) await member._control.SendAsync(sync, member._controlEndpoint!, linked.Token).ConfigureAwait(false);
                    nextSync = elapsed + TimeSpan.FromSeconds(1);
                }
                var encoded = AlacVerbatim.Encode(pcm);
                foreach (var member in members)
                {
                    member.EnsureOwned();
                    var packet = member._cipher!.Encrypt(encoded, member._sequence, _timestamp, member._ssrc, first);
                    lock (member._packetsLock)
                    { member._recent[member._sequence] = packet; member._recentOrder.Enqueue(member._sequence); while (member._recentOrder.Count > 512) member._recent.Remove(member._recentOrder.Dequeue()); }
                    await member._data.SendAsync(packet, member._dataEndpoint!, linked.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref member._packetCount); member._sequence++;
                }
                _timestamp = unchecked(_timestamp + 352); frames += 352; first = false;
                var deadline = TimeSpan.FromSeconds(frames / 44100d); var remaining = deadline - Stopwatch.GetElapsedTime(start);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, linked.Token).ConfigureAwait(false);
                else if (remaining < TimeSpan.FromSeconds(-0.5)) throw new IOException("发送线程落后超过500ms，已停止，避免持续堆积延迟。");
            }
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (_backgroundFailure is not null || _cohort?.Failure is not null)
        { throw _backgroundFailure ?? _cohort?.Failure ?? new IOException("音箱会话已中断。"); }
        catch (Exception e) when (e is not OperationCanceledException) { Fail(e); throw; }
        finally { CryptographicOperations.ZeroMemory(pcm); }
    }

    private async Task<Dictionary<string, object?>> PlistAsync(string method, Dictionary<string, object?> body, CancellationToken cancellation)
    {
        var stage = body.ContainsKey("streams") ? "音频流协商" : "音频会话协商";
        try
        {
            var response = await _rtsp.RequestAsync(method, _url, BinaryPlist.Encode(body), "application/x-apple-binary-plist", cancellation: cancellation).ConfigureAwait(false);
            response.RequireSuccess(stage); return BinaryPlist.Dictionary(response.Body);
        }
        catch (TimeoutException e) { throw new TimeoutException(stage + $"超时，音箱没有响应。当前时钟路径={(_ptp is null ? "NTP" : "PTP")}；已停止连接，请导出诊断。", e); }
    }
    private async Task TimingLoopAsync()
    {
        try
        {
            var peers = (_options.TimingPeers ?? []).Append(_options.RemoteAddress).ToHashSet();
            while (!_lifetime.IsCancellationRequested)
            {
                var packet = await _timing.ReceiveAsync(_lifetime.Token).ConfigureAwait(false); var received = _clock.NtpNow;
                if (!peers.Contains(packet.RemoteEndPoint.Address) || packet.Buffer.Length != 32 || (packet.Buffer[1] & 0x7f) != 82) continue;
                await _timing.SendAsync(NetworkClock.TimingReply(packet.Buffer, received, _clock.NtpNow), packet.RemoteEndPoint, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { Fail(e, "timing"); }
    }
    private async Task ControlLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var message = await _control.ReceiveAsync(_lifetime.Token).ConfigureAwait(false); var packet = message.Buffer;
                if (!message.RemoteEndPoint.Address.Equals(_options.RemoteAddress) || packet.Length < 8 || (packet[1] & 0x7f) != 85) continue;
                var at = packet.Length >= 12 ? 8 : 4;
                var first = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at)); var count = Math.Min(128, (int)BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at + 2)));
                for (var i = 0; i < count; i++)
                {
                    byte[]? original; lock (_packetsLock) _recent.TryGetValue(unchecked((ushort)(first + i)), out original);
                    if (original is null) continue;
                    var reply = new byte[4 + original.Length]; reply[0] = 0x80; reply[1] = 0xd6; packet.AsSpan(2, 2).CopyTo(reply.AsSpan(2)); original.CopyTo(reply, 4);
                    await _control.SendAsync(reply, message.RemoteEndPoint, _lifetime.Token).ConfigureAwait(false); Interlocked.Increment(ref _resendCount);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { Fail(e, "audio-control"); }
    }
    private async Task FeedbackLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token).ConfigureAwait(false);
                var response = await _rtsp.RequestAsync("POST", "/feedback", [], "application/octet-stream", cancellation: _lifetime.Token).ConfigureAwait(false);
                response.RequireSuccess("保持会话");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { Fail(e, "feedback"); }
    }
    private async Task EventLoopAsync()
    {
        try { await ReceiverEventChannel.RunAsync(_eventStream!, _diagnostic, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { Fail(e, "events"); }
    }
    private void Fail(Exception error) => Fail(error, "control");
    private void Fail(Exception error, string stage)
    {
        if (_disposed || _lifetime.IsCancellationRequested) return;
        var failure = error as SessionInterruption ?? new SessionInterruption(stage, error);
        if (Interlocked.CompareExchange(ref _backgroundFailure, failure, null) is not null) return;
        _diagnostic?.Invoke(failure.SafeDiagnostic); _cohort?.Fail(failure); _lifetime.Cancel();
    }
    private static int? TryPort(Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var value) && value is ulong port && port is > 0 and <= 65535 ? (int)port : null;
    private static UdpClient Bound(IPAddress local) => new(new IPEndPoint(local, 0));
    private static int Port(UdpClient udp) => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    private static uint RandomUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));

    public async Task CloseAsync(bool sendTeardown)
    {
        if (_disposed) return; _disposed = true; await _lifetime.CancelAsync().ConfigureAwait(false);
        _sessionPassword = null;
        if (_authenticationSecret is { } pending) { CryptographicOperations.ZeroMemory(pending); _authenticationSecret = null; }
        // A lost session must never issue volume or teardown commands to another owner.
        if (sendTeardown && _configured && _backgroundFailure is null && _cohort?.Failure is null)
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await _rtsp.RequestAsync("TEARDOWN", _url, cancellation: timeout.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
        if (_ptp is not null && _cohort is null) await _ptp.DisposeAsync().ConfigureAwait(false);
        _event?.Dispose(); _data.Dispose(); _control.Dispose(); _timing.Dispose(); await _rtsp.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_background).ConfigureAwait(false); _cipher?.Dispose(); _lifetime.Dispose();
        _eventStream?.Dispose();
        lock (_packetsLock) { _recent.Clear(); _recentOrder.Clear(); }
    }
    public ValueTask DisposeAsync() => new(CloseAsync(true));
}
