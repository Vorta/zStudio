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
        var plan = await Task.Run(() =>
        {
            SourceExportPlanning?.Invoke(cancellation.Token);
            return SourceBuilder.Plan(root, token: cancellation.Token);
        }, cancellation.Token);
        bool Current() => !shutdownToken.IsCancellationRequested && generation == ViewModel.WorkspaceGeneration && root == SourceProjectRoot;
        cancellation.Token.ThrowIfCancellationRequested();
        if (!Current()) throw new StudioCommandException("context_changed", "The project changed; choose the export again.");
        return new(plan, ViewModel.Settings, Current);
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
            ReadDithering = token => root is { } project && SourceProjectRoot == root ? ReadDitheringAsync(project, token) : Task.FromResult<zStudio.Core.Formats.TextureDithering?>(null),
            SetDithering = async (value, token) =>
            {
                string project = root ?? throw new StudioCommandException("no_project", "Open a source project first.");
                await SetSourceDitheringAsync(project, value, token);
            },
            DitheringRequests = () => sourceDitheringRequest,
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
        if (!selection.IsCurrent()) throw new StudioCommandException("context_changed", "The project changed; choose the export again.");
        // Every file selected exports as Export all did: no outputs list, so the automatic packs are sized from the textures as built.
        await ExportSourceInteractiveAsync(selection.AllSelected ? null : selection.SelectedPaths, selection, dialog.Destination);
    });

    private void RegisterSourceExportSelectionCommands(StudioCommands registry)
    {
        RegisterJob(registry, "source_export_selection", "Inspect or remember the Export ZBD files window: its checkbox choices (shared across projects and restarts by relative output path, except each mission's full-size 32-bit pack, remembered as that mission's full-size pack whatever its N; new paths start selected, but new 32-bit files start clear while every 32-bit file was clear when the choices were last remembered: newThirtyTwoBit) and its destination folder (one for every project). The window's three pack boxes: direct3d (rtexture packs and image.zbd; read by Recoil, reRECOIL v1 and v2), software (texture packs; only Recoil's software mode) and 32-bit (rgbatexture<N> tiers and rgbaimage.zbd; only reRECOIL v2 reads them). Outputs carry the window's grid identities: group (Shared files or a mission folder), kind (the grid column: the file name, automatic rtexture, or full-size rgbatexture, one kind for every mission's full-size 32-bit pack whatever its N; null for a shared file), section (shared, map, rtexture, texture, rgbatexture), pack (direct3d, software, 32-bit, null for other files), format (16-bit or 32-bit: what dithering reaches; null for other files), fullSize (the pack holds every texture at full size), budgetMegabytes (a pack's N) and estimatedBytes (every pack; null when not known). packs summarises the three boxes with their files' estimatedBytes (null unless the box has files and every one has an estimate). select and clear take exact outputs, or groups, kinds, packs and sections (all given intersect; values of one selector unite); omitting them all applies to every output. When every output is selected, the GUI exports with outputs omitted (every file; automatic packs sized from the textures as built); otherwise it passes the selected outputs. action destination sets or forgets the remembered folder after the window's checks; destinationShadowing lists larger packs left there that the game would load instead of the selected ones. This command never exports files: use source_export, which always needs its own destination and never changes the remembered one.",
            [P("action", "string", "Get choices, select files, clear files, or set the destination; default get.", false, "get", "select", "clear", "destination"),
             new("outputs", "array", "Exact output paths to select or clear.", Items: new("", "string", "Relative game file path."), MinItems: 1, MaxItems: 256),
             new("groups", "array", "Groups to select or clear (Shared files, or mission folders such as m4); with kinds, packs or sections, only their outputs of those.", Items: new("", "string", "Shared files or a mission folder."), MinItems: 1, MaxItems: 256),
             new("kinds", "array", "Grid columns to select or clear in every mission that has them (gamez.zbd, rtexture16.zbd, automatic rtexture, full-size rgbatexture, rgbatexture64.zbd: a 32-bit tier's own column holds it only where it is not the mission's full-size tier); with groups, only in those groups.", Items: new("", "string", "A kind as get lists it."), MinItems: 1, MaxItems: 64),
             new("packs", "array", "Pack boxes to select or clear (the window's Direct3D, Software and 32-bit packs boxes; every 16-bit pack: direct3d and software); intersect with the other selectors.", Items: new("", "string", "A pack box.", Choices: [SourceExportSelection.Direct3DPack, SourceExportSelection.SoftwarePack, SourceExportSelection.ThirtyTwoBitPack]), MinItems: 1, MaxItems: 3),
             new("sections", "array", "Sections to select or clear (the window's section boxes, and shared for the Shared files row); intersect with the other selectors.", Items: new("", "string", "A section.", Choices: [SourceExportSelection.SharedSection, SourceExportSelection.MapSection, SourceExportSelection.HardwareSection, SourceExportSelection.SoftwareSection, SourceExportSelection.RgbaSection]), MinItems: 1, MaxItems: 8),
             P("destination", "string", "With action destination: a full folder path outside the project; omit or null to forget the remembered folder."),
             .. PageParameters], true, async (args, token) =>
            {
                string action = Text(args, "action", "get");
                if (action is not ("get" or "select" or "clear" or "destination")) throw new StudioCommandException("invalid_argument", "Action is get, select, clear or destination.");
                if (action != "destination" && args.ContainsKey("destination")) throw new StudioCommandException("invalid_argument", "destination applies only to action destination.");
                if (action is "get" or "destination")
                    foreach (string key in new[] { "outputs", "groups", "kinds", "packs", "sections" })
                        if (args.ContainsKey(key)) throw new StudioCommandException("invalid_argument", $"{key} applies only to select or clear.");
                string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project first.");
                var model = await LoadExportSelectionAsync(token);
                string? destination = ViewModel.Settings.ExportDestination;
                if (action is "select" or "clear")
                {
                    model.Set(SelectedOutputs(model, args), action == "select");
                    token.ThrowIfCancellationRequested();
                    if (!model.IsCurrent()) throw new StudioCommandException("context_changed", "The project changed.");
                }
                else if (action == "destination")
                {
                    destination = SourceExportDestination.Clean(args["destination"] is null ? null : Text(args, "destination"));
                }
                var check = await Task.Run(() => SourceExportDestination.Check(root, destination, model.SelectedPaths, token), token);
                if (action == "destination" && check.Problem != null) throw new StudioCommandException("invalid_argument", check.Problem);
                const int GroupsShown = 256, KindsShown = 64, SectionsShown = 8;
                var result = Result(new
                {
                    selectedCount = model.SelectedCount, total = model.Files.Count, allSelected = model.AllSelected,
                    groups = model.Groups.Take(GroupsShown).Select(g => new { group = Bounded(g.Name, 260), selected = g.Selected, total = g.Files.Count }).ToArray(),
                    groupCount = model.Groups.Count, groupsTruncated = model.Groups.Count > GroupsShown,
                    kinds = model.Kinds.Take(KindsShown).Select(k => new { kind = Bounded(k.Kind, 260), section = k.Section, label = Bounded(k.Label, 260), selected = k.Selected, total = k.Files.Count }).ToArray(),
                    kindCount = model.Kinds.Count, kindsTruncated = model.Kinds.Count > KindsShown,
                    packs = model.PackSets.Select(p => new { pack = p.Pack, selected = p.Set.Selected, total = p.Set.Files.Count, estimatedBytes = SourceExportSelection.EstimatedBytes(p.Set) }).ToArray(),
                    sections = SectionSets(model).Take(SectionsShown).Select(s => new { section = s.Id, selected = s.Set.Selected, total = s.Set.Files.Count }).ToArray(),
                    newThirtyTwoBit = (action is "select" or "clear" ? model.NewThirtyTwoBitClear : ViewModel.Settings.ExportRgbaCleared) ? "clear" : "selected",
                    destination = check.Path, destinationExists = check.Exists, destinationExisting = check.Existing, destinationProblem = check.Problem,
                    destinationShadowing = check.Shadows.Select(s => new { path = Bounded(s.Path, 260), instead = Bounded(s.Instead, 260) }).ToArray(), destinationShadowingCount = check.ShadowCount,
                    outputs = Page(model.Files, args, f => f.Path, f => new { path = f.Path, group = f.Group, kind = f.Kind, section = f.Section, pack = f.Pack, format = f.Format, fullSize = f.FullSize,
                        budgetMegabytes = f.BudgetMegabytes, estimatedBytes = f.EstimatedBytes, selected = f.Selected },
                        maximumRowBytes: f => 480 + 12L * (f.Path.Length + f.Group.Length + (f.Kind?.Length ?? 0))).Data
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

    /// <summary>The section boxes as MCP lists them: shared (the Shared files row) when the plan has shared files, then the grid's sections.</summary>
    private static IEnumerable<(string Id, SourceExportSet Set)> SectionSets(SourceExportSelection model) =>
        [.. model.Shared is { } shared ? [(SourceExportSelection.SharedSection, shared)] : Array.Empty<(string, SourceExportSet)>(), .. model.Sections.Select(s => (s.Id, (SourceExportSet)s))];

    /// <summary>The outputs select or clear names: exact paths, or groups, kinds, packs and sections (intersecting), or every output.</summary>
    private static IReadOnlyList<SourceExportFile> SelectedOutputs(SourceExportSelection model, JsonObject args)
    {
        var paths = OutputArguments(args);
        var groups = Names(args, "groups", 256);
        var kinds = Names(args, "kinds", 64);
        var packs = Names(args, "packs", 3);
        var sections = Names(args, "sections", 8);
        if (paths != null && (groups != null || kinds != null || packs != null || sections != null)) throw new StudioCommandException("invalid_argument", "Give outputs, or groups, kinds, packs and sections, not both.");
        if (paths != null)
        {
            var byPath = model.Files.ToDictionary(f => SourceExportSelection.Normalize(f.Path), StringComparer.OrdinalIgnoreCase);
            return paths.Select(path => byPath.GetValueOrDefault(SourceExportSelection.Normalize(path)) ?? throw new StudioCommandException("invalid_argument", $"This source project cannot build {Bounded(path, 260)}.")).ToArray();
        }
        if (groups == null && kinds == null && packs == null && sections == null) return model.Files;
        HashSet<SourceExportFile>? inGroups = null, ofKinds = null;
        if (groups != null)
            inGroups = groups.SelectMany(name => (model.Groups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new StudioCommandException("invalid_argument", $"The plan has no group {Bounded(name, 260)}; groups are Shared files and the mission folders get lists.")).Files).ToHashSet();
        if (kinds != null)
            ofKinds = kinds.SelectMany(name => (model.Kinds.FirstOrDefault(k => k.Kind.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new StudioCommandException("invalid_argument", $"The plan has no kind {Bounded(name, 260)}; kinds are the grid's columns that get lists, such as gamez.zbd.")).Files).ToHashSet();
        var files = model.Files.Where(f => (inGroups == null || inGroups.Contains(f)) && (ofKinds == null || ofKinds.Contains(f))
            && (packs == null || packs.Contains(f.Pack)) && (sections == null || sections.Contains(f.Section))).ToArray();
        if (files.Length == 0) throw new StudioCommandException("invalid_argument", "No output is in these groups, kinds, packs and sections (shared files have no kind; files that are not packs are in no pack box).");
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
