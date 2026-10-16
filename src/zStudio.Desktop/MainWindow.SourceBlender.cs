using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

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
        if (doc.SourceWorld == null || doc.SourceBuild is not { } build)
            throw new StudioCommandException("unsupported", "This document is not a source world.");
        // Geometry checkout follows the physical referenced model, not the source placement wrapper's file.
        // A runtime clone first resolves its template; resource placement text is never a glTF path.
        if (doc == shownDocument && scene?.Mission is { } mission && node >= 0 && node < mission.SourceNodes.Count)
            node = mission.SourceNodes[node];
        var selection = SourceWorldModel(doc).Selection;
        int owner = selection.Owners.GetValueOrDefault(node, node);
        if (selection.Boundaries.GetValueOrDefault(owner)?.Source is { } model) return model;
        if (!build.Provenance.TryGetValue(node, out var origin)) return null;
        // A helper script can load different models from the same line; the root records its own resolved file,
        // including when that model has an empty scene. Imported nodes keep their own model-file identity.
        return origin.ModelFile ?? origin.LoadedFile;
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
    private async Task<(DocumentModel Document, BlenderUpdatePlan Plan, IReadOnlyList<string> Files)> UpdateFromBlenderAsync(DocumentModel doc, string checkoutId, string? export, bool force, CancellationToken token,
        Func<BlenderConflictException, CancellationToken, bool>? confirmConflict = null)
    {
        var session = doc.SourceWorld ?? throw new StudioCommandException("unsupported", "Open a mission world of the source project to update it from Blender.");
        RequireSourceWorldIdle(session);
        BlenderCheckout checkout;
        try
        {
            checkout = SourceBlender.Find(session.Root, checkoutId);
        }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        string mission = session.Mission;
        var provenance = doc.SourceBuild?.Provenance.Values.ToArray() ?? [];
        BlenderUpdatePlan plan = null!;
        IReadOnlyList<string> written = [];
        var next = await PrepareSourceWorldEditAsync(doc, $"Updating {Path.GetFileName(checkout.Model)} from Blender", (workspace, ct) =>
        {
            // Capture pending edits on the dispatcher before planning, and retain every semantic read
            // on this same prepared workspace through Apply, worker verification and acceptance.
            try
            {
                plan = confirmConflict == null ? SourceBlender.PlanUpdate(workspace, checkout, export, force, ct)
                    : SourceBlender.PlanUpdate(workspace, checkout, export, confirmConflict, ct);
                // Scripts that turn or scale a node whose rotation or scale the export changes (theirs replace it), here or in other missions.
                foreach (var (path, content) in plan.Changes.Where(c => c.Relative.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)))
                    if (workspace.Read(path, ct, zStudio.Core.Gltf.GltfDocument.MaximumJsonBytes) is { } current
                        && SourceObjectEdits.ScriptTransformsReached(workspace, mission, path, current, content, provenance, ct) is { Count: > 0 } reached)
                        plan = plan with { Notes = [.. plan.Notes, .. reached] };
            }
            catch (BlenderConflictException ex) { throw new StudioCommandException("conflict", ex.Message); }
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
        long characters = checkout.Id.Length + (long)checkout.Folder.Length + checkout.Model.Length + input.Length + outbox.Length
            + AppContext.BaseDirectory.Length + (Environment.ProcessPath?.Length ?? 0) + 856;
        bool Fits(long cost) { if (characters + cost > 140_000) return false; characters += cost; return true; }
        var shownExports = exports.Take(16).TakeWhile(e => Fits(e.Relative.Length + 128L)).Select(e => new { path = e.Relative, written = e.WrittenUtc, bytes = e.Bytes }).ToArray();
        var shownFiles = checkout.Files.Take(64).TakeWhile(f => Fits(f.Project.Length + (long)f.Checkout.Length + 64)).Select(f => new { project = f.Project, checkout = f.Checkout }).ToArray();
        return (new
        {
            version = checkout.Version, extension = BlenderExtensionInfo(),
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
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project first.");
        string? selected = ViewModel.SelectedDocument is { SourceWorld: not null } doc && selectedNode is int node && SourceObjectNode(node) is int sourceNode
            ? SourceObjectModel(doc, sourceNode) : null;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose source glTF for Blender", Filter = "RECOIL glTF (*.gltf)|*.gltf",
            InitialDirectory = selected == null ? Path.Combine(root, "data") : Path.GetDirectoryName(Path.Combine(root, selected)),
            FileName = selected == null ? "" : Path.GetFileName(selected), CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (SourceProjectRoot != root) throw new StudioCommandException("context_changed", "The source project changed while choosing the model.");
        string model = Path.GetRelativePath(root, picker.FileName).Replace('\\', '/');
        var checkout = await CheckoutForBlenderAsync(model, shutdownToken);
        string message = $"{model} is checked out for Blender.\n\nInstall the bundled extension from dependencies/blender in Blender 5.2 LTS Preferences → Get Extensions → Install from Disk. Set its zStudio executable preference.\n\n1. Use File → Import → RECOIL glTF:\n{checkout.Input}\n2. Use Export RECOIL glTF to:\n{checkout.Outbox}\n3. Choose Tools → Update from Blender export in zStudio. Review the update, then Save to write the source files.\n\nOpen the checkout folder now?";
        if (MessageBox.Show(this, message, "Edit in Blender", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo { FileName = checkout.Folder, UseShellExecute = true });
    });

    /// <summary>
    /// GUI: Update from Blender export — the export chosen among every checkout's listed exports (the newest of all to start
    /// with), as source_blender_update takes a checkout id and export path.
    /// </summary>
    private void UpdateFromBlenderClick(object sender, RoutedEventArgs e) => _ = RunUi(async () =>
    {
        if (ViewModel.SelectedDocument is not { SourceWorld: { } session } doc) throw new StudioCommandException("unsupported", "Open a mission world of the source project first.");
        var listed = await CheckoutExportsAsync(session.Root, shutdownToken);
        // The world may have closed while the checkouts were listed.
        if (doc.IsDisposed || !ViewModel.Documents.Contains(doc)) throw new StudioCommandException("context_changed", "The world was closed while the Blender exports were listed.");
        var candidates = listed.Where(c => c.Exports.Count > 0).ToArray();
        if (candidates.Length == 0) throw new StudioCommandException("not_ready", "No Blender export was found in the project's zstudio/export folder. Use Edit in Blender first.");
        BlenderUpdateDialog dialog = new(candidates) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var (checkout, export) = dialog.Chosen;
        // The world may also have closed while the export was chosen.
        if (doc.IsDisposed || !ViewModel.Documents.Contains(doc)) throw new StudioCommandException("context_changed", "The world was closed while the Blender export was chosen.");
        if (await UpdateFromBlenderWithConsentAsync(doc, checkout.Id, export.Relative, shutdownToken) is not { } result) return;
        int count = result.Files.Count;
        ViewModel.Status = count == 0 ? $"{checkout.Model} already matches the Blender export." : $"Updated {checkout.Model} from Blender: {count} file{(count == 1 ? "" : "s")} changed" + (result.Plan.Notes.Count > 0 ? $"; {result.Plan.Notes.Count} notes in Problems" : "");
    });

    internal Func<string, bool>? ConfirmBlenderUpdate { get; set; }

    /// <summary>GUI consent belongs to the sealed plan being shown, never to a later forced replan.</summary>
    internal async Task<(DocumentModel Document, BlenderUpdatePlan Plan, IReadOnlyList<string> Files)?> UpdateFromBlenderWithConsentAsync(
        DocumentModel doc, string checkoutId, string export, CancellationToken token)
    {
        bool declined = false;
        var session = doc.SourceWorld;
        bool Confirm(BlenderConflictException conflict, CancellationToken cancellation) => Dispatcher.Invoke(() =>
        {
            RequireCurrent();
            bool accepted = ConfirmBlenderUpdate?.Invoke(conflict.Message) ?? MessageBox.Show(this,
                conflict.Message + "\n\nUpdate anyway, accepting all of this?", "Update from Blender export",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            RequireCurrent();
            declined = !accepted;
            return accepted;

            void RequireCurrent()
            {
                cancellation.ThrowIfCancellationRequested();
                if (doc.IsDisposed || ViewModel.SelectedDocument != doc || !ViewModel.Documents.Contains(doc)
                    || doc.SourceWorld != session || sourceWorkspace != session?.Workspace)
                    throw new StudioCommandException("context_changed", "The world changed while the Blender update was being confirmed; try again.");
            }
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellation);
        try { return await UpdateFromBlenderAsync(doc, checkoutId, export, false, token, Confirm); }
        catch (OperationCanceledException) when (declined) { return null; }
    }

    private void RegisterSourceBlenderCommands(StudioCommands r)
    {
        Register(r, "source_blender_extension", "Locate the bundled Blender 5.2 LTS RECOIL extension and its compatible windowless helper. Does not install it or enable automation.", false,
            [], (a, token) => Task.FromResult(Result(BlenderExtensionInfo())));
        // A job like the other long source operations: the copy and the texture decoding run off the UI thread, and can be cancelled.
        RegisterJob(r, "source_blender_checkout", "Create a reference-aware Blender checkout of a source glTF and its complete dependency graph, from the current shared workspace. Requires the bundled RECOIL Blender extension. The project remains unchanged.",
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
        RegisterJob(r, "source_blender_update", "Validate and accept one completed RECOIL Blender graph export as a shared, undoable source transaction. All linked files and placement assignments are validated before publication. Older flattened checkouts are refused.",
            [DocumentParameter, RevisionParameter, P("checkout", "string", "Checkout id from source_blender_checkout or source_blender_checkouts.", true), P("export", "string", "Export path relative to the outbox; default the newest."),
             P("force", "boolean", "Apply despite a reported external-file or shared-texture conflict; default false. Invalid or missing required metadata can never be forced.")], true,
            async (a, token) =>
            {
                var d = TargetDocument(a, true);
                var (next, plan, files) = await UpdateFromBlenderAsync(d, Text(a, "checkout"), a["export"] == null ? null : Text(a, "export"), Flag(a, "force"), token);
                long characters = 0;
                var shownFiles = files.Take(256).TakeWhile(f => (characters += f.Length) <= 64_000).ToArray();
                return Result(new { document = DocumentState(next), files = shownFiles, fileCount = files.Count, notes = plan.Notes.Take(32).Select(n => Bounded(n, 512)).ToArray(), noteCount = plan.Notes.Count, @sealed = plan.Sealed });
            });
    }

    private static object BlenderExtensionInfo()
    {
        string version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainWindow).Assembly)!.InformationalVersion.Split('+')[0];
        string archive = Path.Combine(AppContext.BaseDirectory, "blender", $"zstudio_recoil-{version}.zip");
        return new { archive, available = File.Exists(archive), blenderMin = "5.2.0", blenderMaxExclusive = "5.3.0", protocol = SourceBlender.GraphProtocol,
            helper = Environment.ProcessPath, install = "Blender Preferences → Get Extensions → Install from Disk; select the archive, then set the extension's zStudio executable preference." };
    }
}
