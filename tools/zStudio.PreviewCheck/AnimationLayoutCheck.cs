using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using zStudio.Core.Animation;
using zStudio.Desktop;

internal static class AnimationLayoutCheck
{
    public static int Run(string root, bool treeOnly = false, bool toolbarOnly = false)
    {
        string output = Path.Combine(Path.GetTempPath(), "zstudio-fluent-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(output);
        string settings = zStudio.Desktop.StudioSettings.SettingsPath;
        byte[]? original = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow; window.WindowState = WindowState.Normal; window.Width = 1600; window.Height = 900; window.Left = -12000;
            try
            {
                window.ViewModel.Settings.Workspace = new() { InspectorTab = 2 };
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                CaptureWindow(window,Path.Combine(output,"startup.png"));
                Console.WriteLine("Startup visible workspace surfaces: " + string.Join(", ",new[] { "NavigatorHost","InspectorTabs","BottomTools","DocumentCommands","GlobalSearchHost","PaneButtons" }.Where(n => ((FrameworkElement)window.FindName(n)).IsVisible)));
                await window.ViewModel.OpenRootAsync(Path.GetFullPath(root));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                CaptureWindow(window,Path.Combine(output,"folder-open.png"));
                var document = await window.ViewModel.OpenFileAsync(Path.Combine(Path.GetFullPath(root), "m1", "anim.zbd")) ?? throw new InvalidDataException("No animation pack");
                var asset = document.Assets.First(a => a.Name == (treeOnly ? "destroy_vtol1" : "redsprks.flt")); document.SelectedAsset = asset;
                var sourceHash = SHA256.HashData(File.ReadAllBytes(document.Path));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                var editor = await Ready(asset.Index);
                ((CheckBox)editor.FindName("Mute")).IsChecked = true;
                var tree = (TreeView)editor.FindName("ProgramTree");
                var inspector = (TabControl)window.FindName("InspectorTabs");
                var tools = (TabControl)window.FindName("ToolTabs"); tools.SelectedIndex = 0;
                Require(inspector.Items.Cast<TabItem>().Select(t => t.Header.ToString()).SequenceEqual(["Sequences","Settings","References"]), "Inspector hierarchy incorrect");
                Require(inspector.SelectedIndex == 0,"Entering animation preview did not default to Sequences");
                Require(window.FindName("PropertiesPane") == null,"Docked Properties was not removed");
                Require(tools.Items.Cast<TabItem>().Select(t => t.Header.ToString()).SequenceEqual(["Dispatch","Event log","Problems","Runtime","Related","Bytes"]), "Bottom tool tabs incorrect");
                Require(((ContentControl)window.FindName("ProgramHost")).Content == editor.ProgramView && ReferenceEquals(((TabItem)window.FindName("ProgramTab")).Content,window.FindName("ProgramHost")), "Program has no Inspector tab host");
                if (toolbarOnly)
                {
                    await PreviewToolbarCheck.Run(window, editor, Capture);
                    Require(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(document.Path))) && !document.IsDirty,"Toolbar checks changed source data");
                    Console.WriteLine("Screenshots: " + output); return;
                }
                if (treeOnly)
                {
                    inspector.SelectedItem = window.FindName("ProgramTab"); await Capture("sequences"); CheckTreeText();
                    window.Width = 1080; window.Height = 650; await Capture("sequences-narrow"); CheckTreeText();
                    Descendants(tree).OfType<ScrollViewer>().First().ScrollToBottom();
                    await Capture("sequences-launches-narrow"); CheckTreeText();
#pragma warning disable WPF0001
                    app.ThemeMode = ThemeMode.Light; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Descendants(tree).OfType<ScrollViewer>().First().ScrollToBottom();
                    await Capture("sequences-light"); CheckTreeText();
#pragma warning restore WPF0001
                    Require(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(document.Path))),"Source data changed");
                    Console.WriteLine("PASS: Sequences headers and descriptions fit the tree viewport at wide/narrow widths, scrolled and light/dark, with unchanged source.");
                    Console.WriteLine("Screenshots: " + output); return;
                }
                await editor.SeekAsync(.3); await Capture("edit-dark");
                var pausedFrame = editor.CurrentFrame;
                tools.SelectedIndex = 3; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(((DataGrid)editor.FindName("RuntimeSequences")).Items.Cast<AnimationSequenceStatus>().SequenceEqual(pausedFrame!.Sequences.Where(s => s.Instance == 1)), "Opening Runtime while paused did not display the current root sequences");
                Require(ReferenceEquals(pausedFrame,editor.CurrentFrame), "Opening Runtime advanced the paused player"); tools.SelectedIndex = 0;
                var entry = document.Document.Animations!.Entries[asset.Index];

                var sequence = entry.Sequences.First(s => s.Events.Any(e => e.Type == 10)); var ev = sequence.Events.First(e => e.Type == 10);
                inspector.SelectedItem = window.FindName("ProgramTab");
                ((ProgramItem)tree.Items[0]).Children.First(p => p.Sequence == sequence.Id).IsExpanded = true;
                await Capture("program-right");
                Require(!Descendants((FrameworkElement)window.FindName("NavigatorHost")).Contains(editor.ProgramView),"Program still consumes Navigator space");
                Descendants(tree).OfType<TreeViewItem>().First(t => t.DataContext is ProgramItem p && p.Event == ev.Id).IsSelected = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var bytesBefore = editor.SourceByteSelection();
                Require(bytesBefore.Offset == ev.SourceOffset && bytesBefore.Bytes.Span.SequenceEqual(document.Document.Bytes.Span.Slice((int)ev.SourceOffset,ev.Bytes.Length)),"Bytes did not select original event range");
                var frame = editor.CurrentFrame; var view = editor.Viewport.CaptureView();
                editor.SelectSource(entry.Primary.Id); Require(((ComboBox)editor.FindName("Phase")).SelectedIndex == 0, "Cleanup selection changed phase");
                editor.SelectSource(sequence.Id,ev.Id);
                Require(ReferenceEquals(frame,editor.CurrentFrame), "Source selection restarted preview");
                window.UpdateLayout();
                var navigation = (TabControl)window.FindName("NavigationTabs");
                navigation.SelectedIndex = 2; inspector.SelectedIndex = 2; tools.SelectedIndex = 5;
                var resetViewMenu = ((Menu)window.FindName("AppMenu")).Items.OfType<MenuItem>().Single(m => Equals(m.Header, "_View"));
                resetViewMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Reset layout")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(((FrameworkElement)window.FindName("FileTree")).IsVisible && window.ViewModel.Settings.GetWorkspace().BrowserTab == 0 && inspector.SelectedIndex == 0 && tools.SelectedIndex == 0, "Reset layout did not apply live tab defaults");
                var resetPreferences = window.ViewModel.Settings.GetWorkspace();
                Require(resetPreferences.BrowserTab == 0 && resetPreferences.InspectorTab == 0 && resetPreferences.ToolTab == 0, "Tab events overwrote reset defaults");
                window.ViewModel.Settings.Save();
                var restoredTabs = StudioSettings.Load().GetWorkspace();
                Require(restoredTabs.BrowserTab == 0 && restoredTabs.InspectorTab == 0 && restoredTabs.ToolTab == 0, "Reset tabs were not persisted");
                Require(ReferenceEquals(frame,editor.CurrentFrame) && Equals(view,editor.Viewport.CaptureView()), "Reset layout changed player or camera");
                var layout = window.ViewModel.Settings.GetWorkspace();
                layout.InspectorVisible = false; window.Width = 1599; await Task.Delay(80,timeout.Token);
                layout.InspectorVisible = true; window.Width = 1600; await Task.Delay(80,timeout.Token);
                await Capture("procedural-motion");
                inspector.SelectedItem = window.FindName("ReferencesTab"); window.UpdateLayout();
                inspector.SelectedIndex = 0;
                CheckChrome(window);
                window.WindowState = WindowState.Maximized; await Task.Delay(120,timeout.Token);
                window.WindowState = WindowState.Normal; window.Left = -12000; await Task.Delay(120,timeout.Token);
                Require(ReferenceEquals(frame,editor.CurrentFrame) || editor.CurrentFrame?.Time == frame?.Time,"Window state reset preview time");
                await editor.SeekAsync(2); tools.SelectedIndex = 1; await Capture("event-log");
                Require(((DataGrid)editor.FindName("EventLog")).Items.Count == editor.CurrentFrame!.Trace.Count, "Event log only shows a text tail");
                tools.SelectedIndex = 2; await Capture("problems"); tools.SelectedIndex = 3; await Capture("runtime"); tools.SelectedIndex = 0;
