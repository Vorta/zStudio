using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Reconstructs the animation sources of a mission's shipped <c>anim.zbd</c>. The definitions (<c>.zrd</c>) ship in the
/// resource archives, but the keyframe scripts (<c>.zan</c>) they name were compiled away: each script event's
/// keyframes become the track of its object in the named script, written where the file's source stamps say the
/// original was (or beside its definition), as the SI Animation Script the original was (<see cref="SiScriptWriter"/>).
/// Some archived definitions are an older text version than the shipped compiled animations; a definition that does
/// not compile to the shipped entries is replaced by the definition decompiled from them, so the sources rebuild the
/// animations the game shipped.
/// </summary>
internal static class AnimationSources
{
    internal const long MaximumRepairWork = 4L * 1024 * 1024 * 1024;
    internal sealed record MissionAnimation(int Mission, AnimationPackage Package, IReadOnlyList<(string Path, uint Time)> Stamps, IReadOnlyCollection<string> WorldNodes);

    /// <summary>An object's track in a script: the compiled keyframes, their frame rate, and the keyframe-format text.</summary>
    private sealed record ScriptTrack(string Object, List<AnimationKeyframe> Frames, float Rate, string Text);

    private sealed class ScriptTracks
    {
        public List<ScriptTrack> Ordered { get; } = [];
        public Dictionary<string, ScriptTrack> ByObject { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>All repair compilations share an allowance for repeated source decoding and compiled-package work.</summary>
    internal sealed class RepairWork(long maximum)
    {
        private long used;
        internal long Remaining => maximum - used;
        internal IOException Exceeded(Exception? inner = null) => new($"Animation reconstruction exceeds its {maximum:N0}-unit definition-repair work limit; reconstruct from the original game files or split the animation definitions into smaller missions.", inner);
        public void Charge(long count)
        {
            if (count < 0 || count > maximum - used)
                throw Exceeded();
            used += count;
        }
    }
    internal sealed class RepairFiles(IProjectFiles files, RepairWork work) : IProjectFiles
    {
        public bool Exists(string relative) => files.Exists(relative);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = files.Read(relative, token, limits.WithMaximum(work.Remaining)); }
            catch (InvalidDataException ex) when (work.Remaining < limits.MaximumBytes) { throw work.Exceeded(ex); }
            work.Charge(bytes.LongLength); // Before the compiler decodes/clones this source, on every compilation.
            return bytes;
        }
    }

