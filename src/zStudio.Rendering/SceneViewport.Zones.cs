using System.Numerics;
using System.Windows;
using System.Windows.Input;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using zStudio.Core;
using zStudio.Core.Sources;
using Colors = System.Windows.Media.Colors;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;

namespace zStudio.Rendering;

/// <summary>What a stroke may do to a node's polygons: paint them, look through them, or stop at them unchanged.</summary>
public enum PolygonZoneReach { Paintable, PassThrough, Blocked }
public enum PolygonZoneOutcome { Changed, Unchanged, Full }

/// <summary>
/// The shown source world's identities the polygon zone tool needs, supplied by its owner: which nodes it paints, each node's
/// built-to-source polygon mapping, and the assignment site whose polygons change together (with the nodes sharing it).
/// </summary>
public sealed class PolygonZoneBrush
{
    public required Func<int, PolygonZoneReach> Reach { get; init; }
    public required Func<int, IReadOnlyList<int>?> SourcePolygons { get; init; }
    public required Func<int, string?> Site { get; init; }
    public required Func<int, IReadOnlyList<int>> SiteNodes { get; init; }
}

/// <summary>A built polygon of a scene node, as a pick finds it.</summary>
public sealed record ZonePaintTarget(int Node, int Polygon);

/// <summary>The paintable polygon under the pointer: its source polygon, its zones and what the tool would do to it.</summary>
public sealed record ZonePaintHover(int Node, int Polygon, int Source, IReadOnlyList<byte> Zones, PolygonZoneOutcome Outcome);

/// <summary>A finished stroke: one target per source polygon it reached, and what it does to them.</summary>
public sealed record ZonePaintStroke(long Id, byte Zone, bool Erase, IReadOnlyList<ZonePaintTarget> Targets, int Changed, int Unchanged, int Full,
    IReadOnlyList<ZonePaintTarget> FullTargets, bool CrossedBlocked, bool Limited);

public sealed partial class SceneViewport
{
    public const int MaximumZonePaintTargets = 4096;
    private const float ZoneFade = .62f, ZoneStrong = .6f, ZoneWeak = .15f, ZonePreview = .65f;
    private sealed record ZoneTouch(int Node, int Polygon, int Source, string Site, IReadOnlyList<byte> Before, IReadOnlyList<byte>? After)
    {
        internal PolygonZoneOutcome Outcome => After == null ? PolygonZoneOutcome.Full : After.SequenceEqual(Before) ? PolygonZoneOutcome.Unchanged : PolygonZoneOutcome.Changed;
    }
    private PolygonZoneBrush? zoneBrush;
    private bool zonePaintActive;
    private Dictionary<(string Site, int Source), ZoneTouch>? zoneStroke;
    private bool zoneStrokeErase, zoneStrokeBlocked, zoneStrokeLimited;
    private int zoneStrokeChanged, zoneStrokeFull;
    private readonly HashSet<string> zoneStrokeSites = new(StringComparer.Ordinal);
    /// <summary>The placements one stroke may reach (SourceZoneEdits.MaximumStrokeOccurrences).</summary>
    public const int MaximumZonePaintPlacements = 1024;
    private Point? zoneStrokePoint;
    private long zoneStrokeSerial;
    private readonly Dictionary<(string Site, int Source), (long Stroke, ZoneTouch Touch)> zonePending = [];
    private readonly Dictionary<int, ILookup<int, int>> zoneBuiltPolygons = [];
    private Dictionary<int, List<(int Model, Matrix4x4 Transform)>>? zoneRows;
    private readonly Dictionary<DiffuseMaterial, DiffuseMaterial> zoneGhostMaterials = [];
    private readonly Dictionary<MeshGeometryModel3D, (Material Material, bool Transparent)> zoneGhosts = [];
    private MeshGeometryModel3D? zonePreviewMesh;
    private readonly Dictionary<string, LineGeometryModel3D> zoneLines = [];
    private IReadOnlyList<ZonePaintTarget> zoneShownSkipped = [];
    private bool zoneOverlayDirty;
    private Point? zoneHoverPoint;
    private DateTime zoneHoverTick;

