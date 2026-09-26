using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Desktop;

internal static partial class StudioCapture
{
    internal static BitmapSource Window(Window window)
    {
        if(!window.IsVisible || window.WindowState==WindowState.Minimized) throw new StudioCommandException("not_visible","Restore the requested zStudio window before capturing it.");
        nint handle=new WindowInteropHelper(window).Handle;
        if(!GetWindowRect(handle,out var rect)) throw new InvalidOperationException("Cannot read window bounds.");
        int width=rect.Right-rect.Left,height=rect.Bottom-rect.Top;
        if(width<=0 || height<=0 || (long)width*height>32_000_000) throw new InvalidOperationException("Window capture dimensions exceed the supported limit.");
        nint screen=GetDC(0),memory=0,bitmap=0,previous=0;
        try
        {
            memory=CreateCompatibleDC(screen); bitmap=CreateCompatibleBitmap(screen,width,height);
            if(screen==0 || memory==0 || bitmap==0) throw new InvalidOperationException("Cannot allocate window capture.");
            previous=SelectObject(memory,bitmap);
            if(!PrintWindow(handle,memory,2)) throw new InvalidOperationException("Windows could not capture this window.");
            var source=Imaging.CreateBitmapSourceFromHBitmap(bitmap,0,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions()); source.Freeze(); return source;
        }
        finally { if(previous!=0) SelectObject(memory,previous); if(bitmap!=0) DeleteObject(bitmap); if(memory!=0) DeleteDC(memory); if(screen!=0) ReleaseDC(0,screen); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [LibraryImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint handle,out Rect rect);
    [LibraryImport("user32.dll")] private static partial nint GetDC(nint handle);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint handle,nint dc);
    [LibraryImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static partial bool PrintWindow(nint handle,nint dc,uint flags);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleBitmap(nint dc,int width,int height);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc,nint obj);
    [LibraryImport("gdi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint dc);
}
