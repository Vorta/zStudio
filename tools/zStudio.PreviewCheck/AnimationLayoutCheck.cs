using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Recoil.Zbd.Desktop;

internal static class AnimationLayoutCheck
{
    public static int Run(string root)
    {
        string output = Path.Combine(Path.GetTempPath(), "zbd-animation-layout-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        byte[]? original = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow;
            window.WindowState = WindowState.Normal; window.Width = 1740; window.Height = 980; window.Left = -12000;
            try
            {
                window.ViewModel.Settings.AnimationSections.Clear(); window.ViewModel.Settings.AnimationSidebarWidth = 360;
                window.ViewModel.Settings.AnimationSectionHeights.Clear();
                await window.ViewModel.OpenRootAsync(Path.GetFullPath(root));
                var document = await window.ViewModel.OpenFileAsync(Path.Combine(Path.GetFullPath(root), "m1", "anim.zbd")) ?? throw new InvalidDataException("No animation pack");
                var asset = document.Assets.First(a => a.Name == "destroy_the_gen"); document.SelectedAsset = asset;
                byte[] originalHash = SHA256.HashData(File.ReadAllBytes(document.Document.Path));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                AnimationEditor editor;
                while (true)
                {
                    if (((ContentControl)window.FindName("AnimationHost")).Content is AnimationEditor current && current.EntryIndex == asset.Index && current.CurrentFrame != null && ((Border)current.FindName("LoadingPanel")).Visibility == Visibility.Collapsed) { editor = current; break; }
                    await Task.Delay(100, timeout.Token);
                }
                ((CheckBox)editor.FindName("Mute")).IsChecked = true;
                var assets = (DataGrid)window.FindName("AssetGrid"); assets.ScrollIntoView(asset);
                var sequences = (ListBox)editor.FindName("Sequences"); var events = (DataGrid)editor.FindName("Events");
                var sections = new[] { "PreviewOptionsSection", "SequencesSection", "EventsSection", "StatusSection", "TraceSection" }.Select(n => (Expander)editor.FindName(n)).ToArray();
                var sidebar = (ScrollViewer)editor.FindName("SidebarScroll"); var toggle = (ToggleButton)editor.FindName("SidebarToggle");
                var tracePanel = (Grid)editor.FindName("DispatchedEventsPanel");
                var timeline = (AnimationTimeline)editor.FindName("Timeline");
                double[] defaultHeights = sections.Select(s => ((FrameworkElement)s.Content).Height).ToArray();
                var currentFrame = editor.CurrentFrame;
                var selected = events.SelectedItem;
                var inspector = (TabControl)window.FindName("InspectorTabs");
                var column = (ColumnDefinition)window.FindName("PropertiesColumn");
                foreach (bool properties in new[] { false, true })
                {
                    inspector.Visibility = properties ? Visibility.Visible : Visibility.Collapsed; column.Width = new(properties ? 300 : 0);
                    await Capture(properties ? "properties-open" : "wide");
                }
                double viewportHeight = editor.Viewport.ActualHeight;
                foreach (var section in sections) section.IsExpanded = true;
                await Capture("all-expanded");
                Require(Math.Abs(editor.Viewport.ActualHeight - viewportHeight) < 1, "Expanding details stole preview height.");
                Require(sidebar.ScrollableHeight > 0, "Expanded sidebar cannot scroll.");
                Require(Equals(sections[2].Header, "Events") && Equals(sections[3].Header, "Status"), "Section headers were not simplified.");
                Require(Descendants((DependencyObject)sections[2].Content).Contains((DependencyObject)editor.FindName("EventsContext")), "Sequence context is not in the Events body.");
                Require(Descendants(tracePanel).Contains(timeline) && !Descendants(sidebar).Contains(timeline), "Trace graphic is still inside the sidebar.");
                var traceBody = (Grid)sections[4].Content;
                Require(traceBody.Children.Count == 2 && traceBody.Children.OfType<TextBox>().Single() == editor.FindName("TraceText") && traceBody.Children.OfType<Thumb>().Count() == 1, "Event trace contains controls other than its text and resize handle.");
                CheckTraceBounds();
                for (int i = 0; i < sections.Length; i++)
                {
                    var body = (FrameworkElement)sections[i].Content;
                    var handle = Descendants(body).OfType<Thumb>().Single(t => t.Parent == body);
                    double before = body.ActualHeight;
                    Resize(handle, 96);
                    Require(Math.Abs(body.ActualHeight - before - 96) < .1, sections[i].Name + " did not grow.");
                    Resize(handle, -10000);
                    Require(Math.Abs(body.ActualHeight - body.MinHeight) < .1, sections[i].Name + " shrank below its usable minimum.");
                    Resize(handle, 10000);
                    Require(Math.Abs(body.ActualHeight - body.MaxHeight) < .1, sections[i].Name + " has an unbounded height.");
                    Resize(handle, defaultHeights[i] - body.Height);
                    var keyResize = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent };
                    handle.RaiseEvent(keyResize); window.UpdateLayout();
                    Require(keyResize.Handled && Math.Abs(body.Height - defaultHeights[i] - 8) < .1, "Keyboard resizing failed.");
                    Resize(handle, -8);
                    var nativeSpace = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    handle.RaiseEvent(nativeSpace);
                    Require(!nativeSpace.Handled && !editor.IsPlaying, "Space on the resize handle unexpectedly played the animation.");
                }
                foreach (string name in new[] { "Diagnostics", "TraceText" })
                {
                    var text = (TextBox)editor.FindName(name); string savedText = text.Text;
                    text.Text = string.Join('\n', Enumerable.Range(1, 120).Select(n => $"Long status/trace line {n}"));
                    window.UpdateLayout();
                    var scroll = Descendants(text).OfType<ScrollViewer>().First();
                    Require(scroll.ScrollableHeight > 0 && scroll.ViewportHeight < 240, name + " is not internally scrollable.");
                    scroll.ScrollToBottom(); sidebar.ScrollToTop(); window.UpdateLayout();
                    var boundaryWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                    scroll.RaiseEvent(boundaryWheel); window.UpdateLayout();
                    Require(boundaryWheel.Handled && sidebar.VerticalOffset > 0, name + " trapped the wheel at its boundary.");
                    text.Text = savedText;
                }
                foreach (int i in new[] { 3, 4 })
                {
                    var body = (FrameworkElement)sections[i].Content;
                    double height = i == 3 ? 320 : 360;
                    Resize(Descendants(body).OfType<Thumb>().Single(t => t.Parent == body), height - body.Height);
                    sections[i].IsExpanded = false; sections[i].IsExpanded = true; window.UpdateLayout();
                    Require(Math.Abs(body.ActualHeight - height) < .1, "Collapse/expand lost section height.");
                    Require(Descendants(body).OfType<TextBox>().Single().ActualHeight > height - 30, "Text does not fill the resized section.");
                }
                window.ViewModel.Settings.Save();
                var restored = StudioSettings.Load();
                Require(restored.AnimationSectionHeights["StatusSection"] == 320 && restored.AnimationSectionHeights["TraceSection"] == 360, "Section heights did not survive settings reload.");
                var nested = Descendants(sequences).OfType<ScrollViewer>().First(); nested.ScrollToBottom(); sidebar.ScrollToTop(); window.UpdateLayout();
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                nested.RaiseEvent(wheel); window.UpdateLayout();
                Require(wheel.Handled && sidebar.VerticalOffset > 0, "Wheel did not transfer from list boundary to sidebar.");
                sidebar.ScrollToBottom(); await Capture("trace-bottom");
                Require(((FrameworkElement)editor.FindName("TraceSection")).TranslatePoint(new(), sidebar).Y < sidebar.ViewportHeight, "Event trace cannot be reached.");
                foreach (var section in sections) section.IsExpanded = section.Name is "SequencesSection" or "EventsSection";
                Require(timeline.IsVisible, "Collapsing trace text hid the trace graphic.");
                sidebar.ScrollToTop();
#pragma warning disable WPF0001
                app.ThemeMode = ThemeMode.Light; await Capture("light"); app.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
                window.Width = 1080; window.Height = 700; await Capture("narrow");
                Require(toggle.IsChecked == false, "Narrow details should initially leave room for preview.");
                toggle.IsChecked = true; await Capture("narrow-details");
                Require((string)toggle.Content == "Back to preview", "Compact details lacks a return action.");
                CheckTraceBounds();
                var host = (Border)editor.FindName("SidebarHost");
                var playerTransport = (StackPanel)editor.FindName("PlayerTransport");
                Require(host.TranslatePoint(new(0, host.ActualHeight), editor).Y <= playerTransport.TranslatePoint(new(), editor).Y, "Compact sidebar overlaps transport or trace graphic.");
                var transport = (Grid)editor.FindName("TransportRow");
                var buttons = new[] { "PlayButton", "StopButton", "PreviousFrameButton", "NextFrameButton", "SeekSlider" }.Select(n => (FrameworkElement)editor.FindName(n)).ToArray();
                foreach (var button in buttons)
                {
                    var bounds = new Rect(button.TranslatePoint(new(), transport), button.RenderSize);
                    Require(bounds.Left >= 0 && bounds.Right <= transport.ActualWidth + .1, "Narrow transport clips a control.");
                }
                Require(buttons[^1].ActualWidth >= 80, "Narrow seeker is too small.");
                toggle.IsChecked = false; window.Width = 1740; window.Height = 980; window.UpdateLayout();
                Require(ReferenceEquals(currentFrame, editor.CurrentFrame) && ReferenceEquals(selected, events.SelectedItem), "Layout changes reset playback or selection.");
                sequences.SelectedIndex = 2; window.UpdateLayout();
                Require(((TextBlock)editor.FindName("EventsContext")).Text.Contains("#2", StringComparison.Ordinal) && events.SelectedItem != null, "Sequence selection lost event context.");
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                assets.RaiseEvent(key); Require(key.Handled && editor.IsPlaying, "Space from Assets no longer plays."); editor.Pause();
                foreach (string control in new[] { "Seed", "PreviewHeight", "Condition", "ShowHorizon", "SeekSlider", "SidebarToggle" })
                {
                    var native = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    ((UIElement)editor.FindName(control)).RaiseEvent(native); Require(!native.Handled && !editor.IsPlaying, control + " lost native Space behavior.");
                }
                ((Button)editor.FindName("NextFrameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(200);
                Require(editor.CurrentFrame!.Time > 0, "Next frame did not step.");
                await editor.SeekAsync(2);
                Require(ReferenceEquals(timeline.Frame, editor.CurrentFrame) && timeline.Frame.Trace.Count > 0 && !((TextBox)editor.FindName("TraceText")).Text.StartsWith("No dispatched", StringComparison.Ordinal), "Moved trace graphic/text no longer follow the playhead.");
                timeline.Duration = .5;
                Descendants(tracePanel).OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(timeline.Duration == ((Slider)editor.FindName("SeekSlider")).Maximum, "Relocated Fit trace did not restore the range.");
                foreach (var section in sections) section.IsExpanded = section.Name is "StatusSection" or "TraceSection";
                sidebar.ScrollToBottom(); await Capture("dispatched-events");
                sections[1].IsExpanded = false; sections[4].IsExpanded = true;
                ((GridSplitter)editor.FindName("SidebarSplitter")).RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                ((ColumnDefinition)editor.FindName("SidebarColumn")).Width = new(420); window.UpdateLayout();
                ((GridSplitter)editor.FindName("SidebarSplitter")).RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                var replacement = document.Assets.First(a => a.Name == "pu000"); document.SelectedAsset = replacement;
                AnimationEditor next;
                while (true)
                {
                    if (((ContentControl)window.FindName("AnimationHost")).Content is AnimationEditor current && current.EntryIndex == replacement.Index && current.CurrentFrame != null && ((Border)current.FindName("LoadingPanel")).Visibility == Visibility.Collapsed) { next = current; break; }
                    await Task.Delay(100, timeout.Token);
                }
                Require(!((Expander)next.FindName("SequencesSection")).IsExpanded && ((Expander)next.FindName("TraceSection")).IsExpanded, "Section preferences were lost on asset replacement.");
                Require(Math.Abs(((ColumnDefinition)next.FindName("SidebarColumn")).ActualWidth - 420) < 1, "Preferred sidebar width was lost.");
                Require(((Grid)next.FindName("StatusBody")).Height == 320 && ((Grid)next.FindName("TraceBody")).Height == 360, "Section heights were lost on asset replacement.");
                next.ResetLayout(); window.UpdateLayout();
                Require(((Expander)next.FindName("SequencesSection")).IsExpanded && !((Expander)next.FindName("TraceSection")).IsExpanded && Math.Abs(((ColumnDefinition)next.FindName("SidebarColumn")).ActualWidth - 360) < 1, "Reset layout failed.");
                for (int i = 0; i < sections.Length; i++) Require(((FrameworkElement)((Expander)next.FindName(sections[i].Name)).Content).Height == defaultHeights[i], "Reset layout retained a customized section height.");
                Require(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(document.Document.Path))), "Layout check changed source data.");
                Console.WriteLine("PASS: trace below player, text-only Event trace, simplified headers, all five section resize grips and keyboard resizing, height bounds/persistence/reset, independent sidebar/text/list scrolling, wheel handoff, narrow details/transport/trace, selection/frame preservation, Space, frame step and unchanged source data.");
                Console.WriteLine("Screenshots: " + output);
                void Resize(Thumb handle, double amount)
                {
                    handle.RaiseEvent(new DragDeltaEventArgs(0, amount) { RoutedEvent = Thumb.DragDeltaEvent }); window.UpdateLayout();
                    handle.RaiseEvent(new DragCompletedEventArgs(0, amount, false) { RoutedEvent = Thumb.DragCompletedEvent });
                }
                void CheckTraceBounds()
                {
                    var player = (Grid)editor.FindName("PlayerArea");
                    var transport = (StackPanel)editor.FindName("PlayerTransport");
                    Require(tracePanel.TranslatePoint(new(), player).Y >= transport.TranslatePoint(new(0, transport.ActualHeight), player).Y, "Trace is not below the player controls.");
                    Require(tracePanel.ActualWidth > player.ActualWidth - 10 && tracePanel.TranslatePoint(new(0, tracePanel.ActualHeight), player).Y <= player.ActualHeight + .1, "Trace does not fit the player width/height.");
                    var editorBody = (Grid)editor.FindName("EditorBody");
                    Require(tracePanel.TranslatePoint(new(0, tracePanel.ActualHeight), editorBody).Y <= editorBody.ActualHeight + .1, "Trace overflows the editor's allocated height.");
                }
                async Task Capture(string name)
                {
                    var heightField = (TextBox)editor.FindName("PreviewHeight");
                    heightField.Focus(); heightField.ApplyTemplate();
                    Require(!heightField.AcceptsReturn && heightField.TextWrapping == TextWrapping.NoWrap, "Height lost single-line editing behavior");
                    Require(heightField.Template.FindName("DeleteButton", heightField) is UIElement { Visibility: Visibility.Collapsed }, "Fluent height clear button returned after theme/layout change");
                    window.UpdateLayout(); await Task.Delay(400); window.UpdateLayout();
                    var dpi = VisualTreeHelper.GetDpi(window);
                    RenderTargetBitmap bitmap = new((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    bitmap.Render(window); PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
                    Console.WriteLine($"{name}: editor {editor.ActualWidth:F0}×{editor.ActualHeight:F0}, viewport {editor.Viewport.ActualWidth:F0}×{editor.Viewport.ActualHeight:F0}");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        try { app.Run(); } finally { if (original != null) File.WriteAllBytes(settings, original); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
}
