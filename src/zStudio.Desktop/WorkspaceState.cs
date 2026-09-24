using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Recoil.Zbd.Desktop;

/// <summary>Presentation preferences only. Automatic collapse never overwrites preferred dimensions.</summary>
public sealed class WorkspaceLayout
{
    public int Version { get; set; } = 3;
    public double NavigatorWidth { get; set; } = 294;
    public double InspectorWidth { get; set; } = 352;
    public double ToolsHeight { get; set; } = 180;
    public PropertyWindowBounds PropertiesWindow { get; set; } = new();
    public bool NavigatorVisible { get; set; } = true;
    public bool InspectorVisible { get; set; } = true;
    public bool ToolsVisible { get; set; } = true;
    public string Preset { get; set; } = "Edit";
    public string Density { get; set; } = "Compact";
    public int BrowserTab { get; set; }
    public int InspectorTab { get; set; }
    public int ToolTab { get; set; }
    public Dictionary<string, bool> Groups { get; set; } = [];
    public void Normalize()
    {
        if (Version < 2) BrowserTab = BrowserTab switch { 0 => 1, 1 => 0, _ => BrowserTab };
        if (Version < 3) InspectorTab = InspectorTab switch { 1 => 2, 2 => 1, _ => 0 };
        NavigatorWidth = Bound(NavigatorWidth, 294, 240, 650);
        InspectorWidth = Bound(InspectorWidth, 352, 320, 650);
        ToolsHeight = Bound(ToolsHeight, 180, 100, 700);
        BrowserTab = Math.Clamp(BrowserTab, 0, 3); InspectorTab = Math.Clamp(InspectorTab, 0, 2); ToolTab = Math.Clamp(ToolTab, 0, 5);
        if (Preset is not ("Inspect" or "Edit" or "Debug" or "Focus preview")) Preset = "Edit";
        if (Density is not ("Compact" or "Comfortable")) Density = "Compact";
        Groups ??= []; Version = 3;
        PropertiesWindow ??= new(); PropertiesWindow.Normalize();
    }
    private static double Bound(double value, double fallback, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed class PropertyWindowBounds
{
    public double Width { get; set; } = 640;
    public double Height { get; set; } = 720;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public void Normalize()
    {
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 400, 2400) : 640;
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 300, 2000) : 720;
        if (Left is double x && !double.IsFinite(x)) Left = null;
        if (Top is double y && !double.IsFinite(y)) Top = null;
    }
}

/// <summary>Preview-only presentation state for the authoritative toolbar controls.</summary>
public sealed partial class AnimationPreviewOptions : ObservableObject
{
    [ObservableProperty] private bool map;
    [ObservableProperty] private bool grid = true;
    [ObservableProperty] private bool collision = true;
    [ObservableProperty] private bool horizon = true;
    [ObservableProperty] private bool followCamera;
    [ObservableProperty] private bool effects = true;
}

public sealed partial class ProgramItem : ObservableObject
{
    public Guid Sequence { get; init; }
    public Guid Event { get; init; }
    public string Key => Event != Guid.Empty ? Event.ToString() : Sequence != Guid.Empty ? Sequence.ToString() : "entry";
    [ObservableProperty] private string label = "";
    [ObservableProperty] private string summary = "";
    [ObservableProperty] private string detail = "";
    [ObservableProperty] private string runtimeMarker = "";
    [ObservableProperty] private string description = "";
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool isSelected;
    public ObservableCollection<ProgramItem> Children { get; } = [];
}
