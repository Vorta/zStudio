using System.Diagnostics;

namespace zStudio.Core.Animation;

public enum AnimationDurationKind { Finite, Looping, OpenEnded, Unavailable, AnalysisLimit, DependencyBlocked }

/// <summary>
/// A scenario's Auto playback range (docs/agents/preview.md, "Animation runtime and duration"): where it ends and why.
/// <see cref="AnimationDurationKind.Finite"/>: the run ends there, sound and effect tails included.
/// <see cref="AnimationDurationKind.Looping"/>: every sequence that loops forever has gone round its loop once (a constant spin: one
/// revolution).
/// <see cref="AnimationDurationKind.OpenEnded"/>: something without a timed end goes on or waits, so the range ends 5 s after the
/// last timed event (<see cref="LastTimedSeconds"/>), or at a sound or effect tail or a constant spin's first revolution that
/// outlasts those 5 s.
/// <see cref="AnimationDurationKind.Unavailable"/> and <see cref="AnimationDurationKind.DependencyBlocked"/> end where the preview
/// can follow the run; <see cref="AnimationDurationKind.AnalysisLimit"/> where a safety net stopped the analysis.
/// </summary>
public sealed record AnimationDuration(int Ticks, AnimationDurationKind Kind, string Explanation)
{
    public double Seconds => Ticks * AnimationPlayer.StepSeconds;
    public bool IsFinite => Kind == AnimationDurationKind.Finite;
    /// <summary>The last timed event when the range ends 5 s after it (<see cref="AnimationDurationKind.OpenEnded"/>), else null.</summary>
    public double? LastTimedSeconds { get; init; }
    /// <summary>The range ends 5 s after the last timed event: motion or a wait may go on past it (Extend +5 s follows it).</summary>
    public bool AfterLastTimed => LastTimedSeconds != null;
    /// <summary>
    /// The 5 s rule applies, but a constant spin's first revolution ends later than those 5 s (and any tail): the range ends where
    /// every spin has gone round once.
    /// </summary>
    public bool EndsAtSpin { get; init; }
    /// <summary>
    /// The wall-clock backstop stopped the measurement (a machine far busier than the work limit assumes), not its work limit:
    /// the result depends on the machine. It is kept as it is (never measured again by itself); Auto measures again.
    /// </summary>
    public bool TimedOut { get; init; }
    /// <summary>The work the measurement spent, in <see cref="AnimationPlayer.DurationWorkLimit"/>'s units.</summary>
    internal long Work { get; init; }
}

public sealed partial class AnimationPlayer
{
    /// <summary>
    /// Duration analysis's safety net: a work limit in units that follow what each simulated tick does, each weighted by what it
    /// costs (about a nanosecond a unit on a current desktop): the tick itself, the instances and effects it holds, the events it
    /// dispatches (more for one that starts, more again for a call), the nodes an origin-collision floor query considers, the
    /// surface and collision queries with the branches and triangles they test, the runtime collision triangles built, the mesh
    /// vertices of a ground contact and the nodes the settled-ground pass visits (the weights below). So the same scenario always
    /// stops at the same time whatever the machine does meanwhile (a cold start, a busy view). The analysis normally stops long
    /// before it, when the run's timed part is over (<see cref="MeasureDuration"/>); timed work that never ends (a call cycle, a
    /// loop with a huge count) reaches it, and so does a run too heavy to follow (m1 destroy_the_gen after Include all, 2.517 s
    /// into its run), whose range then ends where the limit stopped it (<see cref="AnimationDurationKind.AnalysisLimit"/>, which
    /// says so). Calibrated on every 1999 binding when the analysis ran each program to its end: that one stopped after about
    /// 2.3 s of wall time there.
    /// </summary>
    public const long DurationWorkLimit = 2_000_000_000;
    // The weights of the work limit's units (see DurationWorkLimit).
    internal const long TickWork = 500, InstanceWork = 200, EffectWork = 3000, EventWork = 300, EventStartWork = 600, CallWork = 1500,
        FloorNodeWork = 25, QueryWork = 400, QueryVisitWork = 5, TriangleWork = 50, GroundVertexWork = 50, SettledNodeWork = 2;
    /// <summary>The wall-clock backstop of duration analysis: only a machine far slower than the work limit assumes reaches it.</summary>
    public static readonly TimeSpan DurationBackstop = TimeSpan.FromSeconds(15);
    /// <summary>The 5 s rule: a run that goes on without a timed end (or waits) shows this long after its last timed event.</summary>
    public const double AfterLastTimedSeconds = 5;
    /// <summary>
    /// A loop's first iteration counts as at least this long from the start of its pass, so a one-tick loop (the player runs at
    /// least one tick per pass) is shown for a second wherever it starts.
    /// </summary>
    public const double MinimumLoopSeconds = 1;
    /// <summary>
    /// The longest first revolution a constant spin counts as a loop for (it turns at least 6° a second on average): a slower spin
    /// is motion without a timed end, under the 5 s rule, so a tiny rate never makes the range run away.
    /// </summary>
    public const double MaximumSpinSeconds = 60;
    /// <summary>The shortest Auto range that is not a run's own end.</summary>
    public const double MinimumRangeSeconds = 5;
    // A sequence pass's (and a binding's) timeline in duration analysis: authored clocks, after something without a timed end,
    // or a repeat of a first loop iteration (not counted).
    internal const byte TimedTimeline = 0, UntimedTimeline = 1, RepeatTimeline = 2;
    private bool measuringDuration;
    private bool unavailableDuration;
    private double measuredEnd;
    private long measureWork;
    // The timed work a run has left (TimedWorkLeft): none; only a pass off authored time that can still reach an event its
    // authored clock starts (a look-ahead, not a timed point); or timed work.
    private const int NoTimedWork = 0, TimedEventAhead = 1, TimedWorkRunning = 2;
    // Duration analysis only: the timed points recorded so far (a change says the run's timed part went on).
    private long timedPoints;
    // Duration analysis only: the last timed point, the last point that is not a repeat, every tail, the dispatching sequence,
    // the timelines bindings ended on, the sounds started this tick and the sequences whose events left tails.
    private double lastTimed, countedEnd, tailsEnd;
    private Sequence? dispatching;
    private Instance? dispatchingInstance;
    private readonly Dictionary<long, byte> endedTimelines = [];
    private readonly Dictionary<long, Sequence?> soundSources = [];
    private readonly HashSet<Sequence> tailed = [];
    /// <summary>The call cycles closed (the binding called back into, and when): each one's first round.</summary>
    private readonly List<(string Name, double At)> cycles = [];
    /// <summary>The sequences found to loop forever, in the order their first iteration ended.</summary>
    private readonly List<Sequence> looped = [];
    /// <summary>Counts work toward the duration analysis's limit (only while measuring).</summary>
    private void CountWork(long units) { if (measuringDuration) measureWork += units; }
    /// <summary>A surface or collision query's work allowance, which also counts toward the duration analysis's limit.</summary>
    private void ReserveQuery(long units) { ReserveGround(units); CountWork(units * QueryVisitWork); }

