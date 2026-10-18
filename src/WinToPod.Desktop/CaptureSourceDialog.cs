// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.Desktop;

internal sealed class CaptureSourceDialog : FluentForm
{
    private readonly ComboBox _sources = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, IntegralHeight = true, MaxDropDownItems = 8, Margin = new Padding(0, 8, 0, 8) };
    public string? CaptureId => (_sources.SelectedItem as CaptureChoice)?.Id;
    public CaptureSourceDialog(IEnumerable<CaptureChoice> sources, string? selectedId, bool connected)
    {
        Text = "电脑声音来源"; Font = new Font("Segoe UI", 9); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 190); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var column = SettingsDialog.Column(); column.Padding = new Padding(16); Controls.Add(column);
        column.Controls.Add(SettingsDialog.Hint("选择全部应用声音，或指定一个输出设备。")); column.Controls.Add(_sources);
        foreach (var choice in sources) _sources.Items.Add(choice); _sources.SelectedIndex = 0;
        for (var i = 0; i < _sources.Items.Count; i++) if (((CaptureChoice)_sources.Items[i]!).Id == selectedId) _sources.SelectedIndex = i;
        column.Controls.Add(SettingsDialog.Hint(connected ? "更改在下次连接生效，当前播放继续。" : "推荐发送全部应用声音；指定设备仅发送该输出的声音。"));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var confirm = SettingsDialog.ActionButton("确定", () => { DialogResult = DialogResult.OK; Close(); });
        var cancel = SettingsDialog.ActionButton("取消", () => { DialogResult = DialogResult.Cancel; Close(); }); cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(confirm); actions.Controls.Add(cancel); column.Controls.Add(actions); AcceptButton = confirm; CancelButton = cancel; CompactDialogLayout.Fit(this, content: column);
    }
}
