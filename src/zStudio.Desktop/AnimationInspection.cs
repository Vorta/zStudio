using System.Globalization;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>
/// What Animation mode's Inspector shows for its selection (design/animation-redesign.md §11): the card's header, its notices
/// and its sections, built by the window from the graph, the run at the playhead, the scenario and the source. The card draws
/// it and MCP animation_graph inspector returns it (<see cref="Json"/>), so the GUI and MCP read one card. Field items name
/// source fields (source_animation set changes them); switches, includes and links name the scenario and graph operations
/// their controls use.
/// </summary>
internal sealed record AnimationInspection(string Kind, string Title, string Subtitle)
{
    /// <summary>The header tile's glyph: a PreviewIcon kind ("Definition", "Cube") or a graph glyph ("graph:condition").</summary>
    public string Glyph { get; init; } = "Definition";
    /// <summary>The graph node or row shown (null for the definition with nothing selected).</summary>
    public string? Selection { get; init; }
    public int Definition { get; init; } = -1;
    public int Entry { get; init; } = -1;
    /// <summary>The source record the card edits (an event or sequence), or "settings" for the definition.</summary>
    public string? Record { get; init; }
    /// <summary>The call path of the frame the selection belongs to ("" for the opened binding's primary start).</summary>
    public string? Path { get; init; }
    /// <summary>A section the card scrolls to when shown (the Activation node shows the Definition card at Activation).</summary>
    public string? ScrollTo { get; init; }
    public IReadOnlyList<AnimationNotice> Notices { get; init; } = [];
    public IReadOnlyList<InspectorSection> Sections { get; init; } = [];
    /// <summary>Identity of what is shown: a change rebuilds the card; the same identity refreshes only its live sections.</summary>
    public string Key => $"{Kind}|{Selection}|{Definition}|{Entry}|{Record}|{Path}";

    internal object Json() => new
    {
        kind = Kind, title = Title, subtitle = Subtitle, selection = Selection, definition = Definition < 0 ? (int?)null : Definition,
        entry = Entry < 0 ? (int?)null : Entry, record = Record, path = Path, scrollTo = ScrollTo,
        notices = Notices.Select(n => n.Json()).ToArray(),
        sections = Sections.Select(s => new { id = s.Id, title = s.Title, summary = s.Summary, expanded = s.Expanded, live = s.Live, items = s.Items.Select(Item).ToArray() }).ToArray(),
    };
    private static object Item(InspectorItem item) => item switch
    {
        InspectorText text => new { type = "text", text = text.Text, style = text.Style },
        InspectorPair pair => new { type = "pair", label = pair.Label, value = pair.Value, link = pair.Link?.Json() },
        InspectorValue value => new { type = "value", label = value.Label, value = value.Value, suffix = value.Suffix, mono = value.Mono ? true : (bool?)null, help = value.Help },
        InspectorLink link => new { type = "link", link = link.Json() },
        InspectorActions actions => new { type = "actions", actions = actions.Links.Select(l => l.Json()).ToArray() },
        InspectorField field => new { type = "field", field = field.FieldId, label = field.Label, help = field.Help, layout = field.Layout,
            marks = field.Marks?.Select(m => new { line = m.Line, mark = m.Mark, status = m.Status }).ToArray() },
        InspectorThreshold threshold => new { type = "threshold", field = threshold.FieldId, present = threshold.Present, clock = Clock(threshold.Clock), seconds = threshold.Seconds,
            editable = threshold.ReadOnly == null, readOnly = threshold.ReadOnly, sentence = threshold.Sentence, observed = threshold.Observed, note = threshold.Note },
        InspectorSwitch choice => new { type = "switch", label = choice.Label, instancePath = choice.Path, entry = choice.Entry, @event = choice.Event,
            value = choice.Choice switch { AnimationConditionChoice.True => "true", AnimationConditionChoice.False => "false", _ => "game" }, scope = choice.Scope, outcome = choice.Outcome },
        InspectorInclude include => new { type = "include", label = include.Label, paths = include.Paths.Take(16).ToArray(), pathCount = include.Paths.Count,
            included = include.Included, below = include.Below, help = include.Help, enabled = include.Enabled },
        InspectorTable table => new { type = "table", columns = table.Columns, rows = table.Rows.Select(r => new { cells = r.Cells, seek = r.Seek, selected = r.Selected ? true : (bool?)null }).ToArray(),
            more = table.More, empty = table.Empty },
        InspectorBindings bindings => new { type = "bindings", bindings = bindings.Rows.Select(b => new { entry = b.Entry, label = b.Label, root = b.Root, plays = b.Plays, frame = b.Frame }).ToArray(),
            more = bindings.More, choose = bindings.Choose },
        InspectorSource source => new { type = "source", file = source.File, line = source.Line, excerpt = source.Excerpt, truncated = source.Truncated, note = source.Note,
            missions = source.Missions, record = source.Record },
        InspectorHost host => new { type = "host", key = host.Key, note = host.Note },
        InspectorNoticeGroup group => new { type = "noticeGroup", label = group.Label, collapsed = true, notices = group.Lines },
        _ => new { type = "unknown" },
    };
    internal static string Clock(string mode) => mode switch { "SEQUENCE_OFFSET" => "sequence", "EVENT_OFFSET" => "previous", _ => "animation" };
}

