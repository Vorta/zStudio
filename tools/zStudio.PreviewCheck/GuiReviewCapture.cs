using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;

/// <summary>Captures only this harness's visible window, including the native caption and GPU surfaces.</summary>
internal static class GuiReviewCapture
{
    public static int Run(string root,bool headerOnly = false,bool menusOnly = false)
    {
        string output = Path.Combine(Path.GetTempPath(), "zstudio-gui-review-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output); Console.WriteLine("Screenshots: " + output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        byte[]? original = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        nint previousWindow = GetForegroundWindow();
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                window.ViewModel.Settings.Workspace = new();
                window.WindowState = WindowState.Normal; window.Width = 1600; window.Height = 900; window.Left = 40; window.Top = 30;
#pragma warning disable WPF0001
                app.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
                await Capture("00-startup");
                if (headerOnly)
                {
                    var title = (FrameworkElement)window.FindName("TitleArea");
                    var diagnostics = Descendants(title).Prepend(title).OfType<FrameworkElement>().Select(v => new {
                        type = v.GetType().Name, v.Name, text = v is TextBlock t ? t.Text : v is AccessText a ? a.Text : null,
                        actual = v.RenderSize.ToString(), desired = v.DesiredSize.ToString(), origin = v.TranslatePoint(new(),window).ToString(),
                        v.Height, v.MinHeight, v.MaxHeight, margin = v.Margin.ToString(), vertical = v.VerticalAlignment.ToString(),
                        clip = VisualTreeHelper.GetClip(v)?.Bounds.ToString(), contentBounds = VisualTreeHelper.GetContentBounds(v).ToString()
                    });
                    File.WriteAllText(Path.Combine(output,"header-layout.json"),System.Text.Json.JsonSerializer.Serialize(diagnostics,new System.Text.Json.JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                    Console.WriteLine("Studio assembly: " + typeof(MainWindow).Assembly.Location);
                    CheckHeader(window);
                    foreach (bool loaded in new[] { false, true })
                    {
                        if (loaded)
                        {
                            await window.ViewModel.OpenRootAsync(Path.GetFullPath(root));
                            var document = await Open("image.zbd");
                            await Select(document,document.Assets.First(a => a.Record.Kind == AssetKind.Texture));
                        }
                        foreach (string theme in new[] { "Dark", "Light", "System" })
                        foreach (string density in new[] { "Compact", "Comfortable" })
                        {
                            Theme(theme);
                            var view = ((Menu)window.FindName("AppMenu")).Items.OfType<MenuItem>().First(m => Equals(m.Header,"_View"));
                            view.Items.OfType<MenuItem>().First(m => Equals(m.Header,"Density")).Items.OfType<MenuItem>().First(m => Equals(m.Header,density)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                            foreach (var (width,state) in new[] { (1600,WindowState.Normal), (1080,WindowState.Normal), (1600,WindowState.Maximized) })
                            {
                                window.Width = width;
                                window.WindowState = state;
                                string sample = $"header-{(loaded ? "loaded" : "startup")}-{theme}-{density}-{width}-{state}";
                                await Capture(sample);
                                CheckHeader(window);
                                var dpi = VisualTreeHelper.GetDpi(title);
                                RenderTargetBitmap headerImage = new((int)Math.Ceiling(title.ActualWidth * dpi.DpiScaleX),(int)Math.Ceiling(title.ActualHeight * dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
                                headerImage.Render(title);
                                PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(headerImage));
                                using var stream = File.Create(Path.Combine(output,sample + "-strip.png")); encoder.Save(stream);
                            }
                        }
                    }
                    Console.WriteLine("PASS: complete header text, centered document commands and reclaimed row in 36 startup/loaded, theme, density and width/window-state combinations."); return;
                }
                CheckHeader(window);
                var fileMenu = ((Menu)window.FindName("AppMenu")).Items.OfType<MenuItem>().First();
                fileMenu.IsSubmenuOpen = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(200,timeout.Token);
                var recentMenu = (MenuItem)window.FindName("RecentMenu");
                var recentHeader = (FrameworkElement)recentMenu.Template.FindName("HeaderHost",recentMenu);
                var firstLeaf = fileMenu.Items.OfType<MenuItem>().First();
                var leafHeader = Descendants(firstLeaf).OfType<ContentPresenter>().First(p => p.ContentSource == "Header");
                Require(Math.Abs(recentHeader.PointToScreen(new()).X - leafHeader.PointToScreen(new()).X) < 1,"Recent folders is misaligned with File menu commands");
                if (fileMenu.Template.FindName("PART_Popup",fileMenu) is System.Windows.Controls.Primitives.Popup { Child: Visual menuVisual } && PresentationSource.FromVisual(menuVisual) is HwndSource menuSource) CaptureHandle(menuSource.Handle,Path.Combine(output,"40-file-menu.png"));
                if (menusOnly)
                {
                    recentMenu.IsSubmenuOpen = true;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(200,timeout.Token);
                    var recentItems = recentMenu.Items.OfType<MenuItem>().ToArray();
                    Require(recentItems.Length == window.ViewModel.Settings.RecentRoots.Count,"Recent folder count differs from history");
                    for (int i = 0; i < recentItems.Length; i++)
                    {
                        string expected = window.ViewModel.Settings.RecentRoots[i];
                        Require(Descendants(recentItems[i]).OfType<TextBlock>().Any(t => t.Text == expected),"Recent folder text is not rendered literally: " + expected);
                        Require(new System.Windows.Automation.Peers.MenuItemAutomationPeer(recentItems[i]).GetName() == expected,"Recent folder accessible name differs from its path");
                    }
                    if (recentMenu.Template.FindName("PART_Popup",recentMenu) is System.Windows.Controls.Primitives.Popup { Child: Visual recentVisual } && PresentationSource.FromVisual(recentVisual) is HwndSource recentSource) CaptureHandle(recentSource.Handle,Path.Combine(output,"42-recent-folders.png"));
                    string expectedRoot = Path.GetFullPath(root);
                    int rootIndex = window.ViewModel.Settings.RecentRoots.FindIndex(p => p.Equals(expectedRoot,StringComparison.OrdinalIgnoreCase));
                    Require(rootIndex >= 0,"This menu check requires the supplied root in the existing recent history");
                    recentItems[rootIndex].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await Wait(() => window.ViewModel.RootPath == expectedRoot && !window.ViewModel.IsBusy);
                    Require(window.ViewModel.Folders.Single().Path == expectedRoot,"Recent-folder click opened a different path");
                    recentMenu.IsSubmenuOpen = fileMenu.IsSubmenuOpen = false; Mouse.Capture(null); Keyboard.ClearFocus();
                    await Capture("43-recent-folder-opened");
                    Console.WriteLine("PASS: literal recent-folder text, accessibility names and opening the exact original path."); return;
                }
                fileMenu.IsSubmenuOpen = false;
                // This fixture opens the menu programmatically; leave its menu-mode
                // capture before separately exercising window system commands.
                Mouse.Capture(null); Keyboard.ClearFocus(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(!((FrameworkElement)window.FindName("NavigatorHost")).IsVisible && !((FrameworkElement)window.FindName("InspectorTabs")).IsVisible && !((FrameworkElement)window.FindName("DocumentCommands")).IsVisible, "Startup exposed empty workspace controls");
                nint handle = new WindowInteropHelper(window).Handle;
                SendMessage(handle, 0x0112, 0xF020, 0); await Task.Delay(250, timeout.Token);
                Require(window.WindowState == WindowState.Minimized, "Native minimize failed");
                SendMessage(handle, 0x0112, 0xF120, 0); await Task.Delay(250, timeout.Token);
                SendMessage(handle, 0x0112, 0xF030, 0); await Task.Delay(250, timeout.Token);
                Require(window.WindowState == WindowState.Maximized, "Native maximize failed");
                await Capture("01-maximized");
                var status = Descendants(window).OfType<System.Windows.Controls.Primitives.StatusBar>().First();
                var bottom = status.PointToScreen(new Point(status.ActualWidth,status.ActualHeight));
                var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; GetMonitorInfo(MonitorFromWindow(handle,2),ref monitor);
                Console.WriteLine($"Maximized status bottom={bottom}, work area={monitor.Work.Left},{monitor.Work.Top},{monitor.Work.Right},{monitor.Work.Bottom}");
                Require(bottom.X <= monitor.Work.Right + 1 && bottom.Y <= monitor.Work.Bottom + 1,"Maximized content escapes the monitor work area");
                SendMessage(handle, 0x0112, 0xF120, 0); await Task.Delay(250, timeout.Token);
                Require(window.WindowState == WindowState.Normal, "Native restore failed");
                await window.ViewModel.OpenRootAsync(Path.GetFullPath(root));
                await Capture("02-folder-open");
                var missionFolders = window.ViewModel.Folders.Single().Children.Where(n => n.File == null).Select(n => n.Name).ToArray();
                Require(Array.IndexOf(missionFolders,"m2") < Array.IndexOf(missionFolders,"m10"),"Natural mission directory order regressed");
                Require(((TabControl)window.FindName("NavigationTabs")).SelectedIndex == 0 && !((FrameworkElement)window.FindName("DocumentCommands")).IsVisible && !((FrameworkElement)window.FindName("InspectorTabs")).IsVisible, "Folder-only state did not show Files without empty document controls");
                ((TabControl)window.FindName("NavigationTabs")).SelectedItem = window.FindName("FilesTab");
                await Capture("03-files");
                ((TabControl)window.FindName("NavigationTabs")).SelectedItem = window.FindName("SearchTab");
                var textures = await Open("image.zbd");
                Require(((TabControl)window.FindName("NavigationTabs")).SelectedItem == window.FindName("SearchTab"),"Opening a document unexpectedly replaced Search selection");
                await Select(textures, textures.Assets.First(a => a.Record.Content is Recoil.Zbd.Core.TextureInfo { Width: 640, Height: 480 }));
                await Capture("04-texture");
                var sounds = await Open("soundsh.zbd"); await Select(sounds, sounds.Assets.First(a => a.Record.Kind == AssetKind.Sound));
                await Capture("05-audio");
                var scripts = await Open("interp.zbd"); await Select(scripts, scripts.Assets.First(a => a.Record.Kind == AssetKind.Script));
                ((TabControl)window.FindName("StructuredPanel")).SelectedIndex = 1; await Capture("06-script");
                var zrd = await Open(Path.Combine("m1", "zrdr.zbd")); await Select(zrd, zrd.Assets.First(a => a.Record.Kind == AssetKind.Zrd));
                ((TabControl)window.FindName("StructuredPanel")).SelectedIndex = 0; await Capture("07-zrd");
                var zrdTree = (TreeView)window.FindName("CentralTree"); var children = zrdTree.Items.OfType<InspectorNode>().Single(n => n.Name == "children");
                Require(children.IsExpanded && children.Children.First().IsExpanded,"Initial ZRD structural content remains hidden");
                ((TreeViewItem)zrdTree.ItemContainerGenerator.ContainerFromItem(children)).IsExpanded = false;
                await Select(zrd,zrd.Assets.First(a => a.Record.Kind == AssetKind.Zrd && a.Index != 0)); await Select(zrd,zrd.Assets.First(a => a.Record.Kind == AssetKind.Zrd && a.Index == 0));
                Require(!zrdTree.Items.OfType<InspectorNode>().Single(n => n.Name == "children").IsExpanded,"Explicit ZRD collapse was overwritten on return");
                var world = await Open(Path.Combine("m1", "gamez.zbd")); await Select(world, world.Assets.First(a => a.Record.Kind == AssetKind.World));
                await Wait(() => ((ContentControl)window.FindName("SceneHost")).Content is SceneViewport { Mission: not null });
                var scene = (SceneViewport)((ContentControl)window.FindName("SceneHost")).Content;
                await Capture("08-world");
                ((Button)window.FindName("PreviewNotices")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Capture("25-world-notices");
                var sceneMenu = Descendants((ToolBar)window.FindName("SceneToolbar")).OfType<MenuItem>().First(m => Equals(m.Header,"Scene"));
                sceneMenu.IsSubmenuOpen = true; await Capture("26-world-scene-menu");
                if (sceneMenu.Template.FindName("PART_Popup",sceneMenu) is System.Windows.Controls.Primitives.Popup { Child: Visual child } && PresentationSource.FromVisual(child) is HwndSource popupSource)
                    CaptureHandle(popupSource.Handle,Path.Combine(output,"26-scene-options-popup.png"));
                sceneMenu.IsSubmenuOpen = false;
                ((TabControl)window.FindName("ToolTabs")).SelectedIndex = 4;
                var actor = scene.Mission!.Actors.Single(a => a.Pickup?.Source.RecordIndex == 49);
                Console.WriteLine("Nanite matched save scope: " + string.Join("; ",world.PickupEdits!.Scope(actor.Pickup!.Source).Sources.Select(s => $"{Path.GetFileName(s.ArchivePath)}/{s.ResourceName} asset#{s.AssetIndex} record#{s.RecordIndex}")));
                var viewport = (Viewport3DX)scene.Content;
                var camera = (HelixToolkit.Wpf.SharpDX.PerspectiveCamera)viewport.Camera!;
                var vertices = SceneBuilder.Assemble(scene.Mission.Scene).Placements.Where(p => scene.PickupAt(p.NodeIndex)?.Root == actor.Root)
                    .SelectMany(p => scene.Mission.Scene.Models[p.ModelIndex].Vertices.Select(v => Vector3.Transform(v, p.Transform))).ToArray();
                var center = (vertices.Aggregate(Vector3.Min) + vertices.Aggregate(Vector3.Max)) * .5f;
                camera.Position = new(center.X + 4, center.Y + 3, center.Z + 5); camera.LookDirection = new(-4, -3, -5); camera.UpDirection = new(0, 1, 0);
                await Task.Delay(500, timeout.Token);
                var point = viewport.Project(new Point3D(center.X, center.Y, center.Z));
                bool selected = false;
                for (int y = -36; y <= 36 && !selected; y += 2)
                    for (int x = -36; x <= 36 && !selected; x += 2)
                    {
                        var p = point + new System.Windows.Vector(x, y);
                        var hit = viewport.FindHits(p).OrderBy(h => h.Distance).FirstOrDefault();
                        if (hit?.ModelHit is not MeshGeometryModel3D || Vector3.Distance(new(hit.PointHit.X, hit.PointHit.Y, hit.PointHit.Z), actor.Pickup!.Position) > 3) continue;
                        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
                        if (!scene.HandlePickupPointerDown(p, args)) typeof(Viewport3DX).GetMethod("MouseDownHitTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(viewport, [p, args]);
                        selected = scene.SelectedPickupRoot == actor.Root;
                    }
                Require(selected, "Could not select Nanite for review");
                ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("PickupLocked")).IsChecked = false; await Capture("09-pickup");
                await Select(world, world.Assets.First(a => a.Record.Kind == AssetKind.Model && a.Record.Content is GameModel m && m.Vertices.Length > 100));
                await Capture("10-model");
                ((TabControl)window.FindName("NavigationTabs")).SelectedItem = window.FindName("DocumentSceneTab");
                await Capture("41-document-scene");
                var animationDoc = await Open(Path.Combine("m1", "anim.zbd"));
                Require(((TabControl)window.FindName("NavigationTabs")).SelectedItem == window.FindName("AssetsTab"),"Switching away from a scene document left an unavailable page selected");
                var editor = await Animation("vtol_destruction1");
                await editor.SeekAsync(.6); editor.Viewport.FrameAnimation(editor.CurrentFrame!); await Capture("11-animation-entry");
                var inspector = (TabControl)window.FindName("InspectorTabs"); var tools = (TabControl)window.FindName("ToolTabs");
                inspector.SelectedItem = window.FindName("ProgramTab"); await Capture("44-program-right");
                Require(editor.ProgramView.IsDescendantOf(inspector) && !editor.ProgramView.IsDescendantOf((FrameworkElement)window.FindName("NavigatorHost")),"Program is not hosted solely in the right Inspector");
                inspector.SelectedItem = window.FindName("PreviewSetupTab"); ((TextBox)editor.FindName("PreviewHeight")).Text = "40";
                await Task.Delay(1000, timeout.Token); await editor.SeekAsync(1); editor.Viewport.FrameAnimation(editor.CurrentFrame!); await Capture("12-preview-setup");
                inspector.SelectedItem = window.FindName("ReferencesTab"); await Capture("13-references");
                editor = await Animation("redsprks.flt");
                var entry = animationDoc.AnimationEdits!.Package.Entries[editor.EntryIndex];
                var sequence = entry.Sequences.First(s => s.Events.Any(e => e.Type == 10));
                editor.SelectSource(sequence.Id); window.OpenAnimationProperties(animationDoc, editor.EntryIndex, sequence.Id, Guid.Empty); await Capture("14-sequence");
                editor.SelectSource(sequence.Id, sequence.Events.First(e => e.Type == 10).Id); window.OpenAnimationProperties(animationDoc, editor.EntryIndex, sequence.Id, sequence.Events.First(e => e.Type == 10).Id);
                await editor.SeekAsync(.4); editor.Viewport.FrameAnimation(editor.CurrentFrame!); await Capture("15-procedural-event");
                tools.SelectedIndex = 1; await Capture("16-event-log"); tools.SelectedIndex = 2; await Capture("17-problems");
                tools.SelectedIndex = 3; await Capture("18-runtime"); tools.SelectedIndex = 5; await Capture("19-bytes"); tools.SelectedIndex = 0;
                tools.SelectedIndex = 1; ((DataGrid)editor.FindName("EventLog")).SelectedIndex = 0;
                CaptureDetails("Event details","27-event-details",editor.EventLogView,"Details…");
                tools.SelectedIndex = 2; ((DataGrid)editor.FindName("Problems")).SelectedIndex = 0;
                if (((DataGrid)editor.FindName("Problems")).SelectedItem != null) CaptureDetails("Problem details","28-problem-details",editor.ProblemsView,"Details…");
                tools.SelectedIndex = 0;
                foreach (var group in Descendants(window.OpenPropertiesWindow!.AnimationFields!).OfType<Expander>().Where(e => Equals(e.Header,"Scheduling") || Equals(e.Header,"Target and modes"))) group.IsExpanded = false;
                Descendants(window.OpenPropertiesWindow!.AnimationFields!).OfType<Expander>().First(e => Equals(e.Header,"Launch ranges")).BringIntoView(); await Capture("29-procedural-ranges");
                Descendants(window.OpenPropertiesWindow!.AnimationFields!).OfType<CheckBox>().First(e => Equals(e.Content,"Show all stored fields")).IsChecked = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Descendants(window.OpenPropertiesWindow!.AnimationFields!).OfType<Expander>().First(e => e.Header?.ToString()?.StartsWith("Spin",StringComparison.Ordinal) == true).BringIntoView(); await Capture("30-procedural-stored-fields");
                inspector.SelectedItem = window.FindName("ReferencesTab"); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                Descendants(editor.ReferencesView).OfType<ListBox>().Single().SelectedIndex = 1; await Capture("31-editable-reference"); inspector.SelectedIndex = 0;
                var keyEntry = animationDoc.AnimationEdits.Package.Entries.First(e => e.Sequences.Any(s => s.Events.Any(v => v.Type == 12)));
                editor = await Animation(animationDoc.Assets.First(a => a.Index == keyEntry.Index).Name);
                var keySeq = keyEntry.Sequences.First(s => s.Events.Any(e => e.Type == 12)); editor.SelectSource(keySeq.Id, keySeq.Events.First(e => e.Type == 12).Id); window.OpenAnimationProperties(animationDoc, editor.EntryIndex, keySeq.Id, keySeq.Events.First(e => e.Type == 12).Id);
                await Capture("20-keyframes");
#pragma warning disable WPF0001
                var assetScroller = Descendants((DataGrid)window.FindName("AssetGrid")).OfType<ScrollViewer>().First();
                ((DataGrid)window.FindName("AssetGrid")).ScrollIntoView(animationDoc.SelectedAsset); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                double scrollOffset = assetScroller.VerticalOffset;
                Theme("Light"); await Capture("21-light");
                Require(Math.Abs(Descendants((DataGrid)window.FindName("AssetGrid")).OfType<ScrollViewer>().First().VerticalOffset - scrollOffset) < 1,"Theme changed Assets scroll position"); Theme("Dark");
#pragma warning restore WPF0001
                window.Width = 1080; window.Height = 650; await Capture("22-narrow");
                inspector.SelectedItem = window.FindName("ProgramTab"); await Capture("45-program-narrow"); inspector.SelectedIndex = 0;
                Require(GetClientRect(handle,out var shortClient),"Cannot measure short-window client area");
                var shortDpi = VisualTreeHelper.GetDpi(window);
                var shortWorkspace = (FrameworkElement)window.FindName("Workspace");
                var shortViewport = (FrameworkElement)editor.FindName("ViewportArea");
                var shortTransport = (FrameworkElement)editor.FindName("TransportRow");
                var shortPhase = (FrameworkElement)editor.FindName("Phase");
                double transportBottom = shortTransport.TranslatePoint(new(0,shortTransport.ActualHeight),window).Y;
                double phaseBottom = shortPhase.TranslatePoint(new(0,shortPhase.ActualHeight),window).Y;
                Require(Math.Abs((shortClient.Bottom - shortClient.Top) / shortDpi.DpiScaleY - 650) < 1 && Math.Abs(window.ActualWidth - 1080) < 1,"Narrow check did not reach the requested client size");
                Require(shortTransport.IsVisible && shortPhase.IsVisible && transportBottom < 650 && phaseBottom < 650 && shortViewport.ActualHeight > 100,"Transport or viewport is clipped at 650 DIP");
                var shortTools = (FrameworkElement)window.FindName("BottomTools");
                Require(((FrameworkElement)window.FindName("InspectorTabs")).IsVisible && editor.Viewport.ActualHeight >= 180 && (!shortTools.IsVisible || shortTools.ActualHeight >= 100),"Short layout must retain usable viewport, Inspector and any visible tools");
                Console.WriteLine($"Short-height evidence: client={shortClient.Right - shortClient.Left}x{shortClient.Bottom - shortClient.Top}px, DPI={shortDpi.DpiScaleX},{shortDpi.DpiScaleY}, window={window.ActualWidth}x{window.ActualHeight}DIP, workspace={shortWorkspace.ActualWidth}x{shortWorkspace.ActualHeight}, viewport={shortViewport.ActualWidth}x{shortViewport.ActualHeight}, transport bottom={transportBottom}, phase bottom={phaseBottom}; tools visible={shortTools.IsVisible}, Inspector scrollable.");
                window.Width = 1600; window.Height = 900;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(((FrameworkElement)window.FindName("BottomTools")).IsVisible && ReferenceEquals(editor,((ContentControl)window.FindName("AnimationHost")).Content),"Restoring height lost tools preference or replaced editor");
                foreach (var group in Descendants(window.OpenPropertiesWindow!.AnimationFields!).OfType<Expander>().Where(e => Equals(e.Header, "Scheduling"))) group.IsExpanded = false;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var keyframes = Descendants(window.OpenPropertiesWindow!.AnimationFields!).OfType<Expander>().First(e => Equals(e.Header, "Keyframe segments"));
                keyframes.BringIntoView(); await Capture("23-keyframe-fields");
                Descendants(window.OpenPropertiesWindow!.AnimationFields!).OfType<ScrollViewer>().First().ScrollToBottom(); await Capture("32-keyframe-actions");
                ((TabControl)window.FindName("NavigationTabs")).SelectedItem = window.FindName("SearchTab");
                ((TextBox)window.FindName("GlobalSearch")).Text = "vtol";
                await Task.Delay(2000, timeout.Token); await Capture("24-search");
                Require(window.ViewModel.SearchIsLimited && ((TextBlock)window.FindName("SearchHint")).Text.StartsWith("First 500",StringComparison.Ordinal),"Bounded search implies an exhaustive count");
                ((TextBox)window.FindName("GlobalSearch")).Text = "v"; await Capture("33-search-minimum");
                Require(window.ViewModel.SearchResults.Count == 0 && ((TextBlock)window.FindName("SearchHint")).Text.Contains("two characters",StringComparison.Ordinal),"Search minimum is not explained");
                ((TextBox)window.FindName("GlobalSearch")).Text = "__zstudio_no_matching_asset_942__"; await Capture("34-search-empty");
                Require(window.ViewModel.SearchResults.Count == 0 && ((TextBlock)window.FindName("SearchHint")).Text.StartsWith("No matching",StringComparison.Ordinal),"Empty search is unexplained");
                ((TextBox)window.FindName("GlobalSearch")).Text = "";
                // An authored dirty document must remain identifiable across strip resizing.
                animationDoc.AnimationEdits.Apply(editor.EntryIndex,"Review temporary reset delay",e => e.SetFloat(164,e.F32(164) + 1));
                await Task.Delay(400,timeout.Token);
                var sameEditor = editor;
                foreach (double width in new[] { 1600d,1280d,1080d,1600d })
                {
                    window.Width = width; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(120,timeout.Token); window.UpdateLayout();
                    var fileNode = window.ViewModel.Folders.SelectMany(Flatten).Single(n => n.Path == animationDoc.Path);
                    Require(fileNode.IsOpen && fileNode.IsActive && fileNode.DisplayName.EndsWith(" *",StringComparison.Ordinal),"Files tree lost the active/dirty document state");
                    Require(((TextBlock)window.FindName("ActiveDocumentTitle")).Text == animationDoc.Title,"Active document label lost its identity");
                    Require(ReferenceEquals(sameEditor,((ContentControl)window.FindName("AnimationHost")).Content),"Resizing recreated animation editor");
                    if (width == 1080) await Capture("35-dirty-narrow");
                }
                ((Button)window.FindName("DocumentUndo")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150,timeout.Token);
                Require(!animationDoc.IsDirty && ((Button)window.FindName("DocumentRedo")).IsEnabled,"Title-bar Undo did not restore the clean animation");
                ((Button)window.FindName("DocumentRedo")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150,timeout.Token);
                Require(animationDoc.IsDirty && ((Button)window.FindName("DocumentUndo")).IsEnabled,"Title-bar Redo did not reapply the animation edit");
                ((Button)window.FindName("DocumentUndo")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150,timeout.Token);
                NavigationFiles(); await Capture("38-files-open");
                var fileTree = (TreeView)window.FindName("FileTree");
                var fileRow = Descendants(fileTree).OfType<TreeViewItem>().First(t => t.DataContext is FolderNode n && n.Path == animationDoc.Path);
                var closeFile = Descendants(fileRow).OfType<Button>().Single(b => ReferenceEquals(b.Tag,animationDoc));
                Require(closeFile.IsVisible,"Open file did not expose its close button");
                animationDoc.AnimationEdits.Apply(editor.EntryIndex,"Close-cancel fixture",e => e.SetFloat(164,e.F32(164) + 1));
                var originalConfirm = window.ViewModel.ConfirmDiscardAsync;
                try
                {
                    window.ViewModel.ConfirmDiscardAsync = _ => Task.FromResult(false);
                    closeFile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(200,timeout.Token);
                    Require(window.ViewModel.Documents.Contains(animationDoc) && animationDoc.IsDirty,"Canceling a file-row close discarded the document");
                }
                finally { window.ViewModel.ConfirmDiscardAsync = originalConfirm; }
                animationDoc.AnimationEdits.Undo(); await Task.Delay(200,timeout.Token);
                var closedLifetime = animationDoc.Lifetime.Token;
                closeFile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(400,timeout.Token);
                var closedNode = (FolderNode)fileRow.DataContext;
                Require(!closedNode.IsOpen && closedNode.Document == null && closedLifetime.IsCancellationRequested && !window.ViewModel.Documents.Contains(animationDoc),"Closing a file row did not release its document or clear its open marker");
                fileRow.IsSelected = true;
                fileTree.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,Environment.TickCount,Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                await Wait(() => window.ViewModel.SelectedDocument?.Path == animationDoc.Path && ((TabControl)window.FindName("NavigationTabs")).SelectedItem == window.FindName("AssetsTab"));
                Require(closedNode.IsOpen && closedNode.IsActive && !ReferenceEquals(closedNode.Document,animationDoc),"Enter did not reopen the file in Assets");
                Console.WriteLine("PASS: Files tree open/active/dirty identity, actual row close, canceled dirty close, and Enter reopening to Assets.");
                Console.WriteLine("Additional captures: keyframe fields and root search.");
                Require(!window.ViewModel.Documents.Any(d => d.IsDirty), "Capture changed serialized records");
                foreach (var openDocument in window.ViewModel.Documents.ToArray()) await window.ViewModel.CloseAsync(openDocument);
                await Capture("36-last-document-closed");
                Require(((TabControl)window.FindName("NavigationTabs")).SelectedItem == window.FindName("FilesTab"),"Closing the last document did not return from Assets to Files");
                Require(!((FrameworkElement)window.FindName("InspectorTabs")).IsVisible && !((FrameworkElement)window.FindName("DocumentCommands")).IsVisible && ((TextBlock)window.FindName("WelcomeTitle")).Text == "Choose a file to inspect","Last document close exposed empty editor controls");
                string stressText = "QA display fixture — deliberately long diagnostic; not a game/corpus finding.\n\n" + string.Join("\n",Enumerable.Range(1,80).Select(i => $"{i}: Full diagnostic detail remains selectable, wraps inside this owned window, and preserves record identity and source context when it exceeds the visible area."));
                var detailsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
                Exception? detailFailure = null;
                detailsTimer.Tick += (_,_) =>
                {
                    var dialog = app.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "Review fixture · long detail"); if (dialog == null) return;
                    detailsTimer.Stop();
                    try
                    {
                        var detail = Descendants(dialog).OfType<TextBox>().Single(); detail.SelectAll();
                        Require(detail.SelectedText == stressText && detail.ExtentHeight > detail.ViewportHeight,"Long details were truncated or not scrollable/selectable");
                        CaptureWindow(dialog,Path.Combine(output,"37-long-detail-fixture.png"));
                        Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content,"Close")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex) { detailFailure = ex; }
                    finally { if (dialog.IsVisible) dialog.Close(); }
                };
                detailsTimer.Start(); try { DetailDialog.Show(window,"Review fixture · long detail",stressText); } finally { detailsTimer.Stop(); }
                if (detailFailure != null) throw new InvalidOperationException("Long detail display failed",detailFailure);
                Console.WriteLine("PASS: native minimize/maximize/restore/work-area, startup/folder context, theme scroll retention and full-window screenshots; no serialized edits.");
                void Theme(string theme)
                {
                    var menu = ((Menu)window.FindName("AppMenu")).Items.OfType<MenuItem>().First(m => Equals(m.Header,"_View")).Items.OfType<MenuItem>().First(m => Equals(m.Header,"_Theme"));
                    menu.Items.OfType<MenuItem>().First(m => Equals(m.Header,theme)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }
                void NavigationFiles() => ((TabControl)window.FindName("NavigationTabs")).SelectedItem = window.FindName("FilesTab");
                void CaptureDetails(string title,string name,FrameworkElement page,string button)
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
                    timer.Tick += (_,_) => { var dialog = app.Windows.OfType<Window>().FirstOrDefault(w => w.Title == title); if (dialog == null) return; timer.Stop(); CaptureWindow(dialog,Path.Combine(output,name + ".png")); dialog.Close(); Console.WriteLine(name); };
                    timer.Start();
                    try { Descendants(page).OfType<Button>().First(b => Equals(b.Content,button)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                    finally { timer.Stop(); }
                }
                async Task<DocumentModel> Open(string path) => await window.ViewModel.OpenFileAsync(Path.Combine(Path.GetFullPath(root), path)) ?? throw new InvalidDataException(path);
                async Task Select(DocumentModel doc, AssetItem asset)
                {
                    doc.SelectedAsset = asset; ((TabControl)window.FindName("NavigationTabs")).SelectedItem = window.FindName("AssetsTab");
                    await Wait(() => ((TextBlock)window.FindName("PreviewTitle")).Text == asset.Name && ((TextBlock)window.FindName("EmptyPreview")).Visibility != Visibility.Visible);
                    ((DataGrid)window.FindName("AssetGrid")).ScrollIntoView(asset); await Task.Delay(300, timeout.Token);
                    await window.OpenAssetPropertiesAsync(doc, asset.Record);
                }
                async Task<AnimationEditor> Animation(string name)
                {
                    var asset = animationDoc.Assets.First(a => a.Name == name); await Select(animationDoc, asset);
                    await Wait(() => ((ContentControl)window.FindName("AnimationHost")).Content is AnimationEditor e && e.EntryIndex == asset.Index && e.CurrentFrame != null && ((Border)e.FindName("LoadingPanel")).Visibility == Visibility.Collapsed);
                    var e = (AnimationEditor)((ContentControl)window.FindName("AnimationHost")).Content;
                    ((CheckBox)e.FindName("Mute")).IsChecked = true; return e;
                }
                async Task Wait(Func<bool> ready) { while (!ready()) await Task.Delay(100, timeout.Token); }
                async Task Capture(string name)
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    await Task.Delay(650, timeout.Token);
                    CheckTitleCommands(window);
                    CaptureWindow(window, Path.Combine(output, name + ".png"));
                    if (window.OpenPropertiesWindow is { IsVisible: true } popup) CaptureWindow(popup, Path.Combine(output, name + "-properties.png"));
                    Console.WriteLine(name);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(); }
        };
        try { app.Run(); }
        finally { if (original != null) File.WriteAllBytes(settings, original); else if (File.Exists(settings)) File.Delete(settings); if (previousWindow != 0) SetForegroundWindow(previousWindow); }
        return exit;
    }
    private static void CheckTitleCommands(MainWindow window)
    {
        var browser = (TabControl)window.FindName("NavigationTabs");
        bool hasDocument = window.ViewModel.SelectedDocument != null;
        bool hasScene = window.ViewModel.SelectedDocument?.SceneRoots.Count > 0;
        string[] expectedTabs = hasScene ? ["Files", "Assets", "Search", "Document scene"] : hasDocument ? ["Files", "Assets", "Search"] : ["Files", "Search"];
        Require(browser.Items.OfType<TabItem>().Where(t => t.Visibility == Visibility.Visible).Select(t => t.Header.ToString()).SequenceEqual(expectedTabs),"Navigator exposes tabs without available content");
        Require(browser.SelectedItem is TabItem { Visibility: Visibility.Visible },"Navigator retains a hidden selected page");
        bool animationOpen = ((ContentControl)window.FindName("AnimationHost")).Content is AnimationEditor;
        Require((((TabItem)window.FindName("ProgramTab")).Visibility == Visibility.Visible) == animationOpen,"Program tab availability does not follow the active animation");
        Require(window.FindName("PropertiesPane") == null,"Docked Properties still exists");

        Require(((MenuItem)window.FindName("PropertiesMenu")).IsEnabled == hasDocument,"Properties command availability is incorrect");
        if (!animationOpen) Require(((FrameworkElement)window.FindName("InspectorHost")).Visibility == Visibility.Collapsed,"An empty inspector still consumes space outside animations");

        var title = (FrameworkElement)window.FindName("TitleArea");
        var workspace = (FrameworkElement)window.FindName("Workbench");
        Require(Math.Abs(workspace.TranslatePoint(new(),window).Y - title.TranslatePoint(new(0,title.ActualHeight),window).Y) < 1,"Removed command row still occupies workspace height");
        var group = (FrameworkElement)window.FindName("DocumentCommands");
        Require(group.IsVisible == (window.ViewModel.SelectedDocument != null),"Document title/actions have the wrong context visibility");
        if (!group.IsVisible) return;
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(title).TransformBounds(new Rect(element.RenderSize));
        var groupBounds = Bounds(group);
        Require(Math.Abs(groupBounds.X + groupBounds.Width / 2 - title.ActualWidth / 2) < 1,"Document group is not centered on the window");
        Require(groupBounds.Left >= Bounds((FrameworkElement)window.FindName("AppMenu")).Right + 10 && groupBounds.Right <= Bounds((FrameworkElement)window.FindName("CaptionButtons")).Left - 10,"Document commands overlap menus or caption buttons");
        var name = (TextBlock)window.FindName("ActiveDocumentTitle");
        Require(name.Text == window.ViewModel.SelectedDocument!.Title,"Title bar lost the current file or dirty marker");
        double previousRight = Bounds(name).Right;
        nint handle = new WindowInteropHelper(window).Handle;
        foreach (string id in new[] { "DocumentUndo","DocumentRedo","DocumentSave" })
        {
            var button = (Button)window.FindName(id); var bounds = Bounds(button);
            Require(button.IsVisible && button.ActualWidth >= 32 && button.ActualHeight >= 32 && bounds.Left >= previousRight && groupBounds.Contains(bounds),"Document command is clipped or out of order: " + id);
            previousRight = bounds.Right;
            var point = button.PointToScreen(new(button.ActualWidth / 2,button.ActualHeight / 2)); int x = (int)point.X,y = (int)point.Y;
            Require(SendMessage(handle,0x84,0,(nint)((y << 16) | (x & 0xffff))) == 1,"Title command does not accept client input: " + id);
        }
    }
    private static void CheckHeader(MainWindow window)
    {
        var title = (TextBlock)window.FindName("ApplicationTitle");
        double titleCenter = title.TranslatePoint(new(0,title.ActualHeight / 2),window).Y;
        foreach (var item in ((Menu)window.FindName("AppMenu")).Items.OfType<MenuItem>().Where(i => i.IsVisible))
        {
            var presenter = (FrameworkElement)item.Template.FindName("HeaderPresenter",item);
            var text = Descendants(presenter).OfType<TextBlock>().Single();
            // Measuring only the presenter center misses Fluent allocating less height
            // than its text needs. Check the full text box against every layout slot.
            for (DependencyObject? parent = VisualTreeHelper.GetParent(text); parent != null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is FrameworkElement ancestor)
                {
                    var bounds = text.TransformToAncestor(ancestor).TransformBounds(new Rect(text.RenderSize));
                    const double tolerance = .01;
                    Require(bounds.Top >= -tolerance && bounds.Bottom <= ancestor.ActualHeight + tolerance,$"{item.Header}: full text height {text.ActualHeight:F2} is clipped by {ancestor.GetType().Name} ({ancestor.ActualHeight:F2})");
                    if (VisualTreeHelper.GetClip(ancestor) is Geometry clip)
                    {
                        var allowed = clip.Bounds; allowed.Inflate(tolerance,tolerance);
                        Require(allowed.Contains(bounds),$"{item.Header}: text escapes an ancestor clip");
                    }
                }
                if (ReferenceEquals(parent,window.FindName("TitleArea"))) break;
            }
            Require(Math.Abs(text.TranslatePoint(new(0,text.ActualHeight / 2),window).Y - titleCenter) < 1,$"{item.Header}: title/menu text centers differ");
        }
    }
    private static void CaptureWindow(Window window, string path)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        CaptureHandle(handle,path);
    }
    private static void CaptureHandle(nint handle,string path)
    {
        Require(GetWindowRect(handle, out var rect), "Cannot obtain owned window bounds");
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, rect.Right - rect.Left, rect.Bottom - rect.Top), old = SelectObject(memory, bitmap);
        try
        {
            Require(PrintWindow(handle, memory, 2), "Cannot capture owned window");
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); using var file = File.Create(path); encoder.Save(file);
        }
        finally { SelectObject(memory, old); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(0, screen); }
    }
    private static IEnumerable<FolderNode> Flatten(FolderNode root) { yield return root; foreach (var child in root.Children) foreach (var descendant in Flatten(child)) yield return descendant; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor,Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint handle,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetDC(nint handle);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint handle, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint handle, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
