using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// The engine's lookups by name (docs/engine-evidence.md#name-lookups-and-node-slot-order). Every live node of every class
/// is in one list (bucket 6), the most recently created first, and a world file's nodes join it in slot order, so a lookup
/// finds the highest slot of a name first (<c>FindByTypeAndName</c> 0x44ecf0).
/// </summary>
public static class NameLookups
{
    /// <summary>The entry byte holding its activation state (zEffectAnimEntry +0x98); the root binding loop skips state 5.</summary>
    public const int StateOffset = 0x98, SkippedState = 5;

    /// <summary>Whether <c>LoadZbd</c>'s binding loop skips an entry: entry 0, and entries in state 5 (without breaking the chain).</summary>
    public static bool SkipsRoot(IReadOnlyList<AnimationEntry> entries, int index) =>
        index == 0 || entries[index].Bytes.Length > StateOffset && entries[index].Bytes[StateOffset] == SkippedState;

    /// <summary>
    /// Which of its root name's nodes (highest slot first) each entry binds to, or −1 for a skipped entry or a name no node
    /// has (0x45f5bf–0x45f689). An entry with the root name of the last search continues it with the next node of that name;
    /// a new name, or the end of the nodes, starts the search again at the highest slot.
    /// </summary>
    public static int[] RootPositions(IReadOnlyList<AnimationEntry> entries, Func<string, int> candidates)
    {
        int[] result = new int[entries.Count]; Array.Fill(result, -1);
        string? searched = null; int position = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (SkipsRoot(entries, i)) continue;
            string name = entries[i].RootName; int count = candidates(name);
            if (searched == name && position >= 0 && position + 1 < count) position++;
            else { searched = name; position = count > 0 ? 0 : -1; }
            result[i] = position;
        }
        return result;
    }
}
