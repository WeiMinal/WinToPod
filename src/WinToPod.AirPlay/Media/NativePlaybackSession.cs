// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using WinToPod.AirPlay.Pairing;
using WinToPod.AirPlay.Transport;

namespace WinToPod.AirPlay.Media;

// One logical target; every native pair member gets the complete stereo frame.
// HomePod's existing native assignment decides left/right, never discovery order.
public sealed class NativePlaybackSession : IAsyncDisposable
{
    private readonly List<RealtimeAudioSession> _members = [];
    private PlaybackCohort? _cohort;
    private readonly SemaphoreSlim _volumeSerial = new(1, 1);
    private bool _closed;
    private NativePlaybackSession(PlaybackCohort? cohort) => _cohort = cohort;
    public long PacketsSent => _members.Sum(m => m.PacketsSent);
    public long Retransmits => _members.Sum(m => m.Retransmits);
    public long[] MemberPackets => _members.Select(m => m.PacketsSent).ToArray();
    public int LatencyFrames => (int)_members.Max(m => m.Latency);
    public int MemberCount => _members.Count;
    public bool IsSingleFallback { get; private set; }
    public int? LastConfirmedVolume => _members.FirstOrDefault()?.LastConfirmedVolume;

    // Caller supplies the resolved tight-sync leader first. This API does not
    // invent a native pair from unrelated receivers or make a takeover probe.
    public static async Task<NativePlaybackSession> OpenAsync(IReadOnlyList<AudioSessionOptions> members, Action<string>? progress,
        CancellationToken cancellation, Action<string>? diagnostic = null,
        Func<NativeMemberAuthenticationException, CancellationToken, Task<bool>>? singleFallbackConsent = null,
        Func<ReceiverPasswordRequest, CancellationToken, Task<string?>>? requestPassword = null, IReceiverPairingStore? pairingStore = null, IPlaybackCredentialStore? playbackCredentials = null)
    {
        if (members.Count is < 1 or > 2) throw new ArgumentException("当前组合连接需要一只音箱或完整的原生双音箱组合。");
        if (members.Select(m => m.RemoteAddress).Distinct().Count() != members.Count || members.Any(m => !m.LocalAddress.Equals(members[0].LocalAddress)))
            throw new IOException("组合成员地址重复或不在同一网络接口，已停止连接。");
        if (members.Select(m => m.SenderIdentity).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            throw new ArgumentException("组合成员必须使用同一发送端身份。", nameof(members));
        if (members.Select(m => m.VolumeCurve).Distinct().Count() != 1)
            throw new ArgumentException("组合成员必须使用同一音量映射。", nameof(members));
        using var memory = pairingStore is null ? new MemoryReceiverPairingStore() : null;
        var store = pairingStore ?? memory!;
        var cohort = members.Count == 2 ? new PlaybackCohort(members) : null;
        var session = new NativePlaybackSession(cohort);
        try
        {
            for (var i = 0; i < members.Count; i++)
            {
                cohort?.ThrowIfFailed();
                progress?.Invoke($"正在验证音箱 {i + 1}/{members.Count}……");
                var index = i + 1;
                try
                {
                    session._members.Add(await RealtimeAudioSession.OpenMemberAsync(members[i], progress, cancellation,
                        line => diagnostic?.Invoke($"成员{index}：{line}"), cohort, authenticateOnly: cohort is not null,
                        requestPassword: requestPassword, pairingStore: store, playbackCredentials: playbackCredentials).ConfigureAwait(false));
                }
                catch (IOException e) when (cohort is not null && e is PairingRejectedException or RtspRejectedException or PasswordRequiredException { Rejected: true })
                {
                    var failure = new NativeMemberAuthenticationException(index, e);
                    if (index == 2 && session._members.Count == 1 && singleFallbackConsent is not null &&
                        await singleFallbackConsent(failure, cancellation).ConfigureAwait(false))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        // Reuse the successful leader authentication, not a retry
                        // against the rejected member. No group SETUP has occurred.
                        session._members[0].UseSingleFallback(); session._cohort = null;
                        await cohort.DisposeAsync().ConfigureAwait(false);
                        session.IsSingleFallback = true;
                        diagnostic?.Invoke("用户确认单只兼容播放：保留已通过的主成员鉴权；不再向副成员鉴权或建立音频流；使用原单成员时钟与流协商路径。");
                        await session._members[0].ConfigurePreparedAsync(progress, cancellation).ConfigureAwait(false);
                        return session;
                    }
                    throw failure;
                }
            }
            if (cohort is not null)
            {
                diagnostic?.Invoke("全部成员鉴权通过，开始播放协商；此前未向任一成员SETUP/RECORD、改音量或发送音频。");
                for (var i = 0; i < session._members.Count; i++)
                {
                    cohort.ThrowIfFailed();
                    progress?.Invoke($"正在准备音箱 {i + 1}/{members.Count}……");
                    try { await session._members[i].ConfigurePreparedAsync(progress, cancellation).ConfigureAwait(false); }
                    catch (PlaybackAuthenticationException e) when (i == 1 && cohort.Failure is null)
                    {
                        var failure = new NativeMemberAuthenticationException(2, e, DuringPlayback: true);
                        // The leader was prepared with group timing. Release that
                        // accepted session before asking; never repurpose it as a
                        // single, or leave it reserved while the dialog is open.
                        await session.CloseAsync(true).ConfigureAwait(false);
                        if (singleFallbackConsent is null || !await singleFallbackConsent(failure, cancellation).ConfigureAwait(false)) throw failure;
                        cancellation.ThrowIfCancellationRequested();
                        diagnostic?.Invoke("用户确认播放验证失败后先用单只：原组合准备会话已释放，仅重新连接主成员，不再访问副成员。");
                        var fallback = await OpenAsync(members.Take(1).ToArray(), progress, cancellation, diagnostic,
                            requestPassword: requestPassword, pairingStore: store, playbackCredentials: playbackCredentials).ConfigureAwait(false);
                        fallback.IsSingleFallback = true; return fallback;
                    }
                }
                progress?.Invoke("正在确认两只音箱的共同时间轴……");
                await cohort.GetClock(diagnostic).WaitUntilReadyAsync(cancellation).ConfigureAwait(false);
                cohort.ThrowIfFailed();
                if (!await session.SetVolumeAndConfirmAsync(members[0].Volume, cancellation).ConfigureAwait(false)) throw new IOException("组合音量未确认。");
                foreach (var member in session._members) member.ConfirmGroupVolume(members[0].Volume);
                diagnostic?.Invoke("原生组合就绪：两条独立鉴权音频链路；同一时钟、帧与锚点；仅主成员控制组合音量。尚需实际试听确认。");
            }
            return session;
        }
        catch
        {
            // No audio was sent. Do not affect a potentially new owner on failure.
            await session.CloseAsync(cancellation.IsCancellationRequested).ConfigureAwait(false); throw;
        }
    }
    public Task RunAsync(Func<Memory<byte>, CancellationToken, ValueTask> readPcm, TimeSpan? duration, CancellationToken cancellation)
        => _members[0].RunMembersAsync(_members, readPcm, duration, cancellation);
    private void EnsureOwned()
    { if (_closed) throw new IOException("连接已结束。"); _cohort?.ThrowIfFailed(); foreach (var member in _members) member.EnsureOwned(); }
    public async Task<bool> SetVolumeAndConfirmAsync(int percent, CancellationToken cancellation)
    {
        await _volumeSerial.WaitAsync(cancellation).ConfigureAwait(false);
        try { EnsureOwned(); var confirmed = await _members[0].SetVolumeAndConfirmAsync(percent, cancellation).ConfigureAwait(false); EnsureOwned(); return confirmed; }
        finally { _volumeSerial.Release(); }
    }
    public async Task<int> ReadVolumeAsync(CancellationToken cancellation)
    {
        await _volumeSerial.WaitAsync(cancellation).ConfigureAwait(false);
        try { EnsureOwned(); var volume = await _members[0].ReadVolumeAsync(cancellation).ConfigureAwait(false); EnsureOwned(); return volume; }
        finally { _volumeSerial.Release(); }
    }
    public async Task CloseAsync(bool sendTeardown)
    {
        if (_closed) return; _closed = true;
        foreach (var member in _members) await member.CloseAsync(sendTeardown && _cohort?.Failure is null).ConfigureAwait(false);
        if (_cohort is not null) await _cohort.DisposeAsync().ConfigureAwait(false);
    }
    public ValueTask DisposeAsync() => new(CloseAsync(true));
}

