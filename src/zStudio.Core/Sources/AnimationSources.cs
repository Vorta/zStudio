using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Reconstructs the animation sources of a mission's shipped <c>anim.zbd</c>. The definitions (<c>.zrd</c>) ship in the
/// resource archives, but the keyframe scripts (<c>.zan</c>) they name were compiled away: each script event's
/// keyframes become the track of its object in the named script, written where the file's source stamps say the
/// original was (or beside its definition). Some shipped definitions changed after the animations were last compiled;
/// a definition that does not compile to the shipped entries is replaced by the definition decompiled from them, so
/// the sources rebuild the animations the game shipped.
/// </summary>
internal static class AnimationSources
{
    internal sealed record MissionAnimation(int Mission, AnimationPackage Package, IReadOnlyList<string> Stamps, IReadOnlyCollection<string> WorldNodes);

    /// <summary>The files written so far over the project on disk.</summary>
    private sealed class Overlay(IProjectFiles project) : IProjectFiles
    {
        public Dictionary<string, byte[]> Written { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string relative) => Written.ContainsKey(relative) || project.Exists(relative);
        public byte[] Read(string relative, CancellationToken token) => Written.TryGetValue(relative, out var bytes) ? bytes : project.Read(relative, token);
    }

    public static List<WorldSources.Output> Reconstruct(IReadOnlyList<MissionAnimation> missions, IProjectFiles project, List<string> notes, CancellationToken token)
    {
        Overlay files = new(project);
        HashSet<(string, int)> attempted = [];
        // Script path → object → track text, in the order first seen.
        Dictionary<string, List<(string Object, string Track)>> scripts = new(StringComparer.OrdinalIgnoreCase);
        foreach (var mission in missions)
        {
            token.ThrowIfCancellationRequested();
            string root = $"data/m{mission.Mission}/zrdr/anim.zrd";
            if (!files.Exists(root)) { notes.Add($"m{mission.Mission}: anim.zbd has no definitions ({root}); its animation sources were not reconstructed."); continue; }
            AnimationDefinitionSet set;
            try { set = AnimationDefinitionSet.Load(files, root, token); }
            catch (InvalidDataException ex) { notes.Add($"m{mission.Mission}: the animation definitions do not load ({ex.Message}); animation sources were not reconstructed."); continue; }
            var bindings = Bindings(set, mission);
            if (bindings == null) { notes.Add($"m{mission.Mission}: the definitions do not list anim.zbd's animations in order; animation sources were not reconstructed."); continue; }

            // Keyframe scripts: the track each script event plays.
            Dictionary<string, string> stamped = new(StringComparer.OrdinalIgnoreCase);
            foreach (string stamp in mission.Stamps)
            {
                string normalized = stamp.Replace('\\', '/'); while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");
                if (normalized.EndsWith(AnimationScript.Extension, StringComparison.OrdinalIgnoreCase) && WorldAssembler.ProjectPath(normalized) is { } path)
                    stamped.TryAdd(Path.GetFileName(path), path);
            }
            HashSet<string> touched = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (definition, digits, entry) in bindings)
                foreach (var (item, ev) in ScriptEvents(definition.Item, entry))
                {
                    string file = AnimationCompiler.Bind(item.TextOf("SCRIPT_FILENAME") ?? "", digits), target = AnimationCompiler.Bind(item.TextOf("NAME") ?? "", digits);
                    float rate = item.Item("SCRIPT_FRAME_RATE")?.Number() ?? 30;
                    if (file.Length == 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || target.Length == 0) continue;
                    string? track;
                    try { track = AnimationScript.Decompile(ev.Keyframes(token), rate); }
                    catch (InvalidDataException) { track = null; }
                    if (track == null) { notes.Add($"m{mission.Mission}: {entry.Name} moves {target} with keyframes that are not on the {rate}/s frame grid of {file}; the track was not reconstructed."); continue; }
                    string path = stamped.GetValueOrDefault(file) ?? $"{Path.GetDirectoryName(definition.File)!.Replace('\\', '/')}/{file}";
                    var tracks = scripts.TryGetValue(path, out var list) ? list : scripts[path] = [];
                    int existing = tracks.FindIndex(t => t.Object == target);
                    if (existing < 0) { tracks.Add((target, track)); touched.Add(path); }
                    else if (tracks[existing].Track != track) notes.Add($"{path}: {entry.Name} moves {target} differently from an earlier animation using the same script; the first track was kept.");
                }
            foreach (string path in touched) files.Written[path] = Encoding.ASCII.GetBytes(AnimationScript.Write(scripts[path]));

            RepairDefinitions(mission, bindings, files, attempted, notes, token);
        }
        return files.Written.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => new WorldSources.Output(w.Key, w.Value)).ToList();
    }

    /// <summary>Each shipped entry with the definition (and pattern digits) it was compiled from, or null when they do not line up.</summary>
    private static List<(AnimationDefinition Definition, string Digits, AnimationEntry Entry)>? Bindings(AnimationDefinitionSet set, MissionAnimation mission)
    {
        List<(AnimationDefinition, string, AnimationEntry)> result = []; int index = 1;
        foreach (var definition in set.Definitions)
            foreach (var (root, digits) in AnimationCompiler.Roots(definition.Item, mission.WorldNodes, _ => { }))
            {
                if (!mission.WorldNodes.Contains(root)) continue;
                if (index >= mission.Package.Entries.Count) return null;
                var entry = mission.Package.Entries[index++];
                if (entry.Name != AnimationCompiler.Bind(definition.Item.TextOf("ANIMATION_NAME") ?? root, digits) || entry.RootName != root) return null;
                result.Add((definition, digits, entry));
            }
        return index == mission.Package.Entries.Count ? result : null;
    }

    /// <summary>The definition's script items paired with the entry's keyframe events, sequence by sequence.</summary>
    private static IEnumerable<(AnimationItem Item, AnimationEvent Event)> ScriptEvents(AnimationItem definition, AnimationEntry entry)
    {
        List<IEnumerable<AnimationItem>> items = [definition.Item("RESET_STATE")?.Items ?? [], .. definition.All("SEQUENCE_DEFINITION").Select(s => s.Items)];
        var compiled = entry.AllSequences.ToList();
        for (int s = 0; s < items.Count && s < compiled.Count; s++)
        {
            var events = compiled[s].Events.Where(e => e.Type == 12).ToList();
            var scriptItems = items[s].Where(i => i.Key == "OBJECT_MOTION_SI_SCRIPT").ToList();
            for (int k = 0; k < scriptItems.Count && k < events.Count; k++) yield return (scriptItems[k], events[k]);
        }
    }

    /// <summary>
    /// Replaces each definition that does not compile to its shipped entries with the definition decompiled from them.
    /// A replacement is kept only when every entry it produces then compiles exactly.
    /// </summary>
    private static void RepairDefinitions(MissionAnimation mission, List<(AnimationDefinition Definition, string Digits, AnimationEntry Entry)> bindings,
        Overlay files, HashSet<(string, int)> attempted, List<string> notes, CancellationToken token)
    {
        string root = $"data/m{mission.Mission}/zrdr/anim.zrd";
        AnimationPackage Compile() => AnimationCompiler.Compile(files, root, mission.WorldNodes, token).Package;
        AnimationPackage built;
        try { built = Compile(); }
        catch (InvalidDataException ex) { notes.Add($"m{mission.Mission}: the reconstructed animations do not compile ({ex.Message})."); return; }
        if (built.Entries.Count != mission.Package.Entries.Count) { notes.Add($"m{mission.Mission}: the reconstructed animations compile to {built.Entries.Count - 1} entries, not {mission.Package.Entries.Count - 1}."); return; }
        var differing = Enumerable.Range(1, bindings.Count).Where(i => AnimationComparer.Difference(mission.Package.Entries[i], built.Entries[i]) != null).ToList();
        foreach (var group in differing.GroupBy(i => (bindings[i - 1].Definition.File, bindings[i - 1].Definition.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            var definition = bindings[group.First() - 1].Definition;
            // A definition shared by several missions is rebuilt once; a failed attempt is not repeated.
            if (!attempted.Add((definition.File, definition.Ordinal))) continue;
            // Every entry of the definition must agree on one decompiled definition.
            var members = Enumerable.Range(1, bindings.Count).Where(i => bindings[i - 1].Definition == definition).ToList();
            string? text = null; ZrdNode? items = null;
            try
            {
                foreach (int i in members)
                {
                    var (_, digits, entry) = bindings[i - 1];
                    var candidate = Generalize(Decompile(definition.Item, entry), definition.Item, digits);
                    string candidateText = ZrdText.Write(new ZrdNode(Guid.Empty, ZrdKind.Array, 0, "", [candidate]), token);
                    if (text != null && text != candidateText) throw new InvalidDataException("its animations decompile to different definitions");
                    text = candidateText; items = candidate;
                }
            }
            catch (InvalidDataException ex) { notes.Add($"{definition.File}: {Name(definition)} does not compile to the shipped animation and could not be rebuilt from it ({ex.Message}); it was kept as shipped."); continue; }
            byte[] original = files.Read(definition.File, token);
            var tree = AnimationDefinitionSet.ReplaceDefinition(AnimationDefinitionSet.Read(files, definition.File, token), definition.Ordinal, items!);
            files.Written[definition.File] = Encoding.ASCII.GetBytes(ZrdText.Write(tree, token));
            string? remaining;
            try
            {
                built = Compile();
                remaining = built.Entries.Count != mission.Package.Entries.Count ? $"{built.Entries.Count - 1} animations"
                    : members.Select(i => AnimationComparer.Difference(mission.Package.Entries[i], built.Entries[i])).FirstOrDefault(d => d != null);
            }
            catch (InvalidDataException ex) { remaining = ex.Message; }
            bool exact = remaining == null;
            if (exact) notes.Add($"{definition.File}: {Name(definition)} was changed after the shipped animations were compiled; it was rebuilt from anim.zbd.");
            else
            {
                files.Written[definition.File] = original;
                notes.Add($"{definition.File}: {Name(definition)} does not compile to the shipped animation, and the definition rebuilt from it did not either ({remaining}); it was kept as shipped.");
            }
        }
    }

    private static string Name(AnimationDefinition definition) => definition.Item.TextOf("ANIMATION_NAME") ?? definition.Item.TextOf("NAME") ?? "(unnamed)";

    /// <summary>The entry decompiled, its keyframe events naming the script items of the original definition.</summary>
    private static ZrdNode Decompile(AnimationItem original, AnimationEntry entry)
    {
        Dictionary<AnimationEvent, AnimationItem> pairs = new(ReferenceEqualityComparer.Instance);
        foreach (var (item, ev) in ScriptEvents(original, entry)) pairs[ev] = item;
        return AnimationDecompiler.Definition(entry, ev =>
        {
            if (!pairs.TryGetValue(ev, out var item)) throw new InvalidDataException("a keyframe event has no script in the definition");
            return (item.TextOf("SCRIPT_FILENAME") ?? throw new InvalidDataException("a script event names no file"), item.Item("SCRIPT_FRAME_RATE")?.Number() ?? 30);
        });
    }

    /// <summary>
    /// A decompiled definition written like the original: names that the original gave as patterns go back to their
    /// patterns, and a NAME listing several roots keeps its list.
    /// </summary>
    private static ZrdNode Generalize(ZrdNode items, AnimationItem original, string digits)
    {
        HashSet<string> patterns = [];
        void Collect(ZrdNode node) { if (node.Kind == ZrdKind.String && node.Text.Contains('*')) patterns.Add(node.Text); foreach (var c in node.Children) Collect(c); }
        if (original.Values != null) Collect(original.Values);
        Dictionary<string, string> bound = new(StringComparer.Ordinal);
        foreach (string pattern in patterns) bound.TryAdd(AnimationCompiler.Bind(pattern, digits), pattern);
        ZrdNode Map(ZrdNode node) => node.Kind == ZrdKind.String && bound.TryGetValue(node.Text, out var pattern) ? node with { Text = pattern }
            : node.Kind == ZrdKind.Array ? node with { Children = node.Children.Select(Map).ToArray() } : node;
        var result = Map(items);
        if (original.Item("NAME") is { } name && name.Scalars.Count > 1 && name.Values != null)
        {
            var children = result.Children.ToList();
            int at = children.FindIndex(c => c.Kind == ZrdKind.String && c.Text == "NAME");
            if (at >= 0 && at + 1 < children.Count) children[at + 1] = name.Values;
            result = result with { Children = children };
        }
        return result;
    }
}
