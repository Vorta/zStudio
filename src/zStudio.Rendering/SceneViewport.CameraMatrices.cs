using System.Numerics;
using System.Windows;
using HelixToolkit.SharpDX.Cameras;
using HelixToolkit.Wpf.SharpDX;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    // Helix 3.1.2 forms Position + LookDirection in float before constructing its
    // view matrix. Short look vectors at map-scale positions lose their direction.
    // Build from the direction itself, preserving navigation targets and scale.
    private static Matrix4x4 DirectionView(CameraCore camera) => camera.CreateLeftHandSystem
        ? Matrix4x4.CreateLookToLeftHanded(camera.Position, camera.LookDirection, camera.UpDirection)
        : Matrix4x4.CreateLookTo(camera.Position, camera.LookDirection, camera.UpDirection);

    private sealed class NavigationPerspectiveCore : PerspectiveCameraCore
    {
        public override Matrix4x4 CreateViewMatrix() => DirectionView(this);
    }
    private sealed class NavigationOrthographicCore : OrthographicCameraCore
    {
        public override Matrix4x4 CreateViewMatrix() => DirectionView(this);
    }
    private sealed class NavigationPerspectiveCamera : PerspectiveCamera
    {
        protected override CameraCore CreatePortableCameraCore() => new NavigationPerspectiveCore();
        protected override Freezable CreateInstanceCore() => new NavigationPerspectiveCamera();
    }
    private sealed class NavigationOrthographicCamera : OrthographicCamera
    {
        protected override CameraCore CreatePortableCameraCore() => new NavigationOrthographicCore();
        protected override Freezable CreateInstanceCore() => new NavigationOrthographicCamera();
    }
}
