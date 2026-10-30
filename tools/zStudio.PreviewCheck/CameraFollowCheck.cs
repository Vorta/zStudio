using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using zStudio.Core.Animation;
using zStudio.Desktop;

internal static class CameraFollowCheck
{
    public static int Run(string root)
    {
        string output = Path.Combine(Path.GetTempPath(), "zbd-camera-follow-" + Path.GetFileName(root) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(output);
        string settings = zStudio.Desktop.StudioSettings.SettingsPath;
        byte[]? original = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow; window.Width = 1740; window.Height = 980; window.Left = -12000;
            try
            {
                await window.ViewModel.OpenRootAsync(Path.GetFullPath(root));
                var document = await window.ViewModel.OpenFileAsync(Path.Combine(Path.GetFullPath(root), "m1", "anim.zbd")) ?? throw new InvalidDataException("Missing animation document");
                var asset = document.Assets.First(a => a.Name == "start_single_player"); document.SelectedAsset = asset;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                AnimationEditor editor;
                while (true)
                {
                    if (((ContentControl)window.FindName("AnimationHost")).Content is AnimationEditor current && current.EntryIndex == asset.Index && current.CurrentFrame != null && ((Border)current.FindName("LoadingPanel")).Visibility == Visibility.Collapsed) { editor = current; break; }
                    await Task.Delay(100, timeout.Token);
                }
                editor.SetTransport(newMuted: true);
                var follow = (System.Windows.Controls.Primitives.ToggleButton)editor.FindName("FollowCamera");
                var camera = editor.Viewport.DebugCamera;
                await editor.SeekAsync(.1);
                follow.IsChecked = true; await Task.Delay(150);
                CheckPose(editor.CurrentFrame!.Camera!);
                Require(editor.FollowCamera.ToolTip is string followed && followed.Contains("camera1 #2", StringComparison.Ordinal) && editor.CameraLabel.StartsWith("camera1 #2", StringComparison.Ordinal), "Follow camera lacks the camera's identity");
                ((System.Windows.Controls.Primitives.ToggleButton)editor.FindName("ShowLevel")).IsChecked = true;
                while (((Border)editor.FindName("LoadingPanel")).Visibility == Visibility.Visible) await Task.Delay(100, timeout.Token);
                foreach (double time in new double[] { .1, 5, 10, 15, 20 })
                {
                    await editor.SeekAsync(time); await Task.Delay(200, timeout.Token);
                    CheckPose(editor.CurrentFrame!.Camera!);
                    var bitmap = editor.Viewport.RenderImage(960, 640);
                    PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, $"intro-{time:00.0}s.png")); encoder.Save(stream);
                }
                follow.IsChecked = false; var free = camera.Position;
                await editor.SeekAsync(5); await Task.Delay(150);
                Require((camera.Position - free).Length < .001, "Follow off still overwrites the free camera");
                follow.IsChecked = true; await Task.Delay(150); CheckPose(editor.CurrentFrame!.Camera!);
                editor.TogglePlayback();
                await Task.Delay(600, timeout.Token); editor.Pause(); await Task.Delay(150);
                Require(editor.CurrentFrame!.Time > 5, "Playback did not advance"); CheckPose(editor.CurrentFrame.Camera!);
                Console.WriteLine("PASS: immediate paused toggle, correct world eye/direction/FOV, mission context, seek/playback following, free camera when unchecked and camera identity in status.");
                Console.WriteLine("Screenshots: " + output);
                void CheckPose(AnimationCamera expected)
                {
                    var p = expected.Position; var f = expected.Target - p;
                    Require((camera.Position - new System.Windows.Media.Media3D.Point3D(p.X, p.Y, p.Z)).Length < .001, $"Viewport eye {camera.Position} differs from authored {p} at {editor.CurrentFrame!.Time:0.000}s");
                    var look = camera.LookDirection; look.Normalize();
                    Require((look - new System.Windows.Media.Media3D.Vector3D(f.X, f.Y, f.Z)).Length < .001, "Viewport camera direction differs from authored negative Z");
                    Require(Math.Abs(camera.FieldOfView - 60) < .01, "Viewport FOV is not 60 degrees");
                    Require(camera.UpDirection.Y > 0, "Follow camera inverted upright controls");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        try { app.Run(); } finally { if (original != null) File.WriteAllBytes(settings, original); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
