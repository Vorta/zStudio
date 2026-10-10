namespace Recoil.Zbd.Core.Animation;

/// <summary>Static sound discovery, including dormant branches and cleanup, without advancing simulation.</summary>
public sealed record AnimationAudioDependencies(IReadOnlySet<string> Names, IReadOnlyList<string> Diagnostics)
{
    public static AnimationAudioDependencies Collect(AnimationPackage package, int entryIndex, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, package.Entries.Count);
        HashSet<string> names = new(StringComparer.Ordinal);
        BoundedDiagnostics diagnostics = new();
        AnimationEntryLookup entries = new(package);
        HashSet<AnimationEntry> scheduled = [package.Entries[entryIndex]];
        Stack<AnimationEntry> pending = new([package.Entries[entryIndex]]);
        while (pending.TryPop(out var entry))
        {
            token.ThrowIfCancellationRequested();
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
                else if (entries.ResolveChild(ev, token) is { } child)
                { if (scheduled.Add(child)) pending.Push(child); }
                else diagnostics.Add($"Audio preparation: unresolved child animation in #{entry.Index} ({entry.Name}).");
            }
        }
        return new(names, diagnostics.Messages);
    }

    // Keep the preload closure and the runtime's index/name fallback identical. Names are not unique.
    internal static AnimationEntry? ResolveChild(AnimationPackage package, AnimationEvent ev)
        => new AnimationEntryLookup(package).ResolveChild(ev);
    /// <summary>
    /// The launched animation's name: 32 bytes at 16 (type 19) or 12 (type 24). The engine compares the whole field with
    /// the entry name (retail 0x45BC60), and shipped names such as reset_the_transporters run past 20 characters.
    /// </summary>
    internal static string ChildName(AnimationEvent ev) => ev.Text(ev.Type == 19 ? 16 : 12);
}
