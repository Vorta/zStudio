using System.Numerics;
using System.Windows.Media.Media3D;
using HelixToolkit.Maths;
using Recoil.Zbd.Core;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;

namespace Recoil.Zbd.Rendering;

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
            bounds.Union(new Rect3D(node.Position.X - 2, node.Position.Y - 2, node.Position.Z - 2, 4, 4, 4));
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
            var assignments = MissionAiValves.EdgeAssignments(network, ValveFilter).Take(remainingEdges).Select(c => (From: c.From!.Value, To: c.To!.Value)).ToArray();
            Dictionary<int, Vector3> endpoints = new(Math.Min(assignments.Length * 2, 2048));
            foreach (var (from, to) in assignments) { endpoints.TryAdd(from, default); endpoints.TryAdd(to, default); }
            if (endpoints.Count > 0)
                foreach (var node in network.Nodes)
                    if (endpoints.ContainsKey(node.Index) && network.ResolvedIndices.Contains(node.Index)) endpoints[node.Index] = node.Position;
            var edges = assignments.Where(e => network.ResolvedIndices.Contains(e.From) && network.ResolvedIndices.Contains(e.To))
                .Select(e => (From: endpoints[e.From], To: endpoints[e.To])).ToArray();
            remainingEdges -= edges.Length;
            if (edges.Length > 0) { var mesh = AiMesh(Links(), color); aiLinks.Add(mesh, Links); }
            MeshGeometry3D Links()
            {
                List<Vector3> positions = []; List<int> indices = [];
                var pose = CaptureView(); var forward = Vector3.Normalize(new Vector3((float)pose.LookDirection.X, (float)pose.LookDirection.Y, (float)pose.LookDirection.Z));
                foreach (var edge in edges)
                {
                    var delta = edge.To - edge.From; if (delta.LengthSquared() < 1e-8f) continue;
                    var direction = Vector3.Normalize(delta); var cross = Vector3.Cross(direction, forward);
                    if (cross.LengthSquared() < 1e-8f) cross = Vector3.Cross(direction, Math.Abs(direction.Y) < .9 ? Vector3.UnitY : Vector3.UnitX);
                    var side = Vector3.Normalize(cross) * AiScale(Vector3.Lerp(edge.From, edge.To, .5f), 1.5);
                    for (int step = 0; step < 12; step += 2)
                    {
                        var a = Vector3.Lerp(edge.From, edge.To, step / 12f); var b = Vector3.Lerp(edge.From, edge.To, (step + 1) / 12f);
                        int i = positions.Count; positions.AddRange([a - side, a + side, b + side, b - side]); indices.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]);
                    }
                }
                return new() { Positions = new(positions), Indices = new(indices), Normals = new(positions.Select(_ => Vector3.UnitY)) };
            }
        }
    }
    private static MeshGeometry3D ValveCage()
    {
        var source = AiOctahedron(); List<Vector3> positions = []; List<int> indices = [];
        var sourceIndices = source.Indices!; var sourcePositions = source.Positions!;
        for (int i = 0; i < sourceIndices.Count; i += 3)
        {
            Vector3[] p = [sourcePositions[sourceIndices[i]], sourcePositions[sourceIndices[i + 1]], sourcePositions[sourceIndices[i + 2]]];
            var center = (p[0] + p[1] + p[2]) / 3;
            for (int j = 0; j < 3; j++)
            {
                var a = p[j]; var b = p[(j + 1) % 3]; int at = positions.Count;
                positions.AddRange([a, b, Vector3.Lerp(b, center, .15f), Vector3.Lerp(a, center, .15f)]);
                indices.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);
            }
        }
        return new() { Positions = new(positions), Indices = new(indices), Normals = new(positions.Select(p => Vector3.Normalize(p))) };
    }
}
