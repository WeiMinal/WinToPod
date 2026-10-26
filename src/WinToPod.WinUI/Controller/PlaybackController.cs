// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using WinToPod.AirPlay.Discovery;
using WinToPod.AirPlay.Media;
using WinToPod.AirPlay.Pairing;
using WinToPod.Core;

namespace WinToPod.Desktop;

internal sealed class PlaybackController : IAsyncDisposable
{
    private readonly DesktopSettings _settings;
    private readonly bool _enableAutomation;
    private readonly AutomaticConnectionPolicy _automation;
    private LocalUseMonitor? _localUse;
    private bool _pumpingAutomatic, _automaticConnecting, _autoSuccessObserved, _automaticAborted, _connectionRemembered, _settingsOpen;
    private volatile bool _automaticAttempt, _autoNeedsManual;
    private PlaybackEntry? _activeEntry;
    private int _automaticSearches;
    private DateTimeOffset _nextAutomaticSearch = DateTimeOffset.UtcNow.AddSeconds(25);
    private readonly ReceiverSelection _devices = new();
    private readonly IPlaybackUi _ui;
    private bool _disposed;
    private bool IsDisposed => _disposed;
    private bool Disposing => _exiting;
    private readonly ActionState _connect = new("连接音箱"), _singleFallback = new("先用一只播放");
    private readonly StatusState _status = new("双击音箱或点击连接。");
    private readonly VolumeState _volume = new();
    private VideoSyncBridge? _videoBridge;
    private VideoPlayerSession? _videoPlayer;
    private int _lastVideoDelay = -1;
    private readonly SemaphoreSlim _videoUpdateGate = new(1, 1);

    private readonly IUiTimer _stats, _volumeDebounce;
    private readonly SemaphoreSlim _preferencesGate = new(1, 1);
    private readonly List<string> _diagnosticLines = [];
    private long _diagnosticTrimmedCount;
    private readonly ReceiverRetrySchedule _retrySchedule;
    private readonly SessionSoundTest _soundTest = new();
    private readonly DesktopPairingStore _pairingStore = new();
    private readonly DesktopPlaybackCredentialStore _playbackCredentials = new();
    private readonly List<string> _importantDiagnosticLines = [];
    private DiscoverySnapshot? _snapshot;
    private CancellationTokenSource? _scanCancel, _sessionCancel;
    private Task? _scanTask, _sessionTask;
    private NativePlaybackSession? _session;
    private EndpointVolumeLink? _volumeLink;
    private readonly IUiTimer _volumePoll;
    private long _volumeRevision;
    private bool _pollingVolume, _volumeFromWindows, _volumeUnconfirmed;
    private LoopbackAudioSource? _audio;

    private bool _activeSingleFallback, _showingRetry;
    private string? _activeMemberName;
    private string? _activeKey, _linkedEndpointId;
    private bool _loading = true, _exiting, _applyingVolume, _stateFailed, _working;

    public PlaybackController(DesktopSettings settings, IPlaybackUi ui, bool startup, bool enableVideoBridge = true, bool enableAutomation = true)
    {
        _ui = ui; _enableAutomation = enableAutomation;
        _automation = new(settings.Policy, settings.AutoConnectOnStart, settings.LastConnectedReceiver);
        _settings = settings; _retrySchedule = new(settings.ReceiverRetryAfter); _settings.CaptureEndpointId = null; // Migrate legacy capture-only selection; do not change Windows output on startup.
        _stats = ui.CreateTimer(500); _volumeDebounce = ui.CreateTimer(450); _volumePoll = ui.CreateTimer(1500);
        _devices.ItemSelectionChanged += (_, _) => SelectionChanged();
        _volume.ValueChanged += (_, _) =>
        {
            RenderView();
            if (!_loading && _session is not null && _sessionCancel?.IsCancellationRequested == false)
            {
                _volumeRevision++;
                try { if (!_volumeFromWindows) _volumeLink?.SetPercent(_volume.Value); }
                catch (Exception e) { Log("Windows音量同步失败：" + e.Message); _ = StopAsync(userInitiated: false); return; }
                _volumeDebounce.Stop(); _volumeDebounce.Start();
            }
        };
        _volumeDebounce.Tick += async () => { _volumeDebounce.Stop(); await ApplyVolumeAsync(); };
        _volumePoll.Tick += async () => await PullVolumeAsync();
        if (enableVideoBridge) try { _videoBridge = new(); } catch (SocketException) { Log("浏览器音画补偿端口不可用，本地播放器功能仍可使用。"); }
        _stats.Tick += async () => { UpdateStats(); await UpdateVideoSyncAsync(); await PumpAutomaticAsync(); };
        _loading = false; UpdateActions();
        Log("WinToPod V1.1 · WinUI 3");
    }
    public async Task InitializeAsync(IntPtr handle)
    {
        if (_enableAutomation)
        {
            _localUse = new(handle);
            _localUse.Intent += trusted =>
            {
                _automation.ObserveSession(_localUse.LocalAndUnlocked);
                if (trusted) _automation.ObserveLocalIntent(); else _automation.ObserveUntrustedIntent();
            };
        }
        _stats.Start();
        if (_settings.Interrupted && !_settings.NoticeDismissed) ShowInterruptionWindow();
        await ScanAsync();
    }
    public void ProcessWindowMessage(int message, IntPtr wParam, IntPtr lParam) => _localUse?.Process(message, wParam, lParam);
    public DesktopSettings Settings => _settings;
    public bool Working => _working;
    public bool Connected => CanTestSound();
    public bool AutomaticPending => _automation.HasPending;
    public void PauseAutomatic() { _automation.Pause(); SetStatus(_automation.Description); }
    public void SetPreferencesOpen(bool value) => _settingsOpen = value;
    public PlaybackEntry? Selection => Selected;
    public void Select(string key) { if (!_working) { _automation.Pause(); _devices.SelectTarget(key); } }
    public async Task ConnectAsync(string key, bool single = false)
    {
        if (_working) { if (key == _activeKey) await StopAsync(); return; }
        if (_devices.SelectTarget(key) && _connect.Enabled) await StartAsync(single || CanUseSingleDuringWait());
    }
    public Task DisconnectAsync() => StopAsync();
    public void SetVolume(int value) { if (!_exiting && !_disposed) _volume.Value = value; }
    public void Mute() => ToggleMute();
    public Task SearchAsync() => ScanAsync();
    public Task ReconnectAsync() => ReconnectInterruptedAsync();
    public void DismissInterruption(bool pause)
    {
        if (pause) _automation.Pause();
        if (!_exiting && _settings.Interrupted) { _settings.NoticeDismissed = true; TrySaveSettings(); }
    }
    private void RenderView() => _ui.Render(new(_devices.Entries, Selected?.Key, _activeKey, _scanCancel is not null, _working,
        _session is not null && !_settings.Interrupted && _sessionCancel?.IsCancellationRequested == false,
        _connect.Enabled, _singleFallback.Visible && _singleFallback.Enabled, _connect.Text, _status.Text, _status.Tag as string == "error", _volume.Value, _stateFailed));
    private PlaybackEntry? AutomaticTarget()
    {
        var id = _settings.LastConnectedReceiver;
        var members = _settings.LastConnectedMembers;
        return _devices.Entries.FirstOrDefault(row => (row.Key == id || members.Length > 0 && row.Members.Select(m => m.Id).Order(StringComparer.Ordinal).SequenceEqual(members.Order(StringComparer.Ordinal))) &&
            (members.Length == 0 || row.Members.Select(m => m.Id).Order(StringComparer.Ordinal).SequenceEqual(members.Order(StringComparer.Ordinal))));
    }
    private async Task PumpAutomaticAsync()
    {
        if (!_enableAutomation || _exiting || IsDisposed) return;
        _automation.ObserveSession(_localUse?.LocalAndUnlocked == true);
        if (_automaticConnecting && !_automation.CanAct)
        { _automaticAborted = true; await StopAsync(userInitiated: false); return; }
        if (_pumpingAutomatic || _working || _scanCancel is not null || _settingsOpen || _stateFailed || !_automation.HasPending) return;
        if (!_automation.CanAct) { SetStatus(_automation.Description); return; }
        var row = AutomaticTarget();
        if (row is null || !row.HasIpv4Endpoint)
        {
            if (_automaticSearches < 3 && DateTimeOffset.UtcNow >= _nextAutomaticSearch)
            { _automaticSearches++; _nextAutomaticSearch = DateTimeOffset.UtcNow.AddSeconds(30); await ScanAsync(); }
            return;
        }
        var retry = RetrySeconds(row);
        if (retry > 0) _automation.DeferredUntil(DateTimeOffset.UtcNow.AddSeconds(retry));
        var request = _automation.TryBegin(true, true); if (request is null) return;
        _pumpingAutomatic = _automaticAttempt = _automaticConnecting = true;
        _autoSuccessObserved = _autoNeedsManual = _automaticAborted = false;
        try
        {
            Log(request.Reason == AutomaticConnectionReason.Startup ? "本机启动意图已确认，自动连接记住的音箱。" : "激进模式：检测到近期本机操作，按退避策略尝试重连。");
            _devices.SelectTarget(row.Key); await StartAsync(automatic: true);
        }
        finally
        {
            _pumpingAutomatic = _automaticAttempt = _automaticConnecting = false;
            if (!_autoSuccessObserved)
            {
                if (_automaticAborted) _automation.AttemptCancelled();
                else _automation.AttemptFailed(_autoNeedsManual || _stateFailed,
                    row.Members.Select(m => _settings.ReceiverRetryAfter.GetValueOrDefault(m.Id)).DefaultIfEmpty().Max());
                if (_autoNeedsManual)
                {
                    SetStatus("自动连接需要确认。点击连接完成验证，原配对资料保留。", true);
                    _ui.Notify("需要确认连接", "请打开 WinToPod 点连接，完成密码或组合确认。");
                }
            }
        }
    }

