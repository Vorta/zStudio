using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Shaders;
using HelixToolkit.Wpf.SharpDX;

namespace Recoil.Zbd.Rendering;

internal static class PreviewMaterials
{
    private const string AlphaPass = "ZbdDiffuseAlpha";
    private const string HorizonPass = "ZbdHorizon";
    public static DefaultEffectsManager CreateEffects()
    {
        var manager = new DefaultEffectsManager();
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
        return manager;
    }
    public static DiffuseMaterial Create(bool horizon = false) => new(new PreviewMaterialCore(horizon));
    private sealed class PreviewMaterialCore(bool horizon) : DiffuseMaterialCore
    {
        public override MaterialVariable CreateMaterialVariables(IEffectsManager manager, IRenderTechnique technique) => new Variables(manager, technique, this, horizon);
    }
    private sealed class Variables : DiffuseMaterialVariables
    {
        private readonly ShaderPass alphaPass;
        private readonly ShaderPass? horizonPass;
        public Variables(IEffectsManager manager, IRenderTechnique technique, DiffuseMaterialCore material, bool horizon)
            : base(DefaultPassNames.Diffuse, manager, technique, material) { alphaPass = technique[AlphaPass]; horizonPass = horizon ? technique[HorizonPass] : null; }
        // The stock diffuse pass writes depth even at alpha zero. Keep opaque depth
        // writes, but blend translucent texels without masking later surfaces.
        public override ShaderPass GetPass(RenderType renderType, RenderContext context) =>
            horizonPass ?? (renderType == RenderType.Transparent ? alphaPass : base.GetPass(renderType, context));
    }
}
