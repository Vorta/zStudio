using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>
/// Animation mode's legend (ⓘ in the panel's view bar, design §18): the edges, the Timing marks, the run states and the event
/// families, as the mockup's legend draws them. <see cref="Entries"/> is its content as text (MCP help topic=animation-mode).
/// </summary>
internal static class AnimationLegend
{
    internal static readonly (string Kind, string Text)[] Edges =
    [
        ("call", "Call (×n when grouped)"), ("callWait", "Call and wait (double line, \"waits\")"), ("local", "Call as local child (stops with caller)"),
        ("release", "Release a waiting sequence"), ("contact", "Release on ground contact"), ("stopSequence", "Stop sequence"),
        ("stopAnimation", "Stop / reset animation (first with that name)"), ("invalidate", "Invalidate: no later start"), ("prerequisite", "Prerequisite (needed)"),
        ("backLink", "Back-link: calls an animation already drawn (cycle)"), ("excluded", "Excluded call path (hollow port)"),
    ];
    internal static readonly (string Kind, string Text)[] Marks =
    [
        ("instant", "Executed instant (colour = family)"), ("duration", "Executed with duration; fades out when still running"),
        ("condition", "Condition evaluated (T/F); accent outline = forced"), ("skipped", "Skipped by a condition"), ("excludedMark", "Call reached, call path excluded"),
        ("refused", "Start refused (reason in the tooltip)"), ("waitRelease", "Waiting for release"), ("waitThreshold", "Waiting for threshold"),
        ("paused", "Paused by a range or zone gate (clocks frozen)"),
    ];
    internal const string SequenceStates = "Running · ✓ Complete · ⏸ Waiting for release · ◷ +0.1 s (waiting for threshold) · ⊘ Blocked · ○ Not started";
    internal const string BindingStates = "Binding: Not started · Running · Running, invalidated · Finished, pose kept · Stopped · Invalidated · Paused · Refused";
    internal static readonly (string Glyph, string Text)[] Families =
    [
        ("state", "Node state"), ("motion", "Motion, keyframes"), ("camera", "Camera"), ("sound", "Sound"), ("effect", "Effect, light, fog, screen"),
        ("call", "Call, flow, game callback"), ("release", "Release"), ("stop", "Stop, reset, invalidate"),
    ];
    /// <summary>The legend as text, section by section.</summary>
    internal static object Entries => new
    {
        edges = Edges.Select(e => new { kind = e.Kind, text = e.Text }).ToArray(),
        marks = Marks.Select(m => new { kind = m.Kind, text = m.Text }).ToArray(),
        states = new[] { SequenceStates, BindingStates },
        families = Families.Select(f => new { glyph = f.Glyph, text = f.Text }).ToArray(),
        notices = "⚠ n on a node or lane: its notices (approximations, support notes, ignored prerequisites, refused starts, unavailable calls, preview limits); the view bar's ⚠ chip lists every notice of the opened animation.",
    };

