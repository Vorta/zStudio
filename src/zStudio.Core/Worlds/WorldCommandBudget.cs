namespace Recoil.Zbd.Core.Worlds;

/// <summary>One assembly's cumulative temporary graph traversal and partition-cell allocation allowance.</summary>
internal sealed class WorldCommandBudget(long maximum = WorldCommandBudget.MaximumBytes, CancellationToken token = default)
{
    internal const long MaximumBytes = 64L * 1024 * 1024;
    internal const int TraversalEntryBytes = 96, PartitionCellBytes = 192;
    private readonly long limit = maximum is >= 0 and <= MaximumBytes ? maximum : throw new ArgumentOutOfRangeException(nameof(maximum));
    internal long UsedBytes { get; private set; }
    internal void Reserve(long bytes)
    {
        token.ThrowIfCancellationRequested();
        if (bytes < 0 || bytes > limit - UsedBytes)
            throw new InvalidDataException("The world requires too much repeated command work (subtree traversal or partition allocation). Simplify repeated lighting, hierarchy or partition commands.");
        UsedBytes += bytes;
    }
    internal void Traversal(long count) => Reserve(checked(count * TraversalEntryBytes));
}
