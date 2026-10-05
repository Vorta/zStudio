using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Worlds;
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

    /// <summary>Reconstructs a source project and optionally opens it (source_reconstruct; the welcome screen's Initialize writes and opens it in two steps).</summary>
    private async Task<SourceReconstructionReport> ReconstructSourceProjectAsync(string source, string destination, bool open, CancellationToken token)
    {
        if (operation != null) throw new StudioCommandException("busy", "An export, validation or source operation is already running.");
        if (open) RequireRootPublication();
        var report = await ExtractSourceProjectAsync(source, destination, token);
        try
        {
            if (open) { RequireRootPublication(); await ViewModel.OpenRootAsync(report.Project, token, RequireRootPublication); }
        }
        finally { ListReconstructionNotes(report); }
        return report;
    }
    // Opening a root clears Problems, so a reconstruction's notes are listed once its project is open (or has failed to open).
    private void ListReconstructionNotes(SourceReconstructionReport report) { foreach (string note in report.Notes.Take(256)) ViewModel.AddProblem(Bounded(note), "Warning", report.Project); }

    /// <summary>Writes a source project without opening it; <paramref name="stage"/> also receives the progress shown in the status bar.</summary>
    private async Task<SourceReconstructionReport> ExtractSourceProjectAsync(string source, string destination, CancellationToken token, IProgress<string>? stage = null)
    {
        if (operation != null) throw new StudioCommandException("busy", "An export, validation or source operation is already running.");
        // A relative folder would resolve against zStudio's own folder, which a new build replaces.
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(destination))
            throw new StudioCommandException("invalid_argument", "Give the game files and the project folder as full paths, such as D:\\Recoil\\zbd.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); operation = cancellation; CancelOperationItem.IsEnabled = true;
        SourceReconstructionReport report;
        try
        {
            // After the game files the counter stays at its total, so later work shows what it is doing instead.
            var progress = new Progress<SourceProgress>(p =>
            {
                if (operation != cancellation || cancellation.IsCancellationRequested) return;
                ViewModel.Status = p.Stage switch
                {
                    SourceStage.Reconstructing => $"Reconstructing {p.Item}",
                    SourceStage.Validating => $"Validating {p.Item}",
                    _ => $"Reconstructing {p.Completed}/{p.Total}: {p.Item}",
                };
                stage?.Report(ViewModel.Status);
            });
            report = await Task.Run(() => SourceExtractor.ExtractAsync(source, destination, progress, cancellation.Token), cancellation.Token);
            ViewModel.Status = $"Reconstructed {report.SourceFiles:N0} source files into {destination}";
        }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        finally { operation = null; CancelOperationItem.IsEnabled = false; }
        return report;
    }

    /// <summary>Refuses a folder that is not an initialized source project (one with data and gamegen folders).</summary>
    private static async Task RequireSourceProjectAsync(string path, CancellationToken token)
    {
        // Folder checks can block on unavailable network shares; keep them off the dispatcher.
        if (!await Task.Run(() => SourceProject.IsProject(path), token).WaitAsync(token))
            throw new StudioCommandException("not_project", $"{path} is not a source project: it has no data and gamegen folders. Initialize one from the retail ZBD files first.");
    }
    private void ShowReconstructionSummary(SourceReconstructionReport report)
    {
        string skipped = report.NotReconstructed.Count > 0 ? $"\n{report.NotReconstructed.Count} files were not reconstructed: {string.Join(", ", report.NotReconstructed.Take(5))}{(report.NotReconstructed.Count > 5 ? ", …" : "")}." : "";
        string notes = report.Notes.Count > 0 ? $"\n{report.Notes.Count} notes are listed in Problems." : "";
        MessageBox.Show(this, $"Reconstructed {report.SourceFiles:N0} source files into {report.Project}.{skipped}{notes}", "Source project ready", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private static object ReconstructResult(SourceReconstructionReport report, bool open) => new
    {
        project = report.Project, opened = open, sourceFiles = report.SourceFiles, families = report.Families,
        notReconstructed = report.NotReconstructed.Take(64).Select(n => Bounded(n, 512)).ToArray(), notReconstructedCount = report.NotReconstructed.Count, notReconstructedTruncated = report.NotReconstructed.Count > 64,
        notes = report.Notes.Take(32).Select(n => Bounded(n, 512)).ToArray(), noteCount = report.Notes.Count, notesTruncated = report.Notes.Count > 32
    };

    /// <summary>The build profile Tools → Build profile chose for a project (null: the project's default).</summary>
    private (string Root, string Name)? sourceProfileChoice;
    private string? SourceProfileFor(string root)
    {
        if (sourceProfileChoice is not { } choice || !choice.Root.Equals(root, StringComparison.OrdinalIgnoreCase)) return null;
        // A chosen profile whose file was removed falls back to the project's default; a broken one stays chosen, so the
        // export reports it rather than building another profile's packs.
        try { if (BuildProfiles.Exists(root, choice.Name)) return choice.Name; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return choice.Name; }
        sourceProfileChoice = null; return null;
    }
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
        // A relative folder would resolve against zStudio's own folder, which a new build replaces.
        if (destination != null && !Path.IsPathFullyQualified(destination))
            throw new StudioCommandException("invalid_argument", "Give the destination as a full path, such as D:\\Recoil\\game.");
        RequireNoDrafts();
        // Exports read source files from disk, so pending edits to them must be saved or discarded first. A source
        // world shows a private build in the project's zstudio/cache/worlds, but its pending edits belong to the project's scripts.
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
            // The lookups by name several nodes share, which edits can make find another node; each run replaces the last list.
            foreach (var old in ViewModel.Problems.Where(p => p.Severity == "Info" && p.File == root && p.Message.Contains(" nodes have the name; the game finds ", StringComparison.Ordinal)).ToArray()) ViewModel.Problems.Remove(old);
            foreach (var lookup in report.Lookups.Take(256))
                ViewModel.AddProblem(Bounded($"{lookup.Mission}: {WorldLookups.Describe(lookup)}: {lookup.Candidates} nodes have the name; the game finds {lookup.Found} (slot {lookup.Slot})."), "Info", root);
            ViewModel.Status = destination == null
                ? $"Checked {GameFiles(report.Outputs.Count)}: {report.Built} build, {report.Failed} failed"
                : $"Exported {GameFiles(report.Built)} to {destination}";
            return report;
        }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        finally { operation = null; CancelOperationItem.IsEnabled = false; }
    }
    private static object Lookup(SourceLookup l) => new { mission = l.Mission, kind = l.Kind, name = Bounded(l.Name, 64), source = Bounded(l.Source, 256), candidates = l.Candidates, slot = l.Slot, found = l.Found == null ? null : Bounded(l.Found, 512) };
    private static object ExportResult(string root, SourceExportReport report)
    {
        const int shown = 256;
        return new
        {
            project = root, destination = report.Destination, written = report.Destination != null, built = report.Built, failed = report.Failed, profile = report.Profile,
            notes = report.Notes.Take(16).Select(n => Bounded(n, 512)).ToArray(), noteCount = report.Notes.Count,
            lookups = report.Lookups.Take(shown).Select(Lookup).ToArray(), lookupCount = report.Lookups.Count, lookupsTruncated = report.Lookups.Count > shown,
            lookupChanges = report.LookupChanges.Take(64).Select(c => new { before = Lookup(c.Before), after = Lookup(c.After), uncertain = c.Uncertain }).ToArray(), lookupChangeCount = report.LookupChanges.Count,
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
            // Without a profile, the one chosen in Tools → Build profile (as source_export uses), else the project's default.
            string? name = a["profile"] is null ? SourceProfileFor(root) : Text(a, "profile");
            profile = name == null ? profiles.Single(p => p.IsDefault) : profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"The project has no build profile {name}.");
            // A profile chosen in Tools → Build profile whose file can no longer be used: the status still lists every
            // profile, with that one's error and no outputs, so the choice can be seen and another profile named.
            if (profile.Error != null && a["profile"] is not null) throw new InvalidDataException(profile.Error);
            plan = profile.Error != null ? [] : await Task.Run(() => SourceBuilder.Plan(root, null, profile), token);
        }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        return new
        {
            project = root, profile = profile.Name, profileError = profile.Error == null ? null : Bounded(profile.Error, 512), selected = SourceProfileFor(root) ?? profiles.Single(p => p.IsDefault).Name,
            profiles = profiles.Select(p => new
            {
                name = p.Name, status = p.Status, @default = p.IsDefault, source = p.Source, description = Bounded(p.Description, 512), error = p.Error == null ? null : Bounded(p.Error, 512),
                texturePacks = p.TexturePacks.Select(t => new { file = t.File, automatic = t.Automatic, budgetMiB = t.BudgetBytes / (1024.0 * 1024), maximumDimension = t.MaximumDimension, missions = t.Missions }).ToArray()
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
        RegisterJob(r, "source_reconstruct", "Reconstruct a RECOIL source project (data/ and gamegen/ in the original build layout, without zStudio metadata) from a shipped data folder into a new or empty folder. Only the original shipped files can be unpacked (their resource archives carry the animation definitions); exported files are refused. Resources become text .zrd files in their recorded folders and animation definitions .zad files beside them (pickup.zrd keeps its pickup data, its animation goes to pickup.zad), prepared scripts become .gs/.gw text, each sound keeps its best-quality WAV and each texture its best-quality PNG, mission worlds become glTF models loaded by the build scripts, and animations keep their definitions with .zan keyframe scripts; files it does not reconstruct are listed. Optionally opens the project as the workspace root; dirty documents must be resolved first.",
            [P("source", "string", "Full path of the shipped game data folder (for example the folder containing interp.zbd and m1\\).", true), P("destination", "string", "Full path of a new or empty project folder outside the source folder.", true),
             P("open", "boolean", "Open the project as the workspace root afterwards; default true.")], true,
            async (a, token) =>
            {
                bool open = a["open"] == null || Flag(a, "open");
                return Result(ReconstructResult(await ReconstructSourceProjectAsync(Text(a, "source"), Text(a, "destination"), open, token), open));
            });
        RegisterJob(r, "source_export", "Build game files of the open source project from its files on disk. Without destination, only check that they build. With destination (outside the project), stage, re-parse and then write the selected outputs, or nothing if any fails; existing game files are replaced only with overwrite, and restored if publication fails. Outputs work in the game but are not byte-identical to the shipped files. Unsaved edits to project files must be resolved first. The result lists the lookups by name the built missions make as the game loads them (texture-effect FindNode, animation roots, attach nodes outside their root, node and tracked-node names inside animations that fall back to the whole world, the first node of each activation prerequisite path) whose name several nodes share, with the node the game finds (lookups, at most 256), and those that find another node than in the destination files the export replaced (lookupChanges, also in notes).",
            [P("destination", "string", "Optional output folder (a full path); omit to check without writing."),
             new("outputs", "array", "Optional game files to build, as listed by zstudio_source_status (for example m1/zrdr.zbd); omitted builds all.", Items: new("", "string", "Game file path."), MinItems: 1, MaxItems: 256),
             P("overwrite", "boolean", "Replace game files that already exist in destination; default false."),
             P("profile", "string", "Build profile (zstudio_source_status lists them): which texture packs to build, with what budgets and largest texture side. Default: the profile checked in Tools → Build profile (source_status selected), which is the project's default unless the user chose another.")], true,
            async (a, token) =>
            {
                // Without profile, the one Tools → Build profile shows checked (the project's default unless the user chose another).
                string? profile = a["profile"] == null ? SourceProjectRoot is { } chosenRoot ? SourceProfileFor(chosenRoot) : null : Text(a, "profile");
                if (profile != null && SourceProjectRoot is { } root) ResolveProfile(root, profile);
                var report = await ExportSourceProjectAsync(a["destination"] == null ? null : Text(a, "destination"), OutputArguments(a), Flag(a, "overwrite"), token, profile);
                return Result(ExportResult(SourceProjectRoot ?? "", report));
            });
        RegisterJob(r, "source_status", "Describe the open source project: its build profiles (the default marked; built-in original and modern plus gamegen/build-profiles/*.json) and the game files it can build with the chosen profile, with family and source inputs (16 previewed), paged and filtered by path.", [.. PageParameters, P("profile", "string", "Build profile whose texture packs are listed; default: the profile chosen in Tools → Build profile, else the project's default.")], false,
            async (a, token) => Result(await SourceStatusAsync(a, token)));
    }

    // Welcome screen, Work with source project: Initialize unpacks the retail files into a new project and opens it.
    private async void WelcomeInitializeClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        var dialog = CreateInitializeDialog();
        if (dialog.ShowDialog() != true || dialog.Report is not { } report) return;
        await OpenInitializedProjectAsync(report);
        ShowReconstructionSummary(report);
    });
    // The dialog writes the project while it shows the progress; the root is replaced once it has closed, since a modal
    // dialog holds the workspace.
    internal SourceInitializeDialog CreateInitializeDialog() => new(this, null, (retail, project, progress, token) => ExtractSourceProjectAsync(retail, project, token, progress));
    internal async Task OpenInitializedProjectAsync(SourceReconstructionReport report)
    {
        try { await ViewModel.OpenRootAsync(report.Project); UpdateRecent(); }
        finally { ListReconstructionNotes(report); }
    }
    // Welcome screen, Work with source project: Open accepts only an initialized project folder.
    private async void WelcomeOpenProjectClick(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new() { Title = "Choose a source project folder (contains data and gamegen)", InitialDirectory = Directory.Exists(ViewModel.Settings.LastRoot) ? ViewModel.Settings.LastRoot : "" };
        if (dialog.ShowDialog(this) != true) return;
        await RunUi(async () =>
        {
            try { await RequireSourceProjectAsync(dialog.FolderName, CancellationToken.None); }
            catch (StudioCommandException ex) when (ex.Code == "not_project") { MessageBox.Show(this, ex.Message, "Open source project", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            await ViewModel.OpenRootAsync(dialog.FolderName); UpdateRecent();
        });
    }
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
        SourceMenuSeparator.Visibility = ExportSourceMenu.Visibility = ExportSourceFileMenu.Visibility = CheckSourceMenu.Visibility = SourceProfileMenu.Visibility = SourceWorldMenu.Visibility = AddSourceModelMenu.Visibility =
            EditInBlenderMenu.Visibility = UpdateFromBlenderMenu.Visibility = CreateTerrainMenu.Visibility = ConvertTerrainMenu.Visibility = SourceRecoveryMenu.Visibility = root != null ? Visibility.Visible : Visibility.Collapsed;
        AddSourceModelMenu.IsEnabled = UpdateFromBlenderMenu.IsEnabled = CreateTerrainMenu.IsEnabled = ConvertTerrainMenu.IsEnabled = ViewModel.SelectedDocument?.SourceWorld is { IsRebuilding: false } && !sourceWorkspaceBusy;
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
        // A chosen profile whose file was removed falls back to the default.
        if (SourceProfileFor(root) is { } stale && !profiles.Any(p => p.Name.Equals(stale, StringComparison.OrdinalIgnoreCase))) sourceProfileChoice = null;
        string chosen = SourceProfileFor(root) ?? profiles.Single(p => p.IsDefault).Name;
        foreach (var profile in profiles)
        {
            // A file that cannot be used is shown with the reason, and cannot be chosen; one chosen before stays chosen, so
            // exports report it rather than building another profile's packs.
            if (profile.Error is { } error)
            {
                MenuItem broken = new() { Header = new TextBlock { Text = profile.Name + " · cannot be used" }, IsCheckable = true, IsChecked = profile.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase), IsEnabled = false, ToolTip = Bounded(error, 512) };
                System.Windows.Automation.AutomationProperties.SetName(broken, profile.Name);
                ToolTipService.SetShowOnDisabled(broken, true);
                SourceProfileMenu.Items.Add(broken);
                continue;
            }
            MenuItem item = new()
            {
                Header = new TextBlock { Text = profile.Name + (profile.IsDefault ? " (default)" : "") + (profile.Status == "experimental" ? " · experimental" : "") },
                IsCheckable = true, IsChecked = profile.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase),
                ToolTip = $"{profile.Description}\n{string.Join(", ", profile.TexturePacks.Select(p => p.File + (p.Automatic ? $" (automatic, up to {p.BudgetBytes / (1024 * 1024)} MB)" : "") + (p.Missions is { } missions ? $" ({string.Join(", ", missions)})" : "")))}" + (profile.Source is { } source ? $"\n{source}" : "\nBuilt in"),
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
