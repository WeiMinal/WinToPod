// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinToPod.Desktop;

internal static class FluentTheme
{
    public static bool HighContrast => SystemInformation.HighContrast;
    public static bool Dark { get; private set; }
    public static Color Background => HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
    public static Color Surface => HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(251, 251, 251);
    public static Color Border => HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(68, 68, 68) : Color.FromArgb(222, 222, 222);
    public static Color Text => HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(244, 244, 244) : Color.FromArgb(26, 26, 26);
    public static Color Secondary => HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(192, 192, 192) : Color.FromArgb(96, 96, 96);
    public static Color Accent => HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 103, 192);
    public static Color AccentText => HighContrast ? SystemColors.HighlightText : Dark ? Color.FromArgb(15, 15, 15) : Color.White;
    public static Color Selection => HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(46, 57, 64) : Color.FromArgb(233, 241, 249);
    public static Color Error => HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(164, 38, 44);

    public static void Refresh()
    {
        Dark = false;
        if (HighContrast) { NativeFluentUi.RefreshPalette(); return; }
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); Dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0; }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        NativeFluentUi.RefreshPalette();
    }
    public static void Apply(Control control)
    {
        control.BackColor = control is TextBoxBase or ComboBox ? Surface : Background;
        control.ForeColor = control.Tag as string == "error" ? Error : control.Tag as string == "secondary" ? Secondary : Text;
        if (control is LinkLabel link) { link.LinkColor = Accent; link.ActiveLinkColor = Accent; link.VisitedLinkColor = Accent; }
        foreach (Control child in control.Controls) Apply(child);
        control.Invalidate();
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    public static void ApplyWindow(Form form)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !form.IsHandleCreated) return;
        var dark = Dark && !HighContrast ? 1 : 0; var corner = 2; var backdrop = HighContrast ? 1 : 2;
        _ = DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(form.Handle, 33, ref corner, sizeof(int));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) _ = DwmSetWindowAttribute(form.Handle, 38, ref backdrop, sizeof(int));
    }
}

internal class FluentForm : Form
{
    private Icon? _brandIcon, _shellIcon;
    public FluentForm() { ApplyBrand(); Font = new Font("Microsoft YaHei UI", 10); }
    private void ApplyBrand()
    {
        var previous = _brandIcon; var previousShell = _shellIcon;
        Icon = _brandIcon = BrandAssets.WindowIcon(); _shellIcon = BrandAssets.ShellIcon();
        // The app and taskbar can have different themes. Keep the small title
        // icon on the app palette and the large shell icon on the taskbar one.
        if (IsHandleCreated) _ = SendMessage(Handle, 0x0080, new IntPtr(1), _shellIcon.Handle);
        previous?.Dispose(); previousShell?.Dispose();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    protected virtual void OnAppThemeChanged() { }
    protected override void OnHandleCreated(EventArgs e)
    { base.OnHandleCreated(e); ApplyTheme(); }
    private void ApplyTheme() { FluentTheme.Refresh(); FluentTheme.Apply(this); FluentTheme.ApplyWindow(this); ApplyBrand(); OnAppThemeChanged(); }
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg is 0x001A or 0x031A) ApplyTheme();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); ApplyBrand(); }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) { _brandIcon?.Dispose(); _shellIcon?.Dispose(); _brandIcon = _shellIcon = null; } }
}

