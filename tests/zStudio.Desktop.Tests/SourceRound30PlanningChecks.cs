using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>GUI/shared planning and real protocol regressions for complete source dependency analysis.</summary>
internal static class SourceRound30PlanningChecks
{
    internal static async Task Run()
    {
        await Check(macroTerrain: true);
        await Check(macroTerrain: false);
    }

    private static async Task Check(bool macroTerrain)
    {
        using SourceWorldFixture fixture = new();
        string script = File.ReadAllText(fixture.Path("gamegen/m1.gs"));
        if (macroTerrain)
        {
            fixture.WriteTerrainDatabase();
            fixture.Write("gamegen/m1.gs", script.Replace("GameZWriteZBDFile", "set prefix flat\nFindNode %prefix%_a\nSetIntersectSurface off\nGameZWriteZBDFile"));
        }
        else
        {
            fixture.Write("gamegen/m1.gs", script.Replace("GameZWriteZBDFile", "SetModelDirectory ../data/m2/models/bft\nLoadGameGen tank.gltf tank\nGameZWriteZBDFile"));
            fixture.Write("gamegen/m2.gs", "NewWorld world\nSetModelDirectory ../data/m2/models/bft\nLoadGameGen tank.gltf tank\nFindNode tank\nFindSubNode hull\nObject3DRotate 0 45 0\nGameZWriteZBDFile world.zbd\n");
        }
        var original = Directory.GetFiles(fixture.Project, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
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
            var opened = await Job("source_world_open", new() { ["mission"] = "m1" });
            var doc = main.ViewModel.Documents.Single(d => d.SessionId.ToString() == opened["document"]!["id"]!.GetValue<string>());
            await ((Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).WaitAsync(token);
            Assert.DoesNotContain(doc.SourceBuild!.Outputs, o => o.Error != null);
            var workspace = doc.SourceWorld!.Workspace;
            long revision = workspace.Revision, contentRevision = workspace.ContentRevision, documentRevision = doc.Revision;
            var history = workspace.History.ToArray(); var preview = doc.PreviewDocument;

            if (macroTerrain)
            {
                // The preview packages every script into interp.zbd: archive membership does not mean m2 ran in m1.
                Assert.Contains("gamegen/m2.gs", doc.SourceBuild.Dependencies);
                var world = GameZWorldReader.FromDocument(doc.Document, token);
                Assert.Single(world.Nodes, n => n.Name == "flat_a");
                Assert.DoesNotContain(doc.SourceBuild.Outputs.SelectMany(o => o.Warnings), w => w.Contains("FindNode") && w.Contains("found no node"));
                var method = typeof(MainWindow).GetMethod("PlanTerrainConversionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var gui = await ((Task<(TerrainConversionPlan Plan, long Revision)>)method.Invoke(main, [doc, token])!).WaitAsync(token);
                Assert.Equal(2, gui.Plan.Converted);
                Unchanged();
                var protocol = await Job("source_terrain_convert", Arguments());
                Assert.Equal(2, protocol["plan"]!["converted"]!.GetValue<int>());
                Assert.Equal(3, protocol["plan"]!["keptCount"]!.GetValue<int>());
                Unchanged();
                var apply = Arguments(); apply["apply"] = true;
                var converted = await Job("source_terrain_convert", apply);
                var next = main.ViewModel.Documents.Single(d => d.SessionId.ToString() == converted["document"]!["id"]!.GetValue<string>());
                Assert.Single(GameZWorldReader.FromDocument(next.Document, token).Nodes, n => n.Name == "flat_a");
                Assert.DoesNotContain(next.SourceBuild!.Outputs.SelectMany(o => o.Warnings), w => w.Contains("FindNode") && w.Contains("found no node"));
                Assert.True(workspace.CanUndo); Assert.True(workspace.IsDirty);
                foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
                await Call("undo_redo", new() { ["document"] = next.SessionId.ToString(), ["revision"] = next.Revision, ["action"] = "undo" });
                Assert.False(workspace.IsDirty);
            }
            else
            {
                var world = GameZWorldReader.FromDocument(doc.Document, token);
                var hull = Assert.Single(world.Nodes, n => n.Name == "hull");
                int node = GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken)[hull];
                var requested = ObjectTransform.Of(hull) with { Position = new Vector3(2, 0, 0) };
                var method = typeof(MainWindow).GetMethod("MoveSourceObjectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Task<DocumentModel> GuiEdit() => (Task<DocumentModel>)method.Invoke(main, [doc, node, requested, token])!;
                Dictionary<string, object?> EditArguments()
                {
                    var args = Arguments(); args["node"] = node; args["action"] = "transform";
                    args["position"] = new Dictionary<string, object?> { ["x"] = 2, ["y"] = 0, ["z"] = 0 }; return args;
                }
                var readable = await Assert.ThrowsAsync<StudioCommandException>(GuiEdit);
                Assert.Equal("invalid_argument", readable.Code); Assert.Contains("Object3DRotate", readable.Message);
                Unchanged();
                using (FileStream held = new(fixture.Path("gamegen/m2.gs"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var refused = await Assert.ThrowsAsync<StudioCommandException>(GuiEdit);
                    Assert.Equal("io_failed", refused.Code);
                    // The workspace dependency guard can reject the lock before transform analysis begins.
                    Assert.Contains(fixture.Path("gamegen/m2.gs").Replace('\\', '/'), refused.Message.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
                    var protocol = await Job("source_world_object_edit", EditArguments(), "failed");
                    Assert.Equal(refused.Code, protocol["code"]!.GetValue<string>());
                    Assert.Equal(refused.Message, protocol["message"]!.GetValue<string>());
                    Unchanged("gamegen/m2.gs");
                }
                // A failed read must not be cached as absence: releasing the lock restores the semantic refusal.
                var retried = await Job("source_world_object_edit", EditArguments(), "failed");
                Assert.Equal("invalid_argument", retried["code"]!.GetValue<string>());
                Assert.Contains("Object3DRotate", retried["message"]!.GetValue<string>());
                Unchanged();
            }

            Dictionary<string, object?> Arguments() => new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision };
            void Unchanged(string? locked = null)
            {
                Assert.Equal(revision, workspace.Revision); Assert.Equal(contentRevision, workspace.ContentRevision);
                Assert.Equal(documentRevision, doc.Revision); Assert.Equal(history, workspace.History);
                Assert.False(workspace.CanUndo); Assert.False(workspace.CanRedo); Assert.False(workspace.IsDirty);
                Assert.Empty(workspace.Overlay()); Assert.Same(doc, main.ViewModel.SelectedDocument);
                Assert.Same(preview, doc.PreviewDocument); Assert.False(doc.IsDisposed);
                Assert.False(main.CancelOperationItem.IsEnabled);
                foreach (var (path, bytes) in original) if (path != (locked == null ? null : fixture.Path(locked))) Assert.Equal(bytes, File.ReadAllBytes(path));
            }
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True(result.IsError != true, name + ": " + text);
                return JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running")
                { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
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
