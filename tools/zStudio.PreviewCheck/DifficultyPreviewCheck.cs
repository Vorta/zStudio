using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;

internal static class DifficultyPreviewCheck
{
    public static int Run(string rootArg)
    {
        string root = Path.GetFullPath(rootArg);
        string output = Path.Combine(Path.GetTempPath(), "zbd-difficulty-ui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        byte[]? originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow; window.Width = 1740; window.Height = 980; window.Left = -12000;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try
            {
                window.ViewModel.Difficulty = MissionDifficulty.Medium;
                await window.ViewModel.OpenRootAsync(root);
                string animPath = Path.Combine(root, "m1", "anim.zbd"); byte[] hash = SHA256.HashData(File.ReadAllBytes(animPath));
                var document = await window.ViewModel.OpenFileAsync(animPath) ?? throw new InvalidDataException("Missing animation");
                var asset = document.Assets.First(a => a.Name == "start_single_player"); document.SelectedAsset = asset;
                AnimationEditor editor;
                while (true)
                {
                    if (((ContentControl)window.FindName("AnimationHost")).Content is AnimationEditor current && current.EntryIndex == asset.Index && current.CurrentFrame != null && ((Border)current.FindName("LoadingPanel")).Visibility == Visibility.Collapsed) { editor = current; break; }
                    await Task.Delay(60, timeout.Token);
                }
                var picker = (ComboBox)editor.FindName("Difficulty"); var map = (CheckBox)editor.FindName("ShowLevel");
                var follow = (CheckBox)editor.FindName("FollowCamera"); var height = (TextBox)editor.FindName("PreviewHeight");
                var row = (StackPanel)editor.FindName("ViewOptionsRow");
                Require(row.Children.IndexOf(picker) == row.Children.IndexOf(map) + 1, "Difficulty must immediately follow Map");
                Require((MissionDifficulty)picker.SelectedItem == MissionDifficulty.Medium, "Initial difficulty is not Medium");
                Require(((CheckBox)editor.FindName("Mute")).IsChecked == false, "Mute default changed");
                ((Slider)editor.FindName("Volume")).Value = 0;
                int outputCount = editor.Audio.OutputInitializations;
                map.IsChecked = true; await Ready(); height.Text = "100"; await Ready();
                var lod = (ComboBox)editor.FindName("Lod"); lod.SelectedIndex = Math.Min(1, lod.Items.Count - 1); await Ready(); int originalLod = lod.SelectedIndex;
                ((TextBox)editor.FindName("EndTime")).Text = "200";
                ((TextBox)editor.FindName("EndTime")).RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent)); await Ready();
                await editor.SeekAsync(2); var pose = editor.Viewport.CaptureView(); var selectedEvent = ((DataGrid)editor.FindName("Events")).SelectedItem;
                picker.SelectedItem = MissionDifficulty.Easy; await Ready();
                Require(editor.Viewport.Mission!.Layout.Difficulty == MissionDifficulty.Easy && AivCount(editor.Viewport) == 80, "Easy layout was not applied");
                Require(Math.Abs(editor.CurrentFrame!.Time - 2) < .001 && !editor.IsPlaying, "Paused playhead changed");
                Require(SameView(pose, editor.Viewport.CaptureView()), "Difficulty reframed camera");
                Require(lod.SelectedIndex == originalLod && height.Text == "100" && ((Slider)editor.FindName("SeekSlider")).Maximum == 200, "Difficulty reset preview controls");
                Require(ReferenceEquals(selectedEvent, ((DataGrid)editor.FindName("Events")).SelectedItem), "Difficulty reset event selection");
                Require(!document.AnimationEdits!.IsDirty, "Difficulty created an authored edit");
                follow.IsChecked = true; await editor.SeekAsync(3); await Task.Delay(80, timeout.Token); CheckFollow();
                picker.SelectedItem = MissionDifficulty.Hard; await Ready(); CheckFollow();
                Require(AivCount(editor.Viewport) == 87, "Hard layout did not add its actors");
                await editor.SeekAsync(4); await Task.Delay(80, timeout.Token); CheckFollow();
                editor.TogglePlayback(); await Task.Delay(150, timeout.Token); double before = editor.CurrentFrame.Time;
                picker.SelectedItem = MissionDifficulty.Easy; await Task.Delay(10, timeout.Token);
                picker.SelectedItem = MissionDifficulty.Medium; picker.SelectedItem = MissionDifficulty.Hard; await Ready();
                Require(editor.IsPlaying && editor.CurrentFrame.Time >= before && editor.Viewport.Mission!.Layout.Difficulty == MissionDifficulty.Hard, "Rapid changes lost latest difficulty or playback");
                await Task.Delay(150, timeout.Token); editor.Pause();
                Require(editor.Audio.IsPrepared && editor.Audio.OutputInitializations == outputCount, "Difficulty reinitialized audio output");
                map.IsChecked = false; await Ready(); picker.SelectedItem = MissionDifficulty.Easy; await Ready();
                Require(picker.IsEnabled && editor.Viewport.Mission!.Layout.Difficulty == MissionDifficulty.Easy, "Hidden map retained wrong binding context");
                ((Expander)editor.FindName("PreviewOptionsSection")).IsExpanded = true;
                picker.BringIntoView(); await Capture("animation-easy");
                window.Width = 1000; await Capture("animation-narrow");
                Require(((Button)editor.FindName("PlayButton")).IsVisible && ((Slider)editor.FindName("SeekSlider")).ActualWidth >= 80, "Narrow toolbar displaced transport");
                window.Width = 1740;
                var worldDocument = await window.ViewModel.OpenFileAsync(Path.Combine(root, "m1", "gamez.zbd")) ?? throw new InvalidDataException("Missing world");
                var scene = await WorldReady(MissionDifficulty.Easy);
                var worldPicker = (ComboBox)window.FindName("WorldDifficulty"); Require((MissionDifficulty)worldPicker.SelectedItem == MissionDifficulty.Easy, "World picker did not inherit shared preference");
                var worldPose = scene.CaptureView(); worldPicker.SelectedItem = MissionDifficulty.Hard; scene = await WorldReady(MissionDifficulty.Hard);
                Require(AivCount(scene) == 87 && SameView(worldPose, scene.CaptureView()), "World difficulty changed camera or kept wrong tanks");
                worldPicker.SelectedItem = MissionDifficulty.Medium; worldPicker.SelectedItem = MissionDifficulty.Easy; scene = await WorldReady(MissionDifficulty.Easy);
                Require(AivCount(scene) == 80, "Rapid world changes did not keep latest layout");
                Require(StudioSettings.Load().Difficulty == MissionDifficulty.Easy, "Difficulty was not persisted");
                using (var reopened = new MainViewModel()) Require(reopened.Difficulty == MissionDifficulty.Easy, "New application state did not load saved difficulty");
                await Capture("whole-world-easy");
                Require(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(animPath))), "Animation source bytes changed");
                Console.WriteLine("PASS: shared/persisted difficulty; authored Easy/Medium/Hard actors; paused and playing changes; rapid cancellation; camera, follow, height, LOD, custom range and event selection; warm audio; Map off; narrow transport; source preservation.");
                Console.WriteLine("Screenshots: " + output);

                async Task Ready()
                {
                    while (!((Button)editor.FindName("PlayButton")).IsEnabled || ((Border)editor.FindName("LoadingPanel")).Visibility == Visibility.Visible) await Task.Delay(30, timeout.Token);
                    await Task.Delay(80, timeout.Token);
                }
                void CheckFollow()
                {
                    var camera = editor.CurrentFrame!.Camera ?? throw new InvalidDataException("Missing authored camera");
                    var position = editor.Viewport.CaptureView().Position;
                    Require(Math.Abs(position.X - camera.Position.X) + Math.Abs(position.Y - camera.Position.Y) + Math.Abs(position.Z - camera.Position.Z) < .05, "Follow camera stopped rendering after difficulty/seek");
                }
                async Task<SceneViewport> WorldReady(MissionDifficulty difficulty)
                {
                    while (true)
                    {
                        if (((ContentControl)window.FindName("SceneHost")).Content is SceneViewport live && live.Mission?.Layout.Difficulty == difficulty && ((TextBlock)window.FindName("EmptyPreview")).Visibility == Visibility.Collapsed) { await Task.Delay(100, timeout.Token); return live; }
                        await Task.Delay(60, timeout.Token);
                    }
                }
                async Task Capture(string name)
                {
                    window.UpdateLayout(); await Task.Delay(250, timeout.Token); window.UpdateLayout();
                    var dpi = VisualTreeHelper.GetDpi(window);
                    RenderTargetBitmap bitmap = new((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    bitmap.Render(window); PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        try { app.Run(); } finally { if (originalSettings != null) File.WriteAllBytes(settings, originalSettings); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    private static int AivCount(SceneViewport viewport) => viewport.Mission!.Actors.Count(a => a.PlacementSource.StartsWith("aiv", StringComparison.Ordinal));
    private static bool SameView(SceneViewport.ViewPose a, SceneViewport.ViewPose b) => (a.Position - b.Position).Length < .001 && (a.LookDirection - b.LookDirection).Length < .001 && Math.Abs(a.FieldOfView - b.FieldOfView) < .001;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
