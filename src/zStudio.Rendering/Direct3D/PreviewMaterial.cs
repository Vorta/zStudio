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
    private TextureModel? helix;
    private TextureResource? resource;
    private RenderTexture(byte[] pixels, int width, int height, bool bgra)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.LongLength < 4L * width * height) throw new ArgumentException("The pixel array is smaller than the texture.", nameof(pixels));
        Pixels = pixels; Width = width; Height = height; IsBgra = bgra;
        if (RenderBackend.UseHelix) helix = new TextureModel(pixels, bgra ? SharpDX.DXGI.Format.B8G8R8A8_UNorm : SharpDX.DXGI.Format.R8G8B8A8_UNorm, width, height);
    }
    public static RenderTexture FromRgba(byte[] rgba, int width, int height) => new(rgba, width, height, false);
    public static RenderTexture FromBgra(byte[] bgra, int width, int height) => new(bgra, width, height, true);
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public bool IsBgra { get; }
    internal TextureModel HelixTexture => helix ??= new TextureModel(Pixels, IsBgra ? SharpDX.DXGI.Format.B8G8R8A8_UNorm : SharpDX.DXGI.Format.R8G8B8A8_UNorm, Width, Height);
    /// <summary>Direct3D backend: the uploaded texture (built on first draw for each device).</summary>
    internal TextureResource Resource(D3DGraphics graphics) => DeviceResource.Refresh(ref resource, graphics, g => new TextureResource(g, this));
    internal void ReleaseDeviceResources() { resource?.Dispose(); resource = null; }
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
    private readonly bool preview, horizon, vertexTint;
    private readonly FaceOrientation? orientation;
    private RenderTexture? map;
    private ZoneEmphasis emphasis;
    // Direct3D backend state (the Helix adapter keeps it in its material and core).
    private Color4 diffuse = new(1, 1, 1, 1), overlay;
    private bool unlit, polygonZones;
    private float vertexBlending, fade;

    private PreviewMaterial(HelixDiffuseMaterial? helix, PreviewCore? core, bool preview, bool horizon, bool vertexTint, FaceOrientation? orientation)
    {
        HelixMaterial = helix; this.core = core; this.preview = preview; this.horizon = horizon; this.vertexTint = vertexTint; this.orientation = orientation;
    }
    private static PreviewMaterial Register(HelixDiffuseMaterial material, PreviewCore? core)
    {
        var result = new PreviewMaterial(material, core, core != null, core?.Horizon == true, core?.VertexTint == true, core?.Orientation);
        wrappers.AddOrUpdate(material, result); return result;
    }
    public static PreviewMaterial Create(bool horizon = false, bool vertexTint = false, FaceOrientation? orientation = null)
    {
        if (!RenderBackend.UseHelix) return new(null, null, true, horizon, vertexTint, orientation);
        var core = new PreviewCore(horizon, vertexTint, orientation);
        return Register(new HelixDiffuseMaterial(core), core);
    }
    public static PreviewMaterial CreatePlain() => RenderBackend.UseHelix ? Register(new HelixDiffuseMaterial(), null) : new(null, null, false, false, false, null);
    /// <summary>The facade of a material the Helix adapter's gizmo created itself (its own colours).</summary>
    internal static PreviewMaterial Wrap(HelixDiffuseMaterial material) => wrappers.GetValue(material, static m =>
        new(m, m.Core as PreviewCore, m.Core is PreviewCore, (m.Core as PreviewCore)?.Horizon == true, (m.Core as PreviewCore)?.VertexTint == true, (m.Core as PreviewCore)?.Orientation));

    /// <summary>The Helix adapter's material, null in the Direct3D backend.</summary>
    internal HelixDiffuseMaterial? HelixMaterial { get; }
    public bool IsPreview => preview;
    public bool Horizon => horizon;
    public bool VertexTint => vertexTint;
    public FaceOrientation? Orientation => orientation;
    // Direct3D backend: a changed value redraws the views (setting the same value renders nothing).
    private static void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; RenderView.InvalidateAll();
    }

    public Color4 DiffuseColor
    {
        get { if (HelixMaterial is not { } helix) return diffuse; var c = helix.DiffuseColor; return new(c.Red, c.Green, c.Blue, c.Alpha); }
        set { if (HelixMaterial is { } helix) { diffuse = value; helix.DiffuseColor = new HelixColor4(value.Red, value.Green, value.Blue, value.Alpha); } else Set(ref diffuse, value); }
    }
    public RenderTexture? DiffuseMap { get => map; set { if (HelixMaterial is { } helix) { map = value; helix.DiffuseMap = value?.HelixTexture; } else Set(ref map, value); } }
    public bool Unlit { get => HelixMaterial?.EnableUnLit ?? unlit; set { if (HelixMaterial is { } helix) { unlit = value; helix.EnableUnLit = value; } else Set(ref unlit, value); } }
    public float VertexColorBlendingFactor
    {
        get => HelixMaterial is { } helix ? (float)helix.VertexColorBlendingFactor : vertexBlending;
        set { if (HelixMaterial is { } helix) { vertexBlending = value; helix.VertexColorBlendingFactor = value; } else Set(ref vertexBlending, value); }
    }

    private void RequirePreview() { if (!preview) throw new InvalidOperationException("Diagnostic overlays require a preview material."); }
    /// <summary>Diagnostic tint: lerp(rgb, overlay.rgb, overlay.a); alpha, cutouts and depth policy unchanged. Alpha 0 is none.</summary>
    public Color4 Overlay
    {
        get { RequirePreview(); if (core == null) return overlay; var c = core.Overlay; return new(c.Red, c.Green, c.Blue, c.Alpha); }
        set { RequirePreview(); if (core != null) { overlay = value; core.Overlay = new HelixColor4(value.Red, value.Green, value.Blue, value.Alpha); } else Set(ref overlay, value); }
    }
    /// <summary>Tints each polygon by the zone set its vertex colours carry (ids n/255 in RGB, count n/3 in A).</summary>
    public bool PolygonZones
    {
        get { RequirePreview(); return core?.PolygonZones ?? polygonZones; }
        set { RequirePreview(); if (core != null) { polygonZones = value; core.PolygonZones = value; } else Set(ref polygonZones, value); }
    }
    public ZoneEmphasis Emphasis
    {
        get { RequirePreview(); return emphasis; }
        set
        {
            RequirePreview();
            if (core == null) { Set(ref emphasis, value); return; }
            core.Emphasis = new HelixColor4(value.Zone / 255f, value.Strong, value.Weak, (float)value.Mode);
            emphasis = value;
        }
    }
    /// <summary>Draws the surfaces at 1 − fade opacity.</summary>
    public float Fade
    {
        get { RequirePreview(); return core?.Fade ?? fade; }
        set { RequirePreview(); if (core != null) { fade = value; core.Fade = value; } else Set(ref fade, value); }
    }

    /// <summary>A preview copy (same pass flags and state) for a mesh that must look different from the others sharing this one.</summary>
    public PreviewMaterial Copy()
    {
        RequirePreview();
        var copy = Create(horizon, vertexTint, orientation);
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
        if (HelixMaterial is { } helix)
        {
            var clone = Register((HelixDiffuseMaterial)helix.Clone(), null);
            clone.map = map;
            return clone;
        }
        var plain = CreatePlain();
        plain.diffuse = diffuse; plain.map = map; plain.unlit = unlit; plain.vertexBlending = vertexBlending;
        return plain;
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
