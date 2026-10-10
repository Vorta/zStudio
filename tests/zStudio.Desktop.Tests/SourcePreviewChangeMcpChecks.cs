using System.IO;
using System.IO.Pipes;
using System.Reflection;
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
/// A source world whose build sees another program change a project file, through the real named-pipe MCP connection: the
/// world does not open, and an edit whose rebuild sees it is taken back, both with <c>external_change</c>; nothing of the
/// build is shown or kept.
/// </summary>
internal static class SourcePreviewChangeMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
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
            Assert.StartsWith("data/m1/models/m1.gltf changed on disk while the m1 world was building; the build was not shown.", refused["message"]!.GetValue<string>());
            Assert.DoesNotContain(main.ViewModel.Documents, d => d.SourceWorld != null);
            Assert.StartsWith("The m1 world did not open: data/m1/models/m1.gltf changed on disk", main.ViewModel.Status);
            Assert.Empty(Directory.EnumerateFiles(SourceWorlds.PreviewRoot(fixture.Project), "*.zbd", SearchOption.AllDirectories));

            // Opened again, it builds; the world shows the model as it is now.
            var doc = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);
            await Preview();
            Assert.False(doc.SourceInputsChanged());
            var workspace = doc.SourceWorld!.Workspace;

            // An edit whose rebuild sees the model change is taken back: the shown world and the workspace stay as they were.
            touches = 1;
            var failed = await Job("source_world_add_model", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["model"] = fixture.Tank, ["name"] = "tank", ["definitionFiles"] = Array.Empty<string>() }, "failed");
            Assert.Equal(0, touches);
            Assert.Equal("external_change", failed["code"]!.GetValue<string>());
            Assert.StartsWith("Adding tank was reverted: data/m1/models/m1.gltf changed on disk while the m1 world was building", failed["message"]!.GetValue<string>());
            Assert.StartsWith("Adding tank was reverted: ", main.ViewModel.Status);
            Assert.False(doc.IsDisposed);
            Assert.Same(doc, main.ViewModel.Documents.Single(d => d.SourceWorld != null));
            Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo); Assert.False(workspace.CanRedo);
            // The world shown was built from the model before this change, which it now reports.
            Assert.True(doc.SourceInputsChanged());
            Assert.Single(Directory.EnumerateFiles(SourceWorlds.PreviewRoot(fixture.Project), "gamez.zbd", SearchOption.AllDirectories));

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
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
}
