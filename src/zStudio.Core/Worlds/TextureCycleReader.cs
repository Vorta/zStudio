using System.Globalization;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>A texture cycle's frames and the texture of the material it is shown on (null when that has none).</summary>
internal sealed record CycledTextures(string? Base, IReadOnlyList<string> Frames);

/// <summary>
/// The texture cycles a mission sets up as the game loads it, each with the material it is shown on: the texture-effect
/// scripts' <c>CycleTextureSetOn</c> on the first material of the node that <c>FindNode</c> (the highest slot of a name)
/// and <c>FindSubNode</c> select, with the <c>CycleTextureSetMap</c> frames it takes, and each effects.zrd effect's
/// <c>MAPS</c> on the first material of the first model at or below its node (zEffect::InitFromPath 0x460070). A cycle
/// changes only which texture its material draws; every frame keeps its own pack word, which sets TEXTUREADDRESSU/V when
/// it is drawn, so a frame no model samples takes the edge mode of the material its cycle is shown on (see
/// <see cref="Addressing"/>), as every frame of the shipped packs has it.
/// </summary>
internal sealed class TextureCycleReader(GameZWorld world, LookupWorkBudget lookups, CancellationToken token)
{
    /// <summary>The most frames a cycle takes (as the preview reads <c>CycleTextureSetOn</c>).</summary>
    internal const int MaximumFrames = 65536;
    private Dictionary<string, WorldNode>? highest;
    private WorldNode? node;
    private List<string>? frames;
    private int count;
    internal List<CycledTextures> Cycles { get; } = [];

    /// <summary>
    /// One instruction the game runs once the world is loaded: its core command and expanded arguments. Each command reads
    /// its first operand (the interpreter's NextToken, zinterp_parse.cpp in retail's DispatchCoreCommand 0x4C20A0).
    /// <c>CycleTextureSetOn</c> without a current node (a failed <c>FindNode</c>) only reports it, so the previous cycle
    /// stays current and takes the frames that follow while it has room; on a node without a model's material the frames
    /// go nowhere.
    /// </summary>
    internal void Run(string command, IReadOnlyList<string> args)
    {
        token.ThrowIfCancellationRequested();
        string argument = args.Count > 0 ? args[0] : "";
        switch (command)
        {
            case "FindNode": node = Highest(argument); break;
            case "FindSubNode": node = node == null ? null : lookups.FindSub(node, argument); break;
            case "CycleTextureSetOn":
                if (node == null) break;
                count = int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n is > 0 and <= MaximumFrames ? n : 0;
                if (node.Model?.Polygons.FirstOrDefault()?.Material is not { } material) { frames = null; break; }
                frames = [];
                Cycles.Add(new(material.Texture?.Name, frames));
                break;
            case "CycleTextureSetMap": if (frames != null && frames.Count < count && args.Count > 0) frames.Add(Stem(argument)); break;
        }
    }

    /// <summary>An effects.zrd effect: the name of its node and its <c>MAPS</c>.</summary>
    internal void Effect(string name, IReadOnlyList<string> maps)
    {
        token.ThrowIfCancellationRequested();
        WorldModel? model = null;
        if (Highest(name) is { } root)
            // Children first to last; the first node with a model ends the search, even when the model is empty.
            model = lookups.Subtree([root], firstChildFirst: true).FirstOrDefault(n => n.Model != null)?.Model;
        lookups.Reserve(maps.Count);
        Cycles.Add(new(model?.Polygons.FirstOrDefault()?.Material?.Texture?.Name, [.. maps.Select(Stem)]));
    }

    /// <summary>
    /// The clamp words a mission's packs store: those of the textures its models sample (<paramref name="sampled"/>), and,
    /// for a texture no model samples, that of the texture of the material its cycles are shown on. A frame shown on
    /// materials sampled with different edge modes is refused, as one texture sampled with different modes is.
    /// </summary>
    internal static Dictionary<string, int> Addressing(IReadOnlyDictionary<string, int> sampled, IEnumerable<CycledTextures> cycles, CancellationToken token)
    {
        Dictionary<string, int> result = new(sampled, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> shownOn = new(StringComparer.OrdinalIgnoreCase);
        foreach (var cycle in cycles)
        {
            token.ThrowIfCancellationRequested();
            if (cycle.Base == null || !sampled.TryGetValue(cycle.Base, out int mode)) continue;
            foreach (string frame in cycle.Frames)
            {
                if (sampled.ContainsKey(frame)) continue;
                if (shownOn.TryAdd(frame, cycle.Base)) result[frame] = mode;
                else if (result[frame] != mode)
                    throw new InvalidDataException($"Texture {JsonData.ShownText(frame)} is a frame of texture cycles shown on {JsonData.ShownText(shownOn[frame])} and {JsonData.ShownText(cycle.Base)}, which are sampled with different edge modes; use one mode for both or give the frames distinct names.");
            }
        }
        return result;
    }

    private WorldNode? Highest(string name)
    {
        if (highest == null)
        {
            lookups.Reserve(2L * world.Nodes.Count);
            var slots = GameZWriter.NodeSlots(world, token);
            highest = new(StringComparer.Ordinal);
            foreach (var candidate in world.Nodes)
            {
                string key = lookups.Name(candidate);
                if (!highest.TryGetValue(key, out var found) || slots[candidate] > slots[found]) highest[key] = candidate;
            }
        }
        lookups.Reserve(1L + name.Length);
        return highest.GetValueOrDefault(name);
    }
    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path.Replace('\\', '/')).ToLowerInvariant();
}
