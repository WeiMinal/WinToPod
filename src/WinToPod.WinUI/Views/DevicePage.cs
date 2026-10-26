// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.Desktop;
using WinToPod.AirPlay.Discovery;
using Microsoft.UI.Xaml.Automation;

namespace WinToPod.WinUI;

internal sealed class DevicePage : Grid
{
    private readonly PlaybackController _controller;
    private readonly Action<Func<Task>> _run;
    private readonly StackPanel _devices = Ui.Column(6);
    private readonly StackPanel _placeholder = Ui.Column(6);
    private readonly TextBlock _searchState = Ui.Hint("正在搜索");
    private readonly TextBlock _status = Ui.Hint("");
    private readonly TextBlock _percent = Ui.Text("—", 12);
    private readonly TextBlock _emptyTitle = Ui.Text("还没有发现设备", 18, true);
    private readonly TextBlock _emptyHint = Ui.Hint("确认电脑和音箱连接同一网络，再试一次。");
    private readonly StackPanel _empty = Ui.Column(10);
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 2 };
    private readonly Button _search, _mute, _source, _diagnosticHelp;
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, MinWidth = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, (Button Row, Button Action, TextBlock Kind, FrameworkElement Dot, Button Single)> _rows = new();
    private string _signature = "";
    private DeviceViewState? _state;
    private bool _rendering;
    public DevicePage(PlaybackController controller, Action<Func<Task>> run, Action settings, Action capture, Action diagnostics)
    {
        _controller = controller; _run = run;
        Margin = new Thickness(24, 12, 24, 20); RowSpacing = 12;
        RowDefinitions.Add(new() { Height = Ui.Auto }); RowDefinitions.Add(new() { Height = Ui.Star });
        RowDefinitions.Add(new() { Height = Ui.Auto }); RowDefinitions.Add(new() { Height = Ui.Auto });
        var header = Ui.Column(6);
        var top = Ui.Grid(Ui.Star, Ui.Auto, Ui.Auto); top.ColumnSpacing = 6;
        Ui.Add(top, Ui.Text("你的音箱", 26, true));
        _search = Ui.Button("添加设备", () => run(controller.SearchAsync));
        Ui.Add(top, _search, 1); Ui.Add(top, Ui.IconButton("\uE713", "设置", settings), 2);
        header.Children.Add(top); header.Children.Add(Ui.Hint("将电脑声音无线传送到 HomePod"));
        var section = Ui.Grid(Ui.Star, Ui.Auto); section.Margin = new Thickness(0, 14, 0, 2);
        Ui.Add(section, Ui.Text("附近的设备", 14, true)); Ui.Add(section, _searchState, 1); header.Children.Add(section);
        header.Children.Add(_progress); Ui.Add(this, header);
        var content = new Grid(); Ui.Add(this, content, row: 1);
        var scroll = new ScrollViewer { Content = _devices, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        content.Children.Add(scroll); content.Children.Add(_placeholder); _placeholder.VerticalAlignment = VerticalAlignment.Top;
        content.SizeChanged += (_, e) =>
        {
            var visible = Math.Clamp((int)((e.NewSize.Height + 6) / 76), 0, 3);
            for (var i = 0; i < _placeholder.Children.Count; i++) _placeholder.Children[i].Visibility = i < visible ? Visibility.Visible : Visibility.Collapsed;
        };
        for (var i = 0; i < 3; i++)
        {
            var ghost = Ui.Grid(new(48), Ui.Star); ghost.MinHeight = 44; ghost.ColumnSpacing = 16;
            var icon = Ui.DeviceIcon("speaker"); icon.Opacity = .18; Ui.Add(ghost, icon);
            var lines = Ui.Column(8); lines.VerticalAlignment = VerticalAlignment.Center;
            lines.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 138 + i * 20, Height = 7, HorizontalAlignment = HorizontalAlignment.Left, Opacity = .22 });
            lines.Children.Add(new ProgressBar { Value = 100, Width = 96, Height = 5, HorizontalAlignment = HorizontalAlignment.Left, Opacity = .12 });
            Ui.Add(ghost, lines, 1); _placeholder.Children.Add(Ui.Card(ghost, new Thickness(14, 12, 14, 12)));
        }
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center;
        var emptyIcon = Ui.DeviceIcon("speaker"); emptyIcon.Width = emptyIcon.Height = 40; emptyIcon.Opacity = .5;
        _empty.Children.Add(emptyIcon); _empty.Children.Add(_emptyTitle); _empty.Children.Add(_emptyHint);
        _empty.Children.Add(Ui.Button("重新搜索", () => run(controller.SearchAsync))); content.Children.Add(_empty);
        _status.MinHeight = 32; _status.TextWrapping = TextWrapping.Wrap; AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var statusRow = Ui.Grid(Ui.Star, Ui.Auto); statusRow.ColumnSpacing = 8; Ui.Add(statusRow, _status);
        _diagnosticHelp = Ui.Button("排查问题", diagnostics); _diagnosticHelp.Visibility = Visibility.Collapsed;
        _diagnosticHelp.Style = (Style)Application.Current.Resources["SubtleButtonStyle"]; Ui.Add(statusRow, _diagnosticHelp, 1); Ui.Add(this, statusRow, row: 2);
        var sound = Ui.Grid(Ui.Auto, Ui.Star, Ui.Auto, Ui.Auto); sound.ColumnSpacing = 10;
        _mute = Ui.IconButton("", "静音", controller.Mute); _mute.Content = Ui.DeviceIcon("volume-2"); Ui.Add(sound, _mute);
        _volume.ValueChanged += (_, _) => { if (!_rendering) controller.SetVolume((int)Math.Round(_volume.Value)); }; Ui.Label(_volume, "音箱音量"); Ui.Add(sound, _volume, 1);
        _percent.MinWidth = 36; _percent.TextAlignment = TextAlignment.Right; Ui.Add(sound, _percent, 2);
        _source = Ui.IconButton("", "Windows 输出设备", capture); _source.Content = Ui.DeviceIcon("audio-lines"); Ui.Add(sound, _source, 3);
        Ui.Add(this, Ui.Card(sound, new Thickness(10, 6, 10, 6)), row: 3);
    }
    public void Render(DeviceViewState state)
    {
        _state = state; _rendering = true;
        try
        {
            var signature = string.Join('|', state.Entries.Select(e => e.Key + ":" + e.Name + ":" + e.Kind));
            if (signature != _signature)
            {
                _signature = signature; _devices.Children.Clear(); _rows.Clear();
                foreach (var entry in state.Entries.OrderByDescending(e => e.Members.Any(m => m.IsHomePod))) AddDevice(entry);
            }
            _search.Content = state.Searching ? "取消搜索" : "添加设备"; _search.IsEnabled = !state.Working;
            _searchState.Text = state.Searching ? "正在搜索" : state.Entries.Count > 0 ? $"{state.Entries.Count} 个设备" : "";
            _progress.Visibility = state.Searching ? Visibility.Visible : Visibility.Collapsed;
            _placeholder.Visibility = state.Searching && state.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _empty.Visibility = !state.Searching && state.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _status.Text = state.Searching && state.Status.StartsWith("正在搜索", StringComparison.Ordinal) ? "搜索不会打断设备正在播放的内容。" : state.Status;
            _diagnosticHelp.Visibility = state.Error ? Visibility.Visible : Visibility.Collapsed;
            _status.Opacity = state.Error ? 1 : .7; if (state.Error) _status.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed); else _status.ClearValue(TextBlock.ForegroundProperty);
            _volume.IsEnabled = _mute.IsEnabled = state.Connected; _volume.Value = state.Volume;
            _mute.Content = Ui.DeviceIcon(state.Volume == 0 ? "volume-off" : "volume-2"); Ui.Label(_mute, state.Volume == 0 ? "取消静音" : "静音");
            _percent.Text = state.Connected ? state.Volume + "%" : "—"; _source.IsEnabled = !state.ConnectionBlocked;
            foreach (var (key, row) in _rows)
            {
                var active = state.ActiveKey == key; row.Dot.Visibility = active && state.Connected ? Visibility.Visible : Visibility.Collapsed;
                row.Action.Content = active && state.Working ? state.Connected ? "断开" : "取消" : "连接";
                var entry = state.Entries.First(e => e.Key == key);
                row.Action.IsEnabled = active && state.Working && state.CanConnect || !state.Working && !state.Searching && !state.ConnectionBlocked && entry.HasIpv4Endpoint && (key != state.SelectedKey || state.CanConnect);
                row.Row.IsEnabled = !state.Working || active;
                row.Single.Visibility = state.CanSingle && key == state.SelectedKey ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally { _rendering = false; }
    }
    private void AddDevice(PlaybackEntry entry)
    {
        var grid = Ui.Grid(new(52), Ui.Star, Ui.Auto); grid.ColumnSpacing = 12; grid.MinHeight = 48;
        var kind = entry.Members.All(m => m.IsHomePod) ? "speaker" : entry.Members.Any(m => m.Model?.StartsWith("Mac", StringComparison.OrdinalIgnoreCase) == true) ? "laptop" : entry.Members.Any(m => m.Model?.Contains("AppleTV", StringComparison.OrdinalIgnoreCase) == true) ? "tv" : "cast";
        var icon = Ui.DeviceIcon(kind, entry.IsGroup && kind == "speaker"); icon.HorizontalAlignment = HorizontalAlignment.Center; Ui.Add(grid, icon);
        var names = Ui.Column(3); names.VerticalAlignment = VerticalAlignment.Center;
        var name = Ui.Text(entry.Name, 14, true); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; ToolTipService.SetToolTip(name, entry.Name); names.Children.Add(name);
        var secondary = Ui.Row(7); var label = Ui.Hint(entry.IsGroup && kind == "speaker" ? "HomePod组合" : entry.Kind); secondary.Children.Add(label);
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Color.FromArgb(255, 22, 132, 60)), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(dot, "已连接"); secondary.Children.Add(dot); names.Children.Add(secondary); Ui.Add(grid, names, 1);
        var actions = Ui.Row(6);
        var single = Ui.Button("先用一只", () => _run(() => _controller.ConnectAsync(entry.Key, true))); single.Visibility = Visibility.Collapsed; actions.Children.Add(single);
        var action = Ui.Button("连接", () => _run(() => _controller.ConnectAsync(entry.Key))); action.MinWidth = 68; actions.Children.Add(action); Ui.Add(grid, actions, 2);
        var row = Ui.Button("", () => _controller.Select(entry.Key)); row.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.HorizontalContentAlignment = HorizontalAlignment.Stretch; row.Padding = new Thickness(14, 10, 14, 10); row.Content = grid;
        // Native Button supplies hover/pressed/keyboard-focus states; no custom list selection outlines.
        row.DoubleTapped += (_, e) => { if (_state?.Working == true || !action.IsEnabled || IsButtonChild(e.OriginalSource, action, single)) return; e.Handled = true; _run(() => _controller.ConnectAsync(entry.Key)); };
        row.KeyDown += (_, e) => { if (!IsButtonChild(e.OriginalSource, action, single) && e.Key == Windows.System.VirtualKey.Enter && action.IsEnabled && _state?.Working != true) { e.Handled = true; _run(() => _controller.ConnectAsync(entry.Key)); } };
        Ui.Label(row, entry.Name + "，" + entry.Kind + "，双击连接");
        _devices.Children.Add(row); _rows.Add(entry.Key, (row, action, label, dot, single));
    }
    private static bool IsButtonChild(object original, params Button[] buttons)
    {
        for (var node = original as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node)) if (buttons.Contains(node)) return true;
        return false;
    }
}
