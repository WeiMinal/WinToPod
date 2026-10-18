// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.Core;
namespace WinToPod.Desktop;

internal sealed class PasswordDialog : FluentForm
{
    private readonly System.Windows.Controls.PasswordBox _value = new() { MaxLength = 128, MinHeight = 38, Margin = new System.Windows.Thickness(0, 8, 0, 16) };
    private readonly System.Windows.Controls.CheckBox _remember = new() { Content = "记住此音箱，下次连接免输播放密码", IsChecked = true, Margin = new System.Windows.Thickness(0, 4, 0, 12) };
    public string Password => _value.Password;
    public bool RememberPlayback => _remember.IsChecked == true;
    public PasswordDialog(string target, bool rejected, bool forPlayback = false)
    {
        NativeFluentUi.ConfigureDialog(this, "输入 AirPlay 密码", 520, 390);
        var stack = NativeFluentUi.Stack(); stack.Children.Add(NativeFluentUi.Text("连接 " + target, 22, true));
        stack.Children.Add(NativeFluentUi.Text(forPlayback ? "音箱已配对，播放还需要家庭AirPlay密码。" : rejected ? "验证未通过。请核对密码和音箱的访问权限。" : "此音箱需要AirPlay密码。"));
        stack.Children.Add(NativeFluentUi.Text("家庭App → 家庭设置 → 扬声器与电视", 12));
        stack.Children.Add(_value); stack.Children.Add(_remember);
        var privacy = NativeFluentUi.Text("仅当前Windows账户加密保存，可在设置中忘记。", 12); privacy.Foreground = NativeFluentUi.Secondary; stack.Children.Add(privacy);
        void Submit() { if (_value.Password.Length > 0) { DialogResult = DialogResult.OK; Close(); } }
        var confirm = NativeFluentUi.Button(forPlayback ? "验证并连接" : "配对并连接", Submit, true); confirm.IsEnabled = false;
        _value.PasswordChanged += (_, _) => confirm.IsEnabled = _value.Password.Length > 0;
        _value.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; Submit(); } };
        NativeFluentUi.Mount(this, NativeFluentUi.Dialog(stack, confirm, NativeFluentUi.Button("取消", () => { DialogResult = DialogResult.Cancel; Close(); })));
        Shown += (_, _) => _value.Focus();
    }
    protected override void Dispose(bool disposing) { if (disposing) _value.Clear(); base.Dispose(disposing); }
}

internal sealed record CaptureChoice(string Name, string? Id) { public override string ToString() => Name; }

internal sealed class SingleFallbackDialog : FluentForm
{
    private readonly System.Windows.Controls.CheckBox _remember = new() { Content = "下次先用单只，可在设置中恢复双只", IsChecked = true, Margin = new System.Windows.Thickness(0, 10, 0, 4) };
    public bool RememberChoice => _remember.IsChecked == true;
    public SingleFallbackDialog(string memberName, bool duringPlayback = false)
    {
        NativeFluentUi.ConfigureDialog(this, "先用一只音箱", 540, 330);
        var stack = NativeFluentUi.Stack(); stack.Children.Add(NativeFluentUi.Text("另一只音箱暂时无法连接", 22, true));
        stack.Children.Add(NativeFluentUi.Text(duringPlayback ? "另一只已配对，但播放验证未通过。" : "另一只暂未通过连接验证。"));
        stack.Children.Add(NativeFluentUi.Text($"可以先用「{memberName}」播放。左右声道合并，家庭App中的原生组合保留。")); stack.Children.Add(_remember);
        NativeFluentUi.Mount(this, NativeFluentUi.Dialog(stack,
            NativeFluentUi.Button("先用" + memberName + "播放", () => { DialogResult = DialogResult.OK; Close(); }, true),
            NativeFluentUi.Button("暂不连接", () => { DialogResult = DialogResult.Cancel; Close(); })));
    }
}

internal sealed class DetailsDialog : FluentForm
{
    public DetailsDialog(string text, Action export)
    {
        NativeFluentUi.ConfigureDialog(this, "连接详情", 740, 530); MaximizeBox = true;
        var root = new System.Windows.Controls.DockPanel { LastChildFill = true, Margin = new System.Windows.Thickness(24) };
        var footer = new System.Windows.Controls.WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 14, 0, 0) };
        footer.Children.Add(NativeFluentUi.Button("导出诊断", export, true)); footer.Children.Add(NativeFluentUi.Button("关闭", () => Close()));
        System.Windows.Controls.DockPanel.SetDock(footer, System.Windows.Controls.Dock.Bottom); root.Children.Add(footer);
        var heading = NativeFluentUi.Text("连接详情与诊断", 22, true); System.Windows.Controls.DockPanel.SetDock(heading, System.Windows.Controls.Dock.Top); root.Children.Add(heading);
        root.Children.Add(new System.Windows.Controls.TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = System.Windows.TextWrapping.Wrap, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Microsoft YaHei UI"), FontSize = 12, Padding = new System.Windows.Thickness(12) });
        NativeFluentUi.Mount(this, root);
    }
}
