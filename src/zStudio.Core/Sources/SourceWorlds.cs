using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A glTF model in the project that a world script can load.</summary>
public sealed record SourceModelChoice(string Path, string Folder, string Name);
/// <summary>
/// A model a mission's world script loads: from any project folder, under a node name, either placed in the world at a
/// position and heading (degrees about Y) or, without a position, kept as an unplaced root that resources such as
/// <c>aiv.zrd</c> place copies of by name.
/// </summary>
public sealed record SourceModelAddition(string Model, string Name, Vector3? Position = null, float Heading = 0);
/// <summary>A definition file other missions list for a root, and the animations it defines for it.</summary>
public sealed record SourceDefinitionFile(string Path, IReadOnlyList<string> Animations, IReadOnlyList<string> Missions);
/// <summary>One accepted world edit: a model and the definition files listed with it.</summary>
public sealed record SourceWorldAddition(SourceModelAddition Model, IReadOnlyList<string> DefinitionFiles);
/// <summary>A private build of one mission for previewing: the world file and the results and inputs of each output.</summary>
public sealed record SourceWorldBuild(string Mission, string Folder, string WorldPath, IReadOnlyList<SourceExportResult> Outputs, IReadOnlyDictionary<string, FileStamp> Inputs)
{
    /// <summary>Every project file the build read or looked for, from the pending content or the disk.</summary>
    public IReadOnlyCollection<string> Dependencies { get; init; } = [];
    /// <summary>Every lookup by name the mission makes as the game loads it, with the node it finds in this build.</summary>
    public IReadOnlyList<SourceLookup> Lookups { get; init; } = [];
    /// <summary>Where each node of the built world came from, by its slot in the world file (the scene's node index).</summary>
    public IReadOnlyDictionary<int, WorldNodeProvenance> Provenance { get; init; } = new Dictionary<int, WorldNodeProvenance>();
    /// <summary>How many times each script instruction (script, line) ran while the world was built.</summary>
    public IReadOnlyDictionary<(string Script, int Line), int> Executions { get; init; } = new Dictionary<(string, int), int>();
    /// <summary>The GameZWriteZBDFile instruction that wrote the world.</summary>
    public SourceInstruction? WriteInstruction { get; init; }
}

