using System.Numerics;

namespace zStudio.Core.Animation;

/// <summary>Where a binding is in its life: the entry's activation state (docs/agents/engine-evidence.md, "Animation runtime").</summary>
public enum AnimationBindingLifecycle
{
    /// <summary>State 0: never started.</summary>
    NotStarted,
    /// <summary>State 2: running its sequences.</summary>
    Running,
    /// <summary>State 6: invalidated while running; it keeps running and every later start is refused.</summary>
    RunningInvalidated,
    /// <summary>State 3: finished without its cleanup (negative RESET_TIME); its final pose stays.</summary>
    FinishedPoseKept,
    /// <summary>State 1: stopped after its cleanup, or by the mission load's cleanup.</summary>
    Stopped,
    /// <summary>State 4: invalidated; every later start is refused for the rest of the mission.</summary>
    Invalidated,
    /// <summary>Running, but a range or zone gate holds it; its clocks are frozen.</summary>
    Paused,
    /// <summary>State 5, or an unknown stored state: the engine never runs it.</summary>
    Unavailable,
}

/// <summary>One binding's state: the entry's base record, never a runtime clone or another entry of the same name.</summary>
public sealed record AnimationBindingStatus(int Entry, string Name, AnimationBindingLifecycle State, int Root, long? Instance, double Time, string Cause)
{
    /// <summary>Why a started binding runs no events now: "out of range" or "zone".</summary>
    public string? PausedBy { get; init; }
    /// <summary>Every later start is refused (states 4 and 6), whether or not it still runs.</summary>
    public bool Invalidated { get; init; }
}

/// <summary>One prerequisite record: an animation (mode 1, met unless stopped) or a node's active flag (mode 2, its path joined by '/').</summary>
public sealed record AnimationPrerequisiteCheck(int Record, int Mode, string Target, bool Required, bool? Satisfied);

/// <summary>A prerequisite evaluation against the run's state. <see cref="AnimationPrerequisiteCheck.Satisfied"/> is null for an unresolved node, which the game skips.</summary>
public sealed record AnimationPrerequisiteResult(bool Passed, int Matched, int Minimum, IReadOnlyList<AnimationPrerequisiteCheck> Checks);

/// <summary>
/// A start the engine refused, with its reason: "invalidated", "unavailable", "oneShot" (RESET_TIME -99 already used),
/// "alreadyRunning" or "prerequisites".
/// </summary>
public sealed record AnimationStartRefusal(int Entry, string Name, double Time, string Reason, string InstancePath, int Root)
{
    public long? Caller { get; init; }
    public Guid? CallEvent { get; init; }
    public bool ScenarioStart { get; init; }
    public long Occurrence { get; init; }
    public AnimationPrerequisiteResult? Prerequisites { get; init; }
}

/// <summary>
/// A binding's activation prerequisites at one start attempt, as evidence (the game checks them on every start):
/// <see cref="Outcome"/> "met", "refused" (also a refusal) or "ignored" (unmet, and the scenario's rule let the start run,
/// where the game would refuse it). It changes nothing in the run.
/// </summary>
public sealed record AnimationPrerequisiteStart(int Entry, string Name, double Time, string Outcome, string InstancePath, int Root, AnimationPrerequisiteResult Result)
{
    public long? Caller { get; init; }
    public Guid? CallEvent { get; init; }
    public bool ScenarioStart { get; init; }
    public long Occurrence { get; init; }
}

/// <summary>
/// The events a branch decision skipped on one pass: <see cref="Reason"/> "false" for a false IF/ELSEIF (its body), "taken"
/// for the rest of the chain after a taken body reached its ELSE or ELSEIF. Events not covered were either run or not reached.
/// </summary>
public sealed record AnimationSkippedSpan(long Instance, Guid Sequence, string InstancePath, Guid Marker, Guid First, Guid Last, int Count, double Time, long Occurrence)
{
    public string Reason { get; init; } = "false";
}

/// <summary>
/// What a sequence does from <see cref="AnimationSequencePhase.Time"/> until its next phase: the sequence's own states, then
/// <see cref="Paused"/> (a range or zone gate holds its binding; its clocks freeze), <see cref="Stopped"/> (its binding was
/// stopped or reset: it waits for the reset time or its cleanup replaced it) and <see cref="Finished"/> (the instance ended;
/// recorded once per instance, with an empty sequence).
/// </summary>
public enum AnimationSequencePhaseState { WaitingForThreshold, Running, Complete, WaitingForRelease, Unavailable, Blocked, Paused, Stopped, Finished }

