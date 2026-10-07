using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Animation;

/// <summary>
/// A keyword item of an animation definition: <c>KEY ( values… )</c>. Values are the scalars and nested items of the
/// parenthesised list; a bare keyword (no list) has none.
/// </summary>
public sealed class AnimationItem(string key, ZrdNode? values, string source)
{
    // Only the compiler's operation-owned view caches this frozen tree. Public items may wrap an editable caller's
    // child list, so their existing live-query behavior remains unchanged.
    private readonly Action<long>? reserve;
    private IReadOnlyList<ZrdNode>? scalars;
    private IReadOnlyList<AnimationItem>? parsed;
    private Dictionary<string, AnimationItem>? first;
    internal AnimationItem(string key, ZrdNode? values, string source, Action<long> reserve) : this(key, values, source) => this.reserve = reserve;
    public string Key { get; } = key;
    public ZrdNode? Values { get; } = values;
    /// <summary>The definition file the item came from, for diagnostics.</summary>
    public string Source { get; } = source;
    public IReadOnlyList<ZrdNode> Scalars
    {
        get
        {
            if (scalars != null) return scalars;
            if (Values == null) return [];
            reserve?.Invoke(32L + 8L * Values.Children.Count);
            var result = Values.Children.Where(c => c.Kind != ZrdKind.Array).ToArray();
            if (reserve != null) scalars = result;
            return result;
        }
    }
    public IEnumerable<AnimationItem> Items
    {
        get
        {
            if (Values == null) return [];
            if (reserve == null) return Parse(Values, Source);
            if (parsed == null)
            {
                reserve(32L + 16L * Values.Children.Count); // Growing list plus its final array, before enumeration.
                parsed = Parse(Values, Source, reserve).ToArray();
            }
            return parsed;
        }
    }
    public AnimationItem? Item(string key)
    {
        if (reserve == null) return Items.FirstOrDefault(i => i.Key == key);
        if (first == null)
        {
            _ = Items;
            reserve(64L + 64L * (parsed?.Count ?? 0));
            first = new(StringComparer.Ordinal);
            foreach (var item in parsed ?? []) first.TryAdd(item.Key, item);
        }
        return first.GetValueOrDefault(key);
    }
    public IEnumerable<AnimationItem> All(string key) => Items.Where(i => i.Key == key);
    public bool Has(string key) => Item(key) != null;
    public string Text(int index = 0)
    {
        var values = Scalars;
        if (index >= values.Count) throw Error($"{JsonData.ShownText(Key)} needs a value at position {index + 1}.");
        return values[index].Kind == ZrdKind.String ? values[index].Text : values[index].Value;
    }
    public string? TextOf(string key) => Item(key) is { } item && item.Scalars.Count > 0 ? item.Text() : null;
    public float Number(int index = 0)
    {
        var values = Scalars;
        if (index >= values.Count) throw Error($"{JsonData.ShownText(Key)} needs a number at position {index + 1}.");
        float result = values[index].Kind switch
        {
            ZrdKind.Float => BitConverter.UInt32BitsToSingle(values[index].Bits),
            ZrdKind.Int => unchecked((int)values[index].Bits),
            _ => throw Error($"{JsonData.ShownText(Key)} value {index + 1} ('{JsonData.ShownText(values[index].Text)}') is not a number."),
        };
        return float.IsFinite(result) ? result : throw Error($"{JsonData.ShownText(Key)} needs a finite number.");
    }
    public int Integer(int index = 0)
    {
        var values = Scalars;
        if (index >= values.Count) throw Error($"{JsonData.ShownText(Key)} needs a number at position {index + 1}.");
        return values[index].Kind switch
        {
            ZrdKind.Int => unchecked((int)values[index].Bits),
            ZrdKind.Float => IntegerFloat(BitConverter.UInt32BitsToSingle(values[index].Bits)),
            _ => throw Error($"{JsonData.ShownText(Key)} value {index + 1} ('{JsonData.ShownText(values[index].Text)}') is not a number."),
        };
        int IntegerFloat(float value) => float.IsFinite(value) && value >= int.MinValue && (double)value <= int.MaxValue
            ? (int)value : throw Error($"{JsonData.ShownText(Key)} needs a number within the signed 32-bit integer range.");
    }
    public InvalidDataException Error(string message) => new($"{JsonData.ShownText(Source)}: {message}");

