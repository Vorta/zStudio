using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Windows;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Rendering;
using Xunit;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Runs on the existing STA application, through the real two-material frame/queue path.</summary>
internal static class AnimationTextureQueueChecks
{
    internal static async Task Run()
    {
        await Check("sample", null, accepted: true);
        // Path-prefixed names are valid basename aliases; they never become an OS file path.
        string largeAlias = new string('x', 600_000) + "/sample";
        await Check(largeAlias, null, accepted: true);
        await Check(largeAlias, new string('y', 600_000) + "/unused", accepted: true);
        await Check(new string('z', 1_000_001) + "/sample", null, accepted: false);
        await CheckPublicationAndAdmission();
    }

    private static async Task CheckPublicationAndAdmission()
    {
        var token = TestContext.Current.CancellationToken;
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "zstudio-render-admission-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var resolver = new AssetResolver(root); using var preview = new SceneViewport();
            var (context, asset) = Scene(4, 3);
            for (int i = 1; i <= 4; i++) context.Scene.Nodes[i].Metadata["zone_id"] = i;
            long boundsVisits = 0;
            preview.StaticBoundsMeasured = count => { Assert.False(preview.Dispatcher.CheckAccess()); boundsVisits += count; };
            long framingCorners = 0;
            preview.StaticFramingMeasured = count => framingCorners = count;
            // Four placements x three transparent material parts produce twelve
            // mesh objects and twelve instance rows, plus four zone boxes/four box rows.
            preview.MaximumRenderUnits = 32;
            await preview.ShowAsync(context.World, asset, resolver, null, 0, token, mission: context.Mission);
            preview.StaticBoundsMeasured = null;
            Assert.Equal(9, boundsVisits); // Three triangles measured once despite four zone groups.
            Assert.Equal(96, framingCorners); // Eight prepared corners x twelve visible part instances.
            Assert.True(preview.TryFrame("selected", 2));
            Assert.Equal(24, framingCorners);
            var boxes = Field<Dictionary<LineGeometryModel3D, ScenePlacement[]>>("boundsPlacements");
            Assert.Equal([1, 2, 3, 4], boxes.Values.SelectMany(p => p).Select(p => p.NodeIndex).Order().ToArray());
            Assert.Equal(4, boxes.Count);
            Assert.All(boxes.Keys, box =>
            {
                var geometry = Assert.IsType<LineGeometry3D>(box.Geometry);
                Assert.NotNull(geometry.Positions);
                Assert.Equal(Vector3.Zero, geometry.Positions[0]);
                Assert.Equal(new Vector3(1, 1, 0), geometry.Positions[6]);
            });
            preview.Isolate(2);
            Assert.Equal(24, framingCorners);
            Assert.Equal(Vector3.Zero, Field<Vector3>("sceneMin"));
            Assert.Equal(new Vector3(1, 1, 0), Field<Vector3>("sceneMax"));
            preview.Isolate(null);
            Assert.Equal(96, framingCorners);
            preview.StaticFramingMeasured = null;
            Assert.Equal(12, Field<System.Collections.IList>("meshes").Count);
            string acceptedSummary = preview.PreviewSummary;
            preview.MaximumRenderUnits = 31;
            await Assert.ThrowsAsync<SceneViewport.RenderLimitException>(() => preview.ShowAsync(context.World, asset, resolver, null, 0, token, mission: context.Mission));
            Assert.Equal(12, Field<System.Collections.IList>("meshes").Count);
            Assert.Equal(acceptedSummary, preview.PreviewSummary);

            AnimationFrame frame = Frame(4);
            // Static and animated retention share a context allowance. Each fits
            // separately, but their combined 32 + 28 units must refuse cleanly.
            preview.MaximumRenderUnits = 59;
            await Assert.ThrowsAsync<SceneViewport.RenderLimitException>(() => preview.ShowAnimationAsync(context, frame, resolver, true, token));
            Assert.Empty(Field<System.Collections.IList>("meshes"));
            Assert.Equal(0, preview.AnimationMeshCount);
            preview.MaximumRenderUnits = 28;
            await preview.ShowAnimationAsync(context, frame, resolver, false, token);
            Assert.Equal(12, preview.AnimationMeshCount);
            Assert.Throws<SceneViewport.RenderLimitException>(() => preview.UpdateAnimationFrame(Frame(5)));
            Assert.Same(frame, Field<AnimationFrame>("animationFrame"));
            Assert.Equal(12, preview.AnimationMeshCount);
            for (int i = 0; i < 32; i++) preview.UpdateAnimationFrame(frame);
            preview.UpdateAnimationFrame(Frame(2)); Assert.Equal(6, preview.AnimationMeshCount);

            // A small object count can still copy too much geometry. Exercise the
            // real frame path with a scaled byte allowance, before any extra meshes
            // are attached, then retry a valid frame in the same viewport.
            preview.MaximumRenderUnits = 65_536;
            preview.MaximumGeometryBytes = 32 * 1024;
            var (byteContext, _) = Scene(100, 3);
            await preview.ShowAnimationAsync(byteContext, frame, resolver, false, token);
            Assert.Throws<SceneViewport.RenderLimitException>(() => preview.UpdateAnimationFrame(Frame(100)));
            Assert.Same(frame, Field<AnimationFrame>("animationFrame")); Assert.Equal(12, preview.AnimationMeshCount);
            long geometryBytes = preview.RetainedGeometryBytes;
            for (int i = 0; i < 32; i++) preview.UpdateAnimationFrame(frame);
            Assert.Equal(geometryBytes, preview.RetainedGeometryBytes);
            preview.UpdateAnimationFrame(Frame(2)); Assert.Equal(6, preview.AnimationMeshCount);
            preview.Clear(); Assert.Equal(0, preview.RetainedGeometryBytes);
            Assert.Empty(Field<System.Collections.IDictionary>("staticMeshBounds"));
            preview.MaximumGeometryBytes = 256L * 1024 * 1024;

            // A morph opens an earlier polygon and a previously absent material.
            // The displayed colors and picking map must follow the rebuilt parts.
            var (morphContext, _) = Scene(1, 2);
            morphContext.Scene.Models[0] = new(0,
                [Vector3.Zero, Vector3.UnitX, 2 * Vector3.UnitX, Vector3.UnitZ, Vector3.UnitX + Vector3.UnitZ, Vector3.UnitY + Vector3.UnitZ], [],
                [Vector3.Zero, Vector3.Zero, Vector3.UnitY, Vector3.Zero, Vector3.Zero, Vector3.Zero],
                [new(0, 3, [0, 1, 2], [], [], []) { Colors = [new(255, 0, 0), new(255, 0, 0), new(255, 0, 0)] },
                 new(0, 3, [3, 4, 5], [], [], []) { Colors = [new(0, 0, 255), new(0, 0, 255), new(0, 0, 255)] },
                 new(1, 3, [0, 1, 2], [], [], []) { Colors = [new(0, 255, 0), new(0, 255, 0), new(0, 255, 0)] }], []);
            var closed = Frame(1);
            var opened = closed with { Nodes = [closed.Nodes[0] with { Morph = 1 }] };
            await preview.ShowAnimationAsync(morphContext, closed, resolver, false, token);
            Assert.Equal(1, preview.AnimationMeshCount);
            preview.UpdateAnimationFrame(opened);
            Assert.Equal(2, preview.AnimationMeshCount);
            var polygonMaps = Field<Dictionary<MeshGeometryModel3D, int[]>>("inspectionPolygons");
            var expanded = polygonMaps.Single(p => p.Value.Length == 6);
            Assert.Equal([0, 0, 0, 1, 1, 1], expanded.Value);
            var expandedGeometry = Assert.IsType<HelixToolkit.SharpDX.MeshGeometry3D>(expanded.Key.Geometry);
            Assert.NotNull(expandedGeometry.Colors);
            Assert.Equal(6, expandedGeometry.Colors.Count);
            Assert.Equal(1f, expandedGeometry.Colors[0].Red); Assert.Equal(1f, expandedGeometry.Colors[3].Blue);
            Assert.Equal(128f / 255, expandedGeometry.Colors[0].Alpha);
            Assert.Contains(polygonMaps, p => p.Value.SequenceEqual([2, 2, 2]));
            preview.UpdateAnimationFrame(closed);
            Assert.Equal(1, preview.AnimationMeshCount); Assert.Equal([1, 1, 1], Assert.Single(polygonMaps).Value);
            preview.UpdateAnimationFrame(closed with { Nodes = [] }); Assert.Empty(polygonMaps);
            preview.UpdateAnimationFrame(opened); Assert.Equal(2, preview.AnimationMeshCount);
            preview.Clear(); Assert.Equal(0, preview.RetainedGeometryBytes);

            var (many, manyAsset) = Scene(31, 1);
            var (replacement, _) = Scene(1, 1);
            preview.MaximumRenderUnits = 200;
            int yields = 0;
            SceneViewport.ViewPose? replacementView = null;
            preview.StaticBatchYielded = async () =>
            {
                yields++;
                preview.StaticBatchYielded = null;
                preview.Clear();
                await preview.ShowAnimationAsync(replacement, Frame(1), resolver, false, token);
                replacementView = preview.CaptureView();
            };
            string summaryBefore = preview.PreviewSummary;
            await preview.ShowAsync(many.World, manyAsset, resolver, null, 0, token, mission: many.Mission);
            Assert.Equal(1, yields);
            Assert.Same(replacement.Scene, preview.PreviewScene);
            Assert.Equal(1, preview.AnimationMeshCount);
            Assert.Empty(Field<System.Collections.IList>("meshes"));
            Assert.Empty(Field<System.Collections.IList>("bounds"));
            Assert.Equal(summaryBefore, preview.PreviewSummary);
            Assert.Equal(replacementView, preview.CaptureView());

            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
            preview.StaticBatchYielded = () => { preview.StaticBatchYielded = null; canceled.Cancel(); return Task.CompletedTask; };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview.ShowAsync(many.World, manyAsset, resolver, null, 0, canceled.Token, mission: many.Mission));
            Assert.Null(preview.PreviewScene); Assert.Empty(Field<System.Collections.IList>("meshes"));
            Assert.Equal(0, preview.RetainedGeometryBytes);
            await preview.ShowAnimationAsync(replacement, Frame(1), resolver, false, token);
            Assert.Equal(1, preview.AnimationMeshCount);

