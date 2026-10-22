using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

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
    /// rendering and hit-test-visible mesh nodes, transparent ones included (line nodes, grids and the transform gizmo are never
    /// picked); empty before the first frame.
    /// </summary>
    IReadOnlyList<RenderHit> HitTest(Vector3 origin, Vector3 direction);
}

/// <summary>
/// The 3D view: renders <see cref="Items"/> through <see cref="Camera"/> into a WPF-composited surface (<see cref="Element"/>,
/// the focusable element that takes the view's input, sized in DIPs; its back buffer is floor(width) × <see cref="DpiScale"/>
/// physical pixels). Rendering is on demand: <see cref="InvalidateRender"/> renders at least the next six frames (hover and
/// inspection refresh rely on that tail), and <see cref="FrameUpdate"/> runs once per rendered frame, before the camera is read
/// for it.
/// <para>
/// Per frame: opaque meshes and lines in ascending render order (among equals horizon materials first, then submission order),
/// then the ground grid, then transparent meshes in submission order (sorting groups sort theirs),
/// then top-most groups, the view cube and the axes, each over a cleared depth buffer. 32-bit float depth, standard Z; no
/// lights, shadows, MSAA or FXAA; an 8-bit BGRA back buffer without sRGB conversion; background <see cref="BackgroundColor"/>.
/// </para>
/// </summary>
internal sealed class RenderView : IRayPicker, INodeOwner, IDisposable
{
    private static readonly List<WeakReference<RenderView>> views = [];
    private readonly Direct3DView direct;
    private RenderCamera? camera;
    private RenderDevice? device;
    private RenderTexture? cubeTexture;
    private Color background = Colors.Black;
    private bool guidesVisible = true, disposed, showCube = true, showAxes = true;
    private double cubeX = .8, cubeY = -.8, cubeSize = 1;
    private DispatcherOperation? resize;

    public RenderView()
    {
        D3DGraphics.WarmUp();
        direct = new Direct3DView(this);
        Items = new(this);
        lock (views) { views.RemoveAll(v => !v.TryGetTarget(out _)); views.Add(new(this)); }
    }

    /// <summary>The focusable WPF element showing the view and taking its input (focus, capture, cursor, pointer positions).</summary>
    public FrameworkElement Element => direct.Element;
    RenderView? INodeOwner.View => this;
    public NodeCollection Items { get; }
    public RenderCamera? Camera
    {
        get => camera;
        set
        {
            if (ReferenceEquals(camera, value)) return;
            if (camera != null) camera.Changed = null;
            camera = value;
            if (value != null) value.Changed = OnCameraChanged;
            OnCameraChanged();
        }
    }
    private void OnCameraChanged() { InvalidateRender(); CameraChanged?.Invoke(); }
    /// <summary>Raised synchronously whenever the camera or one of its properties changes.</summary>
    public event Action? CameraChanged;
    public Color BackgroundColor
    {
        get => background;
        set { if (background != value) InvalidateRender(); background = value; }
    }
    /// <summary>Runs on each rendered frame (with the frame's timestamp) before the camera matrices are captured for it.</summary>
    public Action<TimeSpan>? FrameUpdate { get; set; }
    /// <summary>The surface was loaded, shown, resized or moved to another DPI (synchronously, before the coalesced buffer resize).</summary>
    public event Action? LayoutChanged;
    internal void RaiseLayoutChanged() => LayoutChanged?.Invoke();
    public event Action<Exception>? RenderFailed;
    internal void RaiseRenderFailed(Exception error) => RenderFailed?.Invoke(error);
    public event Action<Exception>? ResizeFailed;
    internal void RaiseResizeFailed(Exception error) => ResizeFailed?.Invoke(error);
    public Exception? RenderException => direct.RenderException;
    /// <summary>The graphics device and its shaders; null detaches them.</summary>
    public RenderDevice? Device
    {
        get => device;
        set
        {
            if (ReferenceEquals(device, value)) return;
            if (device != null) device.Reinitialized -= DeviceReinitialized;
            device = value;
            if (value != null) value.Reinitialized += DeviceReinitialized;
            direct.DeviceChanged();
        }
    }
    private void DeviceReinitialized(object? sender, EventArgs e) => InvalidateRender();
    /// <summary>Retries a view whose rendering failed (fresh recovery budget, a new device when needed); nothing otherwise.</summary>
    public void RetryRendering() => direct.RetryIfFailed();
    /// <summary>Whether the surface has started (picking and projection need it).</summary>
    public bool IsReady => direct.IsReady;
    /// <summary>Whether guide nodes (<see cref="Node.IsGuide"/>) are drawn: false for Hide UI.</summary>
    public bool GuidesVisible
    {
        get => guidesVisible;
        set
        {
            if (guidesVisible == value) return;
            guidesVisible = value;
            InvalidateRender();
        }
    }

    public void InvalidateRender() => direct.InvalidateRender();
    /// <summary>A material or colour stream changed: every live view redraws (they share no state otherwise).</summary>
    internal static void InvalidateAll()
    {
        lock (views)
            foreach (var reference in views)
                if (reference.TryGetTarget(out var view) && !view.disposed) view.direct.InvalidateRender();
    }

