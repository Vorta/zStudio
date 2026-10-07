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
        // Opening a maximum-size recipe must create a bounded set of controls. Paging must expose the final region and
        // replace its generated actions, not accumulate stale actions or retarget the selected region's draft.
        var manyRegions = Enumerable.Range(0, TerrainRecipe.MaximumRegions).Select(i => new TerrainRegion("region" + i, [], null, TerrainAttributes.None)).ToArray();
        string? selectedRegion = null;
        TerrainEditorActions noEdits = new((_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, name => selectedRegion = name, _ => { });
        var largeEditor = new TerrainPropertiesEditor(Recipe, new(1, [], TerrainAttributes.None, manyRegions), null, null, "region4095", null, noEdits);
        var actions = JsonSerializer.SerializeToNode(largeEditor.DescribeAutomationFields())!["actions"]!.AsArray();
        Assert.InRange(actions.Count, 1, 140);
        Assert.InRange(LogicalButtons(largeEditor), 1, 160);
        var last = actions.Single(a => a!["Label"]!.GetValue<string>().Contains("4096. region4095"))!;
        await largeEditor.InvokeAutomationActionAsync(last["Id"]!.GetValue<string>());
        Assert.Equal("region4095", selectedRegion);
        for (int i = 0; i < 3; i++)
        {
            var previous = JsonSerializer.SerializeToNode(largeEditor.DescribeAutomationFields())!["actions"]!.AsArray().Single(a => a!["Label"]!.GetValue<string>() == "Previous regions")!;
            await largeEditor.InvokeAutomationActionAsync(previous["Id"]!.GetValue<string>());
            Assert.InRange(JsonSerializer.SerializeToNode(largeEditor.DescribeAutomationFields())!["actions"]!.AsArray().Count, 1, 140);
        }
        static int LogicalButtons(System.Windows.DependencyObject root) => (root is System.Windows.Controls.Button ? 1 : 0)
            + System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>().Sum(LogicalButtons);
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
            Assert.Empty((await Call("source_terrain", new() { ["document"] = Id(doc), ["offset"] = 1 }))["recipes"]!.AsArray());

            // Reader-valid rings above the editing budget must remain inspectable through the real protocol and Properties.
            var originalRecipe = SourceTerrain.Read(workspace, Recipe, token);
            var longRing = Enumerable.Range(0, 150_000).Select(i => new Vector2(i, 0)).ToArray();
            var largeRecipe = originalRecipe with { Regions = [new("large", [], new([new(longRing, []), new(longRing, [])]), TerrainAttributes.None)] };
            workspace.Apply("inspection fixture", [(Recipe, largeRecipe.Write())], token);
            var largeDescription = await Call("source_terrain", new() { ["document"] = Id(doc), ["recipe"] = Recipe, ["region"] = "large" });
            Assert.Null(largeDescription["regions"]![0]!["shape"]!["area"]);
            Assert.Contains("complexity", largeDescription["regions"]![0]!["shape"]!["areaUnavailable"]!.GetValue<string>());
            Assert.Null(largeDescription["regionShape"]!["squareUnits"]);
            Assert.True(largeDescription["regionShape"]!["truncated"]!.GetValue<bool>());
            Assert.Equal(300_000, largeDescription["regionShape"]!["pointCount"]!.GetValue<int>());
            var largeRegionEditor = new TerrainPropertiesEditor(Recipe, largeRecipe, null, null, "large", null, noEdits);
            Assert.InRange(LogicalButtons(largeRegionEditor), 1, 160);
            workspace.Undo();

            // A held inspection leaves the dispatcher responsive and cannot label stale content as the current revision.
            var originalReader = main.TerrainRecipeReader;
            TaskCompletionSource readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using SemaphoreSlim readProceed = new(0);
            main.TerrainRecipeReader = (w, path, ct) =>
            {
                Assert.False(main.Dispatcher.CheckAccess());
                var value = originalReader(w, path, ct); readEntered.TrySetResult();
                Assert.True(readProceed.Wait(TimeSpan.FromSeconds(20), ct)); return value;
            };
            try
            {
                var reading = Call("source_terrain", new() { ["document"] = Id(doc), ["recipe"] = Recipe }, error: true);
                await readEntered.Task.WaitAsync(token);
                await Call("state", new());
                workspace.Apply("concurrent recipe", [(Recipe, largeRecipe.Write())], token);
                readProceed.Release();
                Assert.Contains("context_changed", (await reading).GetValue<string>());
                workspace.Undo();
            }
            finally { main.TerrainRecipeReader = originalReader; readProceed.Release(); }

            // The expensive edit callback runs on a worker. While held, protocol reads and Tools Cancel remain responsive.
            TaskCompletionSource editEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            main.SourceEditPreparing = ct =>
            {
                Assert.False(main.Dispatcher.CheckAccess());
                editEntered.TrySetResult();
                Assert.True(ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)), "The source preparation was not canceled."); ct.ThrowIfCancellationRequested();
            };
            int historyBefore = workspace.History.Count;
            var canceledEdit = Job("source_terrain_edit", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["recipe"] = Recipe, ["action"] = "add_region", ["region"] = "canceled" }, "canceled");
            await editEntered.Task.WaitAsync(token);
            await Call("state", new());
            ((System.Windows.Controls.MenuItem)main.FindName("CancelOperationItem")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            await canceledEdit;
            Assert.Equal(historyBefore, workspace.History.Count);
            Assert.DoesNotContain(SourceTerrain.Read(workspace, Recipe, token).Regions, r => r.Name == "canceled");
            main.SourceEditPreparing = ct => Assert.False(main.Dispatcher.CheckAccess());

            // A road: a region painted along a path; its pieces block craters.
            doc = Document((await Job("source_terrain_edit", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["recipe"] = Recipe, ["action"] = "add_region", ["region"] = "road", ["attributes"] = new Dictionary<string, object?> { ["craters"] = "blocked" } }))["document"]!);
            doc = Document((await Job("source_terrain_edit", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["recipe"] = Recipe, ["action"] = "paint", ["region"] = "road", ["path"] = new[] { new[] { 220.0, 250.0 }, new[] { 280.0, 250.0 } }, ["radius"] = 4 }))["document"]!);
            var described = await Call("source_terrain", new() { ["document"] = Id(doc), ["recipe"] = Recipe, ["region"] = "road" });
            Assert.Equal(6, described["pieces"]!.GetValue<int>());
            Assert.Equal("blocked", described["regions"]![0]!["set"]!["craters"]!.GetValue<string>());
            double area = described["regions"]![0]!["shape"]!["area"]!.GetValue<double>();
            Assert.InRange(area, 60 * 8, 60 * 8 + Math.PI * 16);
            Assert.NotEmpty(described["regionShape"]!["polygons"]!.AsArray());
            var afterRegion = await Call("source_terrain", new() { ["document"] = Id(doc), ["recipe"] = Recipe, ["offset"] = 1 });
            Assert.Empty(afterRegion["regions"]!.AsArray()); Assert.Equal(1, afterRegion["regionCount"]!.GetValue<int>());
            // A null attribute removes the override; an unknown one is refused.
            var bad = await Job("source_terrain_edit", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["recipe"] = Recipe, ["action"] = "update_region", ["region"] = "road", ["attributes"] = new Dictionary<string, object?> { ["colour"] = 1 } }, "failed");
            Assert.Equal("invalid_argument", bad["code"]!.GetValue<string>());

            // Properties of a piece edits its recipe; the brush erases part of the road with a viewport stroke.
            await Preview();
            int piece = doc.PreviewDocument.Scene!.Nodes.First(n => n.Name.StartsWith("hills_land_", StringComparison.Ordinal)).Index;
            string preview = ((Guid)typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).ToString();
            main.TerrainRecipeReader = (w, path, t) =>
            {
                Assert.False(main.Dispatcher.CheckAccess(), "Terrain recipe parsing must leave the UI dispatcher responsive.");
                return SourceTerrain.Read(w, path, t);
            };
            await Call("scene_properties", new() { ["preview"] = preview, ["node"] = piece, ["open"] = true });
            // Closing the pinned window while another read is held must prevent the late result from reopening it.
            var reader = main.TerrainRecipeReader;
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var proceed = new SemaphoreSlim(0))
            {
                main.TerrainRecipeReader = (w, path, t) =>
                {
                    entered.TrySetResult();
                    Assert.False(main.Dispatcher.CheckAccess());
                    Assert.True(proceed.Wait(TimeSpan.FromSeconds(20), t));
                    return reader(w, path, t);
                };
                var pending = (Task<bool>)typeof(MainWindow).GetMethod("ShowTerrainPropertiesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [doc, Recipe, null, null, null])!;
                try
                {
                    await entered.Task.WaitAsync(token);
                    main.OpenPropertiesWindow!.Dismiss();
                }
                finally { proceed.Release(); }
                Assert.False(await pending);
                Assert.Null(main.OpenPropertiesWindow);
                main.TerrainRecipeReader = reader;
            }
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
            // While the project rebuilds a world, Properties of its worlds takes no input (typing then would take the edit back).
            var busy = typeof(MainWindow).GetField("sourceWorkspaceBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var block = typeof(MainWindow).GetMethod("UpdateSourceInputBlock", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var body = (System.Windows.Controls.ContentControl)typeof(PropertiesWindow).GetField("body", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main.OpenPropertiesWindow)!;
            busy.SetValue(main, true); block.Invoke(main, []);
            Assert.False(body.IsEnabled);
            busy.SetValue(main, false); block.Invoke(main, []);
            Assert.True(body.IsEnabled);
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

            // Convert to editable terrain plans off the UI thread, which keeps answering: Tools → Cancel (Escape) stops the plan,
            // and a plan the project changed under, or made for a world that was replaced meanwhile, is never shown.
            var planner = main.PlanSourceTerrainConversion;
            bool? planOnUi = null; SemaphoreSlim planEntered = new(0), planProceed = new(0);
            main.PlanSourceTerrainConversion = (w, database, dependencies, t) =>
            {
                planOnUi = main.Dispatcher.CheckAccess();
                // Planned on the UI thread, the click would not return before the plan, so nothing could stop or change it.
                if (planOnUi == true) throw new InvalidDataException("The conversion was planned on the UI thread.");
                planEntered.Release();
                Assert.True(planProceed.Wait(TimeSpan.FromSeconds(30)));
                return planner(w, database, dependencies, t);
            };
            try
            {
                var convertClick = typeof(MainWindow).GetMethod("ConvertTerrainClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var cancelClick = typeof(MainWindow).GetMethod("CancelClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
                async Task ClickConvert()
                {
                    convertClick.Invoke(main, [main, new System.Windows.RoutedEventArgs()]);
                    Assert.True(await planEntered.WaitAsync(TimeSpan.FromSeconds(10), token), "The conversion was not planned off the UI thread: " + main.ViewModel.Status);
                    Assert.False(planOnUi);
                    Assert.True(main.CancelOperationItem.IsEnabled);
                    Assert.NotNull(await Call("state", new()));
                }
                async Task Shown(string status)
                {
                    for (int wait = 0; wait < 1000 && (main.CancelOperationItem.IsEnabled || !main.ViewModel.Status.Contains(status, StringComparison.Ordinal)); wait++) await Task.Delay(10, token);
                    Assert.Contains(status, main.ViewModel.Status);
                    Assert.False(main.CancelOperationItem.IsEnabled);
                }
                await ClickConvert();
                cancelClick.Invoke(main, [main, new System.Windows.RoutedEventArgs()]);
                planProceed.Release();
                await Shown("Operation canceled");
                Assert.False(workspace.IsDirty);

                await ClickConvert();
                var note = workspace.Apply("Note", [("gamegen/note.gs", "# note\r\n"u8.ToArray())], token)!;
                planProceed.Release();
                await Shown("The project's sources changed since the conversion was planned");
                Assert.Equal(["gamegen/note.gs"], workspace.DirtyFiles);
                workspace.Retract(note);
                Assert.False(workspace.IsDirty);

                // A redo rebuilds the world (MCP edits still run while the GUI plans); the undo returns it.
                await ClickConvert();
                var replaced = doc;
                doc = Document(await Call("undo_redo", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["action"] = "redo" }));
                Assert.True(replaced.IsDisposed);
                planProceed.Release();
                await Shown("while the conversion was planned");
                doc = Document(await Call("undo_redo", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["action"] = "undo" }));
                Assert.False(workspace.IsDirty);

                // MCP plans the same way, and its job can be cancelled while it plans.
                var operation = await Call("source_terrain_convert", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["apply"] = true });
                Assert.True(await planEntered.WaitAsync(TimeSpan.FromSeconds(10), token));
                Assert.False(planOnUi);
                await Call("operation", new() { ["id"] = operation["id"]!.GetValue<string>(), ["cancel"] = true });
                planProceed.Release();
                JsonNode stopped = operation;
                while (stopped["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); stopped = await Call("operation", new() { ["id"] = operation["id"]!.GetValue<string>() }); }
                Assert.Equal("canceled", stopped["State"]!.GetValue<string>());
                Assert.False(workspace.IsDirty);
            }
            finally { main.PlanSourceTerrainConversion = planner; planProceed.Release(4); }

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
