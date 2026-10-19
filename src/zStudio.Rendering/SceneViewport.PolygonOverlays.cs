using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Data;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using zStudio.Core;

namespace zStudio.Rendering;

// Per-polygon overlays of static meshes: polygon outlines (Wireframe) over the normal appearance, and polygon zone tints.
public sealed partial class SceneViewport
{
    /// <summary>A static mesh geometry's source: its part (each vertex's polygon) and the model those polygons belong to.</summary>
    private readonly Dictionary<MeshGeometry3D, (MeshPart Part, GameModel Model)> polygonSources = [];
    /// <summary>The static mesh geometries of each scene model, for a model replaced with other polygon zones.</summary>
    private readonly Dictionary<int, List<MeshGeometry3D>> polygonGeometries = [];
    private readonly Dictionary<MeshGeometry3D, (Color4Collection? Normal, Color4Collection Zones)> polygonZoneColors = [];
    /// <summary>The zone colours without the hidden zones (<see cref="HiddenZones"/>), made when they are first shown.</summary>
    private readonly Dictionary<MeshGeometry3D, Color4Collection> filteredZoneColors = [];
    private readonly Dictionary<MeshGeometry3D, LineGeometry3D> polygonEdgeGeometries = [];
    private readonly Dictionary<MeshGeometryModel3D, (LineGeometryModel3D Halo, LineGeometryModel3D Core)> polygonEdges = [];
    /// <summary>A polygon outline is a light line on a dark halo, so it shows on bright and dark surfaces alike.</summary>
    private static readonly System.Windows.Media.Color PolygonEdgeHalo = System.Windows.Media.Color.FromArgb(200, 12, 12, 12), PolygonEdgeCore = System.Windows.Media.Color.FromRgb(236, 236, 236);

    private void RegisterPolygonSource(MeshGeometry3D geometry, MeshPart part, IReadOnlyList<GameModel> models, int model)
    {
        if (part.VertexPolygons.Length == part.Positions.Length && part.Positions.Length > 0 && model >= 0 && model < models.Count && polygonSources.TryAdd(geometry, (part, models[model])))
        {
            if (!polygonGeometries.TryGetValue(model, out var list)) polygonGeometries[model] = list = [];
            list.Add(geometry);
        }
    }
    /// <summary>
    /// Models a source update replaced with other polygon zones (the same geometry): their zone colours come from the shown
    /// scene's models from now on, recomputed at once when they are shown.
    /// </summary>
    private void RefreshPolygonZoneModels(IReadOnlySet<int> models)
    {
        if (PreviewScene is not { } scene) return;
        foreach (int model in models)
        {
            if (model < 0 || model >= scene.Models.Count || !polygonGeometries.TryGetValue(model, out var geometries)) continue;
            foreach (var geometry in geometries)
            {
                var (part, _) = polygonSources[geometry];
                polygonSources[geometry] = (part, scene.Models[model]);
                if (polygonZoneColors.Remove(geometry, out var colors) && !ReferenceEquals(geometry.Colors, colors.Normal)) geometry.Colors = colors.Normal;
                filteredZoneColors.Remove(geometry);
            }
        }
        ShowPolygonZoneColors(HighlightMode == WorldHighlightMode.PolygonZones || zonePaintActive);
        viewport.InvalidateRender();
    }

    /// <summary>Outlines every polygon of the static meshes (not their triangles), following each mesh's shown instances.</summary>
    private void ShowPolygonEdges(bool enabled)
    {
        if (!enabled) { RemovePolygonEdges(); return; }
        foreach (var mesh in meshes)
        {
            if (polygonEdges.ContainsKey(mesh) || mesh.Geometry is not MeshGeometry3D geometry || !polygonSources.TryGetValue(geometry, out var source)) continue;
            if (!polygonEdgeGeometries.TryGetValue(geometry, out var edges)) polygonEdgeGeometries[geometry] = edges = PolygonEdgeGeometry(geometry, source.Part, source.Model);
            if (edges.Indices == null || edges.Indices.Count == 0) continue;
            // The core sits nearer than its halo, so it wins the depth test whichever is drawn first.
            var halo = Outline(mesh, edges, PolygonEdgeHalo, 2, -100); var core = Outline(mesh, edges, PolygonEdgeCore, 1, -200);
            viewport.Items.Add(halo); viewport.Items.Add(core); polygonEdges[mesh] = (halo, core);
        }
        viewport.InvalidateRender();

        static LineGeometryModel3D Outline(MeshGeometryModel3D mesh, LineGeometry3D edges, System.Windows.Media.Color color, double thickness, int depthBias)
        {
            LineGeometryModel3D line = new() { Geometry = edges, Color = color, Thickness = thickness, IsHitTestVisible = false, DepthBias = depthBias };
            // The outline shows exactly where its mesh does: hidden, isolated and moved placements included.
            line.SetBinding(GeometryModel3D.InstancesProperty, new Binding(nameof(MeshGeometryModel3D.Instances)) { Source = mesh, Mode = BindingMode.OneWay });
            // Helix's own Visibility (Element3D is no UIElement): a mesh without instances is collapsed, and an outline drawn then would sit at the origin.
            line.SetBinding(Element3D.VisibilityProperty, new Binding(nameof(MeshGeometryModel3D.Visibility)) { Source = mesh, Mode = BindingMode.OneWay });
            return line;
        }
    }
    private void RemovePolygonEdges()
    {
        foreach (var (halo, core) in polygonEdges.Values)
            foreach (var line in new[] { halo, core }) { viewport.Items.Remove(line); BindingOperations.ClearAllBindings(line); line.Dispose(); }
        polygonEdges.Clear();
    }
    /// <summary>Each polygon's closed outline; a strip's runs along its even vertices and back along its odd ones.</summary>
    private static LineGeometry3D PolygonEdgeGeometry(MeshGeometry3D geometry, MeshPart part, GameModel model)
    {
        IntCollection indices = [];
        int[] owners = part.VertexPolygons;
        for (int start = 0; start < owners.Length;)
        {
            int end = start + 1;
            while (end < owners.Length && owners[end] == owners[start]) end++;
            int count = end - start, polygon = owners[start];
            bool strip = polygon >= 0 && polygon < model.Polygons.Length && (model.Polygons[polygon].Flags & 1024) != 0;
            int[] loop = strip ? [.. Enumerable.Range(0, count).Where(i => i % 2 == 0), .. Enumerable.Range(0, count).Where(i => i % 2 == 1).Reverse()] : [.. Enumerable.Range(0, count)];
            if (count >= 2)
                for (int i = 0; i < loop.Length; i++) { indices.Add(start + loop[i]); indices.Add(start + loop[(i + 1) % loop.Length]); }
            start = end;
        }
        return new LineGeometry3D { Positions = geometry.Positions, Indices = indices };
    }

