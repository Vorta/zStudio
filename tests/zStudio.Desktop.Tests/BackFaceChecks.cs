using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Xunit;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Rendering;

namespace zStudio.Desktop.Tests;

internal static class BackFaceChecks
{
    internal static async Task Run()
    {
        // Protect real rendering and pointer visibility, including a rejected nearer face in the same BVH.
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-backfaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var resolver = new AssetResolver(folder);
        using var preview = new SceneViewport();
        var window = new Window { Content = preview, Width = 400, Height = 400, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            foreach (bool animated in new[] { false, true })
            foreach (var sides in new[] { (Near: false, Far: false), (Near: true, Far: false), (Near: false, Far: true) })
            foreach (bool mirrored in new[] { false, true })
            {
                GameScene scene = new();
                scene.Materials.Add(new() { ["alpha"] = 255 });
                scene.Models.Add(new(0,
                    [new(-1,-1,0), new(1,-1,0), new(1,1,0), new(-1,1,0),
                     new(-1,-1,-.2f), new(1,-1,-.2f), new(1,1,-.2f), new(-1,1,-.2f)], [],
                    Enumerable.Repeat(new Vector3(.02f, 0, 0), 8).ToArray(),
                    [new(0, sides.Near ? 0x104u : 4u, [0,3,2,1], [], [], []) { Colors = Enumerable.Repeat(new Vector3(255,0,0), 4).ToArray() },
                     new(0, sides.Far ? 0x104u : 4u, [4,5,6,7], [], [], []) { Colors = Enumerable.Repeat(new Vector3(0,255,0), 4).ToArray() }], []));
                var transform = Matrix4x4.CreateScale(mirrored ? -1 : 1, 1, 1);
                scene.Nodes.Add(new(0, "world", "world", null, [], [1], [], []));
                scene.Nodes.Add(new(1, "surface", "object3d", 0, [0], [], new() { ["flags"] = 4 }, new()
                { ["transform"] = new JsonArray(transform.M11,0,0,0,1,0,0,0,1,0,0,0) }));
                var world = new AssetDocument(Path.Combine(folder, "fixture.zbd"), new(0, DateTime.MinValue),
                    new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
                var asset = world.Add(AssetKind.World, 0, "Whole world", 0, 0);
                var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = world };
                var frame = new AnimationFrame(0, [new(1, 1, 0, transform, true, 1, -1, 0, 0)], [], [], [], null, null, default, default, [], [], []);
                if (animated) await preview.ShowAnimationAsync(context, frame, resolver, false, default);
                else await preview.ShowAsync(world, asset, resolver, null, 0, default);
                preview.SetNavigationAids(false, false);
                preview.RestoreView(new(new(0,0,5), new(0,0,-5), new(0,1,0), 45));
                var camera = preview.CaptureView();
                Check(false);
                preview.SetFaceOrientation(true); Check(true);
                preview.SetFaceOrientation(false); Check(false);
                Assert.Equal(camera, preview.CaptureView());
                if (animated)
                {
                    preview.UpdateAnimationFrame(frame with { Nodes = [frame.Nodes[0] with { Morph = 1 }] });
                    Check(false); // Mixed-sided material ranges reconcile through a morph without losing identity.
                    preview.UpdateAnimationFrame(frame with { Nodes = [frame.Nodes[0] with { Id = 2 }] });
                    Check(false);
                }
                preview.RestoreView(new(new(0,0,-5), new(0,0,5), new(0,1,0), 45));
                Assert.NotEmpty(preview.HitTestScene(new Point3D(0,0,-5), new Vector3D(0,0,1)));

                void Check(bool diagnostic)
                {
                    var image = new FormatConvertedBitmap(preview.RenderImage(256,256), PixelFormats.Bgra32, null, 0);
                    var hits = preview.HitTestScene(new Point3D(0,0,5), new Vector3D(0,0,-1));
                    Assert.NotEmpty(hits); var hit = hits[0];
                    bool near = diagnostic || sides.Near || mirrored;
                    Assert.Equal(near ? 0f : -.2f, hit.Point.Z, 4);
                    if (diagnostic) return; // Both faces intentionally show in the orientation diagnostic.
                    byte[] pixel = new byte[4]; image.CopyPixels(new Int32Rect(128,128,1,1), pixel, 4, 0);
                    Assert.True(near ? pixel[2] > pixel[1] + 80 : pixel[1] > pixel[2] + 80,
                        $"animated={animated}, sides={sides}, mirrored={mirrored}: BGRA={string.Join(',', pixel)}");
                }
            }
            await CheckOpacityOrder(false, false);
            await CheckOpacityOrder(true, false);
            await CheckOpacityOrder(false, true);
            await CheckOpacityOrder(true, true);
            await CheckOpacityOrder(false, true, horizon: true);

            async Task CheckOpacityOrder(bool animated, bool instanced, bool horizon = false)
            {
                byte[]? original = null;
                foreach (bool mixed in new[] { false, true })
                {
                    preview.Clear();
                    GameScene scene = new(); scene.Materials.Add(new() { ["alpha"] = animated || horizon ? 255 : 128 });
                    Vector3[] vertices = Enumerable.Range(0, 3).SelectMany(i =>
                    {
                        float x = horizon && i == 2 ? .2f : 1, z = (horizon ? -5.4f : -.4f) + .2f*i;
                        return new Vector3[] { new(-x,-1,z), new(x,-1,z), new(x,1,z), new(-x,1,z) };
                    }).ToArray();
                    // Colors stand in for different RGB regions of one texture atlas.
                    Polygon Face(int i, Vector3 color) => new(0, mixed && i != 1 ? 0x104u : 4u,
                        [i*4,i*4+1,i*4+2,i*4+3], [], [], []) { Colors = Enumerable.Repeat(color, 4).ToArray() };
                    scene.Models.Add(new(0, vertices, [], [], [Face(0,new(255,0,0)), Face(1,new(0,255,0)), Face(2,new(0,0,255))], []));
                    scene.Nodes.Add(new(0,"world","world",null,[],[1],[],[]));
                    if (horizon)
                    {
                        scene.Nodes.Add(new(1,"horizon","object3d",null,[0],[2,3],new(){["flags"]=4},new(){["flags"]=8}));
                        scene.Nodes.Add(new(2,"piece1","object3d",0,[1],[],new(){["flags"]=4},new(){["flags"]=8}));
                        scene.Nodes.Add(new(3,"piece2","object3d",0,[1],[],new(){["flags"]=4},new()
                            { ["transform"] = new JsonArray(1,0,0,0,1,0,0,0,1,.5f,0,0) }));
                    }
                    else scene.Nodes.Add(new(1,"layers","object3d",0,[0],[],new(){["flags"]=4},new(){["flags"]=8}));
                    var world = new AssetDocument(Path.Combine(folder,"layers.zbd"), new(0,DateTime.MinValue),
                        new(FormatFamily.GameZ,15,Recognition.Supported,"fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
                    var asset = world.Add(AssetKind.World,0,"world",0,0);
                    if (animated)
                    {
                        var context = new AnimationPreviewContext { World = world, Package = new() { Prefix = [], Tail = [] } };
                        var frame = new AnimationFrame(0,[new(1,1,0,Matrix4x4.Identity,true,.5f,-1,0,0)],[],[],[],null,null,default,default,[],[],[]);
                        await preview.ShowAnimationAsync(context,frame,resolver,false,default);
                    }
                    else await preview.ShowAsync(world,asset,resolver,null,0,default,showBackdrop: horizon);
                    // Same render state as a zone-ghosted opaque batch retaining several instances.
                    if (instanced)
                    {
                        var mesh = Assert.Single(preview.EnumerateRenderMeshes(), m => m.Role is SceneViewport.RenderMeshRole.Scene or SceneViewport.RenderMeshRole.Animation);
                        if (horizon) Assert.Equal(2, mesh.Instances!.Count);
                        else mesh.Instances = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0,0,.1f)];
                    }
                    preview.SetFaceOrientation(false); preview.SetNavigationAids(false,false);
                    preview.RestoreView(new(new(0,0,5),new(0,0,-5),new(0,1,0),45));
                    var image = new FormatConvertedBitmap(preview.RenderImage(256,256),PixelFormats.Bgra32,null,0);
                    byte[] pixel = new byte[4]; image.CopyPixels(new Int32Rect(128,128,1,1),pixel,4,0);
                    if (mixed) Assert.Equal(original, pixel);
                    else { Assert.True(horizon ? pixel[1] > 200 : pixel[0] > 100 && pixel[1] > 50, "The layered control must be rendered."); original = pixel; }
                }
            }
        }
        finally { window.Close(); Directory.Delete(folder); }
    }
}
