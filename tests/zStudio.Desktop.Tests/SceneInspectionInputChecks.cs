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
        var card = new SceneInspectionCard(scene, item =>
        {
            JsonObject info = new() { ["Node"] = item.Target == "hover" ? "Pointed object" : "Tank fixture with a long name which must trim in the fixed header", ["Editable"] = true,
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
            var panel = Descendants(card).OfType<Border>().Single(s => s.Name == "InspectionPanel");
            var hover = Descendants(card).OfType<Border>().Single(s => s.Name == "InspectionHover");
            var hoverName = Descendants(hover).OfType<TextBlock>().Single(t => t.Name == "InspectionHoverName");
            var surfaceX = Descendants(hover).OfType<TextBlock>().Single(t => t.Name == "InspectionSurfaceX");
            var originZ = Descendants(hover).OfType<TextBlock>().Single(t => t.Name == "InspectionOriginZ");
            var modes = Descendants(card).OfType<ToggleButton>().Where(b => AutomationProperties.GetName(b) is "Move object" or "Rotate object").ToArray();
            var resize = Descendants(card).OfType<Thumb>().Single(t => t.Name == "InspectionResize");
            double savedHeight = 432; card.PanelHeightChanged += height => savedHeight = height;
            async Task Resize(double delta, bool canceled = false)
            {
                resize.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                resize.RaiseEvent(new DragDeltaEventArgs(0, delta) { RoutedEvent = Thumb.DragDeltaEvent });
                resize.RaiseEvent(new DragCompletedEventArgs(0, delta, canceled) { RoutedEvent = Thumb.DragCompletedEvent });
                await Idle();
            }
            Assert.Equal(2, modes.Length); Assert.All(modes, b => { Assert.False(b.IsVisible); Assert.False(b.IsEnabled); });
            Assert.True(hover.IsVisible); Assert.Equal("X —", surfaceX.Text);
            Assert.Equal(10, Bounds(panel, card).Top, 5);
            Assert.Equal(card.ActualWidth - 10, Bounds(panel, card).Right, 5);
            Assert.True(Bounds(panel, card).Bottom <= scene.NavigationCubeBounds.Top - 10);
            Assert.Equal(432, panel.ActualHeight, 5); Assert.True(resize.IsVisible);
            double panelWidth = panel.ActualWidth;
            await Resize(-1000); Assert.Equal(216, panel.ActualHeight, 5); Assert.Equal(216, savedHeight);
            Assert.Equal(panelWidth, panel.ActualWidth); Assert.Equal(90, hover.ActualHeight);
            Capture(panel, "minimum-height");
            await Resize(10000); Assert.Equal(scene.ActualHeight * .8, panel.ActualHeight, 5);
            Assert.False(Bounds(panel, card).IntersectsWith(scene.NavigationCubeBounds));
            Assert.Equal(panelWidth, panel.ActualWidth);
            double preferred = savedHeight; await Resize(-80, canceled: true);
            Assert.Equal(preferred, savedHeight); Assert.Equal(preferred, panel.ActualHeight, 5);
            foreach (var key in new[] { Key.Home, Key.Down, Key.Up, Key.End })
            {
                var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(resize), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                resize.RaiseEvent(keyEvent); await Idle(); Assert.True(keyEvent.Handled);
                Assert.Equal(key switch { Key.Home or Key.Up => 216, Key.Down => 226, _ => scene.ActualHeight * .8 }, panel.ActualHeight, 5);
            }
            card.SetPanelHeight(432); await Idle();
            var pinned = Bounds(panel, card); var hoverBounds = Bounds(hover, card);
            var hoverItem = selected with { Target = "hover", Surface = new(1.2345678f, -20.25f, 300.5f), Origin = new(11, 22, -33) };
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.HoverInspection))!.SetValue(scene, hoverItem);
            card.Refresh(); await Idle();
            Assert.Equal("Pointed object", hoverName.Text); Assert.Equal("X 1.2345678", surfaceX.Text); Assert.Equal("Z -33", originZ.Text);
            Assert.Same(selected, scene.SelectedInspection); Assert.Equal(pinned, Bounds(panel, card)); Assert.Equal(hoverBounds, Bounds(hover, card));
            // Entering the panel (or leaving the viewport) clears the live hit,
            // but keeps the permanent readout and independently pinned details.
            scene.RenderSurface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent }); await Idle();
            Assert.Equal("X —", surfaceX.Text); Assert.Equal("Z —", originZ.Text);
            Assert.True(hover.IsVisible); Assert.True(frame.IsVisible); Assert.Equal(pinned, Bounds(panel, card));
            scene.RestoreView(new(new(1000, 500, 1000), new(0, 0, -10), new(0, 1, 0), 60)); card.Refresh(); await Idle();
            Assert.Equal(pinned, Bounds(panel, card)); scene.RestoreView(before);
            Assert.True(scene.SelectInspection(null, false)); card.Refresh(); await Idle();
            Assert.True(hover.IsVisible); Assert.False(frame.IsVisible); Assert.Equal(hoverBounds, Bounds(hover, card));
            Assert.False(resize.IsVisible);
            Assert.Equal(pinned.Top, Bounds(panel, card).Top); Assert.Equal(pinned.Right, Bounds(panel, card).Right);
            Assert.True(Bounds(panel, card).Height < pinned.Height);
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.SelectedInspection))!.SetValue(scene, selected);
            scene.SelectFramingNode(0); card.Refresh(); await Idle(); Assert.Equal(pinned, Bounds(panel, card));
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
            await Wheel(hoverName, -120); await Wheel(panel, -120);

            var authored = Descendants(card).OfType<SceneInspectionField>().Single(f => f.Binding == SceneInspectionBinding.AuthoredPosition);
            var input = authored.Inputs[0];
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.HoverInspection))!.SetValue(scene, hoverItem); card.Refresh();
            var confirm = Descendants(card).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Edit object transform");
            var cancel = Descendants(card).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Discard transform draft");
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
                foreach (double controlSize in new[] { 32d, 40d })
                {
                    window.Resources["PreviewControlSize"] = controlSize;
                    window.Resources["PreviewIconSize"] = controlSize == 40 ? 20d : 16d;
                    Application.Current.ThemeMode = theme;
                    scene.LayoutTransform = new ScaleTransform(scale, scale);
                    window.Width = width * scale; window.Height = 600 * scale;
                    await Idle(); card.Refresh(); await Idle();
                    scroll.ScrollToVerticalOffset(35); await Idle();
                    Assert.True(input.IsReadOnly); Assert.Equal(Visibility.Hidden, cancel.Visibility);
                    Assert.All(modes, b => Assert.False(b.IsVisible));
                    var rectangles = stableControls.Select(c => Bounds(c, frame)).ToArray();
                    var frameBounds = Bounds(frame, card);
                    double scrollOffset = scroll.VerticalOffset;
                    scroll.ScrollToTop(); await Idle(); Capture(panel, $"{theme}-{width}-{scale}-{controlSize}-read"); scroll.ScrollToVerticalOffset(scrollOffset); await Idle();
                    card.StartDraft(document, new("fixture.zbd", 0, "fixture", 0), new(1, 2, 3));
                    await Idle();
                    Assert.False(input.IsReadOnly); Assert.Equal(Visibility.Visible, cancel.Visibility);
                    Assert.True(modes.Single(b => AutomationProperties.GetName(b) == "Move object").IsVisible);
                    Assert.False(modes.Single(b => AutomationProperties.GetName(b) == "Rotate object").IsVisible);
                    Assert.False(modes.Single(b => AutomationProperties.GetName(b) == "Rotate object").IsEnabled);
                    Assert.Equal(frameBounds, Bounds(frame, card));
                    Assert.Equal(scrollOffset, scroll.VerticalOffset);
                    Assert.Equal(rectangles, stableControls.Select(c => Bounds(c, frame)).ToArray());
                    Assert.Same(input, authored.Inputs[0]);
                    Assert.All(Descendants(card).OfType<ValueTextBox>().Except(authored.Inputs), c => Assert.True(c.IsReadOnly));
                    Assert.True(Bounds(confirm, frame).Bottom < Bounds(scroll, frame).Top);
                    double barLeft = Bounds(bar, frame).Left;
                    foreach (var button in Descendants(scroll).OfType<Button>().Where(b => AutomationProperties.GetName(b).StartsWith("Copy ")))
                        Assert.True(Bounds(button, frame).Right <= barLeft - 4, "The scrollbar overlaps a field copy button");
                    scroll.ScrollToTop(); await Idle(); Capture(panel, $"{theme}-{width}-{scale}-{controlSize}-edit"); scroll.ScrollToVerticalOffset(scrollOffset); await Idle();
                    card.SetDraft(card.DraftToken, ["-", "2", "3"]); input.Focus(); input.Select(0, 1);
                    await Idle(); scroll.ScrollToVerticalOffset(scrollOffset); await Idle();
                    string draftToken = card.DraftToken; long revision = document.Revision;
                    await Resize(-40); await Resize(40);
                    Assert.Equal(draftToken, card.DraftToken); Assert.Equal(revision, document.Revision);
                    Assert.Equal("-", input.Text); Assert.Equal(0, input.SelectionStart); Assert.Equal(1, input.SelectionLength);
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
                    Assert.All(modes, b => Assert.False(b.IsVisible));
                    Assert.Equal(rectangles, stableControls.Select(c => Bounds(c, frame)).ToArray());
                    Assert.Equal(frameBounds, Bounds(frame, card));
                    Assert.Equal(scrollOffset, scroll.VerticalOffset);
                }
            }
            finally { Application.Current.ThemeMode = originalTheme; scene.LayoutTransform = Transform.Identity; window.Width = 900; window.Height = 600; }