    /// <summary>
    /// The scenario's Auto range from an independent player with the same activation, seed, branch settings and inputs. No
    /// rendering, audio output, or changes to this player's pose/time. Call on a context snapshot if edits can occur.
    /// </summary>
    /// <remarks>
    /// The run is simulated until its timed part is over: no scheduled start is pending, no binding runs on authored clocks any
    /// more and no pass off them can still reach an event an authored clock starts. Such a pass is only a look-ahead: unless the
    /// event dispatches (a timed point), the range ends as it would have when the rest of the timed part was over, so an event
    /// the run never dispatches (in a branch it skips) changes nothing. A <b>timed event</b> is an occurrence whose
    /// dispatch time authored clocks alone set: a scheduled start; an event that waited for its animation- or sequence-clock
    /// threshold on a clock that started on authored time (the binding's, started there and never frozen by a gate; the
    /// sequence's, in a pass begun there), which brings its pass back to authored time; and, on a pass on authored time, an event
    /// whose START_TIME threshold (animation, sequence or after-previous clock) or predecessor let it start and the end of an
    /// instant event, keyframes, an interpolated transform, a fade, a beam, a light, a camera FOV or screen curve, or procedural
    /// motion with a timed end. What an event starts, releases, stops or resets runs on that event's timeline. A sequence pass
    /// leaves authored time, with everything it then starts, when it runs procedural motion without a timed end past its
    /// dispatch tick (falling until a contact, flying) or one a contact or the Y limit ends before its timer, when it resumes
    /// after a waited call whose child ended untimed, when a ground contact released it, or when its binding is paused by a range
    /// or zone gate. A sequence <b>loops forever</b> when its LOOP has no count (65535) or no time limit and sends it back to its
    /// first event still running; its <b>first iteration</b> ends when that LOOP first fires (at least
    /// <see cref="MinimumLoopSeconds"/> after its pass began), and its later passes, with what they start, are repeats and are
    /// not counted. A <b>constant spin</b> (<see cref="SpinRevolution"/>: procedural motion without a timer that only turns its
    /// node) loops forever too: its first iteration is one revolution from its start (at least <see cref="MinimumLoopSeconds"/>).
    /// The end is then: 5 s after the last timed event (or a sound or effect tail or a spin's first revolution past it, named)
    /// when something without a timed end still runs or waits (motion, events after a contact, a gated binding, a sequence of a
    /// binding still running on authored time waiting for its release); else, when a sequence still loops or spins, the latest
    /// first iteration's end or other counted event; else the run's end with its sound and effect tails.
    /// </remarks>
    public AnimationDuration MeasureDuration(CancellationToken token = default)
    {
        AnimationPlayer probe;
        try
        {
            probe = new AnimationPlayer(context, entryIndex, Seed, resetPhase, token, maximumRetainedStateBytes,
                () => RetainedStateBytes + (otherRetainedState?.Invoke() ?? 0), schedule)
            {
                EffectLevel = EffectLevel, ExecutionPolicy = ExecutionPolicy,
                ReferencePosition = ReferencePosition, ActivationStart = ActivationStart, GroundPlaneEnabled = GroundPlaneEnabled, Terrain = Terrain, OriginCollision = OriginCollision,
                ProceduralLimitsEnabled = ProceduralLimitsEnabled, PreviewHeight = PreviewHeight, measuringDuration = true
            };
        }
        catch (StateLimitException)
        { return Result(Math.Max(MinimumRangeSeconds, Time), AnimationDurationKind.AnalysisLimit, StateAllowance(Time)); }
        try { return probe.MeasureCore(token) with { Work = probe.measureWork }; }
        catch (StateLimitException)
        { return Result(Math.Max(MinimumRangeSeconds, probe.Time), AnimationDurationKind.AnalysisLimit, StateAllowance(probe.Time)) with { Work = probe.measureWork }; }
    }
    private static string StateAllowance(double at) =>
        $"Duration analysis reached the retained animation-state allowance at {AnimationSeconds.Format(at)}; Extend +5 s or a custom end lengthens the range.";

