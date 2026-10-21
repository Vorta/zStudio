using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Desktop;
using zStudio.Rendering;

internal static class WorldHighlightCheck
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root);
        string output = Path.Combine(Path.GetTempPath(), "zstudio-highlights-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zStudio", "settings.json");
        byte[]? originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent();
        int exit = 0;
        app.Startup += async (_, _) =>
        {
            var main = (MainWindow)app.MainWindow;
            main.WindowState = WindowState.Normal; main.Width = 1200; main.Height = 850;
            try
            {
                await Synthetic(output);
                await main.ViewModel.OpenRootAsync(root);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                foreach (string mission in new[] { "m1", "m2" })
                {
                    string path = Path.Combine(root, mission, "gamez.zbd");
                    if (!File.Exists(path)) continue;
                    byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
                    var doc = await main.ViewModel.OpenFileAsync(path) ?? throw new InvalidDataException("Could not open " + path);
                    doc.SelectedAsset = doc.Assets.First(a => a.Record.Kind == AssetKind.World);
                    await PreviewWork(main).WaitAsync(deadline.Token);
                    var host = (ContentControl)main.FindName("SceneHost");
                    var preview = (SceneViewport)host.Content;
                    preview.RestoreView(SceneViewport.UprightPose(preview.CaptureView()));
                    var pose = preview.CaptureView(); var data = preview.PreviewScene;
                    var meshIds = Meshes(preview).ToArray();
                    var buttons = new[] { "HighlightSoils", "HighlightCanModify", "HighlightClipTo" }.Select(n => (ToggleButton)main.FindName(n)).ToArray();
                    var group = (FrameworkElement)main.FindName("WorldHighlights");
                    // Shown on the toolbar or, when it is narrow, in its native overflow.
                    Require(group.Visibility == Visibility.Visible && (group.IsVisible || ToolBar.GetIsOverflowItem(group)), $"Whole world highlight controls missing in {mission}");
                    for (int i = 0; i < buttons.Length; i++)
                    {
                        await PresentedAfter(preview, () => buttons[i].IsChecked = true);
                        Require(buttons.Count(b => b.IsChecked == true) == (i == 2 ? 2 : 1), "Soils must be exclusive; crater highlights must combine");
                        Require(preview.CaptureView() == pose && ReferenceEquals(host.Content, preview) && ReferenceEquals(data, preview.PreviewScene), "Highlight replaced scene/camera");
                        Require(meshIds.SequenceEqual(Meshes(preview)), "Highlight rebuilt geometry");
                        Save(preview.CapturePresentedFrame(), Path.Combine(output, mission + "-" + preview.HighlightMode + ".png"));
                        Console.WriteLine($"PASS: {mission} {preview.HighlightMode}, retained geometry/camera and combined GUI state");
                    }
                    Save(StudioCapture.Window(main), Path.Combine(output, mission + "-controls.png"));
                    // Isolation and selection identities survive material changes.
                    int node = SceneBuilder.Assemble(data!).Placements.First().NodeIndex;
                    preview.Isolate(node); var isolated = preview.CaptureView();
                    buttons[0].IsChecked = true;
                    Require(preview.CaptureView() == isolated, "Highlight changed isolation camera");
                    preview.Isolate(null); preview.RestoreView(pose);
                    // Native overflow and theme retain the selected mode.
                    main.Width = 740;
                    var toolbar = (ToolBar)main.FindName("SceneToolbar");
                    toolbar.Width = 380; toolbar.UpdateLayout(); toolbar.IsOverflowOpen = true;
                    Require(ToolBar.GetIsOverflowItem((FrameworkElement)main.FindName("WorldHighlights")), "Highlight group did not enter native overflow");
                    buttons[1].IsChecked = true;
                    toolbar.IsOverflowOpen = false; toolbar.Width = double.NaN;
                    foreach (string theme in new[] { "Dark", "Light" })
                        await main.Commands.ExecuteAsync("zstudio_workspace_view", new() { ["changes"] = new JsonObject { ["theme"] = theme } });
                    Require(preview.HighlightMode == WorldHighlightMode.CanModify && ReferenceEquals(host.Content, preview), "Presentation replaced highlight renderer");
                    main.Width = 1200;
                    var retained = preview.HighlightMode;
                    main.ViewModel.Difficulty = main.ViewModel.Difficulty == MissionDifficulty.Hard ? MissionDifficulty.Easy : MissionDifficulty.Hard;
                    await PreviewWork(main).WaitAsync(deadline.Token);
                    preview = (SceneViewport)host.Content;
                    Require(preview.HighlightMode == retained, "Difficulty lost highlight mode");
                    var model = doc.Assets.First(a => a.Record.Kind == AssetKind.Model);
                    doc.SelectedAsset = model; await PreviewWork(main).WaitAsync(deadline.Token);
                    Require(!((FrameworkElement)main.FindName("WorldHighlights")).IsVisible && ((SceneViewport)host.Content).HighlightMode == WorldHighlightMode.None, "Model retained world-only highlights");
                    doc.SelectedAsset = doc.Assets.First(a => a.Record.Kind == AssetKind.World); await PreviewWork(main).WaitAsync(deadline.Token);
                    Require(((SceneViewport)host.Content).HighlightMode == retained, "World navigation lost session preference");
                    Require(doc.Revision == 0 && !doc.IsDirty && hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Highlight changed source document");
                    main.ViewModel.CloseResolved(doc);
                }
                Console.WriteLine("PASS: half-tint highlights, shared instances, mixed soils, alpha/cutouts, restoration, picking, isolation, overflow/theme, difficulty/navigation retention and source hashes. " + output);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally
            {
                foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
                main.Close(); app.Shutdown(exit);
            }
        };
        try { app.Run(); }
        finally { if (originalSettings != null) File.WriteAllBytes(settings, originalSettings); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }

    private static async Task Synthetic(string output)
    {
        string fixture = Path.Combine(output, "fixture"); Directory.CreateDirectory(fixture);
        byte[] texture = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(texture.AsSpan(4), 1); BinaryPrimitives.WriteUInt32LittleEndian(texture.AsSpan(12), 1);
        "cutout"u8.CopyTo(texture.AsSpan(24)); BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(56), 64);
        texture[64] = 9; texture[68] = 12; texture[70] = 1;
        for (int i = 0; i < 12; i++)
        {
            texture[80 + i * 2] = i < 8 ? (byte)31 : (byte)224; texture[81 + i * 2] = i < 8 ? (byte)248 : (byte)7;
            texture[104 + i] = i < 4 ? (byte)0 : i < 8 ? (byte)128 : (byte)255;
        }
        File.WriteAllBytes(Path.Combine(fixture, "image.zbd"), texture);
        using var resolver = new AssetResolver(fixture); using var preview = new SceneViewport();
        // Keep the presented fixture above its startup owner without activating it.
        // Otherwise the owner can cover it after the first await and suspend presentation.
        var window = new Window { Owner = Application.Current.MainWindow, Content = preview, Width = 1000, Height = 700, Left = 20, Top = 20, ShowInTaskbar = false, ShowActivated = false }; window.Show();
        try
        {
            GameScene scene = new(); scene.Textures.Add(new() { ["name"] = "cutout" });
            scene.Materials.Add(Material(0, 255, 255, 255)); scene.Materials.Add(Material(2, 0, 0, 255));
            var cutout = Material(1, 255, 255, 255); cutout["texture_index"] = 0; scene.Materials.Add(cutout);
            scene.Materials.Add(Material(0, 0, 0, 255)); scene.Materials.Add(Material(1, 255, 255, 255, 128));
            scene.Models.Add(new(0, [new(-.9f,-.5f,0),new(-.1f,-.5f,0),new(-.1f,.5f,0),new(-.9f,.5f,0),new(.1f,-.5f,0),new(.9f,-.5f,0),new(.9f,.5f,0),new(.1f,.5f,0)], [], [], [new(0,0,[0,1,2,3],[],[],[]) { Colors = Enumerable.Repeat(new Vector3(128,255,64),4).ToArray() },new(1,0,[4,5,6,7],[],[],[])], []));
            scene.Models.Add(Quad(1, 2, -1.8f, 1.8f, -2.1f, -.9f, 0));
            scene.Models[1].Polygons[0] = scene.Models[1].Polygons[0] with { Colors = Enumerable.Repeat(new Vector3(128,255,255),4).ToArray() };
            scene.Models.Add(Quad(2, 3, -5, 5, -2.5f, -.5f, -.1f));
            scene.Models.Add(Quad(3, 4, 2.6f, 3.4f, -2, -1, 0));
            scene.Nodes.Add(new(0, "world", "world", null, [], [1,2,3,4,5,6,7], new() { ["flags"] = 0x30004 }, new()));
            for (int i = 0; i < 4; i++) scene.Nodes.Add(Node(i + 1, 0, (uint)(4 | i << 16), -3 + i * 2, 1.3f));
            scene.Nodes.Add(Node(5, 1, 0x30004)); scene.Nodes.Add(Node(6, 2, 4)); scene.Nodes.Add(Node(7, 3, 0x30004));
            var doc = new AssetDocument(Path.Combine(fixture,"gamez.zbd"), new(0,DateTime.MinValue), new(FormatFamily.GameZ,15,Recognition.Supported,"fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
            var asset = doc.Add(AssetKind.World, 0, "Whole world", 0, 0);
            string before = Snapshot(scene);
            await preview.ShowAsync(doc, asset, resolver, null, 0, default, mission: MissionSceneLoader.Build(doc, null, null, null, null));
            preview.SetNavigationAids(false, false); preview.InertiaEnabled = false;
            await PresentedAfter(preview, () => preview.RestoreView(new(new(0,0,12), new(0,0,-12), new(0,1,0), 50)));
            var meshes = Meshes(preview).ToArray(); var materials = meshes.Select(m => m.MaterialIdentity).ToArray(); var pose = preview.CaptureView();
            byte[] normal = Pixels(preview.CapturePresentedFrame());
            foreach (var mode in new[] { WorldHighlightMode.NonDefaultSoils, WorldHighlightMode.CanModify, WorldHighlightMode.ClipTo, WorldHighlightMode.CanModifyAndClipTo })
            {
                await PresentedAfter(preview, () => preview.SetWorldHighlightMode(mode));
                Require(meshes.Any(m => m.OverlayColor.W == .5f), "Highlight did not reach normal material shader state");
                var image = preview.CapturePresentedFrame(); Save(image, Path.Combine(output, "fixture-" + mode + ".png"));
                int[] rgb = mode == WorldHighlightMode.NonDefaultSoils ? [255,255,0] : mode == WorldHighlightMode.CanModify ? [0,255,0] : [255,0,0];
                for (int node = 0; node < 4; node++) for (int part = 0; part < 2; part++)
                {
                    bool matches = mode == WorldHighlightMode.NonDefaultSoils ? part == 1 : mode == WorldHighlightMode.CanModify ? (node & 1) != 0 : mode == WorldHighlightMode.ClipTo ? (node & 2) != 0 : node != 0;
                    int[] baseRgb = part == 0 ? [128,255,64] : [0,0,255];
                    int[] tint = mode == WorldHighlightMode.CanModifyAndClipTo && (node & 2) == 0 ? [0,255,0] : rgb;
                    int[] expected = matches ? Mix(baseRgb, tint) : baseRgb;
                    CheckPixel(image, preview, new(-3 + node * 2 + (part == 0 ? -.5 : .5), 1.3, 0), expected);
                }
                CheckPixel(image, preview, new(-1.3,-1.5,0), [0,0,255]);
                CheckPixel(image, preview, new(0,-1.5,0), Mix(Mix([128,0,255], rgb), [0,0,255]));
                CheckPixel(image, preview, new(1.3,-1.5,0), Mix([0,255,0], rgb));
                CheckPixel(image, preview, new(3,-1.5,0), Mix(Mix([255,255,255], rgb), [0,0,255]));
                await PresentedAfter(preview, () => { preview.SetTextured(false); preview.SetWorldHighlightMode(WorldHighlightMode.None); });
                var untextured = preview.CapturePresentedFrame();
                await PresentedAfter(preview, () => preview.SetWorldHighlightMode(mode));
                foreach (double x in new[] { -1.3, 1.3 })
                    CheckPixel(preview.CapturePresentedFrame(), preview, new(x,-1.5,0), Mix(ReadPixel(untextured, preview, new(x,-1.5,0)), rgb));
                await PresentedAfter(preview, () =>
                {
                    preview.SetTextured(true);
                    preview.SetWireframe(true); Require(preview.PolygonOutlineCount > 0 && meshes.All(m => m.SolidFill), "Wireframe lost highlighted meshes"); preview.SetWireframe(false);
                });
                var hits = preview.HitTestScene(new Point3D(0,0,12), new Vector3D(-3.5,1.3,-12)).Where(h => h.Mesh is { } hit && meshes.Contains(hit));
                Require(hits.Any(h => Vector3.Distance(h.Point, new(-3.5f,1.3f,0)) < .001f), "Highlight lost instance ray picking");
                Require(meshes.SequenceEqual(Meshes(preview)) && preview.CaptureView() == pose, "Highlight changed geometry or camera");
            }
            await PresentedAfter(preview, () => preview.SetWorldHighlightMode(WorldHighlightMode.None));
            Require(materials.SequenceEqual(meshes.Select(m => m.MaterialIdentity)), "Normal material identities were not restored");
            Require(normal.SequenceEqual(Pixels(preview.CapturePresentedFrame())), "Normal pixels were not restored");
            // The same normal appearance and transparency must feed winding overlays
            // in static and animated paths, including vertex tint times a real texture.
            foreach (bool animated in new[] { false, true })
            {
                if (animated)
                {
                    var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = doc };
                    var nodes = SceneBuilder.Assemble(scene).Placements.Select(p => new AnimationNodePose(p.NodeIndex, p.NodeIndex, p.ModelIndex, p.Transform, true, 1, -1, 0, 0)).ToArray();
                    var frame = new AnimationFrame(0, nodes, [], [], [], null, null, default, default, [], [], []);
                    await preview.ShowAnimationAsync(context, frame, resolver, false, default);
                    preview.RestoreView(pose);
                }
                else preview.SetWorldHighlightMode(WorldHighlightMode.ClipTo);
                await PresentedAfter(preview, () => preview.SetFaceOrientation(true));
                var oriented = preview.CapturePresentedFrame();
                CheckPixel(oriented, preview, new(-3.5,1.3,0), Mix([128,255,64], [0,0,255]));
                CheckPixel(oriented, preview, new(-1.3,-1.5,0), [0,0,255]); // Texture alpha-zero hole.
                CheckPixel(oriented, preview, new(0,-1.5,0), Mix(Mix([128,0,255], [0,0,255]), [0,0,255]));
                CheckPixel(oriented, preview, new(1.3,-1.5,0), Mix([0,255,0], [0,0,255]));
                CheckPixel(oriented, preview, new(3,-1.5,0), Mix(Mix([255,255,255], [0,0,255]), [0,0,255]));
                await PresentedAfter(preview, () => preview.SetFaceOrientation(false));
                if (!animated) CheckPixel(preview.CapturePresentedFrame(), preview, new(1.3,-1.5,0), Mix([0,255,0], [255,0,0]));
                else CheckPixel(preview.CapturePresentedFrame(), preview, new(1.3,-1.5,0), [0,255,0]);
            }
            Require(before == Snapshot(scene), "Highlight mutated source metadata");
            Console.WriteLine("PASS: GPU fixture half-tint RGB, combined per-instance flags, soil, two texture colors, vertex tint, original alpha/cutouts, normal pixel restoration and static/animated face overlays.");
        }
        finally { window.Close(); }
    }

    private static JsonObject Material(int soil, int r, int g, int b, int alpha = 255) => new() { ["soil"] = soil, ["alpha"] = alpha, ["texture_index"] = -1, ["color"] = new JsonObject { ["r"] = r, ["g"] = g, ["b"] = b } };
    private static GameNode Node(int index, int model, uint flags, float x = 0, float y = 0) => new(index,"duplicate","object3d",model,[0],[],new() { ["flags"] = flags },new() { ["transform"] = new JsonArray(1,0,0,0,1,0,0,0,1,x,y,0) });
    private static GameModel Quad(int index, int material, float x0, float x1, float y0, float y1, float z) => new(index,[new(x0,y0,z),new(x1,y0,z),new(x1,y1,z),new(x0,y1,z)],[],[],[new(material,0,[0,1,2,3],[],[Vector2.Zero,Vector2.UnitX,Vector2.One,Vector2.UnitY],[])],[]);
    private static string Snapshot(GameScene scene) => string.Join('|', scene.Materials.Select(m => m.ToJsonString()).Concat(scene.Nodes.Select(n => n.Metadata.ToJsonString() + n.Data.ToJsonString())));
    private static Task PreviewWork(MainWindow window) => (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root,i); yield return child;
            foreach (var nested in VisualChildren(child)) yield return nested;
        }
    }
    // The scene's own surfaces: static batches and animation meshes, not overlays.
    private static IEnumerable<SceneViewport.RenderMeshHandle> Meshes(SceneViewport preview) => preview.EnumerateRenderMeshes().Where(m => m.Role != SceneViewport.RenderMeshRole.Overlay);
    private static async Task PresentedAfter(SceneViewport preview, Action change)
    {
        try { await preview.WaitForPresentedFrameAsync(change); }
        catch (TimeoutException error) { throw new InvalidDataException("Appearance change: " + error.Message); }
    }
    private static byte[] Pixels(BitmapSource image) { var converted = new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0); byte[] bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(bytes,converted.PixelWidth*4,0); return bytes; }
    private static int[] Mix(int[] a, int[] b) => Enumerable.Range(0,3).Select(i => (a[i] + b[i]) / 2).ToArray();
    private static int[] ReadPixel(BitmapSource image, SceneViewport preview, Point3D world)
    {
        var p = preview.ProjectToView(world); var surface = preview.RenderSurface; int x = (int)(p.X * image.PixelWidth / surface.ActualWidth), y = (int)(p.Y * image.PixelHeight / surface.ActualHeight);
        var converted = new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0); byte[] pixel = new byte[4]; converted.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);
        return [pixel[2], pixel[1], pixel[0]];
    }
    private static void CheckPixel(BitmapSource image, SceneViewport preview, Point3D world, int[] rgb)
    {
        var p = preview.ProjectToView(world); var surface = preview.RenderSurface; int x = (int)(p.X * image.PixelWidth / surface.ActualWidth), y = (int)(p.Y * image.PixelHeight / surface.ActualHeight);
        var converted = new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0); byte[] pixel = new byte[4]; converted.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);
        Require(Enumerable.Range(0,3).All(i => Math.Abs(pixel[2-i]-rgb[i]) <= 4), $"At {world}: expected RGB {string.Join(',',rgb)}, got {pixel[2]},{pixel[1]},{pixel[0]}");
    }
    private static void Save(BitmapSource image,string path) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
    private static void Require(bool condition,string message) { if (!condition) throw new InvalidDataException(message); }
}
