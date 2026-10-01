using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A model checked out for Blender: its folder under <c>zstudio/export</c> and the project files it was copied from.</summary>
public sealed record BlenderCheckout(string Id, string Folder, string Model, DateTime CreatedUtc, IReadOnlyList<BlenderCheckoutFile> Files)
{
    /// <summary>The copy of the model Blender imports.</summary>
    public string Input => Path.Combine(Folder, "input", Path.GetFileName(Model));
    /// <summary>Where Blender exports go; each export is one glTF file (with its buffers and textures) anywhere below it.</summary>
    public string Outbox => Path.Combine(Folder, "outbox");
}
/// <summary>A project file a checkout copied: its project path, its path in the checkout and its SHA-256 then.</summary>
public sealed record BlenderCheckoutFile(string Project, string Checkout, string Sha256);
/// <summary>An export Blender wrote into a checkout's outbox.</summary>
public sealed record BlenderExport(string Gltf, string Relative, DateTime WrittenUtc, long Bytes);
/// <summary>The changes an export makes to the project, with notes (shared textures, renamed nodes) and the sealed copy it was read from.</summary>
public sealed record BlenderUpdatePlan(string Label, IReadOnlyList<(string Relative, byte[] Content)> Changes, IReadOnlyList<string> Notes, string Sealed);

/// <summary>
/// The file-based Blender round trip. <see cref="Checkout"/> copies a project model, with its buffers and textures, into
/// <c>zstudio/export/&lt;id&gt;/input</c> as a self-contained glTF Blender opens; the artist exports a glTF (Separate:
/// .gltf, .bin and PNG textures) into the checkout's <c>outbox</c>; <see cref="PlanUpdate"/> seals that export (copies it
/// into <c>sealed/&lt;generation&gt;</c> while checking it does not change), reads and checks it, and plans the project
/// changes: the model's glTF and buffer, and each texture that is new or changed. Nothing reaches the project until the
/// plan is applied to the workspace, and nothing is applied without the user asking (Update from export).
/// </summary>
public static class SourceBlender
{
    public const string ExportFolder = "zstudio/export";
    private const string ManifestName = "manifest.json";
    private const long MaximumExportBytes = 512L * 1024 * 1024;

