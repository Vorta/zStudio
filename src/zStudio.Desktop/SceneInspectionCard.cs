using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Core;
using zStudio.Rendering;
using zStudio.Automation;

namespace zStudio.Desktop;

/// <summary>Fixed hover/selection presentation and a shared transform draft delegated to the document service.</summary>
internal sealed partial class SceneInspectionCard : Grid
{
    private readonly SceneViewport viewport;
    private readonly Func<SceneInspection, JsonObject> describe;
    private readonly Action<SceneInspectionCard> begin;
    /// <summary>Accepts the draft; returns the edit's remaining work when it runs on (a source world's), or null when it is done.</summary>
    private readonly Func<SceneInspectionCard, Task?> apply;
    private readonly Border card;
    private readonly Border panel;
    private readonly TextBlock heading = new() { FontWeight = FontWeights.SemiBold, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>What the selected object is (its node type, model or placement kind), below its name.</summary>
    private readonly TextBlock subtitle = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    /// <summary>The details of objects without a source form (placements, AI nodes, compiled nodes), with the gizmo mode in its header.</summary>
    private readonly CardSection detailsSection = new("Details");
    /// <summary>The Move/Rotate gizmo mode as a segmented control, placed in a section header.</summary>
    private readonly Border modesFrame = new() { CornerRadius = new(5), Padding = new(1), BorderThickness = new(1), VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)) };
    private readonly TextBlock error = new() { Foreground = Brushes.LightSalmon, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock scope = new() { Opacity = .75, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(0, 3, 0, 3) };
    private readonly StackPanel rows = new();
    private const string AttackStrategy = "Attack strategy";
    private readonly Dictionary<string, SceneInspectionField> values = [];
    private const string AuthoredPlacement = "Authored placement XYZ";
    private ValueTextBox[] Coordinates => values[AuthoredPlacement].Inputs;
    private readonly Button edit, cancel;
    private readonly System.Windows.Controls.Primitives.ToggleButton isolate;
    /// <summary>Whether the selection can be isolated here (static model and Whole world previews), and whether it is.</summary>
    internal Func<bool>? CanIsolate { get; set; }
    internal Func<bool>? IsIsolated { get; set; }
    /// <summary>Shows only the selected object, or everything again when something is isolated.</summary>
    internal Action<SceneInspectionCard>? ToggleIsolation { get; set; }
    private JsonObject details = [];
    private Guid draftId;
    private long placementDraftLifetime;
    private long? placementReleasedForAcceptance;
    internal DocumentModel? DraftDocument { get; private set; }
    internal MissionPickupSource? DraftSource { get; private set; }
    internal long DraftRevision { get; private set; }
    internal string? DraftTarget { get; private set; }
    internal bool HasDraft => DraftSource != null || HasObjectDraft;
    /// <summary>The footer: whether there is anything to apply or revert, and its status line.</summary>
    private readonly Border footer = new() { Padding = new(14, 6, 10, 6), BorderThickness = new(0, 1, 0, 0) };
    private readonly TextBlock footerStatus = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Shapes.Ellipse footerDot = new() { Width = 6, Height = 6, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Whether the draft changes anything: Apply and Revert act only then.</summary>
    internal bool HasChanges => ObjectEditor != null ? HasObjectDraft : DraftSource != null && (!Coordinates.Select(c => c.Text).SequenceEqual(initialCoordinates) || !Angles.Select(c => c.Text).SequenceEqual(initialAngles)) && StoresChange();
    /// <summary>Whether the draft would store anything: a change below the stored precision (see PlacementPrecision) is none.</summary>
    private bool StoresChange()
    {
        try { return DraftDocument?.PickupEdits is not { } edits || edits.Stored(DraftSource!, DraftTransform()) != originalTransform; }
        catch (Exception ex) when (ex is StudioCommandException or InvalidOperationException or System.IO.InvalidDataException or KeyNotFoundException) { return true; }
    }
    private string[] initialCoordinates = [];
    private bool refreshingRows;
    internal event Action? DraftClosed;
    internal string DraftToken => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{draftId}|{DraftDocument?.SessionId}|{DraftRevision}|{DraftTarget}|{ObjectIdentityToken}|{transformMode}|{(ObjectEditor != null ? ObjectEditor.DraftToken : string.Join('|', DraftStrings))}")));
    internal SceneInspection? Selection => treeSelection ?? viewport.SelectedInspection;
    private readonly Button close;
    private readonly Border objectIcon;
    private readonly PreviewIcon objectGlyph = new() { Kind = "Cube", Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Shapes.Rectangle emptyTileOutline = new() { RadiusX = 6, RadiusY = 6, StrokeThickness = 1, StrokeDashArray = [3, 2], Visibility = Visibility.Collapsed };
    private readonly ScrollViewer scroll, emptyView;
    private readonly StackPanel emptyBody = new();
    private readonly ViewportChip selectionChip = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly ViewportChip surfaceChip = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    /// <summary>A source map's docked Properties pane: the panel is hosted outside the view (see <see cref="SetDocked"/>).</summary>
    internal bool Docked { get; private set; }
    /// <summary>The panel to host in the docked pane.</summary>
    internal Border PanelRoot => panel;
    /// <summary>The outliner type of a selection (world, group, geometry, object or node), for its icon.</summary>
    internal Func<SceneInspection, string>? CategoryOf { get; set; }
    /// <summary>A source map pickup's name for the heading, its subtitle (kind and game name) with tooltip, and the status bar's label; null otherwise.</summary>
    internal Func<SceneInspection, PickupNaming?>? PickupNameOf { get; set; }
    internal sealed record PickupNaming(string Name, string Subtitle, string ToolTip, string Label);
    private string? pickupLabel;
    /// <summary>The recently selected objects the empty pane offers, newest first, and how to select one again.</summary>
    internal Func<IReadOnlyList<RecentSelection>>? RecentSelections { get; set; }
    internal Action<RecentSelection>? PickRecent { get; set; }
    /// <summary>The world summary the empty pane shows (label, value).</summary>
    internal Func<IReadOnlyList<(string Label, string Value)>>? WorldSummary { get; set; }
    internal sealed record RecentSelection(string Name, string Parent, string Category, string[] Path);
    /// <summary>Sections a selection adds after Details (the player tank's Master type and Weapons), or null.</summary>
    internal Func<SceneInspection, FrameworkElement?>? ExtraSections { get; set; }
    /// <summary>The empty pane's Player tank row: the tank's name and how the view draws it, or why the map has none; null hides it.</summary>
    internal Func<PlayerTankRow?>? PlayerTankSummary { get; set; }
    /// <summary>Selects the player tank (the Player tank row).</summary>
    internal Action? PickPlayerTank { get; set; }
    internal sealed record PlayerTankRow(string Name, string? State, string? Absent);
    private readonly ContentControl extraSections = new() { Focusable = false, IsTabStop = false, Visibility = Visibility.Collapsed };
    /// <summary>Hosts <paramref name="sections"/> (taking them from another card that hosted them), or nothing.</summary>
    private void HostExtraSections(FrameworkElement? sections)
    {
        if (!ReferenceEquals(extraSections.Content, sections))
        {
            if (sections is PlayerTankPanel panel) panel.PrepareMove();
            if (sections?.Parent is ContentControl previous && !ReferenceEquals(previous, extraSections)) previous.Content = null;
            extraSections.Content = sections;
        }
        extraSections.Visibility = sections == null ? Visibility.Collapsed : Visibility.Visible;
    }
    /// <summary>Clears the selection (the ✕ and Escape), asking about a pending draft first.</summary>
    internal bool ClearSelection()
    {
        if (!ResolvePending() || !viewport.SelectInspection(null, false)) return false;
        ClearSourceNode(); Refresh(); return true;
    }
    private void ShowTypeTile(string? category)
    {
        bool none = category == null;
        emptyTileOutline.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        objectGlyph.Kind = none ? "Pointer" : OutlinerKinds.Icon(category!);
        if (none) { objectIcon.Background = Brushes.Transparent; objectIcon.BorderBrush = Brushes.Transparent; objectGlyph.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorTertiaryBrush"); return; }
        objectIcon.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        objectIcon.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        objectGlyph.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, OutlinerKinds.Brush(category!));
    }
    internal object DescribeDraft() => ObjectEditor != null ? new { token = DraftToken, target = DraftTarget, revision = DraftRevision, pending = HasObjectDraft, kind = "sourceObject", content = ObjectEditor.Describe(),
        transformMode = handlesShown ? transformMode : "none", handlesVisible = viewport.TransformHandlesVisible, boundsVisible = viewport.SelectionBoundsVisible } : (object)new { token = DraftToken, target = DraftTarget, revision = DraftRevision,
        position = HasDraft ? Coordinates.Select(c => c.Text).ToArray() : new[] { "", "", "" }, pending = HasDraft,
        rotationDegrees = HasDraft && rotationKind == PlacementRotationKind.EulerRadians ? Angles.Select(c => c.Text).ToArray() : null,
        headingDegrees = HasDraft && rotationKind == PlacementRotationKind.HeadingDegrees ? Angles[0].Text : null,
        transformMode = handlesShown ? transformMode : "none", rotationAxes = rotationKind switch { PlacementRotationKind.EulerRadians => "XYZ", PlacementRotationKind.HeadingDegrees => "Y", _ => "none" },
        handlesVisible = viewport.TransformHandlesVisible, boundsVisible = viewport.SelectionBoundsVisible };

    internal SceneInspectionCard(SceneViewport viewport, Func<SceneInspection, JsonObject> describe, Action<SceneInspectionCard> begin, Func<SceneInspectionCard, Task?> apply)
    {
        this.viewport = viewport; this.describe = describe; this.begin = begin; this.apply = apply;
        Background = null;
        InitializeTransformControls();
        Grid layout = new(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        // Header: the object's icon, name and type, then Isolate and Close. Revert and Apply are in the footer.
        DockPanel title = new() { Margin = new(0, 0, 0, 2) };
        close = IconButton("Close", "Close node card", () => ClearSelection());
        close.SetResourceReference(StyleProperty, "CardCloseButton");
        DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
        isolate = new() { Content = Icon("Isolate"), Width = 32, Height = 32, Visibility = Visibility.Collapsed };
        isolate.SetResourceReference(StyleProperty, "CardSegmentToggle");
        System.Windows.Automation.AutomationProperties.SetName(isolate, "Isolate");
        isolate.Click += (_, _) => { Try(() => ToggleIsolation?.Invoke(this)); Refresh(); };
        DockPanel.SetDock(isolate, Dock.Right); title.Children.Add(isolate);
        edit = FooterButton("Apply", "Apply", "Apply changes", "AccentButtonStyle", () => Try(Apply));
        cancel = FooterButton("Discard", "Revert", "Revert changes", "DefaultButtonStyle", () => Try(() => CancelDraft()));
        // A source object's Copy… and Delete stand left of Revert and Apply.
        copyObject = FooterButton("Copy", "Copy…", "Copy object", "DefaultButtonStyle", () => _ = ObjectAction("duplicate"));
        deleteObject = FooterButton("Delete", "Delete", "Delete object", "DefaultButtonStyle", () => _ = ObjectAction("delete"));
        copyObject.Visibility = deleteObject.Visibility = Visibility.Collapsed;
        objectIcon = new() { Width = 34, Height = 34, CornerRadius = new(6), BorderThickness = new(1), Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        // Nothing selected shows a dashed tile with a pointer instead of the object's type.
        Grid tile = new(); tile.Children.Add(emptyTileOutline); tile.Children.Add(objectGlyph); objectIcon.Child = tile;
        emptyTileOutline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextFillColorTertiaryBrush");
        DockPanel.SetDock(objectIcon, Dock.Left); title.Children.Add(objectIcon);
        ShowTypeTile("object");
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        StackPanel names = new() { VerticalAlignment = VerticalAlignment.Center }; names.Children.Add(heading); names.Children.Add(subtitle);
        title.Children.Add(names);
        layout.Children.Add(title);
        // Footer: the draft's status; Copy… and Delete (a source object's), then Revert and Apply (the accent action), enabled once something changes.
        DockPanel foot = new();
        foreach (var button in new[] { edit, cancel, deleteObject, copyObject }) DockPanel.SetDock(button, Dock.Right);
        edit.Margin = new(6, 0, 0, 0); cancel.Margin = new(6, 0, 0, 0); deleteObject.Margin = new(6, 0, 0, 0);
        foot.Children.Add(edit); foot.Children.Add(cancel); foot.Children.Add(deleteObject); foot.Children.Add(copyObject);
        footerStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        StackPanel status = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; status.Children.Add(footerDot); status.Children.Add(footerStatus);
        foot.Children.Add(status);
        footer.Child = foot; footer.Margin = new(-12, 4, -6, -10);
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        SetRow(footer, 2); layout.Children.Add(footer);
        // The edit scope (which records an edit changes, or why it is read only) belongs to the placement: Details.
        modesFrame.Child = modes; detailsSection.Trailing = modesFrame; detailsSection.Body.Children.Add(scope); detailsSection.Body.Children.Add(rows); detailsSection.Body.Margin = new(10, 0, 10, 8);
        StackPanel body = new(); body.Children.Add(error); body.Children.Add(terrainEdit); body.Children.Add(sourceRows); body.Children.Add(detailsSection); body.Children.Add(extraSections);
        emptyView = new ScrollViewer { Content = emptyBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        SetRow(emptyView, 1); layout.Children.Add(emptyView);
        terrainEdit.Click += (_, _) => { if (ResolvePending()) EditTerrain?.Invoke(); };
        error.Visibility = Visibility.Collapsed;
        scroll = new() { Name = "InspectionScroll", Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 6, 0, 0) };
        scroll.SetResourceReference(Control.TemplateProperty, "InspectionScrollTemplate");
        SetRow(scroll, 1); layout.Children.Add(scroll);
        card = Box(layout); card.Name = "InspectionCard"; card.Padding = new(12, 12, 6, 10);
        card.Visibility = Visibility.Collapsed;
        panel = CreatePanel(); Children.Add(panel);
        // Docked, the selection and the surface under the cursor are chips over the view.
        selectionChip.Margin = new(10); selectionChip.HorizontalAlignment = HorizontalAlignment.Left; selectionChip.VerticalAlignment = VerticalAlignment.Bottom;
        surfaceChip.Margin = new(10); surfaceChip.HorizontalAlignment = HorizontalAlignment.Right; surfaceChip.VerticalAlignment = VerticalAlignment.Bottom;
        System.Windows.Automation.AutomationProperties.SetName(surfaceChip, "Surface under cursor");
        System.Windows.Automation.AutomationProperties.SetName(selectionChip, "Selected object");
        Children.Add(selectionChip); Children.Add(surfaceChip);
        viewport.InspectionChanged += Refresh; SizeChanged += (_, _) => Refresh();
        Refresh();
    }
    private static Border Box(UIElement child) => new() { Child = child, Padding = new(10) };
    private static PreviewIcon Icon(string kind) => new() { Kind = kind, Width = 16, Height = 16 };
    /// <summary>A header action: an icon button named for automation by its tooltip.</summary>
    private static Button IconButton(string kind, string tooltip, Action action)
    {
        Button button = new() { Content = Icon(kind), ToolTip = tooltip };
        button.SetResourceReference(StyleProperty, "CardIconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action(); return button;
    }
    /// <summary>What the selection is, below its name: an AI node, a pickup or vehicle placement, or the node's type.</summary>
    private string Kind(SceneInspection selected)
    {
        if (selected.AiNode != null) return "AI network node";
        if (details["Pickup type"] is { } pickup) return "Pickup · " + Display(pickup);
        if (details["Template"] is { } template) return "Vehicle · " + Display(template);
        if (details["Runtime instance"] != null) return "Animation instance";
        var data = viewport.PreviewScene; int node = selected.SelectionNode >= 0 ? selected.SelectionNode : selected.Node;
        return data != null && node >= 0 && node < data.Nodes.Count ? data.Nodes[node].Class : "";
    }
    /// <summary>Puts the gizmo's Move/Rotate mode in <paramref name="target"/>'s header.</summary>
    private void PlaceModes(CardSection target)
    {
        if (ReferenceEquals(target.Trailing, modesFrame)) return;
        if (modesFrame.Parent is Border holder) holder.Child = null;
        target.Trailing = modesFrame;
    }
    /// <summary>A footer action: an icon whose tooltip names it at once (also while disabled), named for automation.</summary>
    private static Button FooterButton(string kind, string text, string name, string style, Action action)
    {
        Button button = new() { Content = new PreviewIcon { Kind = kind, Width = 16, Height = 16 }, Width = 36, Height = 30, Padding = new(0), IsEnabled = false, ToolTip = text };
        button.SetResourceReference(StyleProperty, style);
        ToolTipService.SetInitialShowDelay(button, 0); ToolTipService.SetBetweenShowDelay(button, 0); ToolTipService.SetShowOnDisabled(button, true);
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action(); return button;
    }
    private readonly Button copyObject, deleteObject;
    /// <summary>Shows the footer for editable selections, enabling Revert and Apply once the draft changes something.</summary>
    private void RefreshFooter(bool editable)
    {
        bool changed = HasChanges;
        footer.Visibility = editable || HasDraft ? Visibility.Visible : Visibility.Collapsed;
        edit.IsEnabled = changed && !ObjectApplying; cancel.IsEnabled = changed && !ObjectApplying;
        footerStatus.Text = ObjectApplying ? "Applying…" : changed ? "Unsaved changes" : "No changes";
        if (changed || ObjectApplying) footerDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentFillColorDefaultBrush");
        else footerDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextFillColorTertiaryBrush");
    }
    private static Button Button(string text, string tooltip, Action action)
    {
        Button button = new() { Content = text, ToolTip = tooltip, Width = 30, Height = 30, Padding = new(0), Margin = new(2), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action(); return button;
    }
    private void Try(Action action)
    { error.Text = ""; error.Visibility = Visibility.Collapsed; try { action(); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or System.IO.InvalidDataException or StudioCommandException or System.Runtime.InteropServices.ExternalException) { error.Text = ex.Message; error.Visibility = Visibility.Visible; } }
    internal void StartDraft(DocumentModel document, MissionPickupSource source, Vector3 position)
    {
        Refresh();
        if (!values.TryGetValue(AuthoredPlacement, out var field) || field.Binding != SceneInspectionBinding.AuthoredPosition)
            throw new StudioCommandException("not_ready", "The selection has no authored placement fields.");
        ++placementDraftLifetime; placementReleasedForAcceptance = null; draftId = Guid.NewGuid();
        DraftDocument = document; DraftSource = source; DraftRevision = document.Revision; DraftTarget = Selection?.Target;
        rotationKind = document.PickupEdits?.RotationKind(source) ?? PlacementRotationKind.None;
        originalTransform = lastValidTransform = new(position, document.PickupEdits?.Rotation(source) ?? Vector3.Zero);
        if (transformMode == "rotate" && rotationKind == PlacementRotationKind.None) transformMode = "move";
        changingDraft = true;
        for (int i = 0; i < 3; i++) if (Coordinates[i].Text != position[i].ToString("R", CultureInfo.InvariantCulture)) Coordinates[i].Text = position[i].ToString("R", CultureInfo.InvariantCulture);
        for (int i = 0; i < Angles.Length; i++) Angles[i].Text = AngleText(originalTransform.Rotation[rotationKind == PlacementRotationKind.HeadingDegrees ? 1 : i], rotationKind);
        initialAngles = Angles.Select(c => c.Text).ToArray(); initialCoordinates = Coordinates.Select(c => c.Text).ToArray();
        foreach (var editable in EditableFields) { editable.SetEditing(true); editable.Pinned = true; }
        changingDraft = false;
        error.Text = ""; error.Visibility = Visibility.Collapsed;
        RefreshDraftPreview();
    }
    // Restore only the exact placement intent released for acceptance. A later cancel, draft, selection or
    // document lifetime must never be overwritten by a failed asynchronous publication.
    internal Func<bool>? CapturePlacementDraftRestore()
    {
        if (DraftSource is not { } source || DraftDocument is not { } document || ObjectEditor != null) return null;
        long lifetime = placementDraftLifetime; Guid identity = draftId; string? target = DraftTarget;
        string[] position = Coordinates.Select(c => c.Text).ToArray(), angles = Angles.Select(c => c.Text).ToArray();
        var kind = rotationKind; string mode = transformMode; var original = originalTransform;
        return () =>
        {
            if (placementDraftLifetime != lifetime + 1 || placementReleasedForAcceptance != placementDraftLifetime
                || HasDraft || document.IsDisposed || Selection?.Target != target) return false;
            StartDraft(document, source, original.Position); draftId = identity;
            SetDraft(DraftToken, position, kind == PlacementRotationKind.EulerRadians ? angles : null,
                kind == PlacementRotationKind.HeadingDegrees ? angles[0] : null, mode);
            return true;
        };
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
    internal void CancelDraft(bool accepting = false)
    {
        if (ObjectApplying && !accepting) throw new StudioCommandException("busy", "Wait for the object edit to finish before canceling.");
        ++placementDraftLifetime; placementReleasedForAcceptance = accepting && DraftSource != null ? placementDraftLifetime : null;
        ObjectReleasedForAcceptance = accepting && ObjectEditor != null;
        if (ObjectEditor != null)
        {
            EndObjectEditing(); error.Visibility = Visibility.Collapsed; RefreshSource(); return;
        }
        // Ending a draft from Enter/Escape (or MCP while a field has focus) must
        // not let TextBox's deferred caret reveal scroll the restored readout.
        if (HasDraft && EditableFields.SelectMany(f => f.Inputs).Any(input => input.IsKeyboardFocusWithin)) edit.Focus();
        DraftSource = null; DraftDocument = null; DraftTarget = null; draftId = Guid.Empty;
        DraftClosed?.Invoke();
        foreach (var field in EditableFields) field.Pinned = false;
        // A placement's handles belong to its draft: they hide with it.
        viewport.EndTransformDraft(); handlesShown = false;
        error.Text = ""; error.Visibility = Visibility.Collapsed; Refresh();
    }
    /// <summary>Asks about a pending draft; false while a Yes still applies it (see <see cref="ResolvePendingAsync"/>).</summary>
    internal bool ResolvePending() { var resolving = ResolvePendingAsync(); return resolving.IsCompleted && resolving.GetAwaiter().GetResult(); }
    /// <summary>
    /// Asks about a pending draft: Yes applies it, No discards it, Cancel keeps editing. In a source world the edit changes the
    /// sources and rebuilds the world, keeping the draft until its preparation succeeds, so a Yes completes with the outcome
    /// of that apply rather than as a refusal.
    /// </summary>
    internal async Task<bool> ResolvePendingAsync()
    {
        if (ObjectApplying) return false;
        if (!HasDraft) return true;
        // A draft that changes nothing (handles shown on a placement) ends quietly.
        if (!HasChanges) { CancelDraft(); return true; }
        // A draft already being applied (✓, Enter or an earlier Yes) is not asked about again: the answer is that apply's outcome.
        if (applying is { IsCompleted: false } running) { await running; return !HasDraft; }
        var answer = MessageBox.Show(Window.GetWindow(this), (ObjectEditor != null ? "Apply the pending object changes?" : "Apply the pending position and rotation?") + "\nYes: apply · No: discard · Cancel: keep editing", ObjectEditor != null ? "Object edit draft" : "Node transform draft", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) { Try(() => CancelDraft()); return !HasDraft; }
        Try(Apply);
        if (applying is { IsCompleted: false } started) await started;
        return !HasDraft;
    }
    /// <summary>The GUI's apply of the draft whose work still runs (a source world's edit and rebuild).</summary>
    private Task? applying;
    private void Apply()
    {
        // A placement draft that would store nothing (a change below the stored precision) ends, as Revert would.
        if (!HasChanges) { if (DraftSource != null && ObjectEditor == null) CancelDraft(); return; }
        if (apply(this) is { IsCompleted: false } running) applying = running;
    }
    internal string Copy(string? field, bool clipboard)
    {
        if (Selection == null) throw new StudioCommandException("not_ready", "Select a node first.");
        if (ObjectEditor != null) { string objectText = ObjectEditor.Copy(field); if (clipboard) Clipboard.SetText(objectText); return objectText; }
        var current = describe(Selection);
        string text = field == null ? string.Join(Environment.NewLine, current.Select(p => p.Key + ": " + Display(p.Value, p.Key == AuthoredRotation))) :
            current.TryGetPropertyValue(field, out var value) ? field + ": " + Display(value, field == AuthoredRotation) : throw new StudioCommandException("invalid_argument", "Unknown copy field.");
        if (clipboard) Clipboard.SetText(text); return text;
    }
    private static string Display(JsonNode? value, bool degrees = false)
    {
        if (value is JsonObject vector && new[] { "x", "y", "z" }.All(k => vector[k] is JsonValue))
            return string.Join("   ", new[] { "x", "y", "z" }.Select(k => k.ToUpperInvariant() + " " + (degrees ? vector[k]!.GetValue<double>().ToString("R", CultureInfo.InvariantCulture) : JsonData.Scalar(vector[k], float.NaN).ToString("R", CultureInfo.InvariantCulture))));
        // Polygon zone counts read as their rows (cards without the source-object Zone section list them as a detail).
        if (ZoneReadout.PolygonZoneLines(value) is { } zoneLines) return zoneLines;
        // A list of lines (parents) reads one per line rather than as JSON.
        if (value is JsonArray lines && lines.Count > 0 && lines.All(line => line is JsonValue v && v.TryGetValue<string>(out _)))
            return string.Join('\n', lines.Select(line => line!.GetValue<string>()));
        return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "Unavailable";
    }
    /// <summary>After every refresh: the selection's label may have changed.</summary>
    internal event Action? Refreshed;
    /// <summary>The selection as the status bar names it: "name · node #N", or null without one.</summary>
    internal string? SelectionLabel => Selection is not { } selected ? null : pickupLabel != null ? pickupLabel
        : ObjectEditor is { } editor ? $"{editor.State.Name} · node #{editor.State.Node}" : heading.Text.Length > 0 ? heading.Text + (selected.SelectionNode >= 0 ? $" · node #{selected.SelectionNode}" : "") : null;
    internal void Refresh() { RefreshCore(); Refreshed?.Invoke(); }
    private void RefreshCore()
    {
        ReconcileSourceSelection();
        var selected = Selection;
        RefreshHover();
        bool empty = selected == null && !HasDraft;
        card.Visibility = empty && !Docked ? Visibility.Collapsed : Visibility.Visible;
        ArrangePanel();
        scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        emptyView.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        close.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        close.ToolTip = Docked ? "Clear selection (Esc)" : "Close node card";
        System.Windows.Automation.AutomationProperties.SetName(close, Docked ? "Clear selection" : "Close node card");
        RefreshChips(selected);
        pickupLabel = null;
        if (selected == null)
        {
            HostExtraSections(null);
            if (!HasDraft) { EndObjectHandles(); ObjectEditor = null; objectBinding = null; sourceRows.Children.Clear(); sourceRows.Visibility = Visibility.Collapsed; }
            if (empty)
            {
                heading.Text = "Properties"; heading.ToolTip = null; subtitle.Text = "Nothing selected"; subtitle.ToolTip = null;
                isolate.Visibility = Visibility.Collapsed; footer.Visibility = Visibility.Collapsed; ShowTypeTile(null);
                if (Docked) RefreshEmptyView();
            }
            return;
        }
        ShowTypeTile(CategoryOf?.Invoke(selected) ?? "object");
        // An AI marker is not a scene object to isolate. Like scene_selection, isolation waits for an open edit. In a source
        // map the World tree's eyes isolate (middle-click), so the docked card has no Isolate of its own.
        isolate.Visibility = CanIsolate?.Invoke() == true && selected.AiNode == null && !(IsSourceWorld && Docked) ? Visibility.Visible : Visibility.Collapsed;
        isolate.IsChecked = IsIsolated?.Invoke() == true; isolate.IsEnabled = !HasDraft;
        ToolTipService.SetShowOnDisabled(isolate, true);
        isolate.ToolTip = HasDraft ? "Apply or discard the edit first" : isolate.IsChecked == true ? "Show all objects" : "Isolate: show only this object";
        details = describe(selected);
        if (RefreshSource()) { HostExtraSections(null); return; }
        HostExtraSections(ExtraSections?.Invoke(selected));
        var pickup = PickupNameOf?.Invoke(selected);
        heading.Text = pickup?.Name ?? Display(details["Selected object"] ?? details["Node"]); heading.ToolTip = heading.Text; selectionName.Text = heading.Text;
        subtitle.Text = pickup?.Subtitle ?? Kind(selected); subtitle.ToolTip = pickup?.ToolTip ?? subtitle.Text;
        pickupLabel = pickup?.Label;
        detailsSection.Visibility = Visibility.Visible; PlaceModes(detailsSection);
        scope.Text = details["Edit scope"] is { } affected ? Display(affected) : "";
        scope.Visibility = scope.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        string[] first = selected.AiNode != null
            ? ["Network", "Network type", "Authored placement XYZ", "Object world origin XYZ", AttackStrategy, "Status"]
            : details.ContainsKey("Runtime instance")
            ? ["Parent", "Parents", "Runtime instance", "Object world origin XYZ", "Zone", "Compiled source node", "Placed instance", "Template"]
            : ["Parent", "Parents", "Authored placement XYZ", AuthoredRotation, AuthoredHeading, "Object world origin XYZ", "Zone", "Zone source", "Compiled source node", "Placed instance", "Template", "Placement editing"];
        if (IsSourceWorld && ObjectEditor == null)
            foreach (string key in new[] { "Compiled source node", "Surface compiled source node", "Compiled parent", "Compiled parents", "Selection owner", "glTF node index", "Source scene", "Source file", "Scene local XYZ", "Model", "Material", "Object world origin XYZ" }) details.Remove(key);
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
                        input.TextChanged += (_, _) =>
                        {
                            // Typing into an editable placement starts its draft; Apply and Revert enable once it differs.
                            if (!HasDraft && !refreshingRows && !changingDraft && details["Editable"]?.GetValue<bool>() == true) BeginFromInput();
                            RefreshDraftPreview();
                        };
                        input.PreviewKeyDown += (_, e) =>
                        {
                            if (!HasDraft) return;
                            if (e.Key == Key.Escape) { CancelDraft(); e.Handled = true; }
                            else if (e.Key == Key.Enter) { Try(Apply); e.Handled = true; }
                        };
                    }
                values.Add(key, field); rows.Children.Insert(index, field);
            }
            else if (rows.Children.IndexOf(field) != index) { rows.Children.Remove(field); rows.Children.Insert(index, field); }
            refreshingRows = true;
            try { field.Refresh(details[key], Display(details[key], key == AuthoredRotation)); }
            finally { refreshingRows = false; }
        }
        bool editable = details["Editable"]?.GetValue<bool>() == true;
        foreach (var field in EditableFields) field.SetEditing(HasDraft || editable);
        RefreshModeControls();
        RefreshFooter(editable);
        // A locked compiled world says why its fields cannot change.
        footerStatus.ToolTip = Display(details["Editing"]);
        var anchor = viewport.InspectionAnchor(selected); bool offscreen = !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y) || anchor.X < 0 || anchor.X > ActualWidth || anchor.Y < 0 || anchor.Y > ActualHeight;
        if (offscreen) heading.Text += selected.Active ? " · offscreen" : " · inactive";
    }
}
