using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Shaders;
using HelixToolkit.Wpf.SharpDX;

namespace Recoil.Zbd.Rendering;

internal static class PreviewMaterials
{
    private const string AlphaPass = "ZbdDiffuseAlpha";
    private const string HorizonPass = "ZbdHorizon";
    private const string TintPass = "ZbdVertexTint";
    private static readonly Lazy<byte[]> TintShader = new(() =>
    {
        using var stream = typeof(PreviewMaterials).Assembly.GetManifestResourceStream("Recoil.Zbd.Rendering.VertexTint.hlsl")!;
        using var reader = new System.IO.StreamReader(stream);
        using var shader = SharpDX.D3DCompiler.ShaderBytecode.Compile(reader.ReadToEnd(), "PSMain", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3);
        return shader.Bytecode.Data;
    });
    public static DefaultEffectsManager CreateEffects() => PreviewResourceLifetime.Create<DefaultEffectsManager>(
        static () => new PreviewEffectsManager(), InstallPasses);

    private static void InstallPasses(DefaultEffectsManager manager)
    {
        GroundGridShader.Install(manager);
        manager[DefaultRenderTechniqueNames.Mesh]!.AddPass(new ShaderPassDescription(AlphaPass)
        {
            InputLayoutDescription = new(DefaultVSShaderByteCodes.VSMeshDefault, DefaultInputLayout.VSInput),
            ShaderList = [DefaultVSShaderDescriptions.VSMeshDefault, DefaultPSShaderDescriptions.PSMeshDiffuseMap],
            BlendStateDescription = DefaultBlendStateDescriptions.BSAlphaBlend,
            DepthStencilStateDescription = DefaultDepthStencilDescriptions.DSSLessNoWrite
        });
        manager[DefaultRenderTechniqueNames.Mesh]!.AddPass(new ShaderPassDescription(HorizonPass)
        {
            InputLayoutDescription = new(DefaultVSShaderByteCodes.VSMeshDefault, DefaultInputLayout.VSInput),
            ShaderList = [DefaultVSShaderDescriptions.VSMeshDefault, DefaultPSShaderDescriptions.PSMeshDiffuseMap],
            BlendStateDescription = DefaultBlendStateDescriptions.BSAlphaBlend,
            DepthStencilStateDescription = DefaultDepthStencilDescriptions.DSSNoDepthNoStencil
        });
        foreach (string pass in new[] { TintPass, TintPass + AlphaPass, TintPass + HorizonPass })
            manager[DefaultRenderTechniqueNames.Mesh]!.AddPass(new ShaderPassDescription(pass)
            {
                InputLayoutDescription = new(DefaultVSShaderByteCodes.VSMeshDefault, DefaultInputLayout.VSInput),
                ShaderList = [DefaultVSShaderDescriptions.VSMeshDefault, new("ZbdVertexTintPS", ShaderStage.Pixel, TintShader.Value)],
                BlendStateDescription = DefaultBlendStateDescriptions.BSAlphaBlend,
                DepthStencilStateDescription = pass == TintPass ? DefaultDepthStencilDescriptions.DSSDepthLess : pass.EndsWith(AlphaPass, StringComparison.Ordinal) ? DefaultDepthStencilDescriptions.DSSLessNoWrite : DefaultDepthStencilDescriptions.DSSNoDepthNoStencil
            });
    }
    public static DiffuseMaterial Create(bool horizon = false, bool vertexTint = false) => new(new PreviewMaterialCore(horizon, vertexTint));
    private sealed class PreviewMaterialCore(bool horizon, bool vertexTint) : DiffuseMaterialCore
    {
        public override MaterialVariable CreateMaterialVariables(IEffectsManager manager, IRenderTechnique technique) => new Variables(manager, technique, this, horizon, vertexTint);
    }
    private sealed class Variables : DiffuseMaterialVariables
    {
        private readonly ShaderPass alphaPass;
        private readonly ShaderPass? horizonPass;
        public Variables(IEffectsManager manager, IRenderTechnique technique, DiffuseMaterialCore material, bool horizon, bool vertexTint)
            : base(vertexTint ? TintPass : DefaultPassNames.Diffuse, manager, technique, material)
        { string prefix = vertexTint ? TintPass : ""; alphaPass = technique[prefix + AlphaPass]; horizonPass = horizon ? technique[prefix + HorizonPass] : null; }
        // The stock diffuse pass writes depth even at alpha zero. Keep opaque depth
        // writes, but blend translucent texels without masking later surfaces.
        public override ShaderPass GetPass(RenderType renderType, RenderContext context) =>
            horizonPass ?? (renderType == RenderType.Transparent ? alphaPass : base.GetPass(renderType, context));
    }
}
