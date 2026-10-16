using System.Numerics;

namespace zStudio.Core.Animation;

/// <summary>One existing call/stop pair. Times are local seconds in the repeating flight cycle.</summary>
public sealed record VtolDropWindow(Guid Id, int EntryIndex, Guid SequenceId, Guid CallEventId, Guid StopEventId,
    float Start, float Duration, float MinimumStart, float MaximumStart, bool Conditional)
{
    public double EffectiveStart { get; init; } = Start;
    public double EffectiveEnd { get; init; } = Start + (double)Duration;
    public string? MoveRefusal { get; init; }
}

/// <summary>A structurally recognized flight program; names are labels, never identities.</summary>
public sealed record VtolDropCandidate(int EntryIndex, string Name, string RootName, Guid MotionSequenceId,
    Guid ScheduleSequenceId, int CallbackEntryIndex, IReadOnlyList<VtolDropWindow> Windows, string Fingerprint);

public sealed record VtolDropDiscovery(IReadOnlyList<VtolDropCandidate> Candidates, IReadOnlyList<string> Diagnostics);
public sealed record VtolPathSample(double Time, Vector3 Position);
public sealed record VtolDropRelease(Guid WindowId, double Time, Vector3 Position, bool Conditional);

/// <summary>Frozen analysis. A segment is Samples[index] through Samples[index + 1], including its distinct phase.</summary>
public sealed record VtolDropSnapshot(VtolDropCandidate Candidate, double CycleSeconds, int RootNode,
    IReadOnlyList<VtolPathSample> Samples, IReadOnlyList<VtolDropWindow> Windows, IReadOnlyList<VtolDropRelease> Releases);

/// <summary>Validated threshold-only edit; acceptance rechecks the original package fingerprint.</summary>
public sealed record VtolDropMovePlan(VtolDropCandidate Candidate, VtolDropWindow Window, float NewStart)
{
    internal float? RuntimeVerifiedStart { get; set; }
}
