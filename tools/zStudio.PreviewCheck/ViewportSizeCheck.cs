using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Rendering;

internal static class ViewportSizeCheck
{
    public static int Run(string root)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; int result = 0;
        app.Startup += async (_, _) =>
        {
            using var preview = new SceneViewport();
            var window = new Window { Content = preview, Width = 900, Height = 650, Left = -12000, ShowInTaskbar = false };
            bool busy = true;
            // Reproduce a large scene's render queue without relying on GPU speed.
            // Normal work and actual rendering remain available; Background cannot run.
            void RenderQueue() { if (busy) window.Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)RenderQueue); }
            try
            {
                window.Show(); window.UpdateLayout(); RenderQueue();
                using var resolver = new AssetResolver(Path.GetFullPath(root));
                GameScene scene = new(); scene.Materials.Add(new() { ["alpha"] = 255 });
                var model = new GameModel(0, [new(-1,-1,0), new(1,-1,0), new(0,1,0)], [], [], [new(0, 0, [0,1,2], [], [], [])], []);
                scene.Models.Add(model);
                scene.Nodes.Add(new(0, "fixture", "object3d", 0, [], [], new() { ["flags"] = 4 }, []));
                var document = new AssetDocument("fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, ""), ReadOnlyMemory<byte>.Empty) { Scene = scene };
                await preview.ShowAsync(document, new() { Id = new("fixture", AssetKind.Model, 0), Name = "fixture", Content = model }, resolver, null, 0, default);
                await Check("initial load");
                var camera = preview.CaptureView();
                window.Width = 760; window.Height = 820; window.UpdateLayout(); await Check("portrait resize");
                window.Width = 1100; window.Height = 540; window.UpdateLayout(); await Check("wide resize");
                if (preview.CaptureView() != camera) throw new InvalidDataException("Resizing changed the camera pose.");
                Console.WriteLine("PASS: native D3D buffer follows initial layout and portrait/wide resizes while Background is starved; camera retained.");
                // A refreshed preview loads off screen and replaces the shown one; a capture can come before its first layout pass,
                // its Loaded event and its queued resize. It still renders a frame, at the view's own size.
                using (var fresh = new SceneViewport())
                {
                    await fresh.ShowAsync(document, new() { Id = new("fixture", AssetKind.Model, 0), Name = "fixture", Content = model }, resolver, null, 0, default);
                    window.Content = fresh;
                    if (fresh.RenderSurface.IsMeasureValid || fresh.RenderSurface.ActualWidth > 0) throw new InvalidDataException("The replacement view was laid out before the capture; the check proves nothing.");
                    var image = fresh.RenderImage(320, 240);
                    var size = fresh.PresentedPixelSize; double scale = WindowScale();
                    int width = (int)((int)fresh.RenderSurface.ActualWidth * scale), height = (int)((int)fresh.RenderSurface.ActualHeight * scale);
                    if (image.PixelWidth != 320 || Math.Abs(size.Width - width) > 1 || Math.Abs(size.Height - height) > 1)
                        throw new InvalidDataException($"Capture of a just-shown view: image {image.PixelWidth} × {image.PixelHeight}, buffer {size.Width} × {size.Height}, expected {width} × {height}.");
                    // The view's own queued resize, when it runs, finds the buffer already right.
                    await Task.Delay(200);
                    if (fresh.PresentedPixelSize != size) throw new InvalidDataException($"The queued resize changed the captured view's buffer: {fresh.PresentedPixelSize} after {size}.");
                    window.Content = preview;
                }
                Console.WriteLine("PASS: a capture right after a refreshed view replaces the shown one renders at the view's size.");

                // The window's own DPI scale (the larger axis, as the buffer uses), not the view's idea of it.
                double WindowScale() { var dpi = VisualTreeHelper.GetDpi(window); return Math.Max(dpi.DpiScaleX, dpi.DpiScaleY); }
                async Task Check(string label)
                {
                    await Task.Delay(180);
                    // Inspect the presented GPU surface directly; never force
                    // rendering, capture scaling, or resizing to hide a stale size.
                    var surface = preview.PresentedPixelSize; double scale = WindowScale();
                    int width = (int)((int)preview.RenderSurface.ActualWidth * scale);
                    int height = (int)((int)preview.RenderSurface.ActualHeight * scale);
                    if (Math.Abs(surface.Width - width) > 1 || Math.Abs(surface.Height - height) > 1)
                        throw new InvalidDataException($"{label}: buffer {surface.Width} × {surface.Height}, expected {width} × {height}.");
                    Console.WriteLine($"{label}: {surface.Width} × {surface.Height} presented pixels at DPI scale {scale}.");
                }
            }
            catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
            finally { busy = false; window.Close(); app.Shutdown(result); }
        };
        app.Run(); return result;
    }
}
