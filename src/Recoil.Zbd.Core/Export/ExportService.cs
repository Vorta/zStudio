using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Export;

public sealed record ExportProgress(int Completed, int Total, string Name);
public sealed record ExportResult(string Directory, int Completed, IReadOnlyList<string> Errors);
public interface IAssetExporter
{
    Task<ExportResult> ExportAsync(ZbdDocument document, IReadOnlyList<AssetRecord> assets, string destination, bool jsonOnly,
        string? preferredTexturePack = null, int lodLevel = 0, IProgress<ExportProgress>? progress = null, CancellationToken token = default);
}

public sealed class ExportService(AssetResolver resolver) : IAssetExporter
{
    public async Task<ExportResult> ExportAsync(ZbdDocument doc, IReadOnlyList<AssetRecord> assets, string destination, bool jsonOnly,
        string? preferredTexturePack = null, int lodLevel = 0, IProgress<ExportProgress>? progress = null, CancellationToken token = default)
    {
        destination = Path.GetFullPath(destination);
        if (IsWithin(resolver.Root, destination)) throw new IOException("Choose an export folder outside the opened source tree.");
        for (var directory = new DirectoryInfo(destination); directory != null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Choose an export destination without directory links.");
        Directory.CreateDirectory(destination);
        string folder = SafeName(Path.GetFileName(Path.GetDirectoryName(doc.Path)) + "_" + Path.GetFileName(doc.Path));
        string target = Path.Combine(destination, folder); int suffix = 2;
        while (Directory.Exists(target) || File.Exists(target)) target = Path.Combine(destination, folder + "_" + suffix++);
        Directory.CreateDirectory(target);
        List<string> errors = []; int complete = 0;
        foreach (var asset in assets)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string name = $"{asset.Kind}/{asset.Index:D5}_{SafeName(Path.GetFileName(asset.Name.Replace('\\', '/')))}";
                if (jsonOnly) await WriteJson(target, name + ".json", AssetJson(doc, asset, token), token).ConfigureAwait(false);
                else if (asset.Kind == AssetKind.Texture)
                    await WriteAtomicAsync(target, name + ".png", PngEncoder.Encode(TextureDecoder.Decode(doc, asset, token), token), token).ConfigureAwait(false);
                else if (asset.Content is ScriptContent script)
                    await WriteAtomicAsync(target, name, Encoding.UTF8.GetBytes(script.Text), token).ConfigureAwait(false);
                else if (asset.Kind is AssetKind.Model or AssetKind.World)
                    await ExportObj(doc, asset, target, name, preferredTexturePack, lodLevel, token).ConfigureAwait(false);
                else if (asset.Kind is AssetKind.Animation or AssetKind.Node or AssetKind.Material or AssetKind.TextureReference)
                    await WriteJson(target, name + ".json", AssetJson(doc, asset, token), token).ConfigureAwait(false);
                else
                {
                    await WriteAtomicAsync(target, name, doc.Slice(asset.Offset, asset.Length).ToArray(), token).ConfigureAwait(false);
                    if (asset.Kind == AssetKind.Zrd) await WriteJson(target, name + ".json", ZrdDecoder.Decode(doc.Slice(asset.Offset, asset.Length), token), token).ConfigureAwait(false);
                }
                complete++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { errors.Add($"{asset.Name}: {ex.Message}"); }
            progress?.Report(new(complete + errors.Count, assets.Count, asset.Name));
        }
        await WriteJson(target, "export-report.json", new JsonObject { ["source"] = doc.Path, ["completed"] = complete, ["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()), ["purpose"] = "Standard assets; not a repackable project" }, token).ConfigureAwait(false);
        return new(target, complete, errors);
    }
    public static JsonObject AssetJson(ZbdDocument doc, AssetRecord a, CancellationToken token = default)
    {
        JsonObject result = new() { ["name"] = a.Name, ["kind"] = a.Kind.ToString(), ["index"] = a.Index, ["source_offset"] = a.Offset, ["source_length"] = a.Length, ["properties"] = a.Metadata.DeepClone() };
        if (a.Content is GameModel model)
        {
            result["vertices"] = JsonData.Vectors(model.Vertices); result["normals"] = JsonData.Vectors(model.Normals); result["morphs"] = JsonData.Vectors(model.Morphs);
            JsonArray polygons = []; foreach (var p in model.Polygons) { JsonObject j = (JsonObject)p.Metadata.DeepClone(); j["vertex_indices"] = JsonData.Integers(p.Vertices); j["normal_indices"] = JsonData.Integers(p.Normals); j["uvs"] = new JsonArray(p.Uvs.Select(v => (JsonNode?)new JsonObject { ["u"] = JsonData.Number(v.X), ["v"] = JsonData.Number(v.Y) }).ToArray()); polygons.Add(j); }
            result["polygons"] = polygons;
        }
        else if (a.Content is ScriptContent script) result["instructions"] = JsonSerializer.SerializeToNode(script.Instructions);
        else if (a.Kind == AssetKind.Animation && doc.Animations is { } animations) result["properties"] = animations.Entries[a.Index].ToJson();
        else if (a.Kind == AssetKind.Zrd) result["tree"] = ZrdDecoder.Decode(doc.Slice(a.Offset, a.Length), token);
        else if (a.Kind == AssetKind.Sound) result["wave"] = JsonSerializer.SerializeToNode(WaveDecoder.Read(doc.Slice(a.Offset, a.Length)));
        else if (a.Kind == AssetKind.World && doc.Scene is GameScene scene)
        { result["nodes"] = new JsonArray(scene.Nodes.Select(n => n.Metadata.DeepClone()).ToArray()); result["materials"] = new JsonArray(scene.Materials.Select(m => m.DeepClone()).ToArray()); }
        return result;
    }
    private async Task ExportObj(ZbdDocument doc, AssetRecord asset, string target, string name, string? preferred, int lod, CancellationToken token)
    {
        GameScene scene = doc.Scene ?? throw new InvalidDataException("Missing GameZ scene.");
        var view = SceneBuilder.ForAsset(scene, asset, lod, token);
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
                foreach (Vector3 v in part.Positions) { Vector3 p = Vector3.Transform(v, placement.Transform); obj.AppendLine(FormattableString.Invariant($"v {p.X:R} {p.Y:R} {p.Z:R}")); }
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
            var color = material?["color"]; float r = color.Float("r", 1), g = color.Float("g", 1), b = color.Float("b", 1);
            mtl.AppendLine(FormattableString.Invariant($"Kd {r:R} {g:R} {b:R}")); mtl.AppendLine("illum 1");
            int texture = material.Int("texture_index", -1);
            if (texture >= 0 && texture < scene.Textures.Count)
            {
                string textureName = scene.Textures[texture].Text("name"); var resolved = await resolver.ResolveTextureAsync(doc.Path, textureName, preferred, token).ConfigureAwait(false);
                if (resolved != null)
                {
                    string file = $"texture_{texture:D4}_{SafeName(resolved.Asset.Name)}.png";
                    string relative = (Path.GetDirectoryName(name) + "/" + file).Replace('\\', '/');
                    if (!File.Exists(Path.Combine(target, relative))) await WriteAtomicAsync(target, relative, PngEncoder.Encode(TextureDecoder.Decode(resolved.Document, resolved.Asset, token), token), token).ConfigureAwait(false);
                    mtl.AppendLine("map_Kd " + file);
                    if (resolved.Ambiguous) notes.Add($"Ambiguous texture {textureName}; used {resolved.Asset.Id}.");
                }
                else notes.Add($"Unresolved texture {textureName}.");
            }
            mtl.AppendLine();
        }
        await WriteAtomicAsync(target, name + ".mtl", Encoding.UTF8.GetBytes(mtl.ToString()), token).ConfigureAwait(false);
        await WriteAtomicAsync(target, name + ".obj", Encoding.UTF8.GetBytes(obj.ToString()), token).ConfigureAwait(false);
        await WriteJson(target, name + ".json", new JsonObject { ["properties"] = AssetJson(doc, asset, token), ["notes"] = new JsonArray(notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) }, token).ConfigureAwait(false);
    }
    public static string SafeName(string name)
    {
        string invalid = "<>:\"/\\|?*"; string safe = new(name.Select(c => c < 32 || invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        safe = safe.TrimEnd('.', ' '); if (safe.Length > 140) safe = safe[..140]; if (safe.Length == 0 || safe is "." or "..") safe = "asset";
        string stem = safe.Split('.')[0]; if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) safe = "_" + safe;
        return safe;
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
        string path = DestinationPath(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) throw new IOException($"Export already exists: {relative}");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes.ToArray(), token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); File.Move(temporary, path, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static Task WriteJson(string root, string name, JsonNode node, CancellationToken token) => WriteAtomicAsync(root, name, Encoding.UTF8.GetBytes(node.ToJsonString(JsonData.Options) + "\n"), token);
}
