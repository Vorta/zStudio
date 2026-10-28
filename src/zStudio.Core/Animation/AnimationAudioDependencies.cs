namespace zStudio.Core.Animation;

/// <summary>Static sound discovery, including dormant branches and cleanup, without advancing simulation.</summary>
public sealed record AnimationAudioDependencies(IReadOnlySet<string> Names, IReadOnlyList<string> Diagnostics)
{
    public static AnimationAudioDependencies Collect(AnimationPackage package, int entryIndex, CancellationToken token = default)
        => Collect(package, entryIndex, null, token);

    /// <summary>Discover only calls admitted by the same finite scenario as playback, including dormant branches and cleanup; a policy admitting every call discovers like none.</summary>
    public static AnimationAudioDependencies Collect(AnimationPackage package, int entryIndex, AnimationExecutionPolicy? policy, CancellationToken token = default, AnimationSchedule? activations = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, package.Entries.Count);
        HashSet<string> names = new(StringComparer.Ordinal);
        BoundedDiagnostics diagnostics = new();
        AnimationEntryLookup entries = new(package);
        HashSet<(AnimationEntry Entry, string Path)> scheduled = activations == null ? [(package.Entries[entryIndex], "")] : activations.Starts.Select(s => (package.Entries[s.Entry], s.Path)).ToHashSet();
        Stack<(AnimationEntry Entry, string Path)> pending = new(scheduled);
        while (pending.TryPop(out var item))
        {
            var (entry, path) = item;
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
                {
                    string childPath = "";
                    if (policy is { IncludesAllCalls: false })
                    {
                        string candidate;
                        try { candidate = AnimationExecutionPolicy.ChildPath(path, entry.Index, ev.Id, child.Index); }
                        catch (InvalidDataException) { continue; }
                        if (!policy.TryInclude(candidate, out childPath)) continue;
                        if (scheduled.Contains((child, childPath))) continue;
                        if (scheduled.Count >= AnimationExecutionPolicy.MaximumPaths + AnimationSchedule.MaximumStarts)
                            throw new InvalidDataException("Audio discovery exceeds the preview dependency-path allowance.");
                    }
                    if (scheduled.Add((child, childPath))) pending.Push((child, childPath));
                }
                else
                {
                    if (policy is { IncludesAllCalls: false })
                    {
                        string candidate;
                        try { candidate = AnimationExecutionPolicy.ChildPath(path, entry.Index, ev.Id, -1); }
                        catch (InvalidDataException) { continue; }
                        if (!policy.Includes(candidate)) continue;
                    }
                    diagnostics.Add($"Audio preparation: unresolved child animation in #{entry.Index} ({entry.Name}).");
                }
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
