using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3DCull = Vortice.Direct3D11.CullMode;

namespace zStudio.Rendering.Direct3D;

/// <summary>Per-frame shader constants (cbuffer Frame, register b0).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 View, Projection, ViewProjection;
    public Vector4 Viewport;
    public Vector3 EyePosition;
    public float DpiScale;
    public Vector4 GridParams, GridColor;
}

/// <summary>Per-draw shader constants (cbuffer Object, register b1).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ObjectConstants
{
    public Matrix4x4 World;
    public Vector4 Diffuse, Overlay, Emphasis, Params;
    public uint Textured, Unlit, Flat, Spare;
}

/// <summary>
/// One Direct3D 11 device with the renderer's compiled shaders, input layouts, constant buffers and pipeline states. Device
/// resources made from retained CPU data (geometry, instances, textures) are cached on their owners per device and register
/// here: disposing the device releases every one still alive, so a recovered or closed view leaks nothing (the interop does not
/// release native objects on collection). A new device has a new <see cref="Generation"/>.
/// </summary>
internal sealed class D3DGraphics : IDisposable
{
    private static int generations;
    private readonly Dictionary<(D3DCull Cull, int Bias, bool Clip, bool Scissor), ID3D11RasterizerState> rasterizers = [];
    private readonly HashSet<DeviceResource> resources = [];
    private bool disposed;

