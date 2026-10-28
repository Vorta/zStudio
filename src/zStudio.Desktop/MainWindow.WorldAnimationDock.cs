using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private void ApplyWorldAnimationScreenEffects(AnimationFrame? frame)
    {
        WorldAnimationWaveOverlay.Children.Clear();
        if (frame == null || worldAnimationSession?.Effects != true || scene?.FaceOrientationEnabled == true)
        { WorldAnimationScreenOverlay.Background = null; return; }
        var c = frame.ScreenColor;
        WorldAnimationScreenOverlay.Background = new SolidColorBrush(Color.FromScRgb(Math.Clamp(c.W, 0, 1), Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1)));
        if (frame.ScreenWave.Z <= 0) return;
        var wave = frame.ScreenWave;
        double radius = Math.Clamp(wave.Z, 0, 10000);
        Ellipse circle = new() { Width = radius * 2, Height = radius * 2, Stroke = Brushes.LightSkyBlue, StrokeThickness = Math.Clamp(Math.Abs(wave.W), 1, 12), Opacity = .55 };
        Canvas.SetLeft(circle, wave.X - radius); Canvas.SetTop(circle, wave.Y - radius); WorldAnimationWaveOverlay.Children.Add(circle);
    }

    /// <summary>The source map's Diagnostics content (the problems), hosted in the map layout's bottom pane or Animation mode's strip.</summary>
    private ContentControl? sourceDiagnosticsContent;
}