    private AnimationDuration MeasureCore(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (instances.Count == 0 && !PendingStarts) return Result(5, AnimationDurationKind.Unavailable, "Bind a scene root to determine the duration.");
        var backstop = Stopwatch.StartNew();
        measureWork = 0;
        // Where the range ends when only a look-ahead keeps the analysis going (a pass off authored time that can still reach an
        // event its authored clock starts): the end there stands unless a timed point follows (that event dispatched), so an
        // event the run never dispatches (in a branch it skips, past a threshold the pass reaches late) changes nothing.
        AnimationDuration? untimedEnd = null; long timedAt = 0;
        // The rule's own end comes first; the work limit, the one-hour clock and the wall-clock backstop are safety nets.
        for (int step = 0; step < 216000; step++)
        {
            cues.Clear(); Tick(token);
            measureWork += TickWork + InstanceWork * instances.Count + EffectWork * effects.Count;
            RecordTails();
            if (timedPoints != timedAt) untimedEnd = null;
            if (!PendingStarts && TimedWorkLeft() is var left && left != TimedWorkRunning)
            {
                if (left == NoTimedWork) return untimedEnd ?? Settle();
                if (untimedEnd == null) { untimedEnd = Settle(); timedAt = timedPoints; }
            }
            if (measureWork > DurationWorkLimit)
                return untimedEnd ?? Result(Math.Max(MinimumRangeSeconds, Math.Min(Time, 3600)), AnimationDurationKind.AnalysisLimit,
                    $"Duration analysis reached its work limit at {AnimationSeconds.Format(Time)} while the run's timed part went on; Extend +5 s or a custom end lengthens the range.");
            if (backstop.Elapsed > DurationBackstop)
                return Result(Math.Max(MinimumRangeSeconds, Math.Min(Time, 3600)), AnimationDurationKind.AnalysisLimit,
                    $"Duration analysis stopped at its {DurationBackstop.TotalSeconds:0} s wall-clock backstop at {AnimationSeconds.Format(Time)}, before its work limit: this machine was too busy, so the result depends on the machine. Auto measures it again; Extend +5 s or a custom end lengthens the range.") with { TimedOut = true };
        }
        return untimedEnd ?? Result(3600, AnimationDurationKind.AnalysisLimit, "The run's timed part goes on past the one-hour preview limit.");
    }

