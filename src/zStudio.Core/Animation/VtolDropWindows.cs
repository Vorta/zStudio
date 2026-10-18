using System.Buffers.Binary;
using System.Security.Cryptography;

namespace zStudio.Core.Animation;

/// <summary>Existing callback-911 flight windows. All writes change one clock threshold, never the flight or callback program.</summary>
public static class VtolDropWindows
{
    public const int MaximumSamples = 216001;
    public const int MaximumWindows = 256;
    public const int MaximumReleases = 4096;
    private const int MaximumCandidates = 256;
    internal const long MaximumPackageBytes = 128L * 1024 * 1024;

    public static VtolDropDiscovery Discover(AnimationPackage package, CancellationToken token = default)
    {
        CheckPackage(package, token);
        if (package.Version != 28) return new([], ["Parachute windows require a RECOIL version-28 animation package."]);
        AnimationEntryLookup lookup = new(package);
        List<VtolDropCandidate> candidates = []; List<string> diagnostics = [];
        string? fingerprint = null;
        foreach (var entry in package.Entries)
        {
            token.ThrowIfCancellationRequested();
            foreach (var schedule in entry.Sequences)
            {
                if (!schedule.Events.Any(e => e.Type == 24 && e.Bytes.Length >= 80 &&
                    lookup.ResolveChild(e, token)?.Sequences.Any(s => s.Events.Any(IsDropCallback)) == true)) continue;
                try
                {
                    var candidate = Recognize(package, lookup, entry, schedule, token);
                    if (candidates.Count == MaximumCandidates) throw new InvalidDataException("Parachute flight count exceeds the analysis allowance.");
                    fingerprint ??= Fingerprint(package, token);
                    candidates.Add(candidate with { Fingerprint = fingerprint });
                }
                catch (InvalidDataException ex)
                {
                    if (diagnostics.Count < MaximumCandidates) diagnostics.Add($"Animation #{entry.Index}, sequence {schedule.Name}: {ex.Message}");
                }
            }
        }
        return new(candidates.AsReadOnly(), diagnostics.AsReadOnly());
    }

