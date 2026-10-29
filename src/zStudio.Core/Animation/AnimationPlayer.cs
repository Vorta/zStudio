using System.Numerics;
using System.Text.Json.Nodes;

namespace zStudio.Core.Animation;

public sealed record AnimationNodePose(long Id, int SourceNode, int Model, Matrix4x4 Transform, bool Visible, float Opacity, int Variant, float Morph, double CycleTime, string? Texture = null);
public sealed record AnimationSoundCue(long Id, string Name, bool Stop, bool Persistent, double StartedAt, Vector3 Position)
{
    public float Gain { get; init; } = 1;
}
public sealed record AnimationLight(long Id, Vector3 Position, Vector3 Color, float Range, float Intensity, bool Active);
/// <summary>World-space preview eye/target; horizontal field of view in degrees.</summary>
public sealed record AnimationCamera(Vector3 Position, Vector3 Target, float FieldOfView)
{
    public int SourceNode { get; init; } = -1;
    public string Name { get; init; } = "";
}
public sealed record AnimationFog(bool Enabled, Vector3 Color, float Start, float End);
public sealed record AnimationTrace(long Instance, Guid Sequence, Guid Event, string Name, double Start, double? End, string Status)
{
    public long Occurrence { get; init; }
    public int Entry { get; init; } = -1;
    public string InstancePath { get; init; } = "";
    public string? Disposition { get; init; }
    public int? ChildEntry { get; init; }
    public long? ChildInstance { get; init; }
    public long? ExistingInstance { get; init; }
}
public sealed record AnimationSequenceStatus(long Instance, Guid Sequence, string Name, int EventIndex, string State, int Iteration)
{
    public double AnimationElapsed { get; init; }
    public double SequenceElapsed { get; init; }
    public double EventElapsed { get; init; }
    public string InstancePath { get; init; } = "";
    /// <summary>The event that last released this waiting sequence: a CALL_SEQUENCE ("call") or a motion's ground contact ("contact").</summary>
    public Guid? ReleasedBy { get; init; }
    public long? ReleasedByInstance { get; init; }
    public double? ReleasedAt { get; init; }
    public string? ReleaseKind { get; init; }
}
public sealed record AnimationInstanceStatus(long Instance, int Entry, int Root, string Name, string Path,
    long? ParentInstance, Guid? CallEvent, double StartedAt, bool Finished, bool Cleanup);
/// <summary>
/// One IF or ELSEIF outcome. <see cref="Kind"/> names the rule ("random", "playerRange", "playerCover", "cover",
/// "effectsDetail", "none"); <see cref="Measured"/> is the draw, the distance to the attach node, the cover height above
/// the start or the effects level, against <see cref="Threshold"/> (weight, radius, segment length, level).
/// <see cref="Unavailable"/> says why the game rule could not be evaluated (the value is then false).
/// </summary>
public sealed record AnimationConditionResult(long Instance, Guid Event, string InstancePath, double Time, bool Value,
    bool Forced, long Occurrence)
{
    public string Kind { get; init; } = "none";
    public double? Measured { get; init; }
    public double? Threshold { get; init; }
    public int? Draw { get; init; }
    public string? Unavailable { get; init; }
    /// <summary>A range test's callback node, where the run measured the distance from (authored space; null for other kinds).</summary>
    public Vector3? Center { get; init; }
}
/// <summary>
/// Where a range-tested instance's callback node (the attach node its PLAYER_RANGE conditions and EXECUTION_BY_RANGE gate
/// measure from) is at a frame, in authored space: a node without a model has no pose in <see cref="AnimationFrame.Nodes"/>.
/// </summary>
public sealed record AnimationAttachPosition(long Instance, int Entry, int Node, Vector3 Position);
public sealed record AnimationFrame(double Time, IReadOnlyList<AnimationNodePose> Nodes, IReadOnlyList<AnimationLight> Lights,
    IReadOnlyList<AnimationSoundCue> Sounds, IReadOnlyList<AnimationSoundCue> ActiveSounds, AnimationCamera? Camera, AnimationFog? Fog,
    Vector4 ScreenColor, Vector4 ScreenWave, IReadOnlyList<AnimationTrace> Trace, IReadOnlyList<AnimationSequenceStatus> Sequences, IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyList<AnimationPreviewDiagnostic> Issues { get; init; } = [];
    public long TraceDropped { get; init; }
    public IReadOnlyList<AnimationInstanceStatus> Instances { get; init; } = [];
    public IReadOnlyList<AnimationConditionResult> Conditions { get; init; } = [];
    public long ConditionsDropped { get; init; }
    public bool DependencyBlocked { get; init; }
    public bool HasExcludedCalls { get; init; }
    /// <summary>
    /// The bindings the run started, stopped, reset, invalidated or refused. Any other is in the state the mission load leaves it
    /// (<see cref="AnimationPlayer.InitialState"/>) as far as the preview knows: the preview does not run the mission's ON_STARTUP
    /// and NEW_GAME_START entries, so it reports them stopped or not started where the game runs them.
    /// </summary>
    public IReadOnlyList<AnimationBindingStatus> Bindings { get; init; } = [];
    public IReadOnlyList<AnimationStartRefusal> Refusals { get; init; } = [];
    public long RefusalsDropped { get; init; }
    /// <summary>Each start attempt's activation prerequisites (met, refused or ignored), oldest first.</summary>
    public IReadOnlyList<AnimationPrerequisiteStart> PrerequisiteStarts { get; init; } = [];
    public long PrerequisiteStartsDropped { get; init; }
    public IReadOnlyList<AnimationSkippedSpan> Skipped { get; init; } = [];
    public long SkippedDropped { get; init; }
    /// <summary>Each sequence state change of the run, oldest first (the Timing view's lane bands).</summary>
    public IReadOnlyList<AnimationSequencePhase> Phases { get; init; } = [];
    public long PhasesDropped { get; init; }
    /// <summary>The player reference at this frame.</summary>
    public AnimationPlayerReferenceState? PlayerReference { get; init; }
    /// <summary>The reference at the start, then each change (callbacks 10, 11 and 17) in order.</summary>
    public IReadOnlyList<AnimationPlayerReferenceState> PlayerReferences { get; init; } = [];
    public long PlayerReferencesDropped { get; init; }
    /// <summary>The callback node of each instance whose binding tests a range (a PLAYER_RANGE condition or a range gate).</summary>
    public IReadOnlyList<AnimationAttachPosition> Attaches { get; init; } = [];
}