    /// <summary>The run's timed part is over: where the range ends, and why.</summary>
    private AnimationDuration Settle()
    {
        var live = instances.Where(i => !i.Finished).ToArray();
        // Sequences still going round a loop that repeats forever (their first iteration counted), constant spins among them.
        var loops = live.Where(i => i.StopDelay < 0).SelectMany(i => i.Sequences.Where(s => s.LoopsForever && s.State is 0 or 1 && !s.Retired)).ToArray();
        var spins = loops.Where(s => s.Spins).ToArray();
        // The latest tail (a sound's before an effect's that ends with it: the set's order is no order). A loop's own tails are left
        // out (its next pass plays them again); a spin's count: they come from events before the spin, which never ends its pass, so
        // they play once, to their end.
        Sequence? tail = null;
        foreach (var s in tailed)
            if ((s.Spins || !loops.Contains(s)) && (tail == null || s.Tail > tail.Tail || s.Tail == tail.Tail && s.SoundTail && !tail.SoundTail)) tail = s;
        double tails = tail?.Tail ?? 0;
        bool unavailable = unavailableDuration || instances.Any(i => i.Sequences.Any(s => s.State == 4));
        const string Left = "; an unavailable event or resource is left out (Diagnostics lists it)";
        if (UntimedReason(live) is { } reason)
        {
            double rule = Math.Max(lastTimed + AfterLastTimedSeconds, tails);
            // A constant spin goes round once even so (the latest of the ends).
            var governingSpin = spins.Length > 0 ? spins.MaxBy(LoopEnd) : null;
            bool spun = governingSpin != null && LoopEnd(governingSpin) > rule + 1e-9;
            double end = spun ? LoopEnd(governingSpin!) : rule;
            // A sound, an effect or a spin that outlasts the 5 s says so.
            string later = spun ? $" · spins: {Names(spins)} once round{Floored(governingSpin!)} to {AnimationSeconds.Format(Rounded(end))}"
                : tails > lastTimed + AfterLastTimedSeconds + 1e-9 ? $" · {(tail!.SoundTail ? "sound" : "effect")} tail to {AnimationSeconds.Format(Rounded(end))}" : "";
            return Result(end, AnimationDurationKind.OpenEnded, $"5 s after the last timed event ({AnimationSeconds.Format(lastTimed)}) · {reason}{later}" + (unavailable ? Left : ""))
                with { LastTimedSeconds = lastTimed, EndsAtSpin = spun };
        }
        double counted = Math.Max(countedEnd, tails);
        // The 5 s minimum, said where it sets the end.
        string Minimum(double end) => end < MinimumRangeSeconds ? $" · shown for at least {AnimationSeconds.Short(MinimumRangeSeconds)}" : "";
        if (live.SelectMany(i => i.Sequences.Where(s => s.State == 5 && Timeline(i, s) != RepeatTimeline)).FirstOrDefault() is { } blocked)
            return Result(Math.Max(MinimumRangeSeconds, counted), AnimationDurationKind.DependencyBlocked,
                $"An excluded waited call blocks {blocked.Data.Name} at {AnimationSeconds.Format(Rounded(counted))}: include that call to see beyond it" + Minimum(counted));
        // A loop's or a call cycle's repeat still runs (a repeated pass, what one started or released, a cycle's next round; a
        // spin's pass only goes round).
        bool repeating = live.Any(i => i.Timeline == RepeatTimeline && (i.StopDelay >= 0 || i.Sequences.Any(Active))
            || i.Sequences.Any(s => Timeline(i, s) == RepeatTimeline && Active(s) && !s.Spins));
        if (loops.Length > 0 || repeating)
        {
            // The loops and spins still going round, else the loops whose repeats still run; and the call cycles.
            var shown = loops.Length > 0 ? loops : looped.ToArray();
            var governing = shown.Length > 0 ? shown.MaxBy(LoopEnd) : null;
            bool cycling = cycles.Count > 0 && (repeating || loops.Length == 0);
            double loopEnd = Math.Max(governing != null ? LoopEnd(governing) : 0, cycling ? cycles.Max(c => c.At) : 0), end = Math.Max(loopEnd, counted);
            var looping = shown.Where(s => !s.Spins).Select(s => s.Data.Name).Concat(cycling ? cycles.Select(c => c.Name + " (a call cycle)") : []).Distinct(StringComparer.Ordinal).ToArray();
            var spinning = shown.Where(s => s.Spins).ToArray();
            if (looping.Length == 0 && spinning.Length == 0) looping = ["a repeat"];
            // The governing loop or spin says when the floor set its end.
            string Floor(bool spin) => governing != null && governing.Spins == spin && LoopEnd(governing) >= loopEnd - 1e-9 ? Floored(governing) : "";
            string text = (looping.Length > 0 ? $"Loops: {List(looping)} once round{Floor(false)}" : "")
                + (spinning.Length > 0 ? (looping.Length > 0 ? " · spins: " : "Spins: ") + $"{Names(spinning)} once round{Floor(true)}" : "")
                + (counted > loopEnd + 1e-9 ? $" · the rest ends at {AnimationSeconds.Format(Rounded(counted))}" : "") + Minimum(end);
            return Result(Math.Max(MinimumRangeSeconds, end), AnimationDurationKind.Looping, text + (unavailable ? Left : ""));
        }
        // Its last completion, dispatch (a LOOP that sends an ON_CALL sequence back to waiting completes nothing) or tail.
        double finite = Math.Max(Math.Max(measuredEnd, countedEnd), tailsEnd);
        if (unavailable) return Result(Math.Max(MinimumRangeSeconds, finite), AnimationDurationKind.Unavailable,
            $"The run ends at {AnimationSeconds.Format(Rounded(finite))}{Left}" + Minimum(finite));
        return Result(finite, AnimationDurationKind.Finite, $"The run ends at {AnimationSeconds.Format(Rounded(finite))}, sound and effect tails included"
            + (hasExcludedCalls ? "; excluded calls are left out" : ""));
    }
    private static string List(string[] names) => names.Length switch { 1 => names[0], 2 => names[0] + " and " + names[1], _ => $"{names[0]}, {names[1]} and {names.Length - 2} more" };
    private static string Names(Sequence[] sequences) => List(sequences.Select(s => s.Data.Name).Distinct(StringComparer.Ordinal).ToArray());
    /// <summary>A loop's or a spin's first iteration that the floor (<see cref="MinimumLoopSeconds"/>) lengthened says so.</summary>
    private static string Floored(Sequence s) => LoopEnd(s) > s.LoopEnd + 1e-9 ? $" (at least {AnimationSeconds.Short(MinimumLoopSeconds)})" : "";
    /// <summary>A time as the range shows it: at the end of its 1/60 s tick.</summary>
    private static double Rounded(double seconds) => Ticks(seconds) * StepSeconds;
    private static int Ticks(double seconds) => (int)Math.Clamp(Math.Ceiling(seconds * 60 - .01), 1, 216000);
    /// <summary>
    /// A forever loop's first iteration's end: its LOOP, at least <see cref="MinimumLoopSeconds"/> after its pass began; a constant
    /// spin's first revolution, at least that long after the spin began.
    /// </summary>
    private static double LoopEnd(Sequence s) => Math.Max(s.LoopEnd, s.LoopStart + MinimumLoopSeconds);

