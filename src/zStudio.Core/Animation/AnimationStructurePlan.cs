using System.Numerics;

namespace zStudio.Core.Animation;

public sealed record AnimationStructurePage<T>(IReadOnlyList<T> Rows, int Total, int Offset, int? NextOffset);
public sealed record AnimationStructureEntry(int Entry, string Name, string Root, string Attach, int SequenceCount, AnimationEntrySource? Source);
public sealed record AnimationStructureSequence(int Entry, Guid Id, int Index, string Name, bool Cleanup, bool Waiting,
    int EventCount, AnimationSequenceSource? Source);
public sealed record AnimationStructureEvent(int Entry, Guid Sequence, Guid Id, int Index, byte Type, string Name,
    byte Clock, float Threshold, string Support, int KeyframeCount, AnimationEventSource? Source,
    AnimationStructureCall? Call, string? Diagnostic);
public sealed record AnimationStructureCall(int Entry, Guid Sequence, Guid Event, int? TargetEntry, string TargetName,
    bool WaitForCompletion, string Path);
public sealed record AnimationStructureKeyframe(int Index, float Start, float End, int Flags,
    Vector3? Position, Vector3? Velocity, Quaternion? Rotation, Vector3? Spin, Vector3? Scale, Vector3? Growth);

/// <summary>
/// Read-only, paged view of a caller-owned frozen package. It describes source structure and compiled samples,
/// never execution times, transforms, audio, or trajectories. Source provenance belongs to this exact compilation.
/// </summary>
public sealed class AnimationStructurePlan
{
    public const int MaximumPageSize = 100;
    public const int MaximumDependencyRecords = 4096;
    private readonly AnimationPackage package;
    private readonly AnimationSourceProvenance? provenance;
    private readonly AnimationEntryLookup lookup;
    private AnimationStructurePlan(AnimationPackage package, AnimationSourceProvenance? provenance)
    { this.package = package; this.provenance = provenance; lookup = new(package); }

    public Guid Id { get; } = Guid.NewGuid();
    public Guid? ProvenanceId => provenance?.Id;
    public int EntryCount => package.Entries.Count;
    public static AnimationStructurePlan Create(AnimationPackage package, AnimationSourceProvenance? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new(package, provenance);
    }

    public AnimationStructureEntry Entry(int entry)
    {
        var value = RequireEntry(entry);
        return new(entry, value.Name, value.RootName, value.AttachName, 1 + value.Sequences.Count, provenance?.FindEntry(entry));
    }

    public AnimationStructurePage<AnimationStructureSequence> Sequences(int entry, int offset = 0, int limit = MaximumPageSize,
        CancellationToken token = default)
    {
        CheckPage(offset, limit); var value = RequireEntry(entry); int count = 1 + value.Sequences.Count;
        List<AnimationStructureSequence> rows = [];
        for (int i = offset; i < count && rows.Count < limit; i++)
        {
            token.ThrowIfCancellationRequested(); var sequence = i == 0 ? value.Primary : value.Sequences[i - 1];
            rows.Add(new(entry, sequence.Id, i, sequence.Name, i == 0, sequence.ResetMode == 3, sequence.Events.Count,
                provenance?.FindSequence(sequence.Id)));
        }
        return Page(rows, count, offset);
    }

    public AnimationStructurePage<AnimationStructureEvent> Events(int entry, Guid sequence, int offset = 0,
        int limit = MaximumPageSize, string parentPath = "", CancellationToken token = default)
    {
        CheckPage(offset, limit); CheckPath(parentPath); var value = RequireSequence(entry, sequence);
        List<AnimationStructureEvent> rows = [];
        for (int i = offset; i < value.Events.Count && rows.Count < limit; i++)
        {
            token.ThrowIfCancellationRequested(); var ev = value.Events[i];
            int keys = 0; string? diagnostic = null;
            if (ev.Bytes.Length < 12)
            {
                rows.Add(new(entry, sequence, ev.Id, i, 0, "Unavailable event", 0, 0, "Unavailable", 0,
                    provenance?.FindEvent(ev.Id), null, "The event header is incomplete."));
                continue;
            }
            if (ev.Type == 12)
                try { keys = ev.Keyframes(token).Count; }
                catch (InvalidDataException error) { diagnostic = JsonData.ShownText(error.Message, 1024); }
            rows.Add(new(entry, sequence, ev.Id, i, ev.Type, ev.Name, ev.StartMode, ev.Threshold,
                ev.Spec?.Support ?? "Unavailable", keys, provenance?.FindEvent(ev.Id), Call(entry, sequence, ev, parentPath, token), diagnostic));
        }
        return Page(rows, value.Events.Count, offset);
    }

