using System.Reflection;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        bool extraDetail = false;
        var card = new SceneInspectionCard(scene, _ =>
        {
            JsonObject info = new() { ["Node"] = "Tank fixture with a long name which must trim in the fixed header", ["Editable"] = true,
                ["Authored placement XYZ"] = new JsonObject { ["x"] = 1f, ["y"] = 2f, ["z"] = 3f } };
            if (overflow) info["Object world origin XYZ"] = new JsonObject { ["x"] = 100.125f, ["y"] = 200.5f, ["z"] = 300.75f };
            if (overflow) for (int i = 0; i < 30; i++) info["Field " + i] = "Detailed inspection value";
            if (extraDetail) info["Runtime detail"] = "A newly available field";
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
            var scroll = Descendants(card).OfType<ScrollViewer>().Single(s => s.Name == "InspectionScroll");
            var frame = Descendants(card).OfType<Border>().Single(s => s.Name == "InspectionCard");
            Assert.True(scroll.ScrollableHeight > 0);
            var text = Descendants(scroll).OfType<TextBlock>().First();
            await Wheel(text, -120); Assert.True(scroll.VerticalOffset > 0);
            var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            double offset = scroll.VerticalOffset;
            await Wheel(bar, -120); Assert.True(scroll.VerticalOffset > offset);
            offset = scroll.VerticalOffset;
            ScrollBar.PageDownCommand.Execute(null, bar); await Idle(); Assert.True(scroll.VerticalOffset > offset);
            scroll.ScrollToBottom(); await Idle(); await Wheel(bar, -120);
            Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset);
            scroll.ScrollToTop(); await Idle(); await Wheel(text, 120); Assert.Equal(0, scroll.VerticalOffset);
            await Wheel(frame, -120); // Card padding consumes wheel without reaching the scene.

            var authored = Descendants(card).OfType<SceneInspectionField>().Single(f => f.Binding == SceneInspectionBinding.AuthoredPosition);
            var input = authored.Inputs[0];
            var confirm = Descendants(card).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Edit authored XYZ position");
            var cancel = Descendants(card).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Discard position draft");
            var stableControls = Descendants(card).OfType<ValueTextBox>().Cast<FrameworkElement>().Concat(Descendants(card).OfType<Button>()).ToArray();
            // The same controls and positions must survive mode switches, themes,
            // constrained viewport widths and effective 150% display scaling.
#pragma warning disable WPF0001
            var originalTheme = Application.Current.ThemeMode;
            try
            {
                foreach (var theme in new[] { ThemeMode.Dark, ThemeMode.Light })
                foreach (double scale in new[] { 1d, 1.5d })
                foreach (double width in new[] { 900d, 320d })
                {
                    Application.Current.ThemeMode = theme;
                    scene.LayoutTransform = new ScaleTransform(scale, scale);
                    window.Width = width * scale; window.Height = 600 * scale;
                    await Idle(); card.Refresh(); await Idle();
                    scroll.ScrollToVerticalOffset(35); await Idle();
                    Assert.True(input.IsReadOnly); Assert.Equal(Visibility.Hidden, cancel.Visibility);
                    var rectangles = stableControls.Select(c => Bounds(c, frame)).ToArray();
                    var frameBounds = Bounds(frame, card);
                    double scrollOffset = scroll.VerticalOffset;
                    scroll.ScrollToTop(); await Idle(); Capture(frame, $"{theme}-{width}-{scale}-read"); scroll.ScrollToVerticalOffset(scrollOffset); await Idle();
                    card.StartDraft(document, new("fixture.zbd", 0, "fixture", 0), new(1, 2, 3));
                    await Idle();
                    Assert.False(input.IsReadOnly); Assert.Equal(Visibility.Visible, cancel.Visibility);
                    Assert.Equal(frameBounds, Bounds(frame, card));
                    Assert.Equal(scrollOffset, scroll.VerticalOffset);
                    Assert.Equal(rectangles, stableControls.Select(c => Bounds(c, frame)).ToArray());
                    Assert.Same(input, authored.Inputs[0]);
                    Assert.All(Descendants(card).OfType<ValueTextBox>().Except(authored.Inputs), c => Assert.True(c.IsReadOnly));
                    Assert.True(Bounds(confirm, frame).Bottom < Bounds(scroll, frame).Top);
                    double barLeft = Bounds(bar, frame).Left;
                    foreach (var button in Descendants(scroll).OfType<Button>().Where(b => AutomationProperties.GetName(b).StartsWith("Copy ")))
                        Assert.True(Bounds(button, frame).Right <= barLeft - 4, "The scrollbar overlaps a field copy button");
                    scroll.ScrollToTop(); await Idle(); Capture(frame, $"{theme}-{width}-{scale}-edit"); scroll.ScrollToVerticalOffset(scrollOffset); await Idle();
                    card.SetDraft(card.DraftToken, ["-", "2", "3"]); input.Focus(); input.Select(0, 1);
                    await Idle(); scroll.ScrollToVerticalOffset(scrollOffset); await Idle();
                    Application.Current.ThemeMode = theme == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;
                    await Idle();
                    Assert.All(authored.Inputs, c => Assert.True(c.Template.FindName("DeleteButton", c) is not UIElement clear || clear.Visibility == Visibility.Collapsed));
                    Application.Current.ThemeMode = theme; await Idle();
                    extraDetail = true; card.Refresh(); await Idle();
                    Assert.Equal("-", input.Text); Assert.Equal(0, input.SelectionStart); Assert.Equal(1, input.SelectionLength);
                    Assert.Equal(scrollOffset, scroll.VerticalOffset);
                    Assert.Contains("X 1", card.Copy("Authored placement XYZ", false)); // Copy reflects accepted source, never partial input.
                    card.CancelDraft(); extraDetail = false; card.Refresh(); await Idle();
                    Assert.Equal("1", input.Text); Assert.True(input.IsReadOnly);
                    Assert.Equal(rectangles, stableControls.Select(c => Bounds(c, frame)).ToArray());
                    Assert.Equal(frameBounds, Bounds(frame, card));
                    Assert.Equal(scrollOffset, scroll.VerticalOffset);
                }
            }
            finally { Application.Current.ThemeMode = originalTheme; scene.LayoutTransform = Transform.Identity; window.Width = 900; window.Height = 600; }
#pragma warning restore WPF0001
            await Idle(); card.Refresh(); await Idle(); scroll.ScrollToTop(); await Idle();

            card.StartDraft(document, new("fixture.zbd", 0, "fixture", 0), new(1, 2, 3));
            card.Refresh(); await Idle();
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
    private static Rect Bounds(FrameworkElement element, Visual relative) => new(element.TranslatePoint(new(), (UIElement)relative), element.RenderSize);
    private static void Capture(FrameworkElement element, string name)
    {
        if (Environment.GetEnvironmentVariable("ZSTUDIO_INSPECTION_CAPTURE") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 1.5), (int)Math.Ceiling(element.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(element), null, new Rect(element.RenderSize));
        image.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