    /// <summary>The legend's flyout content.</summary>
    internal static FrameworkElement Create(bool dark)
    {
        var p = AnimationGraphPalette.For(dark);
        StackPanel body = new() { Margin = new(14, 12, 14, 12), Width = 330 };
        Heading("Edges", 0);
        foreach (var (kind, text) in Edges) body.Children.Add(Line(new Sample(kind, p) { Width = 72, Height = 16 }, text));
        Heading("Timing marks", 10);
        foreach (var (kind, text) in Marks) body.Children.Add(Line(new Sample(kind, p) { Width = 44, Height = 14 }, text));
        Heading("States", 10);
        body.Children.Add(new TextBlock { Text = SequenceStates, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 4), Foreground = p.T2 });
        body.Children.Add(new TextBlock { Text = BindingStates, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = p.T3 });
        Heading("Families", 10);
        WrapPanel families = new();
        foreach (var (glyph, text) in Families)
        {
            StackPanel item = new() { Orientation = Orientation.Horizontal, Margin = new(0, 2, 10, 2) };
            var icon = new AnimationGraphGlyph { Glyph = glyph, Size = 14, Margin = new(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            TextElement.SetForeground(icon, glyph switch
            {
                "state" => p.State, "motion" => p.Motion, "camera" => p.Camera, "sound" => p.Sound, "effect" => p.Effect, "release" => p.Release, "stop" => p.Stop, _ => p.Call,
            });
            item.Children.Add(icon);
            item.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = p.T2 });
            families.Children.Add(item);
        }
        body.Children.Add(families);
        TextBlock keys = new() { Text = "Help → Controls and formats lists every key and pointer action.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0), Foreground = p.T3 };
        body.Children.Add(keys);
        Border frame = new() { Child = body, CornerRadius = new(8), BorderThickness = new(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        AutomationProperties.SetName(frame, "Animation mode legend");
        return frame;

        void Heading(string text, double top) => body.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new(0, top, 0, 6), Foreground = p.T1 });
        UIElement Line(FrameworkElement sample, string text)
        {
            DockPanel line = new() { Margin = new(0, 2, 0, 2) };
            sample.Margin = new(0, 0, 10, 0); sample.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(sample);
            line.Children.Add(new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = p.T2 });
            AutomationProperties.SetName(line, text);
            return line;
        }
    }

    /// <summary>One drawn sample: an edge line with its end, or a Timing mark.</summary>
    private sealed class Sample(string kind, AnimationGraphPalette p) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, y = h / 2;
            Pen Line(Brush brush, double thickness, params double[] dashes)
            {
                Pen pen = new(brush, thickness);
                if (dashes.Length > 0) pen.DashStyle = new DashStyle(dashes.Select(d => d / thickness), 0);
                return pen;
            }
            void Arrow(Brush brush, bool open)
            {
                var geometry = Geometry.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"M{w - 12},{y - 4} L{w - 4},{y} L{w - 12},{y + 4}") + (open ? "" : " Z"));
                if (open) dc.DrawGeometry(null, new Pen(brush, 1.6), geometry); else dc.DrawGeometry(brush, null, geometry);
            }
            switch (kind)
            {
                case "call": dc.DrawLine(Line(p.Call, 1.5), new(4, y), new(w - 10, y)); Arrow(p.Call, false); break;
                case "callWait": dc.DrawLine(Line(p.Call, 1.5), new(4, y - 2), new(w - 10, y - 2)); dc.DrawLine(Line(p.Call, 1.5), new(4, y + 2), new(w - 10, y + 2)); Arrow(p.Call, false); break;
                case "local": dc.DrawRectangle(p.Call, null, new Rect(2, y - 3, 6, 6)); dc.DrawLine(Line(p.Call, 1.5), new(8, y), new(w - 10, y)); Arrow(p.Call, false); break;
                case "release": dc.DrawLine(Line(p.Release, 1.6), new(4, y), new(w - 6, y)); Arrow(p.Release, true); break;
                case "contact": dc.DrawLine(Line(p.Release, 1.6, 1.5, 3), new(4, y), new(w - 6, y)); Arrow(p.Release, true); break;
                case "stopSequence": dc.DrawLine(Line(p.Stop, 1.5), new(4, y), new(w - 6, y)); dc.DrawLine(Line(p.Stop, 2), new(w - 6, y - 5), new(w - 6, y + 5)); break;
                case "stopAnimation": dc.DrawLine(Line(p.Stop, 1.5, 5, 3), new(4, y), new(w - 6, y)); dc.DrawLine(Line(p.Stop, 2), new(w - 6, y - 5), new(w - 6, y + 5)); break;
                case "invalidate":
                    dc.DrawLine(Line(p.Stop, 1.5, 5, 3), new(4, y), new(w - 13, y));
                    dc.DrawEllipse(p.Pane, Line(p.Stop, 1.5), new(w - 8, y), 4.5, 4.5); dc.DrawLine(Line(p.Stop, 1.5), new(w - 11, y - 3), new(w - 5, y + 3)); break;
                case "prerequisite":
                    dc.DrawLine(Line(p.Prerequisite, 1.2, 1.5, 3), new(4, y), new(w - 12, y));
                    dc.DrawGeometry(p.Prerequisite, null, Geometry.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"M{w - 12},{y} L{w - 8},{y - 3} L{w - 4},{y} L{w - 8},{y + 3} Z"))); break;
                case "backLink":
                    dc.DrawLine(Line(p.Call, 1.5, 6, 4), new(4, y), new(w - 10, y)); Arrow(p.Call, false);
                    AnimationGraphGlyphs.Draw(dc, "back", new(w / 2 - 6, y - 12), 10, p.T2); break;
                case "excluded":
                    dc.PushOpacity(.35); dc.DrawLine(Line(p.Call, 1.5), new(12, y), new(w - 10, y)); Arrow(p.Call, false); dc.Pop();
                    dc.DrawEllipse(p.Pane, Line(p.Call, 1.5), new(8, y), 4.5, 4.5); break;
                case "instant": Diamond(new(9, y), 4.5, p.State, null); break;
                case "duration":
                    {
                        Color motion = p.Motion is SolidColorBrush solid ? solid.Color : Colors.Teal;
                        LinearGradientBrush fade = new(motion, Color.FromArgb(0, motion.R, motion.G, motion.B), 0);
                        dc.DrawRoundedRectangle(fade, null, new Rect(2, y - 4, w - 4, 8), 2, 2); break;
                    }
                case "condition":
                    Diamond(new(9, y), 5, null, Line(p.T2, 1.5));
                    dc.DrawText(new FormattedText("F", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, p.Strong, 8, p.T2, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(6.5, y - 5.5)); break;
                case "skipped":
                    dc.PushOpacity(.6); Diamond(new(9, y), 5, null, Line(p.T3, 1.5)); dc.DrawLine(Line(p.T2, 1.2), new(5, y + 4), new(13, y - 4)); dc.Pop(); break;
                case "excludedMark": dc.DrawRectangle(null, Line(p.Call, 1.5), new Rect(5, y - 4, 8, 8)); break;
                case "refused": dc.DrawEllipse(null, Line(p.Stop, 1.5), new(9, y), 5, 5); dc.DrawLine(Line(p.Stop, 1.5), new(5.5, y - 3.5), new(12.5, y + 3.5)); break;
                case "waitRelease": Hatch(p.Release, 135); break;
                case "waitThreshold":
                    for (double dx = 2.5; dx < w; dx += 5) for (double dy = y - 4.5; dy <= y + 5; dy += 5) dc.DrawEllipse(p.T3, null, new Point(dx, dy), 1, 1);
                    break;
                case "paused": Hatch(p.T3, 45); break;
            }
            void Diamond(Point c, double r, Brush? fill, Pen? pen) =>
                dc.DrawGeometry(fill, pen, Geometry.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"M{c.X},{c.Y - r} L{c.X + r},{c.Y} L{c.X},{c.Y + r} L{c.X - r},{c.Y} Z")));
            void Hatch(Brush brush, double angle)
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, y - 6, w, 12)));
                dc.PushOpacity(.6);
                for (double x = -12; x < w + 12; x += 6)
                    dc.DrawLine(new Pen(brush, 1.5), angle == 45 ? new(x, y + 6) : new(x, y - 6), angle == 45 ? new(x + 12, y - 6) : new(x + 12, y + 6));
                dc.Pop(); dc.Pop();
            }
        }
    }
}
