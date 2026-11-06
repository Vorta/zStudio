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

    private static void VerifyAndRepair(IReadOnlyList<Mission> missions, IProjectFiles project, List<WorldSources.Output> outputs,
        List<string> notes, ReconstructionBudget budget, CancellationToken token, Action<string>? status)
    {
        AnimationSources.Overlay files = new(project, budget);
        foreach (var output in outputs) files.Written.Add(output.Path, output.Bytes);
        AnimationSources.RepairWork work = new(AnimationSources.MaximumRepairWork);
        AnimationSources.RepairFiles compileFiles = new(files, work);
        Dictionary<(string File, int Ordinal), ZrdNode> repairs = [];
        AnimationDefinitionBudget syntaxBudget = new(token, budget.Retain);
        var keywords = AnimationCatalog.ForVersion(39).Select(e => AnimationPhrases.Keyword(e.Type)).Concat(["DETONATE_WEAPON", "PUFFER_STATE"]).ToHashSet(StringComparer.Ordinal);
        foreach (var mission in missions)
        {
            token.ThrowIfCancellationRequested(); status?.Invoke($"Checking {mission.Name} animation sources");
            var compiled = Compile(mission);
            var set = AnimationDefinitionSet.Load(files, $"data/{mission.Name}/zrdr/anim.zad", GameVariant.MechWarrior3, token);
            var roots = new AnimationRoots(mission.WorldNodes, token, game: GameVariant.MechWarrior3); int index = 1;
            foreach (var definition in set.Definitions)
                foreach (var (root, _) in roots.Resolve(definition.Item, _ => { }))
                {
                    if (!mission.WorldNodes.Contains(root, StringComparer.Ordinal)) continue;
                    var expected = mission.Package.Entries[index]; var actual = AlignReferences(expected, compiled.Entries[index++], token);
                    if (AnimationComparer.Difference(expected, actual, token, allowMw3ArithmeticRounding: true) == null) continue;
                    // Some archived definitions predate anim.zbd. Recover a different authored gravity as an explicit
                    // LOCAL value, then require the complete entry (and every expansion below) to agree before writing.
                    Dictionary<Guid, ZrdNode> replacements = [];
                    var sequences = new[] { definition.Item.Item("RESET_STATE") }.Concat(definition.Item.All("SEQUENCE_DEFINITION")).ToArray();
                    var a = actual.AllSequences.ToArray(); var e = expected.AllSequences.ToArray();
                    if (a.Length != sequences.Length || e.Length != a.Length) throw Mismatch(mission, expected, "sequence structure");
                    for (int s = 0; s < a.Length; s++)
                    {
                        var sourceEvents = sequences[s]?.Items.Where(i => keywords.Contains(i.Key)).ToArray() ?? [];
                        if (sourceEvents.Length != a[s].Events.Count || e[s].Events.Count != a[s].Events.Count) throw Mismatch(mission, expected, "event structure");
                        for (int j = 0; j < a[s].Events.Count; j++)
                        {
                            var ev = e[s].Events[j]; var built = a[s].Events[j];
                            if (ev.Type != 10 || built.Type != 10 || ev.F32(24) == built.F32(24)) continue;
                            var gravity = sourceEvents[j].Item("GRAVITY")?.Values ?? throw Mismatch(mission, expected, "missing gravity source");
                            List<ZrdNode> values = [new(Guid.NewGuid(), ZrdKind.String, 0, (ev.U32(12) & 0x2000) != 0 ? "COMPLEX" : "LOCAL", []), ZrdNode.Create(ZrdKind.Float, ev.F32(24).ToString("R", CultureInfo.InvariantCulture))];
                            values.AddRange(gravity.Children.Where(c => c.Kind == ZrdKind.String && c.Text == "NO_ALTITUDE"));
                            syntaxBudget.CopyChildren(values.Count); replacements.Add(gravity.Id, gravity with { Children = values });
                            built.SetFloat(24, ev.F32(24));
                        }
                    }
                    if (AnimationComparer.Difference(expected, actual, token, allowMw3ArithmeticRounding: true) is { } remaining) throw Mismatch(mission, expected, remaining);
                    var candidate = Replace(definition.Item.Values!);
                    var identity = (definition.File, definition.Ordinal);
                    if (repairs.TryGetValue(identity, out var earlier) && ZrdText.Write(earlier, token) != ZrdText.Write(candidate, token))
                        throw Mismatch(mission, expected, "conflicting expansions of an archived definition");
                    repairs[identity] = candidate;
                    ZrdNode Replace(ZrdNode node)
                    {
                        syntaxBudget.Visit(); if (replacements.TryGetValue(node.Id, out var replacement)) return replacement;
                        if (node.Kind != ZrdKind.Array) return node;
                        syntaxBudget.CopyChildren(node.Children.Count); return node with { Children = node.Children.Select(Replace).ToArray() };
                    }
                }
        }
        foreach (var (identity, items) in repairs)
        {
            var tree = AnimationDefinitionSet.ReplaceDefinition(AnimationDefinitionSet.Read(files, identity.File, token), identity.Ordinal, items, syntaxBudget);
            files.Write(identity.File, ZrdText.Write(tree, token, SourceProject.MaximumSourceTextBytes), Encoding.ASCII);
            notes.Add($"{identity.File}: definition {identity.Ordinal + 1} was rebuilt from anim.zbd, which the game uses. The compiled version records different gravity operands than the archived text; explicit gravity preserves them and their coordinate basis.");
        }
        if (repairs.Count > 0)
            foreach (var mission in missions)
            {
                status?.Invoke($"Verifying {mission.Name} repaired animation definitions"); var built = Compile(mission);
                for (int i = 1; i < built.Entries.Count; i++)
                    if (Difference(mission.Package.Entries[i], built.Entries[i], token) is { } remaining) throw Mismatch(mission, mission.Package.Entries[i], remaining);
            }
        foreach (string path in repairs.Keys.Select(k => k.File).Distinct(StringComparer.OrdinalIgnoreCase)) outputs.Add(new(path, files.Written[path]));
        return;
        AnimationPackage Compile(Mission mission)
        {
            work.Charge(mission.Package.Entries.Sum(e => (long)e.SourceLength) + 32L * mission.WorldNodes.Count);
            var built = AnimationCompiler.Compile(compileFiles, $"data/{mission.Name}/zrdr/anim.zad", mission.WorldNodes, GameVariant.MechWarrior3, token);
            if (built.EngineRejection is { } rejection) throw new InvalidDataException(rejection);
            if (built.Package.Entries.Count != mission.Package.Entries.Count) throw new InvalidDataException($"{mission.Name}: reconstructed animation entry count differs.");
            return built.Package;
        }
        static InvalidDataException Mismatch(Mission mission, AnimationEntry entry, string reason) => new($"{mission.Name}: recovered sources do not reproduce animation {entry.Name}: {reason}. No incomplete project was created.");
    }
}
