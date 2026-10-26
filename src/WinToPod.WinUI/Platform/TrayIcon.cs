// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
namespace WinToPod.WinUI;
internal sealed class TrayIcon : IDisposable
{
    public const int CallbackMessage = 0x8000 + 47;
    private readonly IntPtr _window;
    private IntPtr _icon;
    private bool _added;
    private readonly bool _enabled;
    public TrayIcon(IntPtr window, bool enabled = true) { _window = window; _enabled = enabled; if (enabled) Update(); }
    private NotificationData Data() => new() { Size = (uint)Marshal.SizeOf<NotificationData>(), Window = _window, Id = 1, Flags = 7, CallbackMessage = CallbackMessage, Icon = _icon, Tip = "WinToPod", Info = "", InfoTitle = "" };
    public void Update()
    {
        if (!_enabled) return;
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", NativeWindow.ShellDark ? "WinToPod-dark.ico" : "WinToPod.ico");
        var next = LoadImage(IntPtr.Zero, file, 1, 32, 32, 0x10);
        if (next == IntPtr.Zero) return;
        var old = _icon; _icon = next; var data = Data(); _added = ShellNotifyIcon(_added ? 1u : 0u, ref data);
        if (_added) { data.TimeoutOrVersion = 4; _ = ShellNotifyIcon(4, ref data); }
        if (old != IntPtr.Zero) _ = DestroyIcon(old);
    }
    public void Restore() { _added = false; Update(); }
    public void Balloon(string title, string message)
    {
        var data = Data(); data.Flags = 0x10; data.InfoTitle = title; data.Info = message; data.InfoFlags = 1;
        _ = ShellNotifyIcon(1, ref data);
    }
    public void Menu(IReadOnlyList<(string? Text, Action? Action)> items)
    {
        var menu = CreatePopupMenu(); if (menu == IntPtr.Zero) return;
        try
        {
            for (var i = 0; i < items.Count; i++) _ = AppendMenu(menu, items[i].Text is null ? 0x800u : 0u, (nuint)(i + 1), items[i].Text);
            _ = GetCursorPos(out var point); _ = SetForegroundWindow(_window);
            var selected = TrackPopupMenuEx(menu, 0x4182, point.X, point.Y, _window, IntPtr.Zero);
            _ = PostMessage(_window, 0, IntPtr.Zero, IntPtr.Zero);
            if (selected > 0 && selected <= items.Count) items[(int)selected - 1].Action?.Invoke();
        }
        finally { _ = DestroyMenu(menu); }
    }
    public void Dispose()
    { if (!_enabled) return; var data = Data(); _ = ShellNotifyIcon(2, ref data); if (_icon != IntPtr.Zero) _ = DestroyIcon(_icon); _icon = IntPtr.Zero; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotificationData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint action, ref NotificationData data);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string file, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
