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

namespace Recoil.Zbd.Desktop.Tests;

internal static class BlenderNavigationChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static async Task Run()
    {
        using var scene = new SceneViewport();
        var surface = (Viewport3DX)scene.Content;
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
        scene.RestoreView(initial); scene.ZoomAt(new(900, 600), 120); Assert.Equal(cornerZoom, scene.CaptureView());
        Near(target, cornerZoom.Position + cornerZoom.LookDirection);
        scene.RestoreView(initial); scene.DollyBy(4); Assert.Equal(initial.LookDirection, scene.CaptureView().LookDirection); Assert.Equal(6, scene.CaptureView().Position.Z);
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
        ScenePlacement[] placements = [new(1, 0, "child", mesh.Instances[0]), new(2, 0, "other", mesh.Instances[1])];
        foreach (string field in new[] { "placements", "visiblePlacements" })
            ((Dictionary<MeshGeometryModel3D, ScenePlacement[]>)typeof(SceneViewport).GetField(field, Fields)!.GetValue(scene)!).Add(mesh, placements);
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
                ("set", new() { ["position"] = new JsonArray(0, 0, 1), ["look"] = new JsonArray(0, 0, 0) }) })
            { var pose = scene.CaptureView(); await Call(action, args, "invalid_argument"); Assert.Equal(pose, scene.CaptureView()); }
            foreach (var (action, args) in new (string, JsonObject)[] { ("pan", new() { ["horizontal"] = 20, ["vertical"] = 10 }),
                ("zoom", new() { ["steps"] = 2 }), ("dolly", new() { ["distance"] = 4 }), ("rotate", new() { ["horizontal"] = 10 }) })
            { scene.RestoreView(initial); await Call(action, args); Assert.NotEqual(initial, scene.CaptureView()); }
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
