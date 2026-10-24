using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>Runs on the existing private WPF fixture dispatcher; never opens or captures an owner's surface.</summary>
internal static class WorldAnimationTimelineChecks
{
    public static Task Run()
    {
        var threshold = new WorldAnimationThresholdEditor();
        List<double> proposals = []; threshold.ValueProposed += proposals.Add;
        List<string> edits = []; threshold.TextEdited += edits.Add;
        const double authored = -1.23456789;
        threshold.SetValue("EVENT_OFFSET", authored);
        threshold.Measure(new Size(320, 240)); threshold.Arrange(new Rect(0, 0, 320, 240)); threshold.UpdateLayout();
        Assert.Equal(authored, threshold.Value); Assert.Empty(proposals); Assert.Empty(edits);
        var numeric = Descendants(threshold).OfType<TextBox>().Single();
        numeric.Text = "-";
        Assert.Equal("-", Assert.Single(edits)); Assert.Empty(proposals);
        numeric.Text = "-2.25";
        Assert.Equal("-2.25", edits[1]); Assert.Empty(proposals);
        threshold.SetValue("EVENT_OFFSET", authored);
        Assert.Equal(2, edits.Count);
        var ruler = Descendants(threshold).OfType<FrameworkElement>().Single(e => AutomationProperties.GetName(e) == "Native source-clock threshold ruler");
        var peer = UIElementAutomationPeer.CreatePeerForElement(ruler)!;
        var value = Assert.IsAssignableFrom<IValueProvider>(peer.GetPattern(PatternInterface.Value));
        value.SetValue("-2.000123");
        Assert.Equal(-2.000123, Assert.Single(proposals)); Assert.Equal(authored, threshold.Value);
        threshold.SetValue("EVENT_OFFSET", -2.000123, false);
        Assert.Throws<ElementNotEnabledException>(() => value.SetValue("3"));

        var timeline = new WorldAnimationTimeline();
        int seeks = 0, inclusions = 0; timeline.SeekRequested += _ => seeks++; timeline.InclusionRequested += (_, _) => inclusions++;
        timeline.SetState(new() { Target = "start_north", CanPlay = true, CanSeek = true, RangeEnd = 30 });
        var source = Enumerable.Range(0, 14).Select(i => new WorldAnimationTimelineRow("call:" + i, "child " + i) { Included = false, TimingLabel = "ANIMATION_OFFSET ≥ 0 (default)" }).ToArray();
        timeline.SetRows(source, source); timeline.SelectRow(source[9].Id); timeline.SetView("run"); timeline.SetView("source");
        timeline.SetVisibleRange(4, 8);
        Assert.Equal(30, timeline.State.RangeEnd); Assert.Equal(4, timeline.PresentationState.VisibleStart); Assert.Equal(source[9].Id, timeline.SelectedRowId);
        Assert.Equal(0, seeks); Assert.Equal(0, inclusions);
        int selected = 0, rowSelections = 0; timeline.OccurrenceSelected += (_, _) => selected++; timeline.RowSelected += _ => rowSelections++;
        var observed = new WorldAnimationTimelineRow("run:1", "Observed sequence") { Marks = [new("occurrence:1", WorldAnimationTimelineMarkKind.Instant, 1, Occurrence: 1)] };
        timeline.SetRows(source, [observed]); timeline.SetView("run");
        timeline.SetState(timeline.State with { EvidenceIdentity = "run:old" });
        timeline.SetRows(source, [observed with { Label = "Updated snapshot" }]);
        Assert.True(timeline.SelectCurrentMark(observed.Id, "occurrence:1", "run:old", "run"));
        Assert.Equal(1, selected); Assert.Equal(0, rowSelections); Assert.Equal("occurrence:1", timeline.SelectedMarkId); Assert.Equal(0, seeks);
        timeline.SetState(timeline.State with { EvidenceIdentity = "run:new" });
        Assert.False(timeline.SelectCurrentMark(observed.Id, "occurrence:1", "run:old", "run"));
        timeline.SelectRow(observed.Id); Assert.Null(timeline.SelectedMarkId);
        timeline.SetView("source");
        timeline.Measure(new Size(360, 300)); timeline.Arrange(new Rect(0, 0, 360, 300)); timeline.UpdateLayout();
        var list = Descendants(timeline).OfType<ListBox>().Single(); Assert.Equal(14, list.Items.Count);
        var timeRuler = Descendants(timeline).OfType<WorldAnimationTimeRuler>().Single();
        var plot = Descendants(timeline).OfType<WorldAnimationRowPlot>().First();
        Assert.Equal(plot.ActualWidth, timeRuler.ActualWidth, 2);
        Assert.Equal(plot.TranslatePoint(new(), timeline).X, timeRuler.TranslatePoint(new(), timeline).X, 2);
        var play = Descendants(timeline).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Play");
        timeline.SetBodyVisible(false); Assert.True(play.IsEnabled); timeline.SetBodyVisible(true);
        Assert.Same(play, Descendants(timeline).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Play"));
        Assert.Equal(0, seeks); Assert.Equal(0, inclusions);
        var surface = new Window { Content = timeline, Width = 1000, Height = 450, ShowInTaskbar = false, ShowActivated = false };
#pragma warning disable WPF0001
        var previousTheme = Application.Current.ThemeMode;
        try
        {
            surface.Show();
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                Application.Current.ThemeMode = theme;
                foreach (double width in new[] { 900d, 396d })
                {
                    timeline.Width = width; timeline.Height = 300;
                    foreach (double labels in new[] { 220d, 300d, 120d, 220d })
                    {
                        timeline.SetLabelWidth(labels); surface.UpdateLayout(); AssertAligned();
                    }
                    timeline.SetRows([], []); timeline.SetView("run"); timeline.SetLabelWidth(300); surface.UpdateLayout();
                    timeline.SetRows(source, source); surface.UpdateLayout(); AssertAligned(); timeline.SetView("source");
                }
            }
        }
        finally { Application.Current.ThemeMode = previousTheme; surface.Content = null; surface.Close(); }
#pragma warning restore WPF0001
        return Task.CompletedTask;
        void AssertAligned()
        {
            var actualRuler = Descendants(timeline).OfType<WorldAnimationTimeRuler>().Single();
            foreach (var actualPlot in Descendants(timeline).OfType<WorldAnimationRowPlot>())
            {
                Assert.Equal(actualPlot.ActualWidth, actualRuler.ActualWidth, 2);
                Assert.Equal(actualPlot.TranslatePoint(new(), timeline).X, actualRuler.TranslatePoint(new(), timeline).X, 2);
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
}
