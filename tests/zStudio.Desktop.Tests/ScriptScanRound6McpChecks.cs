using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>
/// The Blender checkout listing through the real named-pipe MCP connection and the GUI's Update from Blender export: both
/// list the checkouts and their outboxes with one bounded, cancellable scan off the UI thread, and a zstudio/export folder
/// beyond the scan's budget is refused (io_failed, and the reason in the status) rather than listed in part.
/// </summary>
internal static class ScriptScanRound6McpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var doc = Document(await Job("source_world_open", new() { ["mission"] = "m1" }));
            Assert.Same(doc, main.ViewModel.SelectedDocument);

            // A manifest listing more files than the scan visits in all: the listing is refused, not taken without it.
            string crowded = Checkout("crowded", 0, new JsonArray([.. Enumerable.Range(0, Recoil.Zbd.Core.Sources.SourceProject.MaximumScannedEntries + 1).Select(_ => (JsonNode?)0)]));
            string refused = (await Call("source_blender_checkouts", new(), error: true)).GetValue<string>();
            Assert.Contains("io_failed", refused, StringComparison.Ordinal);
            Assert.Contains("zstudio/export holds more than 250,000 files and folders", refused, StringComparison.Ordinal);
            // The GUI's Update from Blender export lists the same way, and says why instead of finding no export. (Reporting it
            // shows the Problems tab, a saved preference the later checks' windows start with: it is put back.)
            var layout = main.ViewModel.Settings.GetWorkspace(); var (toolsVisible, toolTab) = (layout.ToolsVisible, layout.ToolTab);
            main.ViewModel.Status = "";
            typeof(MainWindow).GetMethod("UpdateFromBlenderClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [main, new RoutedEventArgs()]);
            for (int wait = 0; wait < 500 && !main.ViewModel.Status.Contains("zstudio/export holds more than", StringComparison.Ordinal); wait++) await Task.Delay(10, token);
            Assert.Contains("zstudio/export holds more than 250,000 files and folders", main.ViewModel.Status, StringComparison.Ordinal);
            ((TabControl)main.FindName("ToolTabs")).SelectedIndex = toolTab; layout.ToolsVisible = toolsVisible; layout.ToolTab = toolTab;
            Directory.Delete(crowded, true);

            // Checkouts and their exports (newest first, nested folders included), each outbox scanned once.
            Checkout("older", 2, created: "2026-01-01T00:00:00.0000000Z");
            Checkout("newer", 20, created: "2026-02-01T00:00:00.0000000Z");
            var listed = await Call("source_blender_checkouts", new());
            Assert.Equal(2, listed["checkoutCount"]!.GetValue<int>());
            var newer = listed["checkouts"]![0]!;
            Assert.Equal(("newer", 20, 16), (newer["id"]!.GetValue<string>(), newer["exportCount"]!.GetValue<int>(), newer["exports"]!.AsArray().Count));
            Assert.Equal(["e0.gltf", "nested/e1.gltf"], listed["checkouts"]![1]!["exports"]!.AsArray().Select(e => e!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal));

            string Checkout(string id, int exports, JsonArray? files = null, string created = "2026-01-01T00:00:00.0000000Z")
            {
                string folder = Path.Combine(fixture.Project, "zstudio", "export", id);
                Directory.CreateDirectory(Path.Combine(folder, "outbox", "nested"));
                JsonObject manifest = new() { ["format"] = "zstudio-blender-checkout", ["version"] = 1, ["id"] = id, ["model"] = "data/m1/models/m1.gltf", ["created"] = created, ["files"] = files ?? [] };
                File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest.ToJsonString());
                for (int i = 0; i < exports; i++) File.WriteAllText(Path.Combine(folder, "outbox", i % 2 == 0 ? "" : "nested", $"e{i}.gltf"), "{}");
                return folder;
            }
            DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == (state["document"]?["id"] ?? state["id"])!.GetValue<string>());
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, bool error = false)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True((result.IsError == true) == error, name + ": " + text);
                return error ? JsonValue.Create(text)! : JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments);
                if (job["id"] == null || job["State"] == null) return job;
                string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, name + ": " + job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
}
