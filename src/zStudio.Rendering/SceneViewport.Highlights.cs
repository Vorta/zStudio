using zStudio.Core;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private sealed record SurfaceAppearance(PreviewMaterial Normal, WorldSurfaceKind Kind, int Zone = 0xFF, bool Horizon = false);
    private readonly Dictionary<MeshNode, SurfaceAppearance> surfaceAppearances = [];
    public WorldHighlightMode HighlightMode { get; private set; }
    private HashSet<byte> hiddenZones = [];
    /// <summary>Zone colours the overlays leave out (the World tree's zone eyes): view only, never geometry or picking.</summary>
    public IReadOnlySet<byte> HiddenZones => hiddenZones;
    /// <summary>The zone a selection emphasises in the zone overlays (its colour strong, the others faint), or null.</summary>
    public byte? ZoneSelection { get; private set; }

    /// <summary>
    /// Which zone colours the zone overlays show and which they emphasise. A polygon whose zones are all hidden looks normal;
    /// one with several zones keeps only the shown ones in its stripes. The polygon zone tool's own view ignores both.
    /// </summary>
    public void SetZonePresentation(IReadOnlySet<byte> hidden, byte? selected)
    {
        bool colours = !hiddenZones.SetEquals(hidden);
        if (!colours && ZoneSelection == selected) return;
        if (colours) { hiddenZones = [.. hidden]; filteredZoneColors.Clear(); }
        ZoneSelection = selected;
        SetWorldHighlightMode(HighlightMode);
    }

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
            if (!surface.Horizon) ApplySurfaceZoneEmphasis(surface.Normal);
            Color4 tint = default;
            byte zone = unchecked((byte)surface.Zone);
            // The horizon is a landmark too, but tinting the whole sky would hide the landmarks seen against it.
            if (WorldSurfaceHighlights.Matches(surface.Kind, mode) && !(mode == WorldHighlightMode.Landmarks && surface.Horizon)
                && !(mode == WorldHighlightMode.Zones && hiddenZones.Contains(zone)))
            {
                var (r, g, b) = WorldSurfaceHighlights.ZoneColor(surface.Zone);
                tint = mode switch
                {
                    WorldHighlightMode.NonDefaultSoils => new(1, 1, 0, .5f),
                    WorldHighlightMode.CanModify => new(0, 1, 0, .5f),
                    WorldHighlightMode.CanModifyAndClipTo when (surface.Kind & WorldSurfaceKind.ClipTo) == 0 => new(0, 1, 0, .5f),
                    // A selected zone is strong and the others faint, as the polygon zone tool shows its zone.
                    WorldHighlightMode.Zones => new(r, g, b, ZoneSelection is byte chosen ? chosen == zone ? ZoneStrong : ZoneWeak : .5f),
                    WorldHighlightMode.Landmarks => new(1, 0, 1, .5f),
                    _ => new(1, 0, 0, .5f)
                };
            }
            PreviewMaterials.SetOverlay(surface.Normal, tint);
        }
        // The polygon zone tool keeps its zones shown whatever the overlay.
        if (zonePaintActive) ApplyZoneEmphasis();
        view.InvalidateRender();
    }

    /// <summary>How the polygon zone pass emphasises: the tool's zone while it is engaged, else a selected zone in the polygon zone overlay.</summary>
    private void ApplySurfaceZoneEmphasis(PreviewMaterial material)
    {
        if (zonePaintActive) PreviewMaterials.SetZoneEmphasis(material, ZoneEmphasisMode.Emphasis, paintZone, ZoneStrong, ZoneWeak);
        else if (HighlightMode == WorldHighlightMode.PolygonZones && ZoneSelection is byte zone && !hiddenZones.Contains(zone))
            PreviewMaterials.SetZoneEmphasis(material, ZoneEmphasisMode.Emphasis, zone, ZoneStrong, ZoneWeak);
        else PreviewMaterials.SetZoneEmphasis(material, ZoneEmphasisMode.None, 0, 0, 0);
    }

    private void ClearWorldHighlights()
    {
        surfaceAppearances.Clear(); HighlightMode = WorldHighlightMode.None;
    }
}
