// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
namespace WinToPod.Desktop;
internal static class NativeTrayMenu
{
    internal sealed record Item(string Text, Action Action);
    public static void Show(Form owner, params Item?[] items)
    {
        var menu = CreatePopupMenu(); if (menu == IntPtr.Zero) return;
        try
        {
            for (var i = 0; i < items.Length; i++) if (items[i] is { } item) AppendMenu(menu, 0, (nuint)(i + 1), item.Text); else AppendMenu(menu, 0x800, 0, null);
            var p = Cursor.Position; _ = SetForegroundWindow(owner.Handle);
            var id = TrackPopupMenuEx(menu, 0x4182, p.X, p.Y, owner.Handle, IntPtr.Zero); _ = PostMessage(owner.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            if (id > 0 && id <= items.Length && items[id - 1] is { } chosen) owner.BeginInvoke(chosen.Action);
        }
        finally { _ = DestroyMenu(menu); }
    }
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
