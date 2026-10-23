namespace zStudio.Core.Animation;

/// <summary>Source addresses belong to one accepted compilation; names and regenerated runtime GUIDs are not durable identities.</summary>
public sealed record AnimationEntrySource(int Entry, int Definition, string File, int DefinitionOrdinal);
public sealed record AnimationSequenceSource(int Entry, Guid Sequence, int Definition, string File, int DefinitionOrdinal,
    string? RecordId, bool Cleanup, bool IgnoredStartTime, string? ReadOnlyReason);
public sealed record AnimationEventSource(int Entry, Guid Sequence, Guid Event, int Definition, string File, int DefinitionOrdinal,
    string? RecordId, string? TimingFieldId, bool TimingPresent, string Kind, float? ScriptFrameRate, string? Script,
    string? ReadOnlyReason);
public sealed record AnimationIgnoredSource(int Entry, Guid Sequence, string? RecordId, string Kind, string Reason)
{
    public bool NameTruncated { get; init; }
}

/// <summary>
/// Immutable, nonserialized source provenance captured while emitting events. Lookup does not parse, compile or simulate.
/// The GUID keys survive package clones, but not archive serialization or a new compilation.
/// </summary>
public sealed class AnimationSourceProvenance
{
    private readonly Dictionary<int, AnimationEntrySource> entries;
    private readonly Dictionary<Guid, AnimationSequenceSource> sequences;
    private readonly Dictionary<Guid, AnimationEventSource> events;
    private readonly Dictionary<Guid, AnimationIgnoredSource[]> ignored;
    private AnimationSourceProvenance(Dictionary<int, AnimationEntrySource> entries,
        Dictionary<Guid, AnimationSequenceSource> sequences, Dictionary<Guid, AnimationEventSource> events,
        Dictionary<Guid, AnimationIgnoredSource[]> ignored, long retainedBytes)
    { this.entries = entries; this.sequences = sequences; this.events = events; this.ignored = ignored; RetainedBytes = retainedBytes; }

    public Guid Id { get; } = Guid.NewGuid();
    public long RetainedBytes { get; }
    public AnimationEntrySource? FindEntry(int entry) => entries.GetValueOrDefault(entry);
    public AnimationSequenceSource? FindSequence(Guid sequence) => sequences.GetValueOrDefault(sequence);
    public AnimationEventSource? FindEvent(Guid ev) => events.GetValueOrDefault(ev);
    internal IReadOnlyList<AnimationIgnoredSource> Ignored(Guid sequence) => ignored.GetValueOrDefault(sequence) ?? [];

    internal sealed class Builder(Action<long> reserve)
    {
        private readonly Dictionary<int, AnimationEntrySource> entries = [];
        private readonly Dictionary<Guid, AnimationSequenceSource> sequences = [];
        private readonly Dictionary<Guid, AnimationEventSource> events = [];
        private readonly Dictionary<Guid, List<AnimationIgnoredSource>> ignored = [];
        private long retained;
        private void Reserve(long bytes) { reserve(bytes); retained = checked(retained + bytes); }
        public void Entry(AnimationEntrySource value)
        { Reserve(192L + 2L * value.File.Length); entries.Add(value.Entry, value); }
        public void Sequence(AnimationSequenceSource value)
        { Reserve(256L + 2L * (value.File.Length + (value.RecordId?.Length ?? 0))); sequences.Add(value.Sequence, value); }
        public void Event(AnimationEventSource value)
        {
            Reserve(384L + 2L * (value.File.Length + (value.RecordId?.Length ?? 0) + (value.TimingFieldId?.Length ?? 0) +
                value.Kind.Length + (value.Script?.Length ?? 0)));
            events.Add(value.Event, value);
        }
        public void Ignored(AnimationIgnoredSource value)
        {
            // Unknown authored keywords need no unbounded displayed copy or response field.
            if (value.Kind.Length > 512) value = value with { Kind = value.Kind[..512], NameTruncated = true };
            Reserve(256L + 2L * (value.Kind.Length + (value.RecordId?.Length ?? 0)));
            if (!ignored.TryGetValue(value.Sequence, out var list)) ignored.Add(value.Sequence, list = []);
            list.Add(value);
        }
        public AnimationSourceProvenance Finish() => new(entries, sequences, events,
            ignored.ToDictionary(p => p.Key, p => p.Value.ToArray()), retained);
    }
}
