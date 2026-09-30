using System.Numerics;
using System.Buffers;
using System.Collections.Frozen;
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
    private sealed record Transform(int Node, int Parent, int Track, Matrix4x4 Local, Vector3 Scale);
    private sealed record Binding(Transform[] Transforms, (ScenePlacement Placement, int Transform)[] Placements);
    private readonly Dictionary<int, Binding> bindings = [];
    private readonly object bindingGate = new();
    private readonly int lodCount;
    public const int MaximumPreviewPlacements = 16384;
    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlySet<int> InspectionNodes { get; }
    public int DiagnosticCount => Diagnostics is PreviewNotes notes ? notes.TotalCount : Diagnostics.Count;
    public MotionPreview(MotionClip clip, ZbdDocument library, MechAssembly assembly, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        this.clip = clip; scene = library.Scene ?? throw new InvalidDataException("A decoded mech library is required."); this.assembly = assembly;
        if (assembly.RootNode < 0 || assembly.NodeCount < 1 || assembly.RootNode + (long)assembly.NodeCount > scene.Nodes.Count) throw new InvalidDataException("Invalid mech member range.");
        InspectionNodes = Enumerable.Range(assembly.RootNode, assembly.NodeCount).ToFrozenSet();
        PreviewNotes notes = new(); notes.Add("Authored motion preview. Aiming, gait adjustment, inverse kinematics and gameplay are not simulated.");
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
            if (!nodesByName.TryGetValue(part.Name, out int node) || node < 0 || partCounts[part.Name] != 1) notes.Add($"Part #{i} {MissionAiValves.Short(part.Name)}: missing or ambiguous node; stored pose retained.");
            else tracks.Add(node, i);
        }
        token.ThrowIfCancellationRequested();
        Diagnostics = notes;
        lodCount = new SceneLods(scene).Count([assembly.RootNode]);
    }
    public AnimationFrame At(double seconds, int lod = 0, CancellationToken token = default)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new InvalidDataException("Motion time must be finite and nonnegative.");
        token.ThrowIfCancellationRequested();
        Binding binding;
        lock (bindingGate)
        {
            int rank = Math.Clamp(lod, 0, lodCount - 1);
            if (!bindings.TryGetValue(rank, out binding!)) bindings[rank] = binding = Prepare(rank, token);
        }
        var matrices = ArrayPool<Matrix4x4>.Shared.Rent(binding.Transforms.Length);
        try
        {
            for (int i = 0; i < binding.Transforms.Length; i++)
            {
                token.ThrowIfCancellationRequested(); var transform = binding.Transforms[i]; var local = transform.Local;
                if (transform.Track >= 0)
                {
                    var sampled = clip.Sample(transform.Track, seconds);
                    local = Matrix4x4.CreateScale(transform.Scale) * Matrix4x4.CreateFromQuaternion(sampled.Rotation) * Matrix4x4.CreateTranslation(sampled.Translation);
                }
                matrices[i] = transform.Parent < 0 ? local : local * matrices[transform.Parent];
            }
            var poses = new AnimationNodePose[binding.Placements.Length];
            for (int i = 0; i < poses.Length; i++)
            { token.ThrowIfCancellationRequested(); var (p, t) = binding.Placements[i]; poses[i] = new(p.NodeIndex, p.NodeIndex, p.ModelIndex, matrices[t], true, 1, 0, 0, seconds); }
            return new(seconds, poses, [], [], [], null, null, default, default, [], [], Diagnostics);
        }
        finally { ArrayPool<Matrix4x4>.Shared.Return(matrices); }
    }
    private Binding Prepare(int lod, CancellationToken token)
    {
        var view = SceneBuilder.Assemble(scene, lod, token: token, rootIndex: assembly.RootNode);
        if (view.Placements.Count > MaximumPreviewPlacements) throw new InvalidDataException($"Motion preview supports at most {MaximumPreviewPlacements:N0} visible model placements. Inspect or export this complete assembly from the library.");
        List<Transform> ordered = []; Dictionary<int, int> indices = []; HashSet<int> active = [];
        int Visit(int index, int depth)
        {
            token.ThrowIfCancellationRequested(); if (indices.TryGetValue(index, out int found)) return found;
            if (depth > 256 || index < assembly.RootNode || index >= assembly.RootNode + assembly.NodeCount || !active.Add(index)) throw new InvalidDataException("Invalid motion hierarchy.");
            var node = scene.Nodes[index]; int parent = index != assembly.RootNode && node.Parents.Length == 1 ? Visit(node.Parents[0], depth + 1) : -1;
            var local = SceneBuilder.LocalTransform(node); if (!Matrix4x4.Decompose(local, out var scale, out _, out _)) scale = Vector3.One;
            int result = ordered.Count; ordered.Add(new(index, parent, tracks.GetValueOrDefault(index, -1), local, scale)); indices.Add(index, result); active.Remove(index); return result;
        }
        var placements = view.Placements.Select(p => (p, Visit(p.NodeIndex, 0))).ToArray();
        return new(ordered.ToArray(), placements);
    }
}
