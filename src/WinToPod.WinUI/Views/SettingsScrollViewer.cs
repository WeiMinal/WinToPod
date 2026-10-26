// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;

namespace WinToPod.WinUI;

// Keep the SDK's scrollbar, input handling and layout. Only fade its visual
// when idle; Hidden/Auto toggles would change the available content width.
internal sealed class SettingsScrollViewer : Grid
{
    private readonly ScrollViewer _viewer;
    private readonly Microsoft.UI.Xaml.DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private ScrollBar? _bar;
    private Storyboard? _fade;
    private bool _loaded, _nearBar, _dragging, _shown;

    public SettingsScrollViewer(UIElement content)
    {
        _viewer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 6, 2, 6) };
        Children.Add(_viewer);
        _idle.Tick += (_, _) => { if (_loaded && !_nearBar && !_dragging) Hide(true); else _idle.Stop(); };
        Loaded += (_, _) => { _loaded = true; EnsureBar(); Hide(false); };
        Unloaded += (_, _) => { _loaded = false; _nearBar = _dragging = false; Hide(false); };
        _viewer.ViewChanged += (_, _) => Show();
        ActualThemeChanged += (_, _) => { if (_loaded) _ = DispatcherQueue.TryEnqueue(() => { if (_loaded) EnsureBar(); }); };
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => Show()), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            var wasNear = _nearBar; _nearBar = NearBar(e);
            if (_nearBar) Show(); else if (wasNear) ScheduleHide();
        }), true);
        PointerExited += (_, e) =>
        {
            var p = e.GetCurrentPoint(this).Position;
            if (p.X < 0 || p.X > ActualWidth || p.Y < 0 || p.Y > ActualHeight)
            { _nearBar = false; ScheduleHide(); }
        };
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        { if (NearBar(e)) { _dragging = true; Show(); } }), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler((_, e) =>
        { if (_dragging) { _dragging = false; _nearBar = NearBar(e); ScheduleHide(); } }), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) =>
        { _dragging = false; ScheduleHide(); }), true);
        AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down or
                Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown or
                Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End or Windows.System.VirtualKey.Space) Show();
        }), true);
    }
    private void EnsureBar()
    {
        if (_bar is { IsLoaded: true }) return;
        StopFade(); _viewer.ApplyTemplate(); _bar = FindBar(_viewer);
        if (_bar is not null) { _bar.Opacity = _shown ? 1 : 0; _bar.IsHitTestVisible = _shown; }
    }
    private static ScrollBar? FindBar(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollBar { Name: "VerticalScrollBar", Orientation: Orientation.Vertical } bar) return bar;
            // Do not alter scrollbars owned by controls in the page content.
            if (child is FrameworkElement { Name: "ScrollContentPresenter" }) continue;
            if (FindBar(child) is { } found) return found;
        }
        return null;
    }
    private bool NearBar(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        return p.X >= ActualWidth - 24 && p.X <= ActualWidth && p.Y >= 0 && p.Y <= ActualHeight;
    }
    private void Show()
    {
        if (!_loaded) return;
        EnsureBar(); if (_bar is null || _viewer.ScrollableHeight <= 0) return;
        StopFade(); _shown = true; _bar.Opacity = 1; _bar.IsHitTestVisible = true; ScheduleHide();
    }
    private void ScheduleHide()
    {
        _idle.Stop();
        if (_loaded && _shown && !_nearBar && !_dragging) _idle.Start();
    }
    private void Hide(bool animate)
    {
        _idle.Stop(); StopFade(); _shown = false;
        if (_bar is null) return;
        _bar.IsHitTestVisible = false;
        if (!animate || !_loaded) { _bar.Opacity = 0; return; }
        var opacity = new DoubleAnimation { From = 1, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(180)) };
        Storyboard.SetTarget(opacity, _bar); Storyboard.SetTargetProperty(opacity, "Opacity");
        _fade = new Storyboard(); _fade.Children.Add(opacity); _fade.Begin();
    }
    private void StopFade() { _fade?.Stop(); _fade = null; }
}