/// <summary>
/// One change of a sequence's state in the run, at tick resolution, as evidence for the Timing view's lane bands (it changes
/// nothing in the run). <see cref="ReleasedBy"/> is the CALL_SEQUENCE (or bouncing motion) that released a waiting sequence
/// into this phase.
/// </summary>
public sealed record AnimationSequencePhase(long Instance, Guid Sequence, AnimationSequencePhaseState State, double Time)
{
    public int EventIndex { get; init; }
    public int Pass { get; init; }
    public Guid? ReleasedBy { get; init; }
    public long? ReleasedByInstance { get; init; }
    public string? PausedBy { get; init; }
    /// <summary>A <see cref="AnimationSequencePhaseState.Finished"/> phase's instance start: the player releases a finished child, so its start stays here.</summary>
    public double? StartedAt { get; init; }
}

/// <summary>
/// The player reference range tests measure from, and where it came from: "aiv start", "opening handover", "custom",
/// "none", "no mission start", "callback 10" or "callback 17". <see cref="Frozen"/> follows callback 11.
/// </summary>
public sealed record AnimationPlayerReferenceState(Vector3? Position, string Source, bool Frozen, double Time)
{
    public Guid? Event { get; init; }
    public long? Instance { get; init; }
}

public sealed partial class AnimationPlayer
{
    /// <summary>An entry's base-record state as the engine keeps it (0 never run, 1 stopped, 2 running, 3 finished with its pose kept, 4 invalidated, 5 unavailable, 6 running and invalidated).</summary>
    private readonly record struct EntryRun(byte State, long Base, int Root, Dictionary<int, Node>? Rest, double Time, string Cause, string Path);
    private Dictionary<int, EntryRun> entryRuns = [];
    private const long EntryRunBytes = 160;
    private List<AnimationStartRefusal> refusals = [];
    private long refusalsDropped, refusalBytes;
    private List<AnimationPrerequisiteStart> prerequisiteStarts = [];
    private long prerequisiteStartsDropped, prerequisiteBytes;
    private List<AnimationSkippedSpan> skipped = [];
    private long skippedDropped;
    private List<AnimationPlayerReferenceState> referenceChanges = [];
    private long referenceChangesDropped;
    private List<AnimationSequencePhase> phases = [];
    private long phasesDropped;
    private AnimationPlayerReferenceState? initialReference;
    /// <summary>The reason the latest AddInstance call refused its start, for the caller's trace.</summary>
    private string? lastRefusal;
    private int cleanupDepth;
    /// <summary>A private player computing the opening's handover: it starts from the AIV start and never asks for a handover itself.</summary>
    private bool openingProbe;
    private AnimationScenarioRules Rules => executionPolicy?.Rules ?? AnimationScenarioRules.Default;

