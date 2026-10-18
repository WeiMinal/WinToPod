// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WinToPod.AirPlay.Media;

// One manually acquired logical target. Only the selected receiver and its
// known native-pair members are timing peers. No multicast clock takeover.
public sealed class PtpClockEngine : IAsyncDisposable
{
    private readonly UdpClient _events, _general;
    private readonly IPAddress _primary;
    private readonly IPAddress[] _peers;
    private readonly ulong _clockId;
    private readonly int _eventPort, _generalPort;
    private readonly PtpClockTracker _tracker;
    private readonly Dictionary<IPAddress, PtpClockTracker> _required = [];
    private readonly NetworkClock _clock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<string>? _diagnostic;
    private readonly Action<Exception> _failed;
    private readonly Task[] _workers;
    private long _received, _sent, _probes, _syncs;
    private Exception? _failure;
    private volatile bool _wasReady;
    private long _lastReport;

    public PtpClockEngine(IPAddress local, IPAddress primary, IEnumerable<IPAddress> peers, ulong clockId,
        bool followReceiver, Action<string>? diagnostic, Action<Exception> failed, PtpTestPorts? testPorts = null,
        IEnumerable<IPAddress>? requiredPeers = null)
    {
        _primary = primary; _peers = peers.Append(primary).Where(x => !x.Equals(local)).Distinct().Take(8).ToArray();
        _clockId = clockId; _tracker = new(clockId, followReceiver); _diagnostic = diagnostic; _failed = failed;
        foreach (var peer in requiredPeers ?? [])
            if (!_peers.Contains(peer) || followReceiver) throw new ArgumentException("Invalid required clock member.");
            else _required[peer] = peer.Equals(primary) ? _tracker : new(clockId, false);
        if (testPorts is not null && (!IPAddress.IsLoopback(local) || !IPAddress.IsLoopback(primary) || _peers.Any(x => !IPAddress.IsLoopback(x)) ||
            testPorts.Event is < 1024 or > 65535 || testPorts.General is < 1024 or > 65535 || testPorts.Event == testPorts.General))
            throw new ArgumentException("Relocated PTP ports are restricted to loopback tests.");
        _eventPort = testPorts?.Event ?? 319; _generalPort = testPorts?.General ?? 320;
        UdpClient? events = null;
        try { events = Bind(local, _eventPort); _general = Bind(local, _generalPort); _events = events; }
        catch (SocketException e)
        { events?.Dispose(); _stop.Dispose(); throw new IOException($"PTP时钟端口{_eventPort}/{_generalPort}无法使用（{e.SocketErrorCode}）。请退出其他投音频程序后手动重试。", e); }
        _diagnostic?.Invoke($"PTP启动：UDP{_eventPort}/{_generalPort}；角色=" + (followReceiver ? "跟随单只音箱时钟" : "提供会话时钟") + $"；成员数={_peers.Length}；仅单播到所选目标。");
        _workers = [ReceiveAsync(_events, true), ReceiveAsync(_general, false), TransmitAsync()];
    }
    private static UdpClient Bind(IPAddress local, int port)
    {
        var socket = new UdpClient(AddressFamily.InterNetwork);
        try { socket.ExclusiveAddressUse = true; socket.Client.Bind(new IPEndPoint(local, port)); return socket; }
        catch { socket.Dispose(); throw; }
    }
    public (ulong ClockId, long TimeNanoseconds) Read()
    {
        var state = _tracker.Snapshot(_clock.UnixNanoseconds);
        if (_failure is not null) throw new IOException("PTP时钟已停止，音频连接结束。", _failure);
        if (!state.Ready || _required.Values.Any(t => !t.Snapshot(_clock.UnixNanoseconds).Ready))
        {
            var error = new IOException("PTP时钟同步丢失，已停止发送，不自动重新连接。");
            Fail(error); throw error;
        }
        return (state.ClockId, state.TimeNanoseconds);
    }
    public async Task WaitUntilReadyAsync(CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(9))
        {
            cancellation.ThrowIfCancellationRequested();
            if (_failure is not null) throw new IOException("PTP时钟通道失败。", _failure);
            var state = _tracker.Snapshot(_clock.UnixNanoseconds);
            if (state.Ready && _required.Values.All(t => t.Snapshot(_clock.UnixNanoseconds).Ready)) { _wasReady = true; _diagnostic?.Invoke($"PTP时钟就绪：samples={state.Samples}；全部成员就绪；尚未发送音频。"); return; }
            await Task.Delay(100, cancellation).ConfigureAwait(false);
        }
        Report(); throw new IOException("PTP时钟尚未同步，已停止连接且未发送音频。请导出诊断查看时钟收发情况。");
    }
    private async Task ReceiveAsync(UdpClient socket, bool eventPort)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await socket.ReceiveAsync(_stop.Token).ConfigureAwait(false); var received = _clock.UnixNanoseconds;
                var packet = message.Buffer;
                if (!_peers.Contains(message.RemoteEndPoint.Address) || message.RemoteEndPoint.Port != (eventPort ? _eventPort : _generalPort) || !PtpWire.IsValid(packet)) continue;
                var type = packet[0] & 15;
                if (eventPort != (type is 0 or 1 or 2 or 3)) continue;
                Interlocked.Increment(ref _received);
                if (message.RemoteEndPoint.Address.Equals(_primary)) _tracker.Observe(packet, received);
                else if (_required.TryGetValue(message.RemoteEndPoint.Address, out var memberClock)) memberClock.Observe(packet, received);
                if (type is 1 or 2)
                {
                    Interlocked.Increment(ref _probes);
                    var reply = PtpWire.DelayReply(packet, _clockId, received);
                    await SendAsync(type == 2 ? _events : _general, message.RemoteEndPoint.Address, type == 2 ? _eventPort : _generalPort, reply).ConfigureAwait(false);
                    if (type == 2) await SendAsync(_general, message.RemoteEndPoint.Address, _generalPort,
                        PtpWire.DelayReply(packet, _clockId, _clock.UnixNanoseconds, true)).ConfigureAwait(false);
                }
                else if (type == 0) Interlocked.Increment(ref _syncs);
                else if (type == 12 && !_tracker.FollowReceiver)
                    foreach (var grant in PtpWire.Grants(packet, _clockId)) await SendAsync(_general, message.RemoteEndPoint.Address, _generalPort, grant).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) { Fail(e); }
    }
    private async Task TransmitAsync()
    {
        try
        {
            ushort sequence = 0, announce = 0; var next = Stopwatch.GetTimestamp(); var ticks = 0;
            while (!_stop.IsCancellationRequested)
            {
                if (!_tracker.FollowReceiver)
                {
                    foreach (var peer in _peers)
                    {
                        if (ticks % 8 == 0) await SendAsync(_general, peer, _generalPort, PtpWire.Announce(_clockId, announce)).ConfigureAwait(false);
                        await SendAsync(_events, peer, _eventPort, PtpWire.Sync(_clockId, sequence)).ConfigureAwait(false);
                        await SendAsync(_general, peer, _generalPort, PtpWire.FollowUp(_clockId, sequence, _clock.UnixNanoseconds)).ConfigureAwait(false);
                    }
                }
                if (_wasReady && (!_tracker.Snapshot(_clock.UnixNanoseconds).Ready || _required.Values.Any(t => !t.Snapshot(_clock.UnixNanoseconds).Ready))) throw new IOException("PTP时钟数据中断或时间线变化。");
                if (ticks % 8 == 0) { announce++; Report(); }
                sequence++; ticks++;
                next += Stopwatch.Frequency / 8;
                var delay = TimeSpan.FromSeconds((next - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                else next = Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) { Fail(e); }
    }
    private async Task SendAsync(UdpClient socket, IPAddress address, int port, byte[] payload)
    { await socket.SendAsync(payload, new IPEndPoint(address, port), _stop.Token).ConfigureAwait(false); Interlocked.Increment(ref _sent); }
    private void Report()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastReport != 0 && Stopwatch.GetElapsedTime(_lastReport, now) < TimeSpan.FromSeconds(30) && !_stop.IsCancellationRequested) return;
        _lastReport = now;
        var state = _tracker.Snapshot(_clock.UnixNanoseconds);
        _diagnostic?.Invoke($"PTP状态：tx={Interlocked.Read(ref _sent)}；rx={Interlocked.Read(ref _received)}；delayRequests={Interlocked.Read(ref _probes)}；syncs={Interlocked.Read(ref _syncs)}；samples={state.Samples}；ready={state.Ready}。");
    }
    private void Fail(Exception error)
    {
        if (_stop.IsCancellationRequested || Interlocked.CompareExchange(ref _failure, error, null) is not null) return;
        var failure = new SessionInterruption("clock", error); _diagnostic?.Invoke(failure.SafeDiagnostic);
        _stop.Cancel(); _failed(failure);
    }
    public async ValueTask DisposeAsync()
    { await _stop.CancelAsync().ConfigureAwait(false); await Task.WhenAll(_workers).ConfigureAwait(false); Report(); _events.Dispose(); _general.Dispose(); _stop.Dispose(); }
}
