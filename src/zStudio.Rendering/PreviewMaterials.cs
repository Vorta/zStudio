using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

/// <summary>The preview's material operations (see <see cref="PreviewMaterial"/> for what each pass draws).</summary>
internal static class PreviewMaterials
{
    public static PreviewMaterial Create(bool horizon = false, bool vertexTint = false, FaceOrientation? orientation = null) => PreviewMaterial.Create(horizon, vertexTint, orientation);
    internal static void SetOverlay(PreviewMaterial material, Color4 color) => material.Overlay = color;
    internal static Color4 OverlayColor(PreviewMaterial material) => material.Overlay;
    /// <summary>Tints each polygon by the zone set its geometry's vertex colours then carry (see SceneViewport.PolygonOverlays).</summary>
    internal static void SetPolygonZones(PreviewMaterial material, bool enabled) => material.PolygonZones = enabled;
    /// <summary>
    /// How the polygon zone pass tints: default (every zone at half strength); <see cref="ZoneEmphasisMode.Emphasis"/> (polygons that
    /// list the zone at <c>Strong</c>, the others at <c>Weak</c>); or <see cref="ZoneEmphasisMode.Preview"/> (the zone colours alone at
    /// <c>Strong</c> opacity, for a mesh drawn over the scene).
    /// </summary>
    internal static void SetZoneEmphasis(PreviewMaterial material, ZoneEmphasisMode emphasis, byte zone = 0, float strong = .5f, float weak = .5f)
        => material.Emphasis = new(emphasis, zone, strong, weak);
    /// <summary>Draws the material's surfaces at <c>1 - fade</c> opacity (a transparent mesh's alpha pass).</summary>
    internal static void SetFade(PreviewMaterial material, float fade) => material.Fade = fade;
    /// <summary>A copy for a mesh that must look different from the others sharing <paramref name="material"/>.</summary>
    internal static PreviewMaterial Copy(PreviewMaterial material) => material.Copy();
    /// <summary>Gives a copy made by <see cref="Copy"/> its source's current appearance again, keeping the copy's fade.</summary>
    internal static void CopyTo(PreviewMaterial material, PreviewMaterial copy) => material.CopyTo(copy);
}