    /// <summary>
    /// The state the mission load leaves an entry in: <c>LoadAndInstantiate</c> (0x45fb30) stops every entry after the
    /// first with a single RESET_TIME (flag 0x20) unless it is stored running or unavailable; others keep their stored state.
    /// </summary>
    internal static byte LoadState(AnimationEntry entry)
    {
        byte stored = entry.Bytes.Length > 152 ? entry.Bytes[152] : (byte)0;
        return entry.Index > 0 && stored is not (2 or 5) && (entry.U32(148) & 0x20) != 0 ? (byte)1 : stored;
    }
    /// <summary>A binding's lifecycle at mission start, before a run touches it.</summary>
    public static AnimationBindingLifecycle InitialState(AnimationEntry entry) => Lifecycle(LoadState(entry), false);
    private static AnimationBindingLifecycle Lifecycle(byte state, bool live) => state switch
    {
        0 => AnimationBindingLifecycle.NotStarted, 1 => AnimationBindingLifecycle.Stopped, 2 => AnimationBindingLifecycle.Running,
        3 => AnimationBindingLifecycle.FinishedPoseKept, 4 => live ? AnimationBindingLifecycle.RunningInvalidated : AnimationBindingLifecycle.Invalidated,
        6 => AnimationBindingLifecycle.RunningInvalidated, _ => AnimationBindingLifecycle.Unavailable,
    };
    private EntryRun EntryState(AnimationEntry entry) => entryRuns.TryGetValue(entry.Index, out var run) ? run : new(LoadState(entry), 0, -1, null, 0, "load", "");
    private void SetEntryState(AnimationEntry entry, EntryRun run)
    {
        if (!entryRuns.ContainsKey(entry.Index)) { RequireState(EntryRunBytes); AdjustRetainedBytes(EntryRunBytes); }
        entryRuns[entry.Index] = run;
    }
    private Instance? LiveBase(AnimationEntry entry) => EntryState(entry) is { Base: > 0 } run
        ? instances.FirstOrDefault(i => i.Id == run.Base && !i.Finished) : null;
    private IReadOnlyList<AnimationBindingStatus> BindingStatuses()
    {
        if (entryRuns.Count == 0) return [];
        Dictionary<long, Instance> live = [];
        foreach (var instance in instances) if (!instance.Finished) live[instance.Id] = instance;
        return entryRuns.OrderBy(p => p.Key).Select(p =>
        {
            var entry = context.Package.Entries[p.Key]; var run = p.Value;
            var active = run.Base > 0 ? live.GetValueOrDefault(run.Base) : null;
            var state = Lifecycle(run.State, active != null);
            string? paused = active?.Paused;
            if (paused != null && state is AnimationBindingLifecycle.Running or AnimationBindingLifecycle.RunningInvalidated) state = AnimationBindingLifecycle.Paused;
            else paused = null;
            return new AnimationBindingStatus(entry.Index, entry.Name, state, active?.Root ?? run.Root, run.Base > 0 ? run.Base : null, run.Time, run.Cause)
                { PausedBy = paused, Invalidated = run.State is 4 or 6 };
        }).ToArray();
    }

    private static long RefusalCost(AnimationStartRefusal refusal) => 256 + 2L * refusal.InstancePath.Length + 2L * refusal.Name.Length
        + (refusal.Prerequisites == null ? 0 : 96L + 128L * refusal.Prerequisites.Checks.Count);
    private void RefuseStart(AnimationEntry entry, string reason, string path, int root, long? caller, Guid? callEvent, bool scenarioStart,
        AnimationPrerequisiteResult? prerequisites = null)
    {
        lastRefusal = reason;
        if (initializingScene) return;
        if (whatIf != null) WhatIfRefused(entry, reason, path, scenarioStart);
        if (refusals.Count >= 20000)
        {
            refusalBytes -= refusals.Take(10000).Sum(RefusalCost);
            refusals.RemoveRange(0, 10000); refusalsDropped += 10000; InvalidateRetainedState();
        }
        AnimationStartRefusal refusal = new(entry.Index, entry.Name, Time, reason, path, root)
        {
            Caller = caller, CallEvent = callEvent, ScenarioStart = scenarioStart, Prerequisites = prerequisites,
            Occurrence = trace.Count == 0 ? 0 : trace[^1].Occurrence
        };
        long bytes = RefusalCost(refusal);
        RequireState(bytes);
        refusals.Add(refusal); refusalBytes += bytes; AdjustRetainedBytes(bytes);
    }
    private static long PrerequisiteCost(AnimationPrerequisiteStart start) => 256 + 2L * start.InstancePath.Length + 2L * start.Name.Length + 128L * start.Result.Checks.Count;
    /// <summary>Records a start attempt's prerequisites (evidence only, bounded like the refusals).</summary>
    private void RecordPrerequisites(AnimationEntry entry, string outcome, AnimationPrerequisiteResult result, string path, int root, long? caller, Guid? callEvent, bool scenarioStart)
    {
        if (initializingScene) return;
        if (prerequisiteStarts.Count >= 20000)
        {
            prerequisiteBytes -= prerequisiteStarts.Take(10000).Sum(PrerequisiteCost);
            prerequisiteStarts.RemoveRange(0, 10000); prerequisiteStartsDropped += 10000; InvalidateRetainedState();
        }
        AnimationPrerequisiteStart start = new(entry.Index, entry.Name, Time, outcome, path, root, result)
        {
            Caller = caller, CallEvent = callEvent, ScenarioStart = scenarioStart, Occurrence = trace.Count == 0 ? 0 : trace[^1].Occurrence,
        };
        long bytes = PrerequisiteCost(start);
        RequireState(bytes);
        prerequisiteStarts.Add(start); prerequisiteBytes += bytes; AdjustRetainedBytes(bytes);
    }
    /// <summary>The most excluded call paths a run keeps (<see cref="AnimationFrame.ExcludedCalls"/>): the scenario's path allowance.</summary>
    public const int MaximumExcludedCalls = AnimationExecutionPolicy.MaximumPaths;
    private List<AnimationExcludedCall> excludedCalls = [];
    private HashSet<string> excludedCallPaths = new(StringComparer.Ordinal);
    private bool excludedCallsTruncated;
    private long excludedCallBytes;
    private static long ExcludedCallCost(string path) => 320 + 4L * path.Length;
    /// <summary>
    /// Records the first dispatch of an excluded call path (evidence only, bounded): what the run left out, so the preview can say
    /// what that call would have changed (<see cref="AnimationExcludedEffects.Candidates"/>). Duration analysis keeps none.
    /// </summary>
    private void RecordExcludedCall(Instance caller, Sequence sequence, AnimationEvent ev, string path, int target, double at, bool waited)
    {
        if (initializingScene || measuringDuration || excludedCallPaths.Contains(path)) return;
        if (excludedCalls.Count >= MaximumExcludedCalls) { excludedCallsTruncated = true; return; }
        long bytes = ExcludedCallCost(path);
        RequireState(bytes);
        excludedCalls.Add(new(path, caller.Entry.Index, ev.Id, target, at) { Instance = caller.Id, Sequence = sequence.Data.Id, Waited = waited });
        excludedCallPaths.Add(path); excludedCallBytes += bytes; AdjustRetainedBytes(bytes);
    }
    private static string RefusalText(string? reason) => reason switch
    {
        "invalidated" => "invalidated", "unavailable" => "unavailable", "oneShot" => "one-shot used",
        "alreadyRunning" => "already running here", "prerequisites" => "prerequisites unmet", _ => "unavailable",
    };

