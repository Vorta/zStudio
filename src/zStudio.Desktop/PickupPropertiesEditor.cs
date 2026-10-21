using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using zStudio.Core;

namespace zStudio.Desktop;

/// <summary>A source map's pickup position, bound to its placement source (never the viewport's current selection) and edited through the sources.</summary>
public sealed class PickupPropertiesEditor : FieldEditor, IDisposable
{
    private readonly DocumentModel document;
    private readonly MissionPickupSource source;
    private readonly JsonObject original;
    private string shownPosition = "";
    private readonly ReadOnlyPropertySheet details = new();
    public event Action? Changed;
    public JsonObject Json
    {
        get
        {
            var json = (JsonObject)original.DeepClone();
            // Another document's change to the archive can leave the current session without this record.
            if (document.PickupEdits is { } gone && gone.Find(source) == null) json["placement_unavailable"] = Gone;
            else if (document.PickupEdits is { } edits)
            {
                json.Remove("placement_unavailable");
                json["preview_world_position"] = JsonData.Vector(edits.Position(source));
                json["source_archive"] = source.ArchivePath;
                json["resource"] = source.ResourceName; json["placement_record"] = source.RecordIndex;
                json["edit_scope"] = edits.Scope(source).Description;
            }
            return json;
        }
    }
    /// <summary>Moves a placement through its sources (the edit rebuilds the world).</summary>
    private readonly Func<MissionPickupSource, Vector3, Task> sourceMove;
    public PickupPropertiesEditor(DocumentModel document, MissionPickupSource source, JsonObject metadata, Func<MissionPickupSource, Vector3, Task> sourceMove)
    {
        this.document = document; this.source = source; this.sourceMove = sourceMove;
        original = JsonData.PreviewObject(metadata);
        document.PropertyChanged += DocumentChanged;
        RefreshProperties();
    }
    private void DocumentChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(DocumentModel.PickupEdits)) RefreshProperties(); }
    // Reads and commits use the document's current session, never one captured when the form was built: a rebuilt world
    // has another one.
    private PickupPlacementEditSession Session() => document.PickupEdits is { } edits && edits.Find(source) != null ? edits
        : throw new InvalidOperationException("This map's placements are reloading or no longer hold this placement. Show the map again, then retry.");
    private const string Gone = "This map's placements no longer hold this record: another change moved, renamed or removed it in its archive. Select the placement again in Whole world.";
    private bool shownGone;
    protected override void RefreshProperties()
    {
        if (disposed || committingDraft) return;
        if (document.PickupEdits is not { } edits) return;
        var record = edits.Find(source);
        // Unfinished input stays visible (its commit is refused) until it is resolved.
        if (record == null ? !shownGone && !HasPendingDrafts : shownGone || Content == null)
        {
            shownGone = record == null; draftInputs.Clear(); ClearAutomationFields("properties"); valueRefresh.Clear();
            if (details.Parent is Panel previous) previous.Children.Remove(details);
            DockPanel panel = new(); Content = panel;
            StackPanel form = new() { Margin = new(12) }; DockPanel.SetDock(form, Dock.Top); panel.Children.Add(form);
            if (record == null)
            {
                // The last shown coordinates stay readable; nothing can be applied to a record the session no longer holds.
                Label(form, "Placement #" + source.RecordIndex, true); Label(form, Gone);
                Input(form, "Position", shownPosition, _ => throw new InvalidOperationException(Gone), readOnly: true, components: ["X", "Y", "Z"]);
                panel.Children.Add(details); details.Show(Json, false); Changed?.Invoke();
                return;
            }
            Label(form, record.Type + " · placement #" + source.RecordIndex, true);
            Label(form, "Position in game units · Enter to apply · Escape to restore");
            string Read()
            {
                if (document.PickupEdits is { } current && current.Find(source) != null)
                { var pos = current.Position(source); shownPosition = string.Join(", ", new[] { pos.X, pos.Y, pos.Z }.Select(v => v.ToEditorText())); }
                return shownPosition;
            }
            Vector3 Parse(string text)
            {
                string[] parts = text.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) throw new FormatException("Enter finite X, Y and Z coordinates.");
                float[] values = parts.Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                if (values.Any(v => !float.IsFinite(v))) throw new FormatException("Coordinates must be finite numbers.");
                return new(values[0], values[1], values[2]);
            }
            Input(form, "Position", Read(), _ => throw new InvalidOperationException("Use the asynchronous edit."), false, getter: Read, components: ["X", "Y", "Z"],
                asyncCommit: async text => { var position = Parse(text); if (position != Session().Position(source)) await sourceMove(source, position); });
            panel.Children.Add(details);
        }
        else foreach (var refresh in valueRefresh.ToArray()) refresh();
        details.Show(Json, false); Changed?.Invoke();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        document.PropertyChanged -= DocumentChanged;
        Content = null; draftInputs.Clear(); GC.SuppressFinalize(this);
    }
}
