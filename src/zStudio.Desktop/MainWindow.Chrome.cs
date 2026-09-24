using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private bool captionMaximizePressed;
    private void InitializeChrome()
    {
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = TitleArea.Height,
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0)
        });
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(ChromeMessage);
            UpdateCaptionBounds();
        };
        SizeChanged += (_, _) => UpdateCaptionBounds();
        TitleArea.SizeChanged += (_, _) => ArrangeTitleCommands();
        AppMenu.SizeChanged += (_, _) => ArrangeTitleCommands();
        CaptionButtons.SizeChanged += (_, _) => ArrangeTitleCommands();
        StateChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateCaptionBounds);
        PreviewMouseMove += (_,e) => { if (captionMaximizePressed) SetMaximizeHover(new Rect(CaptionMaximize.RenderSize).Contains(e.GetPosition(CaptionMaximize))); };
        PreviewMouseLeftButtonUp += (_,e) =>
        {
            if (!captionMaximizePressed) return;
            bool activate = new Rect(CaptionMaximize.RenderSize).Contains(e.GetPosition(CaptionMaximize));
            EndCaptionPress(); e.Handled = true;
            if (activate) CaptionMaximizeClick(CaptionMaximize,e);
        };
        LostMouseCapture += (_,_) => { if (captionMaximizePressed && Mouse.Captured != this) EndCaptionPress(); };
        Deactivated += (_,_) => EndCaptionPress();
    }
    private nint ChromeMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Windows 11 discovers Snap Layouts from the real maximize hit region, even
        // though these full-height caption controls are drawn in WPF.
        if (message == 0x0084 && CaptionMaximize.IsVisible)
        {
            var point = new Point(unchecked((short)(long)lParam),unchecked((short)((long)lParam >> 16)));
            if (new Rect(CaptionMaximize.RenderSize).Contains(CaptionMaximize.PointFromScreen(point))) { handled = true; return 9; }
        }
        if (message == 0x00A0)
        {
            SetMaximizeHover(wParam == 9);
            if (wParam == 9) { var track = new MouseTracking { Size = Marshal.SizeOf<MouseTracking>(), Flags = 0x12, Window = hwnd }; TrackMouseEvent(ref track); }
        }
        if (message == 0x02A2 && !captionMaximizePressed) SetMaximizeHover(false);
        if (message == 0x00A1 && wParam == 9)
        {
            captionMaximizePressed = Mouse.Capture(this,CaptureMode.Element); SetMaximizeHover(true); handled = true; return 0;
        }
        if (message is 0x02E0 or 0x031A or 0x031E or 0x001A or 0x0047) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateCaptionBounds);
        return 0;
    }
    private void EndCaptionPress()
    {
        captionMaximizePressed = false;
        if (Mouse.Captured == this) Mouse.Capture(null);
        SetMaximizeHover(false);
    }
    private void ArrangeTitleCommands()
    {
        // Equal guard bands keep the document group centered on the window while
        // reserving the menu and caption hit regions. Only the filename trims.
        double menuRight = AppMenu.TranslatePoint(new Point(AppMenu.ActualWidth,0),TitleArea).X;
        double side = Math.Max(menuRight,CaptionButtons.ActualWidth) + 12;
        DocumentCommands.Margin = new Thickness(side,0,side,0);
    }
    private void SetMaximizeHover(bool hover) => CaptionMaximize.Background = hover ? SystemParameters.HighContrast ? SystemColors.HighlightBrush : new SolidColorBrush(Color.FromArgb(captionMaximizePressed ? (byte)64 : (byte)32,128,128,128)) : Brushes.Transparent;
    private void CaptionMinimizeClick(object sender,RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void CaptionMaximizeClick(object sender,RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this);
    }
    private void CaptionCloseClick(object sender,RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    private void UpdateCaptionBounds()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || WindowState == WindowState.Minimized || !IsVisible) return;
        UpdateMaximizedInsets(handle,VisualTreeHelper.GetDpi(this));
        bool maximized = WindowState == WindowState.Maximized;
        CaptionMaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
        CaptionMaximize.ToolTip = maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(CaptionMaximize,maximized ? "Restore" : "Maximize");
    }
    private void UpdateMaximizedInsets(nint handle,DpiScale dpi)
    {
        if (WindowState != WindowState.Maximized) { Shell.Margin = new Thickness(0); return; }
        var monitor = new MonitorBounds { Size = Marshal.SizeOf<MonitorBounds>() }; var origin = new ScreenPoint();
        if (!GetMonitorInfo(MonitorFromWindow(handle,2),ref monitor) || !GetClientRect(handle,out var client) || !ClientToScreen(handle,ref origin)) return;
        Shell.Margin = new Thickness(Math.Max(0,monitor.Work.Left - origin.X) / dpi.DpiScaleX,
            Math.Max(0,monitor.Work.Top - origin.Y) / dpi.DpiScaleY,
            Math.Max(0,origin.X + client.Right - monitor.Work.Right) / dpi.DpiScaleX,
            Math.Max(0,origin.Y + client.Bottom - monitor.Work.Bottom) / dpi.DpiScaleY);
    }
    [StructLayout(LayoutKind.Sequential)] private struct CaptionBounds { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorBounds { public int Size; public CaptionBounds Monitor,Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseTracking { public int Size; public uint Flags; public nint Window; public uint HoverTime; }
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref MouseTracking tracking);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor,ref MonitorBounds info);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd,out CaptionBounds rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd,ref ScreenPoint point);
}