internal sealed class PlaybackCohort : IAsyncDisposable
{
    private readonly AudioSessionOptions _leader;
    private readonly AudioSessionOptions[] _members;
    private readonly CancellationTokenSource _stop = new();
    private Exception? _failure;
    private PtpClockEngine? _clock;
    public byte[] Identity { get; } = RandomNumberGenerator.GetBytes(8);
    public string GroupId { get; } = Guid.NewGuid().ToString().ToUpperInvariant();
    public string SenderIdentity { get; } = Guid.NewGuid().ToString("D").ToUpperInvariant();
    public uint InitialTimestamp { get; } = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
    public Exception? Failure => _failure;
    public CancellationToken Cancellation => _stop.Token;
    public PlaybackCohort(IReadOnlyList<AudioSessionOptions> members)
    { _members = members.ToArray(); _leader = _members[0]; Identity[0] = (byte)((Identity[0] | 2) & 0xfe); }
    public bool IsLeader(AudioSessionOptions options) => options.RemoteAddress.Equals(_leader.RemoteAddress);
    public PtpClockEngine GetClock(Action<string>? diagnostic)
        => _clock ??= new(_leader.LocalAddress, _leader.RemoteAddress, _members.Select(m => m.RemoteAddress),
            BinaryPrimitives.ReadUInt64BigEndian(Identity), false, diagnostic, Fail, _leader.LoopbackTestPorts,
            _members.Select(m => m.RemoteAddress));
    public void Fail(Exception error)
    { if (Interlocked.CompareExchange(ref _failure, error, null) is null) _stop.Cancel(); }
    public void ThrowIfFailed()
    { if (_failure is not null) throw new IOException("组合中有成员失去连接，已停止整组音频与音量控制。", _failure); }
    public async ValueTask DisposeAsync()
    { _stop.Cancel(); if (_clock is not null) await _clock.DisposeAsync().ConfigureAwait(false); _stop.Dispose(); CryptographicOperations.ZeroMemory(Identity); }
}

public sealed class NativeMemberAuthenticationException(int member, IOException cause, bool DuringPlayback = false)
    : IOException(DuringPlayback ? $"组合成员{member}已配对，但播放密码验证未通过，整组尚未开始播放。{cause.Message}" : $"组合成员{member}未通过验证{(cause is PairingRejectedException refusal ? $"（TLV {refusal.Code}）" : "")}，整组尚未开始播放。{(cause is PasswordRequiredException { Rejected: true } ? "输入密码后仍被拒绝，请核对密码；也可能存在访问或鉴权兼容问题。" : "拒绝原因尚未确认，请导出诊断；不会自动换口令重试。")}", cause)
{ public int Member { get; } = member; public bool DuringPlayback { get; } = DuringPlayback; }
