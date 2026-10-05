using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A model checked out for Blender: its folder under <c>zstudio/export</c> and the project files it was copied from.</summary>
public sealed record BlenderCheckout(string Id, string Folder, string Model, DateTime CreatedUtc, IReadOnlyList<BlenderCheckoutFile> Files)
{
    /// <summary>Files updates from this checkout wrote (project path and SHA-256): states the project may hold without an edit since the checkout.</summary>
    public IReadOnlyList<BlenderCheckoutFile> Applied { get; init; } = [];
    /// <summary>The copy of the model Blender imports.</summary>
    public string Input => Path.Combine(Folder, "input", Path.GetFileName(Model));
    /// <summary>Where Blender exports go; each export is one glTF file (with its buffers and textures) anywhere below it.</summary>
    public string Outbox => Path.Combine(Folder, "outbox");
}
/// <summary>A project file a checkout copied: its project path, its path in the checkout and its SHA-256 then.</summary>
public sealed record BlenderCheckoutFile(string Project, string Checkout, string Sha256);
/// <summary>An export Blender wrote into a checkout's outbox.</summary>
public sealed record BlenderExport(string Gltf, string Relative, DateTime WrittenUtc, long Bytes);
/// <summary>
/// The changes an export makes to the project, with notes (shared textures, renamed nodes), the sealed copy it was read
/// from, and the SHA-256 each changed file had in the workspace when the plan was made (null for a new file).
/// </summary>
public sealed record BlenderUpdatePlan(string Label, IReadOnlyList<(string Relative, byte[] Content)> Changes, IReadOnlyList<string> Notes, string Sealed)
{
    public IReadOnlyDictionary<string, string?> Expected { get; init; } = new Dictionary<string, string?>();
}
/// <summary>An update refused because project files it would replace changed since the checkout (another edit, or another update).</summary>
public sealed class BlenderConflictException(IReadOnlyList<string> files, string? reason = null) : IOException(reason ??
    $"The export would replace {string.Join(", ", files.Take(8))}{(files.Count > 8 ? $" and {files.Count - 8} more" : "")}, which changed in the project since the checkout or were not part of it (another model's texture of the same name). Check the model out again, rename the texture in Blender, or update anyway to replace them.")
{
    public IReadOnlyList<string> Files { get; } = files;
}

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

    /// <summary>
    /// Copies <paramref name="model"/> (a project .gltf, as the workspace holds it) and the files it uses into a new checkout.
    /// The copy shows transparent textures and, for a model a script loads as a pickup, hides its collision volume as the
    /// game does (<see cref="Worlds.WorldGltf.ApplyPresentation"/>), also for models reconstructed before reconstruction
    /// wrote that; it states every node's zone, so a node Blender moves keeps it (<see cref="Worlds.WorldGltf.ExplicitZones"/>).
    /// The project's file is not changed.
    /// </summary>
    public static BlenderCheckout Checkout(SourceWorkspace workspace, string model, CancellationToken token = default)
    {
        string? created = null;
        try { return Checkout(workspace, model, token, ref created); }
        catch when (created != null)
        {
            // A canceled or failed checkout leaves no partial folder behind (listings skip folders without a manifest).
            try { Directory.Delete(created, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
    private static BlenderCheckout Checkout(SourceWorkspace workspace, string model, CancellationToken token, ref string? created)
    {
        model = SourceWorkspace.Normalize(model);
        workspace.CheckEditable(model);
        if (!model.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{model} is not a .gltf model; Blender checkouts work on glTF files with separate buffers and textures.");
        byte[] json = workspace.Read(model, token) ?? throw new InvalidDataException($"{model} does not exist.");
        JsonObject root = Parse(json, model);
        // The id names a folder and is typed back by MCP clients: the model's stem as plain characters, a time and a random tail.
        string stem = new([.. Path.GetFileNameWithoutExtension(model).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').Take(64)]);
        string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + stem + "-" + Guid.NewGuid().ToString("N")[..6];
        string folder = Path.Combine(Folder(workspace.Root), id), input = Path.Combine(folder, "input");
        SourceProject.RejectLinks(Folder(workspace.Root));
        created = folder;
        Directory.CreateDirectory(Path.Combine(input, "textures")); Directory.CreateDirectory(Path.Combine(folder, "outbox"));
        List<BlenderCheckoutFile> files = [new(model, "input/" + Path.GetFileName(model), SourceProject.Sha256(json))];
        // Buffers sit next to the model in the checkout; textures in its textures folder, under their engine names.
        foreach (var buffer in root["buffers"] as JsonArray ?? [])
        {
            if (Text(buffer?["uri"]) is not { } uri || uri.StartsWith("data:", StringComparison.Ordinal)) continue;
            string project = Worlds.WorldAssembler.Relative(model, Uri.UnescapeDataString(uri));
            byte[] bytes = workspace.Read(project, token) ?? throw new InvalidDataException($"{model} uses {project}, which does not exist.");
            string name = Path.GetFileName(project);
            File.WriteAllBytes(Path.Combine(input, name), bytes); buffer!["uri"] = Uri.EscapeDataString(name);
            files.Add(new(project, "input/" + name, SourceProject.Sha256(bytes)));
        }
        HashSet<string> copied = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Formats.TextureTransparency> transparency = new(StringComparer.OrdinalIgnoreCase);
        foreach (var image in root["images"] as JsonArray ?? [])
        {
            if (Text(image?["uri"]) is not { } uri || uri.StartsWith("data:", StringComparison.Ordinal)) continue;
            string project = Worlds.WorldAssembler.Relative(model, Uri.UnescapeDataString(uri));
            string name = Path.GetFileName(project);
            image!["uri"] = "textures/" + Uri.EscapeDataString(name);
            if (!copied.Add(name)) continue;
            if (workspace.Read(project, token) is not { } bytes) continue;
            File.WriteAllBytes(Path.Combine(input, "textures", name), bytes);
            files.Add(new(project, "input/textures/" + name, SourceProject.Sha256(bytes)));
            if (Transparency(bytes, token) is { } kind) transparency[name] = kind;
        }
        Worlds.WorldGltf.ApplyPresentation(root, uri => transparency.TryGetValue(Path.GetFileName(uri), out var kind) ? kind : null, LoadedAsPickup(workspace, model, token));
        // Blender moves nodes freely: each keeps the zone it has (PlanUpdate takes the stated zones back out).
        Worlds.WorldGltf.ExplicitZones(root);
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
        if (id.Length is 0 or > 128 || id is "." or ".." || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) throw new InvalidDataException($"'{id}' is not a checkout id.");
        string folder = Path.Combine(Folder(root), id);
        if (new DirectoryInfo(folder) is { Exists: true } info && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException($"The checkout {id} is a link.");
        return Read(folder) ?? throw new InvalidDataException($"The project has no Blender checkout {id}.");
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
            var applied = (manifest["applied"] as JsonArray ?? []).Select(f => new BlenderCheckoutFile(f!["project"]!.GetValue<string>(), "", f["sha256"]!.GetValue<string>())).ToArray();
            return new(manifest["id"]!.GetValue<string>(), folder, manifest["model"]!.GetValue<string>(), DateTime.Parse(manifest["created"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), files) { Applied = applied };
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
    /// Records that an update from <paramref name="checkout"/> wrote <paramref name="plan"/>'s files, so a later export of the
    /// same checkout may replace them without counting them as edits made since the checkout.
    /// </summary>
    public static void RecordApplied(BlenderCheckout checkout, BlenderUpdatePlan plan)
    {
        string path = Path.Combine(checkout.Folder, ManifestName);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) return;
        var manifest = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject ?? throw new InvalidDataException($"{path} is not a checkout manifest.");
        var applied = manifest["applied"] as JsonArray ?? (JsonArray)(manifest["applied"] = new JsonArray());
        foreach (var (relative, content) in plan.Changes)
            if (applied.Count < 4096) applied.Add(new JsonObject { ["project"] = relative, ["sha256"] = SourceProject.Sha256(content) });
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    /// <summary>
    /// Seals the newest export (or <paramref name="export"/>, relative to the outbox), checks it and plans the project changes:
    /// the model's glTF (with buffer and texture references pointing into the project) and buffer, and each texture Blender
    /// changed (new, or different from its checked-out copy). A texture other models also use changes for them too; the
    /// notes say so. Files the export would replace must still be as checked out (or as an earlier update from this
    /// checkout left them): otherwise the update is refused with <see cref="BlenderConflictException"/>, unless
    /// <paramref name="force"/> replaces them anyway.
    /// </summary>
    public static BlenderUpdatePlan PlanUpdate(SourceWorkspace workspace, BlenderCheckout checkout, string? export = null, bool force = false, CancellationToken token = default)
    {
        string? sealedFolder = null;
        try { return PlanUpdate(workspace, checkout, export, force, ref sealedFolder, token); }
        catch when (sealedFolder != null)
        {
            // A refused or failed update leaves no sealed copy behind (each can be hundreds of megabytes).
            try { Directory.Delete(sealedFolder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
    private static BlenderUpdatePlan PlanUpdate(SourceWorkspace workspace, BlenderCheckout checkout, string? export, bool force, ref string? sealedFolderCreated, CancellationToken token)
    {
        SourceProject.RejectLinks(checkout.Folder);
        var exports = Exports(checkout);
        var chosen = export == null ? exports.FirstOrDefault() ?? throw new InvalidDataException($"Nothing was exported into {checkout.Outbox} yet.")
            : exports.FirstOrDefault(e => e.Relative.Equals(export.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"{export} is not an export in the outbox.");
        if (chosen.Gltf.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{chosen.Relative} is a binary glTF with embedded textures. Export again with the format glTF Separate (.gltf + .bin + textures).");
        // Seal: copy the export and every file it uses, reading each twice so a file Blender is still writing is refused.
        string generation = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        string sealedFolder = Path.Combine(checkout.Folder, "sealed", generation);
        SourceProject.RejectNestedLinks(checkout.Folder, $"sealed/{generation}");
        Directory.CreateDirectory(sealedFolder);
        sealedFolderCreated = sealedFolder;
        string outbox = Path.GetFullPath(checkout.Outbox), exportFolder = Path.GetDirectoryName(chosen.Gltf)!;
        SourceProject.RejectNestedLinks(checkout.Folder, Path.GetRelativePath(checkout.Folder, chosen.Gltf).Replace('\\', '/'));
        byte[] json = Stable(chosen.Gltf);
        JsonObject root = Parse(json, chosen.Relative);
        Dictionary<string, byte[]> uses = new(StringComparer.OrdinalIgnoreCase);
        long total = json.Length;
        // The sealed copy keeps the export's place in the outbox, so it never leaves the sealed folder.
        void Seal(string full, byte[] bytes)
        {
            string target = Path.GetFullPath(Path.Combine(sealedFolder, Path.GetRelativePath(outbox, full)));
            if (!target.StartsWith(Path.GetFullPath(sealedFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{full} is outside the outbox.");
            SourceProject.RejectNestedLinks(checkout.Folder, Path.GetRelativePath(checkout.Folder, target).Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            SourceProject.RejectNestedLinks(checkout.Folder, Path.GetRelativePath(checkout.Folder, target).Replace('\\', '/'));
            File.WriteAllBytes(target, bytes);
        }
        // A file the export names: its URI already unescaped (as glTF readers resolve them), relative to the export.
        byte[] Use(string relative)
        {
            string full = Path.GetFullPath(Path.Combine(exportFolder, relative.Replace('\\', '/')));
            if (!full.StartsWith(outbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The export uses {relative}, which is outside the outbox.");
            if (uses.TryGetValue(full, out var known)) return known;
            SourceProject.RejectNestedLinks(checkout.Folder, Path.GetRelativePath(checkout.Folder, full).Replace('\\', '/'));
            byte[] bytes = Stable(full);
            if ((total += bytes.Length) > MaximumExportBytes) throw new InvalidDataException("The export is larger than 512 MiB.");
            Seal(full, bytes);
            return uses[full] = bytes;
        }
        Seal(chosen.Gltf, json);
        // Read it as a build would, to refuse what the model loader cannot take before anything changes.
        GltfDocument document;
        try { document = GltfDocument.Read(json, Use, token); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{chosen.Relative}: {ex.Message}", ex); }
        if (!document.AllNodes().Any()) throw new InvalidDataException($"{chosen.Relative} has no nodes.");
        // What Blender cannot keep by itself: copies of a shared node must agree, and a group moved as an empty passes its
        // transform to its objects (each node keeps the zone the checkout stated for it, below).
        Worlds.WorldGltf.CheckInstances(document, chosen.Relative);
        UngroupTransforms(document, root, chosen.Relative);

        List<(string, byte[])> changes = []; List<string> notes = [];
        string model = checkout.Model, modelFolder = Path.GetDirectoryName(model)!.Replace('\\', '/');
        // Buffers: one per model, named as the project names it (further buffers with a dot, which reconstruction never uses).
        var buffers = root["buffers"] as JsonArray ?? [];
        for (int i = 0; i < buffers.Count; i++)
        {
            if (Text(buffers[i]?["uri"]) is not { } uri) throw new InvalidDataException("A buffer of the export has no file; export as glTF Separate.");
            if (uri.StartsWith("data:", StringComparison.Ordinal)) continue;
            string name = Path.GetFileNameWithoutExtension(model) + (i == 0 ? "" : $".{i}") + ".bin";
            changes.Add(($"{modelFolder}/{name}", Use(Uri.UnescapeDataString(uri)))); buffers[i]!["uri"] = Uri.EscapeDataString(name);
        }
        // Textures: by engine name (the file name), found where the model's textures were or, for a new one, beside them.
        var original = checkout.Files.Where(f => f.Checkout.StartsWith("input/textures/", StringComparison.OrdinalIgnoreCase)).ToDictionary(f => Path.GetFileName(f.Project), f => f.Project, StringComparer.OrdinalIgnoreCase);
        string textureFolder = original.Values.Select(p => Path.GetDirectoryName(p)!.Replace('\\', '/')).GroupBy(f => f, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            ?? DefaultTextureFolder(model);
        foreach (var image in root["images"] as JsonArray ?? [])
        {
            if (Text(image?["uri"]) is not { } uri)
                throw new InvalidDataException("The export embeds a texture in its buffer. Export as glTF Separate so each texture is a PNG file.");
            if (uri.StartsWith("data:", StringComparison.Ordinal)) throw new InvalidDataException("The export embeds a texture as data. Export as glTF Separate so each texture is a PNG file.");
            string unescaped = Uri.UnescapeDataString(uri);
            byte[] png = Use(unescaped);
            string name = Path.GetFileName(unescaped.Replace('\\', '/'));
            if (!name.EndsWith(TextureSources.Extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Texture {name} is not a PNG; RECOIL textures are PNG files.");
            string stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
            if (stem.Length is < 1 or > 19 || stem.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))) throw new InvalidDataException($"Texture {name}: names need 1–19 letters, digits or '_' (the engine stores 19).");
            try { Export.PngDecoder.Decode(png); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException) { throw new InvalidDataException($"Texture {name} is not a readable PNG: {ex.Message}", ex); }
            string project = original.TryGetValue(name, out var known) ? known : $"{textureFolder}/{name}";
            image!["uri"] = RelativeUri(modelFolder, project);
            // Only what Blender changed: a checked-out texture it wrote back as it was stays as the project has it now.
            string inputCopy = Path.Combine(checkout.Folder, "input", "textures", name);
            if (original.ContainsKey(name) && File.Exists(inputCopy) && File.ReadAllBytes(inputCopy).AsSpan().SequenceEqual(png)) continue;
            byte[]? existing = workspace.Read(project, token);
            if (existing != null && existing.AsSpan().SequenceEqual(png)) continue;
            notes.Add(existing == null ? $"New texture {project}." : $"Texture {project} changes for every model that uses it.");
            changes.Add((project, png));
        }
        // Node names find animations and placements; report the ones the export no longer has.
        var checkedOut = Parse(workspace.Read(model, token) ?? throw new InvalidDataException($"{model} no longer exists."), model);
        var before = Names(checkedOut);
        var after = Names(root);
        // Engine attributes travel in extras.recoil, which Blender writes only with Custom Properties on.
        bool attributesDropped = EngineAttributes(checkedOut) > 0 && EngineAttributes(root) == 0;
        if (attributesDropped) notes.Add("The export had no engine attributes (Custom Properties off); the model's flags, zones, references and material attributes were dropped.");
        var removed = before.Except(after, StringComparer.Ordinal).Take(16).ToArray();
        if (removed.Length > 0) notes.Add("Nodes no longer present (animations and placements find nodes by name): " + string.Join(", ", removed) + ".");
        // After counting the engine attributes the export carries: the zones the checkout stated go back to what the
        // file's hierarchy gives, which can leave a node without any.
        Worlds.WorldGltf.ImplicitZones(root, chosen.Relative);
        changes.Insert(0, (model,Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))));
        // What the update replaces must be as the checkout (or an earlier update from it) left it.
        var accepted = checkout.Files.Concat(checkout.Applied).GroupBy(f => f.Project, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(f => f.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> expected = new(StringComparer.OrdinalIgnoreCase);
        List<string> conflicts = [];
        foreach (var (relative, _) in changes)
        {
            string? current = workspace.Read(relative, token) is { } bytes ? SourceProject.Sha256(bytes) : null;
            expected[relative] = current;
            if (current != null && (!accepted.TryGetValue(relative, out var states) || !states.Contains(current))) conflicts.Add(relative);
        }
        // One question covers everything the update would replace or drop, so one "update anyway" never hides another.
        if (!force && (conflicts.Count > 0 || attributesDropped))
        {
            List<string> reasons = [];
            if (attributesDropped) reasons.Add("The export has none of the model's engine attributes (extras.recoil: node flags, zones, references, materials); export again with Include → Custom Properties on.");
            if (conflicts.Count > 0) reasons.Add($"It would replace {string.Join(", ", conflicts.Take(8))}{(conflicts.Count > 8 ? $" and {conflicts.Count - 8} more" : "")}, which changed in the project since the checkout or were not part of it (another model's texture of the same name).");
            reasons.Add("Check the model out again, or update anyway to accept this.");
            throw new BlenderConflictException(attributesDropped ? [.. conflicts.Prepend(model).Distinct(StringComparer.OrdinalIgnoreCase)] : conflicts, string.Join(" ", reasons));
        }
        if (conflicts.Count > 0) notes.Add($"Replaced changes made since the checkout in {string.Join(", ", conflicts.Take(8))}.");
        return new($"Update {Path.GetFileName(model)} from Blender", changes, notes, sealedFolder) { Expected = expected };

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

    /// <summary>
    /// Gives the objects of a mission database group the transform an artist gave the group (a Blender empty moved, turned
    /// or scaled): the build deletes the groups, so their objects carry transforms of their own and a group may have none
    /// (WorldAssembler). Groups count as the build finds them, from the file's roots through groups. Refused, saying why,
    /// where the objects cannot take it: a group with geometry, a moved group whose objects are in a part's file, and a
    /// level-of-detail object (it stands where its parent is) or an object several parents share under a moved group.
    /// </summary>
    private static void UngroupTransforms(GltfDocument document, JsonObject root, string path)
    {
        var nodes = root["nodes"] as JsonArray ?? [];
        HashSet<GltfNode> seen = new(ReferenceEqualityComparer.Instance);
        foreach (var node in document.Roots) Visit(node, Matrix4x4.Identity, 0);

        void Visit(GltfNode group, Matrix4x4 passed, int depth)
        {
            if (depth > GltfDocument.MaximumDepth || !seen.Add(group) || !Worlds.WorldGltf.IsGroup(group, path) || group.Index < 0 || group.Index >= nodes.Count || nodes[group.Index] is not JsonObject json) return;
            string name = Worlds.WorldGltf.EngineName(group);
            var engine = group.Extras?[Worlds.WorldGltf.Key] as JsonObject;
            if (group.Mesh != null || engine?["model"] != null || Text(engine?["class"]) == "lod")
                throw new InvalidDataException($"{path}: group {name} has geometry of its own or is a level-of-detail node; a group of the mission database only holds objects (the build deletes it). Give the geometry an object of its own.");
            var transform = (group.Matrix ?? Matrix4x4.Identity) * passed;
            if (!transform.IsIdentity)
            {
                if (Text(engine?["ref"]) is { } part)
                    throw new InvalidDataException($"{path}: group {name} was moved, but the objects it places are in {part}, which every reference to it shares; move them in that file (check it out), or move the group back.");
                foreach (var child in group.Children)
                {
                    if (Worlds.WorldGltf.IsGroup(child, path)) continue;
                    var values = child.Extras?[Worlds.WorldGltf.Key] as JsonObject;
                    string childName = Worlds.WorldGltf.EngineName(child);
                    if (Text(values?["class"]) == "lod") throw new InvalidDataException($"{path}: group {name} was moved, but it holds the level-of-detail node {childName}, which stands where its parent is and cannot take the move; move the objects below it, or move the group back.");
                    if (values?["instance"] != null) throw new InvalidDataException($"{path}: group {name} was moved, but it holds {childName}, a node several parents share, which would move under each of them; move the group back.");
                    var local = (child.Matrix ?? Matrix4x4.Identity) * transform;
                    float[] rows = [local.M11, local.M12, local.M13, local.M21, local.M22, local.M23, local.M31, local.M32, local.M33, local.M41, local.M42, local.M43];
                    if (!rows.All(float.IsFinite) || new[] { local.M41, local.M42, local.M43 }.Any(v => MathF.Abs(v) > SourceWorlds.MaximumCoordinate))
                        throw new InvalidDataException($"{path}: group {name} was moved so far that {childName} would lie beyond ±{SourceWorlds.MaximumCoordinate:N0}.");
                    if (nodes[child.Index] is JsonObject target) GltfNodeEdits.SetLocal(target, local);
                }
                GltfNodeEdits.SetLocal(json, Matrix4x4.Identity);
            }
            foreach (var child in group.Children) Visit(child, transform, depth + 1);
        }
    }

    /// <summary>
    /// Whether a script loads the model as a pickup (LoadGameGen file puNNN), whose collision volume the game hides. Scripts
    /// name a model by its file name and find it through their model folders; for what a viewer shows, the name decides.
    /// </summary>
    private static bool LoadedAsPickup(SourceWorkspace workspace, string model, CancellationToken token)
    {
        string stem = Path.GetFileNameWithoutExtension(model);
        var added = workspace.Overlay().Keys.Where(k => !File.Exists(SourceProject.Resolve(workspace.Root, k))).ToArray();
        foreach (string script in SourceProject.Files(workspace.Root, SourceProject.GameGenFolder, n => n.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".gw", StringComparison.OrdinalIgnoreCase), added))
        {
            token.ThrowIfCancellationRequested();
            if (workspace.Read(script, token) is not { } bytes) continue;
            foreach (var line in GameGenScriptSyntax.Parse(bytes).Lines)
                if (line.Tokens is ["LoadGameGen", var file, var name, ..] && Worlds.WorldGltf.IsPickupName(name)
                    && Path.GetFileNameWithoutExtension(file.Replace('\\', '/')).Equals(stem, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>How a texture is transparent, or null when it is not a PNG the packs could read (the build reports those).</summary>
    private static Formats.TextureTransparency? Transparency(byte[] png, CancellationToken token)
    {
        try { return Formats.TexturePackBuilder.Classify(Export.PngDecoder.Decode(png, token: token)); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException) { return null; }
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
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    /// <summary>How many nodes, meshes, materials and scenes of a glTF carry engine attributes.</summary>
    private static int EngineAttributes(JsonObject root) => new[] { "nodes", "meshes", "materials", "scenes" }
        .Sum(key => (root[key] as JsonArray ?? []).Count(item => ((item as JsonObject)?["extras"] as JsonObject)?[Worlds.WorldGltf.Key] != null));
    /// <summary>The engine names of a glTF's nodes; unexpected shapes of extras or names (another tool's output) count as no name.</summary>
    private static HashSet<string> Names(JsonObject root) => (root["nodes"] as JsonArray ?? [])
        .Select(n => Text((((n as JsonObject)?["extras"] as JsonObject)?[Worlds.WorldGltf.Key] as JsonObject)?["name"]) ?? Text((n as JsonObject)?["name"]) ?? "")
        .Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
    private static JsonObject Parse(byte[] json, string name)
    {
        try { return JsonNode.Parse(json, documentOptions: new() { MaxDepth = 64 }) as JsonObject ?? throw new InvalidDataException($"{name} is not a glTF JSON object."); }
        catch (JsonException ex) { throw new InvalidDataException($"{name} is not valid JSON: {ex.Message}", ex); }
    }
}
