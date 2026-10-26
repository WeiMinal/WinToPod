// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Text;

namespace WinToPod.WinUI;

internal static class Ui
{
    public static TextBlock Text(string text, double size = 14, bool semibold = false) => new()
    {
        Text = text, FontSize = size, FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };
    public static TextBlock Hint(string text) { var t = Text(text, 12); t.Opacity = .7; return t; }
    public static StackPanel Column(double spacing = 12) => new() { Spacing = spacing };
    public static StackPanel Row(double spacing = 8) => new() { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
    public static Button Button(string text, Action action, bool accent = false)
    {
        var button = new Button { Content = text, MinHeight = 32, VerticalAlignment = VerticalAlignment.Center };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => action(); return button;
    }
    public static Button IconButton(string glyph, string label, Action action)
    {
        var b = Button("", action); b.Content = Icon(glyph); b.Width = b.Height = 36; b.Padding = new Thickness(5);
        b.Style = (Style)Application.Current.Resources["SubtleButtonStyle"];
        Label(b, label); return b;
    }
    public static FontIcon Icon(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size };
    public static void Label(FrameworkElement element, string label)
    { AutomationProperties.SetName(element, label); ToolTipService.SetToolTip(element, label); }
    public static Border Card(UIElement content, Thickness? padding = null)
    {
        var card = (Border)XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='1' CornerRadius='8' />");
        card.Padding = padding ?? new Thickness(16); card.Child = content; return card;
    }
    public static Grid Grid(params GridLength[] columns)
    { var grid = new Grid(); foreach (var width in columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width }); return grid; }
    public static GridLength Auto => GridLength.Auto;
    public static GridLength Star => new(1, GridUnitType.Star);
    public static void Add(Grid grid, FrameworkElement child, int column = 0, int row = 0)
    { Microsoft.UI.Xaml.Controls.Grid.SetColumn(child, column); Microsoft.UI.Xaml.Controls.Grid.SetRow(child, row); grid.Children.Add(child); }
    public static FrameworkElement DeviceIcon(string kind, bool pair = false)
    {
        var key = kind switch { "tv" => "tv-minimal", "laptop" => "laptop-minimal", _ => kind };
        if (key is not ("speaker" or "tv-minimal" or "laptop-minimal" or "cast" or "volume-2" or "volume-off" or "audio-lines")) key = "cast";
        FrameworkElement Single()
        {
            // Convert only the pinned bundled Lucide geometry. Original SVGs are shared with the existing UI.
            using var input = typeof(Ui).Assembly.GetManifestResourceStream("WinToPod.DeviceIcons." + key + ".svg") ?? throw new IOException("设备图标缺失。");
            var svg = System.Xml.Linq.XDocument.Load(input).Root ?? throw new IOException("设备图标无效。");
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var canvas = new System.Xml.Linq.XElement(ns + "Canvas", new System.Xml.Linq.XAttribute("Width", "24"), new System.Xml.Linq.XAttribute("Height", "24"));
            foreach (var shape in svg.Elements())
            {
                string A(string name) => shape.Attribute(name)?.Value ?? "0";
                double N(string name) => double.Parse(A(name), System.Globalization.CultureInfo.InvariantCulture);
                var node = shape.Name.LocalName switch
                {
                    "path" => new System.Xml.Linq.XElement(ns + "Path", new System.Xml.Linq.XAttribute("Data", A("d"))),
                    "rect" => new System.Xml.Linq.XElement(ns + "Rectangle", new System.Xml.Linq.XAttribute("Width", A("width")), new System.Xml.Linq.XAttribute("Height", A("height")), new System.Xml.Linq.XAttribute("Canvas.Left", A("x")), new System.Xml.Linq.XAttribute("Canvas.Top", A("y")), new System.Xml.Linq.XAttribute("RadiusX", A("rx")), new System.Xml.Linq.XAttribute("RadiusY", shape.Attribute("ry")?.Value ?? A("rx"))),
                    "circle" => new System.Xml.Linq.XElement(ns + "Ellipse", new System.Xml.Linq.XAttribute("Width", N("r") * 2), new System.Xml.Linq.XAttribute("Height", N("r") * 2), new System.Xml.Linq.XAttribute("Canvas.Left", N("cx") - N("r")), new System.Xml.Linq.XAttribute("Canvas.Top", N("cy") - N("r"))),
                    "line" => new System.Xml.Linq.XElement(ns + "Line", new System.Xml.Linq.XAttribute("X1", A("x1")), new System.Xml.Linq.XAttribute("X2", A("x2")), new System.Xml.Linq.XAttribute("Y1", A("y1")), new System.Xml.Linq.XAttribute("Y2", A("y2"))),
                    _ => throw new IOException("设备图标包含未支持的形状。")
                };
                node.SetAttributeValue("Stroke", "{ThemeResource TextFillColorPrimaryBrush}"); node.SetAttributeValue("StrokeThickness", "2");
                node.SetAttributeValue("StrokeStartLineCap", "Round"); node.SetAttributeValue("StrokeEndLineCap", "Round"); node.SetAttributeValue("StrokeLineJoin", "Round");
                canvas.Add(node);
            }
            var markup = new System.Xml.Linq.XElement(ns + "Viewbox", new System.Xml.Linq.XAttribute("Width", "24"), new System.Xml.Linq.XAttribute("Height", "24"), canvas);
            return (FrameworkElement)XamlReader.Load(markup.ToString());
        }
        if (!pair) return Single();
        var row = Row(2); row.Children.Add(Single()); row.Children.Add(Single()); return row;
    }
}
