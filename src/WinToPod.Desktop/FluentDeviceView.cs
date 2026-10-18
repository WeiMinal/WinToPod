// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.AirPlay.Discovery;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;

namespace WinToPod.Desktop;

internal sealed class ReceiverSelection
{
    private PlaybackEntry[] _entries = [];
    public IReadOnlyList<PlaybackEntry> Entries => _entries;
    public PlaybackEntry? SelectedEntry { get; private set; }
    public int TargetCount => _entries.Length;
    public event EventHandler? ItemSelectionChanged;
    public void ReplaceTargets(IEnumerable<PlaybackEntry> entries, string? key)
    { _entries = entries.ToArray(); SelectedEntry = _entries.FirstOrDefault(x => x.Key == key); ItemSelectionChanged?.Invoke(this, EventArgs.Empty); }
    public bool SelectTarget(string key)
    { var row = _entries.FirstOrDefault(x => x.Key == key); if (row is null) return false; if (SelectedEntry?.Key == key) return true; SelectedEntry = row; ItemSelectionChanged?.Invoke(this, EventArgs.Empty); return true; }
}

internal sealed record DeviceViewState(IReadOnlyList<PlaybackEntry> Entries, string? SelectedKey, string? ActiveKey,
    bool Searching, bool Working, bool Connected, bool CanConnect, bool CanSingle, string ConnectText, string Status, bool Error, int Volume, bool ConnectionBlocked = false);

