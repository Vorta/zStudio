using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core.Sources;
using zStudio.Core;
using zStudio.Automation;
using zStudio.Desktop;
using zStudio.Mcp;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>
/// A source world whose build sees another program change a project file, through the real named-pipe MCP connection: the
/// world does not open, and an edit whose rebuild sees it is taken back, both with <c>external_change</c>; nothing of the
/// build is shown or kept. A preview choice changes the view only: the player tank's type and weapons (m3).
/// </summary>
internal static class SourcePreviewChangeMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        const string puppies = "( ( NANITE 50 ( 100.0 0.0 -100.0 ) ( 0.0 0.0 0.0 ) 30.0 ) )";
        const string easy = "data/m1/zrdr/puppies_easy.zrd";
        fixture.Write("data/m1/zrdr/puppies.zrd", puppies);
        fixture.WritePlayerTank();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            string model = fixture.Path("data/m1/models/m1.gltf");
            // Another program saves the mission's model once every output is built, after the world read it.
            int touches = 0;
            main.SourceBuildStep = p => { if (p.Item == "Built" && touches > 0) { touches--; File.SetLastWriteTimeUtc(model, File.GetLastWriteTimeUtc(model).AddMinutes(1)); } };

            touches = 1;
            var refused = await Job("source_world_open", new() { ["mission"] = "m1" }, "failed");
            Assert.Equal(0, touches);
            Assert.Equal("external_change", refused["code"]!.GetValue<string>());
            Assert.StartsWith("data/m1/models/m1.gltf changed during evaluation; try again.", refused["message"]!.GetValue<string>());
            Assert.DoesNotContain(main.ViewModel.Documents, d => d.SourceWorld != null);
            Assert.StartsWith("The m1 world did not open: data/m1/models/m1.gltf changed during evaluation", main.ViewModel.Status);
            Assert.Empty(Directory.EnumerateFiles(fixture.Project, "*.zbd", SearchOption.AllDirectories));

            // Opened again, it builds; the world shows the model as it is now.
            var doc = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);
            await Preview();
            Assert.False(doc.SourceInputsChanged());
            var workspace = doc.SourceWorld!.Workspace;

            // Adding a difficulty source replaces the shown fallback, without changing any input the build read.
            // The actual Properties placement callback and MCP edits must refuse that old binding before accepting edits.
            var placements = await doc.GetPickupEditsAsync(main.ViewModel.Resolver!, token);
            var source = placements.Records.First(r => r.Type == "NANITE").Source;
            var move = (Func<MissionPickupSource, System.Numerics.Vector3, Task>)typeof(MainWindow)
                .GetMethod("SourcePickupMove", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [doc])!;
            fixture.Write(easy, puppies);
            Assert.True(doc.SourceInputsChanged());
            Assert.Equal("stale_document", (await Assert.ThrowsAsync<StudioCommandException>(() => move(source, new(125, 0, -100)))).Code);
            var stale = await Job("source_world_add_model", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["model"] = fixture.Tank, ["name"] = "tank", ["definitionFiles"] = Array.Empty<string>() }, "failed");
            Assert.Equal("stale_document", stale["code"]!.GetValue<string>());
            Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo);
            Assert.Equal(puppies, await File.ReadAllTextAsync(fixture.Path("data/m1/zrdr/puppies.zrd"), token));
            File.Delete(fixture.Path(easy)); Assert.False(doc.SourceInputsChanged());
            // A file appearing after the initial check is caught by the worker's post-preparation check too.
            main.SourceEditPreparing = _ => fixture.Write(easy, puppies);
            try
            {
                stale = await Job("source_world_add_model", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["model"] = fixture.Tank, ["name"] = "tank", ["definitionFiles"] = Array.Empty<string>() }, "failed");
                Assert.Equal("stale_document", stale["code"]!.GetValue<string>());
                Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo);
            }
            finally { main.SourceEditPreparing = null; File.Delete(fixture.Path(easy)); }

            // An edit whose candidate sees an external change is never accepted; history and the scene stay unchanged.
            touches = 1;
            var failed = await Job("source_world_add_model", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["model"] = fixture.Tank, ["name"] = "tank", ["definitionFiles"] = Array.Empty<string>() }, "failed");
            Assert.Equal(0, touches);
            Assert.Equal("external_change", failed["code"]!.GetValue<string>());
            Assert.StartsWith("data/m1/models/m1.gltf changed during evaluation", failed["message"]!.GetValue<string>());
            Assert.StartsWith("Adding tank was not applied: ", main.ViewModel.Status);
            Assert.False(doc.IsDisposed);
            Assert.Same(doc, main.ViewModel.Documents.Single(d => d.SourceWorld != null));
            Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo); Assert.False(workspace.CanRedo);
            // The world shown was built from the model before this change, which it now reports.
            Assert.True(doc.SourceInputsChanged());
            Assert.Empty(Directory.EnumerateFiles(fixture.Project, "*.zbd", SearchOption.AllDirectories));

            // The player tank's type and weapons change how the view draws it, never the sources: one primary pair and one
            // secondary on its gun, the type a transition of the map's data reaches, refusals with the card's reasons.
            var tankMap = Document((await Job("source_world_open", new() { ["mission"] = "m3" }))["document"]!);
            await Preview();
            var tankWorkspace = tankMap.SourceWorld!.Workspace; long tankRevision = tankMap.Revision;
            string preview = (await Call("scene_tree", new() { ["document"] = Id(tankMap) }))["preview"]!.GetValue<string>();
            var bft = (await Call("preview_state", new() { ["preview"] = preview }))["bft"]!;
            Assert.Equal(["tracked", "RFPG", "HEMORTAR"], new[] { "type", "primary", "secondary" }.Select(k => bft[k]!.GetValue<string>()));
            Assert.Equal(["RFPG_L", "RFPG_R", "HEM"], bft["gun"]!["drawn"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Equal([true, false, true, false], bft["types"]!.AsArray().Select(t => t!["available"]!.GetValue<bool>()));
            Assert.Equal("m3's tank has no transition to amphibious (none_to_amphib).", bft["types"]![1]!["reason"]!.GetValue<string>());
            var tankRow = (await Call("scene_tree", new() { ["document"] = Id(tankMap), ["search"] = "bft_00" }))["matches"]!["items"]!.AsArray()
                .Single(m => m!["item"]!["playerTank"]?.GetValue<bool>() == true)!["item"]!;
            Assert.Equal(bft["node"]!.GetValue<int>(), tankRow["node"]!.GetValue<int>());
            await Job("scene_options", new() { ["preview"] = preview, ["changes"] = new Dictionary<string, object?> { ["bftType"] = "hover", ["bftSecondary"] = "NAPALM" } });
            preview = (await Call("scene_tree", new() { ["document"] = Id(tankMap) }))["preview"]!.GetValue<string>();
            bft = (await Call("preview_state", new() { ["preview"] = preview }))["bft"]!;
            Assert.Equal(["hover", "RFPG", "NAPALM"], new[] { "type", "primary", "secondary" }.Select(k => bft["shown"]![k]!.GetValue<string>()));
            Assert.Equal(["RFPG_L", "RFPG_R", "NAPALM"], bft["gun"]!["drawn"]!.AsArray().Select(n => n!.GetValue<string>()));
            var shownTank = ((zStudio.Rendering.SceneViewport)main.SceneHost.Content!).Mission!;
            bool Active(string name) => (shownTank.Scene.Nodes.Single(n => n.Name == name).Metadata.UInt("flags") & 4) != 0;
            Assert.True(Active("wings_hover")); Assert.False(Active("tracks")); Assert.False(Active("HEM")); Assert.False(Active("ERFPG_L"));
            Assert.False(tankWorkspace.IsDirty); Assert.False(tankWorkspace.CanUndo); Assert.Equal(tankRevision, tankMap.Revision);
            // A type without transitions in this map and a fire weapon in sub are refused; the shown state stays.
            var refusedType = await Job("scene_options", new() { ["preview"] = preview, ["changes"] = new Dictionary<string, object?> { ["bftType"] = "amphibious" } }, "failed");
            Assert.Equal("unsupported", refusedType["code"]!.GetValue<string>());
            Assert.Contains("none_to_amphib", refusedType["message"]!.GetValue<string>());
            Assert.Equal("hover", (await Call("preview_state", new() { ["preview"] = preview }))["bft"]!["type"]!.GetValue<string>());
            // Opening the map again starts at the defaults.
            await Job("source_world_open", new() { ["mission"] = "m1" });
            tankMap = Document((await Job("source_world_open", new() { ["mission"] = "m3" }))["document"]!);
            await Preview();
            preview = (await Call("scene_tree", new() { ["document"] = Id(tankMap) }))["preview"]!.GetValue<string>();
            Assert.Equal("tracked", (await Call("preview_state", new() { ["preview"] = preview }))["bft"]!["type"]!.GetValue<string>());

            async Task Preview()
            {
                var work = (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                await work.WaitAsync(token);
            }
            DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == state["id"]!.GetValue<string>());
            static string Id(DocumentModel d) => d.SessionId.ToString();
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True(result.IsError != true, name + ": " + text);
                return JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, name + ": " + job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            main.SourceBuildStep = null;
            main.SourceEditPreparing = null;
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
}
