using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;

internal static class TextureDpiCheck
{
    public static int Run(string path)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false };
        app.InitializeComponent();
        int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow;
            try
            {
                var doc = await window.ViewModel.OpenFileAsync(Path.GetFullPath(path)) ?? throw new InvalidDataException(path);
                var sample = doc.Assets.First(a => a.Record.Content is TextureInfo { Width: 640, Height: 480 });
                var image = (Image)window.FindName("TextureImage");
                var zoom = (Slider)window.FindName("ZoomSlider");
                var scroll = (ScrollViewer)window.FindName("ImageScroll");
                var toolbar = (WrapPanel)window.FindName("ImageToolbar");
                doc.SelectedAsset = sample;
                await WaitForPreview();
                CheckScreenSize(1);

                foreach (double scale in new[] { 0.5, 2.0 })
                {
                    zoom.Value = scale;
                    window.UpdateLayout();
                    CheckScreenSize(scale);
                }
                toolbar.Children.OfType<Button>().Single(b => Equals(b.Content, "Fit")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                if (image.ActualWidth > scroll.ViewportWidth + 1 || image.ActualHeight > scroll.ViewportHeight + 1)
                    throw new InvalidOperationException("Fit exceeds the texture viewport.");
                toolbar.Children.OfType<Button>().Single(b => Equals(b.Content, "1:1")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                CheckScreenSize(1);

                zoom.Value = 2;
                doc.SelectedAsset = doc.Assets.First(a => a != sample && a.Record.Content is TextureInfo);
                await WaitForPreview();
                CheckScreenSize(1);
                doc.SelectedAsset = sample;
                await WaitForPreview();
                CheckScreenSize(1);

                string output = Path.GetFullPath("artifacts/texture-dpi-preview.png");
                var dpi = VisualTreeHelper.GetDpi(window);
                RenderTargetBitmap rendered = new((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                rendered.Render(window);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(rendered));
                using (var stream = File.Create(output)) encoder.Save(stream);
                Console.WriteLine($"PASS: physical pixel dimensions at {dpi.DpiScaleX:P0} display scaling; 50%/100%/200% zoom, Fit, 1:1 button, and texture-selection reset. Render: {output}");

                async Task WaitForPreview()
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var empty = (TextBlock)window.FindName("EmptyPreview");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    while (empty.Visibility == Visibility.Visible)
                    {
                        if (empty.Text.StartsWith("Preview unavailable", StringComparison.Ordinal)) throw new InvalidDataException(empty.Text);
                        await Task.Delay(25, timeout.Token);
                    }
                    window.UpdateLayout();
                }
                void CheckScreenSize(double expectedZoom)
                {
                    if (zoom.Value != expectedZoom) throw new InvalidOperationException($"Expected zoom {expectedZoom}, got {zoom.Value}.");
                    var bitmap = (BitmapSource)image.Source;
                    Point topLeft = image.PointToScreen(new Point(0, 0));
                    Point bottomRight = image.PointToScreen(new Point(image.ActualWidth, image.ActualHeight));
                    double width = bottomRight.X - topLeft.X, height = bottomRight.Y - topLeft.Y;
                    if (Math.Abs(width - bitmap.PixelWidth * expectedZoom) > 0.01 || Math.Abs(height - bitmap.PixelHeight * expectedZoom) > 0.01)
                        throw new InvalidOperationException($"{bitmap.PixelWidth}×{bitmap.PixelHeight} at {expectedZoom:P0} occupies {width}×{height} screen pixels.");
                    Console.WriteLine($"{bitmap.PixelWidth}×{bitmap.PixelHeight} at {expectedZoom:P0}: {width}×{height} physical screen pixels");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        app.Run();
        return exit;
    }
}