    /// <summary>
    /// Resizes the back buffer to the surface at Normal dispatcher priority, coalescing requests; nothing while the surface is
    /// not loaded or is under 1 × 1 (a collapsed view keeps its last size; showing it raises <see cref="LayoutChanged"/>). Never
    /// moves the camera.
    /// </summary>
    public void QueueResize()
    {
        if (disposed || resize?.Status == DispatcherOperationStatus.Pending) return;
        // At Background priority a large scene's continuous rendering can starve the resize, stretching the initial tiny
        // buffer across the viewport. Coalesce layout changes in the normal UI queue.
        var element = Element;
        resize = element.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            resize = null;
            if (disposed || !element.IsLoaded || element.ActualWidth < 1 || element.ActualHeight < 1) return;
            try { direct.Resize((int)element.ActualWidth, (int)element.ActualHeight); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { ResizeFailed?.Invoke(ex); }
        });
    }
    public void CancelResize() { resize?.Abort(); resize = null; }

    /// <summary>A world point on the surface, in DIPs (with the current camera).</summary>
    public Point Project(Point3D point) => direct.Project(new((float)point.X, (float)point.Y, (float)point.Z));
    /// <summary>The pointer's ray: from the eye through the point (perspective) or from the view plane (orthographic), current camera.</summary>
    public (Vector3 Origin, Vector3 Direction) UnProject(Point point) => camera == null ? default : RayPicking.EyeRay(camera, point, direct.ProjectionSize);
    public IReadOnlyList<RenderHit> HitTest(Vector3 origin, Vector3 direction) => direct.IsReady ? RayPicking.HitTest(this, origin, direction) : [];
    /// <summary>Hits under a surface point along <see cref="UnProject"/>'s ray, nearest first.</summary>
    public IReadOnlyList<RenderHit> FindHits(Point point)
    {
        if (camera == null || !direct.IsReady) return [];
        var (origin, direction) = UnProject(point);
        return [.. RayPicking.HitTest(this, origin, direction).OrderBy(h => h.Distance)];
    }

    // View cube (face order +Z, −Z, +X, −X, +Y, −Y; edge and corner boxes neither drawn nor hit; a grey base ring around it is
    // drawn and hit, a ring hit asking for the top or bottom view) and axes (never hit). While the back buffer is under 100
    // physical pixels on an axis neither is drawn and the cube is not hit.
    public bool ShowViewCube { get => showCube; set { if (showCube != value) InvalidateRender(); showCube = value; } }
    public bool ShowCoordinateSystem { get => showAxes; set { if (showAxes != value) InvalidateRender(); showAxes = value; } }
    /// <summary>The cube's centre in relative surface coordinates (−1..1, Y up; default 0.8, −0.8) and its scale (default 1).</summary>
    public double ViewCubeHorizontalPosition { get => cubeX; set { if (cubeX != value) InvalidateRender(); cubeX = value; } }
    public double ViewCubeVerticalPosition { get => cubeY; set { if (cubeY != value) InvalidateRender(); cubeY = value; } }
    public double ViewCubeSize { get => cubeSize; set { if (cubeSize != value) InvalidateRender(); cubeSize = value; } }
    /// <summary>The six face images side by side (in face order), as BGRA.</summary>
    public RenderTexture? ViewCubeTexture
    {
        get => cubeTexture;
        set { if (!ReferenceEquals(cubeTexture, value)) InvalidateRender(); cubeTexture = value; }
    }
    /// <summary>The cube viewport's side at scale 1, in DIPs (clamped inside the surface).</summary>
    public const double ViewCubePixelSize = 100;
    /// <summary>The view direction a cube click at <paramref name="point"/> asks for (the hit face's look direction); false off the cube.</summary>
    public bool HitViewCube(Point point, out Vector3 lookNormal) => direct.HitViewCube(point, out lookNormal);

    // Capture.
    /// <summary>Renders one frame now at the current size and returns it (view cube and axes included when shown).</summary>
    public BitmapSource? RenderBitmap() => direct.RenderBitmap();
    /// <summary>The displayed back buffer, read without rendering, resizing or moving the camera.</summary>
    public BitmapSource CapturePresented() => direct.CapturePresented();
    /// <summary>Calls <paramref name="presented"/> after each presented frame until the result is disposed.</summary>
    public IDisposable ObservePresentedFrames(Action presented)
    {
        direct.Presented += presented;
        return new Subscription(() => direct.Presented -= presented);
    }
    private sealed class Subscription(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
    /// <summary>The render loop's state, for diagnostics.</summary>
    public string RenderStatus => direct.Status;
    /// <summary>The presented back buffer's size in physical pixels (0 × 0 before the first frame).</summary>
    public (int Width, int Height) PresentedPixelSize => direct.PresentedPixelSize;
    /// <summary>Physical pixels per DIP applied to the back buffer, line widths and shader widths.</summary>
    public double DpiScale => direct.DpiScale;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; CancelResize();
        cubeTexture?.ReleaseDeviceResources();
        Device = null; direct.Dispose();
        lock (views) views.RemoveAll(v => !v.TryGetTarget(out var target) || ReferenceEquals(target, this));
        if (camera != null) camera.Changed = null;
    }
}
