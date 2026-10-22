using System.Runtime.CompilerServices;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Shaders;
using HelixDiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;
using HelixColor4 = HelixToolkit.Maths.Color4;
using TextureModel = HelixToolkit.SharpDX.TextureModel;

namespace zStudio.Rendering.Direct3D;

/// <summary>The preview's face-orientation switch, shared by every material created with it.</summary>
internal sealed class FaceOrientation { public volatile bool Enabled; }

internal enum ZoneEmphasisMode { None = 0, Emphasis = 1, Preview = 2 }

/// <summary>
/// How the polygon zone pass tints: <see cref="ZoneEmphasisMode.None"/> (every zone at half strength);
/// <see cref="ZoneEmphasisMode.Emphasis"/> (polygons listing <see cref="Zone"/> at <see cref="Strong"/>, the others at
/// <see cref="Weak"/>); <see cref="ZoneEmphasisMode.Preview"/> (the zone colours alone at <see cref="Strong"/> opacity, a cleared
/// polygon dark at .55).
/// </summary>
internal readonly record struct ZoneEmphasis(ZoneEmphasisMode Mode, byte Zone, float Strong, float Weak);

/// <summary>An RGBA8 (or BGRA8) texture, sampled with linear filtering, nearest mip and wrapping; uploads build a full mip chain.</summary>
internal sealed class RenderTexture
{
    private RenderTexture(byte[] pixels, int width, int height, bool bgra)
    {
        Pixels = pixels; Width = width; Height = height; IsBgra = bgra;
        HelixTexture = new TextureModel(pixels, bgra ? SharpDX.DXGI.Format.B8G8R8A8_UNorm : SharpDX.DXGI.Format.R8G8B8A8_UNorm, width, height);
    }
    public static RenderTexture FromRgba(byte[] rgba, int width, int height) => new(rgba, width, height, false);
    public static RenderTexture FromBgra(byte[] bgra, int width, int height) => new(bgra, width, height, true);
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public bool IsBgra { get; }
    internal TextureModel HelixTexture { get; }
}

/// <summary>
/// A mesh material. Base colour: the vertex colour when <see cref="VertexColorBlendingFactor"/> is 1, else
/// <see cref="DiffuseColor"/>; times the texture when <see cref="DiffuseMap"/> is set. Lit (<see cref="Unlit"/> false) adds the
/// view term .5 + .5·|dot(eye, normal)| except for textured vertex-tint materials. Output alpha is multiplied by 1 − fade, and it
/// alpha-blends (SrcAlpha/InvSrcAlpha; destination alpha stays 1).
/// <para>
/// A preview material (<see cref="Create"/>) draws through the zStudio pass table: priority face orientation (its
/// <see cref="FaceOrientation"/> enabled) &gt; polygon zones &gt; overlay (overlay alpha &gt; 0) &gt; vertex tint (a vertex-tint
/// material, or a fade &gt; 0); depth: horizon materials neither test nor write depth, transparent meshes test without writing,
/// others test Less and write. A plain material (<see cref="CreatePlain"/>) is the stock diffuse pass only: no overlay, zones,
/// fade or face orientation, and it writes depth even on a transparent mesh.
/// </para>
/// </summary>
internal sealed class PreviewMaterial
{
    private static readonly ConditionalWeakTable<HelixDiffuseMaterial, PreviewMaterial> wrappers = new();
    private readonly PreviewCore? core;
    private RenderTexture? map;
    private ZoneEmphasis emphasis;

    private PreviewMaterial(HelixDiffuseMaterial material, PreviewCore? core) { HelixMaterial = material; this.core = core; }
    private static PreviewMaterial Register(HelixDiffuseMaterial material, PreviewCore? core)
    {
        var result = new PreviewMaterial(material, core); wrappers.AddOrUpdate(material, result); return result;
    }
    public static PreviewMaterial Create(bool horizon = false, bool vertexTint = false, FaceOrientation? orientation = null)
    {
        var core = new PreviewCore(horizon, vertexTint, orientation);
        return Register(new HelixDiffuseMaterial(core), core);
    }
    public static PreviewMaterial CreatePlain() => Register(new HelixDiffuseMaterial(), null);
    /// <summary>The facade of a backend material the backend created itself (the transform gizmo's own colours).</summary>
    internal static PreviewMaterial Wrap(HelixDiffuseMaterial material) => wrappers.GetValue(material, static m => new(m, m.Core as PreviewCore));