    /// <summary>The zone the tool paints or erases.</summary>
    public byte PaintZone { get => paintZone; set { if (paintZone == value) return; paintZone = value; RefreshZoneTool(); } }
    private byte paintZone;
    /// <summary>Erase instead of paint (Ctrl held inverts it for a stroke).</summary>
    public bool PaintErase { get => paintErase; set { if (paintErase == value) return; paintErase = value; RefreshZoneTool(); } }
    private bool paintErase;
    public PolygonZoneBrush? ZoneBrush => zoneBrush;
    /// <summary>The polygon a click would paint, or null (none under the pointer, or a stroke in progress).</summary>
    public ZonePaintHover? ZoneHover { get; private set; }
    /// <summary>Whether the pointer is over geometry that stops the tool without changing (editable terrain).</summary>
    public bool ZoneHoverBlocked { get; private set; }
    /// <summary>Whether the polygon zone tool is engaged: left-button drags paint instead of selecting.</summary>
    public bool ZonePaintActive
    {
        get => zonePaintActive;
        set
        {
            if (value && zoneBrush == null) throw new InvalidOperationException("Engage the polygon zone tool with a brush.");
            if (value && (IsFlyActive || IsPickupDragging || TerrainBrushActive || AiPathEditingNetwork != null || HasParachutePreview || IsParachutePointerMoving || IsTerrainStroking))
                throw new InvalidOperationException("Finish the active navigation or editing gesture before painting zones.");
            if (zonePaintActive == value) return;
            zonePaintActive = value;
            if (!value) { CancelZoneGesture(); ClearZoneToolPresentation(); }
            else { ShowZoneGhosts(); ApplyZoneEmphasis(); }
            RefreshZoneCursor();
            ZonePaintActiveChanged?.Invoke();
        }
    }
    public bool IsZonePainting => zoneStroke != null;
    /// <summary>Whether the stroke in progress erases (its mode is fixed when it starts, Ctrl included).</summary>
    public bool ZoneStrokeErase => zoneStroke != null && zoneStrokeErase;
    private bool zonePaintWaiting;
    /// <summary>While the owner's queue of released strokes is full, clicks start no new stroke and select nothing.</summary>
    public bool ZonePaintWaiting { get => zonePaintWaiting; set { zonePaintWaiting = value; RefreshZoneCursor(); } }
    public event Action? ZonePaintActiveChanged;
    /// <summary>A stroke's running counts changed (changed, full), or it ended.</summary>
    public event Action<int, int, bool>? ZoneStrokeProgress;
    public event Action<ZonePaintStroke>? ZoneStrokeCompleted;
    public event Action? ZoneHoverChanged;
    /// <summary>Shift+click: the owner offers the polygon's zones to choose from.</summary>
    public event Action<ZonePaintHover?>? ZonePickRequested;

    /// <summary>Engages the tool with the shown world's identities (or disengages it with null).</summary>
    public void SetZoneBrush(PolygonZoneBrush? brush)
    {
        if (brush == null) { ZonePaintActive = false; zoneBrush = null; ResetZoneIdentities(); return; }
        if (zonePaintActive) ClearZoneToolPresentation();
        var previous = zoneBrush;
        zoneBrush = brush; ResetZoneIdentities();
        if (zonePaintActive) { ShowZoneGhosts(); ApplyZoneEmphasis(); return; }
        try { ZonePaintActive = true; }
        catch { zoneBrush = previous; throw; }
    }

    private bool ZoneErase => paintErase ^ (Keyboard.Modifiers & ModifierKeys.Control) != 0;
    private void ResetZoneIdentities() { zoneBuiltPolygons.Clear(); zoneRows = null; zoneOverlayDirty = true; }

