using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Desktop;

/// <summary>A world object of a source world as Properties shows it: what it is, where it came from, and its editable transform and flags.</summary>
internal sealed record SourceObjectState(int Node, string Name, string Class, ObjectTransform? Transform, uint Flags, WorldNodeProvenance Origin, string Source, IReadOnlyList<string> Notes)
{
    /// <summary>Identifies the object across rebuilds: its glTF node, or the instruction that created it.</summary>
    public string Identity => Origin.ModelFile is { } file ? $"{file}#{Origin.ModelNode}" : Origin.Created is { } created ? $"{created.Script}:{created.Line}" : $"node:{Node}";
    public JsonObject Json()
    {
        JsonObject json = new() { ["node"] = Node, ["name"] = Name, ["class"] = Class, ["source"] = Source, ["flags"] = $"0x{Flags:X8}" };
        if (Transform is { } t)
        {
            json["position"] = JsonData.Vector(t.Position); json["rotation_degrees"] = JsonData.Vector(t.RotationDegrees); json["scale"] = JsonData.Vector(t.Scale);
        }
        if (Notes.Count > 0) json["notes"] = new JsonArray(Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        return json;
    }
}

/// <summary>
/// Properties of a world object in a source world. Edits change the sources that placed the object (see
/// <see cref="SourceObjectEdits"/>) and rebuild the world; the window then shows the same object in the rebuilt world.
/// </summary>
internal sealed class SourceObjectPropertiesEditor : FieldEditor, IDisposable
{
    /// <summary>The node flags a source can set, with the names the editor shows.</summary>
    internal static readonly (uint Bit, string Label)[] EditableFlags =
    [
        (0x08, "Standable (altitude surface)"), (0x10, "Collision (intersection surface)"), (0x20, "Collide by bounding box"),
        (0x40, "Proximity"), (0x80, "Landmark (never culled)"), (0x10000, "Craters allowed (CanModify)"), (0x20000, "No craters (ClipTo)"),
    ];
    private readonly SourceObjectState state;
    private readonly Func<ObjectTransform, Task> transform;
    private readonly Func<uint, bool, Task> flag;
    public event Action? Changed;
    public JsonObject Json => state.Json();
    public SourceObjectState State => state;

    public SourceObjectPropertiesEditor(SourceObjectState state, Func<ObjectTransform, Task> transform, Func<uint, bool, Task> flag)
    {
        this.state = state; this.transform = transform; this.flag = flag;
        Build();
    }

    private void Build()
    {
        StackPanel form = new() { Margin = new(12) }; Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Label(form, $"{state.Name} · {state.Class} #{state.Node}", true);
        Label(form, "Edits change the project's sources and rebuild the world · Enter to apply · Escape to restore");
        ReadOnlyText(form, "Source: " + state.Source);
        foreach (string note in state.Notes) ReadOnlyText(form, note);
        if (state.Transform is { } current)
        {
            Vector(form, "Position", current.Position, v => current with { Position = v });
            Vector(form, "Rotation (degrees)", current.RotationDegrees, v => current with { RotationDegrees = v });
            Vector(form, "Scale", current.Scale, v => current with { Scale = v });
        }
        foreach (var (bit, label) in EditableFlags)
        {
            bool on = (state.Flags & bit) != 0;
            Input(form, label, on ? "on" : "off", _ => throw new InvalidOperationException("Use the asynchronous edit."), hint: "on or off",
                asyncCommit: async text =>
                {
                    bool value = text.Trim().ToLowerInvariant() switch { "on" or "true" or "1" => true, "off" or "false" or "0" => false, _ => throw new FormatException("Enter on or off.") };
                    if (value != on) await flag(bit, value);
                });
        }
        Changed?.Invoke();
    }
    private void Vector(StackPanel form, string label, Vector3 value, Func<Vector3, ObjectTransform> with)
    {
        string text = string.Join(", ", new[] { value.X, value.Y, value.Z }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        Input(form, label, text, _ => throw new InvalidOperationException("Use the asynchronous edit."), components: ["X", "Y", "Z"],
            asyncCommit: async input =>
            {
                string[] parts = input.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) throw new FormatException("Enter X, Y and Z.");
                float[] values = parts.Select(p => float.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                if (values.Any(v => !float.IsFinite(v))) throw new FormatException("Values must be finite numbers.");
                Vector3 next = new(values[0], values[1], values[2]);
                if (next != value) await transform(with(next));
            });
    }
    public void Dispose() { if (disposed) return; disposed = true; Content = null; draftInputs.Clear(); }
}
