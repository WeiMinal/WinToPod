// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinToPod.Desktop;

internal static class BrandAssets
{
    private static Stream Open(string file) => typeof(BrandAssets).Assembly.GetManifestResourceStream("WinToPod.Brand." + file) ?? throw new IOException("应用标识资源缺失。");
    private static bool DarkApp => SystemInformation.HighContrast ? SystemColors.Window.GetBrightness() < .5 : FluentTheme.Dark;
    private static bool DarkShell
    {
        get
        {
            if (SystemInformation.HighContrast) return SystemColors.Window.GetBrightness() < .5;
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("SystemUsesLightTheme") is not int value || value == 0; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return true; }
        }
    }
    private static Icon ReadIcon(bool dark, Size? size = null)
    { using var stream = Open(dark ? "WinToPod-dark.ico" : "WinToPod.ico"); using var icon = size is { } dimensions ? new Icon(stream, dimensions) : new Icon(stream); return (Icon)icon.Clone(); }
    public static Icon WindowIcon() => ReadIcon(DarkApp);
    public static Icon ShellIcon() => ReadIcon(DarkShell);
    public static Icon TrayIcon() => ReadIcon(DarkShell, new Size(32, 32));
    public static System.Drawing.Image PanelImage()
    { using var stream = Open(DarkApp ? "WinToPod-white-128.png" : "WinToPod-black-128.png"); using var bitmap = System.Drawing.Image.FromStream(stream); return new Bitmap(bitmap); }
    public static void SetShellIdentity() => _ = SetCurrentProcessExplicitAppUserModelID("WinToPod.Desktop");
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
