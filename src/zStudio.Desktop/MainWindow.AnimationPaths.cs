using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// Animation mode's Paths chip (design/animation-redesign.md §14) and what the 3D view draws for the opened animation: the
/// evaluated motion guides with a dot at the playhead and faint ticks every second, their contacts (⊥) and impact sounds (◎),
/// and the range tests (§10): rings around attach nodes with the player pin, the dashed line and its label. Path evaluation
/// runs silently for the opened binding with the scenario and its included paths; its progress and failures go to the chip and
/// the transport's status line, never to Diagnostics opening. MCP: animation_paths (analyze, show, hide, frame, clear, markers,
/// overlay, ranges) and animation_runtime player.
/// </summary>
public partial class MainWindow
{
    private ViewportChip? pathsChip;
    private Button? pathsChipButton;
    private TextBlock? pathsChipWord;
    private AnimationBusyRing? pathsChipRing;
    private Popup? pathsPopup;
    private bool showRangeTests, showPathMarkers;
    /// <summary>The graph row or node under the pointer (a hovered range test is drawn like a selected one).</summary>
    private string? animationGraphHover;
    private zStudio.Rendering.SceneViewport? overlayViewport;
    private string overlayGuidesKey = "", pathsFlyoutState = "";

    /// <summary>The chip's state word and status line for the opened binding's cached paths.</summary>
    private sealed record PathsChipState(string Word, string Status, bool Busy, bool Cached, bool Compatible, bool Shown, AnimationPaths? Analysis);
    private PathsChipState PathsState()
    {
        if (animationMode is not { } mode || shownDocument is not { } document)
            return new("not evaluated", "Open an animation in Animation mode.", false, false, false, false, null);
        bool busy = sourcePathLoading != null;
        if (!sourceAnimationPaths.TryGetValue(mode.Definition, out var cached) || cached.Document != document || cached.Revision != document.Revision)
            return new(busy ? "evaluating" : "not evaluated", busy ? "Evaluating paths… (silent: the playhead, camera and panel stay)" : "Not evaluated. Evaluate paths runs the scenario silently and draws the result.", busy, false, false, false, null);
        var analysis = cached.Analysis;
        bool compatible = cached.Entry == mode.Entry && cached.Scenario == worldAnimationScenarioEpoch;
        bool shown = shownAnimationPaths.Contains(mode.Definition);
        long samples = analysis.Paths.Sum(p => (long)p.Points.Count);
        string counts = $"{AnimationNotice.Plural(analysis.Paths.Count, "1 track", "tracks")} · {AnimationNotice.Plural(samples, "1 sample", "samples")}";
        bool partial = analysis.Status.StartsWith("Partial", StringComparison.Ordinal);
        if (busy) return new("evaluating", "Evaluating paths…", true, true, compatible, shown, analysis);
        if (!compatible)
            return new("out of date", cached.Entry != mode.Entry ? "Out of date: evaluated for another binding." : "Out of date: the scenario changed. Evaluate paths again.", false, true, false, shown, analysis);
        string status = (partial ? analysis.Status + " · " : "Evaluated for this scenario · ") + counts + $" · 0–{AnimationSeconds.Short(analysis.Seconds)}";
        return new(!shown ? "hidden" : partial ? "partial" : "shown", status, false, true, true, shown, analysis);
    }

    // ---------------------------------------------------------------- the chip

