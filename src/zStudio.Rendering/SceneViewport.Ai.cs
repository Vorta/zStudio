using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    // Dedicated group: never part of authored meshes, depth bounds, isolation or export.
    private sealed class AiOverlayGroup : GroupModel3D;
    private AiOverlayGroup? aiOverlay;
    private readonly List<(MeshGeometryModel3D Mesh, AiNode[] Nodes, double Pixels)> aiMarkers = [];
    private readonly Dictionary<MeshGeometryModel3D, Func<MeshGeometry3D>> aiLinks = [];
    private MeshGeometryModel3D? aiSelectionMarker;
    private readonly List<MeshGeometryModel3D> aiDrawables = [];
    private string? hoveredAiNode;
    private ViewPose? aiMarkerPose;
    private double aiMarkerWidth, aiMarkerHeight;
    public AiNetworkSnapshot AiNetworks { get; private set; } = AiNetworkSnapshot.Empty;
    /// <summary>Overlay budget for the dispatcher-bound markers and links. The complete graph remains available for
    /// selection, inspection and export; retail MW3 missions stay far below it.</summary>
    public const int MaximumRenderedAiNodes = 16_384, MaximumRenderedAiLinks = 32_768;
    public int RenderedAiNodes { get; private set; }
    public int RenderedAiLinks { get; private set; }
    public bool AiRenderTruncated { get; private set; }
    private readonly List<AiNode> aiRendered = [];
    public bool AiVisible { get; private set; }
    public bool AiThroughGeometry { get; private set; } = true;
    public string? AiNetworkFilter { get; private set; }
    public string? SelectedAiNode { get; private set; }
    public event Action<string?>? AiNodeSelected;
    public event Action? AiPropertiesRequested;
    public event Action<string?>? AiLabelChanged;

    public void SetAiNetworks(AiNetworkSnapshot snapshot)
    {
        if (AiNetworks.Id == snapshot.Id) return;
        AiNetworks = snapshot; AiNetworkFilter = null; SelectedAiNode = null; hoveredAiNode = null; ValveFilter = null;
        RebuildAiOverlay();
    }
    public void SetAiOptions(bool visible, bool throughGeometry, string? network)
    {
        if (network != null && !AiNetworks.Networks.Any(n => n.Id == network)) throw new ArgumentException("AI network is unavailable.", nameof(network));
        if (AiVisible == visible && AiThroughGeometry == throughGeometry && AiNetworkFilter == network) return;
        AiVisible = visible; AiThroughGeometry = throughGeometry; AiNetworkFilter = network;
        if (!visible || SelectedAiNode is { } id && AiNetworks.Find(id) is { } found && network != null && found.Network.Id != network)
            SelectAiNode(null);
        hoveredAiNode = null; RebuildAiOverlay();
    }
    public bool SelectAiNode(string? id)
    {
        if (id != null && (!InspectionSelectionEnabled || IsPickupDragging || IsFlyActive)) return false;
        if (id != null && (!AiVisible || AiNetworks.Find(id) is not { } found || AiNetworkFilter != null && found.Network.Id != AiNetworkFilter)) return false;
        if (id != null && CanChangeInspection?.Invoke() == false) return false;
        SelectedAiNode = id;
        if (id != null) SelectedInspection = InspectAi(id);
        else if (SelectedInspection?.AiNode != null) SelectedInspection = null;
        InspectionChanged?.Invoke();
        if (id != null) { FramingSelection = null; SelectPickup(null, false, true); }
        RefreshPickupSelection(); RefreshAiSelection(); AiNodeSelected?.Invoke(id); return true;
    }
    private IEnumerable<AiNetwork> VisibleAiNetworks => AiVisible ? AiNetworks.Networks.Where(n => AiNetworkFilter == null || n.Id == AiNetworkFilter) : [];
    private void RebuildAiOverlay()
    {
        ++inspectionSerial;
        ClearAiDrawables();
        RenderedAiNodes = RenderedAiLinks = 0; AiRenderTruncated = false; aiRendered.Clear();
        if (!AiVisible) { PublishAiLabel(); return; }
        aiOverlay = new(); viewport.Items.Add(aiOverlay);
        foreach (var network in VisibleAiNetworks)
        {
            var color = AiNetworkColors.Color(network.AttackStrategy);
            // Materialize only the budgeted nodes and links; later networks and links beyond it are disclosed, not drawn.
            var nodes = network.Nodes.Take(MaximumRenderedAiNodes - RenderedAiNodes).ToArray();
            if (nodes.Length < network.Nodes.Count) AiRenderTruncated = true;
            if (nodes.Length == 0) continue;
            RenderedAiNodes += nodes.Length; aiRendered.AddRange(nodes);
            var markers = AiMesh(AiOctahedron(), color);
            aiMarkers.Add((markers, nodes, 4));
            var byId = nodes.ToDictionary(n => n.Id);
            List<(Vector3 Start, Vector3 End)> edges = [];
            foreach (var node in nodes)
            foreach (string target in node.PreviewLinks.Where(l => l.Target != null).Select(l => l.Target!).Distinct())
            {
                if (!byId.TryGetValue(target, out var end)) { AiRenderTruncated = true; continue; }
                if (RenderedAiLinks == MaximumRenderedAiLinks) { AiRenderTruncated = true; break; }
                edges.Add((node.Position, end.Position)); RenderedAiLinks++;
            }
            var lines = AiMesh(BuildLinks(), color); aiLinks.Add(lines, BuildLinks);
            MeshGeometry3D BuildLinks()
            {
                List<Vector3> positions = []; List<int> indices = [];
                var pose = CaptureView(); Vector3 forward = Vector3.Normalize(new((float)pose.LookDirection.X, (float)pose.LookDirection.Y, (float)pose.LookDirection.Z));
                foreach (var (start, end) in edges)
                {
                    if ((end - start).LengthSquared() < 1e-8f)
                    {
                        const int steps = 16; float radius = 3;
                        for (int i = 0; i < steps; i++)
                        {
                            var a = start + new Vector3(radius + radius * MathF.Cos(MathF.PI + i * MathF.Tau / steps), 0, radius * MathF.Sin(MathF.PI + i * MathF.Tau / steps));
                            var b = start + new Vector3(radius + radius * MathF.Cos(MathF.PI + (i + 1) * MathF.Tau / steps), 0, radius * MathF.Sin(MathF.PI + (i + 1) * MathF.Tau / steps));
                            Segment(a, b);
                            if (i == 11) Arrow(a, b, 1.4f);
                        }
                    }
                    else { Segment(start, end); Arrow(start, end, (end - start).Length() * .18f); }
                }
                return new() { Positions = new(positions), Indices = new(indices), Normals = new(positions.Select(_ => Vector3.UnitY)) };
                Vector3 Side(Vector3 direction)
                {
                    var side = Vector3.Cross(direction, forward);
                    return Vector3.Normalize(side.LengthSquared() > 1e-8 ? side : Vector3.Cross(direction, Math.Abs(direction.Y) < .9 ? Vector3.UnitY : Vector3.UnitX));
                }
                void Segment(Vector3 from, Vector3 to)
                {
                    var side = Side(Vector3.Normalize(to - from)) * AiScale(Vector3.Lerp(from, to, .5f), .8);
                    Quad(from - side, from + side, to + side, to - side);
                }
                void Arrow(Vector3 from, Vector3 to, float size)
                {
                    var direction = Vector3.Normalize(to - from); var tip = Vector3.Lerp(from, to, .72f);
                    size = Math.Min(size, AiScale(tip, 9));
                    var side = Side(direction); var center = tip - direction * size;
                    Triangle(tip, center + side * size * .5f, center - side * size * .5f);
                }
                void Triangle(Vector3 a, Vector3 b, Vector3 c) { int at = positions.Count; positions.AddRange([a, b, c]); indices.AddRange([at, at + 1, at + 2]); }
                void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { Triangle(a, b, c); Triangle(a, c, d); }
            }
        }
        BuildValveOverlay();
        aiSelectionMarker = AiMesh(AiOctahedron(), new(1, 1, 1, 1));
        RefreshAiSelection(); UpdateAiMarkers(); viewport.InvalidateRender();
    }
    private MeshGeometryModel3D AiMesh(MeshGeometry3D geometry, Color4 color)
    {
        var material = PreviewMaterials.Create(AiThroughGeometry); material.DiffuseColor = color; material.EnableUnLit = true;
        var mesh = new MeshGeometryModel3D { Geometry = geometry, Material = material, IsTransparent = true, IsHitTestVisible = false,
            CullMode = SharpDX.Direct3D11.CullMode.None, RenderOrder = 10, IsDepthClipEnabled = !AiThroughGeometry };
        aiDrawables.Add(mesh); aiOverlay!.Children.Add(mesh); return mesh;
    }
    private static MeshGeometry3D AiOctahedron() => new()
    {
        Positions = new([Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ]),
        Normals = new([Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ]),
        Indices = new([0, 2, 4, 4, 2, 1, 1, 2, 5, 5, 2, 0, 0, 4, 3, 4, 1, 3, 1, 5, 3, 5, 0, 3])
    };
    private float AiScale(Vector3 point, double pixels)
    {
        var pose = CaptureView(); var forward = pose.LookDirection; forward.Normalize();
        double depth = Vector3D.DotProduct(new Point3D(point.X, point.Y, point.Z) - pose.Position, forward);
        double width = pose.Projection == "orthographic" ? pose.OrthographicWidth ?? 1 : 2 * Math.Max(.001, depth) * Math.Tan(pose.FieldOfView * Math.PI / 360) * Aspect;
        return (float)Math.Max(.001, width / Math.Max(1, viewport.ActualWidth) * pixels);
    }
    private void UpdateAiMarkers()
    {
        if (!AiVisible || aiOverlay == null) return;
        var pose = CaptureView();
        if (aiMarkerPose == pose && aiMarkerWidth == viewport.ActualWidth && aiMarkerHeight == viewport.ActualHeight) return;
        aiMarkerPose = pose; aiMarkerWidth = viewport.ActualWidth; aiMarkerHeight = viewport.ActualHeight;
        foreach (var (mesh, nodes, pixels) in aiMarkers) mesh.Instances = nodes.Select(n => Matrix4x4.CreateScale(AiScale(n.Position, pixels)) * Matrix4x4.CreateTranslation(n.Position)).ToArray();
        foreach (var (mesh, build) in aiLinks) mesh.Geometry = build();
        RefreshAiSelection();
    }
    private void RefreshAiSelection()
    {
        if (aiSelectionMarker != null)
        {
            var selected = SelectedAiNode == null ? null : AiNetworks.Find(SelectedAiNode);
            aiSelectionMarker.Visibility = !InspectionSelectionEnabled || selected == null ? Visibility.Collapsed : Visibility.Visible;
            if (selected is { } target) aiSelectionMarker.Instances = [Matrix4x4.CreateScale(AiScale(target.Node.Position, 5.5)) * Matrix4x4.CreateTranslation(target.Node.Position)];
        }
        PublishAiLabel(); viewport.InvalidateRender();
    }
    private void PublishAiLabel()
    {
        string? id = hoveredAiNode ?? SelectedAiNode;
        var selected = !AiVisible || id == null ? null : AiNetworks.Find(id);
        string? label = selected is { } p ? $"{p.Network.Member} · node_{p.Node.Index:00} · {p.Network.Name[..Math.Min(70, p.Network.Name.Length)]}" : null;
        AiLabelChanged?.Invoke(label);
    }
    internal string? PickAiNode(Point point)
    {
        if (!AiVisible || IsFlyActive || IsPickupDragging || !IsInsidePickupViewport(point) || viewport.RenderContext == null) return null;
        var pose = CaptureView(); double nearest = 81, chosenDepth = double.PositiveInfinity; string? chosen = null;
        // Only drawn markers are pickable; the complete graph stays selectable through inspection commands.
        foreach (var node in aiRendered)
        {
            Point3D position = new(node.Position.X, node.Position.Y, node.Position.Z);
            var forward = pose.LookDirection; forward.Normalize(); double depth = Vector3D.DotProduct(position - pose.Position, forward);
            if (depth <= 0) continue;
            var screen = viewport.Project(position); double distance = (point - screen).LengthSquared;
            if (!double.IsFinite(distance) || distance > nearest || distance == nearest && depth >= chosenDepth) continue;
            if (!AiThroughGeometry)
            {
                if (viewport.Camera is ProjectionCamera camera && (depth < camera.NearPlaneDistance || depth > camera.FarPlaneDistance)) continue;
                // Check the marker's own pixel, not a nearby click ray that may hit another surface.
                var hit = viewport.FindHits(screen)?.FirstOrDefault(h => h.ModelHit is MeshGeometryModel3D mesh && !mesh.IsTransparent && placements.ContainsKey(mesh) && !placements[mesh].Any(p => IsHorizon(p.NodeIndex)));
                if (hit != null && Vector3D.DotProduct(new Point3D(hit.PointHit.X, hit.PointHit.Y, hit.PointHit.Z) - pose.Position, forward) < depth - AiScale(node.Position, 4)) continue;
            }
            nearest = distance; chosenDepth = depth; chosen = node.Id;
        }
        return chosen;
    }
    private bool HandleAiPointerDown(Point point, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right) || PickAiNode(point) is not { } id) return false;
        if (!SelectAiNode(id)) return false;
        viewport.Focus();
        if (e.ChangedButton == MouseButton.Right)
        {
            var menu = new System.Windows.Controls.ContextMenu();
            var item = new System.Windows.Controls.MenuItem { Header = "Properties…", InputGestureText = "Alt+Enter" };
            item.Click += (_, _) => AiPropertiesRequested?.Invoke(); menu.Items.Add(item); menu.IsOpen = true;
        }
        return true;
    }
    private void HandleAiPointerMove(Point point)
    {
        string? next = Mouse.LeftButton == MouseButtonState.Released && Mouse.MiddleButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released ? PickAiNode(point) : null;
        if (next == hoveredAiNode) return; hoveredAiNode = next; PublishAiLabel();
    }
    private void ClearAiDrawables()
    {
        if (aiOverlay != null) { viewport.Items.Remove(aiOverlay); aiOverlay.Children.Clear(); aiOverlay.Dispose(); aiOverlay = null; }
        foreach (var mesh in aiDrawables) mesh.Dispose(); aiDrawables.Clear(); aiMarkers.Clear(); aiLinks.Clear(); aiSelectionMarker = null; aiMarkerPose = null;
    }
    private void ClearAi()
    {
        ClearAiDrawables(); AiNetworks = AiNetworkSnapshot.Empty; SelectedAiNode = hoveredAiNode = null; AiNetworkFilter = null; PublishAiLabel();
    }
}
