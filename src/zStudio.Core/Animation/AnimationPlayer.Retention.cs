namespace zStudio.Core.Animation;

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

    /// <param name="policy">Admission, overrides and rules known before the reset admits the starts due at zero.</param>
    internal AnimationPlayer(AnimationPreviewContext context, int entryIndex, int seed, bool resetPhase,
        CancellationToken token, long maximumRetainedStateBytes, Func<long>? otherRetainedState = null, AnimationSchedule? schedule = null,
        AnimationExecutionPolicy? policy = null)
    {
        if (maximumRetainedStateBytes is < 1 or > MaximumRetainedStateBytes) throw new ArgumentOutOfRangeException(nameof(maximumRetainedStateBytes));
        this.context = context; this.entryIndex = entryIndex; this.resetPhase = resetPhase; Seed = seed;
        this.schedule = schedule; this.maximumRetainedStateBytes = maximumRetainedStateBytes; this.otherRetainedState = otherRetainedState;
        executionPolicy = policy;
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
            if (retainedStateBytes is long cached) return cached + (ExecutionPolicy?.RetainedBytes ?? 0);
            HashSet<Dictionary<int, Node>> saved = new(ReferenceEqualityComparer.Instance);
            long bytes = StateBytes(instances, sharedNodes, saved) + ConditionBytes(conditions.Count)
                + EvidenceBytes(entryRuns, refusalBytes + prerequisiteBytes, skipped.Count + phases.Count, referenceChanges.Count, saved);
            RetainedAccountingSequenceVisits += instances.Sum(i => (long)i.Sequences.Count);
            foreach (var checkpoint in checkpoints.Values)
            {
                bytes += StateBytes(checkpoint.Instances, checkpoint.SharedNodes, saved) + ConditionBytes(checkpoint.Conditions.Count)
                    + EvidenceBytes(checkpoint.EntryRuns, checkpoint.StartEvidenceBytes, checkpoint.Skipped.Count + checkpoint.Phases.Count, checkpoint.References.Count, saved);
                RetainedAccountingSequenceVisits += checkpoint.Instances.Sum(i => (long)i.Sequences.Count);
            }
            foreach (var retired in retiredSequences)
            {
                bytes += SequenceBytes * retired.Count;
                foreach (var sequence in retired) bytes += WorkBytes(sequence.Work);
                RetainedAccountingSequenceVisits += retired.Count;
            }
            retainedStateBytes = bytes;
            return bytes + (ExecutionPolicy?.RetainedBytes ?? 0);
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
        AnimationPlayer replacement;
        try
        {
            replacement = new(context, entryIndex, Seed, resetPhase, token, maximumRetainedStateBytes,
                () => RetainedStateBytes - (ExecutionPolicy?.RetainedBytes ?? 0) + (otherRetainedState?.Invoke() ?? 0), schedule, executionPolicy);
        }
        catch (StateLimitException) when (interrupted)
        {
            // A refused tick's partial state is not one to keep, and at the allowance it would count against its own
            // replacement. Release it so the reset is admitted exactly as a new player's; until that completes the
            // player stays interrupted, and the next seek, step or playback resets again. An accepted state is kept.
            ReleaseState();
            replacement = new(context, entryIndex, Seed, resetPhase, token, maximumRetainedStateBytes,
                () => otherRetainedState?.Invoke() ?? 0, schedule, executionPolicy);
        }
        token.ThrowIfCancellationRequested();
        instances = replacement.instances; sharedNodes = replacement.sharedNodes;
        effects = replacement.effects; trace = replacement.trace; notes = replacement.notes;
        conditions = replacement.conditions; conditionsDropped = replacement.conditionsDropped; hasExcludedCalls = replacement.hasExcludedCalls;
        entryRuns = replacement.entryRuns; refusals = replacement.refusals; refusalsDropped = replacement.refusalsDropped; refusalBytes = replacement.refusalBytes;
        prerequisiteStarts = replacement.prerequisiteStarts; prerequisiteStartsDropped = replacement.prerequisiteStartsDropped; prerequisiteBytes = replacement.prerequisiteBytes;
        skipped = replacement.skipped; skippedDropped = replacement.skippedDropped; phases = replacement.phases; phasesDropped = replacement.phasesDropped;
        referenceChanges = replacement.referenceChanges; referenceChangesDropped = replacement.referenceChangesDropped;
        lights = replacement.lights; activeSounds = replacement.activeSounds; checkpoints = replacement.checkpoints;
        previewIssues = replacement.previewIssues; omittedIssues = replacement.omittedIssues; diagnosticContext = replacement.diagnosticContext;
        entryLookup = replacement.entryLookup; bindingIndexes = replacement.bindingIndexes;
        nextScheduled = replacement.nextScheduled; originTerrain.Clear(); ticks = replacement.ticks; nextId = replacement.nextId; traceOrdinal = replacement.traceOrdinal;
        traceDropped = replacement.traceDropped; randomState = replacement.randomState; randomIndex = replacement.randomIndex;
        replacement.randomTable.CopyTo(randomTable, 0);
        screenColor = replacement.screenColor; screenWave = replacement.screenWave; fog = replacement.fog;
        measuredEnd = replacement.measuredEnd; unavailableDuration = replacement.unavailableDuration;
        retiredSequences.Clear(); interrupted = false;
        foreach (var instance in instances)
            foreach (var sequence in instance.Sequences) sequence.WorkChanged = AdjustRetainedWork;
        InvalidateRetainedState();
        cues.Clear();
    }
    /// <summary>Drops the runtime state and every checkpoint; frames are refused and the next advance resets.</summary>
    private void ReleaseState()
    {
        interrupted = true;
        instances = []; sharedNodes = []; effects = []; conditions = []; checkpoints = []; retiredSequences.Clear();
        entryRuns = []; refusals = []; refusalBytes = 0; prerequisiteStarts = []; prerequisiteBytes = 0; skipped = []; referenceChanges = []; phases = [];
        InvalidateRetainedState();
    }
    /// <summary>Binding states (with the rest poses only they still hold) and the bounded run evidence.</summary>
    private static long EvidenceBytes(Dictionary<int, EntryRun> runs, long refusals, int skipped, int references, HashSet<Dictionary<int, Node>>? saved)
    {
        long bytes = MapBytes(runs.Count) + EntryRunBytes * runs.Count + refusals + 128L * (skipped + references);
        foreach (var run in runs.Values)
            if (run.Rest is { } rest && saved?.Add(rest) != false) bytes += MapBytes(rest.Count) + NodeBytes * rest.Count;
        return bytes;
    }
    private static long MapBytes(int count) => 128L + MapEntryBytes * count;
    private static long WorkBytes(AnimationEvent? work) => work == null ? 0 : 256L + 16L * work.Bytes.LongLength;
    private static long ConditionBytes(int count) => 128L + 128L * count;
    // The factor includes the raw event copy and worst-case sparse keyframe index/playback objects.
    // Playback already refuses above16,384 keys; reserving before Clone also protects non-keyframe tails.
    private static long StateBytes(List<Instance> state, Dictionary<int, Node> shared, HashSet<Dictionary<int, Node>>? saved = null)
    {
        long bytes = 256 + MapBytes(shared.Count) + NodeBytes * shared.Count;
        foreach (var instance in state)
        {
            bytes += 320 + 2L * instance.CallPath.Length + MapBytes(instance.Nodes.Count) + MapBytes(instance.Children.Count) + MapBytes(instance.Locals.Count)
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
    private void AdmitInstance(IReadOnlySet<int> nodes, bool shared, int sequences, string callPath)
    {
        long freshShared = 0;
        if (shared) foreach (int index in nodes) if (!sharedNodes.ContainsKey(index)) freshShared++;
        long bytes = 320 + 2L * callPath.Length + MapEntryBytes + 2 * MapBytes(nodes.Count) + MapBytes(0) + SequenceBytes * sequences
            + NodeBytes * nodes.Count // immutable reset state
            + (shared ? (NodeBytes + MapEntryBytes) * freshShared : NodeBytes * nodes.Count);
        RequireState(bytes);
    }
    private static long CloneScratch(List<Instance> state, Dictionary<int, Node> shared)
        => 128 + 64L * (shared.Count + state.Where(i => !i.Shared).Sum(i => (long)i.Nodes.Count));

    private bool CanCaptureState() => FitsState(StateBytes(instances, sharedNodes) + ConditionBytes(conditions.Count) + CloneScratch(instances, sharedNodes));
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
