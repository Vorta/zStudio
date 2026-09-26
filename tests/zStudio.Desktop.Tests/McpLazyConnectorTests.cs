using System.IO.Pipes;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Mcp;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class McpLazyConnectorTests
{
    [Fact]
    public async Task DiscoveryAndInvalidRequestsNeverOpenTheWorkspace()
    {
        int connections = 0;
        await WithConnector(_ => { connections++; throw new Exception("Must not connect"); }, () => false, async client =>
        {
            Assert.Equal(52, (await client.ListToolsAsync()).Count);
            Assert.Equal(2, (await client.ListResourcesAsync()).Count);
            Assert.False((await client.CallToolAsync("zstudio_capabilities")).IsError);
            Assert.Single((await client.ReadResourceAsync("zstudio://capabilities")).Contents);
            Assert.True((await client.CallToolAsync("zstudio_state")).IsError);
            Assert.True((await client.CallToolAsync("zstudio_not_a_tool")).IsError);
            Assert.True((await client.CallToolAsync("zstudio_open_root", new Dictionary<string, object?> { ["path"] = 123 })).IsError);
            Assert.Equal(0, connections);
        });
    }

    [Fact]
    public async Task InvalidArrayItemsAreRejectedBeforeConnectingToAnEnabledWorkspace()
    {
        int connections = 0;
        await WithConnector(_ => { connections++; throw new Exception("Must not connect"); }, () => true, async client =>
        {
            foreach (string vector in new[] { "[0,\"bad\",0]", "[0,null,0]", "[0,0]", "[0,0,0,0]", "[0,{},0]" })
            {
                var result = await client.CallToolAsync("zstudio_camera", new Dictionary<string, object?>
                {
                    ["preview"] = Guid.NewGuid().ToString(), ["action"] = "set",
                    ["position"] = JsonNode.Parse(vector), ["look"] = new[] { 0, 0, -1 }
                });
                Assert.True(result.IsError);
                Assert.Equal("invalid_argument", result.StructuredContent!.Value.GetProperty("code").GetString());
            }
            foreach (string assets in new[] { "[1]", "[null]", "[{}]", "[{\"kind\":\"Model\",\"index\":\"0\"}]", "[{\"kind\":\"Unknown\",\"index\":0}]", "[{\"kind\":\"Model\",\"index\":0,\"extra\":true}]" })
            {
                var result = await client.CallToolAsync("zstudio_export", new Dictionary<string, object?>
                {
                    ["document"] = Guid.NewGuid().ToString(), ["destination"] = "unused", ["assets"] = JsonNode.Parse(assets)
                });
                Assert.True(result.IsError);
                Assert.Equal("invalid_argument", result.StructuredContent!.Value.GetProperty("code").GetString());
            }
            Assert.Equal(0, connections);
        });
    }

    [Fact]
    public async Task InvalidPickupIdentitiesMapsAndOverflowAreRejectedBeforeConnecting()
    {
        int connections = 0;
        await WithConnector(_ => { connections++; throw new Exception("Must not connect"); }, () => true, async client =>
        {
            var tools = await client.ListToolsAsync();
            var sourceSchema = tools.Single(t => t.Name == "zstudio_pickup_move").ProtocolTool.InputSchema
                .GetProperty("properties").GetProperty("source");
            Assert.False(sourceSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(4, sourceSchema.GetProperty("required").GetArrayLength());
            JsonObject validSource = new() { ["ArchivePath"] = @"C:\working\zrdr.zbd", ["AssetIndex"] = 0, ["ResourceName"] = "PUPPIES", ["RecordIndex"] = 0 };
            List<JsonObject> malformed = [new()];
            foreach (string field in new[] { "ArchivePath", "AssetIndex", "ResourceName", "RecordIndex" })
            {
                var missing = validSource.DeepClone().AsObject(); missing.Remove(field); malformed.Add(missing);
                var nullValue = validSource.DeepClone().AsObject(); nullValue[field] = null; malformed.Add(nullValue);
            }
            foreach (var (field, value) in new (string, JsonNode)[]
            {
                ("ArchivePath", JsonValue.Create(0)!), ("ResourceName", new JsonObject()),
                ("AssetIndex", JsonValue.Create("0")!), ("RecordIndex", JsonValue.Create(0.5)!),
                ("AssetIndex", JsonValue.Create((long)int.MaxValue + 1)!), ("RecordIndex", JsonValue.Create(-1)!),
                ("extra", JsonValue.Create(true)!), ("archivePath", JsonValue.Create("wrong-case")!)
            })
            { var source = validSource.DeepClone().AsObject(); source[field] = value; malformed.Add(source); }
            foreach (var source in malformed)
            {
                var result = await client.CallToolAsync("zstudio_pickup_move", new Dictionary<string, object?>
                {
                    ["document"] = Guid.NewGuid().ToString(), ["revision"] = 0, ["source"] = source,
                    ["x"] = 0, ["y"] = 0, ["z"] = 0
                });
                Assert.True(result.IsError);
                Assert.Equal("invalid_argument", result.StructuredContent!.Value.GetProperty("code").GetString());
            }
            foreach (JsonNode? value in new JsonNode?[] { null, JsonValue.Create(1), JsonValue.Create(false), new JsonObject(), new JsonArray() })
            {
                var result = await client.CallToolAsync("zstudio_save_document", new Dictionary<string, object?>
                {
                    ["document"] = Guid.NewGuid().ToString(), ["revision"] = 0,
                    ["destinations"] = new JsonObject { ["source.zbd"] = value }
                });
                Assert.True(result.IsError);
                Assert.Equal("invalid_argument", result.StructuredContent!.Value.GetProperty("code").GetString());
            }
            var overflow = await client.CallToolAsync("zstudio_files", new Dictionary<string, object?> { ["offset"] = (long)int.MaxValue + 1 });
            Assert.True(overflow.IsError);
            Assert.Equal("invalid_argument", overflow.StructuredContent!.Value.GetProperty("code").GetString());
            Assert.Equal(0, connections);
        });
    }

    [Fact]
    public async Task ConcurrentFirstCallsConnectOnceAndPreserveResultsAndErrors()
    {
        int connections = 0;
        bool enabled = true;
        StudioCommands commands = new();
        byte[] png = [1, 2, 3, 4];
        commands.Add(new("zstudio_state", "state", false, [], (_, _) => Task.FromResult(new StudioResult(new JsonObject { ["fixture"] = true }, png))));
        commands.Add(new("zstudio_files", "files", false, [], (_, _) => throw new StudioCommandException("fixture_error", "Shared handler error")));
        await using var host = new LocalMcpHost(commands, "test");
        await WithConnector(async token =>
        {
            Interlocked.Increment(ref connections);
            var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            return await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
        }, () => enabled, async client =>
        {
            await client.ListToolsAsync();
            Assert.Equal(0, connections);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.CallToolAsync("zstudio_state").AsTask()));
            Assert.Equal(1, connections);
            foreach (var result in results)
            {
                Assert.False(result.IsError);
                Assert.True(result.StructuredContent!.Value.GetProperty("fixture").GetBoolean());
                Assert.Equal(png, result.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray());
            }
            var error = await client.CallToolAsync("zstudio_files");
            Assert.True(error.IsError);
            Assert.Equal("fixture_error", error.StructuredContent!.Value.GetProperty("code").GetString());
            enabled = false;
            Assert.True((await client.CallToolAsync("zstudio_state")).IsError);
            Assert.Equal(1, connections);
        });
    }

    private static async Task WithConnector(Func<CancellationToken, Task<McpClient>> connect, Func<bool> enabled, Func<McpClient, Task> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string name = "zstudio-lazy-test-" + Guid.NewGuid().ToString("N");
        await using var serverPipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var clientPipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepting = serverPipe.WaitForConnectionAsync(deadline.Token);
        await clientPipe.ConnectAsync(deadline.Token);
        await accepting;
        await using var connector = new LazyMcpConnector(connect, enabled);
        var running = connector.RunAsync(serverPipe, serverPipe, "test", deadline.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(clientPipe, clientPipe), cancellationToken: deadline.Token);
            await check(client);
        }
        finally
        {
            await deadline.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
        }
    }
}
