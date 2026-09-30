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
public sealed record SourceWorldBuild(string Mission, string Folder, string WorldPath, IReadOnlyList<SourceExportResult> Outputs, IReadOnlyDictionary<string, FileStamp> Inputs);

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
        List<string> lines = [$"SetModelDirectory {folder}", $"LoadGameGen {Path.GetFileNameWithoutExtension(model)}.flt {addition.Name}"];
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
    /// <paramref name="definitions"/> (a mission's anim.zrd) with <paramref name="files"/> appended to its ANIMATION_LIST.
    /// Files it already lists are skipped. Text stays text in the canonical layout; compiled data stays compiled.
    /// </summary>
    public static byte[] AddDefinitionFiles(ReadOnlySpan<byte> definitions, IEnumerable<string> files, CancellationToken token = default)
    {
        bool text = ZrdText.LooksLikeText(definitions);
        ZrdNode tree;
        try { tree = text ? ZrdText.Parse(definitions, token) : ZrdDecoder.Read(definitions.ToArray(), token); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"The animation definitions do not parse: {ex.Message}", ex); }
        var list = files.Select(f => "..\\" + f.Replace('/', '\\')).ToList();
        bool added = false;
        // Files listed anywhere in the file are not listed again.
        HashSet<string> listed = new(StringComparer.OrdinalIgnoreCase);
        Listed(tree, 0);
        var result = Walk(tree, 0);
        if (!added) throw new InvalidDataException("The animation definitions have no ANIMATION_DEFINITIONS list.");
        return text ? ZrdText.Encode(result, token) : ZrdWriter.Write(result, token);

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

    /// <summary>
    /// Builds <paramref name="mission"/> into <paramref name="destination"/> (a new private folder outside the project) as
    /// the export would, with <paramref name="overlay"/> replacing project files: the world, its animations and resources,
    /// the common resources, scripts and images the Whole world view reads beside it, and one full-quality texture pack.
    /// Only the world must build; other failures are reported in the outputs.
    /// </summary>
    public static async Task<SourceWorldBuild> BuildPreviewAsync(string root, string mission, string destination, IReadOnlyDictionary<string, byte[]>? overlay = null, IProgress<SourceProgress>? progress = null, CancellationToken token = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (!MissionName().IsMatch(mission)) throw new InvalidDataException($"'{mission}' is not a mission folder name.");
        mission = mission.ToLowerInvariant();
        SourceProject.ValidateSeparate(destination, root, "preview folder"); SourceProject.RejectLinks(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException($"The preview folder {destination} is not empty.");
        var plan = await Task.Run(() => SourceBuilder.Plan(root), token).ConfigureAwait(false);
        var selected = PreviewOutputs.Select(o => string.Format(CultureInfo.InvariantCulture, o, mission))
            .Select(o => plan.FirstOrDefault(p => p.Path.Equals(o, StringComparison.OrdinalIgnoreCase))).OfType<SourceOutputPlan>().ToArray();
        if (!selected.Any(p => p.Family == "world")) throw new InvalidDataException($"The project has no world script for {mission} ({SourceBuilder.WorldScript(mission)}) or no glTF models.");
        SourceBuilder.Snapshot snapshot = new(root, overlay);
        DateTime now = DateTime.UtcNow; List<SourceExportResult> results = [];
        Directory.CreateDirectory(destination);
        for (int i = 0; i < selected.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var output = selected[i]; progress?.Report(new(i, selected.Length, output.Path));
            try
            {
                var built = await Task.Run(() => SourceBuilder.Build(root, output, snapshot, now, token), token).ConfigureAwait(false);
                var check = FormatRegistry.Default.OpenBytes(output.Path, built.Bytes, token: token);
                if (check.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException("The built file does not reopen: " + error.Message);
                string path = SourceProject.Resolve(destination, output.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, built.Bytes, token).ConfigureAwait(false);
                results.Add(new(output.Path, output.Family, "built", built.Bytes.Length, built.Items, built.Warnings));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or FormatException)
            {
                if (output.Family == "world") throw new InvalidDataException($"The {mission} world does not build: {ex.Message}", ex);
                results.Add(new(output.Path, output.Family, "failed", 0, 0, [], ex.Message));
            }
        }
        progress?.Report(new(selected.Length, selected.Length, "Built"));
        return new(mission, destination, SourceProject.Resolve(destination, $"{mission}/gamez.zbd"), results, snapshot.Stamps());
    }
}

/// <summary>
/// Pending world edits of one mission: models added to its build script (<c>gamegen/mN.gs</c>) and definition files added
/// to its animation list (<c>data/mN/zrdr/anim.zrd</c>), with undo and redo. The project files change only on
/// <see cref="Save"/>, which refuses files changed on disk since they were read or saved here.
/// </summary>
public sealed class SourceWorldEdits
{
    public string Root { get; }
    public string Mission { get; }
    public string ScriptPath { get; }
    public string DefinitionsPath { get; }
    private readonly byte[] script;
    private readonly byte[]? definitions;
    /// <summary>What this session read or last wrote, per file: the external-change baseline.</summary>
    private readonly Dictionary<string, (byte[] Bytes, FileStamp Stamp)> disk = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceWorldAddition> history = [];
    private int position, saved;
    public event Action? Changed;

    public SourceWorldEdits(string root, string mission)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); Mission = mission.ToLowerInvariant();
        ScriptPath = SourceBuilder.WorldScript(Mission); DefinitionsPath = SourceBuilder.AnimationRoot(Mission);
        script = ReadDisk(ScriptPath) ?? throw new InvalidDataException($"The project has no world script {ScriptPath}.");
        definitions = ReadDisk(DefinitionsPath);
    }
    private byte[]? ReadDisk(string relative)
    {
        string path = SourceProject.Resolve(Root, relative);
        if (!File.Exists(path)) return null;
        SourceProject.RejectNestedLinks(Root, relative);
        var stamp = FileStamp.Read(path);
        if (stamp.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException($"{relative} exceeds {SourceProject.MaximumSourceTextBytes / (1024 * 1024)} MiB.");
        byte[] bytes = File.ReadAllBytes(path);
        if (FileStamp.Read(path) != stamp) throw new InvalidDataException($"{relative} changed while it was read; try again.");
        disk[relative] = (bytes, stamp);
        return bytes;
    }

    public IReadOnlyList<SourceWorldAddition> Additions => history.Take(position).ToArray();
    public bool IsDirty => position != saved;
    public bool CanUndo => position > 0;
    public bool CanRedo => position < history.Count;
    public bool HasDefinitions => definitions != null;

    /// <summary>Accepts one addition; it must build into the script (see <see cref="SourceWorlds.InsertIntoScript"/>).</summary>
    public void Add(SourceWorldAddition addition, CancellationToken token = default)
    {
        SourceWorlds.Validate(Root, addition.Model);
        if (addition.DefinitionFiles.Count > 0 && definitions == null) throw new InvalidDataException($"The project has no {DefinitionsPath} to list animation definitions in.");
        foreach (string file in addition.DefinitionFiles)
        {
            if (!file.StartsWith(SourceProject.DataFolder + "/", StringComparison.OrdinalIgnoreCase) || !file.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"'{file}' is not a definition file in the data folder.");
            if (!File.Exists(SourceProject.Resolve(Root, file))) throw new InvalidDataException($"The definition file {file} does not exist.");
        }
        var next = history.Take(position).Append(addition).ToArray();
        _ = Content(next, token);
        // Replacing redo steps that include the saved state makes that state unreachable.
        if (saved > position) saved = -1;
        history.RemoveRange(position, history.Count - position); history.Add(addition); position++;
        Changed?.Invoke();
    }
    /// <summary>Withdraws the newest addition, one the world could not be built with; it cannot be redone.</summary>
    public void Retract()
    {
        if (!CanUndo) throw new InvalidOperationException("Nothing to withdraw.");
        position--; history.RemoveAt(position);
        if (saved > position) saved = -1;
        Changed?.Invoke();
    }
    public void Undo() { if (!CanUndo) throw new InvalidOperationException("Nothing to undo."); position--; Changed?.Invoke(); }
    public void Redo() { if (!CanRedo) throw new InvalidOperationException("Nothing to redo."); position++; Changed?.Invoke(); }

    /// <summary>The managed files' current content (whether or not it differs from disk), to build from.</summary>
    public IReadOnlyDictionary<string, byte[]> Overlay(CancellationToken token = default) => Content(Additions, token);
    private Dictionary<string, byte[]> Content(IReadOnlyList<SourceWorldAddition> additions, CancellationToken token)
    {
        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase) { [ScriptPath] = SourceWorlds.InsertIntoScript(script, additions.Select(a => a.Model), ScriptPath) };
        if (definitions != null)
        {
            var listed = additions.SelectMany(a => a.DefinitionFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            files[DefinitionsPath] = listed.Length == 0 ? definitions : SourceWorlds.AddDefinitionFiles(definitions, listed, token);
        }
        return files;
    }

    /// <summary>Whether a managed file changed on disk since this session read or saved it.</summary>
    public bool HasExternalChanges()
    {
        foreach (var (relative, entry) in disk)
        {
            string path = SourceProject.Resolve(Root, relative);
            if (!File.Exists(path) || FileStamp.Read(path) != entry.Stamp) return true;
        }
        return File.Exists(SourceProject.Resolve(Root, DefinitionsPath)) != (definitions != null);
    }

    /// <summary>
    /// Writes each managed file whose content differs from what is on disk: through a temporary file in its folder, read
    /// back and compared before it replaces the original. Returns the files written.
    /// </summary>
    public IReadOnlyList<string> Save(CancellationToken token = default)
    {
        var content = Overlay(token); List<string> written = [];
        foreach (var (relative, bytes) in content)
        {
            token.ThrowIfCancellationRequested();
            var expected = disk[relative];
            if (bytes.AsSpan().SequenceEqual(expected.Bytes)) continue;
            string path = SourceProject.Resolve(Root, relative);
            SourceProject.RejectNestedLinks(Root, relative);
            if (PickupPlacementEditSession.IsProtectedPath(path)) throw new IOException($"{relative} is inside the protected zbd_1998/zbd_1999 folders; nothing was saved.");
            if (!File.Exists(path) || FileStamp.Read(path) != expected.Stamp || !File.ReadAllBytes(path).AsSpan().SequenceEqual(expected.Bytes))
                throw new IOException($"{relative} changed on disk since it was read; nothing more was saved. Reload the world to continue from the file.");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes)) throw new IOException($"{relative} did not write correctly; the original is unchanged.");
                File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            disk[relative] = (bytes, FileStamp.Read(path));
            written.Add(relative);
        }
        saved = position; Changed?.Invoke();
        return written;
    }
}
