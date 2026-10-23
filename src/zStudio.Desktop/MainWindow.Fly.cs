using System.Windows;
using System.Windows.Threading;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private FlyCameraSession? flyCamera;
    private bool synchronizingFly;
    private int flyRequest;

    private void ConfigureFlyScene(SceneViewport viewport)
    {
        flyCamera?.Dispose();
        flyCamera = new(this, viewport);
        flyCamera.Changed += SynchronizeFly;
    }

    private void FlyChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || synchronizingFly) return;
        int request = ++flyRequest;
        if (FlyEnabled.IsChecked != true) { flyCamera?.End(); return; }
        // A toolbar overflow popup owns capture until the activating click completes.
        SceneToolbar.IsOverflowOpen = false;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (request != flyRequest || FlyEnabled.IsChecked != true) return;
            if (sourceAnimationPreview) { SynchronizeFly(); return; }
            if (flyCamera == null) { SynchronizeFly(); return; }
            if (!flyCamera.TryStart(out string? error)) { Notice(error ?? "Fly camera unavailable."); SynchronizeFly(); }
        });
    }

    private void SynchronizeFly()
    {
        synchronizingFly = true;
        try
        {
            bool active = flyCamera?.IsActive == true;
            FlyEnabled.IsChecked = active;
            FlyHint.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (active) FlyHintText.Text = $"Fly camera · {scene!.FlySpeed:0.##} units/s\nWASD move · Space/C up/down · Mouse look · Wheel speed · Esc exit";
            // A source map shows the Flycam banner and an accent frame instead.
            RefreshViewportChips();
        }
        finally { synchronizingFly = false; }
    }
}