    private static string Folder(string root) => Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), "zstudio", "export");

    /// <summary>Copies <paramref name="model"/> (a project .gltf, as the workspace holds it) and the files it uses into a new checkout.</summary>
    public static BlenderCheckout Checkout(SourceWorkspace workspace, string model, CancellationToken token = default)
    {
        model = SourceWorkspace.Normalize(model);
        workspace.CheckEditable(model);
        if (!model.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{model} is not a .gltf model; Blender checkouts work on glTF files with separate buffers and textures.");
        byte[] json = workspace.Read(model, token) ?? throw new InvalidDataException($"{model} does not exist.");
        JsonObject root = Parse(json, model);
        string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Path.GetFileNameWithoutExtension(model) + "-" + Guid.NewGuid().ToString("N")[..6];
        string folder = Path.Combine(Folder(workspace.Root), id), input = Path.Combine(folder, "input");
        SourceProject.RejectLinks(Folder(workspace.Root));
        Directory.CreateDirectory(Path.Combine(input, "textures")); Directory.CreateDirectory(Path.Combine(folder, "outbox"));
        List<BlenderCheckoutFile> files = [new(model, "input/" + Path.GetFileName(model), SourceProject.Sha256(json))];
        // Buffers sit next to the model in the checkout; textures in its textures folder, under their engine names.
        foreach (var buffer in root["buffers"] as JsonArray ?? [])
        {
            if (buffer?["uri"]?.GetValue<string>() is not { } uri || uri.StartsWith("data:", StringComparison.Ordinal)) continue;
            string project = Worlds.WorldAssembler.Relative(model, Uri.UnescapeDataString(uri));
            byte[] bytes = workspace.Read(project, token) ?? throw new InvalidDataException($"{model} uses {project}, which does not exist.");
            string name = Path.GetFileName(project);
            File.WriteAllBytes(Path.Combine(input, name), bytes); buffer["uri"] = name;
            files.Add(new(project, "input/" + name, SourceProject.Sha256(bytes)));
        }
        HashSet<string> copied = new(StringComparer.OrdinalIgnoreCase);
        foreach (var image in root["images"] as JsonArray ?? [])
        {
            if (image?["uri"]?.GetValue<string>() is not { } uri || uri.StartsWith("data:", StringComparison.Ordinal)) continue;
            string project = Worlds.WorldAssembler.Relative(model, Uri.UnescapeDataString(uri));
            string name = Path.GetFileName(project);
            image["uri"] = "textures/" + Uri.EscapeDataString(name);
            if (!copied.Add(name)) continue;
            if (workspace.Read(project, token) is not { } bytes) continue;
            File.WriteAllBytes(Path.Combine(input, "textures", name), bytes);
            files.Add(new(project, "input/textures/" + name, SourceProject.Sha256(bytes)));
        }
        File.WriteAllText(Path.Combine(input, Path.GetFileName(model)), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        BlenderCheckout checkout = new(id, folder, model, DateTime.UtcNow, files);
        JsonObject manifest = new()
        {
            ["format"] = "zstudio-blender-checkout", ["version"] = 1, ["id"] = id, ["model"] = model, ["created"] = checkout.CreatedUtc.ToString("o", CultureInfo.InvariantCulture),
            ["files"] = new JsonArray(files.Select(f => (JsonNode?)new JsonObject { ["project"] = f.Project, ["checkout"] = f.Checkout, ["sha256"] = f.Sha256 }).ToArray()),
            ["instructions"] = "Import input/" + Path.GetFileName(model) + " in Blender (File > Import > glTF 2.0). Export with File > Export > glTF 2.0, format glTF Separate (.gltf + .bin + textures), with Custom Properties enabled, into the outbox folder. Then choose Update from Blender export in zStudio."
        };
        File.WriteAllText(Path.Combine(folder, ManifestName), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return checkout;
    }

    /// <summary>The project's checkouts, newest first; a folder without a readable manifest is left out.</summary>
    public static IReadOnlyList<BlenderCheckout> Checkouts(string root)
    {
        string folder = Folder(root);
        if (!Directory.Exists(folder)) return [];
        List<BlenderCheckout> result = [];
        foreach (var directory in new DirectoryInfo(folder).EnumerateDirectories())
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (Read(directory.FullName) is { } checkout) result.Add(checkout);
        }
        return result.OrderByDescending(c => c.CreatedUtc).ToArray();
    }
    public static BlenderCheckout Find(string root, string id)
    {
        if (id.Length is 0 or > 128 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) throw new InvalidDataException($"'{id}' is not a checkout id.");
        return Read(Path.Combine(Folder(root), id)) ?? throw new InvalidDataException($"The project has no Blender checkout {id}.");
    }
    private static BlenderCheckout? Read(string folder)
    {
        string path = Path.Combine(folder, ManifestName);
        if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return null;
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject;
            if (manifest?["format"]?.GetValue<string>() != "zstudio-blender-checkout") return null;
            var files = (manifest["files"] as JsonArray ?? []).Select(f => new BlenderCheckoutFile(f!["project"]!.GetValue<string>(), f["checkout"]!.GetValue<string>(), f["sha256"]!.GetValue<string>())).ToArray();
            return new(manifest["id"]!.GetValue<string>(), folder, manifest["model"]!.GetValue<string>(), DateTime.Parse(manifest["created"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), files);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NullReferenceException) { return null; }
    }

    /// <summary>The glTF files Blender exported into the checkout's outbox, newest first.</summary>
    public static IReadOnlyList<BlenderExport> Exports(BlenderCheckout checkout)
    {
        if (!Directory.Exists(checkout.Outbox)) return [];
        List<BlenderExport> exports = [];
        foreach (var file in new DirectoryInfo(checkout.Outbox).EnumerateFiles("*.*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            if (file.Extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".glb", StringComparison.OrdinalIgnoreCase))
                exports.Add(new(file.FullName, Path.GetRelativePath(checkout.Outbox, file.FullName).Replace('\\', '/'), file.LastWriteTimeUtc, file.Length));
        return exports.OrderByDescending(e => e.WrittenUtc).ToArray();
    }

    /// <summary>
    /// Seals the newest export (or <paramref name="export"/>, relative to the outbox), checks it and plans the project changes:
    /// the model's glTF (with buffer and texture references pointing into the project) and buffer, and each texture whose
    /// PNG is new or differs from the project's. A texture other models also use changes for them too; the notes say so.
    /// </summary>
    public static BlenderUpdatePlan PlanUpdate(SourceWorkspace workspace, BlenderCheckout checkout, string? export = null, CancellationToken token = default)
    {
        var exports = Exports(checkout);
        var chosen = export == null ? exports.FirstOrDefault() ?? throw new InvalidDataException($"Nothing was exported into {checkout.Outbox} yet.")
            : exports.FirstOrDefault(e => e.Relative.Equals(export.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"{export} is not an export in the outbox.");
        if (chosen.Gltf.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{chosen.Relative} is a binary glTF with embedded textures. Export again with the format glTF Separate (.gltf + .bin + textures).");
        // Seal: copy the export and every file it uses, reading each twice so a file Blender is still writing is refused.
        string generation = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string sealedFolder = Path.Combine(checkout.Folder, "sealed", generation);
        Directory.CreateDirectory(sealedFolder);
        string exportFolder = Path.GetDirectoryName(chosen.Gltf)!;
        byte[] json = Stable(chosen.Gltf);
        JsonObject root = Parse(json, chosen.Relative);
        Dictionary<string, byte[]> uses = new(StringComparer.OrdinalIgnoreCase);
        long total = json.Length;
        byte[] Use(string uri)
        {
            string relative = Uri.UnescapeDataString(uri).Replace('\\', '/');
            string full = Path.GetFullPath(Path.Combine(exportFolder, relative));
            if (!full.StartsWith(Path.GetFullPath(checkout.Outbox) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The export uses {uri}, which is outside the outbox.");
            if (uses.TryGetValue(full, out var known)) return known;
            byte[] bytes = Stable(full);
            if ((total += bytes.Length) > MaximumExportBytes) throw new InvalidDataException("The export is larger than 512 MiB.");
            string target = Path.Combine(sealedFolder, Path.GetRelativePath(exportFolder, full));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, bytes);
            return uses[full] = bytes;
        }
        File.WriteAllBytes(Path.Combine(sealedFolder, Path.GetFileName(chosen.Gltf)), json);
        // Read it as a build would, to refuse what the model loader cannot take before anything changes.
        GltfDocument document;
        try { document = GltfDocument.Read(json, Use, token); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{chosen.Relative}: {ex.Message}", ex); }
        if (!document.AllNodes().Any()) throw new InvalidDataException($"{chosen.Relative} has no nodes.");

        List<(string, byte[])> changes = []; List<string> notes = [];
        string model = checkout.Model, modelFolder = Path.GetDirectoryName(model)!.Replace('\\', '/');
        // Buffers: one per model, named as the project names it.
        var buffers = root["buffers"] as JsonArray ?? [];
        for (int i = 0; i < buffers.Count; i++)
        {
            if (buffers[i]?["uri"]?.GetValue<string>() is not { } uri) throw new InvalidDataException("A buffer of the export has no file; export as glTF Separate.");
            if (uri.StartsWith("data:", StringComparison.Ordinal)) continue;
            string name = Path.GetFileNameWithoutExtension(model) + (i == 0 ? "" : $"_{i}") + ".bin";
            changes.Add(($"{modelFolder}/{name}", Use(uri))); buffers[i]!["uri"] = name;
        }
        // Textures: by engine name (the file name), found where the model's textures were or, for a new one, beside them.
        var original = checkout.Files.Where(f => f.Checkout.StartsWith("input/textures/", StringComparison.OrdinalIgnoreCase)).ToDictionary(f => Path.GetFileName(f.Project), f => f.Project, StringComparer.OrdinalIgnoreCase);
        string textureFolder = original.Values.Select(p => Path.GetDirectoryName(p)!.Replace('\\', '/')).GroupBy(f => f, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            ?? DefaultTextureFolder(model);
        foreach (var image in root["images"] as JsonArray ?? [])
        {
            if (image?["uri"]?.GetValue<string>() is not { } uri)
                throw new InvalidDataException("The export embeds a texture in its buffer. Export as glTF Separate so each texture is a PNG file.");
            if (uri.StartsWith("data:", StringComparison.Ordinal)) throw new InvalidDataException("The export embeds a texture as data. Export as glTF Separate so each texture is a PNG file.");
            byte[] png = Use(uri);
            string name = Path.GetFileName(Uri.UnescapeDataString(uri).Replace('\\', '/'));
            if (!name.EndsWith(TextureSources.Extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Texture {name} is not a PNG; RECOIL textures are PNG files.");
            string stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
            if (stem.Length is < 1 or > 19 || stem.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))) throw new InvalidDataException($"Texture {name}: names need 1–19 letters, digits or '_' (the engine stores 19).");
            string project = original.TryGetValue(name, out var known) ? known : $"{textureFolder}/{name}";
            byte[]? existing = workspace.Read(project, token);
            if (existing == null) notes.Add($"New texture {project}.");
            else if (!existing.AsSpan().SequenceEqual(png)) notes.Add($"Texture {project} changes for every model that uses it.");
            if (existing == null || !existing.AsSpan().SequenceEqual(png)) changes.Add((project, png));
            image["uri"] = RelativeUri(modelFolder, project);
        }
        // Node names find animations and placements; report the ones the export no longer has.
        var before = Names(Parse(workspace.Read(model, token) ?? throw new InvalidDataException($"{model} no longer exists."), model));
        var after = Names(root);
        var removed = before.Except(after, StringComparer.Ordinal).Take(16).ToArray();
        if (removed.Length > 0) notes.Add("Nodes no longer present (animations and placements find nodes by name): " + string.Join(", ", removed) + ".");
        changes.Insert(0, (model, Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))));
        return new($"Update {Path.GetFileName(model)} from Blender", changes, notes, sealedFolder);

        static byte[] Stable(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new InvalidDataException($"{path} does not exist.");
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException($"{path} is a link.");
            if (info.Length > MaximumExportBytes) throw new InvalidDataException($"{path} is larger than 512 MiB.");
            var stamp = FileStamp.Read(path);
            byte[] first = File.ReadAllBytes(path);
            if (FileStamp.Read(path) != stamp || !File.ReadAllBytes(path).AsSpan().SequenceEqual(first)) throw new IOException($"{path} is still changing; wait for Blender to finish exporting.");
            return first;
        }
    }

    private static string DefaultTextureFolder(string model)
    {
        // data/mN/models[/bft] → data/mN/textures[/bft]
        string folder = Path.GetDirectoryName(model)!.Replace('\\', '/');
        return folder.Replace("/models", "/textures", StringComparison.OrdinalIgnoreCase);
    }
    private static string RelativeUri(string fromFolder, string project)
    {
        string relative = Path.GetRelativePath(fromFolder.Replace('/', Path.DirectorySeparatorChar), project.Replace('/', Path.DirectorySeparatorChar)).Replace('\\', '/');
        return string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
    }
    private static HashSet<string> Names(JsonObject root) => (root["nodes"] as JsonArray ?? []).Select(n => n?["extras"]?[Worlds.WorldGltf.Key]?["name"]?.GetValue<string>() ?? n?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
    private static JsonObject Parse(byte[] json, string name)
    {
        try { return JsonNode.Parse(json, documentOptions: new() { MaxDepth = 64 }) as JsonObject ?? throw new InvalidDataException($"{name} is not a glTF JSON object."); }
        catch (JsonException ex) { throw new InvalidDataException($"{name} is not valid JSON: {ex.Message}", ex); }
    }
}
