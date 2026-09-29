using System.Numerics;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

/// <summary>Deterministic authored motion sampling; no gait, aiming, IK, or gameplay simulation.</summary>
public sealed class MotionPreview
{
    private readonly MotionClip clip;
    private readonly GameScene scene;
    private readonly MechAssembly assembly;
    private readonly Dictionary<int, int> tracks = [];
    public IReadOnlyList<string> Diagnostics { get; }
    public MotionPreview(MotionClip clip, ZbdDocument library, MechAssembly assembly, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        this.clip = clip; scene = library.Scene ?? throw new InvalidDataException("A decoded mech library is required."); this.assembly = assembly;
        if (assembly.RootNode < 0 || assembly.NodeCount < 1 || assembly.RootNode + (long)assembly.NodeCount > scene.Nodes.Count) throw new InvalidDataException("Invalid mech member range.");
        List<string> notes = ["Authored motion preview. Aiming, gait adjustment, inverse kinematics and gameplay are not simulated."];
        Dictionary<string, int> nodesByName = new(StringComparer.Ordinal), partCounts = new(StringComparer.Ordinal);
        for (int i = assembly.RootNode; i < assembly.RootNode + assembly.NodeCount; i++)
        {
            token.ThrowIfCancellationRequested();
            var node = scene.Nodes[i];
            if (!nodesByName.TryAdd(node.Name, node.Index)) nodesByName[node.Name] = -1;
        }
        foreach (var part in clip.Parts)
        {
            token.ThrowIfCancellationRequested();
            partCounts[part.Name] = partCounts.GetValueOrDefault(part.Name) + 1;
        }
        for (int i = 0; i < clip.Parts.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var part = clip.Parts[i];
            if (!nodesByName.TryGetValue(part.Name, out int node) || node < 0 || partCounts[part.Name] != 1) notes.Add($"Part #{i} {part.Name}: missing or ambiguous node; stored pose retained.");
            else tracks.Add(node, i);
        }
        token.ThrowIfCancellationRequested();
        Diagnostics = notes;
    }
    public AnimationFrame At(double seconds, int lod = 0, CancellationToken token = default)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new InvalidDataException("Motion time must be finite and nonnegative.");
        Dictionary<int, Matrix4x4> transforms = []; HashSet<int> active = [];
        Matrix4x4 World(int index, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (transforms.TryGetValue(index, out var cached)) return cached;
            if (depth > 256 || !active.Add(index) || index < assembly.RootNode || index >= assembly.RootNode + assembly.NodeCount)
                throw new InvalidDataException("Invalid motion hierarchy.");
            var node = scene.Nodes[index]; Matrix4x4 local = SceneBuilder.LocalTransform(node);
            if (tracks.TryGetValue(index, out int track))
            {
                var sampled = clip.Sample(track, seconds);
                if (!Matrix4x4.Decompose(local, out var scale, out _, out _)) scale = Vector3.One;
                local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(sampled.Rotation) * Matrix4x4.CreateTranslation(sampled.Translation);
            }
            if (index != assembly.RootNode && node.Parents.Length == 1) local *= World(node.Parents[0], depth + 1);
            active.Remove(index); return transforms[index] = local;
        }
        var view = SceneBuilder.Assemble(scene, lod, token: token, rootIndex: assembly.RootNode);
        var poses = view.Placements.Select(p => new AnimationNodePose(p.NodeIndex, p.NodeIndex, p.ModelIndex, World(p.NodeIndex, 0), true, 1, 0, 0, seconds)).ToArray();
        return new(seconds, poses, [], [], [], null, null, default, default, [], [], Diagnostics);
    }
}
