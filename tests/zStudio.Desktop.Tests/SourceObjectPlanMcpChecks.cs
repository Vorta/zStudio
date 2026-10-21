using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Mcp;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>
/// Map-authored objects through MCP and Properties: a rebuilt object retains its selection, and generated Properties
/// and MCP share exclusive crater modes, undo, supported flags and verified source persistence.
/// </summary>
internal static class SourceObjectPlanMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        fixture.WriteNestedDatabase(lookupEdits: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var doc = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);
            int followed = Nodes(doc, "post").Single();

            // Properties' actual generated field callback rebuilds the source world. Its selection
            // must follow the same occurrence using the new snapshot's identity index.
            var show = typeof(MainWindow).GetMethod("ShowSourceObjectPropertiesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.True(await (Task<bool>)show.Invoke(main, [doc, followed, token])!);
            var card = Card(); card.Refresh(); main.UpdateLayout();
            Assert.Null(main.OpenPropertiesWindow);
            var fields = card.ObjectEditor!;
            var position = Descendants(fields).OfType<System.Windows.Controls.TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Position Z");
            int undoBefore = doc.SourceWorld!.Workspace.UndoCount;
            position.Text = "4";
            Assert.True(card.HasObjectDraft); Assert.Equal(undoBefore, doc.SourceWorld.Workspace.UndoCount);
            await Call("resolve_drafts", new() { ["document"] = Id(doc), ["target"] = "scene", ["token"] = card.DraftToken, ["action"] = "discard" });
            Assert.Equal("0", position.Text); Assert.False(card.HasObjectDraft);
            position.Text = "4";
            // A second field shares this one transaction and rebuild.
            await Call("scene_card", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["action"] = "set", ["token"] = card.DraftToken,
                ["fields"] = new[] { new { field = "flag_40", value = "on" } } });
            doc = Document(await Call("resolve_drafts", new() { ["document"] = Id(doc), ["target"] = "scene", ["token"] = card.DraftToken, ["action"] = "apply" }));
            Assert.Equal(undoBefore + 1, doc.SourceWorld!.Workspace.UndoCount);
            var changed = Card().ObjectEditor!;
            Assert.Equal("post", changed.State.Name);
            Assert.Equal(Nodes(doc, "post").Single(), changed.State.Node);
            Assert.NotNull(changed.State.Transform);
            Assert.Equal(4f, changed.State.Transform.Value.Position.Z);

            // Only objects have flags to edit, as Properties shows them (not the world, which a script made); a node of a glTF
            // file carries every flag.
            int world = doc.Document.Scene!.Nodes.First(n => n.Name == "world" && doc.SourceBuild!.Provenance.ContainsKey(n.Index)).Index;
            static IEnumerable<string> Bits(JsonNode state) => state["editableFlags"]!.AsArray().Select(f => f!["bit"]!.GetValue<string>());
            // A meshless World has the same card route and its clean token cannot edit a different selection.
            string priorToken = Card().DraftToken; string priorTarget = Card().Selection!.Target;
            await Call("scene_properties", new() { ["node"] = world, ["open"] = true });
            Assert.Equal(world, Card().ObjectNode);
            var tree = (SceneTreeModel)typeof(MainWindow).GetField("sceneTree", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
            Assert.Equal(world, tree.Selected?.Index);
            Assert.Null(main.OpenPropertiesWindow);
            Assert.NotEqual(priorToken, Card().DraftToken);
            var inspected = await Call("scene_inspect", new());
            Assert.Equal(Card().Selection!.Target, inspected["selected"]!.GetValue<string>());
            Assert.NotNull(inspected["inspection"]);
            Assert.Contains("draft_conflict", (await Call("scene_card", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["action"] = "set", ["token"] = priorToken,
                ["fields"] = new[] { new { field = "WorldSetAmbient", value = "1 1 1" } } }, error: true)).GetValue<string>());
            // Failed GUI Apply retains a form that can be corrected, including after its outer reporting task completes.
            var worldCard = Card();
            worldCard.SetObjectFields(new JsonArray(new JsonObject { ["field"] = "WorldSetFogDensity", ["value"] = "0.25" }));
            var preparing = main.SourceEditPreparing;
            main.SourceEditPreparing = _ => throw new InvalidDataException("Test worker preparation failure");
            try
            {
            typeof(SceneInspectionCard).GetMethod("Apply", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(worldCard, null);
            if (typeof(SceneInspectionCard).GetField("applying", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(worldCard) is Task applying) await applying;
            } finally { main.SourceEditPreparing = preparing; }
            Assert.True(worldCard.HasObjectDraft); Assert.True(worldCard.ObjectEditor!.IsEnabled);
            worldCard.CancelDraft();
            await Call("scene_card", new() { ["action"] = "select", ["target"] = priorTarget });
            Assert.Equal(CurrentPost(), Card().ObjectNode);
            Assert.Empty(Bits(await Call("source_world_object", new() { ["document"] = Id(doc), ["node"] = world })));
            Assert.Contains("0x20000", Bits(await Call("source_world_object", new() { ["document"] = Id(doc), ["node"] = Nodes(doc, "crate").First() })));

            // The actual Properties callback and MCP share one exclusive crater-mode edit; a mode switch is one undoable operation.
            await SetProperty("CanModify (craters and quicksand allowed)", "on");
            await AssertModes(0x10000);
            // Cancel resets mode bookkeeping; retrying the same exclusive switch must still clear CanModify.
            Card().SetObjectFields(new JsonArray(new JsonObject { ["field"] = "flag_20000", ["value"] = "on" }));
            Card().CancelDraft();
            Card().SetObjectFields(new JsonArray(new JsonObject { ["field"] = "flag_20000", ["value"] = "on" }));
            Assert.False(Card().ObjectEditor!.Patch(_ => null).Flags![0x10000]);
            Card().CancelDraft();
            int history = doc.SourceWorld!.Workspace.UndoCount;
            doc = Document((await Job("source_world_object_edit", new()
            {
                ["document"] = Id(doc), ["revision"] = doc.Revision, ["node"] = CurrentPost(),
                ["flag"] = "0x20000", ["on"] = true
            }))["document"]!);
            Assert.Equal(history + 1, doc.SourceWorld!.Workspace.UndoCount);
            await AssertModes(0x20000);
            doc = Document(await Call("undo_redo", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["action"] = "undo" }));
            await AssertModes(0x10000);
            doc = Document(await Call("undo_redo", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["action"] = "redo" }));
            await AssertModes(0x20000);
            await SetProperty("ClipTo (craters and quicksand blocked)", "off");
            await AssertModes(0);
            await SetProperty("Overwrite", "on");
            var overwritten = (await Call("source_world_object", new() { ["document"] = Id(doc), ["node"] = CurrentPost() }))["editableFlags"]!.AsArray();
            Assert.True(overwritten.Single(f => f!["bit"]!.GetValue<string>() == "0x800000")!["on"]!.GetValue<bool>());
            // Saved, so closing asks nothing.
            await Job("save_document", new() { ["document"] = Id(doc), ["revision"] = doc.Revision });
            var saved = JsonNode.Parse(File.ReadAllBytes(fixture.Path("data/m1/models/m1.gltf")))!;
            var attributes = saved["nodes"]!.AsArray().Single(n => n?["name"]?.GetValue<string>() == "post")!["extras"]!["recoil"]!.AsObject();
            Assert.False(attributes.ContainsKey("declient"));
            Assert.False(attributes.ContainsKey("flags"));
            Assert.True(attributes["overwrite"]!.GetValue<bool>());

            int CurrentPost() => Nodes(doc, "post").Single();
            async Task SetProperty(string label, string value)
            {
                Assert.True(await (Task<bool>)show.Invoke(main, [doc, CurrentPost(), token])!);
                var selected = Card(); selected.Refresh();
                string key = "flag_" + SourceObjectFields.EditableFlags.Single(f => f.Label == label).Bit.ToString("X");
                selected.SetObjectFields(new JsonArray(new JsonObject { ["field"] = key, ["value"] = value }));
                doc = Document(await Call("resolve_drafts", new() { ["document"] = Id(doc), ["target"] = "scene", ["token"] = selected.DraftToken, ["action"] = "apply" }));
            }
            async Task AssertModes(uint expected)
            {
                foreach (int node in Nodes(doc, "post"))
                {
                    var flags = (await Call("source_world_object", new() { ["document"] = Id(doc), ["node"] = node }))["editableFlags"]!.AsArray();
                    Assert.Equal((expected & 0x10000) != 0, flags.Single(f => f!["bit"]!.GetValue<string>() == "0x10000")!["on"]!.GetValue<bool>());
                    Assert.Equal((expected & 0x20000) != 0, flags.Single(f => f!["bit"]!.GetValue<string>() == "0x20000")!["on"]!.GetValue<bool>());
                }
            }

            SceneInspectionCard Card() => (SceneInspectionCard)typeof(MainWindow).GetProperty("CurrentInspectionCard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
            static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); yield return child;
                    foreach (var item in Descendants(child)) yield return item;
                }
            }
            List<int> Nodes(DocumentModel d, string name) => [.. d.Document.Scene!.Nodes.Where(n => n.Name == name && d.SourceBuild!.Provenance.ContainsKey(n.Index)).Select(n => n.Index)];
            DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == state["id"]!.GetValue<string>());
            static string Id(DocumentModel d) => d.SessionId.ToString();
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, bool error = false)
            {
                if (name is "scene_card" or "scene_properties" or "scene_inspect") arguments["preview"] = typeof(MainWindow).GetField("previewId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!.ToString();
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True((result.IsError == true) == error, text);
                return error ? JsonValue.Create(text)! : JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            // A failed assertion must not leave a modal draft prompt on the private test desktop.
            if (typeof(MainWindow).GetProperty("CurrentInspectionCard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main) is SceneInspectionCard { ObjectApplying: false } cleanupCard) cleanupCard.CancelDraft();
            // A failed assertion may leave unsaved edits: resolve them, so no Unsaved changes dialog blocks the next checks.
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
}
