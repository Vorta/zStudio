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
using HelixToolkit;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;

internal static class WorldHighlightCheck
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root);
        string output = Path.Combine(Path.GetTempPath(), "zstudio-highlights-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
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
                    var meshIds = Meshes((Viewport3DX)preview.Content).ToArray();
                    var buttons = new[] { "HighlightSoils", "HighlightCanModify", "HighlightClipTo" }.Select(n => (ToggleButton)main.FindName(n)).ToArray();
                    Require(((FrameworkElement)main.FindName("WorldHighlights")).IsVisible, "Whole world highlight controls missing");
                    for (int i = 0; i < buttons.Length; i++)
                    {
                        buttons[i].IsChecked = true;
                        await Task.Delay(150, deadline.Token);
                        Require(buttons.Count(b => b.IsChecked == true) == 1, "Highlight controls are not exclusive");
                        Require(preview.CaptureView() == pose && ReferenceEquals(host.Content, preview) && ReferenceEquals(data, preview.PreviewScene), "Highlight replaced scene/camera");
                        Require(meshIds.SequenceEqual(Meshes((Viewport3DX)preview.Content)), "Highlight rebuilt geometry");
                        Save(Presented((Viewport3DX)preview.Content), Path.Combine(output, mission + "-" + preview.HighlightMode + ".png"));
                        Console.WriteLine($"PASS: {mission} {preview.HighlightMode}, retained geometry/camera and exclusive GUI state");
                    }
                    Save(StudioCapture.Window(main), Path.Combine(output, mission + "-controls.png"));
                    // Isolation and selection identities survive material changes.
                    int node = SceneBuilder.Assemble(data!).Placements.First().NodeIndex;
                    preview.Isolate(node); var isolated = preview.CaptureView();
                    buttons[0].IsChecked = true;
                    Require(preview.CaptureView() == isolated, "Highlight changed isolation camera");
                    preview.Isolate(null); preview.RestoreView(pose);
                    // Pinned popup drafts survive GUI appearance changes; MCP retains its draft guard.
                    if (preview.Mission!.Actors.FirstOrDefault(a => a.Pickup != null) is { } pickup)
                    {
                        var state = (await main.Commands.ExecuteAsync("zstudio_state", new())).Data;
                        string id = state["preview"]!.GetValue<string>();
                        await main.Commands.ExecuteAsync("zstudio_scene_selection", new() { ["preview"] = id, ["action"] = "select", ["node"] = pickup.Root });
                        ((ToggleButton)main.FindName("PickupLocked")).IsChecked = false;
                        main.OpenCurrentProperties();
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        var popup = main.OpenPropertiesWindow!; var fields = popup.PickupFields!;
                        var input = VisualChildren(fields).OfType<ValueTextBox>().First(); string original = input.Text;
                        input.Text = "-"; buttons[0].IsChecked = true;
                        Require(input.Text == "-" && fields.HasPendingDrafts && ReferenceEquals(popup.PickupFields, fields) && doc.Revision == 0, "Highlight retargeted/committed Properties draft");
                        var operation = (await main.Commands.ExecuteAsync("zstudio_scene_options", new() { ["preview"] = id, ["changes"] = new JsonObject { ["highlight"] = "clipTo" } })).Data;
                        string operationId = operation["id"]!.GetValue<string>();
                        while (operation["State"]!.GetValue<string>() is "queued" or "running")
                        {
                            await Task.Delay(10, deadline.Token);
                            operation = (await main.Commands.ExecuteAsync("zstudio_operation", new() { ["id"] = operationId })).Data;
                        }
                        Require(operation["State"]!.GetValue<string>() == "failed" && operation["result"]!["code"]!.GetValue<string>() == "pending_drafts", "MCP highlight bypassed Properties draft guard");
                        input.Text = original; popup.Close();
                        ((ToggleButton)main.FindName("PickupLocked")).IsChecked = true;
                    }
                    // Native overflow, theme and density retain the selected mode.
                    main.Width = 740;
                    var toolbar = (ToolBar)main.FindName("SceneToolbar");
                    toolbar.Width = 380; toolbar.UpdateLayout(); toolbar.IsOverflowOpen = true;
                    Require(ToolBar.GetIsOverflowItem((FrameworkElement)main.FindName("WorldHighlights")), "Highlight group did not enter native overflow");
                    buttons[1].IsChecked = true;
                    toolbar.IsOverflowOpen = false; toolbar.Width = double.NaN;
                    foreach (string theme in new[] { "Dark", "Light" })
                        await main.Commands.ExecuteAsync("zstudio_workspace_view", new() { ["changes"] = new JsonObject { ["theme"] = theme, ["density"] = "Compact" } });
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
                Console.WriteLine("PASS: solid highlights, shared instances, mixed soils, alpha/cutouts, restoration, picking, isolation, overflow/theme/density, difficulty/navigation retention and source hashes. " + output);
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
            texture[80 + i * 2] = 31; texture[81 + i * 2] = 248;
            texture[104 + i] = i < 4 ? (byte)0 : i < 8 ? (byte)128 : (byte)255;
        }
        File.WriteAllBytes(Path.Combine(fixture, "image.zbd"), texture);
        using var resolver = new AssetResolver(fixture); using var preview = new SceneViewport();
        var window = new Window { Content = preview, Width = 1000, Height = 700, Left = 20, Top = 20, ShowInTaskbar = false, ShowActivated = false }; window.Show();
        try
        {
            GameScene scene = new(); scene.Textures.Add(new() { ["name"] = "cutout" });
            scene.Materials.Add(Material(0, 255, 255, 255)); scene.Materials.Add(Material(2, 0, 0, 255));
            var cutout = Material(1, 255, 255, 255); cutout["texture_index"] = 0; scene.Materials.Add(cutout);
            scene.Materials.Add(Material(0, 0, 0, 255)); scene.Materials.Add(Material(1, 255, 255, 255, 128));
            scene.Models.Add(new(0, [new(-.9f,-.5f,0),new(-.1f,-.5f,0),new(-.1f,.5f,0),new(-.9f,.5f,0),new(.1f,-.5f,0),new(.9f,-.5f,0),new(.9f,.5f,0),new(.1f,.5f,0)], [], [], [new(0,0,[0,1,2,3],[],[],[]),new(1,0,[4,5,6,7],[],[],[])], []));
            scene.Models.Add(Quad(1, 2, -1.8f, 1.8f, -2.1f, -.9f, 0));
            scene.Models.Add(Quad(2, 3, -5, 5, -2.5f, -.5f, -.1f));
            scene.Models.Add(Quad(3, 4, 2.6f, 3.4f, -2, -1, 0));
            scene.Nodes.Add(new(0, "world", "world", null, [], [1,2,3,4,5,6,7], new() { ["flags"] = 0x30004 }, new()));
            for (int i = 0; i < 4; i++) scene.Nodes.Add(Node(i + 1, 0, (uint)(4 | i << 16), -3 + i * 2, 1.3f));
            scene.Nodes.Add(Node(5, 1, 0x30004)); scene.Nodes.Add(Node(6, 2, 4)); scene.Nodes.Add(Node(7, 3, 0x30004));
            var doc = new ZbdDocument(Path.Combine(fixture,"gamez.zbd"), new(0,DateTime.MinValue), new(FormatFamily.GameZ,15,Recognition.Supported,"fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
            var asset = doc.Add(AssetKind.World, 0, "Whole world", 0, 0);
            string before = Snapshot(scene);
            await preview.ShowAsync(doc, asset, resolver, null, 0, default, mission: MissionSceneLoader.Build(doc, null, null, null, null));
            var viewport = (Viewport3DX)preview.Content; viewport.ShowCoordinateSystem = viewport.ShowViewCube = viewport.IsInertiaEnabled = false;
            preview.RestoreView(new(new(0,0,12), new(0,0,-12), new(0,1,0), 50));
            await Task.Delay(250);
            var meshes = Meshes(viewport).ToArray(); var materials = meshes.Select(m => m.Material).ToArray(); var pose = preview.CaptureView();
            byte[] normal = Pixels(Presented(viewport));
            foreach (var mode in new[] { WorldHighlightMode.NonDefaultSoils, WorldHighlightMode.CanModify, WorldHighlightMode.ClipTo })
            {
                preview.SetWorldHighlightMode(mode); await Task.Delay(150);
                var image = Presented(viewport); Save(image, Path.Combine(output, "fixture-" + mode + ".png"));
                int[] rgb = mode == WorldHighlightMode.NonDefaultSoils ? [255,255,0] : mode == WorldHighlightMode.CanModify ? [0,255,0] : [255,0,0];
                for (int node = 0; node < 4; node++) for (int part = 0; part < 2; part++)
                {
                    bool matches = mode == WorldHighlightMode.NonDefaultSoils ? part == 1 : mode == WorldHighlightMode.CanModify ? (node & 1) != 0 : (node & 2) != 0;
                    int[] expected = matches ? rgb : part == 0 ? [255,255,255] : [0,0,255];
                    CheckPixel(image, viewport, new(-3 + node * 2 + (part == 0 ? -.5 : .5), 1.3, 0), expected);
                }
                CheckPixel(image, viewport, new(-1.3,-1.5,0), [0,0,255]);
                CheckPixel(image, viewport, new(0,-1.5,0), [rgb[0]/2,rgb[1]/2,(rgb[2]+255)/2]);
                CheckPixel(image, viewport, new(1.3,-1.5,0), rgb);
                CheckPixel(image, viewport, new(3,-1.5,0), [rgb[0]/2,rgb[1]/2,(rgb[2]+255)/2]);
                preview.SetTextured(false); await Task.Delay(100);
                CheckPixel(Presented(viewport), viewport, new(-1.3,-1.5,0), [0,0,255]);
                CheckPixel(Presented(viewport), viewport, new(1.3,-1.5,0), rgb);
                preview.SetTextured(true);
                preview.SetWireframe(true); Require(meshes.All(m => m.FillMode == SharpDX.Direct3D11.FillMode.Wireframe), "Wireframe lost highlighted meshes"); preview.SetWireframe(false);
                await Task.Delay(100);
                var ray = new HelixToolkit.Maths.Ray(new(0,0,12), Vector3.Normalize(new(-3.5f,1.3f,-12)));
                var hitContext = new HitTestContext(viewport.RenderContext, ray, Vector2.Zero);
                List<HelixToolkit.SharpDX.HitTestResult> hits = [];
                foreach (var mesh in meshes) mesh.SceneNode.HitTest(hitContext, ref hits);
                Require(hits.Any(h => Vector3.Distance(h.PointHit, new(-3.5f,1.3f,0)) < .001f), "Highlight lost instance ray picking");
                Require(meshes.SequenceEqual(Meshes(viewport)) && preview.CaptureView() == pose, "Highlight changed geometry or camera");
            }
            preview.SetWorldHighlightMode(WorldHighlightMode.None); await Task.Delay(150);
            Require(materials.SequenceEqual(meshes.Select(m => m.Material)), "Normal material identities were not restored");
            Require(normal.SequenceEqual(Pixels(Presented(viewport))), "Normal pixels were not restored");
            Require(before == Snapshot(scene), "Highlight mutated source metadata");
            Console.WriteLine("PASS: GPU fixture exact RGB, per-instance flags, per-material soil, original texture/material alpha and normal pixel restoration.");
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
    private static IEnumerable<MeshGeometryModel3D> Meshes(Viewport3DX viewport) => viewport.Items.OfType<MeshGeometryModel3D>().Concat(viewport.Items.OfType<SortingGroupModel3D>().SelectMany(g => g.Children.OfType<MeshGeometryModel3D>()));
    private static BitmapSource Presented(Viewport3DX viewport)
    {
        using MemoryStream stream = new();
        HelixToolkit.SharpDX.Utilities.ScreenCapture.SaveWICTextureToBitmapStream(viewport.RenderHost!.EffectsManager!, viewport.RenderHost.RenderBuffer!.BackBuffer!.Resource as SharpDX.Direct3D11.Texture2D, stream);
        stream.Position = 0; return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
    private static byte[] Pixels(BitmapSource image) { var converted = new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0); byte[] bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(bytes,converted.PixelWidth*4,0); return bytes; }
    private static void CheckPixel(BitmapSource image, Viewport3DX viewport, Point3D world, int[] rgb)
    {
        var p = viewport.Project(world); int x = (int)(p.X * image.PixelWidth / viewport.ActualWidth), y = (int)(p.Y * image.PixelHeight / viewport.ActualHeight);
        var converted = new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0); byte[] pixel = new byte[4]; converted.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);
        Require(Enumerable.Range(0,3).All(i => Math.Abs(pixel[2-i]-rgb[i]) <= 4), $"At {world}: expected RGB {string.Join(',',rgb)}, got {pixel[2]},{pixel[1]},{pixel[0]}");
    }
    private static void Save(BitmapSource image,string path) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
    private static void Require(bool condition,string message) { if (!condition) throw new InvalidDataException(message); }
}
