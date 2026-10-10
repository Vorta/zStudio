using System.Numerics;
using System.Windows;
using System.Windows.Input;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;

namespace Recoil.Zbd.Rendering;

public sealed record ZonePaintTarget(int Node, int? Polygon);

public sealed partial class SceneViewport
{
    public const int MaximumZonePaintTargets = 4096;
    private bool zonePaintActive;
    private HashSet<ZonePaintTarget>? zoneGesture;
    private LineGeometryModel3D? zoneOutline;
    public bool ZoneOutlineLimited { get; private set; }
    public bool ZonePaintFaces { get; set; } = true;
    public bool ZonePaintActive
    {
        get => zonePaintActive;
        set
        {
            if (value && (IsFlyActive || IsPickupDragging || TerrainBrushActive))
                throw new InvalidOperationException("Finish the active navigation or editing gesture before painting zones.");
            zonePaintActive = value;
            if (!value) CancelZoneGesture();
            if (!IsFlyActive) Cursor = value ? Cursors.Pen : null;
        }
    }
    public bool IsZonePainting => zoneGesture != null;
    public event Action<IReadOnlyList<ZonePaintTarget>>? ZoneStrokeCompleted;

    private bool HandleZoneDown(Point point, MouseButtonEventArgs e)
    {
        if (!zonePaintActive || e.ChangedButton != MouseButton.Left || IsFlyActive || IsPickupDragging) return false;
        zoneGesture = [];
        AddZoneHit(point); viewport.CaptureMouse(); viewport.Focus(); return true;
    }
    private bool HandleZoneMove(Point point)
    {
        if (zoneGesture == null) return false;
        AddZoneHit(point); return true;
    }
    private void AddZoneHit(Point point)
    {
        if (zoneGesture == null || zoneGesture.Count >= MaximumZonePaintTargets) return;
        var hit = ProbeInspection(point);
        if (hit is not { Active: true, Node: >= 0, RuntimeInstance: null } || ZonePaintFaces && hit.Polygon == null) return;
        ZonePaintTarget target = new(hit.Node, ZonePaintFaces ? hit.Polygon : null);
        // A gesture retains exact identities, while its hover outline is only the current surface.
        // Rebuilding all accumulated outlines at every sample would make long strokes quadratic.
        if (zoneGesture.Add(target)) ShowZoneTargets([target]);
    }
    private bool HandleZoneUp(MouseButtonEventArgs e)
    {
        if (zoneGesture == null || e.ChangedButton != MouseButton.Left) return false;
        var completed = zoneGesture.ToArray(); zoneGesture = null;
        viewport.ReleaseMouseCapture();
        ZoneStrokeCompleted?.Invoke(completed); return true;
    }
    public bool CancelZoneGesture()
    {
        if (zoneGesture == null) return false;
        zoneGesture = null;
        if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture();
        ShowZoneTargets([]); return true;
    }

    /// <summary>
    /// Outline only the exact selected source faces/instances; never contributes to picks or scene bounds. A target's polygon
    /// is a polygon of the node's model, or, with <paramref name="sourcePolygons"/> (each built polygon's source polygon for a
    /// node), a source polygon: every built polygon that comes from it is outlined.
    /// </summary>
    public void ShowZoneTargets(IEnumerable<ZonePaintTarget> targets, Func<int, IReadOnlyList<int>?>? sourcePolygons = null)
    {
        ZoneOutlineLimited = false;
        if (zoneOutline != null) { viewport.Items.Remove(zoneOutline); zoneOutline.Dispose(); zoneOutline = null; }
        if (PreviewScene is not { } data) return;
        var selected = targets.Take(MaximumZonePaintTargets + 1).ToArray();
        if (selected.Length > MaximumZonePaintTargets) throw new InvalidOperationException("A zone draft supports at most 4,096 targets.");
        HashSet<ZonePaintTarget> wanted = new(selected);
        if (wanted.Count == 0) return;
        Dictionary<int, HashSet<int>?> byNode = []; // a null set selects the whole node
        foreach (var target in wanted)
        {
            if (target.Polygon is not { } polygon) byNode[target.Node] = null;
            else if (!byNode.TryGetValue(target.Node, out var polygons)) byNode[target.Node] = [polygon];
            else polygons?.Add(polygon);
        }
        HashSet<(int Node, int Model, Matrix4x4 Transform)> seen = [];
        List<Vector3> points = []; IntCollection indices = [];
        foreach (var rows in visiblePlacements.Values)
            foreach (var row in rows)
            {
                if (!byNode.TryGetValue(row.NodeIndex, out var selectedPolygons) || !seen.Add((row.NodeIndex, row.ModelIndex, row.Transform)) ||
                    row.ModelIndex < 0 || row.ModelIndex >= data.Models.Count) continue;
                var model = data.Models[row.ModelIndex];
                var sources = selectedPolygons == null ? null : sourcePolygons?.Invoke(row.NodeIndex);
                if (selectedPolygons != null && sourcePolygons != null && sources == null) continue;
                for (int polygon = 0; polygon < model.Polygons.Length; polygon++)
                {
                    if (selectedPolygons != null && !selectedPolygons.Contains(sources == null ? polygon : polygon < sources.Count ? sources[polygon] : -1)) continue;
                    var face = model.Polygons[polygon];
                    if (face.Vertices.Length > 200_000 - points.Count) { ZoneOutlineLimited = true; goto Done; }
                    int offset = points.Count;
                    foreach (int vertex in face.Vertices)
                        points.Add(vertex >= 0 && vertex < model.Vertices.Length ? Vector3.Transform(model.Vertices[vertex], row.Transform) : Vector3.Zero);
                    for (int i = 0; i < face.Vertices.Length; i++) { indices.Add(offset + i); indices.Add(offset + (i + 1) % face.Vertices.Length); }
                }
            }
        Done:
        if (points.Count == 0) return;
        zoneOutline = new()
        {
            Geometry = new LineGeometry3D { Positions = new Vector3Collection(points), Indices = indices },
            Color = System.Windows.Media.Colors.OrangeRed, Thickness = 2, IsHitTestVisible = false, DepthBias = -100,
        };
        viewport.Items.Add(zoneOutline);
    }
    private void ClearZonePainting()
    {
        zonePaintActive = false; CancelZoneGesture();
        ZoneOutlineLimited = false;
        if (zoneOutline != null) { viewport.Items.Remove(zoneOutline); zoneOutline.Dispose(); zoneOutline = null; }
    }
}
