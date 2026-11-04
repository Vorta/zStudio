using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private sealed record ModelPlacementDraft(DocumentModel Document, long Revision, SourceWorldAddition Addition, SourceSceneSnapshot Candidate, SceneViewport Viewport, SourceCreationParent? Parent, SourceCreationScope Scope, IReadOnlyDictionary<string, string> Frozen)
    {
        public string Token { get; } = Guid.NewGuid().ToString();
        public CancellationTokenSource Stopped { get; } = new();
    }
    private ModelPlacementDraft? modelPlacement;
    private CancellationTokenSource? modelPlacementPreparing;
    private bool modelPlacementCommitting;

    private ViewportChip? modelPlacementBanner;
    private TextBlock? modelPlacementText;
    private bool HasModelPlacementDraft => modelPlacement != null || modelPlacementPreparing != null;
    private object ModelPlacementState() => new { preparing = modelPlacementPreparing != null, token = modelPlacement?.Token,
        model = modelPlacement?.Addition.Model.Model, name = modelPlacement?.Addition.Model.Name, parent = modelPlacement?.Parent, scope = modelPlacement?.Scope,
        position = modelPlacement?.Viewport.ModelPlacementPosition is { } p ? new { x = p.X, y = p.Y, z = p.Z } : null,
        valid = modelPlacement?.Viewport.ModelPlacementPosition != null, committing = modelPlacementCommitting };

    private async Task BeginModelPlacementAsync(DocumentModel doc, string model, string? name, CancellationToken token, SourceCreationParent? parent = null)
    {
        RequireNoDrafts(); ValidateCreationParent(doc, parent);
        var session = SourceWorldOf(doc);
        RequireSourceWorldIdle(session);
        if (doc != shownDocument || scene == null || !HasPublishedStaticScene || InAnimationMode || ZoneToolEngaged || scene.IsFlyActive || presentationUiHidden)
            throw new StudioCommandException("not_ready", "Open the map's normal Whole world view and leave the active tool before placing an object.");
        if (doc.SourceInputsChanged(verifyContent: false)) throw new StudioCommandException("stale_document", "Sources changed; reload the map before placing an object.");
        var viewport = scene;
        long revision = session.Workspace.Revision;
        var difficulty = ViewModel.Difficulty;
        int lod = Math.Max(0, LodCombo.SelectedIndex);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token, ViewModel.WorkspaceToken, shutdownToken);
        modelPlacementPreparing = cancellation;
        try
        {
            var result = await Task.Run(async () =>
            {
                var prepared = session.Workspace.BeginPreparedEdit();
                var addition = new SourceModelAddition(model.Replace('\\', '/'), name ?? "object_new", Vector3.Zero);
                var assets = SourceWorlds.ValidatePlacement(prepared.Workspace, session.Mission, addition, cancellation.Token);
                if (name == null)
                {
                    HashSet<string> names = new(assets.Names, StringComparer.Ordinal);
                    foreach (var node in doc.Document.Scene?.Nodes ?? []) names.Add(node.Name);
                    foreach (var node in doc.SourceBuild!.AuthoredHierarchy.Nodes) names.Add(node.Name);
                    string stem = Path.GetFileNameWithoutExtension(model);
                    stem = stem[..Math.Min(stem.Length, SourceWorlds.MaximumNameLength - 8)];
                    string chosen = stem + "_new";
                    for (int i = 1; names.Contains(chosen); i++) { cancellation.Token.ThrowIfCancellationRequested(); chosen = stem + "_" + i; }
                    addition = addition with { Name = chosen };
                }
                else if (assets.Names.Contains(name) || doc.Document.Scene?.Nodes.Any(n => n.Name == name) == true)
                    throw new InvalidDataException($"The name {name} already belongs to the world or the selected model. Choose an unused object name.");
                var scope = SourceObjectEdits.CreationScope(prepared.Workspace, doc.SourceBuild!, parent, cancellation.Token);
                var before = prepared.Workspace.Overlay();
                var creation = SourceObjectEdits.PlanCreate(prepared.Workspace, SourceWorldModel(doc).World, doc.SourceBuild!, scope, addition.Name, addition, cancellation.Token);
                var originals = creation.Edit.Changes.ToDictionary(c => c.Relative, c => SourceProject.Sha256(prepared.Workspace.Read(c.Relative, cancellation.Token)!), StringComparer.OrdinalIgnoreCase);
                prepared.Workspace.Apply(creation.Edit.Label, creation.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), cancellation.Token);
                var built = await SourceWorlds.LoadSceneAsync(session.Root, session.Mission, prepared.Workspace.Overlay(), cancellation.Token);
                SourceWorlds.ValidatePlacementTextures(assets, built, cancellation.Token);
                var roots = SourceObjectEdits.CreatedSlots(creation, built);
                var frozen = new Dictionary<string, string>(SourceObjectEdits.ValidateCreation(prepared.Workspace, before, creation, built, addition, cancellation.Token), StringComparer.OrdinalIgnoreCase);
                foreach (var (path, hash) in originals) frozen[path] = hash;
                return (built, addition, roots, scope, frozen, mission: built.MissionScene(difficulty, cancellation.Token, session.PlayerTank));
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (doc.IsDisposed || shownDocument != doc || scene != viewport || session.Workspace.Revision != revision || viewport.IsFlyActive || InAnimationMode || ZoneToolEngaged || presentationUiHidden)
                throw new StudioCommandException("context_changed", "The map changed during placement preparation; choose the object again.");
            await viewport.BeginModelPlacementAsync(result.built, result.mission.Scene, result.roots, lod, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (doc.IsDisposed || shownDocument != doc || scene != viewport || session.Workspace.Revision != revision || viewport.IsFlyActive || InAnimationMode || ZoneToolEngaged || presentationUiHidden)
                throw new StudioCommandException("context_changed", "The map changed during preview creation; choose the object again.");
            modelPlacement = new(doc, revision, new(result.addition, []), result.built, viewport, parent, result.scope, result.frozen);
            viewport.ModelPlacementChanged += UpdateModelPlacementBanner;
            EnsureModelPlacementBanner(); UpdateModelPlacementBanner();
            viewport.RenderSurface.Focus();
        }
        catch { viewport.ClearModelPlacement(); throw; }
        finally { modelPlacementPreparing = null; }
    }
    private void EnsureModelPlacementBanner()
    {
        if (modelPlacementBanner != null) return;
        modelPlacementBanner = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(10, 12, 10, 0), Padding = new(12, 6, 6, 6) };
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        modelPlacementText = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 650 };
        Button cancel = new() { Content = "Cancel", Margin = new(12, 0, 0, 0) };
        cancel.Click += (_, _) => CancelModelPlacement();
        content.Children.Add(modelPlacementText); content.Children.Add(cancel); modelPlacementBanner.Content = content;
        System.Windows.Automation.AutomationProperties.SetName(modelPlacementBanner, "Object placement");
        PreviewOverlayChrome.Children.Add(modelPlacementBanner);
        SceneHost.PreviewMouseMove += (_, e) =>
        {
            if (modelPlacement is not { } draft || modelPlacementCommitting) return;
            if (e.OriginalSource is not DependencyObject origin || !IsWithinRenderSurface(origin, draft.Viewport.RenderSurface))
            { LastModelPlacementPoint = null; draft.Viewport.SetModelPlacementPoint(null); return; }
            LastModelPlacementPoint = e.GetPosition(draft.Viewport.RenderSurface); draft.Viewport.SetModelPlacementPoint(LastModelPlacementPoint);
        };
        SceneHost.MouseLeave += (_, _) => { LastModelPlacementPoint = null; modelPlacement?.Viewport.SetModelPlacementPoint(null); };
        SceneHost.PreviewMouseDown += (_, e) =>
        {
            if (modelPlacement == null || modelPlacementCommitting || e.ChangedButton != MouseButton.Left) return;
            // Overlay controls own their clicks; only the actual render surface places an object.
            if (e.OriginalSource is not DependencyObject origin || !IsWithinRenderSurface(origin, modelPlacement.Viewport.RenderSurface)) return;
            var point = e.GetPosition(modelPlacement.Viewport.RenderSurface);
            if (modelPlacement.Viewport.NavigationCubeBounds.Contains(point))
            { LastModelPlacementPoint = null; modelPlacement.Viewport.SetModelPlacementPoint(null); return; }
            e.Handled = true;
            LastModelPlacementPoint = point; modelPlacement.Viewport.SetModelPlacementPoint(LastModelPlacementPoint);
            if (modelPlacement.Viewport.ModelPlacementPosition != null) _ = ReportUi(() => ConfirmModelPlacementAsync(CancellationToken.None));
        };
    }
    private static bool IsWithinRenderSurface(DependencyObject origin, DependencyObject surface)
    {
        for (var at = origin; at != null; at = at is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at == surface) return true;
        return false;
    }
    private void UpdateModelPlacementBanner()
    {
        if (modelPlacementBanner == null) return;
        modelPlacementBanner.Visibility = modelPlacement == null ? Visibility.Collapsed : Visibility.Visible;
        if (modelPlacement == null) return;
        modelPlacementText!.MaxWidth = Math.Max(140, modelPlacement.Viewport.RenderSurface.ActualWidth - 150);
        modelPlacementText!.Text = modelPlacement.Addition.Model.Name + " · inside " + modelPlacement.Scope.Name + (modelPlacement.Viewport.ModelPlacementPosition is { } p
            ? FormattableString.Invariant($" · X {p.X:0.0}  Y {p.Y:0.0}  Z {p.Z:0.0}\nLeft click to place · Esc cancels")
            : " · Point at a map surface\nLeft click to place · Esc cancels");
    }
    private void CancelModelPlacement()
    {
        modelPlacementPreparing?.Cancel();
        if (modelPlacement is { } draft)
        {
            draft.Stopped.Cancel(); draft.Viewport.ModelPlacementChanged -= UpdateModelPlacementBanner;
            draft.Viewport.ClearModelPlacement(); modelPlacement = null;
            LastModelPlacementPoint = null;
        }
        UpdateModelPlacementBanner();
    }
    private async Task<DocumentModel> ConfirmModelPlacementAsync(CancellationToken token)
    {
        if (modelPlacementCommitting) throw new StudioCommandException("busy", "The object placement is already being confirmed.");
        var draft = modelPlacement ?? throw new StudioCommandException("stale_draft", "No object placement is pending.");
        draft.Viewport.SetModelPlacementPoint(LastModelPlacementPoint);
        var position = draft.Viewport.ModelPlacementPosition ?? throw new StudioCommandException("not_ready", "Point at a visible map surface before placing the object.");
        if (draft.Document != shownDocument || draft.Document.SourceWorld?.Workspace.Revision != draft.Revision)
        { CancelModelPlacement(); throw new StudioCommandException("stale_draft", "The map changed; choose the object again."); }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, draft.Stopped.Token);
        draft.Viewport.FreezeModelPlacement();
        modelPlacementCommitting = true;
        try
        {
            var next = await CreateSourceChildAsync(draft.Document, draft.Parent, draft.Addition.Model.Name,
                draft.Addition.Model with { Position = position }, cancellation.Token, draft.Frozen);
            return next;
        }
        finally { modelPlacementCommitting = false; CancelModelPlacement(); }
    }
    private Point? LastModelPlacementPoint;


    private void RegisterModelPlacementCommands(StudioCommands registry)
    {
        RegisterJob(registry, "source_world_placement", "Prepare a project glTF object for a source map, preview it at a viewport surface point, confirm one undoable source addition, or cancel. Models and external PNG textures must be in common/current-map models and textures folders, including subfolders. No assets are copied and no sources change before confirm. Uses the same ghost and hit test as Add object. Inspect preview_state.modelPlacement for the token and position. No physical input is captured.",
            [PreviewParameter, DocumentParameter, RevisionParameter, CreationParentParameter, P("action", "string", "Placement action.", true, "begin", "get", "set", "confirm", "cancel"),
             P("model", "string", "Project-relative glTF/GLB path for begin."), P("name", "string", "Optional unused object name; omitted generates one."),
             P("token", "string", "Current placement token for set, confirm or cancel."),
             new("screenPoint", "array", "Viewport DIP [x,y] for set; empty space hides the preview and cannot be confirmed.", Items: new("", "number", "Coordinate."), MinItems: 2, MaxItems: 2)], true,
            async (a, token) =>
            {
                var viewport = TargetViewport(a); var doc = TargetDocument(a);
                if (a["revision"]?.GetValue<long>() != doc.Revision) throw new StudioCommandException("revision_conflict", "Read the current document revision.");
                string action = Text(a, "action");
                if (action == "begin") await BeginModelPlacementAsync(doc, Text(a, "model"), a["name"]?.GetValue<string>(), token, CreationParentArgument(a));
                else if (action != "get")
                {
                    if (modelPlacement == null || Text(a, "token") != modelPlacement.Token) throw new StudioCommandException("stale_draft", "Read the current placement token from preview_state.");
                    if (action == "cancel") CancelModelPlacement();
                    else if (action == "confirm") doc = await ConfirmModelPlacementAsync(token);
                    else if (a["screenPoint"] is JsonArray point)
                    {
                        if (modelPlacementCommitting) throw new StudioCommandException("busy", "The object placement is already being confirmed.");
                        double x = point[0]!.GetValue<double>(), y = point[1]!.GetValue<double>();
                        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new StudioCommandException("invalid_argument", "Screen coordinates must be finite.");
                        LastModelPlacementPoint = new(x, y); viewport.SetModelPlacementPoint(LastModelPlacementPoint);
                    }
                    else throw new StudioCommandException("invalid_argument", "Supply screenPoint for set.");
                }
                return Result(new { placement = ModelPlacementState(), document = DocumentState(doc) });
            });
    }
}