    /// <summary>
    /// At the end of a tick, every live sequence whose state changed (or that a release reached) since its last phase gets a
    /// new one; a gate's hold reads <see cref="AnimationSequencePhaseState.Paused"/>, a stop delay
    /// <see cref="AnimationSequencePhaseState.Stopped"/>. Evidence only, bounded like the other run evidence.
    /// </summary>
    private void RecordPhases()
    {
        if (initializingScene || measuringDuration) return;
        foreach (var instance in instances)
            if (!instance.Finished) RecordPhases(instance);
    }
    private void RecordPhases(Instance instance)
    {
            foreach (var sequence in instance.Sequences)
            {
                int state = sequence.State is 0 or 1 && instance.Paused != null ? 6
                    : instance.StopDelay >= 0 && sequence.State is 0 or 1 or 3 or 5 ? 7 : Math.Clamp(sequence.State, 0, 5);
                bool released = sequence.ReleasedBy != null && sequence.ReleasedAt != sequence.RecordedRelease;
                if (state == sequence.RecordedPhase && !released) continue;
                double at = sequence.RecordedPhase < 0 && sequence.Created >= 0 ? Math.Min(sequence.Created, Time) : released ? Math.Min(sequence.ReleasedAt, Time) : Time;
                AddPhase(new(instance.Id, sequence.Data.Id, (AnimationSequencePhaseState)state, at)
                {
                    EventIndex = sequence.Cursor, Pass = sequence.Iteration, PausedBy = state == 6 ? instance.Paused : null,
                    ReleasedBy = released ? sequence.ReleasedBy : null, ReleasedByInstance = released ? sequence.ReleasedByInstance : null,
                });
                sequence.RecordedPhase = state;
                if (released) sequence.RecordedRelease = sequence.ReleasedAt;
            }
    }
    /// <summary>A cleanup replaced <paramref name="replaced"/>: those not complete end stopped.</summary>
    private void RecordStopped(Instance instance, IEnumerable<Sequence> replaced)
    {
        if (initializingScene || measuringDuration) return;
        foreach (var sequence in replaced)
            if (sequence.RecordedPhase is >= 0 and not (2 or 7)) { AddPhase(new(instance.Id, sequence.Data.Id, AnimationSequencePhaseState.Stopped, Time) { EventIndex = sequence.Cursor, Pass = sequence.Iteration }); sequence.RecordedPhase = 7; }
    }
    private void RecordFinished(Instance instance)
    {
        if (initializingScene || measuringDuration) return;
        // What its sequences reached in the tick it ended, then the end.
        RecordPhases(instance);
        AddPhase(new(instance.Id, Guid.Empty, AnimationSequencePhaseState.Finished, Time) { StartedAt = instance.StartedAt });
    }
    private void AddPhase(AnimationSequencePhase phase)
    {
        if (phases.Count >= 20000) { phases.RemoveRange(0, 10000); phasesDropped += 10000; InvalidateRetainedState(); }
        // Evidence never refuses a tick: past the allowance the phase is counted as dropped instead.
        if (!AdmitState(128)) { phasesDropped++; return; }
        phases.Add(phase);
        AdjustRetainedBytes(128);
    }

