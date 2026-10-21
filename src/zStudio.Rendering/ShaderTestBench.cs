using System.IO;
using System.Runtime.InteropServices;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace zStudio.Rendering;

/// <summary>Runs the production vertex-tint pixel shader on a software device, without a window or hardware GPU (tests only).</summary>
internal static class ShaderTestBench
{
    /// <summary>
    /// Draws one full-surface triangle through PSMain: surface normal +Z, authored vertex colour (.5, .25, 1, .5) with vertex
    /// colour blending on, a 1×1 texture (255, 128, 64, alpha) and an eye vector at angle acos(eyeZ) from the normal.
    /// Returns the centre pixel's RGBA8 for each case.
    /// </summary>
    internal static byte[][] RenderVertexTintPixels(bool textured, bool unlit, bool flat, IReadOnlyList<(float EyeZ, byte TextureAlpha)> cases)
    {
        using var sourceStream = typeof(ShaderTestBench).Assembly.GetManifestResourceStream("zStudio.Rendering.VertexTint.hlsl")!;
        using var reader = new StreamReader(sourceStream);
        string source = reader.ReadToEnd();
        using var pixelCode = ShaderBytecode.Compile(source, "PSMain", "ps_5_0", ShaderFlags.OptimizationLevel3);
        using var vertexCode = ShaderBytecode.Compile(source + """

            TintPixel TestVertex(uint id : SV_VertexID)
            {
                TintPixel output = (TintPixel)0;
                float2 xy = float2((id << 1) & 2, id & 2) * 2 - 1;
                output.position = float4(xy, 0, 1);
                output.world = output.position;
                output.eye = vColor;
                output.normal = float3(0, 0, 1);
                output.uv = float2(.5, .5);
                output.color = float4(.5, .25, 1, .5);
                return output;
            }
            """, "TestVertex", "vs_5_0", ShaderFlags.OptimizationLevel3);
        using var reflection = new ShaderReflection(pixelCode);
        var meshLayout = reflection.GetConstantBuffer("cbMesh");
        byte[] meshData = new byte[meshLayout.Description.Size];
        void Flag(string name, bool value) => BitConverter.GetBytes(value ? 1 : 0).CopyTo(meshData, meshLayout.GetVariable(name).Description.StartOffset);
        Flag("bHasDiffuseMap", textured);
        // Helix DiffuseMaterialVariables binds EnableUnLit to this ABI slot.
        Flag("bHasNormalMap", unlit);
        Flag("bRenderFlat", flat);
        // Production vertex-tint materials select authored colors through this ABI field.
        BitConverter.GetBytes(1f).CopyTo(meshData, meshLayout.GetVariable("vertColorBlending").Description.StartOffset);

        using var device = new Device(DriverType.Warp, DeviceCreationFlags.None);
        using var context = device.ImmediateContext;
        using var vertexShader = new VertexShader(device, vertexCode);
        using var pixelShader = new PixelShader(device, pixelCode);
        using var constants = new Buffer(device, meshData.Length, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        var description = new Texture2DDescription
        {
            Width = 4, Height = 4, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget
        };
        using var target = new Texture2D(device, description);
        using var targetView = new RenderTargetView(device, target);
        description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None; description.CpuAccessFlags = CpuAccessFlags.Read;
        using var readback = new Texture2D(device, description);
        description.Width = description.Height = 1; description.Usage = ResourceUsage.Default;
        description.BindFlags = BindFlags.ShaderResource; description.CpuAccessFlags = CpuAccessFlags.None;
        using var texture = new Texture2D(device, description);
        using var textureView = new ShaderResourceView(device, texture);
        using var sampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaximumLod = float.MaxValue
        });
        using var raster = new RasterizerState(device, new RasterizerStateDescription { FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true });
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        context.VertexShader.Set(vertexShader); context.VertexShader.SetConstantBuffer(1, constants);
        context.PixelShader.Set(pixelShader); context.PixelShader.SetConstantBuffer(1, constants);
        context.PixelShader.SetShaderResource(0, textureView); context.PixelShader.SetSampler(0, sampler);
        context.OutputMerger.SetRenderTargets(targetView);
        context.Rasterizer.State = raster; context.Rasterizer.SetViewport(0, 0, 4, 4);
        var results = new byte[cases.Count][];
        for (int i = 0; i < cases.Count; i++)
        {
            var (eyeZ, alpha) = cases[i];
            int eyeOffset = meshLayout.GetVariable("vColor").Description.StartOffset;
            BitConverter.GetBytes(MathF.Sqrt(1 - eyeZ * eyeZ)).CopyTo(meshData, eyeOffset);
            BitConverter.GetBytes(eyeZ).CopyTo(meshData, eyeOffset + 8);
            context.UpdateSubresource(meshData, constants);
            context.UpdateSubresource(new byte[] { 255, 128, 64, alpha }, texture, 0, 4, 4);
            context.Draw(3, 0);
            context.CopyResource(target, readback);
            var mapped = context.MapSubresource(readback, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            byte[] pixel = new byte[4];
            try { Marshal.Copy(mapped.DataPointer + 2 * mapped.RowPitch + 2 * 4, pixel, 0, 4); }
            finally { context.UnmapSubresource(readback, 0); }
            results[i] = pixel;
        }
        context.ClearState();
        return results;
    }
}
