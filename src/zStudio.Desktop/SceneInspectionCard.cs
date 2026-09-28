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

/// <summary>Presentation and an explicit XYZ draft. All accepted edits are delegated to the document service.</summary>
internal sealed class SceneInspectionCard : Grid
{
    private readonly SceneViewport viewport;
    private readonly Func<SceneInspection, JsonObject> describe;
    private readonly Action<SceneInspectionCard> begin, apply;
    private readonly Border card;
    private readonly TextBlock hover = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 390 };
    private readonly TextBlock heading = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock error = new() { Foreground = Brushes.LightSalmon, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock scope = new() { Opacity = .75, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(0, 3, 0, 3) };
    private readonly StackPanel rows = new();
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
    internal string DraftToken => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{draftId}|{DraftDocument?.SessionId}|{DraftRevision}|{DraftTarget}|{string.Join('|', HasDraft ? Coordinates.Select(c => c.Text) : [])}")));
    internal SceneInspection? Selection => viewport.SelectedInspection;
    internal object DescribeDraft() => new { token = DraftToken, target = DraftTarget, revision = DraftRevision, position = HasDraft ? Coordinates.Select(c => c.Text).ToArray() : new[] { "", "", "" }, pending = HasDraft };

    internal SceneInspectionCard(SceneViewport viewport, Func<SceneInspection, JsonObject> describe, Action<SceneInspectionCard> begin, Action<SceneInspectionCard> apply)
    {
        this.viewport = viewport; this.describe = describe; this.begin = begin; this.apply = apply;
        Background = null;
        var hoverBox = Box(hover); hoverBox.HorizontalAlignment = HorizontalAlignment.Right; hoverBox.VerticalAlignment = VerticalAlignment.Top;
        hoverBox.Margin = new(10); hoverBox.IsHitTestVisible = false; Children.Add(hoverBox);
        Grid layout = new(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        DockPanel title = new(); var close = Button("×", "Close node card", () => { if (ResolvePending()) viewport.SelectInspection(null, false); });
        DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
        var copyDetails = Button("⧉", "Copy selected node details", () => Try(() => Copy(null, true)));
        DockPanel.SetDock(copyDetails, Dock.Right); title.Children.Add(copyDetails);
        edit = Button("✎", "Edit authored XYZ position", () => Try(() => { if (HasDraft) this.apply(this); else this.begin(this); }));
        DockPanel.SetDock(edit, Dock.Right); title.Children.Add(edit);
        cancel = Button("↶", "Discard position draft", CancelDraft); cancel.Visibility = Visibility.Hidden;
        DockPanel.SetDock(cancel, Dock.Right); title.Children.Add(cancel); title.Children.Add(heading);
        layout.Children.Add(title);
        StackPanel body = new(); body.Children.Add(scope); body.Children.Add(error); body.Children.Add(rows);
        error.Visibility = Visibility.Collapsed;
        ScrollViewer scroll = new() { Name = "InspectionScroll", Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 6, 0, 0) };
        scroll.SetResourceReference(Control.TemplateProperty, "InspectionScrollTemplate");
        SetRow(scroll, 1); layout.Children.Add(scroll);
        card = Box(layout); card.Name = "InspectionCard";
        card.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        card.BorderBrush = new SolidColorBrush(Color.FromArgb(96, 128, 128, 128)); card.BorderThickness = new(1);
        // Native scrolling runs first; wheel input at the card's padding or
        // scroll limits still belongs to the card, never the surrounding scene.
        card.MouseWheel += (_, e) => e.Handled = true;
        card.Width = 350; card.HorizontalAlignment = HorizontalAlignment.Left; card.VerticalAlignment = VerticalAlignment.Top;
        card.Visibility = Visibility.Collapsed; Children.Add(card);
        viewport.InspectionChanged += Refresh; SizeChanged += (_, _) => Refresh();
    }
    private static Border Box(UIElement child) => new() { Child = child, Background = new SolidColorBrush(Color.FromArgb(242, 32, 37, 44)), CornerRadius = new(6), Padding = new(10) };
    private static Button Button(string text, string tooltip, Action action)
    {
        Button button = new() { Content = text, ToolTip = tooltip, Width = 30, Height = 30, Padding = new(0), Margin = new(2), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action(); return button;
    }
    private void Try(Action action)
    { try { action(); error.Text = ""; error.Visibility = Visibility.Collapsed; } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or StudioCommandException or System.Runtime.InteropServices.ExternalException) { error.Text = ex.Message; error.Visibility = Visibility.Visible; } }
    internal void StartDraft(DocumentModel document, MissionPickupSource source, Vector3 position)
    {
        Refresh();
        if (!values.TryGetValue(AuthoredPlacement, out var field) || field.Binding != SceneInspectionBinding.AuthoredPosition)
            throw new StudioCommandException("not_ready", "The selection has no authored placement fields.");
        draftId = Guid.NewGuid();
        DraftDocument = document; DraftSource = source; DraftRevision = document.Revision; DraftTarget = Selection?.Target;
        for (int i = 0; i < 3; i++) if (Coordinates[i].Text != position[i].ToString("R", CultureInfo.InvariantCulture)) Coordinates[i].Text = position[i].ToString("R", CultureInfo.InvariantCulture);
        field.SetEditing(true);
        cancel.Visibility = Visibility.Visible; edit.Content = "✓"; edit.ToolTip = "Confirm position";
        System.Windows.Automation.AutomationProperties.SetName(edit, "Confirm position"); error.Text = ""; error.Visibility = Visibility.Collapsed;
    }
    internal void SetDraft(string token, IReadOnlyList<string> xyz)
    {
        RequireDraft(token); if (xyz.Count != 3 || xyz.Any(v => v.Length > 64)) throw new StudioCommandException("invalid_argument", "Supply three bounded coordinate strings.");
        for (int i = 0; i < 3; i++) if (Coordinates[i].Text != xyz[i]) Coordinates[i].Text = xyz[i];
    }
    internal void RequireDraft(string token)
    { if (!HasDraft || token != DraftToken) throw new StudioCommandException("draft_conflict", "The position draft changed. Read it again."); }
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
        if (HasDraft && Coordinates.Any(input => input.IsKeyboardFocusWithin)) edit.Focus();
        DraftSource = null; DraftDocument = null; DraftTarget = null; draftId = Guid.Empty;
        DraftClosed?.Invoke();
        if (values.TryGetValue(AuthoredPlacement, out var field)) field.SetEditing(false);
        cancel.Visibility = Visibility.Hidden; edit.Content = "✎"; edit.ToolTip = "Edit authored XYZ position";
        System.Windows.Automation.AutomationProperties.SetName(edit, "Edit authored XYZ position"); error.Text = ""; error.Visibility = Visibility.Collapsed; Refresh();
    }
    internal bool ResolvePending()
    {
        if (!HasDraft) return true;
        var answer = MessageBox.Show(Window.GetWindow(this), "Apply the pending XYZ position?\nYes: apply · No: discard · Cancel: keep editing", "Node position draft", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) { CancelDraft(); return true; }
        Try(() => apply(this)); return !HasDraft;
    }
    internal string Copy(string? field, bool clipboard)
    {
        if (Selection == null) throw new StudioCommandException("not_ready", "Select a node first.");
        var current = describe(Selection);
        string text = field == null ? string.Join(Environment.NewLine, current.Select(p => p.Key + ": " + Display(p.Value))) :
            current.TryGetPropertyValue(field, out var value) ? field + ": " + Display(value) : throw new StudioCommandException("invalid_argument", "Unknown copy field.");
        if (clipboard) Clipboard.SetText(text); return text;
    }
    private static string Display(JsonNode? value)
    {
        if (value is JsonObject vector && new[] { "x", "y", "z" }.All(k => vector[k] is JsonValue))
            return string.Join("   ", new[] { "x", "y", "z" }.Select(k => k.ToUpperInvariant() + " " + JsonData.Scalar(vector[k], float.NaN).ToString("R", CultureInfo.InvariantCulture)));
        return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "Unavailable";
    }
    internal void Refresh()
    {
        var hovered = viewport.HoverInspection;
        var selected = Selection;
        var shown = hovered ?? selected;
        if (shown == null) hover.Text = "";
        else
        {
            var info = describe(shown);
            hover.Text = (hovered == null ? "Selected · " : "") + Display(info["Node"]) +
                (hovered?.Surface is { } surface ? "\nSurface XYZ · " + Vector(surface) : "") +
                (shown.Origin is { } origin ? "\nObject origin XYZ · " + Vector(origin) : "\nInactive / offscreen");
        }
        // The pinned card itself is the selection fallback. Avoid duplicating it behind the card.
        ((Border)hover.Parent).Visibility = hovered == null ? Visibility.Collapsed : Visibility.Visible;
        card.Visibility = selected == null && !HasDraft ? Visibility.Collapsed : Visibility.Visible;
        if (selected == null) return;
        details = describe(selected);
        heading.Text = Display(details["Node"]); heading.ToolTip = heading.Text;
        scope.Text = details["Edit scope"] is { } affected ? Display(affected) : "";
        scope.Visibility = scope.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        string[] first = details.ContainsKey("Runtime instance")
            ? ["Runtime instance", "Object world origin XYZ", "Source node", "Placed instance", "Template"]
            : ["Authored placement XYZ", "Object world origin XYZ", "Source node", "Placed instance", "Template"];
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
                var binding = key == AuthoredPlacement ? SceneInspectionBinding.AuthoredPosition : SceneInspectionBinding.None;
                field = new(key, vector || binding != SceneInspectionBinding.None, binding, Button("⧉", "Copy " + key, () => Try(() => Copy(key, true))));
                if (binding != SceneInspectionBinding.None)
                    foreach (var input in field.Inputs) input.PreviewKeyDown += (_, e) =>
                    {
                        if (!HasDraft) return;
                        if (e.Key == Key.Escape) { CancelDraft(); e.Handled = true; }
                        else if (e.Key == Key.Enter) { Try(() => this.apply(this)); e.Handled = true; }
                    };
                values.Add(key, field); rows.Children.Insert(index, field);
            }
            else if (rows.Children.IndexOf(field) != index) { rows.Children.Remove(field); rows.Children.Insert(index, field); }
            field.Refresh(details[key], Display(details[key]));
        }
        edit.IsEnabled = HasDraft || details["Editable"]?.GetValue<bool>() == true;
        edit.Visibility = edit.IsEnabled || details["Editing"]?.GetValue<string>()?.StartsWith("Unlock", StringComparison.Ordinal) == true ? Visibility.Visible : Visibility.Hidden;
        ToolTipService.SetShowOnDisabled(edit, true);
        if (!HasDraft) edit.ToolTip = Display(details["Editing"]);
        var anchor = viewport.InspectionAnchor(selected); bool offscreen = !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y) || anchor.X < 0 || anchor.X > ActualWidth || anchor.Y < 0 || anchor.Y > ActualHeight;
        if (offscreen) heading.Text += selected.Active ? " · offscreen" : " · inactive";
        card.Width = Math.Max(120, Math.Min(350, ActualWidth - 20));
        card.Height = Math.Max(80, Math.Min(340, ActualHeight - 20));
        double x = offscreen ? ActualWidth - card.Width - 12 : anchor.X + 18;
        double y = offscreen ? 100 : anchor.Y + 12;
        card.Margin = new(Math.Clamp(x, 10, Math.Max(10, ActualWidth - card.Width - 10)), Math.Clamp(y, 10, Math.Max(10, ActualHeight - card.Height - 10)), 0, 0);
    }
    private static string Vector(Vector3 v) => string.Join(", ", new[] { v.X, v.Y, v.Z }.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
}
