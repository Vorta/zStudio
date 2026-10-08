using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Rendering;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class SourceZoneMcpChecks
{
    internal static async Task Run()
    {
        using SourceWorldFixture fixture = new(); fixture.WriteTerrainDatabase();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var token = deadline.Token;
        MainWindow main = new() { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using LocalMcpHost host = new(main.Commands, "test");
            await using NamedPipeClientStream pipe = new(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var doc = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);
            const string manifest = "data/m1/meta/zones.json", geometry = "data/m1/models/m1.gltf";
            byte[] original = File.ReadAllBytes(fixture.Path(geometry));
            int node = doc.SourceBuild!.Provenance.Single(p => p.Value.ModelNodeName == "flat_a").Key;
            // Use the actual rendered hit tester, without synthesizing input or capturing the mouse.
            // The upper face overlaps flat_a in XZ: a zone stroke must not select the hidden lower face.
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var view = (SceneViewport)typeof(MainWindow).GetField("scene", fields)!.GetValue(main)!;
            var viewport = (Viewport3DX)view.RenderSurface;
            int upper = doc.SourceBuild.Provenance.Single(p => p.Value.ModelNodeName == "flat_over").Key;
            view.RestoreView(new(new(220, 100, 321), new(0, -90, -1), new(0, 1, 0), 60));
            SceneInspection? hit = null;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                await Task.Delay(25, token);
                if (!view.IsOrbitPickingReady) continue;
                hit = view.ProbeInspection(viewport.Project(new Point3D(220, 10, 320)));
                if (hit?.Node == upper && hit.Polygon == 0) break;
            }
            Assert.NotNull(hit); Assert.Equal(upper, hit.Node); Assert.Equal(0, hit.Polygon);
            view.ShowZoneTargets([new(upper, 0)]);
            var outline = (LineGeometryModel3D)typeof(SceneViewport).GetField("zoneOutline", fields)!.GetValue(view)!;
            Assert.False(outline.IsHitTestVisible);
            Assert.NotNull(outline.Geometry); Assert.NotNull(outline.Geometry.Positions);
            Assert.All(outline.Geometry.Positions, p => Assert.Equal(10f, p.Y));
            Assert.Equal(upper, view.ProbeInspection(viewport.Project(new Point3D(220, 10, 320)))!.Node);
            var lower = view.ProbeInspection(viewport.Project(new Point3D(205, 0, 305)));
            Assert.NotNull(lower); Assert.Equal(node, lower.Node); Assert.Equal(0, lower.Polygon);
            view.ShowZoneTargets([]);
            var state = await Job("source_zone_draft", Args("begin"));
            Assert.IsType<ZonePropertiesEditor>(main.OpenPropertiesWindow!.SourceFields);
            string draft = state["draft"]!["draftToken"]!.GetValue<string>();
            var set = Args("set"); set["draftToken"] = draft; set["zones"] = new[] { 7, 255 };
            state = await Job("source_zone_draft", set);
            var stale = Args("add"); stale["draftToken"] = draft; stale["targets"] = new[] { new { node, polygon = 0 } };
            Assert.Equal("stale_draft", (await Job("source_zone_draft", stale, "failed"))["code"]!.GetValue<string>());
            draft = state["draft"]!["draftToken"]!.GetValue<string>();
            var add = Args("add"); add["draftToken"] = draft; add["targets"] = new[] { new { node, polygon = 0 } };
            state = await Job("source_zone_draft", add);
            Assert.Equal(1, state["draft"]!["targetCount"]!.GetValue<int>());
            Assert.False(doc.SourceWorld!.Workspace.IsDirty);
            var apply = Args("apply"); apply["draftToken"] = state["draft"]!["draftToken"]!.GetValue<string>();
            Assert.Equal("locked", (await Job("source_zone_draft", apply, "failed"))["code"]!.GetValue<string>());
            // Explicit unlock is the same document state the GUI lock control owns.
            doc.PickupsLocked = false;
            using (CancellationTokenSource cancelBuild = new())
            {
                var workspace = doc.SourceWorld!.Workspace;
                void CancelAccepted(SourceWorkspaceChange change) { if (workspace.IsDirty) cancelBuild.Cancel(); }
                workspace.Changed += CancelAccepted;
                try
                {
                    var task = (Task<DocumentModel>)typeof(MainWindow).GetMethod("ApplyZoneDraftAsync", fields)!
                        .Invoke(main, [doc, apply["draftToken"], cancelBuild.Token])!;
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                }
                finally { workspace.Changed -= CancelAccepted; }
                Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo);
                var retained = await Call("source_zones", new() { ["document"] = doc.SessionId.ToString() });
                Assert.Equal(1, retained["draft"]!["targetCount"]!.GetValue<int>());
                Assert.Equal(new[] { 7, 255 }, retained["draft"]!["zones"]!.AsArray().Select(z => z!.GetValue<int>()));
                Assert.IsType<ZonePropertiesEditor>(main.OpenPropertiesWindow!.SourceFields);
                apply = Args("apply"); apply["draftToken"] = retained["draft"]!["draftToken"]!.GetValue<string>();
            }
            state = await Job("source_zone_draft", apply); doc = Document(state["document"]!);
            Assert.Equal(new[] { manifest }, doc.SourceWorld!.Workspace.DirtyFiles);
            Assert.Equal(original, File.ReadAllBytes(fixture.Path(geometry)));
            var map = SourceMapZones.Parse(doc.SourceWorld.Workspace.Read(manifest, token)!, token);
            Assert.Contains(0xFFFF0702u, map.Assets.SelectMany(a => a.Profile.MeshPolygons).SelectMany(p => p));
            var inspector = await Call("source_zones", new() { ["document"] = doc.SessionId.ToString() });
            Assert.True(inspector["catalog"]!["Exists"]!.GetValue<bool>());
            Assert.IsType<ZonePropertiesEditor>(main.OpenPropertiesWindow!.SourceFields);
            draft = inspector["draft"]!["draftToken"]!.GetValue<string>();
            var name = Args("label"); name["draftToken"] = draft; name["zoneId"] = 7; name["label"] = "Upper platform";
            state = await Job("source_zone_draft", name); doc = Document(state["document"]!);
            Assert.Equal("Upper platform", SourceMapZones.Parse(doc.SourceWorld!.Workspace.Read(manifest, token)!, token).Label(7));
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            Assert.Equal("Upper platform", SourceMapZones.Parse(File.ReadAllBytes(fixture.Path(manifest)), token).Label(7));
            Assert.Equal(original, File.ReadAllBytes(fixture.Path(geometry)));

            Dictionary<string, object?> Args(string action) => new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = action };
            DocumentModel Document(JsonNode value) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == value["id"]!.GetValue<string>());
            async Task<JsonNode> Call(string name, Dictionary<string, object?> args)
            {
                var result = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.False(result.IsError == true, text); return JsonNode.Parse(text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> args, string expected = "completed")
            {
                var job = await Call(name, args); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running")
                { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
}
