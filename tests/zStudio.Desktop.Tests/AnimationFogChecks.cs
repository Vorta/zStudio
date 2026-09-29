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
    }
}