/// <summary>Retail event-clock semantics on a deterministic, device-independent 60 Hz clock.</summary>
public sealed partial class AnimationPlayer
{
    public const double StepSeconds = 1.0 / 60;
    public const int MaximumInstances = 256;
    private readonly AnimationPreviewContext context;
    private AnimationEntryLookup entryLookup = null!;
    private readonly int entryIndex;
    private readonly bool resetPhase;
    private readonly AnimationSchedule? schedule;
    private int nextScheduled;
    public AnimationSchedule? Schedule => schedule;
    public bool OriginCollision { get; init; }
    public int LodLevel { get; set; }
    private List<Instance> instances = [];
    private Dictionary<int, Node> sharedNodes = [];
    private bool initializingScene;
    /// <summary>Entries and roots the mission load marked running without an instance in this player.</summary>
    private HashSet<(int Entry, int Root)>? loadRunning;
    private AnimationBindingOperation? bindingOperation;
    private List<Effect> effects = [];
    private List<AnimationTrace> trace = [];
    private List<AnimationConditionResult> conditions = [];
    private long conditionsDropped;
    private bool hasExcludedCalls;
    private HashSet<string> notes = [];
    private Dictionary<long, AnimationLight> lights = [];
    private Dictionary<long, AnimationSoundCue> activeSounds = [];
    private readonly List<AnimationSoundCue> cues = [];
    private SortedDictionary<long, Checkpoint> checkpoints = [];
    private bool resetInitialized;
    private long ticks, nextId, traceOrdinal, traceDropped;
    private uint randomState;
    private int randomIndex;
    private readonly float[] randomTable = new float[200];
    private Vector4 screenColor, screenWave;
    private AnimationFog? fog;
    private int dispatchBudget;
    public int Seed { get; }
    public int EffectLevel { get; init; } = 2;
    /// <summary>Finite preview-only child admission. Null retains the ordinary runtime's unrestricted calls.</summary>
    private AnimationExecutionPolicy? executionPolicy;
    /// <summary>
    /// The scenario's child admission, condition overrides and rules (<see cref="AnimationExecutionPolicy.Rules"/>). A policy
    /// whose rule checks the scenario's own starts replays the reset, which admitted the starts due at zero.
    /// </summary>
    public AnimationExecutionPolicy? ExecutionPolicy
    {
        get => executionPolicy;
        init
        {
            RequireState(value?.RetainedBytes ?? 0); executionPolicy = value; initialReference = null;
            if (resetInitialized && value?.Rules.Prerequisites == AnimationPrerequisiteRule.Check) Reset(default);
        }
    }
    /// <summary>The point beams aim at (OBJECT_CONNECTOR): the scenario's custom player; range tests use <see cref="AnimationScenarioRules.Player"/>.</summary>
    public Vector3? ReferencePosition { get; init; }
    public Vector3? ActivationStart { get; init; }
    /// <summary>Preview-only mesh contact with the infinite world Y=0 plane.</summary>
    public bool GroundPlaneEnabled { get; init; }
    /// <summary>Static standable map geometry, captured at preview start. Takes precedence over the flat plane.</summary>
    public AnimationTerrain? Terrain { get; init; }
    /// <summary>Stop procedural translations at world Y -500 or 500; authored keyframes are unaffected.</summary>
    public bool ProceduralLimitsEnabled { get; init; }
    private float previewHeight;
    /// <summary>Preview-only world-Y offset, in game units. Authored coordinates remain unchanged.</summary>
    public float PreviewHeight
    {
        get => previewHeight;
        init
        {
            if (!float.IsFinite(value) || value is < -999 or > 999)
                throw new ArgumentOutOfRangeException(nameof(value), "Preview height must be between -999 and 999 game units.");
            previewHeight = value;
        }
    }
    public double Time => ticks * StepSeconds;
    private bool PendingStarts => schedule != null && nextScheduled < schedule.Starts.Count;
    public bool IsComplete => !PendingStarts && instances.All(i => i.Finished) && effects.Count == 0;
    public bool HasExcludedCalls => hasExcludedCalls;
    public bool HasBlockedDependencies => instances.Any(i => !i.Finished && i.Sequences.Any(s => s.State == 5));
    /// <summary>Excluded waited calls remain, with no runnable work able to change their dependency boundary.</summary>
    public bool IsDependencyBlocked => !PendingStarts && HasBlockedDependencies && effects.Count == 0 &&
        instances.Where(i => !i.Finished).All(i => i.StopDelay < 0 && i.Sequences.All(s =>
            s.State != 0 && (s.State != 1 || s.Work is { Type: 24 } call && (call.I16(46) & 16) != 0 &&
                instances.Any(child => child.Id == s.Child && !child.Finished))));

