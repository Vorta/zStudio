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
    /// <summary>The parent's name, or null for a node without one.</summary>
    public string? Parent { get; init; }
    /// <summary>The object that deleting, copying and re-parenting this node apply to, when it is not this node (a loaded model's root).</summary>
    public string? Object { get; init; }
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

/// <summary>Structural edits Properties offers for an object: move under a parent (by name), copy as a new name, delete.</summary>
internal sealed record SourceObjectStructure(Func<string, Task> Reparent, Func<string, Task> Duplicate, Func<Task> Delete);

/// <summary>
/// Properties of a world object in a source world. Edits change the sources that placed the object (see
/// <see cref="SourceObjectEdits"/>) and rebuild the world; the window then shows the same object in the rebuilt world.
/// </summary>
internal sealed class SourceObjectPropertiesEditor : SourcePropertiesEditor
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
    private readonly Func<string, IReadOnlyList<string>, Task>? command;
    private readonly SourceObjectStructure? structure;
    public override JsonObject Json => state.Json();
    public override string Title => state.Name;
    public SourceObjectState State => state;

    public SourceObjectPropertiesEditor(SourceObjectState state, Func<ObjectTransform, Task> transform, Func<uint, bool, Task> flag, Func<string, IReadOnlyList<string>, Task>? command = null, SourceObjectStructure? structure = null)
    {
        this.state = state; this.transform = transform; this.flag = flag; this.command = command; this.structure = structure;
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
        // Settings a script made with a property command (fog, lights, cameras): each writer's arguments, as written.
        if (command != null)
            foreach (var (name, writer) in state.Origin.Writers.Where(w => SourceObjectEdits.PropertyCommands.ContainsKey(w.Key)).OrderBy(w => w.Key, StringComparer.Ordinal))
            {
                if (writer.Tokens.Count > 17 || writer.Tokens.Skip(1).Sum(t => (long)t.Length + 1) > 4096)
                {
                    ReadOnlyText(form, name + ": arguments exceed the Properties display limit; edit the source script directly.");
                    continue;
                }
                string written = string.Join(" ", writer.Tokens.Skip(1));
                Input(form, name, written, _ => throw new InvalidOperationException("Use the asynchronous edit."), hint: SourceObjectEdits.PropertyCommands[name] + $" · {JsonData.ShownText(writer.Script, 256)} line {writer.Line}",
                    asyncCommit: async text =>
                    {
                        string[] args = ComponentText.BoundedTokens(text, " ,\t", 1, 8, 64, "Give 1–8 arguments of up to 64 characters.");
                        if (string.Join(" ", args) != written) await command(name, args);
                    });
            }
        if (state.Class != nameof(Recoil.Zbd.Core.Worlds.WorldNodeClass.Object3D) && state.Class != nameof(Recoil.Zbd.Core.Worlds.WorldNodeClass.Lod)) { RaiseChanged(); return; }
        if (structure != null)
        {
            // Deleting, copying and re-parenting apply to the whole object (a loaded model's root), named when it is not this node.
            if (state.Object != null) ReadOnlyText(form, $"Part of {state.Object}: copying, deleting and moving to another parent apply to {state.Object}.");
            Input(form, "Parent", state.Parent ?? "", _ => throw new InvalidOperationException("Use the asynchronous edit."), hint: "a node's name; the world's name makes it a root of the world (of its part, for a part's node)",
                asyncCommit: async text => { text = text.Trim(); if (text.Length == 0) throw new FormatException("Enter the parent's name."); if (text != state.Parent) await structure.Reparent(text); });
            Input(form, "Copy as", "", _ => throw new InvalidOperationException("Use the asynchronous edit."), hint: "the copy's name; Enter makes the copy",
                asyncCommit: async text => { text = text.Trim(); if (text.Length > 0) await structure.Duplicate(text); });
            StackPanel actions = new() { Orientation = Orientation.Horizontal };
            AsyncButton(actions, "Delete object", structure.Delete);
            form.Children.Add(actions);
        }
        foreach (var (bit, label) in EditableFlags)
        {
            bool on = (state.Flags & bit) != 0;
            // A script object's flag no script command sets (ClipTo) is shown, not offered.
            if (!SourceObjectEdits.FlagSettable(state.Origin, bit)) { ReadOnlyText(form, $"{label}: {(on ? "on" : "off")} (no script command sets it)"); continue; }
            Input(form, label, on ? "on" : "off", _ => throw new InvalidOperationException("Use the asynchronous edit."), hint: "on or off",
                asyncCommit: async text =>
                {
                    bool value = FlagValue(text);
                    if (value != on) await flag(bit, value);
                });
        }
        RaiseChanged();
    }
    private static bool FlagValue(string text)
    {
        var value = text.AsSpan().Trim();
        if (value.Equals("on", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.SequenceEqual("1")) return true;
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase) || value.SequenceEqual("0")) return false;
        throw new FormatException("Enter on or off.");
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
}
