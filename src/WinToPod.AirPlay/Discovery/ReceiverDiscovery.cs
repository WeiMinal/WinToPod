// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;

namespace WinToPod.AirPlay.Discovery;

public sealed record ReceiverEndpoint(string Service, string Instance, string Host, int Port, string InterfaceAddress, string[] Addresses);
public sealed record AirPlayReceiver(string Id, string Name, string? Model, bool IsHomePod,
    ReceiverEndpoint[] Endpoints, IReadOnlyDictionary<string, string?> Properties,
    string? TightSyncId, string? DynamicGroupId, string Occupancy = "Unknown");
public sealed record TightSyncCandidate(string Id, string[] MemberIds, bool TwoHomePods,
    string Status = "RequiresAuthenticatedConfirmation");
public sealed record DiscoverySnapshot(AirPlayReceiver[] Receivers, TightSyncCandidate[] GroupCandidates);

public static class ReceiverDiscovery
{
    public const string AirPlayService = "_airplay._tcp.local.";
    public const string RaopService = "_raop._tcp.local.";
    private static bool Same(string? a, string? b) => StringComparer.OrdinalIgnoreCase.Equals(a, b);

    public static DnsQuestion[] Questions(IEnumerable<DnsRecord> input)
    {
        var records = input.ToArray(); var questions = new List<DnsQuestion>
        { new(AirPlayService, DnsType.Ptr), new(RaopService, DnsType.Ptr) };
        foreach (var ptr in records.Where(r => r.Type == DnsType.Ptr && (Same(r.Name, AirPlayService) || Same(r.Name, RaopService))))
        {
            if (ptr.Target is null || !ValidInstance(ptr.Target, ptr.Name)) continue;
            questions.Add(new(ptr.Target, DnsType.Srv)); questions.Add(new(ptr.Target, DnsType.Txt));
            foreach (var srv in records.Where(r => r.Type == DnsType.Srv && Same(r.Name, ptr.Target) && ValidHost(r.Target)))
            { questions.Add(new(srv.Target!, DnsType.A)); questions.Add(new(srv.Target!, DnsType.Aaaa)); }
        }
        return questions.Distinct().Take(256).ToArray();
    }

    public static AirPlayReceiver[] Resolve(IEnumerable<DnsRecord> input, string interfaceAddress)
    {
        var records = input.ToArray(); var receivers = new List<AirPlayReceiver>();
        foreach (var ptr in records.Where(r => r.Type == DnsType.Ptr && (Same(r.Name, AirPlayService) || Same(r.Name, RaopService))))
        {
            var instance = ptr.Target; if (instance is null || !ValidInstance(instance, ptr.Name)) continue;
            var txtRecords = records.Where(r => r.Type == DnsType.Txt && Same(r.Name, instance)).ToArray();
            // Conflicting TXT RRSets during cache-flush grace are unresolved rather than guessed.
            if (txtRecords.Length != 1 || txtRecords[0].Text is not { } fields) continue;
            var raop = Same(ptr.Name, RaopService);
            var name = instance[..^(ptr.Name.Length + 1)];
            var rawId = Field(fields, "deviceid");
            if (raop)
            {
                var at = name.IndexOf('@');
                if (at > 0) { rawId ??= name[..at]; name = name[(at + 1)..]; }
            }
            var id = NormalizeDeviceId(rawId); if (id is null) continue;
            var endpoints = records.Where(r => r.Type == DnsType.Srv && Same(r.Name, instance) && r.Port != 0 && ValidHost(r.Target))
                .OrderBy(r => r.Priority).Select(srv => new ReceiverEndpoint(ptr.Name, instance, srv.Target!, srv.Port, interfaceAddress,
                    records.Where(r => r.Type is DnsType.A or DnsType.Aaaa && Same(r.Name, srv.Target) && r.Address is not null)
                        .Select(r => r.Address!.ToString()).Distinct().Order().ToArray())).Where(x => x.Addresses.Length != 0).ToArray();
            if (endpoints.Length == 0) continue;
            var model = Field(fields, raop ? "am" : "model");
            receivers.Add(new(id, name, model, model?.StartsWith("AudioAccessory", StringComparison.OrdinalIgnoreCase) == true ||
                model?.StartsWith("HomePod", StringComparison.OrdinalIgnoreCase) == true, endpoints, fields,
                Uuid(Field(fields, "tsid")), Uuid(Field(fields, "gid"))));
        }
        return receivers.ToArray();
    }

    public static DiscoverySnapshot Merge(IEnumerable<AirPlayReceiver> observations)
    {
        var receivers = observations.GroupBy(r => r.Id, StringComparer.Ordinal).Select(group =>
        {
            var ordered = group.OrderBy(r => r.Endpoints.Any(e => Same(e.Service, AirPlayService)) ? 0 : 1).ToArray();
            var first = ordered[0];
            // Keep the AirPlay TXT as the authority; RAOP fills absent keys only.
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in ordered) foreach (var field in item.Properties) fields.TryAdd(field.Key, field.Value);
            var syncIds = ordered.Select(x => x.TightSyncId).Where(x => x is not null).Distinct().ToArray();
            return first with { Endpoints = ordered.SelectMany(r => r.Endpoints).DistinctBy(e => $"{e.InterfaceAddress}|{e.Service}|{e.Instance}|{e.Host}|{e.Port}|{string.Join(',', e.Addresses)}").ToArray(),
                Properties = new ReadOnlyDictionary<string, string?>(fields), TightSyncId = syncIds.Length == 1 ? syncIds[0] : null };
        }).OrderBy(r => r.Id).ToArray();
        var hints = receivers.Where(r => r.TightSyncId is not null).GroupBy(r => r.TightSyncId!)
            .Select(g => new TightSyncCandidate("tight-sync:" + g.Key, g.Select(r => r.Id).Order().ToArray(), g.Count() == 2 && g.All(r => r.IsHomePod))).ToArray();
        // No playback authority is manufactured here: DNS data is unauthenticated, gid is dynamic,
        // and tsid alone cannot establish channel roles or authorize a stereo session.
        return new(receivers, hints);
    }

    private static bool ValidInstance(string instance, string service) => instance.EndsWith("." + service, StringComparison.OrdinalIgnoreCase)
        && instance[..^(service.Length + 1)].Length != 0 && !instance[..^(service.Length + 1)].Contains('.');
    private static bool ValidHost(string? host) => host is not null && host.Length > 6 && host.EndsWith(".local.", StringComparison.OrdinalIgnoreCase);
    private static string? Field(IReadOnlyDictionary<string, string?> fields, string key) => fields.TryGetValue(key, out var value) ? value : null;
    private static string? Uuid(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("D") : null;
    private static string? NormalizeDeviceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var compact = value.Replace(":", "").Replace("-", "");
        if (compact.Length != 12 || compact.Any(c => !Uri.IsHexDigit(c))) return null;
        if (compact.All(c => c == '0') || compact.All(c => char.ToUpperInvariant(c) == 'F')) return null;
        return "device:" + compact.ToLowerInvariant();
    }
}
