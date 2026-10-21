namespace zStudio.Core;

/// <summary>A resolved animation association, not a claim that it is currently playing.</summary>
public sealed record MissionAnimationUse(int EntryIndex, string Name, string Origin, string Reason);

/// <summary>Bounded presentation metadata. Initial-root status is retained even when details are omitted.</summary>
public sealed record MissionAnimationUses(IReadOnlyList<MissionAnimationUse> Uses, bool HasInitialRoot, bool Truncated, bool HasIndirect = false)
{
    public static MissionAnimationUses Empty { get; } = new([], false, false);
    public const int MaximumUses = 8;

    public MissionAnimationUses Merge(MissionAnimationUses other)
    {
        List<MissionAnimationUse> uses = [.. Uses];
        bool truncated = Truncated || other.Truncated;
        foreach (var use in other.Uses)
        {
            if (uses.Contains(use)) continue;
            if (uses.Count < MaximumUses) uses.Add(use);
            else truncated = true;
        }
        return new(uses.AsReadOnly(), HasInitialRoot || other.HasInitialRoot, truncated,
            HasIndirect || other.HasIndirect || Uses.Any(u => u.Origin == "indirect") || other.Uses.Any(u => u.Origin == "indirect"));
    }

    public IReadOnlyList<string> Names => Truncated
        ? [.. Uses.Select(u => u.Name).Distinct(StringComparer.Ordinal), "…"]
        : Uses.Select(u => u.Name).Distinct(StringComparer.Ordinal).ToArray();
}
