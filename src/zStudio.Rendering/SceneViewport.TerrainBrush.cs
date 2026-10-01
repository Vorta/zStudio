using System.Windows;
using System.Windows.Input;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Vector3 = System.Numerics.Vector3;

namespace Recoil.Zbd.Rendering;

/// <summary>
/// Terrain painting: while the brush is on, a left-button drag over the scene collects the surface points under the
/// pointer and hands the stroke to the owner on release. It changes nothing itself; Escape or a lost capture drops it.
/// </summary>
public sealed partial class SceneViewport
{
    private List<Vector3>? brushStroke;
    private LineGeometryModel3D? brushTrail;
    private bool terrainBrushActive;
    /// <summary>Whether left-button drags paint instead of selecting. Turning it off drops a stroke in progress.</summary>
    public bool TerrainBrushActive
    {
        get => terrainBrushActive;
        set
        {
            bool changed = terrainBrushActive != value;
            terrainBrushActive = value;
            if (!value) CancelTerrainStroke();
            // Fly owns the cursor while it runs; an unchanged brush leaves the cursor alone.
            if (changed && !IsFlyActive) Cursor = value ? terrainBrushWaiting ? Cursors.Wait : Cursors.Pen : null;
        }
    }
    private bool terrainBrushWaiting;
    /// <summary>While the owner applies an earlier stroke (its world rebuilds), clicks start no new stroke and select nothing.</summary>
    public bool TerrainBrushWaiting
    {
        get => terrainBrushWaiting;
        set { terrainBrushWaiting = value; if (terrainBrushActive && !IsFlyActive) Cursor = value ? Cursors.Wait : Cursors.Pen; }
    }
    /// <summary>Sets the cursor from the brush's state (after Fly, which owned it, ends).</summary>
    public void RefreshTerrainBrushCursor()
    {
        if (IsFlyActive) return;
        if (terrainBrushActive) Cursor = terrainBrushWaiting ? Cursors.Wait : Cursors.Pen;
        else if (Cursor == Cursors.Pen || Cursor == Cursors.Wait) Cursor = null;
    }
    /// <summary>The brush radius in world units; it spaces the stroke's points.</summary>
    public float TerrainBrushRadius { get; set; } = 8;
    public bool IsTerrainStroking => brushStroke != null;
    /// <summary>A finished stroke: the surface points under the pointer, in order.</summary>
    public event Action<IReadOnlyList<Vector3>>? TerrainStrokeCompleted;

    internal bool HandleTerrainBrushDown(Point point, MouseButtonEventArgs e)
    {
        if (!terrainBrushActive || e.ChangedButton != MouseButton.Left || IsFlyActive || IsPickupDragging) return false;
        if (terrainBrushWaiting) return true;
        if (TryNavigationSurface(point, out var hit))
        {
            brushStroke = [new((float)hit.X, (float)hit.Y, (float)hit.Z)];
            viewport.CaptureMouse(); viewport.Focus(); ShowTrail();
        }
        // A click off the scene does nothing, but never selects while painting.
        return true;
    }
    internal bool HandleTerrainBrushMove(Point point)
    {
        if (brushStroke == null) return false;
        if (TryNavigationSurface(point, out var hit))
        {
            Vector3 p = new((float)hit.X, (float)hit.Y, (float)hit.Z);
            float spacing = Math.Max(TerrainBrushRadius / 4, 0.25f);
            if (Vector3.Distance(p, brushStroke[^1]) >= spacing && brushStroke.Count < 10_000) { brushStroke.Add(p); ShowTrail(); }
        }
        return true;
    }
    internal bool HandleTerrainBrushUp(MouseButtonEventArgs e)
    {
        if (brushStroke == null || e.ChangedButton != MouseButton.Left) return brushStroke != null;
        var stroke = brushStroke; brushStroke = null;
        viewport.ReleaseMouseCapture(); HideTrail();
        TerrainStrokeCompleted?.Invoke(stroke);
        return true;
    }
    /// <summary>Drops a stroke in progress; returns whether there was one.</summary>
    public bool CancelTerrainStroke()
    {
        if (brushStroke == null) return false;
        brushStroke = null; HideTrail();
        if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture();
        return true;
    }
    private void ShowTrail()
    {
        if (brushStroke is not { Count: > 0 } stroke) return;
        // The path drawn a little above the surface, over the scene and without depth writes, never picked.
        List<Vector3> points = [.. stroke.Select(p => p + new Vector3(0, 0.25f, 0))];
        if (points.Count == 1) points.Add(points[0] + new Vector3(0.01f, 0, 0));
        IntCollection indices = [];
        for (int i = 0; i + 1 < points.Count; i++) { indices.Add(i); indices.Add(i + 1); }
        var geometry = new LineGeometry3D { Positions = new Vector3Collection(points), Indices = indices };
        if (brushTrail == null)
        {
            brushTrail = new() { Color = System.Windows.Media.Colors.OrangeRed, Thickness = 3, IsHitTestVisible = false, IsDepthClipEnabled = false, DepthBias = -1000 };
            viewport.Items.Add(brushTrail);
        }
        brushTrail.Geometry = geometry;
    }
    private void HideTrail()
    {
        if (brushTrail == null) return;
        viewport.Items.Remove(brushTrail); brushTrail = null;
    }
}
