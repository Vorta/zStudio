using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Mcp;
using zStudio.Rendering;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>Must-have contract: real MCP placement shares the GUI ghost, cancels without edits, then commits one undo step.</summary>
internal static class ModelPlacementMcpChecks
{
    internal static async Task Run()
    {
        using SourceWorldFixture fixture = new();
        byte[] modelBytes = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(fixture.Path("data/m1/models/m1.gltf")).Replace("\"ground\"", "\"fresh_surface\"", StringComparison.Ordinal));
        fixture.Write("data/m1/models/fresh.gltf", modelBytes);
        string definitions = File.ReadAllText(fixture.Path("data/m1/zrdr/gates.zad"));
        fixture.Write("data/m1/zrdr/gates.zad", definitions.Replace("ANIMATION_LIST (", """
            ANIMATION_LIST (
              ANIMATION_DEFINITION ( NAME ( "lamp*" ) ANIMATION_NAME ( "move_lamp*" ) ACTIVATION ( ON_STARTUP )
                SEQUENCE_DEFINITION ( NAME ( place ) OBJECT_TRANSLATE_STATE ( NAME ( INPUT_NODE ) STATE ( 0 0 0 ) ) ) )
            """, StringComparison.Ordinal));
        fixture.WritePartDatabase();
        var part = JsonNode.Parse(File.ReadAllBytes(fixture.Path("data/m1/models/m1_01.gltf")))!;
        int sharedIndex = part["nodes"]!.AsArray().Count;
        part["nodes"]!.AsArray().Add(new JsonObject { ["name"] = "Shared", ["children"] = part["scenes"]![0]!["nodes"]!.DeepClone(),
            ["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["group"] = true } } });
        part["scenes"]![0]!["nodes"] = new JsonArray(sharedIndex);
        fixture.Write("data/m1/models/m1_01.gltf", part.ToJsonString());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, Width = 1200, Height = 850, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            await Job("source_world_open", new() { ["mission"] = "m1" });
            var doc = main.ViewModel.SelectedDocument!;
            await Job("source_group_add", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["name"] = "Buildings" });
            doc = main.ViewModel.SelectedDocument!;
            var groups = await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString(), ["search"] = "Buildings" });
            var group = groups["matches"]!["items"]!.AsArray().Single()!["item"]!;
            Dictionary<string, object?> Parent(JsonNode row) => new() { ["sourceFile"] = row["sourceFile"]!.GetValue<string>(), ["gltfNode"] = row["gltfNode"]!.GetValue<int>(), ["sourceOccurrence"] = row["sourceOccurrence"]!.GetValue<string>() };
            await Job("source_group_add", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["name"] = "Interior", ["parent"] = Parent(group) });
            doc = main.ViewModel.SelectedDocument!;
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            doc = main.ViewModel.SelectedDocument!;
            groups = await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString(), ["search"] = "Shared" });
            var shared = groups["matches"]!["items"]!.AsArray().Select(i => i!["item"]!).Where(i => i["sourceFile"]?.GetValue<string>() == "data/m1/models/m1_01.gltf").ToArray();
            Assert.Equal(2, shared.Length);
            await Job("source_group_add", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["name"] = "SharedChild", ["parent"] = Parent(shared[1]) });
            doc = main.ViewModel.SelectedDocument!;
            groups = await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString(), ["search"] = "SharedChild" });
            Assert.Equal(2, groups["matches"]!["items"]!.AsArray().Count);
            var currentTree = (SceneTreeModel)typeof(MainWindow).GetField("sceneTree", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            Assert.Equal(groups["matches"]!["items"]![1]!["item"]!["row"]!.GetValue<string>(), currentTree.Selected!.Id);
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            doc = main.ViewModel.SelectedDocument!;
            groups = await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString(), ["search"] = "Interior" });
            group = groups["matches"]!["items"]!.AsArray().Single()!["item"]!;
            var parentArgument = Parent(group);
            Assert.True(group["sourceGroup"]!.GetValue<bool>());
            // Context actions use the right-clicked row, even while a different group remains selected.
            var parentTree = (SceneTreeModel)typeof(MainWindow).GetField("sceneTree", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            var clicked = parentTree.CreatedRows.Single(r => r.StateKey == parentArgument["sourceOccurrence"]!.ToString());
            typeof(MainWindow).GetField("contextScene", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, clicked);
            typeof(MainWindow).GetField("sceneContextDocument", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, doc);
            typeof(MainWindow).GetField("scenePointerContext", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, true);
            var menu = ((System.Windows.Controls.TreeView)main.FindName("DocumentSceneTree")).ContextMenu;
            typeof(MainWindow).GetMethod("SceneContextOpening", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [menu, new RoutedEventArgs(System.Windows.Controls.ContextMenu.OpenedEvent, menu)]);
            Assert.Equal(Visibility.Visible, ((System.Windows.Controls.MenuItem)main.FindName("SceneAddGroupMenu")).Visibility);
            Assert.Equal(Visibility.Visible, ((System.Windows.Controls.MenuItem)main.FindName("SceneAddObjectMenu")).Visibility);
            Assert.Equal(parentArgument["sourceOccurrence"], ((zStudio.Core.Sources.SourceCreationParent)typeof(MainWindow).GetMethod("ContextCreationParent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null)!).SourceOccurrence);
            groups = await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString(), ["search"] = "Shared" });
            var placementParents = groups["matches"]!["items"]!.AsArray().Select(i => i!["item"]!)
                .Where(i => i["sourceFile"]?.GetValue<string>() == "data/m1/models/m1_01.gltf" && i["gltfNode"]?.GetValue<int>() == sharedIndex).ToArray();
            parentArgument = Parent(placementParents[1]);
            var historyBefore = doc.SourceWorld!.Workspace.UndoCount;
            var originalScript = File.ReadAllBytes(fixture.Path("gamegen/m1.gs"));
            var view = (SceneViewport)typeof(MainWindow).GetField("scene", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            var state = await Call("state", new()); string preview = state["preview"]!.GetValue<string>();
            long baseGeometry = view.RetainedGeometryBytes;
            if (Environment.GetEnvironmentVariable("ZSTUDIO_PLACEMENT_CAPTURE") is { } browserFolder)
            {
                Directory.CreateDirectory(browserFolder);
                foreach (string theme in new[] { "Dark", "Light" })
                {
                    typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [theme]);
                    var browser = new SourceObjectBrowser(main, fixture.Project, "m1", ct => Task.Run(() => zStudio.Core.Sources.SourceWorlds.Models(new(fixture.Project), "m1", ct), ct),
                        (_, _, _) => throw new InvalidDataException("A model or texture is misplaced. Move it into the common or current map models/textures folder and update its glTF reference."),
                        "Inside Shared. This source is shared: the change affects all 2 copies in this map and maps m1.");
                    browser.Show();
                    var list = (System.Windows.Controls.ListBox)typeof(SourceObjectBrowser).GetField("list", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(browser)!;
                    while (list.Items.Count == 0) await Task.Delay(10, token);
                    list.SelectedIndex = 0;
                    var name = (System.Windows.Controls.TextBox)typeof(SourceObjectBrowser).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(browser)!;
                    name.Text = "custom_name";
                    ((System.Windows.Controls.TextBox)typeof(SourceObjectBrowser).GetField("filter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(browser)!).Text = theme == "Light" ? "no_matching_models" : "models";
                    Assert.Equal("custom_name", name.Text);
                    await Task.Delay(50, token);
                    SaveWindow(browser, Path.Combine(browserFolder, theme + "-browser.png"));
                    browser.Close();
                }
            }
            Dictionary<string, object?> Args(string action, string? draft = null)
            {
                Dictionary<string, object?> args = new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = preview, ["action"] = action };
                if (draft != null) args["token"] = draft;
                return args;
            }
            var begin = Args("begin"); begin["model"] = "data/m1/models/fresh.gltf"; begin["parent"] = parentArgument;
            var controlled = new Dictionary<string, object?>(begin) { ["name"] = "lamp1" };
            Assert.Contains("mission initialization", (await Job("source_world_placement", controlled, "failed")).ToJsonString());
            Assert.False(view.HasModelPlacement); Assert.False(doc.IsDirty); Assert.Equal(historyBefore, doc.SourceWorld!.Workspace.UndoCount);
            string draft = (await Job("source_world_placement", begin))["placement"]!["token"]!.GetValue<string>();
            Assert.True(view.HasModelPlacement); Assert.False(doc.IsDirty); Assert.Equal(historyBefore, doc.SourceWorld!.Workspace.UndoCount);
            Assert.False(view.CanStartFly(out _));
            Assert.NotNull((await Job("source_world_placement", Args("get")))["placement"]!["token"]);
            var point = Args("set", draft); point["screenPoint"] = new double[] { -10, -10 };
            Assert.False((await Job("source_world_placement", point))["placement"]!["valid"]!.GetValue<bool>());
            Assert.Equal("not_ready", (await Job("source_world_placement", Args("confirm", draft), "failed"))["code"]!.GetValue<string>());
            var beforeCube = view.CaptureView();
            view.RestoreView(beforeCube with { Position = new(32, 100, -32), LookDirection = new(0, -100, 0), UpDirection = new(0, 0, -1), Projection = "orthographic", OrthographicWidth = 16 });
            var cube = view.NavigationCubeBounds; Assert.False(cube.IsEmpty);
            var cubeCenter = new System.Windows.Point(cube.X + cube.Width / 2, cube.Y + cube.Height / 2);
            var cubePoint = Args("set", draft); cubePoint["screenPoint"] = new[] { cubeCenter.X, cubeCenter.Y };
            Assert.False((await Job("source_world_placement", cubePoint))["placement"]!["valid"]!.GetValue<bool>());
            Assert.True(view.NavigateCubeAt(cubeCenter));
            view.RestoreView(beforeCube);
            Assert.True(view.ProjectToView(new Vector3(32, 0, -32), out var screen, out bool front)); Assert.True(front);
            point["screenPoint"] = new[] { screen.X, screen.Y };
            Assert.True((await Job("source_world_placement", point))["placement"]!["valid"]!.GetValue<bool>());
            Assert.NotNull(view.ModelPlacementPosition);
            Assert.True(view.RetainedGeometryBytes > 0);
            if (Environment.GetEnvironmentVariable("ZSTUDIO_PLACEMENT_CAPTURE") is { } folder)
            {
                Directory.CreateDirectory(folder);
                var pose = view.CaptureView();
                foreach (string theme in new[] { "Dark", "Light" })
                {
                    typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [theme]);
                    foreach (string projection in new[] { "perspective", "orthographic" })
                    {
                        view.RestoreView(pose with { Projection = projection, OrthographicWidth = 110 });
                        await Task.Delay(50, token);
                        Assert.True(view.ProjectToView(new Vector3(32, 0, -32), out var hit, out _));
                        var update = Args("set", draft); update["screenPoint"] = new[] { hit.X, hit.Y }; await Job("source_world_placement", update);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(view.RenderImage(900, 600)));
                        using var output = File.Create(Path.Combine(folder, theme + "-" + projection + ".png")); encoder.Save(output);
                        SaveWindow(main, Path.Combine(folder, theme + "-" + projection + "-window.png"));
                    }
                }
                view.RestoreView(pose); await Task.Delay(50, token);
            }
            await Job("source_world_placement", Args("cancel", draft));
            Assert.False(view.HasModelPlacement); Assert.False(doc.IsDirty); Assert.Equal(originalScript, File.ReadAllBytes(fixture.Path("gamegen/m1.gs")));
            Assert.Equal(baseGeometry, view.RetainedGeometryBytes);
            draft = (await Job("source_world_placement", begin))["placement"]!["token"]!.GetValue<string>();
            point["token"] = draft; await Job("source_world_placement", point);
            main.SourceEditPreparing = _ => fixture.Write("data/m1/models/fresh.gltf", System.Text.Encoding.UTF8.GetString(modelBytes).Replace("fresh_surface", "different_surface", StringComparison.Ordinal));
            Assert.Equal("external_change", (await Job("source_world_placement", Args("confirm", draft), "failed"))["code"]!.GetValue<string>());
            main.SourceEditPreparing = null; fixture.Write("data/m1/models/fresh.gltf", modelBytes);
            Assert.False(doc.IsDirty); Assert.False(view.HasModelPlacement);
            draft = (await Job("source_world_placement", begin))["placement"]!["token"]!.GetValue<string>();
            point["token"] = draft; await Job("source_world_placement", point);
            // A rejected competing MCP confirm must not cancel the GUI operation already preparing this draft.
            using var confirmReleased = new ManualResetEventSlim();
            var confirmEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            main.SourceEditPreparing = ct => { confirmEntered.TrySetResult(); confirmReleased.Wait(ct); };
            var guiConfirm = (Task<DocumentModel>)typeof(MainWindow).GetMethod("ConfirmModelPlacementAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [token])!;
            try
            {
                await confirmEntered.Task.WaitAsync(token);
                Assert.Equal("busy", (await Job("source_world_placement", Args("confirm", draft), "failed"))["code"]!.GetValue<string>());
                var moveWhileConfirming = Args("set", draft); moveWhileConfirming["screenPoint"] = new double[] { -10, -10 };
                Assert.Equal("busy", (await Job("source_world_placement", moveWhileConfirming, "failed"))["code"]!.GetValue<string>());
                var fixedPosition = view.ModelPlacementPosition;
                var confirmingPose = view.CaptureView();
                view.RestoreView(confirmingPose with { Position = confirmingPose.Position + new System.Windows.Media.Media3D.Vector3D(10, 0, 0) });
                view.RenderImage(600, 400);
                Assert.Equal(fixedPosition, view.ModelPlacementPosition);
                view.RestoreView(confirmingPose);
                Assert.True(view.HasModelPlacement);
                await Job("source_world_placement", Args("cancel", draft));
            }
            finally { main.SourceEditPreparing = null; confirmReleased.Set(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guiConfirm);
            Assert.False(doc.IsDirty); Assert.Equal(historyBefore, doc.SourceWorld!.Workspace.UndoCount);
            draft = (await Job("source_world_placement", begin))["placement"]!["token"]!.GetValue<string>();
            point["token"] = draft; await Job("source_world_placement", point);
            var accepted = await Job("source_world_placement", Args("confirm", draft));
            doc = main.ViewModel.SelectedDocument!;
            Assert.Equal(historyBefore + 1, doc.SourceWorld!.Workspace.UndoCount); Assert.True(doc.IsDirty);
            Assert.Contains(doc.Document.Scene!.Nodes, n => n.Name == "fresh_new");
            var tree = (SceneTreeModel)typeof(MainWindow).GetField("sceneTree", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            Assert.Equal("Shared", tree.Selected?.Parent?.DisplayName);
            var newRows = await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString(), ["search"] = "fresh_new" });
            Assert.Equal(2, newRows["matches"]!["items"]!.AsArray().Count);
            Assert.Equal(newRows["matches"]!["items"]![1]!["item"]!["row"]!.GetValue<string>(), tree.Selected!.Id);
            Assert.Equal(originalScript, File.ReadAllBytes(fixture.Path("gamegen/m1.gs")));
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" });
            doc = main.ViewModel.SelectedDocument!;
            Assert.False(doc.IsDirty); Assert.DoesNotContain(doc.Document.Scene!.Nodes, n => n.Name == "fresh_new");
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "redo" });
            doc = main.ViewModel.SelectedDocument!;
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            doc = main.ViewModel.SelectedDocument!;
            await Call("close_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            await Job("source_world_open", new() { ["mission"] = "m1" });
            Assert.Contains(main.ViewModel.SelectedDocument!.Document.Scene!.Nodes, n => n.Name == "fresh_new");

            static void SaveImage(System.Windows.Media.Imaging.BitmapSource image, string path)
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using var file = File.Create(path); encoder.Save(file);
            }
            static void SaveWindow(Window window, string path)
            {
                window.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window); SaveImage(bitmap, path);
            }

            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.False(result.IsError, text); return JsonNode.Parse(text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
            }
        }
        finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); }
    }
}