#pragma warning disable WPF0001
                app.ThemeMode = ThemeMode.Light; await Capture("edit-light"); app.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
                foreach (var size in new[] { (1280d,720d),(1080d,650d),(1600d,900d) })
                {
                    window.Width = size.Item1; window.Height = size.Item2; await Capture($"layout-{size.Item1:0}x{size.Item2:0}");
                    var transport = (Grid)editor.FindName("TransportRow");
                    foreach (string name in new[] { "PlayButton","StopButton","PreviousFrameButton","NextFrameButton","SeekSlider" })
                    {
                        var control = (FrameworkElement)editor.FindName(name); var bounds = new Rect(control.TranslatePoint(new(),transport),control.RenderSize);
                        Require(bounds.Left >= -.1 && bounds.Right <= transport.ActualWidth + .1, name + " is clipped at narrow width");
                    }
                    Require(((Slider)editor.FindName("SeekSlider")).ActualWidth >= 80,"Seek bar too narrow");
                    Require(editor.Viewport.ActualHeight >= 180,"Preview collapsed below usable height");
                }
                asset = document.Assets.First(a => a.Name == "vtol_destruction1"); document.SelectedAsset = asset; editor = await Ready(asset.Index); ((CheckBox)editor.FindName("Mute")).IsChecked = true;
                inspector.SelectedItem = window.FindName("PreviewSetupTab"); await Capture("preview-setup");
                var height = (TextBox)editor.FindName("PreviewHeight"); height.ApplyTemplate();
                Require(height.Template.FindName("DeleteButton",height) is UIElement { Visibility: Visibility.Collapsed }, "Height clear button returned");
                var grid = (System.Windows.Controls.Primitives.ToggleButton)editor.FindName("ShowGrid"); var collision = (System.Windows.Controls.Primitives.ToggleButton)editor.FindName("GroundCollision");
                await editor.SeekAsync(0); height.Text = "100"; await Stable(editor);
                await editor.SeekAsync(8); var contact = editor.CurrentFrame!;
                grid.IsChecked = false; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(ReferenceEquals(contact,editor.CurrentFrame), "Visual grid toggle rebuilt simulation");
                collision.IsChecked = false; await Stable(editor); var airborne = editor.CurrentFrame!;
                Require(airborne.Time == contact.Time && !airborne.Nodes.SequenceEqual(contact.Nodes), "Collision off did not change ground constraint");
                grid.IsChecked = true; Require(ReferenceEquals(airborne,editor.CurrentFrame),"Showing grid changed collision state");
                collision.IsChecked = true; await Stable(editor);
                Require(contact.Nodes.SequenceEqual(editor.CurrentFrame!.Nodes),"Reenabled collision failed deterministic reconstruction");
                collision.IsChecked = false; await Stable(editor); await editor.SeekAsync(0);
                height.Text = "0"; await Stable(editor); var zeroHeight = editor.CurrentFrame!;
                height.Text = "-25.5"; await Stable(editor);
                Require(editor.CurrentFrame!.Time == 0 && !editor.IsPlaying && height.Text == "-25.5","Negative height changed the playhead or edit text");
                foreach (var pose in zeroHeight.Nodes)
                    Require(Math.Abs(editor.CurrentFrame.Nodes.Single(n => n.Id == pose.Id).Transform.M42 - pose.Transform.M42 + 25.5f) < .001,"Negative height did not lower the preview");
                var loweredFrame = editor.CurrentFrame;
                foreach (string partial in new[] { "-", "-1e", "-1000" })
                {
                    height.Text = partial; await Stable(editor);
                    Require(height.Text == partial && ReferenceEquals(loweredFrame,editor.CurrentFrame),"Partial/invalid negative height interrupted editing or changed preview");
                }
                height.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
                Require(height.Text == "-25.5","Leaving partial input did not retain the last valid negative height");
                inspector.SelectedItem = window.FindName("PreviewSetupTab"); await Capture("negative-height");
                collision.IsChecked = true; await Stable(editor);
                height.Text = "0"; await Stable(editor); inspector.SelectedIndex = 0;
                var assets = (DataGrid)window.FindName("AssetGrid");
                var space = new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent }; assets.RaiseEvent(space);
                for (int attempt = 0; space.Handled && !editor.IsPlaying && attempt < 100; attempt++) await Task.Delay(20,timeout.Token);
                Require(space.Handled && editor.IsPlaying,$"Space from Assets failed: handled={space.Handled}, loading={((Border)editor.FindName("LoadingPanel")).Visibility}, enabled={((Button)editor.FindName("PlayButton")).IsEnabled}, modifiers={Keyboard.Modifiers}"); editor.Pause();
                foreach (string name in new[] { "Seed","PreviewHeight","Condition","ShowHorizon","SeekSlider" })
                {
                    var native = new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    ((UIElement)editor.FindName(name)).RaiseEvent(native); Require(!native.Handled && !editor.IsPlaying,name + " lost native Space behavior");
                }
                var preserved = editor.CurrentFrame; var camera = editor.Viewport.CaptureView();
                foreach (string preset in new[] { "Inspect","Debug","Focus preview","Edit" })
                {
                    layout.Preset = preset; window.Width -= 1; window.UpdateLayout(); window.Width += 1; await Task.Delay(80,timeout.Token);
                    Require(ReferenceEquals(preserved,editor.CurrentFrame) && Equals(camera,editor.Viewport.CaptureView()),"Preset reset player or camera");
                }
                asset = document.Assets.First(a => a.Name == "destroy_vtol1"); document.SelectedAsset = asset; editor = await Ready(asset.Index);
                ((CheckBox)editor.FindName("Mute")).IsChecked = true;
                inspector.SelectedItem = window.FindName("ProgramTab"); tools.SelectedIndex = 0;
                tree = (TreeView)editor.FindName("ProgramTree");
                var authored = document.Document.Animations!.Entries[asset.Index];
                var rootItem = (ProgramItem)tree.Items[0];
                foreach (var s in rootItem.Children) s.IsExpanded = true;
                var rows = rootItem.Children.SelectMany(s => s.Children).ToArray();
                foreach (var instruction in authored.AllSequences.SelectMany(s => s.Events).Where(e => e.Type is 24 or 25))
                {
                    var row = rows.Single(r => r.Event == instruction.Id);
                    Require(row.Summary.Contains(instruction.Text(12,instruction.Type == 24 ? 20 : 32),StringComparison.Ordinal),"Named animation operand missing from Sequences");
                    Require(row.Detail.Contains("≥",StringComparison.Ordinal),"Start threshold is not labeled as a clock condition");
                    Console.WriteLine($"{row.Label} | {row.Summary} | {row.Detail}");
                }
                await Capture("sequences-destroy-vtol1"); CheckTreeText();
                window.Width = 1080; window.Height = 650; await Capture("sequences-narrow"); CheckTreeText();
                Require(inspector.SelectedIndex == 0,"Retained tree selection left Sequences");
                var scroll = Descendants(tree).OfType<ScrollViewer>().First(); scroll.ScrollToBottom();
                await Capture("sequences-launches-narrow"); CheckTreeText();
                window.Width = 1600; window.Height = 900; scroll.ScrollToTop();
