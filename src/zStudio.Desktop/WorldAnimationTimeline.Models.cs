namespace zStudio.Desktop;

/// <summary>Presentation-only rows. The session owns identities, expansion, inclusion and source edits.</summary>
public sealed record WorldAnimationTimelineRow(string Id, string Label)
{
    public int Depth { get; init; }
    public bool IsGroup { get; init; }
    public bool Expandable { get; init; }
    public bool Expanded { get; init; }
    public bool? Included { get; init; }
    public bool InclusionEnabled { get; init; } = true;
    public string InclusionReason { get; init; } = "";
    public bool? GuidesShown { get; init; }
    public Guid? ActivationId { get; init; }
    public bool CanEditTiming { get; init; }
    public string TimingLabel { get; init; } = "";
    public string Detail { get; init; } = "";
    public IReadOnlyList<WorldAnimationTimelineMark> Marks { get; init; } = [];
    /// <summary>A certified VTOL adapter's native flight-clock strip, not a global run occurrence.</summary>
    public bool IsWindowClock { get; init; }
    public double WindowClockEnd { get; init; } = 30;
    public bool CanMoveWindow { get; init; }
    public string WindowMoveReason { get; init; } = "";
    public double? WindowStartThreshold { get; init; }
    /// <summary>Read-only native sample/flight-clock strip. Null uses the global run-time ruler.</summary>
    public double? NativeClockStart { get; init; }
    public double? NativeClockEnd { get; init; }
    public bool IsSourceClock { get; init; }
    public double? StartThreshold { get; init; }
}

public enum WorldAnimationTimelineMarkKind { Authored, Instant, Interval, Running, Call, Blocked, Loop, Keyframe, Truncation, Window }

public sealed record WorldAnimationTimelineMark(string Id, WorldAnimationTimelineMarkKind Kind, double Start,
    double? End = null, string Label = "", long? Occurrence = null, int? KeyIndex = null);

public sealed record WorldAnimationTimelineState
{
    public string EvidenceIdentity { get; init; } = "";
    public string Target { get; init; } = "Select an animation binding";
    public string Status { get; init; } = "Unprepared";
    public string SelectedTarget { get; init; } = "";
    public bool Playing { get; init; }
    public bool CanPlay { get; init; }
    public bool CanStop { get; init; }
    public bool CanExit { get; init; }
    public bool CanSeek { get; init; }
    public bool CanUseSelection { get; init; }
    public bool Busy { get; init; }
    public bool HasSession { get; init; }
    public double Time { get; init; }
    public double RangeStart { get; init; }
    public double RangeEnd { get; init; } = 30;
    public double Speed { get; init; } = 1;
    public bool Muted { get; init; }
    public double Volume { get; init; } = .5;
    public bool FollowCamera { get; init; }
    public bool Replay { get; init; }
    public string Coverage { get; init; } = "No execution evaluated";
}

public sealed record WorldAnimationTimelinePresentation(string View, double VisibleStart, double VisibleEnd,
    bool FollowPlayhead, string? SelectedRow, string? SelectedMark, double LabelWidth = 220,
    double? RequestedFrame = null, int FrameRate = 40, int FrameOrigin = 1);
