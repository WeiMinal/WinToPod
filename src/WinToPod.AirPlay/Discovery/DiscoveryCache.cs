// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.AirPlay.Discovery;

// One cache per interface. Not thread-safe: the browser serializes receive/query access.
public sealed class DiscoveryCache(int capacity = 4096)
{
    private readonly int _capacity = capacity is > 0 and <= 16384 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private sealed record Entry(DnsRecord Record, DateTimeOffset Seen, DateTimeOffset Expires);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(DnsRecord r) => $"{r.Name}|{(ushort)r.Type}|{r.DataKey}";
    private static bool SameSet(DnsRecord a, DnsRecord b) => a.Type == b.Type && StringComparer.OrdinalIgnoreCase.Equals(a.Name, b.Name);

    public void Apply(DnsPacket packet, DateTimeOffset now)
    {
        if (!packet.Response || packet.Truncated) return; // Never commit an incomplete RRSet.
        Prune(now);
        foreach (var record in packet.Records)
        {
            if (record.Flush && record.Ttl != 0 && record.Type != DnsType.Ptr)
                foreach (var (key, entry) in _entries.ToArray())
                    if (SameSet(record, entry.Record) && entry.Seen < now.AddSeconds(-1) && Key(record) != key)
                        _entries[key] = entry with { Expires = entry.Expires < now.AddSeconds(1) ? entry.Expires : now.AddSeconds(1) };
            var id = Key(record);
            if (record.Ttl == 0)
            {
                if (_entries.TryGetValue(id, out var old))
                    _entries[id] = old with { Expires = old.Expires < now.AddSeconds(1) ? old.Expires : now.AddSeconds(1) };
                continue;
            }
            if (!_entries.ContainsKey(id) && _entries.Count >= _capacity) continue;
            _entries[id] = new(record, now, now.AddSeconds(record.Ttl));
        }
    }

    public DnsRecord[] Records(DateTimeOffset now) { Prune(now); return _entries.Values.Select(x => x.Record).ToArray(); }
    private void Prune(DateTimeOffset now)
    { foreach (var key in _entries.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) _entries.Remove(key); }
}
