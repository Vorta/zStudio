using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>
/// Nodes of a part the mission database copies twice, through MCP and Properties: a copy of a part's node is returned (and
/// followed) in the copy of the part it was made beside, a parent named in Properties is taken from the moved node's copy,
/// and only the flags a node's source can set are offered.
/// </summary>
internal static class SourceObjectPlanMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        fixture.WritePartDatabase();
        // The two spellings still name one physical source; retain its full long identity.
        string part = new string('p', 180) + ".gltf";
        fixture.Write("data/m1/models/" + part, File.ReadAllBytes(fixture.Path("data/m1/models/m1_01.gltf")));
        var database = JsonNode.Parse(File.ReadAllBytes(fixture.Path("data/m1/models/m1.gltf")))!;
        var references = database["nodes"]!.AsArray().Select(n => n?["extras"]?["recoil"] as JsonObject)
            .Where(e => e?["ref"]?.GetValue<string>() == "m1_01.gltf").ToArray();
        Assert.Equal(2, references.Length);
        references[0]!["ref"] = part; references[1]!["ref"] = part.ToUpperInvariant();
        fixture.Write("data/m1/models/m1.gltf", database.ToJsonString());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var doc = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);
            List<string> keys = [.. Nodes(doc, "post").Select(p => Key(doc, p)).Distinct()];
            Assert.Equal(2, keys.Count);

            // Copying either copy's post copies it in the part, so in both copies: the result's copy is the one beside it.
            foreach (string key in keys)
            {
                int post = Nodes(doc, "post").Single(p => Key(doc, p) == key);
                string name = "post_" + keys.IndexOf(key);
                var copied = await Job("source_world_object_edit", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["node"] = post, ["action"] = "duplicate", ["name"] = name });
                doc = Document(copied["document"]!);
                Assert.Equal(2, copied["copies"]!.AsArray().Count);
                int copy = copied["copy"]!.GetValue<int>();
                Assert.Equal(name, doc.PreviewDocument.Scene!.Nodes.Single(n => n.Index == copy).Name);
                Assert.Equal(key, Key(doc, copy));
            }

            // Properties names a parent: of the copies of crate, the one in the moved post's copy of the part.
            var named = typeof(MainWindow).GetMethod("SourceWorldNodeNamedNear", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            // (The world Properties reads, whose nodes the parent is one of.)
            var model = typeof(MainWindow).GetMethod("SourceWorldModel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, [doc])!;
            var slots = GameZWriter.NodeSlots((GameZWorld)model.GetType().GetProperty("World")!.GetValue(model)!);
            foreach (string key in keys)
            {
                int post = Nodes(doc, "post").Single(p => Key(doc, p) == key);
                var crate = (WorldNode)named.Invoke(main, [doc, "crate", (int?)post])!;
                Assert.Equal("crate", crate.Name);
                Assert.Equal(key, Key(doc, slots[crate]));
            }

            // Exercise the actual warm parent callback, including same-copy preference. This small
            // fixture must not format each occurrence's full source/lineage path on every lookup.
            int followed = Nodes(doc, "post").Single(p => Key(doc, p) == keys[1]);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 128; i++) _ = named.Invoke(main, [doc, "crate", (int?)followed]);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 128 * 1024, "Parent lookup copied source identities repeatedly.");

            // Properties' actual generated field callback rebuilds the source world. Its selection
            // must follow the same occurrence using the new snapshot's identity index.
            var show = typeof(MainWindow).GetMethod("ShowSourceObjectPropertiesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.True(await (Task<bool>)show.Invoke(main, [doc, followed, token])!);
            var fields = Assert.IsType<SourceObjectPropertiesEditor>(main.OpenPropertiesWindow!.SourceFields);
            var described = System.Text.Json.JsonSerializer.SerializeToNode(fields.DescribeAutomationFields())!;
            string position = described["fields"]!.AsArray().Single(f => f!["Label"]!.GetValue<string>() == "Position")!["Id"]!.GetValue<string>();
            await fields.WriteAutomationFieldAsync(position, "0, 0, 4");
            doc = main.OpenPropertiesWindow!.Document!;
            var changed = Assert.IsType<SourceObjectPropertiesEditor>(main.OpenPropertiesWindow.SourceFields);
            Assert.Equal("post", changed.State.Name);
            Assert.Equal(keys[1], Key(doc, changed.State.Node));
            Assert.NotNull(changed.State.Transform);
            Assert.Equal(4f, changed.State.Transform.Value.Position.Z);

            // Only objects have flags to edit, as Properties shows them (not the world, which a script made); a node of a glTF
            // file carries every flag.
            int world = doc.PreviewDocument.Scene!.Nodes.First(n => n.Name == "world" && doc.SourceBuild!.Provenance.ContainsKey(n.Index)).Index;
            static IEnumerable<string> Bits(JsonNode state) => state["editableFlags"]!.AsArray().Select(f => f!["bit"]!.GetValue<string>());
            Assert.Empty(Bits(await Call("source_world_object", new() { ["document"] = Id(doc), ["node"] = world })));
            Assert.Contains("0x20000", Bits(await Call("source_world_object", new() { ["document"] = Id(doc), ["node"] = Nodes(doc, "crate").First() })));
            // Saved, so closing asks nothing.
            await Job("save_document", new() { ["document"] = Id(doc), ["revision"] = doc.Revision });

            List<int> Nodes(DocumentModel d, string name) => [.. d.PreviewDocument.Scene!.Nodes.Where(n => n.Name == name && d.SourceBuild!.Provenance.ContainsKey(n.Index)).Select(n => n.Index)];
            static string Key(DocumentModel d, int node) => SourceObjectEdits.CopyKey(d.SourceBuild!.Provenance[node]);
            DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == state["id"]!.GetValue<string>());
            static string Id(DocumentModel d) => d.SessionId.ToString();
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, bool error = false)
            {
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
            // A failed assertion may leave unsaved edits: resolve them, so no Unsaved changes dialog blocks the next checks.
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
}
