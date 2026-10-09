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
    internal static void Resource(RetainedDocumentBudget budget, ResourceSnapshot snapshot, CancellationToken token)
    {
        if (!budget.Object(snapshot, 128L + 16L * snapshot.Members.Count, token)) return;
        Document(budget, snapshot.Document, token);
        foreach (var member in snapshot.Members)
        {
            if (!budget.Object(member, 128, token)) continue;
            budget.Text(member.Name, token); budget.Bytes(member.Data, token); budget.Bytes(member.DirectoryRecord, token);
            if (member.Tree is { } tree) budget.Tree(tree, token);
        }
    }
    internal static void Content(RetainedDocumentBudget budget, ContentSnapshot snapshot, CancellationToken token)
    {
        if (!budget.Object(snapshot, 128L + 64L * snapshot.Documents.Count, token)) return;
        foreach (var document in snapshot.Documents.Values) Document(budget, document, token);
        if (snapshot.State is PreparedScriptPackage script)
        { budget.Script(script, token); ScriptBuffers(budget, script, token); }
        else if (snapshot.State is IReadOnlyDictionary<string, TexturePackEdit> packs)
        {
            if (!budget.Object(packs, 128L + 64L * packs.Count, token)) return;
            foreach (var (path, pack) in packs)
            {
                budget.Text(path, token);
                if (!budget.Object(pack, 128L + 64L * pack.Replacements.Count + 16L * pack.Added.Count, token)) continue;
                Document(budget, pack.Source, token);
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
