using System.ComponentModel;
using System.Globalization;
using System.IO;
using zStudio.Core.Formats;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// One exact output plan as the Export ZBD files window shows it, a mission × file grid, and its remembered checkbox choices.
/// Rows are the plan's groups (Shared files, then the mission folders); columns are the kinds of mission file, derived from
/// the plan in three sections: map files, Direct3D (rtexture) and software (texture) packs. Every header box of the window is
/// a <see cref="SourceExportSet"/> of files whose tri-state follows them.
/// </summary>
internal sealed class SourceExportSelection
{
    internal const string SharedGroup = "Shared files", AutomaticKind = "automatic rtexture";
    internal const string MapSection = "map", HardwareSection = "rtexture", SoftwareSection = "texture", SharedSection = "shared";
    internal static readonly StringComparer Natural = CultureInfo.InvariantCulture.CompareInfo.GetStringComparer(CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
    private static readonly string[] SharedOrder = ["zrdr.zbd", "interp.zbd", "image.zbd", "soundsh.zbd", "soundsm.zbd", "soundsl.zbd"];
    private static readonly string[] MapOrder = ["gamez.zbd", "anim.zbd", "zrdr.zbd"];
    /// <summary>Every output, by group (as <see cref="Groups"/>) and then by path; the order MCP pages them in.</summary>
    internal IReadOnlyList<SourceExportFile> Files { get; }
    /// <summary>Shared files (when the plan has any), then the mission folders in natural order.</summary>
    internal IReadOnlyList<SourceExportSet> Groups { get; }
    /// <summary>The shared row: zrdr, interp, image, the sound banks from high to low quality, then any other by name.</summary>
    internal IReadOnlyList<SourceExportFile> SharedFiles { get; }
    internal SourceExportSet? Shared { get; }
    /// <summary>The grid's rows: every group but Shared files.</summary>
    internal IReadOnlyList<SourceExportSet> Missions { get; }
    /// <summary>The grid's sections that have columns, in order: map files, rtexture, texture.</summary>
    internal IReadOnlyList<SourceExportSection> Sections { get; }
    /// <summary>The grid's columns, section by section.</summary>
    internal IReadOnlyList<SourceExportKind> Kinds { get; }
    internal SourceExportSet All { get; }
    internal SourceExportSet AllMissions { get; }
    internal Func<bool> IsCurrent { get; }
    internal event Action? Changed;
    private readonly Dictionary<(SourceExportSet, SourceExportKind), SourceExportFile> cells = [];
    internal int SelectedCount => Files.Count(f => f.Selected);
    internal bool AllSelected => Files.Count > 0 && Files.All(f => f.Selected);
    internal string[] SelectedPaths => Files.Where(f => f.Selected).Select(f => f.Path).ToArray();

    internal SourceExportSelection(IReadOnlyList<SourceOutputPlan> plan, StudioSettings settings, Func<bool> current)
    {
        IsCurrent = current;
        var uncheckedPaths = settings.ExportUncheckedOutputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<SourceExportFile> files = []; List<SourceExportSet> groups = [];
        Dictionary<string, SourceExportKind> kinds = new(StringComparer.OrdinalIgnoreCase);
        foreach (var group in plan.GroupBy(p => GroupOf(p.Path), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key == SharedGroup ? 0 : Mission(g.Key) ? 1 : 2).ThenBy(g => g.Key, Natural))
        {
            List<SourceExportFile> members = [];
            foreach (var output in group.OrderBy(p => p.Path, Natural))
            {
                bool shared = group.Key == SharedGroup;
                string name = FileName(output.Path), section = shared ? SharedSection : SectionOf(output);
                string? kind = shared ? null : output.Automatic ? AutomaticKind : name.ToLowerInvariant();
                var file = new SourceExportFile(this, output.Path, group.Key, output.Family, kind, section, Describe(output, group.Key, shared), !uncheckedPaths.Contains(Normalize(output.Path)));
                members.Add(file); files.Add(file);
                if (kind == null) continue;
                if (!kinds.TryGetValue(kind, out var column))
                    kinds.Add(kind, column = new SourceExportKind(kind, section, Label(name, section, output.Automatic), output.Automatic, Order(name, section, output)));
                column.Add(file);
            }
            groups.Add(new SourceExportSet(group.Key, members));
        }
        Files = files; Groups = groups;
        Shared = groups.FirstOrDefault(g => g.Name == SharedGroup);
        SharedFiles = Shared == null ? [] : Shared.Files.OrderBy(f => Array.IndexOf(SharedOrder, f.Path.ToLowerInvariant()) is int i and >= 0 ? i : SharedOrder.Length).ThenBy(f => f.Path, Natural).ToArray();
        Missions = groups.Where(g => g != Shared).ToArray();
        List<SourceExportSection> sections = [];
        foreach (var (id, label, detail) in new[] { (MapSection, "Map files", ""), (HardwareSection, "rtexture", "Direct3D"), (SoftwareSection, "texture", "software") })
        {
            var columns = kinds.Values.Where(k => k.Section == id).OrderBy(k => k.Order).ThenBy(k => k.Kind, Natural).ToArray();
            if (columns.Length > 0) sections.Add(new SourceExportSection(id, label, detail, columns));
        }
        Sections = sections; Kinds = sections.SelectMany(s => s.Kinds).ToArray();
        foreach (var mission in Missions) foreach (var file in mission.Files) cells[(mission, kinds[file.Kind!])] = file;
        All = new SourceExportSet("All files", files);
        AllMissions = new SourceExportSet("All missions", Missions.SelectMany(m => m.Files).ToArray());
        foreach (var set in Sets()) set.Owner = this;
    }

    /// <summary>The file of a mission in a column, or null where the project builds none.</summary>
    internal SourceExportFile? Cell(SourceExportSet mission, SourceExportKind kind) => cells.GetValueOrDefault((mission, kind));
    private IEnumerable<SourceExportSet> Sets() => [All, AllMissions, .. Groups, .. Sections, .. Kinds];
    internal static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();
    private static bool Mission(string name) => name.Length > 1 && (name[0] is 'm' or 'M') && name.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
    private static string GroupOf(string path) { int slash = path.IndexOf('/'); return slash < 0 ? SharedGroup : path[..slash]; }
    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static string SectionOf(SourceOutputPlan output) => output.Family != "textures" ? MapSection
        : output.Pack?.Kind switch { TexturePackKind.Hardware => HardwareSection, TexturePackKind.Software => SoftwareSection, _ => MapSection };
    private static string Label(string name, string section, bool automatic)
    {
        if (automatic) return "auto";
        string stem = name.EndsWith(".zbd", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (section == MapSection) return stem;
        string prefix = section == HardwareSection ? "rtexture" : "texture";
        return stem.Length > prefix.Length && stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? stem[prefix.Length..] : stem;
    }
    /// <summary>Map files: gamez, anim, zrdr, then others by name; packs: by budget, a pack without one after, the automatic pack last.</summary>
    private static double Order(string name, string section, SourceOutputPlan output) => section == MapSection
        ? Array.IndexOf(MapOrder, name.ToLowerInvariant()) is int i and >= 0 ? i : MapOrder.Length
        : output.Automatic ? double.MaxValue : output.Pack?.BudgetBytes is long budget ? budget : double.MaxValue / 2;

    private static string Describe(SourceOutputPlan output, string group, bool shared)
    {
        const double MiB = 1024 * 1024;
        return output.Family switch
        {
            "world" => $"{group}'s world, built by gamegen/{group}.gs from its glTF models",
            "animations" => $"{group}'s animations, compiled from data/{group}/zrdr/anim.zad against this world",
            "archive" => shared ? "Common resources: the .zrd files of every zrdr folder under data/common" : $"{group}'s resources: the .zrd files under data/{group}/zrdr",
            "scripts" => "Prepared scripts: gamegen/*.gs and gamegen/support/*.gw",
            "sounds" => FileName(output.Path).ToLowerInvariant() switch
            {
                "soundsh.zbd" => "High-quality sound bank from data/common/sounds",
                "soundsm.zbd" => "Medium-quality sound bank from data/common/sounds",
                "soundsl.zbd" => "Low-quality sound bank from data/common/sounds",
                _ => "Sound bank from data/common/sounds",
            },
            "images" => "Interface images and fonts (data/common, data/mN/images)",
            "textures" when output.Pack is { } pack => output.Automatic
                ? $"Automatic Direct3D pack: every texture at full size, named for the {pack.BudgetBytes / MiB:0.#} MB they need"
                : pack.Kind == TexturePackKind.Hardware ? $"Direct3D texture pack for {pack.BudgetBytes / MiB:0.#} MB cards, textures up to {pack.MaximumDimension} texels"
                : pack.BudgetBytes is long budget ? $"Software-renderer texture pack, {budget / MiB:0.#} MB, textures up to {pack.MaximumDimension} texels"
                : $"Software-renderer texture pack without a budget, textures up to {pack.MaximumDimension} texels",
            _ => $"{output.Family} built from {output.Inputs.Count:N0} source files",
        };
    }

    internal void SetAll(bool value) => Set(Files, value);
    internal void Set(IEnumerable<SourceExportFile> files, bool value)
    {
        foreach (var file in files) file.Set(value);
        Refresh();
    }
    internal void Refresh() { foreach (var set in Sets()) set.Notify(); Changed?.Invoke(); }
    internal void Remember(StudioSettings settings, string? destination = null, bool rememberDestination = false)
    {
        var uncheckedPaths = settings.ExportUncheckedOutputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
            if (file.Selected) uncheckedPaths.Remove(Normalize(file.Path)); else uncheckedPaths.Add(Normalize(file.Path));
        if (uncheckedPaths.Count > StudioSettings.MaximumExportChoices) throw new InvalidOperationException("Too many remembered export choices. Select more files before closing this window.");
        var previous = settings.ExportUncheckedOutputs; var previousDestination = settings.ExportDestination;
        settings.ExportUncheckedOutputs = uncheckedPaths.Order(StringComparer.Ordinal).ToList();
        if (rememberDestination) settings.ExportDestination = destination;
        try { settings.Save(); }
        catch { settings.ExportUncheckedOutputs = previous; settings.ExportDestination = previousDestination; throw; }
    }
}

/// <summary>A set of files a header box selects or clears: checked when all are selected, mixed when some are.</summary>
internal class SourceExportSet(string name, IReadOnlyList<SourceExportFile> files) : INotifyPropertyChanged
{
    private readonly List<SourceExportFile> files = [.. files];
    internal SourceExportSelection? Owner { get; set; }
    public string Name { get; } = name;
    internal IReadOnlyList<SourceExportFile> Files => files;
    internal void Add(SourceExportFile file) => files.Add(file);
    internal int Selected => files.Count(f => f.Selected);
    public bool? IsChecked
    {
        get { int selected = Selected; return files.Count > 0 && selected == files.Count ? true : selected == 0 ? false : null; }
        // A mixed or clear box selects all of its files; a checked one clears them.
        set => Owner?.Set(files, value != false);
    }
    /// <summary>"5 of 12" while only part of the set is selected; empty otherwise.</summary>
    public string Count { get { int selected = Selected; return selected > 0 && selected < files.Count ? $"{selected:N0} of {files.Count:N0}" : ""; } }
    internal void Notify() { PropertyChanged?.Invoke(this, new(nameof(IsChecked))); PropertyChanged?.Invoke(this, new(nameof(Count))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A section of the grid (map files, rtexture, texture): every mission file of its columns.</summary>
internal sealed class SourceExportSection(string id, string label, string detail, IReadOnlyList<SourceExportKind> kinds)
    : SourceExportSet(label, kinds.SelectMany(k => k.Files).ToArray())
{
    internal string Id { get; } = id;
    internal string Label { get; } = label;
    /// <summary>Direct3D or software; shown beside the label when the section has three columns or more.</summary>
    internal string Detail { get; } = detail;
    internal IReadOnlyList<SourceExportKind> Kinds { get; } = kinds;
}

/// <summary>A column of the grid: one kind of mission file (its file name, or the automatic rtexture pack) in every mission that builds it.</summary>
internal sealed class SourceExportKind(string kind, string section, string label, bool automatic, double order) : SourceExportSet(kind, [])
{
    internal string Kind { get; } = kind;
    internal string Section { get; } = section;
    /// <summary>The column label: the stem of a map file, a pack's number of MB, max, or auto.</summary>
    internal string Label { get; } = label;
    internal bool Automatic { get; } = automatic;
    internal double Order { get; } = order;
}

/// <summary>One output of the plan and its checkbox.</summary>
internal sealed class SourceExportFile(SourceExportSelection owner, string path, string group, string family, string? kind, string section, string description, bool selected) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Group { get; } = group;
    internal string Family { get; } = family;
    /// <summary>The grid column of a mission file; null for a shared file.</summary>
    internal string? Kind { get; } = kind;
    internal string Section { get; } = section;
    internal string Description { get; } = description;
    internal bool Selected { get; private set; } = selected;
    public bool? IsChecked { get => Selected; set => owner.Set([this], value != false); }
    internal void Set(bool value) { if (Selected == value) return; Selected = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The export window's destination and what it holds, checked off the UI thread (the window's note, MCP
/// source_export_selection): refused as the export would refuse it, or how many selected outputs are already there.
/// </summary>
internal sealed record SourceExportDestination(string? Path, bool? Exists, int? Existing, int Selected, string? Problem)
{
    internal const string RelativeProblem = "Give the destination as a full path, such as D:\\Games\\RECOIL\\zbd.";
    /// <summary>The folder as typed: trimmed, without surrounding quotes; null when empty.</summary>
    internal static string? Clean(string? text) { string value = (text ?? "").Trim().Trim('"').Trim(); return value.Length == 0 ? null : value; }
    internal static SourceExportDestination Check(string root, string? destination, IReadOnlyCollection<string> selected, CancellationToken token)
    {
        string? path = Clean(destination);
        if (path == null) return new(null, null, null, selected.Count, null);
        SourceExportDestination Refused(string message) => new(path, null, null, selected.Count, message.Length <= 512 ? message : message[..512] + "…");
        if (path.Length > StudioSettings.MaximumExportDestination) return Refused($"Choose a folder whose path has at most {StudioSettings.MaximumExportDestination:N0} characters.");
        if (!System.IO.Path.IsPathFullyQualified(path)) return Refused(RelativeProblem);
        try
        {
            string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            SourceProject.ValidateSeparate(full, root, "export destination");
            SourceProject.RejectLinks(full);
            if (File.Exists(full)) return Refused($"{full} is a file; choose a folder.");
            if (!Directory.Exists(full)) return new(path, false, 0, selected.Count, null);
            int existing = 0;
            foreach (string output in selected)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(System.IO.Path.Combine(full, output.Replace('/', System.IO.Path.DirectorySeparatorChar)))) existing++;
            }
            return new(path, true, existing, selected.Count, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return Refused(ex.Message); }
    }
}
