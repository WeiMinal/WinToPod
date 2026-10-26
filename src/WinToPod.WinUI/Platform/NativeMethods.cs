// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
namespace WinToPod.WinUI;
internal static class NativeMethods
{
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public static void SetMinimumSize(IntPtr window, IntPtr pointer, int width, int height)
    {
        var scale = GetDpiForWindow(window) / 96d;
        var data = Marshal.PtrToStructure<MinMaxInfo>(pointer);
        data.MinTrackSize = new Point { X = (int)(width * scale), Y = (int)(height * scale) }; Marshal.StructureToPtr(data, pointer, false);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
}
