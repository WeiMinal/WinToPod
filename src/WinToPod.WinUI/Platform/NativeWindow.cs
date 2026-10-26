// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
namespace WinToPod.WinUI;
internal sealed class NativeWindow : IDisposable
{
    private readonly IntPtr _handle;
    private readonly SubclassProc _procedure;
    private readonly Action<int, IntPtr, IntPtr> _message;
    public IntPtr Handle => _handle;
    public NativeWindow(Window window, Action<int, IntPtr, IntPtr> message)
    {
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(window); _message = message; _procedure = Callback;
        if (!SetWindowSubclass(_handle, _procedure, 1, 0)) throw new IOException("无法初始化窗口消息。");
    }
    private IntPtr Callback(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    { _message((int)message, wParam, lParam); return DefSubclassProc(hwnd, message, wParam, lParam); }
    public void Dispose() => _ = RemoveWindowSubclass(_handle, _procedure, 1);
    public static void Show(IntPtr handle) { _ = ShowWindow(handle, 9); _ = SetForegroundWindow(handle); }
    public static void Hide(IntPtr handle) => _ = ShowWindow(handle, 0);
    public static void SetIdentity() => _ = SetCurrentProcessExplicitAppUserModelID("WinToPod.Desktop");
    public static bool ShellDark
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not int value || value == 0;
        }
    }
    public static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBox(IntPtr hwnd, string text, string title, uint type);
}
