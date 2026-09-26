using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Mcp;

/// <summary>Discovery never opens a window. The first workspace request connects once.</summary>
public sealed class LazyMcpConnector(Func<CancellationToken, Task<McpClient>> connect, Func<bool> enabled) : IAsyncDisposable
{
    private readonly SemaphoreSlim connectionGate = new(1);
    private McpClient? client;
    private StudioCommands? commands;

    public async Task RunAsync(Stream input, Stream output, string version, CancellationToken token = default)
    {
        commands = McpCommandCatalog.Create(ExecuteAsync);
        await using var server = StudioMcpServer.Create(input, output, commands, version);
        await server.RunAsync(token);
    }

    private async Task<StudioResult> ExecuteAsync(string name, JsonObject args, CancellationToken token)
    {
        // Static schema inspection is safe even without an opted-in GUI. State and
        // all other commands use the shared desktop, including read-only requests.
        if (name == "zstudio_capabilities") return new(commands!.Describe());
        if (!enabled()) throw new StudioCommandException("access_disabled", "Enable MCP once in zStudio: Tools > MCP integration.");
        await connectionGate.WaitAsync(token);
        try { client ??= await connect(token); }
        finally { connectionGate.Release(); }
        // Never retry a dispatched mutation or silently reattach after the user
        // disconnects clients/closes a workspace. A fresh MCP connection is required.
        if (client.Completion.IsCompleted) throw new StudioCommandException("disconnected", "The workspace disconnected. Reconnect the MCP client to use it again.");
        var result = await client.CallToolAsync(name,
            args.ToDictionary(kv => kv.Key, kv => (object?)JsonSerializer.SerializeToElement(kv.Value)), cancellationToken: token);
        var data = result.StructuredContent is { } structured ? JsonNode.Parse(structured.GetRawText())!
            : JsonNode.Parse(result.Content.OfType<TextContentBlock>().First().Text)!;
        if (result.IsError == true)
            throw new StudioCommandException(data["code"]?.GetValue<string>() ?? "operation_failed", data["message"]?.GetValue<string>() ?? data.ToJsonString());
        return new(data, result.Content.OfType<ImageContentBlock>().SingleOrDefault()?.DecodedData.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (client != null) await client.DisposeAsync();
        connectionGate.Dispose();
    }
}
