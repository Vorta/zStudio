using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    internal Action<CancellationToken>? SourceExportPlanning { get; set; }

    private async Task<SourceExportSelection> LoadExportSelectionAsync(CancellationToken token)
    {
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project first.");
        long generation = ViewModel.WorkspaceGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ViewModel.WorkspaceToken);
        string? name = await SourceProfileForAsync(root, cancellation.Token);
        var choice = sourceProfileChoice;
        var profile = await Task.Run(() => BuildProfiles.Find(root, name, token: cancellation.Token), cancellation.Token);
        var plan = await Task.Run(() =>
        {
            SourceExportPlanning?.Invoke(cancellation.Token);
            return SourceBuilder.Plan(root, null, profile, token: cancellation.Token);
        }, cancellation.Token);
        bool Current() => !shutdownToken.IsCancellationRequested && generation == ViewModel.WorkspaceGeneration && root == SourceProjectRoot && choice == sourceProfileChoice;
        cancellation.Token.ThrowIfCancellationRequested();
        if (!Current()) throw new StudioCommandException("context_changed", "The project or build profile changed; choose the export again.");
        return new(plan, ViewModel.Settings, profile.Name, Current);
    }

    /// <summary>
    /// What would refuse an export now, as the export window shows it: another operation, unfinished input, or unsaved
    /// edits to the project's files (exports read the files on disk).
    /// </summary>
    private SourceExportBlock? ExportBlocker(string root)
    {
        if (operation != null) return new("An export, validation or source operation is already running.", "Wait for the running export, validation or source operation to finish");
        try { RequireNoDrafts(); }
        catch (StudioCommandException) { return new(GuiDraftRefusal() + " Exports read the files on disk.", "Apply or revert the unfinished changes first"); }
        string project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), prefix = Path.EndsInDirectorySeparator(project) ? project : project + Path.DirectorySeparatorChar;
        if (ViewModel.Documents.FirstOrDefault(d => d.IsDirty && (d.SourceWorld is { } world ? world.Root.Equals(project, StringComparison.OrdinalIgnoreCase)
            : Path.GetFullPath(d.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) is { } dirty)
            return new($"{dirty.SourceWorld?.Mission ?? Path.GetFileName(dirty.Path)} has unsaved edits. Exports read the files on disk: save or discard them first.", "Save or discard the unsaved edits first");
        return null;
    }

    internal SourceExportSelectionDialog CreateExportSelectionDialog()
    {
        string? root = SourceProjectRoot;
        return new(this, new SourceExportWindowServices
        {
            Load = LoadExportSelectionAsync,
            Remember = (model, destination) =>
            {
                try
                {
                    if (model != null) model.Remember(ViewModel.Settings, destination, rememberDestination: true);
                    else if (ViewModel.Settings.ExportDestination != SourceExportDestination.Clean(destination)) SaveExportDestination(destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { ViewModel.AddProblem("Could not remember the export selection: " + Bounded(ex.Message)); }
            },
            Profiles = async token =>
            {
                string project = root ?? throw new StudioCommandException("no_project", "Open a source project first.");
                var list = await Task.Run(() => BuildProfiles.List(project, token: token), token);
                string? chosen = await SourceProfileForAsync(project, token);
                return new(list, chosen ?? list.Single(p => p.IsDefault).Name);
            },
            SelectProfile = async (name, token) =>
            {
                string project = root ?? throw new StudioCommandException("no_project", "Open a source project first.");
                await SelectSourceProfileAsync(project, name, token);
            },
            ReadDithering = token => root is { } project && SourceProjectRoot == root ? ReadDitheringAsync(project, token) : Task.FromResult<zStudio.Core.Formats.TextureDithering?>(null),
            SetDithering = async (value, token) =>
            {
                string project = root ?? throw new StudioCommandException("no_project", "Open a source project first.");
                await SetSourceDitheringAsync(project, value, token);
            },
            Blocker = () => SourceProjectRoot is { } project ? ExportBlocker(project) : null,
            CheckDestination = root == null ? null : (folder, selected, token) => SourceExportDestination.Check(root, folder, selected, token),
            Destination = ViewModel.Settings.ExportDestination,
        });
    }
    private void SaveExportDestination(string? destination)
    {
        string? previous = ViewModel.Settings.ExportDestination;
        ViewModel.Settings.ExportDestination = destination;
        try { ViewModel.Settings.Save(); }
        catch { ViewModel.Settings.ExportDestination = previous; throw; }
    }

    private async void ExportSourceFilesClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        var dialog = CreateExportSelectionDialog();
        if (dialog.ShowDialog() != true || dialog.Selection is not { } selection) return;
        if (!selection.IsCurrent()) throw new StudioCommandException("context_changed", "The project or build profile changed; choose the export again.");
        // Every file selected exports as Export all did: no outputs list, so the automatic packs are sized from the textures as built.
        await ExportSourceInteractiveAsync(selection.AllSelected ? null : selection.SelectedPaths, selection, dialog.Destination);
    });

    private void RegisterSourceExportSelectionCommands(StudioCommands registry)
    {
        RegisterJob(registry, "source_export_selection", "Inspect or remember the Export ZBD files window: its checkbox choices (shared across projects and restarts by relative output path; new paths start selected) and its destination folder (one for every project). Outputs carry the window's grid identities: group (Shared files or a mission folder), kind (the grid column: the file name, or automatic rtexture; null for a shared file) and section (shared, map, rtexture, texture). select and clear take exact outputs, or groups and kinds (which intersect); omitting all three applies to every output. When every output is selected, the GUI exports with outputs omitted (every file; automatic packs sized from the textures as built); otherwise it passes the selected outputs. action destination sets or forgets the remembered folder after the window's checks. This command never exports files: use source_export, which always needs its own destination and never changes the remembered one.",
            [P("action", "string", "Get choices, select files, clear files, or set the destination; default get.", false, "get", "select", "clear", "destination"),
             new("outputs", "array", "Exact output paths to select or clear.", Items: new("", "string", "Relative game file path."), MinItems: 1, MaxItems: 256),
             new("groups", "array", "Groups to select or clear (Shared files, or mission folders such as m4); with kinds, only their outputs of those kinds.", Items: new("", "string", "Shared files or a mission folder."), MinItems: 1, MaxItems: 256),
             new("kinds", "array", "Grid columns to select or clear in every mission (gamez.zbd, rtexture16.zbd, automatic rtexture); with groups, only in those groups.", Items: new("", "string", "A kind as get lists it."), MinItems: 1, MaxItems: 64),
             P("destination", "string", "With action destination: a full folder path outside the project; omit or null to forget the remembered folder."),
             .. PageParameters], true, async (args, token) =>
            {
                string action = Text(args, "action", "get");
                if (action is not ("get" or "select" or "clear" or "destination")) throw new StudioCommandException("invalid_argument", "Action is get, select, clear or destination.");
                if (action != "destination" && args.ContainsKey("destination")) throw new StudioCommandException("invalid_argument", "destination applies only to action destination.");
                if (action is "get" or "destination")
                    foreach (string key in new[] { "outputs", "groups", "kinds" })
                        if (args.ContainsKey(key)) throw new StudioCommandException("invalid_argument", $"{key} applies only to select or clear.");
                string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project first.");
                var model = await LoadExportSelectionAsync(token);
                string? destination = ViewModel.Settings.ExportDestination;
                if (action is "select" or "clear")
                {
                    model.Set(SelectedOutputs(model, args), action == "select");
                    token.ThrowIfCancellationRequested();
                    if (!model.IsCurrent()) throw new StudioCommandException("context_changed", "The project or profile changed.");
                }
                else if (action == "destination")
                {
                    destination = SourceExportDestination.Clean(args["destination"] is null ? null : Text(args, "destination"));
                }
                var check = await Task.Run(() => SourceExportDestination.Check(root, destination, model.SelectedPaths, token), token);
                if (action == "destination" && check.Problem != null) throw new StudioCommandException("invalid_argument", check.Problem);
                const int GroupsShown = 256, KindsShown = 64;
                var result = Result(new
                {
                    profile = model.Profile, selectedCount = model.SelectedCount, total = model.Files.Count, allSelected = model.AllSelected,
                    groups = model.Groups.Take(GroupsShown).Select(g => new { group = Bounded(g.Name, 260), selected = g.Selected, total = g.Files.Count }).ToArray(),
                    groupCount = model.Groups.Count, groupsTruncated = model.Groups.Count > GroupsShown,
                    kinds = model.Kinds.Take(KindsShown).Select(k => new { kind = Bounded(k.Kind, 260), section = k.Section, label = Bounded(k.Label, 260), selected = k.Selected, total = k.Files.Count }).ToArray(),
                    kindCount = model.Kinds.Count, kindsTruncated = model.Kinds.Count > KindsShown,
                    destination = check.Path, destinationExists = check.Exists, destinationExisting = check.Existing, destinationProblem = check.Problem,
                    outputs = Page(model.Files, args, f => f.Path, f => new { path = f.Path, group = f.Group, kind = f.Kind, section = f.Section, selected = f.Selected },
                        maximumRowBytes: f => 320 + 12L * (f.Path.Length + f.Group.Length + (f.Kind?.Length ?? 0))).Data
                });
                if (action != "get")
                {
                    // A dialog may have opened while the plan was loading; its snapshot must remain authoritative.
                    RequireAutomationMutationAvailable();
                    if (action == "destination") SaveExportDestination(destination);
                    else model.Remember(ViewModel.Settings);
                    CommitRunningJob();
                }
                return result;
            });
    }

    /// <summary>The outputs select or clear names: exact paths, or groups and kinds (intersecting), or every output.</summary>
    private static IReadOnlyList<SourceExportFile> SelectedOutputs(SourceExportSelection model, JsonObject args)
    {
        var paths = OutputArguments(args);
        var groups = Names(args, "groups", 256);
        var kinds = Names(args, "kinds", 64);
        if (paths != null && (groups != null || kinds != null)) throw new StudioCommandException("invalid_argument", "Give outputs, or groups and kinds, not both.");
        if (paths != null)
        {
            var byPath = model.Files.ToDictionary(f => SourceExportSelection.Normalize(f.Path), StringComparer.OrdinalIgnoreCase);
            return paths.Select(path => byPath.GetValueOrDefault(SourceExportSelection.Normalize(path)) ?? throw new StudioCommandException("invalid_argument", $"This source project cannot build {Bounded(path, 260)}.")).ToArray();
        }
        if (groups == null && kinds == null) return model.Files;
        HashSet<SourceExportFile>? inGroups = null, ofKinds = null;
        if (groups != null)
            inGroups = groups.SelectMany(name => (model.Groups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new StudioCommandException("invalid_argument", $"The plan has no group {Bounded(name, 260)}; groups are Shared files and the mission folders get lists.")).Files).ToHashSet();
        if (kinds != null)
            ofKinds = kinds.SelectMany(name => (model.Kinds.FirstOrDefault(k => k.Kind.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new StudioCommandException("invalid_argument", $"The plan has no kind {Bounded(name, 260)}; kinds are the grid's columns that get lists, such as gamez.zbd.")).Files).ToHashSet();
        var files = model.Files.Where(f => (inGroups == null || inGroups.Contains(f)) && (ofKinds == null || ofKinds.Contains(f))).ToArray();
        if (files.Length == 0) throw new StudioCommandException("invalid_argument", "No output is in these groups and of these kinds (shared files have no kind).");
        return files;
    }
    private static string[]? Names(JsonObject args, string key, int maximum) => args[key] switch
    {
        null => null,
        JsonArray list when list.Count >= 1 && list.Count <= maximum => list.Select(item => item is JsonValue value && value.TryGetValue<string>(out var name) && name.Length is > 0 and <= 260 ? name
            : throw new StudioCommandException("invalid_argument", $"{key} are names of 1–260 characters.")).ToArray(),
        _ => throw new StudioCommandException("invalid_argument", $"{key} must list 1–{maximum} names."),
    };
}
