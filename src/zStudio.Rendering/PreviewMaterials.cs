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
    private static readonly Lazy<(byte[] Normal, byte[] Overlay, byte[] Orientation, byte[] PolygonZones)> Shaders = new(() =>
    {
        string Read(string name)
        {
            using var stream = typeof(PreviewMaterials).Assembly.GetManifestResourceStream("zStudio.Rendering." + name)!;
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
    public static DefaultEffectsManager CreateEffects() => PreviewResourceLifetime.Create<DefaultEffectsManager>(
        static () => new PreviewEffectsManager(), InstallPasses);

    private static void InstallPasses(DefaultEffectsManager manager)
    {
        GroundGridShader.Install(manager);
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
    public static DiffuseMaterial Create(bool horizon = false, bool vertexTint = false, OrientationState? orientation = null) => new(new PreviewMaterialCore(horizon, vertexTint, orientation));
    private static PreviewMaterialCore OverlayMaterial(DiffuseMaterial material) =>
        material.Core as PreviewMaterialCore ?? throw new InvalidOperationException("Diagnostic overlays require a preview material.");
    internal static void SetOverlay(DiffuseMaterial material, Color4 color) => OverlayMaterial(material).Overlay = color;
    internal static Color4 OverlayColor(DiffuseMaterial material) => OverlayMaterial(material).Overlay;
    /// <summary>Tints each polygon by the zone set its geometry's vertex colours then carry (see SceneViewport.PolygonOverlays).</summary>
    internal static void SetPolygonZones(DiffuseMaterial material, bool enabled) => OverlayMaterial(material).PolygonZones = enabled;
    private sealed class PreviewMaterialCore(bool horizon, bool vertexTint, OrientationState? orientation) : DiffuseMaterialCore
    {
        private Color4 overlay;
        public Color4 Overlay { get => overlay; set => Set(ref overlay, value); }
        private bool polygonZones;
        public bool PolygonZones { get => polygonZones; set => Set(ref polygonZones, value); }
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
        }
        // Each diagnostic pass retains the normal opaque/alpha/horizon depth policy.
        public override ShaderPass GetPass(RenderType renderType, RenderContext context)
        {
            string prefix = orientation?.Enabled == true ? OrientationPass : material.PolygonZones ? PolygonZonePass : material.Overlay.Alpha > 0 ? OverlayPass : normalPrefix;
            string suffix = horizon ? HorizonPass : renderType == RenderType.Transparent ? AlphaPass : "";
            return prefix.Length == 0 && suffix.Length == 0 ? base.GetPass(renderType, context) : technique[prefix + suffix];
        }
    }
}
