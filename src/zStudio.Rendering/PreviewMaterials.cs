using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Shaders;
using HelixToolkit.Wpf.SharpDX;

namespace zStudio.Rendering;

internal static class PreviewMaterials
{
    private const string AlphaPass = "ZbdDiffuseAlpha";
    private const string HorizonPass = "ZbdHorizon";
    private const string TintPass = "ZbdVertexTint";
    private const string OverlayPass = "ZbdOverlay";
    private const string OrientationPass = "ZbdFaceOrientation";
    private const string PolygonZonePass = "ZbdPolygonZones";
    internal sealed class OrientationState { public volatile bool Enabled; }
    public static DiffuseMaterial Create(bool horizon = false, bool vertexTint = false, OrientationState? orientation = null) => new(new PreviewMaterialCore(horizon, vertexTint, orientation));
    private static PreviewMaterialCore OverlayMaterial(DiffuseMaterial material) =>
        material.Core as PreviewMaterialCore ?? throw new InvalidOperationException("Diagnostic overlays require a preview material.");
    internal static void SetOverlay(DiffuseMaterial material, Color4 color) => OverlayMaterial(material).Overlay = color;
    internal static Color4 OverlayColor(DiffuseMaterial material) => OverlayMaterial(material).Overlay;
    /// <summary>Tints each polygon by the zone set its geometry's vertex colours then carry (see SceneViewport.PolygonOverlays).</summary>
    internal static void SetPolygonZones(DiffuseMaterial material, bool enabled) => OverlayMaterial(material).PolygonZones = enabled;
    /// <summary>
    /// How the polygon zone pass tints: default (every zone at half strength); <see cref="ZoneEmphasis.Emphasis"/> (polygons that
    /// list the zone at <c>Strong</c>, the others at <c>Weak</c>); or <see cref="ZoneEmphasis.Preview"/> (the zone colours alone at
    /// <c>Strong</c> opacity, for a mesh drawn over the scene).
    /// </summary>
    internal static void SetZoneEmphasis(DiffuseMaterial material, ZoneEmphasis emphasis, byte zone = 0, float strong = .5f, float weak = .5f)
        => OverlayMaterial(material).Emphasis = new(zone / 255f, strong, weak, (float)emphasis);
    /// <summary>Draws the material's surfaces at <c>1 - fade</c> opacity (a transparent mesh's alpha pass).</summary>
    internal static void SetFade(DiffuseMaterial material, float fade) => OverlayMaterial(material).Fade = fade;
    /// <summary>A copy for a mesh that must look different from the others sharing <paramref name="material"/>.</summary>
    internal static DiffuseMaterial Copy(DiffuseMaterial material)
    {
        var core = OverlayMaterial(material);
        var copy = new DiffuseMaterial(new PreviewMaterialCore(core.Horizon, core.VertexTint, core.Orientation))
        {
            DiffuseColor = material.DiffuseColor, DiffuseMap = material.DiffuseMap, EnableUnLit = material.EnableUnLit, VertexColorBlendingFactor = material.VertexColorBlendingFactor,
        };
        var target = OverlayMaterial(copy);
        target.Overlay = core.Overlay; target.PolygonZones = core.PolygonZones; target.Emphasis = core.Emphasis; target.Fade = core.Fade;
        return copy;
    }
    /// <summary>Gives a copy made by <see cref="Copy"/> its source's current appearance again, keeping the copy's fade.</summary>
    internal static void CopyTo(DiffuseMaterial material, DiffuseMaterial copy)
    {
        var (core, target) = (OverlayMaterial(material), OverlayMaterial(copy));
        copy.DiffuseColor = material.DiffuseColor; copy.DiffuseMap = material.DiffuseMap; copy.EnableUnLit = material.EnableUnLit; copy.VertexColorBlendingFactor = material.VertexColorBlendingFactor;
        target.Overlay = core.Overlay; target.PolygonZones = core.PolygonZones; target.Emphasis = core.Emphasis;
    }
    internal enum ZoneEmphasis { None = 0, Emphasis = 1, Preview = 2 }
    private sealed class PreviewMaterialCore(bool horizon, bool vertexTint, OrientationState? orientation) : DiffuseMaterialCore
    {
        internal bool Horizon => horizon;
        internal bool VertexTint => vertexTint;
        internal OrientationState? Orientation => orientation;
        private Color4 overlay;
        public Color4 Overlay { get => overlay; set => Set(ref overlay, value); }
        private bool polygonZones;
        public bool PolygonZones { get => polygonZones; set => Set(ref polygonZones, value); }
        private Color4 emphasis;
        public Color4 Emphasis { get => emphasis; set => Set(ref emphasis, value); }
        private float fade;
        public float Fade { get => fade; set => Set(ref fade, value); }
        public override MaterialVariable CreateMaterialVariables(IEffectsManager manager, IRenderTechnique technique) => new Variables(manager, technique, this, horizon, vertexTint, orientation);
    }
    private sealed class Variables : DiffuseMaterialVariables
    {
        private readonly IRenderTechnique technique;
        private readonly PreviewMaterialCore material;
        private readonly string normalPrefix;
        private readonly bool horizon;
        private readonly OrientationState? orientation;
        public Variables(IEffectsManager manager, IRenderTechnique technique, PreviewMaterialCore material, bool horizon, bool vertexTint, OrientationState? orientation)
            : base(vertexTint ? TintPass : DefaultPassNames.Diffuse, manager, technique, material)
        { this.technique = technique; this.material = material; this.horizon = horizon; this.orientation = orientation; normalPrefix = vertexTint ? TintPass : ""; }
        protected override void OnInitialPropertyBindings()
        {
            base.OnInitialPropertyBindings();
            // Helix 3.1.2's diffuse shaders do not use emissive. Its stock mesh VS
            // already carries this exact cbMesh field to COLOR1. No ABI extension.
            AddPropertyBinding(nameof(PreviewMaterialCore.Overlay), () => WriteValue("vMaterialEmissive", material.Overlay));
            // Diffuse materials leave the specular and reflect colours unused: the zone pass's emphasis and the fade.
            AddPropertyBinding(nameof(PreviewMaterialCore.Emphasis), () => WriteValue("vMaterialSpecular", material.Emphasis));
            AddPropertyBinding(nameof(PreviewMaterialCore.Fade), () => WriteValue("vMaterialReflect", new Color4(material.Fade, 0, 0, 0)));
        }
        // Each diagnostic pass retains the normal opaque/alpha/horizon depth policy.
        public override ShaderPass GetPass(RenderType renderType, RenderContext context)
        {
            string prefix = orientation?.Enabled == true ? OrientationPass : material.PolygonZones ? PolygonZonePass : material.Overlay.Alpha > 0 ? OverlayPass : normalPrefix;
            // The stock diffuse pass knows no fade; the tint pass draws plain diffuse materials the same.
            if (prefix.Length == 0 && material.Fade > 0) prefix = TintPass;
            string suffix = horizon ? HorizonPass : renderType == RenderType.Transparent ? AlphaPass : "";
            return prefix.Length == 0 && suffix.Length == 0 ? base.GetPass(renderType, context) : technique[prefix + suffix];
        }
    }
}
