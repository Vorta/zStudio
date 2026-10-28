namespace zStudio.Core.Animation;

/// <summary>What a Structure node is: a binding's activation, a definition frame, one of its sequences, or a reference node.</summary>
public enum AnimationGraphNodeKind { Activation, Frame, Sequence, Reference }

/// <summary>How an edge relates its ends (design §8, "Edges").</summary>
public enum AnimationGraphRelation
{
    Call, CallWait, LocalChild, Release, ContactRelease, StopSequence, StopAnimation, ResetAnimation, InvalidateAnimation, Prerequisite,
}

/// <summary>What a graph row stands for: an ordinary event, an IF/ELSEIF question, an ELSE branch label, the ENDIF or a LOOP.</summary>
public enum AnimationGraphRowRole { Event, Question, Else, EndIf, Loop }

/// <summary>
/// One laid-out node. <see cref="Column"/> and <see cref="Order"/> are the deterministic layout both the GUI and MCP use:
/// column 0 the opened binding's activation (then the bindings that call it without being called back), column 1 its
/// definition frame, then for each call depth d a column of reference nodes (2d) and the frames of expanded ones (2d + 1).
/// A sequence's <see cref="Order"/> is its place in its frame (cleanup first, then source order).
/// </summary>
public sealed record AnimationGraphNode(string Id, AnimationGraphNodeKind Kind, int Column, int Order, int Depth, string Label)
{
    /// <summary>The frame holding a sequence or an expanded frame's activation; the reference node an expanded frame belongs to.</summary>
    public string? Parent { get; init; }
    public int? Entry { get; init; }
    public AnimationGraphActivation? Activation { get; init; }
    public AnimationGraphFrame? Frame { get; init; }
    public AnimationGraphSequence? Sequence { get; init; }
    public AnimationGraphReference? Reference { get; init; }
}

/// <summary>
/// A binding shown with its sequences. <see cref="Path"/> is the call path of the instance it stands for: "" for the opened
/// binding (the primary start), the first call path that reaches it otherwise, null when only a by-name event reaches it.
/// </summary>
public sealed record AnimationGraphFrame(int Entry, string Name, string Root, string Attach, string? Path, int Depth, int SequenceCount)
{
    public string? File { get; init; }
    public AnimationEntrySource? Source { get; init; }
    /// <summary>The reference group an expanded frame stands for: it shows one member at a time (<see cref="Members"/>[<see cref="MemberIndex"/>]).</summary>
    public string? Group { get; init; }
    /// <summary>The group's bindings in entry order (at most 100), any of which the frame can show.</summary>
    public IReadOnlyList<int> Members { get; init; } = [];
    public int MemberIndex { get; init; }
}

