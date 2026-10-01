using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    /// <summary>The open root when it is a source project (it has data and gamegen folders).</summary>
    private string? SourceProjectRoot => ViewModel.HasRoot && SourceProject.IsProject(ViewModel.RootPath) ? ViewModel.RootPath : null;
    private static string Bounded(string text, int maximum = 1024) => text.Length <= maximum ? text : text[..maximum] + "…";
    private static string GameFiles(int count) => count == 1 ? "1 game file" : $"{count} game files";
    /// <summary>Test hook: runs after an export or check finishes and before its result is published to the workspace.</summary>
    internal Func<Task>? SourceExportFinishing { get; set; }
    private long sourceMenuGeneration;

    private async Task<SourceReconstructionReport> ReconstructSourceProjectAsync(string source, string destination, bool open, CancellationToken token)
    {
        if (operation != null) throw new StudioCommandException("busy", "An export, validation or source operation is already running.");
        if (open) RequireRootPublication();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); operation = cancellation; CancelOperationItem.IsEnabled = true;
        SourceReconstructionReport report;
        try
        {
            var progress = new Progress<SourceProgress>(p => { if (operation == cancellation && !cancellation.IsCancellationRequested) ViewModel.Status = $"Reconstructing {p.Completed}/{p.Total}: {p.Item}"; });
            report = await Task.Run(() => SourceExtractor.ExtractAsync(source, destination, progress, cancellation.Token), cancellation.Token);
            foreach (string note in report.Notes.Take(256)) ViewModel.AddProblem(Bounded(note), "Warning", destination);
            ViewModel.Status = $"Reconstructed {report.SourceFiles:N0} source files into {destination}";
        }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        finally { operation = null; CancelOperationItem.IsEnabled = false; }
        if (open) { RequireRootPublication(); await ViewModel.OpenRootAsync(report.Project, token, RequireRootPublication); }
        return report;
    }
    private static object ReconstructResult(SourceReconstructionReport report, bool open) => new
    {
        project = report.Project, opened = open, sourceFiles = report.SourceFiles, families = report.Families,
        notReconstructed = report.NotReconstructed.Take(64).Select(n => Bounded(n, 512)).ToArray(), notReconstructedCount = report.NotReconstructed.Count, notReconstructedTruncated = report.NotReconstructed.Count > 64,
        notes = report.Notes.Take(32).Select(n => Bounded(n, 512)).ToArray(), noteCount = report.Notes.Count, notesTruncated = report.Notes.Count > 32
    };

    /// <summary>The build profile Tools → Build profile chose for a project (null: the project's default).</summary>
    private (string Root, string Name)? sourceProfileChoice;
    private string? SourceProfileFor(string root) => sourceProfileChoice is { } choice && choice.Root.Equals(root, StringComparison.OrdinalIgnoreCase) ? choice.Name : null;
    /// <summary>A project's build profile, refused as an invalid argument when its files are malformed or the name is unknown.</summary>
    private static BuildProfile ResolveProfile(string root, string? name)
    {
        try { return BuildProfiles.Find(root, name); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
    }

    /// <summary>Build game files of the open source project from its files on disk into <paramref name="destination"/>, or only check them without one.</summary>
    private async Task<SourceExportReport> ExportSourceProjectAsync(string? destination, IReadOnlyCollection<string>? outputs, bool overwrite, CancellationToken token, string? profile = null)
    {
        if (operation != null) throw new StudioCommandException("busy", "An export, validation or source operation is already running.");
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
        RequireNoDrafts();
        // Exports read source files from disk, so pending edits to them must be saved or discarded first. A source
        // world shows a private build outside the project, but its pending edits belong to the project's scripts.
        string project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), prefix = project + Path.DirectorySeparatorChar;
        if (ViewModel.Documents.FirstOrDefault(d => d.IsDirty && (d.SourceWorld is { } world ? world.Root.Equals(project, StringComparison.OrdinalIgnoreCase)
            : Path.GetFullPath(d.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) is { } dirty)
            throw new StudioCommandException("unsaved_changes", $"Save or discard the edits to {dirty.SourceWorld?.Label ?? Path.GetFileName(dirty.Path)} before exporting.");
        // Opening another root cancels the export; its result never publishes into the new workspace.
        long generation = ViewModel.WorkspaceGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ViewModel.WorkspaceToken); operation = cancellation; CancelOperationItem.IsEnabled = true;
        try
        {
            string verb = destination == null ? "Checking" : "Exporting";
            var progress = new Progress<SourceProgress>(p => { if (operation == cancellation && !cancellation.IsCancellationRequested) ViewModel.Status = $"{verb} {p.Completed}/{p.Total}: {p.Item}"; });
            var report = await Task.Run(() => destination == null ? SourceBuilder.CheckAsync(root, outputs, progress, cancellation.Token, profile) : SourceBuilder.ExportAsync(root, destination, outputs, overwrite, progress, cancellation.Token, profile), cancellation.Token);
            if (SourceExportFinishing is { } finishing) await finishing();
            // Files already written stay written; say so rather than suggesting that nothing happened.
            if (ViewModel.WorkspaceGeneration != generation) throw new StudioCommandException("context_changed", report.Destination == null ? "The workspace changed while checking."
                : $"The workspace changed after the export wrote {GameFiles(report.Built)} to {report.Destination}.");
            foreach (var output in report.Outputs)
            {
                if (output.Error != null) ViewModel.AddProblem(Bounded($"{output.Path}: {output.Error}"), file: root);
                foreach (string warning in output.Warnings.Take(64)) ViewModel.AddProblem(Bounded($"{output.Path}: {warning}"), "Warning", root);
            }
            foreach (string note in report.Notes.Take(64)) ViewModel.AddProblem(Bounded(note), "Warning", report.Destination ?? root);
            ViewModel.Status = destination == null
                ? $"Checked {GameFiles(report.Outputs.Count)}: {report.Built} build, {report.Failed} failed"
                : $"Exported {GameFiles(report.Built)} to {destination}";
            return report;
        }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        finally { operation = null; CancelOperationItem.IsEnabled = false; }
    }
    private static object ExportResult(string root, SourceExportReport report)
    {
        const int shown = 256;
        return new
        {
            project = root, destination = report.Destination, written = report.Destination != null, built = report.Built, failed = report.Failed, profile = report.Profile,
            notes = report.Notes.Take(16).Select(n => Bounded(n, 512)).ToArray(), noteCount = report.Notes.Count,
            outputs = report.Outputs.Take(shown).Select(o => new
            {
                path = o.Path, family = o.Family, status = o.Status, bytes = o.Bytes, items = o.Items,
                warnings = o.Warnings.Take(16).Select(w => Bounded(w, 512)).ToArray(), warningCount = o.Warnings.Count, error = o.Error == null ? null : Bounded(o.Error)
            }).ToArray(),
            outputCount = report.Outputs.Count, outputsTruncated = report.Outputs.Count > shown
        };
    }

    private async Task<object> SourceStatusAsync(JsonObject a, CancellationToken token)
    {
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
        IReadOnlyList<SourceOutputPlan> plan; IReadOnlyList<BuildProfile> profiles; BuildProfile profile;
        try
        {
            profiles = await Task.Run(() => BuildProfiles.List(root), token);
            string? name = a["profile"] is null ? null : Text(a, "profile");
            profile = name == null ? profiles.Single(p => p.IsDefault) : profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"The project has no build profile {name}.");
            plan = await Task.Run(() => SourceBuilder.Plan(root, null, profile), token);
        }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        return new
        {
            project = root, profile = profile.Name,
            profiles = profiles.Select(p => new
            {
                name = p.Name, status = p.Status, @default = p.IsDefault, source = p.Source, description = Bounded(p.Description, 512),
                texturePacks = p.TexturePacks.Select(t => new { file = t.File, budgetMiB = t.BudgetBytes / (1024.0 * 1024), maximumDimension = t.MaximumDimension }).ToArray()
            }).ToArray(),
            families = plan.GroupBy(o => o.Family).ToDictionary(g => g.Key, g => g.Count()),
            outputs = Page(plan, a, o => o.Path, o => new
            {
                path = o.Path, family = o.Family, inputCount = o.Inputs.Count,
                inputs = o.Inputs.Take(16).Select(i => Bounded(i, 512)).ToArray(), inputsTruncated = o.Inputs.Count > 16
            }).Data
        };
    }
    private static IReadOnlyCollection<string>? OutputArguments(JsonObject a) => a["outputs"] switch
    {
        null => null,
        JsonArray list when list.Count is > 0 and <= 256 => list.Select(o => o is JsonValue value && value.TryGetValue<string>(out var path) && path.Length is > 0 and <= 260 ? path
            : throw new StudioCommandException("invalid_argument", "Outputs are game file paths such as m1/zrdr.zbd.")).ToArray(),
        _ => throw new StudioCommandException("invalid_argument", "Outputs must list 1–256 game file paths.")
    };

    private void RegisterSourceCommands(StudioCommands r)
    {
        RegisterJob(r, "source_reconstruct", "Reconstruct a RECOIL source project (data/ and gamegen/ in the original build layout, without zStudio metadata) from a shipped data folder into a new or empty folder. Resources become text .zrd files in their recorded folders, prepared scripts become .gs/.gw text, each sound keeps its best-quality WAV and each texture its best-quality PNG, mission worlds become glTF models loaded by the build scripts, and animations keep their definitions with .zan keyframe scripts; files it does not reconstruct are listed. Optionally opens the project as the workspace root; dirty documents must be resolved first.",
            [P("source", "string", "Shipped game data folder (for example the folder containing interp.zbd and m1\\).", true), P("destination", "string", "New or empty project folder outside the source folder.", true),
             P("open", "boolean", "Open the project as the workspace root afterwards; default true.")], true,
            async (a, token) =>
            {
                bool open = a["open"] == null || Flag(a, "open");
                return Result(ReconstructResult(await ReconstructSourceProjectAsync(Text(a, "source"), Text(a, "destination"), open, token), open));
            });
        RegisterJob(r, "source_export", "Build game files of the open source project from its files on disk. Without destination, only check that they build. With destination (outside the project), stage, re-parse and then write the selected outputs, or nothing if any fails; existing game files are replaced only with overwrite, and restored if publication fails. Outputs work in the game but are not byte-identical to the shipped files. Unsaved edits to project files must be resolved first.",
            [P("destination", "string", "Optional output folder; omit to check without writing."),
             new("outputs", "array", "Optional game files to build, as listed by zstudio_source_status (for example m1/zrdr.zbd); omitted builds all.", Items: new("", "string", "Game file path."), MinItems: 1, MaxItems: 256),
             P("overwrite", "boolean", "Replace game files that already exist in destination; default false."),
             P("profile", "string", "Build profile (zstudio_source_status lists them): which texture packs to build, with what budgets and largest texture side. Default: the project's default profile.")], true,
            async (a, token) =>
            {
                string? profile = a["profile"] == null ? null : Text(a, "profile");
                if (profile != null && SourceProjectRoot is { } root) ResolveProfile(root, profile);
                var report = await ExportSourceProjectAsync(a["destination"] == null ? null : Text(a, "destination"), OutputArguments(a), Flag(a, "overwrite"), token, profile);
                return Result(ExportResult(SourceProjectRoot ?? "", report));
            });
        RegisterJob(r, "source_status", "Describe the open source project: its build profiles (the default marked; built-in original and modern plus gamegen/build-profiles/*.json) and the game files it can build with the chosen profile, with family and source inputs (16 previewed), paged and filtered by path.", [.. PageParameters, P("profile", "string", "Build profile whose texture packs are listed; default: the project's default.")], false,
            async (a, token) => Result(await SourceStatusAsync(a, token)));
    }

    private async void ReconstructSourceClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        OpenFolderDialog input = new() { Title = "Choose the shipped RECOIL data folder (contains interp.zbd and m1)" };
        // An open ZBD folder is the likely source: it starts selected, so confirming the dialog uses it.
        if (ViewModel.HasRoot && SourceProjectRoot == null && Directory.Exists(ViewModel.RootPath))
        {
            string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ViewModel.RootPath));
            input.InitialDirectory = Path.GetDirectoryName(current) ?? current; input.FolderName = current;
        }
        if (input.ShowDialog(this) != true) return;
        OpenFolderDialog output = new() { Title = "Choose a new or empty folder for the source project" };
        if (output.ShowDialog(this) != true) return;
        bool open = !ViewModel.Documents.Any(d => d.IsDirty) &&
            MessageBox.Show(this, "Open the source project when reconstruction finishes?", "Reconstruct source project", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        var report = await ReconstructSourceProjectAsync(input.FolderName, output.FolderName, open, CancellationToken.None);
        string skipped = report.NotReconstructed.Count > 0 ? $"\n{report.NotReconstructed.Count} game files were not reconstructed: {string.Join(", ", report.NotReconstructed.Take(5))}{(report.NotReconstructed.Count > 5 ? ", …" : "")}." : "";
        string notes = report.Notes.Count > 0 ? $"\n{report.Notes.Count} notes are listed in Problems." : "";
        MessageBox.Show(this, $"Reconstructed {report.SourceFiles:N0} source files into {output.FolderName}.{skipped}{notes}", "Source project ready", MessageBoxButton.OK, MessageBoxImage.Information);
    });
    private async void ExportSourceClick(object sender, RoutedEventArgs e) => await RunUi(() => ExportSourceInteractiveAsync(null));
    private async Task ExportSourceInteractiveAsync(IReadOnlyCollection<string>? outputs)
    {
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project first.");
        OpenFolderDialog folder = new() { Title = outputs == null ? "Choose a folder for the game files" : $"Choose a folder for {string.Join(", ", outputs)}" };
        if (folder.ShowDialog(this) != true) return;
        // Existing game files are replaced only after an explicit confirmation.
        string? profileName = SourceProfileFor(root); var profile = ResolveProfile(root, profileName);
        var plan = await Task.Run(() => SourceBuilder.Plan(root, null, profile));
        var existing = plan.Where(p => outputs == null || outputs.Contains(p.Path, StringComparer.OrdinalIgnoreCase)).Select(p => p.Path)
            .Where(p => File.Exists(Path.Combine(folder.FolderName, p))).ToArray();
        bool overwrite = false;
        if (existing.Length > 0)
        {
            string list = string.Join("\n", existing.Take(12)) + (existing.Length > 12 ? $"\n… and {existing.Length - 12} more" : "");
            if (MessageBox.Show(this, $"{folder.FolderName} already has these game files:\n{list}\n\nReplace them?", "Export ZBD files", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            overwrite = true;
        }
        ShowExportResult(await ExportSourceProjectAsync(folder.FolderName, outputs, overwrite, CancellationToken.None, profileName));
    }
    private async void CheckSourceClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
        ShowExportResult(await ExportSourceProjectAsync(null, null, false, CancellationToken.None, SourceProjectRoot is { } root ? SourceProfileFor(root) : null)));
    private void ShowExportResult(SourceExportReport report)
    {
        int warnings = report.Outputs.Sum(o => o.Warnings.Count) + report.Notes.Count;
        string detail = report.Failed > 0 ? "\nFailures are listed in Problems." : warnings > 0 ? $"\n{warnings} warnings are listed in Problems." : "";
        MessageBox.Show(this, ViewModel.Status + detail, "Source project", MessageBoxButton.OK, report.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    /// <summary>Presentation only: export commands appear when the open root is a source project.</summary>
    private void ToolsMenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender) return;
        string? root = SourceProjectRoot;
        ExportSourceMenu.Visibility = ExportSourceFileMenu.Visibility = CheckSourceMenu.Visibility = SourceProfileMenu.Visibility = SourceWorldMenu.Visibility = AddSourceModelMenu.Visibility =
            EditInBlenderMenu.Visibility = UpdateFromBlenderMenu.Visibility = root != null ? Visibility.Visible : Visibility.Collapsed;
        AddSourceModelMenu.IsEnabled = UpdateFromBlenderMenu.IsEnabled = ViewModel.SelectedDocument?.SourceWorld is { IsRebuilding: false } && !sourceWorkspaceBusy;
        EditInBlenderMenu.IsEnabled = ViewModel.SelectedDocument?.SourceWorld != null && selectedNode != null;
        if (root != null) { _ = FillExportSourceFileMenuAsync(root); _ = FillSourceWorldMenuAsync(root); FillSourceProfileMenu(root); }
    }
    /// <summary>Lists the project's build profiles; the checked one is what exports and checks build until another is chosen.</summary>
    private void FillSourceProfileMenu(string root)
    {
        SourceProfileMenu.Items.Clear();
        IReadOnlyList<BuildProfile> profiles;
        try { profiles = BuildProfiles.List(root); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { SourceProfileMenu.Items.Add(new MenuItem { Header = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, IsEnabled = false }); return; }
        string chosen = SourceProfileFor(root) ?? profiles.Single(p => p.IsDefault).Name;
        foreach (var profile in profiles)
        {
            MenuItem item = new()
            {
                Header = new TextBlock { Text = profile.Name + (profile.IsDefault ? " (default)" : "") + (profile.Status == "experimental" ? " · experimental" : "") },
                IsCheckable = true, IsChecked = profile.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase),
                ToolTip = $"{profile.Description}\n{string.Join(", ", profile.TexturePacks.Select(p => p.File))}" + (profile.Source is { } source ? $"\n{source}" : "\nBuilt in"),
            };
            System.Windows.Automation.AutomationProperties.SetName(item, profile.Name);
            string name = profile.Name; bool isDefault = profile.IsDefault;
            item.Click += (_, _) => { sourceProfileChoice = isDefault ? null : (root, name); ViewModel.Status = $"Exports build the {name} profile."; };
            SourceProfileMenu.Items.Add(item);
        }
    }
    /// <summary>Lists the project's game files off the UI thread; a newer menu opening or root supersedes the listing.</summary>
    private async Task FillExportSourceFileMenuAsync(string root)
    {
        long generation = ++sourceMenuGeneration;
        ExportSourceFileMenu.Items.Clear();
        ExportSourceFileMenu.Items.Add(new MenuItem { Header = "Reading source project…", IsEnabled = false });
        IReadOnlyList<SourceOutputPlan>? plan = null; string? error = null; string? profile = SourceProfileFor(root);
        try { plan = await Task.Run(() => SourceBuilder.Plan(root, null, BuildProfiles.Find(root, profile))); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { error = ex.Message; }
        if (generation != sourceMenuGeneration || SourceProjectRoot != root) return;
        ExportSourceFileMenu.Items.Clear();
        if (plan == null || plan.Count == 0) { ExportSourceFileMenu.Items.Add(new MenuItem { Header = new TextBlock { Text = error ?? "Nothing to build yet" }, IsEnabled = false }); return; }
        foreach (var output in plan)
        {
            // Paths are literal text, not menu access-key labels.
            MenuItem item = new() { Header = new TextBlock { Text = output.Path }, ToolTip = $"{output.Inputs.Count:N0} source files ({output.Family})" };
            System.Windows.Automation.AutomationProperties.SetName(item, output.Path);
            string path = output.Path;
            item.Click += async (_, _) => await RunUi(() => ExportSourceInteractiveAsync([path]));
            ExportSourceFileMenu.Items.Add(item);
        }
    }
}