    internal HelixDiffuseMaterial HelixMaterial { get; }
    public bool IsPreview => core != null;
    public bool Horizon => core?.Horizon == true;
    public bool VertexTint => core?.VertexTint == true;
    public FaceOrientation? Orientation => core?.Orientation;

    public Color4 DiffuseColor
    {
        get { var c = HelixMaterial.DiffuseColor; return new(c.Red, c.Green, c.Blue, c.Alpha); }
        set => HelixMaterial.DiffuseColor = new HelixColor4(value.Red, value.Green, value.Blue, value.Alpha);
    }
    public RenderTexture? DiffuseMap { get => map; set { map = value; HelixMaterial.DiffuseMap = value?.HelixTexture; } }
    public bool Unlit { get => HelixMaterial.EnableUnLit; set => HelixMaterial.EnableUnLit = value; }
    public float VertexColorBlendingFactor { get => (float)HelixMaterial.VertexColorBlendingFactor; set => HelixMaterial.VertexColorBlendingFactor = value; }

    private PreviewCore Core => core ?? throw new InvalidOperationException("Diagnostic overlays require a preview material.");
    /// <summary>Diagnostic tint: lerp(rgb, overlay.rgb, overlay.a); alpha, cutouts and depth policy unchanged. Alpha 0 is none.</summary>
    public Color4 Overlay
    {
        get { var c = Core.Overlay; return new(c.Red, c.Green, c.Blue, c.Alpha); }
        set => Core.Overlay = new HelixColor4(value.Red, value.Green, value.Blue, value.Alpha);
    }
    /// <summary>Tints each polygon by the zone set its vertex colours carry (ids n/255 in RGB, count n/3 in A).</summary>
    public bool PolygonZones { get => Core.PolygonZones; set => Core.PolygonZones = value; }
    public ZoneEmphasis Emphasis
    {
        get { _ = Core; return emphasis; }
        set { Core.Emphasis = new HelixColor4(value.Zone / 255f, value.Strong, value.Weak, (float)value.Mode); emphasis = value; }
    }
    /// <summary>Draws the surfaces at 1 − fade opacity.</summary>
    public float Fade { get => Core.Fade; set => Core.Fade = value; }

    /// <summary>A preview copy (same pass flags and state) for a mesh that must look different from the others sharing this one.</summary>
    public PreviewMaterial Copy()
    {
        var source = Core;
        var copy = Create(source.Horizon, source.VertexTint, source.Orientation);
        copy.DiffuseColor = DiffuseColor; copy.DiffuseMap = DiffuseMap; copy.Unlit = Unlit; copy.VertexColorBlendingFactor = VertexColorBlendingFactor;
        copy.Overlay = Overlay; copy.PolygonZones = PolygonZones; copy.Emphasis = Emphasis; copy.Fade = Fade;
        return copy;
    }
    /// <summary>Gives a <see cref="Copy"/> this material's current appearance again, keeping the copy's fade.</summary>
    public void CopyTo(PreviewMaterial copy)
    {
        copy.DiffuseColor = DiffuseColor; copy.DiffuseMap = DiffuseMap; copy.Unlit = Unlit; copy.VertexColorBlendingFactor = VertexColorBlendingFactor;
        copy.Overlay = Overlay; copy.PolygonZones = PolygonZones; copy.Emphasis = Emphasis;
    }
    /// <summary>A plain material with this one's colour, texture, lighting and vertex blending.</summary>
    public PreviewMaterial ClonePlain()
    {
        var clone = Register((HelixDiffuseMaterial)HelixMaterial.Clone(), null);
        clone.map = map;
        return clone;
    }

