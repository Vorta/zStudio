namespace Recoil.Zbd.Core.Worlds;

/// <summary>Bounds aggregate path decoding, comparisons and list movement before doing the work.</summary>
internal sealed class DirectoryWorkBudget(long maximumUnits = DirectoryWorkBudget.MaximumUnits)
{
    internal const long MaximumUnits = 64L * 1024 * 1024;
    private readonly long maximum = maximumUnits is >= 0 and <= MaximumUnits ? maximumUnits : throw new ArgumentOutOfRangeException(nameof(maximumUnits));
    internal long UsedUnits { get; private set; }
    internal bool Exhausted { get; private set; }
    internal void Reserve(long units)
    {
        if (Exhausted || units > maximum - UsedUnits)
        {
            Exhausted = true;
            throw new InvalidDataException("The scripts perform too much directory search-path work. Reduce directory changes or split the source project into fewer missions.");
        }
        UsedUnits += units;
    }
}

internal static class WorldDirectoryPaths
{
    /// <summary>Retail move-to-front order; returns the last valid authored folder for script-local ownership.</summary>
    internal static string? Add(List<string> directories, string value, DirectoryWorkBudget budget)
    {
        // Charge the entire operand before Split allocates substrings, including invalid/empty path operands.
        budget.Reserve(value.Length);
        string? last = null;
        foreach (string part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // ProjectPath normalizes/scans text; Remove compares up to Count entries and both operations may
            // move Count references. The name comparisons themselves can inspect the whole operand.
            budget.Reserve(4L * part.Length + (long)directories.Count * (part.Length + 3L) + 1);
            if (WorldAssembler.ProjectPath(part) is not { } folder) continue;
            directories.Remove(folder); directories.Insert(0, folder); last = folder;
        }
        return last;
    }

    internal static bool SameOrder(IReadOnlyList<string> current, IReadOnlyList<string> previous, DirectoryWorkBudget budget)
    {
        if (current.Count != previous.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            budget.Reserve((long)current[i].Length + 1);
            if (!current[i].Equals(previous[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
