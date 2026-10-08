using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Core;

/// <summary>One compiled-file discovery's path storage and comparison work, including its projections.</summary>
public sealed class CompiledInventory
{
    public const int MaximumEntries = 250_000;
    private readonly InventoryBudget budget;
    private readonly CancellationToken token;
    private readonly int maximumEntries;
    private int entries;
    public CompiledInventory(CancellationToken token = default) : this(InventoryBudget.MaximumUnits, MaximumEntries, token) { }
    internal CompiledInventory(long units, int maximumEntries, CancellationToken token)
    { budget = new(units); this.maximumEntries = maximumEntries; this.token = token; }
    public void Path(long characters)
    { try { budget.Path(characters, token); } catch (InventoryCapacityException) { throw new CompiledInventoryCapacityException(); } }
    public void Rows(long count)
    { try { budget.Rows(count, token); } catch (InventoryCapacityException) { throw new CompiledInventoryCapacityException(); } }
    public void Compare(long characters)
    { try { budget.Compare(characters, token); } catch (InventoryCapacityException) { throw new CompiledInventoryCapacityException(); } }

    /// <summary>Admit even ignored entries before retaining full paths; recursive queues share this allowance.</summary>
    public IEnumerable<FileSystemInfo> Entries(string directory, bool recurse = false, bool ignoreInaccessible = false, FileAttributes skip = 0)
    {
        Path(directory.Length); Stack<string> pending = new(); pending.Push(directory);
        var options = new EnumerationOptions { IgnoreInaccessible = ignoreInaccessible, AttributesToSkip = skip };
        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > maximumEntries) throw new CompiledInventoryCapacityException();
                Path(entry.FullName.Length);
                if (recurse && entry is DirectoryInfo) { Rows(1); pending.Push(entry.FullName); }
                yield return entry;
            }
        }
    }
    public List<string> Files(string directory, bool sort = true)
    {
        List<string> result = [];
        foreach (var entry in Entries(directory))
            if (entry is FileInfo && System.IO.Path.GetExtension(entry.Name).Equals(".zbd", StringComparison.OrdinalIgnoreCase))
            { Rows(1); result.Add(entry.FullName); }
        if (sort) Sort(result, StringComparer.OrdinalIgnoreCase.Compare, p => p);
        return result;
    }
    /// <summary>Stable sorting, with comparison admission and original cancellation/capacity exceptions.</summary>
    public void Sort<T>(List<T> values, Comparison<T> comparison, Func<T, string> key)
    {
        Rows(values.Count * 2L);
        var indexed = values.Select((value, index) => (Value: value, Index: index)).ToArray();
        try
        {
            Array.Sort(indexed, (a, b) =>
            {
                Compare(2L * (key(a.Value).Length + key(b.Value).Length + 1));
                int order = comparison(a.Value, b.Value);
                return order != 0 ? order : a.Index.CompareTo(b.Index);
            });
        }
        catch (InvalidOperationException ex) when (ex.InnerException is CompiledInventoryCapacityException or OperationCanceledException)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        token.ThrowIfCancellationRequested();
        for (int i = 0; i < indexed.Length; i++) values[i] = indexed[i].Value;
    }
    internal string Fingerprint(IReadOnlyList<string> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[2048];
        foreach (string path in paths)
        {
            Path(path.Length); // Before encoding/hashing, including cache hits.
            BinaryPrimitives.WriteInt32LittleEndian(buffer, path.Length); hash.AppendData(buffer[..4]);
            for (int at = 0; at < path.Length;)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(buffer.Length / 2, path.Length - at);
                for (int i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(i * 2, 2), path[at + i]);
                hash.AppendData(buffer[..(count * 2)]); at += count;
            }
            var stamp = FileStamp.Read(path);
            BinaryPrimitives.WriteInt64LittleEndian(buffer, stamp.Length);
            BinaryPrimitives.WriteInt64LittleEndian(buffer[8..], stamp.LastWriteUtc.Ticks);
            hash.AppendData(buffer[..16]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

public sealed class CompiledInventoryCapacityException() : IOException(
    "The compiled-file inventory exceeds its path, entry or comparison allowance. Open a shallower folder or fewer files together, then try again.");
