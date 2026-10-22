using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Vector2 = System.Numerics.Vector2;
using Color = System.Windows.Media.Color;
using HitTestResult = HelixToolkit.SharpDX.HitTestResult;
using Vector3 = System.Numerics.Vector3;

namespace zStudio.Rendering.Direct3D;

/// <summary>
/// One ray hit. <see cref="Tag"/> is the hit instance's index for an instanced node (one hit per hit instance, its nearest
/// triangle) and the triangle ordinal otherwise. <see cref="Point"/> is in world space; <see cref="Normal"/> is
/// normalize(cross(p1 − p0, p2 − p0)) of the transformed triangle (it follows the winding, not the camera);
/// <see cref="Triangle"/> holds the triangle's vertex indices.
/// </summary>
internal sealed record RenderHit(Node? Node, Vector3 Point, Vector3 Normal, double Distance, int? Tag, (int A, int B, int C)? Triangle);

/// <summary>Ray picking over a view's nodes. Hits come in node submission order (callers sort by distance).</summary>
internal interface IRayPicker
{
    /// <summary>
    /// Every hit of the ray from <paramref name="origin"/> (unit <paramref name="direction"/>) on the view's pickable nodes: shown,
    /// rendering and hit-test-visible nodes, transparent ones included; empty before the first frame.
    /// <paramref name="pointer"/> is the screen point the ray came from (DIPs).
    /// </summary>
    IReadOnlyList<RenderHit> HitTest(Vector3 origin, Vector3 direction, Point pointer = default);
}

/// <summary>
/// The 3D view: renders <see cref="Items"/> through <see cref="Camera"/> into a WPF-composited surface (<see cref="Element"/>,
/// the focusable element that takes the view's input, sized in DIPs; its back buffer is floor(width) × <see cref="DpiScale"/>
/// physical pixels). Rendering is on demand: <see cref="InvalidateRender"/> renders the next few frames, and
/// <see cref="FrameUpdate"/> runs once per rendered frame, before the camera is read for it.
/// <para>
/// Per frame: opaque meshes and lines in ascending render order (submission order among equals), then the ground grid, then
/// transparent meshes in submission order (sorting groups sort theirs), then top-most groups, the view cube and the axes, each
/// over a cleared depth buffer. 32-bit float depth, standard Z; no lights, shadows, MSAA or FXAA; background
/// <see cref="BackgroundColor"/>.
/// </para>
/// </summary>
internal sealed class RenderView : IRayPicker, INodeOwner, IDisposable
{
    private readonly FrameViewport viewport;
    private RenderCamera? camera;
    private RenderDevice? device;
    private RenderTexture? cubeTexture;
    private bool guidesVisible = true, disposed;
    private DispatcherOperation? resize;

    public RenderView()
    {
        viewport = new FrameViewport(this)
        {
            EnableSwapChainRendering = false,
            UseDefaultGestures = false,
            ZoomAroundMouseDownPoint = false,
            ZoomExtentsWhenLoaded = false,
            // The backend's own camera controller stays idle: zStudio owns all navigation. Its wheel zoom was reachable
            // only during Fly (unhandled there), when zStudio had turned it off; keep it off.
            IsZoomEnabled = false,
            ShowViewCube = true,
            ShowCoordinateSystem = true,
        };
        viewport.OITRenderMode = OITRenderType.None;
        viewport.EnableRenderOrder = true;
        viewport.CameraChanged += (_, _) => CameraChanged?.Invoke();
        viewport.RenderExceptionOccurred += (_, e) => { RenderFailed?.Invoke(e.Exception); e.Handled = true; };
        Items = new(this, viewport.Items);
    }

