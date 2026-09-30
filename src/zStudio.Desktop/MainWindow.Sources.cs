using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    /// <summary>The open root when it is a reconstructed source project.</summary>
    private string? SourceProjectRoot => ViewModel.HasRoot && SourceProject.IsProject(ViewModel.RootPath) ? ViewModel.RootPath : null;
    private static string Bounded(string text, int maximum = 1024) => text.Length <= maximum ? text : text[..maximum] + "…";
    /// <summary>Test hook: runs after a pack finishes and before its result is published to the workspace.</summary>
    internal Func<Task>? SourcePackFinishing { get; set; }

    private async Task<SourceProjectManifest> ReconstructSourceProjectAsync(string source, string destination, bool open, CancellationToken token)
    {
        if (operation != null) throw new StudioCommandException("busy", "An export, validation or source operation is already running.");
        if (open && ViewModel.Documents.Any(d => d.IsDirty)) throw new StudioCommandException("unsaved_changes", "Save or explicitly discard dirty documents before opening the reconstructed project.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); operation = cancellation; CancelOperationItem.IsEnabled = true;
        SourceProjectManifest manifest;
        try
        {
            var progress = new Progress<SourceProgress>(p => { if (operation == cancellation && !cancellation.IsCancellationRequested) ViewModel.Status = $"Reconstructing {p.Completed}/{p.Total}: {p.Item}"; });
            manifest = await Task.Run(() => SourceExtractor.ExtractAsync(source, destination, progress, cancellation.Token), cancellation.Token);
            foreach (string note in manifest.Notes.Take(256)) ViewModel.AddProblem(Bounded(note), "Warning", destination);
            ViewModel.Status = $"Reconstructed {manifest.Outputs.Count} game files into {destination}";
        }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        finally { operation = null; CancelOperationItem.IsEnabled = false; }
        if (open) { RequireRootPublication(); await ViewModel.OpenRootAsync(destination, token, RequireRootPublication); }
        return manifest;
    }
    private static object ReconstructResult(SourceProjectManifest manifest, string destination, bool open) => new
    {
        project = Path.GetFullPath(destination), opened = open, origin = manifest.Origin, outputs = manifest.Outputs.Count,
        families = manifest.Outputs.GroupBy(o => o.Family).ToDictionary(g => g.Key, g => g.Count()),
        notes = manifest.Notes.Take(32).Select(n => Bounded(n, 512)).ToArray(), noteCount = manifest.Notes.Count, notesTruncated = manifest.Notes.Count > 32
    };

    /// <summary>Pack (or, without a destination, only verify) the open source project from its files on disk.</summary>
    private async Task<SourcePackReport> PackSourceProjectAsync(string? destination, CancellationToken token)
    {
        if (operation != null) throw new StudioCommandException("busy", "An export, validation or source operation is already running.");
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a reconstructed source project first.");
        RequireNoDrafts();
        // Packing reads source files from disk, so pending edits to them must be saved or discarded first.
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (ViewModel.Documents.FirstOrDefault(d => d.IsDirty && Path.GetFullPath(d.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } dirty)
            throw new StudioCommandException("unsaved_changes", $"Save or discard the edits to {Path.GetFileName(dirty.Path)} before packing.");
        // Opening another root cancels the pack; its result never publishes into the new workspace.
        long generation = ViewModel.WorkspaceGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ViewModel.WorkspaceToken); operation = cancellation; CancelOperationItem.IsEnabled = true;
        try
        {
            string verb = destination == null ? "Verifying" : "Packing";
            var progress = new Progress<SourceProgress>(p => { if (operation == cancellation && !cancellation.IsCancellationRequested) ViewModel.Status = $"{verb} {p.Completed}/{p.Total}: {p.Item}"; });
            var report = await Task.Run(() => destination == null ? SourcePacker.VerifyAsync(root, progress, cancellation.Token) : SourcePacker.PackAsync(root, destination, progress, cancellation.Token), cancellation.Token);
            if (SourcePackFinishing is { } finishing) await finishing();
            if (ViewModel.WorkspaceGeneration != generation) throw new StudioCommandException("context_changed", "The workspace changed while packing.");
            foreach (var failed in report.Outputs.Where(o => o.Status == "failed")) ViewModel.AddProblem(Bounded($"{failed.Path}: {failed.Error}"), file: root);
            ViewModel.Status = $"{(destination == null ? "Verified" : "Packed")} {report.Outputs.Count} game files: {report.Identical} identical, {report.Changed} changed, {report.Failed} failed";
            return report;
        }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        finally { operation = null; CancelOperationItem.IsEnabled = false; }
    }
    private static object PackResult(string root, SourcePackReport report)
    {
        const int shown = 256;
        return new
        {
            project = root, destination = report.Destination, written = report.Destination != null, report.Identical, report.Changed, report.Failed,
            outputs = report.Outputs.Take(shown).Select(o => new
            {
                o.Path, o.Family, o.Status, o.Sha256, o.OriginalSha256, changedSources = o.ChangedSources.Take(16).Select(s => Bounded(s, 512)).ToArray(),
                changedSourceCount = o.ChangedSources.Count, error = o.Error == null ? null : Bounded(o.Error)
            }).ToArray(),
            outputCount = report.Outputs.Count, outputsTruncated = report.Outputs.Count > shown
        };
    }

    private async Task<object> SourceStatusAsync(JsonObject a, CancellationToken token)
    {
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a reconstructed source project first.");
        var manifest = await SourceProject.LoadAsync(root, token);
        return new
        {
            project = root, id = manifest.Id, game = Bounded(manifest.Game, 64), manifest.Version, origin = manifest.Origin,
            families = manifest.Outputs.GroupBy(o => o.Family).ToDictionary(g => g.Key, g => g.Count()),
            notes = manifest.Notes.Take(32).Select(n => Bounded(n, 512)).ToArray(), noteCount = manifest.Notes.Count, notesTruncated = manifest.Notes.Count > 32,
            outputs = Page(manifest.Outputs, a, o => o.Path, o => new { o.Path, o.Family, o.Bytes, o.Sha256 }).Data
        };
    }

    private void RegisterSourceCommands(StudioCommands r)
    {
        RegisterJob(r, "source_reconstruct", "Reconstruct a deduplicated source project (data/ and gamegen/ in the original build layout) from a shipped RECOIL data folder into a new or empty folder. Every output is verified to pack back byte-identically; unconverted families are kept verbatim. Optionally opens the project as the workspace root; dirty documents must be resolved first.",
            [P("source", "string", "Shipped game data folder (for example the folder containing interp.zbd and m1\\).", true), P("destination", "string", "New or empty project folder outside the source folder.", true),
             P("open", "boolean", "Open the project as the workspace root afterwards; default true.")], true,
            async (a, token) =>
            {
                bool open = a["open"] == null || Flag(a, "open"); string destination = Text(a, "destination");
                return Result(ReconstructResult(await ReconstructSourceProjectAsync(Text(a, "source"), destination, open, token), destination, open));
            });
        RegisterJob(r, "source_pack", "Build every game file of the open source project from its files on disk. Without destination, only verify against the shipped files. With destination (new, empty or previously packed folder outside the project), stage, re-parse and then write all outputs, or nothing if any fails. Reports identical/changed/failed per output with changed sources. Unsaved edits to project files must be resolved first.",
            [P("destination", "string", "Optional output folder; omit to verify without writing.")], true,
            async (a, token) =>
            {
                var report = await PackSourceProjectAsync(a["destination"] == null ? null : Text(a, "destination"), token);
                return Result(PackResult(SourceProjectRoot ?? "", report));
            });
        RegisterJob(r, "source_status", "Describe the open source project: origin fingerprint, output families, reconstruction notes (32 previewed) and a paged output list filtered by path.", [.. PageParameters], false,
            async (a, token) => Result(await SourceStatusAsync(a, token)));
    }

    private async void ReconstructSourceClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        OpenFolderDialog input = new() { Title = "Choose the shipped RECOIL data folder (contains interp.zbd and m1)", InitialDirectory = ViewModel.RootPath is { } current && SourceProjectRoot == null ? current : "" };
        if (input.ShowDialog(this) != true) return;
        OpenFolderDialog output = new() { Title = "Choose a new or empty folder for the source project" };
        if (output.ShowDialog(this) != true) return;
        bool open = !ViewModel.Documents.Any(d => d.IsDirty) &&
            MessageBox.Show(this, "Open the source project when reconstruction finishes?", "Reconstruct source project", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        var manifest = await ReconstructSourceProjectAsync(input.FolderName, output.FolderName, open, CancellationToken.None);
        string notes = manifest.Notes.Count > 0 ? $"\n{manifest.Notes.Count} notes are listed in Problems." : "";
        MessageBox.Show(this, $"Reconstructed {manifest.Outputs.Count} game files into {output.FolderName}.\nEvery output was verified to pack back byte-identically.{notes}",
            "Source project ready", MessageBoxButton.OK, MessageBoxImage.Information);
    });
    private async void PackSourceClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        OpenFolderDialog output = new() { Title = "Choose a new, empty or previously packed folder for the game files" };
        if (output.ShowDialog(this) != true) return;
        ShowPackResult(await PackSourceProjectAsync(output.FolderName, CancellationToken.None));
    });
    private async void VerifySourceClick(object sender, RoutedEventArgs e) => await RunUi(async () => ShowPackResult(await PackSourceProjectAsync(null, CancellationToken.None)));
    private void ShowPackResult(SourcePackReport report)
    {
        string detail = report.Failed > 0 ? "\nFailures are listed in Problems." : report.Changed > 0 ? "\nChanged outputs contain your source edits." : "\nEvery output matches the shipped files byte for byte.";
        MessageBox.Show(this, ViewModel.Status + detail, "Source project", MessageBoxButton.OK, report.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
    /// <summary>Presentation only: packing commands appear when the open root is a source project.</summary>
    private void ToolsMenuOpened(object sender, RoutedEventArgs e)
    {
        var visibility = SourceProjectRoot != null ? Visibility.Visible : Visibility.Collapsed;
        PackSourceMenu.Visibility = VerifySourceMenu.Visibility = visibility;
    }
}