#pragma warning disable WPF0001
                app.ThemeMode = ThemeMode.Light; await Capture("sequences-light"); app.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
                Console.WriteLine("PASS: screenshot example exposes all named animation operands and thresholds; Sequences stays selected and scrolls at narrow width.");
                foreach (string tabName in new[] { "ProgramTab", "PreviewSetupTab", "ReferencesTab" })
                {
                    var tab = (TabItem)window.FindName(tabName); inspector.SelectedItem = tab;
                    foreach (string animationName in new[] { "redsprks.flt", "destroy_vtol1" })
                    {
                        asset = document.Assets.First(a => a.Name == animationName); document.SelectedAsset = asset; editor = await Ready(asset.Index);
                        ((CheckBox)editor.FindName("Mute")).IsChecked = true;
                        Require(inspector.SelectedItem == tab,$"Switching to {animationName} left {tab.Header}");
                        var selectedEntry = document.Document.Animations!.Entries[asset.Index];
                        var selectedSequence = selectedEntry.Sequences.First(s => s.Events.Count > 0); var selectedEvent = selectedSequence.Events[0];
                        editor.SelectSource(selectedSequence.Id,selectedEvent.Id);
                        Require(inspector.SelectedItem == tab && editor.SourceByteSelection().Offset == selectedEvent.SourceOffset,"Source selection switched the right tab");
                    }
                }
                inspector.SelectedItem = window.FindName("ProgramTab"); await Capture("sequences");
                Console.WriteLine("PASS: Sequences default; all three right tabs survive animation changes and source selection.");
                                layout.NavigatorWidth = 310; layout.InspectorWidth = 380; layout.ToolsHeight = 200; window.ViewModel.Settings.Save();
                var restored = StudioSettings.Load().GetWorkspace(); Require(restored.NavigatorWidth == 310 && restored.InspectorWidth == 380 && restored.ToolsHeight == 200,"Layout preferences did not persist");
                Require(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(document.Path))),"Source data changed");
                Console.WriteLine("PASS: Fluent workbench hierarchy, contextual selection, cleanup isolation, invalid draft/escape/hide, compound edit and stable form undo, structured tools, light/dark/narrow layouts, four grid/collision states, Height template, Space, presets, persistence and untouched source.");
                Console.WriteLine("Screenshots: " + output);
                async Task<AnimationEditor> Ready(int index)
                {
                    while (true)
                    {
                        if (((ContentControl)window.FindName("AnimationHost")).Content is AnimationEditor current && current.EntryIndex == index && current.CurrentFrame != null && ((Border)current.FindName("LoadingPanel")).Visibility == Visibility.Collapsed) return current;
                        if (((TextBlock)window.FindName("EmptyPreview")).Text.StartsWith("Preview unavailable",StringComparison.Ordinal)) throw new InvalidDataException(((TextBlock)window.FindName("EmptyPreview")).Text);
                        await Task.Delay(100,timeout.Token);
                    }
                }
                async Task Stable(AnimationEditor current)
                {
                    while (!((Button)current.FindName("PlayButton")).IsEnabled || ((Border)current.FindName("LoadingPanel")).Visibility == Visibility.Visible) await Task.Delay(40,timeout.Token);
                    await Task.Delay(80,timeout.Token);
                }
                async Task Capture(string name)
                {
                    window.UpdateLayout(); await Task.Delay(250,timeout.Token); window.UpdateLayout();
                    var dpi = VisualTreeHelper.GetDpi(window);
                    CaptureWindow(window,Path.Combine(output,name+".png"));
                    if (window.OpenPropertiesWindow is { IsVisible: true } propertyWindow) CaptureWindow(propertyWindow, Path.Combine(output,name+"-properties.png"));
                    Console.WriteLine($"{name}: viewport {editor.Viewport.ActualWidth:0} × {editor.Viewport.ActualHeight:0}, DPI {dpi.PixelsPerInchX:0}");
                }
                void CheckTreeText()
                {
                    Require(inspector.SelectedIndex == 0,"Retained selection left Sequences");
                    foreach (var text in Descendants(tree).OfType<TextBlock>().Where(t => t.IsVisible && t.DataContext is ProgramItem))
                    {
                        var bounds = new Rect(text.TranslatePoint(new(),tree),text.RenderSize);
                        Require(bounds.Left >= 0 && bounds.Right <= tree.ActualWidth + .1,$"Sequences text exceeds viewport: {text.Text} (right {bounds.Right}, viewport {tree.ActualWidth})");
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        try { app.Run(); } finally { if (original != null) File.WriteAllBytes(settings,original); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    private static void CaptureWindow(Window window,string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        RenderTargetBitmap bitmap = new((int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(window.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32); bitmap.Render(window);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    internal static void CheckChrome(MainWindow window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        nint Hit(Point point) { int x = (int)point.X,y = (int)point.Y; return SendMessage(handle,0x84,0,(nint)((y << 16) | (x & 0xffff))); }
        var title = ((FrameworkElement)window.FindName("TitleArea")).PointToScreen(new Point(70,20));
        var menu = ((Menu)window.FindName("AppMenu")).PointToScreen(new Point(15,20));
        GetWindowRect(handle,out var bounds);
        nint caption = Hit(title),input = Hit(menu),edge = Hit(new(bounds.Left+2,(bounds.Top+bounds.Bottom)/2));
        Console.WriteLine($"Chrome: caption={caption}, menu={input}, resize={edge}");
        Require(caption == 2 && input == 1 && edge == 10,"Chrome drag/menu/resize hit tests failed");
        foreach (string name in new[] { "CaptionMinimize","CaptionMaximize","CaptionClose" })
        {
            var button = (Button)window.FindName(name);
            Require(Math.Abs(button.ActualHeight - ((FrameworkElement)window.FindName("TitleArea")).ActualHeight) < .1,"Caption control does not fill title height");
            foreach (double y in new[] { 4d,21d,38d })
                Require(Hit(button.PointToScreen(new Point(button.ActualWidth * .5,y))) == (name == "CaptionMaximize" ? 9 : 1),"Caption control has an incorrect full-height hit region");
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern nint SendMessage(nint handle,uint message,nint wparam,nint lparam);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle,out NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle,int attribute,out NativeRect value,int size);
    private static void Require(bool condition,string message) { if (!condition) throw new InvalidDataException(message); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (int i = 0;i < VisualTreeHelper.GetChildrenCount(root);i++) { var child = VisualTreeHelper.GetChild(root,i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
}
