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
            await Edit("exit", new() { ["discard"] = true });
            Assert.False(view.IsZmapView);
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
}
