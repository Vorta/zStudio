using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Rendering;
using Xunit;
using Vector = System.Windows.Vector;

namespace Recoil.Zbd.Desktop.Tests;

internal static class PresentationRendererChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // Retained renderer math and guide publication, with no input capture or native gesture start.
    internal static void Run()
    {
        using var scene = new SceneViewport();
        var surface = (Viewport3DX)scene.RenderSurface;
        Layout(900, 600);
        scene.RestoreView(new(new(0, 0, 100), new(0, 0, -100), new(0, 1, 0), 60));
        var pose = scene.CaptureView();
        AiNode node = new("presentation-node", 0, 0, Vector3.Zero, 0, []);
        scene.SetAiNetworks(new("presentation", [new("network", "fixture", 0, "net_01.zrd", "path", "standard", 0, [node], [])]));
        scene.SetAiOptions(true, true, null);
        var card = new Border(); scene.InspectionContent = card;
        var ai = Get<Element3D>("aiOverlay");
        Assert.True(ai.IsRendering);
        scene.SetPresentationUiHidden(true);
        Assert.False(ai.IsRendering); Assert.False(surface.ShowViewCube); Assert.False(surface.ShowCoordinateSystem);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)card.Parent).Visibility);
        Assert.True(scene.AiVisible); Assert.Equal(pose, scene.CaptureView());
        scene.SetAiOptions(true, false, null); // Recreates the overlay while the override is active.
        Assert.False(Get<Element3D>("aiOverlay").IsRendering);
        Assert.Null(scene.PickAiNode(new(450, 300)));
        scene.SetPresentationUiHidden(false);
        Assert.True(Get<Element3D>("aiOverlay").IsRendering); Assert.True(surface.ShowViewCube);
        Assert.Same(card, scene.InspectionContent); Assert.Equal(Visibility.Visible, ((FrameworkElement)card.Parent).Visibility);

        scene.SetAiPathEditing("network", [node.Id]);
        Set("aiPathPointerStart", new Point(450, 300));
        Set("aiPathPointerRight", Vector3.UnitX); Set("aiPathPointerUp", Vector3.UnitY); Set("aiPathPointerScale", 1f);
        List<(Vector3 Move, double X, double Y)> moves = [];
        scene.AiPathPointerMoved += (move, x, y) => moves.Add((move, x, y));
        Call("HandleAiPathMove", new Point(460, 305));
        Assert.Equal(new Vector3(10, -5, 0), moves[^1].Move);
        scene.SetPresentationUiHidden(true); Layout(1200, 800);
        Call("HandleAiPathMove", new Point(800, 600)); Assert.Single(moves);
        scene.SetPresentationUiHidden(false);
        Call("HandleAiPathMove", new Point(800, 600)); Assert.Single(moves); // Establishes the new pointer origin only.
        Call("HandleAiPathMove", new Point(803, 598));
        Assert.Equal(new Vector3(13, -3, 0), moves[^1].Move);
        scene.SetAiPathPointerTransform(false);
        Set("aiPathBoxStart", new Point(100, 100));
        scene.SetPresentationUiHidden(true);
        Assert.NotNull(typeof(SceneViewport).GetField("aiPathBoxStart", Private)!.GetValue(scene));
        var release = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent };
        scene.RaiseEvent(release);
        Assert.True(release.Handled);
        Assert.Null(typeof(SceneViewport).GetField("aiPathBoxStart", Private)!.GetValue(scene));
        Assert.Equal(new[] { node.Id }, scene.SelectedAiPathNodes);
        scene.SetPresentationUiHidden(false); scene.SetAiPathEditing(null);

        Guid window = Guid.NewGuid();
        scene.SetParachuteOverlay([new(0, new(-20, 0, 0)), new(10, new(20, 0, 0))], [new(window, 5, [])]);
        scene.SetPresentationUiHidden(true);
        Assert.True(scene.HandleScenePointerDown(new(600, 400), new(Mouse.PrimaryDevice, 0, MouseButton.Left)));
        Assert.Null(scene.SelectedParachuteWindow);
        scene.SetPresentationUiHidden(false);
        Assert.True(scene.HandleScenePointerDown(new(600, 400), new(Mouse.PrimaryDevice, 0, MouseButton.Left)));
        Assert.Equal(window, scene.SelectedParachuteWindow);
        scene.SetParachutePointerMove(true);
        List<(int Segment, double Fraction)> phases = [];
        scene.ParachuteMoveRequested += (segment, fraction) => phases.Add((segment, fraction));
        scene.SetPresentationUiHidden(true);
        scene.SetParachutePreview(window, 5, []);
        Assert.False(Get<Element3D>("parachuteOverlay").IsRendering);
        Assert.True(scene.MoveParachutePointer(new(900, 400))); Assert.Empty(phases);
        scene.SetPresentationUiHidden(false); Layout(1000, 700);
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
        try
        {
            scene.SetPresentationUiHidden(true); Layout(1100, 760);
            Assert.True(scene.HandlePickupPointerMove(new(900, 400))); Assert.Empty(transforms);
            scene.SetPresentationUiHidden(false);
            scene.HandlePickupPointerMove(new(1500, 900));
            Assert.True(scene.IsPickupDragging); Assert.Empty(transforms);
            scene.HandlePickupPointerMove(new(650, 380)); Assert.Empty(transforms);
            scene.HandlePickupPointerMove(new(650, 380)); Assert.Equal(draft, transforms[^1]);
            scene.HandlePickupPointerMove(new(660, 380)); Assert.True(transforms[^1].Position.X > draft.Position.X);
            Assert.Equal(0, transforms[^1].Position.Y); Assert.Equal(0, transforms[^1].Position.Z);
            Assert.True(scene.CancelPickupDrag()); Assert.Equal(original, transforms[^1]);
        }
        finally { typeof(SceneViewport).GetProperty(nameof(SceneViewport.IsPickupDragging))!.SetValue(scene, false); }

        T Get<T>(string name) => (T)typeof(SceneViewport).GetField(name, Private)!.GetValue(scene)!;
        void Set(string name, object value) => typeof(SceneViewport).GetField(name, Private)!.SetValue(scene, value);
        object? Call(string name, params object[] values) => typeof(SceneViewport).GetMethod(name, Private)!.Invoke(scene, values);
        void Layout(double width, double height) { scene.Measure(new(width, height)); scene.Arrange(new(0, 0, width, height)); scene.UpdateLayout(); }
    }
}
