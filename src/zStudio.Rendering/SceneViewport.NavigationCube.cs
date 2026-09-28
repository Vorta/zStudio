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
    private double cubeHomeX, cubeHomeY, cubeHomeSize;
    /// <summary>The screen-space cube viewport in DIPs, reserved by the inspection overlay.</summary>
    public Rect NavigationCubeBounds => CubeBounds(viewport.ViewCubeHorizontalPosition, viewport.ViewCubeVerticalPosition, viewport.ViewCubeSize);
    public Rect NavigationCubeHomeBounds => CubeBounds(cubeHomeX, cubeHomeY, cubeHomeSize);
    private Rect CubeBounds(double horizontal, double vertical, double scale)
    {
        if (!viewport.ShowViewCube) return Rect.Empty;
        double size = 100;
        if (viewport.Template?.FindName("PART_ViewCube", viewport) is ScreenSpacedElement3D cube &&
            cube.SceneNode.RenderCore is HelixToolkit.SharpDX.Core.ScreenSpacedMeshRenderCore core) size = core.Size;
        size *= scale;
        // Helix clamps its small screen-space viewport at the surface edges.
        // Relative center alone is wrong in short/narrow render surfaces.
        return new(Math.Clamp(viewport.ActualWidth * (1 + horizontal) / 2 - size / 2, 0, Math.Max(0, viewport.ActualWidth - size)),
            Math.Clamp(viewport.ActualHeight * (1 - vertical) / 2 - size / 2, 0, Math.Max(0, viewport.ActualHeight - size)), size, size);
    }
    /// <summary>Keep cube navigation accessible beside a tall panel in a short viewport.</summary>
    public void ReserveInspectionPanel(Rect bounds)
    {
        var home = NavigationCubeHomeBounds;
        double x = cubeHomeX, y = cubeHomeY, scale = cubeHomeSize;
        if (!bounds.IsEmpty && !home.IsEmpty && home.IntersectsWith(bounds) && viewport.ActualWidth > 0 && viewport.ActualHeight > 0)
        {
            double size = Math.Min(home.Width, Math.Max(0, bounds.Left - 20));
            // The panel reserves at least a 60-DIP side slot at constrained
            // widths. Keep this cube above the bottom-left coordinate axes.
            scale *= size / home.Width;
            x = 2 * (bounds.Left - 10 - size / 2) / viewport.ActualWidth - 1;
            y = 1 - 2 * (10 + size / 2) / viewport.ActualHeight;
        }
        if (viewport.ViewCubeHorizontalPosition != x) viewport.ViewCubeHorizontalPosition = x;
        if (viewport.ViewCubeVerticalPosition != y) viewport.ViewCubeVerticalPosition = y;
        if (viewport.ViewCubeSize != scale) viewport.ViewCubeSize = scale;
    }

    private void ConfigureNavigationCube()
    {
        cubeHomeX = viewport.ViewCubeHorizontalPosition; cubeHomeY = viewport.ViewCubeVerticalPosition; cubeHomeSize = viewport.ViewCubeSize;
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
