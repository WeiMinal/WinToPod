// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.Desktop;
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        BrandAssets.SetShellIdentity();
        ApplicationConfiguration.Initialize();
        NativeFluentUi.Initialize();
#pragma warning disable WFO5001 // Official WinForms system color mode is experimental in .NET 10.
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
        FluentTheme.Refresh();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        if (args.Length == 2 && args[0] == "--self-check")
        {
            try { DesktopSelfCheck.Run(args[1]); }
            catch (Exception e) { Environment.ExitCode = 1; File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new { passed = false, error = e.ToString() })); }
            return;
        }
        using var instance = new Mutex(true, @"Local\WinToPod.Desktop", out var created);
        if (!created)
        {
            NativeFluentUi.ShowMessage("WinToPod 已在运行", "另一个WinToPod正在运行，可能是旧程序。\n\n请从任务栏右下角托盘中退出已有WinToPod，再重新双击当前文件。当前文件尚未启动，不会替换正在运行的窗口。\n\n本次文件：" + Path.GetFileName(Application.ExecutablePath) + "\n位置：" + Application.ExecutablePath);
            return;
        }
        try { Application.Run(new MainForm(DesktopSettings.Load(), args.Contains("--startup"))); }
        catch (Exception e)
        {
            NativeFluentUi.ShowMessage("无法启动", "WinToPod无法启动：\n" + e.Message + "\n\n请保留原设置文件，避免丢失音量与让出状态。");
        }
    }
}
