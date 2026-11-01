using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Xunit;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using zStudio.Rendering;

namespace zStudio.Desktop.Tests;

internal static class RenderingMaterialLifecycleChecks
{
    internal static async Task Run()
    {
        // Protect three visible regressions with real uploads and pixels: colour materials beside RGBA texture slot zero,
        // highlight continuity across world-animation replacement/morph/detach, and repeated guide native-buffer retention.
        var token = TestContext.Current.CancellationToken;
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-render-materials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var resolver = new AssetResolver(folder);
        using var viewport = new SceneViewport();
        var window = new Window { Content = viewport, Width = 500, Height = 400, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            WorldTexture texture = new("blue");
            WorldMaterial opaque = new() { Color = new(255, 0, 0) }, translucent = new() { Color = new(0, 255, 0), Flags = 128 },
                textured = new() { Texture = texture };
            GameZWorld world = new() { NodeCapacity = 2, ModelCapacity = 1, MaterialCapacity = 3 };
            world.Textures.Add(texture); world.Materials.AddRange([opaque, translucent, textured]);
            WorldModel model = new();
            foreach (var (material, x) in new[] { (opaque, -2f), (translucent, 0f), (textured, 2f) })
            {
                int start = model.Vertices.Count;
                model.Vertices.AddRange([new(x - .8f, -.8f, 0), new(x + .8f, -.8f, 0), new(x + .8f, .8f, 0), new(x - .8f, .8f, 0)]);
                model.Polygons.Add(new() { Material = material, Vertices = [start, start + 1, start + 2, start + 3],
                    Uvs = material.Texture == null ? [] : [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY], Zone = 0xFFFF0301 });
            }
            model.Morphs.AddRange(Enumerable.Repeat(new Vector3(.04f, 0, 0), model.Vertices.Count));
            world.Models.Add(model);
            WorldNode root = new("world", WorldNodeClass.World), surface = new("surface", WorldNodeClass.Object3D)
                { Model = model, Flags = 0x0109001C, Zone = 7 };
            surface.SetPayloadInt(0, 0x28);
            root.Children.Add(surface); surface.Parents.Add(root); world.Nodes.AddRange([root, surface]);
            WorldUpdate.RebuildBounds(world);
            byte[] rgba = Enumerable.Range(0, 64).SelectMany(_ => new byte[] { 0, 0, 255, 128 }).ToArray();
            byte[] png;
            var encoder = new PngBitmapEncoder();
            byte[] bgra = Enumerable.Range(0, 64).SelectMany(_ => new byte[] { 255, 0, 0, 128 }).ToArray();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, bgra, 32)));
            using (MemoryStream stream = new()) { encoder.Save(stream); png = stream.ToArray(); }
            SourceSceneSnapshot source = new(folder, "m1")
            {
                World = world, Scene = WorldScene.Create(world, token), Resources = new Dictionary<string, SourceSceneResource>(),
                TextureFiles = new Dictionary<string, string>(), Textures = new Dictionary<string, SourceSceneTexture>
                    { ["blue"] = new("blue.png", "blue-fixture", png, 8, 8) },
                TextureScripts = new Dictionary<string, ScriptContent>(), Provenance = new Dictionary<int, WorldNodeProvenance>(),
                Freed = [], AuthoredHierarchy = SourceAuthoredHierarchy.Empty, Executions = new Dictionary<(string, int), int>(),
                Inputs = new Dictionary<string, FileStamp>(), ReadHashes = new Dictionary<string, string>(), InputHashes = new Dictionary<string, string>(),
                Dependencies = [], MissingInputs = [], MissingFolders = [], ResourceInputs = [], Notes = [], Lookups = []
            };
            source.Add(AssetKind.World, 0, "Whole world", 0, 0);
            byte[] compiledBytes = GameZWriter.Write(world, token);
            var compiled = FormatRegistry.Default.OpenBytes(Path.Combine(folder, "gamez.zbd"), compiledBytes, token: token);
            Assert.DoesNotContain(compiled.Diagnostics, d => d.Severity == "Error");
            Assert.Equal(0, compiled.Scene!.Materials[0].Int("texture_index")); // Keep the raw on-disk metadata.
            Assert.Equal(-1, source.Scene!.Materials[0].Int("texture_index"));
            await File.WriteAllBytesAsync(Path.Combine(folder, "image.zbd"), TexturePackBuilder.Build(
                [new("blue", "blue", new(8, 8, rgba))], TexturePackVariant.FromFileName("image.zbd")!, token).Bytes, token);
            window.Show();
            foreach (AssetDocument document in new AssetDocument[] { source, compiled })
            {
                await viewport.ShowAsync(document, document.Assets.First(a => a.Kind == AssetKind.World), resolver, null, 0, token);
                viewport.SetNavigationAids(false, false);
                viewport.RestoreView(new(new(0, 0, 8), new(0, 0, -8), new(0, 1, 0), 45));
                CheckMaterials(SceneViewport.RenderMeshRole.Scene);
                var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = document };
                var frame = new AnimationFrame(0, [new(1, 1, 0, Matrix4x4.Identity, true, 1, -1, 0, 0)], [], [], [], null, null, default, default, [], [], []);
                Guid attached = viewport.AttachWorldAnimation(context, resolver, viewport.SceneGeneration, CancellationToken.None);
                Assert.True(viewport.UpdateWorldAnimation(attached, frame));
                // Base textures load asynchronously. The loading task republishes the complete same frame.
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!viewport.EnumerateRenderMeshes().Any(m => m.Role == SceneViewport.RenderMeshRole.Animation && m.HasDiffuseMap))
                {
                    Assert.True(DateTime.UtcNow < deadline, "The animation's texture slot zero did not load.");
                    await Task.Delay(10, token);
                }
                CheckMaterials(SceneViewport.RenderMeshRole.Animation);
                Assert.True(viewport.DetachWorldAnimation(attached));
                if (document == source) await CheckHighlights(context, frame);
            }
            CheckPathResources();

            void CheckMaterials(SceneViewport.RenderMeshRole role)
            {
                var meshes = viewport.EnumerateRenderMeshes().Where(m => m.Role == role).ToArray();
                Assert.Equal(3, meshes.Length);
                Assert.False(meshes[0].HasDiffuseMap); Assert.False(meshes[0].IsTransparent);
                Assert.Equal(new Vector4(1, 0, 0, 1), meshes[0].DiffuseColor);
                Assert.False(meshes[1].HasDiffuseMap); Assert.True(meshes[1].IsTransparent);
                Assert.Equal(new Vector4(0, 1, 0, 128f / 255), meshes[1].DiffuseColor);
                Assert.True(meshes[2].HasDiffuseMap); Assert.True(meshes[2].IsTransparent);
                var red = Pixel(new(-2, 0, 0)); var green = Pixel(Vector3.Zero); var blue = Pixel(new(2, 0, 0));
                Assert.True(red[2] > 245 && red[1] < 8 && red[0] < 8, $"Opaque colour sampled slot zero: {string.Join(',', red)}");
                Assert.True(green[1] > green[2] + 90 && green[1] < 180, $"Colour alpha was lost: {string.Join(',', green)}");
                Assert.True(blue[0] > blue[2] + 90 && blue[0] < 180, $"Texture alpha was lost: {string.Join(',', blue)}");
            }
            async Task CheckHighlights(AnimationPreviewContext context, AnimationFrame frame)
            {
                foreach (var mode in new[] { WorldHighlightMode.CanModify, WorldHighlightMode.Zones, WorldHighlightMode.PolygonZones })
                {
                    viewport.SetWorldHighlightMode(mode);
                    var before = Pixel(new(-2, 0, 0));
                    Guid id = viewport.AttachWorldAnimation(context, resolver, viewport.SceneGeneration, CancellationToken.None);
                    viewport.UpdateWorldAnimation(id, frame);
                    Assert.Equal(before, Pixel(new(-2, 0, 0)));
                    viewport.UpdateWorldAnimation(id, frame with { Nodes = [frame.Nodes[0] with { Morph = 1 }] });
                    Assert.Equal(before, Pixel(new(-2, 0, 0)));
                    viewport.SetTextured(false);
                    var untextured = Pixel(new(-2, 0, 0));
                    for (int channel = 0; channel < 4; channel++) Assert.InRange(Math.Abs(before[channel] - untextured[channel]), 0, 4);
                    viewport.SetTextured(true); Assert.Equal(before, Pixel(new(-2, 0, 0)));
                    viewport.UpdateWorldAnimation(id, frame with { Lights = [new(10, Vector3.Zero, Vector3.UnitZ, 100, 1, true)] });
                    var lit = Pixel(new(-2, 0, 0)); // Observe the update before any highlight toggle can repair its state.
                    // The light keeps 65% of a channel brighter than its own (red here). Measure the same lit surface
                    // without a highlight, then verify that each overlay still blends against that actual appearance.
                    viewport.SetWorldHighlightMode(WorldHighlightMode.None);
                    var litBase = Pixel(new(-2, 0, 0));
                    float[] baseExpected = [255, 0, .65f * 255];
                    for (int channel = 0; channel < 3; channel++)
                        Assert.True(Math.Abs(litBase[channel] - baseExpected[channel]) <= 1,
                            $"Highlight={mode}, lighting base channel={channel}, before BGRA={string.Join(',', before)}, actual BGRA={string.Join(',', litBase)}, expected BGR={string.Join(',', baseExpected)}.");
                    var tint = mode == WorldHighlightMode.CanModify ? (R: 0f, G: 1f, B: 0f)
                        : WorldSurfaceHighlights.ZoneColor(mode == WorldHighlightMode.Zones ? 7 : 3);
                    float[] expected = [litBase[0] * .5f + tint.B * 127.5f, litBase[1] * .5f + tint.G * 127.5f, litBase[2] * .5f + tint.R * 127.5f];
                    for (int channel = 0; channel < 3; channel++)
                        Assert.True(Math.Abs(lit[channel] - expected[channel]) <= 2,
                            $"Highlight={mode}, overlay channel={channel}, before BGRA={string.Join(',', before)}, lit base BGRA={string.Join(',', litBase)}, actual BGRA={string.Join(',', lit)}, expected BGR={string.Join(',', expected)}.");
                    viewport.SetWorldHighlightMode(mode);
                    viewport.UpdateWorldAnimation(id, frame);
                    Assert.Equal(before, Pixel(new(-2, 0, 0)));
                    viewport.SetZonePresentation(new HashSet<byte> { 3, 7 }, null);
                    var hidden = Pixel(new(-2, 0, 0));
                    if (mode is WorldHighlightMode.Zones or WorldHighlightMode.PolygonZones) Assert.NotEqual(before, hidden);
                    viewport.SetZonePresentation(new HashSet<byte>(), 3);
                    var emphasis = Pixel(new(-2, 0, 0));
                    viewport.UpdateWorldAnimation(id, frame with { Nodes = [frame.Nodes[0] with { Opacity = .5f, Morph = 0 }] });
                    Assert.NotEqual(emphasis, Pixel(new(-2, 0, 0))); // Opacity updates must not replace polygon zone metadata.
                    viewport.DetachWorldAnimation(id);
                    viewport.SetZonePresentation(new HashSet<byte>(), null);
                    Assert.Equal(before, Pixel(new(-2, 0, 0)));
                    viewport.SetWorldHighlightMode(WorldHighlightMode.None);
                    Assert.True(Pixel(new(-2, 0, 0))[2] > 245);
                }
                await Task.CompletedTask;
            }
            void CheckPathResources()
            {
                viewport.SetWorldHighlightMode(WorldHighlightMode.None);
                viewport.RenderCurrentFrame();
                var baseline = viewport.LineBufferUsage;
                AnimationPath path = new("guide", 0, 1, 1, "surface", Enumerable.Range(0, 121)
                    .Select(i => new AnimationPathPoint(i / 60d, new Vector3(-2 + i / 30f, 0, .2f), false)).ToArray());
                (int Count, long Bytes)? drawn = null;
                for (int i = 0; i < 8; i++)
                {
                    viewport.SetAnimationPaths("definition", [path]); viewport.RenderCurrentFrame();
                    var usage = viewport.LineBufferUsage;
                    Assert.Same(baseline.Device, usage.Device);
                    Assert.True(usage.Count >= baseline.Count + 3 && usage.Bytes > baseline.Bytes);
                    drawn ??= (usage.Count, usage.Bytes);
                    Assert.Equal(drawn.Value, (usage.Count, usage.Bytes));
                    // Refresh replaces an existing guide before rendering the replacement on the same device.
                    viewport.SetAnimationPaths("definition", [path]); viewport.RenderCurrentFrame();
                    Assert.Equal(drawn.Value, (viewport.LineBufferUsage.Count, viewport.LineBufferUsage.Bytes));
                    viewport.ClearAnimationPaths(i % 2 == 0 ? "definition" : null); viewport.RenderCurrentFrame();
                    Assert.Equal(baseline, viewport.LineBufferUsage);
                }
                viewport.SetAnimationPaths("definition", [path]); viewport.RenderCurrentFrame();
                viewport.Clear(); viewport.RenderCurrentFrame();
                Assert.Same(baseline.Device, viewport.LineBufferUsage.Device);
                Assert.True(viewport.LineBufferUsage.Count <= baseline.Count && viewport.LineBufferUsage.Bytes <= baseline.Bytes);
            }
            byte[] Pixel(Vector3 point)
            {
                var image = new FormatConvertedBitmap(viewport.RenderCurrentFrame(), PixelFormats.Bgra32, null, 0);
                Point location = viewport.ProjectToView(new Point3D(point.X, point.Y, point.Z));
                int x = (int)Math.Round(location.X * viewport.RenderDpiScale), y = (int)Math.Round(location.Y * viewport.RenderDpiScale);
                byte[] pixel = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel;
            }
        }
        finally { window.Close(); Directory.Delete(folder, true); }
    }
}
