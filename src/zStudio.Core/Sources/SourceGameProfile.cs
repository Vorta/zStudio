using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

/// <summary>Game-specific source layout and authoring policy, shared by evaluation and editor commands.</summary>
public sealed record SourceGameProfile(GameVariant Game)
{
    private static readonly string[] LibraryFolders = ["data/common/mechs/models", "data/lab/models"];
    /// <summary>Native library roots have scripts and models, but are not campaign world folders.</summary>
    public static IReadOnlyList<string> WorldMaps(IReadOnlyList<string> candidates, GameVariant game)
        => candidates.Where(map => IsMap(map) && (game != GameVariant.MechWarrior3 || !LibraryFolders.Any(folder => folder.StartsWith($"data/{map}/", StringComparison.OrdinalIgnoreCase)))).ToArray();
    public string Name => Game == GameVariant.MechWarrior3 ? "mw3" : "recoil";
    public uint WorldVersion => Game == GameVariant.MechWarrior3 ? 27u : 15u;
    public uint AnimationVersion => Game == GameVariant.MechWarrior3 ? 39u : 28u;
    public bool NativeMissions => Game == GameVariant.MechWarrior3;
    public ExportTarget Target => NativeMissions ? ExportTarget.Mw3 : ExportTarget.V1;
    public static SourceGameProfile For(string root, CancellationToken token = default) => new(SourceProject.Game(root, token));

    public IReadOnlyList<SourceCapability> Capabilities =>
    [
        new("world", true), new("objects", true), new("placements", true), new("ai", true),
        new("zones", true), new("animation", true), new("terrain", true), new("blender", true), new("dithering", true),
        new("difficulty", !NativeMissions, NativeMissions ? "MW3 selects authored missions instead of RECOIL difficulty resources." : null),
        new("playerTank", !NativeMissions, NativeMissions ? "BFT movement forms and loadouts belong to RECOIL." : null),
        new("pickups", !NativeMissions, NativeMissions ? "MW3 has no RECOIL pickup/respawn resource; edit its AIV actors instead." : null),
        new("vtolDrop", !NativeMissions, NativeMissions ? "This helper describes RECOIL's VTOL/parachute release system." : null)
    ];

    internal static void RequireMap(string map)
    {
        if (!IsMap(map))
            throw new InvalidDataException("Invalid source map identifier.");
    }
    internal static bool IsMap(string map) => map.Length is >= 1 and <= 64 && map.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    internal static string[] Resources(string root, string map, SourceBuilder.Snapshot snapshot, string? sourceMission, CancellationToken token, GameVariant? game = null)
    {
        if ((game ?? snapshot.Game) != GameVariant.MechWarrior3)
            return SourceBuilder.ResourceInputs(root, null, snapshot.Added, token, snapshot.Inventory)
                .Concat(SourceBuilder.ResourceInputs(root, map, snapshot.Added, token, snapshot.Inventory)).ToArray();
        var common = SourceBuilder.ResourceInputs(root, null, snapshot.Added, token, snapshot.Inventory);
        var campaign = SourceBuilder.ResourceInputs(root, map, snapshot.Added, token, snapshot.Inventory);
        string prefix = $"data/{map}/zrdr/aipath/";
        if (sourceMission != null && (!sourceMission.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || sourceMission[prefix.Length..].Contains('/') || sourceMission.Length == prefix.Length))
            throw new InvalidDataException("Choose a source mission returned by the mission catalog for this map.");
        return campaign.Where(p => sourceMission != null && p.StartsWith(sourceMission + "/", StringComparison.OrdinalIgnoreCase))
            .Concat(campaign.Where(p => !p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).Concat(common).ToArray();
    }

    public static IReadOnlyList<SourceMissionChoice> Missions(SourceWorkspace workspace, string map, CancellationToken token = default)
        => Missions(workspace.Root, map, new SourceBuilder.Snapshot(workspace.Root, workspace.Overlay(), inventoryToken: token), token);

    internal static IReadOnlyList<SourceMissionChoice> Missions(string root, string map, SourceBuilder.Snapshot snapshot, CancellationToken token)
    {
        RequireMap(map);
        string prefix = $"data/{map}/zrdr/aipath/";
        return SourceProject.Files(root, prefix.TrimEnd('/'), n => n.Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase), snapshot.Added, token, snapshot.Inventory)
            .Where(p => snapshot.Exists(p) && p[prefix.Length..].Count(c => c == '/') == 1).Select(p => p[..p.LastIndexOf('/')]).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetFileName(p).StartsWith('m') && !Path.GetFileName(p).StartsWith("mp", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p, StringComparer.Ordinal).Select(p => new SourceMissionChoice(p, Path.GetFileName(p))).ToArray();
    }

    internal static string[] LibraryModels(string root, SourceBuilder.Snapshot snapshot, CancellationToken token)
        => LibraryFolders.SelectMany(folder => SourceProject.Files(root, folder, Mw3Project.Model, snapshot.Added, token, snapshot.Inventory)).Where(snapshot.Exists).ToArray();

    internal static string[] TextureFolders(string map) => ["data/common/mechs/textures", $"data/{map}/textures", TextureSources.EffectsTextures, TextureSources.CommonTextures];
}

public sealed record SourceCapability(string Name, bool Supported, string? Reason = null);
public sealed record SourceMissionChoice(string SourceMission, string Label);
