using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Recoil.Zbd.Core;
using Recoil.Zbd.Rendering;
using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Desktop;

/// <summary>Fixed hover/selection presentation and a shared transform draft delegated to the document service.</summary>
internal sealed partial class SceneInspectionCard : Grid
{
    private readonly SceneViewport viewport;
    private readonly Func<SceneInspection, JsonObject> describe;
    private readonly Action<SceneInspectionCard> begin, apply;
    private readonly Border card;
    private readonly Border panel;
    private readonly TextBlock heading = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock error = new() { Foreground = Brushes.LightSalmon, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock scope = new() { Opacity = .75, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(0, 3, 0, 3) };
    private readonly StackPanel rows = new();
    private const string AttackStrategy = "Attack strategy";
    private readonly Dictionary<string, SceneInspectionField> values = [];
    private const string AuthoredPlacement = "Authored placement XYZ";
    private ValueTextBox[] Coordinates => values[AuthoredPlacement].Inputs;
    private readonly Button edit, cancel;
    private JsonObject details = [];
    private Guid draftId;
    internal DocumentModel? DraftDocument { get; private set; }
    internal MissionPickupSource? DraftSource { get; private set; }
    internal long DraftRevision { get; private set; }
    internal string? DraftTarget { get; private set; }
    internal bool HasDraft => DraftSource != null;
    internal event Action? DraftClosed;
    internal string DraftToken => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{draftId}|{DraftDocument?.SessionId}|{DraftRevision}|{DraftTarget}|{transformMode}|{string.Join('|', DraftStrings)}")));
    internal SceneInspection? Selection => viewport.SelectedInspection;
    internal object DescribeDraft() => new { token = DraftToken, target = DraftTarget, revision = DraftRevision,
        position = HasDraft ? Coordinates.Select(c => c.Text).ToArray() : new[] { "", "", "" }, pending = HasDraft,
        rotationDegrees = HasDraft && rotationKind == PlacementRotationKind.EulerRadians ? Angles.Select(c => c.Text).ToArray() : null,
        headingDegrees = HasDraft && rotationKind == PlacementRotationKind.HeadingDegrees ? Angles[0].Text : null,
        transformMode, rotationAxes = rotationKind switch { PlacementRotationKind.EulerRadians => "XYZ", PlacementRotationKind.HeadingDegrees => "Y", _ => "none" },
        handlesVisible = viewport.TransformHandlesVisible, boundsVisible = viewport.SelectionBoundsVisible };

    internal SceneInspectionCard(SceneViewport viewport, Func<SceneInspection, JsonObject> describe, Action<SceneInspectionCard> begin, Action<SceneInspectionCard> apply)
    {
        this.viewport = viewport; this.describe = describe; this.begin = begin; this.apply = apply;
        Background = null;
        InitializeTransformControls();
        Grid layout = new(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        DockPanel title = new(); var close = Button("×", "Close node card", () => { if (ResolvePending()) viewport.SelectInspection(null, false); });
        DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
        var copyDetails = Button("⧉", "Copy selected node details", () => Try(() => Copy(null, true)));
        DockPanel.SetDock(copyDetails, Dock.Right); title.Children.Add(copyDetails);
        edit = Button("✎", "Edit object transform", () => Try(() => { if (HasDraft) this.apply(this); else this.begin(this); }));
        DockPanel.SetDock(edit, Dock.Right); title.Children.Add(edit);
        cancel = Button("↶", "Discard transform draft", CancelDraft); cancel.Visibility = Visibility.Hidden;
        DockPanel.SetDock(cancel, Dock.Right); title.Children.Add(cancel); title.Children.Add(heading);
        layout.Children.Add(title);
        StackPanel body = new(); body.Children.Add(modes); body.Children.Add(scope); body.Children.Add(error); body.Children.Add(rows);
        error.Visibility = Visibility.Collapsed;
        ScrollViewer scroll = new() { Name = "InspectionScroll", Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 6, 0, 0) };
        scroll.SetResourceReference(Control.TemplateProperty, "InspectionScrollTemplate");
        SetRow(scroll, 1); layout.Children.Add(scroll);
        card = Box(layout); card.Name = "InspectionCard";
        card.BorderBrush = new SolidColorBrush(Color.FromArgb(96, 128, 128, 128)); card.BorderThickness = new(0, 1, 0, 0);
        card.Visibility = Visibility.Collapsed;
        panel = CreatePanel(); Children.Add(panel);
        viewport.InspectionChanged += Refresh; SizeChanged += (_, _) => Refresh();
        Refresh();
    }
    private static Border Box(UIElement child) => new() { Child = child, Padding = new(10) };
    private static Button Button(string text, string tooltip, Action action)
    {
        Button button = new() { Content = text, ToolTip = tooltip, Width = 30, Height = 30, Padding = new(0), Margin = new(2), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action(); return button;
    }
    private void Try(Action action)
    { try { action(); error.Text = ""; error.Visibility = Visibility.Collapsed; } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or System.IO.InvalidDataException or StudioCommandException or System.Runtime.InteropServices.ExternalException) { error.Text = ex.Message; error.Visibility = Visibility.Visible; } }
    internal void StartDraft(DocumentModel document, MissionPickupSource source, Vector3 position)
    {
        Refresh();
        if (!values.TryGetValue(AuthoredPlacement, out var field) || field.Binding != SceneInspectionBinding.AuthoredPosition)
            throw new StudioCommandException("not_ready", "The selection has no authored placement fields.");
        draftId = Guid.NewGuid();
        DraftDocument = document; DraftSource = source; DraftRevision = document.Revision; DraftTarget = Selection?.Target;
        rotationKind = document.PickupEdits?.RotationKind(source) ?? PlacementRotationKind.None;
        originalTransform = lastValidTransform = new(position, document.PickupEdits?.Rotation(source) ?? Vector3.Zero);
        transformMode = "move"; changingDraft = true;
        for (int i = 0; i < 3; i++) if (Coordinates[i].Text != position[i].ToString("R", CultureInfo.InvariantCulture)) Coordinates[i].Text = position[i].ToString("R", CultureInfo.InvariantCulture);
        for (int i = 0; i < Angles.Length; i++) Angles[i].Text = AngleText(originalTransform.Rotation[rotationKind == PlacementRotationKind.HeadingDegrees ? 1 : i], rotationKind);
        initialAngles = Angles.Select(c => c.Text).ToArray();
        foreach (var editable in EditableFields) editable.SetEditing(true);
        changingDraft = false;
        cancel.Visibility = Visibility.Visible; edit.Content = "✓"; edit.ToolTip = "Confirm transform";
        System.Windows.Automation.AutomationProperties.SetName(edit, "Confirm transform"); error.Text = ""; error.Visibility = Visibility.Collapsed;
        RefreshDraftPreview();
    }
    internal void RequireDraft(string token)
    { if (!HasDraft || token != DraftToken) throw new StudioCommandException("draft_conflict", "The transform draft changed. Read it again."); }
    internal Vector3 DraftPosition()
    {
        float[] p = new float[3];
        for (int i = 0; i < 3; i++)
            if (!float.TryParse(Coordinates[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out p[i]) || !float.IsFinite(p[i]))
                throw new StudioCommandException("invalid_argument", "XYZ must contain finite game-unit numbers (use a decimal point).");
        return new(p[0], p[1], p[2]);
    }
    internal void CancelDraft()
    {
        // Ending a draft from Enter/Escape (or MCP while a field has focus) must
        // not let TextBox's deferred caret reveal scroll the restored readout.
        if (HasDraft && EditableFields.SelectMany(f => f.Inputs).Any(input => input.IsKeyboardFocusWithin)) edit.Focus();
        DraftSource = null; DraftDocument = null; DraftTarget = null; draftId = Guid.Empty;
        DraftClosed?.Invoke();
        foreach (var field in EditableFields) field.SetEditing(false);
        viewport.EndTransformDraft(); transformMode = "move";
        cancel.Visibility = Visibility.Hidden; edit.Content = "✎"; edit.ToolTip = "Edit object transform";
        System.Windows.Automation.AutomationProperties.SetName(edit, "Edit object transform"); error.Text = ""; error.Visibility = Visibility.Collapsed; Refresh();
    }
    internal bool ResolvePending()
    {
        if (!HasDraft) return true;
        var answer = MessageBox.Show(Window.GetWindow(this), "Apply the pending position and rotation?\nYes: apply · No: discard · Cancel: keep editing", "Node transform draft", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) { CancelDraft(); return true; }
        Try(() => apply(this)); return !HasDraft;
    }
    internal string Copy(string? field, bool clipboard)
    {
        if (Selection == null) throw new StudioCommandException("not_ready", "Select a node first.");
        var current = describe(Selection);
        string text = field == null ? string.Join(Environment.NewLine, current.Select(p => p.Key + ": " + Display(p.Value, p.Key == AuthoredRotation))) :
            current.TryGetPropertyValue(field, out var value) ? field + ": " + Display(value, field == AuthoredRotation) : throw new StudioCommandException("invalid_argument", "Unknown copy field.");
        if (clipboard) Clipboard.SetText(text); return text;
    }
    private static string Display(JsonNode? value, bool degrees = false)
    {
        if (value is JsonObject vector && new[] { "x", "y", "z" }.All(k => vector[k] is JsonValue))
            return string.Join("   ", new[] { "x", "y", "z" }.Select(k => k.ToUpperInvariant() + " " + (degrees ? vector[k]!.GetValue<double>().ToString("R", CultureInfo.InvariantCulture) : JsonData.Scalar(vector[k], float.NaN).ToString("R", CultureInfo.InvariantCulture))));
        return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "Unavailable";
    }
    internal void Refresh()
    {
        var selected = Selection;
        RefreshHover();
        card.Visibility = selected == null && !HasDraft ? Visibility.Collapsed : Visibility.Visible;
        ArrangePanel();
        if (selected == null) return;
        details = describe(selected);
        heading.Text = Display(details["Node"]); heading.ToolTip = heading.Text;
        scope.Text = details["Edit scope"] is { } affected ? Display(affected) : "";
        scope.Visibility = scope.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        string[] first = selected.AiNode != null
            ? ["Network", "Network type", "Authored placement XYZ", "Object world origin XYZ", AttackStrategy, "Status"]
            : details.ContainsKey("Runtime instance")
            ? ["Runtime instance", "Object world origin XYZ", "Source node", "Placed instance", "Template"]
            : ["Authored placement XYZ", AuthoredRotation, AuthoredHeading, "Object world origin XYZ", "Source node", "Placed instance", "Template"];
        var keys = first.Where(details.ContainsKey).Concat(details.Select(p => p.Key).Where(k => !first.Contains(k) &&
            k is not ("Node" or "Target" or "Document revision" or "Editable" or "Editing" or "Edit scope"))).ToArray();
        foreach (string removed in values.Keys.Except(keys).ToArray())
        {
            // A pending field belongs to its pinned draft until explicit resolution.
            if (HasDraft && values[removed].Binding != SceneInspectionBinding.None) continue;
            rows.Children.Remove(values[removed]); values.Remove(removed);
        }
        for (int index = 0; index < keys.Length; index++)
        {
            string key = keys[index];
            bool vector = details[key] is JsonObject xyz && new[] { "x", "y", "z" }.All(k => xyz[k] is JsonValue);
            if (vector && values.TryGetValue(key, out var oldField) && oldField.Inputs.Length != 3)
            { rows.Children.Remove(oldField); values.Remove(key); }
            if (!values.TryGetValue(key, out var field))
            {
                var binding = key switch { AuthoredPlacement => SceneInspectionBinding.AuthoredPosition, AuthoredRotation => SceneInspectionBinding.AuthoredRotation, AuthoredHeading => SceneInspectionBinding.AuthoredHeading, _ => SceneInspectionBinding.None };
                field = new(key, vector || binding is SceneInspectionBinding.AuthoredPosition or SceneInspectionBinding.AuthoredRotation, binding, Button("⧉", "Copy " + key, () => Try(() => Copy(key, true))));
                if (binding != SceneInspectionBinding.None)
                    foreach (var input in field.Inputs)
                    {
                        input.TextChanged += (_, _) => RefreshDraftPreview();
                        input.PreviewKeyDown += (_, e) =>
                        {
                            if (!HasDraft) return;
                            if (e.Key == Key.Escape) { CancelDraft(); e.Handled = true; }
                            else if (e.Key == Key.Enter) { Try(() => this.apply(this)); e.Handled = true; }
                        };
                    }
                values.Add(key, field); rows.Children.Insert(index, field);
            }
            else if (rows.Children.IndexOf(field) != index) { rows.Children.Remove(field); rows.Children.Insert(index, field); }
            field.Refresh(details[key], Display(details[key], key == AuthoredRotation));
        }
        RefreshModeControls();
        edit.IsEnabled = HasDraft || details["Editable"]?.GetValue<bool>() == true;
        edit.Visibility = edit.IsEnabled || details["Editing"]?.GetValue<string>()?.StartsWith("Unlock", StringComparison.Ordinal) == true ? Visibility.Visible : Visibility.Hidden;
        ToolTipService.SetShowOnDisabled(edit, true);
        if (!HasDraft) edit.ToolTip = Display(details["Editing"]);
        var anchor = viewport.InspectionAnchor(selected); bool offscreen = !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y) || anchor.X < 0 || anchor.X > ActualWidth || anchor.Y < 0 || anchor.Y > ActualHeight;
        if (offscreen) heading.Text += selected.Active ? " · offscreen" : " · inactive";
    }
}
