using System.IO;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Mcp;

internal static class McpStdioCheck
{
    public static int Run(string executable, string? root = null) => RunAsync(Path.GetFullPath(executable), root).GetAwaiter().GetResult();
    private static async Task<int> RunAsync(string executable, string? root)
    {
        if (LocalMcpHost.Discover(executable).Count > 0) throw new InvalidOperationException("Close this test installation first.");
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        var settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        if (settings["McpEnabled"]?.GetValue<bool>() != true) throw new InvalidOperationException("Enable MCP manually before this portable check. The check never changes access settings.");
        async Task<McpClient> Connect()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await McpClient.CreateAsync(new StdioClientTransport(new() { Command = executable, Arguments = ["--mcp"], Name = "zStudio portable test" }), cancellationToken: timeout.Token);
        }
        try
        {
            await using (var client = await Connect())
            await using (var second = await Connect())
            {
                var tools = await client.ListToolsAsync();
                if (tools.Count < 40) throw new InvalidDataException("Incomplete tool discovery.");
                var resources = await client.ListResourcesAsync();
                if (resources.Count != 2) throw new InvalidDataException("Resource discovery failed.");
                await client.CallToolAsync("zstudio_capabilities");
                await client.ReadResourceAsync("zstudio://capabilities");
                if (LocalMcpHost.Discover(executable).Count != 0) throw new InvalidDataException("Handshake/discovery opened a GUI.");
                var states = await Task.WhenAll(client.CallToolAsync("zstudio_state").AsTask(), second.CallToolAsync("zstudio_state").AsTask());
                if (states.Any(s => s.IsError == true) || LocalMcpHost.Discover(executable).Count != 1) throw new InvalidDataException("Concurrent first calls must start exactly one workspace.");
                Console.WriteLine($"Portable stdio: {tools.Count} tools, {resources.Count} resources; discovery stayed windowless and concurrent first calls opened one GUI.");
                if (root != null) await McpLiveCheck.Run(client, root);
            }
            if (LocalMcpHost.Discover(executable).Count != 1) throw new InvalidDataException("Disconnect must leave the shared GUI open.");
            await using (var client = await Connect())
            await using (var second = await Connect())
            {
                if (LocalMcpHost.Discover(executable).Count != 1) throw new InvalidDataException("Connectors must share one instance.");
                await second.CallToolAsync("zstudio_state");
                var close = await client.CallToolAsync("zstudio_window", new Dictionary<string, object?> { ["action"] = "close" });
                if (close.IsError == true) throw new InvalidDataException("Clean GUI close failed.");
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (LocalMcpHost.Discover(executable).Count > 0) await Task.Delay(100, deadline.Token);
            Console.WriteLine("PASS: lazy stdio handshake/discovery, concurrent first-use launch, reconnect, simultaneous clients and clean shutdown.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