    /// <summary>After the zone, mode or a modifier key changed: the cursor and the hover's outcome follow.</summary>
    public void RefreshZoneTool()
    {
        if (!zonePaintActive) return;
        ApplyZoneEmphasis(); RefreshZoneCursor();
        if (zoneStroke == null && zoneHoverPoint is { } p) UpdateZoneHover(p, force: true);
    }
    private void RefreshZoneCursor()
    {
        if (IsFlyActive) return;
        if (!zonePaintActive) { if (Cursor == PolygonZoneCursors.Paint || Cursor == PolygonZoneCursors.Erase || Cursor == PolygonZoneCursors.Pick || Cursor == Cursors.No || Cursor == Cursors.Wait && !terrainBrushActive) Cursor = null; return; }
        bool erase = zoneStroke != null ? zoneStrokeErase : ZoneErase;
        Cursor = zonePaintWaiting && zoneStroke == null ? Cursors.Wait : zoneStroke == null && (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? PolygonZoneCursors.Pick
            : zoneStroke == null && ZoneHoverBlocked ? Cursors.No : erase ? PolygonZoneCursors.Erase : PolygonZoneCursors.Paint;
    }

    private bool HandleZoneDown(Point point, MouseButtonEventArgs e)
    {
        if (!zonePaintActive || e.ChangedButton != MouseButton.Left || IsFlyActive || IsPickupDragging) return false;
        viewport.Focus();
        if (zonePaintWaiting) return true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) { ZonePickRequested?.Invoke(ProbeZone(point, out _)); return true; }
        zoneStroke = []; zoneStrokeErase = ZoneErase; zoneStrokeBlocked = zoneStrokeLimited = false; zoneStrokeChanged = zoneStrokeFull = 0; zoneStrokeSites.Clear(); zonePresentationSuspended = false;
        zoneStrokePoint = point; ZoneHover = null; ZoneHoverChanged?.Invoke();
        TouchZone(point); viewport.CaptureMouse(); RefreshZoneCursor(); return true;
    }
    private bool HandleZoneMove(Point point)
    {
        zoneHoverPoint = zonePaintActive ? point : null;
        if (zoneStroke == null) return false;
        if (zonePresentationSuspended) return true;
        // Sample the path the pointer travelled, so a fast drag misses no polygon it crossed.
        var from = zoneStrokePoint ?? point;
        double distance = (point - from).Length;
        int steps = Math.Clamp((int)Math.Ceiling(distance / 4), 1, 16);
        for (int i = 1; i <= steps; i++) TouchZone(from + (point - from) * ((double)i / steps));
        zoneStrokePoint = point; return true;
    }
    private bool HandleZoneUp(MouseButtonEventArgs e)
    {
        if (zoneStroke == null || e.ChangedButton != MouseButton.Left) return false;
        var touches = zoneStroke.Values.ToArray();
        long id = ++zoneStrokeSerial;
        var stroke = new ZonePaintStroke(id, paintZone, zoneStrokeErase, touches.Select(t => new ZonePaintTarget(t.Node, t.Polygon)).ToArray(),
            touches.Count(t => t.Outcome == PolygonZoneOutcome.Changed), touches.Count(t => t.Outcome == PolygonZoneOutcome.Unchanged),
            touches.Count(t => t.Outcome == PolygonZoneOutcome.Full), touches.Where(t => t.Outcome == PolygonZoneOutcome.Full).Select(t => new ZonePaintTarget(t.Node, t.Polygon)).ToArray(),
            zoneStrokeBlocked, zoneStrokeLimited);
        // The stroke's result stays shown until its owner publishes (or drops) the edit.
        foreach (var touch in touches.Where(t => t.Outcome == PolygonZoneOutcome.Changed)) zonePending[(touch.Site, touch.Source)] = (id, touch);
        zoneStroke = null; zoneStrokePoint = null; zonePresentationSuspended = false; zoneOverlayDirty = true;
        viewport.ReleaseMouseCapture(); RefreshZoneCursor();
        ZoneStrokeProgress?.Invoke(stroke.Changed, stroke.Full, false);
        ZoneStrokeCompleted?.Invoke(stroke); return true;
    }
    /// <summary>Drops a stroke in progress (nothing changes); returns whether there was one.</summary>
    public bool CancelZoneGesture()
    {
        if (zoneStroke == null) return false;
        zoneStroke = null; zoneStrokePoint = null; zonePresentationSuspended = false; zoneOverlayDirty = true;
        if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture();
        RefreshZoneCursor(); ZoneStrokeProgress?.Invoke(0, 0, false);
        return true;
    }
    /// <summary>The edit of stroke <paramref name="id"/> was published (or dropped): its preview gives way to the world.</summary>
    public void ResolveZoneStroke(long id)
    {
        foreach (var key in zonePending.Where(p => p.Value.Stroke == id).Select(p => p.Key).ToArray()) zonePending.Remove(key);
        zoneOverlayDirty = true; viewport.InvalidateRender();
    }
    /// <summary>Outlines the polygons a stroke skipped (until the next stroke or an empty list).</summary>
    public void ShowZoneSkipped(IReadOnlyList<ZonePaintTarget> targets) { zoneShownSkipped = targets; zoneOverlayDirty = true; viewport.InvalidateRender(); }

