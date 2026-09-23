using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Shaders;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;

namespace Recoil.Zbd.Rendering;

internal static class GroundGridShader
{
    private static readonly Lazy<(byte[] Vertex, byte[] Pixel)> Shaders = new(() =>
    {
        using var stream = typeof(GroundGridShader).Assembly.GetManifestResourceStream("Recoil.Zbd.Rendering.GroundGrid.hlsl")
            ?? throw new InvalidOperationException("The ground grid shader is missing.");
        using var reader = new System.IO.StreamReader(stream);
        string source = reader.ReadToEnd();
        using var vertex = ShaderBytecode.Compile(source, "VSMain", "vs_5_0", ShaderFlags.OptimizationLevel3);
        using var pixel = ShaderBytecode.Compile(source, "PSMain", "ps_5_0", ShaderFlags.OptimizationLevel3);
        return (vertex.Bytecode.Data, pixel.Bytecode.Data);
    });

    public static void Install(DefaultEffectsManager manager)
    {
        var shaders = Shaders.Value;
        var depth = DefaultDepthStencilDescriptions.DSSLessNoWrite;
        depth.DepthComparison = Comparison.LessEqual;
        var rasterizer = DefaultRasterDescriptions.RSPlaneGrid;
        // Only this reference plane may extend past the scene's precision-fitted far
        // plane. Its shader clamps depth; opaque geometry still occludes it normally.
        rasterizer.IsDepthClipEnabled = false;
        var technique = manager[DefaultRenderTechniqueNames.PlaneGrid]!;
        technique.RemovePass(DefaultPassNames.Default);
        technique.AddPass(new ShaderPassDescription(DefaultPassNames.Default)
        {
            Topology = SharpDX.Direct3D.PrimitiveTopology.TriangleStrip,
            ShaderList = [new("ZbdGroundGridVS", ShaderStage.Vertex, shaders.Vertex), new("ZbdGroundGridPS", ShaderStage.Pixel, shaders.Pixel)],
            BlendStateDescription = DefaultBlendStateDescriptions.BSAlphaBlend,
            DepthStencilStateDescription = depth,
            RasterStateDescription = rasterizer
        });
    }
}
