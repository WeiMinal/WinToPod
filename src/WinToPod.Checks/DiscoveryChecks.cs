// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Net;
using System.Text;
using WinToPod.AirPlay.Discovery;

internal static class DiscoveryChecks
{
    private const string Service = ReceiverDiscovery.AirPlayService;
    private const string Instance = "客厅._airplay._tcp.local.";
    private const string SyncId = "39180248-9361-4381-a195-96e7ec55121c";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return Check("DNS：独立手写压缩PTR报文解析，不依赖查询编码器", () =>
        {
            var packet = Convert.FromHexString("000084000000000100000000085f616972706c6179045f746370056c6f63616c00000c000100000078000603506f64c00c");
            var r = DnsWire.Parse(packet).Records.Single();
            True(r.Name == Service && r.Target == "Pod._airplay._tcp.local." && r.Ttl == 120);
        });
        yield return Check("DNS：名称内点号保持单标签，避免重名碰撞", () =>
        {
            var bytes = DnsWire.Query(73, [new("客厅\\046左._airplay._tcp.local.", DnsType.Txt)]);
            True(bytes.AsSpan(0, 2).SequenceEqual(new byte[] { 0, 73 }));
            True(bytes[12] == 10); // 客厅 (6 UTF-8 bytes), literal dot (1), 左 (3).
            True(!DnsWire.Parse(bytes).Response);
            Throws<ArgumentException>(() => DnsWire.EncodeName(new string('a', 64) + ".local."));
            Throws<ArgumentException>(() => DnsWire.EncodeName("bad\\+12.local."));
            Throws<ArgumentException>(() => DnsWire.EncodeName("bad.local.."));
        });
        yield return Check("DNS：前向/自身压缩指针、截断和异常地址长度被拒绝", () =>
        {
            var cycle = Convert.FromHexString("000084000000000100000000c00c00010001000000780004c0a80164");
            Throws<InvalidDataException>(() => DnsWire.Parse(cycle));
            var valid = Packet(("speaker.local.", DnsType.A, 120u, false, IPAddress.Parse("192.168.1.5").GetAddressBytes()));
            Throws<InvalidDataException>(() => DnsWire.Parse(valid[..^1]));
            Throws<InvalidDataException>(() => DnsWire.Parse(Packet(("speaker.local.", DnsType.A, 120u, false, new byte[3]))));
        });
        yield return Check("DNS-SD：TXT键不区分大小写，首次重复值优先，区分无值与空值", () =>
        {
            var record = DnsWire.Parse(Packet((Instance, DnsType.Txt, 120u, false, Txt("Model=AudioAccessory5,1", "model=wrong", "flag", "empty=")))).Records.Single();
            True(record.Text!["MODEL"] == "AudioAccessory5,1");
            True(record.Text.ContainsKey("flag") && record.Text["flag"] is null && record.Text["empty"] == "");
            Throws<InvalidDataException>(() => DnsWire.Parse(Packet((Instance, DnsType.Txt, 120u, false, new byte[] { 5, 1 }))));
        });
        yield return Check("发现缓存：非响应和截断包不污染缓存", () =>
        {
            var cache = new DiscoveryCache(); var r = Address("192.168.1.1");
            cache.Apply(new(0, false, false, [r]), Now); cache.Apply(new(0, true, true, [r]), Now);
            True(cache.Records(Now).Length == 0);
        });
        yield return Check("发现缓存：TTL到期移除，刷新延长寿命", () =>
        {
            var cache = new DiscoveryCache(); var r = Address("192.168.1.1") with { Ttl = 2 };
            cache.Apply(new(0, true, false, [r]), Now); cache.Apply(new(0, true, false, [r]), Now.AddSeconds(1));
            True(cache.Records(Now.AddSeconds(2)).Length == 1); True(cache.Records(Now.AddSeconds(3)).Length == 0);
        });
        yield return Check("发现缓存：goodbye保留一秒宽限，可被新广播挽回", () =>
        {
            var cache = new DiscoveryCache(); var r = Address("192.168.1.1");
            cache.Apply(new(0, true, false, [r]), Now); cache.Apply(new(0, true, false, [r with { Ttl = 0 }]), Now.AddSeconds(2));
            True(cache.Records(Now.AddSeconds(2.5)).Length == 1);
            cache.Apply(new(0, true, false, [r]), Now.AddSeconds(2.6)); True(cache.Records(Now.AddSeconds(4)).Length == 1);
            cache.Apply(new(0, true, false, [r with { Ttl = 0 }]), Now.AddSeconds(5)); True(cache.Records(Now.AddSeconds(6)).Length == 0);
        });
        yield return Check("发现缓存：cache-flush淘汰旧地址，不删除同批多个新地址", () =>
        {
            var cache = new DiscoveryCache(); var old = Address("192.168.1.1");
            cache.Apply(new(0, true, false, [old]), Now);
            cache.Apply(new(0, true, false, [Address("192.168.1.2"), Address("192.168.1.3")]), Now.AddSeconds(2));
            True(cache.Records(Now.AddSeconds(2)).Length == 3);
            var remaining = cache.Records(Now.AddSeconds(3));
            True(remaining.Length == 2 && remaining.All(r => r.Address!.ToString() != "192.168.1.1"));
        });
        yield return Check("发现缓存：共享PTR不误用flush，缓存容量有上限", () =>
        {
            Throws<ArgumentOutOfRangeException>(() => new DiscoveryCache(0));
            var cache = new DiscoveryCache(2); var r = Address("192.168.1.1");
            cache.Apply(new(0, true, false, [r, Address("192.168.1.2"), Address("192.168.1.3")]), Now);
            True(cache.Records(Now).Length == 2);
            cache = new DiscoveryCache(); var a = new DnsRecord(Service, DnsType.Ptr, 120, true, "a", Target: "a." + Service);
            cache.Apply(new(0, true, false, [a]), Now); cache.Apply(new(0, true, false, [a with { DataKey = "b", Target = "b." + Service }]), Now.AddSeconds(2));
            True(cache.Records(Now.AddSeconds(4)).Length == 2);
        });
        yield return Check("发现：乱序PTR/TXT/SRV/A记录解析为稳定设备及真实端口", () =>
        {
            var records = ReceiverRecords(Instance, "AA:BB:CC:DD:EE:01", "192.168.1.3", "AudioAccessory5,1").Reverse();
            var receiver = ReceiverDiscovery.Resolve(records, "192.168.1.2").Single();
            True(receiver.Id == "device:aabbccddee01" && receiver.IsHomePod && receiver.Endpoints.Single().Port == 7000);
            True(receiver.Occupancy == "Unknown");
        });
        yield return Check("发现：只有名称/IP而无设备身份时不伪造持久目标", () =>
        {
            var records = ReceiverRecords(Instance, "", "192.168.1.3", "AudioAccessory5,1");
            True(ReceiverDiscovery.Resolve(records, "192.168.1.2").Length == 0);
            var questions = ReceiverDiscovery.Questions(records.Take(1));
            True(questions.Any(q => q.Type == DnsType.Srv) && questions.Any(q => q.Type == DnsType.Txt));
        });
        yield return Check("发现：缺少SRV、地址或TXT时继续解析而非输出假端点", () =>
        {
            var records = ReceiverRecords(Instance, "AA:BB:CC:DD:EE:01", "192.168.1.3", "AudioAccessory5,1");
            foreach (var type in new[] { DnsType.Srv, DnsType.A, DnsType.Txt })
                True(ReceiverDiscovery.Resolve(records.Where(r => r.Type != type), "192.168.1.2").Length == 0);
            True(ReceiverDiscovery.Questions(records).Any(q => q.Type == DnsType.Aaaa));
        });
        yield return Check("发现：重命名/IP变化不改变设备身份，同名不同设备不合并", () =>
        {
            var a = Resolve("客厅", "AA:BB:CC:DD:EE:01", "192.168.1.3");
            var renamed = Resolve("Bedroom", "aa-bb-cc-dd-ee-01", "192.168.1.4");
            True(a.Id == renamed.Id && a.Name != renamed.Name);
            var b = Resolve("客厅", "AA:BB:CC:DD:EE:02", "192.168.1.5");
            True(ReceiverDiscovery.Merge([a, b]).Receivers.Length == 2);
        });
        yield return Check("发现：多网卡观察同一设备合并身份并保留接口端点", () =>
        {
            var a = Resolve("客厅", "AA:BB:CC:DD:EE:01", "192.168.1.3");
            var b = ReceiverDiscovery.Resolve(ReceiverRecords(Instance, "AA:BB:CC:DD:EE:01", "10.0.0.3", "AudioAccessory5,1"), "10.0.0.2").Single();
            var merged = ReceiverDiscovery.Merge([a, b]).Receivers.Single(); True(merged.Endpoints.Length == 2);
        });
        yield return Check("发现：RAOP及AirPlay按deviceid去重，优先保留AirPlay属性", () =>
        {
            var airplay = Resolve("客厅", "AA:BB:CC:DD:EE:01", "192.168.1.3");
            var instance = "AABBCCDDEE01@客厅." + ReceiverDiscovery.RaopService;
            var records = new[] {
                new DnsRecord(ReceiverDiscovery.RaopService, DnsType.Ptr, 120, false, instance, Target: instance),
                new DnsRecord(instance, DnsType.Txt, 120, true, "txt", Text: new Dictionary<string,string?> { ["am"] = "AudioAccessory5,1" }),
                new DnsRecord(instance, DnsType.Srv, 120, true, "srv", Target: "speaker.local.", Port: 49152), Address("192.168.1.3") };
            var raop = ReceiverDiscovery.Resolve(records, "192.168.1.2").Single();
            var merged = ReceiverDiscovery.Merge([raop, airplay]).Receivers.Single();
            True(merged.Endpoints.Length == 2 && merged.Model == "AudioAccessory5,1");
        });
        yield return Check("分组：共同动态gid不被误认成立体声对", () =>
        {
            var a = Resolve("音箱甲", "AA:BB:CC:DD:EE:01", "192.168.1.3") with { DynamicGroupId = SyncId };
            var b = Resolve("音箱乙", "AA:BB:CC:DD:EE:02", "192.168.1.4") with { DynamicGroupId = SyncId };
            var snapshot = ReceiverDiscovery.Merge([a, b]); True(snapshot.Receivers.Length == 2 && snapshot.GroupCandidates.Length == 0);
        });
        yield return Check("分组：两只HomePod共同tsid仅形成待鉴权候选，不分配左右声道", () =>
        {
            var a = Resolve("音箱甲", "AA:BB:CC:DD:EE:01", "192.168.1.3") with { TightSyncId = SyncId };
            var b = Resolve("音箱乙", "AA:BB:CC:DD:EE:02", "192.168.1.4") with { TightSyncId = SyncId };
            var group = ReceiverDiscovery.Merge([a, b]).GroupCandidates.Single();
            True(group.TwoHomePods && group.MemberIds.Length == 2 && group.Status == "RequiresAuthenticatedConfirmation");
        });
        yield return Check("分组：只看到一半、三个成员或非HomePod不伪造完整音箱对", () =>
        {
            var a = Resolve("音箱甲", "AA:BB:CC:DD:EE:01", "192.168.1.3") with { TightSyncId = SyncId };
            var b = a with { Id = "device:b", IsHomePod = false };
            True(!ReceiverDiscovery.Merge([a]).GroupCandidates.Single().TwoHomePods);
            True(!ReceiverDiscovery.Merge([a, b]).GroupCandidates.Single().TwoHomePods);
            True(!ReceiverDiscovery.Merge([a, b with { IsHomePod = true }, a with { Id = "device:c" }]).GroupCandidates.Single().TwoHomePods);
        });
        yield return Check("发现：冲突TXT和非local目标拒绝建立端点", () =>
        {
            var records = ReceiverRecords(Instance, "AA:BB:CC:DD:EE:01", "192.168.1.3", "AudioAccessory5,1");
            True(ReceiverDiscovery.Resolve(records.Append(records.Single(r => r.Type == DnsType.Txt)), "192.168.1.2").Length == 0);
            True(ReceiverDiscovery.Resolve(records.Select(r => r.Type == DnsType.Srv ? r with { Target = "public.example." } : r), "192.168.1.2").Length == 0);
        });
        yield return Check("DNS：5000个确定性随机损坏输入不越界、不泄漏意外异常", () =>
        {
            var random = new Random(20261016);
            for (var i = 0; i < 5000; i++)
            {
                var bytes = new byte[random.Next(0, 400)]; random.NextBytes(bytes);
                if (bytes.Length >= 12) { bytes[2] = 0x84; bytes[3] = 0; bytes.AsSpan(4, 8).Clear(); bytes[7] = 1; }
                try { DnsWire.Parse(bytes); } catch (InvalidDataException) { }
            }
        });
        yield return Check("组合入口回归：两只igl=0时使用tight-sync标记，不沿用Apple TV组名", () =>
        {
            var a = Resolve("音箱甲", "AA:BB:CC:DD:EE:01", "192.168.1.3") with { TightSyncId = SyncId, Properties = new Dictionary<string, string?> { ["flags"] = "0xb8c84", ["igl"] = "0", ["gpn"] = "不应显示的电视名" } };
            var b = Resolve("音箱乙", "AA:BB:CC:DD:EE:02", "192.168.1.4") with { TightSyncId = SyncId, Properties = new Dictionary<string, string?> { ["flags"] = "0xbac04", ["igl"] = "0", ["gpn"] = "不应显示的电视名" } };
            var entry = PlaybackEntries.Build(ReceiverDiscovery.Merge([a, b])).Single();
            True(entry.Primary?.Id == b.Id && entry.HasIpv4Endpoint && entry.Members.Length == 2 && !entry.Name.Contains("电视"));
        });
        yield return Check("组合入口：动态igl不覆盖tight-sync主成员，也不猜测缺失或冲突主入口", () =>
        {
            var a = Resolve("音箱甲", "AA:BB:CC:DD:EE:01", "192.168.1.3") with { TightSyncId = SyncId, Properties = new Dictionary<string, string?> { ["flags"] = "4", ["igl"] = "1" } };
            var b = a with { Id = "device:b", Properties = new Dictionary<string, string?> { ["flags"] = "2004", ["igl"] = "0" } };
            True(PlaybackEntries.UniqueLeader([a, b])?.Id == b.Id);
            True(PlaybackEntries.UniqueLeader([a, a with { Id = "device:c" }]) is null);
            True(PlaybackEntries.UniqueLeader([b, b with { Id = "device:c" }]) is null);
            var unresolved = PlaybackEntries.Build(ReceiverDiscovery.Merge([a, a with { Id = "device:c" }])).Single();
            True(unresolved.Primary is null && unresolved.HasIpv4Endpoint);
        });
        yield return Check("目标展示：单只、多只独立HomePod不被合并，HomePod排在Mac/Apple TV之前", () =>
        {
            var a = Resolve("z音箱", "AA:BB:CC:DD:EE:01", "192.168.1.3"); var b = a with { Id = "device:b", Name = "y音箱" };
            var mac = a with { Id = "device:m", Name = "aMac", IsHomePod = false, Model = "Macmini9,1" };
            var tv = mac with { Id = "device:t", Model = "AppleTV6,2" };
            var entries = PlaybackEntries.Build(ReceiverDiscovery.Merge([tv, mac, a, b]));
            True(entries.Length == 4 && entries.Take(2).All(e => e.Members.All(m => m.IsHomePod)) && entries.All(e => e.Primary is not null));
        });
        yield return Check("密码提示：flags/pw/sf识别密码与验证码，不把未知当有密码", () =>
        {
            CredentialRequirement Read(params (string Key, string? Value)[] values) => ReceiverAccess.Credentials(values.ToDictionary(p => p.Key, p => p.Value));
            True(Read(("flags", "0x84")) == CredentialRequirement.Password);
            True(Read(("sf", "84")) == CredentialRequirement.Password);
            True(Read(("pw", "true")) == CredentialRequirement.Password);
            True(Read(("pw", "false")) == CredentialRequirement.NoneAdvertised);
            True(Read(("flags", "0x8")) == CredentialRequirement.PinPairing);
            True(Read(("flags", "garbage")) == CredentialRequirement.Unknown && Read() == CredentialRequirement.Unknown);
        });
        yield return Check("密码提示：实时info覆盖过期广播；正向密码标记优先于pw=false", () =>
        {
            var fields = new Dictionary<string, string?> { ["flags"] = "84", ["pw"] = "true" };
            True(ReceiverAccess.Credentials(fields, new() { ["statusFlags"] = 4UL }) == CredentialRequirement.NoneAdvertised);
            True(ReceiverAccess.Credentials(new Dictionary<string, string?> { ["pw"] = "false" }, new() { ["statusFlags"] = 128UL }) == CredentialRequirement.Password);
            True(ReceiverAccess.Credentials(new Dictionary<string, string?> { ["pw"] = "false", ["flags"] = "84" }) == CredentialRequirement.Password);
        });
        yield return ("扫描：非法持续时间及预先取消均不发起网络工作", async () =>
        {
            try { await new AirPlayBrowser().ScanAsync(TimeSpan.Zero); throw new Exception("Expected duration rejection."); }
            catch (ArgumentOutOfRangeException) { }
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await new AirPlayBrowser().ScanAsync(TimeSpan.FromSeconds(2), cancellation: cancel.Token); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
        });
    }

    private static AirPlayReceiver Resolve(string name, string id, string ip)
        => ReceiverDiscovery.Resolve(ReceiverRecords(name + "." + Service, id, ip, "AudioAccessory5,1"), "192.168.1.2").Single();
    private static DnsRecord[] ReceiverRecords(string instance, string id, string ip, string model)
        => DnsWire.Parse(Packet(
            (Service, DnsType.Ptr, 120u, false, DnsWire.EncodeName(instance)),
            (instance, DnsType.Txt, 120u, true, Txt("deviceid=" + id, "model=" + model)),
            (instance, DnsType.Srv, 120u, true, new byte[] { 0, 0, 0, 0, 0x1b, 0x58 }.Concat(DnsWire.EncodeName("speaker.local.")).ToArray()),
            ("speaker.local.", DnsType.A, 120u, true, IPAddress.Parse(ip).GetAddressBytes()))).Records;
    private static DnsRecord Address(string ip) => new("speaker.local.", DnsType.A, 120, true, ip, Address: IPAddress.Parse(ip));
    private static byte[] Txt(params string[] fields)
        => fields.SelectMany(f => { var bytes = Encoding.UTF8.GetBytes(f); return new[] { checked((byte)bytes.Length) }.Concat(bytes); }).ToArray();
    private static byte[] Packet(params (string Name, DnsType Type, uint Ttl, bool Flush, byte[] Data)[] records)
    {
        using var output = new MemoryStream(); var header = new byte[12]; header[2] = 0x84;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)records.Length); output.Write(header);
        foreach (var r in records)
        {
            output.Write(DnsWire.EncodeName(r.Name)); var fixedPart = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart, (ushort)r.Type);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), r.Flush ? (ushort)0x8001 : (ushort)1);
            BinaryPrimitives.WriteUInt32BigEndian(fixedPart.AsSpan(4), r.Ttl);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(8), (ushort)r.Data.Length);
            output.Write(fixedPart); output.Write(r.Data);
        }
        return output.ToArray();
    }
    private static (string, Func<Task>) Check(string name, Action run) => (name, () => { run(); return Task.CompletedTask; });
    private static void True(bool value) { if (!value) throw new Exception("Discovery check failed."); }
    private static void Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