    private void RecordSkip(Instance instance, Sequence sequence, AnimationEvent marker, int first, int last, string reason)
    {
        if (initializingScene || first > last) return;
        if (skipped.Count >= 20000) { skipped.RemoveRange(0, 10000); skippedDropped += 10000; InvalidateRetainedState(); }
        RequireState(128);
        var events = sequence.Data.Events;
        skipped.Add(new(instance.Id, sequence.Data.Id, instance.CallPath, marker.Id, events[first].Id, events[last].Id, last - first + 1, Time,
            trace.Count == 0 ? 0 : trace[^1].Occurrence) { Reason = reason });
        AdjustRetainedBytes(128);
    }

    /// <summary>Evaluates <paramref name="entryIndex"/>'s activation prerequisites against the run's state at this frame.</summary>
    public AnimationPrerequisiteResult Prerequisites(int entryIndex, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, context.Package.Entries.Count);
        if (interrupted) throw new IOException(InterruptedRefusal);
        using var binding = BeginBinding(token);
        return EvaluatePrerequisites(context.Package.Entries[entryIndex]);
    }

    /// <summary>
    /// <c>CheckActivationPrereqs</c> (0x45db20 [B]): a mode-1 record is met unless its animation (first of the name) is stopped
    /// (state 1), and an animation the package lacks refuses the start; a mode-2 record compares its node's active flag with
    /// the expected bit, skipping a node the load could not bind; mode 3 continues the node path. A required record that is
    /// not met refuses the start; met options must reach the minimum.
    /// </summary>
    private AnimationPrerequisiteResult EvaluatePrerequisites(AnimationEntry entry)
    {
        var records = entry.References[6];
        int minimum = entry.Bytes.Length > 268 ? entry.Bytes[268] : 0, matched = 0;
        bool passed = true;
        List<AnimationPrerequisiteCheck> checks = [];
        List<string> path = []; int pathNode = -1;
        for (int i = 0; i < records.Count; i++)
        {
            bindingOperation?.Reserve(1);
            var record = records[i];
            if (record.Bytes.Length < 48) continue;
            int mode = record.Bytes[4]; bool option = record.I32(0) != 0;
            if (mode == 3)
            {
                string part = record.Text(12, 28); path.Add(part);
                pathNode = PrerequisiteNode(pathNode, part, path.Count == 1);
                continue;
            }
            bool? satisfied;
            string target;
            if (mode == 1)
            {
                target = record.Text(8, 32);
                var other = entryLookup.Find(target);
                satisfied = other != null && EntryState(other).State != 1;
                if (other == null) passed = false;
            }
            else if (mode == 2)
            {
                string part = record.Text(12, 28); path.Add(part); target = string.Join('/', path);
                int node = PrerequisiteNode(pathNode, part, path.Count == 1);
                satisfied = node < 0 ? null : (NodeActive(node) ? 1 : 0) == record.I32(8);
                path.Clear(); pathNode = -1;
            }
            else continue;
            if (checks.Count < 64) checks.Add(new(i, mode, target, !option, satisfied));
            if (satisfied == false && !option) passed = false;
            else if (satisfied == true && option) matched++;
        }
        return new(passed && matched >= minimum, matched, minimum, checks.AsReadOnly());
    }
    /// <summary>LoadZbd's prerequisite path binding: the first name in the whole loaded world (its highest slot), each further name inside the node before it.</summary>
    private int PrerequisiteNode(int parent, string name, bool first)
    {
        if (first) return bindingOperation is { } operation && operation.Named(name, context.LoadedLimit) is { Count: > 0 } found ? found[0] : -1;
        return parent < 0 ? -1 : context.FindSubBelow(parent, name);
    }
    private bool NodeActive(int index)
    {
        var source = context.Scene.Nodes[index];
        if (source.Class is "object3d" or "lod" && sharedNodes.TryGetValue(index, out var node)) return node.Active;
        return (source.Metadata.UInt("flags") & 4) != 0;
    }

    private AnimationPlayerReferenceState CurrentReference => referenceChanges.Count > 0 ? referenceChanges[^1] : InitialReference;
    private AnimationPlayerReferenceState InitialReference => initialReference ??= ComputeInitialReference();
    private AnimationPlayerReferenceState ComputeInitialReference()
    {
        var player = Rules.Player;
        if (player.Kind == AnimationPlayerReferenceKind.None) return new(null, "none", false, 0);
        if (player.Kind == AnimationPlayerReferenceKind.Custom) return new(player.Position, "custom", false, 0);
        if (context.Mission?.PlayerStart is not Vector3 start) return new(null, "no mission start", false, 0);
        // m2–m6: an opening that sets the player's pose hands it over at its callback 10. A run without the opening starts there.
        if (!openingProbe && context.Mission.PlayerStartControl is { SetsPose: true } control && !IncludesOpening(control)
            && context.OpeningHandover(() => Handover(control)) is Vector3 handover)
            return new(handover, "opening handover", false, 0);
        return new(start, "aiv start", false, 0);
    }
    private bool IncludesOpening(MissionStartControl control) => schedule == null ? control.Openings.Contains(entryIndex)
        : schedule.Starts.Any(s => control.Openings.Contains(s.Entry));
    /// <summary>The player's position after the opening's cleanup reaches callback 10 or 17, as skipping the opening plays it.</summary>
    private Vector3? Handover(MissionStartControl control)
    {
        foreach (int index in control.Openings)
        {
            if (index < 0 || index >= context.Package.Entries.Count) continue;
            try
            {
                var probe = new AnimationPlayer(context, index, 1, true, CancellationToken.None, MaximumRetainedStateBytes, null, null, AnimationExecutionPolicy.RootOnly)
                    { openingProbe = true, measuringDuration = true };
                for (int tick = 0; tick < 600 && !probe.IsComplete; tick++)
                {
                    probe.cues.Clear(); probe.Tick(CancellationToken.None);
                    if (probe.referenceChanges.FirstOrDefault(r => r.Source is "callback 10" or "callback 17") is { Position: Vector3 position }) return position;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { }
        }
        return null;
    }
    private void AddReference(AnimationPlayerReferenceState state)
    {
        if (referenceChanges.Count >= 4096) { referenceChanges.RemoveRange(0, 2048); referenceChangesDropped += 2048; InvalidateRetainedState(); }
        RequireState(128); referenceChanges.Add(state); AdjustRetainedBytes(128);
    }
    /// <summary>
    /// <c>Player::AsyncCommandCallback</c> (0x42bb30 [B]): 10 takes the player's pose from the tank root and gives the controls
    /// back, so the vehicle tick makes the reference that position; 17 moves the restart point and pose there too; 11
    /// suspends the vehicle, freezing the reference. Only the player at start follows them.
    /// </summary>
    private void PlayerCallback(Instance instance, AnimationEvent ev, int value, double at)
    {
        if (Rules.Player.Kind != AnimationPlayerReferenceKind.AtStart)
        {
            AddNote($"Game callback {value} moves the player in the game; the scenario's player stays where it is set.", "Support", "Information");
            return;
        }
        var current = CurrentReference;
        if (value == 11)
        {
            if (!current.Frozen) AddReference(current with { Frozen = true, Time = at, Event = ev.Id, Instance = instance.Id });
            AddNote("Game callback 11 suspends the player: the player reference stays where it is.", "Support", "Information");
            return;
        }
        if (TankRootPosition(instance) is not Vector3 position)
        { AddNote($"Game callback {value}: the mission has no player tank, so the player reference stays where it is.", "Support", "Information"); return; }
        AddReference(new(position, $"callback {value}", value != 10 && current.Frozen, at) { Event = ev.Id, Instance = instance.Id });
        AddNote($"Game callback {value} moves the player reference to the player tank's root; other game code is not executed.", "Support", "Information");
    }
    private Vector3? TankRootPosition(Instance instance)
    {
        int root = context.Mission?.LocalPlayer?.Root ?? -1;
        if (root < 0 || root >= context.Scene.Nodes.Count) return null;
        if (instance.Nodes.TryGetValue(root, out var node)) return World(instance, node).Translation;
        if (sharedNodes.TryGetValue(root, out var shared) && (instance.Shared ? instance : instances.FirstOrDefault(i => i.Shared)) is { } owner)
            return World(owner, shared).Translation;
        return context.WorldTransform(root, bindingOperation).Translation;
    }

    /// <summary>The squared distance from <paramref name="reference"/> to the callback node; an unreadable position reads as zero (0x45c640).</summary>
    private float CallbackDistanceSquared(Instance instance, Vector3 reference) =>
        CallbackPosition(instance) is Vector3 at ? Vector3.DistanceSquared(reference, at) : 0;
    /// <summary>The callback node's world position (its run pose when the instance tracks it), or null when it cannot be read.</summary>
    private Vector3? CallbackPosition(Instance instance)
    {
        var operation = bindingOperation!;
        int callback = context.CallbackNode(instance.Entry, instance.Root, instance.Binding, operation);
        return callback < 0 || callback >= context.Scene.Nodes.Count ? null
            : ParentNode(instance, callback) is { } node ? World(instance, node).Translation : context.WorldTransform(callback, operation).Translation;
    }
    private Dictionary<int, bool>? rangeTested;
    /// <summary>Whether a binding tests a range: an EXECUTION_BY_RANGE gate (flag 0x02) or an IF/ELSEIF with PLAYER_RANGE (operand bit 0x02).</summary>
    private bool RangeTested(AnimationEntry entry)
    {
        rangeTested ??= [];
        if (rangeTested.TryGetValue(entry.Index, out bool known)) return known;
        bool tested = (entry.U32(148) & 2) != 0 || entry.AllSequences.Any(s => s.Events.Any(e => e.Type is 31 or 33 && e.Bytes.Length >= 16 && (e.U32(12) & 3) == 2));
        if (rangeTested.Count < 4096) rangeTested[entry.Index] = tested;
        return tested;
    }
    /// <summary>
    /// Whether <c>RunSequence</c> (0x45d010) runs a started binding's events this frame: EXECUTION_BY_RANGE (flag 0x02) needs
    /// the player reference within its range of the callback node, EXECUTION_BY_ZONE (flag 0x08) the view's last complete
    /// zone set to allow the callback node's zone. Null when it runs, else what holds it. The game retests on a staggered
    /// cadence; the preview tests every tick.
    /// </summary>
    private string? Gate(Instance instance)
    {
        uint flags = instance.Entry.U32(148);
        var rules = Rules;
        if ((flags & 2) != 0 && rules.RangeGates == AnimationRangeGateRule.Game)
        {
            if (CurrentReference.Position is not Vector3 player) return "out of range";
            float distance = CallbackDistanceSquared(instance, player);
            if (!(distance >= instance.Entry.F32(156) && distance < instance.Entry.F32(160))) return "out of range";
        }
        if ((flags & 8) != 0 && rules.ZoneGates == AnimationZoneGateRule.View)
        {
            if (rules.ViewZones is not { } zones) return "zone";
            int callback = context.CallbackNode(instance.Entry, instance.Root, instance.Binding, bindingOperation!);
            byte zone = callback < 0 || callback >= context.Scene.Nodes.Count ? (byte)0xFF : (byte)(context.Scene.Nodes[callback].Metadata.Int("zone_id", 0xFF) & 0xFF);
            if (!zones.Allows(zone)) return "zone";
        }
        return null;
    }
    private void SetPaused(Instance instance, string reason)
    {
        if (instance.Paused == reason) return;
        instance.Paused = reason;
        AddNote(reason == "zone"
            ? $"{instance.Entry.Name}: paused: the view's zones exclude its attach node (EXECUTION_BY_ZONE); its clocks are frozen."
            : CurrentReference.Position == null
                ? $"{instance.Entry.Name}: paused: range-gated (EXECUTION_BY_RANGE) and the scenario has no player; its clocks are frozen."
                : $"{instance.Entry.Name}: paused: the player is out of its range (EXECUTION_BY_RANGE {MathF.Sqrt(Math.Max(0, instance.Entry.F32(160))):0.###}); its clocks are frozen.",
            "Support", "Information");
    }

    /// <summary>
    /// <c>TraceUpwardHitFromNodeOrPos</c> (0x45c530 [B]): whether a vertical segment from <paramref name="start"/> up by
    /// <paramref name="height"/> crosses an intersection surface. The tested node's subtree is skipped (its pick flag is
    /// cleared during the test). Nodes the run owns are tested at their current pose instead of their baseline.
    /// </summary>
    private (bool Hit, float? Height, string? Unavailable) Cover(Vector3 start, float height, int excludedRoot)
    {
        var surfaces = context.CoverSurfaces(out string? failure);
        // A scene without static surfaces can still have one the run shows; one that could not be built cannot be tested.
        if (surfaces == null && failure != AnimationCoverSurfaces.NoSurfaces) return (false, null, failure ?? AnimationCoverSurfaces.NoSurfaces);
        bool tested = surfaces != null;
        var operation = bindingOperation!;
        HashSet<int> excluded = excludedRoot >= 0 ? [.. operation.Descendants(excludedRoot, context.Scene.Nodes.Count)] : [];
        var zones = Rules.ZoneGates == AnimationZoneGateRule.View ? Rules.ViewZones : null;
        Dictionary<int, bool> zoneChains = [];
        if (surfaces != null) CountWork(QueryWork);
        float? best = surfaces?.Hit(start, height, source => excluded.Contains(source) || sharedNodes.ContainsKey(source) || !ZonesAllow(source), ReserveQuery);
        var owner = instances.FirstOrDefault(i => i.Shared);
        if (owner != null)
            foreach (var (index, node) in sharedNodes.ToArray()) Runtime(owner, index, node);
        foreach (var instance in instances.Where(i => !i.Shared && !i.Finished))
            foreach (var (index, node) in instance.Nodes) Runtime(instance, index, node);
        return tested ? (best != null, best, null) : (false, null, AnimationCoverSurfaces.NoSurfaces);

        bool ZonesAllow(int source)
        {
            if (zones is not { } current) return true;
            if (zoneChains.TryGetValue(source, out bool known)) return known;
            bool allowed = true; HashSet<int> seen = [];
            for (int index = source; index >= 0 && index < context.Scene.Nodes.Count && seen.Add(index); index = context.Scene.Nodes[index].Parents.FirstOrDefault(-1))
            {
                operation.Reserve(8);
                var node = context.Scene.Nodes[index];
                if (node.Class == "world") break;
                if (!current.Allows((byte)(node.Metadata.Int("zone_id", 0xFF) & 0xFF))) { allowed = false; break; }
            }
            return zoneChains[source] = allowed;
        }
        void Runtime(Instance instance, int index, Node node)
        {
            operation.Reserve(1); CountWork(FloorNodeWork);
            var source = context.Scene.Nodes[index];
            if (excluded.Contains(index) || source.ModelIndex is not (>= 0 and int model) || model >= context.Scene.Models.Count) return;
            // Every level from the node to the world must be an active intersection surface in the current zones: active as the run
            // has it now (a surface hidden at load and shown by the run is cover), a surface by its static flags.
            Node? part = node; int child = -1; HashSet<int> seen = [];
            for (int at = index; at >= 0 && at < context.Scene.Nodes.Count && seen.Add(at); )
            {
                operation.Reserve(8);
                var scene = context.Scene.Nodes[at];
                if (scene.Class == "world") break;
                bool active = part != null ? part.Active && !part.PendingPlacement : (scene.Metadata.UInt("flags") & 4) != 0;
                if (!active || !AnimationCoverSurfaces.Surface(scene) || zones is { } current && !current.Allows((byte)(scene.Metadata.Int("zone_id", 0xFF) & 0xFF))) return;
                if (child >= 0 && !context.Lods.Includes(at, child, 0)) return;
                int parent = part?.Parent ?? scene.Parents.FirstOrDefault(-1);
                child = at; at = parent;
                part = parent >= 0 ? ParentNode(instance, parent) : null;
            }
            var world = World(instance, node); tested = true; CountWork(QueryWork);
            if (AnimationCoverSurfaces.Hit(context.Scene.Models[model], world, node.Morph, start, height, ReserveQuery) is float y && (best == null || y < best)) best = y;
        }
    }
}