    private void TouchZone(Point point)
    {
        if (zoneStroke == null || zoneBrush == null) return;
        var hover = ProbeZone(point, out bool blocked, zoneStrokeErase);
        if (blocked) zoneStrokeBlocked = true;
        if (hover == null || zoneBrush.Site(hover.Node) is not { } site || zoneStroke.ContainsKey((site, hover.Source))) return;
        // A polygon past the limits (the planner's: polygons, and placements of models) is left out, and the stroke says so.
        if (zoneStroke.Count >= MaximumZonePaintTargets || !zoneStrokeSites.Contains(site) && zoneStrokeSites.Count >= MaximumZonePaintPlacements) { zoneStrokeLimited = true; return; }
        zoneStrokeSites.Add(site);
        var after = SourceZoneEdits.PaintedPolygonZones(hover.Zones, paintZone, zoneStrokeErase);
        ZoneTouch touch = new(hover.Node, hover.Polygon, hover.Source, site, hover.Zones, after);
        zoneStroke.Add((site, hover.Source), touch);
        if (touch.Outcome == PolygonZoneOutcome.Changed) zoneStrokeChanged++;
        else if (touch.Outcome == PolygonZoneOutcome.Full) zoneStrokeFull++;
        zoneOverlayDirty = true; viewport.InvalidateRender();
        ZoneStrokeProgress?.Invoke(zoneStrokeChanged, zoneStrokeFull, true);
    }

    /// <summary>
    /// The paintable polygon under <paramref name="point"/>, looking through what the tool cannot paint; null over nothing or
    /// over geometry that stops it (<paramref name="blocked"/>).
    /// </summary>
    private ZonePaintHover? ProbeZone(Point point, out bool blocked, bool? erase = null)
    {
        blocked = false;
        if (zoneBrush is not { } brush || !IsNavigationPointInside(point) || IsFlyActive) return null;
        bool stopped = false;
        if (!TrySceneSurface(point, out var hit, candidate =>
        {
            if (candidate.ModelHit is not MeshGeometryModel3D mesh || !visiblePlacements.TryGetValue(mesh, out var rows)) return false;
            int at = candidate.Tag is int i ? i : 0;
            if (at < 0 || at >= rows.Length) return false;
            var reach = brush.Reach(rows[at].NodeIndex);
            if (reach == PolygonZoneReach.Blocked) stopped = true;
            return reach != PolygonZoneReach.PassThrough;
        }) || stopped) { blocked = stopped; return null; }
        var inspected = InspectHit(hit!);
        if (inspected is not { Node: >= 0, Polygon: { } polygon, RuntimeInstance: null } || brush.SourcePolygons(inspected.Node) is not { } mapping
            || polygon < 0 || polygon >= mapping.Count || mapping[polygon] < 0 || brush.Site(inspected.Node) is not { } site) return null;
        int source = mapping[polygon];
        var zones = CurrentZones(site, source, inspected.Model, polygon);
        var after = SourceZoneEdits.PaintedPolygonZones(zones, paintZone, erase ?? ZoneErase);
        var outcome = after == null ? PolygonZoneOutcome.Full : after.SequenceEqual(zones) ? PolygonZoneOutcome.Unchanged : PolygonZoneOutcome.Changed;
        return new(inspected.Node, polygon, source, zones, outcome);
    }
    /// <summary>A polygon's zones: a pending stroke's result, else the shown model's.</summary>
    private IReadOnlyList<byte> CurrentZones(string site, int source, int model, int polygon)
    {
        if (zonePending.TryGetValue((site, source), out var pending)) return pending.Touch.After!;
        if (PreviewScene is not { } scene || model < 0 || model >= scene.Models.Count || polygon < 0 || polygon >= scene.Models[model].Polygons.Length) return [];
        return scene.Models[model].Polygons[polygon].Metadata["zone_set"] is System.Text.Json.Nodes.JsonArray ids
            ? ids.Take(3).Select(id => unchecked((byte)(int)JsonData.Scalar(id))).ToArray() : [];
    }

