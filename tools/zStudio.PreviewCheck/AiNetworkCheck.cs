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
                ((ToggleButton)main.FindName("EditingUnlocked")).IsChecked = true;
                ((ToggleButton)main.FindName("AiEnabled")).IsChecked = true;
                var choice = (ComboBox)main.FindName("AiNetworkCombo"); choice.SelectedIndex = 1;
                var network = scene.AiNetworks.Networks.Single(n => n.Id == scene.AiNetworkFilter); var node = network.Nodes[0];
                Require(scene.SelectAiNode(node.Id) && scene.TryFrame("selected"), "AI selection/frame");
                await Task.Delay(300); Save(Presented((Viewport3DX)scene.RenderSurface), Path.Combine(output, "m1-network.png"));
                // Strategy is an ordinary read-only detail in the scrollable card.
                var strategyField = Descendants((SceneInspectionCard)scene.InspectionContent!).OfType<TextBox>()
                    .Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Attack strategy");
                Require(strategyField.IsReadOnly && strategyField.Text == network.AttackStrategy.Value, "Read-only stored strategy row");
                Save(StudioCapture.Window(main), Path.Combine(output, "m1-controls.png"));
                var card = (SceneInspectionCard)scene.InspectionContent!;
                foreach (var density in new[] { "Compact", "Comfortable" })
                {
                    main.Width = 1080; main.Height = 650;
                    await main.Commands.ExecuteAsync("zstudio_workspace_view", new JsonObject { ["changes"] = new JsonObject { ["density"] = density, ["tools"] = true, ["inspectionPanelHeight"] = 216 } });
                    await Task.Delay(150);
                    var panel = Descendants(card).OfType<Border>().Single(b => b.Name == "InspectionPanel");
                    var grip = Descendants(card).OfType<Thumb>().Single(t => t.Name == "InspectionResize");
                    var scroll = Descendants(card).OfType<ScrollViewer>().Single(s => s.Name == "InspectionScroll");
                    var beforeScroll = scene.CaptureView();
                    strategyField.BringIntoView(); await Task.Delay(100);
                    Require(scroll.VerticalOffset > 0 && scene.CaptureView() == beforeScroll, "Strategy reveal must scroll the details without moving the camera");
                    var top = strategyField.TranslatePoint(new(), card).Y;
                    var bottom = strategyField.TranslatePoint(new(0, strategyField.ActualHeight), card).Y;
                    Require(strategyField.IsVisible && strategyField.ActualWidth > 40 && top >= scroll.TranslatePoint(new(), card).Y - 1 && bottom <= grip.TranslatePoint(new(), card).Y,
                        "Strategy clipped in minimum window: " + density);
                    Save(StudioCapture.Window(main), Path.Combine(output, "m1-card-minimum-" + density + ".png"));
                    // WPF rounds the logical maximum to the nearest device pixel.
                    Require(panel.MaxHeight <= card.ActualHeight * .8 + 1e-6 &&
                        panel.ActualHeight <= panel.MaxHeight + .5 / VisualTreeHelper.GetDpi(panel).DpiScaleY + 1e-6,
                        $"Panel exceeded viewport cap: panel={panel.ActualHeight}, viewport={card.ActualHeight}, maximum={panel.MaxHeight}");
                }
                main.Width = 1250; main.Height = 850;
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
            var view = (Viewport3DX)scene.RenderSurface; view.ShowCoordinateSystem = view.ShowViewCube = view.IsInertiaEnabled = false;
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
            scene.SetPickupLocked(false);
            (string Label, AiAttackStrategy Strategy, string Hex)[] strategyCases =
            [
                ("head", AiAttackStrategy.Stored("Head-on"), "#FFA640"), ("circle", AiAttackStrategy.Stored("cIrClE"), "#33D9FF"),
                ("back", AiAttackStrategy.Stored("back"), "#B380FF"), ("follow", AiAttackStrategy.Stored("follow"), "#4DFFA6"),
                ("zigzag", AiAttackStrategy.Stored("zigzag"), "#FF66B3"), ("sit", AiAttackStrategy.Stored("sit"), "#F2F24D"),
                ("unknown", AiAttackStrategy.Stored("unknown"), "#A0A0A0"), ("empty", AiAttackStrategy.Stored(""), "#A0A0A0"),
                ("invalid", AiAttackStrategy.Invalid, "#A0A0A0"), ("missing", AiAttackStrategy.Missing, "#FF6666")
            ];
            foreach (var (text, strategy, hex) in strategyCases)
            {
                var network = new AiNetwork("network", "fixture.zbd", 0, "net_01.zrd", "fixture", "standard", 10, [a, b], []) { AttackStrategy = strategy };
                var other = network with { Id = "other", MemberIndex = 1, Nodes = [a with { Id = "c", Position = new(-6, 5, -2), Links = [] }] };
                scene.SetAiNetworks(new("strategy-" + text, [network, other]));
                foreach (bool through in new[] { true, false })
                {
                    // Transparent fixture walls allow both depth modes to expose the same markers.
                    foreach (var wall in walls) wall.IsTransparent = true;
                    scene.SetAiOptions(true, through, null); await Task.Delay(120);
                    var rendered = Presented(view);
                    Require(HasColor(rendered, view, point, hex), "Wrong marker color: " + text);
                    Require(HasColor(rendered, view, view.Project(new Point3D(0, 0, -2)), hex), "Wrong arrow/connection color: " + text);
                    Require(HasColor(rendered, view, view.Project(new Point3D(-6, 5, -2)), hex), "Same strategy differs between networks: " + text);
                    scene.SetAiOptions(true, through, "network"); await Task.Delay(100);
                    Require(HasColor(Presented(view), view, point, hex), "Filtering changed strategy color: " + text);
                }
                Require(scene.SelectAiNode("a"), "Strategy marker selection failed"); await Task.Delay(100);
                Require(HasColor(Presented(view), view, point, "#FFFFFF"), "Selected marker lost white highlight");
                Require(scene.SelectAiNode(null), "Strategy marker clearing failed"); await Task.Delay(100);
                Require(HasColor(Presented(view), view, point, hex), "Clearing selection did not restore strategy color: " + text);
                Save(Presented(view), Path.Combine(output, "strategy-" + text + ".png"));
            }
            Console.WriteLine("PASS: rendered strategy colors for markers/arrows, equal strategies across networks, filtering, both depth modes and white selection");
            Console.WriteLine("PASS: GPU through/occluded modes, reciprocal arrows, visibility-aware picking, unchanged clipping/camera and idle back buffer");
        }
        finally { window.Close(); }
    }
    private static Task PreviewWork(MainWindow main) => (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static BitmapSource Presented(Viewport3DX viewport)
    {
        using MemoryStream stream = new();
        HelixToolkit.SharpDX.Utilities.ScreenCapture.SaveWICTextureToBitmapStream(viewport.RenderHost!.EffectsManager!, viewport.RenderHost.RenderBuffer!.BackBuffer!.Resource as SharpDX.Direct3D11.Texture2D, stream);
        stream.Position = 0; return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
    private static byte[] Pixels(BitmapSource image) { var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); byte[] bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(bytes, converted.PixelWidth * 4, 0); return bytes; }
    private static bool HasColor(BitmapSource image, Viewport3DX viewport, Point point, string hex)
    {
        byte[] pixels = Pixels(image); uint rgb = Convert.ToUInt32(hex[1..], 16);
        int x = (int)Math.Round(point.X * image.PixelWidth / viewport.ActualWidth), y = (int)Math.Round(point.Y * image.PixelHeight / viewport.ActualHeight);
        for (int py = Math.Max(0, y - 5); py <= Math.Min(image.PixelHeight - 1, y + 5); py++)
        for (int px = Math.Max(0, x - 5); px <= Math.Min(image.PixelWidth - 1, x + 5); px++)
        {
            int at = (py * image.PixelWidth + px) * 4;
            if (Math.Abs(pixels[at] - (int)(rgb & 255)) <= 3 && Math.Abs(pixels[at + 1] - (int)((rgb >> 8) & 255)) <= 3 && Math.Abs(pixels[at + 2] - (int)((rgb >> 16) & 255)) <= 3) return true;
        }
        return false;
    }
    private static void Save(BitmapSource image, string path) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
