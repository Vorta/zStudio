using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Source projects through the real named-pipe MCP connection and the shared GUI operations.</summary>
internal static class SourceProjectMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);

            var failed = await Job("source_pack", new(), "failed"); Assert.Equal("no_project", failed["code"]!.GetValue<string>());
            var inside = await Job("source_reconstruct", new() { ["source"] = fixture.Corpus, ["destination"] = Path.Combine(fixture.Corpus, "project") }, "failed");
            Assert.Equal("invalid_argument", inside["code"]!.GetValue<string>());

            var built = await Job("source_reconstruct", new() { ["source"] = fixture.Corpus, ["destination"] = fixture.Project });
            Assert.True(built["opened"]!.GetValue<bool>()); Assert.Equal(5, built["outputs"]!.GetValue<int>());
            Assert.Equal(1, built["families"]!["scripts"]!.GetValue<int>()); Assert.Equal(0, built["noteCount"]!.GetValue<int>());
            Assert.Equal(Path.GetFullPath(fixture.Project), Path.GetFullPath(main.ViewModel.RootPath!));
            // Build metadata stays out of Files and Search; the reconstructed sources are listed.
            Assert.DoesNotContain(main.ViewModel.Files, f => f.RelativePath.StartsWith(".zstudio", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(main.ViewModel.Files, f => f.RelativePath == Path.Combine("data", "m1", "zrdr", "ai.zrd"));
            var status = await Job("source_status", new() { ["query"] = "zrdr", ["limit"] = 10 });
            Assert.Equal("m1/zrdr.zbd", status["outputs"]!["items"]!.AsArray().Single()!["Path"]!.GetValue<string>());
            Assert.Equal(3, status["families"]!["archive"]!.GetValue<int>());

            // Packing commands appear once a source project is the root.
            typeof(MainWindow).GetMethod("ToolsMenuOpened", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [main, new RoutedEventArgs()]);
            Assert.Equal(Visibility.Visible, ((MenuItem)main.FindName("PackSourceMenu")).Visibility);

            // A reconstructed .zrd opens in the shared ZRD editor and saves text.
            string source = Path.Combine(fixture.Project, "data", "m1", "zrdr", "ai.zrd");
            await Job("open_document", new() { ["path"] = source });
            var doc = main.ViewModel.Documents.Single(d => d.Path.Equals(source, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("zrd-text", doc.Document.SourceSyntax);
            var members = await Call("archive_members", new() { ["document"] = doc.SessionId.ToString() });
            Guid member = Guid.Parse(members["members"]!["items"]![0]!["member"]!.GetValue<string>());
            var gravity = doc.ResourceEdits!.Tree(doc.ResourceEdits.Member(member), token).Children[0].Children[1].Children[0];
            await Job("zrd_edit", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "set", ["member"] = member.ToString(), ["node"] = gravity.Id.ToString(), ["value"] = "-1.5" });
            var unsaved = await Job("source_pack", new(), "failed"); Assert.Equal("unsaved_changes", unsaved["code"]!.GetValue<string>());
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            Assert.Contains("GRAVITY ( -1.5 )", await File.ReadAllTextAsync(source, token));

            var verified = await Job("source_pack", new());
            Assert.False(verified["written"]!.GetValue<bool>()); Assert.Equal(4, verified["Identical"]!.GetValue<int>()); Assert.Equal(1, verified["Changed"]!.GetValue<int>());
            var changed = verified["outputs"]!.AsArray().Single(o => o!["Status"]!.GetValue<string>() == "changed")!;
            Assert.Equal("data/m1/zrdr/ai.zrd", changed["changedSources"]![0]!.GetValue<string>());
            string packed = Path.Combine(fixture.Root, "packed");
            var written = await Job("source_pack", new() { ["destination"] = packed });
            Assert.True(written["written"]!.GetValue<bool>());
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixture.Corpus, "interp.zbd"), token), await File.ReadAllBytesAsync(Path.Combine(packed, "interp.zbd"), token));
            Assert.True(File.Exists(Path.Combine(packed, SourcePacker.MarkerFileName)));
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                Assert.False(result.IsError == true, string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
                return JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
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
