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
/// A comparison that pairs some copies in order, or runs out of copy checks, says so in the Compare worlds window and
/// through MCP.
/// </summary>
internal static class WorldCompareBudgetChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var token = deadline.Token;
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-compare-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string retail = Path.Combine(folder, "retail.zbd"), rebuilt = Path.Combine(folder, "rebuilt.zbd");
        await File.WriteAllBytesAsync(retail, GameZWriter.Write(World(false), token), token);
        await File.WriteAllBytesAsync(rebuilt, GameZWriter.Write(World(true), token), token);

        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);

            var summary = await Job("world_compare", new() { ["retail"] = retail, ["rebuilt"] = rebuilt });
            Assert.True(summary["approximatePairing"]!.GetValue<bool>());
            Assert.True(summary["wholeWorldLookupsUnchecked"]!.GetValue<int>() >= 1);
            // A read gives the same summary.
            var read = await Call("world_compare_tree", new() { ["context"] = summary["context"]!.GetValue<string>() });
            Assert.Equal(summary["wholeWorldLookupsUnchecked"]!.GetValue<int>(), read["summary"]!["wholeWorldLookupsUnchecked"]!.GetValue<int>());
            Assert.True(read["summary"]!["approximatePairing"]!.GetValue<bool>());
            var window = (WorldCompareWindow)typeof(MainWindow).GetField("compareWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            string text = ((TextBlock)Find(window, c => c is TextBlock t && t.Text.Contains("Merged tree", StringComparison.Ordinal))).Text;
            Assert.Contains("paired in order", text);
            Assert.Contains("not checked for an indistinguishable copy", text);

            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string json = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True(result.IsError != true, json.Length > 2000 ? json[..2000] : json);
                return JsonNode.Parse(json)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments)
            {
                string id = (await Call(name, arguments))["id"]!.GetValue<string>();
                JsonNode job;
                do { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); } while (job["State"]!.GetValue<string>() is "queued" or "running");
                Assert.True(job["State"]!.GetValue<string>() == "completed", job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static WorldNode Node(string name, float x = 0, float trs = 0)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried };
        node.SetPayloadInt(0, 0x20);
        float[] matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1, x, 0, 0];
        for (int i = 0; i < matrix.Length; i++) node.SetPayloadFloat(0x30 + i * 4, matrix[i]);
        node.SetPayloadFloat(0x18, trs);
        return node;
    }
    private static WorldNode Link(WorldNode parent, WorldNode child) { parent.Children.Add(child); child.Parents.Add(parent); return child; }

    /// <summary>
    /// A pile of 1,500 rocks at one place, moved by 2 in the rebuilt world (more candidate pairs than a name pairs by
    /// position); two crates of 1,500 slats that differ only in their class data, listed the other way round in the second
    /// (more checks to tell apart than a comparison makes); and two barrels. The rebuilt world gives the highest slot of the
    /// crates and of the barrels to the other copy.
    /// </summary>
    private static GameZWorld World(bool rebuilt)
    {
        GameZWorld world = new(); WorldNode root = new("world1", WorldNodeClass.World); world.Nodes.Add(root);
        var pile = Link(root, Node("pile")); world.Nodes.Add(pile);
        for (int i = 0; i < 1500; i++) world.Nodes.Add(Link(pile, Node("rock", rebuilt ? 2 : 0)));
        List<WorldNode> crates = [];
        for (int c = 0; c < 2; c++)
        {
            var crate = Link(root, Node("crate")); world.Nodes.Add(crate); crates.Add(crate);
            foreach (int i in c == 0 ? Enumerable.Range(0, 1500) : Enumerable.Range(0, 1500).Reverse()) world.Nodes.Add(Link(crate, Node("slat", trs: i)));
        }
        WorldNode first = Link(root, Node("barrel", 5)), second = Link(root, Node("barrel", 5));
        world.Nodes.Add(first); world.Nodes.Add(second);
        if (rebuilt)
            foreach (var (a, b) in new[] { (crates[0], crates[1]), (first, second) })
            {
                int i = world.Nodes.IndexOf(a), j = world.Nodes.IndexOf(b);
                (world.Nodes[i], world.Nodes[j]) = (world.Nodes[j], world.Nodes[i]);
            }
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