    /// <summary>
    /// Gives each world surface geometry its polygons' zone sets as vertex colours (the polygon zone pass reads them), or restores
    /// its authored colours. Geometry is shared by every placement of a model, as its polygons' zones are.
    /// </summary>
    private void ShowPolygonZoneColors(bool enabled)
    {
        foreach (var (mesh, surface) in surfaceAppearances)
        {
            if (surface.Horizon || mesh.Geometry is not MeshGeometry3D geometry || !polygonSources.TryGetValue(geometry, out var source)) continue;
            if (enabled)
            {
                if (!polygonZoneColors.TryGetValue(geometry, out var colors)) polygonZoneColors[geometry] = colors = (geometry.Colors, PolygonZoneColors(source.Part, source.Model, null));
                // The overlay leaves hidden zones out; the polygon zone tool shows them all.
                var shown = colors.Zones;
                if (hiddenZones.Count > 0 && !zonePaintActive && !filteredZoneColors.TryGetValue(geometry, out shown!))
                    filteredZoneColors[geometry] = shown = PolygonZoneColors(source.Part, source.Model, hiddenZones);
                if (!ReferenceEquals(geometry.Colors, shown)) geometry.Colors = shown;
            }
            else if (polygonZoneColors.TryGetValue(geometry, out var colors) && !ReferenceEquals(geometry.Colors, colors.Normal)) geometry.Colors = colors.Normal;
        }
    }
    private static Color4Collection PolygonZoneColors(MeshPart part, GameModel model, IReadOnlySet<byte>? hidden)
    {
        Dictionary<int, Color4> byPolygon = [];
        Color4Collection colors = new(part.VertexPolygons.Length);
        foreach (int polygon in part.VertexPolygons)
        {
            if (!byPolygon.TryGetValue(polygon, out var color))
                byPolygon[polygon] = color = PolygonZoneColor(polygon >= 0 && polygon < model.Polygons.Length ? model.Polygons[polygon].Metadata["zone_set"] : null, hidden);
            colors.Add(color);
        }
        return colors;
    }
    /// <summary>
    /// Up to three zone ids as n/255 in RGB and their count as n/3 in A; no zones is all zero. Any (255) is stored as -1.
    /// <paramref name="hidden"/> zones are left out (a polygon whose zones are all hidden reads as one without zones).
    /// </summary>
    internal static Color4 PolygonZoneColor(JsonNode? set, IReadOnlySet<byte>? hidden = null)
    {
        if (set is not JsonArray ids || ids.Count == 0) return default;
        // Only the first three are stored; hidden ones drop out of the stripes.
        byte a = 0, b = 0, c = 0; int count = 0;
        for (int i = 0; i < ids.Count && i < 3; i++)
        {
            byte id = unchecked((byte)(int)JsonData.Scalar(ids[i]));
            if (hidden?.Contains(id) == true) continue;
            if (count == 0) a = id; else if (count == 1) b = id; else c = id;
            count++;
        }
        if (count == 0) return default;
        return new(a / 255f, count > 1 ? b / 255f : 0, count > 2 ? c / 255f : 0, count / 3f);
    }

    private void ClearPolygonOverlays()
    {
        RemovePolygonEdges();
        polygonSources.Clear(); polygonGeometries.Clear(); polygonZoneColors.Clear(); filteredZoneColors.Clear(); polygonEdgeGeometries.Clear();
    }
}
