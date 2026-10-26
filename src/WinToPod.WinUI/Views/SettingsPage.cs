// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.Core;
using WinToPod.Desktop;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinToPod.WinUI;

internal sealed class SettingsPage : Grid
{
    private readonly PlaybackController _controller;
    private readonly MainWindow _window;
    private readonly Action<Func<Task>> _run;
    private readonly Action _back;
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _title = Ui.Text("设置", 24, true), _feedback = Ui.Hint("");
    private readonly Button _backButton;
    private readonly Dictionary<string, UIElement> _pages = new();
    private readonly Stack<string> _history = new();
    private string _current = "设置";
    private TextBlock? _browser, _testHint, _connectionSummary, _appearanceSummary;
    private Button? _test;
    private RadioButtons? _policy;
    private ToggleSwitch? _startup, _auto, _hide, _single;
    private Preferences _saved;
    private bool _initializing = true;

    public SettingsPage(PlaybackController controller, MainWindow window, Action<Func<Task>> run, Action back)
    {
        _controller = controller; _window = window; _run = run; _back = back;
        var s = controller.Settings;
        _saved = new(s.Policy, StartupRegistration.IsCurrent, s.AutoConnectOnStart, s.HideOnClose,
            controller.Selection?.IsGroup == true && s.SingleFallbackTargets.Contains(controller.Selection.Key));
        Margin = new Thickness(24, 8, 24, 16); RowSpacing = 8;
        RowDefinitions.Add(new() { Height = Ui.Auto }); RowDefinitions.Add(new() { Height = Ui.Star }); RowDefinitions.Add(new() { Height = Ui.Auto });
        var header = Ui.Row(10); _backButton = Ui.IconButton("\uE72B", "返回音箱", GoBack); header.Children.Add(_backButton); header.Children.Add(_title);
        Ui.Add(this, header); Ui.Add(this, _page, row: 1); Ui.Add(this, _feedback, row: 2);
        ShowPage("设置"); _initializing = false;
    }
    public void GoBack()
    {
        if (_history.TryPop(out var parent)) ShowPage(parent);
        else _back();
    }
    public void Navigate(string name)
    {
        if (name == _current) return;
        _history.Push(_current); ShowPage(name);
    }
    private void ShowPage(string name)
    {
        if (!_pages.TryGetValue(name, out var view))
        {
            var initializing = _initializing; _initializing = true;
            try
            {
                view = name switch { "设置" => Home(), "外观" => Appearance(), "连接与播放" => Connection(), "音画补偿" => Video(), "浏览器补偿" => Browser(), "帮助与诊断" => Diagnostic(), "关于" => About(), _ => throw new ArgumentOutOfRangeException(nameof(name)) };
                _pages.Add(name, view);
            }
            finally { _initializing = initializing; }
        }
        _current = name; _title.Text = name; _page.Content = view; _feedback.Text = "";
        Ui.Label(_backButton, _history.TryPeek(out var parent) ? "返回" + parent : "返回音箱"); Refresh();
        // Every nested view starts at its heading, including keyboard/screen-reader navigation.
        if (IsLoaded) _backButton.Focus(FocusState.Programmatic);
    }
    private UIElement Home()
    {
        var prefs = CurrentPreferences(); var column = Ui.Column(12); var common = Ui.Column(12);
        _startup = SwitchRow(common, "随 Windows 启动", prefs.StartAtLogin,
            StartupRegistration.HasOther ? "开启后将登录启动项更新到当前程序位置" : null);
        _auto = SwitchRow(common, "自动连接上次音箱", prefs.AutoConnect);
        var autoHint = Ui.Hint("本机开始使用后连接，远程使用或锁屏时先待机。");
        autoHint.Visibility = _auto.IsOn ? Visibility.Visible : Visibility.Collapsed; common.Children.Add(autoHint);
        _auto.Toggled += (_, _) => autoHint.Visibility = _auto.IsOn ? Visibility.Visible : Visibility.Collapsed;
        _hide = SwitchRow(common, "关闭窗口后留在托盘", prefs.HideOnClose);
        column.Children.Add(Ui.Card(common, new Thickness(16, 12, 16, 12)));
        column.Children.Add(Ui.Card(NavigationRow("外观", AppearanceName(_controller.Settings.WinUiAppearance), () => Navigate("外观"), out _appearanceSummary), new Thickness(4)));
        var playback = Ui.Column(2);
        var connection = NavigationRow("连接与播放", "", () => Navigate("连接与播放"), out var summary); _connectionSummary = summary; playback.Children.Add(connection);
        playback.Children.Add(NavigationRow("音画补偿", "浏览器或本地视频", () => Navigate("音画补偿"), out _));
        column.Children.Add(Ui.Card(playback, new Thickness(4)));
        var support = Ui.Column(2);
        support.Children.Add(NavigationRow("帮助与诊断", "声音检查、连接记录", () => Navigate("帮助与诊断"), out _));
        support.Children.Add(NavigationRow("关于", "版本与许可", () => Navigate("关于"), out _));
        column.Children.Add(Ui.Card(support, new Thickness(4))); return Scroll(column);
    }
    private UIElement Appearance()
    {
        var column = Ui.Column(14);
        var choices = new RadioButtons { MaxColumns = 1 };
        choices.Items.Add("跟随系统"); choices.Items.Add("浅色"); choices.Items.Add("深色");
        choices.SelectedIndex = (int)_controller.Settings.WinUiAppearance;
        choices.SelectionChanged += (_, _) =>
        {
            if (_initializing || choices.SelectedIndex < 0) return;
            var appearance = (ApplicationAppearance)choices.SelectedIndex;
            if (appearance == _controller.Settings.WinUiAppearance) return;
            _run(async () =>
            {
                choices.IsEnabled = false;
                try { await _window.SetAppearanceAsync(appearance); _feedback.Text = "已保存"; Refresh(); }
                finally
                {
                    var initializing = _initializing; _initializing = true;
                    choices.SelectedIndex = (int)_controller.Settings.WinUiAppearance;
                    _initializing = initializing; choices.IsEnabled = true;
                }
            });
        };
        column.Children.Add(Ui.Card(choices));
        column.Children.Add(Ui.Hint("立即生效，下次打开时保留。跟随系统使用 Windows 的应用模式。"));
        return Scroll(column);
    }
    private static string AppearanceName(ApplicationAppearance appearance) => appearance switch
    { ApplicationAppearance.Light => "浅色", ApplicationAppearance.Dark => "深色", _ => "跟随系统" };
    private UIElement Connection()
    {
        var prefs = CurrentPreferences(); var column = Ui.Column(14); var ownership = Ui.Column(10);
        ownership.Children.Add(Ui.Text("音箱被其他设备使用时", 14, true));
        _policy = new RadioButtons { MaxColumns = 2 };
        _policy.Items.Add("保守模式（推荐）"); _policy.Items.Add("激进模式"); _policy.SelectedIndex = prefs.Policy == ConnectionPolicy.Conservative ? 0 : 1; ownership.Children.Add(_policy);
        var hint = Ui.Hint(""); var aggressive = Ui.Hint("仅在近期有本机操作时有限重试，最多3次；可能打断其他设备播放。远程使用或锁屏时保持待机。");
        void UpdatePolicy()
        {
            var enabled = _policy.SelectedIndex == 1;
            hint.Text = enabled ? "尝试重新取得音箱使用权。" : "让出音箱后提醒，由你决定何时重新连接。";
            aggressive.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }
        ownership.Children.Add(hint); ownership.Children.Add(aggressive); UpdatePolicy();
        _policy.SelectionChanged += (_, _) => { UpdatePolicy(); Save(); }; column.Children.Add(Ui.Card(ownership));
        var advanced = Ui.Column(12);
        if (_controller.Selection?.IsGroup == true)
            _single = SwitchRow(advanced, "暂用单只兼容播放", prefs.PreferSingle, "下次连接生效，家庭 App 中的组合保留");
        var forget = Ui.Button("忘记所选音箱的播放验证", () => _run(async () =>
        {
            if (!await _window.ConfirmAsync("忘记播放验证？", "下次连接可能需要重新输入密码。配对身份和当前播放会保留。", "忘记")) return;
            _feedback.Text = _controller.ForgetPlaybackCredentials();
        })); forget.IsEnabled = _controller.Selection is not null;
        advanced.Children.Add(forget);
        column.Children.Add(new Expander { Header = "验证与兼容", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        return Scroll(column);
    }
    private ToggleSwitch SwitchRow(StackPanel parent, string title, bool enabled, string? hint = null)
    {
        var row = Ui.Grid(Ui.Star, Ui.Auto); row.ColumnSpacing = 12;
        var labels = Ui.Column(2); labels.Children.Add(Ui.Text(title)); if (hint is not null) labels.Children.Add(Ui.Hint(hint)); Ui.Add(row, labels);
        var toggle = new ToggleSwitch { IsOn = enabled, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center, MinWidth = 46 };
        Ui.Label(toggle, title); toggle.Toggled += (_, _) => Save(); Ui.Add(row, toggle, 1); parent.Children.Add(row); return toggle;
    }
    private Preferences CurrentPreferences() => new(_policy is null ? _saved.Policy : _policy.SelectedIndex == 1 ? ConnectionPolicy.Aggressive : ConnectionPolicy.Conservative,
        _startup?.IsOn ?? _saved.StartAtLogin, _auto?.IsOn ?? _saved.AutoConnect, _hide?.IsOn ?? _saved.HideOnClose, _single?.IsOn ?? _saved.PreferSingle);
    private void Save()
    {
        if (_initializing) return;
        var draft = CurrentPreferences(); if (draft == _saved) return;
        _run(async () => { await _controller.SavePreferencesAsync(draft); _saved = draft; _feedback.Text = "已保存"; Refresh(); });
    }
    private UIElement Video()
    {
        var column = Ui.Column(12); column.Children.Add(Ui.Hint("选择你观看视频的方式，查看对应的补偿设置。"));
        column.Children.Add(NavigationRow("浏览器", "Edge / Chrome 浏览器助手", () => Navigate("浏览器补偿"), out _));
        column.Children.Add(NavigationRow("本地视频", "使用 VLC 或 mpv 播放", () => _run(_window.ShowVideoToolsAsync), out _));
        column.Children.Add(Ui.Hint("目前支持以上两种方式，系统级全局音画补偿仍在开发。")); return Scroll(column);
    }
    private UIElement Browser()
    {
        var column = Ui.Column(14); _browser = Ui.Hint(_controller.BrowserSyncDescription()); column.Children.Add(_browser);
        column.Children.Add(Ui.Text("安装一次，之后自动跟随音箱，无需填写延迟。"));
        column.Children.Add(Ui.Button("安装或更新浏览器助手", () => _run(_window.OpenBrowserHelperAsync))); return Scroll(column);
    }
    private UIElement Diagnostic()
    {
        var column = Ui.Column(14); var sound = Ui.Column(10); sound.Children.Add(Ui.Text("没有声音？", 16, true));
        _testHint = Ui.Hint(""); sound.Children.Add(_testHint); _test = Ui.Button("测试当前音箱", _controller.ToggleSoundTest); sound.Children.Add(_test);
        column.Children.Add(Ui.Card(sound));
        var logs = Ui.Column(10); logs.Children.Add(Ui.Text("进一步排查", 14, true));
        logs.Children.Add(Ui.Button("查看连接详情", () => _run(_window.ShowDetailsAsync)));
        logs.Children.Add(Ui.Button("导出诊断", () => _run(_window.ExportDiagnosticsAsync)));
        logs.Children.Add(Ui.Hint("不含密码、密钥或录音，不会自动上传；分享前可检查内容。")); column.Children.Add(Ui.Card(logs));
        return Scroll(column);
    }
    private UIElement About()
    {
        var column = Ui.Column(14); column.Margin = new Thickness(0, 4, 0, 0);
        var identity = Ui.Row(14); var logo = new Image { Width = 48, Height = 48, Stretch = Stretch.Uniform };
        void UpdateLogo() => logo.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", ActualTheme == ElementTheme.Dark ? "WinToPod-white-128.png" : "WinToPod-black-128.png")));
        ActualThemeChanged += (_, _) => UpdateLogo(); Loaded += (_, _) => UpdateLogo();
        identity.Children.Add(logo); var title = Ui.Column(3); title.Children.Add(Ui.Text("WinToPod", 22, true)); title.Children.Add(Ui.Hint("V1.1 · Windows 声音，送到 HomePod")); identity.Children.Add(title); column.Children.Add(identity);
        column.Children.Add(Ui.Text("将 Windows 系统声音无线传送至局域网 HomePod。"));
        column.Children.Add(Ui.Hint("支持单只音箱与原生立体声组合。"));
        column.Children.Add(new Expander { Header = "许可与项目说明", Content = Ui.Hint("开源许可：GPL-3.0-or-later\n独立项目，与 Apple、Microsoft 无关联。"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch }); return Scroll(column);
    }
    private static Button NavigationRow(string title, string description, Action action, out TextBlock summary)
    {
        var row = Ui.Grid(Ui.Star, Ui.Auto); row.ColumnSpacing = 12; var labels = Ui.Column(3);
        labels.Children.Add(Ui.Text(title)); summary = Ui.Hint(description); labels.Children.Add(summary); Ui.Add(row, labels); Ui.Add(row, Ui.Icon("\uE76C", 12), 1);
        var button = Ui.Button("", action); button.Content = row; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(12, 9, 12, 9); button.Style = (Style)Application.Current.Resources["SubtleButtonStyle"]; Ui.Label(button, title); return button;
    }
    public void Refresh()
    {
        if (_appearanceSummary is not null) _appearanceSummary.Text = AppearanceName(_controller.Settings.WinUiAppearance);
        if (_connectionSummary is not null) _connectionSummary.Text = (_policy?.SelectedIndex == 1 || _policy is null && _saved.Policy == ConnectionPolicy.Aggressive) ? "激进模式" : "保守模式（推荐）";
        if (_browser is not null) _browser.Text = _controller.BrowserSyncDescription();
        if (_test is not null && _testHint is not null)
        {
            var state = _controller.SoundTestState(); _testHint.Text = state.Description;
            _test.Content = state.Active ? state.CanStop ? "停止测试" : "正在恢复声音…" : "测试当前音箱"; _test.IsEnabled = state.Available && (!state.Active || state.CanStop);
        }
    }
    private static UIElement Scroll(UIElement content) => new SettingsScrollViewer(content);
}
