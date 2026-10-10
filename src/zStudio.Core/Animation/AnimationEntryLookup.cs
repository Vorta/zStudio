namespace Recoil.Zbd.Core.Animation;

/// <summary>Operation-owned first-name lookup. Recreate after editing a package; names are not unique.</summary>
internal sealed class AnimationEntryLookup(AnimationPackage package)
{
    private Dictionary<string, AnimationEntry>? names;

    internal AnimationEntry? Find(string name, CancellationToken token = default)
    {
        if (names == null)
        {
            Dictionary<string, AnimationEntry> indexed = new(StringComparer.Ordinal);
            foreach (var entry in package.Entries)
            {
                token.ThrowIfCancellationRequested();
                indexed.TryAdd(entry.Name, entry);
            }
            names = indexed;
        }
        return names.GetValueOrDefault(name);
    }

    internal AnimationEntry? ResolveChild(AnimationEvent ev, CancellationToken token = default)
    {
        int index = ev.I16(48);
        // Zero is an unresolved cache, not an explicit reference to the first entry. A valid positive cache wins.
        return index > 0 && index < package.Entries.Count ? package.Entries[index] : Find(AnimationAudioDependencies.ChildName(ev), token);
    }
}
