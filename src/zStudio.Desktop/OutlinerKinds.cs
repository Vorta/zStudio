using System.Windows;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>The source map outliner's node types: each has an icon and a colour, shared by the tree, its legend and Properties.</summary>
internal static class OutlinerKinds
{
    internal static readonly string[] Categories = ["group", "geometry", "object", "node"];
    private static Brush Frozen(byte r, byte g, byte b) { SolidColorBrush brush = new(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    private static readonly Brush GroupBrush = Frozen(0xE8, 0xC2, 0x6B), GeometryBrush = Frozen(0x7F, 0xB8, 0xE8), NodeBrush = Frozen(0xC9, 0xA2, 0xFF);
    internal static string Icon(string category) => category switch { "world" => "World", "group" => "Folder", "geometry" => "Geometry", "object" => "Cube", _ => "Node" };
    /// <summary>A later pass's own icon and colour: AI vehicles (aiv), pickups (puppies) and map animations (anim).</summary>
    internal static string PassIcon(string kind) => kind switch { "aiv" => "Tank", "puppies" => "Pickup", "anim" => "Animation", _ => "Folder" };
    internal static Brush PassBrush(string kind) => kind switch { "aiv" => VehicleBrush, "puppies" => PickupBrush, "anim" => AnimationBrush, _ => GroupBrush };
    private static readonly Brush VehicleBrush = Frozen(0xE8, 0x90, 0x7F), PickupBrush = Frozen(0x8F, 0xD1, 0x8B), AnimationBrush = Frozen(0x6F, 0xC8, 0xD6);
    internal static string Label(string category) => category switch { "world" => "World", "group" => "Group", "geometry" => "Geometry", "object" => "Object", _ => "Node" };
    internal static Brush Brush(string category) => category switch
    {
        "group" => GroupBrush, "geometry" => GeometryBrush, "node" => NodeBrush,
        "object" => Application.Current?.TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.Orange,
        _ => Application.Current?.TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.Gray,
    };
}
