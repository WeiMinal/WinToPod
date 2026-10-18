// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace WinToPod.AirPlay.Discovery;

public sealed record DiscoveryDiagnostic(string InterfaceAddress, string Message);
public sealed record DiscoveryScan(DiscoverySnapshot Snapshot, DiscoveryDiagnostic[] Diagnostics);

// Bounded IPv4 legacy-unicast browsing, RFC 6762 sections 5.1/6.7.
// Uses ephemeral source ports and never owns port 5353, pairs, or starts playback.
// This is not the continuous mDNS monitor needed by the eventual desktop app.
public sealed class AirPlayBrowser
{
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("224.0.0.251"), 5353);
    private sealed record Link(IPAddress Address, int PrefixLength);

    public async Task<DiscoveryScan> ScanAsync(TimeSpan duration, IPAddress? interfaceAddress = null, CancellationToken cancellation = default)
    {
        if (duration < TimeSpan.FromSeconds(2) || duration > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(duration), "Scan duration must be 2–60 seconds.");
        cancellation.ThrowIfCancellationRequested();
        var links = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => new Link(a.Address, a.PrefixLength)).DistinctBy(x => x.Address).ToArray();
        if (interfaceAddress is not null) links = links.Where(l => l.Address.Equals(interfaceAddress)).ToArray();
        if (links.Length == 0) throw new InvalidOperationException("没有可用的 IPv4 多播接口；请检查网络或指定本机接口地址。");
        if (links.Length > 32) throw new InvalidOperationException("接口地址过多，请指定本机 IPv4 接口地址。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(duration);
        var results = await Task.WhenAll(links.Select(link => ScanLinkAsync(link, deadline.Token))).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return new(ReceiverDiscovery.Merge(results.SelectMany(r => r.Receivers)), results.SelectMany(r => r.Diagnostics).ToArray());
    }

    private static async Task<(AirPlayReceiver[] Receivers, DiscoveryDiagnostic[] Diagnostics)> ScanLinkAsync(Link link, CancellationToken cancellation)
    {
        var cache = new DiscoveryCache(); var gate = new object(); var diagnostics = new List<DiscoveryDiagnostic>();
        var rejected = 0; var interfaceName = link.Address.ToString();
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        using var local = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var id = (ushort)RandomNumberGenerator.GetInt32(1, 65536);
        Task? receive = null;
        try
        {
            socket.Client.Bind(new IPEndPoint(link.Address, 0));
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, link.Address.GetAddressBytes());
            receive = ReceiveAsync();
            var interval = TimeSpan.FromSeconds(1);
            while (!local.IsCancellationRequested)
            {
                DnsQuestion[] questions;
                lock (gate) questions = ReceiverDiscovery.Questions(cache.Records(DateTimeOffset.UtcNow));
                foreach (var question in questions)
                {
                    var query = DnsWire.Query(id, [question]);
                    await socket.SendAsync(query, Multicast, local.Token).ConfigureAwait(false);
                }
                await Task.Delay(interval, local.Token).ConfigureAwait(false);
                interval = TimeSpan.FromSeconds(Math.Min(8, interval.TotalSeconds * 2));
            }
        }
        catch (OperationCanceledException) when (local.IsCancellationRequested) { }
        catch (Exception e) when (e is SocketException or NetworkInformationException or InvalidOperationException)
        { diagnostics.Add(new(interfaceName, e.Message)); }
        finally
        {
            await local.CancelAsync().ConfigureAwait(false);
            if (receive is not null)
                try { await receive.ConfigureAwait(false); }
                catch (OperationCanceledException) when (local.IsCancellationRequested) { }
                catch (SocketException e) { diagnostics.Add(new(interfaceName, e.Message)); }
        }
        if (rejected > 0) diagnostics.Add(new(interfaceName, $"忽略 {rejected} 个无效或不匹配的 DNS 响应。"));
        AirPlayReceiver[] receivers;
        lock (gate) receivers = ReceiverDiscovery.Resolve(cache.Records(DateTimeOffset.UtcNow), interfaceName);
        return (receivers, diagnostics.ToArray());

        async Task ReceiveAsync()
        {
            try
            {
                while (!local.IsCancellationRequested)
                {
                    var received = await socket.ReceiveAsync(local.Token).ConfigureAwait(false);
                    if (received.RemoteEndPoint.Port != 5353 || !OnLink(received.RemoteEndPoint.Address, link.Address, link.PrefixLength))
                    { rejected++; continue; }
                    try
                    {
                        var packet = DnsWire.Parse(received.Buffer);
                        if (packet.Id != id || !packet.Response || packet.Truncated) { rejected++; continue; }
                        lock (gate) cache.Apply(packet, DateTimeOffset.UtcNow);
                    }
                    catch (InvalidDataException) { rejected++; }
                }
            }
            catch (SocketException) { await local.CancelAsync().ConfigureAwait(false); throw; }
        }
    }

    private static bool OnLink(IPAddress source, IPAddress local, int prefix)
    {
        if (source.AddressFamily != AddressFamily.InterNetwork || prefix is < 1 or > 32) return false;
        var a = source.GetAddressBytes(); var b = local.GetAddressBytes();
        if (a[0] is 0 or 127 || a[0] >= 224 || a.All(x => x == 255)) return false;
        for (var bit = 0; bit < prefix; bit++) if (((a[bit / 8] ^ b[bit / 8]) & (0x80 >> (bit % 8))) != 0) return false;
        return true;
    }
}
