using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Rendering;
using Xunit;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;

namespace Recoil.Zbd.Desktop.Tests;

internal static class AnimationFogChecks
{
    internal static async Task Run()
    {
        using var resolver = new AssetResolver(Path.GetTempPath()); using var preview = new SceneViewport();
        var scene = new GameScene(); scene.Materials.Add(new() { ["alpha"] = 255 });
        scene.Models.Add(new(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [],
            [new(0, 3, [0, 1, 2], [], [], []) { Colors = [new(255, 128, 64), new(255, 128, 64), new(255, 128, 64)] }], []));
        var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = new("fog", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 27, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene } };
        var frame = new AnimationFrame(.5, [new(1, 0, 0, Matrix4x4.Identity, true, .5f, 0, 0, .5)], [], [], [], null, new(true, Vector3.UnitZ, 5, 10), default, default, [], [], []);
        await preview.ShowAnimationAsync(context, frame, resolver, false, CancellationToken.None);
        var surface = (Viewport3DX)preview.RenderSurface;
        var mesh = surface.Items.OfType<SortingGroupModel3D>().Single().Children.OfType<MeshGeometryModel3D>().Single();
        var geometry = Assert.IsType<MeshGeometry3D>(mesh.Geometry); var positions = geometry.Positions; var transform = mesh.Transform; var material = (DiffuseMaterial)mesh.Material!;
        var camera = (HelixToolkit.Wpf.SharpDX.PerspectiveCamera)surface.Camera!;
        var prepare = typeof(SceneViewport).GetMethod("PrepareCameraFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? Field(string name) => typeof(SceneViewport).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview);
        void Move(double distance) { camera.Position = new(0, 0, distance); camera.LookDirection = new(0, 0, -distance); prepare.Invoke(preview, [TimeSpan.Zero]); }
        Move(3); var near = geometry.Colors!;
        Move(20); var far = geometry.Colors!;
        Assert.NotEqual(near[0], far[0]); Assert.Equal(0, far[0].Red); Assert.Equal(0, far[0].Green);
        Assert.InRange(far[0].Blue, .24f, .26f); Assert.Equal(.5f, far[0].Alpha);
        Assert.Equal(1, material.DiffuseColor.Blue); Assert.Same(frame, Field("animationFrame"));
        Assert.Same(positions, geometry.Positions); Assert.Same(transform, mesh.Transform); Assert.Same(geometry, mesh.Geometry);
        prepare.Invoke(preview, [TimeSpan.Zero]); Assert.Same(far, geometry.Colors);
        Move(3); Assert.Equal(near.ToArray(), geometry.Colors!.ToArray());
        preview.UpdateAnimationFrame(frame, effectLighting: false); var noFog = geometry.Colors;
        Move(20); Assert.Same(noFog, geometry.Colors);

        // Exercise the real frame admission at a small allowance. Three poses
        // and three lights cost16 units; two poses cost11. The inactive light
        // still costs a visit, and refusal must precede mesh/frame publication.
        preview.MaximumAnimationLightingWork = 12;
        AnimationLight[] lights = [new(1, Vector3.Zero, Vector3.UnitX, 100, .25f, true),
            new(2, Vector3.Zero, Vector3.UnitY, 100, .5f, true), new(3, Vector3.Zero, Vector3.One, 100, 1, false)];
        var tooLarge = frame with { Nodes = Enumerable.Range(1, 3).Select(i => frame.Nodes[0] with { Id = i }).ToArray(), Lights = lights };
        Assert.Throws<SceneViewport.RenderLimitException>(() => preview.UpdateAnimationFrame(tooLarge));
        Assert.Same(frame, Field("animationFrame")); Assert.Equal(1, preview.AnimationMeshCount);
        Assert.Same(noFog, geometry.Colors);
        var lit = tooLarge with { Nodes = tooLarge.Nodes.Take(2).ToArray() };
        preview.UpdateAnimationFrame(lit);
        Assert.Equal(11, preview.AnimationLightingWork); Assert.Equal(6, preview.AnimationLightVisits);
        Move(3);
        Assert.InRange(material.DiffuseColor.Red, .82499f, .82501f);
        Assert.InRange(material.DiffuseColor.Green, .95624f, .95626f);
        Assert.InRange(material.DiffuseColor.Blue, .75280f, .75283f);
        Assert.Equal(.5f, material.DiffuseColor.Alpha);
        for (int i = 0; i < 8; i++) { Move(20); Move(3); }
        Assert.Equal(6, preview.AnimationLightVisits); // Fog/camera never rescan lights.
        preview.UpdateAnimationFrame(lit, effectLighting: false); Move(3);
        Assert.Equal(0, preview.AnimationLightVisits); Assert.Equal(1, material.DiffuseColor.Red);
        preview.UpdateAnimationFrame(lit); Move(3);
        Assert.InRange(material.DiffuseColor.Red, .82499f, .82501f); // Options refresh the cached light result.
        await Assert.ThrowsAsync<SceneViewport.RenderLimitException>(() => preview.ShowAnimationAsync(context, tooLarge, resolver, false, TestContext.Current.CancellationToken));
        Assert.Equal(0, preview.AnimationMeshCount); Assert.Equal(0, preview.RetainedGeometryBytes);
        await preview.ShowAnimationAsync(context, lit, resolver, false, TestContext.Current.CancellationToken);
        Assert.Equal(2, preview.AnimationMeshCount);
    }
}
