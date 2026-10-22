using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using zStudio.Core;
using zStudio.Desktop;
using zStudio.Rendering;

internal static class SceneControlsCheck
{
    public static int Run(string root)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false };
        app.InitializeComponent();
        int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow;
            try
            {
                await window.ViewModel.OpenRootAsync(Path.GetFullPath(root));
                var file = window.ViewModel.Files.First(f => f.Probe.Family == FormatFamily.GameZ);
                await window.ViewModel.OpenFileAsync(file.Path);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var empty = (TextBlock)window.FindName("EmptyPreview");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                while (empty.Visibility == Visibility.Visible)
                {
                    if (empty.Text.StartsWith("Preview unavailable", StringComparison.Ordinal)) throw new InvalidDataException(empty.Text);
                    await Task.Delay(50, timeout.Token);
                }
                var scene = (SceneViewport)((ContentControl)window.FindName("SceneHost")).Content;
                var viewport = scene.RenderSurface;
                var camera = scene.DebugCamera;
                if (camera.IsOrthographic) throw new InvalidOperationException("No perspective camera attached.");
                Point3D sceneCenter = camera.Position + camera.LookDirection;
                scene.InertiaEnabled = false;
                if (SceneViewport.Gesture(MouseButton.Middle, ModifierKeys.None) != SceneViewport.NavigationGesture.Orbit
                    || SceneViewport.Gesture(MouseButton.Middle, ModifierKeys.Shift) != SceneViewport.NavigationGesture.Pan
                    || SceneViewport.Gesture(MouseButton.Middle, ModifierKeys.Control) != SceneViewport.NavigationGesture.Zoom
                    || SceneViewport.Gesture(MouseButton.Middle, ModifierKeys.Control | ModifierKeys.Shift) != SceneViewport.NavigationGesture.Dolly)
                    throw new InvalidOperationException("Pan/rotate gesture mapping is incorrect.");

                var textured = scene.EnumerateRenderMeshes().Where(m => m.Role == SceneViewport.RenderMeshRole.Scene && !m.IsTransparent && m.HasDiffuseMap).ToArray();
                if (textured.Length == 0 || textured.Any(m => !m.MaterialUnlit)) throw new InvalidOperationException("Textured materials are not using original-color rendering.");
                scene.SetTextured(false);
                if (textured.Any(m => m.HasDiffuseMap || m.MaterialUnlit)) throw new InvalidOperationException("Untextured inspection mode failed.");
                scene.SetTextured(true);
                if (textured.Any(m => !m.HasDiffuseMap || !m.MaterialUnlit)) throw new InvalidOperationException("Texture restoration failed.");

                Vector3 surface = default;
                bool found = false;
                for (int y = 3; y <= 7 && !found; y++)
                    for (int x = 3; x <= 7 && !found; x++)
                    {
                        // The map surface navigation itself uses (never the horizon backdrop).
                        if (scene.TryNavigationSurface(new Point(viewport.ActualWidth * x / 10, viewport.ActualHeight * y / 10), out var hit))
                        { surface = new((float)hit.X, (float)hit.Y, (float)hit.Z); found = true; }
                    }
                if (!found) throw new InvalidOperationException("Could not find a map surface for navigation checks.");
                double height = Math.Max(1, camera.LookDirection.Length * 0.01);
                Point center = new(viewport.ActualWidth / 2, viewport.ActualHeight / 2);
                scene.SetFly(false);
                // Restored over the surface, the view orbits and zooms about it (a pose written straight to the camera would
                // keep the framing pivot and swing the eye about it when the upright pitch limit applies). Centered zoom keeps
                // this terrain target while the surface under the pointer is no farther than it.
                scene.RestoreView(new(new(surface.X, surface.Y + height, surface.Z), new(0, -height, 0), new(0, 0, -1), scene.CaptureView().FieldOfView));
                for (int step = 0; step < 25; step++)
                {
                    await Task.Delay(25, timeout.Token);
                    scene.ZoomAt(center, 120);
                }
                double remaining = camera.Position.Y - surface.Y;
                if ((camera.Position + camera.LookDirection - new Point3D(surface.X, surface.Y, surface.Z)).Length > .001)
                    throw new InvalidOperationException("Centered zoom changed its target.");
                if (remaining <= camera.NearPlaneDistance || remaining >= height * 0.1)
                    throw new InvalidOperationException($"Orbit zoom failed: {height} → {remaining} above the map.");
                Console.WriteLine($"Orbit zoom: {height:F3} → {remaining:F3} above the map, near clip {camera.NearPlaneDistance:F4}");
                await Task.Delay(100, timeout.Token);
                Save(scene.RenderImage(960, 640), Path.Combine(Path.GetTempPath(), "zbd-scene-controls-close.png"));
                Point3D beforePan = camera.Position;
                Vector3D beforeDirection = camera.LookDirection;
                scene.PanBy(12, 8);
                if (camera.Position == beforePan || camera.LookDirection != beforeDirection) throw new InvalidOperationException("Pan did not translate the camera without rotating.");

                // Render a constant-color patch through a real scene material and verify its pixel values.
                scene.ClearRenderItems();
                var patch = scene.AddFixtureMesh(new([new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)], [0, 1, 2, 0, 2, 3])
                {
                    Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                    TextureCoordinates = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                    MaterialFrom = textured[0], Texture = ([40, 80, 120, 255], 1, 1),
                    Instances = [Matrix4x4.CreateTranslation((float)sceneCenter.X, (float)sceneCenter.Y, (float)sceneCenter.Z)]
                });
                camera.Position = sceneCenter + new Vector3D(0, 0, 4); camera.LookDirection = new(0, 0, -4); camera.UpDirection = new(0, 1, 0);
                scene.SetNavigationAids(false, false);
                await Task.Delay(150, timeout.Token);
                var rendered = scene.RenderImage(128, 128);
                var pixels = new FormatConvertedBitmap(rendered, PixelFormats.Bgra32, null, 0);
                byte[] pixel = new byte[4];
                pixels.CopyPixels(new Int32Rect(pixels.PixelWidth / 2, pixels.PixelHeight / 2, 1, 1), pixel, 4, 0);
                if (Math.Abs(pixel[2] - 40) > 1 || Math.Abs(pixel[1] - 80) > 1 || Math.Abs(pixel[0] - 120) > 1)
                    throw new InvalidOperationException($"Original RGB (40,80,120) rendered as ({pixel[2]},{pixel[1]},{pixel[0]}).");
                Save(rendered, Path.Combine(Path.GetTempPath(), "zbd-scene-color-check.png"));
                Console.WriteLine($"PASS: RGB (40,80,120) renders as ({pixel[2]},{pixel[1]},{pixel[0]}); texture toggle, centered close zoom, screen pan, and Blender middle/Shift-middle/Ctrl-middle/Ctrl-Shift-middle gestures. Captured freecam is checked separately.");
                scene.RemoveFixtureMesh(patch);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
