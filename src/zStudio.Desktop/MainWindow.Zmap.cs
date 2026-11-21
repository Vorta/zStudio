using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private ZmapEditorSession? zmap;
    private SceneViewport.ViewPose? zmapPreviousView;
    private bool zmapBusy, zmapRefreshing, zmapInsert;
    private List<Vector3>? zmapDrawing;
    private (Guid Outline, Guid Vertex, Point Press, Point Projected, Vector3 Position)? zmapDrag;
    private TextBlock zmapName = null!, zmapHint = null!, zmapStatus = null!;
    private ListBox zmapOutlines = null!;
    private ComboBox zmapVertices = null!, zmapObjectives = null!;
    private TextBox zmapColor = null!, zmapX = null!, zmapY = null!, zmapZ = null!;
    private Button zmapSave = null!, zmapSaveAs = null!, zmapUndo = null!, zmapRedo = null!,
        zmapFinish = null!, zmapCancel = null!, zmapInsertButton = null!,
        zmapRemove = null!, zmapApply = null!, zmapRevert = null!,
        zmapSplitButton = null!, zmapRemoveOutline = null!, zmapFrameOutline = null!;
    private ToggleButton zmapDraw = null!;
    private Zmap? zmapListedMap;
    private ZmapOutline? zmapListedOutline;
    private Guid? zmapDisplayedVertex;
    private bool zmapFieldsDirty;

    private bool ZmapAvailable => shownDocument?.SourceWorld is { IsRebuilding: false } &&
        shownDocument == ViewModel.SelectedDocument && shownDocument.Document.Game == GameVariant.Recoil &&
        shownAsset?.Kind == AssetKind.World && HasPublishedStaticScene && !sourceWorkspaceBusy && sourceWorldsOpening == 0 && sourceMapOpening == null && !OperationRunning &&
        !InAnimationMode && !presentationUiHidden && scene is { IsFlyActive: false };

    private bool HasZmapPathDraft => zmapDrawing != null || zmapSplit != null;
    private bool HasZmapDraft => HasZmapPathDraft || zmapDrag != null || zmapFieldsDirty || zmapInsert;

    private void InitializeZmap()
    {
        BuildZmapCommandBar();
        BuildZmapOutlinesPane();
        BuildZmapCard();
        BuildZmapViewChrome();
        InitializeZmapOutlineMenu();
        zmapOutlines.SelectionChanged += (_, _) => ZmapSelectionChanged(zmapOutlines, false);
        zmapVertices.SelectionChanged += (_, _) => ZmapSelectionChanged(zmapVertices, true);
        zmapObjectives.SelectionChanged += (_, _) => ZmapFieldChanged();
        foreach (var field in new[] { zmapColor, zmapX, zmapY, zmapZ }) field.TextChanged += (_, _) => ZmapFieldChanged();
        zmapColor.TextChanged += (_, _) => RefreshZmapSwatch();
        PreviewHost.PreviewMouseDown += ZmapPointerDown;
        PreviewHost.PreviewMouseMove += ZmapPointerMove;
        PreviewHost.PreviewMouseUp += ZmapPointerUp;
        PreviewHost.PreviewMouseWheel += (_, e) => { if (zmapDrag != null) e.Handled = true; };
        PreviewKeyDown += ZmapModifierChanged;
        PreviewKeyUp += ZmapModifierChanged;
        PreviewHost.SizeChanged += (_, _) => { if (zmapDrag != null) { CancelZmapDrag(); RefreshZmap(); } else if (zmap != null) RefreshZmapBanner(); };
        PreviewHost.LostMouseCapture += (_, e) =>
        {
            if (e.OriginalSource == PreviewHost && zmapDrag != null) { CancelZmapDrag(); RefreshZmap(); }
        };
        Deactivated += (_, _) => { if (zmapDrag != null) { CancelZmapDrag(); RefreshZmap(); } };
    }

    private void EditZmapClick(object sender, RoutedEventArgs e) => ZmapUiAsync(async () =>
    {
        if (zmap != null) { await ResolveZmapExitAsync(); return; }
        if (!await ResolvePropertiesDraftsAsync()) return;
        await SourceWorldsIdleAsync();
        if (staticRefreshWork is { IsCompleted: false } refresh) await refresh;
        EnterZmap();
    });

    private void EnterZmap()
    {
        if (zmap != null) return;
        if (!ZmapAvailable || scene == null || ZoneToolEngaged || scene.IsTerrainStroking ||
            scene.AiPathEditingNetwork != null || scene.IsPickupDragging || HasGuiDraft || staticRefreshWork is { IsCompleted: false })
            throw new StudioCommandException("not_ready", "Show a ready RECOIL source map and finish the active editing tool.");
        StopWorldAnimationForEdit();
        zmapPreviousView = scene.CaptureView();
        zmap = new(shownDocument!);
        ZmapLayer.Editor = zmap;
        ZmapLayer.Viewport = scene;
        scene.ZmapSnapChanged += ZmapSnapChanged;
        HashSet<int> recipeNodes = [], databaseNodes = [];
        if (zmap.Document.SourceBuild is { } build)
            foreach (var (node, origin) in build.Provenance)
            {
                if (origin.Terrain != null) recipeNodes.Add(node);
                else if (origin.Database && !origin.ModelTransformAuthored) databaseNodes.Add(node);
            }
        scene.SetZmapView(true, recipeNodes, databaseNodes);
        InitializeZmapTerrainZones();
        scene.ViewChanged += ZmapLayer.InvalidateVisual;
        UpdateSourceInputBlock();
        ApplyTerrainBrush(scene);
        ArrangeWorkspace();
        RefreshZmap();
        UpdateLayout();
        ZmapLayer.Viewport.TryFrame("all");
        ZmapLayer.Viewport.FocusView();
    }

    private void LeaveZmap()
    {
        if (zmap == null) return;
        ClearZmapTerrainControl();
        CancelZmapDraft();
        if (ZmapLayer.Viewport is { } viewport)
        {
            viewport.ViewChanged -= ZmapLayer.InvalidateVisual;
            viewport.ZmapSnapChanged -= ZmapSnapChanged;
            viewport.SetZmapView(false);
            if (zmapPreviousView is { } previous) viewport.RestoreView(previous);
        }
        zmap = null;
        zmapPreviousView = null;
        ZmapLayer.Viewport = null;
        ZmapLayer.Editor = null;
        zmapListedMap = null;
        zmapListedOutline = null;
        zmapOutlines.ItemsSource = null;
        zmapVertices.ItemsSource = null;
        ZmapMessage("");
        RefreshZmapChrome();
        UpdateSourceInputBlock();
        UpdateDocumentCommands();
        ApplyTerrainBrush(scene);
        ArrangeWorkspace();
        scene?.FocusView();
    }

    private void RefreshZmapChrome()
    {
        var visible = zmap != null ? Visibility.Visible : Visibility.Collapsed;
        ZmapBar.Visibility = ZmapOutlinesPane.Visibility = ZmapPane.Visibility = ZmapLayer.Visibility = ZmapChrome.Visibility = ZmapStatus.Visibility = visible;
        if (zmap != null)
        {
            // The map's command bar, chips and status give way to the mode's; the title row keeps the map's name.
            SourceCommandBar.Visibility = PreviewOverlayChrome.Visibility = Visibility.Collapsed;
            SourceStatusLeft.Visibility = SourceStatusRight.Visibility = Visibility.Collapsed;
            AnimationMenu.IsEnabled = HideUiMenu.IsEnabled = false;
        }
        else
        {
            PreviewOverlayChrome.Visibility = Visibility.Visible;
            DocumentCommands.Visibility = ViewModel.SelectedDocument != null && !presentationUiHidden ? Visibility.Visible : Visibility.Collapsed;
            if (sourceChromeActive)
                SourceStatusLeft.Visibility = SourceStatusRight.Visibility = presentationUiHidden ? Visibility.Collapsed : Visibility.Visible;
            AnimationMenu.IsEnabled = animation != null || ViewModel.SelectedDocument?.SourceWorld != null;
            HideUiMenu.IsEnabled = presentationUiHidden || ActiveNavigationViewport != null;
        }
    }

    private readonly record struct ZmapChoice(int Value, string Label);
    /// <summary>An Outlines row: its name, its objective in grey (dimmer without one), its vertex count and its authored colour.</summary>
    private sealed record ZmapOutlineRow(Guid Id, string Name, string Objective, bool Assigned, string Meta, System.Windows.Media.Brush Swatch, string ToolTip);
    private sealed record ZmapVertexRow(Guid Id, string Label);

    private static string ZmapObjectiveLabel(int index, int? count) => index == -1 ? "No objective" :
        index < 0 || index >= (count ?? 10) ? $"Unmatched index {index}" :
        count == null ? $"Objective {index + 1} (unverified)" : $"Objective {index + 1}";

    private void RefreshZmap()
    {
        if (zmap is not { } editor) return;
        var map = editor.Map;
        var outline = editor.Selected;
        zmapRefreshing = true;
        try
        {
            if (!ReferenceEquals(map, zmapListedMap))
            {
                zmapListedMap = map;
                zmapOutlines.ItemsSource = map?.Outlines.Select((o, index) =>
                {
                    string objective = ZmapObjectiveLabel(o.ObjectiveIndex, editor.ObjectiveCount);
                    int count = o.Vertices.Length;
                    return new ZmapOutlineRow(o.Id, $"Outline {index + 1}", objective, o.ObjectiveIndex != -1, $"{count:N0} pts", ZmapOverlay.Swatch(o.Color),
                        $"Outline {index + 1} · {count:N0} {(count == 1 ? "vertex" : "vertices")} · {objective} · {o.Color}");
                }).ToArray();
            }
            zmapOutlines.SelectedValue = editor.Outline;
            if (zmapOutlines.SelectedItem is { } row) zmapOutlines.ScrollIntoView(row);
            if (!ReferenceEquals(outline, zmapListedOutline))
            {
                zmapListedOutline = outline;
                // Its number, then X and Z: short enough to show whole in a picker at the narrowest pane's width.
                zmapVertices.ItemsSource = outline?.Vertices.Select((v, index) => new ZmapVertexRow(v.Id,
                    $"{index + 1} · {v.Position.X:G7}, {v.Position.Z:G7}")).ToArray();
                var objectives = Enumerable.Range(-1, (editor.ObjectiveCount ?? 10) + 1).ToList();
                if (outline != null && !objectives.Contains(outline.ObjectiveIndex)) objectives.Add(outline.ObjectiveIndex);
                zmapObjectives.ItemsSource = objectives.Select(i => new ZmapChoice(i, ZmapObjectiveLabel(i, editor.ObjectiveCount))).ToArray();
            }
            zmapVertices.SelectedValue = editor.Vertex;
            if (!zmapFieldsDirty || zmapDisplayedVertex != editor.Vertex)
            {
                zmapDisplayedVertex = editor.Vertex;
                zmapColor.Text = outline?.Color.ToString() ?? "";
                zmapObjectives.SelectedValue = outline?.ObjectiveIndex;
                var position = SelectedZmapPosition();
                zmapX.Text = Format(position?.X); zmapY.Text = Format(position?.Y); zmapZ.Text = Format(position?.Z);
            }
            // The Draw toggle follows the draft; its handlers ignore this.
            zmapDraw.IsChecked = zmapDrawing != null;
        }
        finally { zmapRefreshing = false; }
        zmapOutlines.IsEnabled = !HasZmapPathDraft;
        bool idle = !zmapBusy && !HasZmapDraft;
        zmapSave.IsEnabled = zmapSaveAs.IsEnabled = map != null && !zmapBusy && !HasZmapPathDraft;
        zmapUndo.IsEnabled = idle && editor.CanUndo;
        zmapRedo.IsEnabled = idle && editor.CanRedo;
        zmapDraw.IsEnabled = map != null && (idle || zmapDrawing != null && !zmapBusy);
        zmapSplitButton.IsEnabled = idle && outline?.Vertices.Length >= 4;
        zmapRemoveOutline.IsEnabled = idle && outline != null;
        zmapFrameOutline.IsEnabled = !zmapBusy && zmapDrag == null && outline != null;
        zmapInsertButton.IsEnabled = idle && outline?.Vertices.Length > 1;
        zmapRemove.IsEnabled = idle && editor.Vertex != null;
        zmapApply.IsEnabled = zmapRevert.IsEnabled = zmapFieldsDirty && !zmapBusy;
        SaveMenu.IsEnabled = zmapSave.IsEnabled;
        UndoMenu.IsEnabled = zmapUndo.IsEnabled;
        RedoMenu.IsEnabled = zmapRedo.IsEnabled;
        zmapColor.IsEnabled = zmapObjectives.IsEnabled = !HasZmapPathDraft && !zmapInsert && zmapDrag == null && !zmapBusy;
        zmapX.IsEnabled = zmapY.IsEnabled = zmapZ.IsEnabled = zmapColor.IsEnabled && editor.Vertex != null;
        ZmapLayer.Pending = zmapDrawing;
        RefreshZmapTerrainControl();
        RefreshZmapPathControls();
        RefreshZmapPresentation(editor);
        ZmapLayer.InvalidateVisual();
        static string Format(float? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    }

    private Vector3? SelectedZmapPosition() => zmap?.Selected is { } outline && zmap.Vertex is Guid vertex
        ? outline.Vertices[ZmapEditorSession.FindVertex(outline, vertex)].Position : null;

    private void ZmapSelectionChanged(Selector selector, bool vertex)
    {
        if (zmapRefreshing || zmap is not { } editor) return;
        EditZmap(() =>
        {
            RequireZmapIdle();
            Guid? value = selector.SelectedValue as Guid?;
            editor.Select(vertex ? editor.Outline : value, vertex ? value : null);
        });
    }

    private void ZmapFieldChanged()
    {
        if (zmap == null || zmapRefreshing) return;
        zmapFieldsDirty = true;
        zmap.Touch();
        zmapApply.IsEnabled = zmapRevert.IsEnabled = true;
        zmapUndo.IsEnabled = zmapRedo.IsEnabled = zmapDraw.IsEnabled = zmapInsertButton.IsEnabled = zmapRemove.IsEnabled = false;
        zmapSplitButton.IsEnabled = zmapRemoveOutline.IsEnabled = zmapAddOutline.IsEnabled = false;
        zmapPreviousVertex.IsEnabled = zmapNextVertex.IsEnabled = zmapCardClose.IsEnabled = false;
        zmapFooterStatus.Text = "Unsaved changes";
        zmapFooterDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentFillColorDefaultBrush");
    }

    private ZmapOutline SelectedZmapOutline() => zmap?.Selected ??
        throw new StudioCommandException("not_ready", "Select an outline.");

    private void ApplyZmapFields()
    {
        if (!zmapFieldsDirty || zmap is not { } editor) return;
        var outline = SelectedZmapOutline();
        ZmapColor color = ZmapEditorSession.ParseColor(zmapColor.Text);
        int objective = zmapObjectives.SelectedValue is int index ? index : outline.ObjectiveIndex;
        Vector3? position = editor.Vertex != null ? new(Parse(zmapX), Parse(zmapY), Parse(zmapZ)) : null;
        editor.SetFields(outline.Id, editor.Vertex, position, color, objective);
        zmapFieldsDirty = false;

        static float Parse(TextBox box) => float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
            ? value : throw new InvalidDataException("Enter finite X, Y, and Z coordinates.");
    }

    private void BeginZmapDraw()
    {
        RequireZmapIdle();
        zmap!.RequireMap();
        zmapDrawing = [];
        zmap.Touch();
        ZmapLayer.Viewport?.FocusView();
    }

    private void AddZmapDraftPoint(Vector3 point)
    {
        if (zmapDrawing!.Count >= ZmapCodec.MaximumVertices - zmap!.RequireMap().Outlines.Sum(o => o.Vertices.Length))
            throw new InvalidDataException("This ZMAP has reached the vertex limit.");
        zmapDrawing.Add(point);
        zmap.Touch();
    }

    private void FinishZmapDraw()
    {
        if (zmapDrawing == null) return;
        zmap!.Add(zmapDrawing.ToImmutableArray(), ZmapColor.White, -1);
        zmapDrawing = null;
        ZmapLayer.Pointer = null;
    }

    private void CancelZmapDraft()
    {
        if (HasZmapDraft) zmap?.Touch();
        CancelZmapDrag();
        zmapDrawing = null;
        zmapSplit = null;
        zmapSplitHover = null;
        ZmapLayer.SplitPreview = null;
        zmapInsert = zmapFieldsDirty = false;
        ZmapLayer.Pointer = null;
    }

    private void CancelZmapDrag()
    {
        if (zmapDrag == null) return;
        zmap?.Touch();
        zmapDrag = null;
        ZmapLayer.Drag = null;
        ZmapLayer.SnapTarget = null;
        if (PreviewHost.IsMouseCaptured) PreviewHost.ReleaseMouseCapture();
    }

    private void ChangeZmapHistory(bool redo)
    {
        RequireZmapIdle();
        zmap!.History(redo);
    }

    private void RemoveZmapVertex()
    {
        RequireZmapIdle();
        var outline = SelectedZmapOutline();
        if (zmap?.Vertex is Guid vertex) zmap.RemoveVertex(outline.Id, vertex);
    }

    private void FrameZmap(string target)
    {
        if (zmapDrag != null) throw new StudioCommandException("busy", "Finish the vertex drag first.");
        var viewport = ZmapLayer.Viewport!;
        if (target == "map")
        {
            if (!viewport.TryFrame("all")) throw new StudioCommandException("not_ready", "There is no visible map geometry to frame.");
            return;
        }
        var outline = SelectedZmapOutline();
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        if (target == "vertex")
            min = max = SelectedZmapPosition() ?? throw new StudioCommandException("not_ready", "Select a vertex.");
        else
            foreach (var vertex in outline.Vertices)
            {
                min = Vector3.Min(min, vertex.Position);
                max = Vector3.Max(max, vertex.Position);
            }
        if (!viewport.FrameZmapOutline(min, max)) throw new StudioCommandException("not_ready", "The view could not frame the selection.");
    }

    private void RequireZmapIdle()
    {
        if (zmapBusy) throw new StudioCommandException("busy", "A ZMAP file operation is in progress.");
        if (HasZmapDraft) throw new StudioCommandException("pending_drafts", "Finish or cancel the ZMAP input first.");
    }

    private void EditZmap(Action action)
    {
        try { ZmapMessage(""); action(); }
        catch (Exception error) when (error is StudioCommandException or InvalidDataException or ArgumentException) { ZmapMessage(error.Message); }
        finally { RefreshZmap(); }
    }

    /// <summary>Shows a message in the chip at the view's lower left: a refusal (caution) or what an action did; the next action clears it.</summary>
    private void ZmapMessage(string message, bool problem = true)
    {
        bool changed = zmapStatus.Text != message;
        zmapStatus.Text = message;
        zmapMessageChip.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // A live region: a screen reader hears a new refusal or confirmation.
        if (changed && message.Length != 0) SourceExportSelectionDialog.LiveRegionChanged(zmapStatus);
        zmapMessageIcon.Kind = problem ? "Warning" : "Info";
        zmapMessageIcon.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, problem ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
    }

    private async void ZmapUiAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is StudioCommandException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            MessageBox.Show(this, error.Message, "ZMAP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RefreshZmap(); }
    }
}
