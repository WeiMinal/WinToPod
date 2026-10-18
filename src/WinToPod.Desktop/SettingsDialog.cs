// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.Core;

namespace WinToPod.Desktop;

// The requested reference is a native Windows property sheet. Use the system's
// actual common controls and 9pt dialog font, not large WPF Fluent templates.
internal sealed class SettingsDialog : FluentForm
{
    private PictureBox? _logo;
    private readonly CheckBox _hide = Check("关闭窗口后继续在托盘运行"), _startup = Check("登录 Windows 后启动"),
        _single = Check("这组音箱暂用单只兼容播放"), _auto = Check("本机使用时自动连接上次音箱");
    private readonly RadioButton _conservative = Mode("保守模式（推荐）"), _aggressive = Mode("激进模式");
    public ConnectionPolicy Policy => _aggressive.Checked ? ConnectionPolicy.Aggressive : ConnectionPolicy.Conservative;
    public bool AutoConnectOnStart => _auto.Checked;
    public bool HideOnClose => _hide.Checked;
    public bool StartAtLogin => _startup.Checked;
    public bool PreferSingle => _single.Checked;
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = false, UseVisualStyleBackColor = true, Margin = new Padding(0, 3, 0, 3) };
    private static RadioButton Mode(string text) => new() { Text = text, AutoSize = false, UseVisualStyleBackColor = true, Margin = new Padding(0, 3, 0, 3) };
    internal static Label Hint(string text) => new() { Text = text, AutoSize = false, Tag = "secondary", Margin = new Padding(0, 3, 0, 5), UseMnemonic = false };
    internal static Button ActionButton(string text, Action action, string name = "")
    {
        var button = new Button { Text = text, Name = name, AutoSize = true, MinimumSize = new Size(76, 26), Padding = new Padding(8, 1, 8, 1), Margin = new Padding(0, 4, 8, 4), UseVisualStyleBackColor = true };
        button.Click += (_, _) => action(); return button;
    }
    internal static CompactColumn Column() => new() { Dock = DockStyle.Top };
    private static CompactColumn Section(Control parent, string title, string name = "")
    {
        var group = new CompactGroup { Text = title, Name = name, Margin = new Padding(0, 0, 0, 10) };
        parent.Controls.Add(group); return group.Content;
    }
    private static FlowLayoutPanel Actions(params Control[] controls)
    { var flow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty, WrapContents = true }; flow.Controls.AddRange(controls); return flow; }
    public SettingsDialog(bool hide, bool startup, bool testAvailable, bool connected = false, bool nativePair = false, bool preferSingle = false, Action? toggleSoundTest = null, Func<(bool Available, bool Active, bool CanStop, string Description)>? soundTestState = null, Func<string>? forgetPlaybackCredentials = null, Action<Form>? videoTools = null, ConnectionPolicy policy = ConnectionPolicy.Conservative, bool autoConnect = true, Func<string>? browserStatus = null, Action<Form>? showDiagnostics = null, Action<Form>? exportDiagnostics = null)
    {
        Text = "设置"; Font = new Font("Segoe UI", 9); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 450); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
        var tabs = new TabControl { Dock = DockStyle.Fill, Name = "SettingsTabs", Padding = new Point(8, 3) }; root.Controls.Add(tabs, 0, 0);
        CompactColumn Page(string title)
        {
            var page = new TabPage(title) { Padding = new Padding(10), UseVisualStyleBackColor = true, AutoScroll = false };
            var column = Column(); page.Controls.Add(column); tabs.TabPages.Add(page); return column;
        }
        var general = Page("常规"); var video = Page("音画补偿"); var diagnostic = Page("诊断"); var about = Page("关于");
        _conservative.Checked = policy == ConnectionPolicy.Conservative; _aggressive.Checked = policy == ConnectionPolicy.Aggressive;
        _auto.Checked = autoConnect; _hide.Checked = hide; _startup.Checked = startup; _single.Checked = preferSingle;
        var modes = Section(general, "音箱使用权", "OwnershipSettings"); modes.Controls.Add(_conservative);
        var conservativeHint = Hint("让出后提醒，等你手动重新连接。"); conservativeHint.Margin = new Padding(18, 0, 0, 4); modes.Controls.Add(conservativeHint);
        modes.Controls.Add(_aggressive); var aggressiveHint = Hint("近期有本机操作时有限重连，最多 3 次，可能打断其他设备。"); aggressiveHint.Margin = new Padding(18, 0, 0, 0); modes.Controls.Add(aggressiveHint);
        var start = Section(general, "启动与关闭"); start.Controls.Add(_startup); start.Controls.Add(_auto); start.Controls.Add(_hide);
        start.Controls.Add(Hint("成功连接后记住音箱；启动后等本机操作再连接，远程或锁屏先待机。"));
        general.Controls.Add(ActionButton("更多连接选项…", () =>
        {
            using var dialog = new ConnectionOptionsDialog(nativePair, _single.Checked, forgetPlaybackCredentials);
            if (dialog.ShowDialog(this) == DialogResult.OK) _single.Checked = dialog.PreferSingle;
        }));
        var web = Section(video, "网页音画补偿"); var syncStatus = Hint(browserStatus?.Invoke() ?? "尚未检测到浏览器助手。"); web.Controls.Add(syncStatus);
        web.Controls.Add(ActionButton("安装或更新浏览器助手", () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "浏览器音画补偿");
            if (!Directory.Exists(folder)) { MessageBox.Show(this, "请完整解压 WinToPod，再从文件夹内启动。", "需要完整程序文件夹"); return; }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
                MessageBox.Show(this, "在 Edge 或 Chrome 扩展管理中开启开发人员模式，选择“加载解压缩的扩展”，选中刚打开的文件夹。\n\n首次授权后 B 站自动跟随音箱；旧助手请重新加载。", "首次安装浏览器助手");
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { syncStatus.Text = "请打开程序文件夹中的“浏览器音画补偿”，按其中说明安装。"; }
        }));
        web.Controls.Add(Hint("首次安装授权后自动跟随连接，无需填写延迟。"));
        video.Controls.Add(Hint("系统级全局音画补偿仍在研究，当前无法替所有应用自动补偿。"));
        var tools = ActionButton("其他播放器与微调…", () => videoTools?.Invoke(this)); tools.Enabled = videoTools is not null; video.Controls.Add(tools);
        var logs = Section(diagnostic, "连接详情与诊断"); logs.Controls.Add(Hint("查看当前连接或导出记录，文件不会自动上传。"));
        var details = ActionButton("查看连接详情", () => showDiagnostics?.Invoke(this), "ShowDiagnosticsAction"); details.Enabled = showDiagnostics is not null;
        var export = ActionButton("导出诊断", () => exportDiagnostics?.Invoke(this), "ExportDiagnosticsAction"); export.Enabled = exportDiagnostics is not null;
        logs.Controls.Add(Actions(details, export)); logs.Controls.Add(Hint("导出不含密码、密钥或录音；分享前仍可检查内容。"));
        var sound = Section(diagnostic, "声音检查"); var soundHint = Hint(""); sound.Controls.Add(soundHint);
        var test = ActionButton("测试当前音箱", () => toggleSoundTest?.Invoke(), "TestSoundAction"); sound.Controls.Add(test);
        void RefreshTest()
        {
            var state = soundTestState?.Invoke() ?? (Available: testAvailable && connected, Active: false, CanStop: false, Description: connected ? "依次播放左、右、一起，结束后恢复电脑声音。" : "先连接音箱，再测试当前连接。");
            test.Enabled = toggleSoundTest is not null && state.Available && (!state.Active || state.CanStop);
            test.Text = state.Active ? state.CanStop ? "停止测试" : "正在恢复声音…" : "测试当前音箱"; soundHint.Text = state.Description;
        }
        var logo = _logo = new PictureBox { Image = BrandAssets.PanelImage(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(56, 56), MinimumSize = new Size(56, 56), MaximumSize = new Size(56, 56), AccessibleName = "WinToPod" };
        about.Controls.Add(logo); Disposed += (_, _) => logo.Image?.Dispose();
        var product = Section(about, "WinToPod");
        product.Controls.Add(Hint("版本 V" + Application.ProductVersion));
        product.Controls.Add(Hint("将 Windows 声音发送到 HomePod / HomePod mini。\n支持单只、原生立体声对、音量联动及本机自动连接。\n多只独立音箱同步与全局音画补偿仍在开发。"));
        about.Controls.Add(Hint("独立项目，与 Apple、Microsoft 无关联。"));
        about.Controls.Add(Hint("开源许可：GPL-3.0-or-later"));
        var save = ActionButton("确定", () => { DialogResult = DialogResult.OK; Close(); });
        var cancel = ActionButton("取消", () => { DialogResult = DialogResult.Cancel; Close(); }); cancel.DialogResult = DialogResult.Cancel;
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0), Margin = Padding.Empty };
        footer.Controls.Add(cancel); footer.Controls.Add(save); root.Controls.Add(footer, 0, 1); AcceptButton = save; CancelButton = cancel;
        CompactDialogLayout.Fit(this, tabs);
        var refresh = new System.Windows.Forms.Timer { Interval = 500 }; refresh.Tick += (_, _) => { RefreshTest(); syncStatus.Text = browserStatus?.Invoke() ?? "尚未检测到浏览器助手。"; };
        Shown += (_, _) => { RefreshTest(); refresh.Start(); }; FormClosed += (_, _) => refresh.Stop(); Disposed += (_, _) => refresh.Dispose(); RefreshTest();
    }
    protected override void OnAppThemeChanged()
    { if (_logo is not null) { var previous = _logo.Image; _logo.Image = BrandAssets.PanelImage(); previous?.Dispose(); } }
}

