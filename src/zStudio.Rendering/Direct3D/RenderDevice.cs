namespace zStudio.Rendering.Direct3D;

/// <summary>
/// The graphics device with the preview's shaders, shared by a view's materials, geometry and textures. Device loss (removed,
/// reset, hung, front buffer lost) rebuilds every device resource from retained CPU data; a recovery that fails while another is
/// running is refused ("3D preview graphics recovery failed repeatedly…") rather than nesting.
/// </summary>
internal sealed class RenderDevice : IDisposable
{
    private readonly bool software;
    private D3DGraphics? graphics;
    private int recovering;
    private bool disposed;
    private RenderDevice(bool software) { this.software = software; graphics = D3DGraphics.Create(software); }
    /// <summary>The current device (null between <see cref="DisposeAllResources"/> and <see cref="Reinitialize"/>).</summary>
    internal D3DGraphics? Graphics => graphics;

    /// <summary>A hardware device on the adapter the window's surfaces use.</summary>
    public static RenderDevice Create() => new(software: false);
    /// <summary>A software (WARP) device (tests of the recovery guard).</summary>
    public static RenderDevice CreateSoftware() => new(software: true);

    /// <summary>Releases the device and every device resource (the retained CPU data stays).</summary>
    public void DisposeAllResources() => Recover(() => { graphics?.Dispose(); graphics = null; });
    /// <summary>Creates a new device and raises <see cref="Reinitialized"/>; resources rebuild from retained data on first use.</summary>
    public void Reinitialize() => Recover(() =>
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        graphics?.Dispose();
        graphics = D3DGraphics.Create(software);
        Reinitialized?.Invoke(this, EventArgs.Empty);
    });
    private void Recover(Action action)
    {
        if (Interlocked.CompareExchange(ref recovering, 1, 0) != 0) throw new InvalidOperationException(RecoveryFailedMessage);
        try { action(); }
        finally { Volatile.Write(ref recovering, 0); }
    }
    /// <summary>The error of a recovery refused while another runs, or of a view whose device keeps getting lost.</summary>
    internal const string RecoveryFailedMessage = "3D preview graphics recovery failed repeatedly. Check the active desktop session and graphics device, then retry the preview.";
    /// <summary>Raised after the device resources were rebuilt.</summary>
    public event EventHandler<EventArgs>? Reinitialized;
    /// <summary>The current native device's identity (null without one).</summary>
    public object? DeviceIdentity => graphics?.Device;
    public bool DeviceDisposed => graphics is not { IsDisposed: false };
    public void Dispose() { disposed = true; graphics?.Dispose(); graphics = null; }
    /// <summary>Whether <paramref name="error"/> is the graphics interop's own exception type.</summary>
    internal static bool IsInteropException(Exception error) => error is SharpGen.Runtime.SharpGenException;
}
