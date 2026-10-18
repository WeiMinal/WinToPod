// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;

namespace WinToPod.Desktop;

// Official WPF Fluent controls inside the existing native window/audio host.
// This incremental boundary avoids changing the working AirPlay/WASAPI engine.
internal static class NativeFluentUi
{
    public static readonly M.FontFamily UiFont = new("Microsoft YaHei UI");
    public static void Initialize()
    {
        var app = W.Application.Current ?? new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001 // Official .NET Fluent API is experimental; protocol does not depend on it.
        app.ThemeMode = W.ThemeMode.System;
#pragma warning restore WPF0001
        // Preserve the official implicit Fluent styles; font is inherited at each visual root.
        app.Resources["WinToPod.UiFont"] = UiFont;
    }
    public static void Mount(Form form, W.FrameworkElement root)
    {
        if (root is C.Control control) { control.FontFamily = UiFont; control.FontSize = 12; }
        void ApplyFonts(W.DependencyObject node)
        {
            if (node is C.Control item)
            {
                if (item.ReadLocalValue(C.Control.FontFamilyProperty) == W.DependencyProperty.UnsetValue) item.FontFamily = UiFont;
                if (item.ReadLocalValue(C.Control.FontSizeProperty) == W.DependencyProperty.UnsetValue) item.FontSize = 12;
            }
            foreach (var child in W.LogicalTreeHelper.GetChildren(node).OfType<W.DependencyObject>()) ApplyFonts(child);
        }
        ApplyFonts(root);
        root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, UiFont); root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, 12d);
        var host = new ElementHost { Dock = DockStyle.Fill, BackColor = FluentTheme.Background };
        // ElementHost's default Font map would overwrite the explicit Chinese fallback.
        host.PropertyMap.Remove("Font"); host.Child = new C.Border { Child = root, UseLayoutRounding = true, SnapsToDevicePixels = true };
        if (!form.MinimizeBox) root.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; form.DialogResult = DialogResult.Cancel; form.Close(); } };
        form.Controls.Add(host);
    }
    public static C.TextBlock Text(string text, double size = 12, bool strong = false) => new()
    {
        Text = text, FontFamily = UiFont, FontSize = size, FontWeight = strong ? W.FontWeights.SemiBold : W.FontWeights.Normal,
        TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 0, 8), LineHeight = size * (size >= 20 ? 1.25 : 1.5)
    };
    public static C.Button Button(string text, Action action, bool primary = false)
    {
        var button = new C.Button { Content = text, FontSize = 12, MinHeight = 28, MinWidth = 72, Padding = new W.Thickness(10, 3, 10, 3), FocusVisualStyle = null, Margin = new W.Thickness(0, 0, 8, 0), FontFamily = UiFont };
        var baseStyle = W.Application.Current.TryFindResource(primary ? "AccentButtonStyle" : typeof(C.Button)) as W.Style;
        if (baseStyle is not null)
        {
            var style = new W.Style(typeof(C.Button), baseStyle);
            var focus = new W.Trigger { Property = W.UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new W.Setter(C.Control.BackgroundProperty, new W.DynamicResourceExtension(primary ? "AccentButtonBackgroundPointerOver" : "ButtonBackgroundPointerOver")));
            style.Triggers.Add(focus); button.Style = style;
        }
        else if (primary) button.SetResourceReference(W.FrameworkElement.StyleProperty, "AccentButtonStyle");
        button.Click += (_, _) => action(); return button;
    }
    public static C.StackPanel Stack() => new() { Margin = new W.Thickness(20) };
    public static C.Border Card(W.UIElement child) => new()
    { Child = child, CornerRadius = new W.CornerRadius(8), BorderThickness = new W.Thickness(1), Margin = new W.Thickness(0, 0, 0, 8), Padding = new W.Thickness(12), Background = Surface, BorderBrush = Border };
    private static readonly M.SolidColorBrush SurfaceBrush = new(), BorderBrush = new(), SecondaryBrush = new(), ErrorBrush = new();
    public static M.Brush Surface => SurfaceBrush;
    public static M.Brush Border => BorderBrush;
    public static M.Brush Secondary => SecondaryBrush;
    public static M.Brush Error => ErrorBrush;
    public static void RefreshPalette()
    {
        M.Color Color(string value) => (M.Color)M.ColorConverter.ConvertFromString(value);
        var high = W.SystemParameters.HighContrast;
        SurfaceBrush.Color = high ? W.SystemColors.WindowColor : Color(FluentTheme.Dark ? "#2D2D2D" : "#FBFBFB");
        BorderBrush.Color = high ? W.SystemColors.WindowTextColor : Color(FluentTheme.Dark ? "#454545" : "#E5E5E5");
        SecondaryBrush.Color = high ? W.SystemColors.WindowTextColor : Color(FluentTheme.Dark ? "#C8C8C8" : "#606060");
        ErrorBrush.Color = high ? W.SystemColors.WindowTextColor : Color(FluentTheme.Dark ? "#FF99A4" : "#A4262C");
    }
    public static M.Brush Brush(string color) => new M.SolidColorBrush((M.Color)M.ColorConverter.ConvertFromString(color));
    public static C.ScrollViewer Scroll(W.UIElement child) => new() { Content = child, VerticalScrollBarVisibility = C.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = C.ScrollBarVisibility.Disabled };
    public static W.FrameworkElement Dialog(C.StackPanel content, params C.Button[] buttons)
    {
        var root = new C.DockPanel { LastChildFill = true };
        var footer = new C.WrapPanel { Margin = new W.Thickness(24, 12, 16, 20), HorizontalAlignment = W.HorizontalAlignment.Right };
        foreach (var button in buttons) footer.Children.Add(button);
        C.DockPanel.SetDock(footer, C.Dock.Bottom); root.Children.Add(footer); root.Children.Add(Scroll(content));
        root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, UiFont); root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, 14d); return root;
    }
    public static void ConfigureDialog(Form form, string title, int width = 520, int height = 420)
    {
        form.Text = "WinToPod · " + title; form.StartPosition = FormStartPosition.CenterParent;
        form.ClientSize = new Size(width, height); form.MinimumSize = new Size(Math.Min(width, 460), 320);
        form.MaximizeBox = false; form.MinimizeBox = false; form.ShowInTaskbar = false;
        form.AutoScaleMode = AutoScaleMode.Dpi; form.AutoScaleDimensions = new SizeF(96, 96);
        form.KeyPreview = true; form.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { form.DialogResult = DialogResult.Cancel; form.Close(); } };
    }
    public static void ShowMessage(string title, string message)
    {
        using var form = new FluentForm(); ConfigureDialog(form, title, 560, 380);
        var stack = Stack(); stack.Children.Add(Text(title, 22, true)); stack.Children.Add(Text(message));
        Mount(form, Dialog(stack, Button("知道了", () => form.Close(), true))); form.ShowDialog();
    }
    public static void PlainList(C.ListBox list)
    {
        list.FocusVisualStyle = null; list.Padding = new W.Thickness(0);
        list.Template = (C.ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
          <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBox"><ScrollViewer Focusable="False" Padding="0" CanContentScroll="True" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"><ItemsPresenter/></ScrollViewer></ControlTemplate>
          """);
    }
    public static void PlainListItem(C.ListBoxItem item)
    {
        item.FocusVisualStyle = null;
        item.Template = (C.ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
          <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem"><ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Stretch"/></ControlTemplate>
          """);
    }
}
