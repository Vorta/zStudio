using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>
/// Source-world editing through the real named-pipe MCP connection: a pickup move that changes only the coordinate tokens
/// of its text sources (with its difficulty counterpart), project-wide undo, a world object moved and flagged through its
/// glTF node, the Blender round trip, and one save of every changed file.
/// </summary>
internal static class SourceEditingMcpChecks
{
    private const string Default = "# pickups\r\n(\r\n  ( HEMORTAR_AMMO 1 ( 12 8 -5 ) ( 0.0 0.0 0.0 ) 12.5 )   # hand-written\r\n  ( NANITE 50 ( 100.0 0.0 -100.0 ) ( 0.0 1.5707964 0.0 ) 30.0 )\r\n)\r\n";
    private const string Easy = "(\r\n  ( NANITE 80 ( 100.0 0.0 -100.0 ) ( 0.0 1.5707964 0.0 ) 30.0 )\r\n  ( HEMORTAR_AMMO 5 ( 12 8 -5 ) ( 0.0 0.0 0.0 ) 12.5 )\r\n)\r\n";

    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        fixture.Write("data/m1/zrdr/puppies.zrd", Default); fixture.Write("data/m1/zrdr/puppies_easy.zrd", Easy);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var doc = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);
            await Preview();

            // Pickups of a source world are the built archive's; moving one changes the text sources it came from.
            var pickups = await Job("pickups", new() { ["document"] = Id(doc) });
            var ammo = pickups["items"]!.AsArray().First(p => p!["Type"]!.GetValue<string>() == "HEMORTAR_AMMO" && p["source"]!["ResourceName"]!.GetValue<string>() == "PUPPIES.ZRD")!;
            await Call("pickup_lock", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["locked"] = false });
            var moved = Document(await Call("pickup_move", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["source"] = ammo["source"]!.DeepClone(), ["x"] = 20.25, ["y"] = 8, ["z"] = -5 }));
            Assert.True(doc.IsDisposed); Assert.False(moved.PickupsLocked);
            var workspace = moved.SourceWorld!.Workspace;
            Assert.Equal(["data/m1/zrdr/puppies.zrd", "data/m1/zrdr/puppies_easy.zrd"], workspace.DirtyFiles);
            Assert.Equal(Default.Replace("( 12 8 -5 )", "( 20.25 8 -5 )"), Text(workspace.Read("data/m1/zrdr/puppies.zrd")));
            Assert.Equal(Easy.Replace("( 12 8 -5 )", "( 20.25 8 -5 )"), Text(workspace.Read("data/m1/zrdr/puppies_easy.zrd")));
            Assert.StartsWith("# pickups", await File.ReadAllTextAsync(fixture.Path("data/m1/zrdr/puppies.zrd"), token));
            // The pending change is visible as a line diff of the working source against the disk.
            var changes = await Call("source_changes", new() { ["file"] = "data/m1/zrdr/puppies.zrd" });
            Assert.Equal(2, changes["workspace"]!["dirtyFileCount"]!.GetValue<int>());
            var lines = changes["diff"]!["Lines"]!.AsArray().Select(l => l!["Kind"]!.GetValue<string>() + l["Text"]!.GetValue<string>()).ToArray();
            Assert.Contains("-  ( HEMORTAR_AMMO 1 ( 12 8 -5 ) ( 0.0 0.0 0.0 ) 12.5 )   # hand-written", lines);
            Assert.Contains("+  ( HEMORTAR_AMMO 1 ( 20.25 8 -5 ) ( 0.0 0.0 0.0 ) 12.5 )   # hand-written", lines);
            // The rebuilt archive holds the new position for both difficulties.
            var after = await Job("pickups", new() { ["document"] = Id(moved) });
            Assert.All(after["items"]!.AsArray().Where(p => p!["Type"]!.GetValue<string>() == "HEMORTAR_AMMO"), p => Assert.Equal(20.25f, p!["OriginalPosition"]!["X"]!.GetValue<float>()));

            // Undo is project-wide and restores the exact source bytes; redo brings the move back.
            var undone = Document(await Call("undo_redo", new() { ["document"] = Id(moved), ["revision"] = moved.Revision, ["action"] = "undo" }));
            Assert.False(undone.SourceWorld!.Workspace.IsDirty); Assert.Equal(Default, Text(workspace.Read("data/m1/zrdr/puppies.zrd")));
            var redone = Document(await Call("undo_redo", new() { ["document"] = Id(undone), ["revision"] = undone.Revision, ["action"] = "redo" }));
            await Preview();

            // A world object: the database's ground knows its glTF node; moving and flagging it edit that node.
            int ground = redone.PreviewDocument.Scene!.Nodes.First(n => n.Name == "ground").Index;
            var described = await Call("source_world_object", new() { ["document"] = Id(redone), ["node"] = ground });
            Assert.Equal("data/m1/models/m1.gltf", described["origin"]!["modelFile"]!.GetValue<string>());
            Assert.True(described["origin"]!["database"]!.GetValue<bool>());
            var placed = Document((await Job("source_world_object_edit", new() { ["document"] = Id(redone), ["revision"] = redone.Revision, ["node"] = ground, ["position"] = new Dictionary<string, object?> { ["x"] = 10, ["y"] = 0, ["z"] = -10 } }))["document"]!);
            Assert.Contains("data/m1/models/m1.gltf", workspace.DirtyFiles);
            int groundAfter = placed.PreviewDocument.Scene!.Nodes.First(n => n.Name == "ground").Index;
            var position = (await Call("source_world_object", new() { ["document"] = Id(placed), ["node"] = groundAfter }))["object"]!["position"]!;
            Assert.Equal([10f, 0f, -10f], new[] { "x", "y", "z" }.Select(c => position[c]!.GetValue<float>()));
            var flagged = Document((await Job("source_world_object_edit", new() { ["document"] = Id(placed), ["revision"] = placed.Revision, ["node"] = groundAfter, ["flag"] = "0x10000", ["on"] = true }))["document"]!);
            var flags = (await Call("source_world_object", new() { ["document"] = Id(flagged), ["node"] = groundAfter }))["editableFlags"]!.AsArray();
            Assert.True(flags.Single(f => f!["bit"]!.GetValue<string>() == "0x10000")!["on"]!.GetValue<bool>());
            var refused = await Job("source_world_object_edit", new() { ["document"] = Id(flagged), ["revision"] = flagged.Revision, ["node"] = groundAfter }, "failed");
            Assert.Equal("invalid_argument", refused["code"]!.GetValue<string>());

            // Blender: check the database out, "export" a changed copy into the outbox, and update from it.
            var checkout = await Call("source_blender_checkout", new() { ["model"] = "data/m1/models/m1.gltf" });
            string input = checkout["input"]!.GetValue<string>(), outbox = Path.Combine(checkout["outbox"]!.GetValue<string>(), "edit");
            Assert.StartsWith(Path.Combine(fixture.Project, "zstudio", "export"), checkout["folder"]!.GetValue<string>());
            Directory.CreateDirectory(Path.Combine(outbox, "textures"));
            File.Copy(input, Path.Combine(outbox, "m1.gltf")); File.Copy(Path.Combine(Path.GetDirectoryName(input)!, "m1.bin"), Path.Combine(outbox, "m1.bin"));
            byte[] rock = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(input)!, "textures", "rock.png"), token);
            var image = Recoil.Zbd.Core.Export.PngDecoder.Decode(rock); image.Rgba[0] ^= 0xFF;
            await File.WriteAllBytesAsync(Path.Combine(outbox, "textures", "rock.png"), Recoil.Zbd.Core.Export.PngEncoder.Encode(image), token);
            var listed = await Call("source_blender_checkouts", new());
            Assert.Equal("edit/m1.gltf", listed["checkouts"]![0]!["exports"]![0]!["path"]!.GetValue<string>());
            var updated = await Job("source_blender_update", new() { ["document"] = Id(flagged), ["revision"] = flagged.Revision, ["checkout"] = checkout["id"]!.GetValue<string>() });
            Assert.Contains("data/m1/textures/rock.png", updated["files"]!.AsArray().Select(f => f!.GetValue<string>()));
            Assert.Contains(updated["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("rock.png", StringComparison.Ordinal));
            var blended = Document(updated["document"]!);
            Assert.Contains("data/m1/textures/rock.png", workspace.DirtyFiles);

            // One save writes every changed file of the project together, and leaves no journal behind.
            var saved = await Job("save_document", new() { ["document"] = Id(blended), ["revision"] = blended.Revision });
            var written = saved["written"]!.AsArray().Select(w => w!.GetValue<string>()).ToArray();
            // The buffer Blender wrote back unchanged is not a change.
            Assert.Equal(["data/m1/models/m1.gltf", "data/m1/textures/rock.png", "data/m1/zrdr/puppies.zrd", "data/m1/zrdr/puppies_easy.zrd"], written.Order(StringComparer.Ordinal));
            Assert.False(blended.IsDirty);
            Assert.Equal(Default.Replace("( 12 8 -5 )", "( 20.25 8 -5 )"), await File.ReadAllTextAsync(fixture.Path("data/m1/zrdr/puppies.zrd"), Encoding.Latin1, token));
            Assert.Empty(new SourcePublisher(fixture.Project).FindInterrupted(token));
            await Call("close_document", new() { ["document"] = Id(blended), ["revision"] = blended.Revision });

            async Task Preview()
            {
                var work = (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                await work.WaitAsync(token);
            }
            static string Text(byte[]? bytes) => Encoding.Latin1.GetString(bytes!);
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
        finally
        {
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
}