    /// <summary>
    /// A constant spin's first revolution, in seconds from its start, or null. A constant spin is procedural motion (OBJECT_MOTION)
    /// without a timer (RUN_TIME, flag 0x400) that only turns its node: XYZ_ROTATION (0x20) with an angular rate (Euler rates at
    /// byte 136, radians per second) or an angular acceleration (byte 148) on some axis, and nothing that moves it otherwise: no
    /// TRANSLATION or TRANSLATION_RANGE (4, 8: a fall or a flight, whose landing or limit ends the turning too), no
    /// FORWARD_ROTATION (0x80) or the distance turn (0x40), which turn across a direction of travel, and no scale (0x100) or morph
    /// (0x200) rate. Gravity alone (flag 1) moves nothing. Nothing but a stop or a reset ends it, so it counts as a loop that
    /// repeats forever: its first iteration is the time its fastest axis takes to turn 360° (θ = ωt + αt²/2, the player's
    /// integration). A degenerate rate, or a spin that would take longer than <see cref="MaximumSpinSeconds"/> to go round once,
    /// stays motion without a timed end (null).
    /// </summary>
    internal static double? SpinRevolution(AnimationEvent motion)
    {
        uint flags = motion.U32(12);
        if ((flags & 0x20) == 0 || (flags & (0x400 | 0xC | 0xC0)) != 0) return null;
        if ((flags & 0x100) != 0 && motion.Vector(172) is var scale && (scale.X != 0 || scale.Y != 0 || scale.Z != 0)) return null;
        if ((flags & 0x200) != 0 && motion.F32(28) != 0) return null;
        var rate = motion.Vector(136); var acceleration = motion.Vector(148);
        double turn = Math.Min(Revolution(rate.X, acceleration.X), Math.Min(Revolution(rate.Y, acceleration.Y), Revolution(rate.Z, acceleration.Z)));
        return turn <= MaximumSpinSeconds ? turn : null;
    }
    /// <summary>The first time one axis turning at <paramref name="rate"/> with <paramref name="acceleration"/> has turned a full turn either way.</summary>
    private static double Revolution(double rate, double acceleration)
    {
        if (!double.IsFinite(rate) || !double.IsFinite(acceleration) || rate == 0 && acceleration == 0) return double.PositiveInfinity;
        // Turning in the rate's direction (an acceleration alone turns its own way): θ = wt + at²/2 with w ≥ 0.
        double w = Math.Abs(rate), a = rate < 0 ? -acceleration : rate > 0 ? acceleration : Math.Abs(acceleration);
        const double FourPi = 4 * Math.PI;
        // Speeding up, or slowing down but still a full turn before it stops: the forward root of wt + at²/2 = 2π (in a form
        // exact as a → 0).
        if (w * w + FourPi * a >= 0) return FourPi / (w + Math.Sqrt(w * w + FourPi * a));
        // It stops short of a turn and turns back: a full turn the other way, wt + at²/2 = −2π.
        return (w + Math.Sqrt(w * w - FourPi * a)) / -a;
    }
    /// <summary>A sequence with work left: running or waiting for a threshold it can reach.</summary>
    private static bool Active(Sequence s) => s.State is 0 or 1 && !s.Retired && !NeverStarts(s);

