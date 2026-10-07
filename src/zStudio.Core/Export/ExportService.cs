using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Core.Export;

public sealed record ExportProgress(int Completed, int Total, string Name);
public sealed record ExportResult(string Directory, int Completed, IReadOnlyList<string> Errors);
public interface IAssetExporter
{
    Task<ExportResult> ExportAsync(ZbdDocument document, IReadOnlyList<AssetRecord> assets, string destination, bool jsonOnly,
        string? preferredTexturePack = null, int lodLevel = 0, IProgress<ExportProgress>? progress = null, CancellationToken token = default);
}

public sealed partial class ExportService(AssetResolver resolver) : IAssetExporter
{
    public async Task<ExportResult> ExportAsync(ZbdDocument doc, IReadOnlyList<AssetRecord> assets, string destination, bool jsonOnly,
        string? preferredTexturePack = null, int lodLevel = 0, IProgress<ExportProgress>? progress = null, CancellationToken token = default)
    {
        using DirectoryLease directories = new();
        destination = ValidateExportDirectory(destination, directories);
        directories.Hold(destination, create: true);
        string folder = SafeName(Path.GetFileName(Path.GetDirectoryName(doc.Path)) + "_" + Path.GetFileName(doc.Path));
        string target = Path.Combine(destination, folder); int suffix = 2;
        while (directories.Exists(target)) target = Path.Combine(destination, folder + "_" + suffix++);
        directories.CreateDirectory(target);
        List<string> errors = []; int complete = 0;
        foreach (var asset in assets)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string name = $"{asset.Kind}/{asset.Index:D5}_{SafeName(Path.GetFileName(asset.Name.Replace('\\', '/')))}";
                if (jsonOnly) await WriteJson(target, name + ".json", AssetJson(doc, asset, token), token, directories).ConfigureAwait(false);
                else if (asset.Kind == AssetKind.Texture)
                    await WriteAtomicAsync(target, name + ".png", PngEncoder.Encode(TextureDecoder.Decode(doc, asset, token), token), token, directories).ConfigureAwait(false);
                else if (asset.Content is ScriptContent script)
                    await WriteAtomicAsync(target, name, Encoding.UTF8.GetBytes(script.GetText(token)), token, directories).ConfigureAwait(false);
                else if (asset.Kind is AssetKind.Model or AssetKind.World)
                    await ExportObj(doc, asset, target, name, preferredTexturePack, lodLevel, token, directories).ConfigureAwait(false);
                else if (asset.Kind is AssetKind.Animation or AssetKind.Node or AssetKind.Material or AssetKind.TextureReference)
                    await WriteJson(target, name + ".json", AssetJson(doc, asset, token), token, directories).ConfigureAwait(false);
                else
                {
                    await WriteAtomicAsync(target, name, doc.Slice(asset.Offset, asset.Length).ToArray(), token, directories).ConfigureAwait(false);
                    if (asset.Kind == AssetKind.Zrd) await WriteJson(target, name + ".json", ZrdDecoder.Decode(doc.Slice(asset.Offset, asset.Length), token), token, directories).ConfigureAwait(false);
                }
                complete++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { errors.Add($"{asset.Name}: {ex.Message}"); }
            progress?.Report(new(complete + errors.Count, assets.Count, asset.Name));
        }
        await WriteJson(target, "export-report.json", new JsonObject { ["source"] = doc.Path, ["completed"] = complete, ["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()), ["purpose"] = "Standard assets; not a repackable project" }, token, directories).ConfigureAwait(false);
        return new(target, complete, errors);
    }
    public static JsonObject AssetJson(ZbdDocument doc, AssetRecord a, CancellationToken token = default, bool boundedZrd = false)
    {
        token.ThrowIfCancellationRequested();
        var metadata = boundedZrd ? JsonData.Preview(a.Metadata, token: token) : (Value: JsonData.Clone(a.Metadata, token), Truncated: false);
        JsonObject result = new() { ["name"] = a.Name, ["kind"] = a.Kind.ToString(), ["index"] = a.Index, ["source_offset"] = a.Offset, ["source_length"] = a.Length, ["properties"] = metadata.Value };
        if (boundedZrd) result["properties_truncated"] = metadata.Truncated;
        if (a.Content is MotionClip motion)
        {
            // Archive metadata is always a small inspection preview; explicit exports retain the full part list.
            if (!boundedZrd) result["properties"]!["motion"] = motion.ToJson(bounded: false, token: token);
        }
        else if (a.Content is GameModel model)
        {
            result["vertices"] = JsonData.Vectors(boundedZrd ? model.Vertices.Take(32) : model.Vertices, token);
            result["normals"] = JsonData.Vectors(boundedZrd ? model.Normals.Take(32) : model.Normals, token);
            result["morphs"] = JsonData.Vectors(boundedZrd ? model.Morphs.Take(32) : model.Morphs, token);
            if (boundedZrd)
            {
                result["vertex_count"] = model.Vertices.Length; result["normal_count"] = model.Normals.Length; result["morph_count"] = model.Morphs.Length; result["polygon_count"] = model.Polygons.Length;
                result["geometry_truncated"] = model.Vertices.Length > 32 || model.Normals.Length > 32 || model.Morphs.Length > 32 || model.Polygons.Length > 32;
            }
            result["polygons"] = JsonData.Array(boundedZrd ? model.Polygons.Take(32) : model.Polygons, p =>
            {
                JsonObject j = (JsonObject)MetadataRow(p.Metadata)!;
                if (p.Colors.Length != 0) j["vertex_colors_rgb"] = JsonData.Vectors(p.Colors, token);
                j["vertex_indices"] = JsonData.Integers(p.Vertices, token);
                j["normal_indices"] = JsonData.Integers(p.Normals, token);
                j["uvs"] = JsonData.Array(p.Uvs, v => new JsonObject { ["u"] = JsonData.Number(v.X), ["v"] = JsonData.Number(v.Y) }, token);
                return j;
            }, token);
        }
        else if (a.Content is ScriptContent script)
        {
            result["instructions"] = JsonData.Array(boundedZrd ? script.Instructions.Take(64) : script.Instructions, instruction =>
                JsonData.Array(boundedZrd ? instruction.Take(16) : instruction, word => JsonValue.Create(boundedZrd && word.Length > 128 ? word[..128] : word), token), token);
            if (boundedZrd) result["instructions_truncated"] = script.Instructions.Count > 64 || script.Instructions.Any(i => i.Length > 16 || i.Any(t => t.Length > 128));
        }
        else if (a.Kind == AssetKind.Animation && doc.Animations is { } animations) result["properties"] = boundedZrd ? animations.Entries[a.Index].ToPreviewJson(token) : animations.Entries[a.Index].ToJson(token);
        else if (a.Kind == AssetKind.Zrd)
        {
            var tree = a.Content as ZrdNode ?? ZrdDecoder.Read(doc.Slice(a.Offset, a.Length), token);
            result["tree"] = boundedZrd ? tree.ToPreviewJson(token) : tree.ToJson(token);
        }
        else if (a.Kind == AssetKind.Sound)
        {
            var info = WaveDecoder.Read(doc.Slice(a.Offset, a.Length), token);
            var wave = JsonSerializer.SerializeToNode(info with { Cues = [] })!;
            wave["Cues"] = JsonData.Array(boundedZrd ? info.Cues.Take(32) : info.Cues, cue => JsonSerializer.SerializeToNode(cue), token);
            if (boundedZrd) { wave["cue_count"] = info.Cues.Count; wave["cues_truncated"] = info.Cues.Count > 32; }
            result["wave"] = wave;
        }
        else if (a.Kind == AssetKind.World && doc.Scene is GameScene scene)
        {
            if (boundedZrd)
            {
                result["node_count"] = scene.Nodes.Count; result["material_count"] = scene.Materials.Count;
                result["nodes_truncated"] = scene.Nodes.Count > 32; result["materials_truncated"] = scene.Materials.Count > 32;
            }
            result["nodes"] = JsonData.Array(boundedZrd ? scene.Nodes.Take(32) : scene.Nodes, n => MetadataRow(n.Metadata), token);
            result["materials"] = JsonData.Array(boundedZrd ? scene.Materials.Take(32) : scene.Materials, MetadataRow, token);
        }
        token.ThrowIfCancellationRequested();
        return result;
        JsonNode? MetadataRow(JsonObject value)
        {
            if (!boundedZrd) return JsonData.Clone(value, token);
            // A world may return 32 nodes and 32 materials, twice when original
            // and edited snapshots are paired. Budget the aggregate, including escapes.
            var preview = JsonData.Preview(value, nodes: 64, characters: 1024, token: token);
            if (preview.Truncated) result["properties_truncated"] = true;
            return preview.Value;
        }
    }
    private async Task ExportObj(ZbdDocument doc, AssetRecord asset, string target, string name, string? preferred, int lod, CancellationToken token, DirectoryLease directories, SceneView? placements = null)
    {
        GameScene scene = doc.Scene ?? throw new InvalidDataException("Missing GameZ scene.");
        var view = placements ?? SceneBuilder.ForAsset(scene, asset, lod, token);
        CheckObjExpansion(scene, view, token);
        StringBuilder obj = new("# zStudio static geometry export\n"); string mtlName = Path.GetFileName(name) + ".mtl"; obj.AppendLine("mtllib " + mtlName);
        HashSet<int> usedMaterials = []; int vertexBase = 1; Dictionary<int, IReadOnlyList<MeshPart>> meshes = [];
        foreach (var placement in view.Placements)
        {
            token.ThrowIfCancellationRequested(); obj.AppendLine($"o node_{placement.NodeIndex}_{SafeName(placement.Name)}");
            if (!meshes.TryGetValue(placement.ModelIndex, out var parts)) meshes[placement.ModelIndex] = parts = GeometryBuilder.Build(scene.Models[placement.ModelIndex], token: token);
            Matrix4x4.Invert(placement.Transform, out Matrix4x4 inverse); Matrix4x4 normalMatrix = Matrix4x4.Transpose(inverse);
            foreach (var part in parts)
            {
                usedMaterials.Add(part.MaterialIndex); obj.AppendLine($"usemtl material_{part.MaterialIndex}");
                for (int i = 0; i < part.Positions.Length; i++)
                {
                    Vector3 p = Vector3.Transform(part.Positions[i], placement.Transform);
                    string color = part.Colors.Length == part.Positions.Length ? FormattableString.Invariant($" {part.Colors[i].X:R} {part.Colors[i].Y:R} {part.Colors[i].Z:R}") : "";
                    obj.AppendLine(FormattableString.Invariant($"v {p.X:R} {p.Y:R} {p.Z:R}") + color);
                }
                foreach (Vector2 uv in part.TextureCoordinates) obj.AppendLine(FormattableString.Invariant($"vt {uv.X:R} {1 - uv.Y:R}"));
                foreach (Vector3 v in part.Normals) { Vector3 n = Vector3.TransformNormal(v, normalMatrix); if (n.LengthSquared() > 1e-12) n = Vector3.Normalize(n); obj.AppendLine(FormattableString.Invariant($"vn {n.X:R} {n.Y:R} {n.Z:R}")); }
                bool reverse = placement.Transform.GetDeterminant() < 0;
                for (int i = 0; i < part.Indices.Length; i += 3)
                {
                    int a = part.Indices[i] + vertexBase, b = part.Indices[i + (reverse ? 2 : 1)] + vertexBase, c = part.Indices[i + (reverse ? 1 : 2)] + vertexBase;
                    obj.AppendLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                }
                vertexBase += part.Positions.Length;
            }
        }
        StringBuilder mtl = new(); List<string> notes = view.Diagnostics.Select(d => d.Message).ToList();
        foreach (int index in usedMaterials.Order())
        {
            mtl.AppendLine($"newmtl material_{index}");
            JsonObject? material = index >= 0 && index < scene.Materials.Count ? scene.Materials[index] : null;
            var color = material?["color"]; float r = color.Float("r", 255) / 255, g = color.Float("g", 255) / 255, b = color.Float("b", 255) / 255;
            mtl.AppendLine(FormattableString.Invariant($"Kd {r:R} {g:R} {b:R}")); mtl.AppendLine("illum 1");
            int texture = material.Int("texture_index", -1);
            if (texture >= 0 && texture < scene.Textures.Count)
            {
                string textureName = scene.Textures[texture].Text("name"); var resolved = await resolver.ResolveTextureAsync(doc.Path, textureName, preferred, token).ConfigureAwait(false);
                if (resolved != null)
                {
                    string file = $"texture_{texture:D4}_{SafeName(resolved.Asset.Name)}.png";
                    string relative = Path.Combine(Path.GetDirectoryName(name) ?? "", file).Replace('\\', '/');
                    if (!directories.Exists(Path.Combine(target, relative))) await WriteAtomicAsync(target, relative, PngEncoder.Encode(TextureDecoder.Decode(resolved.Document, resolved.Asset, token), token), token, directories).ConfigureAwait(false);
                    mtl.AppendLine("map_Kd " + file);
                    if (resolved.Ambiguous) notes.Add($"Ambiguous texture {textureName}; used {resolved.Asset.Id}.");
                }
                else notes.Add($"Unresolved texture {textureName}.");
            }
            mtl.AppendLine();
        }
        await WriteAtomicAsync(target, name + ".mtl", Encoding.UTF8.GetBytes(mtl.ToString()), token, directories).ConfigureAwait(false);
        await WriteAtomicAsync(target, name + ".obj", Encoding.UTF8.GetBytes(obj.ToString()), token, directories).ConfigureAwait(false);
        await WriteJson(target, name + ".json", new JsonObject { ["properties"] = AssetJson(doc, asset, token), ["notes"] = new JsonArray(notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) }, token, directories).ConfigureAwait(false);
    }
    /// <summary>Charge every emitted placement before geometry expansion or text construction, including shared models.</summary>
    internal static void CheckObjExpansion(GameScene scene, SceneView view, CancellationToken token, long maximumBytes = 64L * 1024 * 1024)
    {
        long remaining = maximumBytes;
        Dictionary<int, long> modelCosts = [];
        foreach (var placement in view.Placements)
        {
            token.ThrowIfCancellationRequested();
            if (!modelCosts.TryGetValue(placement.ModelIndex, out long cost))
            {
                cost = 0;
                foreach (var polygon in scene.Models[placement.ModelIndex].Polygons)
                {
                    token.ThrowIfCancellationRequested();
                    if (polygon.Vertices.Length < 3) continue;
                    // GeometryBuilder emits at most one position/normal/UV/color row per stored corner and n-2
                    // triangles. These allowances include round-trip floats, ten-digit indices, UTF-8 names and MTL.
                    cost += 256L * polygon.Vertices.Length + 128L * (polygon.Vertices.Length - 2) + 1024;
                    if (cost > maximumBytes) Refuse();
                }
                modelCosts.Add(placement.ModelIndex, cost);
            }
            cost += 1024; // object name and group/material headers
            if (cost > remaining) Refuse();
            remaining -= cost;
        }
        static void Refuse() => throw new InvalidDataException("The expanded OBJ geometry exceeds the supported 64 MiB text budget. Export fewer objects together or choose a lower-detail LOD.");
    }
    public static string SafeName(string name)
    {
        string invalid = "<>:\"/\\|?*"; string safe = new(name.Select(c => c < 32 || invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        safe = safe.TrimEnd('.', ' '); if (safe.Length > 140) safe = safe[..140]; if (safe.Length == 0 || safe is "." or "..") safe = "asset";
        string stem = safe.Split('.')[0]; if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) safe = "_" + safe;
        return safe;
    }
    private string ValidateExportDirectory(string destination, DirectoryLease directories)
    {
        destination = Path.GetFullPath(destination);
        string captured = directories.CapturedPath(destination);
        if (IsWithin(directories.CapturedPath(resolver.Root), captured) || PickupPlacementEditSession.IsProtectedPath(captured))
            throw new IOException("Choose an export folder outside the opened source tree and protected datasets.");
        // Inspect ancestors above the selected folder too: a junction there can
        // otherwise redirect an apparently external export into the source tree.
        for (var directory = new DirectoryInfo(destination); directory != null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Choose an export destination without directory links.");
        return destination;
    }
    public static bool IsWithin(string root, string path) => Path.GetFullPath(path).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static string DestinationPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new IOException("Absolute export paths are not allowed.");
        string path = Path.GetFullPath(Path.Combine(root, relative)); if (!IsWithin(root, path) || path.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new IOException("Export path escapes destination.");
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(path)!); dir != null && IsWithin(root, dir.FullName); dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Export through directory links is not supported.");
        return path;
    }
    public static async Task WriteAtomicAsync(string root, string relative, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        using DirectoryLease directories = new();
        await WriteAtomicAsync(root, relative, bytes, token, directories).ConfigureAwait(false);
    }
    private static async Task WriteAtomicAsync(string root, string relative, ReadOnlyMemory<byte> bytes, CancellationToken token, DirectoryLease directories)
    {
        string path = DestinationPath(root, relative);
        if (PickupPlacementEditSession.IsProtectedPath(directories.CapturedPath(path))) throw new IOException("Export outside protected reference datasets.");
        directories.Parent(path, create: true);
        if (directories.Exists(path)) throw new IOException($"Export already exists: {relative}");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream output = directories.OpenFile(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Held from its check against the exported bytes until it is in place.
            using SealedFile staged = SealedFile.Open(temporary, Sources.JournalDigest.OfContent(bytes.Span), directories);
            staged.MoveTo(path);
        }
        finally { directories.DeleteFile(temporary); }
    }
    private static Task WriteJson(string root, string name, JsonNode node, CancellationToken token, DirectoryLease directories) => WriteAtomicAsync(root, name, Encoding.UTF8.GetBytes(node.ToJsonString(JsonData.Options) + "\n"), token, directories);
}
