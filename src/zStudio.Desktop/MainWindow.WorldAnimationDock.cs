using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
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

    private Grid? worldAnimationDock;
    private ContentControl? sourceDiagnosticsContent;
    private string WorldAnimationDockView { get; set; } = "timeline";
    private Button? timelineTab, diagnosticsTab;

    private void InitializeWorldAnimationDock()
    {
        if (worldAnimationDock != null) return;
        worldAnimationDock = new();
        worldAnimationDock.SetResourceReference(Panel.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        worldAnimationDock.RowDefinitions.Add(new() { Height = GridLength.Auto });
        worldAnimationDock.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        worldAnimationDock.RowDefinitions.Add(new() { Height = new(0) });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(4, 0, 4, 0) };
        timelineTab = new() { Content = "Timeline", Padding = new(10, 2, 10, 2) };
        diagnosticsTab = new() { Content = "Diagnostics", Padding = new(10, 2, 10, 2) };
        timelineTab.Click += (_, _) => { sourceToolsVisible = true; SetWorldAnimationDockView("timeline"); RememberSourceTools(); ArrangeWorkspace(); };
        diagnosticsTab.Click += (_, _) => { sourceToolsVisible = true; SetWorldAnimationDockView("diagnostics"); RememberSourceTools(); ArrangeWorkspace(); };
        AutomationProperties.SetName(timelineTab, "Show animation timeline");
        AutomationProperties.SetName(diagnosticsTab, "Show diagnostics");
        tabs.Children.Add(timelineTab); tabs.Children.Add(diagnosticsTab);
        worldAnimationDock.Children.Add(tabs);
        Grid.SetRow(WorldTimeline, 1); worldAnimationDock.Children.Add(WorldTimeline);
        sourceDiagnosticsContent = new() { Focusable = false };
        Grid.SetRow(sourceDiagnosticsContent, 2); worldAnimationDock.Children.Add(sourceDiagnosticsContent);
        SourceDiagnosticsHost.Content = worldAnimationDock;
    }

    private void SetWorldAnimationDockView(string view)
    {
        if (view is not ("timeline" or "diagnostics")) throw new ArgumentException("Choose timeline or diagnostics.");
        WorldAnimationDockView = view;
        UpdateWorldAnimationDock(SourceToolsVisible);
    }

    private void OpenWorldAnimationTimeline()
    {
        sourceToolsVisible = true;
        InitializeWorldAnimationDock();
        SetWorldAnimationDockView("timeline");
        ArrangeWorkspace();
    }

    private void UpdateWorldAnimationDock(bool expanded)
    {
        if (worldAnimationDock == null) return;
        bool timeline = expanded && WorldAnimationDockView == "timeline";
        bool diagnostics = expanded && !timeline;
        WorldTimeline.SetBodyVisible(timeline);
        WorldTimeline.Visibility = timeline || HasWorldAnimationTarget ? Visibility.Visible : Visibility.Collapsed;
        worldAnimationDock.RowDefinitions[1].Height = timeline ? new(1, GridUnitType.Star) : GridLength.Auto;
        worldAnimationDock.RowDefinitions[2].Height = diagnostics ? new(1, GridUnitType.Star) : new(0);
        sourceDiagnosticsContent!.Visibility = diagnostics ? Visibility.Visible : Visibility.Collapsed;
        timelineTab!.FontWeight = WorldAnimationDockView == "timeline" ? FontWeights.SemiBold : FontWeights.Normal;
        diagnosticsTab!.FontWeight = WorldAnimationDockView == "diagnostics" ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
