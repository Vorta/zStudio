using System.IO;
using System.Numerics;
using System.Windows;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Rendering;

internal static class InspectionRuntimeCheck
{
    internal static async Task Run()
    {
        using var resolver = new AssetResolver(Path.GetTempPath());
        using var view = new SceneViewport();
        var window = new Window { Content = view, Width = 900, Height = 650, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try
        {
            var scene = new GameScene(); scene.Materials.Add(new() { ["alpha"] = 255 });
            for (int i = 0; i < 2; i++)
            {
                scene.Nodes.Add(new(i, "part" + i, "object3d", i, [], [], new(), new()));
                scene.Models.Add(new(i, [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)], [], [],
                    [new(0, 4, [0, 1, 2, 3], [], [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY], [])], []));
            }
            var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] },
                World = new("inspection", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, ""), ReadOnlyMemory<byte>.Empty) { Scene = scene } };
            const long first = 1L << 32, second = 2L << 32;
            AnimationFrame Frame(params AnimationNodePose[] poses) => new(0, poses, [], [], [], null, null, Vector4.Zero, Vector4.Zero, [], [], []);
            AnimationNodePose Pose(long id, int node, int model, float x) => new(id, node, model, Matrix4x4.CreateTranslation(x, 0, 0), true, 1, -1, 0, 0);
            await view.ShowAnimationAsync(context, Frame(Pose(first, 0, 0, -20), Pose(second, 0, 0, 20), Pose(3L << 32, 1, 1, 50)), resolver, false, CancellationToken.None);
            await Task.Delay(150);
            if (!view.SelectInspectionNode(0, first)) throw new InvalidDataException("Cannot select the requested runtime copy.");
            string target = view.SelectedInspection!.Target;
            bool framed = view.TryFrame("selected");
            var camera = view.CaptureView(); var center = camera.Position + camera.LookDirection;
            bool exactFrame = framed && Math.Abs(center.X + 20) < .01;
            // A later source-tree selection owns Frame selected even while the card stays pinned.
            view.SelectFramingNode(1);
            bool sourceFramed = view.TryFrame("selected");
            camera = view.CaptureView(); center = camera.Position + camera.LookDirection;
            bool sourceFrame = sourceFramed && Math.Abs(center.X - 50) < .01;
            // Seeking/rebinding can reuse a numeric runtime slot for different source geometry.
            view.UpdateAnimationFrame(Frame(Pose(first, 1, 1, 10)));
            bool targetExpired = view.InspectTarget(target) == null;
            bool replacement = view.SelectInspectionNode(1, first) && view.SelectedInspection is { Node: 1, Model: 1 };
            if (!exactFrame || !sourceFrame || !targetExpired || !replacement)
                throw new InvalidDataException($"Runtime inspection: exactFrame={exactFrame}, sourceFrame={sourceFrame}, oldTargetExpired={targetExpired}, replacementIdentified={replacement}");
            string replacementTarget = view.SelectedInspection!.Target;
            view.UpdateAnimationFrame(Frame());
            if (view.InspectTarget(replacementTarget) != null) throw new InvalidDataException("Expired runtime target remained addressable.");
            Console.WriteLine("PASS: selected runtime copy framing, slot reuse, source/model identity and expiry");
        }
        finally { window.Close(); }
    }
}
