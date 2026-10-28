using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>
/// One laid-out node of the Structure view (an activation, a sequence or a reference node): it draws its chrome, bands and run
/// overlay itself, and holds a thin <see cref="AnimationGraphBand"/> per selectable band (rows, questions, branch labels, the
/// header) for pointer, keyboard focus, tooltips and UI Automation, plus the real controls a band needs: condition switches,
/// call ports (include toggles), the chevron, Expand and Open.
/// </summary>
internal sealed class AnimationGraphNodeView : Panel
{
    private readonly AnimationGraphView view;
    private readonly Dictionary<string, FormattedText> texts = new(StringComparer.Ordinal);
    private Button? blockedLink;
    internal AnimationGraphPlacement Placement { get; }
    internal AnimationGraphNode Node => Placement.Node;
    internal List<AnimationGraphBand> Bands { get; } = [];

    internal AnimationGraphNodeView(AnimationGraphView view, AnimationGraphPlacement placement)
    {
        this.view = view; Placement = placement;
        Width = placement.Bounds.Width; Height = placement.Bounds.Height;
        SnapsToDevicePixels = false; Focusable = false;
        ClipToBounds = false;
        Build();
    }

    // ------------------------------------------------------------ children

    private void Build()
    {
        var node = Node;
        Bands.Add(new(view, this, null, "header"));
        foreach (var item in Placement.Items)
        {
            switch (item.Kind)
            {
                case AnimationGraphItemKind.Row or AnimationGraphItemKind.Member or AnimationGraphItemKind.Question or AnimationGraphItemKind.EndIf:
                    Bands.Add(new(view, this, item, item.Row!.Id));
                    break;
                case AnimationGraphItemKind.Branch when item.Row != null:
                    Bands.Add(new(view, this, item, item.Row.Id));
                    break;
                case AnimationGraphItemKind.ShowAll:
                    Bands.Add(new(view, this, item, "showAll:" + node.Id) { ToolTip = "Show every event of this sequence" });
                    break;
                case AnimationGraphItemKind.Outcome when item.Row != null:
                    Bands.Add(new(view, this, item, "outcome:" + item.Row.Id));
                    break;
                case AnimationGraphItemKind.Passes when item.Row != null:
                    Bands.Add(new(view, this, item, "passes:" + item.Row.Id));
                    break;
            }
        }
        foreach (var band in Bands) Children.Add(band);
        if (node.Kind == AnimationGraphNodeKind.Sequence)
        {
            var chevron = IconButton(Placement.Collapsed ? "chevronRight" : "chevron", Placement.Collapsed ? "Expand " + node.Label : "Collapse " + node.Label,
                (Placement.Collapsed ? "Expand" : "Collapse") + " the sequence (double-click its header)", () => view.RequestPresentation(Placement.Collapsed ? "open" : "collapse", node.Id));
            chevron.Tag = new Rect(4, 6, 16, 16);
            Children.Add(chevron);
            // "Blocked · waits for excluded call X" is a link to that call's include toggle (shown while the run is blocked there).
            blockedLink = new() { Padding = new(5, 0, 5, 0), Focusable = true, Visibility = Visibility.Collapsed, Cursor = Cursors.Hand };
            blockedLink.SetResourceReference(StyleProperty, "CommandButton");
            blockedLink.Width = double.NaN; blockedLink.Height = 20;
            blockedLink.Click += (_, e) => { e.Handled = true; if (view.RunSequence(node.Id)?.BlockedBy is { } edge) view.FollowBlocked(edge); };
            blockedLink.MouseDoubleClick += (_, e) => e.Handled = true;
            Children.Add(blockedLink);
        }
        if (node.Reference is { } reference)
        {
            int right = 6;
            if (!reference.Calling && !reference.Unresolved)
            {
                var open = IconButton("open", "Open " + reference.Name, "Open in Animation mode (Ctrl+Enter)", () => view.RequestOpen(node.Id));
                open.Tag = new Rect(Placement.Bounds.Width - right - 22, 3, 22, 22); right += 24;
                Children.Add(open);
            }
            if (reference.Expandable && !reference.Unresolved)
            {
                var expand = IconButton(reference.Expanded ? "chevron" : "chevronRight", (reference.Expanded ? "Collapse " : "Expand ") + reference.Name,
                    reference.Expanded ? "Collapse: hide its frames" : "Expand: show its sequences here (Enter)", () => view.RequestExpand(node.Id, !reference.Expanded));
                expand.Tag = new Rect(Placement.Bounds.Width - right - 22, 3, 22, 22);
                Children.Add(expand);
            }
        }
        foreach (var item in Placement.Items)
        {
            if (item.Kind == AnimationGraphItemKind.Outcome && item.Row != null && item.Branch?.Condition != null)
            {
                var toggle = new AnimationGraphSwitch(view, item.Row, item.Branch.Condition);
                double inset = AnimationGraphLayout.BlockInset * Math.Max(1, item.Depth);
                toggle.Tag = new Rect(Placement.Bounds.Width - inset - AnimationGraphSwitch.SwitchWidth - 4, item.Y + 2, AnimationGraphSwitch.SwitchWidth, 20);
                Children.Add(toggle);
            }
            if (item.Kind == AnimationGraphItemKind.Row && item.Row is { Members.Count: > 1 } grouped)
            {
                bool open = view.Layout?.Options.ExpandedRows.Contains(grouped.Id) == true;
                var members = IconButton(open ? "chevron" : "chevronRight", (open ? "Hide the members of " : "List the members of ") + grouped.Label,
                    open ? "Hide its members" : $"List its {grouped.Members.Count} events", () => view.RequestPresentation(open ? "collapseRow" : "expandRow", grouped.Id));
                members.Tag = new Rect(Placement.Bounds.Width - 46, item.Y + 3, 16, 16);
                Children.Add(members);
            }
        }
        foreach (var route in view.Layout?.Routes ?? [])
        {
            if (!route.FromPort || route.Edge.FromNode != node.Id || route.Edge.Relation is not (AnimationGraphRelation.Call or AnimationGraphRelation.CallWait or AnimationGraphRelation.LocalChild)) continue;
            var port = new AnimationGraphPort(view, route.Edge);
            port.Tag = new Rect(Placement.Bounds.Width - 8, route.Start.Y - Placement.Bounds.Y - 8, 16, 16);
            Children.Add(port);
        }
        AutomationProperties.SetName(this, NodeName());
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new NodePeer(this);
    /// <summary>UI Automation: a node is a tree item (under its frame or the view) whose children are its bands and controls.</summary>
    private sealed class NodePeer(AnimationGraphNodeView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TreeItem;
        protected override string GetClassNameCore() => "AnimationGraphNode";
        protected override string GetNameCore() => owner.NodeName();
        protected override List<AutomationPeer>? GetChildrenCore()
        {
            List<AutomationPeer> children = [];
            foreach (UIElement child in owner.Children) AnimationGraphView.Add(children, child);
            return children;
        }
    }

    private static Button IconButton(string glyph, string name, string tip, Action click)
    {
        Button button = new() { Content = new AnimationGraphGlyph { Glyph = glyph }, ToolTip = tip, Focusable = true, Padding = new(0) };
        button.SetResourceReference(StyleProperty, "CommandButton");
        button.Width = 22; button.Height = 22;
        AutomationProperties.SetName(button, name);
        button.Click += (_, e) => { e.Handled = true; click(); };
        button.MouseDoubleClick += (_, e) => e.Handled = true;
        return button;
    }

    internal string NodeName()
    {
        string run = view.RunLabel(this);
        return Node.Kind switch
        {
            AnimationGraphNodeKind.Activation => "Activation of " + (view.Layout?.Graph.Index.Package.Entries[Node.Entry ?? 0].Name ?? "") + (run.Length > 0 ? ", " + run : ""),
            AnimationGraphNodeKind.Reference => $"Called animation {Node.Label}" + (run.Length > 0 ? ", " + run : ""),
            _ => $"Sequence {Node.Label}" + (Placement.Collapsed ? ", collapsed" : "") + (run.Length > 0 ? ", " + run : ""),
        };
    }

    /// <summary>The header's tooltip: the run state at the playhead ("Blocked · waits for an excluded call"), then what the node is.</summary>
    internal string HeaderTip() => (view.RunLabel(this) is { Length: > 0 } run ? run + "\n" : "") + Node.Kind switch
    {
        AnimationGraphNodeKind.Sequence when Node.Sequence!.Cleanup => "Cleanup (RESET_STATE): runs on stop or reset. Double-click collapses or expands it.",
        AnimationGraphNodeKind.Sequence when Node.Sequence!.WaitsForRelease => "Waits for release (ACTIVATION ( ON_CALL )): starts only when a Release event releases it. Double-click collapses or expands it.",
        AnimationGraphNodeKind.Sequence => "Starts at once with the animation. Double-click collapses or expands it.",
        AnimationGraphNodeKind.Reference when Node.Reference!.Calling => Node.Reference.Name + " calls or names this animation. Select it for its definition.",
        AnimationGraphNodeKind.Reference => Node.Reference!.Name + ": " + Node.Reference.Detail + ". Double-click or Enter expands it; Ctrl+Enter opens it.",
        _ => "How this binding starts, its gates, reset behaviour and prerequisites.",
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new(Placement.Bounds.Width, Placement.Bounds.Height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in Children)
        {
            if (child is AnimationGraphBand band) { band.Arrange(band.Bounds); continue; }
            if (child == blockedLink) { child.Arrange(new Rect(Placement.Bounds.Width - 6 - child.DesiredSize.Width, 4, child.DesiredSize.Width, 20)); continue; }
            if (child is FrameworkElement { Tag: Rect rect }) child.Arrange(rect);
        }
        return finalSize;
    }
    /// <summary>The blocked link shows while the run is blocked at an excluded call of this sequence.</summary>
    private void RefreshBlockedLink()
    {
        if (blockedLink == null) return;
        var run = view.OverlayShown ? view.RunSequence(Node.Id) : null;
        bool blocked = run is { State: AnimationSequenceRunState.Blocked, BlockedBy: not null } && view.Level >= AnimationGraphLevel.Headers;
        blockedLink.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        if (!blocked) return;
        var p = view.Palette;
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        var glyph = new AnimationGraphGlyph { Glyph = "invalidate", Size = 11, Margin = new(0, 1, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        TextElement.SetForeground(glyph, p.Stop);
        content.Children.Add(glyph);
        var text = new TextBlock { Text = "Blocked ›", FontSize = 11, Foreground = p.Stop, VerticalAlignment = VerticalAlignment.Center };
        text.TextDecorations = TextDecorations.Underline;
        content.Children.Add(text);
        blockedLink.Content = content;
        AutomationProperties.SetName(blockedLink, run!.Label + ". Go to its include toggle");
        blockedLink.ToolTip = run.Label + "\nClick to select the call and its include toggle (Space includes it).";
        blockedLink.Measure(new Size(double.PositiveInfinity, 20));
    }

    // ------------------------------------------------------------ drawing

    internal void Refresh()
    {
        texts.Clear();
        AutomationProperties.SetName(this, NodeName());
        foreach (UIElement child in Children)
        {
            if (child is AnimationGraphSwitch toggle) toggle.Refresh();
            else if (child is AnimationGraphPort port) port.Refresh();
            else if (child is AnimationGraphBand band) band.Refresh();
        }
        UpdateChildVisibility();
        InvalidateVisual();
    }
    internal void UpdateChildVisibility()
    {
        var level = view.Level;
        foreach (UIElement child in Children)
            if (child is AnimationGraphSwitch or Button && child != blockedLink)
                child.Visibility = level >= AnimationGraphLevel.Compact && !(child is AnimationGraphSwitch && view.State.ReadOnly) ? Visibility.Visible : Visibility.Hidden;
        RefreshBlockedLink();
    }

    private double Pixels => VisualTreeHelper.GetDpi(this).PixelsPerDip;
    private FormattedText Text(string text, double size, Brush brush, Typeface face, double maxWidth = double.PositiveInfinity, string? key = null)
    {
        string cacheKey = key == null ? "" : key + "|" + brush.GetHashCode();
        if (key != null && texts.TryGetValue(cacheKey, out var cached) && cached.MaxTextWidth == Math.Max(1, maxWidth)) return cached;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, Pixels) { Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
        if (double.IsFinite(maxWidth)) formatted.MaxTextWidth = Math.Max(1, maxWidth);
        if (key != null) texts[cacheKey] = formatted;
        return formatted;
    }
    private static void DrawText(DrawingContext dc, FormattedText text, double x, double centerY) => dc.DrawText(text, new Point(x, Math.Round(centerY - text.Height / 2, 1)));
    private static void DrawRight(DrawingContext dc, FormattedText text, double right, double centerY) => dc.DrawText(text, new Point(right - text.WidthIncludingTrailingWhitespace, Math.Round(centerY - text.Height / 2, 1)));

    protected override void OnRender(DrawingContext dc)
    {
        var p = view.Palette;
        var bounds = new Rect(0, 0, Placement.Bounds.Width, Placement.Bounds.Height);
        var run = view.RunSequence(Node.Id);
        bool selected = view.Selection == Node.Id;
        var level = view.Level;
        // The chrome: radius 6, --node fill, 1 px border; the cleanup dashed; a running sequence's green ring; the selection's accent.
        var pen = Node.Sequence?.Cleanup == true ? p.CleanupPen : p.NodePen;
        dc.DrawRoundedRectangle(p.Node, null, bounds, 6, 6);
        if (view.HoveredBand?.Node == this && view.HoveredBand.Id == "header") dc.DrawRoundedRectangle(p.HeaderHover, null, new Rect(0, 0, bounds.Width, AnimationGraphLayout.Header), 6, 6);
        dc.DrawRoundedRectangle(null, pen, new Rect(.5, .5, bounds.Width - 1, bounds.Height - 1), 6, 6);
        if (run?.State == AnimationSequenceRunState.Running && view.OverlayShown) dc.DrawRoundedRectangle(null, p.RunOutline, new Rect(-.5, -.5, bounds.Width + 1, bounds.Height + 1), 6.5, 6.5);
        if (selected) dc.DrawRoundedRectangle(null, p.AccentOutline, new Rect(-2, -2, bounds.Width + 4, bounds.Height + 4), 8, 8);
        if (level == AnimationGraphLevel.Chips)
        {
            // Below 30 %: the name, large enough to read, and the run state as a large mark (run chips stay at every level).
            var mark = view.OverlayShown ? ChipMark(p) : null;
            double cy = Math.Min(bounds.Height / 2, 30);
            var name = Text(Node.Label, 30, p.T1, p.Strong, bounds.Width - 16 - (mark == null ? 0 : 40));
            DrawText(dc, name, 8, cy);
            if (mark is { } m)
            {
                var center = new Point(bounds.Width - 26, cy);
                if (view.State.Stale) dc.PushOpacity(.5); else dc.PushOpacity(1);
                if (m.Glyph == null) dc.DrawEllipse(m.Fill ? m.Brush : null, new Pen(m.Brush, 3), center, 11, 11);
                else AnimationGraphGlyphs.Draw(dc, m.Glyph, new(center.X - 14, cy - 14), 28, m.Brush);
                dc.Pop();
            }
            return;
        }
        switch (Node.Kind)
        {
            case AnimationGraphNodeKind.Sequence: RenderSequence(dc, p, run, level); break;
            case AnimationGraphNodeKind.Activation: RenderActivation(dc, p, level); break;
            case AnimationGraphNodeKind.Reference: RenderReference(dc, p, level); break;
        }
    }

    private void RenderSequence(DrawingContext dc, AnimationGraphPalette p, AnimationRunSequence? run, AnimationGraphLevel level)
    {
        var sequence = Node.Sequence!;
        double width = Placement.Bounds.Width;
        double x = 24;
        if (sequence.Cleanup) { AnimationGraphGlyphs.Draw(dc, "cleanup", new(x, 7.5), 13, p.T3); x += 18; }
        bool linked = blockedLink?.Visibility == Visibility.Visible;
        var chip = view.OverlayShown && !linked ? RunChip(run, p) : null;
        double chipWidth = linked ? blockedLink!.DesiredSize.Width + 4 : chip == null ? 0 : chip.Value.Width + 6;
        DrawText(dc, Text(Node.Label, 12.5, p.T1, p.Strong, width - x - 8 - chipWidth, "name"), x, 14);
        if (chip is { } c) c.Draw(dc, width - 8, 14);
        if (sequence.Diagnostics.Count > 0) AnimationGraphGlyphs.Draw(dc, "warning", new(width - 8 - chipWidth - 16, 7), 13, p.Caution);
        var bodyTop = AnimationGraphLayout.Header;
        var state = Placement.Items.FirstOrDefault(i => i.Kind == AnimationGraphItemKind.StateLine);
        if (state != null)
        {
            bodyTop = state.Bottom;
            RenderState(dc, p, state, run, level);
        }
        if (Placement.Collapsed)
        {
            var summary = Placement.Items.First(i => i.Kind == AnimationGraphItemKind.Summary);
            dc.DrawLine(p.DividerPen, new(0, summary.Y + .5), new(width, summary.Y + .5));
            double sx = 25;
            foreach (var family in sequence.Summary)
            {
                string glyph = family.Family switch
                {
                    AnimationEventFamily.State => "state", AnimationEventFamily.Motion => "motion", AnimationEventFamily.Camera => "camera", AnimationEventFamily.Sound => "sound",
                    AnimationEventFamily.Effect => "effect", AnimationEventFamily.Call => "call", AnimationEventFamily.Release => "release", AnimationEventFamily.Stop => "stop",
                    AnimationEventFamily.Flow => "loop", _ => "game",
                };
                AnimationGraphGlyphs.Draw(dc, glyph, new(sx, summary.Y + 4), 12, p.Family(family.Family));
                var count = Text(family.Count.ToString(CultureInfo.InvariantCulture), 10.5, p.T3, p.Text);
                DrawText(dc, count, sx + 13, summary.Y + summary.Height / 2);
                sx += 14 + count.Width + 7;
                if (sx > width - 70) break;
            }
            DrawRight(dc, Text($"{sequence.EventCount} events", 11, p.T3, p.Text), width - 8, summary.Y + summary.Height / 2);
            return;
        }
        if (level == AnimationGraphLevel.Headers)
        {
            // 30–50 %: the header and a faint bar per row; edges and run chips stay.
            dc.DrawLine(p.DividerPen, new(0, bodyTop + .5), new(width, bodyTop + .5));
            foreach (var item in Placement.Items.Where(i => i.Kind is AnimationGraphItemKind.Row or AnimationGraphItemKind.Question))
                dc.DrawRoundedRectangle(p.Selected, null, new Rect(12 + item.Depth * 6, item.Y + 6, width - 40 - item.Depth * 12, item.Height - 12), 2, 2);
            return;
        }
        dc.DrawLine(p.DividerPen, new(0, bodyTop + .5), new(width, bodyTop + .5));
        foreach (var box in Placement.Boxes.Where(b => b.Block.Loop == null))
        {
            double inset = AnimationGraphLayout.BlockInset * box.Depth;
            dc.DrawRoundedRectangle(p.Well, p.WellPen, new Rect(inset + .5, box.Top + .5, width - inset * 2 - 1, box.Bottom - box.Top - 1), 4, 4);
        }
        foreach (var box in Placement.Boxes.Where(b => b.Block.Loop != null))
        {
            // The loop bracket in the 14 DIP gutter, from the first event to the LOOP row.
            var bracket = new StreamGeometry();
            using (var g = bracket.Open())
            {
                g.BeginFigure(new(18, box.Top + 4), false, false);
                g.LineTo(new(13, box.Top + 4), true, true); g.ArcTo(new(9, box.Top + 8), new(4, 4), 0, false, SweepDirection.Counterclockwise, true, true);
                g.LineTo(new(9, box.Bottom - 8), true, true); g.ArcTo(new(13, box.Bottom - 4), new(4, 4), 0, false, SweepDirection.Counterclockwise, true, true);
                g.LineTo(new(18, box.Bottom - 4), true, true);
            }
            bracket.Freeze();
            dc.DrawGeometry(null, new Pen(p.T3, 1.5), bracket);
        }
        foreach (var item in Placement.Items) RenderItem(dc, p, item, level);
    }

    private void RenderState(DrawingContext dc, AnimationGraphPalette p, AnimationGraphItem state, AnimationRunSequence? run, AnimationGraphLevel level)
    {
        double cy = state.Y + state.Height / 2, x = 25, width = Placement.Bounds.Width;
        var sequence = Node.Sequence!;
        if (sequence.WaitsForRelease)
        {
            var pill = Text("Waits for release", 10.5, p.Release, p.Text);
            var rect = new Rect(x, cy - 8, pill.Width + 12, 16);
            dc.DrawRoundedRectangle(null, new Pen(p.Release, 1), rect, 8, 8);
            DrawText(dc, pill, x + 6, cy);
            x = rect.Right + 6;
            string? note = null; Brush brush = p.T3;
            if (view.OverlayShown && run is { State: AnimationSequenceRunState.WaitingForRelease } waiting)
            {
                note = waiting.NoReleaserLeft ? "no releaser left" : waiting.ReleasedBy is { } by ? "released by " + view.RowCaption(by) : "waiting";
                brush = waiting.NoReleaserLeft ? p.Caution : p.T2;
            }
            else if (Placement.Boxes.FirstOrDefault(b => b.Block.Loop != null)?.Block.Loop is { } loop) note = "↻ " + loop.Label + " · each pass waits";
            if (note != null) DrawText(dc, Text(note, 11, brush, p.Text, width - x - 8), x, cy);
            return;
        }
        string text = sequence.StateLine ?? "";
        DrawText(dc, Text(text, 11.5, sequence.IgnoredStartTime ? p.Caution : p.T2, p.Text, width - x - 8, "state"), x, cy);
    }

    private readonly record struct Chip(FormattedText Text, string? Glyph, Brush Brush, bool Outline, bool Dot)
    {
        public double Width => Text.Width + (Glyph != null || Dot ? 13 : 0) + (Outline ? 12 : 0);
        public void Draw(DrawingContext dc, double right, double cy)
        {
            double x = right - Width;
            if (Outline) { dc.DrawRoundedRectangle(null, new Pen(Brush, 1), new Rect(x, cy - 8, Width, 16), 8, 8); x += 6; }
            if (Dot) { dc.DrawEllipse(Brush, null, new Point(x + 4, cy), 3.5, 3.5); x += 13; }
            else if (Glyph != null) { AnimationGraphGlyphs.Draw(dc, Glyph, new(x, cy - 5.5), 11, Brush); x += 13; }
            dc.DrawText(Text, new Point(x, Math.Round(cy - Text.Height / 2, 1)));
        }
    }
    /// <summary>The run state at the name-chip level: a glyph, or a ring (filled when running).</summary>
    private (string? Glyph, Brush Brush, bool Fill)? ChipMark(AnimationGraphPalette p)
    {
        switch (Node.Kind)
        {
            case AnimationGraphNodeKind.Sequence when view.RunSequence(Node.Id) is { } run:
                return run.State switch
                {
                    AnimationSequenceRunState.Running => (null, p.Run, true),
                    AnimationSequenceRunState.Complete => ("check", p.T3, false),
                    AnimationSequenceRunState.Stopped => ("stop", p.T3, false),
                    AnimationSequenceRunState.WaitingForRelease => (null, p.Release, false),
                    AnimationSequenceRunState.WaitingForThreshold => ("clock", p.T3, false),
                    AnimationSequenceRunState.Blocked => ("invalidate", p.Stop, false),
                    AnimationSequenceRunState.Paused => ("pause", p.T3, false),
                    _ => (null, p.T4, false),
                };
            case AnimationGraphNodeKind.Activation when view.RunBinding(Node.Entry ?? 0) is { } binding:
                return binding.State is AnimationBindingLifecycle.Running or AnimationBindingLifecycle.RunningInvalidated ? (null, p.Run, true)
                    : binding.Label.StartsWith("Refused", StringComparison.Ordinal) ? ("invalidate", p.Bad, false) : (null, p.T4, false);
            case AnimationGraphNodeKind.Reference when view.RunReference(Node.Id) is { } reference:
                return reference.Label.StartsWith("refused", StringComparison.Ordinal) ? ("invalidate", p.Bad, false)
                    : reference.Label.Contains("running", StringComparison.Ordinal) ? (null, p.Run, true)
                    : reference.Instances > 0 ? ("check", p.T3, false) : (null, p.T4, false);
            default: return null;
        }
    }
    private Chip? RunChip(AnimationRunSequence? run, AnimationGraphPalette p)
    {
        if (run == null) return null;
        Brush faded = view.State.Stale ? p.T4 : p.T3;
        return run.State switch
        {
            AnimationSequenceRunState.Running => new(Text("Running", 11, view.State.Stale ? p.T3 : p.Run, p.Text), null, view.State.Stale ? p.T3 : p.Run, false, true),
            AnimationSequenceRunState.Complete => new(Text("Complete", 11, faded, p.Text), "check", faded, false, false),
            AnimationSequenceRunState.Stopped => new(Text(run.Label, 11, faded, p.Text), "stop", faded, false, false),
            AnimationSequenceRunState.WaitingForRelease => new(Text("Waiting for release", 10.5, p.Release, p.Text), null, p.Release, true, false),
            AnimationSequenceRunState.WaitingForThreshold => new(Text(run.ThresholdRemaining is double r ? "+" + AnimationSeconds.Short(r) : "Waiting", 11, faded, p.Text), "clock", faded, false, false),
            AnimationSequenceRunState.Blocked => new(Text("Blocked", 11, p.Stop, p.Text), "invalidate", p.Stop, false, false),
            AnimationSequenceRunState.Paused => new(Text(run.Label, 11, faded, p.Text), "pause", faded, false, false),
            AnimationSequenceRunState.Unavailable => new(Text("Unavailable", 11, faded, p.Text), null, faded, false, false),
            _ => new(Text("Not started", 11, faded, p.Text), null, faded, false, false),
        };
    }

    private void RenderItem(DrawingContext dc, AnimationGraphPalette p, AnimationGraphItem item, AnimationGraphLevel level)
    {
        double width = Placement.Bounds.Width, cy = item.Y + item.Height / 2;
        double inset = AnimationGraphLayout.BlockInset * item.Depth;
        double left = 10 + (item.Depth > 0 ? 16 + 6 * (item.Depth - 1) : 0) + (item.Looped ? 14 : 0);
        var band = Bands.FirstOrDefault(b => b.Item == item);
        bool selected = band != null && view.Selection == band.Id;
        bool hovered = band != null && view.HoveredBand == band;
        var rowRect = new Rect(inset + (item.Depth > 0 ? 1 : 0), item.Y, width - inset * 2 - (item.Depth > 0 ? 2 : 0), item.Height);
        switch (item.Kind)
        {
            case AnimationGraphItemKind.Row or AnimationGraphItemKind.Member:
                {
                    var row = item.Row!;
                    var runRow = view.OverlayShown ? view.RunRow(row.Id) : null;
                    var disposition = runRow?.Disposition;
                    if (disposition == AnimationRowDisposition.SkippedByCondition) dc.DrawRectangle(p.HatchBrush, null, rowRect);
                    if (selected) dc.DrawRectangle(p.Selected, null, rowRect);
                    else if (hovered) dc.DrawRectangle(p.HeaderHover, null, rowRect);
                    if (selected) dc.DrawRoundedRectangle(p.Accent, null, new Rect(rowRect.X + 1, item.Y + 5, 3, item.Height - 10), 1.5, 1.5);
                    else if (view.State.Highlighted.Contains(row.Id)) dc.DrawRectangle(p.Effect, null, new Rect(rowRect.X, item.Y, 3, item.Height));
                    bool current = disposition == AnimationRowDisposition.Current && !view.State.Stale;
                    if (current) dc.DrawRoundedRectangle(p.Run, null, new Rect(rowRect.X + 1, item.Y + 4, 3, item.Height - 8), 1.5, 1.5);
                    double opacity = disposition switch
                    {
                        AnimationRowDisposition.SkippedByCondition or AnimationRowDisposition.Excluded => .55,
                        AnimationRowDisposition.NotReached => .6,
                        _ => 1,
                    };
                    if (item.Kind == AnimationGraphItemKind.Member) left += 14;
                    double right = width - 9 - inset;
                    // The mark at the right: ✓, now, skipped, excluded, duplicate, refused.
                    string? mark = disposition switch
                    {
                        AnimationRowDisposition.Executed => "✓", AnimationRowDisposition.Current => "now", AnimationRowDisposition.SkippedByCondition => "skipped",
                        AnimationRowDisposition.Excluded => "excluded", AnimationRowDisposition.Duplicate => "duplicate", AnimationRowDisposition.Refused => "refused",
                        AnimationRowDisposition.Unresolved or AnimationRowDisposition.Unavailable => "unavailable", _ => null,
                    };
                    if (row.Members.Count > 1 && item.Kind == AnimationGraphItemKind.Row) right -= 18;
                    if (mark != null)
                    {
                        Brush markBrush = current ? p.Run : disposition == AnimationRowDisposition.Refused ? p.Bad : p.T3;
                        var markText = Text(mark, mark == "✓" ? 11 : 10, markBrush, current ? p.Strong : p.Text);
                        if (view.State.Stale) dc.PushOpacity(.5);
                        DrawRight(dc, markText, right, cy);
                        if (view.State.Stale) dc.Pop();
                        right -= markText.Width + 6;
                    }
                    else right -= 16;
                    if (level >= AnimationGraphLevel.Rows && (row.ThresholdNote is { } || row.ThresholdBadge is { }) && level == AnimationGraphLevel.Rows)
                    {
                        var badge = Text(row.ThresholdBadge ?? "", 10.5, p.T3, p.Text);
                        if (badge.Width < 90 && row.ThresholdBadge != null) { DrawRight(dc, badge, right, cy); right -= badge.Width + 6; }
                    }
                    if (opacity < 1 || view.State.Stale && disposition is not null and not AnimationRowDisposition.NotReached) dc.PushOpacity(view.State.Stale ? Math.Max(.5, opacity) : opacity);
                    else dc.PushOpacity(1);
                    var familyBrush = p.Family(row.Family);
                    AnimationGraphGlyphs.Draw(dc, AnimationGraphGlyphs.Family(row), new(left, cy - 7), 14, familyBrush);
                    var phrase = Text(row.Label, 12, current ? p.Run : p.T1, p.Text, right - left - 20, current ? null : "row:" + row.Id + ":" + right.ToString("0", CultureInfo.InvariantCulture));
                    if (disposition == AnimationRowDisposition.Excluded) phrase.SetTextDecorations(TextDecorations.Strikethrough);
                    DrawText(dc, phrase, left + 20, cy);
                    dc.Pop();
                    break;
                }
            case AnimationGraphItemKind.Question:
                {
                    var row = item.Row!;
                    if (selected) { dc.DrawRectangle(p.Selected, null, rowRect); dc.DrawRoundedRectangle(p.Accent, null, new Rect(rowRect.X - 5, item.Y + 5, 3, item.Height - 10), 1.5, 1.5); }
                    else if (hovered) dc.DrawRectangle(p.HeaderHover, null, rowRect);
                    else if (view.State.Highlighted.Contains(row.Id)) dc.DrawRectangle(p.Effect, null, new Rect(rowRect.X, item.Y, 3, item.Height));
                    double qx = inset + 7;
                    AnimationGraphGlyphs.Draw(dc, "condition", new(qx, cy - 7), 14, p.T2);
                    string question = item.Branch?.Condition?.Question ?? row.Label;
                    DrawText(dc, Text(question, 12, p.T1, p.Strong, width - inset * 2 - 34, "q:" + row.Id), qx + 20, cy);
                    break;
                }
            case AnimationGraphItemKind.Outcome:
                RenderOutcome(dc, p, item, level);
                break;
            case AnimationGraphItemKind.Passes:
                RenderPasses(dc, p, item);
                break;
            case AnimationGraphItemKind.Branch:
                {
                    var branch = item.Branch;
                    if (selected) dc.DrawRectangle(p.Selected, null, rowRect);
                    else if (hovered) dc.DrawRectangle(p.HeaderHover, null, rowRect);
                    bool taken = view.OverlayShown && branch != null && item.Block != null && view.TakenBranch(item.Block) == branch.Index;
                    string label = branch == null ? "branch" : branch.Empty ? branch.Label.Replace(" (nothing)", "", StringComparison.Ordinal) : branch.Label;
                    var text = Text(label + (taken ? " · taken" : ""), 11, taken && !view.State.Stale ? p.Run : p.T3, p.Text);
                    double bx = inset + 20;
                    DrawText(dc, text, bx, cy);
                    if (branch?.Empty == true) DrawText(dc, Text("(nothing)", 11, p.T4, p.Italic), bx + text.WidthIncludingTrailingWhitespace + 5, cy);
                    else if (branch?.Kind == "ifFalse" && item.Row != null && item.Row.ThresholdNote is { } waits && level == AnimationGraphLevel.Rows)
                        DrawRight(dc, Text(waits, 10.5, p.T3, p.Text), width - inset - 9, cy);
                    break;
                }
            case AnimationGraphItemKind.EndIf:
                if (selected || hovered) dc.DrawRectangle(selected ? p.Accent : p.T4, null, new Rect(inset + 8, cy - .5, width - inset * 2 - 16, 1));
                break;
            case AnimationGraphItemKind.ShowAll:
                {
                    if (hovered) dc.DrawRectangle(p.HeaderHover, null, rowRect);
                    DrawText(dc, Text($"Show all {Node.Sequence?.EventCount ?? item.Count} events · {item.Count} more", 11.5, p.Accent, p.Text, width - 30), 26, cy);
                    break;
                }
        }
    }

    private void RenderOutcome(DrawingContext dc, AnimationGraphPalette p, AnimationGraphItem item, AnimationGraphLevel level)
    {
        double width = Placement.Bounds.Width, cy = item.Y + item.Height / 2;
        double inset = AnimationGraphLayout.BlockInset * Math.Max(1, item.Depth);
        // The outcome under the question: "false · game · 20.000 s", or with several passes "true · game" and a pip per pass.
        double x = inset + 7, limit = width - inset - AnimationGraphSwitch.SwitchWidth - 6;
        var row = item.Row!;
        var choice = view.Choice(row);
        var condition = view.OverlayShown ? view.RunCondition(row.Id) : null;
        if (view.State.Stale) dc.PushOpacity(.5); else dc.PushOpacity(1);
        string band = "outcome:" + row.Id;
        // A question with a pass strip (in a loop, or tested more often than this line holds) keeps its pips there.
        bool strip = Placement.Items.Any(i => i.Kind == AnimationGraphItemKind.Passes && i.Row?.Id == row.Id);
        if (condition?.Latest is { } latest)
        {
            var value = Text(latest.Value ? "true" : "false", 11, p.T1, p.Strong);
            DrawText(dc, value, x, cy); x += value.WidthIncludingTrailingWhitespace;
            var how = Text(" · " + (latest.Forced ? "forced" : "game"), 11, latest.Forced ? p.Accent : p.T3, latest.Forced ? p.Strong : p.Text);
            DrawText(dc, how, x, cy); x += how.WidthIncludingTrailingWhitespace;
            if (condition.PassCount <= 1 || strip)
            {
                var at = Text(" · " + AnimationSeconds.Format(latest.Time), 11, p.T3, p.Text);
                if (x + at.Width <= limit) DrawText(dc, at, x, cy);
                view.SetPips(band, x, 0);
                dc.Pop();
                return;
            }
            // A few passes: a pip each after the outcome (the window gives a question with more its strip).
            x += 5;
            int shown = Math.Min(AnimationGraphLayout.OutcomePips, condition.Passes.Count);
            int more = condition.PassCount - shown;
            view.SetPips(band, x, shown);
            x = DrawPips(dc, p, condition.Passes.Skip(condition.Passes.Count - shown), x, cy, 8, 6);
            if (more > 0) DrawText(dc, Text("+" + more.ToString(CultureInfo.InvariantCulture), 10, p.T3, p.Text), x + 2, cy);
        }
        else
        {
            string text = choice == AnimationConditionChoice.Game ? (view.OverlayShown && view.State.Overlay != null ? "not evaluated yet" : "game rule") : (choice == AnimationConditionChoice.True ? "true · forced" : "false · forced");
            DrawText(dc, Text(text, 11, choice == AnimationConditionChoice.Game ? p.T3 : p.Accent, p.Text, limit - x), x, cy);
            view.SetPips(band, x, 0);
        }
        dc.Pop();
        _ = level;
    }

    /// <summary>
    /// A question's pass strip, the node's full width: a pip per pass (the latest 40), filled true, hollow false, an accent
    /// ring when forced, then "+n" for the earlier passes; a pip's tooltip names its pass and a click seeks there.
    /// </summary>
    private void RenderPasses(DrawingContext dc, AnimationGraphPalette p, AnimationGraphItem item)
    {
        double width = Placement.Bounds.Width, cy = item.Y + item.Height / 2;
        double inset = AnimationGraphLayout.BlockInset * Math.Max(1, item.Depth);
        double x = inset + 9, right = width - inset - 9;
        var row = item.Row!;
        string band = "passes:" + row.Id;
        var condition = view.OverlayShown ? view.RunCondition(row.Id) : null;
        if (condition == null || condition.PassCount == 0)
        {
            DrawText(dc, Text(view.OverlayShown && view.State.Overlay != null ? "no passes yet" : "a pip per pass", 10.5, p.T4, p.Italic, right - x), x, cy);
            view.SetPips(band, x, 0);
            return;
        }
        if (view.State.Stale) dc.PushOpacity(.5); else dc.PushOpacity(1);
        int shown = Math.Min(AnimationGraphLayout.StripPips, condition.Passes.Count);
        int more = condition.PassCount - shown;
        // The pitch fits 40 pips and "+n" in the strip, at most 8 DIP.
        var plus = more > 0 ? Text("+" + more.ToString(CultureInfo.InvariantCulture), 10, p.T3, p.Text) : null;
        double room = right - x - (plus == null ? 0 : plus.Width + 4);
        double pitch = Math.Clamp(room / AnimationGraphLayout.StripPips, 3, 8);
        view.SetPips(band, x, shown, pitch);
        x = DrawPips(dc, p, condition.Passes.Skip(condition.Passes.Count - shown), x, cy, pitch, Math.Max(2, pitch - 1.5));
        if (plus != null) DrawText(dc, plus, x + 3, cy);
        dc.Pop();
    }
    private static double DrawPips(DrawingContext dc, AnimationGraphPalette p, IEnumerable<AnimationConditionPass> passes, double x, double cy, double pitch, double pipWidth)
    {
        foreach (var pass in passes)
        {
            var rect = new Rect(x + .5, cy - 4.5, pipWidth, 9);
            dc.DrawRoundedRectangle(pass.Value ? p.T2 : null, new Pen(p.T2, 1), rect, 1, 1);
            if (pass.Forced) dc.DrawRoundedRectangle(null, new Pen(p.Accent, 1.25), new Rect(rect.X - 1.5, rect.Y - 1.5, rect.Width + 3, rect.Height + 3), 1.5, 1.5);
            x += pitch;
        }
        return x;
    }

    private void RenderActivation(DrawingContext dc, AnimationGraphPalette p, AnimationGraphLevel level)
    {
        double width = Placement.Bounds.Width;
        AnimationGraphGlyphs.Draw(dc, "start", new(8, 7.5), 13, p.T3);
        var binding = view.OverlayShown ? view.RunBinding(Node.Entry ?? 0) : null;
        FormattedText? state = null;
        if (binding != null)
        {
            Brush brush = binding.State is AnimationBindingLifecycle.Running or AnimationBindingLifecycle.RunningInvalidated ? p.Run
                : binding.Label.StartsWith("Refused", StringComparison.Ordinal) ? p.Bad : p.T3;
            if (view.State.Stale) brush = p.T4;
            state = Text(binding.Label, 11, brush, p.Text, 150);
            double sx = width - 8 - state.Width;
            if (brush == p.Run) { dc.DrawEllipse(p.Run, null, new Point(sx - 8, 14), 3.5, 3.5); }
            DrawText(dc, state, sx, 14);
        }
        DrawText(dc, Text("Activation", 12.5, p.T1, p.Strong, width - 30 - (state == null ? 0 : state.Width + 18)), 27, 14);
        dc.DrawLine(p.DividerPen, new(0, AnimationGraphLayout.Header - .5), new(width, AnimationGraphLayout.Header - .5));
        if (level == AnimationGraphLevel.Headers) return;
        var rule = view.State.Scenario.Prerequisites;
        foreach (var item in Placement.Items)
        {
            double cy = item.Y + item.Height / 2;
            switch (item.Kind)
            {
                case AnimationGraphItemKind.Trigger or AnimationGraphItemKind.Caller:
                    {
                        string glyph = item.Kind == AnimationGraphItemKind.Caller ? "back" : AnimationGraphGlyphs.Trigger(item.Trigger!.Kind);
                        AnimationGraphGlyphs.Draw(dc, glyph, new(8, cy - 6.5), 13, p.T3);
                        double right = width - 8;
                        if (item.Keyword is { Length: > 0 } keyword && level == AnimationGraphLevel.Rows && keyword.Length <= 16)
                        {
                            var kw = Text(keyword, 10, p.T4, p.Mono);
                            DrawRight(dc, kw, right, cy); right -= kw.Width + 6;
                        }
                        string text = item.Kind == AnimationGraphItemKind.Caller ? item.Text + " · back-link" : item.Text ?? "";
                        DrawText(dc, Text(text, 12, p.T1, p.Text, right - 28, "t:" + item.Y.ToString(CultureInfo.InvariantCulture)), 27, cy);
                        break;
                    }
                case AnimationGraphItemKind.PrerequisiteHeader:
                    {
                        dc.DrawLine(p.DividerPen, new(0, item.Y + .5), new(width, item.Y + .5));
                        AnimationGraphGlyphs.Draw(dc, "condition", new(-3.5, item.Y + 12 - 3.5), 7, p.Prerequisite);
                        DrawText(dc, Text(item.Text ?? "", 11.5, p.T2, p.Strong, width - 20), 10, item.Y + 12);
                        // Its second line: at the latest start attempt met, unmet · refused, or unmet · ignored for this start
                        // (with when); before any, the scenario's rule.
                        var outcome = view.OverlayShown ? view.PrerequisiteGroupOutcome(binding, item.Group!) : null;
                        string policy = outcome != null && binding?.Prerequisites is { } start ? $"{outcome} · at {AnimationSeconds.Format(start.Time)}"
                            : rule switch { AnimationPrerequisiteRule.Check => "Checked on every start", AnimationPrerequisiteRule.Ignore => "Ignored by the scenario", _ => "Ignored for the scenario's starts, checked for the rest" };
                        Brush brush = outcome == null ? p.T3 : outcome == "met" ? p.Run : outcome.EndsWith("refused", StringComparison.Ordinal) ? p.Bad : p.Caution;
                        if (view.State.Stale && outcome != null) brush = p.T4;
                        DrawText(dc, Text(policy, 11, brush, outcome == null ? p.Text : p.Strong, width - 20), 10, item.Y + 27);
                        break;
                    }
                case AnimationGraphItemKind.Prerequisite:
                    {
                        var prerequisite = item.Prerequisite!;
                        var (mark, markBrush, status) = view.PrerequisiteRow(binding, prerequisite, view.OverlayShown ? view.State.Overlay : null);
                        // The group's header says "for this start"; the row keeps room for its target.
                        status = status.Replace(" for this start", "", StringComparison.Ordinal);
                        DrawText(dc, Text(mark, 11.5, markBrush, p.Text), 12, cy);
                        var st = Text(status, 10.5, status.StartsWith("unmet", StringComparison.Ordinal) && !view.State.Stale ? (status.EndsWith("refused", StringComparison.Ordinal) ? p.Bad : status.Contains("ignored", StringComparison.Ordinal) ? p.Caution : p.T3) : p.T3, p.Text, width * .6);
                        DrawRight(dc, st, width - 8, cy);
                        DrawText(dc, Text(prerequisite.Target, 11.5, p.T2, p.Text, width - 40 - st.Width), 28, cy);
                        break;
                    }
                case AnimationGraphItemKind.More:
                    DrawText(dc, Text($"+{item.Count} more", 11, p.T3, p.Text), 28, cy);
                    break;
                case AnimationGraphItemKind.RunLine:
                    {
                        string text = view.PrerequisiteNote(Node.Entry ?? 0, binding);
                        bool caution = text.StartsWith("⚠", StringComparison.Ordinal);
                        var rect = new Rect(6, item.Y, width - 12, item.Height);
                        if (caution) dc.DrawRoundedRectangle(p.CautionBack, null, rect, 4, 4);
                        var formatted = new FormattedText(text.TrimStart('⚠', ' '), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, p.Text, 11, caution ? p.T1 : p.T3, Pixels)
                        { MaxTextWidth = rect.Width - (caution ? 24 : 8), MaxTextHeight = rect.Height - 4, Trimming = TextTrimming.CharacterEllipsis };
                        if (caution) AnimationGraphGlyphs.Draw(dc, "warning", new(rect.X + 5, rect.Y + 4), 12, p.Caution);
                        dc.DrawText(formatted, new Point(rect.X + (caution ? 21 : 4), rect.Y + 3));
                        break;
                    }
            }
        }
    }

    private void RenderReference(DrawingContext dc, AnimationGraphPalette p, AnimationGraphLevel level)
    {
        var reference = Node.Reference!;
        double width = Placement.Bounds.Width;
        if (reference.Unresolved) dc.DrawRoundedRectangle(null, p.CleanupPen, new Rect(.5, .5, width - 1, Placement.Bounds.Height - 1), 6, 6);
        AnimationGraphGlyphs.Draw(dc, "definition", new(8, 7), 14, p.T3);
        double right = width - 8 - (reference.Calling || reference.Unresolved ? 0 : 24) - (reference.Expandable && !reference.Unresolved ? 24 : 0);
        FormattedText? badge = null;
        if (reference.EntryCount > 1)
        {
            badge = Text($"{reference.EntryCount} bindings", 10.5, p.T2, p.Text);
            var rect = new Rect(right - badge.Width - 12, 6, badge.Width + 12, 16);
            dc.DrawRoundedRectangle(p.Control, null, rect, 8, 8);
            DrawText(dc, badge, rect.X + 6, 14);
            right = rect.X - 6;
        }
        // At the headers level its run line hides: the run state stays as a mark in the header.
        var headerMark = level == AnimationGraphLevel.Headers && view.OverlayShown ? ChipMark(p) : null;
        if (headerMark is { } hm)
        {
            right -= 16;
            if (hm.Glyph == null) dc.DrawEllipse(hm.Fill ? hm.Brush : null, new Pen(hm.Brush, 1.5), new Point(right + 8, 14), 4, 4);
            else AnimationGraphGlyphs.Draw(dc, hm.Glyph, new(right + 2, 8), 12, hm.Brush);
        }
        DrawText(dc, Text(reference.Name, 12.5, p.T1, p.Strong, right - 28, "name"), 28, 14);
        dc.DrawLine(p.DividerPen, new(0, AnimationGraphLayout.Header - .5), new(width, AnimationGraphLayout.Header - .5));
        if (level == AnimationGraphLevel.Headers) return;
        foreach (var item in Placement.Items)
        {
            double cy = item.Y + item.Height / 2;
            if (item.Kind == AnimationGraphItemKind.Line)
            {
                Brush brush = item.Keyword switch { "detail" => p.T3, "caution" => p.Caution, _ => p.T2 };
                DrawText(dc, Text(item.Text ?? "", item.Keyword == "detail" ? 11.5 : 12, brush, p.Text, width - 18, "l:" + item.Y.ToString(CultureInfo.InvariantCulture)), 9, cy);
            }
            else if (item.Kind == AnimationGraphItemKind.RunLine && view.OverlayShown && view.RunReference(Node.Id) is { } run)
            {
                Brush brush = run.Label.StartsWith("refused", StringComparison.Ordinal) ? p.Bad : run.Label.Contains("running", StringComparison.Ordinal) ? p.Run : run.Instances == 0 ? p.T3 : p.T2;
                if (view.State.Stale) dc.PushOpacity(.5); else dc.PushOpacity(1);
                DrawText(dc, Text("Run: " + run.Label, 12, brush, p.Text, width - 18), 9, cy);
                dc.Pop();
            }
        }
    }
}

/// <summary>Zoom levels: full rows (≥ 80 %), rows without threshold badges (50–80 %), headers and bars (30–50 %), name chips (&lt; 30 %).</summary>
internal enum AnimationGraphLevel { Chips, Headers, Compact, Rows }

/// <summary>
/// A selectable band of a node (its header, a row, a question, a branch label, the closing line, Show all, an outcome line): it
/// takes the pointer and keyboard focus, carries the tooltip and the UI Automation name, and draws only its focus ring (the
/// node draws the band).
/// </summary>
internal sealed class AnimationGraphBand : FrameworkElement
{
    private readonly AnimationGraphView view;
    internal AnimationGraphNodeView Node { get; }
    internal AnimationGraphItem? Item { get; }
    /// <summary>The band's identity: a row id, "header", "showAll:node" or "outcome:row".</summary>
    internal string Id { get; }
    internal Rect Bounds => Item == null ? new Rect(0, 0, Node.Placement.Bounds.Width, AnimationGraphLayout.Header) : new Rect(0, Item.Y, Node.Placement.Bounds.Width, Item.Height);
    /// <summary>The selection this band makes: its row, or its node for the header.</summary>
    internal string Target => Id == "header" ? Node.Node.Id : Id.StartsWith("outcome:", StringComparison.Ordinal) ? Id["outcome:".Length..] : Id.StartsWith("passes:", StringComparison.Ordinal) ? Id["passes:".Length..] : Id;
    internal bool Selectable => !Id.StartsWith("showAll:", StringComparison.Ordinal) && !Id.StartsWith("outcome:", StringComparison.Ordinal) && !Id.StartsWith("passes:", StringComparison.Ordinal);

    internal AnimationGraphBand(AnimationGraphView view, AnimationGraphNodeView node, AnimationGraphItem? item, string id)
    {
        this.view = view; Node = node; Item = item; Id = id;
        Focusable = Selectable || id.StartsWith("showAll:", StringComparison.Ordinal);
        FocusVisualStyle = null;
        ToolTipService.SetInitialShowDelay(this, 500);
        Refresh();
    }
    internal void Refresh()
    {
        AutomationProperties.SetName(this, view.BandName(this));
        AutomationProperties.SetHelpText(this, view.BandRelations(this));
        if (Item?.Row is { } row && Id == row.Id) ToolTip = view.RowTip(row);
        else if (Id == "header") ToolTip = Node.HeaderTip();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new BandPeer(this);
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (IsKeyboardFocused)
        {
            // The two-tone focus ring.
            var rect = new Rect(1.5, 1.5, Math.Max(0, RenderSize.Width - 3), Math.Max(0, RenderSize.Height - 3));
            dc.DrawRoundedRectangle(null, new Pen(view.Palette.Dark ? Brushes.Black : Brushes.White, 3), rect, 3, 3);
            dc.DrawRoundedRectangle(null, new Pen(view.Palette.T1, 1.5), rect, 3, 3);
        }
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); view.Hover(this); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); view.Hover(null); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = view.BandClicked(this, e.ClickCount, e.GetPosition(this));
    }
    protected override void OnToolTipOpening(ToolTipEventArgs e)
    {
        base.OnToolTipOpening(e);
        if (Id.StartsWith("outcome:", StringComparison.Ordinal) || Id.StartsWith("passes:", StringComparison.Ordinal)) ToolTip = view.OutcomeTip(this, Mouse.GetPosition(this));
    }

    private sealed class BandPeer(AnimationGraphBand owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => owner.Id == "header" ? AutomationControlType.Header : AutomationControlType.ListItem;
        protected override string GetClassNameCore() => "AnimationGraphBand";
        protected override bool IsKeyboardFocusableCore() => owner.Focusable;
        protected override bool IsContentElementCore() => true;
    }
}

/// <summary>The Game | True | False switch on an IF or ELSEIF's outcome line: a radio group through the scenario's condition operation.</summary>
internal sealed class AnimationGraphSwitch : StackPanel
{
    internal const double SwitchWidth = 92;
    private readonly AnimationGraphView view;
    private readonly AnimationGraphRow row;
    private readonly RadioButton game, yes, no;
    private bool changing;

