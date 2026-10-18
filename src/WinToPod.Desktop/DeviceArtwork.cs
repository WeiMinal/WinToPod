// SPDX-License-Identifier: GPL-3.0-or-later
// Lucide SVGs: original ISC/MIT notices in licenses/Lucide-LICENSE.txt.
using System.Globalization;
using System.Xml.Linq;
using WinToPod.AirPlay.Discovery;
using W = System.Windows;
using M = System.Windows.Media;
using C = System.Windows.Controls;

namespace WinToPod.Desktop;

internal static class DeviceArtwork
{
    private static readonly Dictionary<string, M.Geometry> Icons = new(StringComparer.Ordinal)
    {
        ["speaker"] = Load("speaker"), ["laptop-minimal"] = Load("laptop-minimal"),
        ["tv-minimal"] = Load("tv-minimal"), ["cast"] = Load("cast"),
        ["volume-2"] = Load("volume-2"), ["volume-off"] = Load("volume-off"), ["audio-lines"] = Load("audio-lines")
    };
    public static C.Image For(PlaybackEntry entry)
    {
        var pods = entry.Members.Length > 0 && entry.Members.All(m => m.IsHomePod);
        var group = pods && entry.IsGroup;
        var tv = entry.Kind.Equals("Apple TV", StringComparison.OrdinalIgnoreCase) || entry.Members.Any(m => Starts(m.Model, "AppleTV"));
        var mac = entry.Kind.Equals("Mac", StringComparison.OrdinalIgnoreCase) || entry.Members.Any(m => Starts(m.Model, "Mac") || Starts(m.Model, "iMac"));
        var key = pods ? "speaker" : tv ? "tv-minimal" : mac ? "laptop-minimal" : "cast";
        var drawing = new M.DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(M.Brushes.Transparent, null, new W.Rect(0, 0, 48, 36));
            if (group) { Draw(context, key, 0, 6); Draw(context, key, 24, 6); }
            else Draw(context, key, 12, 6);
        }
        // Keep palette brushes mutable for live system theme/high-contrast changes.
        var image = new C.Image { Source = new M.DrawingImage(drawing), Width = 40, Height = 32, Margin = new W.Thickness(0, 0, 12, 0), Stretch = M.Stretch.Uniform, SnapsToDevicePixels = true, Tag = group ? "speaker-pair" : key };
        System.Windows.Automation.AutomationProperties.SetName(image, group ? "音箱组合" : pods ? "音箱" : tv ? "电视接收端" : mac ? "Mac" : "AirPlay接收端");
        return image;
    }
    public static C.Image ControlIcon(string key)
    {
        var drawing = new M.DrawingGroup();
        using (var context = drawing.Open()) { context.DrawRectangle(M.Brushes.Transparent, null, new W.Rect(0, 0, 24, 24)); Draw(context, key, 0, 0); }
        return new C.Image { Source = new M.DrawingImage(drawing), Width = 20, Height = 20, Stretch = M.Stretch.Uniform, Tag = key };
    }
    private static bool Starts(string? value, string prefix) => value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true;
    private static void Draw(M.DrawingContext context, string key, double x, double y)
    {
        var pen = new M.Pen(NativeFluentUi.Secondary, 2) { StartLineCap = M.PenLineCap.Round, EndLineCap = M.PenLineCap.Round, LineJoin = M.PenLineJoin.Round };
        context.PushTransform(new M.TranslateTransform(x, y)); context.DrawGeometry(null, pen, Icons[key]); context.Pop();
    }
    // Only the pinned embedded SVGs are read; no arbitrary file or network access.
    private static M.Geometry Load(string key)
    {
        using var stream = typeof(DeviceArtwork).Assembly.GetManifestResourceStream("WinToPod.DeviceIcons." + key + ".svg") ?? throw new InvalidOperationException("Missing bundled device icon: " + key);
        var root = XDocument.Load(stream).Root ?? throw new InvalidOperationException("Invalid icon.");
        var group = new M.GeometryGroup();
        foreach (var e in root.Elements())
        {
            double N(string name) => double.Parse(e.Attribute(name)?.Value ?? "0", CultureInfo.InvariantCulture);
            var geometry = e.Name.LocalName switch
            {
                "path" => M.Geometry.Parse(e.Attribute("d")!.Value),
                "rect" => new M.RectangleGeometry(new W.Rect(N("x"), N("y"), N("width"), N("height")), N("rx"), e.Attribute("ry") is null ? N("rx") : N("ry")),
                "circle" => new M.EllipseGeometry(new W.Point(N("cx"), N("cy")), N("r"), N("r")),
                "line" => new M.LineGeometry(new W.Point(N("x1"), N("y1")), new W.Point(N("x2"), N("y2"))),
                _ => throw new InvalidOperationException("Unsupported bundled icon shape.")
            };
            group.Children.Add(geometry);
        }
        group.Freeze(); return group;
    }
}