    private void EnsurePathsChip()
    {
        if (pathsChip != null) return;
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        var icon = new PreviewIcon { Kind = "Paths", Width = 14, Height = 14, Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = "Paths", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        pathsChipWord = new() { FontSize = 12, Margin = new(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        pathsChipWord.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        content.Children.Add(pathsChipWord);
        pathsChipRing = new() { Width = 12, Height = 12, Margin = new(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        content.Children.Add(pathsChipRing);
        content.Children.Add(new PreviewIcon { Kind = "Chevron", Width = 12, Height = 12, Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = .65 });
        pathsChipButton = new() { Content = content, Height = 28, Padding = new(10, 0, 8, 0), ToolTip = "Paths: evaluate, show, frame, clear; contacts and impacts; range tests" };
        pathsChipButton.SetResourceReference(StyleProperty, "CommandButton"); pathsChipButton.Width = double.NaN; pathsChipButton.Height = 28;
        AutomationProperties.SetName(pathsChipButton, "Paths");
        pathsChipButton.Click += (_, _) => { if (pathsPopup is { } popup) { popup.Child = PathsFlyout(); popup.IsOpen = true; } };
        pathsChip = new() { Content = pathsChipButton, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(124, 10, 10, 10), Padding = new(3), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(pathsChip, "Paths");
        pathsPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PlacementTarget = pathsChip, VerticalOffset = 4 };
        if (ViewModeChip.Parent is Panel parent) parent.Children.Insert(parent.Children.IndexOf(ViewModeChip) + 1, pathsChip);
        ViewModeChip.SizeChanged += (_, _) => PlacePathsChip();
        ViewModeChip.IsVisibleChanged += (_, _) => RefreshPathsChip();
    }
    private void PlacePathsChip()
    {
        if (pathsChip == null) return;
        double left = ViewModeChip.Margin.Left + (ViewModeChip.ActualWidth > 0 ? ViewModeChip.ActualWidth : 104) + 8;
        pathsChip.Margin = new(left, ViewModeChip.Margin.Top, 10, 10);
    }

    /// <summary>The chip shows in Animation mode over the 3D view: "Paths ▾ · shown", its ring while paths evaluate.</summary>
    private void RefreshPathsChip()
    {
        bool mode = InAnimationMode && sourceChromeActive && !presentationUiHidden && ViewModeChip.Visibility == Visibility.Visible;
        if (!mode && pathsChip == null) return;
        EnsurePathsChip();
        pathsChip!.Visibility = mode ? Visibility.Visible : Visibility.Collapsed;
        if (!mode) { if (pathsPopup != null) pathsPopup.IsOpen = false; return; }
        PlacePathsChip();
        var state = PathsState();
        pathsChipWord!.Text = "· " + state.Word;
        pathsChipRing!.Visibility = state.Busy ? Visibility.Visible : Visibility.Collapsed;
        pathsChipButton!.ToolTip = "Paths · " + state.Status;
        AutomationProperties.SetName(pathsChipButton, "Paths, " + state.Word);
        AutomationProperties.SetHelpText(pathsChipButton, state.Status);
        // An open flyout follows a change of state, not every frame of a playing run.
        string shownState = state.Word + "|" + state.Status + "|" + showRangeTests + "|" + showPathMarkers;
        if (pathsPopup?.IsOpen == true && shownState != pathsFlyoutState) { pathsFlyoutState = shownState; pathsPopup.Child = PathsFlyout(); }
    }

    /// <summary>The chip's flyout: Evaluate paths, Show guides, Show range tests, Contacts and impacts, Frame, Clear, the legend and status.</summary>
    private Border PathsFlyout()
    {
        var state = PathsState();
        StackPanel body = new() { Margin = new(6, 6, 6, 8), Width = 330 };
        Button evaluate = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = 32, Margin = new(0, 0, 0, 6), IsEnabled = !state.Busy && animationMode is { Entry: > 0 },
            ToolTip = "Runs the scenario silently for the opened binding with its included paths and draws the result; the playhead, camera and panel stay" };
        evaluate.SetResourceReference(StyleProperty, "AccentButtonStyle");
        DockPanel evaluateContent = new();
        TextBlock silent = new() { Text = state.Busy ? "evaluating…" : "silent", FontSize = 11, Opacity = .75, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(silent, Dock.Right); evaluateContent.Children.Add(silent);
        evaluateContent.Children.Add(new PreviewIcon { Kind = "Paths", Width = 14, Height = 14, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        evaluateContent.Children.Add(new TextBlock { Text = "Evaluate paths", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        evaluate.Content = evaluateContent;
        AutomationProperties.SetName(evaluate, "Evaluate paths");
        evaluate.Click += (_, _) => _ = ReportUi(() => PathsChipActionAsync("analyze"));
        body.Children.Add(evaluate);
        CheckBox Toggle(string text, bool value, bool enabled, string tip, string action)
        {
            CheckBox box = new() { Content = new TextBlock { Text = text }, IsChecked = value, IsEnabled = enabled, Margin = new(6, 2, 6, 2), MinHeight = 30, ToolTip = tip };
            ToolTipService.SetShowOnDisabled(box, true);
            AutomationProperties.SetName(box, text);
            box.Click += (_, _) => _ = ReportUi(() => PathsChipActionAsync(action, box.IsChecked == true));
            body.Children.Add(box); return box;
        }
        Toggle("Show guides", state.Shown && state.Compatible, state.Cached && state.Compatible && !state.Busy,
            state.Cached && state.Compatible ? "Show or hide the evaluated guides (presentation only)" : "Evaluate paths first: guides show only a result evaluated for this binding and scenario", "guides");
        Toggle("Show range tests", showRangeTests, true, "Draw every range condition and range gate of the opened animation and its expanded calls around their attach nodes, with the player", "rangeTests");
        Toggle("Contacts and impacts", showPathMarkers, true, "Mark where moving nodes touch the ground (⊥) and where they play an impact sound (◎) in the evaluated paths", "contacts");
        Button Action(string text, string icon, bool enabled, string tip, string action)
        {
            StackPanel row = new() { Orientation = Orientation.Horizontal };
            if (icon.Length > 0) row.Children.Add(new PreviewIcon { Kind = icon, Width = 14, Height = 14, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new(icon.Length > 0 ? 0 : 22, 0, 0, 0) });
            Button button = new() { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(6, 0, 6, 0), IsEnabled = enabled, ToolTip = tip };
            button.SetResourceReference(StyleProperty, "RowLinkButton"); button.HorizontalAlignment = HorizontalAlignment.Stretch; button.Height = 30;
            ToolTipService.SetShowOnDisabled(button, true);
            AutomationProperties.SetName(button, text);
            button.Click += (_, _) => _ = ReportUi(() => PathsChipActionAsync(action));
            body.Children.Add(button); return button;
        }
        Action("Frame paths", "Frame", state.Shown && state.Compatible, "Frame the shown guides", "frame");
        Action("Clear cached paths", "", state.Cached && !state.Busy, "Discard the opened binding's evaluated paths", "clear");
        Border rule = new() { Height = 1, Margin = new(4, 6, 4, 6) };
        rule.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        body.Children.Add(rule);
        TextBlock legend = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(6, 0, 6, 0) };
        legend.Inlines.Add(new System.Windows.Documents.Run("■ ") { Foreground = OutlinerKinds.Frozen("#6FC8D6") });
        legend.Inlines.Add(new System.Windows.Documents.Run("objects · "));
        legend.Inlines.Add(new System.Windows.Documents.Run("■ ") { Foreground = OutlinerKinds.Frozen("#F5A742") });
        legend.Inlines.Add(new System.Windows.Documents.Run("cameras. " + state.Status.TrimEnd('.') + ". A dot marks each guide at the playhead; faint ticks mark every second."));
        legend.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetName(legend, "Paths status: " + state.Status);
        body.Children.Add(legend);
        Border frame = new() { CornerRadius = new(8), BorderThickness = new(1), Child = body };
        frame.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        AutomationProperties.SetName(frame, "Paths");
        return frame;
    }

    /// <summary>The chip's actions, as MCP animation_paths runs them on the opened binding.</summary>
    private async Task PathsChipActionAsync(string action, bool value = false)
    {
        var mode = animationMode ?? throw new StudioCommandException("not_ready", "Open an animation in Animation mode first.");
        switch (action)
        {
            case "analyze":
                if (pathsPopup != null) pathsPopup.IsOpen = false;
                if (!await ResolvePropertiesDraftsAsync()) return;
                await AnalyzeSourceAnimationAsync(mode.Document, mode.Definition, mode.Entry, null, CancellationToken.None);
                break;
            case "guides":
                if (value) ShowSourceAnimationPaths(mode.Document, mode.Definition, mode.Entry); else HideSourceAnimationPaths(mode.Definition);
                break;
            case "rangeTests": showRangeTests = value; RefreshAnimationOverlay(); break;
            case "contacts": showPathMarkers = value; RefreshAnimationOverlay(); break;
            case "frame":
                if (pathsPopup != null) pathsPopup.IsOpen = false;
                if (scene?.FrameAnimationPaths(mode.Definition.ToString(CultureInfo.InvariantCulture)) != true) throw new StudioCommandException("not_ready", "No shown guides to frame: evaluate or show paths first.");
                break;
            case "clear": ClearSourceAnimationPaths(mode.Definition); break;
        }
        RefreshPathsChip();
    }

    // ---------------------------------------------------------------- the overlay: guides with time, contacts, range tests

    /// <summary>
    /// What the 3D view draws over the scene in Animation mode. While playing (<paramref name="runtimeOnly"/>) only the time, the
    /// player and the range tests follow; the guides and their emphasis are rebuilt when the paths or the selection change.
    /// </summary>
    private void RefreshAnimationOverlay(bool runtimeOnly = false)
    {
        var overlay = AnimationOverlay;
        if (scene != overlayViewport)
        {
            if (overlayViewport != null) overlayViewport.ViewChanged -= overlay.InvalidateVisual;
            overlayViewport = scene; overlayGuidesKey = "";
            if (scene != null) scene.ViewChanged += overlay.InvalidateVisual;
        }
        overlay.Project ??= ProjectOverlay;
        bool mode = InAnimationMode && sourceChromeActive && scene != null && SceneHost.Visibility == Visibility.Visible && flyCamera?.IsActive != true;
        if (!mode || animationMode is not { } target)
        {
            if (!overlay.IsEmpty) { overlay.SetGuides([], 0); overlay.SetMarkers([]); overlay.SetRanges([], null); }
            overlayGuidesKey = "";
            return;
        }
        double time = worldAnimationSession?.State.Time ?? 0;
        if (!runtimeOnly)
        {
            // The emphasis a selected motion event gives its guide: the nodes the selection names (alpha only).
            HashSet<int>? named = animationGraphSelection != null && animationGraphOutline is { Length: > 0 } outline && animationGraphLayout?.Graph.Row(animationGraphSelection) != null
                ? [.. outline] : null;
            var shown = shownAnimationPaths.Where(d => sourceAnimationPaths.TryGetValue(d, out var c) && c.Document == shownDocument && c.Revision == shownDocument?.Revision).OrderBy(d => d).ToArray();
            string key = string.Join(",", shown.Select(d => sourceAnimationPaths[d].Id)) + "|" + (named == null ? "" : string.Join(",", named.Order())) + "|" + target.Definition;
            if (key != overlayGuidesKey)
            {
                overlayGuidesKey = key;
                List<AnimationGuideTrack> tracks = [];
                foreach (int definition in shown)
                {
                    var paths = sourceAnimationPaths[definition].Analysis.Paths;
                    bool any = named != null && definition == target.Definition && paths.Any(p => named.Contains(p.Node));
                    foreach (var path in paths) tracks.Add(new(path, definition == target.Definition && (!any || named!.Contains(path.Node))));
                }
                overlay.SetGuides(tracks, time);
                if (shown.Contains(target.Definition)) scene!.EmphasizeAnimationPaths(target.Definition.ToString(CultureInfo.InvariantCulture), named);
            }
            var state = PathsState();
            overlay.SetMarkers(showPathMarkers && state.Compatible && state.Analysis is { } analysis ? analysis.Markers : []);
        }
        overlay.SetTime(time);
        var tests = RangeTests();
        overlay.SetRanges(tests, tests.Count > 0 && PlayerNow() is { Position: Vector3 at } player ? new(at, player.Source, player.Frozen) : null);
    }
    private (Point Screen, bool InFront)? ProjectOverlay(Vector3 point)
    {
        if (scene is not { } viewport || !viewport.ProjectToView(point, out var screen, out bool front)) return null;
        try { return (viewport.RenderSurface.TranslatePoint(screen, AnimationOverlay), front); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>One range test of the shown graph: a PLAYER_RANGE question row or a range-gated activation, with its attach node.</summary>
    private sealed record RangeTestGeometry(string Id, int Entry, double Radius, bool Gate, string Attach, Vector3? Center, string Where);
    /// <summary>The range test a graph row (a PLAYER_RANGE question) or an activation node (a range gate) draws; null for anything else.</summary>
    private RangeTestGeometry? RangeOf(string id)
    {
        if (animationGraphLayout is not { } layout) return null;
        var graph = layout.Graph;
        double radius; int entry; bool gate; string where;
        if (graph.Row(id) is { Role: AnimationGraphRowRole.Question } row && ConditionOf(graph, row) is { Operand: "playerRange", Radius: double r })
        { radius = r; entry = row.Entry; gate = false; where = (graph.Node(AnimationGraph.SequenceId(row.Entry, row.Sequence))?.Label ?? "sequence") + " › " + row.Label; }
        else if (layout.Node(id)?.Node.Activation is { } activation && activation.Triggers.FirstOrDefault(t => t.Kind == "rangeGate") is { Value: double value })
        { radius = value; entry = activation.Entry; gate = true; where = "Activation · range gate"; }
        else return null;
        if (entry <= 0 || entry >= graph.Index.Package.Entries.Count) return null;
        var binding = graph.Index.Package.Entries[entry];
        return new(id, entry, radius, gate, binding.AttachName, AttachCenter(entry, gate ? null : graph.Row(id)?.Event), where);
    }
    /// <summary>
    /// Where a range test of <paramref name="entry"/> measures from: its callback node where the run has it now (a node without a
    /// model has no pose, so the run reports it), else where the latest pass of <paramref name="condition"/> measured from, else
    /// where the map places the binding's attach node.
    /// </summary>
    private Vector3? AttachCenter(int entry, Guid? condition)
    {
        var frame = worldAnimationSession?.CurrentFrame ?? worldAnimationSession?.LastFrame;
        if (frame != null)
        {
            // The instance whose latest pass tested the condition, when it is still held; else the binding's latest instance.
            var latest = condition is Guid ev ? frame.Conditions.LastOrDefault(c => c.Event == ev && c.Center != null) : null;
            var attach = (latest != null ? frame.Attaches.FirstOrDefault(a => a.Instance == latest.Instance) : null)
                ?? frame.Attaches.Where(a => a.Entry == entry).MaxBy(a => a.Instance);
            if (attach != null) return attach.Position;
            if (latest?.Center is Vector3 measured) return measured;
        }
        if (animationGraphIndex?.Package is not { } package || entry <= 0 || entry >= package.Entries.Count) return null;
        var binding = package.Entries[entry];
        if (scene?.Mission is { } mission && scene.PreviewScene is { } data && mission.AnimationEntryRoots.TryGetValue(entry, out int root) && root >= 0 && root < data.Nodes.Count)
        {
            int node = binding.AttachName == binding.RootName ? root : FindBelow(data, root, binding.AttachName) ?? root;
            return zStudio.Rendering.SceneViewport.WorldTransform(data, node).Translation;
        }
        return null;
    }
    /// <summary>Every range condition and range gate of the opened animation and its expanded calls (the graph's shown frames).</summary>
    private List<RangeTestGeometry> AllRanges()
    {
        List<RangeTestGeometry> found = [];
        if (animationGraphLayout is not { } layout) return found;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var placement in layout.Nodes)
        {
            var node = placement.Node;
            if (node.Activation != null && RangeOf(node.Id) is { } gate && seen.Add(gate.Id)) found.Add(gate);
            if (node.Sequence is not { } sequence) continue;
            foreach (var branch in layout.Graph.Blocks(sequence.Entry, sequence.Id).SelectMany(b => b.Branches))
                if (branch.Condition is { Operand: "playerRange" } && branch.Marker is Guid marker && layout.Graph.RowOf(sequence.Entry, marker) is { } row && seen.Add(row.Id) && RangeOf(row.Id) is { } test)
                    found.Add(test);
            if (found.Count >= 256) break;
        }
        return found;
    }
    /// <summary>The range tests the view draws: the selected or hovered one, or with Show range tests every one (the selected and hovered emphasised).</summary>
    private List<AnimationRangeTest> RangeTests()
    {
        List<AnimationRangeTest> tests = [];
        if (!InAnimationMode || animationGraphLayout == null) return tests;
        var player = PlayerNow();
        string Label(RangeTestGeometry range)
        {
            if (player.Position is not Vector3 at || range.Center is not Vector3 center) return player.Position == null ? "no player → " + (range.Gate ? "never runs" : "false") : "";
            double distance = Vector3.Distance(center, at);
            bool inside = distance <= range.Radius;
            return $"{distance.ToString("N0", CultureInfo.InvariantCulture)} · {(inside ? "inside" : "outside")} {AnimationPhrases.Number(range.Radius)} → " + (range.Gate ? inside ? "runs" : "paused" : inside ? "true" : "false");
        }
        HashSet<string> emphasized = new(StringComparer.Ordinal);
        foreach (string? id in new[] { animationGraphSelection, animationGraphHover })
            if (id != null && RangeOf(id) is { } range) emphasized.Add(range.Id);
        var ranges = showRangeTests ? AllRanges() : emphasized.Select(RangeOf).OfType<RangeTestGeometry>().ToList();
        foreach (var range in ranges)
            if (range.Center is Vector3 center)
                tests.Add(new(range.Id, center, range.Radius, range.Attach, range.Gate) { Emphasized = emphasized.Contains(range.Id), Label = Label(range) });
        return tests;
    }

    /// <summary>The player reference at the playhead: the run's (at start, moved by callbacks 10 and 17, frozen by 11), else as the scenario sets it.</summary>
    private (Vector3? Position, string Source, bool Frozen, double Since, bool Evaluated) PlayerNow()
    {
        var frame = worldAnimationSession?.CurrentFrame ?? worldAnimationSession?.LastFrame;
        if (frame?.PlayerReference is { } reference) return (reference.Position, reference.Source, reference.Frozen, reference.Time, true);
        var scenario = worldAnimationConfiguration;
        return scenario.Player.Kind switch
        {
            AnimationPlayerReferenceKind.Custom => (scenario.CustomPlayer, "custom", false, 0, false),
            AnimationPlayerReferenceKind.None => (null, "none", false, 0, false),
            _ => (scene?.Mission?.PlayerStart, scene?.Mission?.PlayerStart == null ? "no mission start" : "aiv start", false, 0, false),
        };
    }

    // ---------------------------------------------------------------- MCP

    /// <summary>animation_paths ranges: every range condition and gate of the opened animation and its expanded calls, measured from the player now.</summary>
    private object PathRanges()
    {
        if (!InAnimationMode) throw new StudioCommandException("not_ready", "Range tests belong to the animation opened in Animation mode (animation_mode open).");
        RefreshAnimationGraph();
        var player = PlayerNow();
        var package = animationGraphLayout?.Graph.Index.Package;
        var shown = AnimationOverlay.Ranges.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        return new
        {
            player = PlayerJson(player),
            shown = showRangeTests ? "all" : shown.Count > 0 ? "selection" : "none",
            ranges = AllRanges().Take(256).Select(r =>
            {
                double? distance = r.Center is Vector3 c && player.Position is Vector3 p ? Vector3.Distance(c, p) : null;
                bool? inside = distance is double d ? d <= r.Radius : player.Position == null ? false : null;
                return new
                {
                    id = r.Id, kind = r.Gate ? "gate" : "condition", entry = r.Entry, name = package != null && r.Entry < package.Entries.Count ? package.Entries[r.Entry].Name : null,
                    where = r.Where, attach = r.Attach, center = r.Center is Vector3 at ? new[] { at.X, at.Y, at.Z } : null, radius = r.Radius, distance, inside,
                    outcome = inside is bool value ? r.Gate ? value ? "runs" : "paused" : value ? "true" : "false" : null, drawn = shown.Contains(r.Id),
                };
            }).ToArray(),
        };
    }
    private static object PlayerJson((Vector3? Position, string Source, bool Frozen, double Since, bool Evaluated) player) => new
    {
        position = player.Position is Vector3 p ? new[] { p.X, p.Y, p.Z } : null, source = player.Source, frozen = player.Frozen, since = player.Since, evaluated = player.Evaluated,
    };
    /// <summary>animation_runtime player: the player reference at the playhead with its source, whether it is frozen, and every change of the run.</summary>
    private object RuntimePlayer(int offset, int limit)
    {
        var frame = worldAnimationSession?.CurrentFrame ?? worldAnimationSession?.LastFrame;
        var now = PlayerNow();
        var history = frame?.PlayerReferences ?? [];
        var shown = history.Skip(offset).Take(limit).Select(h => new
        {
            time = h.Time, position = h.Position is Vector3 p ? new[] { p.X, p.Y, p.Z } : null, source = h.Source, frozen = h.Frozen, @event = h.Event, instance = h.Instance,
            label = h.Source + (h.Time > 0 || h.Event != null ? " at " + AnimationSeconds.Format(h.Time) : "") + (h.Frozen ? " · frozen" : ""),
        }).ToArray();
        return new
        {
            time = frame?.Time, stale = worldAnimationSession?.CurrentFrame == null && frame != null,
            scenario = worldAnimationConfiguration.Player.Kind switch { AnimationPlayerReferenceKind.Custom => "custom", AnimationPlayerReferenceKind.None => "none", _ => "atStart" },
            player = PlayerJson(now),
            label = now.Source + (now.Since > 0 ? " at " + AnimationSeconds.Format(now.Since) : "") + (now.Frozen ? " · frozen" : ""),
            changes = new { total = history.Count, dropped = frame?.PlayerReferencesDropped ?? 0, offset, nextOffset = offset + shown.Length < history.Count ? (int?)(offset + shown.Length) : null, items = shown },
        };
    }
    /// <summary>The chip's state as animation_paths reports it.</summary>
    private object PathsChipJson()
    {
        var state = PathsState();
        return new { state = state.Word, status = state.Status, busy = state.Busy, cached = state.Cached, compatible = state.Compatible, shown = state.Shown, rangeTests = showRangeTests, contacts = showPathMarkers,
            markerCount = state.Analysis?.Markers.Count ?? 0 };
    }
    /// <summary>animation_paths overlay: Show range tests and Contacts and impacts (the chip's toggles; presentation only).</summary>
    private void PathsOverlayOptions(JsonObject args)
    {
        if (!InAnimationMode) throw new StudioCommandException("not_ready", "The Paths chip belongs to Animation mode (animation_mode open).");
        if (!args.ContainsKey("rangeTests") && !args.ContainsKey("contacts")) throw new StudioCommandException("invalid_argument", "Supply rangeTests or contacts.");
        if (args.ContainsKey("rangeTests")) showRangeTests = Flag(args, "rangeTests");
        if (args.ContainsKey("contacts")) showPathMarkers = Flag(args, "contacts");
        RefreshAnimationOverlay(); RefreshPathsChip();
    }
    /// <summary>Leaving Animation mode hides the shown guides (their evaluated paths stay cached for Show guides).</summary>
    private void HideAllAnimationGuides()
    {
        foreach (int definition in shownAnimationPaths.ToArray()) scene?.ClearAnimationPaths(definition.ToString(CultureInfo.InvariantCulture));
        shownAnimationPaths.Clear(); overlayGuidesKey = "";
    }
}

/// <summary>A small spinning ring (the Paths chip while paths evaluate).</summary>
internal sealed class AnimationBusyRing : FrameworkElement
{
    private readonly RotateTransform spin = new();
    internal AnimationBusyRing()
    {
        RenderTransform = spin; RenderTransformOrigin = new Point(.5, .5);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(.9)) { RepeatBehavior = RepeatBehavior.Forever });
            else spin.BeginAnimation(RotateTransform.AngleProperty, null);
        };
        AutomationProperties.SetName(this, "Evaluating paths");
    }
    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 2) return;
        var accent = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
        var arc = new StreamGeometry();
        double r = size / 2 - 1.5; Point c = new(ActualWidth / 2, ActualHeight / 2);
        using (var g = arc.Open())
        {
            g.BeginFigure(new Point(c.X + r, c.Y), false, false);
            g.ArcTo(new Point(c.X, c.Y + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            g.ArcTo(new Point(c.X - r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, new Pen(accent, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
    }
}
