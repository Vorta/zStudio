using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Desktop;

internal sealed record ZoneEditorActions(Action<bool> Mode, Action<byte[]> Zones, Action<bool?> Gate,
    Action<bool> SharedScope, Action Paint, Action Select, Func<Task> Apply, Action Cancel, Func<byte, string, Task> Label);

internal sealed class ZonePropertiesEditor : SourcePropertiesEditor
{
    private JsonObject state;
    internal void RefreshState(JsonObject value) => state = value;
    public override string Title => "Map zones";
    public override JsonObject Json => (JsonObject)state.DeepClone();
    public ZonePropertiesEditor(JsonObject state, SourceZoneCatalog catalog, ZoneEditorActions actions)
    {
        this.state = state;
        StackPanel form = new() { Margin = new(12) };
        Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Label(form, "Map zones", true);
        ReadOnlyText(form, catalog.Manifest);
        Label(form, "Paint exact visible faces or objects, up to 4,096 targets per draft. Strokes stay in a draft until Apply; one Apply is one undoable edit.");
        Label(form, "Faces sharing one logical model use the same assignment. Shared scope must be chosen explicitly when other placements are affected.");
        Input(form, "Target", state["faces"]!.GetValue<bool>() ? "faces" : "objects", text =>
        {
            if (text is not ("faces" or "objects")) throw new FormatException("Enter faces or objects.");
            actions.Mode(text == "faces");
        }, hint: "faces or objects; cancel selected targets before changing modes");
        var assigned = state["zones"]!.AsArray().Select(x => x!.GetValue<byte>()).ToArray();
        Input(form, "Zone IDs", string.Join(", ", assigned), text => actions.Zones(ParseIds(text)),
            hint: "faces: zero to three ordered IDs; objects: one or empty to keep; 255 means Any");
        Input(form, "Object gate", state["gate"] == null ? "keep" : state["gate"]!.GetValue<bool>() ? "on" : "off", text =>
            actions.Gate(text switch { "keep" => null, "on" => true, "off" => false, _ => throw new FormatException("Enter keep, on or off.") }),
            hint: "separate object altitude-probe gate; keep leaves it unchanged");
        Input(form, "Shared scope", state["sharedScope"]!.GetValue<bool>() ? "yes" : "no", text =>
            actions.SharedScope(text switch { "yes" => true, "no" => false, _ => throw new FormatException("Enter yes or no.") }),
            hint: "yes permits all uses of the selected logical asset/mesh in this map");
        ReadOnlyText(form, $"{state["targetCount"]} selected targets · release finishes a stroke; it does not apply it");
        if (state["outlineLimited"]?.GetValue<bool>() == true)
            Label(form, "Only part of the selection outline is shown because it exceeds 200,000 points. Apply still edits every selected target.");
        StackPanel selection = new() { Orientation = Orientation.Horizontal }; form.Children.Add(selection);
        AsyncButton(selection, state["painting"]?.GetValue<bool>() == true ? "Stop painting" : "Paint in viewport", () => { actions.Paint(); return Task.CompletedTask; });
        AsyncButton(selection, "Add selected surface or object", () => { actions.Select(); return Task.CompletedTask; });
        StackPanel commit = new() { Orientation = Orientation.Horizontal }; form.Children.Add(commit);
        AsyncButton(commit, "Apply zone draft", actions.Apply);
        AsyncButton(commit, "Cancel zone draft", () => { actions.Cancel(); return Task.CompletedTask; });
        Label(form, "Map zone names", true);
        foreach (var entry in catalog.Labels)
            ReadOnlyText(form, $"{entry.Id}: {entry.Label} · {entry.NodeUses} object assignments · {entry.PolygonUses} face assignments");
        byte labelId = assigned.FirstOrDefault();
        Input(form, "Name zone ID", labelId.ToString(CultureInfo.InvariantCulture), text =>
        {
            if (!byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out labelId)) throw new FormatException("Enter an ID from 0 to 255.");
        });
        Input(form, "Zone name", "", _ => throw new InvalidOperationException("Use the asynchronous edit."),
            asyncCommit: text => actions.Label(labelId, text), hint: "name an ID; catalog edits are separate undoable changes");
    }

    internal static byte[] ParseIds(string text)
    {
        if (text.Length > 32) throw new FormatException("Enter up to three zone IDs from 0 to 255.");
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 3) throw new FormatException("A face supports at most three ordered zone IDs.");
        return parts.Select(p => byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out byte id)
            ? id : throw new FormatException("Enter zone IDs from 0 to 255; 255 means Any.")).ToArray();
    }
}