    /// <summary>The items of a list: each string followed by a list is a keyword with values; a lone string is a bare keyword.</summary>
    public static IEnumerable<AnimationItem> Parse(ZrdNode list, string source) => Parse(list, source, null);
    private static IEnumerable<AnimationItem> Parse(ZrdNode list, string source, Action<long>? reserve)
    {
        var c = list.Children;
        for (int i = 0; i < c.Count; i++)
        {
            if (c[i].Kind != ZrdKind.String) continue;
            reserve?.Invoke(96); // The operation-owned item and its eventual cached lookup fields.
            ZrdNode? values = i + 1 < c.Count && c[i + 1].Kind == ZrdKind.Array ? c[i + 1] : null;
            yield return reserve == null ? new(c[i].Text, values, source) : new(c[i].Text, values, source, reserve);
            if (values != null) i++;
        }
    }
}

/// <summary>An <c>ANIMATION_DEFINITION</c>, the file it was read from and its position among that file's definitions.</summary>
public sealed record AnimationDefinition(AnimationItem Item, string File, int Ordinal);

/// <summary>
/// A mission's animation definitions as the original compiler read them: the root file (<c>data/mN/zrdr/anim.zad</c>)
/// sets the gravity and the search path for bare file names, and its <c>ANIMATION_LIST</c> names definition files
/// (depth first) and inline definitions, in the order their entries are compiled.
/// </summary>
public sealed class AnimationDefinitionSet
{
    /// <summary>
    /// Limits on the definition tree. A file may be listed more than once (each listing compiles its definitions again),
    /// so the total number of definition file reads is bounded as well as their depth.
    /// </summary>
    public const int MaximumFileDepth = 16, MaximumDefinitions = 20_000, MaximumFileReads = 10_000;
    /// <summary>Total definition and keyframe input per compilation, including repeated reads, before parsing/retention.</summary>
    public const long MaximumSourceBytes = 64L * 1024 * 1024;
    public const int MaximumSourceNodes = ZrdText.MaximumNodes;
    private int reads;
    private long sourceBytes;
    private int sourceNodes;
    private readonly long maximumSourceBytes;
    private readonly int maximumSourceNodes;
    public float Gravity { get; private set; } = -9.8f;
    /// <summary>Project folders searched for bare definition and keyframe script names, in order.</summary>
    public List<string> SearchPath { get; } = [];
    public List<AnimationDefinition> Definitions { get; } = [];
    /// <summary>Every definition and script file read, in the order the compiler read them.</summary>
    public List<string> Files { get; } = [];
    public List<string> Warnings { get; } = [];
    private void Warn(string message) { if (Warnings.Count < 2000) Warnings.Add(message); }
    private readonly IProjectFiles files;
    private readonly CancellationToken token;

    private AnimationDefinitionSet(IProjectFiles files, CancellationToken token, long maximumSourceBytes, int maximumSourceNodes)
    { this.files = files; this.token = token; this.maximumSourceBytes = maximumSourceBytes; this.maximumSourceNodes = maximumSourceNodes; }

    /// <summary>Reads the definitions reachable from <paramref name="root"/> (a project path).</summary>
    public static AnimationDefinitionSet Load(IProjectFiles files, string root, CancellationToken token = default)
        => Load(files, root, MaximumSourceBytes, MaximumSourceNodes, token);

    internal static AnimationDefinitionSet Load(IProjectFiles files, string root, long maximumSourceBytes, int maximumSourceNodes, CancellationToken token)
    {
        AnimationDefinitionSet set = new(files, token, maximumSourceBytes, maximumSourceNodes);
        set.Read(root, 0, true);
        return set;
    }

