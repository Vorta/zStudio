using System.Numerics;

namespace zStudio.Core.Animation;

public sealed partial class AnimationPlayer
{
    private int Execute(Instance instance, Sequence sequence, AnimationEvent ev, bool starting, ref float remaining)
    {
        ObservePathDiscontinuity(instance, ev, starting);
        if (initializingScene)
        {
            // Initialization recovers poses only. Never create audio, effects, or
            // guess a gameplay-dependent branch while constructing the baseline.
            if (ev.Type is 1 or 2 or 3 or 4 or 5 or 28 or 36 or 37 or 38) return 2;
            if (ev.Type == 10) return 1;
            if (ev.Type == 31 && (ev.U32(12) & ~4u) != 0) throw new InvalidDataException("Gameplay-dependent initialization condition is unavailable.");
        }
        Node? node;
        switch (ev.Type)
        {
            case 1:
                PlaySound(instance, ReferenceName(instance, 4, ev.I16(12)), false, false, 0, OffsetPosition(instance, ev.I16(14), ev.Vector(16))); return 2;
            case 2:
                PlaySound(instance, ev.Text(12), ev.I32(52) != 1, true, ev.I32(44), OffsetPosition(instance, ev.I32(56), ev.Vector(60))); return 2;
            case 3:
                string effectName = ReferenceName(instance, 5, ev.I16(12));
                if (context.Effects.TryGetValue(effectName, out var template) && template.RootNode >= 0)
                {
                    if (effects.Count + instances.Count < MaximumInstances) effects.Add(new() { Id = ++nextId, Template = template, Position = OffsetPosition(instance, ev.I16(14), ev.Vector(16)), Source = TailSource });
                    else { unavailableDuration = true; AddNote("Effect instance limit reached (256)."); }
                }
                else AddNote($"Unresolved effect template: {effectName}");
                return 2;
            case 4:
                SetLight(instance, ev); return 2;
            case 5:
                AnimateLight(instance, sequence, ev); return Timed(sequence, ev.F32(108), ref remaining);
            case 6:
                node = NodeRef(instance, ev.I16(16)); if (node != null) node.Active = ev.I32(12) == 1; return 2;
            case 7:
                node = NodeRef(instance, ev.I16(28));
                if (node != null) Position(node, OffsetPosition(instance, ev.I16(30), ev.Vector(16)), (ev.U32(12) & 1) != 0);
                return 2;
            case 8:
                node = NodeRef(instance, ev.I16(24)); if (node != null) { node.Scale = ev.Vector(12); node.Changed = true; } return 2;
            case 9:
                node = NodeRef(instance, ev.I16(28));
                if (node != null)
                {
                    Vector3 angles = ev.Vector(16); int flags = ev.I32(12) & 7;
                    if (flags is not (0 or 1) && NodeRef(instance, ev.I16(30)) is Node basis)
                    { context.InitializationTransforms?.Reads.Add(basis.Source); angles += basis.Euler; }
                    Rotation(node, angles, flags == 1);
                }
                return 2;
            case 10: return Procedural(instance, sequence, ev, starting, ref remaining);
            case 11:
                node = NodeRef(instance, ev.I32(16));
                if (node != null)
                {
                    uint flags = ev.U32(12); float end = ev.F32(140), t = Math.Clamp(sequence.EventElapsed, 0, Math.Max(0, end)); bool done = sequence.EventElapsed > end;
                    if ((flags & 1) != 0) Position(node, done ? ev.Vector(44) : ev.Vector(32) + ev.Vector(56) * t);
                    if ((flags & 2) != 0) Rotation(node, done ? ev.Vector(80) : ev.Vector(68) + ev.Vector(92) * t);
                    if ((flags & 4) != 0) { node.Scale = done ? ev.Vector(116) : Vector3.Max(new(.001f), ev.Vector(104) + ev.Vector(128) * t); node.Changed = true; }
                    if ((flags & 8) != 0) node.Morph = Math.Clamp(done ? ev.F32(24) : ev.F32(20) + ev.F32(28) * t, 0, 1);
                }
                return Timed(sequence, ev.F32(140), ref remaining);
            case 12: return Keyframes(instance, sequence, ev, ref remaining);
            case 13:
                node = NodeRef(instance, ev.I16(20)); if (node != null) { node.AlphaEnabled = ev.I16(12) == 1; if (ev.I16(14) == 1) node.Alpha = ev.F32(16); } return 2;
            case 14:
                node = NodeRef(instance, ev.I32(12));
                if (node != null)
                {
                    if (starting && ev.I16(16) is 0 or 1) node.AlphaEnabled = ev.I16(16) == 1;
                    node.Alpha = sequence.EventElapsed > ev.F32(32) ? ev.F32(24) : ev.F32(20) + ev.F32(28) * Math.Min(sequence.EventElapsed, ev.F32(32));
                    if (sequence.EventElapsed > ev.F32(32) && ev.I16(18) is 0 or 1) node.AlphaEnabled = ev.I16(18) == 1;
                }
                return Timed(sequence, ev.F32(32), ref remaining);
            case 15:
            case 16:
                var parent = NodeRef(instance, ev.I16(12)); var child = NodeRef(instance, ev.I16(14));
                if (child != null && parent != null)
                {
                    if (ev.Type == 15)
                    {
                        int ancestor = parent.Source; HashSet<int> seen = [];
                        while (instance.Nodes.TryGetValue(ancestor, out var p) && seen.Add(ancestor)) { if (p.Source == child.Source) throw new InvalidDataException("Parenting would create a cycle."); ancestor = p.Parent; }
                        child.Parent = parent.Source;
                    }
                    else if (child.Parent == parent.Source) { child.Parent = -1; child.Active = false; }
                }
                return 2;
            case 17:
                node = NodeRef(instance, ev.I16(16)); if (node != null) { node.Variant = ev.I16(18); node.CycleStart = Time; } return 2;
            case 18: return Beam(instance, sequence, ev, ref remaining);
            case 19:
            case 24:
                if (starting) vtolWindowEvent?.Invoke(instance, sequence, ev);
                return Launch(instance, sequence, ev, starting, Math.Max(0, Time - remaining));
            case 20:
            case 21:
                node = NodeRef(instance, ev.I32(16));
                if (node != null && (ev.U32(12) & 8) != 0)
                {
                    float fov = ev.Type == 20 ? ev.F32(32) : Curve(ev, 56, sequence.EventElapsed, ev.F32(104));
                    node.Fov = fov; node.Changed = true;
                }
                return ev.Type == 20 ? 2 : Timed(sequence, ev.F32(104), ref remaining);
            case 22:
            case 23:
                // A cleanup resolves these against the runtime sequences, which do not run during it: no effect.
                if (instance.Cleanup) return 2;
                int slot = ev.I32(44); var target = slot >= 0 && slot < instance.Sequences.Count ? instance.Sequences[slot] : instance.Sequences.FirstOrDefault(s => s.Data.Name == ev.Text(12));
                if (target == null) AddNote($"Unresolved sequence: {ev.Text(12)}");
                else if (ev.Type == 23) { EndCurrent(instance, target, StopCause.Sequence); target.State = 2; }
                else if (target.State == 3) ReleaseSequence(target, ev, instance, Math.Max(0, Time - remaining), "call");
                return 2;
            case 25:
            case 26:
            case 27:
                if (ev.Type == 25 && starting) vtolWindowEvent?.Invoke(instance, sequence, ev);
                ByName(instance, ev);
                return 2;
            case 28:
                uint fogFlags = ev.U32(44); var current = fog ?? new AnimationFog(false, Vector3.Zero, 0, 1000);
                fog = new((fogFlags & 1) != 0 ? ev.I32(48) != 0 : current.Enabled, (fogFlags & 2) != 0 ? ev.Vector(52) : current.Color,
                    (fogFlags & 8) != 0 ? ev.F32(72) : current.Start, (fogFlags & 8) != 0 ? ev.F32(76) : current.End); return 2;
            case 30:
                sequence.Iteration = (sequence.Iteration + 1) & 65535; sequence.LoopElapsed += sequence.Elapsed;
                uint stop = ev.U32(12);
                if ((stop & 1) != 0 && (ev.U32(16) & 65535) != 65535 && sequence.Iteration == (ev.U32(16) & 65535)) return 2;
                if ((stop & 1) == 0 && (stop & 2) != 0 && ev.F32(16) >= 0 && sequence.LoopElapsed >= ev.F32(16)) return 2;
                sequence.ObservedInfiniteLoop = (stop & 1) != 0 ? (ev.U32(16) & 65535) == 65535 : (stop & 2) == 0 || ev.F32(16) < 0;
                instance.Elapsed = Math.Min(instance.Elapsed, 86400); sequence.Reset(); return 0;
            case 31:
            {
                // HandleConditionalChainEvent (0x45c3c0): a false test scans forward, depth-unaware, to the first ELSE, ELSEIF
                // or ENDIF; an ELSEIF found there is evaluated at once (its START_TIME ignored).
                var head = ev; double at = Math.Max(0, Time - remaining);
                while (!Condition(instance, ev, head, at))
                {
                    int from = sequence.Cursor + 1, next = from;
                    while (next < sequence.Data.Events.Count && sequence.Data.Events[next].Type is not (32 or 33 or 34)) next++;
                    if (next >= sequence.Data.Events.Count) throw new InvalidDataException("Conditional branch has no closing marker.");
                    RecordSkip(instance, sequence, ev, from, next - 1, "false");
                    sequence.Cursor = next; ev = sequence.Data.Events[next]; if (ev.Type != 33) break;
                }
                return 2;
            }
            case 32:
            case 33:
            {
                // A taken body reaching ELSE or ELSEIF skips to ENDIF.
                int from = sequence.Cursor + 1;
                while (++sequence.Cursor < sequence.Data.Events.Count && sequence.Data.Events[sequence.Cursor].Type != 34) { }
                if (sequence.Cursor >= sequence.Data.Events.Count) throw new InvalidDataException("Conditional branch has no End if.");
                RecordSkip(instance, sequence, ev, from, sequence.Cursor - 1, "taken");
                return 2;
            }
            case 34: case 39: case 40: return 2;
            case 35:
                vtolCallback?.Invoke(instance, sequence, ev);
                if (!initializingScene && ev.I32(12) is 10 or 11 or 17) PlayerCallback(instance, ev, ev.I32(12), Math.Max(0, Time - remaining));
                else AddNote($"Game callback {ev.I32(12)} is a trace marker; game code is not executed.", "Support", "Information");
                return 2;
            case 36:
                float duration = ev.F32(60); screenColor = new(Curve(ev, 12, sequence.EventElapsed, duration), Curve(ev, 24, sequence.EventElapsed, duration), Curve(ev, 48, sequence.EventElapsed, duration), Curve(ev, 36, sequence.EventElapsed, duration));
                return Timed(sequence, duration, ref remaining);
            case 37:
                float endTime = ev.F32(108); screenWave = new(Curve(ev, 28, sequence.EventElapsed, endTime), Curve(ev, 40, sequence.EventElapsed, endTime), Curve(ev, 60, sequence.EventElapsed, endTime), Curve(ev, 72, sequence.EventElapsed, endTime));
                return Timed(sequence, endTime, ref remaining);
            case 38: AddNote($"Game text message ID {ev.I32(12)}; text lookup is unavailable in the preview.", "Support", "Information"); return 2;
            // The scheduler records these MW3 gameplay markers and their support notes.
            // They have no preview side effects and must not block following events.
            case 41 when ev.Version == 39:
            case 42 when ev.Version == 39: return 2;
            default: throw new InvalidDataException("No verified event handler.");
        }
    }
    private static float Curve(AnimationRecord ev, int offset, float time, float end) => time > end ? ev.F32(offset + 4) : ev.F32(offset) + ev.F32(offset + 8) * Math.Min(time, end);
    private static int Timed(Sequence state, float duration, ref float remaining)
    {
        if (!float.IsFinite(duration) || duration < 0) throw new InvalidDataException("Invalid event duration.");
        remaining -= Math.Clamp(duration - (state.EventElapsed - remaining), 0, remaining);
        return state.EventElapsed > duration ? 2 : 1;
    }
    private void Position(Node node, Vector3 value, bool add = false)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new InvalidDataException("Nonfinite animation position.");
        node.Position = add ? node.Position + value : value;
        node.Changed = true;
        if (!add) { node.PendingPlacement = false; node.PositionWritten = true; node.GroundSupported = false; }
    }
    private void Rotation(Node node, Vector3 value, bool add = false)
    {
        node.Euler = add ? node.Euler + value : value; node.Rotation = AnimationMath.FromEuler(node.Euler);
        node.Changed = true;
    }
    private Vector3 OffsetPosition(Instance instance, int reference, Vector3 offset)
    {
        if (reference > 0 || reference == -200) return NodeRef(instance, reference) is Node node ? World(instance, node).Translation + offset : offset;
        return offset;
    }
    private static string ReferenceName(Instance instance, int table, int index) => index >= 0 && index < instance.Entry.References[table].Count ? instance.Entry.References[table][index].Text(0) : $"missing reference {index}";
    private void PlaySound(Instance instance, string name, bool stop, bool persistent, int slot, Vector3 position, float gain = 1)
    {
        long key = (instance.Id << 32) | (uint)(slot > 0 ? slot : StableHash(name));
        if (stop) { activeSounds.Remove(key); cues.Add(new(key, name, true, persistent, Time, position)); return; }
        if (!context.Sounds.ContainsKey(name)) { unavailableDuration = true; AddNote($"Unresolved sound: {name}"); return; }
        persistent |= context.Sounds[name].Loop;
        if (!persistent) key = (++nextId << 32) | 0xffff;
        var cue = new AnimationSoundCue(key, name, false, persistent, Math.Max(0, Time - StepSeconds), position) { Gain = gain };
        cues.Add(cue); activeSounds[key] = cue;
        if (measuringDuration && !persistent) soundSources[key] = TailSource;
    }
    private static int StableHash(string name) { uint hash = 2166136261; foreach (char c in name) hash = unchecked((hash ^ c) * 16777619); return (int)(hash & 0x7fffffff); }
    /// <summary>
    /// One IF or ELSEIF test (HandleConditionalChainEvent 0x45c3c0 [B, BN]), operand bits tested in the order 0x01, 0x02,
    /// 0x08, 0x10, 0x04: a draw from the seeded table (random in the game), the squared distance from the player reference
    /// to the callback node, an upward segment from the reference, an upward segment from a node (the chain's first IF names
    /// it, as in the game), the effects level. A scoped override or the global condition switch takes precedence.
    /// </summary>
    private bool Condition(Instance instance, AnimationEvent ev, AnimationEvent head, double dispatchedAt)
    {
        uint mask = ev.U32(12);
        bool forced = false, value;
        string kind = (mask & 1) != 0 ? "random" : (mask & 2) != 0 ? "playerRange" : (mask & 8) != 0 ? "playerCover" : (mask & 0x10) != 0 ? "cover" : (mask & 4) != 0 ? "effectsDetail" : "none";
        double? measured = null, threshold = null; int? draw = null; string? unavailable = null; Vector3? center = null;
        if ((mask & 0x1f) != 0 && ExecutionPolicy?.TryCondition(instance.CallPath, instance.Entry.Index, ev.Id, out value) == true) forced = true;
        else if ((mask & 1) != 0)
        {
            draw = randomIndex; float unit = RandomUnit();
            value = unit <= ev.F32(20); measured = unit; threshold = ev.F32(20);
        }
        else if ((mask & 2) != 0)
        {
            threshold = MathF.Sqrt(Math.Max(0, ev.F32(20)));
            if (CurrentReference.Position is Vector3 player)
            {
                center = CallbackPosition(instance);
                float distance = center is Vector3 at ? Vector3.DistanceSquared(player, at) : 0;
                value = distance <= ev.F32(20); measured = MathF.Sqrt(distance);
            }
            else { value = false; unavailable = "no player"; }
        }
        else if ((mask & 0x18) != 0)
        {
            threshold = ev.F32(20);
            Vector3? start = null; int excluded = -1;
            if ((mask & 8) != 0) { start = CurrentReference.Position; if (start == null) unavailable = "no player"; }
            else if (NodeRef(instance, head.I32(16)) is { } node) { start = World(instance, node).Translation; excluded = node.Source; }
            else unavailable = "no node";
            value = false;
            if (initializingScene) unavailable ??= "not evaluated while the mission loads";
            else if (start is Vector3 from)
            {
                var (hit, height, reason) = Cover(from, ev.F32(20), excluded);
                value = hit; measured = height - from.Y; unavailable = reason;
                if (reason != null) AddNote($"{instance.Entry.Name}: a cover condition cannot be evaluated ({reason}); it reads false.", "Support", "Information");
            }
        }
        else
        {
            value = (mask & 4) != 0 && EffectLevel >= ev.I32(20);
            if ((mask & 4) != 0) { measured = EffectLevel; threshold = ev.I32(20); }
        }
        if (!initializingScene)
        {
            if (conditions.Count >= 20000) { conditions.RemoveRange(0, 10000); conditionsDropped += 10000; InvalidateRetainedState(); }
            RequireState(128);
            conditions.Add(new(instance.Id, ev.Id, instance.CallPath, dispatchedAt, value, forced, trace.Count == 0 ? 0 : trace[^1].Occurrence)
                { Kind = kind, Measured = measured, Threshold = threshold, Draw = draw, Unavailable = unavailable, Center = center });
            AdjustRetainedBytes(128);
        }
        return value;
    }
    private void ReleaseSequence(Sequence target, AnimationEvent ev, Instance releaser, double at, string kind)
    {
        target.State = 0; target.ReleasedBy = ev.Id; target.ReleasedByInstance = releaser.Id; target.ReleasedAt = at; target.ReleaseKind = kind;
        // A released pass runs on its releaser's timeline; one a ground contact released follows that contact.
        if (measuringDuration) Released(releaser, target, kind == "contact");
    }
    /// <summary>
    /// STOP_ANIMATION, RESET_ANIMATION and INVALIDATE_ANIMATION by name (0x45c100, 0x45c1a0, 0x45c240 [B, BN]) act on one
    /// entry, the first in entry order with that name (never the first entry of the package), on its base record only.
    /// </summary>
    private void ByName(Instance caller, AnimationEvent ev)
    {
        var target = entryLookup.Find(ev.Text(12));
        if (target == null || target.Index <= 0) { NamedTrace("unresolved", target?.Index, null); return; }
        var run = EntryState(target);
        var live = LiveBase(target);
        string disposition = "ignored";
        if (ev.Type == 25)
        {
            // Stop acts only on a running base (states 2 and 6).
            if (run.State is 2 or 6 && live != null && !live.Cleanup) { StopInstance(live); disposition = "applied"; }
        }
        else if (ev.Type == 26)
        {
            // zEffAnimReset in any state but stopped (1) and unavailable (5): rest pose, then cleanup, then stopped.
            if (run.State is not (1 or 5))
            {
                if (live != null) { if (measuringDuration) Restart(live, DispatchTimeline); BeginCleanup(live, live.SavedNodes, StopCause.Reset); }
                else ResetDormant(target, run);
                disposition = "applied";
            }
        }
        else if (run.State != 5)
        {
            // A running entry keeps running (its loops too) and refuses every later start; any other becomes invalidated.
            SetEntryState(target, run with { State = run.State == 2 ? (byte)6 : (byte)4, Time = Time, Cause = "invalidated" });
            disposition = "applied";
        }
        NamedTrace(disposition, target.Index, live?.Id);

        void NamedTrace(string result, int? entry, long? instance)
        {
            if (initializingScene || trace.Count == 0) return;
            int index = trace.FindLastIndex(t => t.Instance == caller.Id && t.Event == ev.Id && t.End == null);
            if (index >= 0) trace[index] = trace[index] with { Disposition = result, ChildEntry = entry, ChildInstance = instance };
        }
    }
    /// <summary>
    /// A reset of a binding with no running base (finished with its pose kept, invalidated or never run): its tracked nodes
    /// return to the rest pose captured at its last activation (or at mission load) and its cleanup runs on its own nodes.
    /// </summary>
    private void ResetDormant(AnimationEntry target, EntryRun run)
    {
        // A finished base the preview still holds (a scenario start, or a child kept for its final pose) is reset in place.
        if (run.Base > 0 && instances.FirstOrDefault(i => i.Id == run.Base) is { } held)
        {
            held.Finished = false; held.StopDelay = -1;
            if (measuringDuration) Restart(held, DispatchTimeline);
            BeginCleanup(held, run.Rest, StopCause.Reset);
            return;
        }
        int? bound = run.Root >= 0 && run.Root != bindingOperation!.Root(target) ? run.Root : null;
        var cleanup = AddInstance(target, null, bound, primary: true, callPath: run.Path, activateRoot: false);
        if (cleanup == null) return;
        SetEntryState(target, run with { Base = cleanup.Id, Time = Time, Cause = "reset" });
        RestoreRestPose(cleanup, run.Rest);
        if (target.Primary.Events.Count == 0) { Finish(cleanup); return; }
        if (RunZeroTime(cleanup, cleanup.Sequences[0]) && !cleanup.Finished && cleanup.Sequences[0].State is not (0 or 1 or 5)) Finish(cleanup);
    }
    private int Launch(Instance parent, Sequence sequence, AnimationEvent ev, bool starting, double dispatchedAt)
    {
        int slot = ev.I16(50);
        if (starting)
        {
            string name = AnimationAudioDependencies.ChildName(ev);
            var target = entryLookup.ResolveChild(ev);
            string path = "";
            if (!initializingScene && ExecutionPolicy is { } policy)
            {
                // A call whose path would pass the allowance (a call cycle every call admits) is left out under every policy,
                // never run with another instance's path.
                string? candidate;
                try { candidate = AnimationExecutionPolicy.ChildPath(parent.CallPath, parent.Entry.Index, ev.Id, target?.Index ?? -1); }
                catch (InvalidDataException) { candidate = null; }
                if (candidate == null || !policy.TryInclude(candidate, out path))
                {
                    hasExcludedCalls = true;
                    bool waited = ev.Type == 24 && (ev.I16(46) & 16) != 0;
                    string? reason = candidate == null ? AnimationExecutionPolicy.CallPathTooDeep + (waited ? "; awaiting excluded dependency" : "; child omitted") : null;
                    LaunchTrace(waited ? "excluded_wait" : "excluded", target?.Index, reason: reason);
                    if (candidate == null)
                        AddNote($"{sequence.Data.Name}: call to {name} left out: {TooDeepNote} (a call cycle).", "Preview");
                    else if (waited) AddNote($"{sequence.Data.Name}: awaiting excluded child {name}.", "Preview", "Information");
                    return waited ? 5 : 2;
                }
            }
            if (target == null)
            {
                unavailableDuration = true; AddNote($"Unresolved child animation: {name}");
                LaunchTrace("unresolved", null); return 2;
            }
            Vector3? position = null; int? bound = null;
            if (ev.Type == 19) position = OffsetPosition(parent, ev.I16(52), ev.Vector(56));
            else
            {
                int flags = (ushort)ev.I16(46);
                if ((flags & 9) != 0) position = OffsetPosition(parent, ev.I16(52), ev.Vector(56));
                if (ev.I16(44) > 0 && NodeRef(parent, ev.I16(44)) is Node n) bound = n.Source;
            }
            int targetRoot = bound ?? bindingOperation!.Root(target);
            bool shared = targetRoot >= 0 && IsWorldNode(targetRoot) && ((target.U32(148) & 0x8000) == 0 || bound.HasValue);
            var existing = shared ? instances.FirstOrDefault(i => i.Entry.Index == target.Index && i.Root == targetRoot && !i.Finished)
                ?? (bound.HasValue ? null : LiveBase(target)) : null;
            // Each launch clears its cached child first, then keeps the one it started (0x45bc60, 0x45b8b0); the launcher's
            // FinalizeStop stops the cached LOCAL_NAME children. Children also keeps a finished child's final pose shown.
            if (slot >= 0 && parent.Locals.Remove(slot)) InvalidateRetainedState();
            var child = AddInstance(target, position, bound, started: true, callPath: path, parentInstance: parent.Id, callEvent: ev.Id, startedAt: dispatchedAt);
            // A call back into its own chain closes a call cycle: the cycle has gone round once, and what it does again repeats.
            if (child != null && measuringDuration) MeasureCall(parent, sequence, child, dispatchedAt);
            if (child != null)
            {
                sequence.Child = child.Id; if (slot >= 0) { parent.Children[slot] = child.Id; parent.Locals[slot] = child.Id; }
                if (ev.Type == 24 && (ev.I16(46) & 4) != 0 && child.Nodes.TryGetValue(child.Root, out var root)) Rotation(root, ev.Vector(68));
            }
            LaunchTrace(child != null ? "launched" : lastRefusal == "alreadyRunning" ? "duplicate" : lastRefusal != null ? "refused" : "unavailable",
                target.Index, child?.Id, existing?.Id);
            AddNote("Nested animation activation uses deterministic preview ordering; gameplay velocity and activation references may differ.", "Support", "Information");
        }
        if (ev.Type == 24 && (ev.I16(46) & 16) != 0 && instances.Any(i => i.Id == sequence.Child && !i.Finished)) return 1;
        return 2;

        void LaunchTrace(string disposition, int? childEntry, long? childInstance = null, long? existingInstance = null, string? reason = null)
        {
            if (initializingScene || trace.Count == 0) return;
            int index = trace.FindLastIndex(t => t.Instance == parent.Id && t.Event == ev.Id && t.End == null);
            if (index >= 0) trace[index] = trace[index] with
            {
                Disposition = disposition, ChildEntry = childEntry, ChildInstance = childInstance, ExistingInstance = existingInstance,
                Status = reason ?? disposition switch
                {
                    "excluded" => "Child omitted by scenario", "excluded_wait" => "Awaiting excluded dependency",
                    "duplicate" => "Already running; start suppressed", "unresolved" => "Unresolved child; preview incomplete",
                    "unavailable" => "Child activation unavailable", "refused" => "Start refused: " + RefusalText(lastRefusal), _ => trace[index].Status
                }
            };
        }
    }
    private int Keyframes(Instance instance, Sequence state, AnimationEvent ev, ref float remaining)
    {
        var node = NodeRef(instance, ev.I32(12)); if (node == null) return 2;
        var frames = ev.PlaybackKeyframes(); if (frames.Count == 0) return 2;
        // Retail 0x45B120/0x45AE90 advance a sample cursor from the event start. A
        // sample's local time runs from max(cursor, start); a finished sample leaves
        // the cursor at its end. Authored reversed spans therefore finish as soon as
        // they start, and sample end - start (negative) unless a same-channel
        // successor replaces them.
        float cursor = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i]; if (state.EventElapsed < frame.Start) break;
            ObserveKeyframeCut(node, frames, i, state.EventElapsed, remaining);
            float entry = Math.Max(cursor, frame.Start); bool finished = state.EventElapsed > frame.End;
            float time = finished ? frame.End - entry : state.EventElapsed - entry;
            if (finished) cursor = frame.End;
            if (finished && i + 1 < frames.Count && frames[i + 1].Flags == frame.Flags && state.EventElapsed >= frames[i + 1].Start) continue;
            int p = frame.ChannelOffset(0), r = frame.ChannelOffset(1), s = frame.ChannelOffset(2);
            if (p >= 0) Position(node, frame.Vector(p) + frame.Vector(p + 16) * time);
            if (r >= 0)
            {
                var q = Quaternion.Multiply(AnimationMath.FromRotationVector(frame.Vector(r + 16) * time), AnimationMath.ReadQuaternion(frame, r));
                if (!float.IsFinite(q.LengthSquared()) || q.LengthSquared() < 1e-10) throw new InvalidDataException("Invalid keyframe quaternion.");
                node.Rotation = Quaternion.Normalize(q); node.Euler = AnimationMath.ToEuler(node.Rotation); node.Changed = true;
            }
            if (s >= 0) { node.Scale = frame.Vector(s) + frame.Vector(s + 16) * time; node.Changed = true; }
        }
        return Timed(state, ev.PlaybackEnd, ref remaining);
    }
    private int Procedural(Instance instance, Sequence state, AnimationEvent ev, bool starting, ref float remaining)
    {
        var node = NodeRef(instance, ev.I32(16)); if (node == null) throw new InvalidDataException("Procedural motion has no target.");
        uint flags = ev.U32(12);
        if (starting)
        {
            if ((flags & 0x400) == 0) ev.SetFloat(ev.Version == 39 ? 328 : 248, 0);
            if ((flags & 0x100) != 0) ev.SetVector(196, ev.Vector(172));
            if ((flags & 0x20) != 0) ev.SetVector(160, ev.Vector(136));
            if ((flags & 4) != 0) { ev.SetVector(88, ev.Vector(64)); ev.SetVector(100, ev.Vector(76)); }
            if ((flags & 8) != 0)
            {
                // Retail 0x459F72–0x45A0DB: yaw, pitch, speed, then radial acceleration.
                // The reconstruction's temporary names obscure the first/fourth draw order.
                float yaw = RandomRange(32), pitch = RandomRange(40), speed = RandomRange(48), acceleration = RandomRange(56);
                float vertical = pitch / 90, horizontal = vertical < 0 ? 1 + vertical : 1 - vertical;
                // Vec3DirFromYaw (0x474580) rotates the -Z forward vector.
                Vector3 direction = new(-horizontal * MathF.Sin(yaw * MathF.PI / 180), vertical, -horizontal * MathF.Cos(yaw * MathF.PI / 180));
                ev.SetVector(112, direction); ev.SetVector(64, direction * speed); ev.SetVector(76, direction * acceleration);
                ev.SetVector(88, ev.Vector(64)); ev.SetVector(100, ev.Vector(76));
            }
            if ((flags & 0xc0) != 0) { ev.SetFloat(132, ev.F32(124)); ev.SetFloat(136, ev.F32(128)); ev.SetFloat(140, ev.F32(132)); }
            if ((flags & 1) != 0)
            {
                Vector3 gravity = new(0, ev.F32(24), 0);
                // Retail adds local Y unless 0x2000 requests the transposed world basis.
                // A runtime inherited-velocity basis is unavailable in manual activation.
                if ((flags & 0x2000) != 0)
                    gravity = Vector3.TransformNormal(gravity, Matrix4x4.Transpose(World(instance, node)));
                ev.SetVector(100, ev.Vector(100) + gravity);
            }
            if ((flags & 1) != 0) AddNote(OriginCollision && Terrain != null ? "Preview collision queries current map surfaces beneath each moving node origin." : Terrain != null ? "Preview collision uses standable map surfaces captured at preview start."
                : GroundPlaneEnabled ? "Flat-ground preview collision at Y=0 uses mesh-based contact and retail impact responses."
                : "Ground collision is disabled; gravity and launch motion are simulated.", "Support", "Information");
            if ((flags & 2) != 0) AddNote("Inherited gameplay launch velocity is unavailable.", "Support", "Information");
        }
        float dt = (flags & 0x400) != 0 ? Math.Clamp(ev.F32(ev.Version == 39 ? 328 : 248) - (state.EventElapsed - remaining), 0, remaining) : remaining;
        bool translating = (flags & 12) != 0;
        Vector3 previousOrigin = World(instance, node).Translation;
        if (translating && ConstrainProcedural(instance, node, previousOrigin))
        {
            NoteProceduralLimit(instance, node);
            remaining -= dt; return 2;
        }
        bool ground = (GroundPlaneEnabled || Terrain != null) && (flags & 1) != 0 && translating && dt > 0
            && context.Scene.Nodes[node.Source].Class != "camera";
        // Depenetrate initial overlap without emitting a contact. All subsequent
        // motion (including spin/morph) is considered before resolving the floor.
        bool originContact = false, originSettled = false;
        bool originGround = ground && OriginCollision && Terrain != null;
        float previousBottom = ground && !originGround ? PrepareGround(instance, node) : 0;
        previousOrigin = World(instance, node).Translation;
        List<Vector3>? previousPoints = ground && !originGround && Terrain != null ? GroundPoints(instance, node) : null;
        if (originGround) originSettled = MoveWithOriginCollision(instance, node, ev, dt, out originContact);
        else if ((flags & 12) != 0) Position(node, ev.Vector(88) * dt, true);
        if ((flags & 0x20) != 0) { Rotation(node, ev.Vector(160) * dt, true); ev.SetVector(160, ev.Vector(160) + ev.Vector(148) * dt); }
        if ((flags & 0x40) != 0) Rotation(node, new Vector3(ev.F32(96), 0, -ev.F32(88)) * (dt * ev.F32(132)), true);
        if ((flags & 0x80) != 0) { Rotation(node, new Vector3(ev.F32(120), 0, -ev.F32(112)) * (dt * ev.F32(132)), true); ev.SetFloat(132, ev.F32(132) + ev.F32(128) * dt); }
        if ((flags & 0x100) != 0) { node.Scale = Vector3.Max(new(.001f), node.Scale + ev.Vector(196) * dt); node.Changed = true; }
        if ((flags & 0x200) != 0) node.Morph = Math.Clamp(node.Morph + ev.F32(28) * dt, 0, 1);
        bool limited = translating && ConstrainProcedural(instance, node, previousOrigin), contact = originContact;
        bool settled = originSettled || ground && !originGround && (Terrain != null
            ? ResolveTerrain(instance, node, ev, previousPoints!, previousOrigin, out contact)
            : ResolveGround(instance, node, ev, previousBottom, out contact));
        // A surface crossed before the boundary owns the contact. Bounds themselves never emit impact cues.
        limited = limited && !contact;
        if (limited) NoteProceduralLimit(instance, node);
        if ((flags & 12) != 0) ev.SetVector(88, ev.Vector(88) + ev.Vector(100) * dt);
        remaining -= dt;
        return limited || settled || (flags & 0x400) != 0 && state.EventElapsed > ev.F32(ev.Version == 39 ? 328 : 248) ? 2 : 1;
        float RandomRange(int offset) => ev.F32(offset) + (ev.F32(offset + 4) - ev.F32(offset)) * RandomUnit();
    }
    private int Beam(Instance instance, Sequence state, AnimationEvent ev, ref float remaining)
    {
        var node = NodeRef(instance, ev.I16(16)); if (node == null) return 2;
        uint flags = ev.U32(12); float time = Math.Clamp(state.EventElapsed, 0, Math.Max(0, ev.F32(80)));
        Vector3 origin = ActivationStart ?? (instance.Nodes.TryGetValue(instance.Root, out var root) ? World(instance, root).Translation : Vector3.Zero);
        Vector3 target = ReferencePosition ?? origin + Vector3.UnitZ * 10;
        Vector3 a = (flags & 8) != 0 ? ev.Vector(24) : (flags & 0x10) != 0 ? origin : Vector3.Zero;
        Vector3 b = (flags & 0x100) != 0 ? ev.Vector(36) : (flags & 0x200) != 0 ? target : Vector3.Zero;
        if ((flags & 1) != 0 && NodeRef(instance, ev.I16(18)) is Node an) a = Vector3.Transform(a, World(instance, an));
        else if ((flags & 6) != 0 && (flags & 0x10) == 0) a += origin;
        if ((flags & 0x20) != 0 && NodeRef(instance, ev.I16(20)) is Node bn) b = Vector3.Transform(b, World(instance, bn));
        else if ((flags & 0xc0) != 0 && (flags & 0x200) == 0) b += target;
        if ((flags & 0x2d6) != 0) AddNote("Beam activation points use the Preview settings (default: root to 10 units along Z).", "Support", "Information");
        float start = (flags & 0x400) != 0 ? ev.F32(48) : (flags & 0x800) != 0 ? ev.F32(48) + ev.F32(56) * time : 0;
        float end = (flags & 0x1000) != 0 ? ev.F32(64) : (flags & 0x2000) != 0 ? ev.F32(64) + ev.F32(72) * time : 1;
        if (state.EventElapsed > ev.F32(80)) { if ((flags & 0x800) != 0) start = ev.F32(52); if ((flags & 0x2000) != 0) end = ev.F32(68); }
        Vector3 delta = b - a; float length = delta.Length(); Position(node, a + delta * start);
        if (length > 1e-6f) Rotation(node, new(-MathF.Asin(Math.Clamp(delta.Y / length, -1, 1)), MathF.Atan2(delta.X, delta.Z), 0));
        node.Scale.Z = length * Math.Abs(end - start); node.Changed = true;
        return Timed(state, ev.F32(80), ref remaining);
    }
    private void SetLight(Instance instance, AnimationEvent ev)
    {
        long id = (instance.Id << 32) | (uint)(ev.I32(44) > 0 ? ev.I32(44) : StableHash(ev.Text(12)));
        var old = lights.GetValueOrDefault(id) ?? new AnimationLight(id, Vector3.Zero, Vector3.One, 10, 1, false); uint fields = ev.U32(48);
        int extra = ev.Version == 39 ? 8 : 0;
        Vector3 position = (fields & 2) != 0 ? OffsetPosition(instance, ev.I32(68 + extra), ev.Vector(72 + extra)) : (fields & 1) != 0 ? ev.Vector(72 + extra) : old.Position;
        lights[id] = new(id, position, (fields & 0x10) != 0 ? Vector3.Clamp(ev.Vector(104 + extra),Vector3.Zero,Vector3.One) : old.Color,
            (fields & 8) != 0 ? Math.Max(.01f,ev.F32(100 + extra)) : old.Range, (fields & 0x20) != 0 ? Math.Max(0,ev.F32(116 + extra)) : old.Intensity, ev.I32(52) == 1);
    }
    private void AnimateLight(Instance instance, Sequence sequence, AnimationEvent ev)
    {
        long id = (instance.Id << 32) | (uint)(ev.I32(44) > 0 ? ev.I32(44) : StableHash(ev.Text(12)));
        var old = lights.GetValueOrDefault(id) ?? new AnimationLight(id, Vector3.Zero, Vector3.One, 10, 1, true);
        float time = Math.Min(sequence.EventElapsed, Math.Max(0, ev.F32(108)));
        lights[id] = old with { Range = Math.Max(.01f, ev.F32(52) + ev.F32(60) * time), Color = Vector3.Clamp(ev.Vector(72) + ev.Vector(84) * time, Vector3.Zero, Vector3.One) };
    }
}