    internal AnimationGraphSwitch(AnimationGraphView view, AnimationGraphRow row, AnimationGraphCondition condition)
    {
        this.view = view; this.row = row;
        Orientation = Orientation.Horizontal; HorizontalAlignment = HorizontalAlignment.Right;
        string group = "switch:" + row.Id;
        game = Segment("Game", "game", group); yes = Segment("True", "true", group); no = Segment("False", "false", group);
        Children.Add(game); Children.Add(yes); Children.Add(no);
        AutomationProperties.SetName(this, condition.Question);
        Refresh();
    }
    private RadioButton Segment(string text, string value, string group)
    {
        RadioButton button = new() { Content = text, GroupName = group, Tag = value, Focusable = true };
        button.SetResourceReference(StyleProperty, "GraphSwitchSegment");
        button.Checked += (_, _) =>
        {
            if (changing) return;
            // The request is applied (or refused, a draft pending) before it returns: the switch then shows the scenario as it is.
            try { view.RequestCondition(row, value switch { "true" => AnimationConditionChoice.True, "false" => AnimationConditionChoice.False, _ => AnimationConditionChoice.Game }); }
            finally { Refresh(); }
        };
        button.PreviewMouseLeftButtonDown += (_, e) => { if (button.IsChecked == true) e.Handled = true; };
        return button;
    }
    internal void Refresh()
    {
        var choice = view.Choice(row);
        string scope = view.ConditionScope(row);
        bool enabled = scope.Length > 0 && !view.State.ReadOnly;
        changing = true;
        try
        {
            game.IsChecked = choice == AnimationConditionChoice.Game; yes.IsChecked = choice == AnimationConditionChoice.True; no.IsChecked = choice == AnimationConditionChoice.False;
            foreach (var segment in new[] { game, yes, no }) segment.IsEnabled = enabled;
        }
        finally { changing = false; }
        string question = row.Label;
        game.ToolTip = enabled ? $"Game: the preview applies the game's rule with the scenario's inputs (G). {scope}" : "This frame is reached only by name: no call path to force its conditions in.";
        yes.ToolTip = enabled ? $"Force true for this call path (T). {scope}" : game.ToolTip;
        no.ToolTip = enabled ? $"Force false for this call path (F). {scope}" : game.ToolTip;
        AutomationProperties.SetName(this, $"{(view.Layout?.Graph.Row(row.Id) is { } shown && view.Layout.Item(row.Id)?.Item.Branch?.Condition?.Question is { } q ? q : question)}, preview: {choice}");
    }
}

/// <summary>A call row's out-port: its include toggle (● included, ○ excluded, ◐ some of its call paths).</summary>
internal sealed class AnimationGraphPort : ToggleButton
{
    private readonly AnimationGraphView view;
    internal AnimationGraphEdge Edge { get; }
    internal AnimationGraphPort(AnimationGraphView view, AnimationGraphEdge edge)
    {
        this.view = view; Edge = edge;
        Template = null; Focusable = true; IsThreeState = true; FocusVisualStyle = null; Cursor = Cursors.Hand;
        ToolTipService.SetInitialShowDelay(this, 300);
        Refresh();
    }
    internal void Refresh()
    {
        var state = view.Inclusion(Edge);
        IsChecked = state switch { true => true, false => false, _ => null };
        IsEnabled = Edge.Paths.Count > 0 && !view.State.ReadOnly;
        AutomationProperties.SetName(this, (state == true ? "Included: " : "Excluded: ") + "call " + view.TargetName(Edge));
        ToolTip = view.PortTip(Edge, state);
        InvalidateVisual();
    }
    // A click or UI Automation's Toggle: mixed or excluded includes every path; included excludes them. The check state
    // follows the scenario, never the click.
    protected override void OnToggle() => view.RequestInclude(Edge, view.Inclusion(Edge) != true);
    protected override void OnRender(DrawingContext dc)
    {
        var p = view.Palette;
        var center = new Point(8, 8);
        var state = view.Inclusion(Edge);
        Brush stroke = Edge.Relation == AnimationGraphRelation.LocalChild || Edge.BackLink ? p.Call : p.Call;
        dc.DrawEllipse(Brushes.Transparent, null, center, 8, 8);
        if (IsMouseOver && IsEnabled) dc.DrawEllipse(p.HeaderHover, null, center, 8, 8);
        double radius = 5;
        if (state == true) dc.DrawEllipse(stroke, new Pen(stroke, 1.5), center, radius, radius);
        else if (state == null)
        {
            dc.DrawEllipse(p.Node, new Pen(stroke, 1.5), center, radius, radius);
            var half = new StreamGeometry();
            using (var g = half.Open()) { g.BeginFigure(new(8, 3), true, true); g.ArcTo(new(8, 13), new(5, 5), 0, false, SweepDirection.Clockwise, true, true); }
            dc.DrawGeometry(stroke, null, half);
        }
        else dc.DrawEllipse(p.Node, new Pen(stroke, 1.5), center, radius, radius);
        if (IsKeyboardFocused) dc.DrawEllipse(null, new Pen(p.T1, 1.5), center, 7.5, 7.5);
    }
    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new ToggleButtonAutomationPeer(this);
}

/// <summary>A graph glyph as button content, in the button's foreground.</summary>
internal sealed class AnimationGraphGlyph : FrameworkElement
{
    public string Glyph { get; set; } = "chevron";
    public double Size { get; set; } = 12;
    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);
    protected override void OnRender(DrawingContext dc)
    {
        var brush = (Brush?)GetValue(TextElement.ForegroundProperty) ?? Brushes.Gray;
        AnimationGraphGlyphs.Draw(dc, Glyph, new(0, 0), Size, brush);
    }
}
