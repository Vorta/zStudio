using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Mcp;
using zStudio.Rendering;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>Essential World workflow: expose an initialized hidden state, select an exact part, and persist only its edit.</summary>
internal static class SourceWorldTreeChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        fixture.WriteReferencedTurretDatabase();
        // Match reconstructed source models, whose polygon-zone arrays are explicit before any editing.
        var fixtureModel = JsonNode.Parse(File.ReadAllBytes(fixture.Path("data/m2/models/turret.gltf")))!;
        foreach (var mesh in fixtureModel["meshes"]!.AsArray())
            foreach (var primitive in mesh!["primitives"]!.AsArray())
                primitive!["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["polygonZones"] = new JsonArray(new JsonArray()) } };
        fixture.Write("data/m2/models/turret.gltf", fixtureModel.ToJsonString());
        byte[] original = File.ReadAllBytes(fixture.Path("data/m2/models/turret.gltf"));
        byte[] placement = File.ReadAllBytes(fixture.Path("data/m2/models/m2.gltf"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var token = deadline.Token;
        var main = new MainWindow { ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var opened = await Job("source_world_open", new() { ["mission"] = "m2" });
            var doc = Document(opened["document"]!);
            var healthy = await Row("healthy"); var wreck = await Row("wreck");
            int owner = healthy["selectionOwner"]!.GetValue<int>();
            Assert.False(healthy["hidden"]!.GetValue<bool>());
            Assert.True(wreck["hidden"]!.GetValue<bool>());
            Assert.False(wreck["visibility"]!["DefaultVisible"]!.GetValue<bool>());
            var pose = View().CaptureView();
            long revision = doc.Revision; int history = doc.SourceWorld!.Workspace.UndoCount;

            await Tree("select", "wreck");
            Assert.Equal(Node("wreck"), Card().Selection!.SelectionNode);
            Assert.Equal(Node("wreck"), Card().ObjectNode);
            Assert.False(Card().Selection!.Active);
            // A visibility refresh must preserve the viewed rows, even when selection is elsewhere.
            main.DocumentSceneTree.Height = 160;
            await Idle();
            var scroll = Descendants(main.DocumentSceneTree).OfType<ScrollViewer>().First();
            scroll.ScrollToBottom(); scroll.ScrollToRightEnd(); await Idle();
            double vertical = scroll.VerticalOffset, horizontal = scroll.HorizontalOffset;
            var retainedTree = TreeModel();
            Assert.True(vertical > 0 && horizontal > 0);
            CheckGutter(scroll);
            await Tree("hide", "healthy");
            await CheckScroll();
            await Tree("show", "wreck");
            await CheckScroll();
            Assert.True((await Row("healthy"))["hidden"]!.GetValue<bool>());
            Assert.False((await Row("wreck"))["hidden"]!.GetValue<bool>());
            Assert.DoesNotContain(Node("healthy"), View().VisibleSceneNodes);
            Assert.Contains(Node("wreck"), View().VisibleSceneNodes);
            Assert.True(View().SelectInspectionNode(Node("wreck"), exact: true));
            Assert.Equal(Node("wreck"), View().SelectedInspection!.SelectionNode);
            Assert.True(View().SelectionBoundsVisible);
            // The same rendered target picked in the map still selects the whole turret.
            string hit = View().SelectedInspection!.Target;
            Assert.True(View().SelectInspection(hit)); Assert.Equal(owner, View().SelectedInspection!.SelectionNode);
            Assert.Equal(pose, View().CaptureView());
            Assert.Equal(revision, doc.Revision); Assert.Equal(history, doc.SourceWorld.Workspace.UndoCount);
            Assert.False(doc.IsDirty); Assert.Equal(original, File.ReadAllBytes(fixture.Path("data/m2/models/turret.gltf")));
            await Idle(); vertical = scroll.VerticalOffset; horizontal = scroll.HorizontalOffset;
            await Job("source_map_parts_visibility", new() { ["preview"] = Preview(), ["document"] = Id(), ["revision"] = doc.Revision, ["owner"] = owner, ["action"] = "reset" });
            await CheckScroll();
            Assert.False((await Row("healthy"))["hidden"]!.GetValue<bool>());
            Assert.True((await Row("wreck"))["hidden"]!.GetValue<bool>());

            await Tree("select", "healthy"); await Idle(); main.Activate();
            var eye = Descendants(main.DocumentSceneTree).OfType<Button>().Single(b => b.Name == "RowEye" && b.Tag is SceneTreeItem r && r.Index == Node("healthy"));
            var eyeRow = typeof(MainWindow).GetMethod("RowContainer", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [eye]);
            typeof(MainWindow).GetMethod("FocusRowButton", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [eye, eyeRow]);
            Assert.Same(eye, System.Windows.Input.Keyboard.FocusedElement); await Idle();
            vertical = scroll.VerticalOffset; horizontal = scroll.HorizontalOffset;
            await ClickEye(eye); await CheckScroll();
            Assert.Same(eye, System.Windows.Input.Keyboard.FocusedElement);
            await ClickEye(eye); await CheckScroll();
            // A filtered list keeps its containers and keyboard-focused eye too.
            main.OutlinerResults.Height = 100;
            main.OutlinerSearch.Text = "e";
            await Task.Delay(220, token); // Let the existing search debounce finish before checking visibility refreshes.
            await Idle();
            var matches = main.OutlinerResults.ItemsSource;
            var matchEye = Descendants(main.OutlinerResults).OfType<Button>().First(b => b.Name == "MatchEye" && b.Tag is SceneTreeItem r && r.Index == Node("healthy"));
            Assert.True(matchEye.Focus()); await Idle();
            var matchScroll = Descendants(main.OutlinerResults).OfType<ScrollViewer>().First();
            CheckGutter(matchScroll);
            double matchOffset = matchScroll.VerticalOffset;
            await ClickEye(matchEye);
            Assert.Same(matches, main.OutlinerResults.ItemsSource); Assert.Same(matchEye, System.Windows.Input.Keyboard.FocusedElement);
            Assert.Equal(matchOffset, matchScroll.VerticalOffset, 2);
            await ClickEye(matchEye);
            main.OutlinerSearch.Text = "";
            await Task.Delay(220, token); // Let the existing search debounce finish before checking visibility refreshes.
            await Idle();
            await Call("scene_card", new() { ["preview"] = Preview(), ["action"] = "clear" });
            scroll.ScrollToBottom(); scroll.ScrollToRightEnd(); await Idle();
            vertical = scroll.VerticalOffset; horizontal = scroll.HorizontalOffset;
            await Tree("hide", "healthy"); await CheckScroll();
            await Tree("show", "healthy"); await CheckScroll();
            using (var canceled = new CancellationTokenSource())
            {
                string beforePreview = Preview();
                canceled.Cancel();
                var update = (Task)typeof(MainWindow).GetMethod("SetSourceMapPartVisibilityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main, [owner, Node("healthy"), false, canceled.Token])!;
                await Assert.ThrowsAsync<zStudio.Automation.StudioCommandException>(() => update);
                Assert.Equal(beforePreview, Preview()); await CheckScroll();
                Assert.False((await Row("healthy"))["hidden"]!.GetValue<bool>());
            }

            await Tree("select", "barrel");
            await Idle();
            var selectedHeader = Descendants(main.DocumentSceneTree).OfType<System.Windows.FrameworkElement>()
                .Single(e => e.Name == "SourceRow" && e.DataContext is SceneTreeItem r && r.Index == Node("barrel"));
            Assert.InRange(selectedHeader.TranslatePoint(new System.Windows.Point(), scroll).Y, -1, scroll.ViewportHeight);
            Assert.Equal(Node("barrel"), View().SelectedInspection!.SelectionNode);
            Assert.Equal(Node("barrel"), Card().ObjectNode);
            var exact = await Call("source_world_object", new() { ["document"] = Id(), ["node"] = Node("barrel"), ["scope"] = "node" });
            Assert.Equal("barrel", exact["object"]!["name"]!.GetValue<string>());
            var whole = await Call("source_world_object", new() { ["document"] = Id(), ["node"] = Node("barrel") });
            Assert.Equal("tur_101", whole["object"]!["name"]!.GetValue<string>());
            bool before = exact["editableFlags"]!.AsArray().Single(f => f!["bit"]!.GetValue<string>() == "0x40")!["on"]!.GetValue<bool>();
            // Ending handles or reverting a child draft must not silently retarget the next edit to its owner.
            Card().SetObjectMode("move"); Card().SetObjectMode("none");
            Assert.True(View().SelectedInspection!.ExactSelection);
            Assert.Equal(Node("barrel"), View().SelectedInspection!.SelectionNode);
            Assert.Equal(Node("barrel"), Card().ObjectNode);
            Card().SetObjectFields(new JsonArray(new JsonObject { ["field"] = "flag_40", ["value"] = before ? "off" : "on" }));
            Card().CancelDraft();
            Assert.True(View().SelectedInspection!.ExactSelection);
            Assert.Equal(Node("barrel"), View().SelectedInspection!.SelectionNode);
            Assert.Equal(Node("barrel"), Card().ObjectNode);
            Card().SetObjectFields(new JsonArray(new JsonObject { ["field"] = "flag_40", ["value"] = before ? "off" : "on" }));
            await Call("scene_tree", new() { ["document"] = Id(), ["context"] = TreeModel().Context, ["preview"] = Preview(), ["action"] = "select", ["node"] = Node("healthy") }, "pending_drafts");
            doc = Document(await Call("resolve_drafts", new() { ["document"] = Id(), ["target"] = "scene", ["token"] = Card().DraftToken, ["action"] = "apply" }));
            Assert.Equal("barrel", Card().ObjectEditor!.State.Name);
            Assert.Equal(new[] { "data/m2/models/turret.gltf" }, doc.SourceWorld!.Workspace.DirtyFiles);
            Assert.Equal(history + 1, doc.SourceWorld.Workspace.UndoCount);
            doc = Document(await Call("undo_redo", new() { ["document"] = Id(), ["revision"] = doc.Revision, ["action"] = "undo" }));
            Assert.Equal(original, doc.SourceWorld!.Workspace.Read("data/m2/models/turret.gltf"));
            doc = Document(await Call("undo_redo", new() { ["document"] = Id(), ["revision"] = doc.Revision, ["action"] = "redo" }));
            await Job("save_document", new() { ["document"] = Id(), ["revision"] = doc.Revision });
            var saved = JsonNode.Parse(File.ReadAllBytes(fixture.Path("data/m2/models/turret.gltf")))!;
            var initial = JsonNode.Parse(original)!;
            var savedNodes = saved["nodes"]!.AsArray(); var initialNodes = initial["nodes"]!.AsArray();
            int barrel = initialNodes.Select((n, i) => (n, i)).Single(p => p.n!["name"]!.GetValue<string>() == "barrel").i;
            Assert.False(JsonNode.DeepEquals(initialNodes[barrel], savedNodes[barrel]));
            savedNodes[barrel] = initialNodes[barrel]!.DeepClone();
            Assert.True(JsonNode.DeepEquals(initial, saved));
            Assert.Equal(placement, File.ReadAllBytes(fixture.Path("data/m2/models/m2.gltf")));
            main.ViewModel.CloseResolved(doc);
            doc = Document((await Job("source_world_open", new() { ["mission"] = "m2" }))["document"]!);
            await Tree("select", "barrel");
            Assert.Equal(!before, (Card().ObjectEditor!.State.Flags & 0x40) != 0);
            // The explicit MCP edit scope must target the same child as the card, never its enclosing placement.
            doc = Document((await Job("source_world_object_edit", new() { ["document"] = Id(), ["revision"] = doc.Revision,
                ["node"] = Node("barrel"), ["scope"] = "node", ["action"] = "flag", ["flag"] = "0x40", ["on"] = before }))["document"]!);
            var changed = await Call("source_world_object", new() { ["document"] = Id(), ["node"] = Node("barrel"), ["scope"] = "node" });
            Assert.Equal(before, changed["editableFlags"]!.AsArray().Single(f => f!["bit"]!.GetValue<string>() == "0x40")!["on"]!.GetValue<bool>());
            Assert.Equal(new[] { "data/m2/models/turret.gltf" }, doc.SourceWorld!.Workspace.DirtyFiles);
            doc = Document(await Call("undo_redo", new() { ["document"] = Id(), ["revision"] = doc.Revision, ["action"] = "undo" }));
            Assert.False(doc.IsDirty);

            string Id() => doc.SessionId.ToString();
            async Task Idle() { await main.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle); }
            async Task CheckScroll()
            {
                await Idle();
                Assert.Same(retainedTree, TreeModel());
                Assert.Equal(vertical, scroll.VerticalOffset, 2);
                Assert.Equal(horizontal, scroll.HorizontalOffset, 2);
            }
            async Task ClickEye(Button button)
            {
                var row = (SceneTreeItem)button.Tag;
                bool wasHidden = row.IsHidden;
                button.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent, button));
                await (Task)typeof(MainWindow).GetField("staticRefreshWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                await Idle();
                Assert.Equal(!wasHidden, row.IsHidden);
            }
            string Preview() => typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!.ToString()!;
            SceneViewport View() => (SceneViewport)main.SceneHost.Content!;
            SceneInspectionCard Card() => (SceneInspectionCard)View().InspectionContent!;
            SceneTreeModel TreeModel() => (SceneTreeModel)typeof(MainWindow).GetField("sceneTree", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            int Node(string name) => doc.Document.Scene!.Nodes.Single(n => n.Name == name && n.Class == "object3d"
                && doc.SourceBuild!.Provenance.GetValueOrDefault(n.Index)?.ModelFile == "data/m2/models/turret.gltf").Index;
            DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == state["id"]!.GetValue<string>());
            async Task<JsonNode> Row(string name)
            {
                var result = await Call("scene_tree", new() { ["document"] = Id(), ["search"] = name });
                return result["matches"]!["items"]!.AsArray().Single(r => r!["item"]!["node"]?.GetValue<int>() == Node(name))!["item"]!;
            }
            async Task Tree(string action, string name)
            {
                var row = await Row(name);
                await Call("scene_tree", new() { ["document"] = Id(), ["context"] = TreeModel().Context, ["preview"] = Preview(), ["row"] = row["row"]!.GetValue<string>(), ["action"] = action });
            }
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, string? error = null)
            {
                var response = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                var result = JsonNode.Parse(response.Content.OfType<TextContentBlock>().Single().Text)!;
                if (error == null) Assert.False(response.IsError == true, result.ToJsonString()); else Assert.Equal(error, result["code"]!.GetValue<string>());
                return result;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments)
            {
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == "completed", job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            if (main.SceneHost.Content is SceneViewport { InspectionContent: SceneInspectionCard card }) card.CancelDraft();
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
    private static void CheckGutter(ScrollViewer scroll)
    {
        // Deep rows can extend beyond the viewport; their visible/hit-testable content must end before the full scrollbar track.
        var content = Assert.IsType<ScrollContentPresenter>(scroll.Template.FindName("PART_ScrollContentPresenter", scroll));
        var bar = Assert.IsType<ScrollBar>(scroll.Template.FindName("PART_VerticalScrollBar", scroll));
        Assert.True(bar.IsVisible); Assert.True(content.ClipToBounds);
        double right = content.TranslatePoint(new(content.ActualWidth, 0), scroll).X;
        double left = bar.TranslatePoint(new(), scroll).X;
        Assert.True(left - right >= 3.5, $"World scrollbar overlaps row content: gap {left - right} DIP");
    }
    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
