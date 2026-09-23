using System.Numerics;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    private readonly Dictionary<int, (HorizonBinding Binding, Vector3 Origin)> horizonNodes = [];
    private bool horizonEnabled = true;
    public MissionSceneContext? Mission { get; private set; }
    public GameScene? PreviewScene { get; private set; }
    public bool HorizonEnabled
    {
        get => horizonEnabled;
        set { horizonEnabled = value; if (animationFrame != null) UpdateAnimationFrame(animationFrame, false, animationEffectLighting); RefreshHorizon(); }
    }
    private void ConfigureHorizon(GameScene scene)
    {
        horizonNodes.Clear();
        foreach (var binding in MissionSceneContext.FindHorizons(scene))
        {
            Vector3 origin = WorldTransform(scene, binding.Root).Translation;
            Stack<int> pending = new(); HashSet<int> seen = []; pending.Push(binding.Root);
            while (pending.TryPop(out int index))
            {
                if (index < 0 || index >= scene.Nodes.Count || !seen.Add(index)) continue;
                horizonNodes[index] = (binding, origin);
                foreach (int child in SceneBuilder.Children(scene.Nodes[index])) pending.Push(child);
            }
        }
    }
    private bool IsHorizon(int index) => horizonNodes.ContainsKey(index);
    private Matrix4x4 HorizonTransform(int index, Matrix4x4 transform)
    {
        if (!horizonNodes.TryGetValue(index, out var horizon) || viewport.Camera is not HCamera camera) return transform;
        Vector3 origin = animationFrame?.Nodes.FirstOrDefault(p => p.SourceNode == horizon.Binding.Root)?.Transform.Translation ?? horizon.Origin;
        var p = camera.Position;
        transform.Translation += new Vector3((float)p.X, horizon.Binding.FollowHeight ? (float)p.Y : origin.Y, (float)p.Z) - origin;
        return transform;
    }
    private void RefreshHorizon()
    {
        foreach (var mesh in meshes)
            if (visiblePlacements.TryGetValue(mesh, out var items) && items.Any(p => IsHorizon(p.NodeIndex)))
                mesh.Instances = items.Select(p => HorizonTransform(p.NodeIndex, p.Transform)).ToArray();
        if (animationFrame != null)
            foreach (var pose in animationFrame.Nodes.Where(p => IsHorizon(p.SourceNode)))
                if (animatedMeshes.TryGetValue(pose.Id, out var items))
                    foreach (var item in items) item.Mesh.Transform = new MatrixTransform3D(ToWpf(HorizonTransform(pose.SourceNode, pose.Transform)));
    }
    public static Matrix4x4 WorldTransform(GameScene scene, int index)
    {
        Matrix4x4 result = Matrix4x4.Identity; HashSet<int> seen = [];
        while (index >= 0 && index < scene.Nodes.Count && seen.Add(index))
        { var node = scene.Nodes[index]; result *= SceneBuilder.LocalTransform(node); index = node.Parents.FirstOrDefault(-1); }
        return result;
    }
}
