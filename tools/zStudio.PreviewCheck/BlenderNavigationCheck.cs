using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;
using ProjectionCamera = HelixToolkit.Wpf.SharpDX.ProjectionCamera;

internal static class BlenderNavigationCheck
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root);
        string output = Path.Combine(Path.GetTempPath(), "zstudio-blender-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        byte[]? previous = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            var main = (MainWindow)app.MainWindow; main.Width = 1550; main.Height = 940;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                await main.ViewModel.OpenRootAsync(root);
                string worldPath = Path.Combine(root, "m1", "gamez.zbd"), animationPath = Path.Combine(root, "m1", "anim.zbd");
                byte[] worldHash = SHA256.HashData(File.ReadAllBytes(worldPath)), animationHash = SHA256.HashData(File.ReadAllBytes(animationPath));
                var doc = await main.ViewModel.OpenFileAsync(worldPath) ?? throw new InvalidDataException("Missing world");
                doc.SelectedAsset = doc.Assets.First(a => a.Record.Kind == AssetKind.World);
                await Preview(); var scene = (SceneViewport)((ContentControl)main.FindName("SceneHost")).Content;
                await CheckViews(scene, "world");
                var pickup = scene.Mission!.Actors.First(a => a.Pickup != null);
                scene.SelectFramingNode(pickup.Root); scene.SelectPickup(pickup.Root, true, false);
                scene.SetAxisView("top"); Require(scene.TryFrame("selected"), "Pickup framing unavailable"); await Task.Delay(200);
                var surface = (Viewport3DX)scene.Content;
                var gizmo = (TransformManipulator3D)typeof(SceneViewport).GetField("pickupManipulator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scene)!;
                Require(gizmo.Visibility == Visibility.Visible, "Orthographic pickup gizmo hidden");
                double gizmoSize = gizmo.SizeScale; scene.DollyBy(-50); await Task.Delay(150);
                Require(Math.Abs(gizmo.SizeScale - gizmoSize) < 1e-6, "Orthographic handle size depends on depth");
                Save(Presented(surface), Path.Combine(output, "pickup-top.png"));
                var point = surface.Project(new Point3D(scene.PickupPosition(pickup.Root).X, scene.PickupPosition(pickup.Root).Y, scene.PickupPosition(pickup.Root).Z));
                Require(double.IsFinite(point.X) && double.IsFinite(point.Y), "Pickup projection invalid");
                var saved = scene.CaptureView();
                ((ComboBox)main.FindName("WorldDifficulty")).SelectedItem = MissionDifficulty.Hard;
                await Task.Delay(20); await Preview();
                scene = (SceneViewport)((ContentControl)main.FindName("SceneHost")).Content;
                Require(scene.CaptureView() == saved, "World difficulty lost orthographic camera state");

                doc.SelectedAsset = doc.Assets.First(a => a.Record.Kind == AssetKind.Model && doc.Document.Scene!.Models[a.Index].Vertices.Length > 0);
                await Preview(); scene = (SceneViewport)((ContentControl)main.FindName("SceneHost")).Content; await CheckViews(scene, "model");

                var animationDoc = await main.ViewModel.OpenFileAsync(animationPath) ?? throw new InvalidDataException("Missing animation");
                animationDoc.SelectedAsset = animationDoc.Assets.First(a => a.Name == "destroy_vtol1"); await Preview();
                var editor = (AnimationEditor)((ContentControl)main.FindName("AnimationHost")).Content;
                ((ToggleButton)editor.FindName("Mute")).IsChecked = true; await editor.SeekAsync(.5);
                var frame = editor.CurrentFrame; await CheckViews(editor.Viewport, "animation");
                Require(ReferenceEquals(frame, editor.CurrentFrame), "Navigation replaced the visible simulation frame");
                editor.Viewport.SetAxisView("top"); saved = editor.Viewport.CaptureView();
                await editor.SetPreviewOptionAsync("map", System.Text.Json.Nodes.JsonValue.Create(((ToggleButton)editor.FindName("ShowLevel")).IsChecked != true)!);
                Require(editor.Viewport.CaptureView() == saved, "Animation map refresh lost orthographic camera state");
                if (((ComboBox)editor.FindName("Lod")).Items.Count > 1)
                {
                    await editor.SetPreviewOptionAsync("lod", System.Text.Json.Nodes.JsonValue.Create(1)!);
                    Require(editor.Viewport.CaptureView() == saved, "Animation LOD lost orthographic camera state");
                }
                ((ToggleButton)editor.FindName("ShowLevel")).IsChecked = false;
                ((ToggleButton)editor.FindName("ShowLevel")).IsChecked = true;
                while (((Border)editor.FindName("LoadingPanel")).Visibility == Visibility.Visible) await Task.Delay(50, timeout.Token);
                Require(editor.Viewport.CaptureView() == saved, "Superseded map refresh lost orthographic camera state");
                main.Width = 1100; await Task.Delay(150); main.Width = 1550; await Task.Delay(150);
                Require(editor.Viewport.CaptureView() == saved, "Resize changed camera state");
                ((Button)editor.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(120); main.RunCameraNavigation("right"); Require(editor.IsPlaying, "View shortcut stopped playback"); editor.Pause();

                animationDoc.SelectedAsset = animationDoc.Assets.First(a => a.Name == "start_single_player"); await Preview();
                editor = (AnimationEditor)((ContentControl)main.FindName("AnimationHost")).Content;
                ((ToggleButton)editor.FindName("Mute")).IsChecked = true; await editor.SeekAsync(.1);
                main.RunCameraNavigation("follow"); Require(editor.Options.FollowCamera, "Numpad 0 equivalent failed to enable follow");
                Require(editor.CurrentFrame!.Camera != null, "Authored camera missing");
                double time = editor.CurrentFrame.Time;
                editor.Viewport.PanBy(20, 10); Require(!editor.Options.FollowCamera, "Manual pan did not exit follow");
                Require(editor.CurrentFrame.Time == time, "Manual navigation changed playhead");
                main.RunCameraNavigation("follow"); Require(editor.Options.FollowCamera, "Follow did not re-enable");
                main.RunCameraNavigation("top"); Require(!editor.Options.FollowCamera && editor.Viewport.CaptureView().AxisView == "top", "Axis view failed to leave follow");
                Require(worldHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(worldPath))) && animationHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(animationPath))), "Source bytes changed");
                Console.WriteLine("PASS: rendered world/model/animation axis views, projection scale, idle stability, centered zoom, pickup handles, refresh/resize retention, playback and camera follow; unchanged sources.");
                Console.WriteLine(output);

                async Task Preview() => await ((Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).WaitAsync(timeout.Token);
                async Task CheckViews(SceneViewport view, string label)
                {
                    var viewport = (Viewport3DX)view.Content;
                    Require(view.TryFrame("all"), label + " has no frameable geometry");
                    foreach (string axis in new[] { "front", "back", "left", "right", "top", "bottom" })
                    {
                        view.SetAxisView(axis); Require(view.TryFrame("all"), "Axis framing failed"); await Task.Delay(180, timeout.Token);
                        var pose = view.CaptureView(); var camera = (ProjectionCamera)viewport.Camera!;
                        Require(pose.AxisView == axis && pose.Projection == "orthographic", "Axis state changed on render");
                        var cubePoint = new Point(viewport.ActualWidth * (1 + viewport.ViewCubeHorizontalPosition) / 2, viewport.ActualHeight * (1 - viewport.ViewCubeVerticalPosition) / 2);
                        Require(view.NavigateCubeAt(cubePoint) && view.CaptureView().AxisView == axis, "View-cube face disagrees with named view: " + axis);
                        Require(camera.NearPlaneDistance > 0 && camera.FarPlaneDistance > camera.NearPlaneDistance, "Invalid orthographic clipping");
                        var image = Presented(viewport); Save(image, Path.Combine(output, label + "-" + axis + ".png"));
                        await Task.Delay(150, timeout.Token);
                        Require(Pixels(image).SequenceEqual(Pixels(Presented(viewport))), "Idle axis image changed: " + label + " " + axis);
                        var center = pose.Position + pose.LookDirection;
                        var right = Vector3D.CrossProduct(pose.LookDirection, pose.UpDirection); right.Normalize();
                        var sample = center + right * (pose.OrthographicWidth!.Value * .1);
                        var before = viewport.Project(sample);
                        view.SetProjection("perspective"); await Task.Delay(100, timeout.Token);
                        var after = viewport.Project(sample);
                        Require((after - before).Length < 1, "Projection transition changed apparent scale");
                        view.SetAxisView(axis); view.RotateBy(10, 5); await Task.Delay(60, timeout.Token);
                        Require(view.CaptureView().Projection == "perspective" && view.CaptureView().UpDirection.Y > 0, "Axis orbit failed to return upright perspective");
                    }
                    view.SetProjection("orthographic"); view.RotateBy(20, 10); await Task.Delay(100, timeout.Token);
                    Require(view.CaptureView().Projection == "orthographic", "Explicit orthographic mode was not retained");
                }
            }
            catch (Exception ex) { exit = 1; Console.Error.WriteLine(ex); Console.WriteLine(output); }
            finally { main.Close(); app.Shutdown(); }
        };
        try { app.Run(); } finally { if (previous != null) File.WriteAllBytes(settings, previous); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    private static BitmapSource Presented(Viewport3DX viewport)
    {
        using MemoryStream stream = new();
        HelixToolkit.SharpDX.Utilities.ScreenCapture.SaveWICTextureToBitmapStream(viewport.RenderHost!.EffectsManager!, viewport.RenderHost.RenderBuffer!.BackBuffer!.Resource as SharpDX.Direct3D11.Texture2D, stream);
        stream.Position = 0; return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(bytes, converted.PixelWidth * 4, 0); return bytes;
    }
    private static void Save(BitmapSource image, string path) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
