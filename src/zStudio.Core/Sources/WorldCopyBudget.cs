using System.Globalization;

namespace Recoil.Zbd.Core.Sources;

/// <summary>Operation-owned admission for reconstruction placement metadata and path work, before allocation.</summary>
internal sealed class WorldCopyBudget(long maximum = WorldCopyBudget.MaximumUnits, CancellationToken token = default)
{
    internal const long MaximumUnits = 64L << 20;
    private readonly long limit = maximum is >= 0 and <= MaximumUnits ? maximum : throw new ArgumentOutOfRangeException(nameof(maximum));
    internal long Storage { get; private set; }
    internal long Work { get; private set; }

    internal void Reserve(long storage, long work = 0)
    {
        token.ThrowIfCancellationRequested();
        if (storage < 0 || work < 0 || storage > limit - Storage || work > limit - Work)
            throw new InvalidDataException("Reconstructed model placement exceeds its metadata or path-work allowance. Reduce model folders or transitive references, or reconstruct fewer missions together.");
        Storage += storage; Work += work;
    }

    // Covers copies/copyPaths entries, FIFO capacity, copied-unit membership and associated indexing overhead.
    internal void Pair(string folder) => Reserve(512L + 2L * folder.Length, folder.Length + 1L);
    internal void Lookup(string value) => Reserve(0, value.Length + 1L);

    internal string Path(string folder, string stem, int? suffix = null)
    {
        // A decimal Int32 suffix is at most 11 characters. Reserve before formatting or concatenating.
        long length = (long)folder.Length + stem.Length + 6 + (suffix.HasValue ? 12 : 0);
        Reserve(96 + 2 * length, 4 * length);
        return suffix is { } number
            ? string.Concat(folder, "/", stem, "_", number.ToString(CultureInfo.InvariantCulture), ".gltf")
            : string.Concat(folder, "/", stem, ".gltf");
    }

    internal void Sort(int count, long maximumKeyCharacters)
    {
        if (count < 0 || maximumKeyCharacters < 0) throw new ArgumentOutOfRangeException(nameof(count));
        int levels = 0;
        for (int n = count; n > 1; n = n / 2 + n % 2) levels++;
        // LINQ's element/key/index arrays and conservative introsort comparison work, reserved before enumeration.
        long comparisons = 4L * count * Math.Max(1, levels);
        long work = count == 0 ? 0 : maximumKeyCharacters >= limit || comparisons > limit / (maximumKeyCharacters + 1)
            ? limit + 1 : comparisons * (maximumKeyCharacters + 1);
        Reserve(64L * count, work);
    }
}