/// <summary>A sequence node. Rows come from <see cref="AnimationGraph.Rows"/>; <see cref="RowCount"/> counts its grouped rows.</summary>
public sealed record AnimationGraphSequence(int Entry, Guid Id, int Index, string Name, bool Cleanup, bool WaitsForRelease, int EventCount, int RowCount)
{
    /// <summary>The state line, only when it says something: "Waits for release", "Cleanup · runs on stop or reset", "START_TIME ignored".</summary>
    public string? StateLine { get; init; }
    public bool IgnoredStartTime { get; init; }
    /// <summary>Collapsed to its header unless asked: a definition with more than 12 sequences shows the first 6 open.</summary>
    public bool CollapsedByDefault { get; init; }
    public IReadOnlyList<AnimationGraphFamilyCount> Summary { get; init; } = [];
    public AnimationSequenceSource? Source { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public sealed record AnimationGraphFamilyCount(AnimationEventFamily Family, int Count);

/// <summary>
/// One row of a sequence node. Consecutive events of one kind whose operands differ only by a name form one row
/// (<see cref="Members"/>, at most 100); <see cref="Event"/> is the first. Rows in a condition block name it and their branch.
/// </summary>
public sealed record AnimationGraphRow(string Id, int Entry, Guid Sequence, Guid Event, int Index, byte Type, string Keyword,
    AnimationEventFamily Family, AnimationGraphRowRole Role, string Label)
{
    public byte Clock { get; init; } = 1;
    public float Threshold { get; init; }
    /// <summary>"anim ≥ 23 s", "seq ≥ 1.5 s", "prev + 0.1 s"; none for the default animation clock 0.</summary>
    public string? ThresholdBadge { get; init; }
    /// <summary>The threshold in words, or what the game does with it ("ignored after a false IF", "waits, then skips").</summary>
    public string? ThresholdNote { get; init; }
    public string? Block { get; init; }
    public int? Branch { get; init; }
    public IReadOnlyList<Guid> Members { get; init; } = [];
    /// <summary>The name a call, release, stop or by-name event targets.</summary>
    public string? Target { get; init; }
    /// <summary>The binding the game picks for a call or a by-name event (the first entry of the name); null when unresolved.</summary>
    public int? TargetEntry { get; init; }
    public AnimationCallbackMeaning? Callback { get; init; }
    public AnimationEventSource? Source { get; init; }
    public string? Diagnostic { get; init; }
}

/// <summary>A condition block (IF … ENDIF) or a loop bracket (event 0 to its LOOP) in one sequence, by event index.</summary>
public sealed record AnimationGraphBlock(string Id, string Kind, int Entry, Guid Sequence, int First, int Last)
{
    public IReadOnlyList<AnimationGraphBranch> Branches { get; init; } = [];
    public Guid? EndIf { get; init; }
    public AnimationGraphLoop? Loop { get; init; }
    public string? Diagnostic { get; init; }
}

/// <summary>
/// One branch: "if true", "else if" or "if false", with "(nothing)" when it is empty (the shipped "if not" idiom). A block
/// without ELSE still has its implicit, empty "if false". <see cref="First"/>..<see cref="Last"/> are its events after the marker.
/// </summary>
public sealed record AnimationGraphBranch(int Index, string Kind, string Label, Guid? Marker, int First, int Last, bool Empty, bool Implicit)
{
    public AnimationGraphCondition? Condition { get; init; }
}

/// <summary>
/// The question an IF or ELSEIF asks, in human terms. <see cref="Operand"/> is "playerRange", "random", "effectsDetail",
/// "cover", "playerCover" or "none"; the values are the radius, the chance, the effects detail level, the cover height and node.
/// </summary>
public sealed record AnimationGraphCondition(Guid Event, string Operand, string Question, string Rule)
{
    public bool ElseIf { get; init; }
    public double? Radius { get; init; }
    public double? Probability { get; init; }
    public string? EffectsDetail { get; init; }
    public double? CoverHeight { get; init; }
    public string? Node { get; init; }
    /// <summary>An ELSEIF's own START_TIME, which the game ignores when a false IF reaches it.</summary>
    public bool ThresholdIgnored { get; init; }
}

/// <summary>A LOOP: "count" (×n), "forever" or "time" (for t s). A sequence that waits for release waits again on every pass.</summary>
public sealed record AnimationGraphLoop(Guid Event, string Mode, int? Count, double? Seconds, string Label, bool WaitsForRelease);

/// <summary>
/// One drawn edge. <see cref="From"/> is a row id (frame rows) or a node id (reference nodes, prerequisites); <see cref="To"/>
/// a node id: a reference node, a sequence (releases, stops), or an activation (back-links, prerequisites). Parallel edges
/// bundle: <see cref="Count"/> events, at most 100 listed with their call paths.
/// </summary>
public sealed record AnimationGraphEdge(string Id, AnimationGraphRelation Relation, string From, string FromNode, string To, int Count, string? Label)
{
    /// <summary>A call to a binding already on the path (a cycle): routed to the target's activation, "calls back (cycle)".</summary>
    public bool BackLink { get; init; }
    public bool Wait { get; init; }
    public bool Local { get; init; }
    public IReadOnlyList<Guid> Events { get; init; } = [];
    /// <summary>The canonical call paths of the bundled calls, where the calling instance's path is known.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];
    public IReadOnlyList<int> TargetEntries { get; init; } = [];
    /// <summary>Other bindings with the by-name target's name, which the event does not affect.</summary>
    public IReadOnlyList<int> AlsoNamed { get; init; } = [];
    /// <summary>Why the edge has no effect, when it has none ("no effect during cleanup").</summary>
    public string? Note { get; init; }
}

/// <summary>How a binding starts, its gates, reset behaviour, prerequisites and callers (design §8, "Activation node").</summary>
public sealed record AnimationGraphActivation(int Entry, string AttachNode, IReadOnlyList<AnimationGraphTrigger> Triggers,
    IReadOnlyList<AnimationGraphPrerequisiteGroup> Prerequisites)
{
    public bool NewGameStart { get; init; }
    public IReadOnlyList<AnimationGraphCallerGroup> Callers { get; init; } = [];
    /// <summary>Every call or by-name event of the map that reaches this binding.</summary>
    public int CallerCount { get; init; }
}

/// <summary>
/// One trigger, gate or reset row. <see cref="Kind"/>: whenCalled, onStartup, newGameStart, weaponHit, collisionHit,
/// weaponOrCollisionHit, rangeGate, zoneGate, loadCleanup, poseKept, cleansUp, oneShot, priority, attach.
/// </summary>
public sealed record AnimationGraphTrigger(string Kind, string Phrase, string Keyword)
{
    public double? Value { get; init; }
}

/// <summary>A required group, or an "at least N of" group, of the binding's activation prerequisites (checked on every start).</summary>
public sealed record AnimationGraphPrerequisiteGroup(bool Required, int Minimum, string Header, IReadOnlyList<AnimationGraphPrerequisite> Items);

/// <summary>An animation item (satisfied unless that binding, the first of its name, is stopped) or an object item (a node path expected active).</summary>
public sealed record AnimationGraphPrerequisite(int Record, string Kind, string Target, int? Entry);

/// <summary>Bindings of one definition that call or name this one; <see cref="Node"/> when they are on screen, with their back-link edge.</summary>
public sealed record AnimationGraphCallerGroup(string Group, string Name, int Bindings, int Events, string? Node, string? BackLink)
{
    /// <summary>How they reach it: calls, or stops, resets and invalidates by name.</summary>
    public IReadOnlyList<AnimationGraphRelation> Relations { get; init; } = [];
}

/// <summary>
/// The bindings of one definition reached from the shown frames (or calling the opened binding), as one node:
/// "rdrail_destroy0* · 8 bindings". Collapsed by default; expanded, one frame to the right shows one member at a time
/// (<see cref="Member"/>, the first unless another is chosen), so a group counts once against the expansion budget.
/// </summary>
public sealed record AnimationGraphReference(string Group, string Name, IReadOnlyList<int> Entries, int EntryCount)
{
    public bool Calling { get; init; }
    public bool Expanded { get; init; }
    /// <summary>The member the expanded frame shows; null when the node has no frame.</summary>
    public int? Member { get; init; }
    public bool Expandable { get; init; }
    public bool Unresolved { get; init; }
    public string? File { get; init; }
    /// <summary>"raildam.zad · rdrail01 … rdrail08 · #233–#240".</summary>
    public string Detail { get; init; } = "";
    /// <summary>"Weapon hit · health 1 · 1 sequence · 5 events".</summary>
    public string Summary { get; init; } = "";
    /// <summary>Its own outgoing relations, one line: each target name, how many events and whether it calls back.</summary>
    public IReadOnlyList<AnimationGraphOutgoing> Calls { get; init; } = [];
    /// <summary>Bindings whose prerequisites name one of these bindings ("needed by stop_bridge_seg*").</summary>
    public IReadOnlyList<string> NeededBy { get; init; } = [];
    /// <summary>"2 definitions are named generic_6; calls use the first (raildam.zad #149)", or why it is unavailable.</summary>
    public string? Caution { get; init; }
}

public sealed record AnimationGraphOutgoing(string Name, int Events, bool BackLink, string? Node);

/// <summary>One event of the map that reaches a binding: a call, or a stop, reset or invalidate by name.</summary>
public sealed record AnimationGraphCaller(int Caller, string Name, Guid Sequence, Guid Event, AnimationGraphRelation Relation, int Target);

/// <summary>Inputs a graph does not find in the package: the definitions as written and the mission's NEW_GAME_START list.</summary>
public sealed record AnimationGraphOptions
{
    public static AnimationGraphOptions Default { get; } = new();
    /// <summary>Each definition's name as written, with its wildcard ("rdrail_destroy0*"), by definition index.</summary>
    public IReadOnlyDictionary<int, string>? DefinitionNames { get; init; }
    /// <summary>The names startanims.zrd lists under NEW_GAME_START (the first entry of each name starts).</summary>
    public IReadOnlyList<string> NewGameStart { get; init; } = [];
    /// <summary>The map's name in messages ("No animation named generic_7 in m4").</summary>
    public string? Map { get; init; }

    /// <summary>The options of a source map: its definitions' names and its mission's NEW_GAME_START list.</summary>
    public static AnimationGraphOptions For(Sources.SourceAnimationDefinitions definitions, IReadOnlyList<string>? newGameStart = null, string? map = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return new()
        {
            DefinitionNames = definitions.Definitions.ToDictionary(d => d.Index, d => d.Name),
            NewGameStart = newGameStart ?? [], Map = map,
        };
    }
}