    /// <summary>The focusable WPF element showing the view and taking its input (focus, capture, cursor, pointer positions).</summary>
    public FrameworkElement Element => viewport;
    internal Viewport3DX HelixViewport => viewport;
    RenderView? INodeOwner.View => this;
    public NodeCollection Items { get; }
    public RenderCamera? Camera
    {
        get => camera;
        set { camera = value; viewport.Camera = value?.HelixCamera; }
    }
    /// <summary>Raised synchronously whenever the camera or one of its properties changes.</summary>
    public event Action? CameraChanged;
    public Color BackgroundColor { get => viewport.BackgroundColor; set => viewport.BackgroundColor = value; }
    /// <summary>Runs on each rendered frame (with the frame's timestamp) before the camera matrices are captured for it.</summary>
    public Action<TimeSpan>? FrameUpdate { get; set; }
    /// <summary>The surface was loaded, re-templated or resized (synchronously, before the coalesced buffer resize).</summary>
    public event Action? LayoutChanged;
    public event Action<Exception>? RenderFailed;
    public event Action<Exception>? ResizeFailed;
    public Exception? RenderException => viewport.RenderException;
    /// <summary>The graphics device and its shaders; null detaches them.</summary>
    public RenderDevice? Device { get => device; set { device = value; viewport.EffectsManager = value?.HelixEffects; } }
    /// <summary>Whether a frame has been prepared (picking and projection need it).</summary>
    public bool IsReady => viewport.RenderContext != null;
    /// <summary>Whether guide nodes (<see cref="Node.IsGuide"/>) are drawn: false for Hide UI.</summary>
    public bool GuidesVisible
    {
        get => guidesVisible;
        set { if (guidesVisible == value) return; guidesVisible = value; foreach (var node in Items) node.RefreshGuides(); }
    }

    public void InvalidateRender() => viewport.InvalidateRender();