internal sealed class FluentDeviceView : C.UserControl
{
    private readonly Func<string, Task> _connect;
    private readonly Action<string> _select;
    private readonly Func<Task> _scan;
    private readonly Action _settings;
    private readonly C.ListBox _list = new() { BorderThickness = new W.Thickness(0), Background = M.Brushes.Transparent, HorizontalContentAlignment = W.HorizontalAlignment.Stretch };
    private readonly C.TextBlock _status = NativeFluentUi.Text("正在查找同一网络中的设备……");
    private readonly C.TextBlock _volumeLabel = NativeFluentUi.Text("—", 12);
    private readonly C.Button _mute, _source;
    private readonly C.TextBlock _searchLabel = NativeFluentUi.Text("正在搜索", 12);
    private readonly C.ProgressBar _searchProgress = new() { IsIndeterminate = true, Height = 3, Margin = new W.Thickness(0, 4, 0, 0) };
    private readonly C.Slider _volume = new() { Minimum = 0, Maximum = 100, IsSnapToTickEnabled = true, TickFrequency = 1, MinWidth = 180, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly C.Grid _placeholder = new();
    private readonly C.Button _add;
    private readonly Func<Task> _fallback;
    private readonly Dictionary<string, (C.ListBoxItem Item, C.Button Action, C.Button Single, C.Border Card, W.FrameworkElement Connected)> _rows = new(StringComparer.Ordinal);
    private string _signature = "";
    private bool _rendering;
    public W.FrameworkElement DeviceList => _list;
    public W.FrameworkElement StatusRegion => _status;
    public W.FrameworkElement VolumeRegion { get; }
    public FluentDeviceView(Func<string, Task> connect, Action<string> select, Func<Task> scan, Action settings, Action<int> volume, Func<Task> single, Action? mute = null, Action? source = null)
    {
        _connect = connect; _select = select; _scan = scan; _settings = settings; _fallback = single;
        FontFamily = NativeFluentUi.UiFont; FontSize = 14;
        var root = new C.Grid { Margin = new W.Thickness(24, 22, 24, 18) };
        root.RowDefinitions.Add(new C.RowDefinition { Height = W.GridLength.Auto });
        root.RowDefinitions.Add(new C.RowDefinition { Height = new W.GridLength(1, W.GridUnitType.Star) });
        root.RowDefinitions.Add(new C.RowDefinition { Height = W.GridLength.Auto });
        var header = new C.StackPanel();
        var top = new C.Grid(); top.ColumnDefinitions.Add(new()); top.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        var title = NativeFluentUi.Text("WinToPod", 28, true); title.Margin = new W.Thickness(0, 0, 0, 4); top.Children.Add(title);
        var actions = new C.StackPanel { Orientation = C.Orientation.Horizontal, VerticalAlignment = W.VerticalAlignment.Top };
        _add = NativeFluentUi.Button("＋ 添加设备", async () => await _scan());
        var preferences = NativeFluentUi.Button("设置", _settings); preferences.MinWidth = 60; preferences.Margin = new W.Thickness(0);
        actions.Children.Add(_add); actions.Children.Add(preferences); C.Grid.SetColumn(actions, 1); top.Children.Add(actions);
        header.Children.Add(top); var subtitle = NativeFluentUi.Text("将电脑声音发送到你的音箱"); subtitle.Foreground = NativeFluentUi.Secondary; subtitle.Margin = new W.Thickness(0); header.Children.Add(subtitle);
        var section = new C.DockPanel { Margin = new W.Thickness(0, 16, 0, 8) };
        _searchLabel.Foreground = NativeFluentUi.Secondary; _searchLabel.HorizontalAlignment = W.HorizontalAlignment.Right; C.DockPanel.SetDock(_searchLabel, C.Dock.Right); section.Children.Add(_searchLabel);
        _searchLabel.Margin = new W.Thickness(0); var sectionTitle = NativeFluentUi.Text("附近的设备", 15, true); sectionTitle.Margin = new W.Thickness(0);
        section.Children.Add(sectionTitle); header.Children.Add(section); header.Children.Add(_searchProgress); root.Children.Add(header);
        var content = new C.Grid { Margin = new W.Thickness(0, 10, 0, 8) }; C.Grid.SetRow(content, 1); root.Children.Add(content);
        System.Windows.Automation.AutomationProperties.SetName(_list, "附近的音箱与设备");
        C.ScrollViewer.SetHorizontalScrollBarVisibility(_list, C.ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => { if (!_rendering && _list.SelectedItem is C.ListBoxItem { Tag: PlaybackEntry entry }) _select(entry.Key); };
        _list.PreviewKeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && _list.SelectedItem is C.ListBoxItem { Tag: PlaybackEntry entry }) { e.Handled = true; await _connect(entry.Key); } };
        NativeFluentUi.PlainList(_list); content.ClipToBounds = true;
        content.Children.Add(_list); content.Children.Add(_placeholder);
        _placeholder.RowDefinitions.Add(new()); _placeholder.RowDefinitions.Add(new()); _placeholder.VerticalAlignment = W.VerticalAlignment.Top;
        content.SizeChanged += (_, _) =>
        {
            var count = content.ActualHeight >= 156 ? 2 : content.ActualHeight >= 78 ? 1 : 0; _placeholder.Height = count * 78;
            for (var row = 0; row < 2; row++) { _placeholder.RowDefinitions[row].Height = new W.GridLength(row < count ? 78 : 0); if (_placeholder.Children.Count > row) _placeholder.Children[row].Visibility = row < count ? W.Visibility.Visible : W.Visibility.Collapsed; }
        };
        for (var i = 0; i < 2; i++)
        {
            var line = new C.Grid { MinHeight = 30 }; line.ColumnDefinitions.Add(new() { Width = new W.GridLength(68) }); line.ColumnDefinitions.Add(new());
            line.Children.Add(new C.Border { Width = 48, Height = 44, CornerRadius = new W.CornerRadius(12), Background = NativeFluentUi.Border, HorizontalAlignment = W.HorizontalAlignment.Left, VerticalAlignment = W.VerticalAlignment.Center });
            var ghost = new C.StackPanel { VerticalAlignment = W.VerticalAlignment.Center }; ghost.Children.Add(new C.Border { Height = 10, Width = 135 + i * 25, HorizontalAlignment = W.HorizontalAlignment.Left, CornerRadius = new W.CornerRadius(4), Background = NativeFluentUi.Border, Margin = new W.Thickness(0, 0, 0, 10) }); ghost.Children.Add(new C.Border { Height = 8, Width = 92, HorizontalAlignment = W.HorizontalAlignment.Left, CornerRadius = new W.CornerRadius(4), Background = NativeFluentUi.Border }); C.Grid.SetColumn(ghost, 1); line.Children.Add(ghost); var ghostCard = NativeFluentUi.Card(line); ghostCard.Padding = new W.Thickness(12, 8, 12, 8); C.Grid.SetRow(ghostCard, i); _placeholder.Children.Add(ghostCard);
        }
        var footer = new C.StackPanel(); C.Grid.SetRow(footer, 2); root.Children.Add(footer);
        _status.MinHeight = 40; _status.Margin = new W.Thickness(0, 4, 0, 8); footer.Children.Add(_status);
        var sound = new C.Grid { MinHeight = 40 };
        sound.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto }); sound.ColumnDefinitions.Add(new());
        sound.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto }); sound.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        C.Button IconButton(string key, string label, Action? action)
        {
            var button = NativeFluentUi.Button("", () => action?.Invoke()); button.Content = DeviceArtwork.ControlIcon(key);
            var style = new W.Style(typeof(C.Button), button.Style);
            style.Setters.Add(new W.Setter(C.Control.BackgroundProperty, M.Brushes.Transparent)); style.Setters.Add(new W.Setter(C.Control.BorderThicknessProperty, new W.Thickness(0))); button.Style = style;
            button.Width = button.Height = 36; button.MinWidth = button.MinHeight = 0; button.Padding = new W.Thickness(7); button.Margin = new W.Thickness(0);
            button.ToolTip = label; System.Windows.Automation.AutomationProperties.SetName(button, label); return button;
        }
        _mute = IconButton("volume-2", "静音", mute); _mute.Name = "MuteAction"; _mute.Margin = new W.Thickness(0, 0, 8, 0); sound.Children.Add(_mute);
        C.Grid.SetColumn(_volume, 1); sound.Children.Add(_volume); _volume.ValueChanged += (_, _) => { if (!_rendering) volume((int)Math.Round(_volume.Value)); };
        _volumeLabel.Width = 42; _volumeLabel.Margin = new W.Thickness(8, 0, 4, 0); _volumeLabel.TextAlignment = W.TextAlignment.Right; _volumeLabel.VerticalAlignment = W.VerticalAlignment.Center;
        C.Grid.SetColumn(_volumeLabel, 2); sound.Children.Add(_volumeLabel);
        _source = IconButton("audio-lines", "电脑声音来源", source); _source.Name = "CaptureSourceAction";
        C.Grid.SetColumn(_source, 3); sound.Children.Add(_source);
        var volumeCard = NativeFluentUi.Card(sound); volumeCard.Padding = new W.Thickness(16, 8, 16, 8); volumeCard.Margin = new W.Thickness(0); VolumeRegion = volumeCard; footer.Children.Add(volumeCard);
        Content = root;
    }
    public void Render(DeviceViewState state)
    {
        _rendering = true;
        try
        {
            var signature = string.Join('|', state.Entries.Select(x => x.Key + ":" + x.Name + ":" + x.Kind));
            if (signature != _signature)
            {
                _signature = signature; _list.Items.Clear(); _rows.Clear();
                foreach (var entry in state.Entries.OrderByDescending(x => x.Members.Any(m => m.IsHomePod))) AddRow(entry);
            }
            _placeholder.Visibility = state.Entries.Count == 0 && state.Searching ? W.Visibility.Visible : W.Visibility.Collapsed;
            _list.Visibility = _placeholder.Visibility == W.Visibility.Visible ? W.Visibility.Hidden : W.Visibility.Visible;
            _add.IsEnabled = !state.Working; _add.Content = state.Searching ? "取消搜索" : "＋ 添加设备";
            _searchLabel.Text = state.Searching ? "正在搜索" : state.Entries.Count > 0 ? $"{state.Entries.Count} 个设备" : "暂未发现设备";
            _searchProgress.Visibility = state.Searching ? W.Visibility.Visible : W.Visibility.Hidden;
            _status.Text = state.Searching && state.Status.StartsWith("正在搜索", StringComparison.Ordinal) ? "搜索不会抢占设备当前的播放。" : state.Status; _status.ToolTip = state.Status; _status.Foreground = state.Error ? NativeFluentUi.Error : NativeFluentUi.Secondary;
            _volume.IsEnabled = _mute.IsEnabled = state.Connected; _volume.Value = state.Volume; _volumeLabel.Text = state.Connected ? $"{state.Volume}%" : "—";
            var muted = state.Volume == 0; _mute.Content = DeviceArtwork.ControlIcon(muted ? "volume-off" : "volume-2");
            _mute.ToolTip = state.Connected ? muted ? "取消静音，恢复静音前音量" : "静音" : "连接音箱后调节音量";
            System.Windows.Automation.AutomationProperties.SetName(_mute, muted ? "取消静音" : "静音");
            _source.IsEnabled = !state.ConnectionBlocked;
            foreach (var (key, row) in _rows)
            {
                row.Item.IsSelected = key == state.SelectedKey; var active = key == state.ActiveKey; row.Item.IsEnabled = !state.Working || active;
                row.Card.BorderBrush = NativeFluentUi.Border; row.Connected.Visibility = active && state.Connected ? W.Visibility.Visible : W.Visibility.Collapsed;
                var showSingle = state.CanSingle && key == state.SelectedKey && ((PlaybackEntry)row.Item.Tag).IsGroup;
                row.Single.Visibility = showSingle ? W.Visibility.Visible : W.Visibility.Hidden;
                row.Action.VerticalAlignment = showSingle ? W.VerticalAlignment.Top : W.VerticalAlignment.Center;
                row.Action.Content = active && state.Working ? state.Connected ? "断开" : "取消" : key == state.SelectedKey && state.ConnectText.StartsWith("先用", StringComparison.Ordinal) ? "先用一只" : "连接";
                row.Action.IsEnabled = active && state.Working && state.CanConnect || !state.Working && !state.Searching && !state.ConnectionBlocked &&
                    ((PlaybackEntry)row.Item.Tag).HasIpv4Endpoint && (key != state.SelectedKey || state.CanConnect);
            }
        }
        finally { _rendering = false; }
    }
    private void AddRow(PlaybackEntry entry)
    {
        var grid = new C.Grid { MinHeight = 56, Margin = new W.Thickness(6, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto }); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        var art = DeviceArtwork.For(entry); grid.Children.Add(art);
        var names = new C.StackPanel { VerticalAlignment = W.VerticalAlignment.Center, Margin = new W.Thickness(0, 0, 12, 0) };
        var name = NativeFluentUi.Text(entry.Name, 14, true); name.TextWrapping = W.TextWrapping.NoWrap; name.TextTrimming = W.TextTrimming.CharacterEllipsis; name.ToolTip = entry.Name; name.Margin = new W.Thickness(0, 0, 0, 4); names.Children.Add(name);
        var kind = NativeFluentUi.Text(entry.IsGroup && entry.Members.All(m => m.IsHomePod) ? "HomePod组合" : entry.Kind, 12);
        kind.Foreground = NativeFluentUi.Secondary; kind.Margin = new W.Thickness(0); kind.LineHeight = double.NaN;
        // One text line owns the label and indicator, so font/DPI changes do
        // not align the dot against an unrelated stretched panel or line box.
        var connected = new C.Border { Visibility = W.Visibility.Collapsed, Margin = new W.Thickness(10, 0, 0, 0), Child = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = NativeFluentUi.Brush("#16843C") } };
        System.Windows.Automation.AutomationProperties.SetName(connected, "已连接");
        kind.Inlines.Add(new System.Windows.Documents.InlineUIContainer(connected) { BaselineAlignment = W.BaselineAlignment.Center }); names.Children.Add(kind);
        C.Grid.SetColumn(names, 1); grid.Children.Add(names);
        var action = NativeFluentUi.Button("连接", async () => await _connect(entry.Key), entry.Members.Any(m => m.IsHomePod)); action.VerticalAlignment = W.VerticalAlignment.Center; action.MinWidth = 72; action.Margin = new W.Thickness(0, 0, 8, 0); var actions = new C.Grid { Height = 56 };
        var fallback = NativeFluentUi.Button("先用一只", async () => { _select(entry.Key); await _fallback(); }); fallback.FontSize = 12; fallback.MinHeight = 22; fallback.Height = 24; fallback.Padding = new W.Thickness(8, 0, 8, 0); fallback.VerticalAlignment = W.VerticalAlignment.Bottom; fallback.Visibility = W.Visibility.Hidden;
        actions.Children.Add(action); actions.Children.Add(fallback); C.Grid.SetColumn(actions, 2); grid.Children.Add(actions);
        var card = NativeFluentUi.Card(grid); card.Padding = new W.Thickness(10, 6, 10, 6); card.Margin = new W.Thickness(0);
        var item = new C.ListBoxItem { Content = card, Tag = entry, HorizontalContentAlignment = W.HorizontalAlignment.Stretch, Padding = new W.Thickness(0), Margin = new W.Thickness(0, 0, 0, 6) };
        NativeFluentUi.PlainListItem(item);
        item.MouseDoubleClick += async (_, e) =>
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Left || InsideButton(e.OriginalSource as W.DependencyObject) || stateWorking()) return;
            e.Handled = true; await _connect(entry.Key);
        };
        bool stateWorking() => !action.IsEnabled || (action.Content as string) is "断开" or "取消";
        System.Windows.Automation.AutomationProperties.SetName(item, entry.Name + "，" + entry.Kind);
        _list.Items.Add(item); _rows.Add(entry.Key, (item, action, fallback, card, connected));
    }
    private static bool InsideButton(W.DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is C.Button) return true;
            node = node is M.Visual ? M.VisualTreeHelper.GetParent(node) : W.LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
