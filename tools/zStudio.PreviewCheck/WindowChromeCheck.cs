using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using zStudio.Desktop;

/// <summary>The custom title bar's hit tests: caption drag, menu input, resize edge and full-height caption buttons.</summary>
internal static class WindowChromeCheck
{
    internal static void CheckChrome(MainWindow window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        nint Hit(Point point) { int x = (int)point.X,y = (int)point.Y; return SendMessage(handle,0x84,0,(nint)((y << 16) | (x & 0xffff))); }
        var title = ((FrameworkElement)window.FindName("TitleArea")).PointToScreen(new Point(70,20));
        var menu = ((Menu)window.FindName("AppMenu")).PointToScreen(new Point(15,20));
        GetWindowRect(handle,out var bounds);
        nint caption = Hit(title),input = Hit(menu),edge = Hit(new(bounds.Left+2,(bounds.Top+bounds.Bottom)/2));
        Console.WriteLine($"Chrome: caption={caption}, menu={input}, resize={edge}");
        Require(caption == 2 && input == 1 && edge == 10,"Chrome drag/menu/resize hit tests failed");
        foreach (string name in new[] { "CaptionMinimize","CaptionMaximize","CaptionClose" })
        {
            var button = (Button)window.FindName(name);
            Require(Math.Abs(button.ActualHeight - ((FrameworkElement)window.FindName("TitleArea")).ActualHeight) < .1,"Caption control does not fill title height");
            foreach (double y in new[] { 4d,21d,38d })
                Require(Hit(button.PointToScreen(new Point(button.ActualWidth * .5,y))) == (name == "CaptionMaximize" ? 9 : 1),"Caption control has an incorrect full-height hit region");
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern nint SendMessage(nint handle,uint message,nint wparam,nint lparam);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle,out NativeRect rect);
    private static void Require(bool condition,string message) { if (!condition) throw new InvalidDataException(message); }
}
