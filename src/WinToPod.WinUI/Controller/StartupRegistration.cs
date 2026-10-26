// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32;
namespace WinToPod.Desktop;
// Only an explicit preference change redirects an existing login entry.
internal static class StartupRegistration
{
    private static string? Value
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("WinToPod") as string; }
    }
    private static string EntryPath
    {
        get
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            var launcher = folder.Name.Equals("app", StringComparison.OrdinalIgnoreCase) && folder.Parent is not null ? Path.Combine(folder.Parent.FullName, "WinToPod.exe") : null;
            return launcher is not null && File.Exists(launcher) ? launcher : Environment.ProcessPath ?? throw new IOException("无法确定程序路径。");
        }
    }
    private static string Command => "\"" + EntryPath + "\" --startup";
    public static bool IsCurrent => string.Equals(Value, Command, StringComparison.OrdinalIgnoreCase);
    public static bool HasOther => Value is not null && !IsCurrent;
    public static void Apply(bool enabled)
    {
        if (enabled && !IsCurrent) DesktopSettings.SetStartAtLogin(true, EntryPath);
        else if (!enabled && IsCurrent) DesktopSettings.SetStartAtLogin(false);
    }
}