    public AnimationPlayer(AnimationPreviewContext context, int entryIndex, int seed = 1, bool resetPhase = false)
        : this(context, entryIndex, seed, resetPhase, default) { }
    public AnimationPlayer(AnimationPreviewContext context, int entryIndex, int seed, bool resetPhase, CancellationToken token)
        : this(context, entryIndex, seed, resetPhase, token, null) { }
    public AnimationPlayer(AnimationPreviewContext context, int entryIndex, int seed, bool resetPhase, CancellationToken token, AnimationSchedule? schedule)
    {
        this.context = context; this.entryIndex = entryIndex; this.resetPhase = resetPhase; this.schedule = schedule; Seed = seed;
        if (schedule != null && schedule.Starts.Single(s => s.Id == Guid.Empty).Entry != entryIndex)
            throw new InvalidDataException("The schedule primary must match the preview binding.");
        Reset(token);
    }
    private AnimationPlayer(AnimationPreviewContext context, AnimationBindingOperation operation)
    {
        this.context = context; entryIndex = 0; Seed = 1; bindingOperation = operation; initializingScene = true;
        entryLookup = operation.EntryLookup();
        randomState = 1;
        for (int i = 0; i < randomTable.Length; i++) { randomState = unchecked(randomState * 214013 + 2531011); randomTable[i] = ((randomState >> 16) & 32767) / 32767f; }
    }
    /// <summary>
    /// This reset's name and subtree indexes, kept across playback ticks: the context's scene and entries are frozen while a
    /// player uses them (edits create a new context), and a reset, scene replacement or node-count change rebuilds them.
    /// </summary>
    private AnimationBindingOperation.Indexes bindingIndexes = new();
    private IDisposable BeginBinding(CancellationToken token)
    {
        var previous = bindingOperation;
        // Kept indexes count against every operation that uses them, so a tick still fits its allowance;
        // past half of it they are rebuilt rather than grown without bound.
        if (bindingIndexes.Retained > AnimationBindingOperation.Allowance(context) / 2) bindingIndexes = new();
        AnimationBindingOperation next = new(context, token, shared: bindingIndexes);
        next.ReserveRetained();
        bindingOperation = next;
        return new BindingScope(() => bindingOperation = previous);
    }
    private sealed class BindingScope(Action restore) : IDisposable { public void Dispose() => restore(); }
    public void Reset() => Reset(default);
    public void Reset(CancellationToken token)
    {
        if (resetInitialized) { ReplaceResetState(token); return; }
        using var binding = BeginBinding(token);
        // The GUI may reset a player after accepted edits; never retain an index of the previous entry names.
        entryLookup = bindingOperation!.EntryLookup();
        InvalidateRetainedState();
        ticks = nextId = traceOrdinal = traceDropped = 0; previewIssues.Clear(); omittedIssues = []; diagnosticContext = new(entryIndex); measuredEnd = 0; unavailableDuration = false; randomIndex = 0; randomState = unchecked((uint)Seed);
        for (int i = 0; i < randomTable.Length; i++) { randomState = unchecked(randomState * 214013 + 2531011); randomTable[i] = ((randomState >> 16) & 32767) / 32767f; }
        instances = []; sharedNodes = []; effects = []; trace = []; conditions = []; conditionsDropped = 0; hasExcludedCalls = false;
        entryRuns = []; refusals = []; refusalsDropped = refusalBytes = 0; skipped = []; skippedDropped = 0; referenceChanges = []; referenceChangesDropped = 0;
        prerequisiteStarts = []; prerequisiteStartsDropped = prerequisiteBytes = 0; phases = []; phasesDropped = 0;
        notes = []; foreach (string message in context.Diagnostics) AddNote(message, "Resource", "Unclassified"); lights = []; activeSounds = []; cues.Clear(); checkpoints.Clear();
        screenColor = screenWave = Vector4.Zero; fog = null;
        nextScheduled = 0; originTerrain.Clear(); dispatchBudget = 10000;
        if (schedule == null) AddInstance(context.Package.Entries[entryIndex], null, null, resetPhase, scenarioStart: true);
        else StartScheduled();
        RequireState(StateBytes(instances, sharedNodes) + ConditionBytes(conditions.Count) + CloneScratch(instances, sharedNodes));
        checkpoints[0] = CaptureCheckpoint(token);
        InvalidateRetainedState();
        resetInitialized = true;
    }
    public AnimationFrame Step(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // After an interrupted tick, the step reaches that tick again from the last complete one.
        long target = interrupted ? ticks : ticks + 1;
        if (interrupted) Rewind(ticks - 1, token);
        cues.Clear();
        while (ticks < target) Tick(token);
        return Frame(token);
    }
    public AnimationFrame AdvanceTo(double seconds, bool seeking = false, CancellationToken token = default)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 3600) throw new InvalidDataException("Preview time must be between zero and one hour.");
        token.ThrowIfCancellationRequested();
        long target = (long)Math.Floor(seconds / StepSeconds + 1e-7);
        if (target < ticks || interrupted) Rewind(interrupted ? Math.Min(target, ticks - 1) : target, token);
        cues.Clear();
        while (ticks < target) { token.ThrowIfCancellationRequested(); Tick(token); }
        if (seeking) cues.Clear();
        return Frame(token);
    }
    /// <summary>
    /// A tick an allowance refused part-way: it applied only some of its changes, so no frame or tick continues from it.
    /// The next seek, step or playback replays from a checkpoint before it; a reset or restore replaces it.
    /// </summary>
    private bool interrupted;
    private const string InterruptedRefusal = "The preview stopped inside a refused animation step. Seek or play to replay it from an earlier point.";
    /// <summary>The latest checkpoint at or before <paramref name="target"/>, else the reset state; either replaces the whole runtime state.</summary>
    private void Rewind(long target, CancellationToken token)
    {
        var closest = checkpoints.LastOrDefault(p => p.Key <= target);
        if (closest.Value == null) { Reset(token); return; }
        // A restore stages its copy beside the state it replaces. When the allowance cannot hold both, a reset (which
        // keeps only the zero snapshot, and rebuilds a refused tick's state as a new player's) replaces it instead,
        // and the caller replays from there.
        try { Restore(closest.Value, token); }
        catch (StateLimitException) { Reset(token); }
    }
    private void Tick(CancellationToken token)
    {
        // Cancellation is observed only between ticks. Events, child activations and their lookups change the
        // runtime state as a tick proceeds, and a reused player must stay at a state an uninterrupted run reaches.
        // The dispatch limit and the binding allowance bound one tick's work instead; a refusal that escapes them
        // marks the tick interrupted.
        token.ThrowIfCancellationRequested();
        using var binding = BeginBinding(CancellationToken.None);
        ReleaseReplacedSequences();
        interrupted = true;
        StartScheduled();
        ticks++; dispatchBudget = 10000; screenColor = screenWave = Vector4.Zero;
        foreach (var instance in instances.ToArray())
        {
            if (instance.Finished) continue;
            bool stopping = instance.StopDelay >= 0;
            // RunSequence is gated (range, zone); a stop delay and a cleanup run anywhere. A held binding's clocks freeze.
            if (!stopping && !instance.Cleanup && !initializingScene && Gate(instance) is { } held)
            {
                SetPaused(instance, held);
                // Its clocks wait for the player or the view's zones: nothing it does from now on runs on authored time.
                if (measuringDuration) MarkUntimed(instance, null, held == "zone" ? "zone" : "range");
                continue;
            }
            instance.Paused = null;
            instance.Elapsed += (float)StepSeconds;
            if (stopping)
            {
                instance.StopDelay -= (float)StepSeconds;
                if (instance.StopDelay <= 0) BeginCleanup(instance);
                else continue;
                if (instance.Finished) continue;
            }
            foreach (var sequence in instance.Sequences.ToArray())
            {
                if (instance.Finished || instance.StopDelay >= 0) break;
                if (sequence.State is 0 or 1 && !sequence.Retired) Run(instance, sequence);
            }
            if (instance.Finished || instance.StopDelay >= 0) continue;
            if (instance.Sequences.All(s => s.State is not (0 or 1 or 5)))
            {
                // Its natural end is when its last event completed, on that event's timeline.
                if (measuringDuration) InheritEnd(instance);
                // Natural completion stops the entry (0x45c040): a negative RESET_TIME keeps the final pose, zero resets at once.
                if (instance.Cleanup || instance.Entry.F32(164) < 0) Finish(instance);
                else { instance.StopDelay = instance.Entry.F32(164); if (instance.StopDelay == 0) BeginCleanup(instance); }
            }
        }
        ReleaseReplacedSequences();
        RecordPhases();
        // Landed debris remains above the solid preview plane during subsequent
        // shrink/sink stages and concurrent transform events, without new impacts.
        ConstrainSettledGround();
        foreach (var effect in effects)
        {
            effect.Age += (float)StepSeconds;
            effect.Scale = Math.Max(.01f, effect.Scale + (effect.Age < .3f ? 6 : -3.75f) * (float)StepSeconds);
        }
        effects.RemoveAll(e => e.Age >= 1);
        // Keep live one-shots in snapshots too: unmuting or resuming must not lose
        // a sound just because its event fired in an earlier presentation frame.
        foreach (var (id, cue) in activeSounds.ToArray())
            if (!cue.Persistent && Time - cue.StartedAt >= context.Sounds[cue.Name].Duration)
                activeSounds.Remove(id);
        // Keep completed root geometry as the final pose, but release nested instances.
        if (instances.RemoveAll(i => i.Finished && i.Id != 1 && !i.Scheduled && !instances.Any(p => p.Children.Values.Contains(i.Id))) > 0)
            InvalidateRetainedState();
        interrupted = false;
        // The tick is complete; canceling its optional checkpoint only leaves a later seek to replay from an earlier one.
        if (!measuringDuration && ticks % 60 == 0) SaveCheckpoint(token);
    }
    private void StartScheduled()
    {
        if (schedule == null) return;
        while (nextScheduled < schedule.Starts.Count && schedule.Starts[nextScheduled].Tick <= ticks)
        {
            var start = schedule.Starts[nextScheduled++];
            // A scheduled start is a timed point: the scenario's own clock.
            if (measuringDuration) MeasurePoint(TimedTimeline, Time);
            var instance = AddInstance(context.Package.Entries[start.Entry], null, null, resetPhase,
                callPath: start.Path, startedAt: Time, scenarioStart: true);
            if (instance != null) instance.Scheduled = true;
            else AddNote($"Scheduled activation {start.Id}: entry #{start.Entry} could not start ({RefusalText(lastRefusal)}).", "Preview", "Information");
        }
    }
    private void Run(Instance instance, Sequence state, float step = (float)StepSeconds)
    {
        if (!measuringDuration) { RunEvents(instance, state, step); return; }
        // Duration analysis follows which sequence dispatches: what an event starts, releases or stops inherits its timeline.
        var (previous, previousInstance) = (dispatching, dispatchingInstance);
        dispatching = state; dispatchingInstance = instance;
        try { RunEvents(instance, state, step); }
        finally { dispatching = previous; dispatchingInstance = previousInstance; }
    }
    private void RunEvents(Instance instance, Sequence state, float step)
    {
        using var diagnosticScope = PushDiagnostic(new(instance.Entry.Index, instance.Id, state.Data.Id));
        float remaining = step;
        if (state.State == 1 || state.Cursor > 0) { state.Elapsed += remaining; state.EventElapsed += remaining; }
        while (state.State is 0 or 1)
        {
            if (--dispatchBudget <= 0) { Block(state, "Event dispatch limit reached; check zero-time loops."); return; }
            if (state.Cursor >= state.Data.Events.Count) { state.State = state.Data.IsEditable ? 2 : 4; unavailableDuration |= state.State == 4; return; }
            var ev = state.Data.Events[state.Cursor];
            diagnosticContext = diagnosticContext with { Event = ev.Id };
            if (ev.Spec == null || ev.Bytes.Length < ev.Spec.Size || ev.StartMode is < 1 or > 3 || !float.IsFinite(ev.Threshold))
            { Block(state, $"{ev.Name}: unverified or malformed event; this sequence is paused."); return; }
            bool starting = state.State == 0;
            if (starting)
            {
                float clock = ev.StartMode switch { 1 => instance.Elapsed, 2 => state.Elapsed, _ => state.EventElapsed };
                if (clock < ev.Threshold) return;
                if (!AdmitState(WorkBytes(ev))) { Block(state, RetainedStateRefusal); return; }
                state.EventElapsed = remaining; if (state.Cursor == 0) state.Elapsed = remaining;
                state.Work = ev.Clone(); state.Child = -1; state.KeyOffset = 0; state.KeyTime = 0;
                CountWork(EventStartWork);
                if (trace.Count >= 20000) { trace.RemoveRange(0, 10000); traceDropped += 10000; }
                trace.Add(new(instance.Id, state.Data.Id, ev.Id, ev.Name, Math.Max(0, Time - remaining), null, ev.Spec.Support)
                    { Occurrence = ++traceOrdinal, Entry = instance.Entry.Index, InstancePath = instance.CallPath });
                if (ev.Spec.Support != "Engine-based") AddNote(ev.Spec.Support, "Support", "Information");
                if (measuringDuration) MeasureDispatch(instance, state, Math.Max(0, Time - remaining));
            }
            CountWork(EventWork + (ev.Type is 19 or 24 ? CallWork : 0));
            int result;
            var work = state.Work!;
            try { result = Execute(instance, state, work, starting, ref remaining); }
            catch (Exception ex) when (ex is InvalidDataException or StateLimitException or ArgumentException or ArithmeticException or IndexOutOfRangeException)
            { Block(state, $"{ev.Name}: {ex.Message}"); return; }
            if (measuringDuration) MeasureResult(instance, state, work, starting, result, Math.Max(0, Time - remaining));
            if (result == 0) return; // Loop/reset explicitly changed the cursor and clocks.
            state.State = result;
            if (result != 2) return;
            int tr = trace.FindLastIndex(t => t.Instance == instance.Id && t.Event == ev.Id && t.End == null);
            if (tr >= 0) trace[tr] = trace[tr] with { End = Time - remaining };
            measuredEnd = Math.Max(measuredEnd, Time - remaining);
            state.Cursor++; state.EventElapsed = 0; state.Work = null;
            if (state.Cursor < state.Data.Events.Count) state.State = 0;
            else if (state.Data.ResetMode == 3) { state.Reset(); return; }
            // The event stopped, reset or finished its own animation: its sequences freeze at once (a stop delay or cleanup
            // replaces them).
            if (state.Retired || instance.Finished || instance.StopDelay >= 0) return;
        }
    }
    private void Block(Sequence sequence, string message) { sequence.State = 4; unavailableDuration = true; blockedSequences++; AddNote(sequence.Data.Name + ": " + message); }
    /// <summary>How many sequences an unavailable event stopped (see <see cref="Block"/>).</summary>
    private int blockedSequences;
    private float RandomUnit() { float result = randomTable[randomIndex]; randomIndex = (randomIndex + 1) % randomTable.Length; return result; }

    /// <param name="started">The instance is a child started at <paramref name="boundRoot"/> (ActivateRuntime binds it there
    /// again even when it is the entry's own root), not a stop at it (StopAndCleanup, which turrets call, only at another node).</param>
    /// <param name="prerequisitesCleared">The NEW_GAME_START list clears the entry's activation prerequisites first (0x4192d0).</param>
    /// <param name="primary">The instance runs the entry's cleanup only (a load, turret or reset cleanup, or the Cleanup phase): not an activation.</param>
    /// <param name="scenarioStart">One of the scenario's own starts, which the prerequisite rule may exempt.</param>
    /// <param name="activateRoot">Show the bound root, as a preview start does; a reset's cleanup leaves it as it is.</param>
    private Instance? AddInstance(AnimationEntry entry, Vector3? position, int? boundRoot, bool primary = false, bool started = false, bool prerequisitesCleared = false,
        string callPath = "", long? parentInstance = null, Guid? callEvent = null, double? startedAt = null, bool scenarioStart = false, bool activateRoot = true)
    {
        var operation = bindingOperation ?? throw new InvalidOperationException("Animation binding requires an active operation.");
        operation.Reserve(1);
        lastRefusal = null;
        // ActivateRuntime (0x45d930 [B]) refuses an invalidated or unavailable entry (states 4–6), and a one-shot entry
        // (RESET_TIME -99) while it runs or keeps its final pose.
        var run = EntryState(entry);
        if (!primary && run.State is 4 or 5 or 6)
        { RefuseStart(entry, run.State == 5 ? "unavailable" : "invalidated", callPath, boundRoot ?? -1, parentInstance, callEvent, scenarioStart); return null; }
        if (!primary && run.State is 2 or 3 && Math.Abs(entry.F32(164) + 99) < .1f)
        { RefuseStart(entry, "oneShot", callPath, boundRoot ?? -1, parentInstance, callEvent, scenarioStart); return null; }
        if (instances.Count + effects.Count >= MaximumInstances)
        { unavailableDuration = true; AddNote("Preview instance limit reached (256). A looping emitter may be producing too many children."); return null; }
        int root = boundRoot ?? operation.Root(entry);
        if (root < 0) { unavailableDuration = true; AddNote($"Unresolved animation root: {entry.RootName}"); return null; }
        if (initializingScene && !primary && !prerequisitesCleared && entry.References[6].Count > 0)
        { AddNote($"{entry.Name}: initialization activation prerequisites are unavailable."); return null; }
        bool shared = IsWorldNode(root) && ((entry.U32(148) & 0x8000) == 0 || boundRoot.HasValue);
        // ActivateRuntime never starts a running animation again at the same node, nor without a target node one whose base
        // runs elsewhere, nor, at mission load, one that load marked running before the first frame (ApplyInitialization).
        if (shared && (instances.Any(i => i.Entry.Index == entry.Index && i.Root == root && !i.Finished) || loadRunning?.Contains((entry.Index, root)) == true
            || !primary && !boundRoot.HasValue && run.State is 2 or 6 && LiveBase(entry) != null))
        { if (!primary) RefuseStart(entry, "alreadyRunning", callPath, root, parentInstance, callEvent, scenarioStart); return null; }
        if (!primary && !initializingScene && entry.References[6].Count > 0)
        {
            var prerequisites = EvaluatePrerequisites(entry);
            bool check = Rules.Prerequisites switch
            {
                AnimationPrerequisiteRule.Check => true, AnimationPrerequisiteRule.IgnoreForScenarioStarts => !scenarioStart, _ => false,
            };
            // Evidence of what the game checks on every start: met, refused, or ignored by the scenario's rule.
            RecordPrerequisites(entry, prerequisites.Passed ? "met" : check ? "refused" : "ignored", prerequisites, callPath, root, parentInstance, callEvent, scenarioStart);
            if (!prerequisites.Passed)
            {
                if (check) { RefuseStart(entry, "prerequisites", callPath, root, parentInstance, callEvent, scenarioStart, prerequisites); return null; }
                AddNote($"{entry.Name}: started although its activation prerequisites are unmet ({prerequisites.Matched} of {prerequisites.Minimum} options met); the scenario ignores them for this start.", "Support", "Information");
            }
        }
        // A start while the base record runs (at another node, or a copied root) is a clone; otherwise it activates the base.
        bool activation = !primary && run.State is not (2 or 6);
        bool cleanupFirst = activation && (entry.U32(148) & 0x20) != 0 && run.State != 1 && entry.Primary.Events.Count > 0;
        var binding = context.Binding(entry, root, operation);
        if (boundRoot.HasValue && (started || binding != AnimationBinding.Loaded)) binding = AnimationBinding.Rebound;
        if (context.RebindDisables(entry, root, binding, operation))
            AddNote($"{entry.Name}: the game disables this animation bound at {context.Scene.Nodes[root].Name}, which lacks its attach node {entry.AttachName}; the preview runs it there.", "Support", "Information");
        var descendants = operation.Descendants(root, context.Scene.Nodes.Count);
        operation.Bound(entry, root, descendants.Count);
        operation.Reserve(40L * descendants.Count);
        var sourceNodes = descendants.ToHashSet();
        for (int r = 1; r < entry.References[1].Count; r++)
        {
            // As events find them (NodeRef), so every node an event changes belongs to the instance.
            int node = context.ResolveInstanceNode(entry, r, root, binding, operation);
            // Per-instance mission resets must not fall back to another turret's
            // identically named component when this instance lacks that part.
            if (initializingScene && boundRoot.HasValue && !sourceNodes.Contains(node)) continue;
            // Every existing member already brought its full transitive subtree into this set.
            if (node >= 0 && !sourceNodes.Contains(node))
                foreach (int child in operation.Descendants(node, context.Scene.Nodes.Count))
                { operation.Reserve(40); sourceNodes.Add(child); }
        }
        // Admit the whole prospective instance before allocating Nodes/SavedNodes or changing shared poses.
        AdmitInstance(sourceNodes, shared, primary ? 1 : entry.Sequences.Count, callPath);
        operation.Reserve(256L * sourceNodes.Count + 32L * entry.Sequences.Count);
        Instance instance = new() { Id = ++nextId, Entry = entry, Root = root, Binding = binding, Cleanup = primary, Shared = shared,
            CallPath = callPath, ParentInstance = parentInstance, CallEvent = callEvent, StartedAt = startedAt ?? Time };
        // Duration analysis: a binding an event starts (a call, a reset's cleanup) begins on that event's timeline; a scheduled start on authored time.
        if (measuringDuration && dispatching != null) Restart(instance, DispatchTimeline);
        foreach (int index in sourceNodes)
        {
            operation.Reserve(256L + context.Scene.Nodes[index].Parents.Length);
            if (shared) { instance.Nodes[index] = SharedNode(index, admitted: true); continue; }
            var source = context.Scene.Nodes[index]; int parent = source.Parents.FirstOrDefault(p => sourceNodes.Contains(p), -1);
            var matrix = parent < 0 ? context.WorldTransform(index, operation) : SceneBuilder.LocalTransform(source);
            var node = new Node { Source = index, Parent = parent, Exact = matrix, Position = matrix.Translation, Scale = Vec(source.Data["scale"], Vector3.One), Euler = Vec(source.Data["rotate"]), Active = source.Class != "object3d" || (source.Metadata.UInt("flags") & 4) != 0, Alpha = source.Data.Float("opacity", 1), AlphaEnabled = (source.Data.UInt("flags") & 2) != 0 };
            node.Rotation = AnimationMath.FromEuler(node.Euler);
            if ((parent < 0 || source.Class == "camera") && Matrix4x4.Decompose(matrix, out var scale, out var rotation, out _))
            { node.Scale = scale; node.Rotation = rotation; node.Euler = AnimationMath.ToEuler(rotation); }
            if (source.Class == "camera") node.Fov = source.Data.Float("fov_h_base", MathF.PI / 3);
            instance.Nodes[index] = node;
            node.RenderId = (instance.Id << 32) | (uint)index;
        }
        if (instance.Nodes.TryGetValue(root, out var rootNode))
        {
            if (!initializingScene && activateRoot) rootNode.Active = true;
            // Explicitly inspecting an unplaced actor's destruction/effect still
            // needs a local preview. World controllers never enable dormant actors.
            if (!initializingScene && (schedule == null ? instance.Id == 1 : callPath.Length == 0 && entry.Index == entryIndex) && rootNode.PendingPlacement && !entry.Sequences.SelectMany(s => s.Events).Any(e =>
                e.Spec != null && e.Bytes.Length >= e.Spec.Size && (e.Type == 12 && context.ResolveInstanceNode(entry,e.I32(12),root,binding,operation) == root || e.Type == 7 && context.ResolveInstanceNode(entry,e.I16(28),root,binding,operation) == root)))
            { rootNode.PendingPlacement = false; AddNote("The selected actor has no recovered starting position; this individual preview uses its stored pose.", "Support", "Information"); }
            if (position is Vector3 p) { Position(rootNode, p); rootNode.Parent = -1; }
        }
        instances.Add(instance);
        InvalidateRetainedState();
        // A flag-0x20 entry not stopped (in practice finished with its pose kept) runs its cleanup first, without the rest
        // pose (StopAndCleanup 0x45d570 from ActivateRuntime); the rest pose is captured after it (ResetForNode 0x45d6c0).
        if (cleanupFirst) ActivationCleanup(instance);
        instance.SavedNodes = instance.Nodes.ToDictionary(p => p.Key, p => { operation.Reserve(1); return p.Value.Clone(); });
        instance.Sequences = (primary ? new[] { entry.Primary } : entry.Sequences.ToArray()).Select(s => new Sequence(s) { WorkChanged = AdjustRetainedWork, Created = instance.StartedAt }).ToList();
        if (activation) SetEntryState(entry, new(2, instance.Id, root, instance.SavedNodes, Time, "start", callPath));
        InvalidateRetainedState();
        return instance;
    }
    /// <summary>The zero-time part of an entry's cleanup on <paramref name="instance"/>, then what FinalizeStop releases when it ends there.</summary>
    private void ActivationCleanup(Instance instance)
    {
        var cleanup = new Sequence(instance.Entry.Primary) { WorkChanged = AdjustRetainedWork };
        instance.Sequences = [cleanup]; instance.Cleanup = true; InvalidateRetainedState();
        RunZeroTime(instance, cleanup);
        if (cleanup.State is not (0 or 1 or 5)) Release(instance);
        else AddNote($"{instance.Entry.Name}: its cleanup outlasts zero time; starting it again abandons the rest, as in the game.", "Support", "Information");
        cleanup.Work = null; cleanup.Retired = true;
        instance.Cleanup = false; instance.Sequences = [];
    }
    /// <summary>Runs a cleanup's zero-time events at once, as StopAndCleanup does; nested cleanups past a small depth wait for the next tick.</summary>
    private bool RunZeroTime(Instance instance, Sequence sequence)
    {
        if (cleanupDepth >= 16) return false;
        cleanupDepth++;
        try { Run(instance, sequence, 0); }
        finally { cleanupDepth--; }
        return true;
    }
    private Node? NodeRef(Instance instance, int reference)
    {
        if (reference == 0) return null;
        var entry = instance.Entry; int index = bindingOperation is { } operation
            ? context.ResolveInstanceNode(entry, reference, instance.Root, instance.Binding, operation)
            : context.ResolveInstanceNode(entry, reference, instance.Root, instance.Binding);
        if (instance.Nodes.TryGetValue(index, out var node)) return node;
        // RECOIL's ResolveNodeByName finds the entry's own light or sound node before the world's: no scene node. MW3 has no
        // such evidence, so its unresolved names stay warnings.
        if (index < 0 && context.World.Game != GameVariant.MechWarrior3 && reference > 0 && reference < entry.References[1].Count
            && entry.References[1][reference].Text(0, 36) is var name && AnimationPreviewContext.OwnsNode(entry, name))
            AddNote($"{entry.Name}: node reference {reference} is the animation's own light or sound {name}, which the preview does not show as a node.", "Support", "Information");
        else AddNote($"{entry.Name}: unresolved node reference {reference}.");
        return null;
    }
    private Matrix4x4 World(Instance instance, Node node)
    {
        bindingOperation?.Reserve(64);
        context.InitializationTransforms?.Reads.Add(node.Source);
        Matrix4x4 matrix = node.Local; HashSet<int> seen = [node.Source]; int parent = node.Parent;
        while (parent >= 0)
        {
            bindingOperation?.Reserve(64);
            if (!seen.Add(parent) || ParentNode(instance, parent) is not { } p) break;
            context.InitializationTransforms?.Reads.Add(p.Source);
            matrix *= p.Local; parent = p.Parent;
        }
        return matrix;
    }
    /// <summary>
    /// The reset path (<c>zEffAnimReset</c> 0x45d6b0 → <c>StopAndCleanup</c> 0x45d570 with immediate cleanup [B, BN]): every
    /// reset restores the tracked nodes' rest pose captured at activation (flag 0x40 is set at run time, never stored),
    /// replaces the runtime sequences with the cleanup and runs its zero-time events at once; timed ones continue per tick.
    /// A reset during a cleanup restarts it.
    /// </summary>
    private void BeginCleanup(Instance instance) => BeginCleanup(instance, instance.SavedNodes);
    private void BeginCleanup(Instance instance, Dictionary<int, Node>? rest)
    {
        RequireState(SequenceBytes);
        instance.StopDelay = -1; instance.Cleanup = true;
        // The cleanup's own events end the binding now (on its timeline: the stop's, the reset's or its natural end's).
        if (measuringDuration) { instance.EndTimeline = 0; instance.EndTick = -1; }
        RestoreRestPose(instance, rest);
        // The dispatch snapshot can still hold the replaced sequences until the frontier finishes.
        foreach (var replaced in instance.Sequences) replaced.Retired = true;
        RecordStopped(instance, instance.Sequences);
        retiredSequences.Add(instance.Sequences);
        var cleanup = new Sequence(instance.Entry.Primary) { WorkChanged = AdjustRetainedWork, Created = Time };
        instance.Sequences = [cleanup];
        InvalidateRetainedState();
        if (instance.Entry.Primary.Events.Count == 0)
        {
            measuredEnd = Math.Max(measuredEnd, Time - StepSeconds);
            if (measuringDuration) MeasurePoint(instance.Timeline, Time - StepSeconds);
            Finish(instance); return;
        }
        if (RunZeroTime(instance, cleanup) && !instance.Finished && cleanup.State is not (0 or 1 or 5)) Finish(instance);
    }
    /// <summary>
    /// RestoreNodeStates: the tracked nodes' active flag, transform and morph (off) as captured. <paramref name="rest"/> is the
    /// capture; null is the mission load's capture, the baseline, for an entry that never ran.
    /// </summary>
    private void RestoreRestPose(Instance instance, Dictionary<int, Node>? rest)
    {
        foreach (var tracked in instance.Entry.References[0])
        {
            // Tracked-node records hold 36-byte names, as node references do.
            int index = bindingOperation is { } operation
                ? context.ResolveTrackedNode(instance.Entry, tracked.Text(0,36), instance.Root, instance.Binding, operation)
                : context.ResolveTrackedNode(instance.Entry, tracked.Text(0,36), instance.Root, instance.Binding);
            if (!instance.Nodes.TryGetValue(index, out var node)) continue;
            Node? saved = rest != null ? rest.GetValueOrDefault(index) : instance.Shared ? BaselineNode(index) : instance.SavedNodes.GetValueOrDefault(index);
            if (saved == null) continue;
            node.Active = saved.Active; node.Position = saved.Position; node.Euler = saved.Euler; node.Scale = saved.Scale;
            node.Rotation = saved.Rotation; node.Exact = saved.Exact; node.Changed = saved.Changed; node.Morph = 0;
            node.PendingPlacement = saved.PendingPlacement; node.PositionWritten = saved.PositionWritten;
            node.GroundSupported = saved.GroundSupported;
            node.GroundHeight = saved.GroundHeight;
        }
    }
    /// <summary>
    /// FinalizeStop (0x45d3d0 [B]): frees the instance's lights and sounds and stops its LOCAL_NAME children; the base record
    /// becomes stopped after a cleanup, or keeps its final pose (state 3) otherwise; an invalidated one stays invalidated.
    /// </summary>
    private void Finish(Instance instance)
    {
        if (instance.Finished) return;
        instance.Finished = true;
        if (measuringDuration) endedTimelines[instance.Id] = Math.Max(instance.Timeline, instance.EndTimeline);
        RecordFinished(instance);
        Release(instance);
        var run = EntryState(instance.Entry);
        if (run.Base == instance.Id)
            SetEntryState(instance.Entry, run with
            {
                State = run.State switch { 4 or 6 => 4, 5 => 5, _ => instance.Cleanup ? (byte)1 : (byte)3 },
                Time = Time, Cause = instance.Cleanup ? "stopped" : "finished"
            });
    }
    private void Release(Instance instance)
    {
        foreach (long key in activeSounds.Keys.Where(k => k >> 32 == instance.Id).ToArray()) { cues.Add(activeSounds[key] with { Stop = true }); activeSounds.Remove(key); }
        foreach (long key in lights.Keys.Where(k => k >> 32 == instance.Id).ToArray()) lights.Remove(key);
        if (instance.Locals.Count == 0) return;
        // Each launch with LOCAL_NAME caches its child (call record +64 = 1); FinalizeStop stops those, each by its own RESET_TIME.
        var table = instance.Entry.References[7];
        var children = instance.Locals.ToArray();
        instance.Locals.Clear(); InvalidateRetainedState();
        foreach (var (slot, id) in children)
            if (slot >= 0 && slot < table.Count && table[slot].Bytes.Length >= 68 && table[slot].I32(64) == 1
                && instances.FirstOrDefault(i => i.Id == id) is { Finished: false } child)
            {
                // Stopped when its launcher ends: on the launcher's end's timeline (an event's, when one stops it).
                if (measuringDuration && dispatching == null) Restart(child, instance.Timeline);
                StopInstance(child);
            }
    }
    /// <summary>
    /// Stop (0x45c040 [B]) of a running instance: a RESET_TIME of zero resets it at once, a positive one after that delay
    /// (its sequences freeze now), a negative one finishes it with its final pose.
    /// </summary>
    private void StopInstance(Instance instance)
    {
        if (instance.Finished || instance.Cleanup) return;
        // What follows a stop (its delay, its cleanup) runs on the stopping event's timeline.
        if (measuringDuration && dispatching != null) Restart(instance, DispatchTimeline);
        float reset = instance.Entry.F32(164);
        if (reset < 0 || !float.IsFinite(reset)) Finish(instance);
        else if (reset == 0) BeginCleanup(instance);
        else instance.StopDelay = reset;
    }
    /// <summary>A node as the mission load left it: the baseline scene's state.</summary>
    private Node BaselineNode(int index)
    {
        var source = context.Scene.Nodes[index]; var matrix = SceneBuilder.LocalTransform(source);
        var node = new Node
        {
            Source = index, RenderId = (uint)index, Parent = source.Parents.FirstOrDefault(-1), Exact = matrix,
            Position = matrix.Translation, Scale = Vec(source.Data["scale"], Vector3.One), Euler = Vec(source.Data["rotate"]),
            Active = source.Class is not ("object3d" or "lod") || (source.Metadata.UInt("flags") & 4) != 0,
            Alpha = source.Data.Float("opacity", 1), AlphaEnabled = (source.Data.UInt("flags") & 2) != 0,
            PendingPlacement = source.Metadata["preview_pending_placement"]?.GetValue<bool>() == true
        };
        node.Rotation = AnimationMath.FromEuler(node.Euler);
        if (source.Class == "camera" && Matrix4x4.Decompose(matrix, out var scale, out var rotation, out _))
        { node.Scale = scale; node.Rotation = rotation; node.Euler = AnimationMath.ToEuler(rotation); }
        return node;
    }
    public AnimationFrame Frame() => Frame(default);
    public AnimationFrame Frame(CancellationToken token) => Frame(token, Worlds.LookupWorkBudget.MaximumUnits);
    internal AnimationFrame Frame(CancellationToken token, long maximumWork, Action<long>? reserved = null)
    {
        if (interrupted) throw new IOException(InterruptedRefusal);
        AnimationBindingOperation frameBindings = new(context, token, maximumWork, reserved);
        frameBindings.Reserve(1);
        var ancestry = BeginFrameAncestry(frameBindings);
        List<AnimationNodePose> poses = []; AnimationCamera? camera = null; HashSet<long> rendered = [];
        foreach (var instance in instances) foreach (var (index, node) in instance.Nodes)
        {
            token.ThrowIfCancellationRequested();
            var source = context.Scene.Nodes[index];
            if (source.ModelIndex is >= 0 and int model && rendered.Add(node.RenderId))
            {
                var pose = ancestry.Get(instance, node);
                poses.Add(new(node.RenderId, index, model, pose.World, pose.Visible, pose.Opacity, node.Variant, node.Morph, Math.Max(0, Time - node.CycleStart)));
            }
            if (source.Class == "camera" && node.Changed)
            {
                // Camera.c 0x44ABF0 takes the eye from the world matrix and
                // forward from its negative Z basis, not from a stored target.
                var transform = ancestry.Get(instance, node).World;
                var forward = Vector3.TransformNormal(-Vector3.UnitZ, transform);
                if (float.IsFinite(forward.LengthSquared()) && forward.LengthSquared() > 1e-12f && float.IsFinite(node.Fov))
                    camera = new(transform.Translation, transform.Translation + Vector3.Normalize(forward), node.Fov * (180 / MathF.PI)) { SourceNode = index, Name = source.Name };
                else AddNote($"Camera {source.Name} #{index}: invalid transform or field of view; follow pose is unavailable.");
            }
        }
        // Range tests measure from each binding's callback node, which often has no model (no pose above): where the run has it.
        List<AnimationAttachPosition> attaches = [];
        foreach (var instance in instances)
        {
            token.ThrowIfCancellationRequested();
            if (attaches.Count >= MaximumInstances || !RangeTested(instance.Entry)) continue;
            int callback = context.CallbackNode(instance.Entry, instance.Root, instance.Binding, frameBindings);
            if (callback < 0 || callback >= context.Scene.Nodes.Count) continue;
            var at = ParentNode(instance, callback) is { } attachNode ? ancestry.Get(instance, attachNode).World.Translation : context.WorldTransform(callback, frameBindings).Translation;
            if (float.IsFinite(at.X) && float.IsFinite(at.Y) && float.IsFinite(at.Z)) attaches.Add(new(instance.Id, instance.Entry.Index, callback, at));
        }
        // Finished children can leave lasting world changes (for example, a filled canal).
        // Their shared nodes outlive the child instance and still replace the static world.
        if (OriginCollision && instances.FirstOrDefault(i => i.Shared) is { } owner)
            foreach (var (index, node) in sharedNodes.ToArray())
                if (context.Scene.Nodes[index].ModelIndex is >= 0 and int model && rendered.Add(node.RenderId))
                {
                    var pose = ancestry.Get(owner, node);
                    poses.Add(new(node.RenderId, index, model, pose.World, pose.Visible, pose.Opacity, node.Variant, node.Morph, Math.Max(0, Time - node.CycleStart)));
                }
        EndFrameAncestry(ancestry);
        foreach (var effect in effects)
        {
            string? texture = null; var template = effect.Template;
            if (template.Textures.Length > 0)
            {
                int frame = (int)Math.Floor(effect.Age * Math.Abs(template.Speed));
                frame = template.Loop ? frame % template.Textures.Length : Math.Min(frame, template.Textures.Length - 1);
                texture = template.Textures[frame];
            }
            Matrix4x4.Invert(context.WorldTransform(template.RootNode, frameBindings), out var rootInverse);
            foreach (int index in frameBindings.Descendants(template.RootNode, context.Scene.Nodes.Count))
            {
                frameBindings.Reserve(64);
                if (context.Scene.Nodes[index].ModelIndex is not int model || model < 0) continue;
                Matrix4x4 transform = context.WorldTransform(index, frameBindings) * rootInverse * Matrix4x4.CreateScale(effect.Scale) * Matrix4x4.CreateTranslation(effect.Position);
                bool visible = true; int child = index; HashSet<int> visited = [];
                while (child != template.RootNode)
                {
                    frameBindings.Reserve(64);
                    if (!visited.Add(child) || context.Scene.Nodes[child].Parents.FirstOrDefault(-1) is not (>= 0 and int parent)) break;
                    if (!context.Lods.Includes(parent, child, LodLevel)) { visible = false; break; }
                    child = parent;
                }
                poses.Add(new((effect.Id << 32) | (uint)index, index, model, transform, visible, 1, 0, 0, effect.Age, texture));
            }
        }
        // Keep simulation/event coordinates in the authored space. Apply the preview
        // origin once at the frame boundary, including nested effects and spatial audio.
        // GroundBottom measures against the same origin, so contact stays at world Y=0.
        Vector3 origin = new(0, PreviewHeight, 0);
        if (PreviewHeight != 0)
        {
            for (int i = 0; i < poses.Count; i++)
            {
                var transform = poses[i].Transform; transform.Translation += origin;
                poses[i] = poses[i] with { Transform = transform };
            }
            if (camera != null) camera = camera with { Position = camera.Position + origin, Target = camera.Target + origin };
        }
        token.ThrowIfCancellationRequested();
        var result = new AnimationFrame(Time, poses, lights.Values.Select(l => l with { Position = l.Position + origin }).ToArray(),
            cues.Select(c => c with { Position = c.Position + origin }).ToArray(), activeSounds.Values.Select(c => c with { Position = c.Position + origin }).ToArray(), camera, fog, screenColor, screenWave, trace.ToArray(),
            instances.SelectMany(i => i.Sequences.Select(s => new AnimationSequenceStatus(i.Id, s.Data.Id, s.Data.Name, s.Cursor, s.State switch { 0 => "Waiting for threshold", 1 => "Running", 2 => "Complete", 3 => "Waiting for release", 5 => "Awaiting excluded dependency", _ => "Unavailable" }, s.Iteration)
                { AnimationElapsed = i.Elapsed, SequenceElapsed = s.Elapsed, EventElapsed = s.EventElapsed, InstancePath = i.CallPath,
                    ReleasedBy = s.ReleasedBy, ReleasedByInstance = s.ReleasedBy == null ? null : s.ReleasedByInstance,
                    ReleasedAt = s.ReleasedBy == null ? null : s.ReleasedAt, ReleaseKind = s.ReleaseKind })).ToArray(), notes.Order(StringComparer.Ordinal).ToArray())
        {
            Issues = previewIssues.Values.ToArray(), TraceDropped = traceDropped,
            Instances = instances.Select(i => new AnimationInstanceStatus(i.Id, i.Entry.Index, i.Root, i.Entry.Name, i.CallPath,
                i.ParentInstance, i.CallEvent, i.StartedAt, i.Finished, i.Cleanup)).ToArray(),
            Conditions = conditions.ToArray(), ConditionsDropped = conditionsDropped,
            DependencyBlocked = IsDependencyBlocked, HasExcludedCalls = hasExcludedCalls,
            Bindings = BindingStatuses(), Refusals = refusals.ToArray(), RefusalsDropped = refusalsDropped,
            PrerequisiteStarts = prerequisiteStarts.ToArray(), PrerequisiteStartsDropped = prerequisiteStartsDropped,
            Skipped = skipped.ToArray(), SkippedDropped = skippedDropped, Phases = phases.ToArray(), PhasesDropped = phasesDropped,
            PlayerReference = CurrentReference, PlayerReferences = [InitialReference, .. referenceChanges], PlayerReferencesDropped = referenceChangesDropped,
            Attaches = attaches,
        };
        token.ThrowIfCancellationRequested();
        return result;
    }
    private static Vector3 Vec(JsonNode? value, Vector3 fallback = default) => value == null ? fallback : new(value.Float("x", fallback.X), value.Float("y", fallback.Y), value.Float("z", fallback.Z));
    private sealed class Node
    {
        public int Source, Parent, Variant = -1;
        public long RenderId;
        public Matrix4x4 Exact;
        public Vector3 Position, Euler, Scale;
        public Quaternion Rotation;
        public float Alpha = 1, Morph, Fov = MathF.PI / 3;
        public float GroundHeight;
        public bool Active, Changed, AlphaEnabled, PendingPlacement, PositionWritten, GroundSupported;
        public double CycleStart;
        public Matrix4x4 Local => Changed ? Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position) : Exact;
        public Node Clone() => (Node)MemberwiseClone();
    }
    private sealed class Sequence(AnimationSequence data)
    {
        public AnimationSequence Data = data;
        public int Cursor, State = data.ResetMode, Iteration, KeyOffset;
        public float Elapsed, EventElapsed, LoopElapsed, KeyTime;
        public bool ObservedInfiniteLoop;
        /// <summary>A stop, reset or finish replaced or ended this sequence; its dispatch loop must not continue.</summary>
        public bool Retired;
        public Guid? ReleasedBy;
        public long ReleasedByInstance;
        public double ReleasedAt;
        public string? ReleaseKind;
        /// <summary>Phase evidence: when the sequence was created, its last recorded phase and the release that phase recorded.</summary>
        public double Created = -1, RecordedRelease = double.NaN;
        public int RecordedPhase = -1;
        public long Child = -1;
        /// <summary>Duration analysis only (<see cref="MeasureDuration"/>): whether this pass runs on authored clocks
        /// (<see cref="TimedTimeline"/>), follows something without a timed end (<see cref="UntimedTimeline"/>, with why) or
        /// repeats a first loop iteration (<see cref="RepeatTimeline"/>); when its pass began, its sounds' and effects' tails, and
        /// whether its LOOP repeats forever (with where its first iteration ends).</summary>
        public byte Timeline;
        public string? UntimedBy;
        public double PassStart = -1, Tail, LoopStart, LoopEnd;
        public bool LoopsForever;
        private AnimationEvent? work;
        public Action<long>? WorkChanged;
        public AnimationEvent? Work
        {
            get => work;
            set { long delta = WorkBytes(value) - WorkBytes(work); work = value; WorkChanged?.Invoke(delta); }
        }
        public Sequence Clone(CancellationToken token, Action<long>? workChanged)
        {
            token.ThrowIfCancellationRequested(); var copy = (Sequence)MemberwiseClone();
            copy.work = work?.Clone(token); copy.WorkChanged = workChanged; return copy;
        }
        public void Reset() { Cursor = 0; State = Data.ResetMode; Elapsed = EventElapsed = 0; Work = null; }
    }
    private sealed class Instance
    {
        public required AnimationEntry Entry;
        public long Id;
        public string CallPath = "";
        public long? ParentInstance;
        public Guid? CallEvent;
        public double StartedAt;
        public int Root;
        public AnimationBinding Binding;
        public float Elapsed, StopDelay = -1;
        public bool Cleanup, Finished, Shared, Scheduled;
        /// <summary>What holds a started instance this tick ("out of range", "zone"), or null while it runs.</summary>
        public string? Paused;
        public Dictionary<int, Node> Nodes = [], SavedNodes = [];
        public List<Sequence> Sequences = [];
        public Dictionary<int, long> Children = [];
        /// <summary>The engine's cached child per call slot: cleared by every launch from the slot, set when it starts one, emptied by FinalizeStop.</summary>
        public Dictionary<int, long> Locals = [];
        /// <summary>Duration analysis only: the binding's own timeline (its start, a stop or reset, a gate, its natural end) and
        /// the timeline of its latest event completion (at <see cref="EndTick"/>), which its natural end inherits.</summary>
        public byte Timeline, EndTimeline;
        public string? UntimedBy;
        public long EndTick = -1;
        public Instance Clone(Func<Node, Node> cloneNode, CancellationToken token, Action<long>? workChanged = null)
        {
            token.ThrowIfCancellationRequested();
            var copy = (Instance)MemberwiseClone(); copy.Nodes = Nodes.ToDictionary(p => p.Key, p => cloneNode(p.Value));
            copy.SavedNodes = SavedNodes; copy.Sequences = Sequences.Select(s => s.Clone(token, workChanged)).ToList();
            copy.Children = new(Children); copy.Locals = new(Locals); return copy;
        }
    }
    private sealed class Effect
    {
        public required AnimationEffectTemplate Template;
        public long Id;
        public Vector3 Position;
        public float Age, Scale = 5;
        /// <summary>Duration analysis only: the sequence whose event created it (its tail counts there).</summary>
        public Sequence? Source;
        public Effect Clone() => (Effect)MemberwiseClone();
    }
    private sealed record Checkpoint(long Ticks, long NextId, int RandomIndex, List<Instance> Instances, Dictionary<int, Node> SharedNodes, List<Effect> Effects, List<AnimationTrace> Trace,
        HashSet<string> Notes, long TraceOrdinal, long TraceDropped, Dictionary<DiagnosticKey, AnimationPreviewDiagnostic> Issues, HashSet<DiagnosticKey> OmittedIssues, Dictionary<long, AnimationLight> Lights, Dictionary<long, AnimationSoundCue> Sounds, Vector4 Color, Vector4 Wave, AnimationFog? Fog,
        List<AnimationConditionResult> Conditions, long ConditionsDropped, bool HasExcludedCalls, int NextScheduled, Dictionary<int, EntryRun> EntryRuns,
        List<AnimationStartRefusal> Refusals, long RefusalsDropped, long RefusalBytes, List<AnimationSkippedSpan> Skipped, long SkippedDropped,
        List<AnimationPlayerReferenceState> References, long ReferencesDropped,
        List<AnimationPrerequisiteStart> PrerequisiteStarts, long PrerequisiteStartsDropped, long PrerequisiteBytes,
        List<AnimationSequencePhase> Phases, long PhasesDropped)
    {
        /// <summary>The bytes of its refusals and prerequisite evidence.</summary>
        public long StartEvidenceBytes => RefusalBytes + PrerequisiteBytes;
    }
    private Checkpoint CaptureCheckpoint(CancellationToken token)
    {
        Dictionary<Node, Node> copies = [];
        Node Copy(Node n) { token.ThrowIfCancellationRequested(); if (!copies.TryGetValue(n, out var copy)) copies[n] = copy = n.Clone(); return copy; }
        return new(ticks, nextId, randomIndex, instances.Select(i => i.Clone(Copy, token)).ToList(), sharedNodes.ToDictionary(p => p.Key, p => Copy(p.Value)), effects.Select(e => e.Clone()).ToList(), [.. trace], [.. notes], traceOrdinal, traceDropped, new(previewIssues), new(omittedIssues), new(lights), new(activeSounds), screenColor, screenWave, fog, [.. conditions], conditionsDropped, hasExcludedCalls, nextScheduled,
            new(entryRuns), [.. refusals], refusalsDropped, refusalBytes, [.. skipped], skippedDropped, [.. referenceChanges], referenceChangesDropped,
            [.. prerequisiteStarts], prerequisiteStartsDropped, prerequisiteBytes, [.. phases], phasesDropped);
    }
    private void Restore(Checkpoint c, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!AdmitState(StateBytes(c.Instances, c.SharedNodes) + ConditionBytes(c.Conditions.Count) + CloneScratch(c.Instances, c.SharedNodes)
            + EvidenceBytes(c.EntryRuns, c.StartEvidenceBytes, c.Skipped.Count + c.Phases.Count, c.References.Count, null), c)) throw new StateLimitException();
        Dictionary<Node, Node> copies = [];
        Node Copy(Node n) { token.ThrowIfCancellationRequested(); if (!copies.TryGetValue(n, out var copy)) copies[n] = copy = n.Clone(); return copy; }
        // Stage the complete replacement while the accepted state and checkpoint are still accounted.
        var restored = c.Instances.Select(i => i.Clone(Copy, token, AdjustRetainedWork)).ToList();
        var shared = c.SharedNodes.ToDictionary(p => p.Key, p => Copy(p.Value));
        var restoredEffects = c.Effects.Select(e => e.Clone()).ToList();
        var restoredTrace = new List<AnimationTrace>(c.Trace); var restoredNotes = new HashSet<string>(c.Notes);
        var restoredIssues = new Dictionary<DiagnosticKey, AnimationPreviewDiagnostic>(c.Issues); var restoredOmitted = new HashSet<DiagnosticKey>(c.OmittedIssues);
        var restoredConditions = new List<AnimationConditionResult>(c.Conditions);
        var restoredLights = new Dictionary<long, AnimationLight>(c.Lights); var restoredSounds = new Dictionary<long, AnimationSoundCue>(c.Sounds);
        token.ThrowIfCancellationRequested();
        ticks = c.Ticks; nextId = c.NextId; randomIndex = c.RandomIndex; instances = restored; sharedNodes = shared; effects = restoredEffects;
        trace = restoredTrace; notes = restoredNotes; traceOrdinal = c.TraceOrdinal; traceDropped = c.TraceDropped; previewIssues = restoredIssues; omittedIssues = restoredOmitted; lights = restoredLights; activeSounds = restoredSounds; screenColor = c.Color; screenWave = c.Wave; fog = c.Fog;
        conditions = restoredConditions; conditionsDropped = c.ConditionsDropped; hasExcludedCalls = c.HasExcludedCalls;
        entryRuns = new(c.EntryRuns); refusals = [.. c.Refusals]; refusalsDropped = c.RefusalsDropped; refusalBytes = c.RefusalBytes;
        prerequisiteStarts = [.. c.PrerequisiteStarts]; prerequisiteStartsDropped = c.PrerequisiteStartsDropped; prerequisiteBytes = c.PrerequisiteBytes;
        skipped = [.. c.Skipped]; skippedDropped = c.SkippedDropped; referenceChanges = [.. c.References]; referenceChangesDropped = c.ReferencesDropped;
        phases = [.. c.Phases]; phasesDropped = c.PhasesDropped;
        nextScheduled = c.NextScheduled; originTerrain.Clear();
        retiredSequences.Clear(); InvalidateRetainedState(); interrupted = false;
    }
}

public static class AnimationMath
{
    public static Quaternion FromEuler(Vector3 radians) => Quaternion.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z);
    public static Quaternion FromRotationVector(Vector3 vector)
    {
        // Retail 0x475B80 uses sin(length), cos(length), not half the length.
        float angle = vector.Length(); return angle < 1e-8f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(vector / angle, angle * 2);
    }
    public static Vector3 ToEuler(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        float pitch = MathF.Asin(Math.Clamp(-m.M32, -1, 1));
        return new(pitch, MathF.Atan2(m.M31, m.M33), MathF.Atan2(m.M12, m.M22));
    }
    public static Quaternion ReadQuaternion(AnimationRecord record, int offset) => new(record.F32(offset + 4), record.F32(offset + 8), record.F32(offset + 12), record.F32(offset));
}
