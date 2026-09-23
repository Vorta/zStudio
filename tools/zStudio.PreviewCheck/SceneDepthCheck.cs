using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;

internal static class SceneDepthCheck
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
                var camera = viewport.Camera as HCamera ?? throw new InvalidOperationException("No perspective camera.");
                viewport.IsInertiaEnabled = false;
                Point3D center = camera.Position + camera.LookDirection;
                Vector3D framedDirection = camera.LookDirection;
                double framedDistance = framedDirection.Length;
                string output = Path.Combine(Path.GetTempPath(), "zbd-scene-depth-preview");
                Directory.CreateDirectory(output);
                foreach (int factor in new[] { 1, 2, 4, 8 })
                {
                    camera.Position = center - framedDirection * factor;
                    camera.LookDirection = framedDirection * factor;
                    await Task.Delay(100, timeout.Token);
                    Save(viewport.RenderBitmap() ?? throw new InvalidOperationException("No rendered frame."), Path.Combine(output, $"world-{factor}x.png"));
                }

                // Two almost adjacent, opaque instanced surfaces at map scale.
                // The red front surface must win regardless of draw order or camera distance.
                float size = (float)(framedDistance * 0.12), gap = (float)(framedDistance * 0.00001);
                using var front = Surface(0, new Color4(1, 0, 0, 1));
                using var back = Surface(-gap, new Color4(0, 1, 0, 1));
                viewport.Items.Clear();
                viewport.ShowCoordinateSystem = viewport.ShowViewCube = false;
                foreach (int factor in new[] { 1, 2, 4, 8 })
                {
                    double distance = framedDistance * factor;
                    camera.Position = center + new Vector3D(0, 0, distance);
                    camera.LookDirection = new(0, 0, -distance); camera.UpDirection = new(0, 1, 0);
                    foreach (bool frontFirst in new[] { true, false })
                    {
                        viewport.Items.Clear();
                        viewport.Items.Add(frontFirst ? front : back);
                        viewport.Items.Add(frontFirst ? back : front);
                        await Task.Delay(100, timeout.Token);
                        var rendered = viewport.RenderBitmap() ?? throw new InvalidOperationException("No rendered frame.");
                        Save(rendered, Path.Combine(output, $"overlap-{factor}x-{(frontFirst ? "front-first" : "back-first")}.png"));
                        var bitmap = new FormatConvertedBitmap(rendered, PixelFormats.Bgra32, null, 0);
                        byte[] pixel = new byte[4];
                        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
                        Console.WriteLine($"{factor}x, front first {frontFirst}, near {camera.NearPlaneDistance:G6}, far {camera.FarPlaneDistance:G6}: RGB ({pixel[2]},{pixel[1]},{pixel[0]})");
                        if (pixel[2] < 250 || pixel[1] > 5 || pixel[0] > 5)
                            throw new InvalidOperationException("A rear surface is showing through the opaque front surface.");
                    }
                }
                viewport.Items.Clear();
                Console.WriteLine("PASS: opaque depth occlusion at 1x/2x/4x/8x overview distance in both draw orders.");

                MeshGeometryModel3D Surface(float z, Color4 color) => new()
                {
                    Geometry = new MeshGeometry3D
                    {
                        Positions = new Vector3Collection([new(-size, -size, z), new(size, -size, z), new(size, size, z), new(-size, size, z)]),
                        Normals = new Vector3Collection([Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ]),
                        TextureCoordinates = new Vector2Collection([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]),
                        Indices = new IntCollection([0, 1, 2, 0, 2, 3])
                    },
                    Instances = new[] { Matrix4x4.CreateTranslation((float)center.X, (float)center.Y, (float)center.Z) },
                    Material = new DiffuseMaterial { DiffuseColor = color, EnableUnLit = true },
                    CullMode = SharpDX.Direct3D11.CullMode.None
                };
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
