using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

public static partial class SourceBlender
{
    public const int GraphProtocol = 1;
    internal const string BridgeKey = "zstudio_blender";
    internal const string GraphManifest = "recoil-export.json";
    internal sealed record GraphModel(string Path, JsonObject Json, GltfDocument Document);

    private static Dictionary<GltfMesh, int> GraphMeshIndices(WorldGltf.ZoneLayout layout, JsonObject json)
    {
        Dictionary<GltfMesh, int> result = new(ReferenceEqualityComparer.Instance);
        foreach (var node in layout.Nodes)
            if (node.Mesh is { } mesh) result.TryAdd(mesh, json["nodes"]![node.Index]!["mesh"]!.GetValue<int>());
        return result;
    }

    /// <summary>A single immutable dependency snapshot, shared by checkout and the Blender helper.</summary>
    internal sealed class Graph(Func<string, long, byte[]> read, CancellationToken token)
    {
        internal readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, GraphModel> Models = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> MissingImages = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
        private long bytes;
        internal byte[] Read(string path, long maximum = MaximumExportBytes)
        {
            token.ThrowIfCancellationRequested();
            if (Files.TryGetValue(path, out var value))
            {
                if (value.LongLength > maximum) throw new InvalidDataException("A Blender dependency exceeds its format allowance.");
                return value;
            }
            if (MissingImages.Contains(path)) throw new FileNotFoundException("A source image is absent.");
            if (Files.Count + MissingImages.Count >= 4096) throw new InvalidDataException("The Blender dependency graph exceeds 4,096 files.");
            value = read(path, Math.Min(maximum, MaximumExportBytes - bytes));
            if (value.LongLength > maximum || value.LongLength > MaximumExportBytes - bytes) throw new InvalidDataException("The Blender dependency graph exceeds 512 MiB.");
            bytes += value.LongLength; Files.Add(path, value); return value;
        }
        internal GraphModel Load(string path, int depth = 0)
        {
            if (depth > GltfDocument.MaximumDepth || active.Contains(path)) throw new InvalidDataException("The Blender reference graph is cyclic or too deep.");
            if (Models.TryGetValue(path, out var cached)) return cached;
            if (!path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("RECOIL Blender sources must be separate .gltf files.");
            var content = Read(path, GltfDocument.MaximumJsonBytes);
            var root = Parse(content, path, token);
            var document = GltfDocument.Read(content, (uri, remaining) => Read(WorldAssembler.Relative(path, uri), remaining), token);
            WorldGltf.ValidateSupported(document, path);
            WorldGltf.CheckInstances(document, path);
            _ = WorldGltf.CaptureZoneProfile(document, token: token);
            active.Add(path);
            var model = new GraphModel(path, root, document); Models.Add(path, model);
            foreach (var image in root["images"] as JsonArray ?? [])
            {
                if (image?["uri"] is not JsonValue uri || !uri.TryGetValue<string>(out var name) || name.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("RECOIL Blender sources require external PNG images.");
                var relative = WorldAssembler.Relative(path, Uri.UnescapeDataString(name));
                if (!relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("RECOIL textures must be PNG files.");
                try { Read(relative); }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { MissingImages.Add(relative); }
            }
            foreach (var node in document.AllNodes())
            {
                if (node.Extras?[WorldGltf.Key]?["ref"] is not { } value) continue;
                string uri = value.GetValue<string>();
                var target = Load(WorldAssembler.Relative(path, uri), depth + 1);
                if (node.Extras?[WorldGltf.Key]?["zoneOverrides"] is JsonObject scope)
                    ValidateScope(target, scope, 0);
            }
            active.Remove(path); return model;
        }
        private void ValidateScope(GraphModel model, JsonObject scope, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > GltfDocument.MaximumDepth) throw new InvalidDataException("A placement override is too deep.");
            WorldGltf.ValidateZoneOverrideScope(model.Document, scope, token);
            if (scope["fingerprint"] != null) WorldGltf.ValidateZoneProfile(model.Document, WorldGltf.ReadZoneOverride(scope, token), token);
            if (scope["references"] is not JsonObject nested) return;
            foreach (var (key, child) in nested)
            {
                int index = int.Parse(key, CultureInfo.InvariantCulture);
                string uri = model.Json["nodes"]![index]!["extras"]![WorldGltf.Key]!["ref"]!.GetValue<string>();
                ValidateScope(Models[WorldAssembler.Relative(model.Path, uri)], child!.AsObject(), depth + 1);
            }
        }
        internal void Verify(Func<string, byte[], bool> matches)
        {
            foreach (var (path, content) in Files)
            { token.ThrowIfCancellationRequested(); if (!matches(path, content)) throw new SourceFileChangedException("A Blender input changed while preparing the graph; retry the import.", [path]); }
        }
    }

    internal static string GraphPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 32767 || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0'))
            throw new InvalidDataException("A Blender graph path must be relative to its selected root.");
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(directory, relative));
        if (path.Equals(directory, StringComparison.OrdinalIgnoreCase) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A Blender dependency escapes the selected root.");
        SourceProject.RejectLinks(path); return path;
    }

