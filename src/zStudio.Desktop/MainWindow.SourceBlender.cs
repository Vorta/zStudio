using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// The Blender round trip of a source project: Edit in Blender copies a model into the project's zstudio/export folder;
/// Update from Blender export reads what Blender exported there and changes the project's sources as one undoable edit,
/// rebuilding the world. Nothing Blender writes is used until the user asks for the update.
/// </summary>
public partial class MainWindow
{
    /// <summary>The model a source world's object comes from: its own glTF node, or the file its script loaded.</summary>
    private string? SourceObjectModel(DocumentModel doc, int node)
    {
        var origin = DescribeSourceObject(doc, node).Origin;
        if (origin.ModelFile != null) return origin.ModelFile;
        // A load's root: the model the LoadGameGen read is the file of its children.
        return doc.SourceBuild!.Provenance.Values.FirstOrDefault(p => p.Load != null && origin.Created != null && p.Load.Script == origin.Created.Script && p.Load.Line == origin.Created.Line && p.ModelFile != null)?.ModelFile;
    }

    internal Func<SourceWorkspace, string, CancellationToken, BlenderCheckout> CheckoutSourceModel { get; set; } = SourceBlender.Checkout;
    private async Task<BlenderCheckout> CheckoutForBlenderAsync(string model, CancellationToken token)
    {
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
        var workspace = SourceWorkspaceFor(root);
        if (sourceWorkspaceBusy) throw new StudioCommandException("busy", "A world of this source project is rebuilding after an edit; check the model out once it is shown.");
        long generation = ViewModel.WorkspaceGeneration;
        var workspaceToken = ViewModel.WorkspaceToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, workspaceToken, shutdownToken);
        // Off the UI thread: a checkout copies the model's files and decodes its textures to show their transparency.
        long revision = workspace.ContentRevision;
        BlenderCheckout checkout;
        var copy = CheckoutSourceModel;
        try { checkout = await Task.Run(() => copy(workspace, model, cancellation.Token), cancellation.Token); }
        // Another program changed a file the checkout read before its copies were complete (it removed them).
        catch (SourceFileChangedException ex) { throw new StudioCommandException("context_changed", ex.Message); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        // A completed copy survives a late caller cancellation, but must not be presented in another workspace,
        // including a reopening of the same root. Compare the captured instance without creating/adopting a new one.
        if (workspaceToken.IsCancellationRequested || shutdownToken.IsCancellationRequested || ViewModel.WorkspaceGeneration != generation
            || SourceProjectRoot != root || sourceWorkspace != workspace || workspace.ContentRevision != revision)
        {
            // The completed copy no longer owns filesystem handles. Its textual path may now name another folder,
            // so a stale GUI result must never recursively delete that path.
            throw new StudioCommandException("context_changed", $"The checkout completed, but the project changed before it could be opened; check the model out again. The completed copy was left where it was written (recorded path: {Bounded(checkout.Folder, 512)}).");
        }
        return checkout;
    }

