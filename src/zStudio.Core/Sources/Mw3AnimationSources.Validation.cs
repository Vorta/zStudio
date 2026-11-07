using System.Globalization;
using System.Text;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

internal static partial class Mw3AnimationSources
{
    /// <summary>Reference tables are compiler intern tables; compare the referenced names, including repeated child calls.</summary>
    internal static AnimationEntry AlignReferences(AnimationEntry expected, AnimationEntry actual, CancellationToken token)
    {
        var result = actual.Clone(token); Dictionary<int, int>[] maps = Enumerable.Range(0, 8).Select(_ => new Dictionary<int, int>()).ToArray();
        for (int table = 0; table < 8; table++)
        {
            token.ThrowIfCancellationRequested(); if (table == 6) continue; // Prerequisites retain their traversal order.
            var source = result.References[table].ToArray();
            if (source.Length != expected.References[table].Count) return result;
            Dictionary<(string Name, string Local), Queue<int>> available = [];
            for (int i = 0; i < source.Length; i++)
            {
                var key = Identity(source[i], table);
                if (!available.TryGetValue(key, out var queue)) available.Add(key, queue = new());
                queue.Enqueue(i);
            }
            List<AnimationRecord> ordered = [];
            for (int i = 0; i < source.Length; i++)
            {
                if (!available.TryGetValue(Identity(expected.References[table][i], table), out var queue) || !queue.TryDequeue(out int from)) return result;
                ordered.Add(source[from]); maps[table].Add(from, i);
            }
            result.References[table].Clear(); result.References[table].AddRange(ordered);
        }
        foreach (var ev in result.AllSequences.SelectMany(s => s.Events))
        {
            token.ThrowIfCancellationRequested();
            foreach (var field in ev.Spec?.Fields.Where(f => f.ReferenceTable >= 0) ?? [])
            {
                int index = field.Kind == AnimationFieldKind.Short ? ev.I16(field.Offset) : ev.I32(field.Offset);
                if (index >= 0 && maps[field.ReferenceTable].TryGetValue(index, out int target))
                { if (field.Kind == AnimationFieldKind.Short) ev.SetShort(field.Offset, checked((short)target)); else ev.SetInt(field.Offset, target); }
            }
            if (ev.Type == 42 && maps[1].TryGetValue(ev.I32(52), out int basis)) ev.SetInt(52, basis);
        }
        return result;
        static (string, string) Identity(AnimationRecord reference, int table) =>
            (reference.Text(0, table is 4 or 5 or 7 ? 32 : 36), table == 7 ? reference.Text(32, 32) : "");
    }

    internal static string? Difference(AnimationEntry expected, AnimationEntry actual, CancellationToken token)
        => AnimationComparer.Difference(expected, AlignReferences(expected, actual, token), token, allowMw3ArithmeticRounding: true);

    private sealed record Recovery(Mission Mission, AnimationDefinitionSet Set, List<RecoveryFile> Files);
    private sealed class RecoveryFile(Recovery owner, int listing, ZrdNode tree)
    {
        internal readonly Recovery Owner = owner;
        internal readonly int Listing = listing;
        internal readonly string Original = owner.Set.Listings[listing].File;
        internal ZrdNode Tree = tree;
        internal string Output = owner.Set.Listings[listing].File;
        internal readonly Dictionary<Guid, string> Scripts = [];
    }

