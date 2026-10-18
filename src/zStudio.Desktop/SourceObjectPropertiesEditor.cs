using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using zStudio.Core;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;

namespace zStudio.Desktop;

/// <summary>A world object of a source world as Properties shows it: what it is, where it came from, and its editable transform and flags.</summary>
internal sealed record SourceObjectState(int Node, string Name, string Class, ObjectTransform? Transform, uint Flags, WorldNodeProvenance Origin, string Source, IReadOnlyList<string> Notes)
{
    /// <summary>The parent's name, or null for a node without one.</summary>
    public string? Parent { get; init; }
    /// <summary>The object that deleting, copying and re-parenting this node apply to, when it is not this node (a loaded model's root).</summary>
    public string? Object { get; init; }
    /// <summary>The project path of the model file the object references (its own reference or its one referencing child's), if any.</summary>
    public string? Model { get; init; }
    /// <summary>The built node's zone word: the effective zone, after inheritance and placement overrides.</summary>
    public uint Zone { get; init; } = 0xFF;
    /// <summary>The built parent's zone word, which the node takes when it inherits; null without a parent.</summary>
    public uint? ParentZone { get; init; }
    /// <summary>
    /// Whether the card can change the node's zone: an object or load root imported from a source model. Terrain keeps its zones
    /// in its recipe, and nodes the scripts create have none.
    /// </summary>
    public bool ZoneEditable => Origin.Terrain == null && (Origin.ModelFile != null || Origin.LoadedFile != null);
    /// <summary>A load root (a file the scripts load) has no parent to inherit a zone from.</summary>
    public bool ZoneCanInherit => ZoneEditable && Origin.ModelFile != null;
    /// <summary>The zone as the card's Zone field shows it: Inherit, Any or the zone number.</summary>
    public string ZoneText => Origin.ImportedZoneInherited == true && ZoneCanInherit ? "Inherit" : (Zone & 0xFF) == 0xFF ? "Any" : (Zone & 0xFF).ToString(CultureInfo.InvariantCulture);
    /// <summary>The zone readout lines; the selected-object card edits object zones and the polygon zone tool polygon zones.</summary>
    public IEnumerable<string> ZoneLines()
    {
        yield return $"Zone: {ZoneReadout.Zone(Zone, Origin.ImportedZoneInherited)} · altitude zone check {((Flags & WorldGltf.ZoneGate) != 0 ? "on" : "off")}";
        if (ZoneReadout.Source(Origin) is { } source) yield return "Zone source: " + source;
    }
    /// <summary>The node's class, which <see cref="Class"/> names.</summary>
    public WorldNodeClass NodeClass => Enum.TryParse(Class, out WorldNodeClass nodeClass) ? nodeClass : WorldNodeClass.Plain;
    public JsonObject Json()
    {
        JsonObject json = new() { ["node"] = Node, ["name"] = Name, ["class"] = Class, ["source"] = Source, ["flags"] = $"0x{Flags:X8}" };
        if (Transform is { } t)
        {
            json["position"] = JsonData.Vector(t.Position); json["rotation_degrees"] = JsonData.Vector(t.RotationDegrees); json["scale"] = JsonData.Vector(t.Scale);
        }
        json["zone"] = (Zone & 0xFF) == 0xFF ? "any" : (int)(Zone & 0xFF);
        json["altitude_zone_check"] = (Flags & WorldGltf.ZoneGate) != 0;
        if (Origin.ImportedZoneInherited == true) json["zone_inherited"] = true;
        if (ZoneReadout.Source(Origin) is { } source) json["zone_source"] = source;
        if (Model != null) json["model"] = Model;
        if (Notes.Count > 0) json["notes"] = new JsonArray(Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        if (Parent != null) json["parent"] = Parent;
        if (Object != null) json["object"] = Object;
        return json;
    }
}

/// <summary>An editor Properties shows for a source world: a world object or a terrain recipe. Its edits rebuild the world.</summary>
internal abstract class SourcePropertiesEditor : FieldEditor, IDisposable
{
    public event Action? Changed;
    protected void RaiseChanged() => Changed?.Invoke();
    public abstract JsonObject Json { get; }
    /// <summary>The heading Properties shows.</summary>
    public abstract string Title { get; }
    public void Dispose() { if (disposed) return; disposed = true; Content = null; draftInputs.Clear(); }
}

/// <summary>Shared object field availability for the card and MCP.</summary>
internal static class SourceObjectFields
{
    internal static readonly (uint Bit, string Label)[] EditableFlags =
    [
        (0x08, "Standable (altitude surface)"), (0x10, "Collision (intersection surface)"), (0x20, "Collide by bounding box"),
        (0x40, "Proximity"), (0x80, "Landmark (never culled)"), (0x10000, "CanModify (craters and quicksand allowed)"), (0x20000, "ClipTo (craters and quicksand blocked)"),
        (0x800000, "Overwrite"),
    ];
    internal static bool CommandFits(string nodeClass, string command) => command.StartsWith(nodeClass switch { "World" => "World", "Light" => "Light", "Camera" => "Camera", "Display" => "Display", _ => "\0" }, StringComparison.Ordinal);
}
