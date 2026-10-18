using HelixToolkit.Maths;
using HelixToolkit.Wpf.SharpDX;
using zStudio.Core;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private sealed record SurfaceAppearance(DiffuseMaterial Normal, WorldSurfaceKind Kind, int Zone = 0xFF, bool Horizon = false);
    private readonly Dictionary<MeshGeometryModel3D, SurfaceAppearance> surfaceAppearances = [];
    public WorldHighlightMode HighlightMode { get; private set; }

    /// <summary>Changes only a shader tint; normal materials, picking and camera stay intact.</summary>
    public void SetWorldHighlightMode(WorldHighlightMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        HighlightMode = mode;
        // Polygon zones tint per polygon from the geometry's vertex colours rather than one colour per node.
        bool polygonZones = mode == WorldHighlightMode.PolygonZones;
        ShowPolygonZoneColors(polygonZones);
        foreach (var surface in surfaceAppearances.Values)
        {
            PreviewMaterials.SetPolygonZones(surface.Normal, polygonZones && !surface.Horizon);
            Color4 tint = default;
            // The horizon is a landmark too, but tinting the whole sky would hide the landmarks seen against it.
            if (WorldSurfaceHighlights.Matches(surface.Kind, mode) && !(mode == WorldHighlightMode.Landmarks && surface.Horizon))
            {
                var (r, g, b) = WorldSurfaceHighlights.ZoneColor(surface.Zone);
                tint = mode switch
                {
                    WorldHighlightMode.NonDefaultSoils => new(1, 1, 0, .5f),
                    WorldHighlightMode.CanModify => new(0, 1, 0, .5f),
                    WorldHighlightMode.CanModifyAndClipTo when (surface.Kind & WorldSurfaceKind.ClipTo) == 0 => new(0, 1, 0, .5f),
                    WorldHighlightMode.Zones => new(r, g, b, .5f),
                    WorldHighlightMode.Landmarks => new(1, 0, 1, .5f),
                    _ => new(1, 0, 0, .5f)
                };
            }
            PreviewMaterials.SetOverlay(surface.Normal, tint);
        }
        viewport.InvalidateRender();
    }

    private void ClearWorldHighlights()
    {
        surfaceAppearances.Clear(); HighlightMode = WorldHighlightMode.None;
    }
}
