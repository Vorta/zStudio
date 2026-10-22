using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Shaders;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;

namespace zStudio.Rendering.Direct3D;

/// <summary>
/// The graphics device with the preview's shaders, shared by a view's materials, geometry and textures. Device loss (removed,
/// reset, hung, front buffer lost) rebuilds every device resource from retained CPU data; a recovery that fails while another is
/// running is refused ("3D preview graphics recovery failed repeatedly…") rather than nesting.
/// </summary>
internal sealed class RenderDevice : IDisposable
{
    private readonly RecoveringEffectsManager effects;
    private RenderDevice(RecoveringEffectsManager effects) => this.effects = effects;
    internal DefaultEffectsManager HelixEffects => effects;

    /// <summary>A hardware device with the preview passes installed (released again when installation fails).</summary>
    public static RenderDevice Create() => PreviewResourceLifetime.Create(static () => new RenderDevice(new RecoveringEffectsManager()), static device => device.InstallPreviewShaders());
    /// <summary>A software (WARP) device without the preview passes (tests of the recovery guard).</summary>
    public static RenderDevice CreateSoftware() => new(new RecoveringEffectsManager(new() { EnableSoftwareRendering = true }));

    private void InstallPreviewShaders()
    {
        InstallGroundGrid(effects);
        PreviewMaterial.InstallHelixPasses(effects);
    }

    // The backend calls these through IEffectsManager when it recovers the device.
    public void DisposeAllResources() => ((IEffectsManager)effects).DisposeAllResources();
    public void Reinitialize() => ((IEffectsManager)effects).Reinitialize();
    /// <summary>Raised after the device resources were rebuilt.</summary>
    public event EventHandler<EventArgs>? Reinitialized { add => effects.Reinitialized += value; remove => effects.Reinitialized -= value; }
    /// <summary>The current native device's identity (null without one).</summary>
    public object? DeviceIdentity => effects.Device;
    public bool DeviceDisposed => effects.Device is not { IsDisposed: false };
    public void Dispose() => effects.Dispose();
    /// <summary>Whether <paramref name="error"/> is the graphics interop's own exception type.</summary>
    internal static bool IsInteropException(Exception error) => error is SharpDX.SharpDXException;

    /// <summary>Bounds Helix's synchronous D3DImage/device recovery callbacks.</summary>
    private sealed class RecoveringEffectsManager : DefaultEffectsManager, IEffectsManager
    {
        private int recovering;
        internal RecoveringEffectsManager() { }
        internal RecoveringEffectsManager(EffectsManagerConfiguration configuration) : base(configuration) { }
        // Helix calls these through IEffectsManager. Its base methods are nonvirtual.
        // Guard disposal too: nested recovery disposes resources before reinitializing.
        void IEffectsManager.DisposeAllResources() => Recover(base.DisposeAllResources);
        void IEffectsManager.Reinitialize() => Recover(base.Reinitialize);
        private void Recover(Action action)
        {
            if (Interlocked.CompareExchange(ref recovering, 1, 0) != 0)
                throw new InvalidOperationException("3D preview graphics recovery failed repeatedly. Check the active desktop session and graphics device, then retry the preview.");
            try { action(); }
            finally { Volatile.Write(ref recovering, 0); }
        }
    }

    private static readonly Lazy<(byte[] Vertex, byte[] Pixel)> GridShaders = new(() =>
    {
        using var stream = typeof(RenderDevice).Assembly.GetManifestResourceStream("zStudio.Rendering.GroundGrid.hlsl")
            ?? throw new InvalidOperationException("The ground grid shader is missing.");
        using var reader = new System.IO.StreamReader(stream);
        string source = reader.ReadToEnd();
        using var vertex = ShaderBytecode.Compile(source, "VSMain", "vs_5_0", ShaderFlags.OptimizationLevel3);
        using var pixel = ShaderBytecode.Compile(source, "PSMain", "ps_5_0", ShaderFlags.OptimizationLevel3);
        return (vertex.Bytecode.Data, pixel.Bytecode.Data);
    });

    // The plane-grid technique's pass: triangle strip of 4, alpha blend, depth LessEqual without writes, cull none,
    // depth bias 10 (clamp 1000), scissor on, no depth clip; the pixel shader writes saturate(z/w) as depth.
    private static void InstallGroundGrid(DefaultEffectsManager manager)
    {
        var shaders = GridShaders.Value;
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
