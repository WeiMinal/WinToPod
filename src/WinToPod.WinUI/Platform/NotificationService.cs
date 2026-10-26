// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
namespace WinToPod.WinUI;
internal sealed class NotificationService : IDisposable
{
    private readonly Action _show;
    private readonly TrayIcon _tray;
    private bool _registered;
    public NotificationService(Action show, TrayIcon tray, bool enabled = true)
    {
        _show = show; _tray = tray; if (!enabled) return;
        try
        {
            if (!AppNotificationManager.IsSupported()) return;
            AppNotificationManager.Default.NotificationInvoked += Invoked;
            AppNotificationManager.Default.Register(); _registered = true;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
    }
    private void Invoked(AppNotificationManager manager, AppNotificationActivatedEventArgs args) => _show();
    public void Show(string title, string message)
    {
        if (_registered)
        {
            try { AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText("WinToPod · " + title).AddText(message).BuildNotification()); return; }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        }
        _tray.Balloon(title, message);
    }
    public void Dispose()
    {
        if (!_registered) return;
        AppNotificationManager.Default.NotificationInvoked -= Invoked;
        try { AppNotificationManager.Default.Unregister(); } catch (System.Runtime.InteropServices.COMException) { }
    }
}