#pragma warning restore WPF0001
            await Idle(); card.Refresh(); await Idle(); scroll.ScrollToTop(); await Idle();
            foreach (double width in new[] { 900d, 320d })
            {
                window.Width = width; window.Height = 300; await Idle(); card.Refresh(); await Idle();
                Assert.False(Bounds(panel, card).IntersectsWith(scene.NavigationCubeBounds));
                Assert.Equal(scene.ActualHeight * .8, panel.ActualHeight, 5);
                Assert.True(scroll.ViewportHeight >= 30, "Short viewport lost its scrollable details area"); Assert.Equal(90, hover.ActualHeight);
                Assert.NotEqual(scene.NavigationCubeHomeBounds, scene.NavigationCubeBounds);
                foreach (var button in new[] { confirm, cancel })
                {
                    Assert.True(Bounds(button, frame).Bottom < frame.ActualHeight);
                    Assert.True(Bounds(button, frame).Right <= frame.ActualWidth);
                }
                Capture(panel, $"short-viewport-{width}");
            }
            window.Width = 900; window.Height = 600; await Idle(); card.Refresh(); await Idle();
            Assert.Equal(432, panel.ActualHeight, 5); // Automatic clamping never overwrites the preference.
            Assert.Equal(scene.NavigationCubeHomeBounds, scene.NavigationCubeBounds);

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
                hoverName.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = Mouse.PreviewMouseDownEvent });
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