            T Field<T>(string name) => (T)typeof(SceneViewport).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
            (AnimationPreviewContext Context, AssetRecord Asset) Scene(int placements, int parts)
            {
                GameScene scene = new();
                for (int i = 0; i < parts; i++) scene.Materials.Add(new() { ["alpha"] = 128, ["texture_index"] = -1 });
                scene.Models.Add(new(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [],
                    Enumerable.Range(0, parts).Select(i => new Polygon(i, 3, [0, 1, 2], [], [], [])).ToArray(), []));
                scene.Nodes.Add(new(0, "world", "world", null, [], Enumerable.Range(1, placements).ToArray(), new(), new()));
                for (int i = 1; i <= placements; i++) scene.Nodes.Add(new(i, "mesh" + i, "object3d", 0, [0], [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
                var world = new ZbdDocument(Path.Combine(root, "gamez.zbd"), new(0, DateTime.MinValue),
                    new(FormatFamily.GameZ, 27, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
                var mission = new MissionSceneContext(scene, Enumerable.Range(0, scene.Nodes.Count).ToList(), [], [], [], MissionLayoutSelection.For(MissionDifficulty.Medium), scene.Nodes.Count);
                return (new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = world, Mission = mission }, world.Add(AssetKind.World, 0, "world", 0, 0));
            }
            static AnimationFrame Frame(int count) => new(0,
                Enumerable.Range(1, count).Select(i => new AnimationNodePose(i, i, 0, Matrix4x4.Identity, true, 1, 0, 0, 0)).ToArray(),
                [], [], [], null, null, default, default, [], [], []);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task Check(string firstName, string? rejectedName, bool accepted)
    {
        var token = TestContext.Current.CancellationToken;
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "zstudio-animation-texture-queue-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            byte[] bytes = Recoil.Zbd.Tests.ContentFixture.Texture(2, 2, false);
            bytes.AsSpan(64, 32).Clear(); Encoding.ASCII.GetBytes("bar").CopyTo(bytes, 64);
            File.WriteAllBytes(Path.Combine(root, "texture1.zbd"), bytes);
            using var resolver = new AssetResolver(root); using var preview = new SceneViewport();
            List<string> notices = []; preview.Information += notices.Add;
            GameScene scene = new();
            for (int i = 0; i < 2; i++)
            {
                scene.Materials.Add(new() { ["alpha"] = 255, ["texture_index"] = i });
                scene.Textures.Add(new() { ["name"] = i == 0 ? firstName : "bar" });
                scene.Models.Add(new(i, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [],
                    [new(i, 3, [0, 1, 2], [], [], [])], []));
            }
            var context = new AnimationPreviewContext
            {
                Package = new() { Prefix = [], Tail = [] },
                World = new(Path.Combine(root, "gamez.zbd"), new(0, DateTime.MinValue),
                    new(FormatFamily.GameZ, 27, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene }
            };
            context.MaterialCycles[0] = new(rejectedName == null ? [firstName] : [firstName, rejectedName], 0, false);
            context.MaterialCycles[1] = new(["bar"], 0, false);
            var frame = new AnimationFrame(0,
                [new(1, 0, 0, Matrix4x4.Identity, true, 1, 0, 0, 0), new(2, 1, 1, Matrix4x4.Identity, true, 1, 0, 0, 0)],
                [], [], [], null, null, default, default, [], [], []);

            // UpdateAnimationFrame queues every cycle member and then its selected
            // member again, before reaching the second material's short valid name.
            await preview.ShowAnimationAsync(context, frame, resolver, false, token);
            for (int i = 0; i < 3; i++) preview.UpdateAnimationFrame(frame);
            var loads = Field<System.Collections.IDictionary>("animationTextureSlots");
            Assert.Equal(accepted ? 2 : 1, loads.Count); Assert.True(loads.Contains("bar"));
            Assert.Equal(accepted, loads.Contains(firstName));
            if (rejectedName != null) Assert.False(loads.Contains(rejectedName));
            Assert.Equal((accepted ? firstName.Length + 1L : 0) + 4, Field<long>("animationTextureNameUnits"));
            var references = Field<System.Collections.IDictionary>("animationTextureReferences");
            Assert.Equal(loads.Count, references.Count);

            var surface = (Viewport3DX)preview.RenderSurface;
            var meshes = surface.Items.OfType<SortingGroupModel3D>().Single().Children.OfType<MeshGeometryModel3D>().ToArray();
            Assert.Equal(2, meshes.Length);
            Assert.Equal(accepted ? Visibility.Visible : Visibility.Collapsed, meshes[0].Visibility);
            Assert.Equal(Visibility.Visible, meshes[1].Visibility);
            Assert.NotNull(((DiffuseMaterial)meshes[1].Material!).DiffuseMap);
            if (accepted) Assert.NotNull(((DiffuseMaterial)meshes[0].Material!).DiffuseMap);
            var limits = notices.Where(n => n.Contains("allowance", StringComparison.Ordinal) || n.Contains("request limit", StringComparison.Ordinal)).ToArray();
            if (accepted && rejectedName == null) Assert.Empty(limits); else Assert.Single(limits);

            if (firstName == "sample")
            {
                // Equal-content references share a live slot and are each remembered once.
                for (int i = 0; i < 8; i++)
                {
                    context.MaterialCycles[0] = new([new string(firstName.AsSpan())], 0, false);
                    preview.UpdateAnimationFrame(frame);
                }
                Assert.Equal(2, loads.Count); Assert.Equal(10, references.Count);
                Assert.Equal(11, Field<long>("animationTextureNameUnits"));
                Assert.Equal(Visibility.Visible, meshes[0].Visibility);
                var stable = frame with { Nodes = [frame.Nodes[0] with { Texture = new string(firstName.AsSpan()) }, frame.Nodes[1]] };
                preview.UpdateAnimationFrame(stable);
                long lookup = Field<long>("animationTextureLookupUnits"), visits = Field<long>("animationTextureCycleMemberVisits");
                for (int i = 0; i < 256; i++) preview.UpdateAnimationFrame(stable);
                Assert.Equal(lookup, Field<long>("animationTextureLookupUnits"));
                Assert.Equal(visits, Field<long>("animationTextureCycleMemberVisits"));
                Assert.Equal(Visibility.Visible, meshes[0].Visibility);
            }
            long cycleVisits = Field<long>("animationTextureCycleMemberVisits"), lookupUnits = Field<long>("animationTextureLookupUnits");
            for (int i = 0; i < 128; i++) preview.UpdateAnimationFrame(frame);
            Assert.Equal(cycleVisits, Field<long>("animationTextureCycleMemberVisits"));
            Assert.Equal(lookupUnits, Field<long>("animationTextureLookupUnits"));
            if (firstName == "sample")
            {
                // Repeated spawned lifetimes must not retain expired meshes through picking metadata.
                var polygons = Field<System.Collections.IDictionary>("inspectionPolygons");
                for (int i = 1; i <= 3; i++)
                {
                    preview.UpdateAnimationFrame(frame with { Nodes = [frame.Nodes[0] with { Id = (long)i << 32 }, frame.Nodes[1]] });
                    var live = surface.Items.OfType<SortingGroupModel3D>().Single().Children.OfType<MeshGeometryModel3D>().ToArray();
                    Assert.Equal(2, live.Length); Assert.Equal(live.Length, polygons.Count);
                    Assert.All(live, mesh => Assert.True(polygons.Contains(mesh)));
                }
                preview.UpdateAnimationFrame(frame with { Nodes = [] });
                Assert.Equal(0, preview.AnimationMeshCount); Assert.Empty(polygons);
            }
            preview.Clear(); Assert.Empty(references);
            Assert.Equal(0, Field<long>("animationTextureNameUnits")); Assert.Equal(0, Field<long>("animationTextureLookupUnits"));
            T Field<T>(string name) => (T)typeof(SceneViewport).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
        }
        finally { Directory.Delete(root, true); }
    }
}
