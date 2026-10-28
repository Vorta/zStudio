using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>What the Structure view shows besides its layout: the scenario's switches and inclusions, the run at the playhead, the 3D selection's rows.</summary>
internal sealed record AnimationGraphState
{
    public static AnimationGraphState Empty { get; } = new();
    public AnimationGraphLayout? Layout { get; init; }
    public AnimationScenario Scenario { get; init; } = AnimationScenario.Default;
    public AnimationRunOverlay? Overlay { get; init; }
    /// <summary>The overlay is from before an edit or a stop: drawn at 50 % with "Re-run to here".</summary>
    public bool Stale { get; init; }
    public bool ReadOnly { get; init; }
    /// <summary>Rows naming the object selected in the 3D view (amber bar).</summary>
    public IReadOnlySet<string> Highlighted { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>Said instead of the graph ("Not bound on Hard …").</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Animation mode's Structure view (design/animation-redesign.md §8): the deterministic node graph of the opened binding
/// (<see cref="AnimationGraphLayout"/>) on a pannable, zoomable canvas (25–200 %; Ctrl+wheel at the pointer, wheel and
/// Shift+wheel scroll, middle-drag or a drag on empty space pans, Ctrl+0 fits), with a minimap, semantic zoom levels, the run
/// overlay at the playhead, condition switches, call ports as include toggles and keyboard navigation. Nodes are realised
/// only inside the viewport plus one screen. It shows a state and raises intents; the window owns the graph, the scenario
/// and the run (the same operations MCP animation_graph and animation_scenario use).
/// </summary>
internal sealed class AnimationGraphView : Grid
{
    internal const double MinimumZoom = .25, MaximumZoom = 2;
    private readonly Border viewport = new() { ClipToBounds = true, Background = Brushes.Transparent, Focusable = false };
    private readonly Canvas content = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly MatrixTransform transform = new();
    private readonly EdgeLayer edges;
    private readonly Minimap minimap;
    private readonly Dictionary<string, AnimationGraphNodeView> realised = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameView> frames = new(StringComparer.Ordinal);
    private readonly Border runTag = new() { CornerRadius = new(13), Padding = new(10, 0, 10, 0), Height = 26, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 8, 10, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock runText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Shapes.Ellipse runDot = new() { Width = 7, Height = 7, Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button rerun = new() { Content = "Re-run to here", Height = 22, Padding = new(6, 0, 6, 0), Margin = new(8, 0, -4, 0), FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 460, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, (double X, int Shown, double Pitch)> pips = new(StringComparer.Ordinal);
    private Point? panFrom; private Point panOrigin; private bool panned;
    private string? graphKey;
    private bool dark = true;
    private AnimationGraphRoute? hoveredEdge;

    internal AnimationGraphState State { get; private set; } = AnimationGraphState.Empty;
    internal AnimationGraphLayout? Layout => State.Layout;
    internal AnimationGraphPalette Palette => AnimationGraphPalette.For(dark);
    internal string? Selection { get; private set; }
    internal double Zoom { get; private set; } = 1;
    internal Vector Offset { get; private set; }
    internal bool RunOverlay { get; private set; } = true;
    internal bool OverlayShown => RunOverlay && State.Overlay != null;
    internal AnimationGraphBand? HoveredBand { get; private set; }
    internal AnimationGraphLevel Level => Zoom >= .75 ? AnimationGraphLevel.Rows : Zoom >= .5 ? AnimationGraphLevel.Compact : Zoom >= .3 ? AnimationGraphLevel.Headers : AnimationGraphLevel.Chips;
    /// <summary>Nodes realised now (virtualisation), for checks.</summary>
    internal int RealisedCount => realised.Count;
    internal FrameworkElement Tools { get; }

    /// <summary>A node or row was chosen (click, keys): the window selects it (Inspector, 3D outlines, Timing).</summary>
    internal event Action<string>? SelectionRequested;
    internal event Action<AnimationGraphRow, AnimationConditionChoice>? ConditionRequested;
    internal event Action<AnimationGraphEdge, bool>? IncludeRequested;
    internal event Action<string, bool>? ExpandRequested;
    internal event Action<string>? OpenRequested;
    /// <summary>collapse, open, showAll, expandRow, collapseRow, collapseAll, expandAll (with the node or row).</summary>
    internal event Action<string, string?>? PresentationRequested;
    internal event Action<double>? SeekRequested;
    internal event Action? RerunRequested, ViewportChanged, EditTimingRequested;
    internal event Action<bool>? RunOverlayChanged;
    /// <summary>all, opened (this animation only) or none.</summary>
    internal event Action<string>? IncludeAllRequested;
    /// <summary>An expanded group's frame should show another of its bindings (its member switcher): the frame and the binding.</summary>
    internal event Action<string, int>? MemberRequested;

    internal AnimationGraphView()
    {
        Focusable = true; FocusVisualStyle = null; ClipToBounds = true;
        AutomationProperties.SetName(this, "Structure view");
        content.RenderTransform = transform;
        edges = new(this);
        Panel.SetZIndex(edges, 1);
        content.Children.Add(edges);
        viewport.Child = content;
        Children.Add(viewport);
        minimap = new(this) { Width = 180, Height = 110, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 10, 10) };
        Children.Add(minimap);
        StackPanel tag = new() { Orientation = Orientation.Horizontal };
        tag.Children.Add(runDot); tag.Children.Add(runText); tag.Children.Add(rerun);
        runTag.Child = tag;
        rerun.SetResourceReference(StyleProperty, "CommandButton"); rerun.Width = double.NaN; rerun.Height = 22;
        rerun.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        rerun.Click += (_, _) => RerunRequested?.Invoke();
        AutomationProperties.SetName(rerun, "Re-run to here");
        AutomationProperties.SetLiveSetting(runTag, AutomationLiveSetting.Polite);
        Children.Add(runTag);
        message.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Children.Add(message);
        Tools = BuildTools();
        SizeChanged += (_, _) => { if (Layout != null && graphKey != null && !viewInitialised) InitialView(); Realise(); minimap.InvalidateVisual(); UpdateMinimapVisibility(); };
        Loaded += (_, _) => RefreshTheme();
    }
    private bool viewInitialised;
    private AnimationGraphLevel shownLevel = AnimationGraphLevel.Rows;

    // ------------------------------------------------------------ state

    /// <summary>
    /// Shows a state. A new graph (another binding or definition) opens at its default view; the same graph keeps the zoom and
    /// the scroll position (an expansion, a collapse, a run update).
    /// </summary>
    internal void SetState(AnimationGraphState state, string key)
    {
        ArgumentNullException.ThrowIfNull(state);
        var previous = State;
        State = state;
        dark = AnimationGraphPalette.IsDark(this);
        bool relayout = !ReferenceEquals(previous.Layout, state.Layout);
        message.Text = state.Message ?? ""; message.Visibility = state.Message != null && state.Layout == null ? Visibility.Visible : Visibility.Collapsed;
        if (relayout)
        {
            foreach (var node in realised.Values) content.Children.Remove(node);
            realised.Clear();
            foreach (var frame in frames.Values) content.Children.Remove(frame);
            frames.Clear(); pips.Clear(); hoveredEdge = null; HoveredBand = null;
            if (state.Layout is { } layout)
            {
                foreach (var placement in layout.Nodes.Where(n => n.Node.Kind == AnimationGraphNodeKind.Frame))
                {
                    FrameView frame = new(this, placement);
                    Canvas.SetLeft(frame, placement.Bounds.X); Canvas.SetTop(frame, placement.Bounds.Y);
                    Panel.SetZIndex(frame, 0);
                    frames[placement.Id] = frame; content.Children.Add(frame);
                }
                edges.Width = layout.Bounds.Width; edges.Height = layout.Bounds.Height;
                content.Width = layout.Bounds.Width; content.Height = layout.Bounds.Height;
            }
            if (graphKey != key) { graphKey = key; viewInitialised = false; InitialView(); }
            else ClampOffset();
            if (Selection != null && !Exists(Selection)) Selection = null;
        }
        else foreach (var node in realised.Values) node.Refresh();
        foreach (var frame in frames.Values) { frame.RefreshSwitcher(); frame.InvalidateVisual(); }
        Realise();
        edges.InvalidateVisual(); minimap.InvalidateVisual();
        UpdateRunTag(); UpdateMinimapVisibility(); UpdateTools();
    }
    /// <summary>Redraws with the current theme's colours.</summary>
    internal void RefreshTheme()
    {
        dark = AnimationGraphPalette.IsDark(this);
        Background = null;
        viewport.Background = Palette.Dots;
        foreach (var node in realised.Values) node.Refresh();
        foreach (var frame in frames.Values) frame.InvalidateVisual();
        edges.InvalidateVisual(); minimap.InvalidateVisual();
        runTag.Background = Palette.Pane; runTag.BorderBrush = Palette.NodeBorder; runTag.BorderThickness = new(1);
        runText.Foreground = Palette.T2;
    }

    private bool Exists(string id) => Layout is { } layout && (layout.Node(id) != null || layout.Item(id) != null);

    // ------------------------------------------------------------ viewport

    private void InitialView()
    {
        if (Layout is not { } layout || ActualWidth <= 0 || ActualHeight <= 0) return;
        viewInitialised = true;
        // Fit when that keeps 75 % (narrow panels fit anyway, semantic zoom keeps it readable), else 100 % at the top left.
        double fit = FitZoom();
        bool narrow = ActualWidth < 900;
        if (fit >= .75 || narrow) { FitView(); return; }
        Zoom = 1; Offset = new(0, 0);
        ApplyTransform();
        _ = layout;
    }
    private double FitZoom() => Layout is not { } layout || ActualWidth <= 0 ? 1
        : Math.Clamp(Math.Min(ActualWidth / layout.Bounds.Width, ActualHeight / layout.Bounds.Height), MinimumZoom, MaximumZoom);
    /// <summary>Fit (Ctrl+0): the whole graph in the panel, centred, within 25–200 %.</summary>
    internal void FitView()
    {
        if (Layout is not { } layout) return;
        Zoom = Math.Min(1, FitZoom());
        Offset = new((ActualWidth - layout.Bounds.Width * Zoom) / 2, Math.Max(0, (ActualHeight - layout.Bounds.Height * Zoom) / 2));
        ApplyTransform();
    }
    /// <summary>Zooms to <paramref name="zoom"/> keeping <paramref name="anchor"/> (viewport DIP; the centre when null) still.</summary>
    internal void ZoomTo(double zoom, Point? anchor = null)
    {
        zoom = Math.Clamp(zoom, MinimumZoom, MaximumZoom);
        var at = anchor ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var contentPoint = new Point((at.X - Offset.X) / Zoom, (at.Y - Offset.Y) / Zoom);
        Zoom = zoom;
        Offset = new(at.X - contentPoint.X * zoom, at.Y - contentPoint.Y * zoom);
        ApplyTransform();
    }
    /// <summary>The content point at the panel's centre.</summary>
    internal Point Center => new((ActualWidth / 2 - Offset.X) / Zoom, (ActualHeight / 2 - Offset.Y) / Zoom);
    internal void CenterOn(Point point)
    {
        Offset = new(ActualWidth / 2 - point.X * Zoom, ActualHeight / 2 - point.Y * Zoom);
        ApplyTransform();
    }
    internal void PanBy(Vector delta) { Offset += delta; ApplyTransform(); }
    private void ClampOffset()
    {
        if (Layout is not { } layout) return;
        double width = layout.Bounds.Width * Zoom, height = layout.Bounds.Height * Zoom;
        double minX = Math.Min(0, ActualWidth - width) - ActualWidth / 2, maxX = Math.Max(0, ActualWidth - width) + ActualWidth / 2;
        double minY = Math.Min(0, ActualHeight - height) - ActualHeight / 2, maxY = Math.Max(0, ActualHeight - height) + ActualHeight / 2;
        Offset = new(Math.Clamp(Offset.X, minX, maxX), Math.Clamp(Offset.Y, minY, maxY));
    }
    private void ApplyTransform()
    {
        ClampOffset();
        transform.Matrix = new Matrix(Zoom, 0, 0, Zoom, Offset.X, Offset.Y);
        Realise();
        // Crossing a semantic zoom level redraws the nodes (rows, compact rows, headers, name chips).
        if (Level != shownLevel) { shownLevel = Level; foreach (var node in realised.Values) node.Refresh(); }
        minimap.InvalidateVisual();
        UpdateTools();
        ViewportChanged?.Invoke();
    }
    /// <summary>The part of the graph the panel shows, in layout DIP.</summary>
    internal Rect Visible => new((-Offset.X) / Zoom, (-Offset.Y) / Zoom, Math.Max(0, ActualWidth) / Zoom, Math.Max(0, ActualHeight) / Zoom);

    /// <summary>Realises the nodes inside the viewport plus one screen of margin, and lets go of those far outside it.</summary>
    private void Realise()
    {
        if (Layout is not { } layout) return;
        var visible = Visible;
        var keep = new Rect(visible.X - visible.Width, visible.Y - visible.Height, visible.Width * 3, visible.Height * 3);
        var drop = new Rect(keep.X - visible.Width, keep.Y - visible.Height, keep.Width + visible.Width * 2, keep.Height + visible.Height * 2);
        foreach (var placement in layout.Nodes)
        {
            if (placement.Node.Kind == AnimationGraphNodeKind.Frame) continue;
            var bounds = new Rect(placement.Bounds.X, placement.Bounds.Y, placement.Bounds.Width, placement.Bounds.Height);
            bool shown = realised.TryGetValue(placement.Id, out var node);
            if (!shown && bounds.IntersectsWith(keep)) Realise(placement);
            else if (shown && !bounds.IntersectsWith(drop) && !(node!.IsKeyboardFocusWithin))
            { content.Children.Remove(node); realised.Remove(placement.Id); }
        }
    }
    private AnimationGraphNodeView Realise(AnimationGraphPlacement placement)
    {
        if (realised.TryGetValue(placement.Id, out var existing)) return existing;
        AnimationGraphNodeView node = new(this, placement);
        Canvas.SetLeft(node, placement.Bounds.X); Canvas.SetTop(node, placement.Bounds.Y);
        Panel.SetZIndex(node, 2);
        realised[placement.Id] = node; content.Children.Add(node);
        node.UpdateChildVisibility();
        return node;
    }

    /// <summary>Scrolls a node or row into view (the panel keeps its zoom) and realises it.</summary>
    internal void Reveal(string id)
    {
        if (Layout is not { } layout) return;
        Rect? target = null;
        if (layout.Item(id) is { } found) target = new Rect(found.Node.Bounds.X, found.Node.Bounds.Y + found.Item.Y, found.Node.Bounds.Width, found.Item.Height);
        else if (layout.Node(id) is { } node) target = new Rect(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, Math.Min(node.Bounds.Height, ActualHeight / Zoom - 40));
        if (target is not { } rect) return;
        var visible = Visible;
        if (visible.Contains(rect)) { Realise(); return; }
        double x = Offset.X, y = Offset.Y;
        if (rect.Left < visible.Left || rect.Right > visible.Right) x = ActualWidth / 2 - (rect.X + rect.Width / 2) * Zoom;
        if (rect.Top < visible.Top || rect.Bottom > visible.Bottom) y = Math.Min(ActualHeight / 2, 60) - rect.Y * Zoom;
        Offset = new(x, y); ApplyTransform();
    }

    // ------------------------------------------------------------ selection

    /// <summary>Shows a selection (a node or row id), revealing it; null clears it. No event: the window chose it.</summary>
    internal void Select(string? id, bool focus = false)
    {
        Selection = id;
        if (id != null) Reveal(id);
        foreach (var node in realised.Values) node.InvalidateVisual();
        foreach (var frame in frames.Values) frame.InvalidateVisual();
        edges.InvalidateVisual();
        if (focus && id != null && BandOf(id) is { } band) band.Focus();
    }
    private AnimationGraphBand? BandOf(string id)
    {
        if (Layout is not { } layout) return null;
        string? node = layout.Item(id)?.Node.Id ?? (layout.Node(id) is { } placement && placement.Node.Kind != AnimationGraphNodeKind.Frame ? placement.Id : null);
        if (node == null) return null;
        var view = realised.TryGetValue(node, out var found) ? found : Realise(layout.Node(node)!);
        view.UpdateLayout();
        return view.Bands.FirstOrDefault(b => b.Id == id || b.Id == "header" && node == id);
    }

    internal bool BandClicked(AnimationGraphBand band, int clicks, Point at)
    {
        Focus();
        if (band.Id.StartsWith("showAll:", StringComparison.Ordinal)) { PresentationRequested?.Invoke("showAll", band.Node.Node.Id); return true; }
        if (band.Id.StartsWith("outcome:", StringComparison.Ordinal) || band.Id.StartsWith("passes:", StringComparison.Ordinal))
        {
            // A pip seeks to its pass.
            string row = band.Target;
            if (PassAt(band, at) is { } pass) { SeekRequested?.Invoke(pass.Time); return true; }
            SelectionRequested?.Invoke(row); band.Focus(); return true;
        }
        if (clicks >= 2 && band.Id == "header")
        {
            var node = band.Node.Node;
            if (node.Kind == AnimationGraphNodeKind.Sequence) PresentationRequested?.Invoke(band.Node.Placement.Collapsed ? "open" : "collapse", node.Id);
            else if (node.Reference is { Expandable: true, Unresolved: false } reference) ExpandRequested?.Invoke(node.Id, !reference.Expanded);
            return true;
        }
        SelectionRequested?.Invoke(band.Target);
        band.Focus();
        return true;
    }
    internal void Hover(AnimationGraphBand? band)
    {
        var previous = HoveredBand; HoveredBand = band;
        previous?.Node.InvalidateVisual(); band?.Node.InvalidateVisual();
    }
    internal void RequestCondition(AnimationGraphRow row, AnimationConditionChoice choice) => ConditionRequested?.Invoke(row, choice);
    internal void RequestInclude(AnimationGraphEdge edge, bool value) => IncludeRequested?.Invoke(edge, value);
    internal void RequestExpand(string node, bool value) => ExpandRequested?.Invoke(node, value);
    internal void RequestOpen(string node) => OpenRequested?.Invoke(node);
    internal void RequestPresentation(string action, string? id) => PresentationRequested?.Invoke(action, id);
    internal void RequestMember(string frame, int entry) => MemberRequested?.Invoke(frame, entry);
    /// <summary>
    /// A blocked sequence's link: selects the call row that waits on the excluded call and moves keyboard focus to its port,
    /// the include toggle (a click or Space includes the call).
    /// </summary>
    internal void FollowBlocked(string edgeId)
    {
        if (Layout?.Graph.Edge(edgeId) is not { } edge) return;
        // A call row beyond the first twelve (or in a collapsed sequence) has no port yet: show every row first.
        if (Layout.Item(edge.From) == null && Layout.Node(edge.FromNode)?.Node.Kind == AnimationGraphNodeKind.Sequence) PresentationRequested?.Invoke("showAll", edge.FromNode);
        SelectionRequested?.Invoke(edge.From);
        Reveal(edge.From);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (Layout?.Node(edge.FromNode) is not { } placement) return;
            var node = realised.TryGetValue(placement.Id, out var found) ? found : Realise(placement);
            node.UpdateLayout();
            node.Children.OfType<AnimationGraphPort>().FirstOrDefault(p => p.Edge.Id == edgeId)?.Focus();
        });
    }
    internal void SetRunOverlay(bool value)
    {
        if (RunOverlay == value) return;
        RunOverlay = value;
        foreach (var node in realised.Values) node.Refresh();
        edges.InvalidateVisual(); UpdateRunTag(); UpdateTools();
    }

    // ------------------------------------------------------------ what nodes ask

    internal AnimationRunSequence? RunSequence(string node) => State.Overlay?.Sequence(node);
    internal AnimationRunRow? RunRow(string row) => State.Overlay?.Row(row);
    internal AnimationRunCondition? RunCondition(string row) => State.Overlay?.Condition(row);
    internal AnimationRunReference? RunReference(string node) => State.Overlay?.Reference(node);
    internal AnimationRunBinding? RunBinding(int entry) => State.Overlay?.Binding(entry);
    internal string RunLabel(AnimationGraphNodeView node) => !OverlayShown ? "" : node.Node.Kind switch
    {
        AnimationGraphNodeKind.Sequence => RunSequence(node.Node.Id)?.Label ?? "",
        AnimationGraphNodeKind.Reference => RunReference(node.Node.Id)?.Label ?? "",
        AnimationGraphNodeKind.Activation => RunBinding(node.Node.Entry ?? 0)?.Label ?? "",
        _ => "",
    };
    /// <summary>The call path a frame's conditions are forced in: "" for the opened binding, the frame's first call path, or "" when only a name reaches it.</summary>
    internal string? FramePath(int entry) => Layout?.Graph.Node(AnimationGraph.FrameId(entry))?.Frame?.Path;
    internal AnimationConditionChoice Choice(AnimationGraphRow row) => FramePath(row.Entry) is { } path ? State.Scenario.Choice(path, row.Entry, row.Event) : AnimationConditionChoice.Game;
    /// <summary>"For destroy_tr_bridge #241 (primary start)." or the call path; empty when the frame has none.</summary>
    internal string ConditionScope(AnimationGraphRow row)
    {
        if (FramePath(row.Entry) is not { } path || Layout is not { } layout) return "";
        var entry = layout.Graph.Index.Package.Entries[row.Entry];
        return path.Length == 0 ? $"For {entry.Name} #{entry.Index} (primary start)." : $"For {entry.Name} #{entry.Index} in call path {path}.";
    }
    /// <summary>Whether the scenario includes an edge's call paths: all (true), none (false) or some (null).</summary>
    internal bool? Inclusion(AnimationGraphEdge edge)
    {
        if (edge.Paths.Count == 0) return false;
        int included = edge.Paths.Count(State.Scenario.Includes);
        return included == edge.Paths.Count ? true : included == 0 ? false : null;
    }
    internal string TargetName(AnimationGraphEdge edge) => Layout?.Graph.Node(edge.To) is { } node ? node.Reference?.Name ?? node.Label : edge.To;
    internal string PortTip(AnimationGraphEdge edge, bool? state)
    {
        string target = TargetName(edge);
        if (edge.Paths.Count == 0) return $"Calls {target}. This frame is reached only by name, so its call paths are not known here: include them from the Scenario tab.";
        string count = edge.Paths.Count == 1 ? "this call path" : $"its {edge.Paths.Count} call paths";
        if (edge.BackLink) return $"{(state == true ? "Included" : "Excluded")}: a call back to an animation that is running or invalidated is refused; including it shows the refusal. Click to {(state == true ? "exclude" : "include")} {count}.";
        return state == true
            ? $"Included: the preview runs {target} when the sequence reaches this call. Click to exclude {count}; excluding also excludes the call paths below it."
            : $"{(state == null ? "Partly included" : "Excluded")}: click to include {count} (with the calls that lead to them).";
    }
    internal int? TakenBranch(AnimationGraphBlock block)
    {
        // The latest pass of the IF decides; a false IF hands over to the first true ELSEIF, else to the last branch.
        if (State.Overlay == null) return null;
        var questions = block.Branches.Where(b => b.Condition != null).ToArray();
        if (questions.Length == 0 || Layout?.Graph.RowOf(block.Entry, questions[0].Condition!.Event) is not { } head || RunCondition(head.Id)?.Latest is not { } first) return null;
        if (first.Value) return questions[0].Index;
        foreach (var branch in questions.Skip(1))
            if (Layout.Graph.RowOf(block.Entry, branch.Condition!.Event) is { } row && RunCondition(row.Id)?.Latest is { Value: true } && RunCondition(row.Id)!.Latest!.Time >= first.Time) return branch.Index;
        return block.Branches[^1].Index;
    }
    internal string RowCaption(string row)
    {
        if (Layout?.Graph.Row(row) is not { } found) return "an event";
        string sequence = Layout.Graph.Node(AnimationGraph.SequenceId(found.Entry, found.Sequence))?.Label ?? "";
        return $"{sequence} › {found.Label}";
    }
    internal string PrerequisiteNote(int entry, AnimationRunBinding? binding)
    {
        string policy = State.Scenario.Prerequisites switch
        {
            AnimationPrerequisiteRule.Check => "The scenario checks them as the game does.",
            AnimationPrerequisiteRule.Ignore => "The scenario ignores them.",
            _ => "The scenario ignores them for its own starts and checks everything the run starts.",
        };
        if (!OverlayShown || binding == null) return "Checked on every start. " + policy;
        if (binding.Prerequisites is { } start)
        {
            // The latest start attempt: what the game would decide, and what the run did.
            string counts = start.Result.Minimum > 0 ? $" · {start.Result.Matched} of {start.Result.Checks.Count(c => !c.Required)} options (needs {start.Result.Minimum})" : "";
            string at = AnimationSeconds.Format(start.Time);
            switch (start.Outcome)
            {
                case "ignored":
                    return $"⚠ At {at}: unmet{counts}. The game refuses this start; " + (State.Scenario.Prerequisites == AnimationPrerequisiteRule.Ignore ? "the scenario ignores prerequisites." : "the scenario ignores prerequisites for its starts.");
                case "met":
                    return $"Met at {at}{counts}. " + policy;
            }
        }
        if (binding.Refusals.Count > 0)
        {
            var refusal = binding.Refusals[^1];
            return $"⚠ Refused at {AnimationSeconds.Format(refusal.Time)}: {AnimationRunOverlay.Refusal(refusal)}. " + policy;
        }
        if (binding.Time is double started && binding.State != AnimationBindingLifecycle.NotStarted)
            return $"Started at {AnimationSeconds.Format(started)}" + (State.Scenario.Prerequisites == AnimationPrerequisiteRule.Check ? " with its prerequisites met." : ". " + policy);
        return "Not started yet. " + policy;
    }

    /// <summary>
    /// A prerequisite group's outcome at the binding's latest start attempt: "met", "unmet · refused" or "unmet · ignored for
    /// this start"; null before any attempt (the header then says the scenario's rule).
    /// </summary>
    internal string? PrerequisiteGroupOutcome(AnimationRunBinding? binding, AnimationGraphPrerequisiteGroup group)
    {
        if (binding?.Prerequisites is not { } start) return null;
        var checks = group.Items.Select(i => start.Result.Checks.FirstOrDefault(c => c.Record == i.Record)).ToArray();
        bool met = group.Required ? checks.All(c => c?.Satisfied != false) : checks.Count(c => c?.Satisfied == true) >= group.Minimum;
        return met ? "met" : Unmet(start);
    }
    private static string Unmet(AnimationPrerequisiteStart start) => start.Outcome switch
    {
        "refused" => "unmet · refused", "ignored" => "unmet · ignored for this start", _ => "unmet",
    };
    /// <summary>
    /// A prerequisite row: at the binding's latest start attempt, its check there ("met", "unmet · refused", "unmet · ignored
    /// for this start", or skipped for a node the load could not bind); before any, an animation item's state at the playhead
    /// (met unless stopped) and a neutral mark for an object item, which only a start evaluates.
    /// </summary>
    internal (string Mark, Brush Brush, string Status) PrerequisiteRow(AnimationRunBinding? binding, AnimationGraphPrerequisite item, AnimationRunOverlay? overlay)
    {
        var p = Palette;
        if (overlay != null && binding?.Prerequisites is { } start && start.Result.Checks.FirstOrDefault(c => c.Record == item.Record) is { } check)
            return check.Satisfied switch
            {
                true => ("✓", State.Stale ? p.T3 : p.Run, "met"),
                false => ("○", p.T2, Unmet(start)),
                _ => ("–", p.T3, "skipped · node not bound"),
            };
        if (item.Kind == "object") return ("·", p.T3, "active?");
        if (item.Entry is not int needed) return ("○", p.T2, "unresolved");
        if (overlay == null) return ("·", p.T3, "not stopped?");
        bool met = overlay.Binding(needed).State != AnimationBindingLifecycle.Stopped;
        return (met ? "✓" : "○", met ? p.Run : p.T2, met ? "not stopped" : "stopped");
    }

    /// <summary>Where a band (an outcome line or a pass strip) drew its pips: the first one's left, how many (the latest), their pitch.</summary>
    internal void SetPips(string band, double x, int shown, double pitch = 8) => pips[band] = (x, shown, pitch);
    private AnimationConditionPass? PassAt(AnimationGraphBand band, Point at)
    {
        string row = band.Target;
        if (!pips.TryGetValue(band.Id, out var strip) || strip.Shown == 0 || RunCondition(row) is not { } condition) return null;
        int index = (int)Math.Floor((at.X - strip.X) / strip.Pitch);
        if (index < 0 || index >= strip.Shown) return null;
        var passes = condition.Passes.Skip(condition.Passes.Count - strip.Shown).ToArray();
        return passes[index];
    }
    /// <summary>A pass's hover text, as the spec words it: "Pass 3 · 24.317 s · false (game)".</summary>
    internal static string PassTip(AnimationConditionPass pass) => $"Pass {pass.Pass} · {AnimationSeconds.Format(pass.Time)} · {(pass.Value ? "true" : "false")} ({(pass.Forced ? "forced" : "game")})";
    internal object OutcomeTip(AnimationGraphBand band, Point at)
    {
        string row = band.Target;
        if (PassAt(band, at) is { } pass) return PassTip(pass) + ". Click to seek there.";
        if (band.Id.StartsWith("passes:", StringComparison.Ordinal))
        {
            var passes = RunCondition(row);
            return passes == null || passes.PassCount == 0 ? "Each pass that tests this condition adds a pip (filled true, hollow false, ringed forced)."
                : $"{passes.PassCount} passes; the latest {Math.Min(passes.Passes.Count, AnimationGraphLayout.StripPips)} as pips. Point at a pip for its pass; click it to seek there.";
        }
        var condition = band.Item?.Branch?.Condition;
        var run = RunCondition(row);
        string outcome = run?.Latest is { } latest ? $"Latest: {latest.Outcome}" + (run.PassCount > 1 ? $" ({run.PassCount} passes)" : "") : "Not evaluated at the playhead.";
        return $"{condition?.Rule}\n{outcome}\n{ConditionScope(band.Item!.Row!)}";
    }
    internal string RowTip(AnimationGraphRow row)
    {
        var text = new System.Text.StringBuilder(row.Label);
        text.Append(" · ").Append(row.Keyword);
        if (row.ThresholdNote is { } note) text.Append(" · ").Append(note);
        else if (row.ThresholdBadge != null) text.Append(" · ").Append(AnimationPhrases.ThresholdWords(row.Clock, row.Threshold));
        if (row.Source is { } source) text.Append(" · ").Append(System.IO.Path.GetFileName(source.File));
        if (row.Callback is { } callback) text.Append('\n').Append(callback.Meaning).Append(" (preview: ").Append(callback.Preview).Append(')');
        if (row.Diagnostic is { } diagnostic) text.Append('\n').Append(diagnostic);
        if (row.Members.Count > 1) text.Append($"\n{row.Members.Count} events in a row; the chevron lists them.");
        if (Layout?.Graph.EdgesOf(row.Id).FirstOrDefault(e => e.AlsoNamed.Count > 0) is { } byName)
            text.Append("\nAlso named ").Append(TargetName(byName)).Append(": ").Append(string.Join(", ", byName.AlsoNamed.Take(8).Select(e => "#" + e.ToString(CultureInfo.InvariantCulture)))).Append(" (not affected)");
        return text.ToString();
    }
    internal string BandName(AnimationGraphBand band)
    {
        var node = band.Node;
        if (band.Id == "header") return node.NodeName();
        if (band.Id.StartsWith("showAll:", StringComparison.Ordinal)) return "Show all events of " + node.Node.Label;
        if (band.Id.StartsWith("outcome:", StringComparison.Ordinal)) return "Outcome of " + (band.Item?.Branch?.Condition?.Question ?? "the condition");
        if (band.Id.StartsWith("passes:", StringComparison.Ordinal)) return "Passes of " + (band.Item?.Branch?.Condition?.Question ?? "the condition") + (RunCondition(band.Target) is { PassCount: > 0 } passes ? $", {passes.PassCount} passes" : "");
        if (band.Item?.Row is not { } row) return band.Id;
        string run = OverlayShown && RunRow(row.Id) is { } runRow ? ", " + runRow.Disposition switch
        {
            AnimationRowDisposition.Executed => "complete", AnimationRowDisposition.Current => "running now", AnimationRowDisposition.SkippedByCondition => "skipped",
            AnimationRowDisposition.Excluded => "excluded", AnimationRowDisposition.NotReached => "not reached", _ => runRow.Disposition.ToString().ToLowerInvariant(),
        } : "";
        string label = band.Item.Kind switch
        {
            AnimationGraphItemKind.Question => band.Item.Branch?.Condition?.Question ?? row.Label,
            AnimationGraphItemKind.Branch => band.Item.Branch?.Label ?? "else",
            AnimationGraphItemKind.EndIf => "End if",
            _ => row.Label,
        };
        int count = node.Node.Sequence?.EventCount ?? 0;
        return $"{label}, event {row.Index + 1} of {count} of {node.Node.Label}{run}";
    }
    internal string BandRelations(AnimationGraphBand band)
    {
        if (band.Item?.Row is not { } row || Layout == null) return "";
        return string.Join("; ", Layout.Graph.EdgesOf(row.Id).Where(e => e.From == row.Id).Select(e => e.Relation switch
        {
            AnimationGraphRelation.Release => "Releases ", AnimationGraphRelation.ContactRelease => "Releases on contact ", AnimationGraphRelation.StopSequence => "Stops sequence ",
            AnimationGraphRelation.StopAnimation => "Stops ", AnimationGraphRelation.ResetAnimation => "Resets ", AnimationGraphRelation.InvalidateAnimation => "Invalidates ",
            _ => e.BackLink ? "Calls back " : "Calls ",
        } + TargetName(e)));
    }

    // ------------------------------------------------------------ pointer

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Layout == null) return;
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ZoomTo(Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), e.GetPosition(this));
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) PanBy(new(e.Delta * .6, 0));
        else PanBy(new(0, e.Delta * .6));
    }
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && Layout != null) { BeginPan(e); e.Handled = true; }
        // Space held: a left drag pans, over nodes too (it never selects); Space then does not play on release.
        else if (e.ChangedButton == MouseButton.Left && Layout != null && spacePanning && Keyboard.IsKeyDown(Key.Space)) { spaceDragged = true; BeginPan(e); e.Handled = true; }
    }
    private bool spacePanning, spaceDragged;
    /// <summary>Space went down with the pointer over the graph: a left drag now pans (design §8, Zoom and pan).</summary>
    internal void SpaceDown() { spacePanning = true; spaceDragged = false; if (panFrom == null) Cursor = Cursors.Hand; }
    /// <summary>Space went up: whether it was used to pan (else it plays or pauses, as Space does).</summary>
    internal bool SpaceUp()
    {
        bool dragged = spaceDragged;
        spacePanning = spaceDragged = false;
        if (panFrom == null) Cursor = null;
        return dragged;
    }
    internal bool SpaceHeld => spacePanning;
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || Layout == null) return;
        Focus();
        if (e.ChangedButton != MouseButton.Left) return;
        if (minimap.IsMouseOver) return;
        // A click on an edge selects its source event; a drag on empty space pans.
        if (hoveredEdge is { } edge && edge.Edge.From.StartsWith("row:", StringComparison.Ordinal)) { SelectionRequested?.Invoke(edge.Edge.From); e.Handled = true; return; }
        BeginPan(e); e.Handled = true;
    }
    private void BeginPan(MouseButtonEventArgs e)
    {
        panFrom = e.GetPosition(this); panOrigin = new(Offset.X, Offset.Y); panned = false;
        CaptureMouse(); Cursor = Cursors.SizeAll;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (panFrom is { } from)
        {
            var at = e.GetPosition(this);
            if ((at - from).Length > 3) panned = true;
            Offset = new(panOrigin.X + at.X - from.X, panOrigin.Y + at.Y - from.Y);
            ApplyTransform(); return;
        }
        if (Layout == null || HoveredBand != null || minimap.IsMouseOver) { SetHoveredEdge(null); return; }
        var point = e.GetPosition(content);
        SetHoveredEdge(EdgeAt(new(point.X, point.Y), 5 / Zoom));
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (panFrom == null) return;
        panFrom = null; ReleaseMouseCapture(); Cursor = spacePanning ? Cursors.Hand : null;
        // A plain click on empty space clears the selection (a Space pan never selects).
        if (!panned && !spacePanning && e.ChangedButton == MouseButton.Left && Selection != null) SelectionRequested?.Invoke("");
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); SetHoveredEdge(null); }
    private void SetHoveredEdge(AnimationGraphRoute? route)
    {
        if (ReferenceEquals(route, hoveredEdge)) return;
        hoveredEdge = route;
        edges.InvalidateVisual();
        ToolTip = route == null ? null : EdgeTip(route);
        Cursor = spacePanning || route != null && route.Edge.From.StartsWith("row:", StringComparison.Ordinal) ? Cursors.Hand : null;
    }
    internal AnimationGraphRoute? HoveredEdge => hoveredEdge;
    private AnimationGraphRoute? EdgeAt(Point point, double tolerance)
    {
        if (Layout is not { } layout) return null;
        foreach (var route in layout.Routes)
            for (int i = 0; i + 1 < route.Points.Count; i++)
            {
                var a = route.Points[i]; var b = route.Points[i + 1];
                double minX = Math.Min(a.X, b.X) - tolerance, maxX = Math.Max(a.X, b.X) + tolerance, minY = Math.Min(a.Y, b.Y) - tolerance, maxY = Math.Max(a.Y, b.Y) + tolerance;
                if (point.X >= minX && point.X <= maxX && point.Y >= minY && point.Y <= maxY) return route;
            }
        return null;
    }
    private string EdgeTip(AnimationGraphRoute route)
    {
        var edge = route.Edge;
        string verb = edge.Relation switch
        {
            AnimationGraphRelation.Call => "Calls", AnimationGraphRelation.CallWait => "Calls and waits for", AnimationGraphRelation.LocalChild => "Calls as a local child (stops with its caller)",
            AnimationGraphRelation.Release => "Releases", AnimationGraphRelation.ContactRelease => "Releases on ground contact", AnimationGraphRelation.StopSequence => "Stops sequence",
            AnimationGraphRelation.StopAnimation => "Stops", AnimationGraphRelation.ResetAnimation => "Resets", AnimationGraphRelation.InvalidateAnimation => "Invalidates",
            _ => "Is needed by",
        };
        string text = $"{verb} {TargetName(edge)}" + (edge.Count > 1 ? $" ×{edge.Count}" : "");
        if (edge.BackLink) text += " · calls back (cycle)";
        if (edge.Note != null) text += "\n" + edge.Note;
        if (edge.AlsoNamed.Count > 0) text += $"\nThe game picks the first entry with that name; also named: {string.Join(", ", edge.AlsoNamed.Take(8).Select(e => "#" + e))} (not affected).";
        if (OverlayShown && State.Overlay!.Edge(edge.Id) is { } run)
            text += run.Reached ? $"\nHappened, latest at {AnimationSeconds.Format(run.Time ?? 0)}" + (run.Excluded > 0 ? $" · excluded ×{run.Excluded}" : "") + (run.Refused > 0 ? $" · refused ×{run.Refused}" : "") : "\nNot reached at the playhead.";
        if (Inclusion(edge) == false && edge.Relation is AnimationGraphRelation.Call or AnimationGraphRelation.CallWait or AnimationGraphRelation.LocalChild) text += "\nExcluded";
        return text;
    }

    // ------------------------------------------------------------ keys

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || Layout == null) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.D0 or Key.NumPad0: FitView(); e.Handled = true; return;
                case Key.OemPlus or Key.Add: ZoomTo(Zoom * 1.25); e.Handled = true; return;
                case Key.OemMinus or Key.Subtract: ZoomTo(Zoom / 1.25); e.Handled = true; return;
                case Key.Enter when Selection != null && Layout.Node(Selection)?.Node.Reference is { Calling: false, Unresolved: false }: OpenRequested?.Invoke(Selection); e.Handled = true; return;
            }
            return;
        }
        if (modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Down or Key.Up: Step(e.Key == Key.Down ? 1 : -1); e.Handled = true; return;
            case Key.Right or Key.Left: Follow(e.Key == Key.Right); e.Handled = true; return;
            case Key.Enter when Selection != null:
                if (Layout.Node(Selection)?.Node.Reference is { Expandable: true, Unresolved: false } reference) ExpandRequested?.Invoke(Selection, !reference.Expanded);
                else if (Layout.Node(Selection)?.Node.Kind == AnimationGraphNodeKind.Sequence) PresentationRequested?.Invoke(Layout.Node(Selection)!.Collapsed ? "open" : "collapse", Selection);
                e.Handled = true; return;
            case Key.G or Key.T or Key.F when Selection != null && Layout.Item(Selection) is { Item.Kind: AnimationGraphItemKind.Question } question && !State.ReadOnly:
                ConditionRequested?.Invoke(question.Item.Row!, e.Key switch { Key.T => AnimationConditionChoice.True, Key.F => AnimationConditionChoice.False, _ => AnimationConditionChoice.Game });
                e.Handled = true; return;
            case Key.F2 when Selection != null && Layout.Item(Selection) != null: EditTimingRequested?.Invoke(); e.Handled = true; return;
            case Key.Escape when Selection != null: SelectionRequested?.Invoke(""); e.Handled = true; return;
        }
    }
    /// <summary>The selectable bands in column order: headers, rows, questions, branch labels, closing lines.</summary>
    private List<string> Order()
    {
        List<string> order = [];
        if (Layout is not { } layout) return order;
        foreach (var node in layout.Nodes.Where(n => n.Node.Kind != AnimationGraphNodeKind.Frame).OrderBy(n => Math.Round(n.Bounds.X)).ThenBy(n => n.Bounds.Y))
        {
            order.Add(node.Id);
            foreach (var item in node.Items)
                if (item.Row != null && item.Kind is AnimationGraphItemKind.Row or AnimationGraphItemKind.Member or AnimationGraphItemKind.Question or AnimationGraphItemKind.Branch or AnimationGraphItemKind.EndIf)
                    order.Add(item.Row.Id);
        }
        return order;
    }
    private void Step(int direction)
    {
        var order = Order();
        if (order.Count == 0) return;
        int at = Selection == null ? -1 : order.IndexOf(Selection);
        int next = at < 0 ? (direction > 0 ? 0 : order.Count - 1) : Math.Clamp(at + direction, 0, order.Count - 1);
        SelectionRequested?.Invoke(order[next]);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { if (Selection == order[next]) BandOf(order[next])?.Focus(); });
    }
    /// <summary>→ follows the selected row's edge out (or expands a header), ← goes back to an edge's source (or collapses a header).</summary>
    private void Follow(bool forward)
    {
        if (Layout is not { } layout || Selection == null) return;
        if (layout.Node(Selection) is { Node.Kind: AnimationGraphNodeKind.Sequence } header)
        {
            if (forward == header.Collapsed) PresentationRequested?.Invoke(forward ? "open" : "collapse", header.Id);
            else if (!forward) Back();
            return;
        }
        if (forward)
        {
            if (layout.Graph.EdgesOf(Selection).FirstOrDefault(e => e.From == Selection) is { } edge) { SelectionRequested?.Invoke(edge.To); return; }
            if (layout.Node(Selection)?.Node.Reference is { Expandable: true, Expanded: false }) ExpandRequested?.Invoke(Selection, true);
            return;
        }
        Back();
        void Back()
        {
            if (layout.Graph.EdgesOf(Selection!).FirstOrDefault(e => e.To == Selection && e.From.StartsWith("row:", StringComparison.Ordinal)) is { } incoming) SelectionRequested?.Invoke(incoming.From);
        }
    }

    // ------------------------------------------------------------ the run tag and tools

    private void UpdateRunTag()
    {
        if (Layout == null || State.Overlay == null && !State.Stale) { runTag.Visibility = Visibility.Collapsed; return; }
        runTag.Visibility = Visibility.Visible;
        runTag.Background = Palette.Pane; runTag.BorderBrush = Palette.NodeBorder; runTag.BorderThickness = new(1);
        runText.Foreground = Palette.T2;
        if (!RunOverlay) { runText.Text = "Run overlay off"; runDot.Fill = Palette.T4; rerun.Visibility = Visibility.Collapsed; }
        else if (State.Stale) { runText.Text = "Stale · from before your edit"; runDot.Fill = Palette.T4; rerun.Visibility = State.ReadOnly ? Visibility.Collapsed : Visibility.Visible; }
        else { runText.Text = "Run at " + AnimationSeconds.Format(State.Overlay!.Time); runDot.Fill = Palette.Run; rerun.Visibility = Visibility.Collapsed; }
        AutomationProperties.SetName(runTag, runText.Text);
    }
    private void UpdateMinimapVisibility()
    {
        bool larger = Layout is { } layout && (layout.Bounds.Width * Zoom > ActualWidth + 1 || layout.Bounds.Height * Zoom > ActualHeight + 1);
        minimap.Visibility = larger && ActualWidth >= 900 ? Visibility.Visible : Visibility.Collapsed;
    }

    private TextBlock? zoomText;
    private ToggleButton? runToggle;
    private FrameworkElement BuildTools()
    {
        StackPanel tools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Icon(string glyph, string name, string tip, Action click)
        {
            Button button = new() { Content = new AnimationGraphGlyph { Glyph = glyph, Size = 15 }, ToolTip = tip };
            button.SetResourceReference(StyleProperty, "CommandButton"); button.Width = 30; button.Height = 28;
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => click();
            return button;
        }
        Button Text(string text, string tip, Action click)
        {
            Button button = new() { Content = new TextBlock { Text = text, FontSize = 12.5 }, ToolTip = tip, Padding = new(8, 0, 8, 0) };
            button.SetResourceReference(StyleProperty, "CommandButton"); button.Width = double.NaN; button.Height = 28;
            AutomationProperties.SetName(button, text);
            button.Click += (_, _) => click();
            return button;
        }
        tools.Children.Add(Icon("fit", "Fit", "Fit the graph in the panel (Ctrl+0)", () => { FitView(); }));
        tools.Children.Add(Icon("zoomOut", "Zoom out", "Zoom out (Ctrl+−)", () => ZoomTo(Zoom / 1.25)));
        zoomText = new() { FontSize = 12.5, Width = 40, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Button zoom = new() { Content = zoomText, ToolTip = "Zoom: 25 %–200 % (Ctrl+wheel at the pointer)", Padding = new(0) };
        zoom.SetResourceReference(StyleProperty, "CommandButton"); zoom.Width = 46; zoom.Height = 28;
        AutomationProperties.SetName(zoom, "Zoom level");
        zoom.Click += (_, _) =>
        {
            ContextMenu menu = new() { PlacementTarget = zoom, Placement = PlacementMode.Bottom };
            foreach (double level in new[] { .25, .5, .75, 1, 1.5, 2 })
            {
                MenuItem item = new() { Header = new TextBlock { Text = (level * 100).ToString("0", CultureInfo.InvariantCulture) + " %" }, IsCheckable = true, IsChecked = Math.Abs(level - Zoom) < .005 };
                item.Click += (_, _) => ZoomTo(level);
                menu.Items.Add(item);
            }
            MenuItem fit = new() { Header = new TextBlock { Text = "Fit" }, InputGestureText = "Ctrl+0" }; fit.Click += (_, _) => FitView();
            menu.Items.Add(new Separator()); menu.Items.Add(fit);
            menu.IsOpen = true;
        };
        tools.Children.Add(zoom);
        tools.Children.Add(Icon("zoomIn", "Zoom in", "Zoom in (Ctrl+=)", () => ZoomTo(Zoom * 1.25)));
        tools.Children.Add(Divider());
        tools.Children.Add(Text("Collapse all", "Collapse every sequence to its header", () => PresentationRequested?.Invoke("collapseAll", null)));
        tools.Children.Add(Text("Expand all", "Open every sequence", () => PresentationRequested?.Invoke("expandAll", null)));
        tools.Children.Add(Divider());
        runToggle = new() { Content = new TextBlock { Text = "Run overlay", FontSize = 12.5 }, ToolTip = "Show the run at the playhead on the graph", Padding = new(8, 0, 8, 0), IsChecked = true };
        runToggle.SetResourceReference(StyleProperty, "CommandButton"); runToggle.Width = double.NaN; runToggle.Height = 28;
        AutomationProperties.SetName(runToggle, "Run overlay");
        runToggle.Click += (_, _) => { SetRunOverlay(runToggle.IsChecked == true); RunOverlayChanged?.Invoke(RunOverlay); };
        tools.Children.Add(runToggle);
        Button include = new() { ToolTip = "Include call paths: all calls, this animation's only, or none (the Scenario tab lists them)", Padding = new(8, 0, 6, 0) };
        StackPanel includeContent = new() { Orientation = Orientation.Horizontal };
        includeContent.Children.Add(new TextBlock { Text = "Include", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        includeContent.Children.Add(new AnimationGraphGlyph { Glyph = "chevron", Size = 11, Margin = new(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        include.Content = includeContent;
        include.SetResourceReference(StyleProperty, "CommandButton"); include.Width = double.NaN; include.Height = 28;
        AutomationProperties.SetName(include, "Include calls");
        include.Click += (_, _) =>
        {
            ContextMenu menu = new() { PlacementTarget = include, Placement = PlacementMode.Bottom };
            foreach (var (header, value, tip) in new[]
            {
                ("All calls", "all", "Include every reachable call path of every start; back-links stay excluded"),
                ("This animation only", "opened", "Include every call path below the opened binding's start; exclude the other starts' calls"),
                ("None", "none", "Exclude every call path"),
            })
            {
                MenuItem item = new() { Header = new TextBlock { Text = header }, ToolTip = tip, IsEnabled = !State.ReadOnly };
                item.Click += (_, _) => IncludeAllRequested?.Invoke(value);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        tools.Children.Add(include);
        tools.Children.Add(Divider());
        tools.Children.Add(Icon("legend", "Legend", "Legend: edges, states and families", ShowLegend));
        UpdateTools();
        return tools;

        Border Divider()
        {
            Border line = new() { Width = 1, Height = 18, Margin = new(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
            return line;
        }
    }
    private void UpdateTools()
    {
        if (zoomText != null) zoomText.Text = (Zoom * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
        if (runToggle != null) runToggle.IsChecked = RunOverlay;
    }
    private void ShowLegend()
    {
        StackPanel body = new() { Margin = new(14, 12, 14, 12), Width = 340 };
        body.Children.Add(new TextBlock { Text = "Structure legend", FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new(0, 0, 0, 8) });
        var p = Palette;
        void Line(string glyph, Brush brush, string text)
        {
            DockPanel line = new() { Margin = new(0, 2, 0, 2) };
            var icon = new AnimationGraphGlyph { Glyph = glyph, Size = 14, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Documents.TextElement.SetForeground(icon, brush);
            line.Children.Add(icon);
            line.Children.Add(new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(line);
        }
        Line("call", p.Call, "Call (×n when grouped); a double line calls and waits; a square at the start: local child (stops with its caller)");
        Line("release", p.Release, "Release a waiting sequence (dotted: on ground contact)");
        Line("stop", p.Stop, "Stop sequence (solid), stop / reset / invalidate by name (dashed: the first entry with that name)");
        Line("condition", p.Prerequisite, "Prerequisite (dotted violet, a diamond at the activation)");
        Line("back", p.T2, "Back-link: calls an animation already drawn (cycle), through the channel above");
        Line("call", p.T4, "Faint: excluded call path (hollow port) or not reached yet; a port ● is the include toggle");
        Line("check", p.T3, "✓ executed · now (green bar) · hatched: skipped by a condition · faded: not reached");
        Line("clock", p.T3, "Run chips: Running · Complete · Waiting for release · +t s waiting for threshold · Blocked · Not started");
        Line("condition", p.T2, "Condition: Game · True · False per IF and ELSEIF; pips per pass (filled true, accent ring forced), click to seek");
        Line("state", p.State, "Families: state · motion · camera · sound · effect · call · release · stop · game callbacks");
        body.Children.Add(new TextBlock { Text = "Keys: ↑ ↓ rows · → follow an edge, ← back · Enter expands · Ctrl+Enter opens · G T F switch · Ctrl+0 fit · Ctrl+= / Ctrl+− zoom", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0), Opacity = .8 });
        Border frame = new() { Child = body, CornerRadius = new(8), BorderThickness = new(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        AutomationProperties.SetName(frame, "Structure legend");
        Popup popup = new() { Child = frame, StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PlacementTarget = Tools, HorizontalOffset = 200, IsOpen = true };
        _ = popup;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ViewPeer(this);
    /// <summary>UI Automation: the graph is a tree, definition frames holding their sequences; reference nodes are siblings.</summary>
    private sealed class ViewPeer(AnimationGraphView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tree;
        protected override string GetClassNameCore() => "AnimationGraphView";
        protected override List<AutomationPeer>? GetChildrenCore()
        {
            List<AutomationPeer> children = [];
            foreach (var frame in owner.frames.Values.OrderBy(f => f.Placement.Bounds.X).ThenBy(f => f.Placement.Bounds.Y))
                if (CreatePeerForElement(frame) is { } peer) children.Add(peer);
            foreach (var node in owner.realised.Values.Where(n => n.Placement.Frame == null).OrderBy(n => n.Placement.Bounds.X).ThenBy(n => n.Placement.Bounds.Y))
                if (CreatePeerForElement(node) is { } peer) children.Add(peer);
            foreach (var element in owner.Children.OfType<FrameworkElement>().Where(e => e != owner.viewport && e.Visibility == Visibility.Visible))
                if (CreatePeerForElement(element) is { } peer) children.Add(peer);
            return children;
        }
    }
    internal IEnumerable<AnimationGraphNodeView> RealisedIn(string frame) => realised.Values.Where(n => n.Placement.Frame == frame).OrderBy(n => n.Placement.Lane).ThenBy(n => n.Placement.Bounds.Y);
    internal IReadOnlyCollection<AnimationGraphNodeView> RealisedNodes => realised.Values;

    // ------------------------------------------------------------ frames

    /// <summary>
    /// A definition frame: its box, its header (definition glyph, name, binding and file), a click selects the definition. An
    /// expanded group's frame shows one member at a time: its header names the group, its member switcher ("rdrail01 · 1 of 8
    /// ▾") chooses the member the frame and its run overlay show, and a line sums up every member's state at the playhead.
    /// </summary>
    private sealed class FrameView : Panel
    {
        private readonly AnimationGraphView view;
        private readonly Button? switcher;
        private readonly TextBlock? switcherText;
        internal AnimationGraphPlacement Placement { get; }
        private double HeaderHeight => Placement.HeaderHeight;
        internal FrameView(AnimationGraphView view, AnimationGraphPlacement placement)
        {
            this.view = view; Placement = placement;
            Width = placement.Bounds.Width; Height = placement.Bounds.Height;
            var frame = placement.Node.Frame!;
            AutomationProperties.SetName(this, frame.Group != null ? $"Called animation {GroupName()}, showing {frame.Root} #{frame.Entry}, {frame.MemberIndex + 1} of {frame.Members.Count}" : $"Definition {frame.Name}, {frame.Root} #{frame.Entry}");
            ToolTip = "Select the definition (Inspector → Definition)";
            ToolTipService.SetInitialShowDelay(this, 700);
            if (frame.Group != null && frame.Members.Count > 0)
            {
                switcherText = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                StackPanel content = new() { Orientation = Orientation.Horizontal };
                content.Children.Add(switcherText);
                content.Children.Add(new AnimationGraphGlyph { Glyph = "chevron", Size = 10, Margin = new(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                switcher = new() { Content = content, Padding = new(6, 0, 4, 0), Focusable = true, HorizontalAlignment = HorizontalAlignment.Left };
                switcher.SetResourceReference(StyleProperty, "CommandButton");
                switcher.Width = double.NaN; switcher.Height = 18;
                switcher.Click += (_, e) => { e.Handled = true; ShowMembers(); };
                Children.Add(switcher);
                RefreshSwitcher();
            }
        }
        private string GroupName() => Placement.Node.Frame!.Group is { } group && view.Layout?.Graph.Node(AnimationGraph.ReferenceId(group))?.Reference is { } reference ? reference.Name : Placement.Node.Frame.Name;
        private string MemberRoot(int entry) => view.Layout?.Graph.Index.Package.Entries is { } entries && entry < entries.Count ? entries[entry].RootName : "#" + entry.ToString(CultureInfo.InvariantCulture);
        /// <summary>"8 bindings · 1 running · 7 not started": every member's state at the playhead.</summary>
        internal string MembersSummary()
        {
            var frame = Placement.Node.Frame!;
            int count = view.Layout?.Graph.Node(AnimationGraph.ReferenceId(frame.Group!))?.Reference?.EntryCount ?? frame.Members.Count;
            string head = $"{count} binding{(count == 1 ? "" : "s")}";
            if (!view.OverlayShown || view.RunReference(AnimationGraph.ReferenceId(frame.Group!)) is not { } run || run.States.Count == 0) return head;
            return head + " · " + string.Join(" · ", run.States.Select(s => $"{s.Bindings} {AnimationRunOverlay.Label(s.State, null).ToLowerInvariant().Replace(" · ", ", ", StringComparison.Ordinal)}"));
        }
        internal void RefreshSwitcher()
        {
            if (switcher == null || switcherText == null) return;
            var frame = Placement.Node.Frame!;
            switcherText.Text = $"{frame.Root} · {frame.MemberIndex + 1} of {frame.Members.Count}";
            switcher.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
            string summary = MembersSummary();
            AutomationProperties.SetName(switcher, $"Shown binding {frame.Root} #{frame.Entry}, {frame.MemberIndex + 1} of {frame.Members.Count}; choose another. {summary}");
            switcher.ToolTip = $"The frame shows {frame.Root} #{frame.Entry}; its run overlay is this binding's. Choose another binding of {GroupName()}.\n{summary}";
            ToolTip = $"{GroupName()} · {summary}\nSelect the definition (Inspector → Definition)";
        }
        private void ShowMembers()
        {
            var frame = Placement.Node.Frame!;
            ContextMenu menu = new() { PlacementTarget = switcher, Placement = PlacementMode.Bottom };
            foreach (int entry in frame.Members)
            {
                string state = view.OverlayShown && view.RunBinding(entry) is { } binding ? " · " + binding.Label : "";
                MenuItem item = new() { Header = new TextBlock { Text = $"{MemberRoot(entry)} · #{entry}{state}" }, IsCheckable = true, IsChecked = entry == frame.Entry };
                AutomationProperties.SetName(item, $"{MemberRoot(entry)} #{entry}{state}");
                int chosen = entry;
                item.Click += (_, _) => { if (chosen != frame.Entry) view.RequestMember(Placement.Id, chosen); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }
        protected override Size MeasureOverride(Size availableSize)
        {
            switcher?.Measure(new Size(Math.Max(0, Width - 40), 18));
            return new(Width, Height);
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            switcher?.Arrange(new Rect(26, AnimationGraphLayout.FrameHeader, Math.Min(switcher.DesiredSize.Width, Math.Max(0, Width - 36)), 18));
            return finalSize;
        }
        protected override void OnRender(DrawingContext dc)
        {
            var p = view.Palette;
            var frame = Placement.Node.Frame!;
            var rect = new Rect(.5, .5, Width - 1, Height - 1);
            dc.DrawRoundedRectangle(p.Frame, p.FramePen, rect, 8, 8);
            if (IsMouseOver && Mouse.GetPosition(this).Y < AnimationGraphLayout.FrameHeader) dc.DrawRoundedRectangle(p.HeaderHover, null, new Rect(1, 1, Width - 2, AnimationGraphLayout.FrameHeader - 1), 7.5, 7.5);
            dc.DrawLine(frame.Path == null && Placement.Node.Parent != null ? new Pen(p.FrameBorder, 1) { DashStyle = new DashStyle([3, 2], 0) } : p.FramePen, new(1, HeaderHeight + .5), new(Width - 1, HeaderHeight + .5));
            if (view.Selection == Placement.Id) dc.DrawRoundedRectangle(null, p.AccentOutline, new Rect(-2, -2, Width + 4, Height + 4), 10, 10);
            AnimationGraphGlyphs.Draw(dc, "definition", new(10, 9.5), 15, p.T2);
            double pixels = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            bool group = frame.Group != null;
            var name = new FormattedText(group ? GroupName() : frame.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, p.Strong, 13, p.T1, pixels) { MaxTextWidth = Math.Max(1, Width - 60), Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
            dc.DrawText(name, new Point(32, 17 - name.Height / 2));
            string caption = (group ? $"{frame.Name} · #{frame.Entry}" : $"{frame.Root} · #{frame.Entry}") + (frame.File is { } file ? " · " + System.IO.Path.GetFileName(file) : "") + (view.State.ReadOnly ? " · read only" : "");
            var detail = new FormattedText(caption, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, p.Text, 12, p.T2, pixels) { MaxTextWidth = Math.Max(1, Width - 48 - name.Width), Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
            dc.DrawText(detail, new Point(32 + name.WidthIncludingTrailingWhitespace + 8, 17 - detail.Height / 2));
            if (Placement.Node.Parent != null && view.Layout?.Graph.Index.Package.Entries.Count > frame.Entry && frame.Path == null && !group)
            {
                var note = new FormattedText("reached by name", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, p.Italic, 11, p.T4, pixels);
                dc.DrawText(note, new Point(Width - 10 - note.Width, 17 - note.Height / 2));
            }
            if (group)
            {
                // The members' line: the switcher (a control), then every member's state at the playhead.
                double x = 26 + (switcher?.DesiredSize.Width ?? 0) + 8;
                var summary = new FormattedText(MembersSummary(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, p.Text, 11, view.State.Stale ? p.T4 : p.T3, pixels)
                    { MaxTextWidth = Math.Max(1, Width - x - 10), Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
                dc.DrawText(summary, new Point(x, AnimationGraphLayout.FrameHeader + 9 - summary.Height / 2));
            }
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); InvalidateVisual(); }
        protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.Handled || e.GetPosition(this).Y > HeaderHeight) return;
            view.Focus();
            view.SelectionRequested?.Invoke(Placement.Id);
            e.Handled = true;
        }
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
            hitTestParameters.HitPoint.Y <= HeaderHeight && hitTestParameters.HitPoint.X >= 0 && hitTestParameters.HitPoint.X <= Width ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;
        protected override AutomationPeer OnCreateAutomationPeer() => new FramePeer(this);
        private sealed class FramePeer(FrameView owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TreeItem;
            protected override string GetClassNameCore() => "AnimationGraphFrame";
            protected override List<AutomationPeer>? GetChildrenCore() =>
                owner.view.RealisedIn(owner.Placement.Id).Select(n => CreatePeerForElement(n)).OfType<AutomationPeer>().ToList();
        }
    }

    // ------------------------------------------------------------ edges

    /// <summary>Every routed edge in one drawing: orthogonal lines with 6 DIP rounded corners, end marks, labels; faint when excluded or not reached.</summary>
    private sealed class EdgeLayer(AnimationGraphView view) : FrameworkElement
    {
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
        protected override void OnRender(DrawingContext dc)
        {
            if (view.Layout is not { } layout) return;
            var p = view.Palette;
            var hovered = view.HoveredEdge;
            string? selectedRow = view.Selection;
            foreach (var pass in new[] { 0, 1, 2 })
                foreach (var route in layout.Routes)
                {
                    bool emphasised = ReferenceEquals(route, hovered) || selectedRow != null && (route.Edge.From == selectedRow || route.Edge.FromNode == selectedRow || route.Edge.To == selectedRow);
                    bool? included = route.Edge.Relation is AnimationGraphRelation.Call or AnimationGraphRelation.CallWait or AnimationGraphRelation.LocalChild ? view.Inclusion(route.Edge) : true;
                    double opacity = 1;
                    if (included == false) opacity = .35;
                    else if (view.OverlayShown && view.State.Overlay!.Edge(route.Edge.Id) is { Reached: false }) opacity = .5;
                    if (view.State.Stale && view.OverlayShown) opacity = Math.Min(opacity, .7);
                    int order = emphasised ? 2 : opacity < 1 ? 0 : 1;
                    if (order != pass) continue;
                    if (emphasised) opacity = 1;
                    dc.PushOpacity(opacity);
                    Draw(dc, p, route, emphasised, route.Edge.BackLink && included == false ? " · excluded" : "");
                    dc.Pop();
                }
        }
        private static void Draw(DrawingContext dc, AnimationGraphPalette p, AnimationGraphRoute route, bool emphasised, string suffix)
        {
            var edge = route.Edge;
            Brush brush = p.Relation(edge.Relation);
            double thickness = emphasised ? 2.25 : edge.Relation == AnimationGraphRelation.Prerequisite ? 1 : 1.5;
            DashStyle dash = edge.Relation switch
            {
                AnimationGraphRelation.Prerequisite => new([1, 3], 0),
                AnimationGraphRelation.ContactRelease => new([1, 2.5], 0),
                AnimationGraphRelation.StopAnimation or AnimationGraphRelation.ResetAnimation or AnimationGraphRelation.InvalidateAnimation => new([5 / thickness * 1.5, 3 / thickness * 1.5], 0),
                _ => edge.BackLink ? new DashStyle([5 / thickness * 1.5, 3 / thickness * 1.5], 0) : DashStyles.Solid,
            };
            var geometry = Path(route.Points);
            if (edge.Relation == AnimationGraphRelation.CallWait)
            {
                // Call and wait: two strokes two apart.
                dc.DrawGeometry(null, new Pen(brush, 3.5 + (emphasised ? .75 : 0)) { LineJoin = PenLineJoin.Round }, geometry);
                dc.DrawGeometry(null, new Pen(p.Pane, 1.5) { LineJoin = PenLineJoin.Round }, geometry);
            }
            else dc.DrawGeometry(null, new Pen(brush, thickness) { DashStyle = dash, LineJoin = PenLineJoin.Round, DashCap = PenLineCap.Flat }, geometry);
            if (edge.Relation == AnimationGraphRelation.LocalChild) dc.DrawRectangle(brush, null, new Rect(route.Start.X + 2, route.Start.Y - 2.5, 5, 5));
            End(dc, p, route, brush);
            if (route.LabelAlign != AnimationGraphLabelAlign.None && route.Text + suffix is { Length: > 0 } text)
            {
                double pixels = 1.25;
                var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, p.Text, 10.5, brush, pixels);
                double x = route.LabelAlign == AnimationGraphLabelAlign.End ? route.Label.X - formatted.Width : route.Label.X;
                double y = route.Label.Y - formatted.Baseline;
                dc.DrawRectangle(p.Pane, null, new Rect(x - 2, y, formatted.Width + 4, formatted.Height));
                dc.DrawText(formatted, new Point(x, y));
            }
        }
        private static Geometry Path(IReadOnlyList<AnimationGraphPoint> points)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new(points[0].X, points[0].Y), false, false);
                for (int i = 1; i < points.Count; i++)
                {
                    var b = new Point(points[i].X, points[i].Y);
                    if (i + 1 < points.Count)
                    {
                        // A 6 DIP rounded corner, smaller when a segment is short.
                        var a = new Point(points[i - 1].X, points[i - 1].Y); var c = new Point(points[i + 1].X, points[i + 1].Y);
                        double r = Math.Min(6, Math.Min((b - a).Length, (c - b).Length) / 2);
                        var inDir = b - a; inDir.Normalize(); var outDir = c - b; outDir.Normalize();
                        var start = b - inDir * r; var end = b + outDir * r;
                        g.LineTo(start, true, true);
                        bool clockwise = inDir.X * outDir.Y - inDir.Y * outDir.X > 0;
                        g.ArcTo(end, new Size(r, r), 0, false, clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true);
                    }
                    else g.LineTo(b, true, true);
                }
            }
            geometry.Freeze();
            return geometry;
        }
        private static void End(DrawingContext dc, AnimationGraphPalette p, AnimationGraphRoute route, Brush brush)
        {
            var tip = new Point(route.EndPoint.X, route.EndPoint.Y);
            var before = route.Points.Count >= 2 ? new Point(route.Points[^2].X, route.Points[^2].Y) : tip - new Vector(1, 0);
            var direction = tip - before; if (direction.Length < .01) direction = new(1, 0); direction.Normalize();
            var normal = new Vector(-direction.Y, direction.X);
            switch (route.End)
            {
                case AnimationGraphEnd.Arrow:
                    {
                        var g = new StreamGeometry();
                        using (var c = g.Open()) { c.BeginFigure(tip, true, true); c.LineTo(tip - direction * 7 + normal * 3.5, true, true); c.LineTo(tip - direction * 7 - normal * 3.5, true, true); }
                        dc.DrawGeometry(brush, null, g);
                        if (route.Edge.BackLink) { var mark = new FormattedText("↩", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, p.Text, 11, brush, 1.25); dc.DrawText(mark, tip - direction * 8 + normal * 6 - new Vector(mark.Width / 2, mark.Height / 2)); }
                        break;
                    }
                case AnimationGraphEnd.OpenArrow:
                    {
                        var g = new StreamGeometry();
                        using (var c = g.Open()) { c.BeginFigure(tip - direction * 7 + normal * 4, false, false); c.LineTo(tip, true, true); c.LineTo(tip - direction * 7 - normal * 4, true, true); }
                        dc.DrawGeometry(null, new Pen(brush, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
                        break;
                    }
                case AnimationGraphEnd.TBar:
                    dc.DrawLine(new Pen(brush, 1.75), tip + normal * 5, tip - normal * 5);
                    break;
                case AnimationGraphEnd.Invalidate:
                    {
                        var center = tip - direction * 5;
                        dc.DrawEllipse(p.Pane, new Pen(brush, 1.5), center, 5, 5);
                        dc.DrawLine(new Pen(brush, 1.5), center + new Vector(-3.5, -3.5), center + new Vector(3.5, 3.5));
                        break;
                    }
                case AnimationGraphEnd.Reset:
                    {
                        var center = tip - direction * 5;
                        dc.DrawEllipse(p.Pane, null, center, 5.5, 5.5);
                        AnimationGraphGlyphs.Draw(dc, "cleanup", center - new Vector(5.5, 5.5), 11, brush);
                        break;
                    }
                case AnimationGraphEnd.Diamond:
                    {
                        var g = new StreamGeometry();
                        using (var c = g.Open()) { c.BeginFigure(tip, true, true); c.LineTo(tip - direction * 4 + normal * 3.5, true, true); c.LineTo(tip - direction * 8, true, true); c.LineTo(tip - direction * 4 - normal * 3.5, true, true); }
                        dc.DrawGeometry(brush, null, g);
                        break;
                    }
            }
            // A release or a stop leaves a coloured dot (calls have their include toggle there).
            if (route.FromPort && route.Edge.Relation is not (AnimationGraphRelation.Call or AnimationGraphRelation.CallWait or AnimationGraphRelation.LocalChild))
                dc.DrawEllipse(brush, new Pen(brush, 1), new Point(route.Start.X, route.Start.Y), 4.5, 4.5);
        }
    }

    // ------------------------------------------------------------ minimap

    /// <summary>The minimap (bottom right, 180×110): node rectangles and the viewport's frame; drag to pan.</summary>
    private sealed class Minimap : FrameworkElement
    {
        private readonly AnimationGraphView view;
        internal Minimap(AnimationGraphView view) { this.view = view; AutomationProperties.SetName(this, "Minimap"); ToolTip = "Drag to pan"; Cursor = Cursors.Hand; }
        private double Scale => view.Layout is { } layout ? Math.Min((ActualWidth - 4) / layout.Bounds.Width, (ActualHeight - 4) / layout.Bounds.Height) : 1;
        protected override void OnRender(DrawingContext dc)
        {
            var p = view.Palette;
            dc.DrawRoundedRectangle(p.Pane, new Pen(p.NodeBorder, 1), new Rect(.5, .5, ActualWidth - 1, ActualHeight - 1), 6, 6);
            if (view.Layout is not { } layout) return;
            double k = Scale;
            dc.PushClip(new RectangleGeometry(new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)), 5, 5));
            dc.PushTransform(new TranslateTransform(2, 2));
            foreach (var node in layout.Nodes)
            {
                var r = new Rect(node.Bounds.X * k, node.Bounds.Y * k, Math.Max(1, node.Bounds.Width * k), Math.Max(1, node.Bounds.Height * k));
                if (node.Node.Kind == AnimationGraphNodeKind.Frame) dc.DrawRectangle(null, new Pen(p.T4, .75), r);
                else dc.DrawRectangle(view.Selection == node.Id ? p.Accent : p.T4, null, r);
            }
            var visible = view.Visible;
            dc.DrawRectangle(null, new Pen(p.Accent, 1.5), new Rect(visible.X * k, visible.Y * k, visible.Width * k, visible.Height * k));
            dc.Pop(); dc.Pop();
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { base.OnMouseLeftButtonDown(e); CaptureMouse(); Move(e); e.Handled = true; }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (IsMouseCaptured) Move(e); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); ReleaseMouseCapture(); }
        private void Move(MouseEventArgs e)
        {
            var at = e.GetPosition(this); double k = Scale;
            view.CenterOn(new((at.X - 2) / k, (at.Y - 2) / k));
        }
    }
}
