using System.IO;
using System.Text.Json.Nodes;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private sealed class AutomationOperation(string name, bool cancellable)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; } = name;
        public bool Cancellable { get; } = cancellable;
        public string State { get; set; } = "queued";
        public JsonNode? Data { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Work { get; set; } = Task.CompletedTask;
        public object Snapshot() => new { id = Id, Name, State, Cancellable, result = Data };
    }
    private readonly Dictionary<Guid, AutomationOperation> automationOperations = [];
    private bool stoppingAutomation;
    private void RegisterJob(StudioCommands r, string name, string description, StudioParameter[] parameters, bool cancellable, Func<JsonObject, CancellationToken, Task<StudioResult>> execute)
    {
        r.Add(new("zstudio_" + name, description + " Returns an operation ID; use zstudio_operation to await/read the result.", true, parameters, async (a, token) =>
        {
            return await Dispatcher.InvokeAsync(() =>
            {
                if (stoppingAutomation) throw new StudioCommandException("shutting_down", "MCP is disconnecting.");
                foreach (var old in automationOperations.Values.Where(j => j.State is "completed" or "failed" or "canceled").Take(Math.Max(0, automationOperations.Count - 127)).ToArray()) { automationOperations.Remove(old.Id); old.Cancellation.Dispose(); }
                if (automationOperations.Count >= 128) throw new StudioCommandException("busy", "Too many queued operations.");
                var job = new AutomationOperation(name, cancellable); automationOperations.Add(job.Id, job);
                var arguments = (JsonObject)a.DeepClone();
                job.Work = RunAutomationJobAsync(job, arguments, execute);
                return Result(job.Snapshot());
            }, System.Windows.Threading.DispatcherPriority.Normal, token);
        }));
    }
    private async Task RunAutomationJobAsync(AutomationOperation job, JsonObject arguments, Func<JsonObject, CancellationToken, Task<StudioResult>> execute)
    {
        bool acquired = false;
        automationRequest.Value = true;
        try
        {
            await automationGate.WaitAsync(job.Cancellation.Token); acquired = true; job.State = "running";
            using var scope = PreviewOperation.Begin(job.Cancellation.Token);
            job.Cancellation.Token.ThrowIfCancellationRequested();
            RequireAutomationMutationAvailable();
            job.Data = (await execute(arguments, job.Cancellation.Token)).Data;
            job.Cancellation.Token.ThrowIfCancellationRequested(); job.State = "completed";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            job.State = ex is OperationCanceledException ? "canceled" : "failed";
            job.Data = Result(new { code = ex is StudioCommandException error ? error.Code : job.State, message = ex.Message }).Data;
        }
        finally { if (acquired) automationGate.Release(); }
    }
    private void RegisterOperationCommands(StudioCommands r)
    {
        r.Add(new("zstudio_operation", "Read operation status/result. Poll at sensible intervals; cancel is available for exports, validation and world comparisons.", true,
            [P("id", "string", "Operation ID.", true), P("cancel", "boolean", "Request cancellation of an export, validation or world comparison.")], async (a, token) => await Dispatcher.InvokeAsync(() =>
            {
                if (!Guid.TryParse(Text(a, "id"), out var id) || !automationOperations.TryGetValue(id, out var job)) throw new StudioCommandException("unknown_operation", "Operation was not found or has expired.");
                if (Flag(a, "cancel")) { if (!job.Cancellable) throw new StudioCommandException("not_cancellable", "This operation cannot be manually canceled."); job.Cancellation.Cancel(); }
                return Result(job.Snapshot());
            }, System.Windows.Threading.DispatcherPriority.Normal, token)));
    }
    private int documentSaveDepth;
    /// <summary>Set for the work of an MCP request or job (it flows into what that work awaits); the GUI's own actions run without it.</summary>
    private readonly AsyncLocal<bool> automationRequest = new();
    private void RequireAutomationMutationAvailable()
    {
        if (shutdownToken.IsCancellationRequested)
            throw new StudioCommandException("shutting_down", "The workspace is closing.");
        // A native dialog (a message box owned by Properties, a picker of Compare worlds) holds MCP changes only: the GUI's
        // own navigation through the windows it leaves enabled is the user's action, not one the dialog's decision could lose.
        if (!IsEnabled || documentSaveDepth != 0 || (automationRequest.Value ? GuiDialogOpen() : System.Windows.Interop.ComponentDispatcher.IsThreadModal))
            throw new StudioCommandException("busy", "A GUI dialog, operation or document save is in progress. Retry after it completes.");
    }
    /// <summary>
    /// Whether a dialog of the GUI waits for the user: a modal WPF window (ShowDialog), or a native dialog whatever window
    /// owns it, or none (a message box, a file or folder picker). A native dialog runs its own message loop, which still runs
    /// the dispatcher work MCP calls queue, and leaves WPF's modal state and IsEnabled as they were; a change accepted then
    /// would be overwritten or lost by the decision the dialog asks for. Call on the UI thread (the dialogs' thread).
    /// </summary>
    private static bool GuiDialogOpen()
    {
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) return true;
        bool open = false;
        EnumThreadWindows(GetCurrentThreadId(), (handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var name = new System.Text.StringBuilder(8);
            // #32770 is the window class of every native dialog: MessageBox, the common file and folder dialogs, task dialogs.
            open = GetClassName(handle, name, name.Capacity) == 6 && name.ToString() == "#32770";
            return !open;
        }, 0);
        return open;
    }
    private delegate bool ThreadWindowCallback(nint handle, nint parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, ThreadWindowCallback callback, nint parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetClassName(nint handle, System.Text.StringBuilder name, int count);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private IDisposable BeginDocumentSave()
    {
        if (documentSaveDepth != 0) throw new StudioCommandException("busy", "A document save is already in progress.");
        bool enabled = IsEnabled;
        var pinnedWindow = propertiesWindow; bool propertiesEnabled = pinnedWindow?.IsEnabled == true;
        ++documentSaveDepth; IsEnabled = false; if (pinnedWindow != null) pinnedWindow.IsEnabled = false;
        return new SaveExclusion(() =>
        {
            --documentSaveDepth; IsEnabled = enabled;
            if (pinnedWindow != null && propertiesWindow == pinnedWindow) pinnedWindow.IsEnabled = propertiesEnabled;
        });
    }
    private sealed class SaveExclusion(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
    internal Func<AnimationPackage, string, string, string, CancellationToken, Task> WriteAnimationArchiveAsync { get; set; } = AnimationWriter.SaveAsAsync;
    internal Func<AssetResolver, IAssetExporter> CreateAssetExporter { get; set; } = static resolver => new ExportService(resolver);
    internal Func<string, CancellationToken, Task<ZbdDocument>> ReadValidationSourceAsync { get; set; } = static (path, token) => FormatRegistry.Default.OpenAsync(path, token);
    internal async Task SaveAnimationToPathAsync(DocumentModel doc, string destination, CancellationToken token = default)
    {
        using var save = BeginDocumentSave();
        var edits = doc.AnimationEdits ?? throw new InvalidOperationException("Not an editable animation pack.");
        if (shownDocument == doc) animation?.Pause();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token);
        await WriteAnimationArchiveAsync(edits.Package, destination, doc.Path, ViewModel.Resolver?.Root ?? Path.GetDirectoryName(doc.Path)!, cancellation.Token);
        doc.LastSavedCopy = destination; edits.MarkSaved(); ViewModel.Status = "Saved and verified " + destination + " · preview keeps the original mission context";
    }
    private async Task<PickupPlacementSaveResult> SavePickupDestinationsAsync(DocumentModel doc, IReadOnlyDictionary<string, string>? destinations, bool backup, CancellationToken token = default)
    {
        using var save = BeginDocumentSave();
        var edits = doc.PickupEdits ?? throw new InvalidOperationException("No editable pickup placements loaded.");
        // Claim only archives this save writes; unedited readers may belong to other documents.
        var written = edits.ArchivePaths.Where(p => edits.IsArchiveDirty(p) || destinations?.Keys.Any(d => Path.GetFullPath(d).Equals(Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase)) == true).ToArray();
        doc.ClaimResourcePaths(written.Concat(written.Select(edits.TargetPath)).Concat(destinations?.Values ?? []));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token);
        var result = await edits.SaveAsync(destinations, backup, cancellation.Token);
        if (result.SavedPaths.Count > 0)
        {
            if (ViewModel.Resolver is { } resolver) await resolver.InvalidateAsync(result.SavedPaths, doc.Lifetime.Token);
            foreach (var open in ViewModel.Documents) open.InvalidateMissionContext();
            ViewModel.CheckExternalChanges(); ViewModel.Status = $"Saved and verified {result.SavedPaths.Count} files";
        }
        foreach (string error in result.Errors) ViewModel.AddProblem(error, file: doc.Path);
        PickupEditsChanged(); return result;
    }
    private async Task<ExportResult> ExportAssetsAsync(DocumentModel doc, AssetRecord[] assets, string destination, bool json, string? pack, int lod, CancellationToken token)
    {
        if (operation != null) throw new StudioCommandException("busy", "An export or validation is already running.");
        if (ViewModel.Resolver is not { } resolver) throw new StudioCommandException("no_workspace", "Open a root first.");
        if (lod < 0) throw new StudioCommandException("invalid_argument", "LOD cannot be negative.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token); operation = cancellation; CancelOperationItem.IsEnabled = true;
        try
        {
            var progress = new Progress<ExportProgress>(p =>
            {
                if (operation == cancellation && !cancellation.IsCancellationRequested && !doc.IsDisposed)
                    ViewModel.Status = $"Exporting {p.Completed}/{p.Total}: {p.Name}";
            });
            var exporter = CreateAssetExporter(resolver);
            var snapshot = doc.PreviewDocument;
            var exportAssets = assets.Select(a => snapshot.Assets.Single(s => s.Id.Kind == a.Kind && s.Index == a.Index)).ToArray();
            var result = await Task.Run(() => exporter.ExportAsync(snapshot, exportAssets, destination, json, pack, lod, progress, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            foreach (string error in result.Errors) ViewModel.AddProblem(error, file: doc.Path);
            ViewModel.Status = $"Exported {result.Completed}/{assets.Length} assets to {result.Directory}"; return result;
        }
        finally { operation = null; CancelOperationItem.IsEnabled = false; }
    }
    private async Task<List<StudioProblem>> ValidateDocumentSourceAsync(DocumentModel selected, CancellationToken token)
    {
        if (operation != null) throw new StudioCommandException("busy", "An export or validation is already running.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, selected.Lifetime.Token); operation = cancellation; CancelOperationItem.IsEnabled = true;
        try
        {
            ViewModel.Status = "Validating source file on disk…";
            var notes = await Task.Run(async () =>
            {
                var doc = await ReadValidationSourceAsync(selected.Path, cancellation.Token);
                var diagnostics = doc.Diagnostics.Select(d => new StudioProblem(d.Severity, "File / operation", d.Message, selected.Path, d.AssetIndex, d.Offset)).ToList();
                foreach (var asset in doc.Assets)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    try
                    {
                        if (asset.Kind == AssetKind.Texture) TextureDecoder.Decode(doc, asset, cancellation.Token);
                        else if (asset.Kind == AssetKind.Zrd) ZrdDecoder.ReadAsset(doc, asset, cancellation.Token);
                        else if (asset.Kind == AssetKind.Sound) WaveDecoder.Read(doc.Slice(asset.Offset, asset.Length), cancellation.Token);
                    }
                    catch (InvalidDataException ex) { diagnostics.Add(new("Error", "File / operation", asset.Name + ": " + ex.Message, selected.Path, asset.Index, asset.Offset)); }
                }
                return diagnostics;
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            foreach (var note in notes) ViewModel.AddProblem(note.Message, note.Severity, note.File, note.AssetIndex, note.Offset);
            ViewModel.Status = $"Source-file validation finished: {notes.Count} diagnostics"; return notes;
        }
        finally { operation = null; CancelOperationItem.IsEnabled = false; }
    }
}
