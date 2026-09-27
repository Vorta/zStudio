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
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;

internal static class AiNetworkCheck
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root);
        string output = Path.Combine(Path.GetTempPath(), "zstudio-ai-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        byte[]? savedSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ProcessCommandLine = false, ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        int exit = 0;
        app.Startup += async (_, _) =>
        {
            var main = (MainWindow)app.MainWindow;
            try
            {
                await Synthetic(output);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await main.ViewModel.OpenRootAsync(root);
                string source = Path.Combine(root, "m1", "gamez.zbd"); byte[] before = SHA256.HashData(File.ReadAllBytes(source));
                var doc = await main.ViewModel.OpenFileAsync(source) ?? throw new InvalidDataException("Mission unavailable.");
                doc.SelectedAsset = doc.Assets.First(a => a.Record.Kind == AssetKind.World); await PreviewWork(main).WaitAsync(timeout.Token);
                var host = (ContentControl)main.FindName("SceneHost"); var scene = (SceneViewport)host.Content;
                Require(scene.AiNetworks.Networks.Count == 91 && scene.AiNetworks.Networks.Sum(n => n.Nodes.Count) == 592, "m1 AI counts");
                main.Width = 1250; main.Height = 850;
                ((ToggleButton)main.FindName("AiEnabled")).IsChecked = true;
                var choice = (ComboBox)main.FindName("AiNetworkCombo"); choice.SelectedIndex = 1;
                var network = scene.AiNetworks.Networks.Single(n => n.Id == scene.AiNetworkFilter); var node = network.Nodes[0];
                Require(scene.SelectAiNode(node.Id) && scene.TryFrame("selected"), "AI selection/frame");
                await Task.Delay(300); Save(Presented((Viewport3DX)scene.Content), Path.Combine(output, "m1-network.png"));
                Save(StudioCapture.Window(main), Path.Combine(output, "m1-controls.png"));
                var pose = scene.CaptureView(); string snapshot = scene.AiNetworks.Id;
                ((ToggleButton)main.FindName("HighlightSoils")).IsChecked = true;
                Require(scene.AiVisible && scene.HighlightMode == WorldHighlightMode.NonDefaultSoils && scene.CaptureView() == pose, "Highlight independence");
                main.ViewModel.Difficulty = main.ViewModel.Difficulty == MissionDifficulty.Hard ? MissionDifficulty.Easy : MissionDifficulty.Hard;
                await PreviewWork(main).WaitAsync(timeout.Token); scene = (SceneViewport)host.Content;
                Require(scene.AiNetworks.Id == snapshot && scene.SelectedAiNode == node.Id && scene.AiNetworkFilter == network.Id && scene.CaptureView() == pose, "Difficulty refresh lost AI selection/filter/camera");
                var toolbar = (ToolBar)main.FindName("SceneToolbar"); toolbar.Width = 360; toolbar.UpdateLayout(); toolbar.IsOverflowOpen = true;
                Require(ToolBar.GetIsOverflowItem((FrameworkElement)main.FindName("AiTools")), "AI group is not in native overflow");
                ((ToggleButton)main.FindName("AiThroughGeometry")).IsChecked = false;
                Require(!scene.AiThroughGeometry && scene.SelectedAiNode == node.Id, "Overflow toggle lost selection");
                await Task.Delay(200);
                Save(StudioCapture.Window(main), Path.Combine(output, "overflow.png")); toolbar.IsOverflowOpen = false; toolbar.Width = double.NaN;
                Require(!doc.IsDirty && doc.Revision == 0 && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "AI visualization changed source");
                Console.WriteLine("PASS: m1 91 networks / 592 nodes, selection/framing, difficulty preservation, highlights, overflow and unchanged source");
                Console.WriteLine("Captures: " + output);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { main.Close(); app.Shutdown(exit); }
        };
        try { app.Run(); }
        finally { if (savedSettings != null) File.WriteAllBytes(settings, savedSettings); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    private static async Task Synthetic(string output)
    {
        using var resolver = new AssetResolver(output); using var scene = new SceneViewport();
        var window = new Window { Content = scene, Width = 900, Height = 650, ShowActivated = false, ShowInTaskbar = false }; window.Show();
        try
        {
            var data = new GameScene();
            data.Materials.Add(new() { ["alpha"] = 255, ["texture_index"] = -1, ["color"] = new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = 255 } });
            data.Nodes.Add(new(0, "world", "world", null, [], [1, 2], new() { ["flags"] = 4 }, new()));
            for (int i = 0; i < 2; i++)
            {
                float z = i * -8;
                data.Models.Add(new(i, [new(-20,-20,z),new(20,-20,z),new(20,20,z),new(-20,20,z)], [], [], [new(0,0,[0,1,2,3],[],[],[])], []));
                data.Nodes.Add(new(i + 1, "wall", "object3d", i, [0], [], new() { ["flags"] = 4 }, new() { ["transform"] = new JsonArray(1,0,0,0,1,0,0,0,1,0,0,0) }));
            }
            var doc = new ZbdDocument(Path.Combine(output, "gamez.zbd"), new(0, DateTime.MinValue), new(FormatFamily.GameZ,15,Recognition.Supported,"fixture"), ReadOnlyMemory<byte>.Empty) { Scene = data };
            var asset = doc.Add(AssetKind.World, 0, "Whole world", 0, 0);
            await scene.ShowAsync(doc, asset, resolver, null, 0, default, mission: MissionSceneLoader.Build(doc, null, null, null, null));
            var view = (Viewport3DX)scene.Content; view.ShowCoordinateSystem = view.ShowViewCube = view.IsInertiaEnabled = false;
            scene.RestoreView(new(new(0, 0, 20), new(0, 0, -20), new(0, 1, 0), 50));
            AiNode a = new("a", 0, 12, new(-6, 0, -2), 0, [new(0, 1, "b", null)]);
            AiNode b = new("b", 1, 12, new(6, 0, -2), 0, [new(0, 0, "a", null)]);
            scene.SetAiNetworks(new("fixture", [new("network", "fixture.zbd", 0, "net_01.zrd", "fixture", "standard", 10, [a,b], [])]));
            await Task.Delay(250); var camera = (HelixToolkit.Wpf.SharpDX.ProjectionCamera)view.Camera!; var clip = (camera.NearPlaneDistance, camera.FarPlaneDistance); var pose = scene.CaptureView();
            byte[] original = Pixels(Presented(view));
            Require(scene.PreviewDiagnostics.All(d => !d.Message.Contains("transform", StringComparison.OrdinalIgnoreCase)), "Invalid occlusion fixture transform");
            Require(original.Count(b => b == 255) > original.Length / 5, "Occlusion fixture wall is missing");
            scene.SetAiOptions(true, true, null); await Task.Delay(250);
            var xray = Presented(view); Save(xray, Path.Combine(output, "fixture-through.png"));
            Require(!original.SequenceEqual(Pixels(xray)), "Through-geometry mode did not draw");
            var point = view.Project(new Point3D(-6, 0, -2));
            Require(scene.PickAiNode(point) == "a", "Through-geometry picking failed");
            Require(clip == (camera.NearPlaneDistance, camera.FarPlaneDistance) && scene.CaptureView() == pose, "Overlay altered camera/depth bounds");
            byte[] idle = Pixels(xray); await Task.Delay(250); Require(idle.SequenceEqual(Pixels(Presented(view))), "AI overlay is not stable at idle");
            scene.SetAiOptions(true, false, null); await Task.Delay(250);
            Save(Presented(view), Path.Combine(output, "fixture-occluded.png"));
            Require(original.SequenceEqual(Pixels(Presented(view))), "Depth-tested nodes/links showed through wall");
            Require(scene.PickAiNode(point) == null, "Occluded marker remained selectable");
            // Transparent meshes do not write depth in the application. Picking
            // must agree with the visible marker behind those surfaces.
            var walls = view.Items.OfType<MeshGeometryModel3D>().ToArray(); Require(walls.Length == 2, "Fixture wall batches");
            foreach (var wall in walls) wall.IsTransparent = true;
            await Task.Delay(200);
            Require(!original.SequenceEqual(Pixels(Presented(view))) && scene.PickAiNode(point) == "a", "Transparent surfaces incorrectly block AI picking");
            foreach (var wall in walls) wall.IsTransparent = false;
            scene.SetAiOptions(false, false, null); await Task.Delay(100);
            Require(original.SequenceEqual(Pixels(Presented(view))), "Disabling AI did not restore the scene");
            Console.WriteLine("PASS: GPU through/occluded modes, reciprocal arrows, visibility-aware picking, unchanged clipping/camera and idle back buffer");
        }
        finally { window.Close(); }
    }
    private static Task PreviewWork(MainWindow main) => (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
    private static BitmapSource Presented(Viewport3DX viewport)
    {
        using MemoryStream stream = new();
        HelixToolkit.SharpDX.Utilities.ScreenCapture.SaveWICTextureToBitmapStream(viewport.RenderHost!.EffectsManager!, viewport.RenderHost.RenderBuffer!.BackBuffer!.Resource as SharpDX.Direct3D11.Texture2D, stream);
        stream.Position = 0; return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
    private static byte[] Pixels(BitmapSource image) { var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); byte[] bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(bytes, converted.PixelWidth * 4, 0); return bytes; }
    private static void Save(BitmapSource image, string path) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
