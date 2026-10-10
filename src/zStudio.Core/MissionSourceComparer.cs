using System.Runtime.CompilerServices;

namespace Recoil.Zbd.Core;

/// <summary>Exact record identity with session-owned, weak query memoization.</summary>
internal sealed class MissionSourceComparer : IEqualityComparer<MissionPickupSource>
{
    internal sealed class Prefix(string text, int hash)
    {
        internal string Text { get; } = text;
        internal int Hash { get; } = hash;
    }
    private sealed record Resolution(Prefix Prefix, bool Known);
    private readonly object gate = new();
    private readonly Dictionary<string, Prefix> retained = new(StringComparer.Ordinal);
    private ConditionalWeakTable<string, Resolution> memo = new();
    private long resolutions;
    internal long PrefixResolutions { get { lock (gate) return resolutions; } }
    internal int RetainedPrefixCount { get { lock (gate) return retained.Count; } }

    internal void Register(MissionPickupSource source)
    {
        lock (gate) { Register(source.ArchivePath); Register(source.ResourceName); }
    }
    private void Register(string? text)
    {
        if (text == null) return;
        var resolved = Resolve(text);
        if (resolved.Known) return;
        retained.Add(text, resolved.Prefix);
        // Negative query resolutions from before registration must not keep a separate representative.
        memo = new();
        memo.Add(text, new(resolved.Prefix, true));
    }
    internal void Reset(IEnumerable<MissionPickupSource> sources)
    {
        lock (gate)
        {
            retained.Clear(); memo = new();
            foreach (var source in sources) { Register(source.ArchivePath); Register(source.ResourceName); }
        }
    }
    private Resolution Resolve(string text)
    {
        if (memo.TryGetValue(text, out var result)) return result;
        resolutions++;
        bool known = retained.TryGetValue(text, out var prefix);
        result = new(prefix ?? new(text, StringComparer.Ordinal.GetHashCode(text)), known);
        memo.Add(text, result);
        return result;
    }
    private bool Same(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        var left = Resolve(a); var right = Resolve(b);
        return ReferenceEquals(left.Prefix, right.Prefix) ||
            (!left.Known || !right.Known) && StringComparer.Ordinal.Equals(a, b);
    }
    public bool Equals(MissionPickupSource? a, MissionPickupSource? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.AssetIndex != b.AssetIndex || a.RecordIndex != b.RecordIndex) return false;
        // Resolve both operands while registration cannot change their generation.
        lock (gate) return Same(a.ArchivePath, b.ArchivePath) && Same(a.ResourceName, b.ResourceName);
    }
    public int GetHashCode(MissionPickupSource source)
    {
        if (source == null) return 0;
        lock (gate) return HashCode.Combine(source.ArchivePath == null ? 0 : Resolve(source.ArchivePath).Prefix.Hash,
            source.AssetIndex, source.ResourceName == null ? 0 : Resolve(source.ResourceName).Prefix.Hash, source.RecordIndex);
    }
    internal bool SameArchive(string a, string b) { lock (gate) return Same(a, b); }
    internal Prefix ArchiveIdentity(MissionPickupSource source)
    {
        lock (gate) return Resolve(source.ArchivePath).Prefix;
    }
}
