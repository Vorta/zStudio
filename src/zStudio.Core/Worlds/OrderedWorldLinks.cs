namespace Recoil.Zbd.Core.Worlds;

/// <summary>List semantics for graph reversal without repeatedly scanning or shifting an adjacency list.</summary>
internal sealed class OrderedWorldLinks
{
    private readonly LinkedList<WorldNode> order = new();
    private readonly Dictionary<WorldNode, LinkedList<LinkedListNode<WorldNode>>> occurrences = new(ReferenceEqualityComparer.Instance);
    private readonly LookupWorkBudget work;
    private readonly CancellationToken token;

    internal OrderedWorldLinks(IReadOnlyList<WorldNode> source, LookupWorkBudget work, CancellationToken token)
    {
        this.work = work; this.token = token;
        foreach (var node in source) Append(node);
    }

    internal void Append(WorldNode node) => Add(node, first: false);

    internal bool PrependIfAbsent(WorldNode node)
    {
        token.ThrowIfCancellationRequested(); work.Reserve(1);
        if (occurrences.ContainsKey(node)) return false;
        Add(node, first: true); return true;
    }

    private void Add(WorldNode node, bool first)
    {
        token.ThrowIfCancellationRequested(); work.Reserve(128);
        if (!occurrences.TryGetValue(node, out var nodes)) occurrences.Add(node, nodes = new());
        var item = first ? order.AddFirst(node) : order.AddLast(node);
        if (first) nodes.AddFirst(item); else nodes.AddLast(item);
    }

    internal void RemoveFirst(WorldNode node)
    {
        token.ThrowIfCancellationRequested(); work.Reserve(1);
        if (!occurrences.TryGetValue(node, out var nodes)) return;
        order.Remove(nodes.First!.Value); nodes.RemoveFirst();
        if (nodes.Count == 0) occurrences.Remove(node);
    }

    internal void CopyTo(List<WorldNode> target)
    {
        token.ThrowIfCancellationRequested(); work.Reserve(8L * order.Count + 1);
        // Reserve capacity before clearing, and check during traversal. No occurrence is normalized away.
        target.EnsureCapacity(order.Count); target.Clear();
        foreach (var node in order) { token.ThrowIfCancellationRequested(); target.Add(node); }
    }
}
