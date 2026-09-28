using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Rendering;
using ProjectionCamera = HelixToolkit.Wpf.SharpDX.ProjectionCamera;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;

internal static class BlenderNavigationCheck
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root);
        string output = Path.Combine(Path.GetTempPath(), "zstudio-blender-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        byte[]? previous = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            var main = (MainWindow)app.MainWindow; main.Width = 1550; main.Height = 940;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                await using var protocolHost = new LocalMcpHost(main.Commands, "test");
                await using var pipe = new NamedPipeClientStream(".", protocolHost.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(timeout.Token);
                await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: timeout.Token);
                await CheckPickingFixture();
                await main.ViewModel.OpenRootAsync(root);
                string worldPath = Path.Combine(root, "m1", "gamez.zbd"), animationPath = Path.Combine(root, "m1", "anim.zbd");
                byte[] worldHash = SHA256.HashData(File.ReadAllBytes(worldPath)), animationHash = SHA256.HashData(File.ReadAllBytes(animationPath));
                var doc = await main.ViewModel.OpenFileAsync(worldPath) ?? throw new InvalidDataException("Missing world");
                doc.SelectedAsset = doc.Assets.First(a => a.Record.Kind == AssetKind.World);
                await Preview(); var scene = (SceneViewport)((ContentControl)main.FindName("SceneHost")).Content;
                await CheckViews(scene, "world");
                var pickup = scene.Mission!.Actors.First(a => a.Pickup != null);
                ((ToggleButton)main.FindName("EditingUnlocked")).IsChecked = true;
                scene.SelectFramingNode(pickup.Root); scene.SelectPickup(pickup.Root, true, false);
                var child = scene.Mission.Scene.Nodes.First(n => scene.PickupAt(n.Index)?.Root == pickup.Root && scene.SelectInspectionNode(n.Index));
                var card = (SceneInspectionCard)scene.InspectionContent!;
                typeof(MainWindow).GetMethod("BeginInspectionEdit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [card]);
                scene.SetAxisView("top"); Require(scene.TryFrame("selected"), "Pickup framing unavailable"); await Task.Delay(200);
                var surface = (Viewport3DX)scene.RenderSurface;
                var gizmo = (TransformManipulator3D)typeof(SceneViewport).GetField("pickupManipulator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scene)!;
                Require(gizmo.Visibility == Visibility.Visible, "Orthographic pickup gizmo hidden");
                double gizmoSize = gizmo.SizeScale; scene.DollyBy(-50); await Task.Delay(150);
                Require(Math.Abs(gizmo.SizeScale - gizmoSize) < 1e-6, "Orthographic handle size depends on depth");
                Save(Presented(surface), Path.Combine(output, "pickup-top.png"));
                var point = surface.Project(new Point3D(scene.PickupPosition(pickup.Root).X, scene.PickupPosition(pickup.Root).Y, scene.PickupPosition(pickup.Root).Z));
                Require(double.IsFinite(point.X) && double.IsFinite(point.Y), "Pickup projection invalid");
                card.CancelDraft();
                Require(scene.SetOrbitPivot(scene.CaptureView().Position + scene.CaptureView().LookDirection * .8 + new Vector3D(1, 0, 2)), "World off-center pivot unavailable");
                var saved = scene.CaptureView();
                ((ComboBox)main.FindName("WorldDifficulty")).SelectedItem = MissionDifficulty.Hard;
                await Task.Delay(20); await Preview();
                scene = (SceneViewport)((ContentControl)main.FindName("SceneHost")).Content;
                Require(scene.CaptureView() == saved, "World difficulty lost orthographic camera state");

                doc.SelectedAsset = doc.Assets.First(a => a.Record.Kind == AssetKind.Model && doc.Document.Scene!.Models[a.Index].Vertices.Length > 0);
                await Preview(); scene = (SceneViewport)((ContentControl)main.FindName("SceneHost")).Content; await CheckViews(scene, "model");

                var animationDoc = await main.ViewModel.OpenFileAsync(animationPath) ?? throw new InvalidDataException("Missing animation");
                animationDoc.SelectedAsset = animationDoc.Assets.First(a => a.Name == "destroy_vtol1"); await Preview();
                var editor = (AnimationEditor)((ContentControl)main.FindName("AnimationHost")).Content;
                ((ToggleButton)editor.FindName("Mute")).IsChecked = true; await editor.SeekAsync(.5);
                var frame = editor.CurrentFrame; await CheckViews(editor.Viewport, "animation");
                Require(ReferenceEquals(frame, editor.CurrentFrame), "Navigation replaced the visible simulation frame");
                editor.Viewport.SetAxisView("top");
                Require(editor.Viewport.SetOrbitPivot(editor.Viewport.CaptureView().Position + editor.Viewport.CaptureView().LookDirection * .8 + new Vector3D(1, 0, 2)), "Animation off-center pivot unavailable");
                saved = editor.Viewport.CaptureView();
                await editor.SetPreviewOptionAsync("map", System.Text.Json.Nodes.JsonValue.Create(((ToggleButton)editor.FindName("ShowLevel")).IsChecked != true)!);
                Require(editor.Viewport.CaptureView() == saved, "Animation map refresh lost orthographic camera state");
                if (((ComboBox)editor.FindName("Lod")).Items.Count > 1)
                {
                    await editor.SetPreviewOptionAsync("lod", System.Text.Json.Nodes.JsonValue.Create(1)!);
                    Require(editor.Viewport.CaptureView() == saved, "Animation LOD lost orthographic camera state");
                }
                ((ToggleButton)editor.FindName("ShowLevel")).IsChecked = false;
                ((ToggleButton)editor.FindName("ShowLevel")).IsChecked = true;
                while (((Border)editor.FindName("LoadingPanel")).Visibility == Visibility.Visible) await Task.Delay(50, timeout.Token);
                Require(editor.Viewport.CaptureView() == saved, "Superseded map refresh lost orthographic camera state");
                main.Width = 1100; await Task.Delay(150); main.Width = 1550; await Task.Delay(150);
                Require(editor.Viewport.CaptureView() == saved, "Resize changed camera state");
                ((Button)editor.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(120); main.RunCameraNavigation("right"); Require(editor.IsPlaying, "View shortcut stopped playback"); editor.Pause();

                animationDoc.SelectedAsset = animationDoc.Assets.First(a => a.Name == "start_single_player"); await Preview();
                editor = (AnimationEditor)((ContentControl)main.FindName("AnimationHost")).Content;
                ((ToggleButton)editor.FindName("Mute")).IsChecked = true; await editor.SeekAsync(.1);
                main.RunCameraNavigation("follow"); Require(editor.Options.FollowCamera, "Numpad 0 equivalent failed to enable follow");
                Require(editor.CurrentFrame!.Camera != null, "Authored camera missing");
                double time = editor.CurrentFrame.Time;
                editor.Viewport.PanBy(20, 10); Require(!editor.Options.FollowCamera, "Manual pan did not exit follow");
                Require(editor.CurrentFrame.Time == time, "Manual navigation changed playhead");
                main.RunCameraNavigation("follow"); Require(editor.Options.FollowCamera, "Follow did not re-enable");
                main.RunCameraNavigation("top"); Require(!editor.Options.FollowCamera && editor.Viewport.CaptureView().AxisView == "top", "Axis view failed to leave follow");
                Require(worldHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(worldPath))) && animationHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(animationPath))), "Source bytes changed");
                Console.WriteLine("PASS: rendered world/model/animation surface-pivot orbit, continuous zoom, GUI/MCP parity, axis views, projection scale, idle stability, pickup handles, refresh/resize retention, playback and camera follow; unchanged sources.");
                Console.WriteLine(output);

                async Task Preview() => await ((Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).WaitAsync(timeout.Token);
                async Task CheckViews(SceneViewport view, string label)
                {
                    var viewport = (Viewport3DX)view.RenderSurface;
                    await CheckSurfaceNavigation(view, label);
                    Require(view.TryFrame("all"), label + " has no frameable geometry");
                    foreach (string axis in new[] { "front", "back", "left", "right", "top", "bottom" })
                    {
                        view.SetAxisView(axis); Require(view.TryFrame("all"), "Axis framing failed"); await Task.Delay(180, timeout.Token);
                        var pose = view.CaptureView(); var camera = (ProjectionCamera)viewport.Camera!;
                        Require(pose.AxisView == axis && pose.Projection == "orthographic", "Axis state changed on render");
                        var cubeBounds = view.NavigationCubeBounds;
                        var cubePoint = new Point(cubeBounds.Left + cubeBounds.Width / 2, cubeBounds.Top + cubeBounds.Height / 2);
                        Require(view.NavigateCubeAt(cubePoint) && view.CaptureView().AxisView == axis, "View-cube face disagrees with named view: " + axis);
                        Require(camera.NearPlaneDistance > 0 && camera.FarPlaneDistance > camera.NearPlaneDistance, "Invalid orthographic clipping");
                        var image = Presented(viewport); Save(image, Path.Combine(output, label + "-" + axis + ".png"));
                        await Task.Delay(150, timeout.Token);
                        Require(Pixels(image).SequenceEqual(Pixels(Presented(viewport))), "Idle axis image changed: " + label + " " + axis);
                        var center = pose.Position + pose.LookDirection;
                        var right = Vector3D.CrossProduct(pose.LookDirection, pose.UpDirection); right.Normalize();
                        var sample = center + right * (pose.OrthographicWidth!.Value * .1);
                        var before = viewport.Project(sample);
                        view.SetProjection("perspective"); await Task.Delay(100, timeout.Token);
                        var after = viewport.Project(sample);
                        Require((after - before).Length < 1, "Projection transition changed apparent scale");
                        view.SetAxisView(axis); view.RotateBy(10, 5); await Task.Delay(60, timeout.Token);
                        Require(view.CaptureView().Projection == "perspective" && view.CaptureView().UpDirection.Y > 0, "Axis orbit failed to return upright perspective");
                    }
                    view.SetProjection("orthographic"); view.RotateBy(20, 10); await Task.Delay(100, timeout.Token);
                    Require(view.CaptureView().Projection == "orthographic", "Explicit orthographic mode was not retained");
                }
                async Task CheckSurfaceNavigation(SceneViewport view, string label)
                {
                    var viewport = (Viewport3DX)view.RenderSurface;
                    var staticMeshes = label == "animation"
                        ? ((List<MeshGeometryModel3D>)typeof(SceneViewport).GetField("meshes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!).ToDictionary(mesh => mesh, mesh => mesh.Visibility)
                        : new Dictionary<MeshGeometryModel3D, Visibility>();
                    foreach (var mesh in staticMeshes.Keys) mesh.Visibility = Visibility.Collapsed;
                    if (label == "animation") Require(view.AnimationMeshCount > 0, "Animation test needs runtime geometry");
                    Require(view.TryFrame("asset"), label + " has no frameable asset");
                    view.SetProjection("perspective"); await Task.Delay(200, timeout.Token);
                    var before = view.CaptureView(); Point? point = null;
                    for (int y = 2; y < 9 && point == null; y++)
                    for (int x = 2; x < 9 && point == null; x++)
                    {
                        var sample = new Point(viewport.ActualWidth * x / 10, viewport.ActualHeight * y / 10);
                        if (view.PickOrbitPivot(sample)) point = sample;
                    }
                    Require(point != null, label + " has no pickable surface");
                    var picked = view.CaptureView(); var pivot = picked.OrbitPivot!.Value;
                    Require((before.Position - picked.Position).Length < 1e-8, "Picking moved the camera");
                    var a = before.LookDirection; a.Normalize(); var b = picked.LookDirection; b.Normalize();
                    Require((a - b).Length < 1e-8, "Picking recentered the camera");
                    await Task.Delay(100, timeout.Token);
                    var projected = viewport.Project(pivot); double radius = (picked.Position - pivot).Length;
                    // The back buffer rounds fractional DIP extents to physical pixels.
                    Require((projected - point!.Value).Length < 1, $"{label} picked wrong screen point: requested={point}, projected={projected}");
                    view.RotateBy(37, 19); await Task.Delay(100, timeout.Token);
                    var rotated = view.CaptureView();
                    Require((viewport.Project(pivot) - projected).Length < .1, label + " orbit moved the picked screen point");
                    Require(Math.Abs((rotated.Position - pivot).Length - radius) < 1e-6, "Orbit radius changed");
                    Require(rotated.OrbitPivot == pivot, "Orbit repicked its surface");
                    Save(Presented(viewport), Path.Combine(output, label + "-picked-orbit.png"));
                    // A pose change immediately followed by a pick must not use stale render matrices.
                    view.RestoreView(before);
                    bool immediateHit = view.PickOrbitPivot(point!.Value);
                    Require(immediateHit && (view.CaptureView().OrbitPivot!.Value - pivot).Length < .002,
                        $"Picking used stale camera matrices: hit={immediateHit}, expected={pivot}, actual={view.CaptureView().OrbitPivot}");
                    view.RestoreView(before); await Task.Delay(100, timeout.Token);
                    var state = (await main.Commands.ExecuteAsync("zstudio_state", new())).Data;
                    string id = state!["preview"]!.GetValue<string>();
                    var reply = await client.CallToolAsync("zstudio_camera", new Dictionary<string, object?> { ["preview"] = id, ["action"] = "rotate",
                        ["screenPoint"] = new[] { point!.Value.X, point.Value.Y }, ["horizontal"] = 37, ["vertical"] = 19 }, cancellationToken: timeout.Token);
                    Require(reply.IsError != true, string.Join(" ", reply.Content.OfType<TextContentBlock>().Select(c => c.Text)));
                    var remote = view.CaptureView();
                    Require((remote.Position - rotated.Position).Length < 1e-6 && (remote.OrbitPivot!.Value - pivot).Length < 1e-6, label + " GUI/MCP pivot mismatch");
                    var nearTarget = before with { LookDirection = a * .01 };
                    view.RestoreView(nearTarget);
                    Require(view.TryNavigationSurface(point.Value, out var speedSurface), "Zoom surface unavailable");
                    double expectedTravel = .12 * (speedSurface - nearTarget.Position).Length;
                    view.ZoomAt(point.Value, 120); var zoomed = view.CaptureView();
                    Require(Math.Abs((zoomed.Position - nearTarget.Position).Length - expectedTravel) < 1e-6, label + " zoom used the old target distance");
                    var towardsSurface = speedSurface - nearTarget.Position; towardsSurface.Normalize();
                    Require((zoomed.Position - (nearTarget.Position + towardsSurface * expectedTravel)).Length < Math.Max(1e-5, expectedTravel * 1e-6), "Zoom did not follow the pointer ray");
                    await Task.Delay(100, timeout.Token);
                    Require((viewport.Project(speedSurface) - point.Value).Length < 1,
                        $"{label} zoom moved the pointed feature on screen: requested={point}, actual={viewport.Project(speedSurface)}, surface={speedSurface}, pose={view.CaptureView()}, before={nearTarget}");
                    Require(Vector3D.DotProduct(zoomed.Position - (nearTarget.Position + nearTarget.LookDirection), a) > 0, label + " zoom stopped at original target");
                    view.RestoreView(nearTarget);
                    reply = await client.CallToolAsync("zstudio_camera", new Dictionary<string, object?> { ["preview"] = id, ["action"] = "zoom",
                        ["screenPoint"] = new[] { point.Value.X, point.Value.Y }, ["steps"] = 1 }, cancellationToken: timeout.Token);
                    Require(reply.IsError != true, string.Join(" ", reply.Content.OfType<TextContentBlock>().Select(c => c.Text)));
                    Require((view.CaptureView().Position - zoomed.Position).Length < 1e-5
                        && Math.Abs(view.CaptureView().NavigationReferenceDistance!.Value - zoomed.NavigationReferenceDistance!.Value) < 1e-5, label + " GUI/MCP pointed zoom mismatch");
                    view.RestoreView(before); view.SetProjection("orthographic"); await Task.Delay(100, timeout.Token);
                    var ortho = view.CaptureView();
                    var orthoPoint = new Point(viewport.ActualWidth * .65, viewport.ActualHeight * .35);
                    Require(view.TryNavigationRay(orthoPoint, out var origin, out var direction), "Orthographic ray unavailable");
                    var anchor = origin + direction * 10;
                    view.ZoomBy(2, orthoPoint); var orthoZoom = view.CaptureView(); await Task.Delay(100, timeout.Token);
                    Require((viewport.Project(anchor) - orthoPoint).Length < 1, label + " orthographic zoom lost its pointer anchor");
                    view.RestoreView(ortho);
                    reply = await client.CallToolAsync("zstudio_camera", new Dictionary<string, object?> { ["preview"] = id, ["action"] = "zoom",
                        ["screenPoint"] = new[] { orthoPoint.X, orthoPoint.Y }, ["steps"] = 2 }, cancellationToken: timeout.Token);
                    Require(reply.IsError != true && view.CaptureView() == orthoZoom, label + " orthographic GUI/MCP zoom mismatch");
                    view.RestoreView(remote); await Task.Delay(100, timeout.Token);
                    Require(view.CaptureView().OrbitPivot == remote.OrbitPivot, "Snapshot lost picked pivot");
                    foreach (var (mesh, visibility) in staticMeshes) mesh.Visibility = visibility;
                    Console.WriteLine($"PASS: {label} picked {pivot}, zoom crossed old target, command matches GUI");
                }
            }
            catch (Exception ex) { exit = 1; Console.Error.WriteLine(ex); Console.WriteLine(output); }
            finally { main.Close(); app.Shutdown(); }
        };
        try { app.Run(); } finally { if (previous != null) File.WriteAllBytes(settings, previous); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    internal static async Task CheckPickingFixture()
    {
        using var effects = new HelixToolkit.SharpDX.DefaultEffectsManager();
        using var view = new SceneViewport(); var viewport = (Viewport3DX)view.RenderSurface;
        viewport.EffectsManager = effects;
        var window = new Window { Content = view, Width = 900, Height = 650, Left = 20, Top = 20, ShowInTaskbar = false, ShowActivated = false };
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var geometry = new HelixToolkit.SharpDX.MeshGeometry3D
        {
            Positions = new HelixToolkit.Vector3Collection([new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)]),
            Indices = new HelixToolkit.IntCollection([0, 1, 2, 0, 2, 3])
        };
        var mesh = new MeshGeometryModel3D { Geometry = geometry, Material = new DiffuseMaterial(), CullMode = SharpDX.Direct3D11.CullMode.None,
            Instances = [System.Numerics.Matrix4x4.CreateTranslation(2, 1, 0), System.Numerics.Matrix4x4.CreateTranslation(2, 1, -3)], Transform = new TranslateTransform3D(1, 0, 0) };
        var helper = new MeshGeometryModel3D { Geometry = geometry, Material = new DiffuseMaterial(), Transform = new TranslateTransform3D(3, 1, 2) };
        var data = new GameScene();
        data.Nodes.Add(new(0, "front", "object3d", null, [], [], new(), new()));
        data.Nodes.Add(new(1, "rear", "object3d", null, [], [], new(), new()));
        typeof(SceneViewport).GetProperty(nameof(SceneViewport.PreviewScene))!.SetValue(view, data);
        ((List<MeshGeometryModel3D>)typeof(SceneViewport).GetField("meshes", fields)!.GetValue(view)!).Add(mesh);
        ScenePlacement[] placements = [new(0, 0, "front", mesh.Instances[0]), new(1, 0, "rear", mesh.Instances[1])];
        foreach (string name in new[] { "placements", "visiblePlacements" })
            ((Dictionary<MeshGeometryModel3D, ScenePlacement[]>)typeof(SceneViewport).GetField(name, fields)!.GetValue(view)!).Add(mesh, placements);
        typeof(SceneViewport).GetMethod("RegisterInspectionMesh", fields)!.Invoke(view, [mesh, -1, null, -1, -1]);
        viewport.Items.Add(mesh); viewport.Items.Add(helper); window.Show();
        try
        {
            var initial = new SceneViewport.ViewPose(new(0, 0, 12), new(0, 0, -12), new(0, 1, 0), 60);
            foreach (string projection in new[] { "perspective", "orthographic" })
            {
                view.RestoreView(initial with { Projection = projection, OrthographicWidth = projection == "orthographic" ? 20 : null });
                Point screen = default; SceneInspection? info = null;
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    await Task.Delay(25);
                    if (!view.IsOrbitPickingReady) continue;
                    screen = viewport.Project(new Point3D(3, 1, 0)); info = view.ProbeInspection(screen);
                    if (info is { Node: 0, Surface: not null } && (info.Surface.Value - new System.Numerics.Vector3(3, 1, 0)).Length() < .04f) break;
                }
                Require(info is { Node: 0, Surface: not null, Origin: not null } && (info.Surface.Value - new System.Numerics.Vector3(3, 1, 0)).Length() < .04f &&
                    (info.Origin.Value - new System.Numerics.Vector3(3, 1, 0)).Length() < .04f, "Inspection did not preserve transformed surface and instance origin");
                Require(view.SelectInspection(info!.Target), "Could not select inspected instance");
                Require(view.ProbeInspection(new(1, 1)) == null && view.SelectedInspection?.Target == info.Target, "Empty space changed pinned inspection");
                var visibleMap = (Dictionary<MeshGeometryModel3D, ScenePlacement[]>)typeof(SceneViewport).GetField("visiblePlacements", fields)!.GetValue(view)!;
                visibleMap[mesh] = [placements[1]]; mesh.Instances = [placements[1].Transform];
                Require(view.InspectTarget(info.Target)?.Active == false, "Isolation rebound the front target to the rear instance");
                visibleMap[mesh] = placements; mesh.Instances = placements.Select(p => p.Transform).ToArray();
                bool hit = view.PickOrbitPivot(screen);
                Require(hit && (view.CaptureView().OrbitPivot!.Value - new Point3D(3, 1, 0)).Length < .04,
                    $"Pick did not choose nearest transformed scene instance through a helper: {projection}, hit={hit}, pivot={view.CaptureView().OrbitPivot}, point={screen}, ready={view.IsOrbitPickingReady}");
                var picked = view.CaptureView();
                Require(!view.PickOrbitPivot(new(1, 1)) && view.CaptureView() == picked, "Empty-space pick changed the pivot");
                mesh.Visibility = Visibility.Collapsed; await Task.Delay(100);
                Require(!view.PickOrbitPivot(screen) && view.CaptureView() == picked, "Hidden geometry or helper supplied orbit pivot");
                mesh.Visibility = Visibility.Visible; mesh.IsDepthClipEnabled = false; await Task.Delay(100);
                Require(!view.PickOrbitPivot(screen), "Horizon supplied orbit pivot");
                mesh.IsDepthClipEnabled = true; mesh.IsTransparent = true; await Task.Delay(100);
                Require(view.PickOrbitPivot(screen), "Transparent scene triangles lost geometric picking");
                mesh.IsTransparent = false;
                // A press alone must not pick or change pan scale before Shift arrives.
                view.RestoreView(initial with { Projection = projection, OrthographicWidth = projection == "orthographic" ? 20 : null });
                var unpicked = view.CaptureView();
                view.BeginNavigationDrag(SceneViewport.NavigationGesture.Orbit, screen);
                Require(view.CaptureView() == unpicked, "Middle press changed camera/pivot before movement");
                view.NavigationModifierKey(System.Windows.Input.Key.RightShift, System.Windows.Input.ModifierKeys.Shift, screen);
                view.MoveNavigationDrag(screen + new Vector(20, 15), System.Windows.Input.ModifierKeys.Shift); view.EndNavigationDrag();
                var middleFirst = view.CaptureView();
                view.RestoreView(unpicked); view.BeginNavigationDrag(SceneViewport.NavigationGesture.Pan, screen);
                view.MoveNavigationDrag(screen + new Vector(20, 15)); view.EndNavigationDrag();
                Require(view.CaptureView() == middleFirst, "Modifier order changed pan with a rendered surface under the pointer");
                view.RestoreView(picked);
                view.BeginNavigationDrag(SceneViewport.NavigationGesture.Orbit, screen);
                view.MoveNavigationDrag(screen + new Vector(20, 15)); view.EndNavigationDrag(); await Task.Delay(60);
                Require(view.CaptureView().OrbitPivot == picked.OrbitPivot && !viewport.IsMouseCaptured, "Orbit inertia repicked or captured input");
                view.StopCameraMotion();
            }
            await CheckPointerZoom(view, mesh, initial);
            // Exercise world selection and rotation pointer math without any
            // physical input, CaptureMouse or native synthetic pointer events.
            typeof(SceneViewport).GetMethod("ConfigurePickups", fields)!.Invoke(view, []);
            await CheckLockedCubeInput(view, mesh);
            view.SetPickupLocked(true);
            Require(!view.SelectInspectionNode(0), "Locked world accepted a card");
            view.SetPickupLocked(false); Require(view.SelectedInspection == null, "Unlock selected an object");
            Require(view.SelectInspectionNode(0) && view.SelectionBoundsVisible && !view.TransformHandlesVisible, "Unlocked selection needs bounds without handles");
            view.RestoreView(new(new(10, 8, 12), new(-10, -8, -12), new(0, 1, 0), 60)); await Task.Delay(100);
            var source = new MissionPickupSource("fixture", 0, "puppies.zrd", 0);
            var authored = new PlacementTransform(System.Numerics.Vector3.Zero, new(.2f, -.3f, .1f));
            PlacementTransform? dragged = null;
            view.TransformDraftChanged += changed => dragged = changed;
            view.PreviewTransformDraft(source, authored, PlacementRotationKind.EulerRadians, "rotate", true);
            Require(view.TransformHandlesVisible, "Edit did not show rotation handles");
            foreach (var axis in new[] { System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ })
            {
                var radial = (axis == System.Numerics.Vector3.UnitY ? System.Numerics.Vector3.UnitZ : System.Numerics.Vector3.UnitY) * 2;
                var finish = System.Numerics.Vector3.Transform(radial, System.Numerics.Matrix4x4.CreateFromAxisAngle(axis, .2f));
                Point Project(System.Numerics.Vector3 v) => viewport.Project(new Point3D(v.X, v.Y, v.Z));
                typeof(SceneViewport).GetField("transformDragStart", fields)!.SetValue(view, authored);
                typeof(SceneViewport).GetMethod("BeginRotationPointer", fields)!.Invoke(view, [Project(radial), axis, radial]);
                typeof(SceneViewport).GetMethod("MoveRotationPointer", fields)!.Invoke(view, [Project(finish)]);
                Require(dragged != null, "Rotation pointer did not publish a draft");
                var expected = PlacementTransform.Orientation(PlacementRotationKind.EulerRadians, authored.RotateWorld(PlacementRotationKind.EulerRadians, axis, .2f).Rotation);
                var actual = PlacementTransform.Orientation(PlacementRotationKind.EulerRadians, dragged!.Value.Rotation);
                Require(Math.Abs(System.Numerics.Quaternion.Dot(expected, actual)) > .99999f, "Ring pointer changed the wrong rotation axis or angle");
            }
            view.EndTransformDraft(); Require(!view.TransformHandlesVisible && view.SelectionBoundsVisible, "Cancel did not hide handles while retaining selection");
            view.SetPickupLocked(true); Require(!view.SelectionBoundsVisible && view.SelectedInspection == null && !viewport.IsMouseCaptured, "Lock did not close selection");
            Console.WriteLine("PASS: rendered perspective/orthographic transformed instances, nearest surface, helper/horizon/hidden exclusions, empty-space fallback and orbit inertia");
        }
        finally { viewport.Items.Remove(helper); helper.Dispose(); window.Close(); }
    }
    private static async Task CheckLockedCubeInput(SceneViewport view, MeshGeometryModel3D mesh)
    {
        var viewport = (Viewport3DX)view.RenderSurface;
        var instances = mesh.Instances; var pose = view.CaptureView();
        var previousInspection = view.InspectionContent;
        var window = Window.GetWindow(view); double previousHeight = window.Height;
        var card = new SceneInspectionCard(view, _ => new JsonObject { ["Node"] = "Cube input fixture", ["Editable"] = false }, _ => { }, _ => { });
        view.InspectionContent = card;
        mesh.Instances = [System.Numerics.Matrix4x4.CreateScale(1000, 1000, 1) * System.Numerics.Matrix4x4.CreateTranslation(0, 0, -100)];
        try
        {
            foreach (double height in new[] { 650d, 300d })
            foreach (bool locked in new[] { true, false })
            foreach (double panelHeight in new[] { 216d, 432d, 2000d })
            {
                window.Height = height;
                card.SetPanelHeight(panelHeight);
                view.SetPickupLocked(true); view.SetPickupLocked(locked);
                view.SetAxisView("front"); view.SetProjection("perspective");
                view.RestoreView(view.CaptureView() with { AxisView = null, AutoPerspective = false }); await Task.Delay(150);
                var cubeBounds = view.NavigationCubeBounds;
                var cubePoint = new Point(cubeBounds.Left + cubeBounds.Width / 2, cubeBounds.Top + cubeBounds.Height / 2);
                Require(view.ProbeInspection(cubePoint) != null, "Cube regression needs geometry behind the cube");
                Require(view.CaptureView().AxisView == null, "Cube regression needs an unnamed perspective view");
                var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left);
                Require(view.HandleScenePointerDown(cubePoint, click) && view.CaptureView().AxisView == "front",
                    $"Scene input swallowed view-cube navigation while locked={locked}, height={height}, cube={cubeBounds}, axis={view.CaptureView().AxisView}");
                Require(view.SelectedInspection == null && !view.SelectionBoundsVisible && !viewport.IsMouseCaptured,
                    "Cube input selected geometry or captured the mouse");
                // Outside the cube the lock still consumes scene clicks without
                // opening a card; unlocking selects the same underlying object.
                var center = new Point(viewport.ActualWidth / 2, viewport.ActualHeight / 2);
                Require(view.HandleScenePointerDown(center, new(Mouse.PrimaryDevice, 0, MouseButton.Left)), "Scene click was not consumed");
                Require((view.SelectedInspection != null) == !locked && view.SelectionBoundsVisible == !locked,
                    "Cube input routing bypassed the scene selection lock");
                card.Refresh(); await Task.Delay(50);
                var panel = (Border)typeof(SceneInspectionCard).GetField("panel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(card)!;
                var panelBounds = new Rect(panel.TranslatePoint(new(), view), panel.RenderSize);
                Require(panelBounds.Height <= view.ActualHeight * .8 + .01, "Inspection panel exceeds 80% of the viewport");
                Require(!panelBounds.IntersectsWith(view.NavigationCubeBounds), "Pinned inspection panel overlaps the view cube");
                Require(Math.Abs(panelBounds.Top - 10) < .01 && Math.Abs(panelBounds.Right - (view.ActualWidth - 10)) < .01,
                    "Rendered inspection panel is not anchored at the top right");
                var currentCube = view.NavigationCubeBounds;
                var currentCubePoint = new Point(currentCube.Left + currentCube.Width / 2, currentCube.Top + currentCube.Height / 2);
                Require(view.InputHitTest(currentCubePoint) is DependencyObject input && !IsInPanel(input), "Pinned panel intercepts native cube input");
                var selected = view.SelectedInspection;
                Require(view.HandleScenePointerDown(currentCubePoint, new(Mouse.PrimaryDevice, 0, MouseButton.Left)) && view.CaptureView().AxisView == "front",
                    "Relocated cube lost its navigation hit target");
                Require(view.SelectedInspection == selected, "Relocated cube changed selection");

                bool IsInPanel(DependencyObject input)
                {
                    for (DependencyObject? current = input; current != null; current = VisualTreeHelper.GetParent(current))
                        if (ReferenceEquals(current, card)) return true;
                    return false;
                }
            }
            Console.WriteLine("PASS: shared pointer input prioritizes the view cube over geometry in locked/unlocked worlds, retaining the scene selection lock");
        }
        finally { view.SetPickupLocked(true); view.InspectionContent = previousInspection; window.Height = previousHeight; mesh.Instances = instances; view.RestoreView(pose); }
    }
    private static async Task CheckPointerZoom(SceneViewport view, MeshGeometryModel3D mesh, SceneViewport.ViewPose initial)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var viewport = (Viewport3DX)view.RenderSurface;
        mesh.Transform = Transform3D.Identity;
        mesh.Instances = [System.Numerics.Matrix4x4.CreateScale(30, 30, 1) * System.Numerics.Matrix4x4.CreateTranslation(0, 0, -100),
            System.Numerics.Matrix4x4.CreateScale(.2f, .2f, 1) * System.Numerics.Matrix4x4.CreateTranslation(-.5f, 0, 11)];
        initial = initial with { LookDirection = new(0, 0, -.01), NavigationReferenceDistance = 12 };
        view.RestoreView(initial); await Task.Delay(150);
        var far = viewport.Project(new Point3D(20, 0, -100)); var near = viewport.Project(new Point3D(-.5, 0, 11));
        var beforeQuery = view.CaptureView();
        Require(view.TryNavigationSurface(far, out var farHit) && Math.Abs(farHit.Z + 100) < .001, "Distant pointed surface unavailable");
        Require(view.CaptureView() == beforeQuery, "Surface query mutated the camera/reference");
        double farDistance = (farHit - initial.Position).Length;
        view.ZoomBy(.5, far); var farZoom = view.CaptureView();
        CheckTravel(initial, farZoom, .06 * farDistance, far);
        Require(farZoom.Position.Z < initial.Position.Z + initial.LookDirection.Z, "Tiny old target stopped far-surface zoom");
        view.RestoreView(initial);
        Require(view.TryNavigationSurface(near, out var nearHit) && Math.Abs(nearHit.Z - 11) < .001, "Near pointed surface unavailable");
        double nearDistance = (nearHit - initial.Position).Length;
        view.ZoomBy(.5, near); CheckTravel(initial, view.CaptureView(), .06 * nearDistance, near);
        Require(farDistance > nearDistance * 90, "Pointed near/far speed did not differ");
        view.ZoomBy(.5, far);
        Require(view.CaptureView().NavigationReferenceDistance > 100, "Moving pointer did not immediately refresh zoom speed");

        // Unrelated nearby geometry and the previous projection near plane must not set speed.
        var instances = mesh.Instances; mesh.Instances = [instances[0]]; view.RestoreView(initial); await Task.Delay(100);
        view.ZoomBy(.5, far); CheckTravel(initial, view.CaptureView(), .06 * farDistance, far);
        mesh.Instances = instances; view.RestoreView(initial); await Task.Delay(100);
        var camera = (ProjectionCamera)viewport.Camera!; double nearPlane = camera.NearPlaneDistance; camera.NearPlaneDistance = 500;
        view.ZoomBy(.5, far); CheckTravel(initial, view.CaptureView(), .06 * farDistance, far); camera.NearPlaneDistance = nearPlane;

        var empty = new Point(1, 1);
        Require(!view.TryNavigationSurface(empty, out _), "Empty-space fixture unexpectedly hit geometry");
        var retained = view.CaptureView(); view.ZoomBy(2.5, empty); CheckTravel(retained, view.CaptureView(), .3 * farDistance, empty);
        Require(view.CaptureView().NavigationReferenceDistance == retained.NavigationReferenceDistance, "Empty space changed zoom speed");
        var restored = view.CaptureView(); view.RestoreView(initial); view.RestoreView(restored); Require(view.CaptureView() == restored, "Snapshot lost zoom speed");
        view.ZoomBy(-.5, empty); CheckTravel(restored, view.CaptureView(), -.06 * farDistance, empty);

        foreach (var gesture in new[] { SceneViewport.NavigationGesture.Pan, SceneViewport.NavigationGesture.Orbit })
        {
            view.RestoreView(farZoom); double reference = view.CaptureView().NavigationReferenceDistance!.Value;
            view.BeginNavigationDrag(gesture, far); view.MoveNavigationDrag(far + new Vector(.01, .01)); view.EndNavigationDrag(); view.StopCameraMotion();
            Require(view.CaptureView().NavigationReferenceDistance == reference, "Tiny pan/orbit reset zoom reference");
            var pose = view.CaptureView(); Require(view.TryNavigationSurface(far, out var pointed), "Pointed surface lost after tiny gesture");
            view.ZoomBy(1, far); CheckTravel(pose, view.CaptureView(), .12 * (pointed - pose.Position).Length, far);
        }
        view.RestoreView(initial); view.ZoomBy(3.5, far); var batched = view.CaptureView();
        view.RestoreView(initial); view.ZoomBy(1, far); view.ZoomBy(1, far); view.ZoomBy(1, far); view.ZoomBy(.5, far);
        Require((batched.Position - view.CaptureView().Position).Length < 1e-6, "Batched zoom did not refresh surfaces between steps");
        view.RestoreView(initial); view.BeginNavigationDrag(SceneViewport.NavigationGesture.Zoom, near);
        var dragPoint = far + new Vector(0, 10); var dragStart = view.CaptureView();
        Require(view.TryNavigationSurface(dragPoint, out var dragHit), "Drag surface unavailable");
        view.MoveNavigationDrag(dragPoint); view.CancelNavigation();
        CheckTravel(dragStart, view.CaptureView(), .12 * (dragHit - dragStart.Position).Length * ((dragPoint.Y - near.Y) / 40), dragPoint);
        view.RestoreView(initial); view.ZoomBy(.25, far); var inertial = view.CaptureView();
        Require(view.TryNavigationSurface(far, out var inertiaHit), "Inertia surface unavailable");
        typeof(SceneViewport).GetField("navigationGesture", fields)!.SetValue(view, SceneViewport.NavigationGesture.Zoom);
        typeof(SceneViewport).GetField("navigationVelocity", fields)!.SetValue(view, new Vector(0, 200));
        typeof(SceneViewport).GetMethod("AdvanceNavigationInertia", fields)!.Invoke(view, [.04]);
        CheckTravel(inertial, view.CaptureView(), .024 * (inertiaHit - inertial.Position).Length, far); view.StopCameraMotion();
        view.RestoreView(initial);
        for (int i = 0; i < 120; i++) view.ZoomBy(1, far);
        Require(view.CaptureView().Position.Z < -100, "Repeated pointed zoom asymptotically stopped at the surface");
        Console.WriteLine("PASS: measured pointer-based near/far speed, unrelated close geometry, tiny pan/orbit, empty-space retention, clipping independence, drag/inertia sampling, batching and surface crossing");

        void CheckTravel(SceneViewport.ViewPose before, SceneViewport.ViewPose after, double travel, Point screen)
        {
            Require(view.TryNavigationRay(screen, out _, out var direction), "Zoom direction unavailable");
            Require((after.Position - (before.Position + direction * travel)).Length < 1e-6,
                $"Incorrect zoom travel: expected={travel}, actual={Vector3D.DotProduct(after.Position - before.Position, direction)}");
        }
    }
    private static BitmapSource Presented(Viewport3DX viewport)
    {
        using MemoryStream stream = new();
        HelixToolkit.SharpDX.Utilities.ScreenCapture.SaveWICTextureToBitmapStream(viewport.RenderHost!.EffectsManager!, viewport.RenderHost.RenderBuffer!.BackBuffer!.Resource as SharpDX.Direct3D11.Texture2D, stream);
        stream.Position = 0; return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(bytes, converted.PixelWidth * 4, 0); return bytes;
    }
    private static void Save(BitmapSource image, string path) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