    /// <summary>
    /// Plans an export's changes off the UI thread (sealing reads every file twice), then applies them to the workspace,
    /// rebuilding <paramref name="doc"/>'s world. Files changed in the project since the checkout are replaced only with
    /// <paramref name="force"/> (code conflict otherwise). Returns the files the change actually wrote.
    /// </summary>
    private async Task<(DocumentModel Document, BlenderUpdatePlan Plan, IReadOnlyList<string> Files)> UpdateFromBlenderAsync(DocumentModel doc, string checkoutId, string? export, bool force, CancellationToken token)
    {
        var session = doc.SourceWorld ?? throw new StudioCommandException("unsupported", "Open a mission world of the source project to update it from Blender.");
        RequireSourceWorldIdle(session);
        BlenderCheckout checkout; BlenderUpdatePlan plan;
        try
        {
            checkout = SourceBlender.Find(session.Root, checkoutId);
            var workspace = session.Workspace; string mission = session.Mission;
            var provenance = doc.SourceBuild?.Provenance.Values.ToArray() ?? [];
            plan = await Task.Run(() =>
            {
                var planned = SourceBlender.PlanUpdate(workspace, checkout, export, force, token);
                // Scripts that turn or scale a node whose own transform the export adds or removes, here or in other missions.
                if (planned.Changes.FirstOrDefault(c => c.Relative.Equals(checkout.Model, StringComparison.OrdinalIgnoreCase)) is { Content: { } content }
                    && workspace.Read(checkout.Model, token, Recoil.Zbd.Core.Gltf.GltfDocument.MaximumJsonBytes) is { } current
                    && SourceObjectEdits.ScriptTransformsReached(workspace, mission, checkout.Model, current, content, provenance, token) is { Count: > 0 } reached)
                    planned = planned with { Notes = [.. planned.Notes, .. reached] };
                return planned;
            }, token);
        }
        catch (BlenderConflictException ex) { throw new StudioCommandException("conflict", ex.Message); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        IReadOnlyList<string> written = [];
        var next = await PrepareSourceWorldEditAsync(doc, $"Updating {Path.GetFileName(checkout.Model)} from Blender", (workspace, ct) =>
        {
            // The plan was made off the UI thread; nothing may have changed those files since.
            var current = workspace.ReadEditHashes(plan.Expected.Keys, ct);
            foreach (var (relative, sha) in plan.Expected)
                if (current[relative] != sha)
                    throw new InvalidDataException($"{relative} changed while the update was prepared; update again.");
            if (workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), ct) is not { } t) return null;
            written = [.. t.Files.Select(f => f.Relative)];
            return t;
        }, token, fromBuild: false);
        try { if (written.Count > 0) SourceBlender.RecordApplied(checkout, plan); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            // Keep this first so bounded MCP notes cannot hide the accepted-but-unrecorded state.
            plan = plan with { Notes = [Bounded($"The update was applied, but the checkout could not record it: {Bounded(ex.Message, 256)}. Create a new checkout before the next update.", 512), .. plan.Notes] };
        }
        foreach (string note in plan.Notes) ViewModel.AddProblem(Bounded($"{session.Label}: {note}"), "Warning", Path.Combine(session.Root, checkout.Model.Replace('/', Path.DirectorySeparatorChar)));
        return (next, plan, written);
    }

    private static object CheckoutResult(BlenderCheckout checkout, IReadOnlyList<BlenderExport> exports) => CheckoutProjection(checkout, exports).Value;
    /// <summary>Keep actionable paths whole; bound nested rows by their worst-case JSON escaping before projection.</summary>
    private static (object Value, long Characters) CheckoutProjection(BlenderCheckout checkout, IReadOnlyList<BlenderExport> exports)
    {
        string input = checkout.Input, outbox = checkout.Outbox;
        long characters = checkout.Id.Length + (long)checkout.Folder.Length + checkout.Model.Length + input.Length + outbox.Length + 256;
        bool Fits(long cost) { if (characters + cost > 140_000) return false; characters += cost; return true; }
        var shownExports = exports.Take(16).TakeWhile(e => Fits(e.Relative.Length + 128L)).Select(e => new { path = e.Relative, written = e.WrittenUtc, bytes = e.Bytes }).ToArray();
        var shownFiles = checkout.Files.Take(64).TakeWhile(f => Fits(f.Project.Length + (long)f.Checkout.Length + 64)).Select(f => new { project = f.Project, checkout = f.Checkout }).ToArray();
        return (new
        {
            id = checkout.Id, folder = checkout.Folder, model = checkout.Model, input, outbox, created = checkout.CreatedUtc,
            files = shownFiles, fileCount = checkout.Files.Count, exports = shownExports, exportCount = exports.Count
        }, characters);
    }

    internal static StudioResult CheckoutPage(IReadOnlyList<(BlenderCheckout Checkout, IReadOnlyList<BlenderExport> Exports)> listed, int offset)
    {
        if (offset < 0) throw new StudioCommandException("invalid_argument", "Use offset >= 0.");
        List<object> shown = []; long characters = 0;
        foreach (var (checkout, exports) in listed.Skip(offset).Take(32))
        {
            var row = CheckoutProjection(checkout, exports);
            if (characters + row.Characters > 500_000 && shown.Count > 0) break;
            shown.Add(row.Value); characters += row.Characters;
        }
        return Result(new { checkouts = shown, checkoutCount = listed.Count, offset, nextOffset = (long)offset + shown.Count < listed.Count ? (int?)(offset + shown.Count) : null });
    }

    /// <summary>
    /// The project's checkouts with the exports in their outboxes, listed off the UI thread: a zstudio/export folder of many
    /// checkouts or files is read on a worker, bounded and cancellable (<see cref="SourceBlender.CheckoutExports(string, CancellationToken)"/>).
    /// </summary>
    internal Func<string, CancellationToken, IReadOnlyList<(BlenderCheckout Checkout, IReadOnlyList<BlenderExport> Exports)>> ReadCheckoutExports { get; set; } = SourceBlender.CheckoutExports;
    private async Task<IReadOnlyList<(BlenderCheckout Checkout, IReadOnlyList<BlenderExport> Exports)>> CheckoutExportsAsync(string root, CancellationToken token)
    {
        long generation = ViewModel.WorkspaceGeneration;
        var workspaceToken = ViewModel.WorkspaceToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, workspaceToken, shutdownToken);
        var read = ReadCheckoutExports;
        try
        {
            var listed = await Task.Run(() => read(root, cancellation.Token), cancellation.Token);
            RequireSourceRead(root, generation, cancellation.Token);
            return listed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
    }

    /// <summary>GUI: Edit in Blender for the object selected in a source world (its model), then show the checkout folder.</summary>
    private void EditInBlenderClick(object sender, RoutedEventArgs e) => _ = RunUi(async () =>
    {
        if (ViewModel.SelectedDocument is not { SourceWorld: not null } doc) throw new StudioCommandException("unsupported", "Open a mission world of the source project and select an object first.");
        if (selectedNode is not int node || SourceObjectNode(node) is not int sourceNode) throw new StudioCommandException("not_ready", "Select an object of the world first.");
        string model = SourceObjectModel(doc, sourceNode) ?? throw new StudioCommandException("unsupported", "The selected object was not loaded from a model file.");
        var checkout = await CheckoutForBlenderAsync(model, shutdownToken);
        string message = $"{model} is checked out for Blender.\n\n1. In Blender, import:\n{checkout.Input}\n2. Export it as glTF 2.0, format glTF Separate (.gltf + .bin + textures), with Custom Properties, into:\n{checkout.Outbox}\n3. Choose Tools → Update from Blender export.\n\nOpen the checkout folder now?";
        if (MessageBox.Show(this, message, "Edit in Blender", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo { FileName = checkout.Folder, UseShellExecute = true });
    });

    /// <summary>GUI: Update from Blender export — the newest export of the newest checkout, after confirmation.</summary>
    private void UpdateFromBlenderClick(object sender, RoutedEventArgs e) => _ = RunUi(async () =>
    {
        if (ViewModel.SelectedDocument is not { SourceWorld: { } session } doc) throw new StudioCommandException("unsupported", "Open a mission world of the source project first.");
        var listed = await CheckoutExportsAsync(session.Root, shutdownToken);
        // The world may have closed while the checkouts were listed.
        if (doc.IsDisposed || !ViewModel.Documents.Contains(doc)) throw new StudioCommandException("context_changed", "The world was closed while the Blender exports were listed.");
        var candidates = listed.Where(c => c.Exports.Count > 0).Select(c => (c.Checkout, Export: c.Exports[0])).ToArray();
        if (candidates.Length == 0) throw new StudioCommandException("not_ready", "No Blender export was found in the project's zstudio/export folder. Use Edit in Blender first.");
        var (checkout, export) = candidates.OrderByDescending(c => c.Export.WrittenUtc).First();
        if (MessageBox.Show(this, $"Update {checkout.Model} from the Blender export {export!.Relative} ({export.WrittenUtc.ToLocalTime():g})?\n\nThe model, its buffer and changed textures are replaced in the project's unsaved edits; Save writes them.",
            "Update from Blender export", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        (DocumentModel Document, BlenderUpdatePlan Plan, IReadOnlyList<string> Files) result;
        try { result = await UpdateFromBlenderAsync(doc, checkout.Id, export.Relative, false, shutdownToken); }
        catch (StudioCommandException ex) when (ex.Code == "conflict")
        {
            if (MessageBox.Show(this, ex.Message + "\n\nUpdate anyway, accepting all of this?", "Update from Blender export", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (ViewModel.SelectedDocument is not { SourceWorld: not null } current) return;
            result = await UpdateFromBlenderAsync(current, checkout.Id, export.Relative, true, shutdownToken);
        }
        int count = result.Files.Count;
        ViewModel.Status = count == 0 ? $"{checkout.Model} already matches the Blender export." : $"Updated {checkout.Model} from Blender: {count} file{(count == 1 ? "" : "s")} changed" + (result.Plan.Notes.Count > 0 ? $"; {result.Plan.Notes.Count} notes in Problems" : "");
    });

    private void RegisterSourceBlenderCommands(StudioCommands r)
    {
        // A job like the other long source operations: the copy and the texture decoding run off the UI thread, and can be cancelled.
        RegisterJob(r, "source_blender_checkout", "Check a project model out for Blender: its glTF, buffers and textures are copied into the project's zstudio/export/<id>/input folder as a self-contained glTF to import in Blender. Export the edited model as glTF Separate (.gltf + .bin + textures, Custom Properties on) into the checkout's outbox, then call source_blender_update. Give the model path, or a source world document and scene node to check out the model that object comes from. Cancelling removes the partial copy.",
            [P("model", "string", "Project path of a .gltf model, for example data/m2/models/bft/ltank.gltf."), DocumentParameter with { Required = false }, new("node", "integer", "Scene node of the document whose model to check out.", Minimum: 0, Maximum: int.MaxValue)], true, async (a, token) =>
        {
            string model;
            if (a["model"] != null) { if (a["node"] != null) throw new StudioCommandException("invalid_argument", "Give a model or a node, not both."); model = Text(a, "model"); }
            else
            {
                var d = TargetDocument(a); if (a["node"] == null) throw new StudioCommandException("invalid_argument", "Give a model path or a node.");
                model = SourceObjectModel(d, Int(a, "node")) ?? throw new StudioCommandException("unsupported", "That object was not loaded from a model file.");
            }
            var checkout = await CheckoutForBlenderAsync(model, token);
            // Written: the job completes with the checkout, even when a cancel arrives as it finishes (the copy stays).
            CommitRunningJob();
            // A new checkout's outbox is empty: nothing to list.
            return Result(CheckoutResult(checkout, []));
        });
        Register(r, "source_blender_checkouts", "List the open source project's Blender checkouts (newest first) with the exports found in each outbox (newest first). Returns at most 32 checkouts, 64 files and 16 exports each, with total counts. Large paths shorten pages and nested lists without shortening paths; follow nextOffset. The listing runs off the UI thread and observes cancellation; more than 250,000 entries or 64 MiB of manifests together is refused with io_failed.", false,
            [new("offset", "integer", "Zero-based checkout offset; default 0. Follow nextOffset to continue.", Minimum: 0, Maximum: int.MaxValue)], async (a, token) =>
        {
            string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
            var listed = await CheckoutExportsAsync(root, token);
            return CheckoutPage(listed, Int(a, "offset"));
        });
        RegisterJob(r, "source_blender_update", "Update a checked-out model from what Blender exported into its outbox, as one undoable change of the project's workspace: the export is sealed (copied while checking it is complete), read as a build reads models, and becomes the model's glTF and buffer, with each texture PNG Blender added or changed; a texture other models use changes for them too (reported in notes). Files changed in the project since the checkout (other edits, or another update), existing project files the checkout did not hold (another model's texture of the same name), and an export without the model's engine attributes (Blender's Custom Properties off) are not applied unless force is true: the command fails with code conflict, naming all of them at once. The source world rebuilds and the result is its replacement document; an export the world cannot be built with is taken back. files lists the files the change wrote. Nothing is written until save_document.",
            [DocumentParameter, RevisionParameter, P("checkout", "string", "Checkout id from source_blender_checkout or source_blender_checkouts.", true), P("export", "string", "Export path relative to the outbox; default the newest."),
             P("force", "boolean", "Apply despite a conflict (files changed since the checkout or outside it, or dropped engine attributes); default false.")], true,
            async (a, token) =>
            {
                var d = TargetDocument(a, true);
                var (next, plan, files) = await UpdateFromBlenderAsync(d, Text(a, "checkout"), a["export"] == null ? null : Text(a, "export"), Flag(a, "force"), token);
                long characters = 0;
                var shownFiles = files.Take(256).TakeWhile(f => (characters += f.Length) <= 64_000).ToArray();
                return Result(new { document = DocumentState(next), files = shownFiles, fileCount = files.Count, notes = plan.Notes.Take(32).Select(n => Bounded(n, 512)).ToArray(), noteCount = plan.Notes.Count, @sealed = plan.Sealed });
            });
    }
}
