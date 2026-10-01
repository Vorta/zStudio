using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>
/// Terrain in a source world through the real named-pipe MCP connection and the GUI: creating a terrain from a surface
/// file, painting a region with a path stroke, the pieces it compiles to, Properties of a piece and a viewport brush stroke.
/// </summary>
internal static class SourceTerrainMcpChecks
{
    private const string Recipe = "data/m1/models/terrain/hills.terrain.json";

    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        fixture.WriteTerrainDatabase();
        // A 100 × 100 surface across m1's cell lines, in its own file.
        ModelBuilder builder = new();
        builder.Add(new([new(200, 0, 300), new(300, 0, 300), new(300, 0, 200), new(200, 0, 200)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], new() { Texture = new("rock"), Flags = 0x1FF }));
        WorldNode land = new("land", WorldNodeClass.Object3D) { Model = builder.Finish(), Flags = WorldGltf.DefaultCarried };
        land.SetPayloadInt(0, 0x28);
        var (json, bin) = WorldGltf.Export([land], 0xFF, new() { Texture = t => ($"../../textures/{t.Name}.png", 0) }).Write("hills.bin");
        fixture.Write("data/m1/models/terrain/hills.gltf", json); fixture.Write("data/m1/models/terrain/hills.bin", bin);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var doc = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);

            // The database is not a surface file; a file of its own is.
            var refused = await Job("source_terrain_create", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["model"] = "data/m1/models/m1.gltf", ["surfaces"] = new[] { "ground" } }, "failed");
            Assert.Equal("invalid_argument", refused["code"]!.GetValue<string>());
            var created = await Job("source_terrain_create", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["model"] = "data/m1/models/terrain/hills.gltf", ["surfaces"] = new[] { "land" } });
            doc = Document(created["document"]!);
            var workspace = doc.SourceWorld!.Workspace;
            Assert.Equal(["data/m1/models/m1.gltf", Recipe], workspace.DirtyFiles);
            var listed = await Call("source_terrain", new() { ["document"] = Id(doc) });
            Assert.Equal(4, listed["recipes"]![0]!["pieces"]!.GetValue<int>());

            // A road: a region painted along a path; its pieces block craters.
            doc = Document((await Job("source_terrain_edit", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["recipe"] = Recipe, ["action"] = "add_region", ["region"] = "road", ["attributes"] = new Dictionary<string, object?> { ["craters"] = "blocked" } }))["document"]!);
            doc = Document((await Job("source_terrain_edit", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["recipe"] = Recipe, ["action"] = "paint", ["region"] = "road", ["path"] = new[] { new[] { 220.0, 250.0 }, new[] { 280.0, 250.0 } }, ["radius"] = 4 }))["document"]!);
            var described = await Call("source_terrain", new() { ["document"] = Id(doc), ["recipe"] = Recipe, ["region"] = "road" });
            Assert.Equal(6, described["pieces"]!.GetValue<int>());
            Assert.Equal("blocked", described["regions"]![0]!["set"]!["craters"]!.GetValue<string>());
            double area = described["regions"]![0]!["shape"]!["area"]!.GetValue<double>();
            Assert.InRange(area, 60 * 8, 60 * 8 + Math.PI * 16);
            Assert.NotEmpty(described["regionShape"]!["polygons"]!.AsArray());
            // A null attribute removes the override; an unknown one is refused.
            var bad = await Job("source_terrain_edit", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["recipe"] = Recipe, ["action"] = "update_region", ["region"] = "road", ["attributes"] = new Dictionary<string, object?> { ["colour"] = 1 } }, "failed");
            Assert.Equal("invalid_argument", bad["code"]!.GetValue<string>());

            // Properties of a piece edits its recipe; the brush erases part of the road with a viewport stroke.
            await Preview();
            int piece = doc.PreviewDocument.Scene!.Nodes.First(n => n.Name.StartsWith("hills_land_", StringComparison.Ordinal)).Index;
            string preview = ((Guid)typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).ToString();
            await Call("scene_properties", new() { ["preview"] = preview, ["node"] = piece, ["open"] = true });
            await main.Dispatcher.InvokeAsync(() => main.OpenPropertiesWindow!.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Loaded);
            var typedFields = Assert.IsType<TerrainPropertiesEditor>(main.OpenPropertiesWindow!.SourceFields);
            // Typing an attribute and pressing Enter edits the recipe through the source edit its draft's commit runs.
            var soil = Descendants(typedFields).OfType<System.Windows.Controls.TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Region road: Soil");
            var shownBefore = doc;
            soil.Text = "water";
            soil.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(soil), Environment.TickCount, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            for (int wait = 0; wait < 1000 && !shownBefore.IsDisposed; wait++) await Task.Delay(10, token);
            Assert.True(shownBefore.IsDisposed, "The Properties commit did not rebuild the world.");
            Assert.Equal("water", SourceTerrain.Read(workspace, Recipe).Regions[0].Set.ToJson()["soil"]!.GetValue<string>());
            doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            await Preview();
            doc = Document(await Call("undo_redo", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["action"] = "undo" }));
            await Preview();
            piece = doc.PreviewDocument.Scene!.Nodes.First(n => n.Name.StartsWith("hills_land_", StringComparison.Ordinal)).Index;
            preview = ((Guid)typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).ToString();
            await Call("scene_properties", new() { ["preview"] = preview, ["node"] = piece, ["open"] = true });
            var fields = Assert.IsType<TerrainPropertiesEditor>(main.OpenPropertiesWindow!.SourceFields);
            Assert.Equal(Recipe, fields.RecipePath); Assert.Equal("road", fields.SelectedRegion);
            var automation = JsonSerializer.SerializeToNode(fields.DescribeAutomationFields())!;
            string erase = automation["actions"]!.AsArray().Single(x => x!["Label"]!.GetValue<string>() == "Erase in viewport")!["Id"]!.GetValue<string>();
            await fields.InvokeAutomationActionAsync(erase);
            var brush = typeof(MainWindow).GetField("terrainBrush", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.False(((TerrainBrushState)brush.GetValue(main)!).Add);
            var paint = (Task)typeof(MainWindow).GetMethod("PaintStrokeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [new List<Vector3> { new(240, 0, 250), new(260, 0, 250) }])!;
            await paint;
            var erased = SourceTerrain.Read(workspace, Recipe);
            Assert.True(TerrainShapes.SquareUnits(erased.Regions[0].Shape!.Polygons) < area - 100);
            Assert.Equal("Erase road", workspace.UndoLabel);
            var shown = Assert.IsType<TerrainPropertiesEditor>(main.OpenPropertiesWindow!.SourceFields);
            Assert.False(shown.Json["brush"]!["mode"]!.GetValue<string>() == "paint");

            // Undo is project-wide: the four changes go back and the files match the disk.
            doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            for (int i = 0; i < 4; i++) doc = Document(await Call("undo_redo", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["action"] = "undo" }));
            Assert.False(workspace.IsDirty);

            // Convert to editable terrain: the plan first, then the conversion with its probe comparison.
            var plan = await Job("source_terrain_convert", new() { ["document"] = Id(doc), ["revision"] = doc.Revision });
            Assert.Equal(3, plan["plan"]!["converted"]!.GetValue<int>());
            Assert.Equal(2, plan["plan"]!["keptCount"]!.GetValue<int>());
            var converted = await Job("source_terrain_convert", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["apply"] = true, ["spacing"] = 2 });
            Assert.Equal(0, converted["probe"]!["mismatches"]!.GetValue<int>());
            Assert.True(converted["probe"]!["hits"]!.GetValue<int>() > 500);
            doc = Document(converted["document"]!);
            Assert.Contains("data/m1/models/m1_terrain.terrain.json", workspace.DirtyFiles);
            doc = Document(await Call("undo_redo", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["action"] = "undo" }));
            Assert.False(workspace.IsDirty);
            await Call("close_document", new() { ["document"] = Id(doc), ["revision"] = doc.Revision });

            async Task Preview()
            {
                var work = (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                await work.WaitAsync(token);
            }
            DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == (state["document"]?["id"] ?? state["id"])!.GetValue<string>());
            static string Id(DocumentModel d) => d.SessionId.ToString();
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, bool error = false)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True((result.IsError == true) == error, name + ": " + text);
                return error ? JsonValue.Create(text)! : JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments);
                if (job["id"] == null || job["State"] == null) return job;
                string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, name + ": " + job.ToJsonString()); return job["result"]!;
            }
        }
        finally { main.Close(); }
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }
}
