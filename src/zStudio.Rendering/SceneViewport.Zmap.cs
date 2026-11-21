using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using zStudio.Core;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    public bool IsZmapView { get; private set; }
    private (Vector3 Minimum, Vector3 Maximum) zmapWorldBounds;
    public (Vector3 Minimum, Vector3 Maximum) ZmapWorldBounds => zmapWorldBounds;

    public void SetZmapView(bool enabled, IReadOnlySet<int>? recipeNodes = null, IReadOnlySet<int>? databaseNodes = null)
    {
        if (enabled == IsZmapView) return;
        StopCameraMotion();
        if (enabled)
        {
            SetAxisView("top");
            zmapWorldBounds = (sceneMin, sceneMax);
            PrepareZmapTerrain(recipeNodes, databaseNodes);
        }
        IsZmapView = enabled;
        view.ShowViewCube = !enabled && !PresentationUiHidden;
        view.ShowCoordinateSystem = !enabled && !PresentationUiHidden && showAxes;
        view.GuidesVisible = !enabled && !PresentationUiHidden;
        presentationOverlays.Visibility = enabled || PresentationUiHidden ? Visibility.Collapsed : Visibility.Visible;
        view.InvalidateRender();
        if (enabled) RefreshZmapSnap();
        else
        {
            bool filtered = hiddenZmapTerrainZones.Count != 0 || ZmapTerrainMaxY.HasValue;
            ClearZmapTerrain();
            if (filtered) ApplyNodeVisibility();
            ResetZmapSnap();
        }
    }

    public bool TryZmapPoint(Point point, float height, out Vector3 position)
    {
        position = default;
        if (!IsZmapView || !TryNavigationRay(point, out var origin, out _)) return false;
        position = new((float)origin.X, height, (float)origin.Z);
        return Finite(position);
    }

    public bool FrameZmapOutline(Vector3 min, Vector3 max)
    {
        if (!IsZmapView || !Finite(min) || !Finite(max)) return false;
        double low = float.IsFinite(zmapWorldBounds.Minimum.Y) ? zmapWorldBounds.Minimum.Y : 0;
        double high = float.IsFinite(zmapWorldBounds.Maximum.Y) ? zmapWorldBounds.Maximum.Y : 0;
        return FrameBounds(new Rect3D(min.X - 1d, low, min.Z - 1d,
            (double)max.X - min.X + 2, high - low, (double)max.Z - min.Z + 2), true);
    }

    private readonly record struct ZmapTerrainPiece(byte Zone, bool Recipe);
    private readonly Dictionary<int, ZmapTerrainPiece> zmapTerrainPieces = [];
    private readonly HashSet<byte> hiddenZmapTerrainZones = [];
    public IReadOnlyList<byte> ZmapTerrainZones { get; private set; } = [];
    public float? ZmapTerrainMaxY { get; private set; }

    public bool IsZmapTerrainZoneVisible(byte zone) => !hiddenZmapTerrainZones.Contains(zone);

    private void PrepareZmapTerrain(IReadOnlySet<int>? recipeNodes, IReadOnlySet<int>? databaseNodes)
    {
        ClearZmapTerrain();
        if (PreviewScene is not { } scene) return;
        var mission = Mission;
        var actors = mission?.PrepareActorIndex(scene);
        foreach (var node in scene.Nodes)
        {
            int index = node.Index;
            // Mission start copies retain the originating slot, while terrain uses the database's actual nodes.
            if (node.ModelIndex == null || IsHorizon(index) || actors?.At(index) != null || actors?.IsAmbiguous(index) == true
                || mission != null && (index >= mission.SourceNodes.Count || mission.SourceNodes[index] != index)) continue;
            bool recipe = recipeNodes?.Contains(index) == true;
            if (recipe || databaseNodes?.Contains(index) == true)
                zmapTerrainPieces.Add(index, new((byte)WorldSurfaceHighlights.NodeZone(scene, index), recipe));
        }

        HashSet<byte> zones = [];
        foreach (var (mesh, rows) in placements)
        {
            if (mesh.Geometry is not { Indices.Length: >= 3 }) continue;
            foreach (var row in rows)
                if (TryZmapTerrainPiece(row, out var piece)) zones.Add(piece.Zone);
        }
        ZmapTerrainZones = [.. zones.Order()];
    }

    private void ClearZmapTerrain()
    {
        zmapTerrainPieces.Clear();
        hiddenZmapTerrainZones.Clear();
        ZmapTerrainZones = [];
        ZmapTerrainMaxY = null;
    }

    private bool TryZmapTerrainPiece(ScenePlacement placement, out ZmapTerrainPiece piece) =>
        zmapTerrainPieces.TryGetValue(placement.NodeIndex, out piece) && (piece.Recipe || placement.Transform.IsIdentity);

    private bool IsZmapTerrainHidden(ScenePlacement placement) =>
        hiddenZmapTerrainZones.Count != 0 && TryZmapTerrainPiece(placement, out var piece) && hiddenZmapTerrainZones.Contains(piece.Zone);

    public bool SetZmapTerrainHeight(float? y)
    {
        if (!IsZmapView) throw new InvalidOperationException("Enter Edit ZMAP first.");
        if (y is float supplied && !float.IsFinite(supplied)) throw new ArgumentException("Enter a finite Y coordinate.", nameof(y));
        if (ZmapTerrainMaxY == y) return false;
        ZmapTerrainMaxY = y;
        ApplyNodeVisibility();
        return true;
    }

    private Matrix4x4 StaticMeshWorld(MeshNode mesh)
    {
        var world = mesh.Transform ?? Matrix4x4.Identity;
        return mesh.IsTransparent && sceneAlphaGroup?.Transform is { } parent ? world * parent : world;
    }

    private bool TryStaticZmapBounds(MeshNode mesh, int index, Matrix4x4 instance, (Vector3 Min, Vector3 Max) local,
        out (Vector3 Min, Vector3 Max) world) =>
        MeshHeightClip.TryWorldBounds(local, instance * StaticMeshWorld(mesh), mesh.HeightClip?.At(index), out world);

    public bool SetZmapTerrainVisibility(byte? zone, bool visible)
    {
        if (!IsZmapView) throw new InvalidOperationException("Enter Edit ZMAP first.");
        bool changed;
        if (zone is byte id)
        {
            if (!ZmapTerrainZones.Contains(id)) throw new ArgumentException("Choose a terrain zone used by this map.", nameof(zone));
            changed = visible ? hiddenZmapTerrainZones.Remove(id) : hiddenZmapTerrainZones.Add(id);
        }
        else
        {
            if (!visible) throw new ArgumentException("Choose a terrain zone to hide.", nameof(zone));
            changed = hiddenZmapTerrainZones.Count != 0;
            hiddenZmapTerrainZones.Clear();
        }
        if (changed) ApplyNodeVisibility();
        return changed;
    }

    private NavigationGesture CurrentGesture(MouseButton button, ModifierKeys modifiers) =>
        IsZmapView && button == MouseButton.Middle
            ? modifiers.HasFlag(ModifierKeys.Control) ? NavigationGesture.Zoom : NavigationGesture.Pan
            : Gesture(button, modifiers);
}