    private static void VerifyAndRepair(IReadOnlyList<Mission> missions, IProjectFiles project, List<WorldSources.Output> outputs,
        List<string> notes, ReconstructionBudget budget, CancellationToken token, Action<string>? status)
    {
        AnimationSources.Overlay files = new(project, budget);
        foreach (var output in outputs) files.Written.Add(output.Path, output.Bytes);
        AnimationSources.RepairWork work = new(AnimationSources.MaximumRepairWork);
        AnimationSources.RepairFiles compileFiles = new(files, work);
        AnimationDefinitionBudget syntax = new(token, budget.Retain);
        List<Recovery> recovery = [];
        var keywords = AnimationCatalog.ForVersion(39).Select(e => AnimationPhrases.Keyword(e.Type)).Concat(["DETONATE_WEAPON", "PUFFER_STATE"]).ToHashSet(StringComparer.Ordinal);
        foreach (var mission in missions)
        {
            token.ThrowIfCancellationRequested(); status?.Invoke($"Checking {mission.Name} animation sources");
            var compiled = Compile(mission);
            var set = AnimationDefinitionSet.Load(files, $"data/{mission.Name}/zrdr/anim.zad", AnimationDefinitionSet.MaximumSourceBytes,
                AnimationDefinitionSet.MaximumSourceNodes, token, game: GameVariant.MechWarrior3, captureIncludes: true);
            // Parsing has its own per-compilation admission. Charge retaining these trees across missions before aggregation.
            syntax.Reserve(192L * set.SourceNodes + 256L * set.Listings.Count);
            Recovery context = new(mission, set, []); recovery.Add(context);
            Dictionary<int, RecoveryFile> listings = [];
            for (int l = 0; l < set.Listings.Count; l++)
                if (!set.Listings[l].Missing && set.Listings[l].Tree is { } tree)
                { var f = new RecoveryFile(context, l, tree); context.Files.Add(f); listings.Add(l, f); }
            Dictionary<(int Listing, int Ordinal), ZrdNode> repairs = [];
            var originalDefinitions = set.Definitions.ToDictionary(d => (d.Listing, d.Ordinal), d => d.Item.Values);
            var roots = new AnimationRoots(mission.WorldNodes, token, game: GameVariant.MechWarrior3); int index = 1;
            foreach (var definition in set.Definitions)
                foreach (var (root, digits) in roots.Resolve(definition.Item, _ => { }))
                {
                    token.ThrowIfCancellationRequested();
                    if (!mission.WorldNodes.Contains(root, StringComparer.Ordinal)) continue;
                    var expected = mission.Package.Entries[index]; var actual = AlignReferences(expected, compiled.Entries[index++], token);
                    var file = listings[definition.Listing];
                    foreach (var (item, _) in AnimationSources.ScriptEvents(definition.Item, expected, token))
                    {
                        var operand = item.Item("SCRIPT_FILENAME"); if (operand?.Values == null) continue;
                        string bound = AnimationCompiler.BoundName(operand.Text(), digits, GameVariant.MechWarrior3);
                        if (set.Resolve(bound, definition.File) is not { } resolved) continue;
                        string pinned = "../" + Path.GetDirectoryName(resolved)!.Replace('\\', '/') + "/" + operand.Text().Replace('\\', '/').Split('/')[^1];
                        if (file.Scripts.TryGetValue(operand.Values.Id, out var previous) && previous != pinned)
                            throw Mismatch(mission, expected, "script bindings use different source folders");
                        file.Scripts[operand.Values.Id] = pinned;
                    }
                    // Include unchanged consumers too: they can require the original version of a shared file.
                    var candidate = Repair(definition.Item, expected, actual, mission);
                    var identity = (definition.Listing, definition.Ordinal);
                    if (repairs.TryGetValue(identity, out var earlier) && !ReferenceEquals(earlier, candidate) && Text(earlier) != Text(candidate))
                        throw Mismatch(mission, expected, "conflicting expansions within one definition listing");
                    repairs[identity] = candidate;
                }
            foreach (var (identity, body) in repairs)
            {
                var file = listings[identity.Listing];
                if (!ReferenceEquals(originalDefinitions[identity], body))
                    file.Tree = AnimationDefinitionSet.ReplaceDefinition(file.Tree, identity.Ordinal, body, syntax);
            }
        }
        PublishVariants(recovery, files, notes, syntax, token);
        // Verify the combined source graph, not each repair in isolation.
        foreach (var context in recovery)
        {
            status?.Invoke($"Verifying {context.Mission.Name} recovered animation definitions");
            var built = Compile(context.Mission);
            for (int i = 1; i < built.Entries.Count; i++)
                if (Difference(context.Mission.Package.Entries[i], built.Entries[i], token) is { } remaining)
                    throw Mismatch(context.Mission, context.Mission.Package.Entries[i], remaining);
            var finalSet = AnimationDefinitionSet.Load(files, $"data/{context.Mission.Name}/zrdr/anim.zad", GameVariant.MechWarrior3, token);
            if (!context.Set.Listings.Where(l => l.Missing).Select(l => l.File).Order(StringComparer.Ordinal)
                .SequenceEqual(finalSet.Listings.Where(l => l.Missing).Select(l => l.File).Order(StringComparer.Ordinal)))
                throw new InvalidDataException($"{context.Mission.Name}: recovery changed which missing includes resolve.");
        }
        foreach (var pair in files.Written.Where(p => p.Key.EndsWith(AnimationDefinitionSet.Extension, StringComparison.OrdinalIgnoreCase)))
            outputs.Add(new(pair.Key, pair.Value));
        return;

        string Text(ZrdNode node) { syntax.Text(node); return ZrdText.Write(node, token, SourceProject.MaximumSourceTextBytes); }
        ZrdNode Repair(AnimationItem definition, AnimationEntry expected, AnimationEntry actual, Mission mission)
        {
            if (AnimationComparer.Difference(expected, actual, token, allowMw3ArithmeticRounding: true) == null) return definition.Values!;
            Dictionary<Guid, ZrdNode> replacements = [];
            var sequences = new[] { definition.Item("RESET_STATE") }.Concat(definition.All("SEQUENCE_DEFINITION")).ToArray();
            var a = actual.AllSequences.ToArray(); var e = expected.AllSequences.ToArray();
            if (a.Length != sequences.Length || e.Length != a.Length) throw Mismatch(mission, expected, "sequence structure");
            for (int s = 0; s < a.Length; s++)
            {
                token.ThrowIfCancellationRequested();
                var sourceEvents = sequences[s]?.Items.Where(i => keywords.Contains(i.Key)).ToArray() ?? [];
                if (sourceEvents.Length != a[s].Events.Count || e[s].Events.Count > a[s].Events.Count) throw Mismatch(mission, expected, "event structure");
                if (e[s].Events.Count < a[s].Events.Count)
                {
                    if (a[s].Events.Skip(e[s].Events.Count).Any(v => v.Type != 24)) throw Mismatch(mission, expected, "unsupported surplus events");
                    var removed = sourceEvents.Skip(e[s].Events.Count).Select(i => i.SyntaxIndex).ToHashSet();
                    var sequence = sequences[s]!.Values!;
                    replacements[sequence.Id] = Filter(sequence, removed);
                    a[s].Events.RemoveRange(e[s].Events.Count, a[s].Events.Count - e[s].Events.Count);
                }
                for (int j = 0; j < a[s].Events.Count; j++)
                {
                    var ev = e[s].Events[j]; var built = a[s].Events[j];
                    const uint gravityFlags = 0x6001;
                    if (ev.Type != 10 || built.Type != 10 || ((ev.U32(12) ^ built.U32(12)) & gravityFlags) == 0 && ev.F32(24) == built.F32(24)) continue;
                    var motion = sourceEvents[j].Values!;
                    var gravity = sourceEvents[j].Item("GRAVITY");
                    var indexes = gravity == null ? new HashSet<int>() : new HashSet<int> { gravity.SyntaxIndex };
                    var body = Filter(motion, indexes);
                    if ((ev.U32(12) & 1) != 0)
                    {
                        List<ZrdNode> values = [Scalar(ZrdKind.String, (ev.U32(12) & 0x2000) != 0 ? "COMPLEX" : "LOCAL"),
                            Scalar(ZrdKind.Float, ev.F32(24).ToString("R", CultureInfo.InvariantCulture))];
                        if ((ev.U32(12) & 0x4000) != 0) values.Add(Scalar(ZrdKind.String, "NO_ALTITUDE"));
                        syntax.Node(); syntax.CopyChildren(values.Count);
                        var attribute = new ZrdNode(Guid.NewGuid(), ZrdKind.Array, 0, "", values);
                        syntax.CopyChildren(body.Children.Count + 2);
                        body = body with { Children = [.. body.Children, Scalar(ZrdKind.String, "GRAVITY"), attribute] };
                    }
                    replacements[motion.Id] = body;
                    built.SetInt(12, unchecked((int)((built.U32(12) & ~gravityFlags) | (ev.U32(12) & gravityFlags))));
                    built.SetFloat(24, ev.F32(24));
                }
            }
            if (AnimationComparer.Difference(expected, actual, token, allowMw3ArithmeticRounding: true) is { } remaining) throw Mismatch(mission, expected, remaining);
            return Replace(definition.Values!);
            ZrdNode Replace(ZrdNode node)
            {
                syntax.Visit(); if (replacements.TryGetValue(node.Id, out var replacement)) node = replacement;
                if (node.Kind != ZrdKind.Array) return node;
                syntax.CopyChildren(node.Children.Count); return node with { Children = node.Children.Select(Replace).ToArray() };
            }
        }
        ZrdNode Scalar(ZrdKind kind, string value) { syntax.Node(value.Length); return kind == ZrdKind.String
            ? new(Guid.NewGuid(), kind, 0, value, []) : ZrdNode.Create(kind, value); }
        ZrdNode Filter(ZrdNode node, HashSet<int> removed)
        {
            syntax.Node(); syntax.CopyChildren(node.Children.Count);
            List<ZrdNode> kept = [];
            for (int i = 0; i < node.Children.Count; i++)
            { syntax.Visit(); if (removed.Contains(i)) { if (i + 1 < node.Children.Count && node.Children[i + 1].Kind == ZrdKind.Array) i++; } else kept.Add(node.Children[i]); }
            return node with { Children = kept };
        }
        AnimationPackage Compile(Mission mission)
        {
            work.Charge(mission.Package.Entries.Sum(e => (long)e.SourceLength) + 32L * mission.WorldNodes.Count);
            var built = AnimationCompiler.Compile(compileFiles, $"data/{mission.Name}/zrdr/anim.zad", mission.WorldNodes, GameVariant.MechWarrior3, token);
            if (built.EngineRejection is { } rejection) throw new InvalidDataException(rejection);
            if (built.Package.Entries.Count != mission.Package.Entries.Count) throw new InvalidDataException($"{mission.Name}: reconstructed animation entry count differs.");
            return built.Package;
        }
    }
    private static InvalidDataException Mismatch(Mission mission, AnimationEntry entry, string reason) => new($"{mission.Name}: recovered sources do not reproduce animation {entry.Name}: {reason}. No incomplete project was created.");
}