    /// <summary>
    /// Resizes the back buffer to the surface at Normal dispatcher priority, coalescing requests; nothing while the surface is
    /// not loaded or visible or is under 1 × 1. Never moves the camera.
    /// </summary>
    public void QueueResize()
    {
        if (disposed || resize?.Status == DispatcherOperationStatus.Pending) return;
        // Helix defers the D3DImage resize at Background priority. A large scene's
        // continuous rendering can starve it, stretching the initial tiny buffer
        // across the viewport. Coalesce layout changes in the normal UI queue.
        resize = viewport.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            resize = null;
            if (disposed || !viewport.IsLoaded || !viewport.IsVisible || viewport.ActualWidth < 1 || viewport.ActualHeight < 1) return;
            try { viewport.RenderHost?.Resize((int)viewport.ActualWidth, (int)viewport.ActualHeight); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { ResizeFailed?.Invoke(ex); }
        });
    }
    public void CancelResize() { resize?.Abort(); resize = null; }

    /// <summary>A world point on the surface, in DIPs.</summary>
    public Point Project(Point3D point) => viewport.Project(point);
    /// <summary>The backend's pointer ray (P1: Helix's, which starts at the near plane and uses the last frame's matrices).</summary>
    public (Vector3 Origin, Vector3 Direction) UnProject(Point point) { var ray = viewport.UnProject(point); return (ray.Position, ray.Direction); }
    public IReadOnlyList<RenderHit> HitTest(Vector3 origin, Vector3 direction, Point pointer = default)
    {
        if (viewport.RenderContext is not { } context) return [];
        var hitContext = new HitTestContext(context, new Ray(origin, direction), new Vector2((float)pointer.X, (float)pointer.Y));
        List<HitTestResult> hits = [];
        foreach (var element in viewport.Items) element.HitTest(hitContext, ref hits);
        return Convert(hits);
    }
    /// <summary>Hits under a surface point along the backend's own pointer ray, nearest first (P1: <see cref="UnProject"/>'s ray).</summary>
    public IReadOnlyList<RenderHit> FindHits(Point point) => viewport.FindHits(point) is { } hits ? Convert(hits) : [];
    private static RenderHit[] Convert(IEnumerable<HitTestResult> hits) => [.. hits.Where(h => h.IsValid).Select(h => new RenderHit(Node.FromHelix(h.ModelHit), h.PointHit, h.NormalAtHit,
        h.Distance, h.Tag as int?, h.TriangleIndices is { } t ? (t.Item1, t.Item2, t.Item3) : null))];

    // View cube (Helix face order +Z, −Z, +X, −X, +Y, −Y; edge and corner boxes neither drawn nor hit) and axes.
    public bool ShowViewCube { get => viewport.ShowViewCube; set => viewport.ShowViewCube = value; }
    public bool ShowCoordinateSystem { get => viewport.ShowCoordinateSystem; set => viewport.ShowCoordinateSystem = value; }
    /// <summary>The cube's centre in relative surface coordinates (−1..1, Y up; default 0.8, −0.8) and its scale (default 1).</summary>
    public double ViewCubeHorizontalPosition { get => viewport.ViewCubeHorizontalPosition; set => viewport.ViewCubeHorizontalPosition = value; }
    public double ViewCubeVerticalPosition { get => viewport.ViewCubeVerticalPosition; set => viewport.ViewCubeVerticalPosition = value; }
    public double ViewCubeSize { get => viewport.ViewCubeSize; set => viewport.ViewCubeSize = value; }
    /// <summary>The six face images side by side (in face order), as BGRA.</summary>
    public RenderTexture? ViewCubeTexture { get => cubeTexture; set { cubeTexture = value; viewport.ViewCubeTexture = value?.HelixTexture; } }
    /// <summary>The cube viewport's side at scale 1, in DIPs (clamped inside the surface).</summary>
    public double ViewCubePixelSize => viewport.Template?.FindName("PART_ViewCube", viewport) is ScreenSpacedElement3D cube &&
        cube.SceneNode.RenderCore is HelixToolkit.SharpDX.Core.ScreenSpacedMeshRenderCore core ? core.Size : 100;
    /// <summary>The view direction a cube click at <paramref name="point"/> asks for (the hit face's look direction); false off the cube.</summary>
    public bool HitViewCube(Point point, out Vector3 lookNormal)
    {
        lookNormal = default;
        if (viewport.RenderContext == null || !viewport.ShowViewCube || viewport.Template?.FindName("PART_ViewCube", viewport) is not ScreenSpacedElement3D cube) return false;
        var screen = new Vector2((float)point.X, (float)point.Y);
        var ray = viewport.UnProject(screen);
        List<HitTestResult> hits = [];
        if (!cube.HitTest(new HitTestContext(viewport.RenderContext, ray, screen), ref hits) || hits.Count == 0) return false;
        lookNormal = hits[0].NormalAtHit;
        return true;
    }

    // Capture.
    /// <summary>Renders one frame now at the current size and returns it (view cube and axes included when shown).</summary>
    public BitmapSource? RenderBitmap() => viewport.RenderBitmap();
    /// <summary>The displayed back buffer, read without rendering, resizing or moving the camera.</summary>
    public BitmapSource CapturePresented()
    {
        var host = viewport.RenderHost;
        if (host?.EffectsManager is not { } effects || host.RenderBuffer?.BackBuffer?.Resource is not SharpDX.Direct3D11.Texture2D buffer)
            throw new InvalidOperationException("The 3D view has no presented frame.");
        using var stream = new MemoryStream();
        if (!HelixToolkit.SharpDX.Utilities.ScreenCapture.SaveWICTextureToBitmapStream(effects, buffer, stream))
            throw new InvalidOperationException("The presented frame could not be read.");
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit(); image.StreamSource = stream; image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze();
        return image;
    }
    /// <summary>Calls <paramref name="presented"/> after each presented frame until the result is disposed (needs the render host).</summary>
    public IDisposable ObservePresentedFrames(Action presented)
    {
        var host = viewport.RenderHost ?? throw new InvalidOperationException("The 3D view has no render host.");
        void Rendered(object? sender, EventArgs e) => presented();
        host.Rendered += Rendered;
        return new Subscription(() => host.Rendered -= Rendered);
    }
    private sealed class Subscription(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
    /// <summary>The render loop's state, for diagnostics.</summary>
    public string RenderStatus => viewport.RenderHost is { } host ? $"IsRendering={host.IsRendering}, IsBusy={host.IsBusy}" : "no render host";
    /// <summary>The presented back buffer's size in physical pixels (0 × 0 before the first frame).</summary>
    public (int Width, int Height) PresentedPixelSize => viewport.RenderHost?.RenderBuffer?.BackBuffer?.Resource is SharpDX.Direct3D11.Texture2D buffer
        ? (buffer.Description.Width, buffer.Description.Height) : (0, 0);
    /// <summary>Physical pixels per DIP applied to the back buffer, line widths and shader widths.</summary>
    public double DpiScale => viewport.RenderHost?.DpiScale ?? 1;

    // Backend-only shims (P1, Helix): nothing to do in an in-house backend.
    /// <summary>Settles the backend's own camera controller (no forces or animations left), changing no pose zStudio keeps.</summary>
    public void StopNativeCameraMotion(TimeSpan timestamp)
    {
        bool inertia = viewport.IsInertiaEnabled, rotation = viewport.IsRotationEnabled;
        try
        {
            // Helix exposes StopSpin but not its full controller. One update with zero
            // inertia drains pan/zoom/move/rotate forces; the caller restores the exact pose afterward.
            viewport.IsInertiaEnabled = false; viewport.IsRotationEnabled = true;
            if (camera != null) viewport.ChangeDirection(camera.LookDirection, camera.UpDirection, 0);
            viewport.DrainNavigation(timestamp);
        }
        finally { viewport.IsInertiaEnabled = inertia; viewport.IsRotationEnabled = rotation; }
    }
    /// <summary>Whether the backend installed its own mouse/keyboard gestures (zStudio owns all navigation input).</summary>
    public bool UsesBuiltInGestures => viewport.UseDefaultGestures || viewport.InputBindings.Count != 0;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; CancelResize(); viewport.Dispose();
    }

    // Helix updates inertia here, immediately before copying the camera to the
    // render context. Preparing in CameraChanged instead observes partial poses
    // and recursively changes projection while another property is still updating.
    private sealed class FrameViewport : Viewport3DX, IViewport3DX
    {
        private readonly RenderView owner;
        public FrameViewport(RenderView owner)
        {
            this.owner = owner;
            Loaded += (_, _) => owner.LayoutChanged?.Invoke();
        }
        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            // Touch acts as the mouse: WPF promotes touch the view leaves unhandled to mouse input. The base template's root
            // enables manipulation, which would capture touch for the base class's camera gestures and never promote it.
            if (VisualTreeHelper.GetChildrenCount(this) > 0 && VisualTreeHelper.GetChild(this, 0) is UIElement root) root.IsManipulationEnabled = false;
            owner.LayoutChanged?.Invoke();
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { base.OnRenderSizeChanged(sizeInfo); owner.LayoutChanged?.Invoke(); }
        void IViewport3DX.Update(TimeSpan timeStamp) { base.Update(timeStamp); owner.FrameUpdate?.Invoke(timeStamp); }
        public void DrainNavigation(TimeSpan timestamp) => base.Update(timestamp);
        // No built-in keyboard camera control (owner decision 2026-10-22): the base class turned, zoomed and moved the
        // camera on arrows, Page Up/Down and W/A/S/D/Z/Q. The keys it consumed stay consumed, so focus stays in the view.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown) e.Handled = true;
        }
        // Nor on the mouse's Back/Forward buttons (the base class restored its own camera history).
        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2) { e.Handled = true; return; }
            base.OnMouseDown(e);
        }
        // The base class's own touch handling (its pointer hit tests keyed to the touch device) stays out; promoted mouse input does the rest.
        protected override void OnTouchDown(TouchEventArgs e) { }
        protected override void OnTouchUp(TouchEventArgs e) { }
    }
}
