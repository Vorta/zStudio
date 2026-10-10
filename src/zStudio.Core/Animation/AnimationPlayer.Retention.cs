namespace Recoil.Zbd.Core.Animation;

public sealed partial class AnimationPlayer
{
    // This is conservative managed node/dictionary/sequence/event-work storage, not source assets,
    // renderer geometry, native memory or trace/diagnostic collections.
    internal const long MaximumRetainedStateBytes = 256L * 1024 * 1024;
    private readonly long maximumRetainedStateBytes = MaximumRetainedStateBytes;
    private readonly Func<long>? otherRetainedState;
    private long? retainedStateBytes;
    private readonly List<List<Sequence>> retiredSequences = [];
    internal long RetainedAccountingSequenceVisits { get; private set; }
    private const long NodeBytes = 256, MapEntryBytes = 128, SequenceBytes = 256;
    private const string RetainedStateRefusal = "Animation runtime state exceeds its retained node/event allowance. Reduce copied children or simplify their hierarchies; the requested activation is unavailable.";
    internal sealed class StateLimitException() : IOException(RetainedStateRefusal) { }

    internal AnimationPlayer(AnimationPreviewContext context, int entryIndex, int seed, bool resetPhase,
        CancellationToken token, long maximumRetainedStateBytes, Func<long>? otherRetainedState = null)
    {
        if (maximumRetainedStateBytes is < 1 or > MaximumRetainedStateBytes) throw new ArgumentOutOfRangeException(nameof(maximumRetainedStateBytes));
        this.context = context; this.entryIndex = entryIndex; this.resetPhase = resetPhase; Seed = seed;
        this.maximumRetainedStateBytes = maximumRetainedStateBytes; this.otherRetainedState = otherRetainedState;
        Reset(token);
    }

