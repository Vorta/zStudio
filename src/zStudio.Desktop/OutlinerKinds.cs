using System.Windows;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>The source map outliner's node types: each has an icon and a colour, shared by the tree, its legend and Properties.</summary>
internal static class OutlinerKinds
{
    internal static readonly string[] Categories = ["group", "geometry", "empty", "object", "node", "zone", "network", "definition"];
    /// <summary>What an empty node is, for its row's tooltip and its Properties subtitle.</summary>
    internal const string EmptyNote = "No geometry and no children: it draws nothing, but keeps its flags and zone.";
    private static Brush Frozen(byte r, byte g, byte b) { SolidColorBrush brush = new(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    internal static Brush Frozen(string hex) { SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
    private static readonly Brush ZonesBrush = Frozen(0xF0, 0xA3, 0xC9);
    /// <summary>The labels of the World tree's sections (the later passes keep their kinds aiv, puppies and anim).</summary>
    internal static string SectionLabel(string section) => section switch
    {
        "zones" => "Zones", "aiNetworks" => "AI Networks", "definitions" => "Animations",
        "aiv" => "AI Vehicles", "puppies" => "Pickups", "anim" => "Objects placed by animations", _ => section,
    };
    internal static string SectionIcon(SectionEntry section) => section switch
    {
        SectionHeader { Section: "zones" } => "Zones", SectionHeader { Section: "aiNetworks" } => "AiNodes", SectionHeader => "Definition",
        DefinitionScopeEntry or PickupGroupEntry => "Folder", DefinitionFileEntry or MissingFileEntry => "DefinitionFile", DefinitionEntry => "Definition", _ => "",
    };
    internal static Brush SectionBrush(SectionEntry section) => section switch
    {
        SectionHeader { Section: "zones" } => ZonesBrush, SectionHeader { Section: "aiNetworks" } => VehicleBrush,
        SectionHeader or DefinitionEntry => AnimationBrush,
        // Pickups' groups only sort: a tertiary folder, not the gold of world groups.
        PickupGroupEntry => Application.Current?.TryFindResource("TextFillColorTertiaryBrush") as Brush ?? Brushes.Gray,
        _ => Application.Current?.TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray,
    };
    /// <summary>An attack strategy in words.</summary>
    internal static string StrategyName(zStudio.Core.AiAttackStrategyKind kind) => kind switch
    {
        zStudio.Core.AiAttackStrategyKind.HEA => "Head-on", zStudio.Core.AiAttackStrategyKind.CIR => "Circle", zStudio.Core.AiAttackStrategyKind.BAC => "Back and forth",
        zStudio.Core.AiAttackStrategyKind.FOL => "Follow network", zStudio.Core.AiAttackStrategyKind.ZIG => "Zigzag", zStudio.Core.AiAttackStrategyKind.SIT => "Sit", _ => "Unknown",
    };
    private static readonly Brush GroupBrush = Frozen(0xE8, 0xC2, 0x6B), GeometryBrush = Frozen(0x7F, 0xB8, 0xE8), NodeBrush = Frozen(0xC9, 0xA2, 0xFF);
    internal static string Icon(string category) => category switch { "world" => "World", "group" => "Folder", "geometry" => "Geometry", "empty" => "Empty", "object" => "Cube", "zone" => "Zones", "network" => "AiNodes", "definition" => "Definition", _ => "Node" };
    /// <summary>A later pass's own icon and colour: AI vehicles (aiv), pickups (puppies) and map animations (anim).</summary>
    internal static string PassIcon(string kind) => kind switch { "aiv" => "Tank", "puppies" => "Pickup", "anim" => "Animation", _ => "Folder" };
    internal static Brush PassBrush(string kind) => kind switch { "aiv" => VehicleBrush, "puppies" => PickupBrush, "anim" => AnimationBrush, _ => GroupBrush };
    private static readonly Brush VehicleBrush = Frozen(0xE8, 0x90, 0x7F), PickupBrush = Frozen(0x8F, 0xD1, 0x8B), AnimationBrush = Frozen(0x6F, 0xC8, 0xD6);
    // Empty nodes are slate: lighter on the dark theme, darker on the light one.
    private static readonly Brush EmptyDarkBrush = Frozen(0x9F, 0xB0, 0xC2), EmptyLightBrush = Frozen(0x5B, 0x6B, 0x7C);
    private static Brush EmptyBrush => LightTheme ? EmptyLightBrush : EmptyDarkBrush;
    /// <summary>The light theme is on (its primary text is dark).</summary>
    internal static bool LightTheme => Application.Current?.TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush { Color: var text } && text.R + text.G + text.B < 384;
    internal static Brush Brush(string category) => category switch
    {
        "group" => GroupBrush, "geometry" => GeometryBrush, "empty" => EmptyBrush, "node" => NodeBrush, "zone" => ZonesBrush, "network" => VehicleBrush, "definition" => AnimationBrush,
        "object" => Application.Current?.TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.Orange,
        _ => Application.Current?.TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.Gray,
    };
}
