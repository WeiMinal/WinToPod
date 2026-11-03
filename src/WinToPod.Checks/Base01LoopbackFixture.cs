// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using WinToPod.AirPlay.Media;
using WinToPod.AirPlay.Transport;

internal sealed class Base01TimeSource : MediaTimeSource
{
    private long _ticks;
    public Func<Task>? BeforeDelay { get; set; }
    public Action? Advanced { get; set; }
    public override long Frequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddSeconds(100);
    public override TimeSpan GetElapsedTime(long start, long end) => TimeSpan.FromTicks(end - start);
    public void Advance(TimeSpan value) { Interlocked.Add(ref _ticks, value.Ticks); Advanced?.Invoke(); }
    public override async Task DelayAsync(TimeSpan delay, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (BeforeDelay is { } pump) await pump();
        Advance(delay);
    }
}

// Shares virtual timestamps with the send loop. PTP background timers remain
// parked until cancelled; tracker observations are explicitly driven by the test.
// Read(), readiness, offset filtering and the audio loop are production code.
internal sealed class Base01PtpTimeSource(Base01TimeSource time) : MediaTimeSource
{
    public override long Frequency => time.Frequency;
    public override long GetTimestamp() => time.GetTimestamp();
    public override DateTimeOffset UtcNow => time.UtcNow;
    public override TimeSpan GetElapsedTime(long start, long end) => time.GetElapsedTime(start, end);
    public override async Task DelayAsync(TimeSpan delay, CancellationToken cancellation)
    {
        var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellation.Register(() => wait.TrySetCanceled(cancellation));
        await wait.Task;
    }
}

// Synthetic prepared session, following the existing volume check's reflection
// fixture pattern. No authentication/capture is claimed. Real ALAC, encryption,
// RunMembersAsync, RTP and Sync datagrams run over loopback UDP without a mock sender.
internal sealed class Base01LoopbackFixture : IAsyncDisposable
{
    private readonly UdpClient _data = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient _control = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly TcpClient _peer;
    private ushort _ptpSequence;
    private long _lastPtpTicks;
    private readonly int _ppm;
    private uint? _previousTimestamp;
    private ushort _previousSequence;
    private const long EpochNanoseconds = 100_000_000_000;
    public const uint InitialTimestamp = 0xfffff000;
    public RealtimeAudioSession Session { get; }
    public Base01TimeSource Time { get; }
    public PtpClockEngine? Ptp { get; private set; }
    public PtpClockTracker? Tracker { get; private set; }
    public List<string> Trace { get; } = [];
    public long ReceivedPackets { get; private set; }
    public long SyncPackets { get; private set; }
    public Action<uint, uint, long>? OnPtpSync { get; set; }

    private Base01LoopbackFixture(RtspConnection connection, TcpClient peer, Base01TimeSource time, int ppm, uint latency)
    {
        _peer = peer; Time = time; _ppm = ppm;
        var ctor = typeof(RealtimeAudioSession).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        Session = (RealtimeAudioSession)ctor.Invoke([connection,
            new AudioSessionOptions(IPAddress.Loopback, 1, IPAddress.Loopback, null, 20) { TimeSource = time },
            (Action<string>)Trace.Add, null]);
        Set("_configured", true); Set("_volumeConfirmed", true);
        Set("_timestamp", InitialTimestamp); Set("_latency", latency);
        Set("_cipher", new AudioPacketCipher(new byte[32]));
        Set("_dataEndpoint", _data.Client.LocalEndPoint!); Set("_controlEndpoint", _control.Client.LocalEndPoint!);
        Time.BeforeDelay = DrainAsync;
    }
    public static async Task<Base01LoopbackFixture> OpenAsync(bool ptp = false, int ppm = 0, uint latency = 88200)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var accept = listener.AcceptTcpClientAsync();
        var connection = await RtspConnection.ConnectAsync(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, IPAddress.Loopback, default);
        var fixture = new Base01LoopbackFixture(connection, await accept, new(), ppm, latency);
        try { if (ptp) fixture.StartPtp(); return fixture; }
        catch { await fixture.DisposeAsync(); throw; }
    }
    private void Set(string name, object value)
        => typeof(RealtimeAudioSession).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Session, value);
    private void StartPtp()
    {
        int eventPort, generalPort;
        using (var events = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var general = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        { eventPort = ((IPEndPoint)events.Client.LocalEndPoint!).Port; generalPort = ((IPEndPoint)general.Client.LocalEndPoint!).Port; }
        Ptp = new(new Base01PtpTimeSource(Time), IPAddress.Loopback, IPAddress.Parse("127.0.0.2"), [], 66,
            true, Trace.Add, _ => { }, new(eventPort, generalPort));
        Set("_ptp", Ptp);
        Tracker = (PtpClockTracker)typeof(PtpClockEngine).GetField("_tracker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Ptp)!;
        Tracker.Observe(PtpWire.Announce(55, 0), EpochNanoseconds - 3_000_000_000);
        // Warm up at zero offset. The rate change starts at virtual t=0.
        for (var i = 0; i <= 24; i++)
        {
            var ns = EpochNanoseconds - 3_000_000_000 + i * 125_000_000L;
            Observe(Tracker, _ptpSequence++, ns, ns);
        }
        Time.Advanced = () =>
        {
            var ticks = Time.GetTimestamp();
            if (ticks - _lastPtpTicks < TimeSpan.TicksPerSecond / 8) return;
            _lastPtpTicks = ticks;
            var local = EpochNanoseconds + ticks * 100;
            var remote = EpochNanoseconds + (long)((Int128)ticks * 100 * 1_000_000 / (1_000_000 + _ppm));
            Observe(Tracker, _ptpSequence++, local, remote);
        };
    }
    internal static void Observe(PtpClockTracker tracker, ushort sequence, long local, long remote)
    {
        var sync = PtpWire.Header(0, 44, 55, sequence); // valid one-step sample
        PtpWire.Timestamp(sync.AsSpan(34), remote); tracker.Observe(sync, local);
    }
    public async Task DrainAsync()
    {
        // Pump after each positive virtual delay. For the stall checks, a bounded
        // catch-up burst can queue multiple packets; read all of them, in order.
        uint? last = null;
        while (_data.Available > 0)
        {
            var packet = (await _data.ReceiveAsync()).Buffer;
            var timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));
            var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
            if (_previousTimestamp is { } previous && (timestamp != unchecked(previous + 352) || sequence != unchecked((ushort)(_previousSequence + 1))))
                throw new Exception("BASE-01 回环接收发生丢包或时间戳不连续。");
            _previousTimestamp = timestamp; _previousSequence = sequence; ReceivedPackets++; last = timestamp;
        }
        while (_control.Available > 0)
        {
            var packet = (await _control.ReceiveAsync()).Buffer; SyncPackets++;
            if (packet[1] == 0xd7)
            {
                if (last is null) throw new Exception("BASE-01 Sync没有对应的RTP包。");
                var position = unchecked(BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)) - 11035);
                var ns = checked((long)BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(8)));
                OnPtpSync?.Invoke(last.Value, position, ns - EpochNanoseconds);
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Time.Advanced = null; Time.BeforeDelay = null;
        await Session.CloseAsync(false); _peer.Dispose(); _data.Dispose(); _control.Dispose();
    }
}
