// SPDX-License-Identifier: GPL-3.0-or-later
internal static class Base01Diagnostics
{
    // Export only the new numeric lines, never RTSP, addresses or fixture secrets.
    public static void Write(IEnumerable<string> trace)
    {
        foreach (var line in trace)
            if (line.Contains("到包间隔桶[", StringComparison.Ordinal) || line.Contains("最大落后ms=", StringComparison.Ordinal))
                Console.Error.WriteLine(line);
    }
}