    /// <summary>Direct references only, across cleanup and runtime sequences. Never expands child definitions.</summary>
    public AnimationStructurePage<AnimationStructureCall> Calls(int entry, string parentPath = "", int offset = 0,
        int limit = MaximumPageSize, CancellationToken token = default)
    {
        CheckPage(offset, limit); CheckPath(parentPath); var value = RequireEntry(entry);
        List<AnimationStructureCall> rows = []; int total = 0;
        foreach (var sequence in value.AllSequences)
            foreach (var ev in sequence.Events)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCall(ev)) continue;
                // Count without resolving or allocating descriptors for rows outside the requested page.
                if (total >= offset && rows.Count < limit) rows.Add(Call(entry, sequence.Id, ev, parentPath, token)!);
                total++;
            }
        return Page(rows, total, offset);
    }

    public AnimationStructurePage<AnimationIgnoredSource> Ignored(int entry, Guid sequence, int offset = 0,
        int limit = MaximumPageSize, CancellationToken token = default)
    {
        CheckPage(offset, limit); _ = RequireSequence(entry, sequence);
        var source = provenance?.Ignored(sequence) ?? [];
        List<AnimationIgnoredSource> rows = [];
        for (int i = offset; i < source.Count && rows.Count < limit; i++) { token.ThrowIfCancellationRequested(); rows.Add(source[i]); }
        return Page(rows, source.Count, offset);
    }

    /// <summary>Compiled sample order and values, not interpolated poses or an invented source-frame grid.</summary>
    public AnimationStructurePage<AnimationStructureKeyframe> Keyframes(int entry, Guid sequence, Guid eventId,
        int offset = 0, int limit = MaximumPageSize, CancellationToken token = default)
    {
        CheckPage(offset, limit);
        var ev = RequireSequence(entry, sequence).Events.FirstOrDefault(e => e.Id == eventId)
            ?? throw new InvalidDataException("The animation event is not part of this structural plan.");
        var frames = ev.Keyframes(token); List<AnimationStructureKeyframe> rows = [];
        for (int i = offset; i < frames.Count && rows.Count < limit; i++)
        {
            token.ThrowIfCancellationRequested(); var frame = frames[i];
            int p = frame.ChannelOffset(0), r = frame.ChannelOffset(1), s = frame.ChannelOffset(2);
            rows.Add(new(i, frame.Start, frame.End, frame.Flags,
                p < 0 ? null : frame.Vector(p), p < 0 ? null : frame.Vector(p + 16),
                r < 0 ? null : AnimationMath.ReadQuaternion(frame, r),
                r < 0 ? null : frame.Vector(r + 16), s < 0 ? null : frame.Vector(s), s < 0 ? null : frame.Vector(s + 16)));
        }
        return Page(rows, frames.Count, offset);
    }

    private AnimationStructureCall? Call(int entry, Guid sequence, AnimationEvent ev, string parentPath, CancellationToken token)
    {
        if (!IsCall(ev)) return null;
        var target = lookup.ResolveChild(ev, token);
        return new(entry, sequence, ev.Id, target?.Index, AnimationAudioDependencies.ChildName(ev),
            ev.Type == 24 && (ev.I16(46) & 16) != 0,
            AnimationExecutionPolicy.ChildPath(parentPath, entry, ev.Id, target?.Index ?? -1));
    }
    private static bool IsCall(AnimationEvent ev) => ev.Bytes.Length >= 80 && ev.Type is 19 or 24;
    private AnimationEntry RequireEntry(int entry) => entry >= 0 && entry < package.Entries.Count
        ? package.Entries[entry] : throw new InvalidDataException("The animation binding is not part of this structural plan.");
    private AnimationSequence RequireSequence(int entry, Guid sequence) => RequireEntry(entry).AllSequences.FirstOrDefault(s => s.Id == sequence)
        ?? throw new InvalidDataException("The animation sequence is not part of this structural plan.");
    private static void CheckPage(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit), "Use offset >= 0 and limit 1–100.");
    }
    private static void CheckPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > AnimationExecutionPolicy.MaximumPathCharacters) throw new InvalidDataException("The dependency path exceeds its structural allowance.");
    }
    private static AnimationStructurePage<T> Page<T>(List<T> rows, int total, int offset) =>
        new(rows.AsReadOnly(), total, offset, rows.Count < total - (long)offset ? offset + rows.Count : null);
}
