using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using HitTestResult = HelixToolkit.SharpDX.HitTestResult;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    /// <summary>The screen-space cube viewport in DIPs, reserved by the inspection overlay.</summary>
    public Rect NavigationCubeBounds
    {
        get
        {
            if (!viewport.ShowViewCube) return Rect.Empty;
            double size = 100;
            if (viewport.Template?.FindName("PART_ViewCube", viewport) is ScreenSpacedElement3D cube &&
                cube.SceneNode.RenderCore is HelixToolkit.SharpDX.Core.ScreenSpacedMeshRenderCore core) size = core.Size;
            size *= viewport.ViewCubeSize;
            return new(viewport.ActualWidth * (1 + viewport.ViewCubeHorizontalPosition) / 2 - size / 2,
                viewport.ActualHeight * (1 - viewport.ViewCubeVerticalPosition) / 2 - size / 2, size, size);
        }
    }

    private void ConfigureNavigationCube()
    {
        // Helix's Y-up face order is +Z, -Z, +X, -X, +Y, -Y.
        // Recoil's front is -Z; explicitly label the same axes used by numpad views.
        const int size = 96;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            string[] labels = ["B", "F", "R", "L", "T", "D"];
            Brush[] colors = [Brushes.SteelBlue, Brushes.SteelBlue, Brushes.IndianRed, Brushes.IndianRed, Brushes.SeaGreen, Brushes.SeaGreen];
            for (int i = 0; i < labels.Length; i++)
            {
                context.DrawRectangle(colors[i], new Pen(Brushes.White, 2), new Rect(i * size, 0, size, size));
                var text = new FormattedText(labels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 48, Brushes.White, 1);
                if (i >= 4) context.PushTransform(new RotateTransform(i == 4 ? -90 : 90, i * size + size / 2, size / 2));
                context.DrawText(text, new Point(i * size + (size - text.Width) / 2, (size - text.Height) / 2));
                if (i >= 4) context.Pop();
            }
        }
        var bitmap = new RenderTargetBitmap(size * 6, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        byte[] pixels = new byte[size * size * 6 * 4]; bitmap.CopyPixels(pixels, size * 6 * 4, 0);
        viewport.ViewCubeTexture = new TextureModel(pixels, SharpDX.DXGI.Format.B8G8R8A8_UNorm, size * 6, size);
    }
    internal bool NavigateCubeAt(Point point)
    {
        if (viewport.RenderContext == null || !viewport.ShowViewCube || viewport.Template?.FindName("PART_ViewCube", viewport) is not ScreenSpacedElement3D cube) return false;
        var screen = new System.Numerics.Vector2((float)point.X, (float)point.Y);
        var ray = viewport.UnProject(screen);
        List<HitTestResult> hits = [];
        if (!cube.HitTest(new HitTestContext(viewport.RenderContext, ray, screen), ref hits) || hits.Count == 0) return false;
        if (!BeginManualNavigation()) return true;
        var normal = hits[0].NormalAtHit;
        string? axis = normal.X > .999 ? "left" : normal.X < -.999 ? "right" : normal.Y > .999 ? "bottom" : normal.Y < -.999 ? "top"
            : normal.Z > .999 ? "front" : normal.Z < -.999 ? "back" : null;
        if (axis != null) SetAxisView(axis);
        else
        {
            StopCameraMotion(); ChangeProjection("perspective"); var pose = CaptureView();
            var look = new Vector3D(normal.X, normal.Y, normal.Z); look.Normalize(); look *= pose.LookDirection.Length;
            var target = pose.Position + pose.LookDirection;
            RestoreView(UprightPose(pose with { Position = target - look, LookDirection = look, OrbitPivot = target }));
        }
        return true;
    }
}
