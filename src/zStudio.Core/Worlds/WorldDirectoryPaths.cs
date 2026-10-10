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

/// <summary>
/// A search-path list as the engine builds it: <c>zRdrAddSearchPaths</c> (retail 0x4a5ce0, which <c>SetTextureDirectory</c>
/// reaches through <c>zImageInitMissionResources</c> 0x46ebd0). The gamegen tool's own <c>SetModelDirectory</c> is lost; it is
/// taken to use the same zUtil routine. Each <c>;</c>-separated folder of an operand goes to the head of the list only when
/// it exists (<c>_access</c>) and the list holds no entry with exactly the same text (<c>strcmp</c>); an entry never moves.
/// So a script naming a listed folder again (<c>weapons.gw</c> and <c>bftN.gw</c> after <c>common.gw</c>) leaves the order
/// as it was, and the first folder a script added stays behind the ones added after it.
/// </summary>
internal sealed class DirectorySearchList
{
    /// <summary>The engine's entries by the text a script gave them; a folder spelled differently is another entry.</summary>
    private readonly HashSet<string> named = new(StringComparer.Ordinal);
    private readonly List<string> folders = [];

    /// <summary>
    /// The project folders in search order, each once. Another spelling of a listed folder (case or separators) is an entry
    /// of its own at the head, where the file system finds that folder first, so the folder is searched from there.
    /// </summary>
    internal IReadOnlyList<string> Folders => folders;

    /// <summary>Empties the list, as <c>zRdrSetPath</c> (retail 0x48cca0) frees the zReader path list before adding its operand.</summary>
    internal void Clear() { named.Clear(); folders.Clear(); }

    /// <summary>
    /// Adds an operand's folders. <paramref name="folderExists"/> tests a project folder; without it every folder counts as
    /// present, as in the original build tree (a missing folder holds no file, so no search finds another file).
    /// Returns the last folder the operand names that exists, listed now or before: the folder the script itself chose.
    /// </summary>
    internal string? Add(string value, DirectoryWorkBudget budget, Func<string, bool>? folderExists)
    {
        // Charge the entire operand before Split allocates substrings, including invalid/empty path operands.
        budget.Reserve(value.Length);
        string? last = null;
        // strtok on ";" skips empty tokens and keeps everything else of the text as written.
        foreach (string part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            // ProjectPath normalizes/scans text and the set hashes it; the folder comparisons and the insertion
            // inspect and move up to Count entries.
            budget.Reserve(4L * part.Length + (long)folders.Count * (part.Length + 3L) + 1);
            if (WorldAssembler.ProjectPath(part) is not { } folder) continue;
            if (named.Contains(part)) { last = folder; continue; }
            if (folderExists != null && !folderExists(folder)) continue;
            named.Add(part);
            int at = folders.FindIndex(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) folders.RemoveAt(at);
            folders.Insert(0, folder); last = folder;
        }
        return last;
    }
}

internal static class WorldDirectoryPaths
{
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
