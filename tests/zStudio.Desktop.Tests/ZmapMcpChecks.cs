using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core.Formats;
using zStudio.Core.Worlds;
using zStudio.Mcp;
using zStudio.Rendering;
using zStudio.Tests;
using Xunit;
using zStudio.Core;

namespace zStudio.Desktop.Tests;

internal static class ZmapMcpChecks
{
    internal static async Task Run()
    {
        using SourceWorldFixture fixture = new();
        const string modelPath = "data/m1/models/m1.gltf";
        var model = JsonNode.Parse(File.ReadAllBytes(fixture.Path(modelPath)))!;
        model["nodes"]![0]!["extras"]![WorldGltf.Key]!["zone"] = "any";
        fixture.Write(modelPath, model.ToJsonString());
        fixture.Write("data/m1/zrdr/objectives.zrd", "( READ_TIME ( 8 ) OBJECTIVE1 ( obj1 MSG1 MSG2 MSG3 ) OBJECTIVE2 ( obj2 MSG4 MSG5 MSG6 ) )");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var token = deadline.Token;
        MainWindow main = new() { Left = -12000, ShowInTaskbar = false };
        main.Show();
        try
        {
            await using LocalMcpHost host = new(main.Commands, "test");
            await using NamedPipeClientStream pipe = new(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var opened = await Job("source_world_open", new() { ["mission"] = "m1" });
            string document = opened["document"]!["id"]!.GetValue<string>();
            string preview = (await Call("state", new()))["preview"]!.GetValue<string>();
            var view = (SceneViewport)typeof(MainWindow).GetField("scene", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
            var layer = (ZmapOverlay)main.FindName("ZmapLayer");
            await Call("workspace_view", new() { ["changes"] = new JsonObject { ["navigatorWidth"] = 200 } });
            var prior = view.CaptureView();
            JsonNode state = await Job("source_zmap", new() { ["action"] = "enter", ["document"] = document });
            Assert.False(state["loaded"]!.GetValue<bool>());
            Assert.Equal(2, state["objectives"]!.GetValue<int>());
            Assert.True(view.IsZmapView);
            Assert.Equal("orthographic", view.CaptureView().Projection);
            Assert.Equal("top", view.CaptureView().AxisView);
            Assert.Equal("mode_active", (await Call("camera", new() { ["action"] = "rotate", ["preview"] = preview }, error: true))["code"]!.GetValue<string>());
            var unfiltered = view.ZmapWorldBounds;
            var top = view.CaptureView();
            int groundNode = view.PreviewScene!.Nodes.Single(n => n.Name == "ground").Index;
            Assert.Equal(255, Assert.Single(state["terrainZones"]!.AsArray())!["zone"]!.GetValue<int>());
            // The Terrain zones row's eye is a toggle: UI Automation hides the zone as a click does.
            var terrainRows = (Panel)typeof(MainWindow).GetField("zmapTerrainRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
            var any = Assert.Single(Assert.IsAssignableFrom<Panel>(Assert.Single(terrainRows.Children)).Children.OfType<ToggleButton>());
            Assert.True(any.IsVisible && any.IsChecked == true);
            var toggle = Assert.IsAssignableFrom<IToggleProvider>(new ToggleButtonAutomationPeer(any).GetPattern(PatternInterface.Toggle));
            toggle.Toggle();
            state = await Job("source_zmap", new() { ["action"] = "state" });
            Assert.False(state["loaded"]!.GetValue<bool>());
            Assert.False(state["terrainZones"]![0]!["visible"]!.GetValue<bool>());
            Assert.False(view.TryFrame("all"));
            Assert.Equal(top, view.CaptureView());
            view.SetHiddenNodes([groundNode]);
            await Edit("terrain_visibility", new() { ["visible"] = true });
            Assert.False(view.TryFrame("all"));
            Assert.Equal(new[] { groundNode }, view.HiddenNodes);
            view.SetHiddenNodes([]);
            view.Isolate(groundNode);
            await Edit("terrain_visibility", new() { ["zone"] = 255, ["visible"] = false });
            Assert.False(view.TryFrame("all"));
            await Edit("terrain_visibility", new() { ["zone"] = 255, ["visible"] = true });
            Assert.True(view.TryFrame("all", manual: false));
            view.Isolate(null);
            CheckOutlinesRoom();
            await CheckTerrainHeight();

            string input = Path.Combine(fixture.Root, "in.zmap"), output = Path.Combine(fixture.Root, "m1.zmap");
            var start = new Vector3(4, 7, 4);
            byte[] original = ZmapCodec.Write(new(31, Vector3.Zero, new(64, 0, 64),
                [new(Guid.NewGuid(), new(0, 0, 255), -1, [new(Guid.NewGuid(), start), new(Guid.NewGuid(), new(18, 9, 4)),
                    new(Guid.NewGuid(), new(18, 13, 18)), new(Guid.NewGuid(), new(4, 11, 18))])]), token);
            File.WriteAllBytes(input, original);
            await Edit("open", new() { ["path"] = input });
            Assert.Equal(input, state["opened"]!.GetValue<string>());
            Assert.Equal(input, state["path"]!.GetValue<string>());
            var outline = (await Job("source_zmap", Args("outlines")))["outlines"]![0]!["Id"]!.GetValue<string>();
            var vertexArgs = Args("vertices");
            vertexArgs["outline"] = outline;
            var points = (await Job("source_zmap", vertexArgs))["vertices"]!;
            var vertex = points[0]!["Id"]!.GetValue<string>();
            var diagonal = points[2]!["Id"]!.GetValue<string>();
            var last = points[3]!["Id"]!.GetValue<string>();
            await Edit("select", new() { ["outline"] = outline, ["vertex"] = vertex });
            await Edit("frame", new() { ["target"] = "outline" });
            AssertAligned();
            await Call("camera", new() { ["action"] = "pan", ["preview"] = preview, ["horizontal"] = 20, ["vertical"] = 30 });
            await Call("camera", new() { ["action"] = "zoom", ["preview"] = preview, ["steps"] = 1 });
            main.Width -= 35;
            main.UpdateLayout();
            AssertAligned();
            await CheckSnap();
            await Edit("draft_set", new() { ["objectiveIndex"] = 1, ["rgb"] = "#12ABEF", ["position"] = new[] { 6, 7, 8 } });
            var oldDraft = Args("draft_apply");
            await Edit("draft_set", new() { ["objectiveIndex"] = 0 });
            Assert.Equal("stale_revision", (await Job("source_zmap", oldDraft, "failed"))["code"]!.GetValue<string>());
            await Edit("draft_apply");
            await Edit("insert_vertex", new() { ["outline"] = outline, ["vertex"] = last, ["fraction"] = .5 });
            Guid inserted = state["vertex"]!.GetValue<Guid>();
            var accepted = layer.Editor!.FindOutline(Guid.Parse(outline));
            Assert.Equal(new Vector3(5, 9, 13), accepted.Vertices[^1].Position);
            await Edit("remove_vertex", new() { ["outline"] = outline, ["vertex"] = inserted.ToString() });
            await Edit("undo");
            Assert.Equal(inserted, layer.Editor.FindOutline(Guid.Parse(outline)).Vertices[^1].Id);
            await Edit("redo");
            await Edit("select", new() { ["outline"] = outline, ["vertex"] = vertex });
            var beforeSplit = layer.Editor.Map;
            await Edit("draft_split");
            await Edit("terrain_height", new() { ["y"] = 2 });
            Assert.Equal("split", state["draft"]!.GetValue<string>());
            Assert.Same(beforeSplit, layer.Editor.Map);
            await Edit("terrain_height");
            bool dirtyBeforeVisibility = state["dirty"]!.GetValue<bool>();
            await Edit("terrain_visibility", new() { ["zone"] = 255, ["visible"] = false });
            Assert.Equal("split", state["draft"]!.GetValue<string>());
            Assert.Equal(dirtyBeforeVisibility, state["dirty"]!.GetValue<bool>());
            Assert.Same(beforeSplit, layer.Editor.Map);
            await Edit("terrain_visibility", new() { ["visible"] = true });
            Assert.Equal(vertex, state["split"]!["first"]!.GetValue<string>());
            await Edit("split_pick", new() { ["vertex"] = last });
            Assert.False(state["split"]!["canApply"]!.GetValue<bool>());
            Assert.Equal("invalid_argument", (await Job("source_zmap", Args("draft_apply"), "failed"))["code"]!.GetValue<string>());
            var saveEarly = Args("save_as"); saveEarly["path"] = output;
            Assert.Equal("pending_drafts", (await Job("source_zmap", saveEarly, "failed"))["code"]!.GetValue<string>());
            Assert.Equal("pending_drafts", (await Job("source_zmap", Args("save"), "failed"))["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));
            Assert.Equal(original, File.ReadAllBytes(input));
            await Edit("split_pick", new() { ["vertex"] = diagonal });
            Assert.True(state["split"]!["canApply"]!.GetValue<bool>());
            Assert.Equal(new Vector3(6, 7, 8), layer.SplitPreview?.A);
            Assert.Equal(new Vector3(18, 13, 18), layer.SplitPreview?.B);
            await Edit("split_pick", new() { ["endpoint"] = "first", ["vertex"] = points[1]!["Id"]!.GetValue<string>() });
            Assert.False(state["split"]!["canApply"]!.GetValue<bool>());
            await Edit("split_pick", new() { ["vertex"] = last });
            Assert.True(state["split"]!["canApply"]!.GetValue<bool>());
            await Edit("draft_cancel");
            Assert.Null(layer.SplitPreview);
            Assert.Same(beforeSplit, layer.Editor.Map);
            await Edit("draft_split");
            await Edit("split_pick", new() { ["vertex"] = diagonal });
            await Edit("draft_apply");
            var pieces = layer.Editor.RequireMap().Outlines;
            Assert.Equal(2, pieces.Length);
            Assert.Equal(Guid.Parse(outline), pieces[0].Id);
            Assert.All(pieces, piece =>
            {
                Assert.Equal(0, piece.ObjectiveIndex);
                Assert.Equal(new ZmapColor(0x12, 0xAB, 0xEF), piece.Color);
                Assert.Equal(3, piece.Vertices.Length);
            });
            Assert.Equal(pieces[0].Vertices[0].Position, pieces[1].Vertices[^1].Position);
            Assert.Equal(pieces[0].Vertices[^1].Position, pieces[1].Vertices[0].Position);
            Assert.Equal(6, pieces.SelectMany(piece => piece.Vertices).Select(v => v.Id).Distinct().Count());
            await Edit("undo");
            Assert.Same(beforeSplit, layer.Editor.Map);
            await Edit("redo");
            Assert.Equal(pieces, layer.Editor.RequireMap().Outlines);
            Assert.Equal("unsaved_changes", (await Job("source_world_open", new() { ["mission"] = "m2" }, "failed"))["code"]!.GetValue<string>());
            await Edit("save");
            Assert.False(state["dirty"]!.GetValue<bool>());
            var updated = ZmapCodec.Write(layer.Editor.RequireMap(), token);
            Assert.NotEqual<byte>(original, updated);
            Assert.Equal(updated, File.ReadAllBytes(input));

            await Edit("set_fields", new() { ["outline"] = outline, ["objectiveIndex"] = 1 });
            File.WriteAllBytes(input, original);
            Assert.Equal("file_error", (await Job("source_zmap", Args("save"), "failed"))["code"]!.GetValue<string>());
            Assert.True(layer.Editor.Dirty);
            Assert.Equal(input, layer.Editor.SavePath);
            Assert.Equal(original, File.ReadAllBytes(input));
            File.WriteAllBytes(input, updated);
            await Edit("save_as", new() { ["path"] = output });
            Assert.Equal(output, state["path"]!.GetValue<string>());
            Assert.Equal(input, state["opened"]!.GetValue<string>());
            await Edit("set_fields", new() { ["outline"] = outline, ["rgb"] = "#123456" });
            await Edit("save");
            Assert.Equal(updated, File.ReadAllBytes(input));
            var saved = ZmapFile.Open(output, token);
            Assert.Equal(new[] { 1, 0 }, saved.Outlines.Select(o => o.ObjectiveIndex));
            Assert.Equal(new ZmapColor(0x12, 0x34, 0x56), saved.Outlines[0].Color);
            for (int i = 0; i < saved.Outlines.Length; i++)
                Assert.Equal(pieces[i].Vertices.Select(v => v.Position), saved.Outlines[i].Vertices.Select(v => v.Position));
            await Edit("open", new() { ["path"] = output });
            Assert.False(state["dirty"]!.GetValue<bool>());
            await Edit("terrain_visibility", new() { ["zone"] = 255, ["visible"] = false });
            await Edit("new");
            Assert.False(state["terrainZones"]![0]!["visible"]!.GetValue<bool>());
            Assert.Equal(unfiltered.Minimum with { Y = 0 }, layer.Editor.RequireMap().Minimum);
            Assert.Equal(unfiltered.Maximum with { Y = 0 }, layer.Editor.RequireMap().Maximum);
            Assert.Null(state["path"]);
            Assert.Equal("invalid_argument", (await Job("source_zmap", Args("save"), "failed"))["code"]!.GetValue<string>());
            await Edit("draft_begin");
            await Edit("draft_add", new() { ["position"] = new[] { 4, 0, 4 } });
            await Edit("draft_apply");
            await CheckOutlineOrder();
            await Edit("terrain_height", new() { ["y"] = -1.25 });
            await Edit("exit", new() { ["discard"] = true });
            Assert.False(view.IsZmapView);
            Assert.Null(view.ZmapTerrainMaxY);
            Assert.Equal(prior, view.CaptureView());
            Assert.True(view.TryFrame("all", manual: false));

            void AssertAligned()
            {
                Assert.True(view.ProjectToView(start, out var screen, out _));
                Point point = view.RenderSurface.TranslatePoint(screen, layer);
                Assert.Equal(Guid.Parse(vertex), layer.Pick(point, edge: false)?.Vertex);
                Assert.True(view.TryZmapPoint(screen, start.Y, out var picked));
                Assert.InRange(Vector3.Distance(start, picked), 0, .001f);
            }

            async Task CheckTerrainHeight()
            {
                const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
                var height = (TextBox)typeof(MainWindow).GetField("zmapTerrainHeight", fields)!.GetValue(main)!;
                var lower = (Button)typeof(MainWindow).GetField("zmapLowerTerrainHeight", fields)!.GetValue(main)!;
                var raise = (Button)typeof(MainWindow).GetField("zmapRaiseTerrainHeight", fields)!.GetValue(main)!;
                main.Height = main.MinHeight;
                main.Width = main.MinWidth;
                main.UpdateLayout();
                Assert.True(height.IsVisible && height.IsEnabled);
                Assert.True(LayoutInformation.GetLayoutSlot(height).Width >= 100);
                Assert.True(lower.IsVisible && raise.IsVisible);
                Assert.False(lower.IsEnabled || raise.IsEnabled);
                Assert.Equal(Visibility.Collapsed, ClearButton().Visibility);
                await Edit("terrain_height", new() { ["y"] = -0.25 });
                Assert.Equal(-0.25f, state["terrainMaxY"]!.GetValue<float>());
                Assert.Equal("-0.25", height.Text);
                Assert.False(view.TryFrame("all"));
                Assert.False(height.IsKeyboardFocusWithin);
                Assert.True(ClearButton().IsVisible);
                height.Text = "-1.235";
                Assert.Equal(-1.235f, view.ZmapTerrainMaxY);
                Assert.True(lower.IsEnabled && raise.IsEnabled);
                long revision = state["revision"]!.GetValue<long>();
                lower.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("-1.735", height.Text);
                Assert.Equal(-1.735f, view.ZmapTerrainMaxY);
                raise.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("-1.235", height.Text);
                state = await Job("source_zmap", new() { ["action"] = "state" });
                Assert.Equal(-1.235f, state["terrainMaxY"]!.GetValue<float>());
                Assert.Equal(revision + 3, state["revision"]!.GetValue<long>());
                Assert.False(state["dirty"]!.GetValue<bool>() || state["canUndo"]!.GetValue<bool>());
                height.Text = "0";
                for (int i = 0; i < 3; i++) raise.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("1.5", height.Text);
                height.Text = "123.456789";
                raise.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("123.956789", height.Text);
                lower.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("123.456789", height.Text);
                height.Text = "-0.5";
                raise.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("0", height.Text);
                height.Text = "-1.";
                Assert.Equal("-1.", height.Text);
                Assert.Equal(-1f, view.ZmapTerrainMaxY);
                height.Text = "-1.75\u00a0";
                Assert.Equal(-1.75f, view.ZmapTerrainMaxY);
                Assert.Equal("-1.75\u00a0", height.Text);
                height.Text = "-";
                Assert.False(lower.IsEnabled || raise.IsEnabled);
                Assert.Equal(-1.75f, view.ZmapTerrainMaxY);
#pragma warning disable WPF0001 // Runtime Fluent theme replacement.
                var theme = System.Windows.Application.Current.ThemeMode;
                try
                {
                    System.Windows.Application.Current.ThemeMode = theme == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light;
                    main.UpdateLayout();
                    await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.False(height.IsKeyboardFocusWithin);
                    Assert.True(ClearButton().IsVisible);
                    var controls = Assert.IsType<Grid>(height.Parent);
                    controls.IsEnabled = false;
                    await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.False(ClearButton().IsEnabled);
                    controls.IsEnabled = true;
                    await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    var clear = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(ClearButton()).GetPattern(PatternInterface.Invoke));
                    clear.Invoke();
                    await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                }
                finally { System.Windows.Application.Current.ThemeMode = theme; }
#pragma warning restore WPF0001
                Assert.Null(view.ZmapTerrainMaxY);
                Assert.Empty(height.Text);
                Assert.Equal(Visibility.Collapsed, ClearButton().Visibility);
                Assert.False(lower.IsEnabled || raise.IsEnabled);
                state = await Job("source_zmap", new() { ["action"] = "state" });
                Assert.Null(state["terrainMaxY"]);

                var decimalFloor = view.AddFixtureMesh(new(
                    [new(16, -.1f, -48), new(23, -.1f, -48), new(16, -.1f, -13),
                     new(16, 100, -48), new(23, 100, -48), new(16, 100, -13)],
                    [0, 1, 2, 3, 4, 5])
                {
                    Instances = [Matrix4x4.Identity],
                    Placements = [new(groundNode, -1, "decimal floor", Matrix4x4.Identity)]
                });
                await Edit("terrain_height", new() { ["y"] = -.1 });
                var decimalHit = Assert.Single(view.HitTestScene(new Point3D(17, 101, -47), new Vector3D(0, -1, 0)),
                    hit => Equals(hit.Mesh, decimalFloor));
                Assert.InRange(decimalHit.Point.Y, -.1001f, -.0999f);
                view.RemoveFixtureMesh(decimalFloor);
                await Edit("terrain_height");

                var prop = Matrix4x4.CreateTranslation(32, 3, 0);
                var mesh = view.AddFixtureMesh(new(
                    [new(16, 12, -48), new(28, 12, -48), new(28, 12, -36), new(16, 12, -36),
                     new(17, -2, -47), new(27, -2, -47), new(27, -2, -37), new(17, -2, -37),
                     new(16, 2, -26), new(28, 10, -26), new(28, 10, -14), new(16, 2, -14),
                     new(16, 0, -13), new(23, 0, -13), new(16, 0, 22)],
                    [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 8, 9, 10, 8, 10, 11, 12, 13, 14])
                {
                    Instances = [Matrix4x4.Identity, prop],
                    Placements = [new(groundNode, -1, "terrain", Matrix4x4.Identity), new(-1, -1, "prop", prop)],
                    Color = new(0, 1, 0, 1), Unlit = true
                });
                await Edit("frame");
                var beforeCut = view.CaptureView();
                var roof = new Vector3(21, 12, -42);
                var propRoof = roof + prop.Translation;
                Assert.Equal(12f, MeshHeight(roof, 0));
                Assert.Equal(15f, MeshHeight(propRoof, 1));
                Assert.True(Green(roof));
                Assert.True(Green(propRoof));
                // This flat triangle's float ray distance rounds below 100 even though it lies on the cut.
                await Edit("terrain_height", new() { ["y"] = 0 });
                Assert.InRange(MeshHeight(new(17, 0, -12), 0), -1e-5f, 1e-5f);
                height.Text = "6";
                Assert.Equal(6f, view.ZmapTerrainMaxY);
                state = await Job("source_zmap", new() { ["action"] = "state" });
                Assert.Equal(6f, state["terrainMaxY"]!.GetValue<float>());
                Assert.Equal(beforeCut, view.CaptureView());
                Assert.Equal(-2f, MeshHeight(roof, 0));
                Assert.Equal(1f, MeshHeight(propRoof, 1));
                Assert.False(Green(roof));
                Assert.True(Green(propRoof));
                Assert.True(Green(new(19, 4, -20)));
                Assert.False(Green(new(25, 8, -20)));
                Assert.Equal(6f, MeshHeight(new(22, 6, -20), 0));
                while (view.ZmapSnapStatus == "preparing") await Task.Delay(10, token);
                Assert.False(view.TrySnapZmapVertex(new(28, 90, -14), out _));
                Assert.True(view.TrySnapZmapVertex(new(16, 90, -14), out var snapped));
                Assert.Equal(new Vector3(16, 90, -14), snapped);
                Assert.False(view.TrySnapZmapVertex(new(60, 90, -14), out _));
                Assert.True(view.TrySnapZmapVertex(new(48, 90, -14), out snapped));
                Assert.Equal(new Vector3(48, 90, -14), snapped);
                await Edit("terrain_visibility", new() { ["zone"] = 255, ["visible"] = false });
                Assert.Equal(6f, state["terrainMaxY"]!.GetValue<float>());
                Assert.True(Green(propRoof));
                await Edit("terrain_visibility", new() { ["visible"] = true });
                Assert.False(Green(roof));
                await Edit("terrain_height");
                Assert.True(Green(roof));
                Assert.Equal(15f, MeshHeight(propRoof, 1));
                view.RemoveFixtureMesh(mesh);
                view.Isolate(null);

                Button ClearButton()
                {
                    height.ApplyTemplate();
                    return Assert.IsAssignableFrom<Button>(height.Template.FindName("DeleteButton", height));
                }

                float MeshHeight(Vector3 world, int instance) => Assert.Single(
                    view.HitTestScene(new Point3D(world.X, 100, world.Z), new Vector3D(0, -1, 0)),
                    hit => Equals(hit.Mesh, mesh) && hit.Instance == instance).Point.Y;

                bool Green(Vector3 world)
                {
                    Assert.True(view.ProjectToView(world, out var point, out _));
                    var image = new FormatConvertedBitmap(view.RenderImage(480, 320), PixelFormats.Bgra32, null, 0);
                    int x = (int)(point.X * image.PixelWidth / view.RenderSurface.ActualWidth);
                    int y = (int)(point.Y * image.PixelHeight / view.RenderSurface.ActualHeight);
                    byte[] rgba = new byte[4];
                    image.CopyPixels(new Int32Rect(x, y, 1, 1), rgba, 4, 0);
                    return rgba[1] > 180 && rgba[2] < 30;
                }
            }

            // The list is the file's order, in which the game draws the outlines: move_outline and a row's arrows change an
            // outline's place and select it, as one undo step.
            async Task CheckOutlineOrder()
            {
                await Edit("add_outline", new() { ["points"] = new[] { new[] { 8, 0, 8 } }, ["rgb"] = "#010203" });
                await Edit("add_outline", new() { ["points"] = new[] { new[] { 12, 0, 12 } }, ["rgb"] = "#040506" });
                var ids = await Order();
                Assert.Equal(3, ids.Length);
                Assert.Equal(ids[2], state["outline"]!.GetValue<string>());
                var refused = Args("move_outline");
                refused["outline"] = ids[0];
                Assert.Equal("invalid_argument", (await Job("source_zmap", refused, "failed"))["code"]!.GetValue<string>());
                refused["index"] = 3;
                Assert.Equal("invalid_argument", (await Job("source_zmap", refused, "failed"))["code"]!.GetValue<string>());
                await Edit("move_outline", new() { ["outline"] = ids[0], ["index"] = 2 });
                Assert.Equal(new[] { ids[1], ids[2], ids[0] }, await Order());
                Assert.Equal(ids[0], state["outline"]!.GetValue<string>());

                var list = (ListBox)typeof(MainWindow).GetField("zmapOutlines", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                main.UpdateLayout();
                Assert.False(RowButton(0, "RowUp").IsEnabled);
                Assert.False(RowButton(2, "RowDown").IsEnabled);
                var down = RowButton(0, "RowDown");
                Assert.True(down.IsEnabled && RowButton(1, "RowUp").IsEnabled);
                down.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                state = await Job("source_zmap", new() { ["action"] = "state" });
                Assert.Equal(new[] { ids[2], ids[1], ids[0] }, await Order());
                Assert.Equal(ids[1], state["outline"]!.GetValue<string>());
                await Edit("undo");
                Assert.Equal(new[] { ids[1], ids[2], ids[0] }, await Order());

                async Task<string[]> Order() =>
                    (await Job("source_zmap", Args("outlines")))["outlines"]!.AsArray().Select(o => o!["Id"]!.GetValue<string>()).ToArray();
                Button RowButton(int row, string name) =>
                    Descendants(list.ItemContainerGenerator.ContainerFromIndex(row)).OfType<Button>().Single(button => button.Name == name);
            }

            // The Outlines pane's rule (the owner's report: zone rows scrolled beside an empty list): the zone rows show whole
            // while they and the list fit, and beside a list that scrolls while it keeps four rows; otherwise each keeps half the
            // room unless the other needs less.
            static void CheckOutlinesRoom()
            {
                // 150 DIP of headers and the cutoff section leave 450; an outline's row is 32 and a zone's 28.
                Assert.Equal((254, 196), Room(5, 7));
                Assert.Equal((254, 196), Room(20, 7));
                Assert.Equal((170, 280), Room(20, 10));
                Assert.Equal((170, 280), Room(5, 14));
                Assert.Equal((226, 224), Room(20, 14));
                Assert.Equal((142, 308), Room(0, 14, empty: 120));
                // An exact fit at a fractional display scale: 648.8 less 156.8 is 491.99999999999994 in doubles, and
                // four outlines with thirteen zones need 492.
                Assert.Equal((128, 364), Room(4, 13, header: 46.8, height: 648.8));
                // Rows that a display scale rounded to whole pixels (28.4 DIP for 28) show whole too: their height is
                // measured, not assumed.
                Assert.InRange(Room(4, 13, row: 28.4, height: 660).Zones, 369.19, 369.21);
                // So is a list row's: rows of 33 for the 32 they are given leave room for eleven zone rows, not twelve,
                // from the first layout on and after the list's rows are replaced (as a move does).
                Assert.Equal((124, 308), Room(3, 13, height: 582, item: 33));
                // With layout rounding at 115 DPI (a custom display scale) the 32 and 28 DIP rows are 38 and 34 pixels and
                // the fixed parts 48, 96 and 36. Three outlines and thirteen zones need 736 pixels: at exactly that
                // height nothing scrolls, and a pixel less costs the zones one row and no more. Seven and seven in a
                // tall pane do not scroll either.
                Assert.Equal((false, false, 13 * 34), Scrolls(3, 13, 736));
                Assert.Equal((false, true, 12 * 34), Scrolls(3, 13, 735));
                Assert.Equal((false, false, 7 * 34), Scrolls(7, 7, 1018));
                // Zone rows that are scrolled stay at their row when the display scale changes (the window moves to
                // another monitor), although their heights are measured anew.
                {
                    var (layout, _, rows) = Pane(20, 14, 0, 40, 28, 32);
                    System.Windows.Media.VisualTreeHelper.SetRootDpi(layout, new(1.5, 1.5));
                    Lay();
                    rows.ScrollToVerticalOffset(3);
                    Lay();
                    Assert.Equal(3, rows.VerticalOffset);
                    System.Windows.Media.VisualTreeHelper.SetRootDpi(layout, new(1.25, 1.25));
                    layout.InvalidateMeasure();
                    Lay();
                    Assert.Equal(3, rows.VerticalOffset);

                    void Lay()
                    {
                        layout.Measure(new(300, 600));
                        layout.Arrange(new(0, 0, 300, 600));
                        layout.UpdateLayout();
                    }
                }

                static (double List, double Zones) Room(int outlines, int zones, double empty = 0, double header = 40, double height = 600, double row = 28, double item = 32)
                {
                    var (layout, list, rows) = Pane(outlines, zones, empty, header, row, item);
                    var first = Slots();
                    // The list's rows are replaced, as after every edit of the outlines: the room is shared as before.
                    list.ItemsSource = Enumerable.Range(0, outlines).ToArray();
                    Assert.Equal(first, Slots());
                    return first;

                    (double List, double Zones) Slots()
                    {
                        layout.Measure(new(300, height));
                        layout.Arrange(new(0, 0, 300, height));
                        layout.UpdateLayout();
                        // Whole rows show without a scrollbar, and a scrolled list shows whole rows.
                        Assert.Equal(LayoutInformation.GetLayoutSlot(rows).Height < zones * row - .01 ? Visibility.Visible : Visibility.Collapsed, rows.ComputedVerticalScrollBarVisibility);
                        return (LayoutInformation.GetLayoutSlot(list).Height, LayoutInformation.GetLayoutSlot(rows).Height);
                    }
                }
                static (bool List, bool Zones, int ZonePixels) Scrolls(int outlines, int zones, int pixels)
                {
                    const double scale = 115 / 96.0;
                    var (layout, list, rows) = Pane(outlines, zones, 0, 40, 28, 32);
                    // Laid out once so that the templates' parts exist; then every part gets the scale and layout rounding,
                    // as under the window.
                    layout.Measure(new(300, 2000));
                    layout.Arrange(new(0, 0, 300, 2000));
                    System.Windows.Media.VisualTreeHelper.SetRootDpi(layout, new(scale, scale));
                    foreach (var part in Descendants(layout).OfType<FrameworkElement>().Prepend(layout)) part.UseLayoutRounding = true;
                    layout.Measure(new(300, pixels / scale));
                    layout.Arrange(new(0, 0, 300, pixels / scale));
                    layout.UpdateLayout();
                    var inner = Descendants(list).OfType<ScrollViewer>().First();
                    return (inner.ComputedVerticalScrollBarVisibility == Visibility.Visible, rows.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                        (int)Math.Round(LayoutInformation.GetLayoutSlot(rows).Height * scale));
                }
                static (ZmapOutlinesLayout Layout, ListBox List, ScrollViewer Rows) Pane(int outlines, int zones, double empty, double header, double row, double item)
                {
                    Style rowStyle = new(typeof(ListBoxItem));
                    rowStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, item));
                    ListBox list = new() { ItemsSource = Enumerable.Range(0, outlines).ToArray(), ItemContainerStyle = rowStyle, BorderThickness = new(0), Padding = new(0) };
                    StackPanel stack = new();
                    for (int i = 0; i < zones; i++) stack.Children.Add(new Border { Height = row });
                    ScrollViewer rows = new() { Content = stack, CanContentScroll = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                    return (new(new Border { Height = header }, list, new Border { Height = empty, Visibility = empty > 0 ? Visibility.Visible : Visibility.Collapsed },
                        new Border { Height = 80 }, new Border { Height = 30 }, rows, 32, 28), list, rows);
                }
            }

            async Task CheckSnap()
            {
                const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
                // Short layout keeps Remove outline in view: inside what the card's scrolling body shows, above its footer.
                main.Height = main.MinHeight;
                main.UpdateLayout();
                var remove = (Button)typeof(MainWindow).GetField("zmapRemoveOutline", fields)!.GetValue(main)!;
                Assert.True(remove.IsVisible && remove.IsEnabled);
                ScrollViewer? body = null;
                for (DependencyObject? at = remove; at != null && body == null; at = System.Windows.Media.VisualTreeHelper.GetParent(at)) body = at as ScrollViewer;
                Assert.NotNull(body);
                Assert.InRange(remove.TranslatePoint(new(0, 0), body).Y, 0, body.ViewportHeight);
                Assert.InRange(remove.TranslatePoint(new(0, remove.ActualHeight), body).Y, 0, body.ViewportHeight);

                // Include a rotated/translated structure in the same renderer snapshot as the fixture terrain.
                var matrix = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(31, 23, -28);
                var corner = new Vector3(9, 5, 0);
                var structure = view.AddFixtureMesh(new([Vector3.Zero, corner, new(0, 2, -3)], [0, 1, 2])
                {
                    Instances = [matrix], Placements = [new ScenePlacement(0, -1, "wall", matrix)]
                });
                view.Isolate(null);
                while (view.ZmapSnapStatus == "preparing") await Task.Delay(10, token);
                Assert.Equal("ready", view.ZmapSnapStatus);
                await Edit("frame");
                Assert.True(view.TrySnapZmapVertex(new(.1f, 91, -.1f), out var ground));
                Assert.Equal(new Vector3(0, 91, 0), ground);
                await Edit("terrain_visibility", new() { ["zone"] = 255, ["visible"] = false });
                while (view.ZmapSnapStatus == "preparing") await Task.Delay(10, token);
                Assert.False(view.TrySnapZmapVertex(new(.1f, 91, -.1f), out _));
                await Edit("terrain_visibility", new() { ["visible"] = true });
                double edgeY = view.RenderSurface.ActualHeight / 6;
                Assert.True(view.TryZmapPoint(new(1, edgeY), start.Y, out var edgeTarget));
                Assert.True(view.TryZmapPoint(new(8, edgeY), start.Y, out var visibleCorner));
                var offscreenCorner = edgeTarget - (visibleCorner - edgeTarget) * (2f / 7);
                var edgeMesh = view.AddFixtureMesh(new([offscreenCorner, visibleCorner, visibleCorner + new Vector3(0, 0, 10)], [0, 1, 2])
                {
                    Instances = [Matrix4x4.Identity], Placements = [new ScenePlacement(0, -1, "edge", Matrix4x4.Identity)]
                });
                view.Isolate(null);
                while (view.ZmapSnapStatus == "preparing") await Task.Delay(10, token);
                Assert.True(view.ProjectToView(offscreenCorner, out var outside, out _));
                Assert.False(view.IsNavigationPointInside(outside));
                Assert.True(view.TrySnapZmapVertex(edgeTarget, out var nearEdge));
                Assert.Equal(visibleCorner, nearEdge);
                var world = Vector3.Transform(corner, matrix);
                var target = world + new Vector3(.1f, 0, .1f);
                Assert.True(view.ProjectToView(start, out var press, out _));
                Assert.True(view.ProjectToView(target, out var pointer, out _));
                typeof(MainWindow).GetField("zmapDrag", fields)!.SetValue(main, (Guid.Parse(outline), Guid.Parse(vertex), press, press, start));
                var drag = typeof(MainWindow).GetMethod("UpdateZmapDrag", fields)!;
                drag.Invoke(main, [pointer, false]);
                Assert.Null(layer.SnapTarget);
                drag.Invoke(main, [pointer, true]);
                Assert.Equal(new Vector3(world.X, start.Y, world.Z), layer.SnapTarget);
                drag.Invoke(main, [pointer, false]);
                Assert.Null(layer.SnapTarget);
                typeof(MainWindow).GetMethod("CancelZmapDrag", fields)!.Invoke(main, null);
                state = await Job("source_zmap", new() { ["action"] = "state" });
                await Edit("move_vertex", new() { ["outline"] = outline, ["vertex"] = vertex, ["snap"] = true,
                    ["position"] = new[] { target.X, -200f, target.Z } });
                Assert.Equal(new Vector3(world.X, start.Y, world.Z), layer.Editor!.Selected!.Vertices[0].Position);
                await Edit("undo");
                view.RemoveFixtureMesh(edgeMesh);
                view.RemoveFixtureMesh(structure);
                view.Isolate(null);
            }
            Dictionary<string, object?> Args(string action) => new()
            {
                ["action"] = action, ["document"] = document, ["session"] = state["session"]!.GetValue<string>(), ["revision"] = state["revision"]!.GetValue<long>()
            };
            async Task Edit(string action, Dictionary<string, object?>? extras = null)
            {
                var args = Args(action);
                if (extras != null) foreach (var pair in extras) args.Add(pair.Key, pair.Value);
                state = await Job("source_zmap", args);
            }
            async Task<JsonNode> Call(string name, Dictionary<string, object?> args, bool error = false)
            {
                var result = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: token);
                string payload = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
                Assert.True((result.IsError == true) == error, payload);
                return JsonNode.Parse(payload)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> args, string expected = "completed")
            {
                var job = await Call(name, args);
                string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running")
                { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString());
                return job["result"]!;
            }
        }
        finally
        {
            typeof(MainWindow).GetMethod("LeaveZmap", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null);
            main.Close();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