    private static string AbsoluteGraphPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Blender helper folders and files must use absolute paths.");
        path = Path.GetFullPath(path); SourceProject.RejectLinks(path); return path;
    }

    /// <summary>Prepare immutable input copies and editor-only provenance, never altering source files.</summary>
    public static JsonObject PrepareBlenderImport(string root, string model, string destination, CancellationToken token = default)
    {
        root = AbsoluteGraphPath(root); destination = AbsoluteGraphPath(destination); model = SourceWorkspace.Normalize(model);
        Graph graph = new((path, maximum) => SourceRead.All(GraphPath(root, path), maximum, token), token);
        graph.Load(model);
        graph.Verify((path, bytes) => SourceRead.Matches(GraphPath(root, path), bytes.Length, SourceProject.Sha256(bytes), token));
        foreach (string path in graph.MissingImages)
            if (SourceRead.FileExists(GraphPath(root, path))) throw new IOException("A missing source image appeared during import; retry.");
        using CopyFiles written = new(); written.Root(destination);
        JsonArray models = [];
        foreach (var (path, content) in graph.Files)
            written.Write(GraphPath(destination, "baseline/" + path), content);
        foreach (var entry in graph.Models.Values)
        {
            token.ThrowIfCancellationRequested();
            var json = entry.Json.DeepClone().AsObject();
            var nodes = json["nodes"] as JsonArray ?? [];
            for (int i = 0; i < nodes.Count; i++) Stamp(nodes[i]!.AsObject(), i);
            var meshes = json["meshes"] as JsonArray ?? [];
            for (int i = 0; i < meshes.Count; i++) Stamp(meshes[i]!.AsObject(), i);
            var materials = json["materials"] as JsonArray ?? [];
            for (int i = 0; i < materials.Count; i++) Stamp(materials[i]!.AsObject(), i);
            foreach (var image in json["images"] as JsonArray ?? [])
                if (graph.MissingImages.Contains(WorldAssembler.Relative(entry.Path, Uri.UnescapeDataString(image!["uri"]!.GetValue<string>()))))
                { image["extras"] ??= new JsonObject(); image["extras"]![BridgeKey] = "missing-image"; }
            written.Write(GraphPath(destination, "input/" + entry.Path), GltfJson.Write(json, true, token));
            var description = DescribeModel(entry, token);
            string descriptor = "metadata/" + entry.Path + ".json";
            written.Write(GraphPath(destination, descriptor), GltfJson.Write(description, false, token));
            models.Add(new JsonObject { ["path"] = entry.Path, ["metadata"] = descriptor });
        }
        foreach (var (path, content) in graph.Files.Where(p => !graph.Models.ContainsKey(p.Key))) written.Write(GraphPath(destination, "input/" + path), content);
        // A named runtime texture-cycle base need not have a source PNG (retail
        // m9 surf00). This conspicuous editor-only image is never a source file.
        foreach (string path in graph.MissingImages)
            written.Write(GraphPath(destination, "input/" + path), Export.PngEncoder.Encode(new DecodedImage(2, 2,
                [255, 0, 255, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 0, 255, 255]), token));
        JsonObject manifest = new()
        {
            ["protocol"] = GraphProtocol, ["root"] = model, ["sourceRoot"] = root, ["models"] = models,
            ["files"] = new JsonArray(graph.Files.Select(p => (JsonNode)new JsonObject { ["path"] = p.Key, ["sha256"] = SourceProject.Sha256(p.Value) }).ToArray()),
        };
        written.Write(GraphPath(destination, "session.json"), GltfJson.Write(manifest, true, token));
        written.Keep(); return new() { ["protocol"] = GraphProtocol, ["session"] = Path.Combine(destination, "session.json"), ["missingImages"] = graph.MissingImages.Count };
    }