/// <summary>
/// Editing a mission world from its sources: the world is what the mission's build script assembles, so adding a model
/// adds the script lines that load it (<c>SetModelDirectory</c> for its folder, <c>LoadGameGen</c>, and for a placed model
/// its transform and <c>AddChild</c> under the world) before the script writes the world. The export then includes the
/// model's geometry and materials, and every texture it uses joins the mission's packs.
/// </summary>
public static partial class SourceWorlds
{
    public const int MaximumNameLength = 31;
    public const float MaximumCoordinate = 1_000_000;
    /// <summary>Game files a preview builds: the world, what the Whole world view reads beside it, and one full-quality texture pack.</summary>
    private static readonly string[] PreviewOutputs = ["{0}/gamez.zbd", "{0}/anim.zbd", "{0}/zrdr.zbd", "{0}/rtexture16.zbd", "zrdr.zbd", "interp.zbd", "image.zbd"];
    [GeneratedRegex(@"\A[A-Za-z0-9_.\-]{1,31}\z", RegexOptions.CultureInvariant)] private static partial Regex NodeName();
    [GeneratedRegex(@"\Am\d{1,3}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex MissionName();

    /// <summary>Missions whose world the project builds (those with a gamegen/mN.gs script), in number order.</summary>
    public static IReadOnlyList<string> Missions(string root) => SourceBuilder.Plan(root).Where(p => p.Family == "world").Select(p => p.Path.Split('/')[0]).ToArray();

    /// <summary>Every glTF model under data that a script can load by name, in path order.</summary>
    public static IReadOnlyList<SourceModelChoice> Models(string root)
    {
        if (!SourceProject.IsProject(root)) throw new InvalidDataException("This folder is not a source project (it needs data and gamegen folders).");
        return SourceProject.Files(root, SourceProject.DataFolder, n => n.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            .Where(p => IsLoadable(Path.GetFileNameWithoutExtension(p)) && IsScriptFolder(Path.GetDirectoryName(p)!))
            .Select(p => new SourceModelChoice(p, Path.GetDirectoryName(p)!.Replace('\\', '/'), Path.GetFileNameWithoutExtension(p))).ToArray();
    }
    private static bool IsLoadable(string stem) => NodeName().IsMatch(stem);
    /// <summary>A folder a script can name in one token: no separators, comments, macros or path lists.</summary>
    private static bool IsScriptFolder(string folder) => folder.Length <= 200 && folder.All(c => c is > ' ' and < (char)127 and not (',' or '#' or '%' or ';'));

    /// <summary>Checks an addition against the project and the tokens a script can hold.</summary>
    public static void Validate(string root, SourceModelAddition addition)
    {
        string model = addition.Model.Replace('\\', '/');
        if (!model.StartsWith(SourceProject.DataFolder + "/", StringComparison.OrdinalIgnoreCase) || !(model.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || model.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"'{addition.Model}' is not a glTF model in the data folder.");
        string path = SourceProject.Resolve(root, model);
        if (!File.Exists(path)) throw new InvalidDataException($"The model {model} does not exist.");
        SourceProject.RejectNestedLinks(root, model);
        if (!IsLoadable(Path.GetFileNameWithoutExtension(model))) throw new InvalidDataException($"Scripts load models by a name of 1–{MaximumNameLength} letters, digits, '_', '-' or '.'; rename {Path.GetFileName(model)} first.");
        string folder = Path.GetDirectoryName(model)!.Replace('\\', '/');
        if (!IsScriptFolder(folder)) throw new InvalidDataException($"Scripts cannot name the folder {folder} (spaces, commas, '#', '%' or ';'); move the model first.");
        // LoadGameGen finds name.gltf before name.glb in a folder.
        if (model.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(path, ".gltf")))
            throw new InvalidDataException($"{folder} also holds {Path.GetFileNameWithoutExtension(model)}.gltf, which scripts load instead of {Path.GetFileName(model)}.");
        if (!NodeName().IsMatch(addition.Name)) throw new InvalidDataException($"Node names need 1–{MaximumNameLength} letters, digits, '_', '-' or '.'.");
        if (addition.Position is { } p && !(Finite(p.X) && Finite(p.Y) && Finite(p.Z))) throw new InvalidDataException($"Positions need finite coordinates within ±{MaximumCoordinate:N0}.");
        if (!float.IsFinite(addition.Heading) || Math.Abs(addition.Heading) > 360) throw new InvalidDataException("The heading needs −360 to 360 degrees.");
        static bool Finite(float v) => float.IsFinite(v) && Math.Abs(v) <= MaximumCoordinate;
    }

    /// <summary>The script lines that load <paramref name="addition"/>, as the shipped scripts load models.</summary>
    public static IReadOnlyList<string> ScriptLines(SourceModelAddition addition)
    {
        string model = addition.Model.Replace('\\', '/');
        string folder = "..\\" + Path.GetDirectoryName(model)!.Replace('/', '\\');
        List<string> lines = [$"SetModelDirectory {folder}", $"LoadGameGen {Path.GetFileName(model)} {addition.Name}"];
        if (addition.Position is { } p)
        {
            lines.Add($"Object3DTranslate {Number(p.X)} {Number(p.Y)} {Number(p.Z)}");
            if (addition.Heading != 0) lines.Add($"Object3DRotate 0.0 {Number(addition.Heading)} 0.0");
            lines.Add("FindNode %worldName%");
            lines.Add($"AddChild {addition.Name}");
        }
        return lines;
    }
    private static string Number(float value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    /// <summary>
    /// <paramref name="script"/> (a world script's bytes) with the lines of each addition inserted before the line that
    /// writes the world. Everything else, including line endings, is kept.
    /// </summary>
    public static byte[] InsertIntoScript(ReadOnlySpan<byte> script, IEnumerable<SourceModelAddition> additions, string name = "the world script")
    {
        string text = GameGenScriptText.Decode(script);
        var lines = additions.SelectMany(ScriptLines).ToArray();
        if (lines.Length == 0) return script.ToArray();
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        int start = 0;
        while (start <= text.Length)
        {
            int end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
            var tokens = GameGenScriptText.TokenizeLine(text[start..end].TrimEnd('\r'));
            if (tokens.Count > 0 && tokens[0] == "GameZWriteZBDFile")
                return Encoding.Latin1.GetBytes(text[..start] + string.Concat(lines.Select(l => l + newline)) + text[start..]);
            if (tokens.Count > 0 && tokens[0].Equals("Quit", StringComparison.OrdinalIgnoreCase)) break;
            start = end + 1;
        }
        throw new InvalidDataException($"{name} does not write the world itself (GameZWriteZBDFile); add the model lines to the script that does.");
    }

    /// <summary>
    /// <paramref name="definitions"/> (a mission's anim.zad) with <paramref name="files"/> appended to its ANIMATION_LIST.
    /// Files it already lists are skipped. Text stays text, with its comments and layout; compiled data stays compiled.
    /// </summary>
    public static byte[] AddDefinitionFiles(ReadOnlySpan<byte> definitions, IEnumerable<string> files, CancellationToken token = default)
    {
        bool text = ZrdText.LooksLikeText(definitions);
        ZrdTextSyntax? syntax = null; ZrdNode tree;
        try { if (text) { syntax = ZrdTextSyntax.Parse(definitions, token); tree = syntax.Root; } else tree = ZrdDecoder.Read(definitions.ToArray(), token); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"The animation definitions do not parse: {ex.Message}", ex); }
        var list = files.Select(f => "..\\" + f.Replace('/', '\\')).ToList();
        bool added = false;
        // Files listed anywhere in the file are not listed again.
        HashSet<string> listed = new(StringComparer.OrdinalIgnoreCase);
        Listed(tree, 0);
        var result = Walk(tree, 0);
        if (!added) throw new InvalidDataException("The animation definitions have no ANIMATION_DEFINITIONS list.");
        if (ZrdTextSyntax.StructurallyEqual(result, tree)) return definitions.ToArray();
        // Text keeps its comments and layout; only the list gains lines.
        if (syntax == null) return ZrdWriter.Write(result, token);
        var (rewritten, lossless) = syntax.Rewrite(result, token);
        if (!lossless) throw new InvalidDataException("The animation list cannot keep its comments and layout with this change (it would be too large, or would not read back as edited); add the files in a text editor.");
        return System.Text.Encoding.Latin1.GetBytes(rewritten);

        // As the compiler reads the file: arrays that only wrap the keyword list are unwrapped, and new files join the
        // last top-level ANIMATION_DEFINITIONS, so they compile after everything the file already lists.
        ZrdNode Walk(ZrdNode node, int depth)
        {
            if (node.Kind != ZrdKind.Array || depth > 64) return node;
            if (node.Children is [{ Kind: ZrdKind.Array } wrapped]) return node with { Children = [Walk(wrapped, depth + 1)] };
            var children = node.Children.ToList();
            int at = -1;
            for (int i = 0; i + 1 < children.Count; i++)
                if (children[i] is { Kind: ZrdKind.String, Text: "ANIMATION_DEFINITIONS" } && children[i + 1].Kind == ZrdKind.Array) at = i;
            if (at < 0) return node;
            children[at + 1] = Definitions(children[at + 1]); added = true;
            return node with { Children = children };
        }
        ZrdNode Definitions(ZrdNode body)
        {
            var items = body.Children.ToList();
            int at = -1;
            for (int i = 0; i + 1 < items.Count; i++) if (items[i] is { Kind: ZrdKind.String, Text: "ANIMATION_LIST" } && items[i + 1].Kind == ZrdKind.Array) at = i;
            if (at < 0)
            {
                items.Add(Text("ANIMATION_LIST")); items.Add(ZrdNode.Create(ZrdKind.Array)); at = items.Count - 2;
            }
            var entries = items[at + 1].Children.ToList();
            foreach (string file in list)
                if (listed.Add(Normalize(file)))
                {
                    entries.Add(Text("ANIMATION_DEFINITION_FILE"));
                    entries.Add(ZrdNode.Create(ZrdKind.Array) with { Children = [Text(file)] });
                }
            items[at + 1] = items[at + 1] with { Children = entries };
            return body with { Children = items };
        }
        void Listed(ZrdNode node, int depth)
        {
            if (node.Kind != ZrdKind.Array || depth > 64) return;
            for (int i = 0; i < node.Children.Count; i++)
            {
                if (node.Children[i] is { Kind: ZrdKind.String, Text: "ANIMATION_DEFINITION_FILE" } && i + 1 < node.Children.Count && node.Children[i + 1].Children is [{ Kind: ZrdKind.String } path, ..])
                    listed.Add(Normalize(path.Text));
                Listed(node.Children[i], depth + 1);
            }
        }
        static ZrdNode Text(string text) => new(Guid.NewGuid(), ZrdKind.String, 0, text, []);
        static string Normalize(string path)
        {
            string normalized = path.Replace('/', '\\');
            while (normalized.Contains("\\\\", StringComparison.Ordinal)) normalized = normalized.Replace("\\\\", "\\", StringComparison.Ordinal);
            return normalized;
        }
    }

    /// <summary>
    /// Definition files other missions list with an animation for <paramref name="root"/> that <paramref name="mission"/>
    /// does not list yet: the animations a model brought from another mission needs, such as an enemy's destruction.
    /// </summary>
    public static IReadOnlyList<SourceDefinitionFile> DefinitionsFor(string projectRoot, string mission, string root, IReadOnlyDictionary<string, byte[]>? overlay = null, CancellationToken token = default)
    {
        DiskFiles files = new(projectRoot, overlay);
        HashSet<string> own = new(StringComparer.OrdinalIgnoreCase);
        string ownRoot = SourceBuilder.AnimationRoot(mission);
        if (files.Exists(ownRoot)) own.UnionWith(AnimationDefinitionSet.Load(files, ownRoot, token).Files);
        Dictionary<string, (SortedSet<string> Animations, SortedSet<string> Missions)> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (string other in MissionFolders(projectRoot).Where(m => !m.Equals(mission, StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            string path = SourceBuilder.AnimationRoot(other);
            if (!files.Exists(path)) continue;
            AnimationDefinitionSet set;
            try { set = AnimationDefinitionSet.Load(files, path, token); }
            catch (InvalidDataException) { continue; }
            foreach (var definition in set.Definitions)
            {
                if (own.Contains(definition.File)) continue;
                bool binds;
                try { binds = AnimationCompiler.Roots(definition.Item, [root], _ => { }).Any(r => r.Root == root); }
                catch (InvalidDataException) { continue; }
                if (!binds) continue;
                if (!found.TryGetValue(definition.File, out var entry)) found[definition.File] = entry = (new(StringComparer.Ordinal), new(StringComparer.OrdinalIgnoreCase));
                entry.Animations.Add(definition.Item.Item("ANIMATION_NAME")?.Text() is { Length: > 0 } name ? name : root);
                entry.Missions.Add(other);
            }
        }
        return found.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f => new SourceDefinitionFile(f.Key, [.. f.Value.Animations], [.. f.Value.Missions.OrderBy(m => int.Parse(m.AsSpan(1)))])).ToArray();
    }
    private static IEnumerable<string> MissionFolders(string root) => new DirectoryInfo(SourceProject.Resolve(root, SourceProject.DataFolder)).EnumerateDirectories()
        .Where(d => MissionName().IsMatch(d.Name)).Select(d => d.Name.ToLowerInvariant()).OrderBy(n => int.Parse(n.AsSpan(1)));

    /// <summary>The project on disk with some files replaced by pending content; links are refused.</summary>
    internal sealed class DiskFiles(string root, IReadOnlyDictionary<string, byte[]>? overlay) : IProjectFiles
    {
        public bool Exists(string relative)
        {
            if (overlay?.ContainsKey(relative) == true) return true;
            if (!File.Exists(SourceProject.Resolve(root, relative))) return false;
            SourceProject.RejectNestedLinks(root, relative);
            return true;
        }
        public byte[] Read(string relative, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (overlay?.TryGetValue(relative, out var bytes) == true) return bytes;
            SourceProject.RejectNestedLinks(root, relative);
            string path = SourceProject.Resolve(root, relative);
            if (new FileInfo(path).Length > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException($"{relative} exceeds 512 MiB.");
            return File.ReadAllBytes(path);
        }
    }

    /// <summary>Where mission worlds are built to be shown: derived data in zStudio's working folder of the project, which builds never read.</summary>
    public const string PreviewFolder = SourcePublisher.WorkingFolder + "/cache/worlds";
    /// <summary>The project's <see cref="PreviewFolder"/>.</summary>
    public static string PreviewRoot(string root) => Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), PreviewFolder.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Builds <paramref name="mission"/> into <paramref name="destination"/> (a new folder inside the project's
    /// <see cref="PreviewFolder"/>) as the export would, with <paramref name="overlay"/> replacing project files: the world, its animations and resources,
    /// the common resources, scripts and images the Whole world view reads beside it, and one full-quality texture pack.
    /// Only the world must build; other failures are reported in the outputs. <paramref name="additions"/> are models
    /// the overlay's script has just added (see <see cref="AddModel"/>): the world must load each of them where it is written,
    /// and hold each placed one (see <see cref="CheckAdditions"/>).
    /// </summary>
    public static async Task<SourceWorldBuild> BuildPreviewAsync(string root, string mission, string destination, IReadOnlyDictionary<string, byte[]>? overlay = null, IProgress<SourceProgress>? progress = null, CancellationToken token = default, IReadOnlyList<SourceModelAddition>? additions = null)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (!MissionName().IsMatch(mission)) throw new InvalidDataException($"'{mission}' is not a mission folder name.");
        mission = mission.ToLowerInvariant();
        // Only inside the project's preview folder, never through a link: nothing else of the project (or elsewhere) is written.
        string previews = PreviewRoot(root);
        if (!destination.StartsWith(previews + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || PickupPlacementEditSession.IsProtectedPath(destination))
            throw new InvalidDataException($"A mission world is built in the project's {PreviewFolder} folder, not in {destination}.");
        SourceProject.RejectLinks(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException($"The preview folder {destination} is not empty.");
        IReadOnlyCollection<string> added = overlay?.Keys.Where(k => !File.Exists(SourceProject.Resolve(root, k))).ToArray() ?? [];
        var plan = await Task.Run(() => SourceBuilder.Plan(root, added), token).ConfigureAwait(false);
        var selected = PreviewOutputs.Select(o => string.Format(CultureInfo.InvariantCulture, o, mission))
            .Select(o => plan.FirstOrDefault(p => p.Path.Equals(o, StringComparison.OrdinalIgnoreCase))).OfType<SourceOutputPlan>().ToArray();
        if (!selected.Any(p => p.Family == "world")) throw new InvalidDataException($"The project has no world script for {mission} ({SourceBuilder.WorldScript(mission)}) or no glTF models.");
        SourceBuilder.Snapshot snapshot = new(root, overlay);
        DateTime now = DateTime.UtcNow; List<SourceExportResult> results = []; Animation.AnimationPackage? animations = null;
        Directory.CreateDirectory(destination);
        for (int i = 0; i < selected.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var output = selected[i]; progress?.Report(new(i, selected.Length, output.Path));
            try
            {
                var built = await Task.Run(() => SourceBuilder.Build(root, output, snapshot, now, token), token).ConfigureAwait(false);
                if (output.Family == "animations") animations = built.Package;
                if (output.Family == "world" && additions != null) CheckAdditions(snapshot.World(mission, token).LoadedRoots, additions);
                var check = FormatRegistry.Default.OpenBytes(output.Path, built.Bytes, token: token);
                if (check.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException("The built file does not reopen: " + error.Message);
                string path = SourceProject.Resolve(destination, output.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, built.Bytes, token).ConfigureAwait(false);
                results.Add(new(output.Path, output.Family, "built", built.Bytes.Length, built.Items, built.Warnings));
            }
            catch (Exception ex) when (SourceBuilder.IsBuildFailure(ex))
            {
                if (output.Family == "world") throw new InvalidDataException($"The {mission} world does not build: {ex.Message}", ex);
                results.Add(new(output.Path, output.Family, "failed", 0, 0, [], ex.Message));
            }
        }
        progress?.Report(new(selected.Length, selected.Length, "Built"));
        var assembled = snapshot.World(mission, token);
        var slots = GameZWriter.SlotIndices(assembled.World);
        Dictionary<int, WorldNodeProvenance> provenance = [];
        foreach (var (node, origin) in assembled.Provenance) if (slots.TryGetValue(node, out int slot)) provenance[slot] = origin;
        // What the mission looks up by name when the game loads it, so an edit that changes what a lookup finds can say so.
        // Only the world must build: lookups the project's scripts or animations keep from being resolved are not reported.
        IReadOnlyList<SourceLookup> lookups;
        try { lookups = await Task.Run(() => SourceBuilder.MissionLookups(mission, snapshot, animations, token), token).ConfigureAwait(false); }
        catch (Exception ex) when (SourceBuilder.IsBuildFailure(ex)) { lookups = []; }
        return new(mission, destination, SourceProject.Resolve(destination, $"{mission}/gamez.zbd"), results, snapshot.Stamps()) { Dependencies = snapshot.Dependencies(), Lookups = lookups, Provenance = provenance, Executions = assembled.Executions, WriteInstruction = assembled.WriteInstruction };
    }

    /// <summary>
    /// Adds a model to <paramref name="mission"/>'s world as one change of <paramref name="workspace"/>: the lines that load it
    /// go into the world script (<c>gamegen/mN.gs</c>) before the line that writes the world, and its definition files into the
    /// mission's animation list (<c>data/mN/zrdr/anim.zad</c>). The world must then be built to check it (see <see cref="CheckAdditions"/>).
    /// </summary>
    public static SourceTransaction AddModel(SourceWorkspace workspace, string mission, SourceWorldAddition addition, CancellationToken token = default)
    {
        if (!MissionName().IsMatch(mission)) throw new InvalidDataException($"'{mission}' is not a mission folder name.");
        mission = mission.ToLowerInvariant();
        Validate(workspace.Root, addition.Model);
        string scriptPath = SourceBuilder.WorldScript(mission), definitionsPath = SourceBuilder.AnimationRoot(mission);
        byte[] script = workspace.Read(scriptPath, token) ?? throw new InvalidDataException($"The project has no world script {scriptPath}.");
        List<(string, byte[]?)> changes = [(scriptPath, InsertIntoScript(script, [addition.Model], scriptPath))];
        if (addition.DefinitionFiles.Count > 0)
        {
            foreach (string file in addition.DefinitionFiles)
            {
                if (!file.StartsWith(SourceProject.DataFolder + "/", StringComparison.OrdinalIgnoreCase) || !file.EndsWith(AnimationDefinitionSet.Extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"'{file}' is not a definition file ({AnimationDefinitionSet.Extension}) in the data folder.");
                if (!workspace.Exists(file)) throw new InvalidDataException($"The definition file {file} does not exist.");
            }
            byte[] definitions = workspace.Read(definitionsPath, token) ?? throw new InvalidDataException($"The project has no {definitionsPath} to list animation definitions in.");
            changes.Add((definitionsPath, AddDefinitionFiles(definitions, addition.DefinitionFiles, token)));
        }
        return workspace.Apply($"Add {addition.Model.Name}", changes, token) ?? throw new InvalidDataException("Adding the model changed no source file.");
    }

    /// <summary>
    /// Checks that the script loaded <paramref name="additions"/> as the last models before it wrote the world (their
    /// lines stand right before <c>GameZWriteZBDFile</c>) and that each placed one is a child of the world. AddChild
    /// attaches the newest node with the name, so a model with a node of its own named like it would put that node in
    /// the world instead of the placed root.
    /// </summary>
    internal static void CheckAdditions(IReadOnlyList<WorldNode> loadedRoots, IReadOnlyList<SourceModelAddition> additions)
    {
        for (int i = 0; i < additions.Count; i++)
        {
            var addition = additions[i]; int at = loadedRoots.Count - additions.Count + i;
            var root = at >= 0 ? loadedRoots[at] : null;
            if (root == null || root.Name != addition.Name)
                throw new InvalidDataException($"The script does not run the lines that load {addition.Name} before it writes the world.");
            if (addition.Position == null || root.Parents.Any(p => p.Class == WorldNodeClass.World)) continue;
            throw new InvalidDataException(Holds(root, addition.Name)
                ? $"{Path.GetFileName(addition.Model)} has a node of its own named {addition.Name}, so AddChild {addition.Name} would put that node in the world instead of the placed model; choose another name."
                : $"{addition.Name} is not placed in the world: the script's %worldName% names no world node.");
        }
        static bool Holds(WorldNode root, string name)
        {
            HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance); Stack<WorldNode> pending = new(root.Children);
            while (pending.TryPop(out var node))
            {
                if (!seen.Add(node)) continue;
                if (node.Name == name) return true;
                foreach (var child in node.Children) pending.Push(child);
            }
            return false;
        }
    }
}
