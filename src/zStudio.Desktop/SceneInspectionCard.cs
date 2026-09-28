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
    private readonly TextBlock heading = new() { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { Foreground = Brushes.LightSalmon, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock scope = new() { Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(0, 3, 0, 3) };
    private readonly StackPanel rows = new(), draftRows = new();
    private readonly WrapPanel actions = new();
    private readonly Dictionary<string, TextBlock> values = [];
    private readonly TextBox[] coordinates = [new(), new(), new()];
    private readonly Button edit, cancel;
    private JsonObject details = [];
    private Guid draftId;
    internal DocumentModel? DraftDocument { get; private set; }
    internal MissionPickupSource? DraftSource { get; private set; }
    internal long DraftRevision { get; private set; }
    internal string? DraftTarget { get; private set; }
    internal bool HasDraft => DraftSource != null;
    internal event Action? DraftClosed;
    internal string DraftToken => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{draftId}|{DraftDocument?.SessionId}|{DraftRevision}|{DraftTarget}|{string.Join('|', coordinates.Select(c => c.Text))}")));
    internal SceneInspection? Selection => viewport.SelectedInspection;
    internal object DescribeDraft() => new { token = DraftToken, target = DraftTarget, revision = DraftRevision, position = coordinates.Select(c => c.Text).ToArray(), pending = HasDraft };

    internal SceneInspectionCard(SceneViewport viewport, Func<SceneInspection, JsonObject> describe, Action<SceneInspectionCard> begin, Action<SceneInspectionCard> apply)
    {
        this.viewport = viewport; this.describe = describe; this.begin = begin; this.apply = apply;
        Background = null;
        var hoverBox = Box(hover); hoverBox.HorizontalAlignment = HorizontalAlignment.Right; hoverBox.VerticalAlignment = VerticalAlignment.Top;
        hoverBox.Margin = new(10); hoverBox.IsHitTestVisible = false; Children.Add(hoverBox);
        StackPanel body = new();
        DockPanel title = new(); var close = Button("×", "Close node card", () => { if (ResolvePending()) viewport.SelectInspection(null, false); });
        DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
        var copyDetails = Button("⧉", "Copy selected node details", () => Try(() => Copy(null, true)));
        DockPanel.SetDock(copyDetails, Dock.Right); title.Children.Add(copyDetails); title.Children.Add(heading); body.Children.Add(title);
        body.Children.Add(rows);
        for (int i = 0; i < 3; i++)
        {
            DockPanel row = new() { Margin = new(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = "XYZ"[i].ToString(), Foreground = Brushes.White, Width = 22, VerticalAlignment = VerticalAlignment.Center });
            coordinates[i].MinWidth = 120; coordinates[i].MaxLength = 64;
            System.Windows.Automation.AutomationProperties.SetName(coordinates[i], "Authored position " + "XYZ"[i]);
            coordinates[i].PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { CancelDraft(); e.Handled = true; }
                else if (e.Key == Key.Enter) { Try(() => this.apply(this)); e.Handled = true; }
            };
            row.Children.Add(coordinates[i]); draftRows.Children.Add(row);
        }
        draftRows.Visibility = Visibility.Collapsed; body.Children.Add(draftRows); body.Children.Add(error);
        edit = Button("Edit", "Edit authored XYZ position", () => Try(() => { if (HasDraft) this.apply(this); else this.begin(this); }));
        cancel = Button("Cancel", "Discard position draft", CancelDraft); cancel.Visibility = Visibility.Collapsed;
        actions.Children.Add(edit); actions.Children.Add(cancel); error.Visibility = Visibility.Collapsed;
        body.Children.Clear(); body.Children.Add(title); body.Children.Add(scope); body.Children.Add(actions); body.Children.Add(draftRows); body.Children.Add(error);
        body.Children.Add(rows);
        card = Box(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        card.Width = 350; card.HorizontalAlignment = HorizontalAlignment.Left; card.VerticalAlignment = VerticalAlignment.Top;
        card.Visibility = Visibility.Collapsed; Children.Add(card);
        viewport.InspectionChanged += Refresh; SizeChanged += (_, _) => Refresh();
    }
    private static Border Box(UIElement child) => new() { Child = child, Background = new SolidColorBrush(Color.FromArgb(242, 32, 37, 44)), CornerRadius = new(6), Padding = new(10) };
    private static Button Button(string text, string tooltip, Action action)
    {
        Button button = new() { Content = text, ToolTip = tooltip, MinWidth = 30, MinHeight = 30, Margin = new(2) };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action(); return button;
    }
    private void Try(Action action)
    { try { action(); error.Text = ""; error.Visibility = Visibility.Collapsed; } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or StudioCommandException or System.Runtime.InteropServices.ExternalException) { error.Text = ex.Message; error.Visibility = Visibility.Visible; } }
    internal void StartDraft(DocumentModel document, MissionPickupSource source, Vector3 position)
    {
        draftId = Guid.NewGuid();
        DraftDocument = document; DraftSource = source; DraftRevision = document.Revision; DraftTarget = Selection?.Target;
        for (int i = 0; i < 3; i++) coordinates[i].Text = position[i].ToString("R", CultureInfo.InvariantCulture);
        draftRows.Visibility = cancel.Visibility = Visibility.Visible; edit.Content = "✓"; edit.ToolTip = "Confirm position";
        System.Windows.Automation.AutomationProperties.SetName(edit, "Confirm position"); error.Text = ""; error.Visibility = Visibility.Collapsed; coordinates[0].Focus();
    }
    internal void SetDraft(string token, IReadOnlyList<string> xyz)
    {
        RequireDraft(token); if (xyz.Count != 3 || xyz.Any(v => v.Length > 64)) throw new StudioCommandException("invalid_argument", "Supply three bounded coordinate strings.");
        for (int i = 0; i < 3; i++) coordinates[i].Text = xyz[i];
    }
    internal void RequireDraft(string token)
    { if (!HasDraft || token != DraftToken) throw new StudioCommandException("draft_conflict", "The position draft changed. Read it again."); }
    internal Vector3 DraftPosition()
    {
        float[] p = new float[3];
        for (int i = 0; i < 3; i++)
            if (!float.TryParse(coordinates[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out p[i]) || !float.IsFinite(p[i]))
                throw new StudioCommandException("invalid_argument", "XYZ must contain finite game-unit numbers (use a decimal point).");
        return new(p[0], p[1], p[2]);
    }
    internal void CancelDraft()
    {
        DraftSource = null; DraftDocument = null; DraftTarget = null; draftId = Guid.Empty;
        DraftClosed?.Invoke();
        draftRows.Visibility = cancel.Visibility = Visibility.Collapsed; edit.Content = "Edit"; edit.ToolTip = "Edit authored XYZ position";
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
        heading.Text = Display(details["Node"]); heading.ToolTip = heading.Text; heading.MaxHeight = 40; heading.TextTrimming = TextTrimming.CharacterEllipsis;
        scope.Text = details["Edit scope"] is { } affected ? Display(affected) : "";
        scope.Visibility = scope.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        string[] first = details.ContainsKey("Runtime instance")
            ? ["Runtime instance", "Object world origin XYZ", "Source node", "Placed instance", "Template"]
            : ["Authored placement XYZ", "Object world origin XYZ", "Source node", "Placed instance", "Template"];
        var keys = first.Where(details.ContainsKey).Concat(details.Select(p => p.Key).Where(k => !first.Contains(k) &&
            k is not ("Node" or "Target" or "Document revision" or "Editable" or "Editing" or "Edit scope"))).ToArray();
        if (!values.Keys.SequenceEqual(keys))
        {
            rows.Children.Clear(); values.Clear();
            foreach (string key in keys)
            {
                DockPanel row = new() { Margin = new(0, 3, 0, 3) };
                var copy = Button("⧉", "Copy " + key, () => Try(() => Copy(key, true))); DockPanel.SetDock(copy, Dock.Right); row.Children.Add(copy);
                StackPanel text = new(); text.Children.Add(new TextBlock { Text = key, Foreground = Brushes.Silver, FontSize = 11 });
                TextBlock value = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxHeight = 54, TextTrimming = TextTrimming.CharacterEllipsis }; text.Children.Add(value); values.Add(key, value);
                row.Children.Add(text); rows.Children.Add(row);
            }
        }
        foreach (var (key, value) in values) { value.Text = Display(details[key]); value.ToolTip = value.Text; }
        edit.IsEnabled = HasDraft || details["Editable"]?.GetValue<bool>() == true;
        actions.Visibility = edit.IsEnabled || details["Editing"]?.GetValue<string>()?.StartsWith("Unlock", StringComparison.Ordinal) == true ? Visibility.Visible : Visibility.Collapsed;
        if (!HasDraft) edit.ToolTip = Display(details["Editing"]);
        var anchor = viewport.InspectionAnchor(selected); bool offscreen = !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y) || anchor.X < 0 || anchor.X > ActualWidth || anchor.Y < 0 || anchor.Y > ActualHeight;
        if (offscreen) heading.Text += selected.Active ? " · offscreen" : " · inactive";
        card.Width = Math.Max(120, Math.Min(350, ActualWidth - 20));
        card.Height = Math.Max(80, Math.Min(HasDraft ? 400 : 340, ActualHeight - 20));
        double x = offscreen ? ActualWidth - card.Width - 12 : anchor.X + 18;
        double y = offscreen ? 100 : anchor.Y + 12;
        card.Margin = new(Math.Clamp(x, 10, Math.Max(10, ActualWidth - card.Width - 10)), Math.Clamp(y, 10, Math.Max(10, ActualHeight - card.Height - 10)), 0, 0);
    }
    private static string Vector(Vector3 v) => string.Join(", ", new[] { v.X, v.Y, v.Z }.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
}
