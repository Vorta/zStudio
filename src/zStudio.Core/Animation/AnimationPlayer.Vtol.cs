using System.Diagnostics;
using System.Numerics;

namespace Recoil.Zbd.Core.Animation;

public sealed partial class AnimationPlayer
{
    private Action<Instance, Sequence, AnimationEvent>? vtolCallback;
    private Action<Instance, Sequence, AnimationEvent>? vtolWindowEvent;

    /// <summary>One isolated first cycle, using the ordinary scheduler and exact target-root transform.</summary>
    internal VtolDropSnapshot AnalyzeVtol(VtolDropCandidate candidate, CancellationToken token)
    {
        measuringDuration = true; // Analysis does not retain seek checkpoints or construct render frames.
        var flight = instances.SingleOrDefault(i => i.Id == 1 && i.Entry.Index == candidate.EntryIndex)
            ?? throw new InvalidDataException("The flight animation has no exact bound root instance.");
        var motion = flight.Sequences.Single(s => s.Data.Id == candidate.MotionSequenceId);
        var schedule = flight.Sequences.Single(s => s.Data.Id == candidate.ScheduleSequenceId);
        var script = motion.Data.Events.Single(e => e.Type == 12);
        using (BeginBinding(token))
            if (NodeRef(flight, script.I32(12))?.Source != flight.Root)
                throw new InvalidDataException("The flight script does not animate its bound root; a release path is unavailable.");
        if (!flight.Nodes.TryGetValue(flight.Root, out var root)) throw new InvalidDataException("The bound flight root is unavailable.");

        List<VtolPathSample> samples = []; List<VtolDropRelease> releases = [];
        Dictionary<Guid, double> dispatchTimes = [];
        var windowIndices = candidate.Windows.ToDictionary(w => w.Id, w => schedule.Data.Events.FindIndex(e => e.Id == w.CallEventId));
        vtolWindowEvent = (instance, state, ev) =>
        {
            if (instance == flight && state == schedule) dispatchTimes.TryAdd(ev.Id, trace[^1].Start);
        };
        vtolCallback = (instance, state, ev) =>
        {
            if (instance.Entry.Index != candidate.CallbackEntryIndex || ev.I32(12) != 911) return;
            if (instance.Root != flight.Root) throw new InvalidDataException("The callback instance binds a different root than the flight.");
            var window = candidate.Windows.SingleOrDefault(w => schedule.Cursor == windowIndices[w.Id] + 1);
            if (window == null) throw new InvalidDataException("A callback occurred outside its authored drop window.");
            if (releases.Count >= VtolDropWindows.MaximumReleases) throw new InvalidDataException("Parachute release markers exceed the analysis allowance.");
            // The observer runs inside the ordinary callback dispatch, before any later sequence can alter this pose.
            Vector3 position = World(flight, root).Translation;
            RequireFinite(position);
            releases.Add(new(window.Id, trace[^1].Start, position, window.Conditional));
        };
        var watch = Stopwatch.StartNew();
        try
        {
            Sample();
            for (int step = 1; step < VtolDropWindows.MaximumSamples; step++)
            {
                token.ThrowIfCancellationRequested(); cues.Clear(); Tick(token);
                if (instances.Any(i => i.Sequences.Any(s => s.State == 4)))
                    throw new InvalidDataException("The flight analysis encountered an unavailable runtime event.");
                Sample();
                if (motion.Iteration > 0)
                {
                    double cycle = Time;
                    if (candidate.Windows.Any(w => !dispatchTimes.ContainsKey(w.CallEventId) || !dispatchTimes.ContainsKey(w.StopEventId)))
                        throw new InvalidDataException("A drop window did not complete inside the flight cycle.");
                    var windows = candidate.Windows.Select((w, i) =>
                    {
                        double begin = dispatchTimes[w.CallEventId], end = dispatchTimes[w.StopEventId];
                        bool delaysNext = i + 1 < candidate.Windows.Count &&
                            candidate.Windows[i + 1].Start < end && Math.Abs(dispatchTimes[candidate.Windows[i + 1].CallEventId] - end) < .00001;
                        return w with
                        {
                            EffectiveStart = begin, EffectiveEnd = end,
                            MinimumStart = delaysNext ? w.Start : i == 0 ? 0 : RoundUp(dispatchTimes[candidate.Windows[i - 1].StopEventId]),
                            MaximumStart = delaysNext ? w.Start : RoundDown(Math.Min(cycle, i + 1 < candidate.Windows.Count ? candidate.Windows[i + 1].Start : cycle) - w.Duration),
                            MoveRefusal = delaysNext ? "This window's stop releases an already-due neighboring window. Moving it independently would change that neighbor; its authored timing is retained." : null
                        };
                    }).ToArray();
                    if (windows.Any(w => w.EffectiveStart < 0 || w.EffectiveEnd > cycle || w.EffectiveEnd <= w.EffectiveStart))
                        throw new InvalidDataException("An authored drop window extends beyond the complete flight cycle.");
                    if (schedule.State != 3)
                        throw new InvalidDataException("The drop schedule does not complete before the flight repeats.");
                    return new(candidate, cycle, flight.Root, samples.AsReadOnly(), Array.AsReadOnly(windows), releases.AsReadOnly());
                }
                if (flight.Finished) throw new InvalidDataException("The flight ended before completing a repeating cycle.");
                if (step % 60 == 0 && watch.Elapsed > TimeSpan.FromSeconds(15))
                    throw new InvalidDataException("Flight analysis exceeded its work limit; no complete path was published.");
            }
            throw new InvalidDataException("Flight analysis exceeded the one-hour path limit; no complete path was published.");
        }
        finally { vtolCallback = null; vtolWindowEvent = null; }

        void Sample()
        {
            token.ThrowIfCancellationRequested();
            var work = new AnimationBindingOperation(context, token);
            var ancestry = BeginFrameAncestry(work);
            Vector3 position;
            try { position = ancestry.Get(flight, root).World.Translation; }
            finally { EndFrameAncestry(ancestry); }
            RequireFinite(position); samples.Add(new(Time, position));
        }
        static void RequireFinite(Vector3 position)
        {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new InvalidDataException("The flight root has a non-finite world position.");
        }
        static float RoundDown(double value) { float result = (float)value; return result > value ? MathF.BitDecrement(result) : result; }
        static float RoundUp(double value) { float result = (float)value; return result < value ? MathF.BitIncrement(result) : result; }
    }
}