    // Backend pass table (Helix 3.1.2): the stock diffuse pass for plain opaque surfaces, zStudio passes for the rest.
    private const string AlphaPass = "ZbdDiffuseAlpha";
    private const string HorizonPass = "ZbdHorizon";
    private const string TintPass = "ZbdVertexTint";
    private const string OverlayPass = "ZbdOverlay";
    private const string OrientationPass = "ZbdFaceOrientation";
    private const string PolygonZonePass = "ZbdPolygonZones";
    private static readonly Lazy<(byte[] Normal, byte[] Overlay, byte[] Orientation, byte[] PolygonZones)> Shaders = new(() =>
    {
        string Read(string name)
        {
            using var stream = typeof(PreviewMaterial).Assembly.GetManifestResourceStream("zStudio.Rendering." + name)!;
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
        string common = Read("VertexTint.hlsl");
        byte[] Compile(string source, string entry)
        {
            using var shader = SharpDX.D3DCompiler.ShaderBytecode.Compile(source, entry, "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3);
            return shader.Bytecode.Data;
        }
        return (Compile(common, "PSMain"), Compile(common, "PSOverlay"), Compile(common + Read("FaceOrientation.hlsl"), "PSOrientation"), Compile(common, "PSPolygonZones"));
    });

    internal static void InstallHelixPasses(DefaultEffectsManager manager)
    {
        foreach (string prefix in new[] { "", TintPass, OverlayPass, OrientationPass, PolygonZonePass })
        foreach (string suffix in new[] { "", AlphaPass, HorizonPass })
        {
            if (prefix.Length == 0 && suffix.Length == 0) continue; // Retain the exact stock normal diffuse pass.
            var pixel = prefix switch
            {
                TintPass => new ShaderDescription("ZbdVertexTintPS", ShaderStage.Pixel, Shaders.Value.Normal),
                OverlayPass => new ShaderDescription("ZbdOverlayPS", ShaderStage.Pixel, Shaders.Value.Overlay),
                OrientationPass => new ShaderDescription("ZbdFaceOrientationPS", ShaderStage.Pixel, Shaders.Value.Orientation),
                PolygonZonePass => new ShaderDescription("ZbdPolygonZonesPS", ShaderStage.Pixel, Shaders.Value.PolygonZones),
                _ => DefaultPSShaderDescriptions.PSMeshDiffuseMap
            };
            manager[DefaultRenderTechniqueNames.Mesh]!.AddPass(new ShaderPassDescription(prefix + suffix)
            {
                InputLayoutDescription = new(DefaultVSShaderByteCodes.VSMeshDefault, DefaultInputLayout.VSInput),
                ShaderList = [DefaultVSShaderDescriptions.VSMeshDefault, pixel],
                BlendStateDescription = DefaultBlendStateDescriptions.BSAlphaBlend,
                DepthStencilStateDescription = suffix == HorizonPass ? DefaultDepthStencilDescriptions.DSSNoDepthNoStencil
                    : suffix == AlphaPass ? DefaultDepthStencilDescriptions.DSSLessNoWrite : DefaultDepthStencilDescriptions.DSSDepthLess
            });
        }
    }

    private sealed class PreviewCore(bool horizon, bool vertexTint, FaceOrientation? orientation) : DiffuseMaterialCore
    {
        internal bool Horizon => horizon;
        internal bool VertexTint => vertexTint;
        internal FaceOrientation? Orientation => orientation;
        private HelixColor4 overlay;
        public HelixColor4 Overlay { get => overlay; set => Set(ref overlay, value); }
        private bool polygonZones;
        public bool PolygonZones { get => polygonZones; set => Set(ref polygonZones, value); }
        private HelixColor4 emphasis;
        public HelixColor4 Emphasis { get => emphasis; set => Set(ref emphasis, value); }
        private float fade;
        public float Fade { get => fade; set => Set(ref fade, value); }
        public override MaterialVariable CreateMaterialVariables(IEffectsManager manager, IRenderTechnique technique) => new Variables(manager, technique, this, horizon, vertexTint, orientation);
    }
    private sealed class Variables : DiffuseMaterialVariables
    {
        private readonly IRenderTechnique technique;
        private readonly PreviewCore material;
        private readonly string normalPrefix;
        private readonly bool horizon;
        private readonly FaceOrientation? orientation;
        public Variables(IEffectsManager manager, IRenderTechnique technique, PreviewCore material, bool horizon, bool vertexTint, FaceOrientation? orientation)
            : base(vertexTint ? TintPass : DefaultPassNames.Diffuse, manager, technique, material)
        { this.technique = technique; this.material = material; this.horizon = horizon; this.orientation = orientation; normalPrefix = vertexTint ? TintPass : ""; }
        protected override void OnInitialPropertyBindings()
        {
            base.OnInitialPropertyBindings();
            // Helix 3.1.2's diffuse shaders do not use emissive. Its stock mesh VS
            // already carries this exact cbMesh field to COLOR1. No ABI extension.
            AddPropertyBinding(nameof(PreviewCore.Overlay), () => WriteValue("vMaterialEmissive", material.Overlay));
            // Diffuse materials leave the specular and reflect colours unused: the zone pass's emphasis and the fade.
            AddPropertyBinding(nameof(PreviewCore.Emphasis), () => WriteValue("vMaterialSpecular", material.Emphasis));
            AddPropertyBinding(nameof(PreviewCore.Fade), () => WriteValue("vMaterialReflect", new HelixColor4(material.Fade, 0, 0, 0)));
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
