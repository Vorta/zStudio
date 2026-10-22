using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Rendering;

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
                var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = new AssetDocument("fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, ""), ReadOnlyMemory<byte>.Empty) { Scene = scene } };
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
                    preview.SetNavigationAids(false, false);
                    var camera = preview.DebugCamera;
                    camera.Position = new(2041, 32, 1893); camera.LookDirection = new(0, 0, -200); camera.UpDirection = new(0, 1, 0);
                    await Task.Delay(300);
                    AssertRed(preview.CapturePresentedFrame(), $"inside world, instanced {instanced}, front first {frontFirst}, near {camera.NearPlaneDistance:G6}");
                    // Wheel zoom with inertia; read the last presented frame after it stops.
                    preview.WheelZoom(Center(), 120);
                    await Task.Delay(100); AssertRed(preview.CapturePresentedFrame(), "moving");
                    await Task.Delay(2400); var idle = preview.CapturePresentedFrame(); AssertRed(idle, "idle 2 s");
                    await Task.Delay(8000); var later = preview.CapturePresentedFrame(); AssertRed(later, "idle 10 s");
                    Require(Pixels(idle).SequenceEqual(Pixels(later)), "Displayed frame changed while idle.");
                    var size = preview.PresentedPixelSize;
                    var position = camera.Position;
                    for (int i = 0; i < 8; i++) { var export = preview.RenderImage(320, 240); Require(export.PixelWidth == 320 && export.PixelHeight == 240, "Wrong export size"); }
                    Require(preview.PresentedPixelSize == size && camera.Position == position, "Capturing resized the live viewport or moved its camera.");
                }
                Console.WriteLine("PASS: inside-world opaque depth, animated/instanced, both draw orders, inertia, 2/10-second idle and repeated DPI captures.");
                var cam = preview.DebugCamera;
                foreach (bool fly in new[] { false, true })
                {
                    // A restored pose orbits about its own target (a pose written straight to the camera keeps the framing pivot).
                    preview.SetFly(fly); preview.RestoreView(new(new(4, 5, 6), new(0, 0, -10), new(0, 1, 0), 45));
                    var anchor = fly ? cam.Position : cam.Position + cam.LookDirection;
                    foreach (double dy in new[] { -10000d, 10000d, -10000d, 10000d })
                    {
                        preview.RotateBy(2000, dy); await Task.Delay(30);
                        Require(cam.UpDirection.Y > 0 && Math.Abs(cam.LookDirection.Y / cam.LookDirection.Length) <= Math.Sin(89 * Math.PI / 180) + 1e-12, "Camera crossed a pole or rolled.");
                        Require(((fly ? cam.Position : cam.Position + cam.LookDirection) - anchor).Length < 1e-8, "Rotation moved the orbit target or Fly position.");
                    }
                    cam.UpDirection = new(0, -1, 0); await Task.Delay(100); Require(cam.UpDirection.Y > 0, "External camera roll was not removed.");
                    var stopped = preview.CaptureView();
                    // A fast middle-button flick leaves orbit inertia past the pole; Fly refuses the gesture.
                    preview.BeginNavigationDrag(SceneViewport.NavigationGesture.Orbit, Center());
                    preview.MoveNavigationDrag(Center() + new System.Windows.Vector(0, 1000)); preview.EndNavigationDrag();
                    await Task.Delay(1800);
                    Require(cam.UpDirection.Y > 0 && Math.Abs(cam.LookDirection.Y / cam.LookDirection.Length) <= Math.Sin(89 * Math.PI / 180) + 1e-12, "Orbit inertia crossed a pole or inverted the camera.");
                    if (fly) Require(preview.CaptureView() == stopped, "An orbit drag changed captured freecam.");
                    preview.StopCameraMotion();
                }
                Console.WriteLine("PASS: upright Orbit/Fly, large drags, pole limits, no roll, fixed orbit target/Fly position and orbit inertia.");
                preview.SetFly(false);
                // A pose written straight down (as an external camera write) is clamped to the pitch limit on the next frame.
                cam.LookDirection = new(0, -10, 0); cam.UpDirection = new(0, 0, -1);
                await Task.Delay(600); Require(cam.UpDirection.Y > 0 && Math.Abs(cam.LookDirection.Y / cam.LookDirection.Length) <= Math.Sin(89 * Math.PI / 180) + 1e-12, "A straight-down camera crossed the pitch limit.");
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
                // WPF sees finished frames: it reads the shared surface on the view's Direct3D 9Ex device, unordered with the frame's
                // Direct3D 11 work, so with Wireframe (long frames) a read right after a frame was submitted showed it half drawn
                // (the cleared background where terrain goes). Each read must be opaque, free of background and match the
                // presented frame at this pose (a read of the previous frame at the same pose is the same picture).
                cam.Position = new(2065, 72, 1702); cam.LookDirection = new(-24, -40, -9); cam.UpDirection = new(0, 1, 0);
                preview.SetWireframe(true); await Task.Delay(600);
                var reference = preview.CapturePresentedFrame();
                Require(BackgroundPixels(reference) == 0, "The wireframe pose does not fill the view with terrain.");
                double meanSeen = 0, worstSeen = 0;
                for (int i = 0; i < 12; i++)
                {
                    var composed = preview.RenderFrameReadAsComposed() ?? throw new InvalidDataException("No frame was rendered for the composed read.");
                    int missing = BackgroundPixels(composed), clear = TransparentPixels(composed);
                    var (mean, worst) = BlockDifference(reference, composed);
                    if (missing > 0 || clear > composed.PixelWidth * composed.PixelHeight / 100 || mean > 8 || worst > 40)
                    {
                        Save(composed, Path.Combine(output, "wireframe-composed-read.png"));
                        throw new InvalidDataException($"WPF read a frame that is not the finished one: {missing} background and {clear} transparent pixels, block difference mean {mean:0.0}, worst {worst:0.0}.");
                    }
                    meanSeen = Math.Max(meanSeen, mean); worstSeen = Math.Max(worstSeen, worst);
                    await Task.Delay(30);
                }
                preview.SetWireframe(false);
                Console.WriteLine($"PASS whole-world wireframe: 12 frames read as WPF composes them right after drawing, each the finished frame (block difference to the presented frame: max mean {meanSeen:0.0}, max worst {worstSeen:0.0}).");
                var model = context.World.Assets.First(a => a.Kind == AssetKind.Model && a.Index == 384);
                await preview.ShowAsync(context.World, model, resolver, null, 0, default);
                await CheckReal("sandbags-model", false);
                Console.WriteLine("Images: " + output);

                Point Center() => new(preview.RenderSurface.ActualWidth / 2, preview.RenderSurface.ActualHeight / 2);
                async Task CheckReal(string label, bool world)
                {
                    if (world) { cam.Position = new(2065, 72, 1702); cam.LookDirection = new(-24, -40, -9); cam.UpDirection = new(0, 1, 0); }
                    await Task.Delay(400); preview.WheelZoom(Center(), 120);
                    await Task.Delay(100); Save(preview.CapturePresentedFrame(), Path.Combine(output, label + "-moving.png"));
                    await Task.Delay(2400); var idle = preview.CapturePresentedFrame(); var position = cam.Position;
                    var identities = preview.EnumerateRenderMeshes().ToArray();
                    Save(idle, Path.Combine(output, label + "-idle2.png"));
                    await Task.Delay(8000); var later = preview.CapturePresentedFrame(); Save(later, Path.Combine(output, label + "-idle10.png"));
                    Require(Pixels(idle).SequenceEqual(Pixels(later)) && cam.Position == position, label + " changed while idle.");
                    Require(identities.SequenceEqual(preview.EnumerateRenderMeshes()), label + " replaced mesh identities while idle.");
                    Console.WriteLine($"PASS {label}: stable displayed buffers at 2/10 seconds; near={cam.NearPlaneDistance:G6}, far={cam.FarPlaneDistance:G6}, animated meshes={preview.AnimationMeshCount}");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    private static GameModel Plane(int material) => new(material, [new(-40, -40, 0), new(40, -40, 0), new(40, 40, 0), new(-40, 40, 0)], [], [], [new(material, 0, [0, 1, 2, 3], [], [], [])], []);
    private static void AssertRed(BitmapSource image, string label)
    {
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        Console.WriteLine($"{label}: RGB {pixel[2]},{pixel[1]},{pixel[0]}");
        if (pixel[2] < 250 || pixel[1] > 5 || pixel[0] > 5) throw new InvalidDataException("Rear surface shows through an opaque foreground surface.");
    }
    private static byte[] Pixels(BitmapSource image)
    { var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); byte[] data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0); return data; }
    // Pixels of the preview background (#1A1F26): what a frame shows where nothing has been drawn yet.
    private static int BackgroundPixels(BitmapSource image)
    {
        var data = Pixels(image); int count = 0;
        for (int i = 0; i < data.Length; i += 4) if (data[i] == 38 && data[i + 1] == 31 && data[i + 2] == 26) count++;
        return count;
    }
    private static int TransparentPixels(BitmapSource image)
    {
        var data = Pixels(image); int count = 0;
        for (int i = 3; i < data.Length; i += 4) if (data[i] < 250) count++;
        return count;
    }
    // Mean and worst absolute colour difference of the two images' averages over a 16 × 10 grid (sizes and filtering may differ).
    private static (double Mean, double Worst) BlockDifference(BitmapSource a, BitmapSource b)
    {
        double[] Blocks(BitmapSource image)
        {
            var data = Pixels(image); int w = image.PixelWidth, h = image.PixelHeight;
            var sums = new double[16 * 10 * 3]; var counts = new int[16 * 10];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int block = y * 10 / h * 16 + x * 16 / w, i = (y * w + x) * 4;
                    sums[block * 3] += data[i]; sums[block * 3 + 1] += data[i + 1]; sums[block * 3 + 2] += data[i + 2]; counts[block]++;
                }
            for (int k = 0; k < sums.Length; k++) sums[k] /= Math.Max(1, counts[k / 3]);
            return sums;
        }
        var first = Blocks(a); var second = Blocks(b);
        double total = 0, worst = 0;
        for (int k = 0; k < first.Length; k++) { double d = Math.Abs(first[k] - second[k]); total += d; worst = Math.Max(worst, d); }
        return (total / first.Length, worst);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Save(BitmapSource image, string path) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
}
