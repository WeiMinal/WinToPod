// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using WinToPod.AirPlay.Media;
using WinToPod.Core;

internal static class MetadataChecks
{
    private static void Require(bool test, string message) { if (!test) throw new Exception(message); }
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("V1.2｜曲目信息UTF8与有界DMAP", () =>
        {
            var encoded = MetadataPublisher.EncodeText(new("曲目🎵", "作者", "专辑"));
            Require(Encoding.ASCII.GetString(encoded, 0, 4) == "mlit" && BinaryPrimitives.ReadInt32BigEndian(encoded.AsSpan(4)) == encoded.Length - 8, "Container length.");
            Require(Encoding.ASCII.GetString(encoded, 8, 4) == "minm" && BinaryPrimitives.ReadInt32BigEndian(encoded.AsSpan(12)) == Encoding.UTF8.GetByteCount("曲目🎵"), "UTF8 length.");
            var bounded = MetadataPublisher.EncodeText(new(string.Concat(Enumerable.Repeat("🎵", 900)), "", ""));
            Require(BinaryPrimitives.ReadInt32BigEndian(bounded.AsSpan(12)) == 2048, "Rune bound.");
            Require(MetadataPublisher.EncodeText(null).Length == 32, "Clear fields.");
            Require(Encoding.ASCII.GetString(MetadataPublisher.Progress(new("", "", "", Position: TimeSpan.FromSeconds(1), Duration: TimeSpan.FromSeconds(2)), 10)) == "progress: 4294923206/10/44110\r\n", "RTP wrap.");
            Require(Encoding.ASCII.GetString(MetadataPublisher.Progress(new("", "", "", Duration: TimeSpan.FromDays(3)), 5)) == "progress: 0/0/0\r\n", "Unknown progress must clear.");
            return Task.CompletedTask;
        });
        yield return ("V1.2｜媒体能力门控、去重和拒绝降级", async () =>
        {
            var publisher = new MetadataPublisher(); var calls = new List<string>();
            Task<int> Send(string type, byte[] bytes, uint stamp, CancellationToken token) { calls.Add(type); return Task.FromResult(type == "image/jpeg" ? 415 : 200); }
            var value = new PlaybackMetadata("title", "artist", "album", [0xff, 0xd8, 0xff, 0xd9]);
            await publisher.PublishAsync(value, default, 100, Send, default); Require(calls.Count == 0, "Unknown capability sends.");
            var all = new ReceiverCapabilities(true, true, true, true, true);
            await publisher.PublishAsync(value, all, 100, Send, default);
            await publisher.PublishAsync(value, all, 100, Send, default); Require(calls.Count == 3, "Repeated/unsupported metadata retried.");
            await publisher.PublishAsync(value with { Title = "changed" }, all, 200, Send, default); Require(calls.Count == 4, "Text updates after art refusal.");
            await publisher.PublishAsync(null, all, 200, Send, default); Require(calls.Count == 5, "Clear lifecycle.");
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await publisher.PublishAsync(value, all, 1, Send, stop.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
            Require(calls.Count == 5, "Cancelled publish sends.");
            try { await publisher.PublishAsync(value with { Jpeg = new byte[MetadataPublisher.MaximumArtworkBytes + 1] }, all, 1, Send, default); throw new Exception("Art limit ignored."); } catch (ArgumentException) { }
        });
        yield return ("V1.2｜媒体时间快照推进与无效时序", () =>
        {
            var epoch = DateTimeOffset.UnixEpoch;
            Require(MediaTimeline.Position(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60), epoch, epoch.AddSeconds(5), 2) == TimeSpan.FromSeconds(20), "Timestamp/rate ignored.");
            Require(MediaTimeline.Position(TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(60), epoch, epoch.AddSeconds(10), 1) == TimeSpan.FromSeconds(60), "Position exceeds end.");
            foreach (var rate in new double?[] { null, 0, -1, double.NaN, double.PositiveInfinity })
                Require(MediaTimeline.Position(TimeSpan.Zero, TimeSpan.FromSeconds(60), epoch, epoch, rate) is null, "Invalid rate invented progress.");
            Require(MediaTimeline.Position(TimeSpan.Zero, TimeSpan.FromSeconds(60), epoch, epoch.AddSeconds(-1), 1) is null, "Future snapshot accepted.");
            Require(MediaTimeline.Position(TimeSpan.Zero, TimeSpan.FromSeconds(60), epoch, epoch.AddDays(2), 1) is null, "Stale snapshot accepted.");
            return Task.CompletedTask;
        });
        yield return ("V1.2｜真实RTSP媒体发送、清空与隐私", () => WireAsync(false));
        yield return ("V1.2｜媒体控制拒绝后不再发送", () => WireAsync(true));
    }

    private static async Task WireAsync(bool reject)
    {
        await using var f = await Base01LoopbackFixture.OpenAsync();
        typeof(RealtimeAudioSession).GetProperty("DeviceInfo")!.SetValue(f.Session,
            new Dictionary<string, object?> { ["features"] = (1UL << 17) | (1UL << 15) | (1UL << 16) });
        typeof(RealtimeAudioSession).GetField("_url", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f.Session, "rtsp://127.0.0.1/test");
        var peer = (TcpClient)typeof(Base01LoopbackFixture).GetField("_peer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<(string Header, byte[] Body)>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < (reject ? 1 : 6); i++)
            {
                using var header = new MemoryStream(); var one = new byte[1];
                while (true)
                {
                    await peer.GetStream().ReadExactlyAsync(one, timeout.Token); header.WriteByte(one[0]);
                    if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
                }
                var text = Encoding.UTF8.GetString(header.ToArray());
                var lines = text.Split("\r\n");
                var length = int.Parse(lines.Single(x => x.StartsWith("Content-Length:", StringComparison.Ordinal)).Split(':')[1]);
                var body = new byte[length]; await peer.GetStream().ReadExactlyAsync(body, timeout.Token); received.Add((text, body));
                var sequence = lines.Single(x => x.StartsWith("CSeq:", StringComparison.Ordinal));
                await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"RTSP/1.0 {(reject ? 403 : 200)} Result\r\n{sequence}\r\nContent-Length: 0\r\n\r\n"), timeout.Token);
            }
        });
        var value = new PlaybackMetadata("private-title", "private-artist", "private-album", [0xff, 0xd8, 0xff, 0xd9], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        if (reject)
        {
            for (var i = 0; i < 2; i++)
            {
                try { await f.Session.PublishMetadataAsync(value, timeout.Token); throw new Exception("403 ignored."); } catch (IOException) { }
            }
        }
        else
        {
            await f.Session.PublishMetadataAsync(value, timeout.Token);
            await f.Session.PublishMetadataAsync(value, timeout.Token);
            await f.Session.PublishMetadataAsync(null, timeout.Token);
        }
        await server;
        Require(received.Count == (reject ? 1 : 6), "Wrong request count.");
        Require(received[0].Header.Contains("RTP-Info: rtptime=4294963200", StringComparison.Ordinal), "Metadata timing missing.");
        if (!reject)
        {
            Require(received[0].Body.SequenceEqual(MetadataPublisher.EncodeText(value)), "Wrong DMAP bytes.");
            Require(received[1].Body.SequenceEqual(value.Jpeg!), "Wrong artwork bytes.");
            Require(received[4].Header.Contains("image/none", StringComparison.Ordinal) && received[4].Body.Length == 0, "Art not cleared.");
        }
        Require(!string.Join('\n', f.Trace).Contains("private-", StringComparison.Ordinal), "Metadata leaked into diagnostics.");
    }
}
