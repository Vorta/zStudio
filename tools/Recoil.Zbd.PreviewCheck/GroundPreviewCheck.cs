using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Desktop;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

internal static class GroundPreviewCheck
{
    public static int Run(string root)
    {
        string output = Path.Combine(Path.GetTempPath(), "zbd-ground-" + Path.GetFileName(root) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        byte[]? original = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow; window.Width = 1740; window.Height = 980; window.Left = -12000;
            try
            {
                await window.ViewModel.OpenRootAsync(Path.GetFullPath(root));
                var document = await window.ViewModel.OpenFileAsync(Path.Combine(Path.GetFullPath(root), "m1", "anim.zbd")) ?? throw new InvalidDataException("Missing animation document");
                var asset = document.Assets.First(a => a.Name == "vtol_destruction1"); document.SelectedAsset = asset;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                AnimationEditor editor;
                while (true)
                {
                    if (((ContentControl)window.FindName("AnimationHost")).Content is AnimationEditor loading)
                    {
                        string message = ((TextBlock)loading.FindName("LoadingText")).Text;
                        if (message.Contains("Event editing is still available", StringComparison.Ordinal)) throw new InvalidDataException(message);
                    }
                    if (((TextBlock)window.FindName("EmptyPreview")).Text is string error && error.StartsWith("Preview unavailable", StringComparison.Ordinal)) throw new InvalidDataException(error);
                    if (((ContentControl)window.FindName("AnimationHost")).Content is AnimationEditor current && current.EntryIndex == asset.Index && current.CurrentFrame != null && ((Border)current.FindName("LoadingPanel")).Visibility == Visibility.Collapsed) { editor = current; break; }
                    await Task.Delay(100, timeout.Token);
                }
                var grid = (CheckBox)editor.FindName("ShowGrid");
                Require(grid.IsChecked == true, "Grid must default checked");
                ((CheckBox)editor.FindName("Mute")).IsChecked = true;
                var viewport = (Viewport3DX)editor.Viewport.Content;
                var camera = (HCamera)viewport.Camera!;
                var plane = viewport.Items.OfType<AxisPlaneGridModel3D>().Single();
                Require(plane.Offset == 0 && !plane.IsHitTestVisible && plane.PlaneColor.A == 0 && plane.GridPattern == HelixToolkit.SharpDX.GridPattern.Grid, "Grid configuration invalid");
                foreach (double time in new[] { 1d, 5d, 8d })
                {
                    await editor.SeekAsync(time); editor.Viewport.FrameAnimation(editor.CurrentFrame!);
                    await Task.Delay(200, timeout.Token); Save(editor.Viewport.RenderImage(1100, 740), $"vtol-{time:00}s.png");
                }
                var eye = camera.Position; var look = camera.LookDirection; double near = camera.NearPlaneDistance, far = camera.FarPlaneDistance;
                grid.IsChecked = false; await Ready();
                Require(Math.Abs(editor.CurrentFrame!.Time - 8) < .001, "Paused grid toggle moved playhead");
                Require(!editor.IsPlaying && plane.Visibility == Visibility.Collapsed, "Paused grid toggle state wrong");
                Require(editor.CurrentFrame.Nodes.Any(n => n.Transform.M42 < -5), "Grid off still constrains debris");
                grid.IsChecked = true; await Ready();
                Require(Math.Abs(editor.CurrentFrame.Time - 8) < .001 && editor.Duration!.IsFinite, "Grid enable failed to reconstruct playhead/duration");
                Require(camera.Position == eye && camera.LookDirection == look, "Grid toggle reframed camera");
                await Task.Delay(200, timeout.Token);
                Require(Math.Abs(camera.NearPlaneDistance - near) < .001 && Math.Abs(camera.FarPlaneDistance - far) < .001, "Grid changed clip bounds at same pose");
                ((TextBox)editor.FindName("EndTime")).Text = "35";
                ((TextBox)editor.FindName("EndTime")).RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent)); await Ready();
                grid.IsChecked = false; await Ready(); grid.IsChecked = true; await Ready();
                Require(((Slider)editor.FindName("SeekSlider")).Maximum == 35, "Grid lost custom range");
                await editor.SeekAsync(.8);
                ((CheckBox)editor.FindName("Mute")).IsChecked = false; ((Slider)editor.FindName("Volume")).Value = 0;
                var play = (Button)editor.FindName("PlayButton"); play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(200, timeout.Token);
                double before = editor.CurrentFrame!.Time;
                grid.IsChecked = false; grid.IsChecked = true; await Ready();
                Require(editor.IsPlaying, "Rapid grid toggles lost playback state");
                await Task.Delay(350, timeout.Token); editor.Pause();
                Require(editor.CurrentFrame.Time > before, "Grid toggle did not resume playback");
                ((CheckBox)editor.FindName("ShowLevel")).IsChecked = true; await Ready();
                Require(viewport.Items.OfType<AxisPlaneGridModel3D>().Single().Visibility == Visibility.Visible, "Mission refresh lost grid");
                ((CheckBox)editor.FindName("ShowHorizon")).IsChecked = false; grid.IsChecked = false; await Ready();
                Require(((TextBox)editor.FindName("Diagnostics")).Text.Contains("grid and collision disabled", StringComparison.Ordinal), "Grid change during scene reload was lost");
                grid.IsChecked = true; await Ready();
                Require(viewport.Items.OfType<AxisPlaneGridModel3D>().Count() == 1, "Horizon refresh duplicated grid");
                ((CheckBox)editor.FindName("ShowLevel")).IsChecked = false; await Ready();
                await editor.SeekAsync(5); editor.Viewport.FrameAnimation(editor.CurrentFrame!);
                viewport.AddZoomForce(-.12); await Task.Delay(2600, timeout.Token);
                var idle = RenderStabilityCheck.Capture(viewport); await Task.Delay(2500, timeout.Token);
                var later = RenderStabilityCheck.Capture(viewport);
                Require(Pixels(idle).SequenceEqual(Pixels(later)), "Ground grid changed while idle"); Save(later, "vtol-idle.png");
                var height = (TextBox)editor.FindName("PreviewHeight");
                Require(height.Text == "0", "Height must default to zero");
                height.Focus(); height.ApplyTemplate();
                Require(height.Template.FindName("DeleteButton", height) is UIElement { Visibility: Visibility.Collapsed }, "Fluent height field still has a clear button");
                List<string> heightMessages = []; editor.StatusChanged += heightMessages.Add;
                await editor.SeekAsync(0); var originalPose = editor.CurrentFrame!; var originalDuration = editor.Duration!;
                height.Text = "4"; await Ready();
                foreach (var pose in originalPose.Nodes)
                    Require(Math.Abs(editor.CurrentFrame!.Nodes.Single(n => n.Id == pose.Id).Transform.M42 - pose.Transform.M42 - 4) < .001, "Typing alone did not update height");
                var acceptedFrame = editor.CurrentFrame;
                foreach (string text in new[] { "", ".", "1e", "NaN", "-1", "100001" })
                {
                    height.Text = text; await Ready();
                    Require(height.Text == text && ReferenceEquals(acceptedFrame, editor.CurrentFrame), "Incomplete/invalid text changed the preview or interrupted editing");
                }
                height.Text = "10";
                await Task.Delay(10, timeout.Token);
                height.Text = "25.5";
                height.Text = "100";
                await Ready();
                Require(editor.CurrentFrame!.Time == 0 && !editor.IsPlaying, "Height moved paused playhead");
                Require(editor.Duration!.IsFinite && editor.Duration.Frames > originalDuration.Frames, "Height did not recalculate falling duration");
                foreach (var pose in originalPose.Nodes)
                    Require(Math.Abs(editor.CurrentFrame.Nodes.Single(n => n.Id == pose.Id).Transform.M42 - pose.Transform.M42 - 100) < .001, "Height did not raise initial pose");
                Require(((Slider)editor.FindName("SeekSlider")).Maximum == 35, "Height lost custom range");
                Require(height.Text == "100", "Live height changed the edit text");
                height.Text = "NaN"; height.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent)); await Ready();
                Require(height.Text == "100", "Invalid height was accepted");
                Require(!heightMessages.Any(m => m.Contains("Height must", StringComparison.Ordinal)), "Typing or leaving an incomplete height raised an error");
                await editor.SeekAsync(.1);
                plane = viewport.Items.OfType<AxisPlaneGridModel3D>().Single();
                editor.Viewport.FrameAnimation(editor.CurrentFrame); await Task.Delay(200, timeout.Token);
                var target = camera.Position + camera.LookDirection;
                foreach (var (name, offset) in new[] { ("top-down", new Vector3D(0, 60, 0)), ("perspective", new Vector3D(30, 35, 65)) })
                {
                    camera.Position = target + offset; camera.LookDirection = -offset; camera.UpDirection = new(0, 1, 0);
                    await Task.Delay(200, timeout.Token);
                    var on = editor.Viewport.RenderImage(1100, 740); Save(on, "height100-" + name + ".png");
                    double sceneFar = camera.FarPlaneDistance;
                    plane.Visibility = Visibility.Collapsed; await Task.Delay(100, timeout.Token);
                    var off = editor.Viewport.RenderImage(1100, 740); plane.Visibility = Visibility.Visible;
                    Require(Math.Abs(sceneFar - camera.FarPlaneDistance) < .001, "Grid extended scene clipping planes");
                    var a = Pixels(on); var b = Pixels(off); int[] bands = new int[4];
                    for (int y = 0; y < 740; y++) for (int x = 0; x < 1100; x++)
                    {
                        int at = (y * 1100 + x) * 4;
                        if (Math.Abs(a[at] - b[at]) + Math.Abs(a[at + 1] - b[at + 1]) + Math.Abs(a[at + 2] - b[at + 2]) > 10) bands[y / 185]++;
                    }
                    Require(bands.All(n => n > 500), name + " grid faded out within the view: " + string.Join(",", bands));
                    if (name == "top-down") Require(sceneFar < camera.Position.Y, "Top-down test did not exercise grid beyond scene far clipping");
                    Console.WriteLine($"PASS grid {name}: pixels per screen quarter {string.Join(",", bands)}; near/far {camera.NearPlaneDistance:0.###}/{sceneFar:0.###}");
                }
                await editor.SeekAsync(1); play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150, timeout.Token);
                before = editor.CurrentFrame!.Time; height.Text = "4"; height.Text = "40"; await Ready();
                Require(editor.IsPlaying && editor.CurrentFrame.Time >= before, "Height did not preserve playback"); editor.Pause();
                var row = (StackPanel)editor.FindName("ViewOptionsRow");
                foreach (string name in new[] { "UndoButton", "RedoButton", "SaveAsButton", "Lod", "ShowGrid", "PreviewHeight", "ShowLevel", "ShowHorizon", "FollowCamera", "Lighting" })
                    Require(row.Children.Contains((UIElement)editor.FindName(name)), name + " is outside the single toolbar row");
                foreach (string name in new[] { "UndoButton", "RedoButton", "SaveAsButton" })
                {
                    var button = (Button)editor.FindName(name);
                    Require(button.Content is System.Windows.Shapes.Path && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), name + " lacks an accessible icon");
                }
                Require((string)((CheckBox)editor.FindName("ShowLevel")).Content == "Map", "Map label was not shortened");
                window.Width = 1000; await Task.Delay(250, timeout.Token);
                Require(grid.IsVisible && ((StackPanel)editor.FindName("ViewOptionsRow")).Children.Contains(grid), "Grid is outside options row");
                Require(!((TextBox)editor.FindName("Diagnostics")).Text.Contains("3D preview unavailable", StringComparison.Ordinal), "Render failure");
                Console.WriteLine("PASS: live height without Enter/blur, no clear button, quiet incomplete/invalid input, latest-value cancellation, pause/play preservation; grid visibility/contact, same playhead, finite duration, custom range, camera/clips, mission/horizon reload, narrow layout and idle framebuffer stability.");
                Console.WriteLine("Screenshots: " + output);
                async Task Ready()
                {
                    while (!((Button)editor.FindName("PlayButton")).IsEnabled || ((Border)editor.FindName("LoadingPanel")).Visibility == Visibility.Visible) await Task.Delay(30, timeout.Token);
                    await Task.Delay(60, timeout.Token);
                }
                void Save(BitmapSource image, string name) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(output, name)); encoder.Save(stream); }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        try { app.Run(); } finally { if (original != null) File.WriteAllBytes(settings, original); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    private static byte[] Pixels(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0); byte[] data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0); return data;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
