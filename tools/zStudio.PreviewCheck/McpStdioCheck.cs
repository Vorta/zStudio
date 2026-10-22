using System.IO;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Mcp;

internal static class McpStdioCheck
{
    public static int Run(string executable, string? root = null) => RunAsync(Path.GetFullPath(executable), root).GetAwaiter().GetResult();
    private static async Task<int> RunAsync(string executable, string? root)
    {
        using var savedSettings = new SettingsSnapshot();
        if (LocalMcpHost.Discover(executable).Count > 0) throw new InvalidOperationException("Close this test installation first.");
        // Never overlap the user's zStudio: refuse a copy that is running, and a build that would ignore this
        // check's private profile and use the user's own settings and MCP registrations.
        if (IsRunning(executable)) throw new InvalidOperationException("This zStudio copy is running. Check a separate copy (extract the portable ZIP into an empty folder).");
        if (executable.Replace('/', '\\').Contains(@"\artifacts\zStudio-win-x64\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("artifacts/zStudio-win-x64 is the user's build. Check a separate copy (extract the portable ZIP into an empty folder).");
        if (zStudio.Automation.StudioProfile.IsPrivate && !UsesProfileOverride(executable)) throw new InvalidOperationException("This build predates ZSTUDIO_PROFILE and would use the user's own profile. Check a newer build.");
        string path = zStudio.Desktop.StudioSettings.SettingsPath;
        var settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        if (settings["McpEnabled"]?.GetValue<bool>() != true)
        {
            // Access is the user's decision in their own profile; a private test profile is the check's own.
            if (!zStudio.Automation.StudioProfile.IsPrivate) throw new InvalidOperationException("Enable MCP manually before this portable check. The check never changes the user's access settings.");
            settings["McpEnabled"] = true; Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, settings.ToJsonString());
        }
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
                if (root != null)
                {
                    if (File.Exists(Path.Combine(root, "c1", "gamez.zbd")) && zStudio.Core.Formats.FormatRegistry.Probe(Path.Combine(root, "c1", "gamez.zbd")).Version == 27)
                        await Mw3LiveCheck.Run(client, root);
                    else await McpLiveCheck.Run(client, root);
                }
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

    private static bool IsRunning(string executable)
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            using (process)
                try { if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return true; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return false;
    }

    /// <summary>Whether the build beside <paramref name="executable"/> (build output or portable layout) reads <c>ZSTUDIO_PROFILE</c>.</summary>
    private static bool UsesProfileOverride(string executable)
    {
        string folder = Path.GetDirectoryName(executable)!;
        string? library = new[] { Path.Combine(folder, "zStudio.Application.dll"), Path.Combine(folder, "dependencies", "zStudio.Application.dll") }.FirstOrDefault(File.Exists);
        return library != null && File.ReadAllBytes(library).AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(zStudio.Automation.StudioProfile.OverrideVariable)) >= 0;
    }
}
