using System.Globalization;

namespace zStudio.Core.Animation;

/// <summary>How a dragged threshold, start or drop window snaps: the 0.025 s grid, Shift's 0.1 s or Ctrl's free 0.001 s.</summary>
public enum AnimationSnap { Grid, Coarse, Free }

/// <summary>
/// The animation UI's one time unit (docs/agents/preview.md, "Source-world animation session"): seconds with three decimals,
/// stepped by one 1/60 s preview tick, dragged on a 0.025 s grid (Shift 0.1 s, Ctrl 0.001 s), on the 0–3,600 s shared clock.
/// </summary>
public static class AnimationSeconds
{
    /// <summary>One preview tick, the step in the GUI and MCP alike.</summary>
    public const double Tick = AnimationPlayer.StepSeconds;
    public const double Maximum = 3600;
    public const double GridStep = 0.025, CoarseStep = 0.1, FreeStep = 0.001;

    /// <summary>"22.217 s": three decimals, invariant culture.</summary>
    public static string Format(double seconds) => Value(seconds) + " s";

    /// <summary>The number alone, three decimals ("22.217").</summary>
    public static string Value(double seconds) =>
        double.IsFinite(seconds) ? seconds.ToString("#,##0.000", CultureInfo.InvariantCulture) : "—";

    /// <summary>A duration or offset in a phrase, without trailing zeros ("35 s", "0.1 s").</summary>
    public static string Short(double seconds) =>
        double.IsFinite(seconds) ? seconds.ToString("#,##0.###", CultureInfo.InvariantCulture) + " s" : "—";

    /// <summary>The grid a drag snaps to.</summary>
    public static double Step(AnimationSnap snap) => snap switch { AnimationSnap.Coarse => CoarseStep, AnimationSnap.Free => FreeStep, _ => GridStep };

    /// <summary>
    /// A dragged time on its grid, within 0–3,600 s. The value is rounded in decimal, so a snapped threshold writes a plain
    /// value such as 0.125 rather than a binary neighbour.
    /// </summary>
    public static double Snap(double seconds, AnimationSnap snap = AnimationSnap.Grid)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds), "Use a finite time.");
        decimal step = (decimal)Step(snap);
        decimal clamped = (decimal)Math.Clamp(seconds, 0, Maximum);
        return (double)(Math.Round(clamped / step, MidpointRounding.AwayFromZero) * step);
    }

    /// <summary>A dragged start threshold on its grid: as <see cref="Snap"/>, but a threshold may be negative (−3,600 … 3,600 s).</summary>
    public static double SnapSigned(double seconds, AnimationSnap snap = AnimationSnap.Grid)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds), "Use a finite time.");
        decimal step = (decimal)Step(snap);
        decimal clamped = (decimal)Math.Clamp(seconds, -Maximum, Maximum);
        return (double)(Math.Round(clamped / step, MidpointRounding.AwayFromZero) * step);
    }

    /// <summary>The preview tick at or before <paramref name="seconds"/>: what a seek shows and its readout reports.</summary>
    public static long TickIndex(double seconds) =>
        double.IsFinite(seconds) ? (long)Math.Floor(Math.Clamp(seconds, 0, Maximum) / Tick + 1e-7) : 0;

    /// <summary>The time of the tick at or before <paramref name="seconds"/>.</summary>
    public static double AtTick(double seconds) => TickIndex(seconds) * Tick;

    /// <summary>
    /// The playhead that shows an occurrence starting at <paramref name="start"/>: the end of the tick it ran in (a trace start
    /// is the moment inside its tick, so the tick at or before it may not have run it yet).
    /// </summary>
    public static double OccurrenceTime(double start) => AtTick(Math.Min(Maximum, Math.Max(0, start) + Tick));

    /// <summary>One tick earlier (<paramref name="direction"/> −1) or later (+1) than the tick at or before <paramref name="seconds"/>, within the clock.</summary>
    public static double Stepped(double seconds, int direction) =>
        Math.Clamp((TickIndex(seconds) + Math.Sign(direction)) * Tick, 0, Maximum);
}
