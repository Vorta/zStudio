using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using Color = System.Windows.Media.Color;
using D3D9Format = Vortice.Direct3D9.Format;
using DxgiFormat = Vortice.DXGI.Format;
using Usage = Vortice.Direct3D9.Usage;
using PresentParameters = Vortice.Direct3D9.PresentParameters;
using SwapEffect = Vortice.Direct3D9.SwapEffect;

namespace zStudio.Rendering.Direct3D;

/// <summary>
/// The Direct3D backend of <see cref="RenderView"/>: a WPF element showing a D3DImage whose back buffer is a Direct3D 11 texture
/// shared with a Direct3D 9Ex surface. Rendering is driven by <see cref="CompositionTarget.Rendering"/> on demand while the
/// element is visible: a frame renders while one was requested and for six more frames after each request (stale-frame
/// workaround for the shared surface, and the tail hover refresh relies on). A lost device or front buffer rebuilds everything
/// (both devices) from retained data; after three recoveries without a frame in between the view fails instead. A resize after a
/// failure (the window resized, the preview reloaded) or a regained front buffer retries with a fresh budget, recreating the
/// device when the failed recovery left none or a removed one. A forced frame (a capture) renders while the view is hidden too.
/// </summary>
internal sealed class Direct3DView : IDisposable
{
    private const int MinimumUpdateCount = 6, MaximumRecoveries = 3;
    private readonly RenderView owner;
    private readonly Image image = new() { Stretch = Stretch.Fill };
    private readonly SceneRenderer scene = new();
    private readonly NavigationAids aids = new();
    private D3DImage? imageSource;
    private IDirect3D9Ex? d3d9;
    private IDirect3DDevice9Ex? device9;
    private IDirect3DTexture9? texture9;
    private IDirect3DSurface9? surface9;
    private ID3D11Texture2D? backBuffer, depthBuffer, staging;
    private ID3D11RenderTargetView? targetView;
    private ID3D11DepthStencilView? depthView;
    private int targetGeneration;
    private int requestedWidth, requestedHeight, bufferWidth, bufferHeight;
    private float renderWidth, renderHeight;
    private double dpiScale = 1;
    private bool updateRequested = true, hooked, busy, failed, disposed, ready, frontBufferAvailable = true;
    private int updateCounter, recoveries;
    private TimeSpan lastRenderingTime = TimeSpan.MinValue;

