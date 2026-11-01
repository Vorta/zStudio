using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace zStudio.Rendering.Direct3D;

/// <summary>Runs the production vertex-tint pixel shader on a software device, without a window or hardware GPU (tests only).</summary>
internal static class ShaderTestBench
{
    /// <summary>Draws homogeneous clip-space segments through the production line expansion and pixel shaders on WARP.</summary>
    internal static byte[][] RenderLinePixels(IReadOnlyList<(Vector4 A, Vector4 B)> segments)
    {
        string source = D3DGraphics.ShaderSource + """

            cbuffer LineTest : register(b2) { float4 TestA; float4 TestB; };
            LineSegmentEnd TestLineVertex(uint id : SV_VertexID)
            {
                LineSegmentEnd output;
                output.position = id == 0 ? TestA : TestB;
                return output;
            }
            """;
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.None, [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using var _device = device;
        using var _context = context;
        using var vertex = device.CreateVertexShader(D3DGraphics.Compile(source, "TestLineVertex", "vs_5_0"));
        using var geometry = device.CreateGeometryShader(D3DGraphics.Compile(source, "GSLine", "gs_5_0"));
        using var pixel = device.CreatePixelShader(D3DGraphics.Compile(source, "PSLine", "ps_5_0"));
        using var frame = device.CreateBuffer([new FrameConstants { Viewport = new(64, 64, 1f / 64, 1f / 64), DpiScale = 1 }], BindFlags.ConstantBuffer);
        using var draw = device.CreateBuffer([new ObjectConstants { Diffuse = new(1, 1, 1, 1), Params = new(0, 0, 2, 0) }], BindFlags.ConstantBuffer);
        using var ends = device.CreateBuffer(32, BindFlags.ConstantBuffer);
        var description = new Texture2DDescription { Width = 64, Height = 64, MipLevels = 1, ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm, SampleDescription = new(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget };
        using var target = device.CreateTexture2D(description);
        using var targetView = device.CreateRenderTargetView(target);
        description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None; description.CPUAccessFlags = CpuAccessFlags.Read;
        using var readback = device.CreateTexture2D(description);
        using var raster = device.CreateRasterizerState(new RasterizerDescription(Vortice.Direct3D11.CullMode.None, FillMode.Solid) { DepthClipEnable = false });
        context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        context.VSSetShader(vertex); context.VSSetConstantBuffer(2, ends);
        context.GSSetShader(geometry); context.GSSetConstantBuffer(0, frame); context.GSSetConstantBuffer(1, draw);
        context.PSSetShader(pixel); context.PSSetConstantBuffer(1, draw);
        context.RSSetState(raster); context.RSSetViewport(0, 0, 64, 64);
        context.OMSetRenderTargets(targetView);
        byte[][] results = new byte[segments.Count][];
        for (int i = 0; i < segments.Count; i++)
        {
            context.ClearRenderTargetView(targetView, new(0, 0, 0, 0));
            context.UpdateSubresource(new[] { segments[i].A, segments[i].B }, ends);
            context.Draw(2, 0);
            context.CopyResource(readback, target);
            var mapped = context.Map(readback, 0, MapMode.Read);
            byte[] rgba = new byte[64 * 64 * 4];
            try { for (int row = 0; row < 64; row++) Marshal.Copy(mapped.DataPointer + (nint)(row * mapped.RowPitch), rgba, row * 64 * 4, 64 * 4); }
            finally { context.Unmap(readback, 0); }
            results[i] = rgba;
        }
        context.ClearState();
        return results;
    }
    /// <summary>
    /// Draws one full-surface triangle through the vertex-tint pass: surface normal +Z, authored vertex colour (.5, .25, 1, .5)
    /// with vertex colour blending on, a 1×1 texture (255, 128, 64, alpha) and an eye vector at angle acos(eyeZ) from the normal.
    /// Returns the centre pixel's RGBA8 for each case.
    /// </summary>
    internal static byte[][] RenderVertexTintPixels(bool textured, bool unlit, bool flat, IReadOnlyList<(float EyeZ, byte TextureAlpha)> cases)
    {
        string source = D3DGraphics.ShaderSource + """

            cbuffer TestBench : register(b2) { float4 TestEye; };
            MeshPixel TestVertex(uint id : SV_VertexID)
            {
                MeshPixel output = (MeshPixel)0;
                float2 xy = float2((id << 1) & 2, id & 2) * 2 - 1;
                output.position = float4(xy, 0, 1);
                output.world = output.position;
                output.eye = TestEye;
                output.normal = float3(0, 0, 1);
                output.uv = float2(.5, .5);
                output.color = float4(.5, .25, 1, .5);
                return output;
            }
            """;
        byte[] pixelCode = D3DGraphics.Compile(D3DGraphics.ShaderSource, "PSTint", "ps_5_0");
        byte[] vertexCode = D3DGraphics.Compile(source, "TestVertex", "vs_5_0");
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.None, [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using var _device = device;
        using var _context = context;
        using var vertexShader = device.CreateVertexShader(vertexCode);
        using var pixelShader = device.CreatePixelShader(pixelCode);
        var constants = new ObjectConstants
        {
            World = Matrix4x4.Identity,
            // Production vertex-tint materials select authored colours through the blending factor.
            Params = new(0, 1, 0, 0),
            Textured = textured ? 1u : 0, Unlit = unlit ? 1u : 0, Flat = flat ? 1u : 0
        };
        using var objectBuffer = device.CreateBuffer([constants], BindFlags.ConstantBuffer);
        using var frameBuffer = device.CreateBuffer([new FrameConstants()], BindFlags.ConstantBuffer);
        using var eyeBuffer = device.CreateBuffer(16, BindFlags.ConstantBuffer);
        var description = new Texture2DDescription
        {
            Width = 4, Height = 4, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget
        };
        using var target = device.CreateTexture2D(description);
        using var targetView = device.CreateRenderTargetView(target);
        description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None; description.CPUAccessFlags = CpuAccessFlags.Read;
        using var readback = device.CreateTexture2D(description);
        description.Width = description.Height = 1; description.Usage = ResourceUsage.Default;
        description.BindFlags = BindFlags.ShaderResource; description.CPUAccessFlags = CpuAccessFlags.None;
        using var texture = device.CreateTexture2D(description);
        using var textureView = device.CreateShaderResourceView(texture);
        using var sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, ComparisonFunc = ComparisonFunction.Never, MaxLOD = float.MaxValue
        });
        using var raster = device.CreateRasterizerState(new RasterizerDescription(Vortice.Direct3D11.CullMode.None, FillMode.Solid) { DepthClipEnable = true });
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertexShader); context.VSSetConstantBuffer(0, frameBuffer); context.VSSetConstantBuffer(1, objectBuffer); context.VSSetConstantBuffer(2, eyeBuffer);
        context.PSSetShader(pixelShader); context.PSSetConstantBuffer(0, frameBuffer); context.PSSetConstantBuffer(1, objectBuffer);
        context.PSSetShaderResource(0, textureView); context.PSSetSampler(0, sampler);
        context.OMSetRenderTargets(targetView);
        context.RSSetState(raster); context.RSSetViewport(0, 0, 4, 4);
        var results = new byte[cases.Count][];
        for (int i = 0; i < cases.Count; i++)
        {
            var (eyeZ, alpha) = cases[i];
            context.UpdateSubresource([new Vector4(MathF.Sqrt(1 - eyeZ * eyeZ), 0, eyeZ, 0)], eyeBuffer);
            context.UpdateSubresource(new byte[] { 255, 128, 64, alpha }, texture, 0, 4, 4);
            context.Draw(3, 0);
            context.CopyResource(readback, target);
            var mapped = context.Map(readback, 0, MapMode.Read);
            byte[] pixel = new byte[4];
            try { Marshal.Copy(mapped.DataPointer + (nint)(2 * mapped.RowPitch) + 2 * 4, pixel, 0, 4); }
            finally { context.Unmap(readback, 0); }
            results[i] = pixel;
        }
        context.ClearState();
        return results;
    }
}
