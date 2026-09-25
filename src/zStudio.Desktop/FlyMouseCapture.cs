using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Recoil.Zbd.Desktop;

/// <summary>One foreground raw-mouse registration owned by the application, never the format/render libraries.</summary>
internal sealed partial class FlyMouseCapture : IDisposable
{
    private readonly FrameworkElement target;
    private readonly HwndSource source;
    private readonly DispatcherTimer pointerTimer;
    private bool registered, clipped, disposed;
    private bool fallbackPending;
    private long relativeGeneration, pointerGeneration;
    private PointI pointerCenter;
    internal event Action<double, double>? Look;
    internal event Action? Invalidated;

    internal FlyMouseCapture(Window window, FrameworkElement target)
    {
        this.target = target;
        pointerTimer = new(DispatcherPriority.Background, target.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        pointerTimer.Tick += PointerTick;
        nint hwnd = new WindowInteropHelper(window).Handle;
        source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("The viewer window is not ready.");
        try
        {
            RawDevice device = new() { Page = 1, Usage = 2, Target = hwnd };
            if (!RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception(Marshal.GetLastPInvokeError());
            registered = true;
            source.AddHook(OnMessage);
            UpdateBounds();
            pointerTimer.Start();
        }
        catch { Dispose(); throw; }
    }

    internal void UpdateBounds()
    {
        if (disposed) return;
        var start = target.PointToScreen(new());
        var end = target.PointToScreen(new(target.ActualWidth, target.ActualHeight));
        RectI rect = new() { Left = (int)Math.Ceiling(start.X), Top = (int)Math.Ceiling(start.Y), Right = (int)Math.Floor(end.X), Bottom = (int)Math.Floor(end.Y) };
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) throw new InvalidOperationException("The 3D viewer has no visible input area.");
        if (!ClipCursor(ref rect)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        clipped = true;
        pointerCenter = new() { X = rect.Left + (rect.Right - rect.Left) / 2, Y = rect.Top + (rect.Bottom - rect.Top) / 2 };
        SetCursorPos(pointerCenter.X, pointerCenter.Y);
    }

    internal void QueueFallbackLook()
    {
        if (disposed || fallbackPending) return;
        fallbackPending = true;
        // Some remote/absolute devices and injected Windows input only provide
        // pointer positions. Defer until raw messages from this input batch arrive
        // so ordinary relative devices never rotate through both paths.
        target.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            fallbackPending = false;
            if (disposed || !GetCursorPos(out var point)) return;
            bool hasRelativeInput = pointerGeneration != relativeGeneration;
            pointerGeneration = relativeGeneration;
            int dx = point.X - pointerCenter.X, dy = point.Y - pointerCenter.Y;
            if (dx == 0 && dy == 0) return;
            SetCursorPos(pointerCenter.X, pointerCenter.Y);
            if (!hasRelativeInput) Look?.Invoke(dx, dy);
        });
    }

    // Some remote/injected pointer providers update GetCursorPos without delivering
    // WM_MOUSEMOVE to WPF. Poll only during explicit capture; raw packets still take
    // priority and each physical displacement is consumed once.
    private void PointerTick(object? sender, EventArgs e) => QueueFallbackLook();

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (disposed) return 0;
        if (message == 0x0200) QueueFallbackLook();
        if (message == 0x00FF) // WM_INPUT; leave unhandled so Windows can perform foreground input cleanup.
        {
            uint size = (uint)Marshal.SizeOf<RawInput>();
            uint read = GetRawInputData(lParam, 0x10000003, out var data, ref size, (uint)Marshal.SizeOf<RawHeader>());
            if (read != uint.MaxValue && read >= Marshal.SizeOf<RawInput>() && data.Header.Type == 0)
                ProcessMouse(data.Mouse.Flags, data.Mouse.X, data.Mouse.Y);
        }
        else if (message is 0x0003 or 0x0005 or 0x02E0 or 0x007E) // move, size, DPI/display change
        {
            target.Dispatcher.BeginInvoke(() =>
            {
                if (disposed) return;
                try { UpdateBounds(); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { Invalidated?.Invoke(); }
            });
        }
        return 0;
    }

    internal void ProcessMouse(ushort flags, int x, int y)
    {
        // Absolute packets cannot sustain turning at screen edges. Let their legacy
        // positions use the recentered fallback instead of treating them as deltas.
        if (disposed) return;
        if ((flags & 1) != 0) return;
        if (x == 0 && y == 0) return;
        relativeGeneration++;
        Look?.Invoke(x, y);
        QueueFallbackLook();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pointerTimer.Stop(); pointerTimer.Tick -= PointerTick;
        try
        {
            source.RemoveHook(OnMessage);
            if (registered)
            {
                RawDevice device = new() { Page = 1, Usage = 2, Flags = 1 }; // RIDEV_REMOVE requires a null target.
                RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RawDevice>()); registered = false;
            }
        }
        finally { if (clipped) { ReleaseClip(0); clipped = false; } }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawHeader { public uint Type, Size; public nint Device, WParam; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(12)] public int X;
        [FieldOffset(16)] public int Y;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawInput { public RawHeader Header; public RawMouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct PointI { public int X, Y; }
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterRawInputDevices(ref RawDevice devices, uint count, uint size);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial uint GetRawInputData(nint input, uint command, out RawInput data, ref uint size, uint headerSize);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ClipCursor(ref RectI rect);
    [LibraryImport("user32.dll", EntryPoint = "ClipCursor")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ReleaseClip(nint rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetCursorPos(out PointI point);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetCursorPos(int x, int y);
}
