using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>A model checked out for Blender: its folder under <c>zstudio/export</c> and the project files it was copied from.</summary>
public sealed record BlenderCheckout(string Id, string Folder, string Model, DateTime CreatedUtc, IReadOnlyList<BlenderCheckoutFile> Files)
{
    public int Version { get; init; } = SourceBlender.CheckoutVersion;
    /// <summary>Files updates from this checkout wrote (project path and SHA-256): states the project may hold without an edit since the checkout.</summary>
    public IReadOnlyList<BlenderCheckoutFile> Applied { get; init; } = [];
    /// <summary>The copy of the model Blender imports.</summary>
    public string Input => Path.Combine(Folder, "input", Model);
    /// <summary>Graph generations are completed by a receipt below this outbox.</summary>
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
/// The reference-aware Blender round trip. <see cref="Checkout"/> copies one project graph into
/// <c>zstudio/export/&lt;id&gt;/input</c>. The RECOIL Blender extension writes a completed graph generation into
/// <c>outbox</c>; <see cref="PlanUpdate"/> verifies and seals its receipt and dependencies before planning changes.
/// Nothing reaches the project until the user accepts the plan into the shared workspace.
/// </summary>
public static partial class SourceBlender
{
    public const string ExportFolder = "zstudio/export";
    private const string ManifestName = "manifest.json";
    private const long MaximumExportBytes = 512L * 1024 * 1024;
    private const long MaximumManifestBytes = 64L * 1024 * 1024;
    private const int MaximumCheckoutManifestBytes = 4 * 1024 * 1024;
    public const int CheckoutVersion = 2;

