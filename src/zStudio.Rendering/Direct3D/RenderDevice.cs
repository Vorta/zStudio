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
    private readonly RecoveringEffectsManager? effects;
    private readonly bool software;
    private D3DGraphics? graphics;
    private int recovering;
    private bool disposed;
    private RenderDevice(RecoveringEffectsManager effects) => this.effects = effects;
    private RenderDevice(bool software) { this.software = software; graphics = D3DGraphics.Create(software); }
    internal DefaultEffectsManager? HelixEffects => effects;
    /// <summary>Direct3D backend: the current device (null between <see cref="DisposeAllResources"/> and <see cref="Reinitialize"/>).</summary>
    internal D3DGraphics? Graphics => graphics;

    /// <summary>A hardware device with the preview passes installed (released again when installation fails).</summary>
    public static RenderDevice Create() => RenderBackend.UseHelix
        ? PreviewResourceLifetime.Create(static () => new RenderDevice(new RecoveringEffectsManager()), static device => device.InstallPreviewShaders())
        : new RenderDevice(software: false);
    /// <summary>A software (WARP) device (tests of the recovery guard).</summary>
    public static RenderDevice CreateSoftware() => RenderBackend.UseHelix
        ? new(new RecoveringEffectsManager(new() { EnableSoftwareRendering = true }))
        : new RenderDevice(software: true);

    private void InstallPreviewShaders()
    {
        InstallGroundGrid(effects!);
        PreviewMaterial.InstallHelixPasses(effects!);
    }

    /// <summary>Releases the device and every device resource (the retained CPU data stays).</summary>
    public void DisposeAllResources()
    {
        if (effects != null) { ((IEffectsManager)effects).DisposeAllResources(); return; }
        Recover(() => { graphics?.Dispose(); graphics = null; });
    }
    /// <summary>Creates a new device and raises <see cref="Reinitialized"/>; resources rebuild from retained data on first use.</summary>
    public void Reinitialize()
    {
        if (effects != null) { ((IEffectsManager)effects).Reinitialize(); return; }
        Recover(() =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            graphics?.Dispose();
            graphics = D3DGraphics.Create(software);
            reinitialized?.Invoke(this, EventArgs.Empty);
        });
    }
    private void Recover(Action action)
    {
        if (Interlocked.CompareExchange(ref recovering, 1, 0) != 0) throw new InvalidOperationException(RecoveryFailedMessage);
        try { action(); }
        finally { Volatile.Write(ref recovering, 0); }
    }
    /// <summary>The error of a recovery refused while another runs, or of a view whose device keeps getting lost.</summary>
    internal const string RecoveryFailedMessage = "3D preview graphics recovery failed repeatedly. Check the active desktop session and graphics device, then retry the preview.";
    private EventHandler<EventArgs>? reinitialized;
    /// <summary>Raised after the device resources were rebuilt.</summary>
    public event EventHandler<EventArgs>? Reinitialized
    {
        add { if (effects != null) effects.Reinitialized += value; else reinitialized += value; }
        remove { if (effects != null) effects.Reinitialized -= value; else reinitialized -= value; }
    }
    /// <summary>The current native device's identity (null without one).</summary>
    public object? DeviceIdentity => effects != null ? effects.Device : graphics?.Device;
    public bool DeviceDisposed => effects != null ? effects.Device is not { IsDisposed: false } : graphics is not { IsDisposed: false };
    public void Dispose()
    {
        if (effects != null) { effects.Dispose(); return; }
        disposed = true; graphics?.Dispose(); graphics = null;
    }
    /// <summary>Whether <paramref name="error"/> is the graphics interop's own exception type.</summary>
    internal static bool IsInteropException(Exception error) => error is SharpDX.SharpDXException or SharpGen.Runtime.SharpGenException;

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
