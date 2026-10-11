using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Real GUI and named-pipe refusal when conversion cannot discover a complete reference set.</summary>
internal static class SourceTerrainReferenceCapacityChecks
{
    internal static async Task Run()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteTerrainDatabase();
        const string resource = "data/m1/zrdr/terrain-references.zrd";
        fixture.Write(resource, "MODEL ( \"flat_a\" ) \"" + new string(' ', 4 * 1024 * 1024 + 1) + "\"");
        // The shared warning collector must bound the authored operand before repeatedly interpolating it.
        fixture.Write("gamegen/repeated-warning.gw", "FindNode " + new string('q', 16_384) + "\n");
        fixture.Write("gamegen/m1.gs", string.Concat(Enumerable.Repeat("source repeated-warning.gw\n", 100))
            + File.ReadAllText(fixture.Path("gamegen/m1.gs")));
        var originalFiles = Directory.GetFiles(fixture.Project, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var opened = await Job("source_world_open", new() { ["mission"] = "m1" }, "completed");
            var doc = main.ViewModel.Documents.Single(d => d.SessionId.ToString() == opened["document"]!["id"]!.GetValue<string>());
            await ((Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).WaitAsync(token);
            Assert.Contains(resource, doc.SourceBuild!.Dependencies);
            Assert.DoesNotContain(doc.SourceBuild.Outputs, o => o.Error != null);
            var workspace = doc.SourceWorld!.Workspace;
            long revision = workspace.Revision, contentRevision = workspace.ContentRevision, documentRevision = doc.Revision;
            var history = workspace.History.ToArray();
            bool canUndo = workspace.CanUndo, canRedo = workspace.CanRedo;
            var preview = doc.PreviewDocument;

            var warning = Assert.Single(doc.SourceBuild.Outputs.SelectMany(o => o.Warnings), w => w.Contains("FindNode") && w.Contains("found no node"));
            Assert.InRange(warning.Length, 1, 1024);
            var shownWarning = Assert.Single(main.ViewModel.Problems, p => p.Message.Contains("FindNode") && p.Message.Contains("found no node"));
            Assert.Equal("Warning", shownWarning.Severity);
            var problems = (await Call("problems", new() { ["query"] = "found no node" }))["items"]!.AsArray();
            var protocolWarning = Assert.Single(problems);
            Assert.Equal(shownWarning.Message, protocolWarning!["Message"]!.GetValue<string>());
            Assert.Equal(shownWarning.Severity, protocolWarning["Severity"]!.GetValue<string>());
            Assert.Equal(shownWarning.File, protocolWarning["File"]!.GetValue<string>());

            // Check the exact planner used by the GUI before clicking: a regression must fail this assertion,
            // never open the conversion-confirmation dialog and block the test process.
            var planMethod = typeof(MainWindow).GetMethod("PlanTerrainConversionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = (Task<(TerrainConversionPlan Plan, long Revision)>)planMethod.Invoke(main, [doc, token])!;
            var refusal = await Assert.ThrowsAsync<StudioCommandException>(async () => await pending);
            Assert.Equal("invalid_argument", refusal.Code);
            Assert.Contains("characters of them", refusal.Message);
            Assert.Contains(resource, refusal.Message);
            Unchanged();

            ((MenuItem)main.FindName("ConvertTerrainMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            while (main.CancelOperationItem.IsEnabled || main.ViewModel.Status != refusal.Message) await Task.Delay(10, token);
            Assert.Contains(main.ViewModel.Problems, p => p.Message == refusal.Message);
            Unchanged();

            foreach (bool apply in new[] { false, true })
            {
                var failed = await Job("source_terrain_convert", new()
                {
                    ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["apply"] = apply,
                }, "failed");
                Assert.Equal("invalid_argument", failed["code"]!.GetValue<string>());
                Assert.Equal(refusal.Message, failed["message"]!.GetValue<string>());
                Unchanged();
            }

            void Unchanged()
            {
                Assert.Equal(revision, workspace.Revision); Assert.Equal(contentRevision, workspace.ContentRevision);
                Assert.Equal(documentRevision, doc.Revision); Assert.Equal(history, workspace.History);
                Assert.Equal(canUndo, workspace.CanUndo); Assert.Equal(canRedo, workspace.CanRedo);
                Assert.Empty(workspace.DirtyFiles); Assert.Empty(workspace.Overlay());
                Assert.False(doc.IsDisposed); Assert.Same(doc, main.ViewModel.SelectedDocument);
                Assert.Same(doc, main.ViewModel.Documents.Single(d => d.SourceWorld != null));
                Assert.Same(preview, doc.PreviewDocument); Assert.False(main.CancelOperationItem.IsEnabled);
                foreach (var (path, bytes) in originalFiles) Assert.Equal(bytes, File.ReadAllBytes(path));
                Assert.Empty(Directory.GetFiles(fixture.Path("data"), "*.terrain.json", SearchOption.AllDirectories));
            }

            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True(result.IsError != true, name + ": " + text);
                return JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected)
            {
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running")
                { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, name + ": " + job.ToJsonString());
                return job["result"]!;
            }
        }
        finally
        {
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
}
