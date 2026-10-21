using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using zStudio.Core;
using zStudio.Rendering;
using Xunit;
using Vector = System.Windows.Vector;

namespace zStudio.Desktop.Tests;

internal static class PresentationRendererChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // Gestures and inertia across a resize of the view (fullscreen changes its size), with no input capture or native gesture start.
    internal static void Run()
    {
        using var scene = new SceneViewport();
        var surface = (Viewport3DX)scene.RenderSurface;
        Layout(900, 600);
        scene.RestoreView(new(new(0, 0, 100), new(0, 0, -100), new(0, 1, 0), 60));
        var pose = scene.CaptureView();
        foreach (var (gesture, modifiers) in new[]
        {
            (SceneViewport.NavigationGesture.Orbit, ModifierKeys.None),
            (SceneViewport.NavigationGesture.Pan, ModifierKeys.Shift),
            (SceneViewport.NavigationGesture.Zoom, ModifierKeys.Control),
            (SceneViewport.NavigationGesture.Dolly, ModifierKeys.Control | ModifierKeys.Shift)
        })
        {
            scene.RestoreView(pose);
            scene.BeginNavigationDrag(gesture, new(150, 150));
            scene.MoveNavigationDrag(new(155, 155), modifiers);
            var moved = scene.CaptureView();
            Layout(1100, 700);
            scene.MoveNavigationDrag(new(1500, 900), modifiers);
            Assert.Equal(moved, scene.CaptureView());
            scene.MoveNavigationDrag(new(350, 250), modifiers); // The first point inside the resized view only sets the anchor.
            Assert.Equal(moved, scene.CaptureView());
            scene.MoveNavigationDrag(new(353, 254), modifiers);
            Assert.NotEqual(moved, scene.CaptureView());
            Layout(900, 600);
            var restored = scene.CaptureView();
            scene.NavigationModifierKey(Key.LeftShift, ModifierKeys.Shift, new(153, 154));
            Assert.Equal(restored, scene.CaptureView());
            scene.MoveNavigationDrag(new(153, 154), ModifierKeys.Shift);
            Assert.Equal(restored, scene.CaptureView());
            scene.MoveNavigationDrag(new(155, 156), ModifierKeys.Shift);
            Assert.NotEqual(restored, scene.CaptureView());
            scene.CancelNavigation();
        }
        scene.RestoreView(pose);
        foreach (string projection in new[] { "perspective", "orthographic" })
        {
            var startingPose = projection == "orthographic" ? pose with { Projection = projection, OrthographicWidth = 100 } : pose;
            SceneViewport.ViewPose Inertia(bool resize)
            {
                scene.CancelNavigation(); Layout(900, 600); scene.RestoreView(startingPose);
                scene.ZoomBy(1, new(150, 100));
                Set("navigationGesture", SceneViewport.NavigationGesture.Zoom); Set("navigationVelocity", new Vector(0, 80));
                if (resize) Layout(1200, 250);
                Call("AdvanceNavigationInertia", .02);
                return scene.CaptureView();
            }
            var baseline = Inertia(false); var resized = Inertia(true);
            Assert.True((baseline.Position - resized.Position).Length < 1e-7);
            Assert.True((baseline.LookDirection - resized.LookDirection).Length < 1e-7);
            Assert.Equal(baseline.OrthographicWidth, resized.OrthographicWidth);
        }
        scene.CancelNavigation(); Layout(900, 600); scene.RestoreView(pose);
        AiNode node = new("presentation-node", 0, 0, Vector3.Zero, 0, []);
        scene.SetAiNetworks(new("presentation", [new("network", "fixture", 0, "net_01.zrd", "path", "standard", 0, [node], [])]));
        scene.SetAiOptions(true, true, null);

        scene.SetAiPathEditing("network", [node.Id]);
        Set("aiPathPointerStart", new Point(450, 300));
        Set("aiPathPointerRight", Vector3.UnitX); Set("aiPathPointerUp", Vector3.UnitY); Set("aiPathPointerScale", 1f);
        List<(Vector3 Move, double X, double Y)> moves = [];
        scene.AiPathPointerMoved += (move, x, y) => moves.Add((move, x, y));
        Call("HandleAiPathMove", new Point(460, 305));
        Assert.Equal(new Vector3(10, -5, 0), moves[^1].Move);
        Layout(1200, 800);
        Call("HandleAiPathMove", new Point(800, 600)); Assert.Single(moves); // Establishes the new pointer origin only.
        Call("HandleAiPathMove", new Point(803, 598));
        Assert.Equal(new Vector3(13, -3, 0), moves[^1].Move);
        scene.SetAiPathPointerTransform(false);
        var marquee = new System.Windows.Shapes.Rectangle();
        Set("aiPathBox", marquee); Set("aiPathBoxStart", new Point(100, 100)); Set("aiPathBoxLast", new Point(200, 200));
        Call("RebasePresentationPointers");
        Call("HandleAiPathMove", new Point(400, 300));
        Assert.Equal(new Thickness(300, 200, 0, 0), marquee.Margin);
        Assert.Equal(100, marquee.Width); Assert.Equal(100, marquee.Height);
        scene.CancelAiPathBox(); scene.SetAiPathEditing(null);

        Guid window = Guid.NewGuid();
        scene.SetParachuteOverlay([new(0, Vector3.Zero), new(1, new(-20, 0, 0)), new(10, new(20, 0, 0))], [new(window, 5, [])]);
        var flightLine = Get<LineGeometryModel3D>("parachuteLine").Geometry!;
        Assert.Equal(new[] { 1, 2 }, flightLine.Indices);
        Assert.Equal(Vector3.Zero, flightLine.Positions![0]); // Retain sample identity; omit only its rendered edge.
        scene.SetParachuteOverlay([new(0, new(-20, 0, 0)), new(10, new(20, 0, 0))], [new(window, 5, [])]);
        Assert.Empty(Get<LineGeometryModel3D>("parachuteLine").Geometry!.Indices!);
        // A read-only world inspects objects, but drop-window selection enters an edit-only interaction.
        // Exercise the real pointer dispatch over the visible marker without native input or mouse capture.
        scene.SetPlacementEditing(false);
        Assert.True(scene.HandleScenePointerDown(new(600, 400), new(Mouse.PrimaryDevice, 0, MouseButton.Left)));
        Assert.Null(scene.SelectedParachuteWindow);
        Assert.False(scene.IsParachutePointerMoving);
        Assert.False(scene.HasParachutePreview);
        scene.SetPlacementEditing(true);
        Assert.True(scene.HandleScenePointerDown(new(600, 400), new(Mouse.PrimaryDevice, 0, MouseButton.Left)));
        Assert.Equal(window, scene.SelectedParachuteWindow);
        scene.SetParachutePointerMove(true);
        List<(int Segment, double Fraction)> phases = [];
        scene.ParachuteMoveRequested += (segment, fraction) => phases.Add((segment, fraction));
        scene.SetParachutePreview(window, 5, []);
        Layout(1000, 700);
        scene.MoveParachutePointer(new(700, 350)); Assert.Empty(phases);
        scene.MoveParachutePointer(new(700, 350));
        Assert.Equal(.5, phases[^1].Fraction, 6); // A stationary pointer after resize cannot relocate the window.
        scene.MoveParachutePointer(new(705, 350)); Assert.True(phases[^1].Fraction > .5);
        scene.SetParachuteOverlay([], []);

        // The real translation handler's post-layout path retains the existing draft and original Escape rollback.
        using var arrow = new MeshGeometryModel3D { Transform = new MatrixTransform3D(Matrix3D.Identity) };
        Set("activePickupHandle", new HelixToolkit.SharpDX.HitTestResult { ModelHit = arrow, PointHit = Vector3.Zero });
        var original = new PlacementTransform(Vector3.Zero, Vector3.Zero);
        var draft = original with { Position = new(7, 0, 0) };
        Set("transformDragStart", original); Set("transformDraft", draft);
        typeof(SceneViewport).GetProperty(nameof(SceneViewport.IsPickupDragging))!.SetValue(scene, true);
        List<PlacementTransform> transforms = [];
        scene.TransformDraftChanged += transforms.Add;
        var handles = new GroupModel3D(); Set("pickupTarget", handles);
        try
        {
            Layout(1100, 760);
            scene.HandlePickupPointerMove(new(1500, 900));
            Assert.True(scene.IsPickupDragging); Assert.Empty(transforms);
            scene.HandlePickupPointerMove(new(650, 380)); Assert.Empty(transforms);
            scene.HandlePickupPointerMove(new(650, 380)); Assert.Equal(draft, transforms[^1]);
            scene.HandlePickupPointerMove(new(660, 380)); Assert.True(transforms[^1].Position.X > draft.Position.X);
            Assert.Equal(transforms[^1].Position.X, handles.Transform.Value.OffsetX, 4); // The handles follow during the drag, not on release.
            Assert.Equal(0, transforms[^1].Position.Y); Assert.Equal(0, transforms[^1].Position.Z);
            Assert.True(scene.CancelPickupDrag()); Assert.Equal(original, transforms[^1]);
        }
        finally { typeof(SceneViewport).GetProperty(nameof(SceneViewport.IsPickupDragging))!.SetValue(scene, false); }

        T Get<T>(string name) => (T)typeof(SceneViewport).GetField(name, Private)!.GetValue(scene)!;
        void Set(string name, object value) => typeof(SceneViewport).GetField(name, Private)!.SetValue(scene, value);
        object? Call(string name, params object[] values) => typeof(SceneViewport).GetMethod(name, Private)!.Invoke(scene, values);
        void Layout(double width, double height) { scene.Measure(new(width, height)); scene.Arrange(new(0, 0, width, height)); scene.UpdateLayout(); }
    }

    internal static void CheckSurfaceStrokes(SceneViewport scene)
    {
        var surface = (Viewport3DX)scene.RenderSurface;
        // Interior points of the existing ModelFixture triangle, tested against the actual rendered surface.
        Point first = surface.Project(new Point3D(0, .4, .02)), next = surface.Project(new Point3D(0, 1.4, .07));
        Assert.True(scene.TryNavigationSurface(first, out var firstHit));
        Assert.True(scene.TryNavigationSurface(next, out _));
        var zoneHit = Assert.IsType<SceneInspection>(scene.ProbeInspection(next));
        Assert.True(zoneHit.Active); Assert.True(zoneHit.Node >= 0);
        Assert.Null(zoneHit.RuntimeInstance); Assert.NotNull(zoneHit.Polygon);
        var position = new Vector3((float)firstHit.X, (float)firstHit.Y, (float)firstHit.Z);
        float radius = scene.TerrainBrushRadius; scene.TerrainBrushRadius = .1f;
        int brushCompletions = 0, zoneCompletions = 0;
        scene.TerrainStrokeCompleted += BrushCompleted; scene.ZoneStrokeCompleted += ZoneCompleted;
        // The polygon zone tool paints every face of this model; each source polygon is its built one.
        scene.SetZoneBrush(new PolygonZoneBrush { Reach = _ => PolygonZoneReach.Paintable, SourcePolygons = _ => Enumerable.Range(0, 4096).ToArray(), Site = node => "node" + node, SiteNodes = _ => [] });
        var strokeField = typeof(SceneViewport).GetField("zoneStroke", Private)!;
        System.Collections.IDictionary Stroke() { var stroke = (System.Collections.IDictionary)Activator.CreateInstance(strokeField.FieldType)!; strokeField.SetValue(scene, stroke); return stroke; }
        try
        {
            var ordinary = new List<Vector3> { position }; Set("brushStroke", ordinary);
            scene.HandleTerrainBrushMove(next); Assert.Equal(2, ordinary.Count); scene.CancelTerrainStroke();
            var retained = new List<Vector3> { position }; Set("brushStroke", retained);
            Call("RebasePresentationPointers"); Call("RebasePresentationPointers");
            scene.HandleTerrainBrushMove(next); scene.HandleTerrainBrushMove(first);
            Assert.Single(retained); Assert.Equal(0, brushCompletions);
            Assert.True(scene.HandleTerrainBrushUp(Release())); Assert.Equal(1, brushCompletions);
            Assert.False(scene.HandleTerrainBrushUp(Release())); Assert.Equal(1, brushCompletions);
            ordinary = [position]; Set("brushStroke", ordinary);
            scene.HandleTerrainBrushMove(next); Assert.Equal(2, ordinary.Count); scene.CancelTerrainStroke();

            var normalTargets = Stroke();
            Call("HandleZoneMove", next); Assert.Single(normalTargets); scene.CancelZoneGesture();
            var frozenTargets = Stroke();
            Call("RebasePresentationPointers"); Call("RebasePresentationPointers");
            Call("HandleZoneMove", next); Assert.Empty(frozenTargets); Assert.Equal(0, zoneCompletions);
            Assert.True((bool)Call("HandleZoneUp", Release())!); Assert.Equal(1, zoneCompletions);
            Assert.False((bool)Call("HandleZoneUp", Release())!); Assert.Equal(1, zoneCompletions);
            normalTargets = Stroke();
            Call("HandleZoneMove", next); Assert.Single(normalTargets);
        }
        finally
        {
            scene.CancelTerrainStroke(); scene.CancelZoneGesture(); scene.SetZoneBrush(null); scene.TerrainBrushRadius = radius;
            scene.TerrainStrokeCompleted -= BrushCompleted; scene.ZoneStrokeCompleted -= ZoneCompleted;
        }
        void BrushCompleted(IReadOnlyList<Vector3> points) { brushCompletions++; Assert.Single(points); Assert.Equal(position, points[0]); }
        void ZoneCompleted(ZonePaintStroke stroke) { zoneCompletions++; Assert.Empty(stroke.Targets); }
        void Set(string name, object value) => typeof(SceneViewport).GetField(name, Private)!.SetValue(scene, value);
        object? Call(string name, params object[] values) => typeof(SceneViewport).GetMethod(name, Private)!.Invoke(scene, values);
        static MouseButtonEventArgs Release() => new(Mouse.PrimaryDevice, 0, MouseButton.Left);
    }
}