internal sealed class ConnectionOptionsDialog : FluentForm
{
    private readonly CheckBox _single = new() { Text = "这组音箱暂用单只兼容播放", AutoSize = true, UseVisualStyleBackColor = true };
    public bool PreferSingle => _single.Checked;
    public ConnectionOptionsDialog(bool nativePair, bool preferSingle, Func<string>? forget)
    {
        Text = "更多连接选项"; Font = new Font("Segoe UI", 9); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 310); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var column = SettingsDialog.Column(); column.Padding = new Padding(16); Controls.Add(column);
        column.Controls.Add(SettingsDialog.Hint("手动断开会暂停本次自动连接。特殊远控可能无法准确识别，可关闭自动连接。"));
        _single.Checked = preferSingle; _single.Visible = nativePair; column.Controls.Add(_single);
        if (nativePair) column.Controls.Add(SettingsDialog.Hint("下次连接生效，左右声道合并，家庭 App 组合保留。"));
        var hint = SettingsDialog.Hint("忘记后下次可能需要密码，当前播放和配对身份保留。");
        var forgetButton = SettingsDialog.ActionButton("忘记所选音箱的播放验证", () => hint.Text = forget?.Invoke() ?? hint.Text); forgetButton.Enabled = forget is not null;
        column.Controls.Add(forgetButton); column.Controls.Add(hint);
        var done = SettingsDialog.ActionButton("确定", () => { DialogResult = DialogResult.OK; Close(); }); column.Controls.Add(done); AcceptButton = done; CompactDialogLayout.Fit(this, content: column);
    }
}