    private void Read(string path, int depth, bool root)
    {
        token.ThrowIfCancellationRequested();
        if (depth > MaximumFileDepth) throw new InvalidDataException($"{path}: definition files include each other more than {MaximumFileDepth} levels deep.");
        if (++reads > MaximumFileReads) throw new InvalidDataException($"{path}: the animation definitions list definition files more than {MaximumFileReads:N0} times.");
        Files.Add(path);
        var document = Parse(path); int ordinal = 0;
        foreach (var top in AnimationItem.Parse(document, path).Where(i => i.Key == "ANIMATION_DEFINITIONS"))
            foreach (var item in top.Items)
                switch (item.Key)
                {
                    case "GRAVITY" when root: Gravity = item.Number(); break;
                    case "ANIMATION_PATH" when root:
                        foreach (string part in item.Text().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            if (!part.Contains('\0') && WorldAssembler.ProjectPath(part) is { } folder) { if (!SearchPath.Contains(folder, StringComparer.OrdinalIgnoreCase)) SearchPath.Add(folder); }
                            else Warn($"{JsonData.ShownText(path)}: ANIMATION_PATH folder '{JsonData.ShownText(part).Replace('\0', '?')}' is outside the project and is not searched.");
                        break;
                    case "ANIMATION_LIST":
                        foreach (var entry in item.Items)
                        {
                            if (entry.Key == "ANIMATION_DEFINITION_FILE")
                            {
                                // The original compiler skipped files that did not exist (they have no source stamp).
                                string? file = Resolve(entry.Text(), path);
                                if (file == null) Warn($"{JsonData.ShownText(path)}: definition file {JsonData.ShownText(entry.Text())} does not exist; its animations are not compiled.");
                                else Read(file, depth + 1, false);
                            }
                            else if (entry.Key == "ANIMATION_DEFINITION")
                            {
                                if (Definitions.Count >= MaximumDefinitions) throw entry.Error($"more than {MaximumDefinitions} animation definitions.");
                                Definitions.Add(new(entry, path, ordinal++));
                            }
                        }
                        break;
                }
    }

    private ZrdNode Parse(string path)
    {
        var tree = ReadBytes(ReadSource(path), path, token);
        Count(tree);
        return Unwrap(tree);
        void Count(ZrdNode node)
        {
            token.ThrowIfCancellationRequested();
            if (++sourceNodes > maximumSourceNodes) throw new InvalidDataException($"{path}: animation definition files together exceed {maximumSourceNodes:N0} syntax nodes.");
            foreach (var child in node.Children) Count(child);
        }
    }

    private byte[] ReadSource(string path)
    {
        byte[] bytes = files.Read(path, token);
        if (bytes.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException($"{path} exceeds {SourceProject.MaximumSourceTextBytes / (1024 * 1024)} MiB.");
        if ((sourceBytes += bytes.Length) > maximumSourceBytes)
            throw new InvalidDataException($"{path}: animation definitions and keyframe scripts together exceed {maximumSourceBytes:N0} bytes; split the mission's animation sources.");
        return bytes;
    }

    /// <summary>A definition file's zReader tree as stored (text or compiled).</summary>
    public static ZrdNode Read(IProjectFiles files, string path, CancellationToken token)
        => ReadBytes(files.Read(path, token), path, token);

    private static ZrdNode ReadBytes(byte[] bytes, string path, CancellationToken token)
    {
        if (bytes.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException($"{path} exceeds {SourceProject.MaximumSourceTextBytes / (1024 * 1024)} MiB.");
        try { return ZrdText.LooksLikeText(bytes) ? ZrdText.Parse(bytes, token) : ZrdDecoder.Read(bytes, token); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{path}: {ex.Message}", ex); }
    }
    /// <summary>A source file lists one top-level array holding the keyword list.</summary>
    private static ZrdNode Unwrap(ZrdNode root)
    {
        while (root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array) root = root.Children[0];
        return root;
    }

    /// <summary>
    /// The extension of animation definition files: each mission's list (<c>data/mN/zrdr/anim.zad</c>) and the files it
    /// names. The original build kept them as <c>.zrd</c> beside the zReader resources and packed them into <c>zrdr.zbd</c>,
    /// although the game never reads them; a separate extension keeps them out of the archives.
    /// </summary>
    public const string Extension = ".zad";

    /// <summary>Whether a zReader tree holds animation definitions (a top-level <c>ANIMATION_DEFINITIONS</c>).</summary>
    public static bool HoldsDefinitions(ZrdNode root)
    {
        var body = Unwrap(root).Children;
        for (int i = 0; i + 1 < body.Count; i++)
            if (body[i] is { Kind: ZrdKind.String, Text: "ANIMATION_DEFINITIONS" } && body[i + 1].Kind == ZrdKind.Array) return true;
        return false;
    }

    /// <summary>
    /// <paramref name="root"/> split into its animation definitions, with the definition files they list renamed to
    /// <see cref="Extension"/>, and everything else (null when nothing else remains, as in every shipped definition file
    /// but <c>pickup.zrd</c>), both wrapped as the file was.
    /// </summary>
    public static (ZrdNode Definitions, ZrdNode? Others) Split(ZrdNode root)
    {
        var body = Unwrap(root).Children;
        List<ZrdNode> definitions = [], rest = [];
        for (int i = 0; i < body.Count; i++)
            if (body[i] is { Kind: ZrdKind.String, Text: "ANIMATION_DEFINITIONS" } && i + 1 < body.Count && body[i + 1].Kind == ZrdKind.Array)
            {
                definitions.Add(body[i]); definitions.Add(Renamed(body[i + 1], 0)); i++;
            }
            else rest.Add(body[i]);
        if (definitions.Count == 0) throw new InvalidDataException("The file holds no animation definitions.");
        return (Rewrap(root, definitions), rest.Count == 0 ? null : Rewrap(root, rest));

        static ZrdNode Rewrap(ZrdNode node, List<ZrdNode> children) =>
            node.Children.Count == 1 && node.Children[0].Kind == ZrdKind.Array ? node with { Children = [Rewrap(node.Children[0], children)] } : node with { Children = children };
        // ANIMATION_DEFINITION_FILE ( name ): a listed .zrd becomes .zad, whatever its folder.
        static ZrdNode Renamed(ZrdNode node, int depth)
        {
            if (node.Kind != ZrdKind.Array) return node;
            var children = node.Children.ToList(); bool changed = false;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is { Kind: ZrdKind.String, Text: "ANIMATION_DEFINITION_FILE" } && i + 1 < children.Count
                    && children[i + 1].Children is [{ Kind: ZrdKind.String } name, ..] value
                    && name.Text.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase))
                {
                    children[i + 1] = children[i + 1] with { Children = [name with { Text = name.Text[..^4] + Extension }, .. value.Skip(1)] };
                    changed = true; i++;
                    continue;
                }
                var next = Renamed(children[i], depth + 1);
                if (!ReferenceEquals(next, children[i])) { children[i] = next; changed = true; }
            }
            return changed ? node with { Children = children } : node;
        }
    }

    /// <summary>
    /// <paramref name="root"/> (a file's tree) with its <paramref name="ordinal"/>-th animation definition's items
    /// replaced by <paramref name="items"/>.
    /// </summary>
    public static ZrdNode ReplaceDefinition(ZrdNode root, int ordinal, ZrdNode items)
    {
        int seen = 0; bool done = false;
        var result = Walk(root, 0);
        if (!done) throw new InvalidDataException($"The file has no animation definition {ordinal + 1}.");
        return result;
        ZrdNode Walk(ZrdNode node, int level)
        {
            if (node.Kind != ZrdKind.Array || level > 8) return node;
            var children = node.Children.ToList(); bool changed = false;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].Kind == ZrdKind.String && i + 1 < children.Count && children[i + 1].Kind == ZrdKind.Array)
                {
                    string key = children[i].Text;
                    if (key == "ANIMATION_DEFINITION" && level > 0)
                    {
                        if (seen++ == ordinal) { children[i + 1] = items; changed = done = true; }
                    }
                    else if (key is "ANIMATION_DEFINITIONS" or "ANIMATION_LIST") { var next = Walk(children[i + 1], level + 1); if (!ReferenceEquals(next, children[i + 1])) { children[i + 1] = next; changed = true; } }
                    i++;
                }
                else if (children[i].Kind == ZrdKind.Array && level == 0) { var next = Walk(children[i], 0); if (!ReferenceEquals(next, children[i])) { children[i] = next; changed = true; } }
            }
            return changed ? node with { Children = children } : node;
        }
    }

    /// <summary>
    /// A file named in a definition: a path (<c>..\data\…</c>, relative to the gamegen folder) or a bare name, which is
    /// looked for beside the naming file and then in the animation path folders. A path that does not exist falls back
    /// to its file name, as the shipped build found <c>m1\zrdr\start_north.zrd</c> in <c>envmodels</c>.
    /// </summary>
    public string? Resolve(string name, string from)
    {
        if (name.Contains('\0')) throw new InvalidDataException($"{JsonData.ShownText(from)}: '{JsonData.ShownText(name).Replace('\0', '?')}' is not a file name.");
        string normalized = name.Replace('\\', '/');
        if (normalized.Contains('/'))
        {
            // The shipped root definitions doubled some separators; the file system ignores empty segments.
            while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");
            string? project = WorldAssembler.ProjectPath(normalized);
            if (project != null && files.Exists(project)) return project;
            normalized = normalized[(normalized.LastIndexOf('/') + 1)..];
        }
        if (normalized.Length == 0 || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException($"{JsonData.ShownText(from)}: '{JsonData.ShownText(name)}' is not a file name.");
        string beside = Path.GetDirectoryName(from)!.Replace('\\', '/');
        return new[] { beside }.Concat(SearchPath).Select(folder => $"{folder}/{normalized}").FirstOrDefault(files.Exists);
    }

    /// <summary>A keyframe script's bytes (resolved like definition files, and limited like every text source), recorded as an input.</summary>
    public (string Path, byte[] Bytes)? ReadScript(string name, string from)
    {
        string? path = Resolve(name, from);
        if (path == null) return null;
        Files.Add(path);
        byte[] bytes = ReadSource(path);
        return (path, bytes);
    }
}
