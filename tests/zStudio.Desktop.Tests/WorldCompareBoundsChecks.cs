using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core.Worlds;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>
/// Compare worlds at sizes far from retail: pages of rows whose text all escapes stay within the response limit, a pair's
/// many differences are counted beyond those listed, and a tree too large to show says so in the window and through MCP.
/// </summary>
internal static class WorldCompareBoundsChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180)); var token = deadline.Token;
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-compare-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string retail = Path.Combine(folder, "retail.zbd"), rebuilt = Path.Combine(folder, "rebuilt.zbd"), ladder = Path.Combine(folder, "ladder.zbd"), ladderRebuilt = Path.Combine(folder, "ladder-rebuilt.zbd");
        await File.WriteAllBytesAsync(retail, GameZWriter.Write(Wide(false), token), token);
        await File.WriteAllBytesAsync(rebuilt, GameZWriter.Write(Wide(true), token), token);
        await File.WriteAllBytesAsync(ladder, GameZWriter.Write(Ladder(), token), token);
        await File.WriteAllBytesAsync(ladderRebuilt, GameZWriter.Write(Ladder(), token), token);

        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);

            // Reading the comparison changes nothing but the window's presentation: clients see a read-only tool.
            var tools = await client.ListToolsAsync(cancellationToken: token);
            Assert.True(tools.Single(t => t.Name == "zstudio_world_compare_tree").ProtocolTool.Annotations?.ReadOnlyHint);

            var summary = await Job("world_compare", new() { ["retail"] = retail, ["rebuilt"] = rebuilt });
            var window = (WorldCompareWindow)typeof(MainWindow).GetField("compareWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            string context = summary["context"]!.GetValue<string>();
            Assert.False(summary["truncated"]!.GetValue<bool>());
            var world = (await Call("world_compare_tree", new() { ["context"] = context }))["children"]!["items"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "world1")!;
            // The world's hundred differing cells: 64 listed, all counted, in the window as through MCP.
            Assert.Equal(100, world["differenceCount"]!.GetValue<int>());
            var worldRow = window.View!.Roots.Single(r => r.Name == "world1");
            Assert.Contains("100 differences", worldRow.Detail);
            var details = window.View.Details(worldRow);
            Assert.Equal(("More differences", "36 not listed"), (details[^1].Field, details[^1].Retail));
            var selected = await Call("world_compare_tree", new() { ["context"] = context, ["action"] = "select", ["row"] = world["row"]!.GetValue<string>() });
            Assert.Equal(details.Count, selected["selected"]!["propertyCount"]!.GetValue<int>());

            // A full page of the world's 220 members: 210 worlds whose 16 cells all differ, named in characters JSON escapes.
            string text = await Text("world_compare_tree", new() { ["context"] = context, ["row"] = world["row"]!.GetValue<string>(), ["limit"] = 200 });
            Assert.InRange(text.Length, 1, 3 * 1024 * 1024);
            var page = JsonNode.Parse(text)!["children"]!;
            Assert.Equal((220, 200), (page["total"]!.GetValue<int>(), page["nextOffset"]!.GetValue<int>()));
            var items = page["items"]!.AsArray().Select(r => r!).ToList();
            Assert.Equal(200, items.Count);
            Assert.Equal(210, window.View.Roots.Single(r => r.Name == "world1").Children.Count(r => r.Source.DifferenceCount == 16));
            foreach (var item in items.Where(r => r["status"]!.GetValue<string>() == "changed"))
            {
                Assert.Equal(16, item["differenceCount"]!.GetValue<int>());
                var listed = item["differences"]!.AsArray();
                Assert.InRange(listed.Count, 1, 16);
                Assert.InRange(listed.Sum(d => d!["retail"]!.GetValue<string>().Length + d!["rebuilt"]!.GetValue<string>().Length), 1, 1024);
                Assert.StartsWith("world.area", listed[0]!["field"]!.GetValue<string>());
            }

            // Reads need no exclusive turn: one answers while an operation holds the workspace (here, the test holds it).
            var gate = (SemaphoreSlim)typeof(MainWindow).GetField("automationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            await gate.WaitAsync(token);
            try { Assert.Equal(context, (await Call("world_compare_tree", new()).WaitAsync(TimeSpan.FromSeconds(20), token))["context"]!.GetValue<string>()); }
            finally { gate.Release(); }

            // A comparison too large for its tree says so, in the window and through MCP, down to the rows it cut.
            var truncated = await Job("world_compare", new() { ["retail"] = ladder, ["rebuilt"] = ladderRebuilt });
            Assert.True(truncated["truncated"]!.GetValue<bool>()); Assert.False(truncated["pairingTruncated"]!.GetValue<bool>());
            Assert.Contains("too large to show whole", ((TextBlock)Find(window, c => c is TextBlock t && t.Text.Contains("Merged tree", StringComparison.Ordinal))).Text);
            var ladderWorld = (await Call("world_compare_tree", new() { ["context"] = truncated["context"]!.GetValue<string>() }))["children"]!["items"]!.AsArray().Single()!;
            // The yard after the ladder is left out.
            Assert.True(ladderWorld["truncated"]!.GetValue<bool>());
            Assert.Equal(1, ladderWorld["childCount"]!.GetValue<int>());
            Assert.Contains("more children not shown", window.View!.Roots.Single().Detail);
            Assert.Contains("children are not shown", window.View.Roots.Single().ToolTip);

            async Task<string> Text(string name, Dictionary<string, object?> arguments, bool error = false)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True((result.IsError == true) == error, text.Length > 2000 ? text[..2000] : text);
                return text;
            }
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments) => JsonNode.Parse(await Text(name, arguments))!;
            async Task<JsonNode> Wait(string id)
            {
                JsonNode job;
                do { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); } while (job["State"]!.GetValue<string>() is "queued" or "running");
                Assert.True(job["State"]!.GetValue<string>() == "completed", job.ToJsonString()); return job["result"]!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments) => await Wait((await Call(name, arguments))["id"]!.GetValue<string>());
        }
        finally
        {
            main.Close();
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static WorldNode Node(string name)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried };
        node.SetPayloadInt(0, 0x28);
        return node;
    }
    private static WorldNode Link(WorldNode parent, WorldNode child) { parent.Children.Add(child); child.Parents.Add(parent); return child; }

    /// <summary>
    /// A world of ten members named in characters JSON escapes and 210 worlds (any node may be one), its 100 cells and their
    /// 16 each holding members: in the rebuilt world, other members.
    /// </summary>
    private static GameZWorld Wide(bool rebuilt)
    {
        GameZWorld world = new(); WorldNode root = new("world1", WorldNodeClass.World); world.Nodes.Add(root);
        List<WorldNode> members = [.. Enumerable.Range(0, 10).Select(i => Link(root, Node(new string('<', 16) + new string('é', 17) + $"{i:00}")))];
        world.Nodes.AddRange(members);
        root.SetPayloadInt(0x78, 10); root.SetPayloadInt(0x7C, 10);
        for (int i = 0; i < 100; i++) { WorldArea area = new(); area.Nodes.Add(members[rebuilt ? 1 : 0]); root.Areas.Add(area); }
        for (int c = 0; c < 210; c++)
        {
            var inner = Link(root, new WorldNode($"w{c:000}", WorldNodeClass.World)); world.Nodes.Add(inner);
            inner.SetPayloadInt(0x78, 4); inner.SetPayloadInt(0x7C, 4);
            for (int i = 0; i < 16; i++) { WorldArea area = new(); area.Nodes.AddRange(members.Skip(rebuilt ? 1 : 0).Take(8)); inner.Areas.Add(area); }
        }
        return world;
    }

    /// <summary>A world holding thirty levels of two nodes, each under both nodes above (2^31 places in its merged tree), then a yard.</summary>
    private static GameZWorld Ladder()
    {
        GameZWorld world = new(); WorldNode root = new("world1", WorldNodeClass.World); world.Nodes.Add(root);
        List<WorldNode> above = [Link(root, Node("ladder"))]; world.Nodes.Add(above[0]);
        for (int k = 0; k < 30; k++)
        {
            WorldNode a = Node("a"), b = Node("b"); world.Nodes.Add(a); world.Nodes.Add(b);
            foreach (var parent in above) { Link(parent, a); Link(parent, b); }
            above = [a, b];
        }
        world.Nodes.Add(Link(root, Node("yard")));
        return world;
    }

    private static System.Windows.DependencyObject Find(System.Windows.DependencyObject root, Func<System.Windows.DependencyObject, bool> match)
    {
        if (match(root)) return root;
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
            if (Find(child, match) is { } found && match(found)) return found;
        return null!;
    }
}