    /// <summary>
    /// The timed work the run has left: <see cref="TimedWorkRunning"/> while a binding has a pending stop delay or a sequence
    /// that runs or waits for its threshold on authored time; else <see cref="TimedEventAhead"/> while a pass off it can still
    /// reach an event its authored clock starts (the analysis follows it, but only the event's dispatch is a timed point); else
    /// <see cref="NoTimedWork"/>.
    /// </summary>
    private int TimedWorkLeft()
    {
        int left = NoTimedWork;
        foreach (var instance in instances)
        {
            if (instance.Finished) continue;
            if (instance.StopDelay >= 0) { if (instance.Timeline == TimedTimeline) return TimedWorkRunning; continue; }
            foreach (var sequence in instance.Sequences)
            {
                // A sequence that can never start is no work.
                if (sequence.State is not (0 or 1) || sequence.Retired || NeverStarts(sequence)) continue;
                byte timeline = Timeline(instance, sequence);
                // A waited call's own wait is its child's work.
                if (timeline == TimedTimeline && !WaitsOnCall(sequence)) return TimedWorkRunning;
                if (left == NoTimedWork && timeline == UntimedTimeline && instance.Timeline == TimedTimeline && AuthoredEventAhead(instance, sequence)) left = TimedEventAhead;
            }
        }
        return left;
    }
    private static bool WaitsOnCall(Sequence s) => s.State == 1 && s.Work is { Type: 24 } call && (call.I16(46) & 16) != 0;
    /// <summary>
    /// A sequence whose first event waits for a sequence-clock or after-previous threshold: those clocks run only once the
    /// sequence's first event has started, so it never starts.
    /// </summary>
    private static bool NeverStarts(Sequence s) => s.State == 0 && s.Cursor == 0 && s.Data.Events.Count > 0 && s.Data.Events[0] is { StartMode: 2 or 3 } first
        && (first.StartMode == 2 ? s.Elapsed : s.EventElapsed) < first.Threshold;

    /// <summary>What still runs or waits without a timed end, in words (the most telling one), or null.</summary>
    private string? UntimedReason(Instance[] live)
    {
        int falling = 0; bool motion = false; string? gate = null, events = null;
        foreach (var instance in live)
        {
            if (instance.Paused != null)
            {
                if (instance.Timeline == UntimedTimeline)
                    gate ??= instance.Entry.Name + (instance.Paused == "zone" ? " waits for the view's zones (zone gate)" : " waits for the player (range gate)");
                continue;
            }
            if (instance.StopDelay >= 0) { if (instance.Timeline == UntimedTimeline) events ??= "its stop follows motion without a timed end"; continue; }
            foreach (var sequence in instance.Sequences)
            {
                if (sequence.State is not (0 or 1) || sequence.Retired || Timeline(instance, sequence) != UntimedTimeline || WaitsOnCall(sequence) || NeverStarts(sequence)) continue;
                if (sequence.State == 1 && sequence.Work is { Type: 10 } work)
                {
                    if (Falls(work.U32(12))) falling++; else motion = true;
                }
                else events ??= (sequence.UntimedBy ?? instance.UntimedBy) == "contact" ? "events follow a ground contact" : "events follow motion without a timed end";
            }
        }
        if (falling > 0) return falling == 1 ? "a piece falls until it hits the ground" : "pieces fall until they hit the ground";
        if (motion) return "motion has no timed end";
        if (gate != null) return gate;
        // A sequence still waiting for the release (CALL_SEQUENCE or a contact) the run never gave it, in a binding that still runs
        // on authored time: a finished, stopping or paused binding can never release it, and one off authored time waits on what
        // took it there (the events that follow it say so). One that ran and went back to waiting has done what the run asked.
        foreach (var instance in live)
            if (instance.StopDelay < 0 && instance.Paused == null)
                foreach (var sequence in instance.Sequences)
                    if (sequence.State == 3 && sequence.ReleasedBy == null && sequence.Data.Events.Count > 0 && !sequence.Retired && Timeline(instance, sequence) == TimedTimeline)
                        return sequence.Data.Name + " waits for a release";
        return events;
    }
    /// <summary>Procedural motion that falls under gravity onto the preview's ground (a plane or the map's surfaces) until it lands.</summary>
    private bool Falls(uint flags) => (flags & 1) != 0 && (flags & 12) != 0 && (GroundPlaneEnabled || Terrain != null);

    // ---------------------------------------------------------------- what the run records while measuring

    private static byte Timeline(Instance instance, Sequence sequence) => Math.Max(instance.Timeline, sequence.Timeline);
    /// <summary>The timeline of the event being dispatched (authored time outside a dispatch: a scheduled start).</summary>
    private byte DispatchTimeline => dispatching != null ? Timeline(dispatchingInstance!, dispatching) : TimedTimeline;
    private string? DispatchUntimedBy => dispatching == null ? null : dispatching.Timeline >= dispatchingInstance!.Timeline ? dispatching.UntimedBy : dispatchingInstance.UntimedBy;
    /// <summary>The sequence a sound or effect's tail counts toward: the dispatching one unless it repeats a loop.</summary>
    private Sequence? TailSource => measuringDuration && dispatching != null && DispatchTimeline != RepeatTimeline ? dispatching : null;

