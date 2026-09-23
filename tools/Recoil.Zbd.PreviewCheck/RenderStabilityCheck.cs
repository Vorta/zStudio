using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Rendering;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

internal static class RenderStabilityCheck
{
    public static int Run(string root)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; int exit = 0;
        app.Startup += async (_, _) =>
        {
            using var preview = new SceneViewport();
            var window = new Window { Content = preview, Width = 900, Height = 650, Left = -12000, ShowInTaskbar = false };
            window.Show();
            using var resolver = new AssetResolver(Path.GetFullPath(root));
            try
            {
                GameScene scene = new();
                scene.Materials.Add(new() { ["alpha"] = 255, ["color"] = new System.Text.Json.Nodes.JsonObject { ["r"] = 255, ["g"] = 0, ["b"] = 0 } });
                scene.Materials.Add(new() { ["alpha"] = 255, ["color"] = new System.Text.Json.Nodes.JsonObject { ["r"] = 0, ["g"] = 255, ["b"] = 0 } });
                scene.Models.Add(Plane(0)); scene.Models.Add(Plane(1));
                var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = new("fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, ""), ReadOnlyMemory<byte>.Empty) { Scene = scene } };
                foreach (bool instanced in new[] { false, true })
                foreach (bool frontFirst in new[] { true, false })
                {
                    AnimationNodePose[] surfaces = [new(1, 0, 0, Matrix4x4.CreateTranslation(2041, 32, 1693), true, 1, -1, 0, 0), new(2, 1, 1, Matrix4x4.CreateTranslation(2041, 32, 1692.999f), true, 1, -1, 0, 0)];
                    if (!frontFirst) Array.Reverse(surfaces);
                    AnimationFrame frame = new(0, [..surfaces, new(3, 2, 0, Matrix4x4.CreateTranslation(0, -100, 0), true, 1, -1, 0, 0), new(4, 3, 0, Matrix4x4.CreateTranslation(4000, 100, 4000), true, 1, -1, 0, 0)], [], [], [], null, null, Vector4.Zero, Vector4.Zero, [], [], []);
                    if (instanced)
                    {
                        scene.Nodes.Clear(); scene.Nodes.Add(new(0, "world", "world", null, [], [1, 2, 3, 4], [], []));
                        foreach (var pose in frame.Nodes)
                        {
                            var p = pose.Transform.Translation; int index = scene.Nodes.Count;
                            scene.Nodes.Add(new(index, "surface", "object3d", pose.Model, [0], [], new() { ["flags"] = 4 }, new() { ["transform"] = new System.Text.Json.Nodes.JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, p.X, p.Y, p.Z) }));
                        }
                        await preview.ShowAsync(context.World, new() { Id = new("fixture", AssetKind.Node, 0), Name = "world", Content = scene.Nodes[0] }, resolver, null, 0, default);
                    }
                    else await preview.ShowAnimationAsync(context, frame, resolver, false, default);
                    var viewport = (Viewport3DX)preview.Content; viewport.ShowCoordinateSystem = viewport.ShowViewCube = false;
                    var camera = (HCamera)viewport.Camera!;
                    camera.Position = new(2041, 32, 1893); camera.LookDirection = new(0, 0, -200); camera.UpDirection = new(0, 1, 0);
                    await Task.Delay(300);
                    AssertRed(Capture(viewport), $"inside world, instanced {instanced}, front first {frontFirst}, near {camera.NearPlaneDistance:G6}");
                    // Use native inertia; read the last presented frame after it stops.
                    viewport.AddZoomForce(-.12);
                    await Task.Delay(100); AssertRed(Capture(viewport), "moving");
                    await Task.Delay(2400); var idle = Capture(viewport); AssertRed(idle, "idle 2 s");
                    await Task.Delay(8000); var later = Capture(viewport); AssertRed(later, "idle 10 s");
                    Require(Pixels(idle).SequenceEqual(Pixels(later)), "Displayed frame changed while idle.");
                    double width = viewport.RenderHost!.ActualWidth, height = viewport.RenderHost.ActualHeight;
                    var position = camera.Position;
                    for (int i = 0; i < 8; i++) { var export = preview.RenderImage(320, 240); Require(export.PixelWidth == 320 && export.PixelHeight == 240, "Wrong export size"); }
                    Require(viewport.RenderHost.ActualWidth == width && viewport.RenderHost.ActualHeight == height && camera.Position == position, "Capturing resized the live viewport or moved its camera.");
                }
                Console.WriteLine("PASS: inside-world opaque depth, animated/instanced, both draw orders, inertia, 2/10-second idle and repeated DPI captures.");
                var view = (Viewport3DX)preview.Content; var cam = (HCamera)view.Camera!;
                foreach (bool fly in new[] { false, true })
                {
                    preview.SetFly(fly); cam.Position = new(4, 5, 6); cam.LookDirection = new(0, 0, -10);
                    var anchor = fly ? cam.Position : cam.Position + cam.LookDirection;
                    foreach (double dy in new[] { -10000d, 10000d, -10000d, 10000d })
                    {
                        preview.RotateBy(2000, dy); await Task.Delay(30);
                        Require(cam.UpDirection.Y > 0 && Math.Abs(cam.LookDirection.Y / cam.LookDirection.Length) <= Math.Sin(89 * Math.PI / 180) + 1e-12, "Camera crossed a pole or rolled.");
                        Require(((fly ? cam.Position : cam.Position + cam.LookDirection) - anchor).Length < 1e-8, "Rotation moved the orbit target or Fly position.");
                    }
                    cam.UpDirection = new(0, -1, 0); await Task.Delay(100); Require(cam.UpDirection.Y > 0, "External camera roll was not removed.");
                    view.AddRotateForce(0, 1000); await Task.Delay(1800); Require(cam.UpDirection.Y > 0, "Native rotation inertia inverted the camera.");
                }
                Console.WriteLine("PASS: upright Orbit/Fly, large drags, pole limits, no roll, fixed orbit target/Fly position and native rotation inertia.");
                preview.SetFly(false);
                view.ChangeDirection(new(0, -10, 0), new(0, 0, -1), 300);
                await Task.Delay(600); Require(cam.UpDirection.Y > 0 && Math.Abs(cam.LookDirection.Y / cam.LookDirection.Length) <= Math.Sin(89 * Math.PI / 180) + 1e-12, "Top view animation crossed the pitch limit.");
                var animation = await resolver.OpenCachedAsync(Path.Combine(resolver.Root, "m1", "anim.zbd"), default);
                context = await AnimationPreviewContext.LoadAsync(animation.Animations!, animation.Path, resolver);
                var entry = context.Package.Entries.First(e => e.Name == "start_single_player");
                var player = new AnimationPlayer(context, entry.Index);
                string output = Path.Combine(Path.GetTempPath(), "zbd-render-stability-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(output);
                foreach (bool level in new[] { false, true })
                {
                    await preview.ShowAnimationAsync(context, player.Frame(), resolver, level, default);
                    await CheckReal($"start-frame0-level-{level}", true);
                }
                preview.UpdateAnimationFrame(player.AdvanceTo(1, true));
                await CheckReal("start-paused-after-play", true);
                preview.UpdateAnimationFrame(player.Frame() with { Camera = new(new(2041, 50, 1693), new(2041, -50, 1693), 60) }, true);
                await Task.Delay(150);
                Require((cam.Position - new Point3D(2041, 50, 1693)).Length < 1e-8 && cam.UpDirection.Y > 0, "Authored camera normalization moved its position or inverted its up vector.");
                await preview.ShowAsync(context.World, context.World.Assets.First(a => a.Kind == AssetKind.World), resolver, null, 0, default, true, context.Mission);
                await CheckReal("whole-world", true);
                var model = context.World.Assets.First(a => a.Kind == AssetKind.Model && a.Index == 384);
                await preview.ShowAsync(context.World, model, resolver, null, 0, default);
                await CheckReal("sandbags-model", false);
                Console.WriteLine("Images: " + output);

                async Task CheckReal(string label, bool world)
                {
                    if (world) { cam.Position = new(2065, 72, 1702); cam.LookDirection = new(-24, -40, -9); cam.UpDirection = new(0, 1, 0); }
                    await Task.Delay(400); view.AddZoomForce(-.12);
                    await Task.Delay(100); Save(Capture(view), Path.Combine(output, label + "-moving.png"));
                    await Task.Delay(2400); var idle = Capture(view); var position = cam.Position;
                    var identities = Meshes(view.Items).ToArray();
                    Save(idle, Path.Combine(output, label + "-idle2.png"));
                    await Task.Delay(8000); var later = Capture(view); Save(later, Path.Combine(output, label + "-idle10.png"));
                    Require(Pixels(idle).SequenceEqual(Pixels(later)) && cam.Position == position, label + " changed while idle.");
                    Require(identities.SequenceEqual(Meshes(view.Items)), label + " replaced mesh identities while idle.");
                    Console.WriteLine($"PASS {label}: stable displayed buffers at 2/10 seconds; near={cam.NearPlaneDistance:G6}, far={cam.FarPlaneDistance:G6}, animated meshes={preview.AnimationMeshCount}");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    private static GameModel Plane(int material) => new(material, [new(-40, -40, 0), new(40, -40, 0), new(40, 40, 0), new(-40, 40, 0)], [], [], [new(material, 0, [0, 1, 2, 3], [], [], [])], []);
    // Read the displayed buffer. RenderBitmap would force another frame and hide an idle-frame defect.
    internal static BitmapSource Capture(Viewport3DX viewport)
    {
        using var stream = new MemoryStream();
        HelixToolkit.SharpDX.Utilities.ScreenCapture.SaveWICTextureToBitmapStream(viewport.RenderHost!.EffectsManager!, viewport.RenderHost.RenderBuffer!.BackBuffer!.Resource as SharpDX.Direct3D11.Texture2D, stream);
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.StreamSource = stream; image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); return image;
    }
    private static void AssertRed(BitmapSource image, string label)
    {
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        Console.WriteLine($"{label}: RGB {pixel[2]},{pixel[1]},{pixel[0]}");
        if (pixel[2] < 250 || pixel[1] > 5 || pixel[0] > 5) throw new InvalidDataException("Rear surface shows through an opaque foreground surface.");
    }
    private static byte[] Pixels(BitmapSource image)
    { var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); byte[] data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0); return data; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Save(BitmapSource image, string path) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
    private static IEnumerable<MeshGeometryModel3D> Meshes(IEnumerable<Element3D> elements)
    { foreach (var e in elements) { if (e is MeshGeometryModel3D m) yield return m; else if (e is GroupModel3D g) foreach (var child in Meshes(g.Children)) yield return child; } }
}
