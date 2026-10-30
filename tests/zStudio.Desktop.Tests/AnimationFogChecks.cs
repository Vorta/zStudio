using System.IO;
using System.Numerics;
using System.Reflection;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Rendering;
using Xunit;

namespace zStudio.Desktop.Tests;

internal static class AnimationFogChecks
{
    internal static async Task Run()
    {
        using var resolver = new AssetResolver(Path.GetTempPath()); using var preview = new SceneViewport();
        var scene = new GameScene(); scene.Materials.Add(new() { ["alpha"] = 255 });
        scene.Models.Add(new(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [],
            [new(0, 3, [0, 1, 2], [], [], []) { Colors = [new(255, 128, 64), new(255, 128, 64), new(255, 128, 64)] }], []));
        var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = new AssetDocument("fog", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 27, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene } };
        var frame = new AnimationFrame(.5, [new(1, 0, 0, Matrix4x4.Identity, true, .5f, 0, 0, .5)], [], [], [], null, new(true, Vector3.UnitZ, 5, 10), default, default, [], [], []);
        await preview.ShowAnimationAsync(context, frame, resolver, false, CancellationToken.None);
        var mesh = Assert.Single(preview.EnumerateRenderMeshes()); Assert.Equal(SceneViewport.RenderMeshRole.Animation, mesh.Role);
        var geometry = mesh.GeometryIdentity; Assert.NotNull(geometry); var positions = mesh.Positions; var transform = mesh.Transform;
        // Fog and lighting recolour in place: the same geometry, positions, transform and material object every frame.
        var material = mesh.MaterialIdentity; Assert.NotNull(material);
        var camera = preview.DebugCamera;
        var prepare = typeof(SceneViewport).GetMethod("PrepareCameraFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? Field(string name) => typeof(SceneViewport).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview);
        void Move(double distance) { camera.Position = new(0, 0, distance); camera.LookDirection = new(0, 0, -distance); prepare.Invoke(preview, [TimeSpan.Zero]); }
        Move(3); var near = mesh.Colors!;
        Move(20); var far = mesh.Colors!;
        Assert.NotEqual(near[0], far[0]); Assert.Equal(0, far[0].X); Assert.Equal(0, far[0].Y);
        Assert.InRange(far[0].Z, .24f, .26f); Assert.Equal(.5f, far[0].W);
        Assert.Equal(1, mesh.DiffuseColor.Z); Assert.Same(frame, Field("animationFrame"));
        Assert.Same(positions, mesh.Positions); Assert.Equal(transform, mesh.Transform); Assert.Same(geometry, mesh.GeometryIdentity); Assert.Same(material, mesh.MaterialIdentity);
        prepare.Invoke(preview, [TimeSpan.Zero]); Assert.Same(far, mesh.Colors);
        Move(3); Assert.Equal(near.ToArray(), mesh.Colors!.ToArray());
        preview.UpdateAnimationFrame(frame, effectLighting: false); var noFog = mesh.Colors;
        Move(20); Assert.Same(noFog, mesh.Colors);

        // Exercise the real frame admission at a small allowance. Three poses
        // and three lights cost16 units; two poses cost11. The inactive light
        // still costs a visit, and refusal must precede mesh/frame publication.
        preview.MaximumAnimationLightingWork = 12;
        AnimationLight[] lights = [new(1, Vector3.Zero, Vector3.UnitX, 100, .25f, true),
            new(2, Vector3.Zero, Vector3.UnitY, 100, .5f, true), new(3, Vector3.Zero, Vector3.One, 100, 1, false)];
        var tooLarge = frame with { Nodes = Enumerable.Range(1, 3).Select(i => frame.Nodes[0] with { Id = i }).ToArray(), Lights = lights };
        Assert.Throws<SceneViewport.RenderLimitException>(() => preview.UpdateAnimationFrame(tooLarge));
        Assert.Same(frame, Field("animationFrame")); Assert.Equal(1, preview.AnimationMeshCount);
        Assert.Same(noFog, mesh.Colors);
        var lit = tooLarge with { Nodes = tooLarge.Nodes.Take(2).ToArray() };
        preview.UpdateAnimationFrame(lit);
        Assert.Equal(11, preview.AnimationLightingWork); Assert.Equal(6, preview.AnimationLightVisits);
        Move(3);
        Assert.InRange(mesh.DiffuseColor.X, .82499f, .82501f);
        Assert.InRange(mesh.DiffuseColor.Y, .95624f, .95626f);
        Assert.InRange(mesh.DiffuseColor.Z, .75280f, .75283f);
        Assert.Equal(.5f, mesh.DiffuseColor.W); Assert.Same(material, mesh.MaterialIdentity);
        for (int i = 0; i < 8; i++) { Move(20); Move(3); }
        Assert.Equal(6, preview.AnimationLightVisits); // Fog/camera never rescan lights.
        preview.UpdateAnimationFrame(lit, effectLighting: false); Move(3);
        Assert.Equal(0, preview.AnimationLightVisits); Assert.Equal(1, mesh.DiffuseColor.X);
        preview.UpdateAnimationFrame(lit); Move(3);
        Assert.InRange(mesh.DiffuseColor.X, .82499f, .82501f); // Options refresh the cached light result.
        Assert.Same(material, mesh.MaterialIdentity);
        await Assert.ThrowsAsync<SceneViewport.RenderLimitException>(() => preview.ShowAnimationAsync(context, tooLarge, resolver, false, TestContext.Current.CancellationToken));
        Assert.Equal(0, preview.AnimationMeshCount); Assert.Equal(0, preview.RetainedGeometryBytes);
        await preview.ShowAnimationAsync(context, lit, resolver, false, TestContext.Current.CancellationToken);
        Assert.Equal(2, preview.AnimationMeshCount);
        // Splitting the same material by sidedness also splits its light visits.
        scene.Models[0] = scene.Models[0] with { Polygons = [scene.Models[0].Polygons[0], scene.Models[0].Polygons[0] with { Flags = 0x103 }] };
        var recoil = new AnimationPreviewContext { Package = context.Package, World = new AssetDocument("mixed", new(0, DateTime.MinValue),
            new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene } };
        preview.MaximumAnimationLightingWork = 19;
        await Assert.ThrowsAsync<SceneViewport.RenderLimitException>(() => preview.ShowAnimationAsync(recoil, lit, resolver, false, TestContext.Current.CancellationToken));
        preview.MaximumAnimationLightingWork = 20;
        await preview.ShowAnimationAsync(recoil, lit, resolver, false, TestContext.Current.CancellationToken);
        Assert.Equal(20, preview.AnimationLightingWork); Assert.Equal(12, preview.AnimationLightVisits);
        Assert.Equal(4, preview.AnimationMeshCount);
    }
}
