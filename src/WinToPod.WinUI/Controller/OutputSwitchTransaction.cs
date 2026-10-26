// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.Desktop;
internal static class OutputSwitchTransaction
{
    // Change console and multimedia; leave communication-call routing alone.
    public static void Apply(string id, Func<int, string> read, Action<int, string> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var before = new[] { read(0), read(1) }; var attempted = new List<int>();
        try
        {
            for (var role = 0; role < before.Length; role++)
            {
                if (string.Equals(before[role], id, StringComparison.OrdinalIgnoreCase)) continue;
                attempted.Add(role); write(role, id);
            }
            if (Enumerable.Range(0, 2).Any(role => !string.Equals(read(role), id, StringComparison.OrdinalIgnoreCase))) throw new IOException("Windows 未确认输出设备变更。");
        }
        catch (Exception error)
        {
            var rollbackFailed = false;
            foreach (var role in attempted.AsEnumerable().Reverse())
            {
                try { if (string.Equals(read(role), id, StringComparison.OrdinalIgnoreCase)) { write(role, before[role]); if (!string.Equals(read(role), before[role], StringComparison.OrdinalIgnoreCase)) rollbackFailed = true; } }
                catch { rollbackFailed = true; }
            }
            if (rollbackFailed) throw new IOException("输出切换未完成，且原设备未能全部恢复，请检查 Windows 声音设置。", error);
            throw;
        }
    }
}
