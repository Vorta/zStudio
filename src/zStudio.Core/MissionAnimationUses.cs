namespace zStudio.Core;

/// <summary>A resolved animation association, not a claim that it is currently playing.</summary>
public sealed record MissionAnimationUse(int EntryIndex, string Name, string Origin, string Reason);

/// <summary>
/// Bounded presentation metadata: every association up to <see cref="MaximumUses"/> (far beyond any shipped object), so the
/// object card, the glyph flyout and MCP list them all. Initial-root status is retained even when details are omitted.
/// </summary>
public sealed record MissionAnimationUses(IReadOnlyList<MissionAnimationUse> Uses, bool HasInitialRoot, bool Truncated, bool HasIndirect = false)
{
    public static MissionAnimationUses Empty { get; } = new([], false, false);
    public const int MaximumUses = 256;
    /// <summary>How many names a row's tooltip and <see cref="Names"/> list before "…".</summary>
    public const int ShownNames = 8;

    public MissionAnimationUses Merge(MissionAnimationUses other)
    {
        List<MissionAnimationUse> uses = [.. Uses];
        HashSet<MissionAnimationUse> known = [.. Uses];
        bool truncated = Truncated || other.Truncated;
        foreach (var use in other.Uses)
        {
            if (known.Contains(use)) continue;
            if (uses.Count < MaximumUses) { uses.Add(use); known.Add(use); }
            else truncated = true;
        }
        return new(uses.AsReadOnly(), HasInitialRoot || other.HasInitialRoot, truncated,
            HasIndirect || other.HasIndirect || Uses.Any(u => u.Origin == "indirect") || other.Uses.Any(u => u.Origin == "indirect"));
    }

    /// <summary>The first <see cref="ShownNames"/> distinct names, then "…" when there are more (or details were omitted).</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            var names = Uses.Select(u => u.Name).Distinct(StringComparer.Ordinal).ToArray();
            return Truncated || names.Length > ShownNames ? [.. names.Take(ShownNames), "…"] : names;
        }
    }
}