    private static VtolDropCandidate Recognize(AnimationPackage package, AnimationEntryLookup lookup,
        AnimationEntry entry, AnimationSequence schedule, CancellationToken token)
    {
        void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
        Require(schedule.IsEditable && schedule.ResetMode == 3, "The drop schedule must be an explicitly released sequence.");
        Require(schedule.Events.Count >= 3 && schedule.Events.Count <= 1 + MaximumWindows * 2 && (schedule.Events.Count & 1) == 1,
            "The drop schedule must contain an initial stop followed by bounded call/stop pairs.");
        var initial = schedule.Events[0];
        Require(initial.Type == 25 && initial.Bytes.Length >= 48 && initial.Threshold == 0, "The drop schedule requires an immediate initial stop.");
        List<VtolDropWindow> windows = [];
        AnimationEntry? callback = null;
        bool conditional = false;
        for (int i = 1; i < schedule.Events.Count; i += 2)
        {
            token.ThrowIfCancellationRequested();
            var call = schedule.Events[i]; var stop = schedule.Events[i + 1];
            Require(call.Type == 24 && call.Bytes.Length >= 80 && call.StartMode == 2 && float.IsFinite(call.Threshold) && call.Threshold >= 0,
                "A window must start with an absolute sequence-time CALL_ANIMATION.");
            Require(call.I16(44) <= 0 && call.I16(46) == 0, "Relocated, rotated or waiting child activations are not supported drop windows.");
            var child = lookup.ResolveChild(call, token);
            Require(child != null, "A drop callback animation is unresolved.");
            callback ??= child!;
            Require(ReferenceEquals(callback, child), "A drop schedule targets different callback entries.");
            Require(stop.Type == 25 && stop.Bytes.Length >= 48 && stop.StartMode == 3 && float.IsFinite(stop.Threshold) && stop.Threshold > 0,
                "A window must end with a positive relative-time STOP_ANIMATION.");
            Require(stop.Text(12) == callback.Name && initial.Text(12) == callback.Name,
                "The call and stop do not target the same callback entry.");
            Require(package.Entries.Count(e => e.Name == callback.Name) == 1, "The name-wide stop has more than one animation target.");
            Require(windows.Count == 0 || windows[^1].Start <= call.Threshold,
                "Drop windows are not in authored order.");
            windows.Add(new(call.Id, entry.Index, schedule.Id, call.Id, stop.Id, call.Threshold, stop.Threshold, 0, 0, false));
        }
        Require(callback!.RootName == entry.RootName && callback.AttachName == entry.AttachName && callback.F32(164) < 0,
            "The callback must stop immediately and bind the same authored root as the flight.");
        Require(callback.Primary.Events.Count == 0 && callback.Sequences.Count == 1,
            "The callback animation has additional execution paths.");
        var callbacks = callback.Sequences[0];
        Require(callbacks.ResetMode != 3 && callbacks.IsEditable, "The callback sequence cannot wait for another release.");
        var ce = callbacks.Events;
        conditional = ce.Count == 4 && ce[0].Type == 31 && ce[0].Bytes.Length >= 24 && ce[0].U32(12) == 2 &&
            IsDropCallback(ce[1]) && ce[2].Type == 34;
        Require(conditional || ce.Count == 2 && IsDropCallback(ce[0]), "The callback must be 911, optionally gated by player range, with a repeating cadence.");
        Require(IsInfiniteLoop(ce[^1]) && ce[^1].StartMode == 3 && ce[^1].Threshold > 0 && float.IsFinite(ce[^1].Threshold),
            "The callback cadence must be a positive relative-time infinite loop.");
        Require(ce.Take(ce.Count - 1).All(e => e.Threshold == 0), "The callback contains additional timing gates.");

        var callers = entry.Sequences.SelectMany(s => s.Events.Where(e => e.Type == 22 && e.Bytes.Length >= 48 && ResolveSequence(entry, e) == schedule).Select(e => (Sequence: s, Event: e))).ToArray();
        Require(callers.Length == 1, "The schedule must have exactly one flight-sequence caller.");
        var motion = callers[0].Sequence;
        Require(motion.ResetMode != 3 && motion.IsEditable && motion.Events.Count(e => e.Type == 12) == 1 && motion.Events.Count(e => e.Type == 22) == 1,
            "The flight must have one motion script and one schedule release.");
        Require(motion.Events.All(e => e.Type is 6 or 12 or 22 or 30) && IsInfiniteLoop(motion.Events[^1]) &&
            motion.Events.Count(e => e.Type == 30) == 1 && motion.Events.All(e => e.Threshold == 0),
            "The flight contains unsupported timing or control flow.");
        Require(motion.Events.IndexOf(callers[0].Event) < motion.Events.FindIndex(e => e.Type == 12), "The schedule must begin before the motion script.");
        Require(entry.Sequences.Where(s => s != motion && s != schedule).All(s => s.Events.All(e => e.Type is 1 or 2 or 6 or 15)),
            "Another flight sequence can change the path or callback lifetime.");
        for (int i = 0; i < windows.Count; i++) windows[i] = windows[i] with
        {
            Conditional = conditional,
            MinimumStart = i == 0 ? 0 : windows[i - 1].Start + windows[i - 1].Duration,
            MaximumStart = i + 1 == windows.Count ? float.MaxValue : windows[i + 1].Start - windows[i].Duration
        };
        return new(entry.Index, entry.Name, entry.RootName, motion.Id, schedule.Id, callback.Index, windows.AsReadOnly(), "");
    }

    private static bool IsDropCallback(AnimationEvent e) => e.Type == 35 && e.Bytes.Length >= 16 && e.I32(12) == 911;
    private static bool IsInfiniteLoop(AnimationEvent e) => e.Type == 30 && e.Bytes.Length >= 20 && (e.U32(12) & 1) != 0 && (e.U32(16) & 65535) == 65535;
    private static AnimationSequence? ResolveSequence(AnimationEntry entry, AnimationEvent ev) =>
        ev.I32(44) is int slot && slot >= 0 && slot < entry.Sequences.Count ? entry.Sequences[slot] : entry.Sequences.FirstOrDefault(s => s.Name == ev.Text(12));

    public static VtolDropSnapshot Analyze(AnimationPreviewContext frozenContext, VtolDropCandidate candidate, CancellationToken token = default)
    {
        CheckCandidate(frozenContext.Package, candidate, token);
        // The caller supplies a frozen context. The player creates its own runtime nodes and never mutates serialized entries.
        return new AnimationPlayer(frozenContext, candidate.EntryIndex, 1, false, token) { ConditionOverride = true }
            .AnalyzeVtol(candidate, token);
    }

    public static VtolDropMovePlan PlanMove(VtolDropSnapshot snapshot, Guid windowId, float start)
    {
        var window = snapshot.Windows.SingleOrDefault(w => w.Id == windowId) ?? throw new InvalidDataException("The selected drop window no longer exists.");
        if (start == window.Start) return new(snapshot.Candidate, window, start);
        if (window.MoveRefusal != null) throw new InvalidDataException(window.MoveRefusal);
        if (!float.IsFinite(start) || start < window.MinimumStart || start > window.MaximumStart ||
            (double)start + window.Duration > snapshot.CycleSeconds)
            throw new InvalidDataException($"Window start must be between {window.MinimumStart:G9} and {window.MaximumStart:G9} seconds without crossing another window or the flight cycle.");
        return new(snapshot.Candidate, window, start);
    }

