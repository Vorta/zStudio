using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class SceneInspectionInputChecks
{
    internal static async Task Run()
    {
        using var scene = new SceneViewport();
        var selected = new SceneInspection("fixture", 0, -1, -1, null, null, null, null, null, true, null);
        typeof(SceneViewport).GetProperty(nameof(SceneViewport.SelectedInspection))!.SetValue(scene, selected);
        bool overflow = true;
        var card = new SceneInspectionCard(scene, _ =>
        {
            JsonObject info = new() { ["Node"] = "Tank fixture", ["Editable"] = false };
            if (overflow) for (int i = 0; i < 30; i++) info["Field " + i] = "Detailed inspection value";
            return info;
        }, _ => { }, _ => { });
        scene.InspectionContent = card;
        var window = new Window { Content = scene, Width = 900, Height = 600, Left = -12000, ShowInTaskbar = false };
        using var document = new DocumentModel(new ZbdDocument("fixture.zbd", new(0, DateTime.MinValue),
            new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty));
        try
        {
            window.Show(); card.Refresh(); await Idle();
            scene.RestoreView(new(new(0, 0, 10), new(0, 0, -10), new(0, 1, 0), 60));
            scene.SelectFramingNode(0);
            var before = scene.CaptureView();
            int navigationStarts = 0;
            scene.ManualNavigationStarting += () => navigationStarts++;
            var scroll = Descendants(card).OfType<ScrollViewer>().Single();
            Assert.True(scroll.ScrollableHeight > 0);
            var text = Descendants(scroll).OfType<TextBlock>().First();
            await Wheel(text, -120); Assert.True(scroll.VerticalOffset > 0);
            var bar = Descendants(scroll).OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
            double offset = scroll.VerticalOffset;
            await Wheel(bar, -120); Assert.True(scroll.VerticalOffset > offset);
            offset = scroll.VerticalOffset;
            ScrollBar.PageDownCommand.Execute(null, bar); await Idle(); Assert.True(scroll.VerticalOffset > offset);
            scroll.ScrollToBottom(); await Idle(); await Wheel(bar, -120);
            Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset);
            scroll.ScrollToTop(); await Idle(); await Wheel(text, 120); Assert.Equal(0, scroll.VerticalOffset);
            await Wheel((Border)scroll.Parent, -120); // Card padding consumes wheel without reaching the scene.

            card.StartDraft(document, new("fixture.zbd", 0, "fixture", 0), new(1, 2, 3));
            card.Refresh(); await Idle();
            var input = Descendants(card).OfType<TextBox>().First();
            await Wheel(input, -120); Assert.True(scroll.VerticalOffset > 0);
            Assert.Equal("1", input.Text); Assert.True(card.HasDraft);
            // Fail before CaptureMouse if a regression starts a camera gesture.
            void RejectCameraGesture() => throw new InvalidOperationException("Card input reached camera navigation");
            scene.ManualNavigationStarting += RejectCameraGesture;
            try
            {
                input.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = Mouse.PreviewMouseDownEvent });
                bar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            }
            finally { scene.ManualNavigationStarting -= RejectCameraGesture; }
            card.CancelDraft(); overflow = false; card.Refresh(); await Idle();
            Assert.Equal(0, scroll.ScrollableHeight); await Wheel(Descendants(scroll).OfType<TextBlock>().First(), -120);
            Assert.Equal(0, navigationStarts); Assert.Equal(before, scene.CaptureView());
            Assert.Equal(0, scene.FramingSelection); Assert.Same(selected, scene.SelectedInspection);
            Assert.Equal(default, (Vector)typeof(SceneViewport).GetField("navigationVelocity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!);
            Assert.Null(Mouse.Captured);
            // Wheel navigation outside the card still uses the normal camera handler.
            await Wheel(scene.RenderSurface, -120);
            Assert.True(navigationStarts > 0); Assert.NotEqual(before.Position, scene.CaptureView().Position);
            scene.StopCameraMotion();
        }
        finally { card.CancelDraft(); window.Close(); }

        static async Task Wheel(UIElement source, int delta)
        {
            var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            source.RaiseEvent(e); e.RoutedEvent = Mouse.MouseWheelEvent; source.RaiseEvent(e);
            Assert.True(e.Handled); await Idle();
        }
    }
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
