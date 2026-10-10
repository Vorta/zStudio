using System.Runtime.CompilerServices;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

/// <summary>Compiled edit ownership and construction estimates; no formatting or content cloning to count it.</summary>
internal static class EditRetentionBudget
{
    // Raw documents may already be 512 MiB; typed readers separately admit 64 MiB ZRD/128 MiB scripts.
    // These ceilings include retained raw backing arrays, decoded graphs and authored edit state.
    internal const long MaximumRetainedBytes = 1L << 30;
    internal const long MaximumConstructionBytes = 1L << 30;
    internal static void Limit(long value, long maximum)
    { if (value < 0 || value > maximum) throw new ArgumentOutOfRangeException(nameof(value)); }
    internal static InvalidDataException Refusal() => new("Compiled editing exceeds its retained-content or construction allowance. Use a smaller document or edit fewer resources together; the current edit and undo history were kept.");

    internal static void Document(RetainedDocumentBudget budget, ZbdDocument document, CancellationToken token)
    {
        budget.Bytes(document.Bytes, token);
        budget.Document(document, token);
        if (document.Scripts is { } package) ScriptBuffers(budget, package, token);
    }
    private static void ScriptBuffers(RetainedDocumentBudget budget, PreparedScriptPackage package, CancellationToken token)
    {
        budget.Bytes(package.Header, token); budget.Bytes(package.PrefixGap, token); budget.Bytes(package.Tail, token);
        if (!budget.Object(package.Entries, 32L + 8L * package.Entries.Count, token)) return;
        foreach (var entry in package.Entries)
        {
            token.ThrowIfCancellationRequested();
            budget.Bytes(entry.DirectoryRecord, token); budget.Bytes(entry.FollowingBytes, token);
            foreach (var instruction in entry.Instructions)
            { budget.Bytes(instruction.Raw, token); budget.Bytes(instruction.Padding, token); }
        }
    }
    /// <summary>A script package's objects and buffers, by identity: edited packages share their unchanged entries.</summary>
    internal static void ScriptPackage(RetainedDocumentBudget budget, PreparedScriptPackage package, CancellationToken token)
    { budget.Script(package, token); ScriptBuffers(budget, package, token); }
    internal static void Resource(RetainedDocumentBudget budget, ResourceSnapshot snapshot, DecodedCosts costs, CancellationToken token)
    {
        if (!budget.Object(snapshot, 128L + 16L * snapshot.Members.Count, token)) return;
        costs.Document(budget, snapshot.Document, token);
        foreach (var member in snapshot.Members)
        {
            if (!budget.Object(member, 128, token)) continue;
            budget.Text(member.Name, token); budget.Bytes(member.Data, token); budget.Bytes(member.DirectoryRecord, token);
            // Members share trees across snapshots (an edit copies only what it changes); walk them by node identity.
            if (member.Tree is { } tree) budget.Tree(tree, token);
        }
    }
    internal static void Content(RetainedDocumentBudget budget, ContentSnapshot snapshot, DecodedCosts costs, CancellationToken token)
    {
        if (!budget.Object(snapshot, 128L + 64L * snapshot.Documents.Count, token)) return;
        foreach (var document in snapshot.Documents.Values) costs.Document(budget, document, token);
        if (snapshot.State is PreparedScriptPackage script) ScriptPackage(budget, script, token);
        else if (snapshot.State is IReadOnlyDictionary<string, TexturePackEdit> packs)
        {
            if (!budget.Object(packs, 128L + 64L * packs.Count, token)) return;
            foreach (var (path, pack) in packs)
            {
                budget.Text(path, token);
                if (!budget.Object(pack, 128L + 64L * pack.Replacements.Count + 16L * pack.Added.Count, token)) continue;
                costs.Document(budget, pack.Source, token);
                foreach (var payload in pack.Replacements.Values.Concat(pack.Added))
                    if (budget.Object(payload, 64, token))
                    { budget.Text(payload.Name, token); budget.Bytes(payload.Bytes, token); }
            }
        }
        else throw new InvalidDataException("This compiled edit state has no retained-content accounting.");
    }
    internal static void Construction(Action<RetainedDocumentBudget> count, long limit, long added = 0)
    {
        RetainedDocumentBudget budget = new(limit);
        try
        {
            count(budget);
            // A working copy, encoded output, verification reader and its independently retained asset graph
            // can coexist. Four copies of the complete known shape conservatively cover these phases.
            long known = budget.UsedBytes;
            if (added < 0 || known > (limit - Math.Min(limit, added)) / 4 || added > limit) throw Refusal();
        }
        catch (InvalidDataException) { throw Refusal(); }
    }
    internal static int KeepNewest<T>(IReadOnlyList<T> history, RetainedDocumentBudget budget, Action<RetainedDocumentBudget, T> add)
    {
        int keep = 0;
        for (int i = history.Count - 1; i >= 0 && keep < 128; i--)
        {
            var checkpoint = budget.Checkpoint();
            try { add(budget, history[i]); keep++; }
            catch (InvalidDataException) when (budget.Exhausted) { budget.Restore(checkpoint); break; }
        }
        return keep;
    }
}

/// <summary>
/// The decoded content of immutable documents and of their ZRD trees, measured once where a snapshot is built (off the
/// dispatcher) and cached by identity, so admitting an edit sums sizes instead of re-walking every retained document. Raw
/// arrays, trees and script packages, which snapshots share, are still charged by identity, each once per budget.
/// </summary>
internal sealed class DecodedCosts(long capacity)
{
    private readonly ConditionalWeakTable<object, StrongBox<long>> costs = new();
    internal void Document(RetainedDocumentBudget budget, ZbdDocument document, CancellationToken token)
    {
        budget.Bytes(document.Bytes, token);
        if (document.Scripts is { } package) EditRetentionBudget.ScriptPackage(budget, package, token);
        if (!budget.Object(document, Measure(document, token, shape =>
        {
            // The parts charged on their own are excluded from the document's size.
            shape.Bytes(document.Bytes, token);
            if (document.Scripts is { } shared) EditRetentionBudget.ScriptPackage(shape, shared, token);
            foreach (var asset in document.Assets) if (asset.Content is ZrdNode root) shape.Object(root, 0, token);
            long excluded = shape.UsedBytes;
            EditRetentionBudget.Document(shape, document, token);
            return shape.UsedBytes - excluded;
        }), token)) return;
        // Each decoded tree is its own identity: a member retaining it, charged before or after, adds nothing more.
        foreach (var asset in document.Assets)
            if (asset.Content is ZrdNode tree) budget.Object(tree, Measure(tree, token, shape => { shape.Tree(tree, token); return shape.UsedBytes; }), token);
    }
    private long Measure(object value, CancellationToken token, Func<RetainedDocumentBudget, long> measure)
    {
        if (costs.TryGetValue(value, out var known)) return known.Value;
        RetainedDocumentBudget shape = new(capacity);
        try
        {
            long cost = measure(shape);
            costs.AddOrUpdate(value, new(cost));
            return cost;
        }
        catch (InvalidDataException) when (shape.Exhausted) { throw EditRetentionBudget.Refusal(); }
    }
}
