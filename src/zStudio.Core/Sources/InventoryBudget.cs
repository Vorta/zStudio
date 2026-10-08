namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// One operation's path materialization, including repeated scans. Units conservatively cover four UTF-16
/// representations and row/list/sort/dictionary storage, not a measurement of the managed heap. Full identities
/// are retained; exceeding the allowance refuses the inventory rather than returning a partial list.
/// </summary>
internal sealed class InventoryBudget(long maximum = InventoryBudget.MaximumUnits)
{
    internal const long MaximumUnits = 128L * 1024 * 1024;
    internal const int PathOverhead = 256, CharacterUnits = 8, RowUnits = 64;
    private long remaining = maximum is >= 0 and <= MaximumUnits ? maximum : throw new ArgumentOutOfRangeException(nameof(maximum));
    private bool exhausted;
    internal long Used => maximum - remaining;

    internal void Path(long characters, CancellationToken token = default)
    {
        if (characters < 0 || characters > (long.MaxValue - PathOverhead) / CharacterUnits) throw new ArgumentOutOfRangeException(nameof(characters));
        Reserve(PathOverhead + CharacterUnits * characters, token);
    }
    internal void Rows(long count, CancellationToken token = default)
    {
        if (count < 0 || count > long.MaxValue / RowUnits) throw new ArgumentOutOfRangeException(nameof(count));
        Reserve(RowUnits * count, token);
    }
    internal void Inspect(long characters, CancellationToken token = default)
    {
        if (characters < 0 || characters > (long.MaxValue - RowUnits) / 2) throw new ArgumentOutOfRangeException(nameof(characters));
        Reserve(RowUnits + 2 * characters, token);
    }
    internal void Compare(long characters, CancellationToken token = default)
    {
        if (characters < 0) throw new ArgumentOutOfRangeException(nameof(characters));
        Reserve(characters, token);
    }
    private void Reserve(long units, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (exhausted || units > remaining)
        {
            exhausted = true;
            throw new InventoryCapacityException();
        }
        remaining -= units;
    }
}

internal sealed class InventoryCapacityException() : IOException(
    "The source inventory's combined path lengths and rows exceed its allocation allowance; use shallower folders or fewer files together, then try again.");