    public Direct3DView(RenderView owner)
    {
        this.owner = owner;
        Element = new HostElement(this) { Child = image };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        Element.Loaded += (_, _) => { UpdateDpi(); Hook(true); owner.RaiseLayoutChanged(); };
        Element.Unloaded += (_, _) => Hook(false);
        // A view loaded or resized while hidden sizes itself once shown (its resize is skipped only while unloaded).
        Element.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) owner.RaiseLayoutChanged(); InvalidateRender(); };
    }

    /// <summary>The focusable input surface (DIPs).</summary>
    public HostElement Element { get; }
    public event Action? Presented;
    public Exception? RenderException { get; private set; }
    /// <summary>Whether a frame was rendered since the device was attached: picking and projection are available.</summary>
    public bool IsReady => ready;
    public double DpiScale => dpiScale;
    public (int Width, int Height) PresentedPixelSize => backBuffer != null ? (bufferWidth, bufferHeight) : (0, 0);
    public string Status => $"IsRendering={hooked && Element.IsVisible && !failed && frontBufferAvailable}, Loaded={hooked}, Visible={Element.IsVisible}, IsBusy={busy}, Failed={failed}, FrontBuffer={frontBufferAvailable}, Buffer={bufferWidth}x{bufferHeight}";
    /// <summary>The surface size in DIPs as the projection uses it: the integral size the buffer was made for.</summary>
    public Size ProjectionSize => requestedWidth > 0 && requestedHeight > 0 ? new(requestedWidth, requestedHeight) : new(Math.Max(1, Element.ActualWidth), Math.Max(1, Element.ActualHeight));

    public void InvalidateRender() { updateRequested = true; updateCounter = 0; }

    private void Hook(bool on)
    {
        if (on == hooked || disposed && on) return;
        hooked = on;
        if (on) CompositionTarget.Rendering += OnRendering; else CompositionTarget.Rendering -= OnRendering;
        if (on) InvalidateRender();
    }

    private void UpdateDpi()
    {
        if (PresentationSource.FromVisual(Element) is { CompositionTarget: { } target })
            dpiScale = Math.Max(target.TransformToDevice.M11, target.TransformToDevice.M22);
    }
    internal void DpiChanged(DpiScale dpi)
    {
        dpiScale = Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
        if (requestedWidth > 0) Resize(requestedWidth, requestedHeight, force: true);
    }

    /// <summary>Sizes the back buffer to <paramref name="width"/> × <paramref name="height"/> DIPs (floor of each times the DPI scale, at least 2).</summary>
    public void Resize(int width, int height, bool force = false)
    {
        if (force || width != requestedWidth || height != requestedHeight || renderWidth != Math.Max(2, (float)(width * dpiScale)))
        {
            requestedWidth = width; requestedHeight = height;
            renderWidth = Math.Max(2, (float)(width * dpiScale)); renderHeight = Math.Max(2, (float)(height * dpiScale));
            ReleaseTargets();
        }
        if (failed) Retry();
        CreateTargets();
        InvalidateRender();
    }

    // The buffers exist as soon as the size and the device are known (the first frame follows), as the presented size reports.
    // A failure is a resize failure; the next frame tries again (and fails the view if it fails too).
    private void CreateTargets()
    {
        if (failed || !frontBufferAvailable || owner.Device?.Graphics is not { IsDisposed: false } graphics || renderWidth <= 10 || renderHeight <= 10) return;
        try { EnsureTargets(graphics); }
        catch (Exception error) when (IsDeviceLost(error, graphics)) { Recover(); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { ReleaseTargets(); owner.RaiseResizeFailed(error); }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == lastRenderingTime) return;
            lastRenderingTime = args.RenderingTime;
        }
        RenderFrame(force: false);
    }

    /// <summary>Renders a frame when one is due (or <paramref name="force"/>); false when no frame was rendered and presented.</summary>
    public bool RenderFrame(bool force)
    {
        // A hidden view renders only when forced (a capture): the panel holding it may be collapsed while its scene stays live.
        if (disposed || busy || failed || !force && !Element.IsVisible || owner.Device?.Graphics is not { IsDisposed: false } graphics || owner.Camera is not { } camera
            || renderWidth <= 10 || renderHeight <= 10 || !frontBufferAvailable || !force && !updateRequested && updateCounter >= MinimumUpdateCount) return false;
        busy = true;
        bool rendered = false;
        var timestamp = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        updateRequested = false; ++updateCounter;
        try
        {
            // Runs on every rendered frame before the camera is read for it (navigation, inertia, horizon, clipping…).
            owner.FrameUpdate?.Invoke(timestamp);
            if (owner.Camera is { } current) camera = current;
            try
            {
                EnsureTargets(graphics);
                // Draw into the shared surface only while WPF is not reading it.
                bool locked = imageSource != null && surface9 != null && imageSource.IsFrontBufferAvailable;
                if (locked) imageSource!.Lock();
                try
                {
                    Draw(graphics, camera);
                    if (locked) imageSource!.AddDirtyRect(new Int32Rect(0, 0, imageSource.PixelWidth, imageSource.PixelHeight));
                }
                finally { if (locked) imageSource!.Unlock(); }
                ready = true; rendered = true; recoveries = 0; RenderException = null;
            }
            catch (Exception error) when (IsDeviceLost(error, graphics)) { Recover(); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { Fail(error); }
        }
        finally { busy = false; }
        if (rendered) Presented?.Invoke();
        return rendered;
    }

    private void Draw(D3DGraphics g, RenderCamera camera)
    {
        var context = g.Context;
        context.ClearState();
        context.OMSetRenderTargets(targetView!, depthView);
        var background = owner.BackgroundColor;
        context.ClearRenderTargetView(targetView!, new Vortice.Mathematics.Color4(background.R / 255f, background.G / 255f, background.B / 255f, background.A / 255f));
        context.ClearDepthStencilView(depthView!, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1, 0);
        context.RSSetViewport(0, 0, renderWidth, renderHeight);
        context.RSSetScissorRect(0, 0, bufferWidth, bufferHeight);
        var view = camera.CreateViewMatrix();
        var projection = camera.CreateProjectionMatrix(renderWidth / renderHeight);
        var eye = RenderCamera.ToFloat(camera.Position);
        var frame = new FrameConstants
        {
            View = view, Projection = projection, ViewProjection = view * projection,
            Viewport = new(renderWidth, renderHeight, 1 / renderWidth, 1 / renderHeight), EyePosition = eye, DpiScale = (float)dpiScale
        };
        scene.Target = (targetView, depthView);
        scene.Render(g, owner, frame, eye, RenderCamera.ToFloat(camera.LookDirection));
        aids.Render(g, owner, camera, renderWidth, renderHeight, dpiScale, depthView!, renderWidth, renderHeight);
        context.ClearState();
        context.Flush();
    }

    private void EnsureTargets(D3DGraphics g)
    {
        int width = (int)Math.Floor(renderWidth), height = (int)Math.Floor(renderHeight);
        if (backBuffer != null && targetGeneration == g.Generation && width == bufferWidth && height == bufferHeight) return;
        ReleaseTargets();
        bufferWidth = width; bufferHeight = height;
        var shared = !g.IsSoftware;
        backBuffer = g.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = shared ? ResourceOptionFlags.Shared : ResourceOptionFlags.None
        });
        targetView = g.Device.CreateRenderTargetView(backBuffer);
        // 32-bit float depth with stencil, standard (not reversed) Z.
        depthBuffer = g.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.R32G8X24_Typeless,
            SampleDescription = new(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil
        });
        depthView = g.Device.CreateDepthStencilView(depthBuffer, new DepthStencilViewDescription(depthBuffer, DepthStencilViewDimension.Texture2D, DxgiFormat.D32_Float_S8X24_UInt));
        targetGeneration = g.Generation;
        if (shared) AttachSurface();
    }

    // The D3DImage shows the shared texture through a Direct3D 9Ex surface opened from its handle.
    private void AttachSurface()
    {
        if (backBuffer == null) return;
        if (device9 == null)
        {
            d3d9 = D3D9.Direct3DCreate9Ex();
            var parameters = new PresentParameters
            {
                Windowed = true, SwapEffect = SwapEffect.Discard, PresentationInterval = PresentInterval.Default,
                BackBufferWidth = 1, BackBufferHeight = 1, BackBufferFormat = D3D9Format.Unknown
            };
            try
            {
                device9 = d3d9.CreateDeviceEx(0, DeviceType.Hardware, IntPtr.Zero,
                    CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve, parameters);
            }
            catch { d3d9.Dispose(); d3d9 = null; throw; }
        }
        using var resource = backBuffer.QueryInterface<IDXGIResource>();
        var handle = resource.SharedHandle;
        if (handle == IntPtr.Zero) throw new InvalidOperationException("The 3D view's back buffer has no shared handle.");
        texture9 = device9.CreateTexture((uint)bufferWidth, (uint)bufferHeight, 1, Usage.RenderTarget, D3D9Format.A8R8G8B8, Pool.Default, ref handle);
        surface9 = texture9.GetSurfaceLevel(0);
        if (imageSource == null)
        {
            imageSource = new D3DImage();
            imageSource.IsFrontBufferAvailableChanged += FrontBufferChanged;
            image.Source = imageSource;
        }
        imageSource.Lock();
        try
        {
            // The software fallback keeps Remote Desktop sessions working.
            imageSource.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface9.NativePointer, true);
            imageSource.AddDirtyRect(new Int32Rect(0, 0, imageSource.PixelWidth, imageSource.PixelHeight));
        }
        finally { imageSource.Unlock(); }
    }

    // Screen saver, sleep, a locked or disconnected session: the front buffer goes and comes back; rebuild the surface then.
    private void FrontBufferChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        bool available = (bool)e.NewValue;
        if (available == frontBufferAvailable) return;
        frontBufferAvailable = available;
        ReleaseSurface(device: true);
        if (!available) return;
        // A regained front buffer is a fresh start: an earlier failure may have been the lost session.
        ReleaseTargets();
        Retry();
        InvalidateRender();
    }

    // A fresh start after a failure or a lost session: the recovery budget is reset, and a device left removed, or none left by
    // a recovery whose new device could not be created, is recreated.
    private void Retry()
    {
        failed = false; recoveries = 0;
        if (owner.Device is not { } device) return;
        bool lost;
        try { lost = device.Graphics is not { IsDisposed: false } graphics || graphics.Device.DeviceRemovedReason.Failure; }
        catch (SharpGenException) { lost = true; }
        if (lost) Recover();
    }

    private static bool IsDeviceLost(Exception error, D3DGraphics graphics)
    {
        if (error is not SharpGenException native) return false;
        uint code = (uint)native.ResultCode.Code;
        // DXGI_ERROR_DEVICE_REMOVED, _HUNG, _RESET, _ACCESS_LOST; D3DERR_DEVICELOST, _DEVICEREMOVED, _DEVICEHUNG.
        if (code is 0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0026 or 0x88760868 or 0x88760870 or 0x88760874) return true;
        try { return graphics.Device.DeviceRemovedReason.Failure; } catch (SharpGenException) { return true; }
    }

    // Rebuilds every device resource from retained CPU data on new Direct3D 11 and 9Ex devices (non-reentrant: see RenderDevice).
    // A device lost again before any frame rendered counts against the bound; past it the view fails.
    private void Recover()
    {
        ReleaseTargets();
        ReleaseSurface(device: true);
        if (++recoveries > MaximumRecoveries) { Fail(new InvalidOperationException(RenderDevice.RecoveryFailedMessage)); return; }
        try
        {
            if (owner.Device is not { } device) return;
            device.DisposeAllResources();
            device.Reinitialize();
            InvalidateRender();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        failed = true; RenderException = error;
        ReleaseTargets();
        owner.RaiseRenderFailed(error);
    }

    /// <summary>The device changed (attached, detached or recovered): targets are rebuilt on the next frame.</summary>
    public void DeviceChanged() { failed = false; ready = false; recoveries = 0; RenderException = null; ReleaseTargets(); CreateTargets(); InvalidateRender(); }

    /// <summary>Renders one frame now and reads it back; null when no frame could be rendered (never an older frame).</summary>
    public BitmapSource? RenderBitmap()
    {
        InvalidateRender();
        if (!RenderFrame(force: true) || owner.Device?.Graphics is not { IsDisposed: false } graphics || backBuffer == null || targetGeneration != graphics.Generation) return null;
        return Read(graphics);
    }

    /// <summary>The displayed back buffer, without rendering, resizing or moving the camera.</summary>
    public BitmapSource CapturePresented()
    {
        if (backBuffer == null || owner.Device?.Graphics is not { IsDisposed: false } graphics || targetGeneration != graphics.Generation)
            throw new InvalidOperationException("The 3D view has no presented frame.");
        return Read(graphics);
    }

    private BitmapSource Read(D3DGraphics g)
    {
        if (staging is not { } copy || copy.Description.Width != bufferWidth || copy.Description.Height != bufferHeight || targetGeneration != g.Generation)
        {
            staging?.Dispose();
            staging = copy = g.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)bufferWidth, Height = (uint)bufferHeight, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.B8G8R8A8_UNorm,
                SampleDescription = new(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
            });
        }
        g.Context.CopyResource(copy, backBuffer!);
        var mapped = g.Context.Map(copy, 0, MapMode.Read);
        var pixels = new byte[bufferWidth * bufferHeight * 4];
        try
        {
            for (int y = 0; y < bufferHeight; y++)
                System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer + (nint)(y * mapped.RowPitch), pixels, y * bufferWidth * 4, bufferWidth * 4);
        }
        finally { g.Context.Unmap(copy, 0); }
        var bitmap = BitmapSource.Create(bufferWidth, bufferHeight, 96, 96, PixelFormats.Bgra32, null, pixels, bufferWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A world point on the surface (DIPs), with the current camera.</summary>
    public Point Project(Vector3 point)
    {
        if (owner.Camera is not { } camera) return default;
        var size = ProjectionSize;
        var clip = Vector4.Transform(new Vector4(point, 1), camera.CreateViewMatrix() * camera.CreateProjectionMatrix(renderWidth > 0 ? renderWidth / renderHeight : size.Width / size.Height));
        double x = clip.X / clip.W, y = clip.Y / clip.W;
        return new((x + 1) / 2 * size.Width, (1 - y) / 2 * size.Height);
    }

    public bool HitViewCube(Point point, out Vector3 lookNormal)
    {
        lookNormal = default;
        return ready && owner.Camera is { } camera && NavigationAids.HitCube(owner, camera, point, renderWidth, renderHeight, dpiScale, out lookNormal);
    }

    private void ReleaseTargets()
    {
        ReleaseSurface(device: false);
        targetView?.Dispose(); targetView = null;
        depthView?.Dispose(); depthView = null;
        backBuffer?.Dispose(); backBuffer = null;
        depthBuffer?.Dispose(); depthBuffer = null;
        staging?.Dispose(); staging = null;
        targetGeneration = 0;
    }

    private void ReleaseSurface(bool device)
    {
        if (imageSource != null)
        {
            imageSource.Lock();
            try { imageSource.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); }
            finally { imageSource.Unlock(); }
        }
        surface9?.Dispose(); surface9 = null;
        texture9?.Dispose(); texture9 = null;
        if (device) { device9?.Dispose(); device9 = null; d3d9?.Dispose(); d3d9 = null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Hook(false);
        ReleaseTargets();
        ReleaseSurface(device: true);
        aids.Release();
        if (imageSource != null) { imageSource.IsFrontBufferAvailableChanged -= FrontBufferChanged; image.Source = null; imageSource = null; }
    }

    /// <summary>
    /// The view's WPF element: focusable; it consumes arrows and Page Up/Down (no camera motion: zStudio owns navigation) and the
    /// mouse's Back/Forward buttons, takes keyboard focus on any press zStudio leaves unhandled, and has no manipulation (touch
    /// promotes to mouse input).
    /// </summary>
    internal sealed class HostElement : Border
    {
        private readonly Direct3DView owner;
        public HostElement(Direct3DView owner)
        {
            this.owner = owner;
            Focusable = true; FocusVisualStyle = null; IsManipulationEnabled = false;
            Background = Brushes.Transparent; SnapsToDevicePixels = true;
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown) e.Handled = true;
            base.OnKeyDown(e);
        }
        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDown(e);
            Focus();
        }
        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2) { e.Handled = true; return; }
            base.OnMouseDown(e);
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { base.OnRenderSizeChanged(sizeInfo); owner.owner.RaiseLayoutChanged(); }
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); owner.DpiChanged(newDpi); owner.owner.RaiseLayoutChanged(); }
    }
}
