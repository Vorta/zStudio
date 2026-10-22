using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private double cubeHomeX, cubeHomeY, cubeHomeSize;
    /// <summary>The screen-space cube viewport in DIPs, reserved by the inspection overlay.</summary>
    public Rect NavigationCubeBounds => CubeBounds(view.ViewCubeHorizontalPosition, view.ViewCubeVerticalPosition, view.ViewCubeSize);
    public Rect NavigationCubeHomeBounds => CubeBounds(cubeHomeX, cubeHomeY, cubeHomeSize);
    private Rect CubeBounds(double horizontal, double vertical, double scale)
    {
        if (!view.ShowViewCube) return Rect.Empty;
        double size = view.ViewCubePixelSize * scale;
        // The renderer clamps the cube's small screen-space viewport at the surface edges.
        // Relative center alone is wrong in short/narrow render surfaces.
        return new(Math.Clamp(viewport.ActualWidth * (1 + horizontal) / 2 - size / 2, 0, Math.Max(0, viewport.ActualWidth - size)),
            Math.Clamp(viewport.ActualHeight * (1 - vertical) / 2 - size / 2, 0, Math.Max(0, viewport.ActualHeight - size)), size, size);
    }
    /// <summary>Whether the cube sits in the top right corner (with nothing over the view there) instead of the bottom right.</summary>
    public bool NavigationCubeTopRight { get; set; }
    /// <summary>Keep cube navigation accessible beside a tall panel in a short viewport.</summary>
    public void ReserveInspectionPanel(Rect bounds)
    {
        var home = NavigationCubeHomeBounds;
        double x = cubeHomeX, y = cubeHomeY, scale = cubeHomeSize;
        if (NavigationCubeTopRight && !home.IsEmpty && viewport.ActualWidth > 0 && viewport.ActualHeight > 0)
        {
            // 12 DIPs from the top right edges.
            x = 2 * (viewport.ActualWidth - 12 - home.Width / 2) / viewport.ActualWidth - 1;
            y = 1 - 2 * (12 + home.Height / 2) / viewport.ActualHeight;
        }
        else if (!bounds.IsEmpty && !home.IsEmpty && home.IntersectsWith(bounds) && viewport.ActualWidth > 0 && viewport.ActualHeight > 0)
        {
            double size = Math.Min(home.Width, Math.Max(0, bounds.Left - 20));
            // The panel reserves at least a 60-DIP side slot at constrained
            // widths. Keep this cube above the bottom-left coordinate axes.
            scale *= size / home.Width;
            x = 2 * (bounds.Left - 10 - size / 2) / viewport.ActualWidth - 1;
            y = 1 - 2 * (10 + size / 2) / viewport.ActualHeight;
        }
        if (view.ViewCubeHorizontalPosition != x) view.ViewCubeHorizontalPosition = x;
        if (view.ViewCubeVerticalPosition != y) view.ViewCubeVerticalPosition = y;
        if (view.ViewCubeSize != scale) view.ViewCubeSize = scale;
    }

    private void ConfigureNavigationCube()
    {
        cubeHomeX = view.ViewCubeHorizontalPosition; cubeHomeY = view.ViewCubeVerticalPosition; cubeHomeSize = view.ViewCubeSize;
        // The cube's Y-up face order is +Z, -Z, +X, -X, +Y, -Y.
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
        view.ViewCubeTexture = RenderTexture.FromBgra(pixels, size * 6, size);
    }
    internal bool NavigateCubeAt(Point point)
    {
        if (!view.HitViewCube(point, out var normal)) return false;
        if (!BeginManualNavigation()) return true;
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
