// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using WinToPod.AirPlay.Media;
using WinToPod.AirPlay.Transport;

// Production RTSP + session volume methods against a small TCP receiver.
// Authentication, PTP, RTP and Windows capture are outside this boundary test.
internal static class VolumeConfirmationChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("音量确认：重放51→50与50→49旧值读回，不重发SET", async () =>
        {
            await using var f = await Fixture.OpenAsync();
            True(await f.Session.SetVolumeAndConfirmAsync(51, default));
            f.Peer.Readbacks.Enqueue(Db(51));
            True(await f.Session.SetVolumeAndConfirmAsync(50, default));
            f.Peer.Readbacks.Enqueue(Db(50));
            True(await f.Session.SetVolumeAndConfirmAsync(49, default));
            True(f.Session.LastConfirmedVolume == 49 && f.Peer.Sets == 3 && f.Peer.Gets == 5);
            True(!f.Trace.Any(l => l.Contains("会话故障")));
        });
        yield return ("音量确认：持续旧值最多四读，保留会话及历史，之后仍可调节", async () =>
        {
            await using var f = await Fixture.OpenAsync();
            True(await f.Session.SetVolumeAndConfirmAsync(51, default));
            f.Peer.FixedReadback = Db(51);
            True(!await f.Session.SetVolumeAndConfirmAsync(50, default));
            True(f.Peer.Sets == 2 && f.Peer.Gets == 5 && f.Session.LastConfirmedVolume == 51);
            True(await f.Session.ReadVolumeAsync(default) == 51);
            True(!f.Trace.Any(l => l.Contains("会话故障")));
            f.Peer.FixedReadback = null;
            True(await f.Session.SetVolumeAndConfirmAsync(49, default));
            True(await f.Session.ReadVolumeAsync(default) == 49);
        });
        yield return ("音量确认：首次未确认不伪造可播放状态", async () =>
        {
            await using var f = await Fixture.OpenAsync(); f.Peer.FixedReadback = Db(10);
            True(!await f.Session.SetVolumeAndConfirmAsync(50, default));
            True(f.Session.LastConfirmedVolume is null && f.Peer.Gets == 4 && f.Peer.Sets == 1);
        });
        yield return ("音量确认：未确认的取消静音保留静音，成功确认后恢复", async () =>
        {
            await using var f = await Fixture.OpenAsync();
            True(await f.Session.SetVolumeAndConfirmAsync(51, default));
            f.Peer.FixedReadback = Db(51);
            True(await f.Session.SetVolumeAndConfirmAsync(0, default));
            True(await f.Session.ReadVolumeAsync(default) == 0);
            True(!await f.Session.SetVolumeAndConfirmAsync(50, default));
            True(f.Session.LastConfirmedVolume == 0 && await f.Session.ReadVolumeAsync(default) == 0);
            f.Peer.FixedReadback = null;
            True(await f.Session.SetVolumeAndConfirmAsync(50, default));
            True(await f.Session.ReadVolumeAsync(default) == 50);
        });
        yield return ("音量确认：非法响应立即停止且不循环读取", async () =>
        {
            foreach (var invalid in new[] { "NaN", "-200", "garbage" })
            {
                await using var f = await Fixture.OpenAsync(); f.Peer.FixedReadback = invalid;
                await Throws<InvalidDataException>(() => f.Session.SetVolumeAndConfirmAsync(50, default));
                True(f.Peer.Gets == 1 && f.Peer.Sets == 1);
                True(!await f.Session.SetVolumeAndConfirmAsync(49, default));
                True(f.Peer.Sets == 1);
            }
        });
        yield return ("音量确认：EOF立即停止，失去连接后不再发送控制", async () =>
        {
            await using var f = await Fixture.OpenAsync(); f.Peer.CloseOnGet = true;
            await Throws<EndOfStreamException>(() => f.Session.SetVolumeAndConfirmAsync(50, default));
            await Throws<IOException>(() => f.Session.ReadVolumeAsync(default));
            True(f.Peer.Gets == 1 && f.Peer.Sets == 1);
        });
        yield return ("音量确认：RTSP拒绝立即停止，不用读回重试掩盖拒绝", async () =>
        {
            await using var f = await Fixture.OpenAsync(); f.Peer.GetStatus = 403;
            await Throws<RtspRejectedException>(() => f.Session.SetVolumeAndConfirmAsync(50, default));
            True(!await f.Session.SetVolumeAndConfirmAsync(49, default));
            True(f.Peer.Gets == 1 && f.Peer.Sets == 1);
        });
        yield return ("音量确认：等待期间取消，不追加GET或损坏健康会话", async () =>
        {
            using var cancel = new CancellationTokenSource();
            await using var f = await Fixture.OpenAsync(line => { if (line.Contains("accepted=False")) cancel.Cancel(); });
            f.Peer.Readbacks.Enqueue(Db(51));
            await Throws<OperationCanceledException>(() => f.Session.SetVolumeAndConfirmAsync(50, cancel.Token));
            True(f.Peer.Gets == 1 && f.Peer.Sets == 1);
            True(await f.Session.SetVolumeAndConfirmAsync(49, default));
        });
    }
    private static string Db(int percent) => ReceiverVolume.Decibels(percent, ReceiverVolumeCurve.DesktopCalibrated).ToString("F6", CultureInfo.InvariantCulture);
    private static void True(bool value) { if (!value) throw new Exception("Volume confirmation invariant failed."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    private sealed class Fixture(VolumePeer peer, RealtimeAudioSession session, List<string> trace) : IAsyncDisposable
    {
        public VolumePeer Peer => peer;
        public RealtimeAudioSession Session => session;
        public List<string> Trace => trace;
        public static async Task<Fixture> OpenAsync(Action<string>? diagnostic = null)
        {
            var peer = new VolumePeer();
            var connection = await RtspConnection.ConnectAsync(IPAddress.Loopback, peer.Port, IPAddress.Loopback, default);
            var trace = new List<string>();
            Action<string> log = line => { trace.Add(line); diagnostic?.Invoke(line); };
            // Inject only the socket boundary; the production confirmation,
            // parsing, failure state and readback paths run without substitutes.
            var ctor = typeof(RealtimeAudioSession).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            var session = (RealtimeAudioSession)ctor.Invoke([connection,
                new AudioSessionOptions(IPAddress.Loopback, peer.Port, IPAddress.Loopback, null, 20, VolumeCurve: ReceiverVolumeCurve.DesktopCalibrated), log, null]);
            typeof(RealtimeAudioSession).GetField("_url", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, "rtsp://127.0.0.1/session");
            return new(peer, session, trace);
        }
        public async ValueTask DisposeAsync() { await session.CloseAsync(false); await peer.DisposeAsync(); }
    }
    private sealed class VolumePeer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public readonly Queue<string> Readbacks = new();
        public string? FixedReadback;
        public bool CloseOnGet;
        public int GetStatus = 200, Sets, Gets;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public VolumePeer() { _listener.Start(); _loop = RunAsync(); }
        private async Task RunAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token); using var stream = client.GetStream();
                var requested = "-24.000000";
                while (!_stop.IsCancellationRequested)
                {
                    var header = new List<byte>(); var one = new byte[1];
                    while (header.Count < 4 || !header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 }))
                    { await stream.ReadExactlyAsync(one, _stop.Token); header.Add(one[0]); }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                    var fields = lines.Skip(1).Where(l => l.Contains(':')).ToDictionary(l => l[..l.IndexOf(':')], l => l[(l.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
                    var body = new byte[int.Parse(fields.GetValueOrDefault("Content-Length", "0"), CultureInfo.InvariantCulture)];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    var status = 200; var reply = "";
                    if (lines[0].StartsWith("SET_PARAMETER ", StringComparison.Ordinal))
                    { Sets++; requested = Encoding.ASCII.GetString(body).Split(':')[1].Trim(); }
                    else if (lines[0].StartsWith("GET_PARAMETER ", StringComparison.Ordinal))
                    { Gets++; if (CloseOnGet) return; status = GetStatus; reply = "volume: " + (Readbacks.Count > 0 ? Readbacks.Dequeue() : FixedReadback ?? requested) + "\r\n"; }
                    else throw new Exception("Unexpected volume-test method.");
                    var bytes = Encoding.ASCII.GetBytes(reply);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"RTSP/1.0 {status} Test\r\nCSeq: {fields["CSeq"]}\r\nContent-Length: {bytes.Length}\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch (Exception e) when (e is EndOfStreamException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _loop; _stop.Dispose(); }
    }
}
