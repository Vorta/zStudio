using System.ComponentModel;
using System.Globalization;
using System.IO;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// The outputs one export target builds from an exact output plan, as the Export ZBD files window shows them (a mission ×
/// file grid), and that target's remembered checkbox choices. Rows are the plan's groups (Shared files, then the mission
/// folders); columns are the kinds of mission file, derived from the plan in sections: map files, Direct3D (rtexture), and
/// software (texture, target v1) or 32-bit (rgbatexture, target v2) packs. Every header box of the window is a
/// <see cref="SourceExportSet"/> of files whose tri-state follows them, as are the panel's pack boxes
/// (design/export-zbd-files-rgba.md, design/export-target-v1-v2.md). Each target remembers its own choices, by path, except a
/// mission's full-size 32-bit pack, remembered by its role ("m4's full-size pack") so the choice survives a change of its
/// tier; and while every 32-bit file was clear when target v2's choices were last remembered, every 32-bit file starts clear.
/// </summary>
internal sealed class SourceExportSelection
{
    internal const string SharedGroup = "Shared files", AutomaticKind = "automatic rtexture", FullSizeKind = "full-size rgbatexture";
    internal const string MapSection = "map", HardwareSection = "rtexture", SoftwareSection = "texture", RgbaSection = "rgbatexture", SharedSection = "shared";
    /// <summary>The pack formats: the 16-bit packs (Direct3D and software; what dithering reaches) and the 32-bit ones only reRECOIL v2 reads.</summary>
    internal const string SixteenBit = "16-bit", ThirtyTwoBit = "32-bit";
    /// <summary>
    /// The panel's pack boxes, as MCP names them: the Direct3D packs with image.zbd (Recoil, reRECOIL v1 and v2), the software
    /// packs (only Recoil's software mode) and the 32-bit packs with rgbaimage.zbd (only reRECOIL v2).
    /// </summary>
    internal const string Direct3DPack = "direct3d", SoftwarePack = "software", ThirtyTwoBitPack = "32-bit";
    private const long MiB = 1024 * 1024;
    internal static readonly StringComparer Natural = CultureInfo.InvariantCulture.CompareInfo.GetStringComparer(CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
    private static readonly string[] SharedOrder = ["zrdr.zbd", "interp.zbd", "image.zbd", "rgbaimage.zbd", "soundsh.zbd", "soundsm.zbd", "soundsl.zbd"];
    private static readonly string[] MapOrder = ["gamez.zbd", "anim.zbd", "zrdr.zbd"];
    /// <summary>Every output, by group (as <see cref="Groups"/>) and then by path; the order MCP pages them in.</summary>
    internal IReadOnlyList<SourceExportFile> Files { get; }
    /// <summary>Shared files (when the plan has any), then the mission folders in natural order.</summary>
    internal IReadOnlyList<SourceExportSet> Groups { get; }
    /// <summary>The shared row: zrdr, interp, image, rgbaimage, the sound banks from high to low quality, then any other by name.</summary>
    internal IReadOnlyList<SourceExportFile> SharedFiles { get; }
    internal SourceExportSet? Shared { get; }
    /// <summary>The grid's rows: every group but Shared files.</summary>
    internal IReadOnlyList<SourceExportSet> Missions { get; }
    /// <summary>The grid's sections that have columns, in order: map files, rtexture, texture, rgbatexture.</summary>
    internal IReadOnlyList<SourceExportSection> Sections { get; }
    /// <summary>The grid's columns, section by section.</summary>
    internal IReadOnlyList<SourceExportKind> Kinds { get; }
    internal SourceExportSet All { get; }
    internal SourceExportSet AllMissions { get; }
    /// <summary>The panel's Direct3D packs box: rtexture packs of every mission (the automatic one too) and image.zbd.</summary>
    internal SourceExportSet Direct3DPacks { get; }
    /// <summary>The panel's software packs box: texture packs of every mission, the same files as the texture section's box.</summary>
    internal SourceExportSet SoftwarePacks { get; }
    /// <summary>The panel's 32-bit packs box: rgbatexture packs of every mission, and rgbaimage.zbd.</summary>
    internal SourceExportSet ThirtyTwoBitPacks { get; }
    /// <summary>The panel's pack boxes of the target in order, by the names MCP gives them: direct3d and software (v1), or direct3d and 32-bit (v2).</summary>
    internal IReadOnlyList<(string Pack, SourceExportSet Set)> PackSets { get; }
    /// <summary>Every 16-bit pack (the Direct3D and software boxes together): what Texture dithering changes. Not a box of its own.</summary>
    internal SourceExportSet SixteenBitPacks { get; }
    /// <summary>Whether every 32-bit file was clear when the choices were last remembered (new 32-bit files then start clear).</summary>
    private readonly bool rememberedClear;
    /// <summary>
    /// How a 32-bit file the plan adds starts, as these choices would be remembered: clear while every 32-bit file is clear, as
    /// remembered when the plan has none.
    /// </summary>
    internal bool NewThirtyTwoBitClear => ThirtyTwoBitPacks.Files.Count > 0 ? ThirtyTwoBitPacks.Selected == 0 : rememberedClear;
    internal Func<bool> IsCurrent { get; }
    /// <summary>The game these outputs are built for: the plan's outputs that <see cref="ExportTargets.Builds"/> gives it.</summary>
    internal ExportTarget Target { get; }
    internal event Action? Changed;
    private readonly Dictionary<(SourceExportSet, SourceExportKind), SourceExportFile> cells = [];
    internal int SelectedCount => Files.Count(f => f.Selected);
    internal bool AllSelected => Files.Count > 0 && Files.All(f => f.Selected);
    internal string[] SelectedPaths => Files.Where(f => f.Selected).Select(f => f.Path).ToArray();

    /// <summary>
    /// The outputs of <paramref name="plan"/> that <paramref name="target"/> builds, with that target's remembered choices from
    /// <paramref name="settings"/>. <paramref name="current"/> says whether the plan still describes the open project.
    /// </summary>
    internal SourceExportSelection(IReadOnlyList<SourceOutputPlan> plan, ExportTarget target, StudioSettings settings, Func<bool> current)
    {
        IsCurrent = current; Target = target;
        plan = [.. plan.Where(output => target.Builds(output))];
        var uncheckedPaths = settings.ExportUnchecked(target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        rememberedClear = target == ExportTarget.V2 && settings.ExportRgbaClearedV2;
        // Each mission's full-size 32-bit pack, which its smaller tiers' descriptions name.
        Dictionary<string, string> fullTiers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var output in plan.Where(FullSizeRgba)) fullTiers.TryAdd(GroupOf(output.Path), FileName(output.Path));
        List<SourceExportFile> files = []; List<SourceExportSet> groups = [];
        Dictionary<string, SourceExportKind> kinds = new(StringComparer.OrdinalIgnoreCase);
        foreach (var group in plan.GroupBy(p => GroupOf(p.Path), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key == SharedGroup ? 0 : Mission(g.Key) ? 1 : 2).ThenBy(g => g.Key, Natural))
        {
            List<SourceExportFile> members = [];
            foreach (var output in group.OrderBy(p => p.Path, Natural))
            {
                bool shared = group.Key == SharedGroup;
                string name = FileName(output.Path), section = shared ? SharedSection : SectionOf(output);
                // The automatic rtexture pack and the full-size 32-bit tier are named for what their textures need: one column each.
                bool named = output.Automatic || FullSizeRgba(output);
                string? kind = shared ? null : output.Automatic ? AutomaticKind : FullSizeRgba(output) ? FullSizeKind : name.ToLowerInvariant();
                string? format = FormatOf(output), pack = PackOf(output);
                string key = ChoiceKey(output);
                bool selected = !(format == ThirtyTwoBit && rememberedClear) && !uncheckedPaths.Contains(key);
                var file = new SourceExportFile(this, output.Path, group.Key, output.Family, kind, section, Describe(output, group.Key, shared, fullTiers.GetValueOrDefault(group.Key), target), selected)
                {
                    Format = format, Pack = pack, ChoiceKey = key, FullSize = output.FullSize, EstimatedBytes = output.Estimate?.Bytes,
                    BudgetMegabytes = output.Pack is { Kind: not (TexturePackKind.Interface or TexturePackKind.RgbaInterface), BudgetBytes: long budget } ? budget / MiB : null,
                };
                members.Add(file); files.Add(file);
                if (kind == null) continue;
                if (!kinds.TryGetValue(kind, out var column))
                    kinds.Add(kind, column = new SourceExportKind(kind, section, Label(name, section, named), named, Order(name, section, output)));
                column.Add(file);
            }
            groups.Add(new SourceExportSet(group.Key, members));
        }
        Files = files; Groups = groups;
        Shared = groups.FirstOrDefault(g => g.Name == SharedGroup);
        SharedFiles = Shared == null ? [] : Shared.Files.OrderBy(f => Array.IndexOf(SharedOrder, f.Path.ToLowerInvariant()) is int i and >= 0 ? i : SharedOrder.Length).ThenBy(f => f.Path, Natural).ToArray();
        Missions = groups.Where(g => g != Shared).ToArray();
        List<SourceExportSection> sections = [];
        foreach (var (id, label, detail) in new[] { (MapSection, "Map files", ""), (HardwareSection, "rtexture", "Direct3D"), (SoftwareSection, "texture", "software"), (RgbaSection, "rgbatexture", "32-bit") })
        {
            var columns = kinds.Values.Where(k => k.Section == id).OrderBy(k => k.Order).ThenBy(k => k.Kind, Natural).ToArray();
            if (columns.Length > 0) sections.Add(new SourceExportSection(id, label, detail, columns));
        }
        Sections = sections; Kinds = sections.SelectMany(s => s.Kinds).ToArray();
        foreach (var mission in Missions) foreach (var file in mission.Files) cells[(mission, kinds[file.Kind!])] = file;
        All = new SourceExportSet("All files", files);
        AllMissions = new SourceExportSet("All missions", Missions.SelectMany(m => m.Files).ToArray());
        Direct3DPacks = new SourceExportSet("Direct3D packs", files.Where(f => f.Pack == Direct3DPack).ToArray());
        SoftwarePacks = new SourceExportSet("Software packs", files.Where(f => f.Pack == SoftwarePack).ToArray());
        ThirtyTwoBitPacks = new SourceExportSet("32-bit packs", files.Where(f => f.Pack == ThirtyTwoBitPack).ToArray());
        // The target's boxes only: no box of the other target shows in the window or in MCP.
        PackSets = target == ExportTarget.V2 ? [(Direct3DPack, Direct3DPacks), (ThirtyTwoBitPack, ThirtyTwoBitPacks)] : [(Direct3DPack, Direct3DPacks), (SoftwarePack, SoftwarePacks)];
        SixteenBitPacks = new SourceExportSet("16-bit packs", files.Where(f => f.Format == SixteenBit).ToArray());
        foreach (var set in Sets()) set.Owner = this;
    }

    /// <summary>The file of a mission in a column, or null where the project builds none.</summary>
    internal SourceExportFile? Cell(SourceExportSet mission, SourceExportKind kind) => cells.GetValueOrDefault((mission, kind));
    private IEnumerable<SourceExportSet> Sets() => [All, AllMissions, Direct3DPacks, SoftwarePacks, ThirtyTwoBitPacks, SixteenBitPacks, .. Groups, .. Sections, .. Kinds];
    /// <summary>What a set's files will take on disk: the sum of their estimates, null when it is empty or any file has none (never a partial sum).</summary>
    internal static long? EstimatedBytes(SourceExportSet set) =>
        set.Files.Count > 0 && set.Files.All(f => f.EstimatedBytes != null) ? set.Files.Sum(f => f.EstimatedBytes!.Value) : null;
    /// <summary>A pack's format, from the plan's pack kind (never the file name); null for a file that is not a pack.</summary>
    private static string? FormatOf(SourceOutputPlan output) => output.Pack?.Kind switch
    {
        TexturePackKind.Hardware or TexturePackKind.Software or TexturePackKind.Interface => SixteenBit,
        TexturePackKind.Rgba or TexturePackKind.RgbaInterface => ThirtyTwoBit,
        _ => null,
    };
    /// <summary>A pack's panel box, from the plan's pack kind: Direct3D (with image.zbd), software or 32-bit; null for a file that is not a pack.</summary>
    private static string? PackOf(SourceOutputPlan output) => output.Pack?.Kind switch
    {
        TexturePackKind.Hardware or TexturePackKind.Interface => Direct3DPack,
        TexturePackKind.Software => SoftwarePack,
        TexturePackKind.Rgba or TexturePackKind.RgbaInterface => ThirtyTwoBitPack,
        _ => null,
    };
    /// <summary>The key a choice is remembered by: the normalized path, or for a mission's full-size 32-bit pack its role (<c>m4/full-size rgbatexture</c>).</summary>
    private static string ChoiceKey(SourceOutputPlan output) => Normalize(FullSizeRgba(output) ? $"{GroupOf(output.Path)}/{FullSizeKind}" : output.Path);
    /// <summary>
    /// A size as the window gives it, in the window's culture: whole MB, one decimal below 1 MB (at least 0.1 MB), GB with one
    /// decimal from 1,000 MB as rounded.
    /// </summary>
    internal static string Size(long bytes)
    {
        double megabytes = bytes / (double)MiB, rounded = Math.Round(megabytes, MidpointRounding.AwayFromZero);
        return rounded >= 1000 ? (megabytes / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
            : megabytes < 0.95 ? Math.Max(0.1, Math.Round(megabytes, 1, MidpointRounding.AwayFromZero)).ToString("0.0", CultureInfo.CurrentCulture) + " MB"
            : rounded.ToString("N0", CultureInfo.CurrentCulture) + " MB";
    }
    internal static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();
    private static bool Mission(string name) => name.Length > 1 && (name[0] is 'm' or 'M') && name.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
    private static string GroupOf(string path) { int slash = path.IndexOf('/'); return slash < 0 ? SharedGroup : path[..slash]; }
    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static string SectionOf(SourceOutputPlan output) => output.Family == "rgbatextures" ? RgbaSection : output.Family != "textures" ? MapSection
        : output.Pack?.Kind switch { TexturePackKind.Hardware => HardwareSection, TexturePackKind.Software => SoftwareSection, _ => MapSection };
    /// <summary>A mission's full-size 32-bit tier, whose N the textures' authored sizes set.</summary>
    private static bool FullSizeRgba(SourceOutputPlan output) => output.Family == "rgbatextures" && output.FullSize;
    private static string Label(string name, string section, bool named)
    {
        if (named) return section == RgbaSection ? "full" : "auto";
        string stem = name.EndsWith(".zbd", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (section == MapSection) return stem;
        string prefix = section switch { HardwareSection => "rtexture", RgbaSection => "rgbatexture", _ => "texture" };
        return stem.Length > prefix.Length && stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? stem[prefix.Length..] : stem;
    }
    /// <summary>
    /// Map files: gamez, anim, zrdr, then others by name; packs: by budget, a pack without one after, the automatic pack and the
    /// full-size 32-bit tier last.
    /// </summary>
    private static double Order(string name, string section, SourceOutputPlan output) => section == MapSection
        ? Array.IndexOf(MapOrder, name.ToLowerInvariant()) is int i and >= 0 ? i : MapOrder.Length
        : output.Automatic || FullSizeRgba(output) ? double.MaxValue : output.Pack?.BudgetBytes is long budget ? budget : double.MaxValue / 2;

    /// <summary>The game a target exports for, as the window names it: "Recoil and reRECOIL v1" or "reRECOIL v2".</summary>
    internal static string Game(ExportTarget target) => target == ExportTarget.V2 ? "reRECOIL v2" : "Recoil and reRECOIL v1";

    /// <summary>
    /// A file's description (tooltip and help text) in the window of <paramref name="target"/>: map files name their format
    /// and the games that read it; a smaller 32-bit tier names its mission's <paramref name="fullTier"/>.
    /// </summary>
    private static string Describe(SourceOutputPlan output, string group, bool shared, string? fullTier, ExportTarget target)
    {
        const double MiB = SourceExportSelection.MiB;
        string about = output.Estimate is { } known ? $"; about {Size(known.Bytes)}" : "";
        bool v2 = target == ExportTarget.V2;
        return output.Family switch
        {
            "world" => $"{group}'s world, built by gamegen/{group}.gs from its glTF models: " + (v2 ? "version 200, which only reRECOIL v2 reads" : "version 15, which Recoil and reRECOIL v1 read"),
            "animations" => $"{group}'s animations, compiled from data/{group}/zrdr/anim.zad against this world: " + (v2 ? "format 200, which only reRECOIL v2 reads" : "format 28, which Recoil and reRECOIL v1 read"),
            "archive" => shared ? "Common resources: the .zrd files of every zrdr folder under data/common and the TrueType fonts (.ttf) under data/common/fonts" : $"{group}'s resources: the .zrd files under data/{group}/zrdr",
            "scripts" => "Prepared scripts: gamegen/*.gs and gamegen/support/*.gw",
            "sounds" => FileName(output.Path).ToLowerInvariant() switch
            {
                "soundsh.zbd" => v2 ? "High-quality sound bank from data/common/sounds: the only sound bank reRECOIL v2 reads" : "High-quality sound bank from data/common/sounds, for the Sound quality option",
                "soundsm.zbd" => "Medium-quality sound bank from data/common/sounds, for the Sound quality option",
                "soundsl.zbd" => "Low-quality sound bank from data/common/sounds, for the Sound quality option",
                _ => "Sound bank from data/common/sounds",
            },
            "images" => $"16-bit interface images and fonts (data/common, data/mN/images){about}" + (v2 ? ". reRECOIL v2 draws them until its new renderer." : ""),
            "rgbaimages" => $"32-bit interface images and fonts, with the PNGs' exact pixels (data/common, data/mN/images){about}. reRECOIL v2 draws them from its new renderer on.",
            "rgbatextures" when output.Pack is { } pack => output.FullSize
                ? (output.Estimate is { } full
                    ? $"32-bit texture pack: all {full.Textures:N0} textures at their authored size, {Size(full.NeedBytes)} of texture memory{about}"
                    : "32-bit texture pack: every texture at its authored size")
                : $"32-bit texture pack with {pack.BudgetBytes / MiB:0.#} MB of texture memory: "
                    + (output.Estimate is { } fitted ? $"{fitted.Reduced:N0} of {fitted.Textures:N0} textures resampled smaller to fit{about}" : "textures resampled smaller to fit")
                    + (fullTier != null ? $". The full-size pack is {fullTier}." : "."),
            "textures" when output.Pack is { } pack => (output.Automatic
                ? $"Automatic 16-bit Direct3D pack: every texture at full size, named for the {pack.BudgetBytes / MiB:0.#} MB they need"
                : pack.Kind == TexturePackKind.Hardware ? $"16-bit Direct3D texture pack for {pack.BudgetBytes / MiB:0.#} MB cards, textures up to {pack.MaximumDimension} texels"
                : pack.BudgetBytes is long budget ? $"16-bit software-renderer texture pack, {budget / MiB:0.#} MB, textures up to {pack.MaximumDimension} texels"
                : $"16-bit software-renderer texture pack without a budget, textures up to {pack.MaximumDimension} texels") + about,
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
    /// <summary>
    /// Remembers every file's choice under its <see cref="SourceExportFile.ChoiceKey"/> in the target's list and, for target
    /// v2, how new 32-bit files start (<see cref="NewThirtyTwoBitClear"/>); a failed save leaves the settings as they were.
    /// </summary>
    internal void Remember(StudioSettings settings)
    {
        var before = SourceExportMemory.Of(settings);
        try { Apply(settings); settings.Save(); }
        catch { before.Restore(settings); throw; }
    }
    /// <summary>
    /// Writes the choices into the target's list of <paramref name="settings"/> without saving: every file's choice under its key;
    /// target v1 also drops the 32-bit keys an earlier version kept in that list (no v1 file has one). Other keys stay: they
    /// are other projects' files. Target v2 records how new 32-bit files start; a plan without 32-bit files leaves that as it is.
    /// </summary>
    internal void Apply(StudioSettings settings)
    {
        var uncheckedPaths = settings.ExportUnchecked(Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Target == ExportTarget.V1) uncheckedPaths.RemoveWhere(ThirtyTwoBitKey);
        foreach (var file in Files)
            if (file.Selected) uncheckedPaths.Remove(file.ChoiceKey); else uncheckedPaths.Add(file.ChoiceKey);
        if (uncheckedPaths.Count > StudioSettings.MaximumExportChoices) throw new InvalidOperationException("Too many remembered export choices. Select more files before closing this window.");
        settings.SetExportUnchecked(Target, uncheckedPaths.Order(StringComparer.Ordinal).ToList());
        if (Target == ExportTarget.V2 && ThirtyTwoBitPacks.Files.Count > 0) settings.ExportRgbaClearedV2 = ThirtyTwoBitPacks.Selected == 0;
    }
    /// <summary>A remembered choice of a 32-bit file: an rgbatexture pack, rgbaimage.zbd or a mission's full-size 32-bit pack.</summary>
    internal static bool ThirtyTwoBitKey(string key)
    {
        string name = FileName(key);
        return name.StartsWith("rgbatexture", StringComparison.OrdinalIgnoreCase) || name.Equals("rgbaimage.zbd", StringComparison.OrdinalIgnoreCase)
            || name.Equals(FullSizeKind, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The export window's remembered state in the settings (target, both targets' choices and destinations, v2's 32-bit start
/// rule), taken before a change so that a failed save can put it back.
/// </summary>
internal sealed record SourceExportMemory(string Target, List<string> UncheckedV1, List<string> UncheckedV2, string? DestinationV1, string? DestinationV2, bool RgbaClearedV2)
{
    internal static SourceExportMemory Of(StudioSettings settings) => new(settings.ExportTarget, settings.ExportUncheckedOutputs, settings.ExportUncheckedOutputsV2,
        settings.ExportDestination, settings.ExportDestinationV2, settings.ExportRgbaClearedV2);
    internal void Restore(StudioSettings settings)
    {
        settings.ExportTarget = Target; settings.ExportUncheckedOutputs = UncheckedV1; settings.ExportUncheckedOutputsV2 = UncheckedV2;
        settings.ExportDestination = DestinationV1; settings.ExportDestinationV2 = DestinationV2; settings.ExportRgbaClearedV2 = RgbaClearedV2;
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

/// <summary>A section of the grid (map files, rtexture, texture, rgbatexture): every mission file of its columns.</summary>
internal sealed class SourceExportSection(string id, string label, string detail, IReadOnlyList<SourceExportKind> kinds)
    : SourceExportSet(label, kinds.SelectMany(k => k.Files).ToArray())
{
    internal string Id { get; } = id;
    internal string Label { get; } = label;
    /// <summary>Direct3D, software or 32-bit; shown beside the label when the section has three columns or more.</summary>
    internal string Detail { get; } = detail;
    internal IReadOnlyList<SourceExportKind> Kinds { get; } = kinds;
}

/// <summary>
/// A column of the grid: one kind of mission file (its file name, the automatic rtexture pack or the full-size 32-bit tier)
/// in every mission that builds it; a 32-bit tier's own column holds it only in missions that build it below their
/// full-size tier.
/// </summary>
internal sealed class SourceExportKind(string kind, string section, string label, bool automatic, double order) : SourceExportSet(kind, [])
{
    internal string Kind { get; } = kind;
    internal string Section { get; } = section;
    /// <summary>The column label: the stem of a map file, a pack's number of MB, max, auto or full.</summary>
    internal string Label { get; } = label;
    /// <summary>Whether the column is a pack named per mission for what its textures need: the automatic rtexture pack or the full-size rgbatexture tier.</summary>
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
    /// <summary>"16-bit" or "32-bit" for a pack, from the plan's pack kind (what dithering reaches); null for a file that is not a pack.</summary>
    internal string? Format { get; init; }
    /// <summary>The panel box holding the pack: "direct3d", "software" or "32-bit"; null for a file that is not a pack.</summary>
    internal string? Pack { get; init; }
    /// <summary>What the choice is remembered by: the normalized path, or a mission's full-size 32-bit pack's role.</summary>
    internal string ChoiceKey { get; init; } = SourceExportSelection.Normalize(path);
    /// <summary>Whether the pack holds every texture at its full size: the full-size 32-bit tier, or the 16-bit pack the plan promises at full size.</summary>
    internal bool FullSize { get; init; }
    /// <summary>A texture pack's budget in MB (its N); null for other files and packs without one.</summary>
    internal long? BudgetMegabytes { get; init; }
    /// <summary>A pack's estimated size in bytes (<see cref="SourceOutputPlan.Estimate"/>); null for other files or when not known.</summary>
    internal long? EstimatedBytes { get; init; }
    internal bool Selected { get; private set; } = selected;
    public bool? IsChecked { get => Selected; set => owner.Set([this], value != false); }
    internal void Set(bool value) { if (Selected == value) return; Selected = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// A pack already in the destination that the export does not replace and the game would load instead of an exported one:
/// its path, the exported pack it would win over, and which game loads it (the game for rtexture, reRECOIL v2 for rgbatexture).
/// </summary>
internal sealed record SourceExportShadow(string Path, string Instead, string Game);

/// <summary>
/// The export window's destination and what it holds, checked off the UI thread (the window's note, MCP
/// source_export_selection): refused as the export would refuse it, or how many selected outputs are already there, and the
/// cautions, the most serious first (design/export-target-v1-v2.md §6): for target v2, that the folder is also target v1's
/// (<see cref="SharedWithV1"/>); for target v1, the version-200 world or animation files left beside the ones the selection
/// writes (at most <see cref="ShadowsShown"/> of <see cref="Version200Count"/>); and the larger packs left there that the
/// game would load instead of the exported ones (at most <see cref="ShadowsShown"/>, of <see cref="ShadowCount"/>).
/// </summary>
internal sealed record SourceExportDestination(string? Path, bool? Exists, int? Existing, int Selected, string? Problem)
{
    internal const int ShadowsShown = 16;
    internal IReadOnlyList<SourceExportShadow> Shadows { get; init; } = [];
    internal int ShadowCount { get; init; }
    /// <summary>Target v1: a mission's gamez.zbd or anim.zbd at version or format 200 that stays beside the other file of that mission, which the selection writes.</summary>
    internal IReadOnlyList<string> Version200 { get; init; } = [];
    internal int Version200Count { get; init; }
    /// <summary>Target v2: the folder is target v1's remembered one too, and the selection writes a world or animations Recoil cannot load.</summary>
    internal bool SharedWithV1 { get; init; }
    internal const string RelativeProblem = "Give the destination as a full path, such as D:\\Games\\RECOIL\\zbd.";
    /// <summary>The folder as typed: trimmed, without surrounding quotes; null when empty.</summary>
    internal static string? Clean(string? text) { string value = (text ?? "").Trim().Trim('"').Trim(); return value.Length == 0 ? null : value; }
    /// <summary>
    /// Checks <paramref name="destination"/> for an export of <paramref name="selected"/> for <paramref name="target"/>, as the
    /// export would; <paramref name="v1Destination"/> is target v1's remembered folder, which target v2's caution compares with.
    /// </summary>
    internal static SourceExportDestination Check(string root, string? destination, IReadOnlyCollection<string> selected, CancellationToken token,
        ExportTarget target = ExportTarget.V1, string? v1Destination = null)
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
            bool shared = target == ExportTarget.V2 && SameFolder(full, v1Destination) && selected.Any(MapFile);
            if (!Directory.Exists(full)) return new(path, false, 0, selected.Count, null) { SharedWithV1 = shared };
            int existing = 0;
            foreach (string output in selected)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(System.IO.Path.Combine(full, output.Replace('/', System.IO.Path.DirectorySeparatorChar)))) existing++;
            }
            var version200 = target == ExportTarget.V1 ? LeftVersion200(full, selected, token) : [];
            var shadows = Shadowing(full, selected, token);
            return new(path, true, existing, selected.Count, null)
            {
                Shadows = [.. shadows.Take(ShadowsShown)], ShadowCount = shadows.Count, SharedWithV1 = shared,
                Version200 = [.. version200.Take(ShadowsShown)], Version200Count = version200.Count,
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return Refused(ex.Message); }
    }
    /// <summary>The caution when target v2's folder is target v1's too (design/export-target-v1-v2.md §6), in the window and in the export's report.</summary>
    internal const string SharedWithV1Caution = "Exports for Recoil and reRECOIL v1 go to this folder too. They cannot load the version-200 worlds and animations that this export writes.";
    /// <summary>Whether <paramref name="folder"/> is target v1's folder too while <paramref name="written"/> holds a world or animations (target v2's caution).</summary>
    internal static bool SharesV1Folder(string folder, string? v1Folder, IEnumerable<string> written)
    {
        try { return SameFolder(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder)), v1Folder) && written.Any(MapFile); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    /// <summary>A mission's world or animations (<c>mN/gamez.zbd</c>, <c>mN/anim.zbd</c>), the files whose format the target sets.</summary>
    private static bool MapFile(string output) => output.Count(c => c == '/') == 1
        && (output.EndsWith("/gamez.zbd", StringComparison.OrdinalIgnoreCase) || output.EndsWith("/anim.zbd", StringComparison.OrdinalIgnoreCase));
    /// <summary>Whether <paramref name="other"/> names the folder <paramref name="full"/> (full paths, case ignored); false when it is not a usable full path.</summary>
    private static bool SameFolder(string full, string? other)
    {
        if (Clean(other) is not { } folder || !System.IO.Path.IsPathFullyQualified(folder)) return false;
        try { return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder)).Equals(full, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    /// <summary>
    /// For each mission where the selection writes gamez.zbd or anim.zbd but not both, the other file when the destination holds
    /// it at version or format 200 (reRECOIL v2's): Recoil and reRECOIL v1 then cannot load that mission. Only each candidate's
    /// header is read (<see cref="FormatRegistry.Probe"/>); a file that cannot be read is left to the export's report.
    /// </summary>
    internal static List<string> LeftVersion200(string destination, IReadOnlyCollection<string> selected, CancellationToken token)
    {
        List<string> found = [];
        foreach (var mission in selected.Where(MapFile).GroupBy(p => p[..p.IndexOf('/')], StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, SourceExportSelection.Natural))
        {
            token.ThrowIfCancellationRequested();
            var written = mission.Select(p => p[(p.IndexOf('/') + 1)..].ToLowerInvariant()).ToHashSet();
            if (written.Count != 1) continue;
            (string file, FormatFamily family) = written.Contains("gamez.zbd") ? ("anim.zbd", FormatFamily.Animation) : ("gamez.zbd", FormatFamily.GameZ);
            string left = System.IO.Path.Combine(destination, mission.Key, file);
            if (!File.Exists(left)) continue;
            var probe = FormatRegistry.Probe(left);
            if (probe.Family == family && probe.Version == 200) found.Add($"{mission.Key}/{file}");
        }
        return found;
    }

    /// <summary>
    /// The packs in the mission folders the selection writes packs of a family into that are larger than the largest it
    /// writes there and that it does not write: the game opens the largest rtexture&lt;N&gt; at or below its texture memory, and
    /// reRECOIL v2 the largest rgbatexture&lt;N&gt;, so either would load the leftover instead. Software packs, chosen by an option,
    /// are left to the export's report. Each folder is listed as the export lists it (<see cref="BuildProfiles.ShadowingPacks(string, string, IReadOnlyCollection{string}, CancellationToken, bool)"/>);
    /// a folder that cannot be listed is skipped (the export's report still names what it leaves).
    /// </summary>
    internal static List<SourceExportShadow> Shadowing(string destination, IReadOnlyCollection<string> selected, CancellationToken token)
    {
        List<SourceExportShadow> found = [];
        foreach (var mission in selected.Where(p => p.Count(c => c == '/') == 1).GroupBy(p => p[..p.IndexOf('/')], StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, SourceExportSelection.Natural))
        {
            token.ThrowIfCancellationRequested();
            string[] names = [.. mission.Select(p => p[(p.IndexOf('/') + 1)..])];
            static long? Megabytes(string name, bool rgba) => rgba ? BuildProfiles.RgbaTextureMegabytes(name)
                : TexturePackVariant.FromFileName(name) is { Kind: TexturePackKind.Hardware, BudgetBytes: long budget } ? budget >> 20 : null;
            // The largest pack of each family the selection writes there; none: nothing to compare with, nothing listed.
            var ours = new[] { false, true }.Select(rgba => (Rgba: rgba, Largest: names.Select(n => (Name: n, Megabytes: Megabytes(n, rgba))).Where(n => n.Megabytes != null)
                .OrderByDescending(n => n.Megabytes).FirstOrDefault())).Where(f => f.Largest.Megabytes != null).ToArray();
            if (ours.Length == 0) continue;
            IReadOnlyList<string> left;
            try { left = BuildProfiles.LeftPacks(destination, mission.Key, names, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var (rgba, largest) in ours)
                foreach (string pack in left)
                    if (Megabytes(pack, rgba) is long megabytes && megabytes > largest.Megabytes)
                        found.Add(new(pack, $"{mission.Key}/{largest.Name}".ToLowerInvariant(), rgba ? "reRECOIL v2" : "the game"));
        }
        return found;
    }
}
