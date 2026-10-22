using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;
using HelixToolkit.SharpDX.Cameras;
using HelixProjectionCamera = HelixToolkit.Wpf.SharpDX.ProjectionCamera;
using HelixPerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using HelixOrthographicCamera = HelixToolkit.Wpf.SharpDX.OrthographicCamera;

namespace zStudio.Rendering.Direct3D;

/// <summary>
/// A right-handed camera with Direct3D 0..1 depth. The pose is kept in WPF doubles; the view matrix is built from the look
/// direction itself (<c>Matrix4x4.CreateLookTo</c>), never from a float target, so short look vectors keep their direction at
/// map-scale positions. Setting any property raises <see cref="RenderView.CameraChanged"/> synchronously on its view.
/// </summary>
internal abstract class RenderCamera
{
    private Point3D position;
    private Vector3D look = new(0, 0, -1), up = new(0, 1, 0);
    private double near = .001, far = 100;

    /// <param name="helix">The Helix adapter's camera (Helix backend only).</param>
    private protected RenderCamera(HelixProjectionCamera? helix) => HelixCamera = helix;
    /// <summary>The Helix adapter's camera, null in the Direct3D backend.</summary>
    internal HelixProjectionCamera? HelixCamera { get; }
    /// <summary>Direct3D backend: the view showing this camera, told about every change.</summary>
    internal Action? Changed { get; set; }
    private protected void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; Changed?.Invoke();
    }

    public Point3D Position
    {
        get => HelixCamera?.Position ?? position;
        set { if (HelixCamera is { } helix) helix.Position = value; else Set(ref position, value); }
    }
    public Vector3D LookDirection
    {
        get => HelixCamera?.LookDirection ?? look;
        set { if (HelixCamera is { } helix) helix.LookDirection = value; else Set(ref look, value); }
    }
    public Vector3D UpDirection
    {
        get => HelixCamera?.UpDirection ?? up;
        set { if (HelixCamera is { } helix) helix.UpDirection = value; else Set(ref up, value); }
    }
    public double NearPlaneDistance
    {
        get => HelixCamera?.NearPlaneDistance ?? near;
        set { if (HelixCamera is { } helix) helix.NearPlaneDistance = value; else Set(ref near, value); }
    }
    public double FarPlaneDistance
    {
        get => HelixCamera?.FarPlaneDistance ?? far;
        set { if (HelixCamera is { } helix) helix.FarPlaneDistance = value; else Set(ref far, value); }
    }
    public Matrix4x4 CreateViewMatrix() => HelixCamera is { } helix ? helix.CreateViewMatrix()
        : Matrix4x4.CreateLookTo(ToFloat(position), ToFloat(look), ToFloat(up));
    /// <summary>Perspective: vertical <see cref="PerspectiveRenderCamera.FieldOfView"/>; orthographic: <see cref="OrthographicRenderCamera.Width"/> × Width/aspect. A NaN M33/M43 becomes −1.</summary>
    public Matrix4x4 CreateProjectionMatrix(double aspect) => HelixCamera is { } helix ? helix.CreateProjectionMatrix(aspect) : Projection((float)aspect);
    private protected abstract Matrix4x4 Projection(float aspect);
    private protected float Near => (float)near;
    private protected float Far => (float)far;
    /// <summary>An independent copy with the same pose, planes and projection.</summary>
    public abstract RenderCamera Clone();
    private protected T CopyTo<T>(T copy) where T : RenderCamera
    {
        copy.position = position; copy.look = look; copy.up = up; copy.near = near; copy.far = far; return copy;
    }
    internal static Vector3 ToFloat(Point3D p) => new((float)p.X, (float)p.Y, (float)p.Z);
    internal static Vector3 ToFloat(Vector3D v) => new((float)v.X, (float)v.Y, (float)v.Z);

    // Helix 3.1.2 forms Position + LookDirection in float before constructing its
    // view matrix. Short look vectors at map-scale positions lose their direction.
    // Build from the direction itself, preserving navigation targets and scale.
    private static Matrix4x4 DirectionView(CameraCore camera) => camera.CreateLeftHandSystem
        ? Matrix4x4.CreateLookToLeftHanded(camera.Position, camera.LookDirection, camera.UpDirection)
        : Matrix4x4.CreateLookTo(camera.Position, camera.LookDirection, camera.UpDirection);
    private sealed class PerspectiveCore : PerspectiveCameraCore { public override Matrix4x4 CreateViewMatrix() => DirectionView(this); }
    private sealed class OrthographicCore : OrthographicCameraCore { public override Matrix4x4 CreateViewMatrix() => DirectionView(this); }
    private protected sealed class PerspectiveBackend : HelixPerspectiveCamera
    {
        protected override CameraCore CreatePortableCameraCore() => new PerspectiveCore();
        protected override Freezable CreateInstanceCore() => new PerspectiveBackend();
    }
    private protected sealed class OrthographicBackend : HelixOrthographicCamera
    {
        protected override CameraCore CreatePortableCameraCore() => new OrthographicCore();
        protected override Freezable CreateInstanceCore() => new OrthographicBackend();
    }
}

