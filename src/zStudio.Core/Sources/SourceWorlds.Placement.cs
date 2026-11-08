using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>Assets admitted for one new map object. Texture identities are checked again against the real build.</summary>
public sealed record SourcePlacementAssets(IReadOnlySet<string> Names, IReadOnlyDictionary<string, string?> Textures);

public static partial class SourceWorlds
{
    public static bool IsPlacementFolder(string path, string mission, string kind, GameVariant game = GameVariant.Recoil)
    {
        path = SourceWorkspace.Normalize(path);
        return path.StartsWith($"data/common/{kind}/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith($"data/{mission}/{kind}/", StringComparison.OrdinalIgnoreCase)
            || game == GameVariant.MechWarrior3 && (path.StartsWith($"data/common/mechs/{kind}/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith($"data/lab/{kind}/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith($"data/common/effects/{kind}/", StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<SourceModelChoice> Models(SourceWorkspace workspace, string mission, CancellationToken token = default)
    {
        var game = SourceProject.Game(workspace.Root, token);
        return Models(workspace, token).Where(m => IsPlacementFolder(m.Path, mission, "models", game)).ToArray();
    }

    /// <summary>Inspect only the added graph; older world assets keep their existing loading rules.</summary>
    public static SourcePlacementAssets ValidatePlacement(SourceWorkspace workspace, string mission, SourceModelAddition addition, CancellationToken token = default)
    {
        try { return InspectPlacement(workspace, mission, addition, token); }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"Cannot place {JsonData.ShownText(addition.Model, 1024)} on {JsonData.ShownText(mission, 32)}.\n\n{ex.Message}\n\nWorkspace: {workspace.Root}\nModels: data/common/models/ or data/{mission}/models/\nTextures: data/common/textures/ or data/{mission}/textures/\nSubfolders are allowed. Move misplaced files and update their glTF references; zStudio does not copy assets.", ex);
        }
    }
    private static SourcePlacementAssets InspectPlacement(SourceWorkspace workspace, string mission, SourceModelAddition addition, CancellationToken token)
    {
        ValidateTokens(addition);
        SourceGameProfile.RequireMap(mission);
        var game = SourceProject.Game(workspace.Root, token);
        List<string> problems = [];
        int problemCount = 0;
        void Problem(string text) { problemCount++; if (problems.Count < 32) problems.Add(JsonData.ShownText(text, 1536)); }
        void Folder(string path, string kind)
        {
            if (!IsPlacementFolder(path, mission, kind, game)) Problem($"{path}: move this file into a {kind} folder of this map or the game's common library, then update the glTF reference.");
        }
        HashSet<string> names = new(StringComparer.Ordinal);
        Dictionary<string, string?> textures = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> pending = new([SourceWorkspace.Normalize(addition.Model)]);
        long remaining = 128L << 20;
        void Reserve(long bytes)
        {
            token.ThrowIfCancellationRequested();
            if (bytes < 0 || bytes > remaining) throw new InvalidDataException("The model exceeds the 128 MiB placement inspection allowance.");
            remaining -= bytes;
        }
        while (pending.TryDequeue(out string? path))
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(path)) continue;
            Reserve(path.Length * 2L + 1);
            Folder(path, "models");
            byte[] bytes = workspace.ReadModel(path, token, remaining, GltfDocument.MaximumJsonBytes)
                ?? throw new InvalidDataException($"Model dependency {path} is missing. Restore it and update the glTF reference.");
            Reserve(bytes.LongLength);
            var doc = WorldAssembler.ReadModel(bytes, path, (file, limit) =>
            {
                byte[] buffer = workspace.Read(file, token, Math.Min(limit, remaining))
                    ?? throw new InvalidDataException($"{path}: buffer {file} is missing.");
                Reserve(buffer.LongLength); return buffer;
            }, token, remaining);
            foreach (var dependency in SourceMissionModels.Dependencies(path, doc, Reserve)) pending.Enqueue(dependency);
            foreach (var node in doc.AllNodes())
            {
                Reserve(1); names.Add(node.Name);
                if (node.Extras?[WorldGltf.Key]?["name"] is { } named) names.Add(named.GetValue<string>());
                if (node.Mesh == null) continue;
                foreach (var primitive in node.Mesh.Primitives)
                {
                    Reserve(1);
                    var material = primitive.Material;
                    if (material == null) continue;
                    if (material.EmbeddedImage) { Problem($"{path}, material {material.Name}: embedded images cannot be placed. Export glTF with external PNG textures in data/common/textures/ or data/{mission}/textures/."); continue; }
                    string? image = material.ImageUri;
                    string? name = image == null ? material.Extras?[WorldGltf.Key]?["texture"]?.GetValue<string>() : Path.GetFileNameWithoutExtension(image);
                    if (name == null) continue;
                    string? texture = image == null ? null : WorldAssembler.Relative(path, image);
                    if (texture != null)
                    {
                        Folder(texture, "textures");
                        if (!texture.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !workspace.Exists(texture, token))
                            Problem($"{path}, material {material.Name}: {texture} must be an existing external PNG. Save the PNG in an allowed texture folder and update this image URI.");
                    }
                    if (textures.TryGetValue(name, out var other) && other != null && texture != null && !other.Equals(texture, StringComparison.OrdinalIgnoreCase))
                        Problem($"{path}: texture name {name} refers to both {other} and {texture}. Give different textures different filenames and update their image URIs.");
                    else if (texture != null || !textures.ContainsKey(name)) textures[name] = texture;
                }
            }
        }
        if (problems.Count > 0) throw new InvalidDataException($"Cannot place {addition.Model} on {mission}.\n\n" + string.Join("\n\n", problems)
            + (problemCount > problems.Count ? $"\n\n{problemCount - problems.Count} more problems omitted." : "")
            );
        return new(names, textures);
    }

    public static void ValidatePlacementTextures(SourcePlacementAssets assets, SourceSceneSnapshot built, CancellationToken token = default)
    {
        foreach (var (name, expected) in assets.Textures)
        {
            token.ThrowIfCancellationRequested();
            if (!built.Textures.TryGetValue(name, out var texture)) throw new InvalidDataException($"Texture {name} has no usable PNG. Put it in data/common/textures/ or data/{built.Mission}/textures/ and update the glTF reference.");
            if (!IsPlacementFolder(texture.Path, built.Mission, "textures", built.Game) || expected != null && !texture.Path.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Texture {name}: the map would use {texture.Path}{(expected == null ? "" : $" instead of {expected}")}. Use data/common/textures/ or data/{built.Mission}/textures/; rename conflicting textures and update their glTF references.");
            _ = texture.Decode(token);
        }
    }
}
