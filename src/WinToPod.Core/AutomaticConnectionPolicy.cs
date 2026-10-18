// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.Core;

public enum AutomaticConnectionReason { Startup, Reclaim }
public sealed record AutomaticConnectionRequest(string TargetId, AutomaticConnectionReason Reason);

// Pure, clock-injected decision layer. No discovery, pairing or takeover probe.
// Local input is a short-lived prerequisite, never proof that a speaker is idle.
public sealed class AutomaticConnectionPolicy
{
    public static readonly TimeSpan LocalIntentLifetime = TimeSpan.FromSeconds(90);
    private readonly TimeProvider _time;
    private DateTimeOffset? _localInput;
    private DateTimeOffset _due;
    private int _retries;
    private bool _startupPending, _reclaimPending, _paused, _manualRequired, _inFlight;
    private string? _target;
    private AutomaticConnectionReason _lastReason;
    private DateTimeOffset _startupNotBefore;
    public ConnectionPolicy Mode { get; private set; }
    public bool AutomaticStartup { get; private set; }
    public bool SessionAllowed { get; private set; }
    public int Attempts => _retries;
    public bool RetryBudgetExhausted => _retries >= 3;
    public bool HasPending => !_paused && !_manualRequired && (_startupPending || _reclaimPending);
    public bool CanAct => SessionAllowed && _localInput is { } input && _time.GetUtcNow() >= input && _time.GetUtcNow() - input <= LocalIntentLifetime;
    public AutomaticConnectionReason? InFlightReason => _inFlight ? _lastReason : null;
    public AutomaticConnectionPolicy(ConnectionPolicy mode, bool automaticStartup, string? target, TimeProvider? time = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        _time = time ?? TimeProvider.System; Mode = mode; AutomaticStartup = automaticStartup;
        _target = target; _startupPending = automaticStartup && !string.IsNullOrWhiteSpace(target);
    }
    public void ObserveSession(bool localAndUnlocked)
    {
        if (SessionAllowed != localAndUnlocked || !localAndUnlocked) _localInput = null;
        SessionAllowed = localAndUnlocked;
    }
    public void ObserveLocalIntent()
    {
        if (!SessionAllowed) return;
        if (_localInput is null) _startupNotBefore = _time.GetUtcNow().AddSeconds(2);
        _localInput = _time.GetUtcNow();
    }
    public void ObserveUntrustedIntent() => _localInput = null;
    public void Configure(ConnectionPolicy mode, bool startup)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Mode = mode; AutomaticStartup = startup;
        if (!startup) _startupPending = false;
        // Changing a radio button is not a fresh request to seize the speaker.
        if (mode == ConnectionPolicy.Conservative) _reclaimPending = false;
    }
    public void ManualConnect()
    { _startupPending = _reclaimPending = _inFlight = _paused = _manualRequired = false; _retries = 0; }
    public void Pause()
    { _startupPending = _reclaimPending = _inFlight = false; _paused = true; }
    public void RequireManual()
    { _startupPending = _reclaimPending = _inFlight = false; _manualRequired = true; }
    public void Connected(string target)
    { _target = target; _startupPending = _reclaimPending = _inFlight = false; }
    public void Interrupted(string target)
    {
        _target = target; _startupPending = _inFlight = false;
        _reclaimPending = Mode == ConnectionPolicy.Aggressive && !_paused && !_manualRequired && _retries < 3;
        _due = _time.GetUtcNow() + Delay(_retries);
    }
    public void DeferredUntil(DateTimeOffset due) { if (due > _due) _due = due; }
    public AutomaticConnectionRequest? TryBegin(bool ready, bool targetAvailable)
    {
        if (!ready || !targetAvailable || !HasPending || !CanAct || _inFlight || _time.GetUtcNow() < _due || _startupPending && _time.GetUtcNow() < _startupNotBefore) return null;
        var reason = _startupPending ? AutomaticConnectionReason.Startup : AutomaticConnectionReason.Reclaim;
        if (reason == AutomaticConnectionReason.Reclaim && (Mode != ConnectionPolicy.Aggressive || _retries >= 3)) return null;
        _inFlight = true; _startupPending = _reclaimPending = false;
        _lastReason = reason; if (reason == AutomaticConnectionReason.Reclaim) _retries++;
        return new(_target!, reason);
    }
    public void AttemptFailed(bool requiresManual, DateTimeOffset? receiverRetryAfter = null)
    {
        _inFlight = false;
        if (requiresManual) { RequireManual(); return; }
        if (_lastReason == AutomaticConnectionReason.Startup) _retries++;
        _reclaimPending = Mode == ConnectionPolicy.Aggressive && !_paused && _retries < 3;
        _due = _time.GetUtcNow() + Delay(_retries);
        if (receiverRetryAfter is { } wait) DeferredUntil(wait);
    }
    public void AttemptCancelled()
    {
        if (!_inFlight) return; _inFlight = false;
        if (_paused || _manualRequired) return;
        if (_lastReason == AutomaticConnectionReason.Startup) { if (AutomaticStartup) _startupPending = true; }
        else if (Mode == ConnectionPolicy.Aggressive && _retries < 3) _reclaimPending = true;
    }
    public string Description => _paused ? "本次自动连接已暂停。需要时可手动连接。" : _manualRequired ? "需要手动连接并完成验证。" :
        !HasPending ? RetryBudgetExhausted ? "自动重试次数已用完。需要时请手动连接。" : "" : !SessionAllowed ? "远程、锁屏或会话状态不明，自动连接已暂停。" : !CanAct ? "等待本机键盘或鼠标操作，再连接上次音箱。" :
        _time.GetUtcNow() < _due ? "已准备自动重连，正在遵守音箱等待时间。" : "正在准备连接上次音箱。";
    private static TimeSpan Delay(int attempts) => TimeSpan.FromSeconds(attempts switch { 0 => 10, 1 => 30, _ => 90 });
}
