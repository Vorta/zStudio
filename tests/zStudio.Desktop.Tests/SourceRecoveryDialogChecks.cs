using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Actual journal descriptions cannot push recovery decisions out of the bounded dialog viewport.</summary>
internal static class SourceRecoveryDialogChecks
{
    internal static async Task Run(MainWindow owner)
    {
        using var fixture = new SourceWorldFixture();
        string description = string.Join('\n', Enumerable.Repeat("x", 512));
        string[] paths = [.. Enumerable.Range(0, 12).Select(i => $"gamegen/{new string('q', 180)}/file{i:D2}.gs")];
        var publisher = new SourcePublisher(fixture.Project) { Fault = (step, _) => { if (step == "prepared") throw new SourcePublisher.Crash(); } };
        Assert.Throws<SourcePublisher.Crash>(() => publisher.Publish(paths.Select(path => new SourceFileWrite(path, null, Encoding.ASCII.GetBytes("# note"))).ToArray(), description, TestContext.Current.CancellationToken));
        var interrupted = Assert.Single(new SourcePublisher(fixture.Project).FindInterrupted(TestContext.Current.CancellationToken));
        Assert.Equal(description, interrupted.Description); Assert.Equal(12, interrupted.Files.Count); Assert.False(interrupted.Committed);

        foreach (var available in new[] { new Size(1280, 720), new Size(1920, 1080), new Size(320, 360) })
            await Check(interrupted, available, expectsScroll: true, "Later");
        var ordinary = interrupted with { Description = "Save source scripts", Files = [interrupted.Files[0]] };
        await Check(ordinary, new(1280, 720), expectsScroll: false, "Roll back");
        await Check(ordinary, new(1280, 720), expectsScroll: false, "Complete");
        await Check(ordinary with { Committed = true }, new(1280, 720), expectsScroll: false, "Keep files");
        // The test invokes only presentation callbacks; the real journal is still intact until explicit recovery.
        Assert.Equal(description, Assert.Single(new SourcePublisher(fixture.Project).FindInterrupted(TestContext.Current.CancellationToken)).Description);
        Assert.True(new SourcePublisher(fixture.Project).Resolve(interrupted.SaveId, SourceRecoveryAction.RollBack, TestContext.Current.CancellationToken).Resolved);

        async Task Check(SourceRecoveryCase recovery, Size available, bool expectsScroll, string choose)
        {
            string? selected = null;
            Window dialog = owner.CreateSourceRecoveryDialog(recovery, label => selected = label, available);
            dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -12000; dialog.Top = 0; dialog.ShowActivated = false;
            try
            {
                dialog.Show(); dialog.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert.Same(owner, dialog.Owner);
                Assert.InRange(dialog.ActualWidth, 1, available.Width); Assert.InRange(dialog.ActualHeight, 1, available.Height);
                var layout = Assert.IsType<Grid>(dialog.Content);
                var scroll = Assert.Single(layout.Children.OfType<ScrollViewer>());
                var details = Assert.IsType<TextBlock>(scroll.Content);
                Assert.Contains(recovery.Description, details.Text);
                Assert.Contains(MainWindow.RecoveryFilesText(recovery.Files, detailed: true), details.Text);
                var buttons = Assert.Single(layout.Children.OfType<WrapPanel>()).Children.OfType<Button>().ToArray();
                Assert.Equal(recovery.Committed ? ["Complete", "Keep files", "Later"] : new[] { "Roll back", "Complete", "Keep files", "Later" }, buttons.Select(b => (string)b.Content));
                Assert.True(buttons.Single(b => (string)b.Content == "Later").IsCancel);
                Assert.True(scroll.ViewportHeight > 0);
                Assert.Equal(expectsScroll, scroll.ScrollableHeight > 0);
                Rect[] before = Bounds(); AssertVisible(before);
                scroll.ScrollToBottom(); scroll.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                if (expectsScroll) Assert.InRange(scroll.VerticalOffset, scroll.ScrollableHeight - 0.5, scroll.ScrollableHeight + 0.5);
                Assert.Equal(before, Bounds()); AssertVisible(Bounds());
                buttons.Single(b => (string)b.Content == choose).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(choose, selected); Assert.False(dialog.IsVisible);

                Rect[] Bounds() => [.. buttons.Select(b => b.TransformToAncestor(layout).TransformBounds(new Rect(b.RenderSize)))];
                void AssertVisible(IEnumerable<Rect> bounds)
                {
                    foreach (var box in bounds)
                    {
                        Assert.True(box.Width > 0 && box.Height > 0);
                        Assert.True(box.Left >= -0.5 && box.Top >= -0.5 && box.Right <= layout.ActualWidth + 0.5 && box.Bottom <= layout.ActualHeight + 0.5,
                            $"Decision bounds {box} exceed the dialog client {layout.RenderSize}.");
                    }
                }
            }
            finally { dialog.Close(); }
        }
    }
}
