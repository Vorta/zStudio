using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// Source worlds: when the open root is a source project, a mission's world is shown as its build script assembles it
/// from the project's models, and edited by changing those sources. Each edit rebuilds a private copy of the mission
/// and replaces the shown document, keeping the camera.
/// </summary>
public partial class MainWindow
{
    private Task sourceWorldWork = Task.CompletedTask;
    private (DocumentModel Document, SceneViewport.ViewPose View)? pendingSourceView;
    private long sourceWorldMenuGeneration;
    private static string SourceWorldProblemFile(SourceWorldSession session) => Path.Combine(session.Root, SourceProject.GameGenFolder, session.Mission + ".gs");

    private DocumentModel? OpenSourceWorld(string root, string mission) => ViewModel.Documents.FirstOrDefault(d => !d.IsDisposed && d.SourceWorld is { IsDisposed: false } world &&
        world.Mission.Equals(mission, StringComparison.OrdinalIgnoreCase) && world.Root.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens (or activates) the world <paramref name="mission"/> builds from the open source project.</summary>
    private async Task<DocumentModel> OpenSourceWorldAsync(string mission, CancellationToken token)
    {
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
        mission = mission.Trim().ToLowerInvariant();
        IReadOnlyList<string> missions;
        try { missions = await Task.Run(() => SourceWorlds.Missions(root), token); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        if (!missions.Contains(mission, StringComparer.OrdinalIgnoreCase))
            throw new StudioCommandException("invalid_argument", missions.Count == 0 ? "This project builds no worlds (it needs gamegen/mN.gs scripts and glTF models)." : $"This project builds no {mission} world. Worlds: {string.Join(", ", missions)}.");
        if (OpenSourceWorld(root, mission) is { } open) { ViewModel.SelectedDocument = open; SelectNavigatorSection(1); return open; }
        RequireNoDrafts();
        long workspace = ViewModel.WorkspaceGeneration;
        SourceWorldSession session;
        try { session = await Task.Run(() => new SourceWorldSession(root, mission), token); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        DocumentModel? doc = null;
        try
        {
            var (world, build) = await BuildSourceWorldAsync(session, token);
            if (ViewModel.WorkspaceGeneration != workspace || SourceProjectRoot != root) throw new StudioCommandException("context_changed", "The workspace changed while the world was building.");
            if (OpenSourceWorld(root, mission) is { } other) { session.Dispose(); ViewModel.SelectedDocument = other; return other; }
            doc = new DocumentModel(world, session, build);
            ReportSourceBuild(session, build);
            ViewModel.AddDocument(doc, true);
            SelectNavigatorSection(1);
            ViewModel.Status = $"Built the {mission} world from its sources";
            return doc;
        }
        catch
        {
            if (doc == null) session.Dispose();
            else if (!ViewModel.Documents.Contains(doc)) doc.Dispose();
            throw;
        }
    }

    /// <summary>Builds the session's current sources into a new private folder; a newer request supersedes this one.</summary>
    private async Task<(ZbdDocument World, SourceWorldBuild Build)> BuildSourceWorldAsync(SourceWorldSession session, CancellationToken token)
    {
        session.Building?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ViewModel.WorkspaceToken, shutdownToken);
        session.Building = cancellation;
        string folder = session.NextFolder(); bool built = false;
        try
        {
            var overlay = session.Edits.Overlay(cancellation.Token);
            var progress = new Progress<SourceProgress>(p => { if (session.Building == cancellation && !cancellation.IsCancellationRequested) ViewModel.Status = $"Building the {session.Mission} world {p.Completed}/{p.Total}: {p.Item}"; });
            var build = await Task.Run(() => SourceWorlds.BuildPreviewAsync(session.Root, session.Mission, folder, overlay, progress, cancellation.Token), cancellation.Token);
            var world = await Task.Run(() => FormatRegistry.Default.OpenAsync(build.WorldPath, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (session.IsDisposed || session.Building != cancellation) throw new OperationCanceledException(cancellation.Token);
            if (world.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException("The built world does not reopen: " + error.Message);
            built = true;
            return (world, build);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new StudioCommandException("context_changed", "A newer edit, the world's closing or a workspace change superseded this build."); }
        catch (InvalidDataException ex) { throw new StudioCommandException("build_failed", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        finally
        {
            if (session.Building == cancellation) session.Building = null;
            if (!built) SourceWorldSession.DeleteBuild(folder);
        }
    }

    /// <summary>Lists the build's problems under the world's script, replacing those of its previous build.</summary>
    private void ReportSourceBuild(SourceWorldSession session, SourceWorldBuild build)
    {
        string file = SourceWorldProblemFile(session);
        foreach (var old in ViewModel.Problems.Where(p => p.File == file).ToArray()) ViewModel.Problems.Remove(old);
        foreach (var output in build.Outputs)
        {
            if (output.Error != null) ViewModel.AddProblem(Bounded($"{session.Label}: {output.Path} did not build: {output.Error}"), "Warning", file);
            // Interface image notes concern the export, not the world.
            if (output.Family == "images") continue;
            foreach (string warning in output.Warnings.Take(64)) ViewModel.AddProblem(Bounded($"{session.Label}: {output.Path}: {warning}"), "Warning", file);
        }
    }

    /// <summary>Rebuilds the session's world and replaces the document showing it, keeping the camera.</summary>
    private async Task<DocumentModel> RebuildSourceWorldAsync(SourceWorldSession session, CancellationToken token)
    {
        var (world, build) = await BuildSourceWorldAsync(session, token);
        var current = session.Owner;
        if (session.IsDisposed || current == null || current.IsDisposed || !ViewModel.Documents.Contains(current))
        { SourceWorldSession.DeleteBuild(build.Folder); throw new StudioCommandException("context_changed", "The world was closed while it was building."); }
        try { RequireNoDrafts(current); }
        catch { SourceWorldSession.DeleteBuild(build.Folder); throw; }
        SceneViewport.ViewPose? view = null;
        if (shownDocument == current && scene != null && HasPublishedStaticScene)
            try { view = scene.CaptureView(); } catch (InvalidOperationException) { }
        var replacement = new DocumentModel(world, session, build);
        ReportSourceBuild(session, build);
        pendingSourceView = view == null ? null : (replacement, view);
        ViewModel.ReplaceDocument(current, replacement);
        ViewModel.Status = $"Rebuilt the {session.Mission} world from its sources";
        return replacement;
    }

    /// <summary>Applies one edit and rebuilds. An edit whose rebuild fails is reverted, so the shown world and the edits agree; a newer edit that supersedes the rebuild includes it.</summary>
    private async Task<DocumentModel> EditSourceWorldAsync(DocumentModel doc, string action, Action<SourceWorldEdits> apply, Action<SourceWorldEdits> revert, CancellationToken token)
    {
        var session = doc.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
        if (doc.IsDisposed || session.Owner != doc) throw new StudioCommandException("stale_document", "The world was rebuilt; read zstudio_state for its current document.");
        RequireNoDrafts(doc);
        try { apply(session.Edits); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        UpdateDocumentCommands();
        try { return await RebuildSourceWorldAsync(session, token); }
        catch (StudioCommandException ex) when (ex.Code != "context_changed" && !session.IsDisposed)
        {
            revert(session.Edits); UpdateDocumentCommands();
            throw new StudioCommandException(ex.Code, ex.Code == "build_failed" ? $"{action} was reverted because the world does not build with it: {ex.Message}" : $"{action} was reverted: {ex.Message}");
        }
    }
    private Task<DocumentModel> AddSourceModelAsync(DocumentModel doc, SourceWorldAddition addition, CancellationToken token) =>
        EditSourceWorldAsync(doc, $"Adding {addition.Model.Name}", edits => edits.Add(addition, token), edits => edits.Retract(), token);
    private Task<DocumentModel> UndoSourceWorldAsync(DocumentModel doc, bool redo, CancellationToken token)
    {
        var edits = doc.SourceWorld?.Edits ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
        if (redo ? !edits.CanRedo : !edits.CanUndo) throw new StudioCommandException("unsupported", redo ? "Nothing to redo." : "Nothing to undo.");
        return EditSourceWorldAsync(doc, redo ? "Redo" : "Undo", e => { if (redo) e.Redo(); else e.Undo(); }, e => { if (redo) e.Undo(); else e.Redo(); }, token);
    }

    /// <summary>Rebuilds from the project on disk. Pending edits are kept unless the files they change were changed on disk.</summary>
    private async Task<DocumentModel> ReloadSourceWorldAsync(DocumentModel doc, bool discardAccepted, CancellationToken token)
    {
        var session = doc.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
        if (doc.IsDisposed || session.Owner != doc) throw new StudioCommandException("stale_document", "The world was rebuilt; read zstudio_state for its current document.");
        RequireNoDrafts(doc);
        if (!session.Edits.HasExternalChanges()) return await RebuildSourceWorldAsync(session, token);
        if (session.Edits.IsDirty && !discardAccepted)
            throw new StudioCommandException("unsaved_changes", $"{session.Edits.ScriptPath} or {session.Edits.DefinitionsPath} changed on disk, so the pending edits cannot be kept. Close the world with discard, or undo them, then reload.");
        // The files the edits change were replaced: start again from them.
        SourceWorldSession fresh;
        try { fresh = await Task.Run(() => new SourceWorldSession(session.Root, session.Mission), token); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        try
        {
            var (world, build) = await BuildSourceWorldAsync(fresh, token);
            if (doc.IsDisposed || !ViewModel.Documents.Contains(doc) || session.Owner != doc) throw new StudioCommandException("context_changed", "The world was closed or rebuilt while it was reloading.");
            RequireNoDrafts(doc);
            var replacement = new DocumentModel(world, fresh, build);
            ReportSourceBuild(fresh, build);
            ViewModel.ReplaceDocument(doc, replacement);
            return replacement;
        }
        catch { if (fresh.Owner == null) fresh.Dispose(); throw; }
    }

    private Task<IReadOnlyList<string>> SaveSourceWorldAsync(DocumentModel doc, CancellationToken token)
    {
        var session = doc.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
        RequireNoDrafts(doc);
        IReadOnlyList<string> written;
        // Two small text files, written on the UI thread so the document's change notifications stay on it.
        try { token.ThrowIfCancellationRequested(); written = session.Edits.Save(token); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        ViewModel.Status = written.Count == 0 ? $"The {session.Mission} sources already match" : $"Saved {string.Join(", ", written)}";
        UpdateDocumentCommands();
        return Task.FromResult(written);
    }
    private async Task<bool> SaveSourceWorldDocumentAsync(DocumentModel doc)
    {
        try { await SaveSourceWorldAsync(doc, CancellationToken.None); return true; }
        catch (StudioCommandException ex) { Report(ex); return false; }
    }

    /// <summary>The GUI's Add model: choose a model, name, placement and animations, then rebuild.</summary>
    private async Task AddSourceModelInteractiveAsync()
    {
        if (ViewModel.SelectedDocument is not { SourceWorld: { } session } doc) throw new StudioCommandException("unsupported", "Open a mission world of the source project first (Tools → Open mission world).");
        var models = await Task.Run(() => SourceWorlds.Models(session.Root));
        HashSet<string> names = new(doc.PreviewDocument.Scene?.Nodes.Select(n => n.Name) ?? [], StringComparer.Ordinal);
        Vector3 position = Vector3.Zero;
        if (shownDocument == doc && scene != null && HasPublishedStaticScene)
            try { var pivot = scene.CaptureView().OrbitPivot; if (pivot is { } p) position = new((float)p.X, (float)p.Y, (float)p.Z); } catch (InvalidOperationException) { }
        var overlay = session.Edits.Overlay();
        SourceModelDialog dialog = new(this, session.Mission, models, names, position,
            (root, token) => Task.Run(() => SourceWorlds.DefinitionsFor(session.Root, session.Mission, root, overlay, token), token),
            addition => { try { SourceWorlds.Validate(session.Root, addition); return null; } catch (Exception ex) when (ex is InvalidDataException or IOException) { return ex.Message; } });
        if (dialog.ShowDialog() != true || dialog.Result is not { } result || doc.IsDisposed) return;
        await AddSourceModelAsync(session.Owner ?? doc, result, CancellationToken.None);
    }
    private async void AddSourceModelClick(object sender, RoutedEventArgs e) => await RunUi(AddSourceModelInteractiveAsync);

    /// <summary>Presentation only: lists the project's worlds off the UI thread; a newer menu opening or root supersedes it.</summary>
    private async Task FillSourceWorldMenuAsync(string root)
    {
        long generation = ++sourceWorldMenuGeneration;
        SourceWorldMenu.Items.Clear();
        SourceWorldMenu.Items.Add(new MenuItem { Header = "Reading source project…", IsEnabled = false });
        IReadOnlyList<string>? missions = null; string? error = null;
        try { missions = await Task.Run(() => SourceWorlds.Missions(root)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { error = ex.Message; }
        if (generation != sourceWorldMenuGeneration || SourceProjectRoot != root) return;
        SourceWorldMenu.Items.Clear();
        if (missions == null || missions.Count == 0) { SourceWorldMenu.Items.Add(new MenuItem { Header = new TextBlock { Text = error ?? "No world scripts (gamegen/mN.gs) with models" }, IsEnabled = false }); return; }
        foreach (string mission in missions)
        {
            MenuItem item = new() { Header = new TextBlock { Text = mission }, ToolTip = $"Build the {mission} world from gamegen/{mission}.gs and the project's models" };
            System.Windows.Automation.AutomationProperties.SetName(item, mission);
            item.Click += async (_, _) => await RunUi(() => OpenSourceWorldAsync(mission, CancellationToken.None));
            SourceWorldMenu.Items.Add(item);
        }
    }

    private static object? SourceWorldState(DocumentModel d) => d.SourceWorld is not { } world ? null : new
    {
        mission = world.Mission, project = world.Root, script = world.Edits.ScriptPath, definitions = world.Edits.HasDefinitions ? world.Edits.DefinitionsPath : null,
        world.Edits.CanUndo, world.Edits.CanRedo, current = world.Owner == d,
        additionCount = world.Edits.Additions.Count,
        additions = world.Edits.Additions.Take(256).Select(a => new
        {
            model = a.Model.Model, name = a.Model.Name, placed = a.Model.Position != null,
            position = a.Model.Position is { } p ? new { p.X, p.Y, p.Z } : null, heading = a.Model.Heading, definitionFiles = a.DefinitionFiles
        }).ToArray(),
        outputs = d.SourceBuild?.Outputs.Select(o => new { path = o.Path, status = o.Status, warningCount = o.Warnings.Count, error = o.Error == null ? null : Bounded(o.Error, 512) }).ToArray()
    };

    private void RegisterSourceWorldCommands(StudioCommands r)
    {
        RegisterJob(r, "source_world_open", "Open (or activate) a mission world of the open source project as its build script assembles it from the project's glTF models, textures and animation definitions. The world is built privately, as the export builds it, and shown read-only in Whole world; edit it with zstudio_source_world_add_model, undo_redo and save_document, which change only the project's sources. Build problems are listed in problems.",
            [P("mission", "string", "Mission folder, for example m1 (see zstudio_source_status: outputs of family world).", true)], true,
            async (a, token) => { var doc = await OpenSourceWorldAsync(Text(a, "mission"), token); return Result(new { document = DocumentState(doc) }); });
        Register(r, "source_world_models", "List the glTF models of the open source project that a world script can load (every .gltf/.glb under data with a loadable name), paged and filtered by path.", false, [.. PageParameters], async (a, token) =>
        {
            string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
            IReadOnlyList<SourceModelChoice> models;
            try { models = await Task.Run(() => SourceWorlds.Models(root), token); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
            return Page(models, a, m => m.Path, m => new { path = m.Path, folder = m.Folder, name = m.Name });
        });
        Register(r, "source_world_definitions", "List animation definition files that other missions list with an animation for a root name and this source world does not list yet, such as an enemy's destruction animations.", false,
            [DocumentParameter, P("name", "string", "Root node name, as the model's node would be named.", true)], async (a, token) =>
        {
            var d = TargetDocument(a); var world = d.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
            string name = Text(a, "name");
            if (name.Length is 0 or > SourceWorlds.MaximumNameLength) throw new StudioCommandException("invalid_argument", $"Names have 1–{SourceWorlds.MaximumNameLength} characters.");
            var overlay = world.Edits.Overlay(token);
            IReadOnlyList<SourceDefinitionFile> files;
            try { files = await Task.Run(() => SourceWorlds.DefinitionsFor(world.Root, world.Mission, name, overlay, token), token); }
            catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
            return Result(new { name, files = files.Take(64).Select(f => new { path = f.Path, animations = f.Animations.Take(32).Select(n => Bounded(n, 128)).ToArray(), animationCount = f.Animations.Count, missions = f.Missions }).ToArray(), fileCount = files.Count, truncated = files.Count > 64 });
        });
        RegisterJob(r, "source_world_add_model", "Add a project model to a source world as one undoable edit: its folder's SetModelDirectory and a LoadGameGen line go before the line of gamegen/mN.gs that writes the world, with Object3DTranslate/Object3DRotate and AddChild under the world when placed. Without position the model is an unplaced root that resources such as aiv.zrd place copies of by name. Definition files are appended to data/mN/zrdr/anim.zrd. The world rebuilds (an edit it cannot build with is reverted) and the result is the replacement document; nothing is written until save_document.",
            [DocumentParameter, RevisionParameter, P("model", "string", "Project path of the glTF model, as zstudio_source_world_models lists it (for example data/m2/models/bft/ltank.gltf).", true),
             P("name", "string", "Node name: 1–31 letters, digits, '_', '-' or '.'. Resources and animations find the model by it.", true),
             new("position", "object", "Optional world position; omit for an unplaced root.", Properties: [P("x", "number", "World X.", true), P("y", "number", "World Y.", true), P("z", "number", "World Z.", true)]),
             P("heading", "number", "Rotation about Y in degrees (−360 to 360) for a placed model; default 0."),
             new("definitionFiles", "array", "Definition files to list, as zstudio_source_world_definitions returns them; omit to list all it returns, or pass [] to list none.", Items: new("", "string", "Project path of a definition file."), MaxItems: 64)], true,
            async (a, token) =>
            {
                var d = TargetDocument(a, true); var world = d.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
                Vector3? position = a["position"] is JsonObject p ? new(Coordinate(p, "x"), Coordinate(p, "y"), Coordinate(p, "z")) : null;
                float heading = a["heading"] == null ? 0 : Coordinate(a, "heading");
                SourceModelAddition model = new(Text(a, "model").Replace('\\', '/'), Text(a, "name"), position, heading);
                try { SourceWorlds.Validate(world.Root, model); }
                catch (Exception ex) when (ex is InvalidDataException or IOException) { throw new StudioCommandException("invalid_argument", ex.Message); }
                IReadOnlyList<string> files;
                if (a["definitionFiles"] is JsonArray list)
                    files = list.Select(f => f is JsonValue v && v.TryGetValue<string>(out var path) && path.Length is > 0 and <= 260 ? path.Replace('\\', '/') : throw new StudioCommandException("invalid_argument", "Definition files are project paths.")).ToArray();
                else
                {
                    var overlay = world.Edits.Overlay(token);
                    try { files = (await Task.Run(() => SourceWorlds.DefinitionsFor(world.Root, world.Mission, model.Name, overlay, token), token)).Select(f => f.Path).ToArray(); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { throw new StudioCommandException("invalid_argument", ex.Message); }
                }
                bool duplicate = d.PreviewDocument.Scene?.Nodes.Any(n => n.Name == model.Name) == true;
                var next = await AddSourceModelAsync(d, new(model, files), token);
                return Result(new { document = DocumentState(next), definitionFiles = files, duplicateName = duplicate });
            });
    }
    private static float Coordinate(JsonObject a, string name) => a[name] is JsonValue value && value.TryGetValue<double>(out double number) && double.IsFinite(number) && Math.Abs(number) <= SourceWorlds.MaximumCoordinate
        ? (float)number : throw new StudioCommandException("invalid_argument", $"{name} must be a finite number within ±{SourceWorlds.MaximumCoordinate:N0}.");
}
