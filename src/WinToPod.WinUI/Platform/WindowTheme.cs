// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Windowing;
using Microsoft.Win32;
using WinToPod.Desktop;

namespace WinToPod.WinUI;

internal static class WindowTheme
{
    public static ElementTheme Resolve(ApplicationAppearance appearance)
    {
        if (appearance == ApplicationAppearance.Light) return ElementTheme.Light;
        if (appearance == ApplicationAppearance.Dark) return ElementTheme.Dark;
        // Windows permits different shell and application modes. Follow the
        // application mode here; the tray icon still follows the shell mode.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? ElementTheme.Dark : ElementTheme.Light;
    }

    public static void Apply(Window window, ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        // Keep the native caption, drag behavior and system caption buttons.
        // XAML's ActualTheme does not opt the non-client title bar into dark.
        var captionTheme = dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        if (window.AppWindow.TitleBar.PreferredTheme != captionTheme) window.AppWindow.TitleBar.PreferredTheme = captionTheme;
        // Caption and taskbar can have different backgrounds when the app
        // appearance overrides Windows. Keep their native icon slots separate.
        window.AppWindow.SetTitleBarIcon(IconPath(dark));
        window.AppWindow.SetTaskbarIcon(IconPath(NativeWindow.ShellDark));
    }
    private static string IconPath(bool darkBackground) => Path.Combine(AppContext.BaseDirectory,
        "Assets", "Brand", darkBackground ? "WinToPod-dark.ico" : "WinToPod.ico");
}
