namespace Recoil.Zbd.Core.Worlds;

/// <summary>One operation's aggregate node comparisons and graph visits, reserved before scanning or allocating.</summary>
internal sealed class LookupWorkBudget(long maximum = LookupWorkBudget.MaximumUnits, CancellationToken token = default,
    long ceiling = LookupWorkBudget.MaximumUnits)
{
    internal const long MaximumUnits = 64L * 1024 * 1024;
    private const long MaximumCeiling = 96L * 1024 * 1024;
    private readonly long limit = ValidateLimit(maximum, ceiling);
    private static long ValidateLimit(long maximum, long ceiling)
    {
        if (ceiling is < 0 or > MaximumCeiling) throw new ArgumentOutOfRangeException(nameof(ceiling));
        if (maximum < 0 || maximum > ceiling) throw new ArgumentOutOfRangeException(nameof(maximum));
        return maximum;
    }
    private readonly Dictionary<WorldNode, string> names = new(ReferenceEqualityComparer.Instance);
    internal long UsedUnits { get; private set; }
    internal bool Exhausted { get; private set; }
    internal void Reserve(long units)
    {
        token.ThrowIfCancellationRequested();
        if (Exhausted || units > limit - UsedUnits)
        {
            Exhausted = true;
            throw new InvalidDataException("The world requires too much repeated node lookup work. Simplify repeated name lookups or split the mission into smaller models.");
        }
        UsedUnits += units;
    }
    internal bool Matches(WorldNode node, string name)
        => Name(node) == name;

    internal string Name(WorldNode node)
    {
        Reserve(1);
        // Name decodes the fixed 36-byte field. Cache the full identity, never a shortened diagnostic label.
        if (!names.TryGetValue(node, out string? actual)) names[node] = actual = node.Name;
        return actual;
    }
    internal void Invalidate(WorldNode node) => names.Remove(node);

    internal WorldNode? FindSub(WorldNode node, string name, bool firstChildFirst = false)
        => Subtree([node], firstChildFirst).FirstOrDefault(next => Matches(next, name));

    internal IEnumerable<WorldNode> Subtree(IEnumerable<WorldNode> roots, bool firstChildFirst = false, Action<long>? reserveTraversal = null)
    {
        HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
        Stack<WorldNode> pending = new();
        foreach (var root in roots)
        {
            reserveTraversal?.Invoke(1);
            pending.Push(root);
            while (pending.TryPop(out var next))
            {
                Reserve(1);
                if (!seen.Add(next)) continue;
                yield return next;
                Reserve(next.Children.Count);
                reserveTraversal?.Invoke(next.Children.Count);
                if (firstChildFirst)
                    for (int i = next.Children.Count - 1; i >= 0; i--) pending.Push(next.Children[i]);
                else
                    foreach (var child in next.Children) pending.Push(child);
            }
        }
    }
}
