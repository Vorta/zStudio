using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;

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
                var viewport = (Viewport3DX)scene.Content;
                var camera = viewport.Camera as HCamera ?? throw new InvalidOperationException("No perspective camera attached.");
                Point3D sceneCenter = camera.Position + camera.LookDirection;
                viewport.IsInertiaEnabled = false;
                var bindings = viewport.InputBindings.OfType<MouseBinding>().ToArray();
                bool Bound(MouseAction action, ModifierKeys modifiers, ICommand command) => bindings.Any(b => b.Gesture is MouseGesture g && g.MouseAction == action && g.Modifiers == modifiers && b.Command == command);
                if (!Bound(MouseAction.MiddleClick, ModifierKeys.None, ViewportCommands.Pan)
                    || !Bound(MouseAction.RightClick, ModifierKeys.Shift, ViewportCommands.Pan)
                    || !Bound(MouseAction.RightClick, ModifierKeys.None, ViewportCommands.Rotate))
                    throw new InvalidOperationException("Pan/rotate gesture mapping is incorrect.");

                var textured = viewport.Items.OfType<MeshGeometryModel3D>().Select(m => m.Material).OfType<DiffuseMaterial>().Distinct().Where(m => m.DiffuseMap != null).ToArray();
                if (textured.Length == 0 || textured.Any(m => !m.EnableUnLit)) throw new InvalidOperationException("Textured materials are not using original-color rendering.");
                scene.SetTextured(false);
                if (textured.Any(m => m.DiffuseMap != null || m.EnableUnLit)) throw new InvalidOperationException("Untextured inspection mode failed.");
                scene.SetTextured(true);
                if (textured.Any(m => m.DiffuseMap == null || !m.EnableUnLit)) throw new InvalidOperationException("Texture restoration failed.");

                Vector3 surface = default;
                bool found = false;
                for (int y = 3; y <= 7 && !found; y++)
                    for (int x = 3; x <= 7 && !found; x++)
                        found = viewport.FindNearest(new Vector2((float)viewport.ActualWidth * x / 10, (float)viewport.ActualHeight * y / 10), out surface, out _, out _);
                if (!found) throw new InvalidOperationException("Could not find a map surface for navigation checks.");
                double height = Math.Max(1, camera.LookDirection.Length * 0.01);
                Point center = new(viewport.ActualWidth / 2, viewport.ActualHeight / 2);
                foreach (bool fly in new[] { false, true })
                {
                    scene.SetFly(fly);
                    camera.Position = new(surface.X, surface.Y + height, surface.Z);
                    camera.LookDirection = new(0, -height * 0.1, 0); // Stale target still far above the terrain.
                    camera.UpDirection = new(0, 0, -1);
                    for (int step = 0; step < 25; step++)
                    {
                        await Task.Delay(25, timeout.Token);
                        scene.ZoomAt(center, 120);
                    }
                    double remaining = camera.Position.Y - surface.Y;
                    if (remaining <= camera.NearPlaneDistance || remaining >= height * 0.1)
                        throw new InvalidOperationException($"{(fly ? "Fly" : "Orbit")} zoom failed: {height} → {remaining} above the map.");
                    Console.WriteLine($"{(fly ? "Fly" : "Orbit")} zoom: {height:F3} → {remaining:F3} above the map, near clip {camera.NearPlaneDistance:F4}");
                }
                await Task.Delay(100, timeout.Token);
                Save(scene.RenderImage(960, 640), Path.Combine(Path.GetTempPath(), "zbd-scene-controls-close.png"));
                Point3D beforePan = camera.Position;
                Vector3D beforeDirection = camera.LookDirection;
                viewport.AddPanForce(12, 8);
                if (camera.Position == beforePan || camera.LookDirection != beforeDirection) throw new InvalidOperationException("Pan did not translate the camera without rotating.");

                // Render a constant-color patch through a real scene material and verify its pixel values.
                var material = (DiffuseMaterial)textured[0].Clone();
                material.DiffuseMap = new TextureModel(new byte[] { 40, 80, 120, 255 }, SharpDX.DXGI.Format.R8G8B8A8_UNorm, 1, 1);
                viewport.Items.Clear();
                using MeshGeometryModel3D patch = new()
                {
                    Geometry = new MeshGeometry3D
                    {
                        Positions = new Vector3Collection([new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)]),
                        Normals = new Vector3Collection([Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ]),
                        TextureCoordinates = new Vector2Collection([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]),
                        Indices = new IntCollection([0, 1, 2, 0, 2, 3])
                    },
                    Material = material,
                    Instances = new[] { Matrix4x4.CreateTranslation((float)sceneCenter.X, (float)sceneCenter.Y, (float)sceneCenter.Z) },
                    CullMode = SharpDX.Direct3D11.CullMode.None
                };
                viewport.Items.Add(patch);
                camera.Position = sceneCenter + new Vector3D(0, 0, 4); camera.LookDirection = new(0, 0, -4); camera.UpDirection = new(0, 1, 0);
                viewport.ShowCoordinateSystem = viewport.ShowViewCube = false;
                await Task.Delay(150, timeout.Token);
                var rendered = scene.RenderImage(128, 128);
                var pixels = new FormatConvertedBitmap(rendered, PixelFormats.Bgra32, null, 0);
                byte[] pixel = new byte[4];
                pixels.CopyPixels(new Int32Rect(pixels.PixelWidth / 2, pixels.PixelHeight / 2, 1, 1), pixel, 4, 0);
                if (Math.Abs(pixel[2] - 40) > 1 || Math.Abs(pixel[1] - 80) > 1 || Math.Abs(pixel[0] - 120) > 1)
                    throw new InvalidOperationException($"Original RGB (40,80,120) rendered as ({pixel[2]},{pixel[1]},{pixel[0]}).");
                Save(rendered, Path.Combine(Path.GetTempPath(), "zbd-scene-color-check.png"));
                Console.WriteLine($"PASS: RGB (40,80,120) renders as ({pixel[2]},{pixel[1]},{pixel[0]}); texture toggle, orbit/fly close zoom, pan translation, and middle/Shift-right/right gesture bindings.");
                viewport.Items.Remove(patch);
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