/// <summary>One collapsible section of the card: its title, its trailing summary and its items. A live section follows the run.</summary>
internal sealed record InspectorSection(string Id, string Title)
{
    public string? Summary { get; init; }
    public bool Expanded { get; init; } = true;
    /// <summary>Rebuilt with the run at the playhead and the scenario (outcomes, occurrences, the run, inclusion, notices).</summary>
    public bool Live { get; init; }
    public IReadOnlyList<InspectorItem> Items { get; init; } = [];
}

internal abstract record InspectorItem;

/// <summary>A line of text: "help" (tertiary), "body", "mono" (source keyword), "caution" or "info" (a notice box).</summary>
internal sealed record InspectorText(string Text, string Style = "help") : InspectorItem;

/// <summary>
/// A stored value shown read only (a compiled animation's cards): its caption above a selectable read-only field, with a unit
/// (<see cref="Suffix"/>), in monospace for keywords and bytes (<see cref="Mono"/>; several lines for a hex dump).
/// </summary>
internal sealed record InspectorValue(string Label, string Value) : InspectorItem
{
    public string? Suffix { get; init; }
    public bool Mono { get; init; }
    public string? Help { get; init; }
}

/// <summary>A label left of its value ("Released by" → a link to the releasing row).</summary>
internal sealed record InspectorPair(string Label, string Value) : InspectorItem
{
    public InspectorLink? Link { get; init; }
}

/// <summary>
/// A link or button: <see cref="Action"/> select (a graph node or row), object (a scene node), open (Animation mode on a
/// binding), expand (a reference node), seek (seconds), tab (scenario), frame (a binding's root), diagnostics.
/// </summary>
internal sealed record InspectorLink(string Text, string Action, string Target) : InspectorItem
{
    public string? Detail { get; init; }
    public string? Tip { get; init; }
    public bool Primary { get; init; }
    internal object Json() => new { text = Text, action = Action, target = Target, detail = Detail, tip = Tip };
}

/// <summary>Buttons in a row (a reference node's Open and Expand).</summary>
internal sealed record InspectorActions(IReadOnlyList<InspectorLink> Links) : InspectorItem;

/// <summary>
/// A source field of the card's definition, edited through its draft: <see cref="Layout"/> "auto" (a name box, a combo for
/// choices, X/Y/Z boxes for a vector), "check" (an optional field as one check box: Waits for release), "list" (one name or
/// path per line, with <see cref="Marks"/> beside the lines: ✓/○ satisfaction at the playhead).
/// </summary>
internal sealed record InspectorField(string FieldId) : InspectorItem
{
    public string? Label { get; init; }
    public string? Help { get; init; }
    public string Layout { get; init; } = "auto";
    public IReadOnlyList<InspectorMark>? Marks { get; init; }
}
internal sealed record InspectorMark(string Line, string Mark, string Status);

