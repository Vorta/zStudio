using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using zStudio.Automation;

namespace zStudio.Desktop;

internal sealed partial class SceneInspectionCard
{
    internal sealed record ObjectBinding(DocumentModel Document, SourceObjectState State)
    {
        // The DocumentModel survives incremental publication; its source snapshot does not.
        internal zStudio.Core.AssetDocument Snapshot { get; } = Document.Document;
    }
    internal Func<ObjectBinding?>? SourceObject { get; set; }
    internal Action<SceneInspectionCard>? SourceDraftStarted { get; set; }
    internal Func<string, string?, Task>? SourceAction { get; set; }
    /// <summary>Whether the world has started evaluating an accepted edit (after its plan was accepted): Copy object closes then.</summary>
    internal Func<bool>? WorldRebuilding { get; set; }
    /// <summary>Why a name cannot name the selected object's copy (the copy's own checks), or null; Copy object checks it as it is typed.</summary>
    internal Func<string, string?>? CheckCopyName { get; set; }
    internal Action? EditTerrain { get; set; }
    /// <summary>Chooses a model file for the Model field (its project path), or null.</summary>
    internal Func<string?>? BrowseModel { get; set; }
    internal bool TerrainAvailable { get; set; }
    private readonly Button terrainEdit = new() { Content = "Edit terrain…", HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    internal SourceObjectCardEditor? ObjectEditor { get; private set; }
    private ObjectBinding? objectBinding;
    private string? objectTarget;
    private Guid objectLifetime;
    internal bool ObjectApplying { get; set; }
    /// <summary>A draft of this object is open: its first change started it; Apply or Revert ends it.</summary>
    internal bool ObjectEditing => ObjectEditor != null && DraftDocument != null;
    private zStudio.Core.Sources.ObjectTransform lastObjectTransform, dragStartTransform;
    private long dragGesture;
    private (string? Position, string? Rotation) dragStartTexts;
    internal bool ObjectReleasedForAcceptance { get; private set; }
    private zStudio.Rendering.SceneInspection? treeSelection;
    private string? rendererTreeTarget;
    internal void SelectSourceNode(zStudio.Rendering.SceneInspection selection)
    {
        treeSelection = selection; rendererTreeTarget = viewport.SelectedInspection?.Target; Refresh();
    }
    internal void ClearSourceNode() { treeSelection = null; rendererTreeTarget = null; }
    private void ReconcileSourceSelection()
    {
        if (treeSelection != null && viewport.SelectedInspection?.Target != rendererTreeTarget) ClearSourceNode();
    }
    private string ObjectIdentityToken => $"{objectLifetime}|{objectBinding?.Document.SessionId}|{objectBinding?.State.Node}|{objectTarget}";
    private readonly StackPanel sourceRows = new();
    private readonly StackPanel surfaceRows = new();
    private bool releasingSource;
    internal bool HasObjectDraft => ObjectEditor?.HasPendingDrafts == true;
    internal bool IsSourceWorld { get; set; }
    /// <summary>A zone's label in the shown source map, for the Polygon zones rows; null without one.</summary>
    internal Func<byte, string?>? ZoneLabel { get; set; }
    internal int? ObjectNode => objectBinding?.State.Node;
    internal DocumentModel? ObjectDocument => objectBinding?.Document;

    private bool RefreshSource()
    {
        if (SourceObject == null) return false;
        // Rebase accepted fields only after publication/Apply completes. Drafts and the old editor
        // must survive preparation failures so their exact input and lifetime can be restored.
        if (!ObjectApplying && !HasObjectDraft && (ObjectEditor == null || objectTarget != Selection?.Target || objectBinding?.State.Node != Selection?.SelectionNode
            || objectBinding?.Document.IsDisposed == true
            || objectBinding != null && !ReferenceEquals(objectBinding.Snapshot, objectBinding.Document.Document)))
        {
            if (DraftDocument != null && DraftSource == null) { DraftDocument = null; DraftTarget = null; draftId = Guid.Empty; DraftClosed?.Invoke(); }
            EndObjectHandles();
            ObjectEditor = null; objectBinding = null; sourceRows.Children.Clear();
            // An error belongs to the object it was about.
            if (objectTarget != Selection?.Target) { error.Text = ""; error.Visibility = Visibility.Collapsed; objectTarget = Selection?.Target; }
            var binding = SourceObject();
            if (binding != null)
            {
                objectBinding = binding; objectTarget = Selection?.Target; objectLifetime = Guid.NewGuid(); ObjectReleasedForAcceptance = false;
                ObjectEditor = new(binding.State) { BrowseModel = BrowseModel };
                ObjectEditor.SetEditing(true); // Fields are editable at once; the first change starts the draft.
                if (binding.State.Transform is { } accepted) lastObjectTransform = accepted;
                ObjectEditor.Changed += ObjectChanged;
                rebound = true;
                sourceRows.Children.Add(ObjectEditor);
                CardSection surface = new("Surface", expanded: false);
                if (surfaceRows.Parent is Panel previous) previous.Children.Remove(surfaceRows); // The rows outlive the editor they were shown with.
                surface.Body.Children.Add(surfaceRows);
                sourceRows.Children.Add(surface);
            }
        }
        terrainEdit.Visibility = TerrainAvailable ? Visibility.Visible : Visibility.Collapsed;
        sourceRows.Visibility = ObjectEditor == null ? Visibility.Collapsed : Visibility.Visible;
        copyObject.Visibility = deleteObject.Visibility = objectBinding?.State.Class is "Object3D" or "Lod" ? Visibility.Visible : Visibility.Collapsed;
        if (ObjectEditor == null) { detailsSection.Visibility = Visibility.Visible; return false; }
        detailsSection.Visibility = Visibility.Collapsed;
        if (ObjectEditor.TransformSection is { } transformSection) PlaceModes(transformSection);
        ObjectEditor.IsEnabled = !objectBinding!.Document.SourceWorld!.IsRebuilding && !ObjectApplying;
        copyObject.IsEnabled = deleteObject.IsEnabled = ObjectEditor.IsEnabled && objectBinding!.State.Object == null;
        copyObject.ToolTip = deleteObject.ToolTip = objectBinding!.State.Object != null ? "This is a shared model part. Structural changes are made in its model source; select the whole object to copy or delete its placement." : null;
        heading.Text = objectBinding.State.Name; heading.ToolTip = objectBinding.State.Name; selectionName.Text = heading.Text;
        // The node's type and its built node, as the header's second line; the tooltip names the model (or the file holding it).
        var state = objectBinding.State;
        bool empty = Selection is { } shown && CategoryOf?.Invoke(shown) == "empty";
        subtitle.Text = $"{(empty ? "Empty " : "")}{state.Class} · node #{state.Node}";
        subtitle.ToolTip = subtitle.Text + (empty ? "\n" + OutlinerKinds.EmptyNote : "") + (state.Model != null ? "\n" + state.Model : state.Origin.ModelFile is { } file ? "\nIn " + file : "");
        scope.Text = ""; scope.Visibility = Visibility.Collapsed;
        RefreshModeControls();
        RefreshFooter(ObjectEditor.CanEdit || copyObject.Visibility == Visibility.Visible);
        surfaceRows.Children.Clear();
        // Polygon zones belong with the object zone, in the Zone section: every polygon of the object, its own and below it
        // (the whole world is not counted).
        bool counted = Selection is { } chosen && CategoryOf?.Invoke(chosen) is "geometry" or "object" or "group";
        ObjectEditor.ShowPolygonZones(counted ? details["Object polygon zones"] as JsonObject : null, ZoneLabel, objectBinding.State.Name);
        foreach (string key in new[] { "Surface world XYZ", "Surface normal", "Surface zone", "Surface altitude zone check", "Soil", "Stored texture" })
            if (details[key] is { } value) surfaceRows.Children.Add(new TextBlock { Text = key + ": " + Display(value), TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 3) });
        // A newly shown object keeps the handles the user has on.
        if (rebound) { rebound = false; UpdateObjectHandles(); }
        return true;
    }
    private bool rebound;
    private void ObjectChanged()
    {
        if (releasingSource) return;
        // The first change opens the object's draft; returning every field to its accepted value closes it.
        if (HasObjectDraft && DraftDocument == null)
        {
            try { OpenObjectDraft(); }
            catch (Exception ex) when (ex is StudioCommandException or InvalidOperationException)
            {
                releasingSource = true; try { ObjectEditor!.Discard(); } finally { releasingSource = false; }
                ShowSourceError(ex.Message); return;
            }
        }
        // A handle gesture keeps its draft while it passes the accepted values; its end closes one that changed nothing.
        else if (!viewport.IsPickupDragging) CloseUnchangedObjectDraft();
        RefreshSource(); UpdateObjectHandles();
    }
    private bool CloseUnchangedObjectDraft()
    {
        if (HasObjectDraft || DraftDocument == null || ObjectEditor == null) return false;
        DraftDocument = null; DraftTarget = null; draftId = Guid.Empty; DraftClosed?.Invoke();
        return true;
    }
    private void ObjectGestureEnded() { if (!ObjectApplying && CloseUnchangedObjectDraft()) RefreshSource(); }
    /// <summary>
    /// A handle gesture on an object opens its draft before it starts, as typing would (none can open during a drag).
    /// False, with the reason shown, when an edit cannot start; a gesture that changes nothing closes the draft again.
    /// </summary>
    internal bool OpenObjectDraftForGesture()
    {
        if (DraftDocument != null) return true;
        try { BeginObjectEdit(); OpenObjectDraft(); return true; }
        catch (Exception ex) when (ex is StudioCommandException or InvalidOperationException) { ShowSourceError(ex.Message); return false; }
    }
    /// <summary>Opens the object's draft: its document and revision, checked like any edit (an idle world, no other drafts).</summary>
    private void OpenObjectDraft()
    {
        if (ObjectEditor == null || objectBinding == null) throw new StudioCommandException("not_ready", "Select an editable source object.");
        if (!ObjectEditor.IsEnabled) throw new StudioCommandException("busy", "Wait for the world to finish its current edit.");
        SourceDraftStarted?.Invoke(this);
        DraftDocument = objectBinding.Document; DraftRevision = DraftDocument.Revision; DraftTarget = objectTarget; draftId = Guid.NewGuid();
        ObjectReleasedForAcceptance = false; error.Visibility = Visibility.Collapsed;
    }
    /// <summary>MCP begin: an object's fields are already editable, so it only checks that an edit could start.</summary>
    internal void BeginObjectEdit()
    {
        if (ObjectEditor == null || objectBinding == null) throw new StudioCommandException("not_ready", "Select an editable source object.");
        if (!ObjectEditor.CanEdit) throw new StudioCommandException("read_only", "This object has nothing to edit here.");
        if (!ObjectEditor.IsEnabled) throw new StudioCommandException("busy", "Wait for the world to finish its current edit.");
    }
    /// <summary>A shared animated node becomes its static-world target when editing stops playback; keep its exact pending input.</summary>
    internal void RebindSourceEditSelection()
    {
        var selected = Selection;
        var current = SourceObject?.Invoke();
        if (selected == null || current == null || objectBinding == null || current.Document != objectBinding.Document || current.State.Node != objectBinding.State.Node)
            throw new StudioCommandException("context_changed", "The source object changed while returning to the static world. Select it again before editing.");
        objectTarget = selected.Target;
    }
    /// <summary>Revert, or the accepted draft's release: the fields return to the accepted values; shown handles stay.</summary>
    private void EndObjectEditing()
    {
        releasingSource = true;
        try { ObjectEditor?.Discard(); }
        finally { releasingSource = false; }
        bool open = DraftDocument != null;
        DraftDocument = null; DraftTarget = null; draftId = Guid.Empty;
        if (open) DraftClosed?.Invoke();
        if (ObjectApplying) { if (viewport.HasTransformDraft) viewport.EndTransformDraft(); }
        else UpdateObjectHandles();
    }
    /// <summary>The transform header's toggles for a source object: "move" or "rotate" shows those handles, "none" hides them.</summary>
    internal void SetObjectMode(string mode)
    {
        if (ObjectEditor == null) throw new StudioCommandException("not_ready", "Select an editable source object.");
        if (ObjectApplying) throw new StudioCommandException("busy", "Wait for the object edit to finish.");
        if (viewport.IsPickupDragging) throw new StudioCommandException("busy", "Finish or cancel the active transform drag.");
        if (mode is not ("move" or "rotate" or "none")) throw new StudioCommandException("invalid_argument", "Invalid transform mode.");
        if (ObjectEditor.State.Transform == null) throw new StudioCommandException("read_only", "This object has no transform.");
        // Enabling handles explicitly enters editing. Passive selection and the MCP begin check remain observational.
        if (mode != "none") SourceDraftStarted?.Invoke(this);
        if (mode == "none") handlesShown = false; else { handlesShown = true; transformMode = mode; }
        viewport.TransformHandlesEnabled = handlesShown;
        UpdateObjectHandles(); RefreshModeControls();
    }
    /// <summary>
    /// Shows the object's draft transform: its instances (a change previews in place) and, when toggled on, its handles. With
    /// no change and no handles nothing is shown. Invalid input keeps the last valid transform.
    /// </summary>
    private void UpdateObjectHandles()
    {
        if (ObjectApplying || ObjectEditor?.State is not { Transform: { } accepted } state || !ObjectEditor.CanEdit) return;
        if (!handlesShown && !HasObjectDraft) { EndObjectHandles(); return; }
        var draft = ObjectEditor.DraftTransform();
        if (draft is { } valid) lastObjectTransform = valid;
        try { viewport.PreviewObjectDraft(state.Node, accepted.Matrix(), lastObjectTransform.Matrix(), transformMode, draft != null); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { ShowSourceError(ex.Message); }
    }
    /// <summary>Ends a shown object's handles and preview (never a placement's draft).</summary>
    private void EndObjectHandles() { if (DraftSource == null && viewport.HasTransformDraft && !viewport.IsPickupDragging) viewport.EndTransformDraft(); }
    /// <summary>Shows the edit mode's handles again (after an apply that kept the draft).</summary>
    internal void RefreshObjectHandles() => UpdateObjectHandles();
    /// <summary>
    /// A handle drag writes the object's local position (moving) or rotation (turning) into its fields, from the values the
    /// gesture started with; the authored scale and the other field stay as typed. A canceled gesture restores its exact text.
    /// </summary>
    private void ReceiveObjectDrag(zStudio.Rendering.SceneViewport.ObjectDrag drag)
    {
        if (ObjectEditor == null || ObjectApplying) return;
        if (drag.Gesture != dragGesture) { dragGesture = drag.Gesture; dragStartTexts = ObjectEditor.TransformTexts(); dragStartTransform = lastObjectTransform; }
        if (drag.Canceled) { ObjectEditor.SetTransformTexts(dragStartTexts.Position, dragStartTexts.Rotation); return; }
        try
        {
            // A gesture that has not turned (a click) keeps the rotation as typed rather than an equivalent Euler triple.
            if (transformMode == "rotate" && drag.Rotation.IsIdentity) ObjectEditor.SetTransformTexts(null, dragStartTexts.Rotation);
            else if (transformMode == "rotate")
            {
                var turned = new zStudio.Core.Sources.ObjectTransform(System.Numerics.Vector3.Zero, dragStartTransform.RotationDegrees, System.Numerics.Vector3.One).Matrix() * drag.Rotation;
                ObjectEditor.SetTransformText(null, zStudio.Core.Sources.ObjectTransform.Snap(zStudio.Core.Sources.ObjectTransform.FromMatrix(turned).RotationDegrees, angles: true));
            }
            else
            {
                // Only the axes the drag moved change; an untouched coordinate keeps its exact authored value.
                var start = dragStartTransform.Position; var moved = zStudio.Core.Sources.ObjectTransform.SnapPosition(start + drag.Translation);
                ObjectEditor.SetTransformText(new(drag.Translation.X == 0 ? start.X : moved.X, drag.Translation.Y == 0 ? start.Y : moved.Y, drag.Translation.Z == 0 ? start.Z : moved.Z), null);
            }
        }
        catch (System.IO.InvalidDataException) { }
    }
    internal void ShowSourceError(string message) { error.Text = message; error.Visibility = Visibility.Visible; }
    internal void SetObjectFields(JsonArray fields)
    {
        if (ObjectEditor == null) throw new StudioCommandException("not_ready", "Select an editable source object.");
        if (viewport.IsPickupDragging) throw new StudioCommandException("busy", "Finish or cancel the active transform drag.");
        BeginObjectEdit();
        // Open the draft first so a refused edit (a busy world, another draft) refuses here rather than discarding the input.
        if (DraftDocument == null) OpenObjectDraft();
        ObjectEditor.SetFields(fields);
        if (!HasObjectDraft && DraftDocument != null) { DraftDocument = null; DraftTarget = null; draftId = Guid.Empty; DraftClosed?.Invoke(); }
    }
    private async Task ObjectAction(string action)
    {
        try
        {
            if (!await ResolvePendingAsync()) return;
            if (action == "duplicate") { CopyObjectDialog(); return; }
            if (SourceAction != null) await SourceAction(action, null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is StudioCommandException or InvalidOperationException or System.IO.InvalidDataException) { ShowSourceError(ex.Message); }
    }
    /// <summary>
    /// Copy object: the name is checked as it is typed, the copy's own refusal is shown below the name too, and the window
    /// closes only once the copy is made.
    /// </summary>
    private void CopyObjectDialog()
    {
        TextBox input = new() { MinWidth = 260, Margin = new(12, 6, 12, 0) };
        System.Windows.Automation.AutomationProperties.SetName(input, "Copy name");
        TextBlock problem = new() { Margin = new(12, 6, 12, 0), MaxWidth = 320, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        problem.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        Button accept = new() { Content = "Copy", IsDefault = true, IsEnabled = false, MinWidth = 80, Margin = new(0, 0, 8, 0) };
        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(12) };
        buttons.Children.Add(accept); buttons.Children.Add(cancel);
        StackPanel body = new();
        body.Children.Add(new TextBlock { Text = "Copy as", Margin = new(12, 12, 12, 0) }); body.Children.Add(input); body.Children.Add(problem); body.Children.Add(buttons);
        Window dialog = new() { Owner = Window.GetWindow(this), Title = "Copy object", Content = body, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        bool copying = false;
        void Show(string? message)
        {
            problem.Text = message ?? ""; problem.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
            // A refused copy (the world was busy, say) can be tried again with the same name.
            accept.IsEnabled = !copying && input.Text.Trim() is { Length: > 0 } name && Check(name) == null;
        }
        string? Check(string name) => name.Length == 0 ? null : CheckCopyName?.Invoke(name);
        input.TextChanged += (_, _) => { if (!copying) Show(Check(input.Text.Trim())); };
        dialog.Closing += (_, e) => e.Cancel = copying; // A copy being made finishes first.
        accept.Click += async (_, _) =>
        {
            string name = input.Text.Trim();
            if (copying || name.Length == 0 || Check(name) is not null) { Show(Check(name)); return; }
            copying = true; input.IsEnabled = cancel.IsEnabled = false; Show(null); accept.Content = "Copying…";
            string? failure = null;
            var work = SourceAction?.Invoke("duplicate", name) ?? Task.CompletedTask;
            // The copy's refusals (its plan) show here; once the world rebuilds with it, the window closes and the card shows the rest.
            while (!work.IsCompleted && WorldRebuilding?.Invoke() != true) await Task.WhenAny(work, Task.Delay(50));
            if (!work.IsCompleted) { copying = false; dialog.DialogResult = true; _ = FinishOnCard(work); return; }
            try { await work; }
            catch (OperationCanceledException) { failure = "The copy was canceled."; }
            catch (Exception ex) when (ex is StudioCommandException or InvalidOperationException or System.IO.InvalidDataException) { failure = ex.Message; }
            copying = false; accept.Content = "Copy"; input.IsEnabled = cancel.IsEnabled = true;
            if (failure == null) { dialog.DialogResult = true; return; }
            Show(failure); input.Focus(); input.SelectAll();
        };
        dialog.Loaded += (_, _) => input.Focus();
        dialog.ShowDialog();
    }
    private async Task FinishOnCard(Task work)
    {
        try { await work; }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is StudioCommandException or InvalidOperationException or System.IO.InvalidDataException) { ShowSourceError(ex.Message); }
    }
    /// <summary>
    /// Gives the keyboard to the card: a source object's editor's first control, or a placement's first editable coordinate
    /// while it is shown, else the first choice of the sections a selection adds (the player tank's chosen master type).
    /// Otherwise the keyboard stays where it is (the docked card's panel is not under this element, so moving focus from
    /// here would reach the view's chips).
    /// </summary>
    internal void FocusObject()
    {
        if (ObjectEditor != null) { ObjectEditor.MoveFocus(new(System.Windows.Input.FocusNavigationDirection.First)); return; }
        if (values.TryGetValue(AuthoredPlacement, out var field) && field.Inputs.FirstOrDefault(i => i.IsEnabled && !i.IsReadOnly && i.IsVisible) is { } input) { input.Focus(); return; }
        // No editable coordinate shown (Details collapsed, or the placement read only): the added sections' first choice.
        if (extraSections.Content is PlayerTankPanel { IsVisible: true } tank) tank.FocusFirst();
    }
}
