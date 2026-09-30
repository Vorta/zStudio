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
    public string Key { get; } = key;
    public ZrdNode? Values { get; } = values;
    /// <summary>The definition file the item came from, for diagnostics.</summary>
    public string Source { get; } = source;
    public IReadOnlyList<ZrdNode> Scalars => Values?.Children.Where(c => c.Kind != ZrdKind.Array).ToArray() ?? [];
    public IEnumerable<AnimationItem> Items => Values == null ? [] : Parse(Values, Source);
    public AnimationItem? Item(string key) => Items.FirstOrDefault(i => i.Key == key);
    public IEnumerable<AnimationItem> All(string key) => Items.Where(i => i.Key == key);
    public bool Has(string key) => Items.Any(i => i.Key == key);
    public string Text(int index = 0)
    {
        var values = Scalars;
        if (index >= values.Count) throw Error($"{Key} needs a value at position {index + 1}.");
        return values[index].Kind == ZrdKind.String ? values[index].Text : values[index].Value;
    }
    public string? TextOf(string key) => Item(key) is { } item && item.Scalars.Count > 0 ? item.Text() : null;
    public float Number(int index = 0)
    {
        var values = Scalars;
        if (index >= values.Count) throw Error($"{Key} needs a number at position {index + 1}.");
        return values[index].Kind switch
        {
            ZrdKind.Float => BitConverter.UInt32BitsToSingle(values[index].Bits),
            ZrdKind.Int => unchecked((int)values[index].Bits),
            _ => throw Error($"{Key} value {index + 1} ('{values[index].Text}') is not a number."),
        };
    }
    public int Integer(int index = 0)
    {
        var values = Scalars;
        if (index >= values.Count) throw Error($"{Key} needs a number at position {index + 1}.");
        return values[index].Kind switch
        {
            ZrdKind.Int => unchecked((int)values[index].Bits),
            ZrdKind.Float => (int)BitConverter.UInt32BitsToSingle(values[index].Bits),
            _ => throw Error($"{Key} value {index + 1} ('{values[index].Text}') is not a number."),
        };
    }
    public InvalidDataException Error(string message) => new($"{Source}: {message}");

    /// <summary>The items of a list: each string followed by a list is a keyword with values; a lone string is a bare keyword.</summary>
    public static IEnumerable<AnimationItem> Parse(ZrdNode list, string source)
    {
        var c = list.Children;
        for (int i = 0; i < c.Count; i++)
        {
            if (c[i].Kind != ZrdKind.String) continue;
            if (i + 1 < c.Count && c[i + 1].Kind == ZrdKind.Array) { yield return new(c[i].Text, c[i + 1], source); i++; }
            else yield return new(c[i].Text, null, source);
        }
    }
}

/// <summary>An <c>ANIMATION_DEFINITION</c>, the file it was read from and its position among that file's definitions.</summary>
public sealed record AnimationDefinition(AnimationItem Item, string File, int Ordinal);

/// <summary>
/// A mission's animation definitions as the original compiler read them: the root file (<c>data/mN/zrdr/anim.zrd</c>)
/// sets the gravity and the search path for bare file names, and its <c>ANIMATION_LIST</c> names definition files
/// (depth first) and inline definitions, in the order their entries are compiled.
/// </summary>
public sealed class AnimationDefinitionSet
{
    public const int MaximumFileDepth = 16, MaximumDefinitions = 20_000;
    public float Gravity { get; private set; } = -9.8f;
    /// <summary>Project folders searched for bare definition and keyframe script names, in order.</summary>
    public List<string> SearchPath { get; } = [];
    public List<AnimationDefinition> Definitions { get; } = [];
    /// <summary>Every definition and script file read, in the order the compiler read them.</summary>
    public List<string> Files { get; } = [];
    public List<string> Warnings { get; } = [];
    private readonly IProjectFiles files;
    private readonly CancellationToken token;

    private AnimationDefinitionSet(IProjectFiles files, CancellationToken token) { this.files = files; this.token = token; }

    /// <summary>Reads the definitions reachable from <paramref name="root"/> (a project path).</summary>
    public static AnimationDefinitionSet Load(IProjectFiles files, string root, CancellationToken token = default)
    {
        AnimationDefinitionSet set = new(files, token);
        set.Read(root, 0, true);
        return set;
    }

    private void Read(string path, int depth, bool root)
    {
        token.ThrowIfCancellationRequested();
        if (depth > MaximumFileDepth) throw new InvalidDataException($"{path}: definition files include each other more than {MaximumFileDepth} levels deep.");
        Files.Add(path);
        var document = Parse(path); int ordinal = 0;
        foreach (var top in AnimationItem.Parse(document, path).Where(i => i.Key == "ANIMATION_DEFINITIONS"))
            foreach (var item in top.Items)
                switch (item.Key)
                {
                    case "GRAVITY" when root: Gravity = item.Number(); break;
                    case "ANIMATION_PATH" when root:
                        foreach (string part in item.Text().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            if (WorldAssembler.ProjectPath(part) is { } folder && !SearchPath.Contains(folder, StringComparer.OrdinalIgnoreCase)) SearchPath.Add(folder);
                            else if (WorldAssembler.ProjectPath(part) == null) Warnings.Add($"{path}: ANIMATION_PATH folder '{part}' is outside the project and is not searched.");
                        break;
                    case "ANIMATION_LIST":
                        foreach (var entry in item.Items)
                        {
                            if (entry.Key == "ANIMATION_DEFINITION_FILE")
                            {
                                // The original compiler skipped files that did not exist (they have no source stamp).
                                string? file = Resolve(entry.Text(), path);
                                if (file == null) Warnings.Add($"{path}: definition file {entry.Text()} does not exist; its animations are not compiled.");
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

    private ZrdNode Parse(string path) => Unwrap(Read(files, path, token));

    /// <summary>A definition file's zReader tree as stored (text or compiled).</summary>
    public static ZrdNode Read(IProjectFiles files, string path, CancellationToken token)
    {
        byte[] bytes = files.Read(path, token);
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
        string normalized = name.Replace('\\', '/');
        if (normalized.Contains('/'))
        {
            // The shipped root definitions doubled some separators; the file system ignores empty segments.
            while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");
            string? project = WorldAssembler.ProjectPath(normalized);
            if (project != null && files.Exists(project)) return project;
            normalized = normalized[(normalized.LastIndexOf('/') + 1)..];
        }
        if (normalized.Length == 0 || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException($"{from}: '{name}' is not a file name.");
        string beside = Path.GetDirectoryName(from)!.Replace('\\', '/');
        return new[] { beside }.Concat(SearchPath).Select(folder => $"{folder}/{normalized}").FirstOrDefault(files.Exists);
    }

    /// <summary>A keyframe script's bytes (resolved like definition files), recorded as an input.</summary>
    public (string Path, byte[] Bytes)? ReadScript(string name, string from)
    {
        string? path = Resolve(name, from);
        if (path == null) return null;
        Files.Add(path);
        return (path, files.Read(path, token));
    }
}
