using System.Windows;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>The source map outliner's node types: each has an icon and a colour, shared by the tree, its filters and Properties.</summary>
internal static class OutlinerKinds
{
    internal static readonly string[] Categories = ["group", "geometry", "object", "node"];
    private static Brush Frozen(byte r, byte g, byte b) { SolidColorBrush brush = new(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    private static readonly Brush GroupBrush = Frozen(0xE8, 0xC2, 0x6B), GeometryBrush = Frozen(0x7F, 0xB8, 0xE8), NodeBrush = Frozen(0xC9, 0xA2, 0xFF);
    internal static string Icon(string category) => category switch { "world" => "World", "group" => "Folder", "geometry" => "Geometry", "object" => "Cube", _ => "Node" };
    internal static string Label(string category) => category switch { "world" => "World", "group" => "Group", "geometry" => "Geometry", "object" => "Object", _ => "Node" };
    internal static Brush Brush(string category) => category switch
    {
        "group" => GroupBrush, "geometry" => GeometryBrush, "node" => NodeBrush,
        "object" => Application.Current?.TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.Orange,
        _ => Application.Current?.TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.Gray,
    };
}
