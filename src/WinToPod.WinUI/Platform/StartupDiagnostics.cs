// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.Desktop;
namespace WinToPod.WinUI;
internal static class StartupDiagnostics
{
    private static string? _readyEvent;
    private static string _stage = "进入托管入口";
    public static bool Ready { get; private set; }
    public static void Begin(string[] args)
    {
        var i = Array.IndexOf(args, "--ready-event");
        if (i >= 0 && i + 1 < args.Length && args[i + 1].StartsWith(@"Local\WinToPod.Startup.", StringComparison.Ordinal)) _readyEvent = args[i + 1];
        Stage(_stage);
    }
    public static void Stage(string value)
    {
        _stage = value;
        try { Directory.CreateDirectory(DesktopSettings.DirectoryPath); File.AppendAllText(Path.Combine(DesktopSettings.DirectoryPath, "winui-startup.log"), DateTimeOffset.Now.ToString("O") + "  " + value + Environment.NewLine); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public static void WindowReady()
    {
        Ready = true; Stage("窗口已加载");
        if (_readyEvent is null) return;
        try { using var signal = EventWaitHandle.OpenExisting(_readyEvent); signal.Set(); }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { }
    }
    public static void RecordFailure(Exception error)
    {
        try { Directory.CreateDirectory(DesktopSettings.DirectoryPath); File.WriteAllText(Path.Combine(DesktopSettings.DirectoryPath, "winui-startup-error.txt"), "失败阶段：" + _stage + Environment.NewLine + error); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public static void Fail(Exception error)
    {
        RecordFailure(error); Environment.ExitCode = 1;
        if (_readyEvent is null) NativeWindow.MessageBox(IntPtr.Zero, "WinToPod 无法启动：\n" + error.Message, "WinToPod", 0x10);
    }
}