    private D3DGraphics(ID3D11Device device, ID3D11DeviceContext context, bool software)
    {
        Device = device; Context = context; IsSoftware = software;
        Generation = Interlocked.Increment(ref generations);
        var code = ShaderCode.Value;
        VSMesh = device.CreateVertexShader(code.VSMesh);
        PSPlain = device.CreatePixelShader(code.PSPlain);
        PSTint = device.CreatePixelShader(code.PSTint);
        PSOverlay = device.CreatePixelShader(code.PSOverlay);
        PSPolygonZones = device.CreatePixelShader(code.PSPolygonZones);
        PSOrientation = device.CreatePixelShader(code.PSOrientation);
        PSColor = device.CreatePixelShader(code.PSColor);
        PSViewCube = device.CreatePixelShader(code.PSViewCube);
        VSLine = device.CreateVertexShader(code.VSLine);
        GSLine = device.CreateGeometryShader(code.GSLine);
        PSLine = device.CreatePixelShader(code.PSLine);
        VSGrid = device.CreateVertexShader(code.VSGrid);
        PSGrid = device.CreatePixelShader(code.PSGrid);
        VSLabel = device.CreateVertexShader(code.VSLabel);
        PSLabel = device.CreatePixelShader(code.PSLabel);
        MeshLayout = device.CreateInputLayout(
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new("COLOR", 0, Format.R32G32B32A32_Float, 0, 3),
            new("INSTANCE", 0, Format.R32G32B32A32_Float, 0, 4, InputClassification.PerInstanceData, 1),
            new("INSTANCE", 1, Format.R32G32B32A32_Float, 16, 4, InputClassification.PerInstanceData, 1),
            new("INSTANCE", 2, Format.R32G32B32A32_Float, 32, 4, InputClassification.PerInstanceData, 1),
            new("INSTANCE", 3, Format.R32G32B32A32_Float, 48, 4, InputClassification.PerInstanceData, 1),
        ], code.VSMesh);
        LineLayout = device.CreateInputLayout(
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("INSTANCE", 0, Format.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new("INSTANCE", 1, Format.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1),
            new("INSTANCE", 2, Format.R32G32B32A32_Float, 32, 1, InputClassification.PerInstanceData, 1),
            new("INSTANCE", 3, Format.R32G32B32A32_Float, 48, 1, InputClassification.PerInstanceData, 1),
        ], code.VSLine);
        LabelLayout = device.CreateInputLayout(
        [
            new("POSITION", 0, Format.R32G32B32A32_Float, 0, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 16, 0),
        ], code.VSLabel);
        FrameBuffer = device.CreateBuffer((uint)Unsafe.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        ObjectBuffer = device.CreateBuffer((uint)Unsafe.SizeOf<ObjectConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        // Colour SrcAlpha/InvSrcAlpha; alpha SrcAlpha/DestAlpha, so an opaque back buffer keeps alpha 1.
        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha, DestinationBlend = Blend.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.SourceAlpha, DestinationBlendAlpha = Blend.DestinationAlpha, BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        AlphaBlend = device.CreateBlendState(blend);
        blend.RenderTarget[0].BlendEnable = false;
        NoBlend = device.CreateBlendState(blend);
        DepthLessWrite = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.Less));
        DepthLessNoWrite = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.Less));
        DepthLessEqualWrite = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual));
        DepthLessEqualNoWrite = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual));
        DepthNone = device.CreateDepthStencilState(new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always));
        // The grid: cull none, depth bias 10 (clamp 1000), scissor, no depth clip; its shader writes the clamped depth itself.
        GridRasterizer = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid, CullMode = D3DCull.None, FrontCounterClockwise = true, DepthBias = 10, DepthBiasClamp = 1000,
            SlopeScaledDepthBias = 0, DepthClipEnable = false, ScissorEnable = true
        });
        // Texture maps: linear filtering within the nearest mip level, wrapping.
        SurfaceSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 4, ComparisonFunc = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue
        });
        // The view cube: outside its face images (the base ring) the border colour, grey.
        CubeSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Border, AddressV = TextureAddressMode.Border,
            AddressW = TextureAddressMode.Border, MaxAnisotropy = 1, ComparisonFunc = ComparisonFunction.Never,
            BorderColor = new Vortice.Mathematics.Color4(128 / 255f, 128 / 255f, 128 / 255f, 1), MinLOD = 0, MaxLOD = float.MaxValue
        });
        LabelSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunc = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue
        });
        ZeroBuffer = device.CreateBuffer(new byte[64], BindFlags.VertexBuffer, ResourceUsage.Immutable);
        IdentityInstance = device.CreateBuffer([Matrix4x4.Identity], BindFlags.VertexBuffer, ResourceUsage.Immutable);
    }

    /// <summary>
    /// A hardware device on the adapter WPF's Direct3D 9Ex surfaces use (its adapter 0, matched by LUID on hybrid-GPU machines;
    /// the default hardware adapter when none matches or that one refuses), or the software rasterizer (WARP). Every shader is
    /// shader model 5, so feature level 11_0 is required.
    /// </summary>
    public static D3DGraphics Create(bool software)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        const DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport;
        ID3D11Device? device = null; ID3D11DeviceContext? context = null;
        SharpGen.Runtime.Result result = default;
        bool created = false, unsupported = false;
        // DXGI_ERROR_UNSUPPORTED: the adapter offers none of the requested feature levels.
        bool Attempt(SharpGen.Runtime.Result attempt) { result = attempt; unsupported |= (uint)attempt.Code == 0x887A0004; return attempt.Success; }
        if (software) created = Attempt(D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, flags, levels, out device, out context));
        else
        {
            using (var adapter = PresentationAdapter())
                if (adapter != null) created = Attempt(D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags, levels, out device, out context));
            if (!created) created = Attempt(D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, levels, out device, out context));
        }
        // Either attempt refusing the feature level is the actionable cause, whatever the fallback reported.
        if (!created && unsupported)
            throw new InvalidOperationException("The 3D preview needs a Direct3D 11 graphics adapter (feature level 11_0); this one does not provide it. Update the graphics driver or use a Direct3D 11 adapter.",
                new SharpGen.Runtime.SharpGenException(result));
        result.CheckError();
        try { return new D3DGraphics(device!, context!, software); }
        catch { context!.Dispose(); device!.Dispose(); throw; }
    }

    // The DXGI adapter with the LUID of Direct3D 9Ex adapter 0 (where the D3DImage surface lives); null when none matches.
    private static IDXGIAdapter1? PresentationAdapter()
    {
        try
        {
            Vortice.Direct3D9.Luid luid;
            using (var d3d9 = Vortice.Direct3D9.D3D9.Direct3DCreate9Ex()) luid = d3d9.GetAdapterLuid(0);
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                var id = adapter.Description1.Luid;
                if (id.LowPart == luid.LowPart && id.HighPart == luid.HighPart) return adapter;
                adapter.Dispose();
            }
        }
        catch (SharpGen.Runtime.SharpGenException) { }
        return null;
    }

    internal void Track(DeviceResource resource) { if (disposed) throw new ObjectDisposedException(nameof(D3DGraphics)); resources.Add(resource); }
    internal void Untrack(DeviceResource resource) => resources.Remove(resource);

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public bool IsSoftware { get; }
    /// <summary>Unique per device: caches built for another generation are rebuilt.</summary>
    public int Generation { get; }
    public bool IsDisposed => disposed;

    public ID3D11VertexShader VSMesh { get; }
    public ID3D11PixelShader PSPlain { get; }
    public ID3D11PixelShader PSTint { get; }
    public ID3D11PixelShader PSOverlay { get; }
    public ID3D11PixelShader PSPolygonZones { get; }
    public ID3D11PixelShader PSOrientation { get; }
    public ID3D11PixelShader PSColor { get; }
    public ID3D11PixelShader PSViewCube { get; }
    public ID3D11VertexShader VSLine { get; }
    public ID3D11GeometryShader GSLine { get; }
    public ID3D11PixelShader PSLine { get; }
    public ID3D11VertexShader VSGrid { get; }
    public ID3D11PixelShader PSGrid { get; }
    public ID3D11VertexShader VSLabel { get; }
    public ID3D11PixelShader PSLabel { get; }
    public ID3D11InputLayout MeshLayout { get; }
    public ID3D11InputLayout LineLayout { get; }
    public ID3D11InputLayout LabelLayout { get; }
    public ID3D11Buffer FrameBuffer { get; }
    public ID3D11Buffer ObjectBuffer { get; }
    public ID3D11BlendState AlphaBlend { get; }
    public ID3D11BlendState NoBlend { get; }
    public ID3D11DepthStencilState DepthLessWrite { get; }
    public ID3D11DepthStencilState DepthLessNoWrite { get; }
    public ID3D11DepthStencilState DepthLessEqualWrite { get; }
    public ID3D11DepthStencilState DepthLessEqualNoWrite { get; }
    public ID3D11DepthStencilState DepthNone { get; }
    public ID3D11RasterizerState GridRasterizer { get; }
    public ID3D11SamplerState SurfaceSampler { get; }
    public ID3D11SamplerState CubeSampler { get; }
    public ID3D11SamplerState LabelSampler { get; }
    /// <summary>Zeros read with stride 0: a missing normal, texture coordinate or colour stream.</summary>
    public ID3D11Buffer ZeroBuffer { get; }
    /// <summary>One identity instance: a node without instances draws once.</summary>
    public ID3D11Buffer IdentityInstance { get; }

    /// <summary>Front faces wind counterclockwise on screen; integer depth bias with clamp −1000.</summary>
    public ID3D11RasterizerState Rasterizer(CullMode cull, int depthBias, bool depthClip, bool scissor = true)
    {
        var mode = cull switch { CullMode.Front => D3DCull.Front, CullMode.Back => D3DCull.Back, _ => D3DCull.None };
        var key = (mode, depthBias, depthClip, scissor);
        if (!rasterizers.TryGetValue(key, out var state))
            rasterizers[key] = state = Device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid, CullMode = mode, FrontCounterClockwise = true, DepthBias = depthBias, DepthBiasClamp = -1000,
                SlopeScaledDepthBias = 0, DepthClipEnable = depthClip, ScissorEnable = scissor, MultisampleEnable = true
            });
        return state;
    }

    public void Write<T>(ID3D11Buffer buffer, in T value) where T : unmanaged
    {
        var mapped = Context.Map(buffer, MapMode.WriteDiscard);
        try { mapped.AsSpan<T>(1)[0] = value; }
        finally { Context.Unmap(buffer, 0); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Context.ClearState(); Context.Flush(); } catch (SharpGen.Runtime.SharpGenException) { }
        foreach (var resource in resources.ToArray()) resource.Dispose();
        resources.Clear();
        foreach (var state in rasterizers.Values) state.Dispose();
        rasterizers.Clear();
        foreach (var item in new IDisposable[] { VSMesh, PSPlain, PSTint, PSOverlay, PSPolygonZones, PSOrientation, PSColor, PSViewCube, VSLine, GSLine, PSLine,
            VSGrid, PSGrid, VSLabel, PSLabel, MeshLayout, LineLayout, LabelLayout, FrameBuffer, ObjectBuffer, AlphaBlend, NoBlend, DepthLessWrite, DepthLessNoWrite,
            DepthLessEqualWrite, DepthLessEqualNoWrite, DepthNone, GridRasterizer, SurfaceSampler, CubeSampler, LabelSampler, ZeroBuffer, IdentityInstance })
            item.Dispose();
        Context.Dispose();
        Device.Dispose();
    }

    internal sealed record CompiledShaders(byte[] VSMesh, byte[] PSPlain, byte[] PSTint, byte[] PSOverlay, byte[] PSPolygonZones, byte[] PSOrientation,
        byte[] PSColor, byte[] PSViewCube, byte[] VSLine, byte[] GSLine, byte[] PSLine, byte[] VSGrid, byte[] PSGrid, byte[] VSLabel, byte[] PSLabel);

    /// <summary>The embedded shader source.</summary>
    internal static string ShaderSource { get; } = ReadSource();
    private static string ReadSource()
    {
        using var stream = typeof(D3DGraphics).Assembly.GetManifestResourceStream("zStudio.Rendering.Direct3D.Scene.hlsl")
            ?? throw new InvalidOperationException("The 3D preview shaders are missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Compiles <paramref name="entry"/> of <paramref name="source"/> through the system shader compiler (d3dcompiler_47).</summary>
    internal static byte[] Compile(string source, string entry, string profile)
    {
        var result = Compiler.Compile(source, null!, null!, entry, "Scene.hlsl", profile, ShaderFlags.OptimizationLevel3, out var blob, out var errors);
        try
        {
            if (result.Failure || blob == null)
                throw new InvalidOperationException($"The 3D preview shader {entry} did not compile: {(errors == null ? result.ToString() : errors.AsString())}");
            return blob.AsBytes();
        }
        finally { blob?.Dispose(); errors?.Dispose(); }
    }

    /// <summary>Starts compiling the shaders in the background (a view does on creation, before its device is needed).</summary>
    internal static void WarmUp() { if (!ShaderCode.IsValueCreated) _ = Task.Run(() => ShaderCode.Value); }

    // Compiled once per process; a recovered device reuses the bytecode.
    private static readonly Lazy<CompiledShaders> ShaderCode = new(() =>
    {
        string s = ShaderSource;
        return new(Compile(s, "VSMesh", "vs_5_0"), Compile(s, "PSPlain", "ps_5_0"), Compile(s, "PSTint", "ps_5_0"), Compile(s, "PSOverlay", "ps_5_0"),
            Compile(s, "PSPolygonZones", "ps_5_0"), Compile(s, "PSOrientation", "ps_5_0"), Compile(s, "PSColor", "ps_5_0"), Compile(s, "PSViewCube", "ps_5_0"),
            Compile(s, "VSLine", "vs_5_0"), Compile(s, "GSLine", "gs_5_0"), Compile(s, "PSLine", "ps_5_0"), Compile(s, "VSGrid", "vs_5_0"),
            Compile(s, "PSGrid", "ps_5_0"), Compile(s, "VSLabel", "vs_5_0"), Compile(s, "PSLabel", "ps_5_0"));
    });
}
