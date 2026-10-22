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
    private protected RenderCamera(HelixProjectionCamera camera) => HelixCamera = camera;
    internal HelixProjectionCamera HelixCamera { get; }
    public Point3D Position { get => HelixCamera.Position; set => HelixCamera.Position = value; }
    public Vector3D LookDirection { get => HelixCamera.LookDirection; set => HelixCamera.LookDirection = value; }
    public Vector3D UpDirection { get => HelixCamera.UpDirection; set => HelixCamera.UpDirection = value; }
    public double NearPlaneDistance { get => HelixCamera.NearPlaneDistance; set => HelixCamera.NearPlaneDistance = value; }
    public double FarPlaneDistance { get => HelixCamera.FarPlaneDistance; set => HelixCamera.FarPlaneDistance = value; }
    public Matrix4x4 CreateViewMatrix() => HelixCamera.CreateViewMatrix();
    /// <summary>Perspective: vertical <see cref="PerspectiveRenderCamera.FieldOfView"/>; orthographic: <see cref="OrthographicRenderCamera.Width"/> × Width/aspect. A NaN M33/M43 becomes −1.</summary>
    public Matrix4x4 CreateProjectionMatrix(double aspect) => HelixCamera.CreateProjectionMatrix(aspect);
    /// <summary>An independent copy with the same pose, planes and projection.</summary>
    public abstract RenderCamera Clone();

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
    public PerspectiveRenderCamera() : base(new PerspectiveBackend()) { }
    private PerspectiveRenderCamera(HelixProjectionCamera camera) : base(camera) { }
    /// <summary>Vertical field of view in degrees (default 45).</summary>
    public double FieldOfView { get => ((HelixPerspectiveCamera)HelixCamera).FieldOfView; set => ((HelixPerspectiveCamera)HelixCamera).FieldOfView = value; }
    public override RenderCamera Clone() => new PerspectiveRenderCamera((HelixProjectionCamera)HelixCamera.Clone());
}

internal sealed class OrthographicRenderCamera : RenderCamera
{
    public OrthographicRenderCamera() : base(new OrthographicBackend()) { }
    private OrthographicRenderCamera(HelixProjectionCamera camera) : base(camera) { }
    /// <summary>The view's width in world units.</summary>
    public double Width { get => ((HelixOrthographicCamera)HelixCamera).Width; set => ((HelixOrthographicCamera)HelixCamera).Width = value; }
    public override RenderCamera Clone() => new OrthographicRenderCamera((HelixProjectionCamera)HelixCamera.Clone());
}
