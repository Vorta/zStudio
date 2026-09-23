using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] is "--pickup-editor" or "--pickup-pointer") return PickupEditorCheck.Run(args[1], args[0] == "--pickup-pointer");
        if (args.Length == 2 && args[0] == "--placements") return MissionPlacementPreviewCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--difficulty") return DifficultyPreviewCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--ground") return GroundPreviewCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--camera-follow") return CameraFollowCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--animation-layout") return AnimationLayoutCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--render-stability") return RenderStabilityCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--animation") return AnimationCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--mission") return MissionPreviewCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--alpha") return AlphaCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--lod") return LodCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--overview") { Overview(args[1]); return 0; }
        if (args.Length == 2 && args[0] == "--lifecycle") return Lifecycle(args[1]);
        if (args.Length == 2 && args[0] == "--texture-dpi") return TextureDpiCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--scene-controls") return SceneControlsCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--scene-depth") return SceneDepthCheck.Run(args[1]);
        if (args.Length == 2 && args[0] == "--audio")
        {
            using var reader = new NAudio.Wave.WaveFileReader(args[1]); using var player = new NAudio.Wave.WasapiPlayerBuilder().Build(); player.Init(reader); player.Volume = 0; player.Play(); System.Threading.Thread.Sleep(150); player.Stop(); Console.WriteLine("WASAPI initialized, played silently, and stopped successfully"); return 0;
        }
        if (args.Length < 2) { Console.Error.WriteLine("Usage: PreviewCheck <output> <dataset-root> [...]"); return 2; }
        // Exercises application controls through their normal binding and rendering lifecycle.
        // This runner owns its test window; it does not automate other applications.
        var application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false };
        application.InitializeComponent();
        return Run(application, args);
    }
    private static int Run(Application app, string[] args)
    {
        int exit = 0; Directory.CreateDirectory(args[0]); List<object> results = [];
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            try
            {
                foreach (string rootArg in args.Skip(1))
                {
                    string root = Path.GetFullPath(rootArg); string tag = Path.GetFileName(root);
                    await window.ViewModel.OpenRootAsync(root);
                    foreach (var file in window.ViewModel.Files.Where(f => f.Probe.Family is FormatFamily.GameZ))
                    {
                        Console.WriteLine("Render " + file.RelativePath); var model = await window.ViewModel.OpenFileAsync(file.Path) ?? throw new InvalidDataException(file.Path);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        // Wait for asynchronous binding-triggered preview completion, bounded by a timeout.
                        var info = (TextBlock)window.FindName("PreviewInfo"); var empty = (TextBlock)window.FindName("EmptyPreview");
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                        while (empty.Visibility == Visibility.Visible && !empty.Text.StartsWith("Preview unavailable", StringComparison.Ordinal)) await Task.Delay(100, timeout.Token);
                        if (empty.Visibility == Visibility.Visible) throw new InvalidDataException(empty.Text);
                        var scene = ((ContentControl)window.FindName("SceneHost")).Content as SceneViewport ?? throw new InvalidDataException("World preview did not attach");
                        await Task.Delay(500); var image = scene.RenderImage(960, 640); string name = tag + "_" + Path.GetFileName(Path.GetDirectoryName(file.Path)); Save(image, Path.Combine(args[0], name + ".png"));
                        results.Add(new { file = file.Path, preview = info.Text, width = image.PixelWidth, height = image.PixelHeight });
                        window.ViewModel.Close(model);
                    }
                    // Preview representative textures, audio, scripts, animation, and ZRD.
                    HashSet<AssetKind> covered = [];
                    foreach (var file in window.ViewModel.Files.Where(f => f.Probe.Recognition == Recognition.Supported && f.Probe.Family != FormatFamily.GameZ))
                    {
                        if (covered.Count == 5) break;
                        var doc = await FormatRegistry.Default.OpenAsync(file.Path); var sample = doc.Assets.FirstOrDefault(a => a.Kind is AssetKind.Texture or AssetKind.Sound or AssetKind.Script or AssetKind.Animation or AssetKind.Zrd && !covered.Contains(a.Kind));
                        if (sample == null) continue;
                        var model = await window.ViewModel.OpenFileAsync(file.Path) ?? throw new InvalidDataException(file.Path); model.SelectedAsset = model.Assets.First(a => a.Record.Id == sample.Id);
                        var empty = (TextBlock)window.FindName("EmptyPreview"); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        do { await Task.Delay(100, timeout.Token); } while (empty.Visibility == Visibility.Visible && !empty.Text.StartsWith("Preview unavailable", StringComparison.Ordinal));
                        if (empty.Visibility == Visibility.Visible) throw new InvalidDataException(empty.Text);
                        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
                        RenderTargetBitmap image = new((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                        image.Render(window); Save(image, Path.Combine(args[0], tag + "_" + sample.Kind + "_layout.png"));
                        results.Add(new { file = file.Path, kind = sample.Kind.ToString(), asset = sample.Name }); covered.Add(sample.Kind); window.ViewModel.Close(model);
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); results.Add(new { error = ex.ToString() }); exit = 1; }
            finally { File.WriteAllText(Path.Combine(args[0], "preview-results.json"), JsonSerializer.Serialize(results, JsonData.Options)); window.Close(); app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    private static void Save(BitmapSource image, string path) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
    private static int Lifecycle(string root)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int result = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow;
            try
            {
                var scan = window.ViewModel.OpenRootAsync(root); await Task.Delay(100);
                await window.ViewModel.OpenRootAsync(root); await scan;
                Console.WriteLine("Root folders: " + string.Join(", ", window.ViewModel.Folders.Single().Children.Where(n => n.File == null).Select(n => n.Name)));
                var file = window.ViewModel.Files.First(f => f.Probe.Family == FormatFamily.TexturePack);
                var doc = await window.ViewModel.OpenFileAsync(file.Path) ?? throw new Exception("Opening after root replacement failed");
                if (await window.ViewModel.OpenFileAsync(file.Path) != doc || window.ViewModel.Documents.Count != 1) throw new Exception("Duplicate tab identity failed");
                for (int i = 0; i < 20; i++) doc.SelectedAsset = doc.Assets[i % doc.Assets.Count];
                await Task.Delay(500); window.ViewModel.Close(doc);
                var game = window.ViewModel.Files.First(f => f.Probe.Family == FormatFamily.GameZ);
                var modelDoc = await window.ViewModel.OpenFileAsync(game.Path) ?? throw new Exception("GameZ open failed");
                modelDoc.SelectedAsset = modelDoc.Assets.Where(a => a.Record.Kind == AssetKind.Model).OrderByDescending(a => ((GameModel)a.Record.Content!).Vertices.Length).First();
                await Task.Delay(200); var empty = (TextBlock)window.FindName("EmptyPreview"); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (empty.Visibility == Visibility.Visible && !empty.Text.StartsWith("Preview unavailable", StringComparison.Ordinal)) await Task.Delay(100, timeout.Token);
                if (empty.Visibility == Visibility.Visible) throw new Exception(empty.Text);
                var scene = (SceneViewport)((ContentControl)window.FindName("SceneHost")).Content;
                scene.SetWireframe(true); scene.SetTextured(false); scene.SetFly(true); scene.SetBounds(true); scene.Isolate(-1); scene.FrameAll(); await Task.Delay(200);
                Save(scene.RenderImage(800, 600), Path.Combine("artifacts", "individual-model.png"));
                window.ViewModel.Close(modelDoc);
                string copy = Path.GetFullPath(Path.Combine("artifacts", "lifecycle-copy.zbd")); File.Copy(file.Path, copy, true);
                var changed = await window.ViewModel.OpenFileAsync(copy) ?? throw new Exception("Copied file open failed");
                File.SetLastWriteTimeUtc(copy, changed.Document.Stamp.LastWriteUtc.AddSeconds(2)); window.ViewModel.CheckExternalChanges();
                if (!changed.IsStale) throw new Exception("External file change not detected"); await window.ViewModel.ReloadAsync();
                if (window.ViewModel.SelectedDocument?.IsStale != false) throw new Exception("Reload did not clear stale state");
                Console.WriteLine("PASS: automatic cancellation on root replacement, opening afterward, duplicate tabs, rapid selection, individual model rendering/options/isolation, external changes and reload");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { window.Close(); app.Shutdown(result); }
        };
        app.Run(); return result;
    }
    private static void Overview(string directory)
    {
        var files = Directory.GetFiles(directory, "*_m*.png").Order().ToArray(); int width = 1440, height = ((files.Length + 3) / 4) * 265;
        DrawingVisual visual = new(); using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int i = 0; i < files.Length; i++) { int x = i % 4 * 360, y = i / 4 * 265; BitmapImage bitmap = new(new Uri(Path.GetFullPath(files[i]))); drawing.DrawImage(bitmap, new Rect(x, y, 360, 240)); drawing.DrawText(new FormattedText(Path.GetFileNameWithoutExtension(files[i]), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.Black, 1), new Point(x + 8, y + 243)); }
        }
        RenderTargetBitmap output = new(width, height, 96, 96, PixelFormats.Pbgra32); output.Render(visual); Save(output, Path.Combine(directory, "overview.png"));
    }
}
