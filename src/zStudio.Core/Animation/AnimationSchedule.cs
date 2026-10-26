namespace zStudio.Core.Animation;

/// <summary>A preview activation, not an edit to an authored event threshold.</summary>
public sealed record AnimationScheduledStart(Guid Id, int Entry, double Seconds)
{
    public string Path => Id == Guid.Empty ? "" : "/@" + Id.ToString("N");
    internal long Tick => (long)Math.Ceiling(Seconds * 60 - 1e-7);
}

/// <summary>Immutable, finite starts on the shared preview clock. Empty GUID identifies the primary.</summary>
public sealed class AnimationSchedule
{
    public const int MaximumStarts = 32;
    public IReadOnlyList<AnimationScheduledStart> Starts { get; }
    public AnimationSchedule(IEnumerable<AnimationScheduledStart> starts, int entryCount)
    {
        List<AnimationScheduledStart> values = []; HashSet<Guid> ids = [];
        foreach (var start in starts)
        {
            if (values.Count >= MaximumStarts || !ids.Add(start.Id) || start.Entry < 0 || start.Entry >= entryCount ||
                !double.IsFinite(start.Seconds) || start.Seconds is < 0 or > 3600)
                throw new InvalidDataException("Use at most 32 distinct animation activations with valid bindings and start times within 0–3600 seconds.");
            values.Add(start);
        }
        if (!ids.Contains(Guid.Empty)) throw new InvalidDataException("An animation schedule requires its primary activation.");
        Starts = Array.AsReadOnly(values.OrderBy(s => s.Tick).ToArray());
    }
}
