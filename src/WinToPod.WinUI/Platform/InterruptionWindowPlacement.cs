// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace WinToPod.WinUI;

internal static class InterruptionWindowPlacement
{
    public static void Show(Window notice, Window fallback)
    {
        // Activate alone does not make a background-created window topmost.
        // Keep this short-lived notice above ordinary windows, never the panel.
        if (notice.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
        }
        var foreground = NativeMethods.GetForegroundWindow();
        var display = foreground == IntPtr.Zero
            ? DisplayArea.GetFromWindowId(fallback.AppWindow.Id, DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(foreground), DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        // Move to the chosen monitor before reading DPI: the hidden main panel
        // may live on a different monitor with a different scale factor.
        notice.AppWindow.Move(new PointInt32(work.X + work.Width / 2, work.Y + work.Height / 2));
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(notice);
        var scale = Math.Max(96, NativeMethods.GetDpiForWindow(handle)) / 96d;
        notice.AppWindow.ResizeClient(new SizeInt32(Math.Min((int)(460 * scale), Math.Max(1, work.Width - 48)),
            Math.Min((int)(280 * scale), Math.Max(1, work.Height - 80))));
        var size = notice.AppWindow.Size;
        notice.AppWindow.Move(new PointInt32(work.X + Math.Max(0, (work.Width - size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
        notice.Activate();
    }
}