    /// <summary>A point on the shared clock: timed points set the 5 s rule's start; every point but a repeat's ends a counted run.</summary>
    private void MeasurePoint(byte timeline, double at)
    {
        if (timeline == RepeatTimeline) return;
        countedEnd = Math.Max(countedEnd, at);
        if (timeline == TimedTimeline) { lastTimed = Math.Max(lastTimed, at); timedPoints++; }
    }
    private void MeasureDispatch(Instance instance, Sequence sequence, AnimationEvent ev, double at)
    {
        // An event that waited for its animation- or sequence-clock threshold starts when that clock says: on a clock that started
        // on authored time its pass is back on authored time (a landing or a contact earlier in the pass ended only its motion).
        if (sequence.ClockWait && sequence.Timeline == UntimedTimeline && instance.Timeline == TimedTimeline && AuthoredClock(instance, sequence, ev.StartMode))
        { sequence.Timeline = TimedTimeline; sequence.UntimedBy = null; }
        sequence.ClockWait = false; sequence.EventStart = at;
        if (sequence.Cursor == 0) { sequence.PassStart = at; sequence.ClockTimed = Timeline(instance, sequence) == TimedTimeline; }
        MeasurePoint(Timeline(instance, sequence), at);
    }
    /// <summary>
    /// Whether a START_TIME clock runs on authored time: the animation clock (mode 1) when its binding started there and no gate
    /// froze it; the sequence clock (mode 2) when, in addition, the pass under way began there.
    /// </summary>
    private static bool AuthoredClock(Instance instance, Sequence sequence, int mode) => !instance.UntimedClock
        && (mode == 1 || mode == 2 && sequence.ClockTimed && (sequence.Cursor > 0 || sequence.State == 1));
    /// <summary>
    /// Whether a pass that left authored time can still reach an event its authored clock starts: one later in the pass (up to its
    /// LOOP) whose animation- or sequence-clock threshold is still ahead on a clock that started on authored time. A look-ahead
    /// that ignores branches: only the event's dispatch counts (<see cref="MeasureCore"/>).
    /// </summary>
    private static bool AuthoredEventAhead(Instance instance, Sequence sequence)
    {
        if (instance.UntimedClock) return false;
        var events = sequence.Data.Events;
        for (int i = sequence.State == 1 ? sequence.Cursor + 1 : sequence.Cursor; i < events.Count; i++)
        {
            var ev = events[i];
            if (ev.Type == 30) return false;
            if (ev.StartMode == 1 ? instance.Elapsed < ev.Threshold : ev.StartMode == 2 && AuthoredClock(instance, sequence, 2) && sequence.Elapsed < ev.Threshold) return true;
        }
        return false;
    }
    /// <summary>An event's step: motion without a timed end, a waited call's resumption, a first loop iteration and a completion.</summary>
    private void MeasureResult(Instance instance, Sequence sequence, AnimationEvent work, bool starting, int result, double at)
    {
        switch (work.Type)
        {
            case 10:
                {
                    uint flags = work.U32(12);
                    bool timer = (flags & 0x400) != 0;
                    // A constant spin goes round forever, as a loop does: its first revolution is its first iteration, and the pass
                    // (which nothing but a stop or reset ends) repeats it.
                    if (result == 1 && starting && Timeline(instance, sequence) != RepeatTimeline && SpinRevolution(work) is double revolution)
                    {
                        sequence.LoopsForever = sequence.Spins = true; sequence.LoopStart = sequence.EventStart;
                        sequence.LoopEnd = sequence.EventStart + revolution; sequence.Timeline = RepeatTimeline;
                    }
                    // Still moving after its dispatch tick without a timer; or ended by a contact or the Y limit before its timer.
                    else if (result == 1 && !timer || result == 2 && timer && !starting && !(sequence.EventElapsed > work.F32(work.Version == 39 ? 328 : 248)))
                        MarkUntimed(instance, sequence, Falls(flags) ? "falls" : "motion");
                    break;
                }
            case 24 when result == 2 && (work.I16(46) & 16) != 0 && sequence.Child >= 0 && endedTimelines.TryGetValue(sequence.Child, out byte child) && child != TimedTimeline:
                MarkUntimed(instance, sequence, "call");
                break;
            case 30 when result == 0 && sequence.ObservedInfiniteLoop && sequence.State is 0 or 1 && !sequence.LoopsForever && Timeline(instance, sequence) != RepeatTimeline:
                // Its first iteration ends here; what its later passes do repeats it.
                sequence.LoopsForever = true; sequence.LoopEnd = at; sequence.LoopStart = sequence.PassStart >= 0 ? sequence.PassStart : at;
                sequence.Timeline = RepeatTimeline; looped.Add(sequence);
                break;
        }
        if (result != 2) return;
        byte timeline = Timeline(instance, sequence);
        MeasurePoint(timeline, at);
        // The binding's natural end follows its latest completion (the latest of a tick's).
        if (instance.EndTick != ticks) { instance.EndTick = ticks; instance.EndTimeline = timeline; }
        else instance.EndTimeline = Math.Max(instance.EndTimeline, timeline);
    }
    /// <summary>
    /// A call into a binding already on its caller's chain (a back-link the scenario follows) closes a call cycle: the cycle's
    /// first round ends at that call, and the called instance, with what it does, repeats it.
    /// </summary>
    private void MeasureCall(Instance parent, Sequence sequence, Instance child, double at)
    {
        if (Timeline(parent, sequence) == RepeatTimeline || !OnChain(parent, child.Entry.Index)) return;
        child.Timeline = RepeatTimeline; child.UntimedBy = null;
        cycles.Add((child.Entry.Name, at));
    }
    /// <summary>Whether <paramref name="entry"/> is <paramref name="instance"/>'s binding or one of its callers' (its call path, then its live parents).</summary>
    private bool OnChain(Instance instance, int entry)
    {
        if (instance.Entry.Index == entry) return true;
        string number = entry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (string segment in instance.CallPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith('@')) continue;
            int first = segment.IndexOf(':'), last = segment.LastIndexOf(':');
            if (first > 0 && (segment.AsSpan(0, first).SequenceEqual(number) || segment.AsSpan(last + 1).SequenceEqual(number))) return true;
        }
        for (long? up = instance.ParentInstance; up is long id && instances.FirstOrDefault(i => i.Id == id) is { } parent; up = parent.ParentInstance)
            if (parent.Entry.Index == entry) return true;
        return false;
    }
    /// <summary>The pass (or, without one, the binding) leaves authored time.</summary>
    private static void MarkUntimed(Instance instance, Sequence? sequence, string why)
    {
        if (sequence == null) { if (instance.Timeline == TimedTimeline) { instance.Timeline = UntimedTimeline; instance.UntimedBy = why; } return; }
        if (sequence.Timeline == TimedTimeline) { sequence.Timeline = UntimedTimeline; sequence.UntimedBy = why; }
    }
    /// <summary>A binding started, stopped or reset by an event (or its launcher's end) runs on that timeline from now on.</summary>
    private void Restart(Instance instance, byte timeline)
    {
        instance.Timeline = timeline; instance.UntimedBy = timeline == UntimedTimeline ? DispatchUntimedBy ?? "events" : null;
        instance.EndTimeline = TimedTimeline; instance.EndTick = -1;
    }
    /// <summary>A natural end on an untimed completion leaves authored time with what follows it (a stop delay, a cleanup).</summary>
    private static void InheritEnd(Instance instance)
    {
        if (instance.EndTimeline <= instance.Timeline) return;
        instance.Timeline = instance.EndTimeline; instance.UntimedBy ??= "events";
    }
    /// <summary>A released pass runs on its releaser's timeline; a ground contact's release follows the contact.</summary>
    private void Released(Instance releaser, Sequence target, bool contact)
    {
        byte from = dispatching != null ? DispatchTimeline : releaser.Timeline;
        if (contact && from == TimedTimeline) { target.Timeline = UntimedTimeline; target.UntimedBy = "contact"; }
        else { target.Timeline = from; target.UntimedBy = from == UntimedTimeline ? DispatchUntimedBy ?? releaser.UntimedBy : null; }
        target.PassStart = -1;
    }
    /// <summary>
    /// The tails of the sounds and effects still playing after a tick (a sound's full length, an effect's life), on the run and on
    /// the sequence whose event started them.
    /// </summary>
    private void RecordTails()
    {
        foreach (var (key, cue) in activeSounds)
            if (!cue.Persistent) { soundSources.TryGetValue(key, out var source); RecordTail(source, cue.StartedAt + context.Sounds[cue.Name].Duration, true); }
        soundSources.Clear();
        foreach (var effect in effects) RecordTail(effect.Source, Time + Math.Max(0, 1 - effect.Age), false);
    }
    private void RecordTail(Sequence? source, double end, bool sound)
    {
        tailsEnd = Math.Max(tailsEnd, end);
        if (source == null) return;
        tailed.Add(source);
        if (end > source.Tail) { source.Tail = end; source.SoundTail = sound; }
    }
    private static AnimationDuration Result(double seconds, AnimationDurationKind kind, string explanation) => new(Ticks(seconds), kind, explanation);
}
