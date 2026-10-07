using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class SourceDefinitionPagingChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        var files = Enumerable.Range(0, 65).Select(i => $"data/common/zrdr/late/choice{i:D3}.zad").ToArray();
        string definition = File.ReadAllText(fixture.Path(SourceWorldFixture.TankDefinitions));
        foreach (var file in files) fixture.Write(file, definition);
        fixture.Write("data/m2/zrdr/anim.zad", SourceWorlds.AddDefinitionFiles("( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ) ) )"u8, files));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var opened = (await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!;
            string id = opened["id"]!.GetValue<string>();
            var first = await Call("source_world_definitions", new() { ["document"] = id, ["name"] = "tank" });
            Assert.Equal(65, first["fileCount"]!.GetValue<int>()); Assert.Equal(64, first["files"]!.AsArray().Count);
            var last = await Call("source_world_definitions", new() { ["document"] = id, ["name"] = "tank", ["offset"] = first["nextOffset"]!.GetValue<int>() });
            Assert.Null(last["nextOffset"]); Assert.Equal(files[^1], last["files"]!.AsArray().Single()!["path"]!.GetValue<string>());
            var filtered = await Call("source_world_definitions", new() { ["document"] = id, ["name"] = "tank", ["query"] = "CHOICE064", ["limit"] = 1 });
            Assert.Equal(1, filtered["total"]!.GetValue<int>()); Assert.Equal(65, filtered["fileCount"]!.GetValue<int>());
            Assert.Equal(files[^1], filtered["files"]![0]!["path"]!.GetValue<string>());
            var added = (await Job("source_world_add_model", new() { ["document"] = id, ["revision"] = opened["Revision"]!.GetValue<long>(),
                ["model"] = fixture.Tank, ["name"] = "tank", ["definitionFiles"] = new[] { last["files"]![0]!["path"]!.GetValue<string>() } }))["document"]!;
            var current = main.ViewModel.Documents.Single(d => d.SessionId.ToString() == added["id"]!.GetValue<string>());
            string list = Encoding.Latin1.GetString(current.SourceWorld!.Workspace.Read("data/m1/zrdr/anim.zad")!);
            Assert.Contains("choice064.zad", list); Assert.DoesNotContain("choice000.zad", list);
            Assert.Contains(current.PreviewDocument.Scene!.Nodes, n => n.Name == "tank");

            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = result.Content.OfType<TextContentBlock>().Single().Text;
                Assert.False(result.IsError == true, text); return JsonNode.Parse(text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments)
            {
                var job = await Call(name, arguments); string operation = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running")
                { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = operation }); }
                Assert.Equal("completed", job["State"]!.GetValue<string>()); return job["result"]!;
            }
        }
        finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); }
    }
}

public sealed class SourceDefinitionPagingTests
{
    [Fact]
    public void EscapedPathsKeepTheirIdentityAcrossBoundedPagesAndFilters()
    {
        SourceDefinitionFile[] files = [.. Enumerable.Range(0, 65).Select(i => new SourceDefinitionFile(
            "data/" + new string('\u0100', 30_000) + $"/{i:D3}.zad", [.. Enumerable.Repeat(new string('\u0100', 128), 32)], ["m2"]))];
        List<string> seen = []; int? offset = 0;
        do
        {
            var page = MainWindow.SourceDefinitionPage("tank", files, new() { ["offset"] = offset.Value }).Data;
            Assert.True(page.ToJsonString().Length < 1024 * 1024);
            seen.AddRange(page["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
            offset = page["nextOffset"]?.GetValue<int>();
        } while (offset != null);
        Assert.Equal(files.Select(f => f.Path), seen);
        var filtered = MainWindow.SourceDefinitionPage("tank", files, new() { ["query"] = "064.ZAD" }).Data;
        Assert.Equal(files[^1].Path, filtered["files"]!.AsArray().Single()!["path"]!.GetValue<string>());
        Assert.Equal(1, filtered["total"]!.GetValue<int>());
    }

    [Fact]
    public void DiscoverySchemaPagesAllFilesWhileAdditionKeepsItsSelectionLimit()
    {
        var commands = McpCommandCatalog.Create((_, _, _) => throw new InvalidOperationException("Schema test must not execute."));
        var list = commands.All.Single(c => c.Name == "zstudio_source_world_definitions");
        JsonObject args = new() { ["document"] = Guid.NewGuid().ToString(), ["name"] = "tank", ["offset"] = 64, ["limit"] = 64, ["query"] = "064" };
        list.Validate(args);
        args["limit"] = 65;
        Assert.Equal("invalid_argument", Assert.Throws<StudioCommandException>(() => list.Validate(args)).Code);
        args["limit"] = 1; args["offset"] = -1;
        Assert.Equal("invalid_argument", Assert.Throws<StudioCommandException>(() => list.Validate(args)).Code);
        Assert.Equal(64, commands.All.Single(c => c.Name == "zstudio_source_world_add_model").InputSchema["properties"]!["definitionFiles"]!["maxItems"]!.GetValue<int>());
    }
}
