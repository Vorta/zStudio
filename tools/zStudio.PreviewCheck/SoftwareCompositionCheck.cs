using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using zStudio.Core;
using zStudio.Rendering;

/// <summary>
/// WPF composing in software, as over Remote Desktop: the 3D view still draws, captures and is composed from its software copy.
/// Then the view's reaction to WPF reporting its front buffer lost and available again (a display change, a locked session or a
/// reset device): it keeps drawing while the buffer is gone and draws again once it is back.
/// </summary>
internal static class SoftwareCompositionCheck
{
    public static int Run(string root)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; int result = 0;
        app.Startup += async (_, _) =>
        {
            using var preview = new SceneViewport();
            var window = new Window { Content = preview, Width = 900, Height = 650, Left = -12000, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout();
                using var resolver = new AssetResolver(Path.GetFullPath(root));
                var world = await resolver.OpenCachedAsync(Path.Combine(resolver.Root, "m1", "gamez.zbd"), default);
                await preview.ShowAsync(world, world.Assets.First(a => a.Kind == AssetKind.World), resolver, null, 0, default);
                // Terrain fills the view from here: a frame that is not drawn shows the background.
                var low = new SceneViewport.ViewPose(new(2065, 72, 1702), new(-24, -40, -9), new(0, 1, 0), 45);
                preview.RestoreView(low); await Task.Delay(1200);
                Console.WriteLine($"Software composition: render tier {RenderCapability.Tier >> 16}, front buffer available {preview.FrontBufferAvailable?.ToString() ?? "(no surface)"}.");
                // The point of the check: WPF composing in software reports the front buffer unavailable, as over Remote Desktop.
                if (preview.FrontBufferAvailable != false) throw new InvalidDataException("WPF did not report its front buffer unavailable under software composition; the check no longer tests that path.");
                Check("software composition");
                // Lost: the view keeps drawing (WPF shows its software copy) and capturing, also after the camera moves.
                preview.SimulateFrontBufferAvailability(false);
                preview.RestoreView(low with { Position = low.Position + new System.Windows.Media.Media3D.Vector3D(1, 0, 0) }); await Task.Delay(600);
                Check("front buffer lost");
                // Back: the view rebuilds its surface and draws again.
                preview.SimulateFrontBufferAvailability(true);
                preview.RestoreView(low); await Task.Delay(600);
                Check("front buffer back");
                Console.WriteLine("PASS: the 3D view draws and captures under software composition and through a lost and regained front buffer.");

                void Check(string label)
                {
                    var capture = preview.RenderImage(320, 240);
                    var presented = preview.CapturePresentedFrame();
                    var composed = preview.RenderFrameReadAsComposed() ?? throw new InvalidDataException($"{label}: no frame was rendered for the composed read.");
                    int background = BackgroundPixels(presented), composedBackground = BackgroundPixels(composed), clear = TransparentPixels(composed);
                    var (mean, worst) = BlockDifference(presented, composed);
                    Console.WriteLine($"{label}: capture {capture.PixelWidth} × {capture.PixelHeight}; presented background {background}; composed background {composedBackground}, transparent {clear}, block difference mean {mean:0.0}, worst {worst:0.0}.");
                    if (capture.PixelWidth != 320 || background > 0 || composedBackground > 0 || clear > composed.PixelWidth * composed.PixelHeight / 100 || mean > 8 || worst > 40)
                    {
                        Save(composed, Path.Combine(Path.GetTempPath(), "zbd-software-composition-" + label.Replace(' ', '-') + ".png"));
                        throw new InvalidDataException($"{label}: the view is not drawn as WPF composes it.");
                    }
                }
            }
            catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
            finally { window.Close(); app.Shutdown(result); }
        };
        app.Run(); return result;
    }

    private static byte[] Pixels(BitmapSource image)
    { var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); byte[] data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0); return data; }
    // Pixels of the preview background (#1A1F26): what a frame shows where nothing was drawn.
    private static int BackgroundPixels(BitmapSource image)
    {
        var data = Pixels(image); int count = 0;
        for (int i = 0; i < data.Length; i += 4) if (data[i] == 38 && data[i + 1] == 31 && data[i + 2] == 26) count++;
        return count;
    }
    private static int TransparentPixels(BitmapSource image)
    {
        var data = Pixels(image); int count = 0;
        for (int i = 3; i < data.Length; i += 4) if (data[i] < 250) count++;
        return count;
    }
    // Mean and worst absolute colour difference of the two images' averages over a 16 × 10 grid (sizes and filtering may differ).
    private static (double Mean, double Worst) BlockDifference(BitmapSource a, BitmapSource b)
    {
        double[] Blocks(BitmapSource image)
        {
            var data = Pixels(image); int w = image.PixelWidth, h = image.PixelHeight;
            var sums = new double[16 * 10 * 3]; var counts = new int[16 * 10];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int block = y * 10 / h * 16 + x * 16 / w, i = (y * w + x) * 4;
                    sums[block * 3] += data[i]; sums[block * 3 + 1] += data[i + 1]; sums[block * 3 + 2] += data[i + 2]; counts[block]++;
                }
            for (int k = 0; k < sums.Length; k++) sums[k] /= Math.Max(1, counts[k / 3]);
            return sums;
        }
        var first = Blocks(a); var second = Blocks(b);
        double total = 0, worst = 0;
        for (int k = 0; k < first.Length; k++) { double d = Math.Abs(first[k] - second[k]); total += d; worst = Math.Max(worst, d); }
        return (total / first.Length, worst);
    }
    private static void Save(BitmapSource image, string path) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
}