    public static VtolDropMovePlan PlanMove(VtolDropSnapshot snapshot, Guid windowId, int segmentIndex, float fraction)
    {
        if (segmentIndex < 0 || segmentIndex >= snapshot.Samples.Count - 1 || !float.IsFinite(fraction) || fraction < 0 || fraction > 1)
            throw new InvalidDataException("Select a flight-path segment and a fraction between zero and one.");
        double a = snapshot.Samples[segmentIndex].Time, b = snapshot.Samples[segmentIndex + 1].Time;
        return PlanMove(snapshot, windowId, checked((float)(a + (b - a) * fraction)));
    }

    public static void ValidatePlan(AnimationPackage package, VtolDropMovePlan plan, CancellationToken token = default)
    {
        CheckCandidate(package, plan.Candidate, token);
        var original = plan.Candidate.Windows.SingleOrDefault(w => w.Id == plan.Window.Id);
        if (original == null || original.EntryIndex != plan.Window.EntryIndex || original.SequenceId != plan.Window.SequenceId ||
            original.CallEventId != plan.Window.CallEventId || original.StopEventId != plan.Window.StopEventId ||
            original.Start != plan.Window.Start || original.Duration != plan.Window.Duration ||
            !float.IsFinite(plan.NewStart) || plan.NewStart != original.Start &&
            (plan.Window.MoveRefusal != null || plan.NewStart < plan.Window.MinimumStart || plan.NewStart > plan.Window.MaximumStart))
            throw new InvalidDataException("The drop-window edit is stale or outside its legal range.");
        var sequence = package.Entries[original.EntryIndex].Sequences.Single(s => s.Id == original.SequenceId);
        if (sequence.Events.Single(e => e.Id == original.CallEventId).Threshold != original.Start ||
            sequence.Events.Single(e => e.Id == original.StopEventId).Threshold != original.Duration)
            throw new InvalidDataException("The drop-window timing changed during editing.");
    }

    public static void Apply(AnimationEditSession session, VtolDropMovePlan plan, CancellationToken token = default)
    {
        ValidatePlan(session.Package, plan, token);
        RequireVerifiedMove(plan);
        token.ThrowIfCancellationRequested();
        session.Apply(plan.Window.EntryIndex, "Move parachute drop window", entry =>
            entry.Sequences.Single(s => s.Id == plan.Window.SequenceId).Events.Single(e => e.Id == plan.Window.CallEventId).Threshold = plan.NewStart);
    }

    public static VtolDropSnapshot PreviewMove(AnimationPreviewContext frozenContext, VtolDropSnapshot original,
        VtolDropMovePlan plan, CancellationToken token = default)
    {
        ValidatePlan(frozenContext.Package, plan, token);
        if (plan.NewStart == plan.Window.Start) return original;
        CheckPackage(frozenContext.Package, token);
        var copy = frozenContext.Snapshot(); token.ThrowIfCancellationRequested();
        // Snapshot owns every cloned entry. This disposable simulation needs no document undo session,
        // whose compiled-edit comparison would otherwise serialize the before/after entry records.
        copy.Package.Entries[plan.Window.EntryIndex].Sequences.Single(s => s.Id == plan.Window.SequenceId)
            .Events.Single(e => e.Id == plan.Window.CallEventId).Threshold = plan.NewStart;
        var candidate = Discover(copy.Package, token).Candidates.SingleOrDefault(c => c.EntryIndex == original.Candidate.EntryIndex && c.ScheduleSequenceId == original.Candidate.ScheduleSequenceId)
            ?? throw new InvalidDataException("The edited drop window no longer has a supported flight schedule.");
        var preview = Analyze(copy, candidate, token);
        VerifyMove(original, preview, plan);
        token.ThrowIfCancellationRequested();
        plan.RuntimeVerifiedStart = plan.NewStart;
        return preview;
    }

    internal static void RequireVerifiedMove(VtolDropMovePlan plan)
    {
        if (plan.NewStart != plan.Window.Start && plan.RuntimeVerifiedStart != plan.NewStart)
            throw new InvalidDataException("Preview this drop-window move before accepting it so its duration, cadence and neighboring windows can be verified.");
    }