    // Containers are counted by ownership. Shared world Nodes occur once in each state; instance maps
    // still own their slots. SavedNodes are immutable and shared by checkpoint clones, counted once by
    // dictionary identity across current and saved states. Rebuild only after structural ownership
    // changes: hot event admission reads the cache, and Work assignment/reset adjusts it in O(1).
    // No per-node walk is needed. Checkpoint copies have no live-owner callback.
    internal long RetainedStateBytes
    {
        get
        {
            if (retainedStateBytes is long cached) return cached;
            HashSet<Dictionary<int, Node>> saved = new(ReferenceEqualityComparer.Instance);
            long bytes = StateBytes(instances, sharedNodes, saved);
            RetainedAccountingSequenceVisits += instances.Sum(i => (long)i.Sequences.Count);
            foreach (var checkpoint in checkpoints.Values)
            {
                bytes += StateBytes(checkpoint.Instances, checkpoint.SharedNodes, saved);
                RetainedAccountingSequenceVisits += checkpoint.Instances.Sum(i => (long)i.Sequences.Count);
            }
            foreach (var retired in retiredSequences)
            {
                bytes += SequenceBytes * retired.Count;
                foreach (var sequence in retired) bytes += WorkBytes(sequence.Work);
                RetainedAccountingSequenceVisits += retired.Count;
            }
            retainedStateBytes = bytes;
            return bytes;
        }
    }
    private void InvalidateRetainedState() => retainedStateBytes = null;
    private void AdjustRetainedWork(long delta) => AdjustRetainedBytes(delta);
    private void AdjustRetainedBytes(long delta)
    {
        if (retainedStateBytes.HasValue) retainedStateBytes += delta;
    }
    private void RemoveCheckpoint(long at) { checkpoints.Remove(at); InvalidateRetainedState(); }
    private void ReleaseReplacedSequences()
    {
        if (retiredSequences.Count == 0) return;
        retiredSequences.Clear(); InvalidateRetainedState();
    }
    internal int RetainedCheckpointCount => checkpoints.Count;
    internal int RetainedInstanceCount => instances.Count;
    private void ReplaceResetState(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Optional replay caches can retire, but the accepted live state and zero snapshot remain
        // owned until the complete replacement is ready. Its constructor admits that overlap.
        while (checkpoints.Count > 1) RemoveCheckpoint(checkpoints.Keys.First(k => k != 0));
        AnimationPlayer replacement = new(context, entryIndex, Seed, resetPhase, token, maximumRetainedStateBytes,
            () => RetainedStateBytes + (otherRetainedState?.Invoke() ?? 0));
        token.ThrowIfCancellationRequested();
        instances = replacement.instances; sharedNodes = replacement.sharedNodes;
        effects = replacement.effects; trace = replacement.trace; notes = replacement.notes;
        lights = replacement.lights; activeSounds = replacement.activeSounds; checkpoints = replacement.checkpoints;
        previewIssues = replacement.previewIssues; diagnosticContext = replacement.diagnosticContext;
        entryLookup = replacement.entryLookup; bindingIndexes = replacement.bindingIndexes;
        ticks = replacement.ticks; nextId = replacement.nextId; traceOrdinal = replacement.traceOrdinal;
        traceDropped = replacement.traceDropped; randomState = replacement.randomState; randomIndex = replacement.randomIndex;
        replacement.randomTable.CopyTo(randomTable, 0);
        screenColor = replacement.screenColor; screenWave = replacement.screenWave; fog = replacement.fog;
        measuredEnd = replacement.measuredEnd; unavailableDuration = replacement.unavailableDuration;
        retiredSequences.Clear();
        foreach (var instance in instances)
            foreach (var sequence in instance.Sequences) sequence.WorkChanged = AdjustRetainedWork;
        InvalidateRetainedState();
        cues.Clear();
    }
    private static long MapBytes(int count) => 128L + MapEntryBytes * count;
    private static long WorkBytes(AnimationEvent? work) => work == null ? 0 : 256L + 16L * work.Bytes.LongLength;
    // The factor includes the raw event copy and worst-case sparse keyframe index/playback objects.
    // Playback already refuses above16,384 keys; reserving before Clone also protects non-keyframe tails.
    private static long StateBytes(List<Instance> state, Dictionary<int, Node> shared, HashSet<Dictionary<int, Node>>? saved = null)
    {
        long bytes = 256 + MapBytes(shared.Count) + NodeBytes * shared.Count;
        foreach (var instance in state)
        {
            bytes += 256 + MapBytes(instance.Nodes.Count) + MapBytes(instance.Children.Count)
                + (instance.Shared ? 0 : NodeBytes * instance.Nodes.Count) + SequenceBytes * instance.Sequences.Count;
            foreach (var sequence in instance.Sequences) bytes += WorkBytes(sequence.Work);
            if (saved?.Add(instance.SavedNodes) == true)
                bytes += MapBytes(instance.SavedNodes.Count) + NodeBytes * instance.SavedNodes.Count;
        }
        return bytes;
    }
    private bool FitsState(long additional) => additional >= 0
        && additional <= maximumRetainedStateBytes - RetainedStateBytes - (otherRetainedState?.Invoke() ?? 0);
    private bool AdmitState(long additional, Checkpoint? keep = null)
    {
        // Optional history must not change which event a fresh replay can execute. Release it before
        // refusing live state; the immutable zero checkpoint and a restore's selected source stay held.
        while (!FitsState(additional))
        {
            long remove = -1;
            foreach (var (at, checkpoint) in checkpoints)
                if (at != 0 && !ReferenceEquals(checkpoint, keep)) { remove = at; break; }
            if (remove < 0) return false;
            RemoveCheckpoint(remove);
        }
        return true;
    }
    private void RequireState(long additional)
    {
        if (!AdmitState(additional)) throw new StateLimitException();
    }
    private void AdmitInstance(IReadOnlySet<int> nodes, bool shared, int sequences)
    {
        long freshShared = 0;
        if (shared) foreach (int index in nodes) if (!sharedNodes.ContainsKey(index)) freshShared++;
        long bytes = 256 + MapEntryBytes + 2 * MapBytes(nodes.Count) + MapBytes(0) + SequenceBytes * sequences
            + NodeBytes * nodes.Count // immutable reset state
            + (shared ? (NodeBytes + MapEntryBytes) * freshShared : NodeBytes * nodes.Count);
        RequireState(bytes);
    }
    private static long CloneScratch(List<Instance> state, Dictionary<int, Node> shared)
        => 128 + 64L * (shared.Count + state.Where(i => !i.Shared).Sum(i => (long)i.Nodes.Count));

    private bool CanCaptureState() => FitsState(StateBytes(instances, sharedNodes) + CloneScratch(instances, sharedNodes));
    private void SaveCheckpoint(CancellationToken token)
    {
        int maximum = instances.Sum(i => (long)i.Nodes.Count) > 10000 ? 2 : 8;
        // Drop obsolete ownership before allocating its replacement, including before the admission
        // calculation. The zero checkpoint remains the deterministic fallback for backward seeking.
        while (checkpoints.Count > maximum) RemoveCheckpoint(checkpoints.Keys.First(k => k != 0));
        while (!CanCaptureState() && checkpoints.Count > 1) RemoveCheckpoint(checkpoints.Keys.First(k => k != 0));
        if (!CanCaptureState())
        {
            AddNote("Animation checkpoint storage reached its allowance; seeking may replay from an earlier point.", "Support", "Information");
            return;
        }
        checkpoints[ticks] = CaptureCheckpoint(token);
        InvalidateRetainedState();
    }
}
