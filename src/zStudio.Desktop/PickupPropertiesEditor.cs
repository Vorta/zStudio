using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

/// <summary>Coordinates are bound to a placement source, never the viewport's current selection.</summary>
public sealed class PickupPropertiesEditor : FieldEditor, IDisposable
{
    private readonly DocumentModel document;
    private readonly MissionPickupSource source;
    private readonly JsonObject original;
    private readonly string label;
    private bool? locked;
    private readonly ReadOnlyPropertySheet details = new();
    public event Action? Changed;
    public JsonObject Json
    {
        get
        {
            var json = (JsonObject)original.DeepClone();
            if (document.PickupEdits is { } edits)
            {
                json["preview_world_position"] = JsonData.Vector(edits.Position(source));
                json["source_archive"] = source.ArchivePath;
                json["resource"] = source.ResourceName; json["placement_record"] = source.RecordIndex;
                json["save_destination"] = edits.TargetPath(source.ArchivePath);
                json["edit_scope"] = edits.Scope(source).Description;
            }
            return json;
        }
    }
    public PickupPropertiesEditor(DocumentModel document, MissionPickupSource source, string label, JsonObject metadata)
    {
        this.document = document; this.source = source; this.label = label;
        original = (JsonObject)metadata.DeepClone();
        document.PickupEditsChanged += RefreshProperties; document.PropertyChanged += DocumentChanged;
        RefreshProperties();
    }
    private void DocumentChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(DocumentModel.PickupsLocked)) RefreshProperties(); }
    protected override void RefreshProperties()
    {
        if (disposed || committingDraft) return;
        if (document.PickupEdits is not { } edits || edits.Find(source) is not { } record) return;
        if (locked != document.PickupsLocked)
        {
            locked = document.PickupsLocked; draftInputs.Clear(); ClearAutomationFields("properties"); valueRefresh.Clear();
            if (details.Parent is Panel previous) previous.Children.Remove(details);
            DockPanel panel = new(); Content = panel;
            StackPanel form = new() { Margin = new(12) }; DockPanel.SetDock(form, Dock.Top); panel.Children.Add(form);
            Label(form, record.Type + " · placement #" + source.RecordIndex, true);
            Label(form, locked == true ? "Turn off the lock icon in Whole world to edit this placement." : "Position in game units · Enter to apply · Escape to restore");
            string Read() { var pos = edits.Position(source); return string.Join(", ", new[] { pos.X, pos.Y, pos.Z }.Select(v => v.ToEditorText())); }
            Input(form, "Position", Read(), text =>
            {
                if (document.PickupsLocked) throw new InvalidOperationException("This document's pickup placements are locked.");
                string[] parts = text.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) throw new FormatException("Enter finite X, Y and Z coordinates.");
                float[] values = parts.Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                if (values.Any(v => !float.IsFinite(v))) throw new FormatException("Coordinates must be finite numbers.");
                edits.MoveTo(source, new Vector3(values[0], values[1], values[2]));
            }, locked == true, getter: Read, components: ["X", "Y", "Z"]);
            panel.Children.Add(details);
        }
        else foreach (var refresh in valueRefresh.ToArray()) refresh();
        details.Show(Json, false); Changed?.Invoke();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        document.PickupEditsChanged -= RefreshProperties; document.PropertyChanged -= DocumentChanged;
        Content = null; draftInputs.Clear(); GC.SuppressFinalize(this);
    }
}
