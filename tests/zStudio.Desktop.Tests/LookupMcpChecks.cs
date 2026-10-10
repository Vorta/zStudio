using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>
/// Lookups by name through the GUI and MCP: Check lists the names several nodes share that a mission looks up, and an edit
/// that makes a lookup find another node is reported in Problems until it is saved.
/// </summary>
internal static class LookupMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        // The database copies a part (a crate with a lid, and a post) twice; the mission's texture effects find the lid.
        fixture.WritePartDatabase();
        fixture.Write("gamegen/m1_zbd.gs", "source support\\tex_fxm1.gw\r\nQuit\r\n");
        fixture.Write("gamegen/support/tex_fxm1.gw", "FindNode lid\r\nQuit\r\n");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);

            // Check lists the lookup, with the node the game finds, in its result and in Problems.
            var check = await Job("source_export", new() { ["outputs"] = new JsonArray("m1/gamez.zbd", "m1/anim.zbd") });
            var lid = check["lookups"]!.AsArray().Single(l => l!["name"]!.GetValue<string>() == "lid")!;
            Assert.Equal(("texture effect", "gamegen/support/tex_fxm1.gw", 2), (lid["kind"]!.GetValue<string>(), lid["source"]!.GetValue<string>(), lid["candidates"]!.GetValue<int>()));
            Assert.EndsWith("/lid", lid["found"]!.GetValue<string>());
            Assert.Equal(0, check["lookupChangeCount"]!.GetValue<int>());
            Assert.Contains(main.ViewModel.Problems, p => p.Severity == "Info" && p.Message.Contains("FindNode lid in gamegen/support/tex_fxm1.gw: 2 nodes have the name; the game finds", StringComparison.Ordinal));
            // A second check replaces the list rather than adding to it.
            await Job("source_export", new() { ["outputs"] = new JsonArray("m1/gamez.zbd") });
            Assert.Single(main.ViewModel.Problems, p => p.Severity == "Info" && p.Message.Contains("FindNode lid", StringComparison.Ordinal));

            // Copying the crate copies its lid, newer than the others: FindNode lid finds the copy's, and Problems says so.
            var opened = (await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!;
            var doc = Document(opened);
            int crate = doc.PreviewDocument.Scene!.Nodes.First(n => n.Name == "crate" && doc.SourceBuild!.Provenance.ContainsKey(n.Index)).Index;
            Assert.DoesNotContain(main.ViewModel.Problems, Changed);
            var copied = Document((await Job("source_world_object_edit", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["node"] = crate, ["action"] = "duplicate", ["name"] = "crate2" }))["document"]!);
            var warning = Assert.Single(main.ViewModel.Problems, Changed);
            Assert.Equal("Warning", warning.Severity);
            Assert.Contains("crate2/lid", warning.Message);
            var listed = (await Call("problems", new() { ["query"] = "FindNode lid" }))["items"]!.AsArray();
            Assert.Contains(listed, p => p!["Message"]!.GetValue<string>().Contains("when the world was opened or last saved", StringComparison.Ordinal));
            // Saved, the copy is what later edits compare with: the warning is settled.
            await Job("save_document", new() { ["document"] = Id(copied), ["revision"] = copied.Revision });
            Assert.DoesNotContain(main.ViewModel.Problems, Changed);
            // Properties takes a parent by name: the copies of one node of the part are one parent to the part's file, while
            // a name nothing has is refused.
            var shown = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            var named = typeof(MainWindow).GetMethod("SourceWorldNodeNamed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.Equal("crate", ((Recoil.Zbd.Core.Worlds.WorldNode)named.Invoke(main, [shown, "crate"])!).Name);
            Assert.IsType<Recoil.Zbd.Automation.StudioCommandException>(Assert.Throws<System.Reflection.TargetInvocationException>(() => named.Invoke(main, [shown, "nothing"])).InnerException);
            // The crate's lids and its copy's are different nodes of one name: the Parent field lists each with its scene node
            // number and takes "#index" (or "name #index"), the index source_world_object_edit's parent takes.
            var parentOf = typeof(MainWindow).GetMethod("SourceParentNode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            int[] lids = [.. shown.PreviewDocument.Scene!.Nodes.Where(n => n.Name == "lid" && shown.SourceBuild!.Provenance.ContainsKey(n.Index)).Select(n => n.Index)];
            int moved = shown.PreviewDocument.Scene!.Nodes.First(n => n.Name == "crate2" && shown.SourceBuild!.Provenance.ContainsKey(n.Index)).Index;
            Assert.True(lids.Length > 2);
            var several = Assert.IsType<Recoil.Zbd.Automation.StudioCommandException>(Assert.Throws<System.Reflection.TargetInvocationException>(() => parentOf.Invoke(main, [shown, "lid", moved])).InnerException);
            Assert.All(lids, l => Assert.Contains($"#{l} under ", several.Message, StringComparison.Ordinal));
            Assert.Equal(lids[^1], (int)parentOf.Invoke(main, [shown, $"#{lids[^1]}", moved])!);
            Assert.Equal(lids[0], (int)parentOf.Invoke(main, [shown, $"lid #{lids[0]}", moved])!);
            Assert.Equal("invalid_argument", Assert.IsType<Recoil.Zbd.Automation.StudioCommandException>(Assert.Throws<System.Reflection.TargetInvocationException>(() => parentOf.Invoke(main, [shown, $"crate #{lids[0]}", moved])).InnerException).Code);

            static bool Changed(StudioProblem p) => p.Message.Contains("FindNode lid in gamegen/support/tex_fxm1.gw finds", StringComparison.Ordinal) && p.Message.Contains("when the world was opened or last saved", StringComparison.Ordinal);
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
        finally { main.Close(); }
    }
}
