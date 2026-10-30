using System.IO;
using System.Numerics;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Rendering;
using Xunit;

namespace zStudio.Desktop.Tests;

internal static class WorldAnimationAttachmentChecks
{
    internal static async Task Run()
    {
        using var resolver = new AssetResolver(Path.GetTempPath());
        using var viewport = new SceneViewport();
        GameScene scene = new();
        scene.Materials.Add(new() { ["alpha"] = 128, ["texture_index"] = -1 });
        scene.Models.Add(new(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [new(0, 3, [0, 1, 2], [], [], [])], []));
        scene.Nodes.Add(new(0, "world", "world", null, [], [1, 2], new(), new()));
        for (int i = 1; i <= 2; i++) scene.Nodes.Add(new(i, "mesh" + i, "object3d", 0, [0], [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
        var world = new AssetDocument(Path.Combine(Path.GetTempPath(), "gamez.zbd"), new(0, DateTime.MinValue),
            new(FormatFamily.GameZ, 27, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        var mission = new MissionSceneContext(scene, [0, 1, 2], [], [], [], MissionLayoutSelection.For(MissionDifficulty.Medium), 3);
        var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = world, Mission = mission };
        await viewport.ShowAsync(world, world.Add(AssetKind.World, 0, "world", 0, 0), resolver, null, 0, TestContext.Current.CancellationToken, mission: mission);
        var baseline = viewport.EnumerateRenderMeshes().Where(m => m.Role == SceneViewport.RenderMeshRole.Scene).ToArray();
        Assert.NotEmpty(baseline);
        var materials = baseline.Select(m => m.MaterialIdentity).ToArray();
        int generation = viewport.SceneGeneration;
        var free = viewport.CaptureView();
        Assert.True(viewport.SelectInspectionNode(1));
        string selection = viewport.SelectedInspection!.Target;
        viewport.SetHiddenNodes([2]); viewport.SetWireframe(true);
        int outlines = viewport.PolygonOutlineCount;
        var frame = new AnimationFrame(0, [new(1, 1, 0, Matrix4x4.CreateTranslation(8, 0, 0), true, 1, 0, 0, 0)],
            [], [], [], new(new(10, 8, 2), Vector3.Zero, 70), null, default, default, [], [], []);

        Guid first = viewport.AttachWorldAnimation(context, resolver, generation, CancellationToken.None);
        Assert.Equal(free, viewport.CaptureView());
        Assert.True(viewport.UpdateWorldAnimation(first, frame, followCamera: true));
        Assert.True(viewport.WorldAnimationCameraFollowing);
        Assert.NotEqual(free.Position, viewport.CaptureView().Position);
        Assert.Same(mission, viewport.Mission);
        Assert.All(baseline, mesh => Assert.Empty(mesh.Instances!));
        Assert.Equal(1L, viewport.SelectedInspection!.RuntimeInstance);
        Assert.True(viewport.SelectedInspection.Active);
        Assert.True(viewport.PolygonOutlineCount > outlines);

        // A complete frame with an instance-owned copy restores the shared source actor.
        viewport.UpdateWorldAnimation(first, frame with { Nodes = [frame.Nodes[0] with { Id = (1L << 32) | 1 }] });
        Assert.Equal(free, viewport.CaptureView());
        Assert.False(viewport.WorldAnimationCameraFollowing);
        Assert.All(baseline.SelectMany(mesh => mesh.Instances!), transform => Assert.Equal(Matrix4x4.Identity, transform));
        Assert.Equal(selection, viewport.SelectedInspection!.Target);
        Assert.Equal(1, baseline.Sum(mesh => mesh.Instances!.Count));
        Assert.True(viewport.SelectInspectionNode(1, (1L << 32) | 1));
        Assert.True(viewport.SelectionBoundsVisible);
        Assert.All(viewport.SelectionBox!.Positions, point => Assert.InRange(point.X, 8, 9));
        Assert.True(viewport.SelectInspectionNode(1));
        Assert.All(viewport.SelectionBox!.Positions, point => Assert.InRange(point.X, 0, 1));
        Assert.True(viewport.DetachWorldAnimation(first));
        Assert.Same(scene, viewport.PreviewScene); Assert.Null(viewport.InspectionNodes);
        Assert.Equal(generation, viewport.SceneGeneration);
        Assert.Equal(outlines, viewport.PolygonOutlineCount);
        Assert.Equal(baseline, viewport.EnumerateRenderMeshes().Where(m => m.Role == SceneViewport.RenderMeshRole.Scene));
        Assert.Equal(materials, baseline.Select(m => m.MaterialIdentity));
        Assert.Equal(0, viewport.AnimationMeshCount);

        Guid second = viewport.AttachWorldAnimation(context, resolver, generation, CancellationToken.None);
        Assert.False(viewport.DetachWorldAnimation(first));
        Assert.False(viewport.UpdateWorldAnimation(first, frame));
        Assert.True(viewport.UpdateWorldAnimation(second, frame, followCamera: true));
        viewport.RotateBy(5, 0);
        var navigated = viewport.CaptureView();
        Assert.False(viewport.WorldAnimationCameraFollowing);
        viewport.UpdateWorldAnimation(second, frame, followCamera: true);
        Assert.Equal(navigated, viewport.CaptureView());
        viewport.DetachWorldAnimation(second);
        Assert.Equal(navigated, viewport.CaptureView());

        // Admission failure rolls back to the same world, including transparent static batches.
        Guid refused = viewport.AttachWorldAnimation(context, resolver, generation, CancellationToken.None);
        viewport.MaximumAnimationLightingWork = 0;
        Assert.Throws<SceneViewport.RenderLimitException>(() => viewport.UpdateWorldAnimation(refused, frame));
        Assert.Same(scene, viewport.PreviewScene); Assert.Equal(0, viewport.AnimationMeshCount);
        Assert.Equal(baseline, viewport.EnumerateRenderMeshes().Where(m => m.Role == SceneViewport.RenderMeshRole.Scene));
        Assert.Equal(1, baseline.Sum(mesh => mesh.Instances!.Count));
        viewport.Clear();
        Assert.NotEqual(generation, viewport.SceneGeneration);
        Assert.Throws<InvalidOperationException>(() => viewport.AttachWorldAnimation(context, resolver, generation, CancellationToken.None));
    }
}