    private static string Folder(string root) => Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), "zstudio", "export");

    /// <summary>Owns one new checkout/sealed generation, including cleanup through the same held directories.</summary>
    private sealed class CopyFiles : IDisposable
    {
        private readonly DirectoryLease directories = new();
        private readonly List<string> folders = [];
        private readonly List<SealedFile.Ownership> files = [];
        private readonly HashSet<string> knownFolders = new(StringComparer.OrdinalIgnoreCase);
        private string? root;
        private bool keep;

        public void Root(string path)
        {
            path = Path.GetFullPath(path);
            if (PickupPlacementEditSession.IsProtectedPath(directories.CapturedPath(path)))
                throw new InvalidDataException("Blender checkouts cannot be written inside the protected zbd_1998/zbd_1999 folders.");
            directories.Parent(path, create: true);
            // A colliding folder belongs to somebody else; neither write into it nor clean it up.
            directories.CreateDirectory(path);
            root = path;
            folders.Add(path); knownFolders.Add(path);
        }

        public void Folder(string path)
        {
            path = Path.GetFullPath(path);
            if (knownFolders.Contains(path)) return;
            if (root == null || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A Blender copy folder is outside its new checkout or sealed generation.");
            Folder(Path.GetDirectoryName(path)!);
            directories.CreateDirectory(path);
            folders.Add(path); knownFolders.Add(path);
        }

        public void Write(string path, byte[] bytes)
        {
            path = Path.GetFullPath(path);
            Folder(Path.GetDirectoryName(path)!);
            using var output = SealedFile.Create(path, bytes, directories);
            files.Add(output.RetainCreated());
        }

        public void Keep() => keep = true;

        public void Dispose()
        {
            try
            {
                if (keep) return;
                // No recursive path enumeration: remove only our known files and then known empty folders.
                // A changed/occupied entry may prevent cleanup, but can never redirect it outside the held parent.
                foreach (var file in files)
                    try { SealedFile.DeleteOwned(file, directories); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                for (int i = folders.Count - 1; i >= 0; i--)
                    try { directories.DeleteDirectory(folders[i]); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            finally { directories.Dispose(); }
        }
    }

    /// <summary>Copies the current project model and its complete reference graph from one verified snapshot.</summary>
    public static BlenderCheckout Checkout(SourceWorkspace workspace, string model, CancellationToken token = default) => CheckoutGraph(workspace, model, token);

    private static SourceProject.ScanBudget ScanBudget(int maximum, CancellationToken token, InventoryBudget? inventory = null) => new(maximum, limit => new IOException(
        $"{ExportFolder} holds more than {limit:N0} files and folders, far more than Blender checkouts need. Delete the checkouts and exports you no longer need there (each checkout is one folder), then try again."), token, inventory);

    /// <summary>
    /// The project's checkouts, newest first; a folder without a readable manifest is left out. Every file and folder the
    /// listing visits, and every file a manifest lists, counts towards <see cref="SourceProject.MaximumScannedEntries"/>
    /// (beyond that it is refused with <see cref="IOException"/>), and <paramref name="token"/> is observed at each.
    /// </summary>
    public static IReadOnlyList<BlenderCheckout> Checkouts(string root, CancellationToken token = default) => Checkouts(root, ScanBudget(SourceProject.MaximumScannedEntries, token));
    /// <summary>
    /// The project's checkouts (newest first), each with the exports in its outbox (newest first): one listing, bounded as
    /// <see cref="Checkouts(string, CancellationToken)"/> is with the outboxes' files and folders counted too.
    /// </summary>
    public static IReadOnlyList<(BlenderCheckout Checkout, IReadOnlyList<BlenderExport> Exports)> CheckoutExports(string root, CancellationToken token = default) =>
        CheckoutExports(root, SourceProject.MaximumScannedEntries, token);
    /// <param name="maximumEntries">The files and folders the listing may visit (<see cref="SourceProject.MaximumScannedEntries"/>; smaller in tests).</param>
    internal static IReadOnlyList<(BlenderCheckout Checkout, IReadOnlyList<BlenderExport> Exports)> CheckoutExports(string root, int maximumEntries, CancellationToken token, long maximumManifestBytes = MaximumManifestBytes,
        long inventoryLimit = InventoryBudget.MaximumUnits)
    {
        var budget = ScanBudget(maximumEntries, token, new(inventoryLimit));
        List<(BlenderCheckout, IReadOnlyList<BlenderExport>)> result = [];
        foreach (var checkout in Checkouts(root, budget, maximumManifestBytes)) { budget.Rows(1); result.Add((checkout, Exports(checkout, budget))); }
        return result;
    }
    private static IReadOnlyList<BlenderCheckout> Checkouts(string root, SourceProject.ScanBudget budget, long maximumManifestBytes = MaximumManifestBytes)
    {
        string folder = Folder(root);
        if (!Directory.Exists(folder)) return [];
        List<BlenderCheckout> result = [];
        // Files beside the checkouts cost as much to pass over as folders.
        foreach (var entry in SourceProject.Entries(folder, budget))
        {
            if (entry is not DirectoryInfo directory || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (Read(directory.FullName, budget, bytes =>
            {
                if ((maximumManifestBytes -= bytes) < 0) throw new IOException("Blender checkout manifests together exceed 64 MiB; remove checkouts no longer needed and try again.");
            }) is { } checkout) result.Add(checkout);
        }
        budget.Rows(result.Count);
        return result.OrderByDescending(c => c.CreatedUtc).ToArray();
    }
    public static BlenderCheckout Find(string root, string id)
    {
        if (id.Length is 0 or > 128 || id is "." or ".." || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) throw new InvalidDataException($"'{id}' is not a checkout id.");
        string folder = Path.Combine(Folder(root), id);
        if (new DirectoryInfo(folder) is { Exists: true } info && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException($"The checkout {id} is a link.");
        return Read(folder) ?? throw new InvalidDataException($"The project has no supported Blender checkout {id}. Create a new checkout.");
    }
    /// <param name="budget">When the manifest is one of a listing's: the files it lists count towards that listing's entries.</param>
    private static BlenderCheckout? Read(string folder, SourceProject.ScanBudget? budget = null, Action<int>? charge = null)
    {
        string path = Path.Combine(folder, ManifestName);
        if (!File.Exists(path) || new FileInfo(path).Length > MaximumCheckoutManifestBytes) return null;
        try
        {
            byte[] bytes = SourceRead.All(path, MaximumCheckoutManifestBytes, budget?.Token ?? default);
            charge?.Invoke(bytes.Length);
            var manifest = JsonNode.Parse(bytes) as JsonObject;
            if (manifest?["format"]?.GetValue<string>() != "zstudio-blender-checkout" || manifest["version"]?.GetValue<int>() != CheckoutVersion) return null;
            string id = manifest["id"]!.GetValue<string>();
            if (id != Path.GetFileName(folder) || id.Length is 0 or > 128 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) return null;
            budget?.Visit((long)((manifest["files"] as JsonArray)?.Count ?? 0) + ((manifest["applied"] as JsonArray)?.Count ?? 0));
            var inventory = budget?.Inventory ?? new InventoryBudget();
            // The bounded DOM already owns its text; admit every retained row before projecting arrays.
            foreach (string key in new[] { "files", "applied" })
                foreach (var row in manifest[key] as JsonArray ?? [])
                    inventory.Path((long)row!["project"]!.GetValue<string>().Length + (key == "files" ? row["checkout"]!.GetValue<string>().Length : 0) + row["sha256"]!.GetValue<string>().Length, budget?.Token ?? default);
            inventory.Path((long)folder.Length + manifest["model"]!.GetValue<string>().Length, budget?.Token ?? default);
            var files = (manifest["files"] as JsonArray ?? []).Select(f => new BlenderCheckoutFile(f!["project"]!.GetValue<string>(), f["checkout"]!.GetValue<string>(), f["sha256"]!.GetValue<string>())).ToArray();
            var applied = (manifest["applied"] as JsonArray ?? []).Select(f => new BlenderCheckoutFile(f!["project"]!.GetValue<string>(), "", f["sha256"]!.GetValue<string>())).ToArray();
            string model = manifest["model"]!.GetValue<string>();
            // These fields identify Windows paths, not arbitrary text. Bound them before path composition or presentation.
            if (model.Length > 32767 || Path.GetFileName(model).Length > 255 || files.Concat(applied).Any(f => f.Project.Length > 32767 || f.Checkout.Length > 32767 || f.Sha256.Length > 64)) return null;
            return new(id, folder, model, DateTime.Parse(manifest["created"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), files) { Applied = applied, Version = CheckoutVersion };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NullReferenceException) { return null; }
    }

    /// <summary>
    /// Completed graph export receipts in the checkout's outbox, newest first. Every file and folder below the outbox
    /// counts towards <see cref="SourceProject.MaximumScannedEntries"/> (beyond that the listing is refused with
    /// <see cref="IOException"/>), and <paramref name="token"/> is observed at each.
    /// </summary>
    public static IReadOnlyList<BlenderExport> Exports(BlenderCheckout checkout, CancellationToken token = default) => Exports(checkout, ScanBudget(SourceProject.MaximumScannedEntries, token));
    private static IReadOnlyList<BlenderExport> Exports(BlenderCheckout checkout, SourceProject.ScanBudget budget)
    {
        RequireCurrentCheckout(checkout);
        budget.Path((long)checkout.Folder.Length + 7);
        string outbox = checkout.Outbox;
        if (!Directory.Exists(outbox)) return [];
        List<BlenderExport> exports = [];
        // Folders count too: a tree of empty folders costs as much to walk as one of files.
        foreach (var entry in SourceProject.Entries(outbox, budget, recurse: true, skip: FileAttributes.ReparsePoint, ignoreInaccessible: true))
        {
            if (entry is FileInfo file && file.Name.Equals(GraphManifest, StringComparison.OrdinalIgnoreCase))
                exports.Add(new(file.FullName, Path.GetRelativePath(outbox, file.FullName).Replace('\\', '/'), file.LastWriteTimeUtc, file.Length));
        }
        budget.Rows(exports.Count);
        return exports.OrderByDescending(e => e.WrittenUtc).ToArray();
    }

    /// <summary>
    /// Records that an update from <paramref name="checkout"/> wrote <paramref name="plan"/>'s files, so a later export of the
    /// same checkout may replace them without counting them as edits made since the checkout.
    /// </summary>
    public static void RecordApplied(BlenderCheckout checkout, BlenderUpdatePlan plan)
    {
        RequireCurrentCheckout(checkout);
        string path = Path.Combine(checkout.Folder, ManifestName);
        using DirectoryLease directories = new();
        directories.Parent(path);
        byte[] writtenManifest = AppliedManifest(checkout, plan.Changes, directories);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        SourceProject.RejectLinks(checkout.Folder);
        using var sealedFile = SealedFile.Create(temporary, writtenManifest, directories);
        SourceProject.RejectLinks(checkout.Folder);
        SourceProject.RejectNestedLinks(checkout.Folder, ManifestName);
        sealedFile.MoveTo(path, replace: true);
    }

    // Also run while planning, before accepting any edit: a manifest limit must refuse the update, never omit states.
    private static byte[] AppliedManifest(BlenderCheckout checkout, IReadOnlyList<(string Relative, byte[] Content)> changes, DirectoryLease? directories = null)
    {
        using DirectoryLease? owned = directories == null ? new() : null;
        directories ??= owned!;
        SourceProject.RejectLinks(checkout.Folder);
        SourceProject.RejectNestedLinks(checkout.Folder, ManifestName);
        string path = Path.Combine(checkout.Folder, ManifestName);
        using FileStream input = directories.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumCheckoutManifestBytes) throw new InvalidDataException("The checkout manifest exceeds 4 MiB; create a new checkout.");
        byte[] previous = SourceRead.All(input, MaximumCheckoutManifestBytes, path);
        var manifest = JsonNode.Parse(previous) as JsonObject ?? throw new InvalidDataException($"{path} is not a checkout manifest.");
        if (manifest["format"]?.GetValue<string>() != "zstudio-blender-checkout" || manifest["version"]?.GetValue<int>() != CheckoutVersion)
            throw new InvalidDataException("This Blender checkout is unsupported. Create a new checkout.");
        var applied = manifest["applied"] as JsonArray ?? (JsonArray)(manifest["applied"] = new JsonArray());
        var known = applied.Select(f => (f!["project"]!.GetValue<string>().ToUpperInvariant(), f["sha256"]!.GetValue<string>().ToUpperInvariant())).ToHashSet();
        long estimatedBytes = previous.Length;
        foreach (var (relative, content) in changes)
        {
            string sha = SourceProject.Sha256(content);
            if (!known.Add((relative.ToUpperInvariant(), sha.ToUpperInvariant()))) continue;
            // Charge escaped paths before appending or serializing thousands of records.
            if ((estimatedBytes += 6L * relative.Length + 128) > MaximumCheckoutManifestBytes) throw new InvalidDataException("The checkout manifest would exceed 4 MiB; create a new checkout.");
            applied.Add(new JsonObject { ["project"] = relative, ["sha256"] = sha });
        }
        byte[] writtenManifest = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        if (writtenManifest.Length > MaximumCheckoutManifestBytes) throw new InvalidDataException("The checkout manifest would exceed 4 MiB; create a new checkout.");
        return writtenManifest;
    }

    /// <summary>
    /// Seals the newest export (or <paramref name="export"/>, relative to the outbox), checks it and plans the project changes:
    /// the model's glTF (with buffer and texture references pointing into the project) and buffer, and each texture Blender
    /// changed (new, or different from its original checkout digest). A texture other models also use changes for them too; the
    /// notes say so. Files the export would replace must still be as checked out (or as an earlier update from this
    /// checkout left them): otherwise the update is refused with <see cref="BlenderConflictException"/>, unless
    /// <paramref name="force"/> replaces them anyway.
    /// </summary>
    public static BlenderUpdatePlan PlanUpdate(SourceWorkspace workspace, BlenderCheckout checkout, string? export = null, bool force = false, CancellationToken token = default)
        => PlanGraphUpdate(workspace, checkout, export, force, null, token);

    /// <summary>
    /// Ask about all conflicts of this sealed export before continuing the same plan. The callback runs on the caller's
    /// thread; callers preparing on a worker must marshal their question. Declining cancels without changing sources.
    /// Prepared workspace validation still rejects project inputs changed while the question was open.
    /// </summary>
    public static BlenderUpdatePlan PlanUpdate(SourceWorkspace workspace, BlenderCheckout checkout, string? export,
        Func<BlenderConflictException, CancellationToken, bool> confirmConflict, CancellationToken token = default)
        => PlanGraphUpdate(workspace, checkout, export, false, confirmConflict, token);

    /// <summary>
    /// The texture a material shows is the one of its image's name (the build's <see cref="WorldAssembler"/>): a texture name a
    /// material records follows the image Blender assigned in its place, so the file does not name a texture it no longer shows.
    /// The caller identifies materials whose imported image was removed; they must explicitly clear the named texture too.
    /// </summary>
    private static void FollowImages(JsonObject root, Func<JsonObject, string, bool> removedImage, string path, bool followNames = true)
    {
        var textures = root["textures"] as JsonArray ?? []; var images = root["images"] as JsonArray ?? [];
        foreach (var material in (root["materials"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if ((material["extras"] as JsonObject)?[Worlds.WorldGltf.Key] is not JsonObject engine || Text(engine["texture"]) is not { } recorded) continue;
            if (((material["pbrMetallicRoughness"] as JsonObject)?["baseColorTexture"] as JsonObject)?["index"] is not { } index)
            {
                if (removedImage(material, recorded)) throw new InvalidDataException($"{path}: material {JsonData.ShownText(Text(material["name"]) ?? "")} no longer has its imported image but still names texture {JsonData.ShownText(recorded)}. Reconnect the image, or remove 'texture' from RECOIL Material Properties > Other values (extras.recoil) to make the material untextured.");
                continue;
            }
            if (!followNames) continue;
            if (!GltfInteger.TryInt64(index, out long t) || t < 0 || t >= textures.Count
                || !GltfInteger.TryInt64((textures[(int)t] as JsonObject)?["source"], out long i) || i < 0 || i >= images.Count || Text((images[(int)i] as JsonObject)?["uri"]) is not { } uri) continue;
            string stem = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(uri)).ToLowerInvariant();
            if (!stem.Equals(recorded, StringComparison.OrdinalIgnoreCase)) engine["texture"] = stem;
        }
    }

    /// <summary>A changed or newly assigned texture that is not what its name finds in a mission (<see cref="TextureShadows"/>).</summary>
    private readonly record struct TextureShadow(string Texture, string Mission, string Other, bool Hides)
    {
        public string Reason => Hides
            ? $"{Mission} would find {Texture} before {Other}, which the models of {Mission} that show {Path.GetFileNameWithoutExtension(Texture)} use now"
            : $"{Mission} finds {Other} first, so the model would show that file there, not {Texture}";
    }

    /// <summary>
    /// Where a changed or newly assigned texture would not be what its name finds. A mission's pack holds the PNGs of its texture
    /// folders, the first of each name in folder order (<see cref="TextureSources.MissionFolders"/>, as
    /// <see cref="SourceBuilder.MissionTextures"/> packs them), and the game finds a texture by name; a name no folder holds is
    /// taken from where the model's image points. So, for each texture and each mission that builds the model (a model of
    /// <c>data/mN</c> in mN, one of <c>data/common</c> in every mission, every mission whose scripts load it and every mission whose map binds it, <paramref name="bound"/>,
    /// such as 1999 m6 loading m13's <c>wlpart</c> files): another file of the name found first is what the model shows there;
    /// and in each mission that searches the texture's folder, a new texture found before another file of its name takes the
    /// name from that file for every model of the mission that shows it.
    /// </summary>
    private static List<TextureShadow> TextureShadows(SourceWorkspace workspace, string model, IReadOnlyList<string> textures, IReadOnlyDictionary<string, string?> current,
        IReadOnlySet<string> bound, CancellationToken token)
    {
        List<TextureShadow> shadows = [];
        if (textures.Count == 0) return shadows;
        string[] parts = model.Split('/');
        bool common = parts.Length > 2 && parts[1].Equals("common", StringComparison.OrdinalIgnoreCase);
        TextureModelUses uses = new(workspace, model, token);
        // Each file once, as the workspace holds it (pending additions and deletions included).
        Dictionary<string, bool> present = new(StringComparer.OrdinalIgnoreCase);
        bool Exists(string relative)
        {
            if (!present.TryGetValue(relative, out bool found)) present[relative] = found = workspace.Exists(relative, token);
            return found;
        }
        foreach (string mission in SourceProject.MissionFolders(workspace.Root, token))
        {
            bool builds = common || parts.Length > 2 && parts[1].Equals(mission, StringComparison.OrdinalIgnoreCase) || bound.Contains(mission);
            IReadOnlyList<string>? folders = null;
            foreach (string texture in textures)
            {
                token.ThrowIfCancellationRequested();
                string folder = Path.GetDirectoryName(texture)!.Replace('\\', '/'), name = Path.GetFileName(texture);
                // Another mission matters only when a PNG in one of its possible pack folders can take this name.
                // The multiplayer classifier below chooses the actual folders and their order.
                if (!builds && !folder.StartsWith("data/common/", StringComparison.OrdinalIgnoreCase) && !folder.StartsWith($"data/{mission}/", StringComparison.OrdinalIgnoreCase)
                    && !TextureSources.MissionFolders(mission, false).Concat(TextureSources.MissionFolders(mission, true)).Any(f => Exists($"{f}/{name}"))) continue;
                if (folders == null)
                {
                    var script = workspace.Read($"{SourceProject.GameGenFolder}/support/load{mission}.gw", token, SourceProject.MaximumSourceTextBytes);
                    folders = TextureSources.MissionFolders(mission, script != null && SourceBuilder.Multiplayer(script, token));
                }
                bool searched = folders.Contains(folder, StringComparer.OrdinalIgnoreCase);
                int at = -1;
                for (int i = 0; i < folders.Count && at < 0; i++)
                    if (folders[i].Equals(folder, StringComparison.OrdinalIgnoreCase) || Exists($"{folders[i]}/{name}")) at = i;
                if (at < 0) continue;
                if (!folders[at].Equals(folder, StringComparison.OrdinalIgnoreCase))
                {
                    // Inspect actual uses only when another texture would win. Same names in unrelated missions do not conflict.
                    if (builds || uses.InMission(mission)) shadows.Add(new(texture, mission, $"{folders[at]}/{name}", false));
                }
                else if ((builds || searched) && current[texture] == null)
                    for (int i = at + 1; i < folders.Count; i++)
                        if (Exists($"{folders[i]}/{name}")) { shadows.Add(new(texture, mission, $"{folders[i]}/{name}", true)); break; }
            }
        }
        return shadows;
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
                throw new InvalidDataException($"{path}: group {JsonData.ShownText(name)} has geometry of its own or is a level-of-detail node; a group of the mission database only holds objects (the build deletes it). Give the geometry an object of its own.");
            var transform = (group.Matrix ?? Matrix4x4.Identity) * passed;
            if (!transform.IsIdentity)
            {
                if (Text(engine?["ref"]) is { } || engine?[Worlds.WorldGltf.ZoneReference] != null)
                    throw new InvalidDataException($"{path}: group {JsonData.ShownText(name)} was moved, but the objects it places are in {(Text(engine?["ref"]) is { } part ? JsonData.ShownText(part) : "a referenced model")}, which every reference to it shares; move them in that file (check it out), or move the group back.");
                foreach (var child in group.Children)
                {
                    if (Worlds.WorldGltf.IsGroup(child, path)) continue;
                    var values = child.Extras?[Worlds.WorldGltf.Key] as JsonObject;
                    string childName = Worlds.WorldGltf.EngineName(child);
                    if (Text(values?["class"]) == "lod") throw new InvalidDataException($"{path}: group {JsonData.ShownText(name)} was moved, but it holds the level-of-detail node {JsonData.ShownText(childName)}, which stands where its parent is and cannot take the move; move the objects below it, or move the group back.");
                    if (values?["instance"] != null) throw new InvalidDataException($"{path}: group {JsonData.ShownText(name)} was moved, but it holds {JsonData.ShownText(childName)}, a node several parents share, which would move under each of them; move the group back.");
                    var local = (child.Matrix ?? Matrix4x4.Identity) * transform;
                    float[] rows = [local.M11, local.M12, local.M13, local.M21, local.M22, local.M23, local.M31, local.M32, local.M33, local.M41, local.M42, local.M43];
                    if (!rows.All(float.IsFinite) || new[] { local.M41, local.M42, local.M43 }.Any(v => MathF.Abs(v) > SourceWorlds.MaximumCoordinate))
                        throw new InvalidDataException($"{path}: group {JsonData.ShownText(name)} was moved so far that {JsonData.ShownText(childName)} would lie beyond ±{SourceWorlds.MaximumCoordinate:N0}.");
                    if (nodes[child.Index] is JsonObject target) GltfNodeEdits.SetLocal(target, local);
                }
                GltfNodeEdits.SetLocal(json, Matrix4x4.Identity);
            }
            foreach (var child in group.Children) Visit(child, transform, depth + 1);
        }
    }

    /// <summary>
    /// Whether a terrain recipe of the project takes surfaces from <paramref name="model"/>: the build cuts those into pieces
    /// that stay below the vertex limit, so the limit of a model's mesh is not theirs. A recipe that cannot be read is
    /// skipped; the build reports it.
    /// </summary>
    private static bool TerrainSurfaces(SourceWorkspace workspace, string model, CancellationToken token)
    {
        foreach (string path in SourceTerrain.Recipes(workspace, token))
        {
            try
            {
                if (SourceTerrain.Read(workspace, path, token).Surfaces.Any(s => WorldAssembler.Relative(path, s.Model).Equals(model, StringComparison.OrdinalIgnoreCase))) return true;
            }
            catch (InvalidDataException) { }
        }
        return false;
    }

    private static void RequireCurrentCheckout(BlenderCheckout checkout)
    {
        if (checkout.Version != CheckoutVersion) throw new InvalidDataException("This Blender checkout is unsupported. Create a new checkout.");
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    private static JsonObject Parse(byte[] json, string name, CancellationToken token)
    {
        try { GltfDocument.ValidateJsonText(json, token); return JsonNode.Parse(json, documentOptions: new() { MaxDepth = 64 }) as JsonObject ?? throw new InvalidDataException($"{name} is not a glTF JSON object."); }
        catch (JsonException ex) { throw new InvalidDataException($"{name} is not valid JSON: {ex.Message}", ex); }
    }
}
