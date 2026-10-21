using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using zStudio.Core;
using zStudio.Desktop;
using zStudio.Rendering;

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
                var camera = scene.DebugCamera;
                if (camera.IsOrthographic) throw new InvalidOperationException("No perspective camera.");
                scene.InertiaEnabled = false;
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
                    Save(scene.RenderCurrentFrame(), Path.Combine(output, $"world-{factor}x.png"));
                }

                // Two almost adjacent, opaque instanced surfaces at map scale.
                // The red front surface must win regardless of draw order or camera distance.
                float size = (float)(framedDistance * 0.12), gap = (float)(framedDistance * 0.00001);
                var front = Surface(0, new Vector4(1, 0, 0, 1));
                var back = Surface(-gap, new Vector4(0, 1, 0, 1));
                scene.ClearRenderItems();
                scene.SetNavigationAids(false, false);
                foreach (int factor in new[] { 1, 2, 4, 8 })
                {
                    double distance = framedDistance * factor;
                    camera.Position = center + new Vector3D(0, 0, distance);
                    camera.LookDirection = new(0, 0, -distance); camera.UpDirection = new(0, 1, 0);
                    foreach (bool frontFirst in new[] { true, false })
                    {
                        scene.ClearRenderItems();
                        var first = scene.AddFixtureMesh(frontFirst ? front : back);
                        var second = scene.AddFixtureMesh(frontFirst ? back : front);
                        await Task.Delay(100, timeout.Token);
                        var rendered = scene.RenderCurrentFrame();
                        scene.RemoveFixtureMesh(first); scene.RemoveFixtureMesh(second);
                        Save(rendered, Path.Combine(output, $"overlap-{factor}x-{(frontFirst ? "front-first" : "back-first")}.png"));
                        var bitmap = new FormatConvertedBitmap(rendered, PixelFormats.Bgra32, null, 0);
                        byte[] pixel = new byte[4];
                        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
                        Console.WriteLine($"{factor}x, front first {frontFirst}, near {camera.NearPlaneDistance:G6}, far {camera.FarPlaneDistance:G6}: RGB ({pixel[2]},{pixel[1]},{pixel[0]})");
                        if (pixel[2] < 250 || pixel[1] > 5 || pixel[0] > 5)
                            throw new InvalidOperationException("A rear surface is showing through the opaque front surface.");
                    }
                }
                scene.ClearRenderItems();
                Console.WriteLine("PASS: opaque depth occlusion at 1x/2x/4x/8x overview distance in both draw orders.");

                SceneViewport.FixtureMesh Surface(float z, Vector4 color) => new([new(-size, -size, z), new(size, -size, z), new(size, size, z), new(-size, size, z)], [0, 1, 2, 0, 2, 3])
                {
                    Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                    TextureCoordinates = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                    Instances = [Matrix4x4.CreateTranslation((float)center.X, (float)center.Y, (float)center.Z)],
                    Color = color, Unlit = true
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