    private PlaybackEntry? Selected => _devices.SelectedEntry;
    private async Task ScanAsync()
    {
        if (_scanCancel is not null) { _scanCancel.Cancel(); return; } if (_working || _exiting) return;
        using var cancel = new CancellationTokenSource(); _scanCancel = cancel; SetStatus("正在搜索同一网络中的音箱……"); UpdateActions();
        _scanTask = ScanCoreAsync(cancel.Token);
        try { await _scanTask; }
        finally { _scanTask = null; _scanCancel = null; if (!IsDisposed && !_exiting) UpdateActions(); }
    }
    private async Task ScanCoreAsync(CancellationToken cancellation)
    {
        try
        {
            var result = await new AirPlayBrowser().ScanAsync(TimeSpan.FromSeconds(10), cancellation: cancellation);
            if (_exiting || IsDisposed) return;
            _snapshot = result.Snapshot; PopulateTargets(result.Snapshot);
            foreach (var diagnostic in result.Diagnostics) Log("搜索提示：" + diagnostic.Message);
            SetStatus(_devices.TargetCount == 0 ? "暂未找到音箱。请确认电脑和音箱连接同一网络，再点重新搜索。" : "双击音箱或选中后连接。连接可能打断音箱当前播放。", _devices.TargetCount == 0);
            Log($"发现 {result.Snapshot.Receivers.Length} 个物理设备，显示 {_devices.TargetCount} 个可选目标；占用状态未知。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if (!_exiting) SetStatus("已取消搜索。已有结果仍可选择。"); }
        catch (Exception e) { if (!_exiting) ShowError(e); }
    }
    private void PopulateTargets(DiscoverySnapshot snapshot)
    {
        var selectedKey = Selected?.Key; _loading = true;
        _devices.ReplaceTargets(PlaybackEntries.Build(snapshot), selectedKey);
        _loading = false; SelectionChanged();
    }
    private void SelectionChanged()
    {
        if (_loading || _exiting) return; var row = Selected;
        if (row is null) { if (_scanCancel is null) SetStatus("选择音箱后连接。"); }
        else
        {
            _settings.SelectedReceiver = row.Key; _loading = true; _volume.Value = _settings.Volumes.TryGetValue(row.Key, out var remembered) ? remembered : 20; _loading = false;
            if (_scanCancel is null) SetStatus(!row.HasIpv4Endpoint ? "设备网络地址尚不完整，请重新搜索。" : row.IsGroup && _settings.SingleFallbackTargets.Contains(row.Key) ? "这组音箱已记住单只兼容模式，可在设置中恢复双只。" : "双击音箱或点击连接。连接可能打断音箱当前播放。");
            TrySaveSettings();
        }
        UpdateActions();
    }
    public async Task SavePreferencesAsync(Preferences preferences)
    {
        await _preferencesGate.WaitAsync();
        try
        {
        var cancelAutomatic = _automation.InFlightReason == AutomaticConnectionReason.Reclaim && preferences.Policy == ConnectionPolicy.Conservative ||
            _automation.InFlightReason == AutomaticConnectionReason.Startup && !preferences.AutoConnect;
        StartupRegistration.Apply(preferences.StartAtLogin);
        _settings.Policy = preferences.Policy; _settings.AutoConnectOnStart = preferences.AutoConnect;
        _automation.Configure(preferences.Policy, preferences.AutoConnect);
        if (cancelAutomatic) { _automaticAborted = true; await StopAsync(userInitiated: false); }
        _settings.HideOnClose = preferences.HideOnClose;
        if (Selected?.IsGroup == true)
        {
            if (preferences.PreferSingle) _settings.SingleFallbackTargets.Add(Selected.Key);
            else _settings.SingleFallbackTargets.Remove(Selected.Key);
        }
        if (TrySaveSettings()) { UpdateActions(); SetStatus("设置已保存。"); }
        }
        finally { _preferencesGate.Release(); }
    }
    public async Task SaveAppearanceAsync(ApplicationAppearance appearance)
    {
        if (!Enum.IsDefined(appearance)) throw new ArgumentOutOfRangeException(nameof(appearance));
        await _preferencesGate.WaitAsync();
        try
        {
            if (_exiting || _disposed) return;
            var previous = _settings.WinUiAppearance;
            _settings.WinUiAppearance = appearance;
            try { _settings.Save(); }
            catch { _settings.WinUiAppearance = previous; throw; }
        }
        finally { _preferencesGate.Release(); }
    }
    public string ForgetPlaybackCredentials()
    {
        if (Selected is not { } selected) return "请先选择音箱。";
        foreach (var member in selected.Members) _playbackCredentials.Forget(member.Id);
        Log("已忘记所选音箱的播放凭据，配对身份保留。");
        return "已忘记。下次连接可能需要密码；当前播放和配对身份保留。";
    }
    private async Task StartAsync(bool singleOverride = false, bool automatic = false)
    {
        if (_stateFailed || _working || _scanCancel is not null || _exiting || Selected is not { } row) return;
        if (!automatic) _automation.ManualConnect();
        if (RetrySeconds(row, singleOverride) > 0) { SetStatus(RetryMessage(row), true); return; }
        if (!row.HasIpv4Endpoint) { SetStatus("设备网络地址不完整，请重新搜索。", true); return; } if (!TrySaveSettings()) return;
        _working = true; _connectionRemembered = false; _activeEntry = row; _sessionCancel = new(); _activeKey = row.Key; UpdateActions();
        _sessionTask = RunSessionAsync(row, singleOverride, _sessionCancel.Token);
        try { await _sessionTask; }
        finally { _sessionTask = null; _sessionCancel.Dispose(); _sessionCancel = null; _activeKey = null; _activeEntry = null; _working = false; if (!IsDisposed && !_exiting) UpdateActions(); }
    }
    private async Task RunSessionAsync(PlaybackEntry row, bool singleOverride, CancellationToken cancellation)
    {
        NativePlaybackSession? session = null; var interrupted = false; var rememberFallback = false; AirPlayReceiver? resolvedReceiver = null;
        _activeSingleFallback = row.IsGroup && (singleOverride || _settings.SingleFallbackTargets.Contains(row.Key));
        try
        {
            SetStatus(row.Primary is null ? "正在确认这组音箱的连接入口……" : "正在连接音箱……");
            var receiver = await PlaybackEntries.ResolveAsync(row, cancellation); resolvedReceiver = receiver;
            var blockedIds = _activeSingleFallback ? new[] { receiver.Id } : row.Members.Select(m => m.Id);
            if (_retrySchedule.Remaining(blockedIds, DateTimeOffset.UtcNow) > 0) throw new IOException("所选音箱仍处于验证等待中。请等待结束后再手动连接。"); var endpoint = PlaybackEntry.Endpoint(receiver) ?? throw new IOException("设备地址不完整，请重新搜索。");
            _activeMemberName = receiver.Name;
            Log($"{(_automaticAttempt ? "自动" : "手动")}连接：{row.Kind}；接收型号={receiver.Model}；组合主入口={(ReceiverAccess.IsTightSyncLeader(receiver) ? "tight-sync" : "single")}；密码不写入日志。");
            var remote = endpoint.Addresses.Select(IPAddress.Parse).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            var peers = row.Members.SelectMany(m => m.Endpoints).SelectMany(e => e.Addresses).Select(IPAddress.Parse).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Distinct().ToArray();
            var volume = _settings.Volumes.TryGetValue(row.Key, out var remembered) ? remembered : 20;
            if (!_settings.WinUiPreMasterTargets.Contains(row.Key))
            {
                volume = Math.Min(volume, 10);
                Log("首次使用修正后的应用声音采集，以不超过10%的音量起步，后续记忆手动调整。");
            }
            var options = new AudioSessionOptions(remote, endpoint.Port, IPAddress.Parse(endpoint.InterfaceAddress), null, volume, peers, receiver.Properties, SenderIdentity: _settings.SenderIdentity, ReceiverId: receiver.Id, VolumeCurve: ReceiverVolumeCurve.DesktopCalibrated);
            var members = new List<AudioSessionOptions> { options };
            if (row.IsGroup && !_activeSingleFallback)
            {
                if (row.Members.Length != 2 || row.Members.Any(m => !m.IsHomePod) || row.Members.Select(m => m.TightSyncId).Distinct().Count() != 1 || row.Members.Any(m => m.TightSyncId is null))
                    throw new IOException("原生立体声对成员尚未找全或分组信息不一致，请重新搜索；不会只播放一边。");
                foreach (var member in row.Members.Where(m => m.Id != receiver.Id))
                {
                    var memberEndpoint = PlaybackEntry.Endpoint(member) ?? throw new IOException("组合成员缺少网络地址，请重新搜索。");
                    var address = memberEndpoint.Addresses.Select(IPAddress.Parse).First(a => a.AddressFamily == AddressFamily.InterNetwork);
                    members.Add(new(address, memberEndpoint.Port, IPAddress.Parse(memberEndpoint.InterfaceAddress), null, volume, peers, member.Properties, SenderIdentity: _settings.SenderIdentity, ReceiverId: member.Id, VolumeCurve: ReceiverVolumeCurve.DesktopCalibrated));
                }
            }
            session = await NativePlaybackSession.OpenAsync(members, message => Ui(() => { SetStatus(FriendlyProgress(message)); Log(message); }), cancellation,
                        diagnostic: message => Ui(() => Log(message)), singleFallbackConsent: async (failure, token) =>
                        {
                            var accepted = await AskSingleFallbackAsync(receiver.Name, failure, row, receiver.Id, token);
                            if (accepted is null) return false;
                            rememberFallback = accepted.Value; _activeSingleFallback = true; return true;
                        }, requestPassword: (request, token) => AskPasswordAsync(request, row, token), pairingStore: _pairingStore, playbackCredentials: _playbackCredentials);
            _session = session; RememberVolume(row.Key, session.LastConfirmedVolume!.Value);
            if (_activeSingleFallback && rememberFallback) _settings.SingleFallbackTargets.Add(row.Key);
            Log(_activeSingleFallback ? "当前模式：单只兼容播放；仅主成员一条音频链路，左右声道合并。" : row.IsGroup ? "当前模式：双成员原生立体声。" : "当前模式：单个AirPlay设备。");
            Log($"协商音频缓冲：{session.LatencyFrames}采样 / {VideoSyncState.FromFrames(session.LatencyFrames)}毫秒；这是协议估算，不是实测声学延迟。");
            SetDisplayedVolume(session.LastConfirmedVolume.Value);
            _settings.Interrupted = false; _settings.NoticeDismissed = false; _settings.InterruptedReceiver = null; _settings.InterruptionReason = "unknown";
            if (!TrySaveSettings()) throw new IOException("音量已确认但设置无法保存，已停止发送声音。");
            RenderView(); _ui.CloseInterruption(); UpdateActions();
            _audio = await LoopbackAudioSource.OpenAsync(null, cancellation, requirePreMaster: true);
            _settings.WinUiPreMasterTargets.Add(row.Key);
            if (!TrySaveSettings()) throw new IOException("采集偏好无法保存，已停止推流。");
            Log("采集路径：" + _audio.CaptureMode + (_audio.CaptureFallback is null ? "" : "；兼容回退=" + _audio.CaptureFallback));
            if (_videoPlayer is { HasExited: false }) await _audio.IncludePlayerAsync(_videoPlayer.ProcessId, cancellation);
            AttachVolumeLink(_audio.EndpointId); _volumePoll.Start();
            await Task.Delay(120, cancellation); Log("开始推送电脑声音；采集样本保持原样，由音箱控制播放音量，不采集麦克风。");
            SetStatus(_activeSingleFallback ? $"单只兼容播放 · {receiver.Name}。左右声道已合并，原生组合保留。" : "正在推送电脑声音。");
            await session.RunAsync((memory, token) => ReadPlaybackPcmAsync(_audio.ReadAsync, memory, token), null, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Log("用户取消或停止连接，不会自动重连。"); SetStatus("已断开。需要时点击连接音箱。"); }
        catch (Exception e)
        {
            if (_automaticAttempt && (e is NativeMemberAuthenticationException || e.GetBaseException() is PairingRejectedException or PasswordRequiredException or System.Security.Cryptography.CryptographicException or WinToPod.AirPlay.Transport.PlaybackAuthenticationException or WinToPod.AirPlay.Transport.RtspRejectedException)) _autoNeedsManual = true;
            interrupted = session is not null && session.PacketsSent > 0;
            if (e.GetBaseException() is PairingRejectedException { RetryDelaySeconds: > 0 } refusal)
            {
                var failed = e is NativeMemberAuthenticationException { Member: 2 } ? row.Members.First(m => m.Id != resolvedReceiver?.Id) : resolvedReceiver;
                if (failed is not null) { _retrySchedule.Record(failed.Id, refusal.RetryDelaySeconds.Value, refusal.ReceivedAtUtc); TrySaveSettings(); }
                Log($"音箱要求等待{refusal.RetryDelaySeconds.Value}秒；只限制后续手动连接，不自动重试。");
            }
            if (SessionInterruption.Find(e) is { } failure) Log(failure.SafeDiagnostic);
            Log(e.GetType().Name + "：" + e.Message); if (interrupted) MarkInterrupted(row.Key, e);
            else SetStatus(row.Members.Any(m => m.Model?.StartsWith("Mac", StringComparison.OrdinalIgnoreCase) == true) && (e is PairingRejectedException { Code: 403 } || e.Message.Contains("403", StringComparison.Ordinal))
                ? "Mac拒绝连接（403）。请检查Mac自己的“隔空播放接收器”访问范围；它独立于家庭App。仍失败请导出诊断。" : UserMessage(e), true);
        }
        finally
        {
            _soundTest.Reset(); DetachVolumeLink(); _session = null; await UpdateVideoSyncAsync(waitForPrior: true);
            try { _audio?.Dispose(); } catch (Exception e) { Log("采集停止提示：" + e.Message); } _audio = null;
            if (session is not null) try { await session.CloseAsync(!interrupted); } catch (Exception e) { Log("连接清理提示：" + e.Message); }
            _activeSingleFallback = false; _activeMemberName = null;
        }
    }
    private async ValueTask ReadPlaybackPcmAsync(Func<Memory<byte>, CancellationToken, ValueTask> source, Memory<byte> memory, CancellationToken cancellation)
    { await source(memory, cancellation); _soundTest.Apply(memory.Span); if (_activeSingleFallback) PcmMix.ToDualMono(memory.Span); }

    private Task<string?> AskPasswordAsync(ReceiverPasswordRequest request, PlaybackEntry row, CancellationToken cancellation)
        => _ui.InvokeAsync(() => AskPasswordCoreAsync(request, row, cancellation));
    private async Task<string?> AskPasswordCoreAsync(ReceiverPasswordRequest request, PlaybackEntry row, CancellationToken cancellation)
    {
        if (_automaticAttempt) { _autoNeedsManual = true; return null; }
        var member = row.Members.FirstOrDefault(m => m.Endpoints.Any(e => e.Addresses.Any(a => IPAddress.TryParse(a, out var ip) && ip.Equals(request.RemoteAddress))));
        var name = member?.Name ?? row.Name;
        var answer = await _ui.RequestPasswordAsync(name, request.ForPlayback, cancellation);
        if (answer is null) { _sessionCancel?.Cancel(); return null; }
        _playbackCredentials.SetRemember(member?.Id ?? row.Primary?.Id ?? request.RemoteAddress.ToString(), answer.Remember);
        Log(answer.Remember ? "用户选择以当前Windows账户加密保存验证凭据；密码不写入日志。" : "仅在本次程序运行期间记住播放凭据。");
        return answer.Password;
    }
    private Task<bool?> AskSingleFallbackAsync(string memberName, NativeMemberAuthenticationException failure, PlaybackEntry row, string leaderId, CancellationToken cancellation)
        => _ui.InvokeAsync(() => AskSingleFallbackCoreAsync(memberName, failure, row, leaderId, cancellation));
    private async Task<bool?> AskSingleFallbackCoreAsync(string memberName, NativeMemberAuthenticationException failure, PlaybackEntry row, string leaderId, CancellationToken cancellation)
    {
        if (_automaticAttempt) { _autoNeedsManual = true; return null; }
        if (failure.InnerException is PairingRejectedException { RetryDelaySeconds: > 0 } refusal)
        {
            var member = row.Members.First(m => m.Id != leaderId);
            _retrySchedule.Record(member.Id, refusal.RetryDelaySeconds.Value, refusal.ReceivedAtUtc); TrySaveSettings();
        }
        var answer = await _ui.RequestSingleFallbackAsync(memberName, failure.DuringPlayback, cancellation);
        if (answer == true) { _settings.SingleFallbackTargets.Add(row.Key); if (!TrySaveSettings()) throw new IOException("无法保存单只兼容选择，已停止连接。"); }
        return answer;
    }
    private static string FriendlyProgress(string message) => message switch
    {
        "正在读取设备信息" => "正在检查音箱的连接要求……", "正在进行会话鉴权" => "正在验证连接……", "正在恢复并确认音箱音量" => "正在恢复上次音量……",
        _ when message.Contains("SETUP", StringComparison.Ordinal) || message.Contains("事件通道", StringComparison.Ordinal) => "正在准备音箱播放……", _ when message.Contains("RECORD", StringComparison.Ordinal) => "正在确认播放连接……", _ => message
    };
    private async Task StopAsync(bool userInitiated = true)
    {
        if (userInitiated) _automation.Pause();
        _soundTest.Reset(); DetachVolumeLink(); _scanCancel?.Cancel(); _sessionCancel?.Cancel(); _ui.CancelPrompts();
        if (_sessionTask is not null) { _connect.Enabled = false; SetStatus("正在断开……"); }
        await UpdateVideoSyncAsync(waitForPrior: true);
        if (_sessionTask is { } task) { _connect.Enabled = false; SetStatus("正在断开……"); await task; }
    }
    private async Task ApplyVolumeAsync()
    {
        if (_applyingVolume || _session is not { } session || _activeKey is not { } key || _sessionCancel?.IsCancellationRequested != false) return;
        _applyingVolume = true; var displayed = _volume.Value; var value = displayed; var revision = _volumeRevision;
        try
        {
            if (!await session.SetVolumeAndConfirmAsync(value, _sessionCancel.Token))
            {
                if (!ReferenceEquals(_session, session) || _sessionCancel?.IsCancellationRequested != false) return;
                // Do not overwrite an adjustment made while confirmation was
                // pending, and do not schedule a SET loop after restoring UI.
                if (_volumeRevision == revision && session.LastConfirmedVolume is int previous)
                {
                    _volumeDebounce.Stop(); _volumeLink?.SetPercent(previous); SetDisplayedVolume(previous);
                    displayed = previous; _volumeRevision++; revision = _volumeRevision;
                }
                Log($"音量暂未确认：请求 {value}%；保留上次确认值和播放连接；后续轮询继续读取音箱实际状态。");
                _volumeUnconfirmed = true;
                SetStatus("音箱暂未确认本次音量，播放连接保留。可稍后再调节。", true);
                return;
            }
            if (ReferenceEquals(_session, session) && _sessionCancel?.IsCancellationRequested == false) { RememberVolume(key, value); if (!TrySaveSettings()) throw new IOException("设置无法保存，已停止声音推送。"); Log($"已确认音量：面板 {displayed}%；映射 desktop-calibrated-v1；接收端 {ReceiverVolume.Decibels(value, ReceiverVolumeCurve.DesktopCalibrated):F3} dB。"); }
            if (ReferenceEquals(_session, session) && _volumeUnconfirmed && _sessionCancel?.IsCancellationRequested == false)
            { _volumeUnconfirmed = false; SetStatus("音量已确认，正在推送电脑声音。"); }
        }
        catch (OperationCanceledException) when (_sessionCancel?.IsCancellationRequested != false) { }
        catch (Exception e) { if (ReferenceEquals(_session, session)) { Log("音量更新停止：" + e.Message); MarkInterrupted(key, e); await StopAsync(userInitiated: false); } }
        finally { _applyingVolume = false; if (ReferenceEquals(_session, session) && _sessionCancel?.IsCancellationRequested == false && (_volumeRevision != revision || _volume.Value != displayed)) _volumeDebounce.Start(); }
    }
    private void RememberVolume(string key, int receiverPercent)
    {
        _settings.Volumes[key] = receiverPercent;
        if (receiverPercent > 0) _settings.UnmutedVolumes[key] = receiverPercent;
    }
    private void ToggleMute()
    {
        if (_session is null || _activeKey is not { } key || _sessionCancel?.IsCancellationRequested != false || _settings.Interrupted) return;
        if (_volume.Value > 0)
        {
            var confirmed = _settings.Volumes.GetValueOrDefault(key, 20);
            _settings.UnmutedVolumes[key] = !_volumeDebounce.Enabled && !_applyingVolume && confirmed > 0 && confirmed == _volume.Value
                ? confirmed : _volume.Value;
            _volume.Value = 0;
        }
        else
        {
            _volume.Value = _settings.UnmutedVolumes.GetValueOrDefault(key, 20);
        }
        _volumeDebounce.Stop(); _ = ApplyVolumeAsync();
    }

    public IReadOnlyList<CaptureChoice> CaptureSources() => WindowsOutputControl.List();
    public string CurrentOutputId() => WindowsOutputControl.Current();
    public async Task ChangeOutputAsync(string id)
    {
        if (_exiting || _disposed) return;
        await _preferencesGate.WaitAsync();
        try
        {
            if (_exiting || _disposed) return;
            WindowsOutputControl.SetDefault(id);
            _settings.CaptureEndpointId = null;
            if (_session is not null && _sessionCancel?.IsCancellationRequested == false)
            {
                try { AttachVolumeLink(id); }
                catch { await StopAsync(userInitiated: false); throw new IOException("Windows 默认输出已切换，音量联动未能建立，请重新连接音箱。"); }
            }
            if (!TrySaveSettings()) throw new IOException("Windows 默认输出已切换，但偏好未能保存。");
            Log("Windows 默认输出已切换并读回确认；应用混音采集不变。");
            SetStatus(_session is null ? "Windows 默认输出已切换。" : "Windows 默认输出已切换，HomePod 播放继续。");
        }
        finally { _preferencesGate.Release(); }
    }
    private void AttachVolumeLink(string endpointId)
    {
        if (_session is not { } ownedSession) return;
        EndpointVolumeLink? next = null;
        try
        {
            next = new(endpointId, value => Ui(() =>
            {
                if (!ReferenceEquals(_session, ownedSession) || !ReferenceEquals(_volumeLink, next) || _sessionCancel?.IsCancellationRequested != false) return;
                _volumeFromWindows = true;
                try { _volume.Value = value; }
                finally { _volumeFromWindows = false; }
            }));
            // Set before publishing, so a failure preserves the old subscription.
            next.SetPercent(_volume.Value);
        }
        catch { next?.Dispose(); throw; }
        var old = _volumeLink; _volumeLink = next; _linkedEndpointId = endpointId; _volumeRevision++; old?.Dispose();
    }
    private void SetDisplayedVolume(int value)
    {
        var loading = _loading; _loading = true;
        try { _volume.Value = value; } finally { _loading = loading; }
    }
    private void DetachVolumeLink()
    {
        _volumeUnconfirmed = false;
        _volumeRevision++; _volumePoll.Stop(); _volumeDebounce.Stop();
        _volumeLink?.Dispose(); _volumeLink = null; _linkedEndpointId = null;
    }
    private async Task PullVolumeAsync()
    {
        if (_pollingVolume || _applyingVolume || _volumeDebounce.Enabled || _volumeLink is null || _session is not { } session ||
            _activeKey is not { } key || _sessionCancel?.IsCancellationRequested != false) return;
        // Also follow a device changed in Windows itself, without replacing audio capture.
        try
        {
            var current = WindowsOutputControl.Current();
            if (!string.Equals(current, _linkedEndpointId, StringComparison.OrdinalIgnoreCase)) AttachVolumeLink(current);
        }
        catch (Exception e) { Log("Windows输出音量联动停止：" + e.Message); await StopAsync(userInitiated: false); return; }
        var revision = _volumeRevision; _pollingVolume = true;
        try
        {
            var value = await session.ReadVolumeAsync(_sessionCancel.Token);
            if (!ReferenceEquals(_session, session) || _sessionCancel?.IsCancellationRequested != false || _volumeLink is null ||
                _volumeRevision != revision || _applyingVolume || _volumeDebounce.Enabled) return;
            if (_settings.Volumes.GetValueOrDefault(key, -1) != value)
            {
                var display = value;
                _volumeLink.SetPercent(display); SetDisplayedVolume(display); _volumeRevision++;
                RememberVolume(key, value);
                if (!TrySaveSettings()) throw new IOException("音量设置无法保存。");
                Log($"音箱音量变化已同步至Windows：{value}%。");
                if (_volumeUnconfirmed) { _volumeUnconfirmed = false; SetStatus("音量已同步，正在推送电脑声音。"); }
            }
        }
        catch (OperationCanceledException) when (_sessionCancel?.IsCancellationRequested != false) { }
        catch (Exception e)
        {
            if (ReferenceEquals(_session, session)) { Log("音量读取停止：" + e.Message); MarkInterrupted(key, e); await StopAsync(userInitiated: false); }
        }
        finally { _pollingVolume = false; }
    }
    private void MarkInterrupted(string key, Exception? error = null)
    {
        _automation.Interrupted(key);
        DetachVolumeLink();
        _videoBridge?.Update(VideoSyncState.Offline);
        var pending = _settings.Interrupted && _settings.InterruptedReceiver == key;
        _settings.Interrupted = true; _settings.InterruptedReceiver = key; if (!pending) _settings.NoticeDismissed = false;
        _settings.InterruptionReason = error is null ? "unknown" : SessionInterruption.Find(error)?.Category ?? "unknown";
        TrySaveSettings(); RenderView(); SetStatus((error is null ? "连接已中断。" : UserMessage(error)) + " " + InterruptionPolicyHint(), true);
        if (!pending) { ShowInterruptionWindow(); _ui.Notify("连接已中断", InterruptionPolicyHint()); }
    }
    private string InterruptionPolicyHint() => _settings.Policy == ConnectionPolicy.Conservative ? "保守模式：让出后等待你手动连接。" :
        _automation.HasPending ? "激进模式：近期本机操作时有限重试；点暂不连接可暂停。" :
        string.IsNullOrEmpty(_automation.Description) ? "自动重连已停止，需要时请手动连接。" : _automation.Description;
    private void ShowInterruptionWindow()
    {
        var reason = _settings.InterruptionReason switch { "remote-eof" => "音箱关闭了本次播放连接，具体原因尚未确认。", "timeout" => "音箱控制请求超时。", "socket" => "音箱网络通道出现错误。", _ => "音箱连接已中断，具体原因尚未确认。" };
        _ui.ShowInterruption(reason, InterruptionPolicyHint());
    }
    private async Task ReconnectInterruptedAsync()
    {
        _ui.ShowPanel(); if (_working || _scanCancel is not null || _stateFailed) return;
        var found = _settings.InterruptedReceiver is { } key && _devices.SelectTarget(key);
        if (!found) { SetStatus("请先重新搜索，找到上次中断的音箱后再连接。"); return; }
        await StartAsync();
    }
    private void UpdateStats()
    {
        if (!_connectionRemembered && _session is { PacketsSent: > 0 } && _activeEntry is { } active)
        {
            _connectionRemembered = true; _settings.LastConnectedReceiver = active.Key; _settings.LastConnectedMembers = active.Members.Select(m => m.Id).ToArray();
            _automation.Connected(active.Key); _automaticConnecting = false; if (_automaticAttempt) _autoSuccessObserved = true;
            if (!TrySaveSettings()) _ = StopAsync(userInitiated: false);
        }
        if (!_working && Selected is { } selected)
        {
            UpdateActions();
            if (RetrySeconds(selected) > 0) { _showingRetry = true; SetStatus(RetryMessage(selected)); return; }
            if (_showingRetry) { _showingRetry = false; SetStatus("验证等待已结束。需要使用音箱时，点击连接。"); }
        }
        if (_session is null || _sessionCancel?.IsCancellationRequested != false || _settings.Interrupted) return;
        if (_soundTest.Status is { Active: true } test) { SetStatus(test.Description + (_activeSingleFallback ? " 单只模式下三段声音均从同一只播放。" : "")); return; }
        SetStatus(_activeSingleFallback ? $"单只兼容播放 · {_activeMemberName}，约有2秒缓冲。" : _audio is null ? "正在准备电脑声音……" : "正在推送电脑声音。点击断开即可停止。");
    }
    private void UpdateActions()
    {
        RenderView();
        _connect.Enabled = !_stateFailed && (_working || _scanCancel is null && Selected is { HasIpv4Endpoint: true } selected && (RetrySeconds(selected) == 0 || CanUseSingleDuringWait()));
        _connect.Text = _working ? _session is null ? "取消连接" : "断开连接" : CanUseSingleDuringWait() ? "先用一只播放" : _settings.Interrupted && Selected?.Key == _settings.InterruptedReceiver ? "重新连接" : "连接音箱";
        _singleFallback.Visible = !_working && Selected is { IsGroup: true, Primary: not null } chosen && !_settings.SingleFallbackTargets.Contains(chosen.Key) && !CanUseSingleDuringWait();
        _singleFallback.Enabled = !_stateFailed && _scanCancel is null && Selected is { } pair && RetrySeconds(pair, true) == 0;
        RenderView();
    }
    private long RetrySeconds(PlaybackEntry row, bool singleOverride = false)
    {
        var single = row.IsGroup && (singleOverride || _settings.SingleFallbackTargets.Contains(row.Key));
        var members = single ? row.Primary is { } primary ? new[] { primary } : [] : row.Members;
        return _retrySchedule.Remaining(members.Select(m => m.Id), DateTimeOffset.UtcNow);
    }
    private bool CanUseSingleDuringWait() => Selected is { IsGroup: true, Primary: not null } row &&
        !_settings.SingleFallbackTargets.Contains(row.Key) && RetrySeconds(row) > 0 && RetrySeconds(row, true) == 0;
    private static string WaitDuration(long seconds) => seconds < 60 ? $"{seconds}秒" : seconds < 3600 ? $"{seconds / 60}分{seconds % 60:00}秒" : $"{seconds / 3600}小时{seconds / 60 % 60:00}分";
    private string RetryMessage(PlaybackEntry row) => CanUseSingleDuringWait()
        ? $"另一只音箱需等待{WaitDuration(RetrySeconds(row))}；现在可先用{row.Primary!.Name}播放。"
        : $"音箱要求等待{WaitDuration(RetrySeconds(row))}，期间不会重新验证。";
    private bool CanTestSound() => _session is not null && _audio is not null && !_settings.Interrupted && _sessionCancel?.IsCancellationRequested == false;
    private VideoSyncState CurrentVideoState()
    {
        if (!CanTestSound() || _activeKey is null) return VideoSyncState.Offline;
        return new(true, VideoSyncState.FromFrames(_session!.LatencyFrames, (_audio?.EstimatedCaptureLagMs ?? 0) + _settings.VideoCorrectionsMs.GetValueOrDefault(_activeKey)));
    }
    private async Task UpdateVideoSyncAsync(bool waitForPrior = false)
    {
        var state = CurrentVideoState(); _videoBridge?.Update(state);
        var delay = state.Connected ? state.DelayMs : 0;
        if (_videoPlayer is null) return;
        if (!await _videoUpdateGate.WaitAsync(waitForPrior ? Timeout.Infinite : 0)) return;
        try
        {
            state = CurrentVideoState(); delay = state.Connected ? state.DelayMs : 0;
            if (_videoPlayer is null || delay == _lastVideoDelay) return;
            if (_videoPlayer.HasExited) { _videoPlayer.Dispose(); _videoPlayer = null; _lastVideoDelay = -1; return; }
            if (await _videoPlayer.SetDelayAsync(delay)) _lastVideoDelay = delay;
            else { Log("本次播放器偏移未确认；请在播放器中检查音轨同步，关闭播放器后可重新同步打开。"); _lastVideoDelay = delay; }
        }
        finally { _videoUpdateGate.Release(); }
    }
    public string BrowserSyncDescription()
    {
        if (_videoBridge is null) return "本机浏览器接口暂不可用。";
        var s = _videoBridge.CompanionStatus;
        if (!s.Detected) return "尚未检测到浏览器助手。打开WinToPod本身不会改变网页画面。";
        if (!CurrentVideoState().Connected) return "助手已检测到，连接音箱后自动同步。";
        return s.Phase switch
        {
            "active" => $"正在补偿画面：已呈现{s.FramesPresented}帧，延后约{s.ObservedDelayMs}毫秒。",
            "paused" => "视频已暂停，同步助手保持就绪。", "buffering" => "正在缓冲画面以对齐音箱。",
            "unsupported" or "error" => "当前视频未能补偿，已保留原画面。请在扩展中检查兼容状态。",
            _ => "助手已连接，等待视频播放。"
        };
    }
    public VideoSyncState VideoState => CurrentVideoState();
    public int VideoCorrection => (_activeKey ?? Selected?.Key) is { } key ? _settings.VideoCorrectionsMs.GetValueOrDefault(key) : 0;
    public void SetVideoCorrection(int value)
    {
        if ((_activeKey ?? Selected?.Key) is not { } key) return;
        _settings.VideoCorrectionsMs[key] = Math.Clamp(value, -1500, 1500); TrySaveSettings();
    }
    public async Task LaunchVideoAsync(string player, string media)
    {
        var current = CurrentVideoState(); if (!current.Connected) throw new ArgumentException("音箱已断开，请先重新连接。");
        if (_videoPlayer is { HasExited: false }) throw new ArgumentException("请先关闭上次由WinToPod打开的播放器。");
        _videoPlayer?.Dispose(); _videoPlayer = new(player, media, current.DelayMs); _lastVideoDelay = current.DelayMs;
        if (_audio is not null) await _audio.IncludePlayerAsync(_videoPlayer.ProcessId, _sessionCancel?.Token ?? CancellationToken.None);
        _settings.VideoPlayerPath = player; TrySaveSettings();
    }
    public (bool Available, bool Active, bool CanStop, string Description) SoundTestState()
    {
        var state = _soundTest.Status;
        var muted = _volume.Value == 0;
        return (CanTestSound() && (!muted || state.Active), state.Active, state.CanStop, state.Active ? state.Description :
            CanTestSound() && muted ? "当前已静音，取消静音后可测试声音。" :
            CanTestSound() ? _activeSingleFallback ? "当前仅一只播放；可检查三段测试声音是否完整，无法验证双只左右位置。" : "测试会依次播放左、右、左右一起，随后自动恢复电脑声音。"
            : _working ? "正在准备连接，请稍候。" : "先连接音箱，再测试当前连接的声音。");
    }
    public void ToggleSoundTest()
    {
        if (!CanTestSound()) return;
        if (_soundTest.Status.Active) { _soundTest.Stop(); Log("已停止测试音；保持当前连接并恢复电脑声音。"); }
        else if (_volume.Value == 0) return;
        else if (_soundTest.Start(_session!.LatencyFrames)) Log("在当前播放连接内测试声音：左 → 右 → 一起；采集持续排空，测试后恢复实时声音，不重新鉴权。");
        UpdateStats();
    }

    private bool TrySaveSettings()
    {
        try { _settings.Save(); return true; }
        catch (Exception e) { _stateFailed = true; Log("设置保存失败：" + e.Message); SetStatus("设置保存失败，已暂停连接。请检查磁盘权限并重新启动，原设置文件请保留。", true); return false; }
    }
    private static string UserMessage(Exception e)
    {
        if (SessionInterruption.Find(e) is { } interruption) return interruption.Message;
        if (e is WinToPod.AirPlay.Transport.RtspRejectedException rejected && rejected.Status is 401 or 403) return $"连接请求被拒绝（{rejected.Status}），原因尚未确认。请通过连接详情导出诊断。";
        if (e is PairingRejectedException pairing && pairing.Code == 2) return "连接验证未通过，可能与密码、访问要求或协议兼容有关。已停止连接，请导出诊断。";
        if (e is PasswordRequiredException { Rejected: false }) return "音箱要求额外的密码验证，当前流程尚未兼容。已停止连接，请导出诊断。";
        if (e is TimeoutException) return "音箱在准备连接时没有响应，可能存在兼容性问题。已停止连接；请通过连接详情导出诊断。";
        if (e.Message.Any(c => c is >= '\u4e00' and <= '\u9fff')) return e.Message;
        return e switch
        {
            CryptographicException => "连接验证未通过。请查看连接详情，或导出诊断。", SocketException => "暂时无法与音箱通信，请检查网络或重新搜索。",
            OperationCanceledException or TimeoutException => "连接超时。请确认音箱在线，再重试；详细步骤已记录，可导出诊断。", InvalidDataException => "音箱返回的格式暂未兼容，请导出诊断。", _ => "暂时无法连接。请查看连接详情，或导出诊断。"
        };
    }
    private void SetStatus(string message, bool error = false) { if (IsDisposed) return; _status.Text = message; _status.Tag = error ? "error" : "secondary"; RenderView(); }
    private void ShowError(Exception e) { Log(e.GetType().Name + "：" + e.Message); SetStatus(UserMessage(e), true); }
    private void Ui(Action action) { if (IsDisposed || _exiting) return; if (_ui.HasThreadAccess) action(); else _ui.Post(action); }
    private void Log(string message)
    {
        if (IsDisposed || Disposing) return; if (!_ui.HasThreadAccess) { Ui(() => Log(message)); return; }
        _diagnosticLines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        if (message.Contains("故障", StringComparison.Ordinal) || message.Contains("Exception", StringComparison.Ordinal) || message.Contains("中断", StringComparison.Ordinal) ||
            message.Contains("手动连接", StringComparison.Ordinal) || message.Contains("当前模式", StringComparison.Ordinal) || message.Contains("音量读取停止", StringComparison.Ordinal) || message.Contains("已确认音量", StringComparison.Ordinal) || message.Contains("音箱音量变化", StringComparison.Ordinal) || message.Contains("自动连接", StringComparison.Ordinal))
        { _importantDiagnosticLines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message); if (_importantDiagnosticLines.Count > 512) _importantDiagnosticLines.RemoveAt(0); }
        // Preserve setup evidence as well as recent activity after long playback.
        if (_diagnosticLines.Count > 2000) { var remove = _diagnosticLines.Count - 2000; _diagnosticTrimmedCount += remove; _diagnosticLines.RemoveRange(100, remove); }
    }
    public string DetailsText()
    {
        var selected = Selected; var device = selected is null ? "尚未选择音箱。" : selected.Kind + "\r\n" + string.Join("\r\n", selected.Members.Select(m => m.Name + " · " + m.Model));
        var stats = _session is null ? "当前未发送音频。" : $"成员 {_session.MemberCount}；各成员包数：{string.Join(" / ", _session.MemberPackets)}；重传 {_session.Retransmits} 包。";
        return device + "\r\n\r\n" + stats + "\r\n\r\n" + string.Join("\r\n", _diagnosticLines);
    }
    public void ExportDiagnostics(string path)
    {
        try
        {
            var privateLabels = (_snapshot?.Receivers ?? []).SelectMany(r => new[] { r.Name, r.Id, r.TightSyncId, r.DynamicGroupId }).OfType<string>()
                .Concat(_activeEntry?.Members.Select(m => m.Name) ?? []).Append(_settings.SenderIdentity).Append(Environment.UserName);
            using var privacy = new DiagnosticRedactor(privateLabels, [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DesktopSettings.DirectoryPath]);
            string Redact(string text) => privacy.Redact(text);
            string Token(string id) => privacy.Token(id);
            using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None); using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            void Write(string name, string content) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8); writer.Write(content); }
            foreach (var name in new[] { "launcher-startup.log", "winui-startup.log", "winui-startup-error.txt" })
            {
                var startupPath = Path.Combine(DesktopSettings.DirectoryPath, name);
                try { if (File.Exists(startupPath) && new FileInfo(startupPath).Length <= 262144) Write("startup/" + name, Redact(File.ReadAllText(startupPath))); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            Write("session.log", string.Join(Environment.NewLine, _diagnosticLines.Select(Redact)));
            Write("events.log", string.Join(Environment.NewLine, _importantDiagnosticLines.Select(Redact)));
            var summary = new
            {
                Product = "WinToPod", Version = "1.1", Build = "V1.1", Stage = "Release", LogRetention = new { Entries = _diagnosticLines.Count, Limit = 2000, PreservedInitialEntries = 100, TrimmedEntries = _diagnosticTrimmedCount }, BuildLabel = Path.GetFileNameWithoutExtension((Environment.ProcessPath ?? "WinToPod.exe")), ConnectionRevision = WinToPod.AirPlay.Transport.AirPlayRequestProfile.Revision, AirPlayUserAgent = WinToPod.AirPlay.Transport.AirPlayRequestProfile.UserAgent, AutomaticConnection = new { Policy = _settings.Policy.ToString(), _settings.AutoConnectOnStart, SavedTarget = _settings.LastConnectedReceiver is not null, _automation.HasPending, _automation.CanAct, _automation.Attempts, InputMonitorAvailable = _localUse?.Registered == true, AcceptedLocalIntents = _localUse?.AcceptedIntents, RejectedInputIntents = _localUse?.RejectedIntents }, ExecutableName = Path.GetFileName((Environment.ProcessPath ?? "WinToPod.exe")), OS = Environment.OSVersion.VersionString, Runtime = Environment.Version.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Receivers = (_snapshot?.Receivers ?? []).Select(r => new { Token = Token(r.Id), r.Model, Fields = r.Properties.Where(p => new[] { "model", "features", "srcvers", "osvers", "protovers", "flags", "sf", "igl", "pw", "gcgl", "acl" }.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value), TightSyncLeader = ReceiverAccess.IsTightSyncLeader(r), Credentials = ReceiverAccess.Credentials(r.Properties).ToString(), HasTightSync = r.TightSyncId is not null, EndpointCount = r.Endpoints.Length }),
                Groups = (_snapshot?.GroupCandidates ?? []).Select(g => new { g.TwoHomePods, g.Status, Members = g.MemberIds.Select(Token) }),
                Selection = Selected is { } row ? new { Token = Token(row.Key), Primary = row.Primary is null ? null : Token(row.Primary.Id), HasEndpoint = row.HasIpv4Endpoint } : null,
                ConnectActionEnabled = _connect.Enabled, Working = _working, Packets = _session?.PacketsSent, Resends = _session?.Retransmits, InputPeak = _audio?.Peak, CapturePcmGain = 1.0, CaptureMode = _audio?.CaptureMode, CaptureFallback = _audio?.CaptureFallback, CaptureReceivedFrames = _audio?.ReceivedFrames, CaptureDiscontinuities = _audio?.Discontinuities, InputPeakDbfs = _audio is { Peak: > 0 } meter ? (double?)(20 * Math.Log10(meter.Peak)) : null,
                NegotiatedLatencyFrames = _session?.LatencyFrames, EstimatedLatencyMs = _session is null ? (int?)null : VideoSyncState.FromFrames(_session.LatencyFrames), AudioTimingForVideo = CurrentVideoState(), BrowserBridgeAvailable = _videoBridge is not null, BrowserCompanion = _videoBridge?.CompanionStatus ?? VideoCompanionStatus.Missing, CaptureLagEstimateMs = _audio?.EstimatedCaptureLagMs, InputBufferedFrames = _audio?.BufferedFrames, InputDroppedFrames = _audio?.DroppedFrames, InputMissingFrames = _audio?.MissingFrames,
                MemberPackets = _session?.MemberPackets, VolumeLinkActive = _volumeLink is not null, SettingsEnabled = !_exiting, VolumePercent = _volume.Value, ConfirmedPanelVolumePercent = _session is not null && _activeKey is { } ownedKey ? (int?)_settings.Volumes.GetValueOrDefault(ownedKey) : null, Appearance = _settings.WinUiAppearance.ToString(), VolumeUpdateUnconfirmed = _volumeUnconfirmed, VolumeCurve = "desktop-calibrated-v1", ReceiverVolumeDb = _session is not null && _activeKey is { } volumeKey ? (double?)ReceiverVolume.Decibels(_settings.Volumes.GetValueOrDefault(volumeKey), ReceiverVolumeCurve.DesktopCalibrated) : null, CaptureRequiresPreMaster = true, WindowsEndpointVolume = _volumeLink?.Snapshot(),
                PlaybackMode = _session is null ? "None" : _activeSingleFallback ? "SingleFallbackDualMono" : Selected?.IsGroup == true ? "NativePair" : "Single", PreferSingleForSelectedTarget = Selected is { } selectedTarget && _settings.SingleFallbackTargets.Contains(selectedTarget.Key),
                PersistedInterruption = _settings.Interrupted, InterruptionReason = _settings.Interrupted ? Redact(_settings.InterruptionReason ?? "") : null
            };
            Write("summary.json", JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true })); SetStatus("诊断已保存，不含密码、密钥或录音，不会自动上传。");
        }
        catch (Exception e) { ShowError(e); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed || _exiting) return;
        _exiting = true; _scanCancel?.Cancel(); await StopAsync();
        if (_scanTask is { } scan) await scan;
        _stats.Stop(); _volumeDebounce.Stop(); _volumePoll.Stop();
        _ui.CloseInterruption(); _localUse?.Dispose(); _localUse = null;
        DetachVolumeLink(); _videoBridge?.Dispose(); _videoPlayer?.Dispose();
        _stats.Dispose(); _volumeDebounce.Dispose(); _volumePoll.Dispose();
        _pairingStore.Dispose(); _playbackCredentials.Dispose(); _disposed = true;
    }
}
