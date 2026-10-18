// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using WinToPod.AirPlay.Serialization;
using WinToPod.AirPlay.Transport;

namespace WinToPod.AirPlay.Discovery;

public sealed record PlaybackEntry(string Key, string Name, string Kind, AirPlayReceiver[] Members, AirPlayReceiver? Primary)
{
    public bool IsGroup => Members.Length > 1 || Members.Any(m => m.TightSyncId is not null);
    public bool HasIpv4Endpoint => Members.Any(m => Endpoint(m) is not null);
    public static ReceiverEndpoint? Endpoint(AirPlayReceiver receiver) => receiver.Endpoints.FirstOrDefault(e =>
        e.Service.Equals(ReceiverDiscovery.AirPlayService, StringComparison.OrdinalIgnoreCase) &&
        e.Addresses.Any(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork));
}

public static class PlaybackEntries
{
    public static PlaybackEntry[] Build(DiscoverySnapshot snapshot)
    {
        var entries = new List<PlaybackEntry>(); var grouped = new HashSet<string>();
        foreach (var group in snapshot.GroupCandidates)
        {
            var members = snapshot.Receivers.Where(r => group.MemberIds.Contains(r.Id)).ToArray();
            if (members.Length == 0) continue;
            foreach (var member in members) grouped.Add(member.Id);
            var leader = UniqueLeader(members);
            // gpn may name the Apple TV currently driving a HomePod pair. Do not rename the pair after that TV.
            var names = members.Select(m => m.Name).Distinct().ToArray();
            var name = names.Length == 1 ? names[0] : string.Join(" + ", names);
            entries.Add(new("candidate:" + group.Id, name,
                group.TwoHomePods ? "HomePod 组合 · 2只" : $"同步组合 · {members.Length}只已发现", members, leader));
        }
        foreach (var receiver in snapshot.Receivers.Where(r => !grouped.Contains(r.Id)))
            entries.Add(new(receiver.Id, receiver.Name, receiver.IsHomePod ? "HomePod / HomePod mini" : receiver.Model?.StartsWith("AppleTV", StringComparison.OrdinalIgnoreCase) == true ? "Apple TV" : receiver.Model?.StartsWith("Mac", StringComparison.OrdinalIgnoreCase) == true ? "Mac" : "其他 AirPlay 设备", [receiver], receiver));
        return entries.OrderByDescending(e => e.Members.All(m => m.IsHomePod)).ThenBy(e => e.Name).ToArray();
    }

    public static AirPlayReceiver? UniqueLeader(IEnumerable<AirPlayReceiver> members)
    {
        var leaders = members.Where(ReceiverAccess.IsTightSyncLeader).ToArray();
        return leaders.Length == 1 ? leaders[0] : null;
    }

    // Called only after an explicit Connect click. GET /info does not pair, SETUP, change volume, or send audio.
    // Never try both members with active sessions if metadata is incomplete or contradictory.
    public static async Task<AirPlayReceiver> ResolveAsync(PlaybackEntry entry, CancellationToken cancellation)
    {
        if (entry.Primary is { } known) return known;
        var observations = await Task.WhenAll(entry.Members.Select(async member =>
        {
            var endpoint = PlaybackEntry.Endpoint(member);
            if (endpoint is null) return member;
            try
            {
                var remote = endpoint.Addresses.Select(IPAddress.Parse).First(a => a.AddressFamily == AddressFamily.InterNetwork);
                await using var connection = await RtspConnection.ConnectAsync(remote, endpoint.Port, IPAddress.Parse(endpoint.InterfaceAddress), cancellation).ConfigureAwait(false);
                var response = await connection.RequestAsync("GET", "/info", cancellation: cancellation).ConfigureAwait(false);
                if (response.Status != 200 || response.Body.Length == 0) return member;
                var info = BinaryPlist.Dictionary(response.Body);
                if (!info.TryGetValue("statusFlags", out var flags) || ReceiverAccess.Integer(flags) is not { } value) return member;
                var properties = new Dictionary<string, string?>(member.Properties, StringComparer.OrdinalIgnoreCase) { ["flags"] = "0x" + value.ToString("x", System.Globalization.CultureInfo.InvariantCulture) };
                return member with { Properties = properties };
            }
            catch (Exception e) when (e is IOException or SocketException or TimeoutException) { return member; }
        })).ConfigureAwait(false);
        return UniqueLeader(observations) ?? throw new IOException("已经找到这组音箱，但尚未确认唯一的组合入口。请重新搜索；也可在设置中导出诊断。无需拆开立体声组合。");
    }
}
