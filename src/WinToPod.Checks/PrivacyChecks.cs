// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.Core;

internal static class PrivacyChecks
{
    public static IEnumerable<(string, Func<Task>)> All()
    {
        yield return ("诊断：IP和硬件地址不进入导出", () =>
        {
            using var r = new DiagnosticRedactor([], []);
            var result = r.Redact("peer 192.0.2.7 mac AA:BB:CC:DD:EE:01 v6 fe80::1234%9");
            if (result.Contains("192.0.2") || result.Contains("AA:BB") || result.Contains("fe80")) throw new Exception(result);
            return Task.CompletedTask;
        });
        yield return ("诊断：发现名称与发送身份被替换", () =>
        {
            using var r = new DiagnosticRedactor(["Fixture speaker", "fixture-controller"], []);
            var result = r.Redact("Fixture speaker / fixture-controller");
            if (result.Contains("Fixture", StringComparison.OrdinalIgnoreCase)) throw new Exception(result);
            return Task.CompletedTask;
        });
        yield return ("诊断：含空格的本地文件路径被隐藏", () =>
        {
            using var r = new DiagnosticRedactor([], [@"C:\Users\Fixture Person"]);
            var result = r.Redact("failed: C:\\Users\\Fixture Person\\private movie.mp4\nHRESULT 0x80070005");
            if (result.Contains("Fixture") || result.Contains("movie") || !result.Contains("0x80070005")) throw new Exception(result);
            return Task.CompletedTask;
        });
        yield return ("诊断：时刻和协议计数保留用于排障", () =>
        {
            using var r = new DiagnosticRedactor([], []);
            const string line = "21:17:20 RTSP SET_PARAMETER 200 packets=450";
            if (r.Redact(line) != line) throw new Exception("Non-private diagnostic fields were removed.");
            return Task.CompletedTask;
        });
        yield return ("诊断：同一导出可关联成员，不同导出不可直接关联", () =>
        {
            using var a = new DiagnosticRedactor([], []); using var b = new DiagnosticRedactor([], []);
            if (a.Token("fixture") != a.Token("fixture") || a.Token("fixture") == b.Token("fixture")) throw new Exception("Export tokens are not scoped.");
            return Task.CompletedTask;
        });
    }
}
