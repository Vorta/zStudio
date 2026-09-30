using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Source worlds through the real named-pipe MCP connection: a model from another mission, undo, save and reload.</summary>
internal static class SourceWorldMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        string? sessionFolder = null;
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);

            var none = await Job("source_world_open", new() { ["mission"] = "m1" }, "failed"); Assert.Equal("no_project", none["code"]!.GetValue<string>());
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var unknown = await Job("source_world_open", new() { ["mission"] = "m9" }, "failed"); Assert.Equal("invalid_argument", unknown["code"]!.GetValue<string>());

            // Tools lists the project's worlds.
            typeof(MainWindow).GetMethod("ToolsMenuOpened", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [main, new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, main)]);
            var worlds = (MenuItem)main.FindName("SourceWorldMenu");
            Assert.Equal(Visibility.Visible, worlds.Visibility); Assert.False(((MenuItem)main.FindName("AddSourceModelMenu")).IsEnabled);
            while (worlds.Items.Count != 2 || worlds.Items[0] is MenuItem { IsEnabled: false }) { token.ThrowIfCancellationRequested(); await Task.Delay(10, token); }
            Assert.Equal(["m1", "m2"], worlds.Items.Cast<MenuItem>().Select(i => ((TextBlock)i.Header).Text));

            // The world is a private build, shown read-only with the source world tools.
            var opened = (await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!;
            var doc = Document(opened);
            Assert.Equal("m1", opened["sourceWorld"]!["mission"]!.GetValue<string>());
            Assert.Equal("m1 world (sources)", doc.Title);
            Assert.False(doc.Path.StartsWith(fixture.Project, StringComparison.OrdinalIgnoreCase));
            sessionFolder = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(doc.Path)));
            Assert.Null(doc.ModelEdits);
            await Preview();
            Assert.Equal(Visibility.Visible, ((FrameworkElement)main.FindName("SourceWorldTools")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)main.FindName("PickupTools")).Visibility);
            var locked = await Call("pickup_lock", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["locked"] = false }, error: true);
            Assert.Contains("unsupported", locked.ToJsonString());
            // Opening it again activates the same document.
            Assert.Equal(Id(doc), (await Job("source_world_open", new() { ["mission"] = "M1" }))["document"]!["id"]!.GetValue<string>());

            var models = await Call("source_world_models", new() { ["query"] = "bft" });
            Assert.Equal("data/m2/models/bft/tank.gltf", models["items"]![0]!["path"]!.GetValue<string>());
            var definitions = await Call("source_world_definitions", new() { ["document"] = Id(doc), ["name"] = "tank" });
            Assert.Equal(SourceWorldFixture.TankDefinitions, definitions["files"]![0]!["path"]!.GetValue<string>());

            // Adding a model from m2 rebuilds the world into a replacement document; the old one is released.
            var added = Document((await Job("source_world_add_model", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["model"] = fixture.Tank, ["name"] = "tank" }))["document"]!);
            Assert.True(doc.IsDisposed); Assert.NotEqual(doc.SessionId, added.SessionId);
            Assert.False(File.Exists(doc.Path));
            Assert.True(added.IsDirty); Assert.Equal("m1 world (sources) *", added.Title);
            Assert.Contains(added.PreviewDocument.Scene!.Nodes, n => n.Name == "tank");
            Assert.Equal([SourceWorldFixture.TankDefinitions], added.SourceWorld!.Edits.Additions.Single().DefinitionFiles);
            var stale = await Job("source_world_add_model", new() { ["document"] = Id(doc), ["revision"] = 0, ["model"] = fixture.Tank, ["name"] = "tank2" }, "failed");
            Assert.Equal("stale_document", stale["code"]!.GetValue<string>());
            var bad = await Job("source_world_add_model", new() { ["document"] = Id(added), ["revision"] = added.Revision, ["model"] = "gamegen/m1.gs", ["name"] = "x" }, "failed");
            Assert.Equal("invalid_argument", bad["code"]!.GetValue<string>());
            var placed = Document((await Job("source_world_add_model", new()
            {
                ["document"] = Id(added), ["revision"] = added.Revision, ["model"] = fixture.Tank, ["name"] = "tank_wreck",
                ["position"] = new Dictionary<string, object?> { ["x"] = 100, ["y"] = 0, ["z"] = -50 }, ["heading"] = 90, ["definitionFiles"] = Array.Empty<string>()
            }))["document"]!);
            await Preview();
            Assert.Contains(placed.PreviewDocument.Scene!.Nodes, n => n.Name == "tank_wreck");

            // Undo and redo rebuild too.
            var undone = Document(await Call("undo_redo", new() { ["document"] = Id(placed), ["revision"] = placed.Revision, ["action"] = "undo" }));
            Assert.DoesNotContain(undone.PreviewDocument.Scene!.Nodes, n => n.Name == "tank_wreck");
            var redone = Document(await Call("undo_redo", new() { ["document"] = Id(undone), ["revision"] = undone.Revision, ["action"] = "redo" }));
            Assert.Contains(redone.PreviewDocument.Scene!.Nodes, n => n.Name == "tank_wreck");

            // Save writes the script and the animation list; there is no Save As.
            var saveAs = await Job("save_document", new() { ["document"] = Id(redone), ["revision"] = redone.Revision, ["destination"] = Path.Combine(fixture.Root, "x.zbd") }, "failed");
            Assert.Equal("invalid_argument", saveAs["code"]!.GetValue<string>());
            var saved = await Job("save_document", new() { ["document"] = Id(redone), ["revision"] = redone.Revision });
            Assert.Equal(["gamegen/m1.gs", "data/m1/zrdr/anim.zrd"], saved["written"]!.AsArray().Select(w => w!.GetValue<string>()));
            Assert.False(redone.IsDirty);
            string script = fixture.Path("gamegen/m1.gs");
            Assert.Contains("LoadGameGen tank.flt tank\r\n", await File.ReadAllTextAsync(script, token));

            // A changed model marks the world stale; reloading rebuilds it from disk.
            main.ViewModel.CheckExternalChanges(); Assert.False(redone.IsStale);
            File.SetLastWriteTimeUtc(fixture.Path(fixture.Tank), DateTime.UtcNow.AddMinutes(2));
            main.ViewModel.CheckExternalChanges(); Assert.True(redone.IsStale);
            var reloaded = Document(await Job("reload_document", new() { ["document"] = Id(redone), ["revision"] = redone.Revision }));
            Assert.False(reloaded.IsStale); Assert.Contains(reloaded.PreviewDocument.Scene!.Nodes, n => n.Name == "tank_wreck");
            // After the script changes on disk, the pending edits cannot be kept and reloading starts from the file.
            var pending = Document((await Job("source_world_add_model", new() { ["document"] = Id(reloaded), ["revision"] = reloaded.Revision, ["model"] = "data/m1/models/m1.gltf", ["name"] = "extra", ["definitionFiles"] = Array.Empty<string>() }))["document"]!);
            await File.AppendAllTextAsync(script, "# elsewhere\r\n", token); File.SetLastWriteTimeUtc(script, DateTime.UtcNow.AddMinutes(3));
            var refused = await Job("reload_document", new() { ["document"] = Id(pending), ["revision"] = pending.Revision }, "failed");
            Assert.Equal("unsaved_changes", refused["code"]!.GetValue<string>());
            var conflict = await Job("save_document", new() { ["document"] = Id(pending), ["revision"] = pending.Revision }, "failed");
            Assert.Equal("io_failed", conflict["code"]!.GetValue<string>());
            Assert.EndsWith("# elsewhere\r\n", await File.ReadAllTextAsync(script, token));
            var undoneExtra = Document(await Call("undo_redo", new() { ["document"] = Id(pending), ["revision"] = pending.Revision, ["action"] = "undo" }));
            var fresh = Document(await Job("reload_document", new() { ["document"] = Id(undoneExtra), ["revision"] = undoneExtra.Revision }));
            Assert.False(fresh.SourceWorld!.Edits.CanUndo); Assert.Contains(fresh.PreviewDocument.Scene!.Nodes, n => n.Name == "tank");

            // Closing the world removes its private files.
            await Call("close_document", new() { ["document"] = Id(fresh), ["revision"] = fresh.Revision });
            Assert.False(Directory.Exists(sessionFolder));

            async Task Preview()
            {
                var work = (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                await work.WaitAsync(token);
            }
            DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == state["id"]!.GetValue<string>());
            static string Id(DocumentModel d) => d.SessionId.ToString();
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, bool error = false)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True((result.IsError == true) == error, text);
                return error ? JsonValue.Create(text)! : JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
            if (sessionFolder != null) Assert.False(Directory.Exists(sessionFolder));
        }
    }
}
