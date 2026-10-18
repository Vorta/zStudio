using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelixToolkit.Wpf.SharpDX;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Rendering;

internal static class FaceOrientationCheck
{
    internal static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exit = 0;
        app.Startup += async (_, _) =>
        {
            using var preview = new SceneViewport();
            // No activation, keyboard input or physical mouse capture is needed.
            var window = new Window { Content = preview, Width = 400, Height = 400, Left = -12000, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            string folder = Path.Combine(Path.GetTempPath(), "zstudio-orientation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            using var resolver = new AssetResolver(folder);
            try
            {
                GameScene scene = new();
                scene.Materials.Add(new() { ["alpha"] = 255 });
                // Intentionally wrong normals: the +Z winding must still appear blue from +Z.
                scene.Models.Add(new(0, [new(-1,-1,0), new(1,-1,0), new(1,1,0), new(-1,1,0)], [-Vector3.UnitZ], [],
                    [new(0, 4, [0,1,2,3], [0,0,0,0], [], [])], []));
                foreach (bool animated in new[] { false, true })
                foreach (bool mirrored in new[] { false, true })
                foreach (bool nearFirst in new[] { false, true })
                {
                    var near = Matrix4x4.CreateScale(mirrored ? -1 : 1, 1, 1);
                    var far = Matrix4x4.CreateScale(mirrored ? 1 : -1, 1, 1) * Matrix4x4.CreateTranslation(0, 0, -.2f);
                    var transforms = nearFirst ? new[] { near, far } : new[] { far, near };
                    scene.Nodes.Clear(); scene.Nodes.Add(new(0, "world", "world", null, [], [1,2], [], []));
                    for (int i = 0; i < transforms.Length; i++)
                    {
                        var m = transforms[i];
                        scene.Nodes.Add(new(i + 1, "surface", "object3d", 0, [0], [], new() { ["flags"] = 4 },
                            new() { ["transform"] = new JsonArray(m.M11,m.M12,m.M13,m.M21,m.M22,m.M23,m.M31,m.M32,m.M33,m.M41,m.M42,m.M43) }));
                    }
                    var world = new AssetDocument(Path.Combine(folder, $"{animated}-{mirrored}-{nearFirst}.zbd"), new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
                    var asset = world.Add(AssetKind.World, 0, "Whole world", 0, 0);
                    var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = world };
                    var frame = new AnimationFrame(0, transforms.Select((m,i) => new AnimationNodePose(i + 1, i + 1, 0, m, true, 1, -1, 0, 0)).ToArray(), [], [], [], null, null, default, default, [], [], []);
                    if (animated) await preview.ShowAnimationAsync(context, frame, resolver, false, default);
                    else await preview.ShowAsync(world, asset, resolver, null, 0, default);
                    var surface = (Viewport3DX)preview.RenderSurface;
                    surface.ShowViewCube = surface.ShowCoordinateSystem = false;
                    preview.RestoreView(new(new(0,0,5), new(0,0,-5), new(0,1,0), 45));
                    var pose = preview.CaptureView();
                    preview.SetWireframe(true);
                    preview.SetFaceOrientation(true);
                    await Task.Delay(100);
                    AssertColor(mirrored, $"animated={animated}, mirrored={mirrored}, nearFirst={nearFirst}");
                    if (animated)
                    {
                        // Replacing animation meshes must inherit the active override.
                        preview.UpdateAnimationFrame(frame with { Nodes = frame.Nodes.Select(n => n with { Id = n.Id + 10 }).ToArray() });
                        AssertColor(mirrored, "new animation meshes");
                    }
                    else
                    {
                        preview.SetWorldHighlightMode(WorldHighlightMode.Zones);
                        AssertColor(mirrored, "world highlight material replacement");
                    }
                    preview.SetFaceOrientation(false);
                    if (preview.CaptureView() != pose) throw new InvalidOperationException("Appearance changed camera state.");

                    // Wireframe outlines the static meshes' polygons; it never replaces their fill.
                    if (!preview.Wireframe || !animated && preview.PolygonOutlineCount == 0 || surface.Items.OfType<MeshGeometryModel3D>().Any(m => m.FillMode != SharpDX.Direct3D11.FillMode.Solid)) throw new InvalidOperationException("Wireframe choice was lost.");
                }
                Console.WriteLine("PASS: static and animated winding, wrong normals, mirrored transforms, opaque depth in both submission orders, mesh replacement, half-tint priority and wireframe restoration.");
                byte[] Pixel()
                {
                    var image = new FormatConvertedBitmap(preview.RenderImage(256,256), PixelFormats.Bgra32, null, 0);
                    byte[] pixel = new byte[4]; image.CopyPixels(new Int32Rect(128,128,1,1), pixel, 4, 0); return pixel;
                }
                void AssertColor(bool red, string label)
                {
                    var pixel = Pixel();
                    if (Math.Abs(pixel[0] - (red ? 128 : 255)) > 1 || Math.Abs(pixel[1] - 128) > 1 || Math.Abs(pixel[2] - (red ? 255 : 128)) > 1)
                        throw new InvalidOperationException($"{label}: expected {(red ? "red back" : "blue front")}, got BGRA {string.Join(',', pixel)}.");
                }
            }
            catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
            finally { window.Close(); app.Shutdown(); Directory.Delete(folder); }
        };
        app.Run(); return exit;
    }
}
