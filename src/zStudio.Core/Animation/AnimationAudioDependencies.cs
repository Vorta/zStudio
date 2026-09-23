namespace Recoil.Zbd.Core.Animation;

/// <summary>Static sound discovery, including dormant branches and cleanup, without advancing simulation.</summary>
public sealed record AnimationAudioDependencies(IReadOnlySet<string> Names, IReadOnlyList<string> Diagnostics)
{
    public static AnimationAudioDependencies Collect(AnimationPackage package, int entryIndex, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, package.Entries.Count);
        HashSet<string> names = new(StringComparer.Ordinal);
        List<string> diagnostics = [];
        HashSet<AnimationEntry> visited = [];
        Stack<AnimationEntry> pending = new([package.Entries[entryIndex]]);
        while (pending.TryPop(out var entry))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(entry)) continue;
            foreach (var ev in entry.AllSequences.SelectMany(s => s.Events))
            {
                token.ThrowIfCancellationRequested();
                if (ev.Bytes.Length == 0 || ev.Bytes[0] is not (1 or 2 or 10 or 19 or 24)) continue;
                if (ev.Spec == null || ev.Bytes.Length < ev.Spec.Size)
                { diagnostics.Add($"Audio preparation: truncated event in animation #{entry.Index} ({entry.Name})."); continue; }
                if (ev.Type is 1 or 10)
                {
                    if (ev.Type == 10 && ((ev.U32(12) & 0x1000) == 0 || ev.I16(242) <= 0)) continue;
                    int index = ev.I16(ev.Type == 10 ? 242 : 12);
                    if (index >= 0 && index < entry.References[4].Count && entry.References[4][index].Bytes.Length >= 32)
                        names.Add(entry.References[4][index].Text(0));
                    else diagnostics.Add($"Audio preparation: unresolved sample reference {index} in animation #{entry.Index} ({entry.Name}).");
                }
                else if (ev.Type == 2)
                {
                    if (ev.I32(52) == 1) names.Add(ev.Text(12));
                }
                else if (ResolveChild(package, ev) is { } child) pending.Push(child);
                else diagnostics.Add($"Audio preparation: unresolved child animation in #{entry.Index} ({entry.Name}).");
            }
        }
        return new(names, diagnostics);
    }

    // Keep the preload closure and the runtime's index/name fallback identical. Names are not unique.
    internal static AnimationEntry? ResolveChild(AnimationPackage package, AnimationEvent ev)
    {
        int index = ev.I16(48);
        string name = ev.Type == 19 ? ev.Text(16) : ev.Text(12, 20);
        return index > 0 && index < package.Entries.Count ? package.Entries[index] : package.Entries.FirstOrDefault(e => e.Name == name);
    }
}