    private void UpdateZoneHover(Point point, bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - zoneHoverTick).TotalMilliseconds < 40) return;
        zoneHoverTick = now;
        var hover = ProbeZone(point, out bool blocked);
        if (!force && blocked == ZoneHoverBlocked && SameHover(hover, ZoneHover)) return;
        ZoneHover = hover; ZoneHoverBlocked = blocked; zoneOverlayDirty = true;
        RefreshZoneCursor(); ZoneHoverChanged?.Invoke(); viewport.InvalidateRender();
    }

    private static bool SameHover(ZonePaintHover? a, ZonePaintHover? b) => a == null || b == null ? a == b
        : a.Node == b.Node && a.Polygon == b.Polygon && a.Outcome == b.Outcome && a.Zones.SequenceEqual(b.Zones);
    private void LeaveZoneHover()
    {
        zoneHoverPoint = null;
        if (ZoneHover == null && !ZoneHoverBlocked) return;
        ZoneHover = null; ZoneHoverBlocked = false; zoneOverlayDirty = true; RefreshZoneCursor(); ZoneHoverChanged?.Invoke();
    }
    /// <summary>Per frame while engaged (bounded): the hover under a still pointer, and the preview and outlines when they changed.</summary>
    private void RefreshZoneFrame()
    {
        if (!zonePaintActive) return;
        if (zoneStroke == null && zoneHoverPoint is { } point && Mouse.LeftButton == MouseButtonState.Released) UpdateZoneHover(point);
        if (zoneOverlayDirty) { zoneOverlayDirty = false; ShowZoneOverlay(); }
    }

    private IEnumerable<int> BuiltPolygons(int node, int source)
    {
        if (zoneBrush == null) return [];
        if (!zoneBuiltPolygons.TryGetValue(node, out var lookup))
        {
            var mapping = zoneBrush.SourcePolygons(node) ?? [];
            zoneBuiltPolygons[node] = lookup = Enumerable.Range(0, mapping.Count).ToLookup(i => mapping[i]);
        }
        return lookup[source];
    }
    private IReadOnlyList<(int Model, Matrix4x4 Transform)> ZoneRows(int node)
    {
        if (zoneRows == null)
        {
            zoneRows = [];
            HashSet<(int, int, Matrix4x4)> seen = [];
            foreach (var rows in visiblePlacements.Values)
                foreach (var row in rows)
                    if (seen.Add((row.NodeIndex, row.ModelIndex, row.Transform)))
                    {
                        if (!zoneRows.TryGetValue(row.NodeIndex, out var list)) zoneRows[row.NodeIndex] = list = [];
                        list.Add((row.ModelIndex, row.Transform));
                    }
        }
        return zoneRows.TryGetValue(node, out var found) ? found : [];
    }

    /// <summary>The stroke and pending results as zone colours over the scene, and the outlines of the hover, stroke and skipped polygons.</summary>
    private void ShowZoneOverlay()
    {
        if (PreviewScene is not { } scene || zoneBrush is not { } brush) { RemoveZoneOverlay(); return; }
        Dictionary<(string, int), ZoneTouch> shown = new(zonePending.ToDictionary(p => p.Key, p => p.Value.Touch));
        // A touch that changes nothing leaves an earlier stroke's pending result as it is.
        foreach (var (key, touch) in zoneStroke ?? []) if (touch.Outcome == PolygonZoneOutcome.Changed) shown[key] = touch;
        Vector3Collection positions = []; IntCollection indices = []; Color4Collection colors = [];
        foreach (var touch in shown.Values)
        {
            var color = ZoneSetColor(touch.After!);
            foreach (int node in brush.SiteNodes(touch.Node).Append(touch.Node).Distinct())
                foreach (var (model, transform) in ZoneRows(node))
                    foreach (int polygon in BuiltPolygons(node, touch.Source))
                        if (positions.Count < 400_000) AddZoneTriangles(model, polygon, transform, color, positions, indices, colors);
        }
        if (positions.Count == 0) { if (zonePreviewMesh != null) { viewport.Items.Remove(zonePreviewMesh); zonePreviewMesh.Dispose(); zonePreviewMesh = null; } }
        else
        {
            if (zonePreviewMesh == null)
            {
                var material = PreviewMaterials.Create(vertexTint: true); material.EnableUnLit = true; material.VertexColorBlendingFactor = 1;
                PreviewMaterials.SetPolygonZones(material, true); PreviewMaterials.SetZoneEmphasis(material, PreviewMaterials.ZoneEmphasis.Preview, strong: ZonePreview);
                zonePreviewMesh = new() { Material = material, CullMode = SharpDX.Direct3D11.CullMode.None, IsTransparent = true, IsHitTestVisible = false, IsThrowingShadow = false, DepthBias = -200 };
                viewport.Items.Add(zonePreviewMesh);
            }
            zonePreviewMesh.Geometry = new MeshGeometry3D { Positions = positions, Indices = indices, Colors = colors, Normals = new Vector3Collection(Enumerable.Repeat(Vector3.UnitY, positions.Count)) };
        }
        List<(int Node, int Source, int Model)> white = [], grey = [], yellow = [], dim = [];
        if (ZoneHover is { } hover && zoneStroke == null && brush.Site(hover.Node) is { } hoverSite)
        {
            (hover.Outcome switch { PolygonZoneOutcome.Changed => white, PolygonZoneOutcome.Unchanged => grey, _ => yellow }).Add((hover.Node, hover.Source, -1));
            foreach (int other in brush.SiteNodes(hover.Node)) if (other != hover.Node) dim.Add((other, hover.Source, -1));
        }
        foreach (var touch in zoneStroke?.Values ?? Enumerable.Empty<ZoneTouch>())
            (touch.Outcome switch { PolygonZoneOutcome.Changed => white, PolygonZoneOutcome.Unchanged => grey, _ => yellow }).Add((touch.Node, touch.Source, -1));
        foreach (var skipped in zoneShownSkipped)
            if (brush.SourcePolygons(skipped.Node) is { } mapping && skipped.Polygon >= 0 && skipped.Polygon < mapping.Count && mapping[skipped.Polygon] >= 0) yellow.Add((skipped.Node, mapping[skipped.Polygon], -1));
        ShowZoneLines("changed", white, Colors.White, 2);
        ShowZoneLines("unchanged", grey, System.Windows.Media.Color.FromArgb(140, 255, 255, 255), 1.5);
        ShowZoneLines("full", yellow, System.Windows.Media.Color.FromRgb(0xFC, 0xE1, 0x00), 2);
        ShowZoneLines("others", dim, System.Windows.Media.Color.FromArgb(170, 255, 255, 255), 1);
        viewport.InvalidateRender();
    }
    /// <summary>A zone set as the polygon zone pass reads it; no zones draws the preview's "cleared" fill.</summary>
    private static Color4 ZoneSetColor(IReadOnlyList<byte> zones) => zones.Count == 0 ? new(0, 0, 0, 0)
        : PolygonZoneColor(new System.Text.Json.Nodes.JsonArray(zones.Select(z => (System.Text.Json.Nodes.JsonNode?)(int)z).ToArray()));
    private readonly Dictionary<MeshPart, ILookup<int, int>> zonePartTriangles = new(ReferenceEqualityComparer.Instance);
    /// <summary>The triangles the scene built for a polygon (its own triangulation, ear-clipped or stripped), placed by the row.</summary>
    private void AddZoneTriangles(int model, int polygon, Matrix4x4 transform, Color4 color, Vector3Collection positions, IntCollection indices, Color4Collection colors)
    {
        if (!polygonGeometries.TryGetValue(model, out var geometries)) return;
        foreach (var geometry in geometries)
        {
            var part = polygonSources[geometry].Part;
            if (part.TrianglePolygons.Length * 3 != part.Indices.Length) continue;
            if (!zonePartTriangles.TryGetValue(part, out var triangles))
                zonePartTriangles[part] = triangles = Enumerable.Range(0, part.TrianglePolygons.Length).ToLookup(t => part.TrianglePolygons[t]);
            foreach (int triangle in triangles[polygon])
                for (int corner = 0; corner < 3; corner++)
                {
                    int vertex = part.Indices[triangle * 3 + corner];
                    if (vertex < 0 || vertex >= part.Positions.Length) continue;
                    indices.Add(positions.Count); positions.Add(Vector3.Transform(part.Positions[vertex], transform)); colors.Add(color);
                }
        }
    }
    private void ShowZoneLines(string name, List<(int Node, int Source, int Model)> polygons, System.Windows.Media.Color color, double thickness)
    {
        if (zoneLines.Remove(name, out var old)) { viewport.Items.Remove(old); old.Dispose(); }
        if (polygons.Count == 0 || PreviewScene is not { } scene) return;
        List<Vector3> points = []; IntCollection indices = [];
        foreach (var (node, source, _) in polygons)
            foreach (var (model, transform) in ZoneRows(node))
                foreach (int polygon in BuiltPolygons(node, source))
                {
                    if (model < 0 || model >= scene.Models.Count || polygon >= scene.Models[model].Polygons.Length || points.Count > 200_000) continue;
                    var built = scene.Models[model]; var face = built.Polygons[polygon];
                    int offset = points.Count, n = face.Vertices.Length;
                    bool strip = (face.Flags & 1024) != 0;
                    int[] loop = strip ? [.. Enumerable.Range(0, n).Where(i => i % 2 == 0), .. Enumerable.Range(0, n).Where(i => i % 2 == 1).Reverse()] : [.. Enumerable.Range(0, n)];
                    foreach (int i in loop) points.Add(face.Vertices[i] >= 0 && face.Vertices[i] < built.Vertices.Length ? Vector3.Transform(built.Vertices[face.Vertices[i]], transform) : Vector3.Zero);
                    for (int i = 0; i < loop.Length; i++) { indices.Add(offset + i); indices.Add(offset + (i + 1) % loop.Length); }
                }
        if (points.Count == 0) return;
        LineGeometryModel3D line = new() { Geometry = new LineGeometry3D { Positions = new Vector3Collection(points), Indices = indices }, Color = color, Thickness = thickness, IsHitTestVisible = false, DepthBias = -300 };
        viewport.Items.Add(line); zoneLines[name] = line;
    }
    private void RemoveZoneOverlay()
    {
        if (zonePreviewMesh != null) { viewport.Items.Remove(zonePreviewMesh); zonePreviewMesh.Dispose(); zonePreviewMesh = null; }
        foreach (var line in zoneLines.Values) { viewport.Items.Remove(line); line.Dispose(); }
        zoneLines.Clear();
    }

    /// <summary>Fades the meshes whose every shown placement the tool cannot paint (they let the pointer through).</summary>
    private void ShowZoneGhosts()
    {
        RestoreZoneGhosts();
        if (zoneBrush is not { } brush) return;
        foreach (var mesh in meshes)
        {
            if (!visiblePlacements.TryGetValue(mesh, out var rows) || rows.Length == 0 || mesh.Material is not DiffuseMaterial material
                || surfaceAppearances.TryGetValue(mesh, out var surface) && surface.Horizon
                || !rows.All(row => brush.Reach(row.NodeIndex) == PolygonZoneReach.PassThrough)) continue;
            if (!zoneGhostMaterials.TryGetValue(material, out var ghost))
            {
                zoneGhostMaterials[material] = ghost = PreviewMaterials.Copy(material);
                PreviewMaterials.SetFade(ghost, ZoneFade);
            }
            zoneGhosts[mesh] = (material, mesh.IsTransparent);
            mesh.Material = ghost; mesh.IsTransparent = true;
        }
        viewport.InvalidateRender();
    }
    private void RestoreZoneGhosts()
    {
        foreach (var (mesh, (material, transparent)) in zoneGhosts) { mesh.Material = material; mesh.IsTransparent = transparent; }
        zoneGhosts.Clear(); zoneGhostMaterials.Clear();
    }

    /// <summary>While engaged, the chosen zone is drawn strongly and the others faintly, whatever overlay is chosen.</summary>
    private void ApplyZoneEmphasis()
    {
        bool overlay = HighlightMode == WorldHighlightMode.PolygonZones;
        ShowPolygonZoneColors(overlay || zonePaintActive);
        foreach (var surface in surfaceAppearances.Values)
        {
            if (surface.Horizon) continue;
            PreviewMaterials.SetPolygonZones(surface.Normal, overlay || zonePaintActive);
            if (zonePaintActive) PreviewMaterials.SetZoneEmphasis(surface.Normal, PreviewMaterials.ZoneEmphasis.Emphasis, paintZone, ZoneStrong, ZoneWeak);
            else PreviewMaterials.SetZoneEmphasis(surface.Normal, PreviewMaterials.ZoneEmphasis.None, 0, 0, 0);
        }
        SyncZoneGhosts();
        viewport.InvalidateRender();
    }

    private void ClearZoneToolPresentation()
    {
        RestoreZoneGhosts(); RemoveZoneOverlay();
        ZoneHover = null; ZoneHoverBlocked = false; zoneShownSkipped = [];
        ApplyZoneEmphasis();
        ZoneHoverChanged?.Invoke();
    }

    /// <summary>Hiding, isolating or showing objects while engaged: the outlines, preview and fading follow what the view shows.</summary>
    private void RefreshZoneVisibility()
    {
        if (!zonePaintActive) return;
        ResetZoneIdentities(); ShowZoneGhosts();
    }
    /// <summary>The faded copies follow a change of their materials (textures on or off).</summary>
    private void SyncZoneGhosts()
    {
        foreach (var (material, ghost) in zoneGhostMaterials) PreviewMaterials.CopyTo(material, ghost);
    }
    /// <summary>After a source update replaced models with other polygon zones: the overlay colours and identities follow.</summary>
    private void RefreshZoneModels(IReadOnlySet<int> models)
    {
        if (models.Count > 0) RefreshPolygonZoneModels(models);
        // The meshes and their placements stay; the faded ones stay faded.
        ResetZoneIdentities();
    }

    private void ClearZonePainting()
    {
        bool stroking = zoneStroke != null;
        zonePaintActive = false; zoneStroke = null; zoneStrokePoint = null; zonePresentationSuspended = false;
        if (stroking && viewport.IsMouseCaptured) viewport.ReleaseMouseCapture();
        zonePending.Clear(); zoneGhosts.Clear(); zoneGhostMaterials.Clear(); zoneShownSkipped = []; zonePartTriangles.Clear();
        zonePreviewMesh?.Dispose(); zonePreviewMesh = null;
        foreach (var line in zoneLines.Values) line.Dispose();
        zoneLines.Clear(); zoneBrush = null; ResetZoneIdentities();
        if (stroking) ZoneStrokeProgress?.Invoke(0, 0, false);
        ZoneHover = null; ZoneHoverBlocked = false; zoneHoverPoint = null;
        if (Cursor == PolygonZoneCursors.Paint || Cursor == PolygonZoneCursors.Erase || Cursor == PolygonZoneCursors.Pick || Cursor == Cursors.No) Cursor = null;
    }
}
