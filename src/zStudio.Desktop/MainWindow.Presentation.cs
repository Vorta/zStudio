using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using zStudio.Automation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    internal sealed record PresentationStatus(bool Fullscreen, bool UiHidden, bool Available);
    private bool presentationFullscreen, presentationUiHidden, refreshingPresentation, fullscreenBoundsQueued;
    private PropertiesWindow? presentationProperties;
    private WindowPresentation? ordinaryWindow;
    private int layoutLeases;
    internal bool IsChangingLayout => layoutLeases != 0;
    internal PresentationStatus PresentationState => new(presentationFullscreen, presentationUiHidden, ActiveNavigationViewport != null);

    internal PresentationStatus SetPresentation(bool? fullscreen = null, bool? uiHidden = null)
    {
        Dispatcher.VerifyAccess();
        if (fullscreen == null && uiHidden == null) return PresentationState;
        if ((fullscreen == true || uiHidden == true) && ActiveNavigationViewport == null)
            throw new StudioCommandException("preview_unavailable", "Open a 3D preview before enabling presentation modes.");
        // The polygon zone tool's banner and picker are UI: hiding it leaves the tool (refused during a stroke).
        if (uiHidden == true && !presentationUiHidden) EndZoneToolFor("hiding the UI");
        using var lease = BeginLayoutChange();
        if (fullscreen is bool full && full != presentationFullscreen) SetFullscreen(full);
        if (uiHidden is bool hidden) presentationUiHidden = hidden;
        RefreshPresentation();
        ArrangeWorkspace();
        return PresentationState;
    }

    internal static string? PresentationShortcut(Key key, ModifierKeys modifiers) =>
        key == Key.F11 && modifiers == ModifierKeys.None ? "fullscreen" :
        key == Key.F10 && modifiers == ModifierKeys.None ? "uiHidden" : null;

    internal bool PresentationKey(KeyEventArgs e)
    {
        var action = PresentationShortcut(e.Key == Key.System ? e.SystemKey : e.Key, System.Windows.Input.Keyboard.Modifiers);
        if (action == null) return false;
        e.Handled = true;
        if (!e.IsRepeat) TogglePresentation(action);
        return true;
    }

    /// <summary>F10's release: Windows would otherwise enter its own menu mode, which keeps the next keys and clicks.</summary>
    internal static void PresentationKeyUp(KeyEventArgs e)
    {
        if (PresentationShortcut(e.Key == Key.System ? e.SystemKey : e.Key, System.Windows.Input.Keyboard.Modifiers) != null) e.Handled = true;
    }

    private void PresentationActionClick(object sender, RoutedEventArgs e) => TogglePresentation((string)((MenuItem)sender).Tag);
    private void TogglePresentation(string action)
    {
        try { SetPresentation(action == "fullscreen" ? !presentationFullscreen : null, action == "uiHidden" ? !presentationUiHidden : null); }
        catch (Exception ex) when (ex is StudioCommandException or Win32Exception) { ViewModel.Status = ex.Message; RefreshPresentation(); }
    }

    private void RefreshPresentation(bool previewSettled = false)
    {
        if (refreshingPresentation) return;
        refreshingPresentation = true;
        using var lease = BeginLayoutChange();
        try
        {
            // Loading temporarily removes the viewport. Only a completed selection decides
            // whether navigation left 3D; a 3D replacement inherits the current override.
            if (previewSettled && ActiveNavigationViewport == null) presentationUiHidden = false;
            bool hidden = presentationUiHidden;
            FullscreenMenu.IsChecked = presentationFullscreen;
            HideUiMenu.IsChecked = hidden;
            FullscreenMenu.IsEnabled = presentationFullscreen || ActiveNavigationViewport != null;
            HideUiMenu.IsEnabled = hidden || ActiveNavigationViewport != null;
            CaptionMaximize.IsEnabled = !presentationFullscreen;
            foreach (var chrome in new UIElement[] { TitleArea, PresentationStatusBar, PreviewHeading, PreviewOverlayChrome })
                chrome.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            // A source map's chrome keeps the compiled preview's toolbar and footer hidden (RefreshSourceChrome).
            if (hidden) PreviewToolbarHost.Visibility = PreviewFooter.Visibility = Visibility.Collapsed;
            else if (!sourceChromeActive) { PreviewToolbarHost.ClearValue(VisibilityProperty); PreviewFooter.ClearValue(VisibilityProperty); }
            Workbench.Margin = hidden ? new(0) : new(6, 0, 6, 4);
            PreviewInner.Margin = hidden || sourceChromeActive ? new(0) : new(4);
            // The title row is gone: its strip of the view takes the pointer instead of moving the window.
            foreach (var chrome in new[] { WindowChrome.GetWindowChrome(this), ordinaryWindow?.Chrome })
                if (chrome != null) chrome.CaptionHeight = hidden ? 0 : TitleArea.Height;
            RefreshStaleBanner();
            ApplyRetainedPresentation();
            if (hidden && propertiesWindow?.IsVisible == true)
            {
                presentationProperties = propertiesWindow;
                propertiesWindow.Hide();
            }
            else if (!hidden && presentationProperties is { } popup)
            {
                presentationProperties = null;
                if (popup == propertiesWindow && popup.Document?.IsDisposed == false)
                {
                    // Showing restores the same draft and owner without activating an MCP request.
                    bool activate = popup.ShowActivated;
                    popup.ShowActivated = false;
                    popup.Show();
                    popup.ShowActivated = activate;
                }
            }
        }
        finally { refreshingPresentation = false; }
        // A source map's chrome keeps its own toolbar and footer hidden.
        RefreshSourceChrome();
    }

    private void ApplyRetainedPresentation()
    {
        // Availability excludes loading previews, but their retained controls can already be
        // attached and rendering. Apply before attachment and on mode changes during loading.
        animation?.SetPresentationUiHidden(presentationUiHidden);
        motion?.SetPresentationUiHidden(presentationUiHidden);
        scene?.SetPresentationUiHidden(presentationUiHidden);
        animation?.Viewport.SetPresentationUiHidden(presentationUiHidden);
        motion?.Viewport.SetPresentationUiHidden(presentationUiHidden);
    }

    internal bool IsPropertiesPresented(PropertiesWindow window) => window.IsVisible || ReferenceEquals(window, presentationProperties);

    private IDisposable BeginLayoutChange()
    {
        ++layoutLeases;
        return new LayoutLease(this);
    }
    private sealed class LayoutLease(MainWindow owner) : IDisposable
    {
        private MainWindow? window = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref window, null) is { } current)
                current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { --current.layoutLeases; });
        }
    }

    private sealed record WindowPresentation(NativePlacement Placement, WindowStyle Style, ResizeMode Resize,
        WindowChrome? Chrome, double MinWidth, double MinHeight, double MaxWidth, double MaxHeight,
        double SavedWidth, double SavedHeight);

    private void SetFullscreen(bool enabled)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) throw new StudioCommandException("window_unavailable", "Show the workspace before changing fullscreen.");
        if (enabled)
        {
            var placement = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
            RequireNative(GetWindowPlacement(handle, ref placement));
            var monitor = new MonitorBounds { Size = Marshal.SizeOf<MonitorBounds>() };
            RequireNative(GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor));
            var saved = new WindowPresentation(placement, WindowStyle, ResizeMode, WindowChrome.GetWindowChrome(this),
                MinWidth, MinHeight, MaxWidth, MaxHeight,
                WindowState == WindowState.Normal ? ActualWidth : RestoreBounds.Width,
                WindowState == WindowState.Normal ? ActualHeight : RestoreBounds.Height);
            ordinaryWindow = saved;
            presentationFullscreen = true;
            try
            {
                ApplyFullscreenStyle();
                ApplyFullscreenBounds(monitor.Monitor);
                ApplyCornerPreference(handle);
            }
            catch
            {
                presentationFullscreen = false;
                RestoreOrdinaryWindow(handle, saved);
                ordinaryWindow = null;
                throw;
            }
        }
        else if (ordinaryWindow is { } saved)
        {
            presentationFullscreen = false;
            try { RestoreOrdinaryWindow(handle, saved); ordinaryWindow = null; }
            catch
            {
                presentationFullscreen = true;
                ApplyFullscreenStyle();
                ApplyFullscreenBounds();
                throw;
            }
        }
    }

    private void RestoreOrdinaryWindow(nint handle, WindowPresentation saved)
    {
        WindowStyle = saved.Style; ResizeMode = saved.Resize;
        MinWidth = saved.MinWidth; MinHeight = saved.MinHeight; MaxWidth = saved.MaxWidth; MaxHeight = saved.MaxHeight;
        WindowChrome.SetWindowChrome(this, saved.Chrome);
        var placement = saved.Placement;
        RequireNative(SetWindowPlacement(handle, ref placement));
        ApplyCornerPreference(handle);
        UpdateCaptionBounds();
    }
    private void ApplyFullscreenStyle()
    {
        WindowState = WindowState.Normal;
        MinWidth = MinHeight = 0; MaxWidth = MaxHeight = double.PositiveInfinity;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        WindowChrome.SetWindowChrome(this, null);
    }
    private void ApplyFullscreenBounds()
    {
        if (!presentationFullscreen || WindowState == WindowState.Minimized) return;
        nint handle = new WindowInteropHelper(this).Handle;
        var monitor = new MonitorBounds { Size = Marshal.SizeOf<MonitorBounds>() };
        RequireNative(GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor));
        ApplyFullscreenBounds(monitor.Monitor);
    }
    private void ApplyFullscreenBounds(CaptionBounds bounds)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        Shell.Margin = new(0);
        RequireNative(SetWindowPos(handle, 0, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            0x0004 | 0x0010 | 0x0020 | 0x0200)); // no Z-order/activation/owner-order change; recalculate frame
    }
    private void QueueFullscreenBounds()
    {
        if (!presentationFullscreen || fullscreenBoundsQueued) return;
        fullscreenBoundsQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            fullscreenBoundsQueued = false;
            if (!presentationFullscreen || shutdownToken.IsCancellationRequested) return;
            try { ApplyFullscreenBounds(); }
            catch (Win32Exception ex) { ViewModel.Status = "Fullscreen bounds could not be updated: " + ex.Message; }
        });
    }
    private static void RequireNative(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        public int Length; public uint Flags, ShowCommand; public ScreenPoint Minimum, Maximum; public CaptionBounds Normal;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint handle, ref NativePlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint handle, ref NativePlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
}
