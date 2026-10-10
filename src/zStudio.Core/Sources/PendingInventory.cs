using System.Collections;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// One operation's immutable pending identities. Folder queries reuse the index rather than revisiting every
/// unrelated borrowed path for each build family/mission. Disk inventories are still read afresh.
/// </summary>
internal sealed class PendingInventory : IReadOnlyCollection<string>
{
    private readonly record struct Entry(string Path, int Order);
    private readonly Entry[] paths;
    private readonly InventoryBudget inventory;
    public int Count => paths.Length;

    internal PendingInventory(IReadOnlyCollection<string> source, InventoryBudget inventory, CancellationToken token)
    {
        this.inventory = inventory;
        if (source.Count > SourceProject.MaximumScannedEntries) throw SourceProject.TooManyEntries("Pending sources", SourceProject.MaximumScannedEntries);
        inventory.Rows(source.Count, token);
        paths = new Entry[source.Count];
        int index = 0;
        foreach (string path in source)
        {
            token.ThrowIfCancellationRequested();
            if (index == paths.Length) throw new IOException("The pending source inventory changed while it was being captured; try again.");
            inventory.Path(path.Length, token);
            paths[index] = new(path, index); index++;
        }
        if (index != paths.Length) throw new IOException("The pending source inventory changed while it was being captured; try again.");
        // Row admission covers sorting storage; each nonallocating comparison separately spends at most
        // the compared character range. Original ordinal breaks case-alias ties without changing spelling.
        try { Array.Sort(paths, (a, b) =>
        {
            inventory.Compare(Math.Min(a.Path.Length, b.Path.Length) + 1L, token);
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
            return comparison != 0 ? comparison : a.Order.CompareTo(b.Order);
        }); }
        catch (InvalidOperationException ex) when (ex.InnerException is InventoryCapacityException or OperationCanceledException)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
    }

    internal static PendingInventory Capture(IReadOnlyCollection<string> source, InventoryBudget inventory, CancellationToken token) =>
        source is PendingInventory known && ReferenceEquals(known.inventory, inventory) ? known : new(source, inventory, token);

    private int LowerBound(string key, CancellationToken token)
    {
        int lo = 0, hi = paths.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            inventory.Inspect(Math.Min(paths[mid].Path.Length, key.Length), token);
            if (StringComparer.OrdinalIgnoreCase.Compare(paths[mid].Path, key) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
    internal bool ContainsPath(string path, CancellationToken token)
    {
        int index = LowerBound(path, token);
        if (index == paths.Length) return false;
        inventory.Inspect(Math.Min(paths[index].Path.Length, path.Length), token);
        return paths[index].Path.Equals(path, StringComparison.OrdinalIgnoreCase);
    }
    internal IEnumerable<string> Below(string prefix, CancellationToken token)
    {
        for (int index = LowerBound(prefix, token); index < paths.Length; index++)
        {
            string path = paths[index].Path;
            inventory.Inspect(Math.Min(path.Length, prefix.Length), token);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) yield break;
            yield return path;
        }
    }
    public IEnumerator<string> GetEnumerator() { foreach (var entry in paths) yield return entry.Path; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
