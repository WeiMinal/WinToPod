// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using C = System.Windows.Controls;
using W = System.Windows;

namespace WinToPod.Desktop;

// Advanced compatibility tools, deliberately absent from the everyday connection page.
internal sealed class VideoSyncDialog : FluentForm
{
    public VideoSyncDialog(int delayMs, int correctionMs, bool connected, bool bridgeAvailable, Action<int> saveCorrection, Func<string, string, Task> launch, string? preferredPlayer = null, Action<string>? rememberPlayer = null)
    {
        NativeFluentUi.ConfigureDialog(this, "视频兼容工具", 580, 530);
        var stack = NativeFluentUi.Stack(); stack.Children.Add(NativeFluentUi.Text("视频兼容工具", 22, true));
        stack.Children.Add(NativeFluentUi.Text("这部分供特定播放器排查使用。系统级自动音画补偿尚未实现，不需要为了连接音箱先配置这里。"));
        var local = new C.StackPanel(); local.Children.Add(NativeFluentUi.Text("本地视频", 15, true));
        local.Children.Add(NativeFluentUi.Text(connected ? $"本次建议补偿 {delayMs} 毫秒。自动寻找已安装的VLC，也会记住你选择的播放器。" : "连接音箱后可使用兼容播放。", 12));
        var feedback = NativeFluentUi.Text("");
        var open = NativeFluentUi.Button("打开视频", () => { }, true); open.IsEnabled = connected; open.HorizontalAlignment = W.HorizontalAlignment.Left;
        open.Click += async (_, _) =>
        {
            var playerPath = new[] { preferredPlayer, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC", "vlc.exe") }
                .FirstOrDefault(p => p is not null && File.Exists(p) && Path.GetFileName(p).ToLowerInvariant() is "vlc.exe" or "mpv.exe");
            if (playerPath is null)
            {
                using var picker = new OpenFileDialog { Title = "首次选择 VLC 或 mpv", Filter = "播放器|vlc.exe;mpv.exe", CheckFileExists = true };
                if (picker.ShowDialog(this) != DialogResult.OK) return; playerPath = picker.FileName;
            }
            using var media = new OpenFileDialog { Title = "选择视频", Filter = "视频|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.m4v;*.ts|所有文件|*.*", CheckFileExists = true };
            if (media.ShowDialog(this) != DialogResult.OK) return;
            open.IsEnabled = false;
            try { await launch(playerPath, media.FileName); rememberPlayer?.Invoke(playerPath); preferredPlayer = playerPath; feedback.Text = "已启动兼容播放。请按人物口型检查补偿结果。"; }
            catch (Exception e) { feedback.Text = e is ArgumentException ? e.Message : "播放器暂未启动，请检查选择的文件。"; }
            finally { open.IsEnabled = connected; }
        };
        local.Children.Add(open); stack.Children.Add(NativeFluentUi.Card(local));
        var browser = new C.StackPanel(); browser.Children.Add(NativeFluentUi.Text("浏览器助手 · 实验功能", 15, true));
        browser.Children.Add(NativeFluentUi.Text(bridgeAvailable ? "为网站授权后，助手会根据音箱连接状态自动启停；浏览器授权仍需用户确认。受保护视频、原生控制条和画中画等暂不支持。" : "浏览器状态端口暂不可用。", 12));
        browser.Children.Add(NativeFluentUi.Button("打开助手说明", () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "浏览器音画补偿");
            try { Process.Start(new ProcessStartInfo(Directory.Exists(path) ? path : AppContext.BaseDirectory) { UseShellExecute = true }); } catch { feedback.Text = "请打开软件同目录下的“浏览器音画补偿”文件夹。"; }
        })); stack.Children.Add(NativeFluentUi.Card(browser));
        var fine = new C.StackPanel(); var amount = NativeFluentUi.Text($"微调：{correctionMs:+#;-#;0} 毫秒", 14, true);
        var slider = new C.Slider { Minimum = -1500, Maximum = 1500, Value = Math.Clamp(correctionMs, -1500, 1500), TickFrequency = 50, IsSnapToTickEnabled = true, Margin = new W.Thickness(0, 10, 0, 10) };
        slider.ValueChanged += (_, _) => amount.Text = $"微调：{(int)slider.Value:+#;-#;0} 毫秒";
        fine.Children.Add(amount); fine.Children.Add(slider); fine.Children.Add(NativeFluentUi.Text("正数让画面更晚，负数更早；更改后重新打开视频。", 12));
        stack.Children.Add(new C.Expander { Header = "高级微调", Content = fine, IsExpanded = false, Margin = new W.Thickness(0, 12, 0, 4) }); stack.Children.Add(feedback);
        NativeFluentUi.Mount(this, NativeFluentUi.Dialog(stack, NativeFluentUi.Button("保存微调", () => { saveCorrection((int)slider.Value); feedback.Text = "微调已保存。"; }), NativeFluentUi.Button("关闭", () => Close())));
    }
}
