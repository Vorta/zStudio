using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using HelixToolkit;
using HelixToolkit.Wpf.SharpDX;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Rendering;
using Xunit;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;
using ProjectionCamera = HelixToolkit.Wpf.SharpDX.ProjectionCamera;

namespace Recoil.Zbd.Desktop.Tests;

internal static class BlenderNavigationChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static async Task Run()
    {
        using var scene = new SceneViewport();
        var surface = (Viewport3DX)scene.RenderSurface;
        scene.Measure(new(900, 600)); scene.Arrange(new(0, 0, 900, 600));
        Assert.False(surface.UseDefaultGestures);
        Assert.Empty(surface.InputBindings);
        var initial = new SceneViewport.ViewPose(new(0, 0, 10), new(0, 0, -10), new(0, 1, 0), 60);
        scene.RestoreView(initial);
        var target = initial.Position + initial.LookDirection;
        scene.SetProjection("orthographic");
        var orthographic = scene.CaptureView();
        Assert.Equal(2 * 10 * Math.Tan(Math.PI / 6) * 1.5, orthographic.OrthographicWidth!.Value, 8);
        scene.SetProjection("perspective"); Near(initial.Position, scene.CaptureView().Position);
        scene.RestoreView(orthographic); Assert.Equal(orthographic, scene.CaptureView());
        scene.PanBy(50, 30); Assert.Equal(orthographic.LookDirection, scene.CaptureView().LookDirection);
        Assert.True(scene.CaptureView().Position.X < orthographic.Position.X);
        Assert.True(scene.CaptureView().Position.Y > orthographic.Position.Y);
        foreach (string axis in new[] { "front", "back", "left", "right", "top", "bottom" })
        {
            scene.RestoreView(initial); scene.SetAxisView(axis);
            var pose = scene.CaptureView(); Assert.Equal("orthographic", pose.Projection); Assert.Equal(axis, pose.AxisView);
            Near(target, pose.Position + pose.LookDirection);
            Assert.Equal(0, Vector3D.DotProduct(pose.LookDirection, pose.UpDirection), 8);
            if (axis is "top" or "bottom") Assert.Equal(10, Math.Abs(pose.LookDirection.Y));
            scene.ZoomBy(2); Assert.Equal(pose.Position, scene.CaptureView().Position);
            Assert.True(scene.CaptureView().OrthographicWidth < pose.OrthographicWidth);
            scene.OppositeView(); Assert.Equal(-pose.LookDirection, scene.CaptureView().LookDirection);
            scene.RotateBy(10, 10); Assert.Equal("perspective", scene.CaptureView().Projection); Assert.Null(scene.CaptureView().AxisView);
            Assert.True(scene.CaptureView().UpDirection.Y > 0);
        }
        scene.SetProjection("orthographic"); scene.RotateBy(20, 10); Assert.Equal("orthographic", scene.CaptureView().Projection);
        scene.RestoreView(initial); scene.ZoomAt(new(0, 0), 120); var cornerZoom = scene.CaptureView();
        scene.RestoreView(initial); scene.ZoomAt(new(900, 600), 120); var oppositeZoom = scene.CaptureView();
        Assert.True(cornerZoom.Position.X < 0 && cornerZoom.Position.Y > 0);
        Assert.True(oppositeZoom.Position.X > 0 && oppositeZoom.Position.Y < 0);
        Assert.Equal(1.2, (cornerZoom.Position - initial.Position).Length, 8);
        Assert.Equal(initial.UpDirection, cornerZoom.UpDirection);
        Near(target, cornerZoom.OrbitPivot!.Value);
        CheckPointerAnchoring(scene, initial);
        scene.RestoreView(initial); scene.DollyBy(4); Assert.Equal(initial.LookDirection, scene.CaptureView().LookDirection); Assert.Equal(6, scene.CaptureView().Position.Z);
        CheckContinuousZoomAndPivot(scene, initial);
        CheckLiveModifiers(scene, initial);
        foreach (var (modifier, gesture) in new[]
        {
            (ModifierKeys.None, SceneViewport.NavigationGesture.Orbit), (ModifierKeys.Shift, SceneViewport.NavigationGesture.Pan),
            (ModifierKeys.Control, SceneViewport.NavigationGesture.Zoom), (ModifierKeys.Control | ModifierKeys.Shift, SceneViewport.NavigationGesture.Dolly)
        })
        {
            Assert.Equal(gesture, SceneViewport.Gesture(MouseButton.Middle, modifier));
            Assert.Equal(SceneViewport.NavigationGesture.None, SceneViewport.Gesture(MouseButton.Right, modifier));
            scene.RestoreView(initial); scene.BeginNavigationDrag(gesture, new(100, 100)); scene.MoveNavigationDrag(new(130, 120)); scene.EndNavigationDrag();
            Assert.NotEqual(initial, scene.CaptureView()); Assert.False(surface.IsMouseCaptured);
            scene.StopCameraMotion();
            Assert.Equal(default, (System.Windows.Vector)typeof(SceneViewport).GetField("rotationVelocity", Fields)!.GetValue(scene)!);
            Assert.Equal(default, (System.Windows.Vector)typeof(SceneViewport).GetField("navigationVelocity", Fields)!.GetValue(scene)!);
        }
        Assert.Equal(SceneViewport.NavigationGesture.None, SceneViewport.Gesture(MouseButton.Middle, ModifierKeys.Alt));
        scene.SetAxisView("top"); scene.ZoomBy(3); var flyEye = scene.CaptureView().Position;
        scene.SetFly(true); Assert.Equal("perspective", scene.CaptureView().Projection); Assert.Equal(flyEye, scene.CaptureView().Position);
        Assert.True(scene.CaptureView().UpDirection.Y > 0); scene.SetFly(false);
        CheckKeys();
        InstallGeometry(scene);
        scene.SetAxisView("front"); scene.SelectFramingNode(0);
        Assert.True(scene.TryFrame("selected")); Near(new(10, 0, 0), scene.CaptureView().Position + scene.CaptureView().LookDirection);
        Assert.Equal("front", scene.CaptureView().AxisView);
        Assert.True(scene.TryFrame("all")); Assert.True((scene.CaptureView().Position + scene.CaptureView().LookDirection).X > 40);
        var prior = scene.CaptureView(); Assert.False(scene.TryFrame("selected", 99)); Assert.Equal(prior, scene.CaptureView());
        await CheckProtocol(scene, initial);
        CheckInertiaHorizon();
    }
    private static void CheckPointerAnchoring(SceneViewport scene, SceneViewport.ViewPose initial)
    {
        var surface = (Viewport3DX)scene.RenderSurface;
        var point = new Point3D(2, 1, 0);
        foreach (string projection in new[] { "perspective", "orthographic" })
        {
            scene.RestoreView(initial with { Projection = projection, OrthographicWidth = projection == "orthographic" ? 20 : null });
            var before = scene.CaptureView(); var screen = Project(point);
            scene.ZoomBy(2, screen); var after = scene.CaptureView();
            Assert.True((Project(point) - screen).Length < .0001, "Zoom moved the pointed feature on screen");
            Assert.Equal(before.UpDirection, after.UpDirection);
            Assert.Equal(0, Vector3D.CrossProduct(before.LookDirection, after.LookDirection).Length, 8);
            Assert.True(after.Position.X > before.Position.X && after.Position.Y > before.Position.Y);
            scene.ZoomBy(-2, screen); Near(before.Position, scene.CaptureView().Position);
            if (projection == "orthographic")
            {
                Near(before.OrbitPivot!.Value, scene.CaptureView().OrbitPivot!.Value);
                Assert.Equal(before.OrthographicWidth!.Value, scene.CaptureView().OrthographicWidth!.Value, 8);
                foreach (var (width, steps) in new[] { (.001, 100d), (1e12, -100d) })
                {
                    scene.RestoreView(before with { OrthographicWidth = width }); var limit = scene.CaptureView();
                    scene.ZoomBy(steps, new(100, 100)); Assert.Equal(limit, scene.CaptureView());
                }
            }
        }
        foreach (string axis in new[] { "front", "back", "left", "right", "top", "bottom" })
        {
            scene.RestoreView(initial); scene.SetAxisView(axis); var before = scene.CaptureView();
            var screen = new Point(650, 200);
            Assert.True(scene.TryNavigationRay(screen, out var origin, out var direction));
            var anchor = origin + direction * 10; var projected = Project(anchor);
            scene.ZoomBy(1, screen); Assert.True((Project(anchor) - projected).Length < .0001);
            Assert.Equal(before.AxisView, scene.CaptureView().AxisView);
            Assert.Equal(before.LookDirection, scene.CaptureView().LookDirection);
        }
        scene.RestoreView(initial); scene.ZoomBy(2, new(-300, 900)); var clamped = scene.CaptureView();
        scene.RestoreView(initial); scene.ZoomBy(2, new(0, Math.BitDecrement(600))); Assert.Equal(clamped, scene.CaptureView());
        foreach (string projection in new[] { "perspective", "orthographic" })
        foreach (double length in new[] { .000001, .01, 1, 1000 })
        {
            var look = new Vector3D(-.5, -.4, -.75); look.Normalize();
            var pose = SceneViewport.UprightPose(initial with { Position = new(5305, 2879, 7446), LookDirection = look * length,
                Projection = projection, OrthographicWidth = projection == "orthographic" ? 20 : null });
            scene.RestoreView(pose);
            var camera = (ProjectionCamera)surface.Camera!;
            foreach (var matrix in new[] { camera.CreateViewMatrix(), ((ProjectionCamera)camera.Clone()).CreateViewMatrix() })
                Assert.True(Vector3.Distance(new(-matrix.M13, -matrix.M23, -matrix.M33), new((float)look.X, (float)look.Y, (float)look.Z)) < 1e-6,
                    "Rendered view direction lost precision with a short look vector at a map-scale position");
        }
        scene.RestoreView(initial);

        Point Project(Point3D p)
        {
            var camera = (ProjectionCamera)surface.Camera!;
            var matrix = camera.CreateViewMatrix() * camera.CreateProjectionMatrix(1.5);
            var clip = Vector4.Transform(new Vector4((float)p.X, (float)p.Y, (float)p.Z, 1), matrix);
            return new((clip.X / clip.W + 1) * 450, (1 - clip.Y / clip.W) * 300);
        }
    }
    private static void CheckLiveModifiers(SceneViewport scene, SceneViewport.ViewPose initial)
    {
        Point start = new(300, 200), finish = new(330, 220);
        var surface = (Viewport3DX)scene.RenderSurface;
        foreach (var (key, modifiers) in new[] { (Key.LeftShift, ModifierKeys.Shift), (Key.RightShift, ModifierKeys.Shift),
            (Key.LeftCtrl, ModifierKeys.Control), (Key.RightCtrl, ModifierKeys.Control),
            (Key.LeftShift, ModifierKeys.Control | ModifierKeys.Shift), (Key.RightCtrl, ModifierKeys.Control | ModifierKeys.Shift) })
        {
            scene.RestoreView(initial); scene.BeginNavigationDrag(SceneViewport.Gesture(MouseButton.Middle, modifiers), start);
            scene.MoveNavigationDrag(finish, modifiers); scene.EndNavigationDrag(); var first = scene.CaptureView();
            scene.RestoreView(initial); scene.BeginNavigationDrag(SceneViewport.NavigationGesture.Orbit, start);
            var before = scene.CaptureView(); Assert.True(scene.NavigationModifierKey(key, modifiers, start));
            Assert.Equal(before, scene.CaptureView());
            scene.MoveNavigationDrag(finish, modifiers); scene.EndNavigationDrag(); Assert.Equal(first, scene.CaptureView());
            Assert.False(surface.IsMouseCaptured);
        }
        foreach (string projection in new[] { "perspective", "orthographic" })
        {
            scene.RestoreView(initial with { Projection = projection, OrthographicWidth = projection == "orthographic" ? 20 : null });
            scene.BeginNavigationDrag(SceneViewport.NavigationGesture.Orbit, start); scene.MoveNavigationDrag(finish);
            foreach (var modifiers in new[] { ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Shift, ModifierKeys.Control,
                ModifierKeys.None, ModifierKeys.Alt, ModifierKeys.Shift, ModifierKeys.Windows, ModifierKeys.None })
            {
                var before = scene.CaptureView();
                scene.UpdateNavigationModifiers(modifiers, finish); Assert.Equal(before, scene.CaptureView());
                Assert.Equal(default, Velocity("rotationVelocity")); Assert.Equal(default, Velocity("navigationVelocity"));
                scene.MoveNavigationDrag(start, modifiers);
                if (modifiers is ModifierKeys.Alt or ModifierKeys.Windows) Assert.Equal(before, scene.CaptureView());
                else Assert.NotEqual(before.Position, scene.CaptureView().Position);
                Assert.Equal(default, Velocity(modifiers == ModifierKeys.None ? "navigationVelocity" : "rotationVelocity"));
                scene.MoveNavigationDrag(finish, modifiers);
            }
            var last = scene.CaptureView(); scene.UpdateNavigationModifiers(ModifierKeys.Shift, finish); scene.EndNavigationDrag();
            typeof(SceneViewport).GetMethod("PrepareCameraFrame", Fields)!.Invoke(scene, [TimeSpan.FromSeconds(1)]);
            Assert.Equal(last, scene.CaptureView()); // No last-mode movement means no release inertia.
        }
        scene.RestoreView(initial); scene.SetAxisView("top");
        scene.BeginNavigationDrag(SceneViewport.NavigationGesture.Pan, start); scene.MoveNavigationDrag(finish, ModifierKeys.Shift);
        scene.NavigationModifierKey(Key.LeftShift, ModifierKeys.None, finish); scene.MoveNavigationDrag(start, ModifierKeys.None);
        Assert.Equal("perspective", scene.CaptureView().Projection);
        scene.NavigationModifierKey(Key.RightShift, ModifierKeys.Shift, start); var orbit = scene.CaptureView();
        scene.MoveNavigationDrag(finish, ModifierKeys.Shift); Assert.NotEqual(orbit.Position, scene.CaptureView().Position);
        Assert.Equal(orbit.LookDirection, scene.CaptureView().LookDirection); scene.CancelNavigation();
        foreach (var routedEvent in new[] { UIElement.LostMouseCaptureEvent, Keyboard.LostKeyboardFocusEvent })
        {
            scene.RestoreView(initial); scene.BeginNavigationDrag(SceneViewport.NavigationGesture.Orbit, start); scene.MoveNavigationDrag(finish);
            if (routedEvent == Keyboard.LostKeyboardFocusEvent)
                surface.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, surface, null) { RoutedEvent = routedEvent });
            else surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = routedEvent });
            var canceled = scene.CaptureView(); scene.MoveNavigationDrag(start, ModifierKeys.Shift); scene.EndNavigationDrag();
            Assert.Equal(canceled, scene.CaptureView()); Assert.Equal(default, Velocity("rotationVelocity")); Assert.Equal(default, Velocity("navigationVelocity"));
        }
        scene.RestoreView(initial); scene.BeginNavigationDrag(SceneViewport.NavigationGesture.Orbit, start);
        scene.MoveNavigationDrag(finish, ModifierKeys.Shift); var reconciled = scene.CaptureView();
        scene.MoveNavigationDrag(start, ModifierKeys.Shift); Assert.NotEqual(reconciled.Position, scene.CaptureView().Position);
        var panVelocity = Velocity("navigationVelocity");
        Assert.True(scene.NavigationModifierKey(Key.LeftShift, ModifierKeys.Shift, start));
        Assert.True(scene.NavigationModifierKey(Key.RightShift, ModifierKeys.Shift, start));
        Assert.Equal(panVelocity, Velocity("navigationVelocity")); // Repeats/one Shift released while the other is held.
        Assert.False(scene.NavigationModifierKey(Key.A, ModifierKeys.Shift, start));
        Assert.Equal(panVelocity, Velocity("navigationVelocity"));
        scene.CancelNavigation(); scene.RestoreView(initial);
        System.Windows.Vector Velocity(string name) => (System.Windows.Vector)typeof(SceneViewport).GetField(name, Fields)!.GetValue(scene)!;
    }
    private static void CheckContinuousZoomAndPivot(SceneViewport scene, SceneViewport.ViewPose initial)
    {
        var surface = (Viewport3DX)scene.RenderSurface;
        scene.RestoreView(initial); scene.ZoomBy(100); var batch = scene.CaptureView();
        Assert.True(batch.Position.Z < 0, "Zoom must travel past the original target");
        Assert.Equal(.01, batch.LookDirection.Length, 10);
        Assert.Equal(-110, batch.Position.Z, 8);
        scene.RestoreView(initial);
        for (int i = 0; i < 400; i++) scene.ZoomBy(.25);
        Near(batch.Position, scene.CaptureView().Position); Near(batch.OrbitPivot!.Value, scene.CaptureView().OrbitPivot!.Value);
        double previous = scene.CaptureView().Position.Z;
        for (int i = 0; i < 100; i++) { scene.ZoomBy(1); Assert.Equal(1.2, previous - scene.CaptureView().Position.Z, 8); previous = scene.CaptureView().Position.Z; }
        scene.ZoomBy(-2); Assert.Equal(2.4, scene.CaptureView().Position.Z - previous, 8); Assert.True(scene.CaptureView().LookDirection.Z < 0);
        foreach (var gesture in new[] { SceneViewport.NavigationGesture.Pan, SceneViewport.NavigationGesture.Orbit })
        {
            scene.BeginNavigationDrag(gesture, new(100, 100)); scene.MoveNavigationDrag(new(100.01, 100.01)); scene.EndNavigationDrag(); scene.StopCameraMotion();
            var eye = scene.CaptureView().Position; scene.ZoomBy(1);
            Assert.Equal(1.2, (scene.CaptureView().Position - eye).Length, 8); Assert.Equal(10, scene.CaptureView().NavigationReferenceDistance);
        }
        scene.RestoreView(initial); typeof(SceneViewport).GetField("minimumClipDistance", Fields)!.SetValue(scene, 10d);
        scene.ZoomBy(100); Near(batch.Position, scene.CaptureView().Position);
        foreach (double distance in new[] { .000001, .001, .01 })
        {
            scene.RestoreView(initial with { LookDirection = new(0, 0, -distance) }); scene.ZoomBy(1);
            Assert.True(scene.CaptureView().Position.Z < initial.Position.Z);
            Assert.True(scene.CaptureView().LookDirection.Length >= .01);
            Near(scene.CaptureView().Position + scene.CaptureView().LookDirection, scene.CaptureView().OrbitPivot!.Value);
        }
        scene.RestoreView(initial); scene.ZoomBy(40);
        previous = scene.CaptureView().Position.Z;
        typeof(SceneViewport).GetField("navigationGesture", Fields)!.SetValue(scene, SceneViewport.NavigationGesture.Zoom);
        typeof(SceneViewport).GetField("navigationVelocity", Fields)!.SetValue(scene, new System.Windows.Vector(0, 200));
        typeof(SceneViewport).GetMethod("AdvanceNavigationInertia", Fields)!.Invoke(scene, [.04]);
        Assert.Equal(.24, previous - scene.CaptureView().Position.Z, 8); scene.StopCameraMotion();

        var pivot = new Point3D(2, 1, 0);
        foreach (string projection in new[] { "perspective", "orthographic" })
        {
            scene.RestoreView(initial with { Projection = projection, OrthographicWidth = projection == "orthographic" ? 20 : null });
            var before = scene.CaptureView(); Assert.True(scene.SetOrbitPivot(pivot));
            Assert.Equal(before.Position, scene.CaptureView().Position); Assert.Equal(before.LookDirection, scene.CaptureView().LookDirection);
            var screen = CameraPoint(scene.CaptureView(), pivot);
            double radius = (initial.Position - pivot).Length;
            for (int i = 0; i < 30; i++)
            {
                scene.RotateBy(13, i < 15 ? 18 : -18);
                var pose = scene.CaptureView(); Near(screen, CameraPoint(pose, pivot));
                Assert.Equal(radius, (pose.Position - pivot).Length, 8); Assert.Equal(pivot, pose.OrbitPivot);
                Assert.True(pose.UpDirection.Y > 0);
            }
            var picked = scene.CaptureView(); scene.RestoreView(initial); scene.RestoreView(picked); Assert.Equal(picked, scene.CaptureView());
            Assert.False(scene.PickOrbitPivot(new(-1, -1))); Assert.Equal(picked, scene.CaptureView());
            scene.PanBy(30, 20); var panned = scene.CaptureView(); Near(pivot + (panned.Position - picked.Position), panned.OrbitPivot!.Value);
            scene.DollyBy(3); var dollied = scene.CaptureView(); Near(panned.OrbitPivot!.Value + (dollied.Position - panned.Position), dollied.OrbitPivot!.Value);
            scene.SetAxisView("top"); var axis = scene.CaptureView(); Near(axis.Position + axis.LookDirection, axis.OrbitPivot!.Value);
        }
        scene.RestoreView(initial); Assert.True(scene.SetOrbitPivot(new(2, 1, 5)));
        Assert.Equal(initial.Position, scene.CaptureView().Position); Assert.Equal(5, scene.CaptureView().LookDirection.Length);
        Assert.Equal(10, scene.CaptureView().NavigationReferenceDistance);
        var inertial = scene.CaptureView();
        typeof(SceneViewport).GetField("rotationVelocity", Fields)!.SetValue(scene, new System.Windows.Vector(100, 20));
        typeof(SceneViewport).GetMethod("PrepareCameraFrame", Fields)!.Invoke(scene, [TimeSpan.FromSeconds(1)]);
        Assert.NotEqual(inertial.Position, scene.CaptureView().Position); Assert.Equal(inertial.OrbitPivot, scene.CaptureView().OrbitPivot);
        Assert.Equal((inertial.Position - inertial.OrbitPivot!.Value).Length, (scene.CaptureView().Position - inertial.OrbitPivot.Value).Length, 8);
        scene.StopCameraMotion();
        typeof(SceneViewport).GetProperty(nameof(SceneViewport.IsPickupDragging))!.SetValue(scene, true);
        var blocked = scene.CaptureView(); scene.ZoomBy(100); scene.RotateBy(10, 10); Assert.False(scene.PickOrbitPivot(new(100, 100)));
        Assert.Equal(blocked, scene.CaptureView());
        typeof(SceneViewport).GetProperty(nameof(SceneViewport.IsPickupDragging))!.SetValue(scene, false);
        Assert.False(surface.IsMouseCaptured);
        scene.RestoreView(initial);

        static Point3D CameraPoint(SceneViewport.ViewPose pose, Point3D point)
        {
            var forward = pose.LookDirection; forward.Normalize(); var right = Vector3D.CrossProduct(forward, pose.UpDirection); right.Normalize();
            var offset = point - pose.Position;
            return new(Vector3D.DotProduct(offset, right), Vector3D.DotProduct(offset, pose.UpDirection), Vector3D.DotProduct(offset, forward));
        }
    }
    private static void CheckInertiaHorizon()
    {
        using var scene = new SceneViewport();
        scene.Measure(new(900, 600)); scene.Arrange(new(0, 0, 900, 600));
        InstallGeometry(scene);
        var data = scene.PreviewScene!;
        data.Nodes[0] = data.Nodes[0] with { Name = "horizon", Class = "object3d",
            Data = new() { ["transform"] = new JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0) } };
        Assert.Single(MissionSceneContext.FindHorizons(data));
        typeof(SceneViewport).GetMethod("ConfigureHorizon", Fields)!.Invoke(scene, [data]);
        var mesh = ((List<MeshGeometryModel3D>)typeof(SceneViewport).GetField("meshes", Fields)!.GetValue(scene)!).Single();
        var prepare = typeof(SceneViewport).GetMethod("PrepareCameraFrame", Fields)!;
        foreach (string projection in new[] { "perspective", "orthographic" })
        foreach (var gesture in new[] { SceneViewport.NavigationGesture.Pan, SceneViewport.NavigationGesture.Zoom, SceneViewport.NavigationGesture.Dolly })
        {
            scene.RestoreView(new(new(0, 0, 20), new(0, 0, -20), new(0, 1, 0), 60, projection, projection == "orthographic" ? 40 : null));
            prepare.Invoke(scene, [TimeSpan.FromSeconds(1)]);
            Assert.Equal(new Vector3(10, 0, 20), mesh.Instances![0].Translation);
            typeof(SceneViewport).GetField("navigationGesture", Fields)!.SetValue(scene, gesture);
            typeof(SceneViewport).GetField("navigationVelocity", Fields)!.SetValue(scene, new System.Windows.Vector(300, 200));
            for (int i = 1; i <= 3; i++)
            {
                prepare.Invoke(scene, [TimeSpan.FromSeconds(1 + i * .02)]);
                var eye = scene.CaptureView().Position;
                var expected = new Vector3(10 + (float)eye.X, (float)eye.Y, (float)eye.Z);
                Assert.True(Vector3.Distance(expected, mesh.Instances![0].Translation) < 1e-5, $"{projection} {gesture}: horizon did not follow inertial camera movement");
            }
            scene.StopCameraMotion();
        }
    }
    private static void CheckKeys()
    {
        string[] expected = ["follow", "front", "orbitDown", "right", "orbitLeft", "projection", "orbitRight", "top", "orbitUp", "opposite"];
        for (int i = 0; i <= 9; i++) Assert.Equal(expected[i], MainWindow.CameraKeyAction(Key.NumPad0 + i, ModifierKeys.None));
        foreach (var (key, action) in new[] { (Key.NumPad1, "back"), (Key.NumPad3, "left"), (Key.NumPad7, "bottom"),
            (Key.NumPad2, "panDown"), (Key.NumPad4, "panLeft"), (Key.NumPad6, "panRight"), (Key.NumPad8, "panUp") })
            Assert.Equal(action, MainWindow.CameraKeyAction(key, ModifierKeys.Control));
        Assert.Equal("frameAll", MainWindow.CameraKeyAction(Key.Home, ModifierKeys.None));
        Assert.Equal("frameSelected", MainWindow.CameraKeyAction(Key.Decimal, ModifierKeys.None));
        Assert.Equal("zoomIn", MainWindow.CameraKeyAction(Key.Add, ModifierKeys.None));
        Assert.Equal("zoomOut", MainWindow.CameraKeyAction(Key.Subtract, ModifierKeys.None));
        Assert.Null(MainWindow.CameraKeyAction(Key.D1, ModifierKeys.None)); Assert.Null(MainWindow.CameraKeyAction(Key.NumPad1, ModifierKeys.Shift));
        foreach (DependencyObject control in new DependencyObject[] { new TextBox(), new Slider(), new ComboBox(), new Button(), new MenuItem(), new AnimationTimeline(), new DataGrid(), new TreeView() })
            Assert.True(MainWindow.CameraInputIsNative(control));
        Assert.False(MainWindow.CameraInputIsNative(new Grid()));
    }
    private static void InstallGeometry(SceneViewport scene)
    {
        var data = new GameScene();
        data.Nodes.Add(new(0, "parent", "Object3D", null, [], [1], new(), new()));
        data.Nodes.Add(new(1, "child", "Object3D", 0, [0], [], new(), new()));
        data.Nodes.Add(new(2, "other", "Object3D", 0, [], [], new(), new()));
        typeof(SceneViewport).GetProperty(nameof(SceneViewport.PreviewScene))!.SetValue(scene, data);
        var geometry = new MeshGeometry3D { Positions = new Vector3Collection([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)]), Indices = new IntCollection([0, 1, 2]) };
        var mesh = new MeshGeometryModel3D { Geometry = geometry, Instances = [Matrix4x4.CreateTranslation(10, 0, 0), Matrix4x4.CreateTranslation(100, 0, 0)] };
        ((List<MeshGeometryModel3D>)typeof(SceneViewport).GetField("meshes", Fields)!.GetValue(scene)!).Add(mesh);
        ((Dictionary<MeshGeometry3D, (Vector3 Min, Vector3 Max)>)typeof(SceneViewport).GetField("staticMeshBounds", Fields)!.GetValue(scene)!).Add(geometry, (new(-1, -1, 0), new(1, 1, 0)));
        ScenePlacement[] placements = [new(1, 0, "child", mesh.Instances[0]), new(2, 0, "other", mesh.Instances[1])];
        foreach (string field in new[] { "placements", "visiblePlacements" })
            ((Dictionary<MeshGeometryModel3D, ScenePlacement[]>)typeof(SceneViewport).GetField(field, Fields)!.GetValue(scene)!).Add(mesh, placements);
        var surface = typeof(SceneViewport).GetMethod("IsOrbitSurface", Fields)!;
        bool Eligible(MeshGeometryModel3D candidate, int instance) => (bool)surface.Invoke(scene, [candidate, instance])!;
        Assert.True(Eligible(mesh, 0)); Assert.True(Eligible(mesh, 1)); Assert.False(Eligible(mesh, 2));
        using var helper = new MeshGeometryModel3D { Geometry = geometry }; Assert.False(Eligible(helper, 0));
        mesh.IsDepthClipEnabled = false; Assert.False(Eligible(mesh, 0)); mesh.IsDepthClipEnabled = true;
        mesh.Visibility = Visibility.Collapsed; Assert.False(Eligible(mesh, 0)); mesh.Visibility = Visibility.Visible;
        mesh.IsRendering = false; Assert.False(Eligible(mesh, 0)); mesh.IsRendering = true;
        mesh.IsHitTestVisible = false; Assert.False(Eligible(mesh, 0)); mesh.IsHitTestVisible = true;
        mesh.IsTransparent = true; Assert.True(Eligible(mesh, 0)); mesh.IsTransparent = false;
    }
    private static async Task CheckProtocol(SceneViewport scene, SceneViewport.ViewPose initial)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        var source = new ZbdDocument("camera-fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty);
        using var doc = new DocumentModel(source); main.ViewModel.Documents.Add(doc);
        Set("shownDocument", doc); Set("scene", scene); Set("shownAsset", source.Add(AssetKind.World, 0, "Whole world", 0, 0));
        ((FrameworkElement)main.FindName("SceneHost")).Visibility = Visibility.Visible;
        ((FrameworkElement)main.FindName("EmptyPreview")).Visibility = Visibility.Collapsed;
        var preview = (Guid)typeof(MainWindow).GetField("previewId", Fields)!.GetValue(main)!;
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            scene.RestoreView(initial); await Call("view", new() { ["view"] = "top" }); Assert.Equal("top", scene.CaptureView().AxisView);
            var top = scene.CaptureView();
            var menu = (MenuItem)typeof(MainWindow).GetField("cameraNavigationMenu", Fields)!.GetValue(main)!;
            var menuItems = menu.Items.Cast<MenuItem>().ToDictionary(i => (string)i.Tag);
            Assert.Equal(new[] { "back", "bottom", "follow", "frameAll", "frameSelected", "front", "left", "opposite", "orthographic", "perspective", "right", "top" }, menuItems.Keys.Order());
            menuItems["front"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); var gui = scene.CaptureView(); Assert.Equal("front", gui.AxisView);
            scene.RestoreView(top); await Call("view", new() { ["view"] = "front" }); Assert.Equal(gui, scene.CaptureView());
            await Call("projection", new() { ["projection"] = "orthographic", ["width"] = .5 }); Assert.Equal(.5, scene.CaptureView().OrthographicWidth);
            foreach (string projection in new[] { "perspective", "orthographic", "perspective" })
            {
                var request = new JsonObject { ["projection"] = projection, ["position"] = new JsonArray(12, 34, 56), ["look"] = new JsonArray(0, 0, -10) };
                if (projection == "orthographic") request["width"] = 20;
                await Call("set", request);
                Near(new(12, 34, 56), scene.CaptureView().Position); Assert.Equal(new Vector3D(0, 0, -10), scene.CaptureView().LookDirection);
            }
            foreach (var (action, args) in new (string, JsonObject)[] { ("projection", new() { ["projection"] = "orthographic", ["width"] = 0 }),
                ("zoom", new() { ["steps"] = 101 }), ("pan", new() { ["horizontal"] = 36001 }), ("dolly", new() { ["distance"] = 1e10 }),
                ("view", new() { ["view"] = "invalid" }), ("view", new()), ("projection", new()),
                ("rotate", new() { ["screenPoint"] = new JsonArray(1) }), ("rotate", new() { ["screenPoint"] = new JsonArray(1, 2, 3) }),
                ("rotate", new() { ["screenPoint"] = new JsonArray("x", 2) }), ("rotate", new() { ["screenPoint"] = new JsonArray(-1, 2) }),
                ("rotate", new() { ["screenPoint"] = new JsonArray(901, 2) }), ("pan", new() { ["screenPoint"] = new JsonArray(1, 2) }),
                ("zoom", new() { ["screenPoint"] = new JsonArray(1) }), ("zoom", new() { ["screenPoint"] = new JsonArray(901, 2) }),
                ("set", new() { ["position"] = new JsonArray(0, 0, 1), ["look"] = new JsonArray(0, 0, 0) }) })
            { var pose = scene.CaptureView(); await Call(action, args, "invalid_argument"); Assert.Equal(pose, scene.CaptureView()); }
            var notReady = scene.CaptureView();
            await Call("zoom", new() { ["screenPoint"] = new JsonArray(10, 10), ["steps"] = 1 }, "not_ready"); Assert.Equal(notReady, scene.CaptureView());
            foreach (var (action, args) in new (string, JsonObject)[] { ("pan", new() { ["horizontal"] = 20, ["vertical"] = 10 }),
                ("zoom", new() { ["steps"] = 2 }), ("dolly", new() { ["distance"] = 4 }), ("rotate", new() { ["horizontal"] = 10 }) })
            { scene.RestoreView(initial); await Call(action, args); Assert.NotEqual(initial, scene.CaptureView()); }
            scene.RestoreView(initial); scene.ZoomBy(100); var continuous = scene.CaptureView();
            scene.RestoreView(initial); var readback = await Call("zoom", new() { ["steps"] = 100 }); Assert.Equal(continuous, scene.CaptureView());
            Assert.NotNull(readback["OrbitPivot"]); Assert.Equal(10, readback["NavigationReferenceDistance"]!.GetValue<double>());
            await Call("frame", new() { ["target"] = "selected", ["node"] = 0 }); Near(new(10, 0, 0), scene.CaptureView().Position + scene.CaptureView().LookDirection);
            await Call("frame", new() { ["node"] = 99 }, "stale_record");
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.IsPickupDragging))!.SetValue(scene, true);
            foreach (string action in new[] { "pan", "zoom", "dolly", "view", "projection", "frame" }) await Call(action, new(), "busy");
            await Call("read", new());
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.IsPickupDragging))!.SetValue(scene, false);

            var package = new AnimationPackage { Prefix = new byte[72], Tail = [] }; package.Entries.Add(new(new byte[308], 0, 0));
            using var animationDoc = new DocumentModel(new ZbdDocument("animation-fixture", new(0, DateTime.MinValue), new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package });
            using var editor = new AnimationEditor(animationDoc, 0, new AssetResolver(Path.GetTempPath()), CancellationToken.None);
            Set("animation", editor);
            try
            {
                editor.ToggleCameraFollow(); Assert.True(editor.Options.FollowCamera);
                await Call("view", new() { ["view"] = "invalid" }, "invalid_argument"); Assert.True(editor.Options.FollowCamera);
                await Call("rotate", new() { ["screenPoint"] = new JsonArray(-1, 2) }, "invalid_argument"); Assert.True(editor.Options.FollowCamera);
                await Call("zoom", new() { ["screenPoint"] = new JsonArray(-1, 2) }, "invalid_argument"); Assert.True(editor.Options.FollowCamera);
                await Call("view", new() { ["view"] = "top" }); Assert.False(editor.Options.FollowCamera); Assert.Equal("top", editor.Viewport.CaptureView().AxisView);
                Assert.True(main.RunCameraNavigation("follow")); Assert.True(editor.Options.FollowCamera); Assert.Equal("perspective", editor.Viewport.CaptureView().Projection);
                await Call("zoom", new() { ["steps"] = 1 }); Assert.False(editor.Options.FollowCamera);
            }
            finally { Set("animation", null); }
            Assert.Equal(0, doc.Revision);

            async Task<JsonNode> Call(string action, JsonObject args, string? error = null)
            {
                args["action"] = action; args["preview"] = preview.ToString();
                var result = await client.CallToolAsync("zstudio_camera", args.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: deadline.Token);
                var data = JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
                if (error == null) Assert.False(result.IsError == true, data.ToJsonString()); else Assert.Equal(error, data["code"]!.GetValue<string>());
                return data;
            }
        }
        finally { typeof(SceneViewport).GetProperty(nameof(SceneViewport.IsPickupDragging))!.SetValue(scene, false); Set("scene", null); Set("animation", null); main.Close(); }
        void Set(string field, object? value) => typeof(MainWindow).GetField(field, Fields)!.SetValue(main, value);
    }
    private static void Near(Point3D expected, Point3D actual) => Assert.True((expected - actual).Length < 1e-7, $"Expected {expected}, actual {actual}");
}