internal sealed class PerspectiveRenderCamera : RenderCamera
{
    private double fieldOfView = 45;
    public PerspectiveRenderCamera() : base(RenderBackend.UseHelix ? new PerspectiveBackend() : null) { }
    private PerspectiveRenderCamera(HelixProjectionCamera? camera) : base(camera) { }
    /// <summary>Vertical field of view in degrees (default 45).</summary>
    public double FieldOfView
    {
        get => HelixCamera is HelixPerspectiveCamera helix ? helix.FieldOfView : fieldOfView;
        set { if (HelixCamera is HelixPerspectiveCamera helix) helix.FieldOfView = value; else Set(ref fieldOfView, value); }
    }
    private protected override Matrix4x4 Projection(float aspect)
    {
        float fov = (float)((float)fieldOfView * Math.PI / 180);
        Matrix4x4 projection;
        try { projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, Near, Far); }
        catch (ArgumentOutOfRangeException) { projection = Degenerate(fov, aspect); }
        if (float.IsNaN(projection.M33) || float.IsNaN(projection.M43)) projection.M33 = projection.M43 = -1;
        return projection;
    }
    // Invalid planes or angles (the system method refuses them): the same formula without the checks.
    private Matrix4x4 Degenerate(float fov, float aspect)
    {
        float y = 1 / MathF.Tan(fov * .5f), range = Far / (Near - Far);
        return new(y / aspect, 0, 0, 0, 0, y, 0, 0, 0, 0, range, -1, 0, 0, range * Near, 0);
    }
    public override RenderCamera Clone() => HelixCamera is { } helix ? new PerspectiveRenderCamera((HelixProjectionCamera)helix.Clone())
        : CopyTo(new PerspectiveRenderCamera { fieldOfView = fieldOfView });
}

internal sealed class OrthographicRenderCamera : RenderCamera
{
    private double width = 10;
    public OrthographicRenderCamera() : base(RenderBackend.UseHelix ? new OrthographicBackend() : null) { }
    private OrthographicRenderCamera(HelixProjectionCamera? camera) : base(camera) { }
    /// <summary>The view's width in world units.</summary>
    public double Width
    {
        get => HelixCamera is HelixOrthographicCamera helix ? helix.Width : width;
        set { if (HelixCamera is HelixOrthographicCamera helix) helix.Width = value; else Set(ref width, value); }
    }
    private protected override Matrix4x4 Projection(float aspect)
    {
        float w = (float)width, h = w / aspect, farPlane = Math.Min(1e15f, Far);
        try { return Matrix4x4.CreateOrthographic(w, h, Near, farPlane); }
        catch (ArgumentOutOfRangeException)
        {
            float range = 1 / (Near - farPlane);
            return new(2 / w, 0, 0, 0, 0, 2 / h, 0, 0, 0, 0, range, 0, 0, 0, range * Near, 1);
        }
    }
    public override RenderCamera Clone() => HelixCamera is { } helix ? new OrthographicRenderCamera((HelixProjectionCamera)helix.Clone())
        : CopyTo(new OrthographicRenderCamera { width = width });
}