    private static void Stamp(JsonObject entry, int index)
    {
        if (entry["extras"] == null) entry["extras"] = new JsonObject();
        var extras = entry["extras"]!.AsObject();
        if (extras.ContainsKey(BridgeKey) || extras.ContainsKey("zstudio_editor_id")) throw new InvalidDataException("A source file contains reserved Blender transport metadata.");
        extras[BridgeKey] = index;
    }

    private static JsonObject DescribeModel(GraphModel model, CancellationToken token)
    {
        var layout = WorldGltf.ZoneLayout.Read(model.Document, token);
        var profile = WorldGltf.CaptureZoneProfile(model.Document, token: token);
        var values = WorldGltf.WriteZoneOverride(profile, token);
        var indices = GraphMeshIndices(layout, model.Json);
        JsonArray meshes = [];
        for (int i = 0; i < layout.Meshes.Count; i++)
        {
            var mesh = layout.Meshes[i]; JsonArray primitives = [];
            foreach (var primitive in mesh.Primitives)
                primitives.Add(new JsonObject { ["triangles"] = new JsonArray(layout.Triangles[primitive].Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()) });
            int index = indices[mesh];
            meshes.Add(new JsonObject { ["index"] = index, ["primitives"] = primitives, ["zones"] = values["meshes"]![i]!.DeepClone() });
        }
        return new()
        {
            ["path"] = model.Path, ["source"] = model.Json.DeepClone(), ["layout"] = values,
            ["nodeOrder"] = new JsonArray(layout.Nodes.Select(n => (JsonNode)JsonValue.Create(n.Index)!).ToArray()), ["meshes"] = meshes,
        };
    }

    internal static BlenderCheckout CheckoutGraph(SourceWorkspace workspace, string model, CancellationToken token)
    {
        model = SourceWorkspace.Normalize(model); workspace.CheckEditable(model);
        var snapshot = new SourceBuilder.Snapshot(workspace.Root, workspace.Overlay(), inventoryToken: token);
        var source = snapshot.Files();
        Graph graph = new((path, maximum) => source.Read(path, token, ProjectReadLimits.Bytes(maximum)), token);
        graph.Load(model); snapshot.CheckUnchanged(token);
        string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..12];
        string folder = Path.Combine(Folder(workspace.Root), id);
        using CopyFiles written = new(); written.Root(folder);
        List<BlenderCheckoutFile> files = [];
        foreach (var (path, content) in graph.Files)
        {
            written.Write(GraphPath(folder, "input/" + path), content);
            files.Add(new(path, "input/" + path, SourceProject.Sha256(content)));
        }
        JsonObject manifest = new()
        {
            ["format"] = "zstudio-blender-checkout", ["version"] = 2, ["id"] = id, ["model"] = model, ["created"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["project"] = f.Project, ["checkout"] = f.Checkout, ["sha256"] = f.Sha256 }).ToArray()),
            ["instructions"] = "Use Blender's Import RECOIL glTF on input/" + model + ". Export RECOIL glTF to outbox, then Update from Blender export in zStudio.",
        };
        written.Write(Path.Combine(folder, ManifestName), GltfJson.Write(manifest, true, token, MaximumCheckoutManifestBytes));
        written.Folder(Path.Combine(folder, "outbox")); written.Keep();
        return new(id, folder, model, DateTime.UtcNow, files) { Version = 2 };
    }
}