/// <summary>
/// The Start threshold editor (§11): its clock, value, the "May start …" sentence and, only with evidence, what was observed.
/// <see cref="Present"/> false shows "None (animation clock 0)" with Add threshold.
/// </summary>
internal sealed record InspectorThreshold(string? FieldId, bool Present, string Clock, double Seconds) : InspectorItem
{
    public string? ReadOnly { get; init; }
    public string Sentence { get; init; } = "";
    public string? Observed { get; init; }
    /// <summary>What the game does with this threshold when it is not a plain one ("ignored after a false IF").</summary>
    public string? Note { get; init; }
}

/// <summary>A condition's Game / True / False switch in one call path, with the outcome there.</summary>
internal sealed record InspectorSwitch(string Label, string Path, int Entry, Guid Event, AnimationConditionChoice Choice, string Scope) : InspectorItem
{
    public string? Outcome { get; init; }
}

/// <summary>"Included in preview" for call paths (a call's, or those into a reference node): the switch and what excluding takes with it.</summary>
internal sealed record InspectorInclude(string Label, IReadOnlyList<string> Paths, bool? Included, int Below, string Help) : InspectorItem
{
    public bool Enabled { get; init; } = true;
}

/// <summary>A small table (outcomes, occurrences): its rows (a row may seek), and how many more rows there are.</summary>
internal sealed record InspectorTable(IReadOnlyList<string> Columns, IReadOnlyList<InspectorTableRow> Rows) : InspectorItem
{
    public int More { get; init; }
    public string? Empty { get; init; }
}
internal sealed record InspectorTableRow(IReadOnlyList<string> Cells)
{
    public double? Seek { get; init; }
    public bool Selected { get; init; }
}

/// <summary>The definition's bindings: root object links, Frame, and which one Animation mode plays (choosing another opens it).</summary>
internal sealed record InspectorBindings(IReadOnlyList<InspectorBinding> Rows) : InspectorItem
{
    public int More { get; init; }
    /// <summary>Choosing a binding opens it in Animation mode (the opened definition), rather than only selecting its root.</summary>
    public bool Choose { get; init; }
}
internal sealed record InspectorBinding(int Entry, string Label, int? Root, bool Plays, bool Frame);

/// <summary>The source: file, line, a read-only excerpt (while it loads, only the file), and the maps it affects.</summary>
internal sealed record InspectorSource(string File, string? Record) : InspectorItem
{
    public int? Line { get; init; }
    public string? Excerpt { get; init; }
    public bool Truncated { get; init; }
    public string? Note { get; init; }
    public IReadOnlyList<string> Missions { get; init; } = [];
}

/// <summary>A panel the window hosts in the card as it is: a VTOL flight's Drop windows ("drops").</summary>
internal sealed record InspectorHost(string Key) : InspectorItem
{
    public string? Note { get; init; }
}

/// <summary>
/// A notice about one item of the opened animation (design §15): an approximation, a support note, ignored prerequisites, a
/// refused start, an unavailable call or a preview limit. <see cref="Node"/> and <see cref="Lane"/> are where its ⚠ badge is
/// counted (a graph node, a Timing lane).
/// </summary>
internal sealed record AnimationNotice(string Id, string Kind, string Severity, string Text)
{
    public string? Node { get; init; }
    public string? Row { get; init; }
    public string? Lane { get; init; }
    public int? Entry { get; init; }
    public Guid? Sequence { get; init; }
    public Guid? Event { get; init; }
    public double? Time { get; init; }
    public long Count { get; init; } = 1;
    public string Category { get; init; } = "Preview";
    /// <summary>A generic preview approximation (not about this item): grouped as one collapsed line on the definition's card, left out of badges and the chip's count.</summary>
    public bool Generic { get; init; }
    internal object Json() => new
    {
        id = Id, kind = Kind, severity = Severity, text = Text, category = Category, node = Node, row = Row, lane = Lane, entry = Entry, sequence = Sequence, @event = Event,
        time = Time, count = Count, generic = Generic ? true : (bool?)null,
    };
    internal string Where => Time is double t ? " · " + AnimationSeconds.Format(t) : "";
    internal static string Plural(long count, string one, string many) => count == 1 ? one : count.ToString("N0", CultureInfo.InvariantCulture) + " " + many;
}

/// <summary>The preview's generic approximations of the definition, as one collapsed line ("4 preview approximations ▸").</summary>
internal sealed record InspectorNoticeGroup(string Label, IReadOnlyList<string> Lines) : InspectorItem;
