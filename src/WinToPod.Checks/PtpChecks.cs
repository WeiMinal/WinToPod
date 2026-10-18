// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using WinToPod.AirPlay.Media;

internal static class PtpChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return Check("时钟选择：实时features优先，PTP位41决定协议，缺失保留NTP", () =>
        {
            var fields = new Dictionary<string, string?> { ["features"] = "0x4A7FCA00,0x3C354BD0" };
            True(ReceiverTiming.UsesPtp(fields, null));
            True(!ReceiverTiming.UsesPtp(fields, new() { ["features"] = 0UL }));
            True(!ReceiverTiming.UsesPtp(new Dictionary<string, string?>(), null));
        });
        yield return Check("时钟角色：单只OS27跟随音箱，原生对及Apple TV组保留会话主时钟", () =>
        {
            var fields = new Dictionary<string, string?> { ["model"] = "AudioAccessory5,1", ["osvers"] = "27.0", ["igl"] = "1" };
            True(ReceiverTiming.FollowsReceiver(fields, null));
            fields["tsid"] = "synthetic-pair"; True(!ReceiverTiming.FollowsReceiver(fields, null));
            fields.Remove("tsid"); fields["pgid"] = "synthetic-tv-group"; True(!ReceiverTiming.FollowsReceiver(fields, null));
            fields.Remove("pgid"); fields["osvers"] = "26.6"; True(!ReceiverTiming.FollowsReceiver(fields, null));
        });
        yield return Check("gPTP：majorSdoId及标准端口身份，拒绝普通PTP/截断/错误domain", () =>
        {
            var packet = PtpWire.Sync(0x1020304050607080, 65535);
            True(packet.Length == 44 && packet[0] == 0x10 && packet[1] == 2 && packet[6] == 6 && packet[7] == 8);
            True(PtpWire.IsValid(packet)); packet[0] = 0; True(!PtpWire.IsValid(packet)); packet[0] = 0x10;
            packet[4] = 1; True(!PtpWire.IsValid(packet)); packet[4] = 0;
            True(!PtpWire.IsValid(packet.AsSpan(0, 43)));
        });
        yield return Check("PTP时间：秒/纳秒大端编码，不把纳秒当NTP小数", () =>
        {
            var bytes = new byte[10]; const long ns = 1_800_000_000_123_456_789;
            PtpWire.Timestamp(bytes, ns); True(PtpWire.ReadTimestamp(bytes) == ns);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), 1_000_000_000);
            Throws<InvalidDataException>(() => PtpWire.ReadTimestamp(bytes));
            Array.Fill(bytes, (byte)255); Throws<InvalidDataException>(() => PtpWire.ReadTimestamp(bytes));
        });
        yield return Check("PTP通告：时钟身份、PATH_TRACE与两个FollowUp组织扩展一致", () =>
        {
            const ulong clock = 0x1020304050607080;
            var announce = PtpWire.Announce(clock, 42); var follow = PtpWire.FollowUp(clock, 43, 12345678900);
            True(announce.Length == 76 && announce[47] == 128 && announce[48] == 6 && announce[49] == 0x21 && announce[63] == 0x20);
            True(BinaryPrimitives.ReadUInt64BigEndian(announce.AsSpan(53)) == clock && BinaryPrimitives.ReadUInt64BigEndian(announce.AsSpan(68)) == clock);
            True(follow.Length == 96 && PtpWire.ReadTimestamp(follow.AsSpan(34)) == 12345678900);
            True(follow[49] == 0x80 && follow[50] == 0xc2 && follow[81] == 0x0d && follow[82] == 0x93 && BinaryPrimitives.ReadUInt64BigEndian(follow.AsSpan(86)) == clock);
        });
        yield return Check("PTP应答：E2E及Pdelay回显请求身份序号，双步时间分别编码", () =>
        {
            foreach (var type in new byte[] { 1, 2 })
            {
                var query = PtpWire.Header(type, type == 1 ? 44 : 54, 55, 77);
                var reply = PtpWire.DelayReply(query, 66, 123456789);
                True((reply[0] & 15) == (type == 1 ? 9 : 3) && BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(30)) == 77);
                True(reply.AsSpan(44, 10).SequenceEqual(query.AsSpan(20, 10)) && PtpWire.ReadTimestamp(reply.AsSpan(34)) == 123456789);
                if (type == 2) True((PtpWire.DelayReply(query, 66, 987654321, true)[0] & 15) == 10);
            }
        });
        yield return Check("PTP单播授权：只回应发给本时钟的有界请求，拒绝畸形TLV", () =>
        {
            var query = PtpWire.Header(12, 54, 55, 77);
            BinaryPrimitives.WriteUInt64BigEndian(query.AsSpan(34), 66); query[43] = 1;
            query[45] = 4; query[47] = 6; query[48] = 0xb0; query[49] = 0; query[53] = 30;
            var grant = PtpWire.Grants(query, 66).Single(); True(grant.Length == 56 && grant[45] == 5 && grant[47] == 8 && grant[53] == 30);
            True(!PtpWire.Grants(query, 99).Any()); query[47] = 9; True(!PtpWire.Grants(query, 66).Any());
        });
        yield return Check("PTP就绪：需要持续唯一探测，重放不累计，序号回绕不阻断", () =>
        {
            var tracker = new PtpClockTracker(66, false); long now = 10000000000;
            var repeated = PtpWire.Header(1, 44, 55, 65530);
            for (var i = 0; i < 15; i++) tracker.Observe(repeated, now + i * 250000000L);
            True(!tracker.Snapshot(now + 3500000000L).Ready);
            for (var i = 0; i < 15; i++) tracker.Observe(PtpWire.Header(1, 44, 55, unchecked((ushort)(65531 + i))), now + 4000000000L + i * 250000000L);
            True(tracker.Snapshot(now + 7500000000L).Ready);
            True(!tracker.Snapshot(now + 11000000000L).Ready);
        });
        yield return Check("跟随时钟：只收到DelayReq不能就绪，匹配Sync/FollowUp后才锁定异epoch", () =>
        {
            var tracker = new PtpClockTracker(66, true); const long local = 1_800_000_000_000_000_000; const long peer = 900_000_000_000;
            tracker.Observe(PtpWire.Announce(55, 1), local);
            for (var i = 0; i < 15; i++) tracker.Observe(PtpWire.Header(1, 44, 55, (ushort)i), local + i * 250000000L);
            True(!tracker.Snapshot(local + 3500000000L).Ready);
            for (ushort i = 0; i < 15; i++)
            {
                var elapsed = i * 250000000L;
                tracker.Observe(PtpWire.Sync(55, i), local + elapsed);
                tracker.Observe(PtpWire.FollowUp(55, i, peer + elapsed), local + elapsed + 1000);
            }
            var state = tracker.Snapshot(local + 3500001000L);
            True(state.Ready && state.ClockId == 55 && state.TimeNanoseconds == peer + 3500001000L);
            tracker.Observe(PtpWire.Announce(77, 2), local + 3500002000L); True(!tracker.Snapshot(local + 3500002000L).Ready);
        });
        yield return Check("跟随时钟：错序号/错时钟FollowUp及时间突跳不沿用已锁定锚点", () =>
        {
            var tracker = new PtpClockTracker(66, true); tracker.Observe(PtpWire.Announce(55, 1), 10000000000);
            for (ushort i = 0; i < 15; i++)
            {
                var now = 10000000000L + i * 250000000L; tracker.Observe(PtpWire.Sync(55, i), now);
                tracker.Observe(PtpWire.FollowUp(55, (ushort)(i + 100), now), now + 1000);
                tracker.Observe(PtpWire.FollowUp(77, i, now), now + 2000);
            }
            True(!tracker.Snapshot(13500000000).Ready);
        });
        yield return Check("PTP音频锚点：28字节0xd7、数值纳秒、渲染位置与时钟身份", () =>
        {
            var packet = NetworkClock.PtpSync(0xfffffff0, 12345678900, 55, true);
            True(packet.Length == 28 && packet[0] == 0x90 && packet[1] == 0xd7 && packet[3] == 6);
            var a = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));
            True(unchecked(a - 11035) == 0xfffffff0 && unchecked(BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16)) - a) == 77175);
            True(BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(8)) == 12345678900 && BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(20)) == 55);
        });
        yield return Check("跟随时钟：单个调度抖动不移动时间线，持续跳变使就绪过期", () =>
        {
            var tracker = new PtpClockTracker(66, true); const long start = 10000000000;
            tracker.Observe(PtpWire.Announce(55, 1), start);
            for (ushort i = 0; i < 40; i++)
            {
                var now = start + i * 125000000L;
                tracker.Observe(PtpWire.Sync(55, i), now);
                tracker.Observe(PtpWire.FollowUp(55, i, now + (i == 15 ? 15000000 : 0)), now + 1000);
            }
            True(tracker.Snapshot(start + 4875001000L).Ready);
            for (ushort i = 40; i < 64; i++)
            {
                var now = start + i * 125000000L;
                tracker.Observe(PtpWire.Sync(55, i), now);
                tracker.Observe(PtpWire.FollowUp(55, i, now + 1000000000), now + 1000);
            }
            True(!tracker.Snapshot(start + 7875001000L).Ready);
        });
        yield return Check("PTP测试端口：真实网络不能使用回环测试的端口替换", () =>
        {
            Throws<ArgumentException>(() => new PtpClockEngine(System.Net.IPAddress.Parse("192.0.2.1"), System.Net.IPAddress.Parse("192.0.2.2"), [], 55, false, null, _ => { }, new(31319, 31320)));
        });
    }
    private static (string, Func<Task>) Check(string name, Action action) => (name, () => { action(); return Task.CompletedTask; });
    private static void True(bool value) { if (!value) throw new Exception("PTP check failed."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
