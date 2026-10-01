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
/// from the project's models, and edited by changing those sources. Every world of the project edits one
/// <see cref="SourceWorkspace"/>: one history, one save. Each edit rebuilds a private copy of the mission and replaces the
/// shown document, keeping the camera.
/// </summary>
public partial class MainWindow
{
    private Task sourceWorldWork = Task.CompletedTask;
    private (DocumentModel Document, SceneViewport.ViewPose View)? pendingSourceView;
    private long sourceWorldMenuGeneration;
    private SourceWorkspace? sourceWorkspace;
    /// <summary>A world of the open project is rebuilding after an edit, undo, redo or reload; the project's other changes wait for it.</summary>
    private bool sourceWorkspaceBusy;
    private static string SourceWorldProblemFile(SourceWorldSession session) => Path.Combine(session.Root, SourceProject.GameGenFolder, session.Mission + ".gs");

    /// <summary>The workspace of the source project at <paramref name="root"/>; every open world of the project shares it.</summary>
    private SourceWorkspace SourceWorkspaceFor(string root)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // A Discard whose release waited (for a rebuild or an opening) is carried out before a new world uses the workspace.
        if (sourceWorkspace != null && discardApprovedWorkspace is { } pending && pending.Workspace == sourceWorkspace && pending.Revision == sourceWorkspace.Revision
            && !sourceWorkspace.IsSaving && !sourceWorkspaceBusy && sourceWorldsOpening == 0 && !ViewModel.Documents.Any(d => d.SourceWorld?.Workspace == sourceWorkspace))
        { sourceWorkspace.Discard(); discardApprovedWorkspace = null; }
        else if (sourceWorkspace != null && discardApprovedWorkspace is { } waiting && waiting.Workspace == sourceWorkspace && waiting.Revision == sourceWorkspace.Revision
            && sourceWorkspace.Root.Equals(full, StringComparison.OrdinalIgnoreCase)
            && (sourceWorldsOpening > 0 || sourceWorkspaceBusy) && !ViewModel.Documents.Any(d => d.SourceWorld?.Workspace == sourceWorkspace))
            throw new StudioCommandException("busy", "The source project's discarded edits are still being dropped (a world was opening or rebuilding); try again in a moment.");
        if (sourceWorkspace?.Root.Equals(full, StringComparison.OrdinalIgnoreCase) == true) return sourceWorkspace;
        // Changing roots closes every document first, so a previous project's workspace has no world left to lose edits of.
        if (sourceWorkspace != null && ViewModel.Documents.Any(d => d.SourceWorld?.Workspace == sourceWorkspace))
            throw new StudioCommandException("busy", "Close the other source project's worlds first.");
        try { sourceWorkspace = new SourceWorkspace(full); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        var workspace = sourceWorkspace;
        // A file an open resource or content editor holds unsaved changes of cannot also change in the workspace.
        workspace.EditGuard = relative =>
        {
            string path = SourceProject.Resolve(workspace.Root, relative);
            return ViewModel.Documents.FirstOrDefault(d => d.SourceWorld == null && !d.IsDisposed && d.IsDirty && Path.GetFullPath(d.Path).Equals(path, StringComparison.OrdinalIgnoreCase)) is { } open
                ? $"{relative} is open with unsaved edits ({open.Title.TrimEnd(' ', '*')}); save or close it first." : null;
        };
        return sourceWorkspace;
    }
    /// <summary>When no world of the project is open any more, its unsaved edits go with the last one (its close was confirmed).</summary>
    private void ReleaseUnusedSourceWorkspace()
    {
        if (sourceWorkspace == null || sourceWorkspaceBusy || sourceWorldsOpening > 0 || ViewModel.Documents.Any(d => d.SourceWorld?.Workspace == sourceWorkspace)) return;
        if (sourceWorkspace.IsDirty)
        {
            // Unsaved edits go only with an explicit Discard of this workspace at its current state; otherwise they stay
            // for the project's next world.
            if (sourceWorkspace.IsSaving || discardApprovedWorkspace is not { } approved || approved.Workspace != sourceWorkspace || approved.Revision != sourceWorkspace.Revision) return;
            sourceWorkspace.Discard();
        }
        sourceWorkspace = null; discardApprovedWorkspace = null;
    }
    /// <summary>
    /// A world of the project is opening or rebuilding: closing the last open world now could not carry out its decision
    /// (the opening would show the edits; a failed rebuild's take-back would change what Discard approved).
    /// </summary>
    private bool SourceWorldPending(DocumentModel doc) => doc.SourceWorld != null && (sourceWorldsOpening > 0 || sourceWorkspaceBusy);
    /// <summary>
    /// A Discard approval belongs to the close that asked for it: when a world of its workspace is still open, the close
    /// did not happen (cancelled, superseded), and a later close asks again.
    /// </summary>
    private void ForgetStaleDiscardApproval()
    {
        if (discardApprovedWorkspace is { } approved && ViewModel.Documents.Any(d => !d.IsDisposed && d.SourceWorld?.Workspace == approved.Workspace)) discardApprovedWorkspace = null;
    }
    /// <summary>The document a source world shows now: a draft commit or another edit may have rebuilt it.</summary>
    private DocumentModel LiveDocument(DocumentModel doc) =>
        doc.IsDisposed && doc.SourceWorld?.Owner is { IsDisposed: false } owner && ViewModel.Documents.Contains(owner) ? owner : doc;
    /// <summary>Whether another open world of the same project keeps its edits when <paramref name="doc"/> closes.</summary>
    private bool OtherSourceWorldOpen(DocumentModel doc) => doc.SourceWorld is { } world &&
        ViewModel.Documents.Any(d => d != doc && !d.IsDisposed && d.SourceWorld is { IsDisposed: false } other && other != world && other.Workspace == world.Workspace);

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
        var project = SourceWorkspaceFor(root);
        // Until the world's document exists, the opening holds the workspace: closing another document must not release it.
        sourceWorldsOpening++;
        SourceWorldSession session;
        try { session = await Task.Run(() => new SourceWorldSession(project, mission), token); }
        catch (InvalidDataException ex) { sourceWorldsOpening--; ReleaseUnusedSourceWorkspace(); throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sourceWorldsOpening--; ReleaseUnusedSourceWorkspace(); throw new StudioCommandException("io_failed", ex.Message); }
        catch { sourceWorldsOpening--; ReleaseUnusedSourceWorkspace(); throw; }
        DocumentModel? doc = null;
        try
        {
            var built = await BuildSourceWorldAsync(session, token);
            if (ViewModel.WorkspaceGeneration != workspace || SourceProjectRoot != root) throw new StudioCommandException("context_changed", "The workspace changed while the world was building.");
            if (OpenSourceWorld(root, mission) is { } other) { session.Dispose(); SourceWorldSession.DeleteBuild(built.Build.Folder); ViewModel.SelectedDocument = other; return other; }
            doc = new DocumentModel(built.World, session, built.Build, built.Revision);
            ReportSourceBuild(session, built.Build);
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
        finally { sourceWorldsOpening--; ReleaseUnusedSourceWorkspace(); }
    }
    private int sourceWorldsOpening;
    private TaskCompletionSource? sourceRebuild;
    /// <summary>
    /// Marks the project rebuilding a world after an edit or reload (or done): commands, Properties input for source worlds
    /// (typing then would count as unfinished input and take the edit back) and waiters follow it.
    /// </summary>
    private void SetSourceRebuilding(SourceWorldSession session, bool rebuilding)
    {
        session.IsRebuilding = sourceWorkspaceBusy = rebuilding;
        if (rebuilding) sourceRebuild ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        else { var done = sourceRebuild; sourceRebuild = null; done?.TrySetResult(); }
        UpdateDocumentCommands();
        ApplyTerrainBrush(scene);
    }
    /// <summary>Completes when no world of the project rebuilds (after a scene-card edit, undo, stroke, Properties or MCP edit).</summary>
    private async Task SourceWorldsIdleAsync()
    {
        await sourceWorldWork;
        while (sourceRebuild is { } rebuilding) await rebuilding.Task;
    }
    /// <summary>Cancels the builds of every open source world (a second request to close the application while it waits).</summary>
    private void CancelSourceBuilds()
    {
        foreach (var world in ViewModel.Documents.Select(d => d.SourceWorld).OfType<SourceWorldSession>().Distinct()) world.Building?.Cancel();
    }
    /// <summary>Properties of a source world takes no input while the project rebuilds.</summary>
    private void UpdateSourceInputBlock()
    {
        if (propertiesWindow == null) return;
        bool blocked = sourceWorkspaceBusy && propertiesWindow.Document?.SourceWorld != null;
        propertiesWindow.InputBlocked = blocked;
        // Input typed before the block stayed as it was (a blocked field does not commit); say so once it can be applied.
        if (!blocked && propertiesWindow.Document?.SourceWorld != null && propertiesWindow.HasUncommittedDrafts)
            ViewModel.Status = "Properties has unfinished input; press Enter in the field to apply it, or Escape to restore it.";
    }

