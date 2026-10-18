// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text.Json;
using WinToPod.AirPlay.Discovery;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Contains("--help"))
{
    Console.WriteLine("WinToPod 设备扫描：--seconds 10 --interface 本机IPv4地址 --json");
    Console.WriteLine("仅发送局域网 DNS 查询，不配对、不建立播放会话、不改动音量；当前不支持 IPv6-only 网络。");
    return 0;
}
var seconds = 10; IPAddress? address = null; var json = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--json": json = true; break;
        case "--seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var value) && value is >= 2 and <= 60:
            seconds = value; i++; break;
        case "--interface" when i + 1 < args.Length && IPAddress.TryParse(args[i + 1], out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork:
            address = ip; i++; break;
        default: Console.Error.WriteLine("参数无效。使用 --help 查看用法。"); return 2;
    }
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (!json) Console.WriteLine($"WinToPod 正在扫描局域网（{seconds}秒），不会连接或抢占音箱……");
    var scan = await new AirPlayBrowser().ScanAsync(TimeSpan.FromSeconds(seconds), address, cancellation.Token);
    if (json) Console.WriteLine(JsonSerializer.Serialize(scan, new JsonSerializerOptions { WriteIndented = true }));
    else
    {
        foreach (var receiver in scan.Snapshot.Receivers)
        {
            Console.WriteLine($"{receiver.Name} | {receiver.Model ?? "型号未知"} | {receiver.Id} | 占用未知");
            foreach (var endpoint in receiver.Endpoints)
                Console.WriteLine($"  {endpoint.Service} {string.Join(", ", endpoint.Addresses)}:{endpoint.Port}（本机接口 {endpoint.InterfaceAddress}）");
        }
        foreach (var group in scan.Snapshot.GroupCandidates)
            Console.WriteLine($"同步组合线索：{group.Id}，{group.MemberIds.Length}个成员；立体声关系尚待鉴权信息确认。");
        foreach (var diagnostic in scan.Diagnostics) Console.Error.WriteLine($"{diagnostic.InterfaceAddress}: {diagnostic.Message}");
        if (scan.Snapshot.Receivers.Length == 0)
            Console.WriteLine("未发现已完整解析的设备。请检查同网段、网络隔离、防火墙或指定本机接口；这不代表音箱空闲或不兼容。");
    }
    return scan.Snapshot.Receivers.Length == 0 ? 1 : 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
catch (Exception error) when (error is System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException)
{ Console.Error.WriteLine(error.Message); return 1; }
