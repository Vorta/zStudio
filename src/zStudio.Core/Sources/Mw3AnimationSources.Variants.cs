using System.Text;
using zStudio.Core.Animation;
using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

internal static partial class Mw3AnimationSources
{
    /// <summary>Choose ordinary source files only after every campaign has contributed its required contents.</summary>
    private static void PublishVariants(List<Recovery> contexts, AnimationSources.Overlay files, List<string> notes,
        AnimationDefinitionBudget budget, CancellationToken token)
    {
        int count = contexts.Sum(c => c.Files.Count);
        budget.Reserve(1024L * count);
        var groups = contexts.SelectMany(c => c.Files).GroupBy(f => f.Original, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        Dictionary<RecoveryFile, RecoveryFile[]> children = [];
        foreach (var context in contexts)
        {
            var byParent = context.Files.Where(f => context.Set.Listings[f.Listing].Parent >= 0)
                .ToLookup(f => context.Set.Listings[f.Listing].Parent);
            foreach (var file in context.Files) children.Add(file, byParent[file.Listing].ToArray());
        }
        HashSet<string> done = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);
        // A file's desired text includes its child choices. Process the dependency graph from leaves to roots,
        // grouping by original identity rather than depth (the same file may be reached at several depths).
        while (done.Count < groups.Count)
        {
            bool progress = false;
            foreach (var (path, consumers) in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (done.Contains(path) || consumers.Any(f => children[f].Any(c => !done.Contains(c.Original)))) continue;
                foreach (var file in consumers) file.Tree = Rewrite(file, copying: false);
                if (consumers.All(f => ReferenceEquals(f.Tree, f.Owner.Set.Listings[f.Listing].Tree)))
                { done.Add(path); progress = true; continue; }
                string original = Text(consumers[0].Owner.Set.Listings[consumers[0].Listing].Tree!);
                var desired = consumers.Select(f => ReferenceEquals(f.Tree, f.Owner.Set.Listings[f.Listing].Tree) ? original : Text(f.Tree)).ToArray();
                if (desired.All(t => t == desired[0]))
                {
                    if (desired[0] != original)
                    {
                        Write(path, desired[0]);
                        notes.Add($"{path}: animation definitions recovered from the compiled programs and verified for every campaign that includes them.");
                    }
                }
                else
                {
                    Dictionary<(string Mission, string Text), string> variants = [];
                    for (int i = 0; i < consumers.Length; i++)
                    {
                        var file = consumers[i];
                        if (desired[i] == original) continue;
                        var identity = (file.Owner.Mission.Name, desired[i]);
                        if (!variants.TryGetValue(identity, out string? output))
                        {
                            output = Allocate(file);
                            string text = Text(Rewrite(file, copying: true));
                            Write(output, text); variants.Add(identity, output);
                            notes.Add($"{output}: campaign-specific animation definitions recovered from {path}.");
                        }
                        file.Output = output;
                    }
                }
                done.Add(path); progress = true;
            }
            if (!progress) throw new InvalidDataException("Animation recovery has a cyclic source-file dependency; no incomplete project was created.");
        }
        return;

        ZrdNode Rewrite(RecoveryFile file, bool copying)
        {
            Dictionary<Guid, string> operands = [];
            foreach (var child in children[file])
                if (copying || !child.Output.Equals(child.Original, StringComparison.OrdinalIgnoreCase))
                    operands.Add(file.Owner.Set.Listings[child.Listing].Include, "../" + child.Output);
            if (copying)
            {
                // Preserve ordinary script lookups even for definitions whose roots are absent in this campaign.
                PinScripts(file.Tree);
                foreach (var pair in file.Scripts) operands[pair.Key] = pair.Value;
            }
            if (operands.Count == 0) return file.Tree;
            return Visit(file.Tree);
            void PinScripts(ZrdNode node)
            {
                budget.Visit();
                for (int i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    if (child.Kind == ZrdKind.String && child.Text == "SCRIPT_FILENAME" && i + 1 < node.Children.Count &&
                        node.Children[i + 1] is { Kind: ZrdKind.Array, Children: [{ Kind: ZrdKind.String } name, ..] } operand &&
                        file.Owner.Set.Resolve(name.Text, file.Original) is { } resolved)
                    {
                        budget.Reserve(96L + 2L * resolved.Length);
                        operands[operand.Id] = "../" + resolved;
                    }
                    if (child.Kind == ZrdKind.Array) PinScripts(child);
                }
            }
            ZrdNode Visit(ZrdNode node)
            {
                budget.Visit();
                if (operands.TryGetValue(node.Id, out string? value))
                {
                    if (node.Children.Count == 0 || node.Children[0].Kind != ZrdKind.String)
                        throw new InvalidDataException($"{file.Original}: cannot relocate a non-string animation source operand.");
                    budget.Node(); budget.Node(value.Length); budget.CopyChildren(node.Children.Count);
                    return node with { Children = [node.Children[0] with { Text = value }, .. node.Children.Skip(1)] };
                }
                if (node.Kind != ZrdKind.Array) return node;
                ZrdNode[]? changed = null;
                for (int i = 0; i < node.Children.Count; i++)
                {
                    var child = Visit(node.Children[i]);
                    if (ReferenceEquals(child, node.Children[i])) continue;
                    if (changed == null) { budget.Node(); budget.CopyChildren(node.Children.Count); changed = node.Children.ToArray(); }
                    changed[i] = child;
                }
                return changed == null ? node : node with { Children = changed };
            }
        }
        string Allocate(RecoveryFile file)
        {
            var parts = file.Original.Split('/', 3);
            if (parts.Length != 3 || !parts[0].Equals("data", StringComparison.OrdinalIgnoreCase) || !parts[2].Contains('/'))
                throw new InvalidDataException($"{file.Original}: cannot place a campaign-specific animation definition.");
            string candidate = $"data/{file.Owner.Mission.Name}/{parts[2]}";
            string stem = candidate[..^AnimationDefinitionSet.Extension.Length];
            for (int suffix = 0; suffix <= AnimationDefinitionSet.MaximumFileReads; suffix++)
            {
                token.ThrowIfCancellationRequested();
                string path = suffix == 0 ? candidate : stem + "_recovered" + (suffix == 1 ? "" : suffix) + AnimationDefinitionSet.Extension;
                SourceProject.RequireSource(path);
                if (!files.Exists(path) && claimed.Add(path)) return path;
            }
            throw new InvalidDataException($"{candidate}: no free path for recovered animation definitions.");
        }
        string Text(ZrdNode tree) { budget.Text(tree); return ZrdText.Write(tree, token, SourceProject.MaximumSourceTextBytes); }
        void Write(string path, string text)
        {
            files.Write(path, text, Encoding.Latin1);
        }
    }
}
