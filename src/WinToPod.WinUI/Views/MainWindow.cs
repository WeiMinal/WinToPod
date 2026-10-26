// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinToPod.Desktop;
using Microsoft.UI.Xaml.Markup;

namespace WinToPod.WinUI;

internal sealed class MainWindow : Window, IPlaybackUi
{
    private readonly Grid _root = new();
    private readonly ContentControl _content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly PlaybackController _controller;
    private readonly NativeWindow _native;
    private readonly TrayIcon _tray;
    private readonly NotificationService _notifications;
    private readonly DevicePage _devices;
    private SettingsPage? _settings;
    private DeviceViewState? _state;
    private ContentDialog? _activeDialog;
    private Window? _interruption;
    private bool _quitting, _closed, _initialized;
    private readonly bool _preview;
    private readonly string? _selfCheckPath;
    public MainWindow(DesktopSettings settings, bool startup, bool preview, string? selfCheckPath)
    {
        _preview = preview; _selfCheckPath = selfCheckPath;
        Title = "WinToPod"; SystemBackdrop = new MicaBackdrop();
        _root.Children.Add(_content); Content = _root;
        _native = new NativeWindow(this, WindowMessage);
        _controller = new PlaybackController(settings, this, startup, enableVideoBridge: !preview, enableAutomation: !preview);
        _devices = new DevicePage(_controller, Run, OpenSettings, () => Run(ShowCaptureAsync), () => OpenSettingsPage("帮助与诊断")); _content.Content = _devices;
        _tray = new TrayIcon(_native.Handle, !preview);
        _notifications = new NotificationService(() => Post(ShowPanel), _tray, !preview);
        _root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _activeDialog is null && _settings is not null)
            { _settings.GoBack(); e.Handled = true; }
        };
        ApplyTheme(); _root.ActualThemeChanged += (_, _) => ApplyTheme();
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var dpi = NativeMethods.GetDpiForWindow(_native.Handle) / 96d;
        var width = Math.Min((int)(720 * dpi), area.WorkArea.Width - 32);
        var height = Math.Min((int)(620 * dpi), area.WorkArea.Height - 32);
        AppWindow.ResizeClient(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(area.WorkArea.X + (area.WorkArea.Width - width) / 2, area.WorkArea.Y + (area.WorkArea.Height - height) / 2));
        AppWindow.Closing += (_, e) =>
        {
            if (_closed) return; e.Cancel = true;
            if (!_quitting && settings.HideOnClose && !_preview) { NativeWindow.Hide(_native.Handle); return; }
            Run(QuitAsync);
        };
        _root.Loaded += async (_, _) =>
        {
            if (_initialized) return; _initialized = true; StartupDiagnostics.WindowReady();
            if (_preview) { _root.IsHitTestVisible = false; RenderPreview(); if (_selfCheckPath is not null) await RunUiSelfCheckAsync(_selfCheckPath); return; }
            if (startup) NativeWindow.Hide(_native.Handle);
            await SafeAsync(() => _controller.InitializeAsync(_native.Handle));
        };
    }
    private void ApplyTheme()
    {
        var theme = WindowTheme.Resolve(_controller.Settings.WinUiAppearance);
        if (_root.RequestedTheme != theme) _root.RequestedTheme = theme;
        WindowTheme.Apply(this, _root.ActualTheme);
        if (_interruption?.Content is FrameworkElement frame)
        {
            frame.RequestedTheme = theme;
            WindowTheme.Apply(_interruption, frame.ActualTheme);
        }
        if (_activeDialog is not null) _activeDialog.RequestedTheme = _root.ActualTheme;
        // Native Mica stays visible; the solid theme fallback remains legible when transparency is disabled.

    }
    public async Task SetAppearanceAsync(ApplicationAppearance appearance)
    {
        await _controller.SaveAppearanceAsync(appearance);
        ApplyTheme();
    }
    private void WindowMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        if (_closed) return;
        _controller?.ProcessWindowMessage(message, wParam, lParam);
        if (message == TrayIcon.CallbackMessage)
        {
            var kind = (int)((long)lParam & 0xFFFF);
            if (kind is 0x205 or 0x7B) Post(TrayMenu);
            else if (kind is 0x203 or 0x400 or 0x401 or 0x405) Post(ShowPanel);
        }
        if (message is 0x1A or 0x31A) { _tray?.Update(); Post(ApplyTheme); }
        if ((uint)message == NativeWindow.TaskbarCreated) _tray?.Restore();
        if (message == 0x24) NativeMethods.SetMinimumSize(WinRT.Interop.WindowNative.GetWindowHandle(this), lParam, 600, 520);
    }
    private void TrayMenu()
    {
        var items = new List<(string?, Action?)>();
        if (_controller.Working) items.Add(("断开音箱", () => Run(_controller.DisconnectAsync)));
        items.Add(("打开面板", ShowPanel));
        if (_controller.Settings.Interrupted && !_controller.Working) items.Add(("重新连接", () => Run(_controller.ReconnectAsync)));
        if (_controller.AutomaticPending) items.Add(("暂停本次自动连接", _controller.PauseAutomatic));
        items.Add(("设置", () => { ShowPanel(); OpenSettings(); })); items.Add((null, null)); items.Add(("退出", () => Run(QuitAsync))); _tray.Menu(items);
    }
    public bool HasThreadAccess => DispatcherQueue.HasThreadAccess;
    public void Post(Action action) { if (!_closed) _ = DispatcherQueue.TryEnqueue(() => { if (!_closed) action(); }); }
    public IUiTimer CreateTimer(int intervalMs) => new DispatcherTimer(DispatcherQueue, intervalMs);
    public void Render(DeviceViewState state)
    {
        _state = state; if (_devices is null || _closed) return;
        _devices.Render(state); _settings?.Refresh();
    }
    public void ShowPanel() { if (!_closed) { AppWindow.Show(); NativeWindow.Show(_native.Handle); Activate(); } }
    private void OpenSettings() => OpenSettingsPage(null);
    private void OpenSettingsPage(string? page)
    {
        if (_settings is null)
        {
            _controller.SetPreferencesOpen(true);
            _settings = new SettingsPage(_controller, this, Run, () => { _content.Content = _devices; _settings = null; _controller.SetPreferencesOpen(false); });
            _content.Content = _settings;
        }
        if (page is not null) _settings.Navigate(page);
    }
    public void Notify(string title, string message) => _notifications.Show(title, message);
    private void Run(Func<Task> operation) => _ = SafeAsync(operation);
    private async Task SafeAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!_quitting && !_closed) await MessageAsync("暂时无法完成", e.Message);
        }
    }
    private async Task<ContentDialogResult> DialogAsync(ContentDialog dialog, CancellationToken cancellation = default)
    {
        await _dialogGate.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            dialog.XamlRoot = _root.XamlRoot; dialog.RequestedTheme = _root.ActualTheme; _activeDialog = dialog;
            using var registration = cancellation.Register(() => Post(dialog.Hide));
            var result = await dialog.ShowAsync(); cancellation.ThrowIfCancellationRequested(); return result;
        }
        finally { _activeDialog = null; _dialogGate.Release(); }
    }
    public void CancelPrompts() => Post(() => _activeDialog?.Hide());
    public Task<T> InvokeAsync<T>(Func<Task<T>> operation)
    {
        if (HasThreadAccess) return operation();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () => { try { completion.TrySetResult(await operation()); } catch (OperationCanceledException e) { completion.TrySetCanceled(e.CancellationToken); } catch (Exception e) { completion.TrySetException(e); } });
        return completion.Task;
    }
    public Task<PasswordAnswer?> RequestPasswordAsync(string device, bool playback, CancellationToken cancellation) => InvokeAsync<PasswordAnswer?>(async () =>
    {
        ShowPanel(); var column = Ui.Column(12);
        column.Children.Add(Ui.Text(playback ? $"{device} 要求 AirPlay 播放密码。" : $"请输入 {device} 的连接密码。"));
        var password = new PasswordBox { PlaceholderText = "AirPlay 密码", MaxLength = 128 }; Ui.Label(password, "AirPlay密码"); column.Children.Add(password);
        var remember = new CheckBox { Content = "在这台电脑记住此音箱", IsChecked = true }; column.Children.Add(remember);
        column.Children.Add(Ui.Hint("凭据使用当前 Windows 账户加密保存，密码不会写入日志。"));
        var dialog = new ContentDialog { Title = "验证连接", Content = column, PrimaryButtonText = "连接", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false };
        password.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = password.Password.Length > 0;
        dialog.Opened += (_, _) => password.Focus(FocusState.Programmatic);
        return await DialogAsync(dialog, cancellation) == ContentDialogResult.Primary ? new PasswordAnswer(password.Password, remember.IsChecked == true) : null;
    });
    public Task<bool?> RequestSingleFallbackAsync(string device, bool playback, CancellationToken cancellation) => InvokeAsync<bool?>(async () =>
    {
        var column = Ui.Column(); column.Children.Add(Ui.Text("另一只音箱暂时未通过验证，可以先使用 " + device + " 播放。"));
        column.Children.Add(Ui.Hint("左右声道会合并播放，家庭 App 中的立体声组合会保留。")); var remember = new CheckBox { Content = "下次也先用单只兼容播放" }; column.Children.Add(remember);
        var dialog = new ContentDialog { Title = "先用一只音箱？", Content = column, PrimaryButtonText = "先用一只", CloseButtonText = "暂不连接", DefaultButton = ContentDialogButton.Close };
        return await DialogAsync(dialog, cancellation) == ContentDialogResult.Primary ? remember.IsChecked == true : null;
    });
    public void ShowInterruption(string reason, string policy)
    {
        if (_interruption is not null) { InterruptionWindowPlacement.Show(_interruption, this); return; }
        var notice = new Window { Title = "WinToPod · 连接已中断", SystemBackdrop = new MicaBackdrop() };
        var content = Ui.Column(16); content.Margin = new Thickness(24); content.Children.Add(Ui.Text("连接已中断", 22, true)); content.Children.Add(Ui.Text(reason)); content.Children.Add(Ui.Hint(policy));
        var actions = Ui.Row(); actions.Children.Add(Ui.Button("重新连接", () => { notice.Close(); ShowPanel(); Run(_controller.ReconnectAsync); }, true));
        actions.Children.Add(Ui.Button("暂不连接", () => { _controller.DismissInterruption(true); notice.Close(); })); content.Children.Add(actions);
        var frame = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            RequestedTheme = WindowTheme.Resolve(_controller.Settings.WinUiAppearance) };
        notice.Content = frame; _interruption = notice;
        frame.ActualThemeChanged += (_, _) => WindowTheme.Apply(notice, frame.ActualTheme);
        frame.Loaded += (_, _) => WindowTheme.Apply(notice, frame.ActualTheme);
        WindowTheme.Apply(notice, _root.ActualTheme);
        notice.Closed += (_, _) => { if (ReferenceEquals(_interruption, notice)) _interruption = null; _controller.DismissInterruption(false); };
        content.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { notice.Close(); e.Handled = true; } };
        InterruptionWindowPlacement.Show(notice, this);
    }
    public void CloseInterruption() { var notice = _interruption; _interruption = null; notice?.Close(); }
    public async Task<bool> ConfirmAsync(string title, string message, string action) => await DialogAsync(new ContentDialog { Title = title, Content = Ui.Text(message), PrimaryButtonText = action, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }) == ContentDialogResult.Primary;
    private async Task MessageAsync(string title, string message) => _ = await DialogAsync(new ContentDialog { Title = title, Content = Ui.Text(message), CloseButtonText = "知道了" });
    public async Task ShowCaptureAsync()
    {
        var choices = _controller.CaptureSources();
        if (choices.Count == 0) { await MessageAsync("没有可用输出设备", "请先在 Windows 中连接或启用一个播放设备。"); return; }
        var current = _controller.CurrentOutputId();
        var column = Ui.Column();
        var list = new ComboBox { ItemsSource = choices.Select(c => c.Name).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 280 };
        list.SelectedIndex = Array.FindIndex(choices.ToArray(), c => c.Id == current); column.Children.Add(list);
        column.Children.Add(Ui.Hint("更改 Windows 默认输出，立即生效。跟随默认设备的应用会切换，HomePod 推流继续。"));
        var more = new HyperlinkButton { Content = "Windows 声音设置" };
        more.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); column.Children.Add(more);
        if (await DialogAsync(new ContentDialog { Title = "Windows 输出设备", Content = column, PrimaryButtonText = "应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary }) == ContentDialogResult.Primary && list.SelectedIndex >= 0 && choices[list.SelectedIndex].Id is { } id)
        {
            try { await _controller.ChangeOutputAsync(id); }
            catch (Exception e) { await MessageAsync("未能完成切换", e.Message); }
        }
    }
    public async Task ShowDetailsAsync()
    {
        var text = new TextBox { Text = _controller.DetailsText(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 360, MaxHeight = 340, FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(12) };
        if (await DialogAsync(new ContentDialog { Title = "连接详情", Content = text, PrimaryButtonText = "导出诊断", CloseButtonText = "关闭" }) == ContentDialogResult.Primary) await ExportDiagnosticsAsync();
    }
    public async Task ExportDiagnosticsAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = "WinToPod-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("ZIP 诊断包", new List<string> { ".zip" }); WinRT.Interop.InitializeWithWindow.Initialize(picker, _native.Handle);
        var file = await picker.PickSaveFileAsync(); if (file is not null) _controller.ExportDiagnostics(file.Path);
    }
    public async Task OpenBrowserHelperAsync()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "浏览器音画补偿");
        if (!Directory.Exists(folder)) { await MessageAsync("需要完整程序文件夹", "请完整解压 WinToPod，再从文件夹内启动。"); return; }
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        await MessageAsync("首次安装浏览器助手", "在 Edge 或 Chrome 的扩展管理中开启开发人员模式，选择“加载解压缩的扩展”，选中刚打开的文件夹。\n\n首次授权后自动跟随音箱。旧助手请重新加载。");
    }
    public async Task ShowVideoToolsAsync()
    {
        var column = Ui.Column(); column.Children.Add(Ui.Hint("使用 VLC / mpv 播放本地视频，自动跟随当前音箱的补偿。"));
        var feedback = Ui.Hint(""); var open = Ui.Button("打开本地视频", () => Run(async () =>
        {
            var player = new[] { _controller.Settings.VideoPlayerPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe") }.FirstOrDefault(p => p is not null && File.Exists(p));
            if (player is null) player = await PickFileAsync("选择 VLC 或 mpv", new[] { ".exe" });
            if (player is null) return;
            if (Path.GetFileName(player).ToLowerInvariant() is not ("vlc.exe" or "mpv.exe")) { feedback.Text = "请选择 vlc.exe 或 mpv.exe。"; return; }
            var media = await PickFileAsync("选择本地视频", new[] { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".ts" });
            if (media is not null) { await _controller.LaunchVideoAsync(player, media); feedback.Text = "已启动兼容播放，请按人物口型检查补偿效果。"; }
        })); open.IsEnabled = _controller.Connected; column.Children.Add(open);
        var correction = new NumberBox { Header = "高级微调（毫秒）", Minimum = -1500, Maximum = 1500, SmallChange = 50, Value = _controller.VideoCorrection, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var advanced = Ui.Column(10); advanced.Children.Add(correction); advanced.Children.Add(Ui.Hint("默认无需调整。正数让画面更晚，负数更早。"));
        advanced.Children.Add(Ui.Button("保存微调", () => { if (!double.IsNaN(correction.Value)) { _controller.SetVideoCorrection((int)correction.Value); feedback.Text = "微调已保存。"; } }));
        column.Children.Add(new Expander { Header = "画面仍不同步？", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch }); column.Children.Add(feedback);
        await DialogAsync(new ContentDialog { Title = "本地视频", Content = column, CloseButtonText = "完成" });
    }
    private async Task<string?> PickFileAsync(string title, IEnumerable<string> types)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List }; foreach (var type in types) picker.FileTypeFilter.Add(type);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _native.Handle); return (await picker.PickSingleFileAsync())?.Path;
    }
    private async Task QuitAsync()
    {
        if (_quitting) return; _quitting = true; CancelPrompts();
        await _controller.DisposeAsync(); _notifications.Dispose(); _tray.Dispose(); _native.Dispose();
        _closed = true; Close(); Application.Current.Exit();
    }
    private void RenderPreview()
    {
        // Explicit preview mode never discovers, pairs or captures audio. No invented devices are presented as discovered.
        Render(new([], null, null, true, false, false, false, false, "连接", "正在搜索同一网络中的设备……", false, 20));
    }
    private async Task RunUiSelfCheckAsync(string path)
    {
        await Task.Delay(250);
        try
        {
            var report = new { Product = "WinToPod", Framework = typeof(Window).Assembly.GetName().Name, Ui = "WinUI 3", WindowsAppSdk = "2.5.1", Passed = _root.ActualWidth > 0 && _devices.ActualHeight > 0, Width = _root.ActualWidth, Height = _root.ActualHeight };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(report));
        }
        finally { await QuitAsync(); }
    }
}
