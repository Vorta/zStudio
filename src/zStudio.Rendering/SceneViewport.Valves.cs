using System.Numerics;
using System.Windows.Media.Media3D;
using zStudio.Core;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    public bool ValveOverlayVisible { get; private set; }
    public string? ValveFilter { get; private set; }
    public void SetValveOptions(bool visible, string? valve = null)
    {
        if (ValveOverlayVisible == visible && ValveFilter == valve) return;
        ValveOverlayVisible = visible; ValveFilter = valve; RebuildAiOverlay();
    }
    public bool FrameValveAssociations()
    {
        if (!AiVisible || IsPickupDragging || IsFlyActive) return false;
        Rect3D bounds = Rect3D.Empty;
        foreach (var network in VisibleAiNetworks)
        foreach (var node in network.Nodes.Where(n => MissionAiValves.HasAssociation(network, n, ValveFilter)))
        {
            var point = AiPosition(node);
            bounds.Union(new Rect3D(point.X - 2, point.Y - 2, point.Z - 2, 4, 4, 4));
        }
        return FrameBounds(bounds, true);
    }
    private void BuildValveOverlay()
    {
        if (!ValveOverlayVisible) return;
        int remaining = 4096, remainingEdges = 1024;
        foreach (var network in VisibleAiNetworks.Where(n => n.IsMw3))
        {
            var nodes = network.Nodes.Where(n => MissionAiValves.HasAssociation(network, n, ValveFilter)).Take(remaining).ToArray();
            remaining -= nodes.Length;
            Color4 color = ValveFilter == null ? new(.95f, .95f, .95f, 1) : new(1, .8f, .2f, 1);
            if (nodes.Length > 0) aiMarkers.Add((AiMesh(ValveCage(), color), nodes, 8));
            // Select the capped edges first, then resolve only their endpoints: toggling or filtering valves must
            // not index every node. ValveEdgeSources retain only edges whose endpoint indices are unique.
            var assignments = MissionAiValves.EdgeAssignments(network, ValveFilter)
                .SelectMany(c => (c.TargetNodes.Count == 0 ? [c.To!.Value] : c.TargetNodes).Select(t => (From: c.From!.Value, To: t))).Take(remainingEdges).ToArray();
            Dictionary<int, AiNode?> endpoints = new(Math.Min(assignments.Length * 2, 2048));
            foreach (var (from, to) in assignments) { endpoints.TryAdd(from, default); endpoints.TryAdd(to, default); }
            if (endpoints.Count > 0)
                foreach (var node in network.Nodes)
                    if (endpoints.ContainsKey(node.Index) && network.ResolvedIndices.Contains(node.Index)) endpoints[node.Index] = node;
            var edges = assignments.Where(e => network.ResolvedIndices.Contains(e.From) && network.ResolvedIndices.Contains(e.To))
                .Select(e => (From: endpoints[e.From], To: endpoints[e.To])).ToArray();
            remainingEdges -= edges.Length;
            if (edges.Length > 0) { var mesh = AiMesh(Links(), color); aiLinks.Add(mesh, Links); }
            MeshData Links()
            {
                List<Vector3> positions = []; List<int> indices = [];
                var pose = CaptureView(); var forward = Vector3.Normalize(new Vector3((float)pose.LookDirection.X, (float)pose.LookDirection.Y, (float)pose.LookDirection.Z));
                foreach (var edge in edges)
                {
                    if (edge.From == null || edge.To == null) continue;
                    var from = AiPosition(edge.From); var to = AiPosition(edge.To);
                    var delta = to - from; if (delta.LengthSquared() < 1e-8f) continue;
                    var direction = Vector3.Normalize(delta); var cross = Vector3.Cross(direction, forward);
                    if (cross.LengthSquared() < 1e-8f) cross = Vector3.Cross(direction, Math.Abs(direction.Y) < .9 ? Vector3.UnitY : Vector3.UnitX);
                    var side = Vector3.Normalize(cross) * AiScale(Vector3.Lerp(from, to, .5f), 1.5);
                    for (int step = 0; step < 12; step += 2)
                    {
                        var a = Vector3.Lerp(from, to, step / 12f); var b = Vector3.Lerp(from, to, (step + 1) / 12f);
                        int i = positions.Count; positions.AddRange([a - side, a + side, b + side, b - side]); indices.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]);
                    }
                }
                return new([.. positions], [.. indices], [.. positions.Select(_ => Vector3.UnitY)]);
            }
        }
    }
    private static MeshData ValveCage() => AiCircle();
}