    private sealed record SourceWorldBuilt(ZbdDocument World, SourceWorldBuild Build, long Revision);
    /// <summary>
    /// Builds the workspace's current sources of the session's mission into a new private folder; a newer request supersedes
    /// this one. <paramref name="additions"/> are models just added, which the build checks it loaded.
    /// </summary>
    private async Task<SourceWorldBuilt> BuildSourceWorldAsync(SourceWorldSession session, CancellationToken token, IReadOnlyList<SourceModelAddition>? additions = null)
    {
        // A world opened now would build with another world's edit before its rebuild verified it; a failed rebuild takes the
        // edit back and treats builds made before it as current.
        if (sourceWorkspaceBusy && !session.IsRebuilding)
            throw new StudioCommandException("busy", "A world of this source project is rebuilding after an edit; open this world when it is shown.");
        session.Building?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ViewModel.WorkspaceToken, shutdownToken);
        session.Building = cancellation;
        string folder = session.NextFolder(); bool built = false;
        try
        {
            // The overlay and its revision are read together on the UI thread, where the workspace changes.
            long revision = session.Workspace.ContentRevision;
            var overlay = session.Workspace.Overlay();
            var progress = new Progress<SourceProgress>(p => { if (session.Building == cancellation && !cancellation.IsCancellationRequested) ViewModel.Status = $"Building the {session.Mission} world {p.Completed}/{p.Total}: {p.Item}"; });
            var build = await Task.Run(() => SourceWorlds.BuildPreviewAsync(session.Root, session.Mission, folder, overlay, progress, cancellation.Token, additions), cancellation.Token);
            var world = await Task.Run(() => FormatRegistry.Default.OpenAsync(build.WorldPath, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (session.IsDisposed || session.Building != cancellation) throw new OperationCanceledException(cancellation.Token);
            if (world.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException("The built world does not reopen: " + error.Message);
            built = true;
            return new(world, build, revision);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new StudioCommandException("context_changed", "The world's closing or a workspace change superseded this build."); }
        catch (InvalidDataException ex) { throw new StudioCommandException("build_failed", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        // Malformed project sources (a glTF that is not JSON, for example) can fail the build in other ways.
        catch (Exception ex) when (ex is not (OperationCanceledException or StudioCommandException or OutOfMemoryException or StackOverflowException))
        { throw new StudioCommandException("build_failed", ex.Message); }
        finally
        {
            if (session.Building == cancellation) session.Building = null;
            if (!built) SourceWorldSession.DeleteBuild(folder);
        }
    }

    /// <summary>
    /// What an addition broke: an output that built before and fails now, or warnings that the game rejects a file which
    /// the previous build did not have (a listed definition file binding names this world gives other nodes, for example).
    /// </summary>
    private static string? NewRejections(SourceWorldBuild previous, SourceWorldBuild next)
    {
        // Output by output: one the game already rejected (or that failed) gains nothing from another warning.
        List<string> broken = [];
        foreach (var output in next.Outputs)
        {
            if (previous.Outputs.FirstOrDefault(p => p.Path == output.Path) is not { Error: null } before) continue;
            if (output.Error != null) { broken.Add($"{output.Path} no longer builds ({output.Error})"); continue; }
            if (before.Warnings.Any(Rejects)) continue;
            if (output.Warnings.FirstOrDefault(Rejects) is { } warning) broken.Add($"{output.Path}: {warning}");
        }
        if (broken.Count == 0) return null;
        return Bounded("The addition would break the mission's files: " + string.Join("; ", broken.Take(4)) + (broken.Count > 4 ? $" and {broken.Count - 4} more" : "")
            + ". For a model added with animation definitions, list only those it needs; for a copy, choose a name no animation definition binds.");
        static bool Rejects(string warning) => warning.Contains("the game rejects", StringComparison.Ordinal);
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
    private async Task<DocumentModel> RebuildSourceWorldAsync(SourceWorldSession session, CancellationToken token, IReadOnlyList<SourceModelAddition>? additions = null)
    {
        var built = await BuildSourceWorldAsync(session, token, additions);
        var current = session.Owner;
        if (session.IsDisposed || current == null || current.IsDisposed || !ViewModel.Documents.Contains(current))
        { SourceWorldSession.DeleteBuild(built.Build.Folder); throw new StudioCommandException("context_changed", "The world was closed while it was building."); }
        try
        {
            RequireNoDrafts(current, committing: true);
            if (additions is { Count: > 0 } && current.SourceBuild is { } previous && NewRejections(previous, built.Build) is { } rejection)
                throw new StudioCommandException("build_failed", rejection);
        }
        catch { SourceWorldSession.DeleteBuild(built.Build.Folder); throw; }
        SceneViewport.ViewPose? view = null;
        if (shownDocument == current && scene != null && HasPublishedStaticScene)
            try { view = scene.CaptureView(); } catch (InvalidOperationException) { }
        var replacement = new DocumentModel(built.World, session, built.Build, built.Revision) { PickupsLocked = current.PickupsLocked };
        ReportSourceBuild(session, built.Build);
        pendingSourceView = view == null ? null : (replacement, view);
        ViewModel.ReplaceDocument(current, replacement);
        ViewModel.Status = $"Rebuilt the {session.Mission} world from its sources";
        return replacement;
    }

    /// <summary>Refuses a second change while a world of the project is rebuilding (see <see cref="SourceWorldSession.IsRebuilding"/>).</summary>
    private void RequireSourceWorldIdle(SourceWorldSession session)
    {
        if (sourceWorkspaceBusy || session.IsRebuilding) throw new StudioCommandException("busy", $"A world of this source project is rebuilding after an edit or reload; try again when the {session.Mission} world is shown.");
        if (session.Workspace.IsSaving) throw new StudioCommandException("busy", "The source project is being saved; try again when it finishes.");
    }

    /// <summary>
    /// Applies one change to the project's workspace and rebuilds this world. <paramref name="apply"/> returns how to take the
    /// change back, or null when it changed nothing. Until the rebuilt world replaces the document, the project's other edits,
    /// saves and reloads are refused; a change whose rebuild fails or is canceled is taken back, so the shown world and the
    /// workspace agree.
    /// </summary>
    /// <remarks>
    /// <paramref name="additions"/> is read after <paramref name="apply"/> runs, so a plan can list the models it adds.
    /// Edits planned from this world's build (<paramref name="fromBuild"/>: provenance, line numbers, archive layouts) are
    /// refused once a source the build read changed in the workspace, until the world is reloaded.
    /// </remarks>
    private async Task<DocumentModel> EditSourceWorldAsync(DocumentModel doc, string action, Func<SourceWorkspace, Action?> apply, CancellationToken token, IReadOnlyList<SourceModelAddition>? additions = null, bool fromBuild = true)
    {
        var session = doc.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
        if (doc.IsDisposed || session.Owner != doc) throw new StudioCommandException("stale_document", "The world was rebuilt; read zstudio_state for its current document.");
        RequireSourceWorldIdle(session);
        // A Properties commit runs this edit; its own draft is being resolved (MCP writes checked every draft before).
        RequireNoDrafts(doc, committing: true);
        // Plans from the build (node and line numbers, archive layouts) hold only while every source it read is unchanged,
        // in the workspace and on disk.
        if (fromBuild && doc.SourceInputsChanged())
            throw new StudioCommandException("stale_document", "Sources this world was built from changed since (an edit in another world, an undo, or another program); reload the world before editing it.");
        long contentBefore = session.Workspace.ContentRevision;
        Action? revert;
        try { revert = apply(session.Workspace); }
        catch (SourceFileChangedException ex) { throw new StudioCommandException("external_change", ex.Message); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (NotSupportedException ex) { throw new StudioCommandException("unsupported", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        if (revert == null) return doc;
        try
        {
            SetSourceRebuilding(session, true);
            return await RebuildSourceWorldAsync(session, token, additions);
        }
        // The rebuilt world was never shown (failed, canceled, or its world closed meanwhile): the edit is taken back, so no
        // other world keeps an edit nothing was built with, and worlds built before it are current again.
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            bool discardApproved = discardApprovedWorkspace is { } approved && approved.Workspace == session.Workspace && approved.Revision == session.Workspace.Revision;
            try { revert(); }
            catch (Exception undo) when (undo is InvalidDataException or SourceFileChangedException or IOException or InvalidOperationException or NotSupportedException)
            {
                throw new StudioCommandException("build_failed", $"{action} did not finish ({(ex as StudioCommandException)?.Message ?? ex.Message}) and could not be taken back: {undo.Message} Reload the world before continuing.");
            }
            // The take-back changes the revision; a Discard chosen meanwhile still covers the workspace.
            finally { if (discardApproved) discardApprovedWorkspace = (session.Workspace, session.Workspace.Revision); }
            session.Workspace.ForgetChangesAfter(contentBefore);
            if (ex is StudioCommandException { Code: not "context_changed" } failure)
                throw new StudioCommandException(failure.Code, failure.Code == "build_failed" ? $"{action} was reverted because the world does not build with it: {failure.Message}" : $"{action} was reverted: {failure.Message}");
            throw;
        }
        finally { SetSourceRebuilding(session, false); MarkStaleSourceWorlds(); ReleaseUnusedSourceWorkspace(); }
    }
    private Task<DocumentModel> AddSourceModelAsync(DocumentModel doc, SourceWorldAddition addition, CancellationToken token)
    {
        var session = doc.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
        string mission = session.Mission;
        if (doc.IsDisposed || session.Owner != doc) throw new StudioCommandException("stale_document", "The world was rebuilt; read zstudio_state for its current document.");
        RequireSourceWorldIdle(session);
        // The rebuild is checked against the shown build; a stale one would blame the addition for others' changes.
        if (doc.SourceInputsChanged()) throw new StudioCommandException("stale_document", "Sources this world was built from changed since; reload the world before adding a model.");
        return EditSourceWorldAsync(doc, $"Adding {addition.Model.Name}", workspace => SourceWorlds.AddModel(workspace, mission, addition, token) is var t ? () => workspace.Retract(t) : null, token, [addition.Model], fromBuild: false);
    }
    private Task<DocumentModel> UndoSourceWorldAsync(DocumentModel doc, bool redo, CancellationToken token)
    {
        var workspace = doc.SourceWorld?.Workspace ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
        if (redo ? !workspace.CanRedo : !workspace.CanUndo) throw new StudioCommandException("unsupported", redo ? "Nothing to redo." : "Nothing to undo.");
        return EditSourceWorldAsync(doc, redo ? "Redo" : "Undo", w =>
        {
            if (redo) { w.Redo(); return () => w.Undo(); }
            w.Undo(); return () => w.Redo();
        }, token, fromBuild: false);
    }

    /// <summary>
    /// A placement edit of a source world (a pickup, AI vehicle or AI node, with its linked difficulty counterparts) becomes
    /// one change of the resource sources the world's archives were built from; only the edited coordinate tokens change.
    /// </summary>
    private Task<DocumentModel> MoveSourcePlacementAsync(DocumentModel doc, MissionPickupSource source, PlacementTransform transform, CancellationToken token) =>
        ApplySourcePlacementAsync(doc, PlanSourcePlacement(doc, source, transform, token), token);
    /// <summary>
    /// The source changes a placement move makes, checked against other editors (a resource editor holding a file
    /// unsaved); a refusal comes before anything changes, so the scene card can keep its draft.
    /// </summary>
    private (string Label, List<(string, byte[]?)> Changes) PlanSourcePlacement(DocumentModel doc, MissionPickupSource source, PlacementTransform transform, CancellationToken token)
    {
        var edits = doc.PickupEdits ?? throw new StudioCommandException("not_ready", "Load the world's placements first.");
        var workspace = SourceWorldOf(doc).Workspace;
        string label = edits.Find(source) is { } pickup ? $"Move {pickup.Type}" : edits.Coordinate(source) is { } record ? $"Move {record.Name}" : "Move placement";
        try
        {
            var after = edits.PreviewTransform(source, transform);
            List<(string, byte[]?)> changes = [];
            foreach (var archive in edits.ScalarWrites(after).GroupBy(w => w.ArchivePath, StringComparer.OrdinalIgnoreCase))
                changes.AddRange(SourceResourceEdits.SourceChanges(edits.ArchiveBytes(archive.Key), archive.Select(w => new SourceResourceEdits.ScalarEdit(w.Offset, SourceResourceEdits.Float(w.Value))),
                    relative => workspace.Read(relative, token), token).Select(c => (c.Relative, (byte[]?)c.Content)));
            foreach (var (relative, _) in changes)
                if (workspace.EditGuard?.Invoke(relative) is { } refusal) throw new StudioCommandException("unsaved_changes", refusal);
            return (label, changes);
        }
        catch (SourceFileChangedException ex) { throw new StudioCommandException("external_change", ex.Message); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
    }
    private Task<DocumentModel> ApplySourcePlacementAsync(DocumentModel doc, (string Label, List<(string, byte[]?)> Changes) plan, CancellationToken token) =>
        EditSourceWorldAsync(doc, plan.Label, workspace => workspace.Apply(plan.Label, plan.Changes, token) is { } t ? () => workspace.Retract(t) : null, token);

    /// <summary>For a source world, how Properties moves a placement: through its sources, like the scene card; null otherwise.</summary>
    private Func<MissionPickupSource, System.Numerics.Vector3, Task>? SourcePickupMove(DocumentModel doc) => doc.SourceWorld == null ? null : async (source, position) =>
    {
        var edits = doc.PickupEdits ?? throw new StudioCommandException("not_ready", "Load the world's placements first.");
        await MoveSourcePlacementAsync(doc, source, edits.Transform(source) with { Position = position }, CancellationToken.None);
    };

    /// <summary>Rebuilds from the project. Workspace edits are kept unless a file they change was changed on disk.</summary>
    private async Task<DocumentModel> ReloadSourceWorldAsync(DocumentModel doc, bool discardAccepted, CancellationToken token)
    {
        var session = doc.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
        if (doc.IsDisposed || session.Owner != doc) throw new StudioCommandException("stale_document", "The world was rebuilt; read zstudio_state for its current document.");
        RequireSourceWorldIdle(session);
        RequireNoDrafts(doc);
        var workspace = session.Workspace;
        var changed = workspace.ExternalChanges();
        if (changed.Any(workspace.IsFileDirty))
        {
            if (!discardAccepted) throw new StudioCommandException("unsaved_changes", $"{string.Join(", ", changed.Where(workspace.IsFileDirty))} changed on disk, so the project's unsaved edits cannot be kept. Close the project's worlds with discard, or undo the edits, then reload.");
            if (OtherSourceWorldOpen(doc)) throw new StudioCommandException("unsaved_changes", "Other worlds of this source project are open with its unsaved edits; close them before discarding.");
            workspace.Discard();
        }
        else
            try { workspace.Reload(); }
            catch (SourceFileChangedException ex) { throw new StudioCommandException("unsaved_changes", ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        try
        {
            SetSourceRebuilding(session, true);
            return await RebuildSourceWorldAsync(session, token);
        }
        finally { SetSourceRebuilding(session, false); MarkStaleSourceWorlds(); ReleaseUnusedSourceWorkspace(); }
    }
    /// <summary>Other open worlds whose build read a file the workspace changed since show as stale until reloaded.</summary>
    private void MarkStaleSourceWorlds() { if (ViewModel.Documents.Any(d => d.SourceWorld != null)) ViewModel.CheckExternalChanges(); }

    private Task<IReadOnlyList<string>> SaveSourceWorldAsync(DocumentModel doc, CancellationToken token)
    {
        var session = doc.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
        // A rebuilding edit is not verified yet: saving it could write sources the world cannot be built with.
        RequireSourceWorldIdle(session);
        RequireNoDrafts(doc);
        IReadOnlyList<string> written;
        // Source text files, written on the UI thread so the documents' change notifications stay on it.
        try { token.ThrowIfCancellationRequested(); written = session.Workspace.Save(token); }
        catch (SourceConflictException ex) { throw new StudioCommandException("external_change", $"{ex.Message} Nothing was saved; reload the world to continue from the files on disk."); }
        catch (SourceRecoveryRequiredException ex) { ViewModel.AddProblem(ex.Message, "Error", session.Root); throw new StudioCommandException("recovery_required", ex.Message); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        ViewModel.Status = written.Count == 0 ? "The source project's files already match" : $"Saved {string.Join(", ", written.Take(8))}{(written.Count > 8 ? $" and {written.Count - 8} more" : "")}";
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
        RequireSourceWorldIdle(session);
        var models = await Task.Run(() => SourceWorlds.Models(session.Root));
        HashSet<string> names = new(doc.PreviewDocument.Scene?.Nodes.Select(n => n.Name) ?? [], StringComparer.Ordinal);
        Vector3 position = Vector3.Zero;
        if (shownDocument == doc && scene != null && HasPublishedStaticScene)
            try { var pivot = scene.CaptureView().OrbitPivot; if (pivot is { } p) position = new((float)p.X, (float)p.Y, (float)p.Z); } catch (InvalidOperationException) { }
        var overlay = session.Workspace.Overlay();
        SourceModelDialog dialog = new(this, session.Mission, models, names, position,
            (root, token) => Task.Run(() => SourceWorlds.DefinitionsFor(session.Root, session.Mission, root, overlay, token), token),
            addition => { try { SourceWorlds.Validate(session.Root, addition); return null; } catch (Exception ex) when (ex is InvalidDataException or IOException) { return ex.Message; } });
        if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;
        // A closed world reports stale_document rather than dropping the choice silently.
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
        mission = world.Mission, project = world.Root, script = world.ScriptPath, definitions = world.Workspace.Exists(world.DefinitionsPath) ? world.DefinitionsPath : null,
        current = world.Owner == d, rebuilding = world.IsRebuilding, builtRevision = d.SourceRevision, workspace = SourceWorkspaceState(world.Workspace),
        outputs = d.SourceBuild?.Outputs.Select(o => new { path = o.Path, status = o.Status, warningCount = o.Warnings.Count, error = o.Error == null ? null : Bounded(o.Error, 512) }).ToArray()
    };
    /// <summary>The project's accepted, unsaved state: what Save would write and what Undo/Redo would change.</summary>
    private static object SourceWorkspaceState(SourceWorkspace workspace)
    {
        var dirty = workspace.DirtyFiles;
        return new
        {
            revision = workspace.Revision, contentRevision = workspace.ContentRevision, saving = workspace.IsSaving,
            canUndo = workspace.CanUndo, canRedo = workspace.CanRedo, undo = workspace.UndoLabel, redo = workspace.RedoLabel,
            dirtyFiles = dirty.Take(64).ToArray(), dirtyFileCount = dirty.Count, dirtyFilesTruncated = dirty.Count > 64,
            history = workspace.History.Select((t, i) => (Step: t, Index: i)).TakeLast(32).Select(h => new { id = h.Step.Id, label = Bounded(h.Step.Label, 128), files = h.Step.Files.Take(16).Select(f => f.Relative).ToArray(), fileCount = h.Step.Files.Count, undone = h.Index >= workspace.UndoCount }).ToArray(),
            historyCount = workspace.History.Count
        };
    }

    private void RegisterSourceWorldCommands(StudioCommands r)
    {
        RegisterJob(r, "source_world_open", "Open (or activate) a mission world of the open source project as its build script assembles it from the project's glTF models, textures, resources and animation definitions, including the project's unsaved edits. The world is built privately, as the export builds it, and shown in Whole world. Edit it with zstudio_source_world_add_model, the placement commands (pickup_lock, pickup_move, scene_card), undo_redo and save_document, which change only the project's sources; every open world of the project shares one edit history and one save. Build problems are listed in problems.",
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
            var overlay = world.Workspace.Overlay();
            IReadOnlyList<SourceDefinitionFile> files;
            try { files = await Task.Run(() => SourceWorlds.DefinitionsFor(world.Root, world.Mission, name, overlay, token), token); }
            catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
            return Result(new { name, files = files.Take(64).Select(f => new { path = f.Path, animations = f.Animations.Take(32).Select(n => Bounded(n, 128)).ToArray(), animationCount = f.Animations.Count, missions = f.Missions }).ToArray(), fileCount = files.Count, truncated = files.Count > 64 });
        });
        RegisterJob(r, "source_world_add_model", "Add a project model to a source world as one undoable change of the project's workspace: its folder's SetModelDirectory and a LoadGameGen line go before the line of gamegen/mN.gs that writes the world, with Object3DTranslate/Object3DRotate and AddChild under the world when placed. Without position the model is an unplaced root that resources such as aiv.zrd place copies of by name. Definition files are appended to data/mN/zrdr/anim.zrd, keeping its comments and layout. The world rebuilds and the result is the replacement document; a change the world cannot be built with, or that makes another mission file fail or makes the game reject one (an animation bound to a node, attachment or effect this world lacks), returns build_failed and is taken back, as is one canceled before the rebuilt world is shown. A stale world returns stale_document until reload_document. While a world of the project rebuilds, its other edits, undo_redo, save_document and reload_document return busy. Nothing is written until save_document.",
            [DocumentParameter, RevisionParameter, P("model", "string", "Project path of the glTF model, as zstudio_source_world_models lists it (for example data/m2/models/bft/ltank.gltf).", true),
             P("name", "string", "Node name: 1–31 letters, digits, '_', '-' or '.'. Resources and animations find the model by it. A placed model's name must not also name a node inside the model, which AddChild would attach instead (build_failed).", true),
             new("position", "object", "Optional world position; omit for an unplaced root.", Properties: [P("x", "number", "World X.", true), P("y", "number", "World Y.", true), P("z", "number", "World Z.", true)]),
             P("heading", "number", "Rotation about Y in degrees (−360 to 360) for a placed model; default 0."),
             new("definitionFiles", "array", "Definition files to list, as zstudio_source_world_definitions returns them; omit to list all it returns (refused above 64), or pass [] to list none.", Items: new("", "string", "Project path of a definition file."), MaxItems: 64)], true,
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
                    var overlay = world.Workspace.Overlay();
                    try { files = (await Task.Run(() => SourceWorlds.DefinitionsFor(world.Root, world.Mission, model.Name, overlay, token), token)).Select(f => f.Path).ToArray(); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { throw new StudioCommandException("invalid_argument", ex.Message); }
                    if (files.Count > 64) throw new StudioCommandException("invalid_argument", $"{files.Count} definition files name {model.Name}; choose them with definitionFiles (zstudio_source_world_definitions lists them).");
                }
                bool duplicate = d.PreviewDocument.Scene?.Nodes.Any(n => n.Name == model.Name) == true;
                var next = await AddSourceModelAsync(d, new(model, files), token);
                return Result(new { document = DocumentState(next), definitionFiles = files, duplicateName = duplicate });
            });
        Register(r, "source_changes", "Describe the open source project's unsaved edits: the files save_document would write, the shared edit history (newest last, with undone steps marked) and, for one file, its working text beside the file on disk as a bounded line diff.", false,
            [P("file", "string", "Optional project path (for example data/m1/zrdr/puppies.zrd) to diff against the disk."), new("maxLines", "integer", "Diff lines to return; default 200.", Minimum: 1, Maximum: 2000)], a =>
        {
            string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
            var workspace = SourceWorkspaceFor(root);
            object? diff = null;
            if (a["file"] != null)
            {
                string file = SourceWorkspace.Normalize(Text(a, "file"));
                try { workspace.CheckEditable(file); }
                catch (Exception ex) when (ex is InvalidDataException or IOException) { throw new StudioCommandException("invalid_argument", ex.Message); }
                diff = SourceDiff.Describe(file, ReadDiskOrNull(root, file), workspace.Read(file), Int(a, "maxLines", 200));
            }
            return Result(new { project = root, workspace = SourceWorkspaceState(workspace), diff });
        });
    }
    private static byte[]? ReadDiskOrNull(string root, string relative)
    {
        string path = SourceProject.Resolve(root, relative);
        if (!File.Exists(path)) return null;
        return new FileInfo(path).Length > SourceProject.MaximumSourceTextBytes ? throw new StudioCommandException("too_large", $"{relative} is too large to diff.") : File.ReadAllBytes(path);
    }
    private static float Coordinate(JsonObject a, string name) => a[name] is JsonValue value && value.TryGetValue<double>(out double number) && double.IsFinite(number) && Math.Abs(number) <= SourceWorlds.MaximumCoordinate
        ? (float)number : throw new StudioCommandException("invalid_argument", $"{name} must be a finite number within ±{SourceWorlds.MaximumCoordinate:N0}.");
}