    /// <summary>The files written so far over the project on disk.</summary>
    internal sealed class Overlay(IProjectFiles project, ReconstructionBudget budget) : IProjectFiles
    {
        public Dictionary<string, byte[]> Written { get; } = new(StringComparer.OrdinalIgnoreCase);
        public void Write(string path, string text, Encoding encoding)
        {
            budget.Retain(encoding.GetByteCount(text));
            Written[path] = encoding.GetBytes(text);
        }
        public bool Exists(string relative) => Written.ContainsKey(relative) || project.Exists(relative);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested();
            if (!Written.TryGetValue(relative, out var bytes)) return project.Read(relative, token, limits);
            limits.Validate(bytes);
            return bytes;
        }
    }

    /// <param name="status">Told what is being done: each keyframe script as it starts being written (from worker threads),
    /// then each mission's check of its rebuilt animations.</param>
    public static List<WorldSources.Output> Reconstruct(IReadOnlyList<MissionAnimation> missions, IProjectFiles project, List<string> notes, CancellationToken token, Action<SourceStage, string>? status = null, long maximumRetainedBytes = ReconstructionBudget.MaximumBytes, long maximumRepairWork = MaximumRepairWork,
        int maximumDefinitionNodes = ZrdText.MaximumNodes)
    {
        ReconstructionBudget budget = new(maximumRetainedBytes);
        AnimationDefinitionBudget definitionBudget = new(token, budget.Retain, maximumDefinitionNodes);
        RepairWork repairWork = new(maximumRepairWork);
        Overlay files = new(project, budget);
        HashSet<(string, int)> attempted = [];
        // Script path → its object tracks in the order first seen, and the time its stamp records.
        Dictionary<string, ScriptTracks> scripts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, uint> times = new(StringComparer.OrdinalIgnoreCase);
        // A script that several missions use is written again when one adds to it: only its last note counts.
        Dictionary<string, string?> scriptNotes = new(StringComparer.OrdinalIgnoreCase); List<string> noted = [];
        foreach (var mission in missions)
        {
            token.ThrowIfCancellationRequested();
            string root = SourceBuilder.AnimationRoot($"m{mission.Mission}");
            if (!files.Exists(root)) { notes.Add($"m{mission.Mission}: anim.zbd has no definitions ({root}); its animation sources were not reconstructed."); continue; }
            AnimationDefinitionSet set;
            try { set = AnimationDefinitionSet.Load(files, root, token); }
            catch (InvalidDataException ex) { notes.Add($"m{mission.Mission}: the animation definitions do not load ({ex.Message}); animation sources were not reconstructed."); continue; }
            var bindings = Bindings(set, mission, token);
            if (bindings == null) { notes.Add($"m{mission.Mission}: the definitions do not list anim.zbd's animations in order; animation sources were not reconstructed."); continue; }

            // Keyframe scripts: the track each script event plays.
            Dictionary<string, (string Path, uint Time)> stamped = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (stamp, time) in mission.Stamps)
            {
                string normalized = stamp.Replace('\\', '/'); while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");
                if (normalized.EndsWith(AnimationScript.Extension, StringComparison.OrdinalIgnoreCase) && WorldAssembler.ProjectPath(normalized) is { } path)
                    stamped.TryAdd(Path.GetFileName(path), (path, time));
            }
            HashSet<string> touched = new(StringComparer.OrdinalIgnoreCase); List<string> order = [];
            void Touch(string path) { if (touched.Add(path)) order.Add(path); }
            foreach (var (definition, digits, entry) in bindings)
                foreach (var (item, ev) in ScriptEvents(definition.Item, entry, token))
                {
                    string file = AnimationCompiler.Bind(item.TextOf("SCRIPT_FILENAME") ?? "", digits), target = AnimationCompiler.Bind(item.TextOf("NAME") ?? "", digits);
                    float rate = item.Item("SCRIPT_FRAME_RATE")?.Number() ?? 30;
                    if (file.Length == 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || target.Length == 0) continue;
                    // OBJECT lines separate names with whitespace and comments start with #.
                    if (!AnimationScript.IsObjectName(target)) { notes.Add($"m{mission.Mission}: {entry.Name} moves {target}, which a keyframe script cannot name; the track was not reconstructed."); continue; }
                    string? track; List<AnimationKeyframe> frames;
                    try
                    {
                        var keys = ev.Keyframes(token);
                        budget.Retain(256L * keys.Count); // Frame objects, copied channel data and their collection.
                        frames = [.. keys];
                    }
                    catch (InvalidDataException) { frames = []; }
                    // Decoding failures may omit a malformed track. Capacity refusal must abort reconstruction,
                    // not masquerade as an off-grid track; the writer reserves its builder/string before allocation.
                    track = AnimationScript.Decompile(frames, rate, token, SourceProject.MaximumSourceTextBytes, budget.Retain);
                    if (track == null) { notes.Add($"m{mission.Mission}: {entry.Name} moves {target} with keyframes that are not on the {rate}/s frame grid of {file}; the track was not reconstructed."); continue; }
                    string path = stamped.TryGetValue(file, out var stamp) ? stamp.Path : $"{Path.GetDirectoryName(definition.File)!.Replace('\\', '/')}/{file}";
                    // A stamp seen only by a later mission still dates a script written before.
                    if (stamped.ContainsKey(file) && times.TryAdd(path, stamp.Time) && scripts.ContainsKey(path)) Touch(path);
                    var tracks = scripts.TryGetValue(path, out var list) ? list : scripts[path] = new();
                    if (!tracks.ByObject.TryGetValue(target, out var existing))
                    {
                        budget.Retain(128L + 2L * target.Length); // Ordered identity record and dictionary entry.
                        ScriptTrack added = new(target, frames, rate, track);
                        tracks.ByObject.Add(target, added); tracks.Ordered.Add(added); Touch(path);
                    }
                    else if (existing.Rate != rate) notes.Add($"{path}: {entry.Name} plays {target} at {rate} frames per second, an earlier animation at {existing.Rate}; the script was written from the earlier one.");
                    else if (existing.Text != track) notes.Add($"{path}: {entry.Name} moves {target} differently from an earlier animation using the same script; the first track was kept.");
                }
            // Scripts are independent and their rotation searches are the longest work of a reconstruction, so they are
            // written in parallel; outputs and notes keep the order the scripts were first seen. Scripts are read as
            // Latin-1, like the names they hold.
            var written = new (string Text, string? Note)[order.Count];
            try
            {
                Parallel.For(0, order.Count, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 4 }, i =>
                {
                    status?.Invoke(SourceStage.Reconstructing, $"keyframe script {order[i]}");
                    var result = WriteScript(order[i], scripts[order[i]], times.TryGetValue(order[i], out uint t) ? t : null, token, budget.Retain);
                    written[i] = result;
                });
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            }
            for (int i = 0; i < order.Count; i++)
            {
                files.Write(order[i], written[i].Text, Encoding.Latin1);
                if (scriptNotes.TryAdd(order[i], written[i].Note)) noted.Add(order[i]); else scriptNotes[order[i]] = written[i].Note;
            }

            status?.Invoke(SourceStage.Validating, $"the m{mission.Mission} animations");
            RepairDefinitions(mission, bindings, files, attempted, notes, repairWork, definitionBudget, token);
        }
        notes.AddRange(noted.Select(path => scriptNotes[path]).OfType<string>());
        return files.Written.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => new WorldSources.Output(w.Key, w.Value)).ToList();
    }

    /// <summary>
    /// A script's text: the SI Animation Script its tracks compile from exactly, or zStudio's keyframe format (noted)
    /// when the keyframes have no such script. A script the shipped file's stamps record (its time) is written as the
    /// original's exporter wrote it, with the Softimage DKit messages of its date; scripts of other files (zStudio's
    /// exports carry no stamps) hold only their frames.
    /// </summary>
    private static (string Text, string? Note) WriteScript(string path, ScriptTracks tracks, uint? time, CancellationToken token, Action<long> reserveText)
    {
        var objects = tracks.Ordered.Select(t => t.Object).ToList();
        var ordered = SourceOrder(objects).Select(name => tracks.ByObject[name]).ToList();
        SiScriptWriter.Layout layout = new(Version(time), !MessagesFollowFrames(path, time));
        try { return (SiScriptWriter.Write([.. ordered.Select(t => new SiScriptWriter.Track(t.Object, t.Frames, t.Rate))], layout, token, reserveText: reserveText), null); }
        catch (InvalidDataException ex)
        {
            string text = AnimationScript.Write(tracks.Ordered.Select(t => (t.Object, t.Text)), token, reserveText);
            var parsed = AnimationScript.Parse(Encoding.Latin1.GetBytes(text), path, token);
            AnimationCompileWork work = new(token);
            foreach (var track in tracks.Ordered)
            {
                token.ThrowIfCancellationRequested();
                var keys = AnimationScript.Track(parsed, track.Object) ?? throw new InvalidDataException($"{path}: a reconstructed track is missing.");
                var compiled = AnimationScript.Compile(keys, track.Rate, path, token, work);
                if (compiled.Count != track.Frames.Count || !compiled.Zip(track.Frames).All(pair => SiScriptWriter.Same(pair.First, pair.Second)))
                    throw new InvalidDataException($"{path}: the reconstructed keyframe script does not compile back exactly.");
            }
            return (text,
                $"{path}: the keyframes have no SI Animation Script ({ex.Message}); it was written in zStudio's keyframe format.");
        }
    }

    /// <summary>
    /// The order the scene listed its objects in. The exit scripts' objects were compiled vtol1, cargodoor, rengine,
    /// lengine, but the surviving fragments of their texts list them vtol1, lengine, rengine, cargodoor (seven of the ten
    /// show it; the pickup and dropoff scripts are compiled in that order), so they are written that way.
    /// </summary>
    internal static IReadOnlyList<string> SourceOrder(IReadOnlyList<string> compiled) =>
        compiled.SequenceEqual(["vtol1", "cargodoor", "rengine", "lengine"], StringComparer.Ordinal) ? ["vtol1", "lengine", "rengine", "cargodoor"] : compiled;

    /// <summary>
    /// The Softimage version in the DKit warning: the version that saved the scene, which the fragments show changing
    /// with the scripts' dates (3.5001 in June 1997, 3.7 from July 1997, 3.71 from May 1998). The breaks between the
    /// last and first evidence are chosen dates: 1 July 1997, and 4 May 1998 (when Softimage released its game kit for
    /// 3.7 SP1). A script without a recorded time has no DKit messages (null).
    /// </summary>
    internal static string? Version(uint? time) => time switch { null => null, < 867_715_200 => "3.5001", < 894_240_000 => "3.7", _ => "3.71" };

    /// <summary>
    /// Whether the DKit messages follow each frame block instead of preceding it. The fragments show them preceding
    /// every frame, except in the m5doexit.zan of 20 May 1998, whose memory residue ends with them.
    /// </summary>
    internal static bool MessagesFollowFrames(string path, uint? time) =>
        time == 895_642_826 && Path.GetFileName(path).Equals("m5doexit.zan", StringComparison.OrdinalIgnoreCase);

    /// <summary>Each shipped entry with the definition (and pattern digits) it was compiled from, or null when they do not line up.</summary>
    private static List<(AnimationDefinition Definition, string Digits, AnimationEntry Entry)>? Bindings(AnimationDefinitionSet set, MissionAnimation mission, CancellationToken token)
    {
        List<(AnimationDefinition, string, AnimationEntry)> result = []; int index = 1;
        AnimationRoots roots = new(mission.WorldNodes, token);
        HashSet<string> nodeNames = new(mission.WorldNodes, StringComparer.Ordinal);
        foreach (var definition in set.Definitions)
            foreach (var (root, digits) in roots.Resolve(definition.Item, _ => { }))
            {
                if (!nodeNames.Contains(root)) continue;
                if (index >= mission.Package.Entries.Count) return null;
                var entry = mission.Package.Entries[index++];
                if (entry.Name != AnimationCompiler.Bind(definition.Item.TextOf("ANIMATION_NAME") ?? root, digits) || entry.RootName != root) return null;
                result.Add((definition, digits, entry));
            }
        return index == mission.Package.Entries.Count ? result : null;
    }

    /// <summary>The definition's script items paired with the entry's keyframe events, sequence by sequence.</summary>
    private static IEnumerable<(AnimationItem Item, AnimationEvent Event)> ScriptEvents(AnimationItem definition, AnimationEntry entry, CancellationToken token)
    {
        List<IEnumerable<AnimationItem>> items = [definition.Item("RESET_STATE")?.Items ?? [], .. definition.All("SEQUENCE_DEFINITION").Select(s => s.Items)];
        var compiled = entry.AllSequences.ToList();
        for (int s = 0; s < items.Count && s < compiled.Count; s++)
        {
            token.ThrowIfCancellationRequested();
            using var scriptItems = items[s].Where(i => i.Key == "OBJECT_MOTION_SI_SCRIPT").GetEnumerator();
            if (!scriptItems.MoveNext()) continue;
            foreach (var ev in compiled[s].Events)
            {
                token.ThrowIfCancellationRequested();
                if (ev.Type != 12) continue;
                yield return (scriptItems.Current, ev);
                if (!scriptItems.MoveNext()) break;
            }
        }
    }

    /// <summary>
    /// Replaces each definition that does not compile to its shipped entries with the definition decompiled from them.
    /// A replacement is kept only when every entry it produces then compiles exactly.
    /// </summary>
    private static void RepairDefinitions(MissionAnimation mission, List<(AnimationDefinition Definition, string Digits, AnimationEntry Entry)> bindings,
        Overlay files, HashSet<(string, int)> attempted, List<string> notes, RepairWork work, AnimationDefinitionBudget budget, CancellationToken token)
    {
        string root = SourceBuilder.AnimationRoot($"m{mission.Mission}");
        long packageWork = mission.Package.Prefix.LongLength + mission.Package.Tail.LongLength
            + mission.Package.Entries.Sum(e => (long)e.SourceLength) + 32L * mission.WorldNodes.Count + 1;
        RepairFiles compileFiles = new(files, work);
        AnimationPackage Compile()
        {
            token.ThrowIfCancellationRequested();
            work.Charge(packageWork); // Charge a whole mission again before every repair attempt, including no-op entries.
            var result = AnimationCompiler.Compile(compileFiles, root, mission.WorldNodes, token);
            if (result.Bytes.LongLength > packageWork) work.Charge(result.Bytes.LongLength - packageWork);
            return result.Package;
        }
        AnimationPackage built;
        try { built = Compile(); }
        catch (InvalidDataException ex) { notes.Add($"m{mission.Mission}: the reconstructed animations do not compile ({ex.Message})."); return; }
        if (built.Entries.Count != mission.Package.Entries.Count) { notes.Add($"m{mission.Mission}: the reconstructed animations compile to {built.Entries.Count - 1} entries, not {mission.Package.Entries.Count - 1}."); return; }
        var differing = Enumerable.Range(1, bindings.Count).Where(i => AnimationComparer.Difference(mission.Package.Entries[i], built.Entries[i], token) != null).ToList();
        var definitionMembers = Enumerable.Range(1, bindings.Count).GroupBy(i => bindings[i - 1].Definition)
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var group in differing.GroupBy(i => (bindings[i - 1].Definition.File, bindings[i - 1].Definition.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            var definition = bindings[group.First() - 1].Definition;
            // A definition shared by several missions is rebuilt once; a failed attempt is not repeated.
            if (!attempted.Add((definition.File, definition.Ordinal))) continue;
            // Every entry of the definition must agree on one decompiled definition.
            var members = definitionMembers[definition];
            string? text = null; ZrdNode? items = null;
            try
            {
                foreach (int i in members)
                {
                    var (_, digits, entry) = bindings[i - 1];
                    var candidate = Generalize(Decompile(definition.Item, entry, budget, token), definition.Item, digits, budget);
                    budget.Node();
                    var candidateRoot = new ZrdNode(Guid.Empty, ZrdKind.Array, 0, "", [candidate]);
                    budget.Text(candidateRoot);
                    string candidateText = ZrdText.Write(candidateRoot, token, SourceProject.MaximumSourceTextBytes);
                    if (text != null && text != candidateText) throw new InvalidDataException("its animations decompile to different definitions");
                    text = candidateText; items = candidate;
                }
            }
            catch (InvalidDataException ex) { notes.Add($"{definition.File}: {Name(definition)} does not compile to the shipped animation and could not be rebuilt from it ({ex.Message}); it was kept as shipped."); continue; }
            byte[] original = files.Read(definition.File, token, ProjectReadLimits.Resource(SourceProject.MaximumSourceTextBytes));
            var tree = AnimationDefinitionSet.ReplaceDefinition(AnimationDefinitionSet.Read(files, definition.File, token), definition.Ordinal, items!, budget);
            budget.Text(tree);
            files.Write(definition.File, ZrdText.Write(tree, token, SourceProject.MaximumSourceTextBytes), Encoding.ASCII);
            string? remaining;
            try
            {
                built = Compile();
                remaining = built.Entries.Count != mission.Package.Entries.Count ? $"{built.Entries.Count - 1} animations"
                    : members.Select(i => AnimationComparer.Difference(mission.Package.Entries[i], built.Entries[i], token)).FirstOrDefault(d => d != null);
            }
            catch (InvalidDataException ex) { remaining = ex.Message; }
            bool exact = remaining == null;
            if (exact) notes.Add($"{definition.File}: {Name(definition)} is not the version the shipped animations were compiled from; it was rebuilt from anim.zbd.");
            else
            {
                files.Written[definition.File] = original;
                notes.Add($"{definition.File}: {Name(definition)} does not compile to the shipped animation, and the definition rebuilt from it did not either ({remaining}); it was kept as shipped.");
            }
        }
    }

    private static string Name(AnimationDefinition definition) => definition.Item.TextOf("ANIMATION_NAME") ?? definition.Item.TextOf("NAME") ?? "(unnamed)";

    /// <summary>The entry decompiled, its keyframe events naming the script items of the original definition.</summary>
    private static ZrdNode Decompile(AnimationItem original, AnimationEntry entry, AnimationDefinitionBudget budget, CancellationToken token)
    {
        Dictionary<AnimationEvent, AnimationItem> pairs = new(ReferenceEqualityComparer.Instance);
        foreach (var (item, ev) in ScriptEvents(original, entry, token)) { budget.Reserve(128); pairs[ev] = item; }
        return AnimationDecompiler.Definition(entry, ev =>
        {
            if (!pairs.TryGetValue(ev, out var item)) throw new InvalidDataException("a keyframe event has no script in the definition");
            return (item.TextOf("SCRIPT_FILENAME") ?? throw new InvalidDataException("a script event names no file"), item.Item("SCRIPT_FRAME_RATE")?.Number() ?? 30);
        }, budget);
    }

    /// <summary>
    /// A decompiled definition written like the original: names that the original gave as patterns go back to their
    /// patterns, and a NAME listing several roots keeps its list.
    /// </summary>
    private static ZrdNode Generalize(ZrdNode items, AnimationItem original, string digits, AnimationDefinitionBudget budget)
    {
        HashSet<string> patterns = [];
        void Collect(ZrdNode node)
        {
            budget.Visit();
            if (node.Kind == ZrdKind.String && node.Text.Contains('*') && !patterns.Contains(node.Text))
            { budget.Reserve(128L + 2L * node.Text.Length); patterns.Add(node.Text); }
            foreach (var c in node.Children) Collect(c);
        }
        if (original.Values != null) Collect(original.Values);
        Dictionary<string, string> bound = new(StringComparer.Ordinal);
        foreach (string pattern in patterns) { budget.Reserve(128L + 4L * pattern.Length); bound.TryAdd(AnimationCompiler.Bind(pattern, digits), pattern); }
        ZrdNode Map(ZrdNode node)
        {
            budget.Visit();
            if (node.Kind == ZrdKind.String && bound.TryGetValue(node.Text, out var pattern)) { budget.Node(pattern.Length); return node with { Text = pattern }; }
            if (node.Kind != ZrdKind.Array) return node;
            budget.Node(); budget.CopyChildren(node.Children.Count);
            return node with { Children = node.Children.Select(Map).ToArray() };
        }
        var result = Map(items);
        if (original.Item("NAME") is { } name && name.Scalars.Count > 1 && name.Values != null)
        {
            budget.Node(); budget.CopyChildren(result.Children.Count);
            var children = result.Children.ToList();
            int at = children.FindIndex(c => c.Kind == ZrdKind.String && c.Text == "NAME");
            if (at >= 0 && at + 1 < children.Count) children[at + 1] = name.Values;
            result = result with { Children = children };
        }
        return result;
    }
}
