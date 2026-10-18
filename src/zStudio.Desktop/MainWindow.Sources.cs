using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using zStudio.Automation;
using zStudio.Core.Worlds;
using zStudio.Core.Formats;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    /// <summary>The open root when it is a source project (it has data and gamegen folders).</summary>
    private string? SourceProjectRoot => ViewModel.HasRoot && ViewModel.IsSourceProject ? ViewModel.RootPath : null;
    private static string Bounded(string text, int maximum = 1024) => text.Length <= maximum ? text : text[..maximum] + "…";
    private static string GameFiles(int count) => count == 1 ? "1 game file" : $"{count} game files";
    private const int SourceWarningsShown = 64;
    private const int ReconstructionNotesShown = 256;
    internal static string SourceProblemText(string context, string message) => context.Length == 0 ? Bounded(message, 1023)
        : $"{Bounded(context, 255)}: {Bounded(message, 765)}";
    internal static IEnumerable<string> SourceWarningMessages(IReadOnlyList<string> warnings, string context, int maximum = SourceWarningsShown, string noun = "warnings")
    {
        int shown = Math.Min(warnings.Count, maximum);
        for (int i = 0; i < shown; i++) yield return SourceProblemText(context, warnings[i]);
        if (shown < warnings.Count)
            yield return SourceProblemText(context, $"Showing {shown} of {warnings.Count} {noun}; {warnings.Count - shown} more not shown.");
    }
    internal static string SourceWarningSummary(long total, long shown, string noun = "warnings") => total == 0 ? ""
        : shown < total ? $"\n{shown} of {total} {noun} are listed in Diagnostics; {total - shown} more not shown."
        : total == 1 ? $"\n1 {noun.TrimEnd('s')} is listed in Diagnostics." : $"\n{total} {noun} are listed in Diagnostics.";
    /// <summary>Test hook: runs after an export or check finishes and before its result is published to the workspace.</summary>
    internal Func<Task>? SourceExportFinishing { get; set; }
    /// <summary>Test hook: pauses a completed reconstruction before its optional workspace publication.</summary>
    internal Func<Task>? SourceReconstructionFinishing { get; set; }
    private long sourceMenuGeneration;

    /// <summary>
    /// Reconstructs a source project and optionally opens it (source_reconstruct; the welcome screen's Initialize writes and
    /// opens it in two steps). A refusal to open the written project (unsaved changes or drafts arrived meanwhile, a busy or
    /// changed workspace) leaves it unopened in its folder and is returned with it, so the result still names the project.
    /// </summary>
    private async Task<(SourceReconstructionReport Report, StudioCommandException? NotOpened)> ReconstructSourceProjectAsync(string source, string destination, bool open, CancellationToken token)
    {
        if (operation != null) throw new StudioCommandException("busy", "An export, validation or source operation is already running.");
        if (open) RequireRootPublication();
        long generation = ViewModel.WorkspaceGeneration;
        long navigation = open ? ViewModel.BeginNavigation(token) : ViewModel.NavigationGeneration;
        var report = await ExtractSourceProjectAsync(source, destination, token);
        if (SourceReconstructionFinishing is { } finishing) await finishing();
        // The project is complete in its folder: a cancel arriving from now on cannot take it back, so the operation
        // completes and names it (opened, or with the reason it was not).
        CommitRunningJob();
        try
        {
            if (open)
            {
                if (navigation != ViewModel.NavigationGeneration)
                    throw new StudioCommandException("context_changed", "A newer workspace navigation superseded opening the reconstructed project; it stays in its folder.");
                RequireReconstructionPublication(); await ViewModel.OpenRootAsync(report.Project, token, RequireReconstructionPublication);
            }
        }
        catch (StudioCommandException refusal) { return (report, refusal); }
        // Canceled before the project became the root: it stays unopened. Once it is the root, the open completes (a source
        // project has no indexing for a cancel to stop).
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return (report, new StudioCommandException("canceled", "The operation was canceled before the project was opened; it stays in its folder.")); }
        finally { ListReconstructionNotes(report); }
        return (report, null);

        void RequireReconstructionPublication()
        {
            if (generation != ViewModel.WorkspaceGeneration)
                throw new StudioCommandException("context_changed", "The workspace changed while the project was reconstructed; it stays in its folder and can be opened separately.");
            RequireRootPublication();
        }
    }
    // Opening a root clears Problems, so a reconstruction's notes are listed once its project is open (or has failed to open).
    private void ListReconstructionNotes(SourceReconstructionReport report)
    {
        foreach (string note in SourceWarningMessages(report.Notes, "", ReconstructionNotesShown, "notes"))
            ViewModel.AddProblem(note, "Warning", report.Project);
    }

    /// <summary>Writes a source project without opening it; <paramref name="stage"/> also receives the progress shown in the status bar.</summary>
    private async Task<SourceReconstructionReport> ExtractSourceProjectAsync(string source, string destination, CancellationToken token, IProgress<string>? stage = null)
    {
        if (operation != null) throw new StudioCommandException("busy", "An export, validation or source operation is already running.");
        // A relative folder would resolve against zStudio's own folder, which a new build replaces.
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(destination))
            throw new StudioCommandException("invalid_argument", "Give the game files and the project folder as full paths, such as D:\\Recoil\\zbd.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); operation = cancellation;
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
        finally { operation = null; }
        return report;
    }

    internal Action<CancellationToken>? SourceProjectChecking { get; set; }
    /// <summary>Refuses an invalid source project or a check superseded by a newer workspace.</summary>
    private async Task RequireSourceProjectAsync(string path, CancellationToken token)
    {
        long navigation = ViewModel.BeginNavigation(token);
        // Folder checks can block on unavailable network shares; keep them off the dispatcher.
        string? refusal = await Task.Run(() =>
        {
            SourceProjectChecking?.Invoke(token);
            if (!Directory.Exists(path)) return $"{path} does not exist.";
            if (!SourceProject.IsProject(path))
            {
                bool data = Directory.Exists(Path.Combine(path, "data")), gamegen = Directory.Exists(Path.Combine(path, "gamegen"));
                string missing = data ? "gamegen folder" : gamegen ? "data folder" : "data or gamegen folder";
                return $"{path} is not a source project: it has no {missing}. A source project holds both, as Initialize writes them from the retail ZBD files; choose the folder that contains them, or initialize one first.";
            }
            // As the workspace would refuse to edit it (and Initialize to write it): nothing may be written through a link.
            try { SourceProject.RejectLinkedProject(path); SourceProject.RequireCurrentFormat(path, token); return null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return ex.Message; }
        }, token).WaitAsync(token);
        if (refusal != null) throw new StudioCommandException("not_project", refusal);
        if (navigation != ViewModel.NavigationGeneration)
            throw new StudioCommandException("context_changed", "The workspace changed while the source project folder was checked; open the project again if wanted.");
    }
    private void ShowReconstructionSummary(SourceReconstructionReport report)
    {
        string skipped = report.NotReconstructed.Count > 0 ? $"\n{report.NotReconstructed.Count} files were not reconstructed: {string.Join(", ", report.NotReconstructed.Take(5))}{(report.NotReconstructed.Count > 5 ? ", …" : "")}." : "";
        string notes = SourceWarningSummary(report.Notes.Count, Math.Min(report.Notes.Count, ReconstructionNotesShown), "notes");
        string text = $"Reconstructed {report.SourceFiles:N0} source files into {report.Project}.{skipped}{notes}";
        // The map picker has no Diagnostics pane: the notes are listed here too.
        if (report.Notes.Count > 0 && NoMapOpen) DetailDialog.Show(this, "Source project ready", Below(text) + PickerDetails(report.Notes));
        else MessageBox.Show(this, text, "Source project ready", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private bool NoMapOpen => ViewModel.SelectedDocument?.SourceWorld == null;
    /// <summary>A refused or failed run on the map picker: its whole message in a window, and in Diagnostics for later.</summary>
    private void ShowPickerFailure(string title, StudioCommandException ex)
    {
        ViewModel.Status = Bounded(ex.Message, 512); ViewModel.AddProblem(ex.Message, file: SourceProjectRoot);
        DetailDialog.Show(this, title, ex.Message);
    }
    /// <summary>A result shown on the map picker lists its problems below, as Diagnostics does once a map is open.</summary>
    private static string Below(string text) => text.Replace("listed in Diagnostics", "in Diagnostics once a map is open (the first 40 are listed below)", StringComparison.Ordinal);
    /// <summary>The first lines of a result's problems, for the map picker (which has no Diagnostics pane).</summary>
    private static string PickerDetails(IEnumerable<string> lines)
    {
        const int Shown = 40;
        var list = lines.Take(Shown + 1).Select(line => "• " + Bounded(line, 512)).ToList();
        return "\n\n" + string.Join("\n", list.Take(Shown)) + (list.Count > Shown ? "\n…" : "");
    }
    private static object ReconstructResult(SourceReconstructionReport report, bool open) => new
    {
        project = report.Project, opened = open, sourceFiles = report.SourceFiles, families = report.Families,
        notReconstructed = report.NotReconstructed.Take(64).Select(n => Bounded(n, 512)).ToArray(), notReconstructedCount = report.NotReconstructed.Count, notReconstructedTruncated = report.NotReconstructed.Count > 64,
        notes = report.Notes.Take(32).Select(n => Bounded(n, 512)).ToArray(), noteCount = report.Notes.Count, notesTruncated = report.Notes.Count > 32
    };

    /// <summary>The build profile Export → Build profile chose for a project (null: the project's default).</summary>
    private (string Root, string Name)? sourceProfileChoice;
    internal Action<CancellationToken>? SourceProfilesReading { get; set; }
    private long sourceProfileSelection, sourceProfileMenuRequest;
    private async Task<object> SelectSourceProfileAsync(string root, string? name, CancellationToken token = default)
    {
        if (SourceProjectRoot != root) throw new StudioCommandException("context_changed", "The source project changed; choose its profile again.");
        if (operation != null) throw new StudioCommandException("busy", "Another source operation is running.");
        long generation = ViewModel.WorkspaceGeneration, request = ++sourceProfileSelection;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ViewModel.WorkspaceToken);
        operation = cancellation;
        BuildProfile profile; IReadOnlyList<BuildProfile>? profiles; string? menuError;
        try
        {
            (profile, profiles, menuError) = await Task.Run(() =>
            {
                SourceProfilesReading?.Invoke(cancellation.Token);
                var selected = ResolveProfile(root, name, cancellation.Token);
                // A malformed unrelated/default profile may prevent listing, but must not block a valid named choice.
                try { return (selected, (IReadOnlyList<BuildProfile>?)BuildProfiles.List(root, token: cancellation.Token), (string?)null); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                { return (selected, (IReadOnlyList<BuildProfile>?)null, (string?)Bounded(ex.Message, 512)); }
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != ViewModel.WorkspaceGeneration || request != sourceProfileSelection || SourceProjectRoot != root)
                throw new StudioCommandException("context_changed", "The source project changed; choose its profile again.");
        }
        finally { if (operation == cancellation) { operation = null; } }
        sourceProfileChoice = profile.IsDefault ? null : (root, profile.Name);
        ++sourceProfileMenuRequest; // A scan started before this choice must not replace its menu.
        if (profiles != null) FillSourceProfileMenu(root, profiles);
        else { SourceProfileMenu.Items.Clear(); SourceProfileMenu.Items.Add(new MenuItem { Header = new TextBlock { Text = menuError, MaxWidth = 420, TextWrapping = TextWrapping.Wrap }, IsEnabled = false }); }
        _ = FillExportSourceFileMenuAsync(root);
        ViewModel.Status = $"Exports build the {profile.Name} profile.";
        return new { project = root, profile = profile.Name, usesDefault = profile.IsDefault };
    }
    private async Task<string?> SourceProfileForAsync(string root, CancellationToken token = default)
    {
        if (sourceProfileChoice is not { } choice || !choice.Root.Equals(root, StringComparison.OrdinalIgnoreCase)) return null;
        // A chosen profile whose file was removed falls back to the project's default; a broken one stays chosen, so the
        // export reports it rather than building another profile's packs.
        long generation = ViewModel.WorkspaceGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ViewModel.WorkspaceToken);
        bool exists;
        try { exists = await Task.Run(() => BuildProfiles.Exists(root, choice.Name, token: cancellation.Token), cancellation.Token); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { exists = true; }
        cancellation.Token.ThrowIfCancellationRequested();
        if (generation != ViewModel.WorkspaceGeneration || sourceProfileChoice != choice) throw new StudioCommandException("context_changed", "The project or profile choice changed.");
        if (exists) return choice.Name;
        sourceProfileChoice = null;
        // Another pending output scan may still hold the removed choice and refuse its result.
        _ = FillExportSourceFileMenuAsync(root);
        return null;
    }
    /// <summary>A project's build profile, refused as an invalid argument when its files are malformed or the name is unknown.</summary>
    private static BuildProfile ResolveProfile(string root, string? name, CancellationToken token = default)
    {
        try { return BuildProfiles.Find(root, name, token: token); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
    }

    /// <summary>Build game files of the open source project from its files on disk into <paramref name="destination"/>, or only check them without one.</summary>
    internal async Task<SourceExportReport> ExportSourceProjectAsync(string? destination, IReadOnlyCollection<string>? outputs, bool overwrite, CancellationToken token, string? profile = null, long? expectedGeneration = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? confirmReplace = null, IProgress<SourceProgress>? observer = null)
    {
        RequireExportGeneration(expectedGeneration);
        if (operation != null) throw new StudioCommandException("busy", "An export, validation or source operation is already running.");
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
        // A relative folder would resolve against zStudio's own folder, which a new build replaces.
        if (destination != null && !Path.IsPathFullyQualified(destination))
            throw new StudioCommandException("invalid_argument", "Give the destination as a full path, such as D:\\Recoil\\game.");
        RequireNoDrafts();
        // Exports read source files from disk, so pending edits to them must be saved or discarded first. A source
        // world evaluates its source workspace in memory; pending edits belong to the owning project files.
        string project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), prefix = Path.EndsInDirectorySeparator(project) ? project : project + Path.DirectorySeparatorChar;
        if (ViewModel.Documents.FirstOrDefault(d => d.IsDirty && (d.SourceWorld is { } world ? world.Root.Equals(project, StringComparison.OrdinalIgnoreCase)
            : Path.GetFullPath(d.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) is { } dirty)
            throw new StudioCommandException("unsaved_changes", $"Save or discard the edits to {dirty.SourceWorld?.Label ?? Path.GetFileName(dirty.Path)} before exporting.");
        // Opening another root cancels the export; its result never publishes into the new workspace.
        long generation = ViewModel.WorkspaceGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ViewModel.WorkspaceToken); operation = cancellation;
        try
        {
            string verb = destination == null ? "Checking" : "Exporting";
            var progress = new Progress<SourceProgress>(p =>
            {
                if (operation != cancellation || cancellation.IsCancellationRequested) return;
                ViewModel.Status = $"{verb} {p.Completed}/{p.Total}: {p.Item}"; observer?.Report(p);
            });
            var dithering = await Task.Run(() => SourceProjectSettings.ReadDithering(root), cancellation.Token);
            var report = await Task.Run(() => destination == null ? SourceBuilder.CheckAsync(root, outputs, progress, cancellation.Token, profile, dithering)
                : SourceBuilder.ExportAsync(root, destination, outputs, overwrite, progress, cancellation.Token, profile, confirmReplace, dithering), cancellation.Token);
            if (SourceExportFinishing is { } finishing) await finishing();
            // Files already written stay written; say so rather than suggesting that nothing happened.
            if (ViewModel.WorkspaceGeneration != generation) throw new StudioCommandException("context_changed", report.Destination == null ? "The workspace changed while checking."
                : $"The workspace changed after the export wrote {GameFiles(report.Built)} to {report.Destination}.");
            foreach (var output in report.Outputs)
            {
                if (output.Error != null) ViewModel.AddProblem(SourceProblemText(output.Path, output.Error), file: root);
                foreach (string warning in SourceWarningMessages(output.Warnings, output.Path)) ViewModel.AddProblem(warning, "Warning", root);
            }
            foreach (string note in SourceWarningMessages(report.Notes, "", noun: "notes")) ViewModel.AddProblem(note, "Warning", report.Destination ?? root);
            // The lookups by name several nodes share, which edits can make find another node; each run replaces the last list.
            foreach (var old in ViewModel.Problems.Where(p => p.Severity == "Info" && p.File == root && (p.Message.Contains(" nodes have the name; the game finds ", StringComparison.Ordinal)
                || p.Message.StartsWith("Ambiguous lookups: ", StringComparison.Ordinal))).ToArray()) ViewModel.Problems.Remove(old);
            foreach (var lookup in report.Lookups.Take(256))
            {
                var shown = LookupPreview(lookup);
                ViewModel.AddProblem(SourceProblemText(shown.Mission, $"{WorldLookups.Describe(shown)}: {shown.Candidates} nodes have the name; the game finds {shown.Found} (slot {shown.Slot})."), "Info", root);
            }
            if (report.Lookups.Count > 256) ViewModel.AddProblem($"Ambiguous lookups: showing 256 of {report.Lookups.Count}; {report.Lookups.Count - 256} more not shown.", "Info", root);
            ViewModel.Status = destination == null
                ? $"Checked {GameFiles(report.Outputs.Count)}: {report.Built} build, {report.Failed} failed"
                : $"Exported {GameFiles(report.Built)} to {destination}";
            return report;
        }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        // A canceled publication that could not undo everything says what it left (MCP returns the same message); it stays a cancellation.
        catch (OperationCanceledException ex) when (ex.InnerException is OperationCanceledException && ViewModel.WorkspaceGeneration == generation) { ViewModel.AddProblem(Bounded(ex.Message), "Warning", destination ?? root); throw; }
        finally { operation = null; }
    }
    private static object Lookup(SourceLookup l) => new { mission = l.Mission, kind = l.Kind, name = Bounded(l.Name, 64), source = Bounded(l.Source, 256), candidates = l.Candidates, slot = l.Slot, found = l.Found == null ? null : Bounded(l.Found, 512) };
    private static SourceLookup LookupPreview(SourceLookup lookup) => lookup with
    { Mission = Bounded(lookup.Mission, 32), Name = Bounded(lookup.Name, 64), Source = Bounded(lookup.Source, 256), Found = lookup.Found == null ? null : Bounded(lookup.Found, 512) };
    internal static object ExportResult(string root, SourceExportReport report)
    {
        const int shown = 256;
        return new
        {
            project = root, destination = report.Destination, written = report.Destination != null, built = report.Built, failed = report.Failed, profile = report.Profile,
            textureDithering = SourceProjectSettings.Name(report.Dithering),
            notes = report.Notes.Take(16).Select(n => Bounded(n, 512)).ToArray(), noteCount = report.Notes.Count,
            lookups = report.Lookups.Take(shown).Select(Lookup).ToArray(), lookupCount = report.Lookups.Count, lookupsTruncated = report.Lookups.Count > shown,
            lookupChanges = report.LookupChanges.Take(64).Select(c => new { before = Lookup(c.Before), after = Lookup(c.After), uncertain = c.Uncertain }).ToArray(), lookupChangeCount = report.LookupChanges.Count,
            outputs = report.Outputs.Take(shown).Select(o => new
            {
                path = o.Path, family = o.Family, status = o.Status, bytes = o.Bytes, items = o.Items,
                warnings = o.Warnings.Take(4).Select(w => Bounded(w, 128)).ToArray(), warningCount = o.Warnings.Count,
                warningsTruncated = o.Warnings.Count > 4 || o.Warnings.Take(4).Any(w => w.Length > 128),
                error = o.Error == null ? null : Bounded(o.Error, 256), errorTruncated = o.Error?.Length > 256
            }).ToArray(),
            outputCount = report.Outputs.Count, outputsTruncated = report.Outputs.Count > shown
        };
    }

    private async Task<object> SourceStatusAsync(JsonObject a, CancellationToken token)
    {
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
        long generation = ViewModel.WorkspaceGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ViewModel.WorkspaceToken);
        token = cancellation.Token;
        IReadOnlyList<SourceOutputPlan> plan; IReadOnlyList<BuildProfile> profiles; BuildProfile profile; string dithering;
        try
        {
            profiles = await Task.Run(() => BuildProfiles.List(root, token: token), token);
            dithering = SourceProjectSettings.Name(await Task.Run(() => SourceProjectSettings.ReadDithering(root), token));
            // Without a profile, the one chosen in Export → Build profile (as source_export uses), else the project's default.
            string? name = a["profile"] is null ? await SourceProfileForAsync(root, token) : Text(a, "profile");
            profile = name == null ? profiles.Single(p => p.IsDefault) : profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"The project has no build profile {name}.");
            // A profile chosen in Export → Build profile whose file can no longer be used: the status still lists every
            // profile, with that one's error and no outputs, so the choice can be seen and another profile named.
            if (profile.Error != null && a["profile"] is not null) throw new InvalidDataException(profile.Error);
            plan = profile.Error != null ? [] : await Task.Run(() => SourceBuilder.Plan(root, null, profile, token: token), token);
        }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        token.ThrowIfCancellationRequested(); RequireExportGeneration(generation);
        return new
        {
            project = root, textureDithering = dithering, profile = profile.Name, profileError = profile.Error == null ? null : Bounded(profile.Error, 512), selected = await SourceProfileForAsync(root, token) ?? profiles.Single(p => p.IsDefault).Name,
            profiles = profiles.Select(p => new
            {
                name = p.Name, status = p.Status, @default = p.IsDefault, source = p.Source, description = Bounded(p.Description, 512), error = p.Error == null ? null : Bounded(p.Error, 512),
                texturePacks = p.TexturePacks.Select(t => new { file = t.File, automatic = t.Automatic, budgetMiB = t.BudgetBytes / (1024.0 * 1024), maximumDimension = t.MaximumDimension, missions = t.Missions }).ToArray()
            }).ToArray(),
            families = plan.GroupBy(o => o.Family).ToDictionary(g => g.Key, g => g.Count()),
            outputs = SourceOutputPage(plan, a).Data
        };
    }
    internal static StudioResult SourceOutputPage(IEnumerable<SourceOutputPlan> plan, JsonObject a) => Page(plan, a, o => o.Path, o => new
            {
                path = o.Path, family = o.Family, inputCount = o.Inputs.Count,
                inputs = o.Inputs.Take(16).Select(i => Bounded(i, 512)).ToArray(), inputsTruncated = o.Inputs.Count > 16 || o.Inputs.Take(16).Any(i => i.Length > 512)
            }, maximumRowBytes: o => 512 + 6L * (o.Path.Length + o.Family.Length + o.Inputs.Take(16).Sum(i => Math.Min(i.Length, 512) + 1)));
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
                var (report, notOpened) = await ReconstructSourceProjectAsync(Text(a, "source"), Text(a, "destination"), open, token);
                // Opened unless refused or a newer workspace replaced it meanwhile; its indexing may have been canceled.
                bool opened = open && notOpened == null && ViewModel.HasRoot && ViewModel.RootPath.Equals(Path.GetFullPath(report.Project), StringComparison.OrdinalIgnoreCase);
                var result = Result(ReconstructResult(report, opened));
                if (notOpened != null) result.Data.AsObject()["openRefused"] = new JsonObject { ["code"] = notOpened.Code, ["message"] = Bounded(notOpened.Message, 512) };
                return opened ? IndexCompleteness(result) : result;
            });
        RegisterJob(r, "source_export", "Build game files of the open source project from its files on disk. Without destination, only check that they build. With destination (outside the project), stage, re-parse and then write the selected outputs, or nothing if any fails; existing game files are replaced only with overwrite, and restored if publication fails. Outputs work in the game but are not byte-identical to the shipped files. Unsaved edits to project files must be resolved first. The result lists the lookups by name the built missions make as the game loads them (texture-effect FindNode, animation roots, attach nodes outside their root, node and tracked-node names inside animations that fall back to the whole world, the first node of each activation prerequisite path) whose name several nodes share, with the node the game finds (lookups, at most 256), and those that find another node than in the destination files the export replaced (lookupChanges, also in notes).",
            [P("destination", "string", "Optional output folder (a full path); omit to check without writing."),
             new("outputs", "array", "Optional game files to build, as listed by zstudio_source_status (for example m1/zrdr.zbd); omitted builds all.", Items: new("", "string", "Game file path."), MinItems: 1, MaxItems: 256),
             P("overwrite", "boolean", "Replace game files that already exist in destination; default false."),
             P("profile", "string", "Build profile (zstudio_source_status lists them): which texture packs to build, with what budgets and largest texture side. Default: the profile checked in Export → Build profile (source_status selected), which is the project's default unless the user chose another.")], true,
            async (a, token) =>
            {
                // Without profile, the one Export → Build profile shows checked (the project's default unless the user chose another).
                long generation = ViewModel.WorkspaceGeneration;
                string? profile = a["profile"] == null ? SourceProjectRoot is { } chosenRoot ? await SourceProfileForAsync(chosenRoot, token) : null : Text(a, "profile");
                if (profile != null && SourceProjectRoot is { } root) await Task.Run(() => ResolveProfile(root, profile, token), token);
                var report = await ExportSourceProjectAsync(a["destination"] == null ? null : Text(a, "destination"), OutputArguments(a), Flag(a, "overwrite"), token, profile, generation);
                // Written (or checked): the job completes with the report, even when a cancel arrives as publication ends.
                CommitRunningJob();
                return Result(ExportResult(SourceProjectRoot ?? "", report));
            });
        RegisterJob(r, "source_profile", "Choose the open source project's shared Export > Build profile selection for subsequent GUI and MCP checks/exports. Omit profile to restore the project default. source_status lists valid profiles. Invalid choices leave the selection unchanged.",
            [P("profile", "string", "Profile name; omit to restore the project's default.")], true,
            async (a, token) =>
            {
                token.ThrowIfCancellationRequested();
                string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project first.");
                var result = await SelectSourceProfileAsync(root, a["profile"] == null ? null : Text(a, "profile"), token);
                CommitRunningJob();
                return Result(result);
            });
        RegisterJob(r, "source_dithering", "Read or set the open source project's Export > Texture dithering: how exports and checks convert texture colours to the packs' RGB565 or palette colours. none rounds each texel; ordered uses a fixed 4x4 pattern (seamless for tiling textures); diffusion is Floyd-Steinberg error diffusion. Colours the packs already hold exactly are kept unless diffused error reaches them, so a texture of only such colours builds the same with every choice. Saved in the project's hidden .zstudio/settings.json; omit textureDithering to read it.",
            [P("textureDithering", "string", "none, ordered or diffusion; omit to read the current choice.", false, "none", "ordered", "diffusion")], true,
            async (a, token) =>
            {
                token.ThrowIfCancellationRequested();
                string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project first.");
                if (a["textureDithering"] == null)
                {
                    try { return Result(new { project = root, textureDithering = SourceProjectSettings.Name(await Task.Run(() => SourceProjectSettings.ReadDithering(root), token)) }); }
                    catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
                }
                var dithering = SourceProjectSettings.Parse(Text(a, "textureDithering")) ?? throw new StudioCommandException("invalid_argument", "textureDithering is none, ordered or diffusion.");
                var result = await SetSourceDitheringAsync(root, dithering, token);
                CommitRunningJob();
                return Result(result);
            });
        RegisterJob(r, "source_status", "Describe the open source project: its build profiles (the default marked; built-in original and modern plus gamegen/build-profiles/*.json) and the game files it can build with the chosen profile, with family and source inputs (16 previewed at 512 characters), paged and filtered by path. Large previews shorten pages; follow nextOffset.", [.. PageParameters, P("profile", "string", "Build profile whose texture packs are listed; default: the profile chosen in Export → Build profile, else the project's default.")], false,
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
    // File → Open workspace… (Ctrl+O) and the welcome screen's Open accept only an initialized source project folder;
    // another folder is refused with what is wrong with it.
    private async void WelcomeOpenProjectClick(object sender, RoutedEventArgs e) => await OpenWorkspaceInteractiveAsync();
    private async void OpenWorkspaceClick(object sender, RoutedEventArgs e) => await OpenWorkspaceInteractiveAsync();
    private async Task OpenWorkspaceInteractiveAsync()
    {
        OpenFolderDialog dialog = new() { Title = "Open workspace: choose a source project folder (contains data and gamegen)", InitialDirectory = Directory.Exists(ViewModel.Settings.LastRoot) ? ViewModel.Settings.LastRoot : "" };
        if (dialog.ShowDialog(this) != true) return;
        await RunUi(async () =>
        {
            try { await RequireSourceProjectAsync(dialog.FolderName, CancellationToken.None); }
            catch (StudioCommandException ex) when (ex.Code == "not_project") { MessageBox.Show(this, ex.Message, "Open workspace", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            await ViewModel.OpenRootAsync(dialog.FolderName); UpdateRecent();
        });
    }
    private async void ExportSourceClick(object sender, RoutedEventArgs e) => await RunUi(() => ExportSourceInteractiveAsync(null));
    private async Task ExportSourceInteractiveAsync(IReadOnlyCollection<string>? outputs)
    {
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project first.");
        long generation = ViewModel.WorkspaceGeneration;
        // What the export would refuse anyway is said before a folder is chosen.
        if (operation != null) throw new StudioCommandException("busy", "An export, validation or source operation is already running.");
        RequireNoDrafts();
        if (ViewModel.Documents.FirstOrDefault(d => d.IsDirty && d.SourceWorld != null) is { } dirty)
            throw new StudioCommandException("unsaved_changes", $"Save or discard the edits to {dirty.SourceWorld!.Label} before exporting.");
        OpenFolderDialog folder = new() { Title = outputs == null ? "Choose a folder for the game files" : $"Choose a folder for {string.Join(", ", outputs)}" };
        if (folder.ShowDialog(this) != true) return;
        RequireExportGeneration(generation);
        string? profileName = await SourceProfileForAsync(root, ViewModel.WorkspaceToken);
        // Existing game files are replaced only after an explicit confirmation, asked by the export itself once it has planned
        // its outputs: the list is exactly what it would replace (an export of every output names the automatic pack from the
        // textures as its build counts them, which PNG headers alone can misjudge), and it replaces only those.
        bool declined = false;
        Task<bool> Confirm(IReadOnlyList<string> existing, CancellationToken token) => Dispatcher.InvokeAsync(() =>
        {
            string list = string.Join("\n", existing.Take(12)) + (existing.Count > 12 ? $"\n… and {existing.Count - 12} more" : "");
            declined = MessageBox.Show(exportDialog ?? (Window)this, $"{folder.FolderName} already has these game files:\n{list}\n\nReplace them?", "Export ZBD files", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes;
            return !declined;
        }).Task;
        SourceExportReport? report = null;
        // While it runs, a modal dialog shows what is being built; the workspace below cannot be used.
        exportDialog = new(this, outputs == null ? "Export all ZBD files" : "Export ZBD file",
            $"Exporting {(outputs == null ? "every game file" : string.Join(", ", outputs))} to {folder.FolderName}", writes: true,
            async (shown, token) => report = await ExportSourceProjectAsync(folder.FolderName, outputs, false, token, profileName, generation, Confirm, shown));
        try { exportDialog.Run(); }
        catch (StudioCommandException ex) when (NoMapOpen && ex.Code != "context_changed") { ShowPickerFailure("Export ZBD files", ex); return; }
        catch (OperationCanceledException) when (declined) { ViewModel.Status = "Export canceled; no game file was replaced."; return; }
        // A cancel that came while files were being replaced and could not restore them all says so (it is also in Problems).
        catch (OperationCanceledException ex)
        {
            bool partial = ex.InnerException is OperationCanceledException;
            ViewModel.Status = partial ? Bounded(ex.Message, 512) : "Export canceled; no game file was replaced.";
            if (partial && NoMapOpen) DetailDialog.Show(this, "Export ZBD files", ex.Message);
            else if (partial) SetToolsVisible(true, problems: true);
            return;
        }
        // The workspace changed after the export wrote files: say what was written (the GUI otherwise drops context_changed).
        catch (StudioCommandException ex) when (ex.Code == "context_changed" && ex.Message.Contains(" wrote ", StringComparison.Ordinal)) { ViewModel.Status = ex.Message; MessageBox.Show(this, ex.Message, "Export ZBD files", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        finally { exportDialog = null; }
        if (report != null) ShowExportResult(report);
    }
    /// <summary>The open export or check progress dialog: what asks during the run (replacing files) belongs to it.</summary>
    private SourceExportDialog? exportDialog;
    private void RequireExportGeneration(long? expected)
    {
        if (expected is { } generation && generation != ViewModel.WorkspaceGeneration)
            throw new StudioCommandException("context_changed", "The source project changed while the export destination was being prepared; choose the export again.");
    }
    private async void CheckSourceClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        string? profile = SourceProjectRoot is { } root ? await SourceProfileForAsync(root) : null;
        SourceExportReport? report = null;
        exportDialog = new(this, "Check source project", "Building every game file in memory to check it; nothing is written", writes: false,
            async (shown, token) => report = await ExportSourceProjectAsync(null, null, false, token, profile, observer: shown));
        try { exportDialog.Run(); }
        catch (StudioCommandException ex) when (NoMapOpen && ex.Code != "context_changed") { ShowPickerFailure("Check source project", ex); return; }
        catch (OperationCanceledException) { ViewModel.Status = "Check canceled."; return; }
        finally { exportDialog = null; }
        if (report != null) ShowExportResult(report);
    });
    private void ShowExportResult(SourceExportReport report)
    {
        string detail = ExportProblemSummary(report);
        var problems = report.Outputs.Where(o => o.Error != null).Select(o => o.Path + ": " + o.Error)
            .Concat(report.Outputs.SelectMany(o => o.Warnings.Select(w => o.Path + ": " + w))).Concat(report.Notes);
        // The map picker has no Diagnostics pane: the failures and warnings are listed here too.
        if (NoMapOpen && problems.Any()) DetailDialog.Show(this, "Source project", Below(ViewModel.Status + detail) + PickerDetails(problems));
        else MessageBox.Show(this, ViewModel.Status + detail, "Source project", MessageBoxButton.OK, report.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
    internal static string ExportProblemSummary(SourceExportReport report)
    {
        long warnings = report.Outputs.Sum(o => (long)o.Warnings.Count) + report.Notes.Count;
        long shown = report.Outputs.Sum(o => (long)Math.Min(o.Warnings.Count, SourceWarningsShown)) + Math.Min(report.Notes.Count, SourceWarningsShown);
        return (report.Failed > 0 ? "\nFailures are listed in Diagnostics." : "") + SourceWarningSummary(warnings, shown);
    }

    /// <summary>Presentation only: export commands appear when the open root is a source project.</summary>
    private void ToolsMenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender) return;
        string? root = SourceProjectRoot;
        SourceMenuSeparator.Visibility = AddSourceModelMenu.Visibility =
            EditInBlenderMenu.Visibility = UpdateFromBlenderMenu.Visibility = CreateTerrainMenu.Visibility = ConvertTerrainMenu.Visibility = EditZonesMenu.Visibility = root != null ? Visibility.Visible : Visibility.Collapsed;
        CreateTerrainMenu.IsEnabled = ConvertTerrainMenu.IsEnabled = EditZonesMenu.IsEnabled = ViewModel.SelectedDocument?.SourceWorld is { IsRebuilding: false } && !sourceWorkspaceBusy;
        EditInBlenderMenu.IsEnabled = root != null && !sourceWorkspaceBusy;
        UpdateFromBlenderMenu.IsEnabled = ViewModel.SelectedDocument?.SourceWorld is { IsRebuilding: false } && !sourceWorkspaceBusy;
        EditInBlenderMenu.ToolTip = "Choose a source model or master map and check out its complete graph for the RECOIL Blender extension";
        UpdateFromBlenderMenu.ToolTip = "Validate and accept one complete Blender graph export as an undoable source edit";
        ToolTipService.SetShowOnDisabled(EditInBlenderMenu, true);
        ToolTipService.SetShowOnDisabled(UpdateFromBlenderMenu, true);
    }
    /// <summary>Presentation only: the Export menu (shown for a source project) lists its game files, profiles and dithering.</summary>
    private void ExportMenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender || SourceProjectRoot is not { } root) return;
        _ = FillExportSourceFileMenuAsync(root); _ = FillSourceProfileMenuAsync(root); _ = ReportUi(async () => ShowDithering(await ReadDitheringAsync(root, CancellationToken.None)));
    }

    // Export → Texture dithering: the project's choice, kept in its hidden .zstudio folder and passed to every export and check.
    private void TextureDitheringClick(object sender, RoutedEventArgs e)
    {
        if (SourceProjectRoot is not { } root || (sender as FrameworkElement)?.Tag is not string name || SourceProjectSettings.Parse(name) is not { } dithering) return;
        // Native checkable items toggle before Click; the checks follow the saved choice.
        _ = ReportUi(async () => { ShowDithering(await ReadDitheringAsync(root, CancellationToken.None)); await SetSourceDitheringAsync(root, dithering, CancellationToken.None); });
    }
    private void ShowDithering(TextureDithering? dithering)
    {
        DitherNoneItem.IsChecked = dithering == TextureDithering.None; DitherOrderedItem.IsChecked = dithering == TextureDithering.Ordered;
        DitherDiffusionItem.IsChecked = dithering == TextureDithering.Diffusion;
    }
    /// <summary>The project's texture dithering, or null (shown as no choice) when its settings file cannot be read.</summary>
    private async Task<TextureDithering?> ReadDitheringAsync(string root, CancellationToken token)
    {
        try { return await Task.Run(() => SourceProjectSettings.ReadDithering(root), token); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { ViewModel.Status = Bounded(ex.Message, 512); return null; }
    }
    private async Task<object> SetSourceDitheringAsync(string root, TextureDithering dithering, CancellationToken token)
    {
        if (SourceProjectRoot != root) throw new StudioCommandException("context_changed", "The source project changed; choose its texture dithering again.");
        long generation = ViewModel.WorkspaceGeneration;
        try { await Task.Run(() => SourceProjectSettings.WriteDithering(root, dithering), token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        if (generation != ViewModel.WorkspaceGeneration || SourceProjectRoot != root) throw new StudioCommandException("context_changed", "The source project changed; choose its texture dithering again.");
        ShowDithering(dithering);
        ViewModel.Status = $"Exports dither textures: {DitheringLabel(dithering)}.";
        return new { project = root, textureDithering = SourceProjectSettings.Name(dithering) };
    }
    private static string DitheringLabel(TextureDithering dithering) => dithering switch { TextureDithering.Ordered => "ordered", TextureDithering.Diffusion => "error diffusion", _ => "none" };
    /// <summary>Lists the project's build profiles; the checked one is what exports and checks build until another is chosen.</summary>
    private async Task FillSourceProfileMenuAsync(string root)
    {
        long generation = ViewModel.WorkspaceGeneration, request = ++sourceProfileMenuRequest;
        CancellationToken token = ViewModel.WorkspaceToken;
        SourceProfileMenu.Items.Clear();
        SourceProfileMenu.Items.Add(new MenuItem { Header = new TextBlock { Text = "Reading profiles…" }, IsEnabled = false });
        IReadOnlyList<BuildProfile> profiles;
        try { profiles = await Task.Run(() => { SourceProfilesReading?.Invoke(token); return BuildProfiles.List(root, token: token); }, token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            if (generation != ViewModel.WorkspaceGeneration || request != sourceProfileMenuRequest) return;
            SourceProfileMenu.Items.Clear(); SourceProfileMenu.Items.Add(new MenuItem { Header = new TextBlock { Text = Bounded(ex.Message, 512), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, IsEnabled = false }); return;
        }
        if (token.IsCancellationRequested || generation != ViewModel.WorkspaceGeneration || request != sourceProfileMenuRequest || SourceProjectRoot != root) return;
        FillSourceProfileMenu(root, profiles);
    }
    private void FillSourceProfileMenu(string root, IReadOnlyList<BuildProfile> profiles)
    {
        SourceProfileMenu.Items.Clear();
        // A chosen profile whose file was removed falls back to the default.
        if (sourceProfileChoice is { } stale && stale.Root == root && !profiles.Any(p => p.Name.Equals(stale.Name, StringComparison.OrdinalIgnoreCase)))
        {
            sourceProfileChoice = null;
            // Refresh even when this scan wins the race with an output scan checking the removed profile.
            _ = FillExportSourceFileMenuAsync(root);
        }
        string chosen = sourceProfileChoice is { } choice && choice.Root == root ? choice.Name : profiles.Single(p => p.IsDefault).Name;
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
            string name = profile.Name;
            item.Click += (_, _) =>
            {
                // Native checkable items toggle before Click; keep the old choice visible until the worker accepts the new one.
                FillSourceProfileMenu(root, profiles);
                _ = RunUi(async () => { await SelectSourceProfileAsync(root, name); });
            };
            SourceProfileMenu.Items.Add(item);
        }
    }
    /// <summary>Lists the project's game files off the UI thread; a newer menu opening or root supersedes the listing.</summary>
    private async Task FillExportSourceFileMenuAsync(string root)
    {
        long request = ++sourceMenuGeneration, generation = ViewModel.WorkspaceGeneration;
        ExportSourceFileMenu.Items.Clear();
        ExportSourceFileMenu.Items.Add(new MenuItem { Header = "Reading source project…", IsEnabled = false });
        IReadOnlyList<SourceOutputPlan>? plan = null; string? error = null;
        CancellationToken token = ViewModel.WorkspaceToken;
        var choice = sourceProfileChoice;
        try
        {
            string? profile = await SourceProfileForAsync(root, token);
            if (request != sourceMenuGeneration) return; // A removed-profile fallback already scheduled the replacement scan.
            choice = sourceProfileChoice; // A removed choice may have fallen back to the default.
            plan = await Task.Run(() => SourceBuilder.Plan(root, null, BuildProfiles.Find(root, profile, token: token), token: token), token);
        }
        catch (OperationCanceledException) { return; }
        catch (StudioCommandException) { return; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { error = ex.Message; }
        bool Current() => !token.IsCancellationRequested && !shutdownToken.IsCancellationRequested && request == sourceMenuGeneration &&
            generation == ViewModel.WorkspaceGeneration && SourceProjectRoot == root && sourceProfileChoice == choice;
        if (!Current()) return;
        ExportSourceFileMenu.Items.Clear();
        if (plan == null || plan.Count == 0) { ExportSourceFileMenu.Items.Add(new MenuItem { Header = new TextBlock { Text = error == null ? "Nothing to build yet" : Bounded(error, 512) }, IsEnabled = false }); return; }
        FillSourceOutputMenu(ExportSourceFileMenu, plan, Current, path => _ = RunUi(() => ExportSourceInteractiveAsync([path])));
    }

    /// <summary>At most 64 output controls, plus a count and two navigation controls; visited pages are replaced.</summary>
    internal const int SourceOutputMenuPageSize = 64;
    internal static void FillSourceOutputMenu(MenuItem menu, IReadOnlyList<SourceOutputPlan> plan, Func<bool> current, Action<string> export)
    {
        Show(0);
        void Show(int offset)
        {
            if (!current()) return;
            menu.Items.Clear();
            int end = offset + Math.Min(SourceOutputMenuPageSize, plan.Count - offset);
            if (plan.Count > SourceOutputMenuPageSize)
                menu.Items.Add(new MenuItem { Header = new TextBlock { Text = $"Files {offset + 1:N0}–{end:N0} of {plan.Count:N0}" }, IsEnabled = false });
            if (offset > 0) Navigation("Previous outputs", offset - SourceOutputMenuPageSize);
            for (int index = offset; index < end; index++)
            {
                var output = plan[index];
                // Paths remain complete literal identities, not menu access-key labels.
                MenuItem item = new() { Header = new TextBlock { Text = output.Path }, ToolTip = $"{output.Inputs.Count:N0} source files ({output.Family})" };
                System.Windows.Automation.AutomationProperties.SetName(item, output.Path);
                string path = output.Path;
                item.Click += (_, _) => { if (current()) export(path); };
                menu.Items.Add(item);
            }
            if (end < plan.Count) Navigation("Next outputs", end);
        }
        void Navigation(string label, int offset)
        {
            MenuItem item = new() { Header = new TextBlock { Text = label }, StaysOpenOnClick = true };
            System.Windows.Automation.AutomationProperties.SetName(item, label);
            item.Click += (_, _) => Show(offset);
            menu.Items.Add(item);
        }
    }
}
