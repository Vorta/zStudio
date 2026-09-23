using System.Diagnostics;

namespace Recoil.Zbd.Core.Animation;

public enum AnimationDurationKind { Finite, Looping, OpenEnded, Unavailable, AnalysisLimit }

/// <summary>A finite preview duration, or an explicitly bounded view of an event program with no known end.</summary>
public sealed record AnimationDuration(int Frames, AnimationDurationKind Kind, string Explanation)
{
    public double Seconds => Frames * AnimationPlayer.StepSeconds;
    public bool IsFinite => Kind == AnimationDurationKind.Finite;
}

public sealed partial class AnimationPlayer
{
    private bool measuringDuration;
    private bool unavailableDuration;
    private double measuredEnd;

    /// <summary>
    /// Evaluate an independent player with the same activation, seed and branch settings. No rendering,
    /// audio output, or changes to this player's pose/time. Call on a context snapshot if edits can occur.
    /// </summary>
    public AnimationDuration MeasureDuration(CancellationToken token = default)
    {
        var probe = new AnimationPlayer(context, entryIndex, Seed, resetPhase)
        {
            ConditionOverride = ConditionOverride, EffectLevel = EffectLevel,
            ReferencePosition = ReferencePosition, ActivationStart = ActivationStart, GroundPlaneEnabled = GroundPlaneEnabled, PreviewHeight = PreviewHeight, measuringDuration = true
        };
        return probe.MeasureCore(token);
    }

    private AnimationDuration MeasureCore(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (instances.Count == 0) return Result(5, AnimationDurationKind.Unavailable, "Bind a scene root to determine the duration.");
        var budget = Stopwatch.StartNew();
        double tailsEnd = 0;
        // Bounded work even for malformed, recursive, collision-driven or very long programs.
        for (int step = 0; step < 216000; step++)
        {
            cues.Clear(); Tick(token);
            foreach (var cue in activeSounds.Values.Where(c => !c.Persistent))
                tailsEnd = Math.Max(tailsEnd, cue.StartedAt + context.Sounds[cue.Name].Duration);
            foreach (var effect in effects) tailsEnd = Math.Max(tailsEnd, Time + Math.Max(0, 1 - effect.Age));
            bool unavailable = unavailableDuration || instances.Any(i => i.Sequences.Any(s => s.State == 4)) || notes.Any(n => n.Contains("instance limit", StringComparison.OrdinalIgnoreCase));
            if (IsComplete && activeSounds.Count == 0)
            {
                double end = Math.Max(measuredEnd, tailsEnd);
                if (unavailable) return Result(Math.Max(5, end), AnimationDurationKind.Unavailable, "An unavailable event or resource prevents a complete duration calculation.");
                if (instances.Any(i => i.Sequences.Any(s => s.State == 3 && s.Data.Events.Count > 0)))
                    return Result(Math.Max(5, end), AnimationDurationKind.OpenEnded, "A sequence waits for release; this is a preview range, not a total duration.");
                return Result(end, AnimationDurationKind.Finite, "Calculated for the current phase, seed and conditions, including nested animations, cleanup, effects and sound tails.");
            }
            var live = instances.Where(i => !i.Finished).ToArray();
            var active = live.SelectMany(i => i.Sequences).Where(s => s.State is 0 or 1).ToArray();
            // Do not stop at a loop if another sequence can still stop it or launch a finite child.
            if (active.Length > 0 && live.All(i => i.StopDelay < 0) && active.All(s => IsUnconditionalLoop(s) || IsUnboundedMotion(s)))
            {
                bool looping = active.Any(s => s.ObservedInfiniteLoop);
                return Result(Math.Max(5, Math.Max(Time, tailsEnd)), looping ? AnimationDurationKind.Looping : AnimationDurationKind.OpenEnded,
                    looping ? "The active program repeats indefinitely. This is a preview range; extend it in Preview options."
                            : "Motion has no timed end and depends on game state or collision. Extend the preview range in Preview options.");
            }
            if (step % 60 == 0 && budget.Elapsed > TimeSpan.FromSeconds(2))
                return Result(Math.Max(5, Math.Min(Time, 3600)), AnimationDurationKind.AnalysisLimit, "Duration analysis reached its work limit. This is an adjustable preview range.");
        }
        return Result(3600, AnimationDurationKind.AnalysisLimit, "The program exceeds the one-hour preview limit.");
    }

    private bool IsUnboundedMotion(Sequence state) => state.State == 1 && state.Work is { Type: 10 } ev && (ev.U32(12) & 0x400) == 0
        && !(GroundPlaneEnabled && (ev.U32(12) & 1) != 0 && (ev.U32(12) & 12) != 0);
    // A later random branch, child, or named stop can terminate an otherwise unbounded loop.
    private static bool IsUnconditionalLoop(Sequence state) => state.ObservedInfiniteLoop &&
        state.Data.Events.All(e => e.Type is not (19 or 24 or 25 or 26 or 27 or 31 or 33));
    private static AnimationDuration Result(double seconds, AnimationDurationKind kind, string explanation) =>
        new((int)Math.Clamp(Math.Ceiling(seconds * 60 - .01), 1, 216000), kind, explanation);
}