    private static void VerifyMove(VtolDropSnapshot original, VtolDropSnapshot preview, VtolDropMovePlan plan)
    {
        const double tolerance = 0.00001;
        if (original.CycleSeconds != preview.CycleSeconds || original.Windows.Count != preview.Windows.Count)
            throw new InvalidDataException("The move changes the flight cycle or drop schedule.");
        foreach (var before in original.Windows)
        {
            var after = preview.Windows.Single(w => w.Id == before.Id);
            bool selected = before.Id == plan.Window.Id;
            if (!selected && (Math.Abs(before.EffectiveStart - after.EffectiveStart) > tolerance || Math.Abs(before.EffectiveEnd - after.EffectiveEnd) > tolerance))
                throw new InvalidDataException("This move changes a neighboring window's dispatch time; choose a different start.");
            if (Math.Abs((before.EffectiveEnd - before.EffectiveStart) - (after.EffectiveEnd - after.EffectiveStart)) > tolerance)
                throw new InvalidDataException("This start changes the window's effective duration at the runtime clock boundary; choose a different start.");
            var first = original.Releases.Where(r => r.WindowId == before.Id).ToArray();
            var second = preview.Releases.Where(r => r.WindowId == before.Id).ToArray();
            if (first.Length != second.Length) throw new InvalidDataException("This start changes the number of eligible releases; choose a different start.");
            for (int i = 0; i < first.Length; i++)
            {
                double a = first[i].Time - (selected ? before.EffectiveStart : 0);
                double b = second[i].Time - (selected ? after.EffectiveStart : 0);
                if (Math.Abs(a - b) > tolerance || !selected && first[i].Position != second[i].Position)
                    throw new InvalidDataException("This move changes the release cadence or a neighboring release; choose a different start.");
            }
        }
    }

    private static void CheckCandidate(AnimationPackage package, VtolDropCandidate candidate, CancellationToken token)
    {
        if (candidate.EntryIndex < 0 || candidate.EntryIndex >= package.Entries.Count || Fingerprint(package, token) != candidate.Fingerprint)
            throw new InvalidDataException("The animation changed; analyze its parachute windows again.");
    }

    internal static string Fingerprint(AnimationPackage package, CancellationToken token = default)
    {
        CheckPackage(package, token);
        // Candidates belong to an in-memory snapshot, including source packages that have never been
        // serialized. Frame every collection and record so different record boundaries cannot alias.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Bytes(package.Prefix); Bytes(package.Tail); Count(package.Entries.Count);
        foreach (var entry in package.Entries)
        {
            Bytes(entry.Bytes); Count(entry.References.Length);
            foreach (var table in entry.References)
            {
                Count(table.Count);
                foreach (var record in table) Bytes(record.Bytes);
            }
            Count(entry.Puffers.Count);
            foreach (var puffer in entry.Puffers) Bytes(puffer.Bytes);
            Count(1 + entry.Sequences.Count);
            foreach (var sequence in entry.AllSequences)
            {
                Bytes(sequence.Bytes); Bytes(sequence.OpaqueTail); Count(sequence.Events.Count);
                foreach (var ev in sequence.Events) Bytes(ev.Bytes);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());

        void Count(int count)
        {
            token.ThrowIfCancellationRequested();
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, count); hash.AppendData(bytes);
        }
        void Bytes(byte[] bytes)
        {
            Count(bytes.Length);
            for (int offset = 0; offset < bytes.Length; offset += 4096)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(bytes.AsSpan(offset, Math.Min(4096, bytes.Length - offset)));
            }
        }
    }

    private static void CheckPackage(AnimationPackage package, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (package.Entries.Count > 20000) throw new InvalidDataException("Animation discovery exceeds its entry allowance.");
        long size = package.Prefix.LongLength + package.Tail.LongLength; int events = 0;
        if (size > MaximumPackageBytes) throw new InvalidDataException("Animation discovery exceeds its byte allowance.");
        foreach (var entry in package.Entries)
        {
            token.ThrowIfCancellationRequested(); size += entry.Bytes.LongLength;
            foreach (var table in entry.References) foreach (var record in table) size += record.Bytes.LongLength;
            foreach (var puffer in entry.Puffers) size += puffer.Bytes.LongLength;
            foreach (var sequence in entry.AllSequences)
            {
                size += sequence.Bytes.LongLength + sequence.OpaqueTail.LongLength;
                foreach (var ev in sequence.Events)
                {
                    token.ThrowIfCancellationRequested(); size += ev.Bytes.LongLength;
                    if (++events > 1_000_000 || size > MaximumPackageBytes) throw new InvalidDataException("Animation discovery exceeds its byte/event allowance.");
                }
            }
            if (size > MaximumPackageBytes) throw new InvalidDataException("Animation discovery exceeds its byte allowance.");
        }
    }
}
