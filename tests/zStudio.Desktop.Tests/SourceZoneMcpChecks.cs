using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit.Wpf.SharpDX;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Automation;
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
            var other = Document((await Job("source_world_open", new() { ["mission"] = "m2" }))["document"]!);
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
            // A clean editor may retarget; selected faces must survive a refused cross-document begin.
            var otherBegin = new Dictionary<string, object?> { ["document"] = other.SessionId.ToString(), ["revision"] = other.Revision, ["action"] = "begin" };
            await Job("source_zone_draft", otherBegin);
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
            var pending = state["draft"]!.DeepClone();
            Assert.Equal("pending_drafts", (await Job("source_zone_draft", otherBegin, "failed"))["code"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(pending, (await Call("source_zones", new() { ["document"] = doc.SessionId.ToString() }))["draft"]));
            Assert.Same(doc, main.OpenPropertiesWindow!.Document);
            // Refusing the native close must happen before disabling the window or canceling its lifetime.
            var lifetime = ((CancellationTokenSource)typeof(MainWindow).GetField("shutdown", fields)!.GetValue(main)!).Token;
            bool closed = false; main.Closed += (_, _) => closed = true;
            TaskCompletionSource refusedClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            answer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (handle, _) =>
            {
                var title = new StringBuilder(256); GetWindowText(handle, title, title.Capacity);
                if (title.ToString() != "Map zone draft") return true;
                SendMessage(handle, 0x0111, 7, 0); // WM_COMMAND, IDNO; only this fixture's dialog.
                refusedClose.TrySetResult(); return false;
            }, 0);
            answer.Start();
            try
            {
                main.Close(); await refusedClose.Task.WaitAsync(token);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            finally { answer.Stop(); }
            Assert.False(closed); Assert.True(main.IsEnabled); Assert.False(lifetime.IsCancellationRequested);
            Assert.False(doc.SourceWorld.Workspace.IsDirty);
            Assert.True(JsonNode.DeepEquals(pending, (await Call("source_zones", new() { ["document"] = doc.SessionId.ToString() }))["draft"]));
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
            // A rejected selector must not change the ID used by the subsequent name commit.
            var zoneFields = Assert.IsType<ZonePropertiesEditor>(main.OpenPropertiesWindow!.SourceFields);
            main.OpenPropertiesWindow.UpdateLayout();
            var inputs = Descendants(zoneFields).OfType<System.Windows.Controls.TextBox>().ToArray();
            var nameId = inputs.Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Name zone ID");
            var zoneName = inputs.Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Zone name");
            byte[] beforeName = doc.SourceWorld.Workspace.Read(manifest, token)!;
            string zeroName = SourceMapZones.Parse(beforeName, token).Label(0);
            nameId.Text = "5";
            await zoneFields.ResolveAutomationDraftsAsync(zoneFields.DraftToken, true);
            nameId.Text = "999";
            var rejected = await Assert.ThrowsAsync<StudioCommandException>(() => zoneFields.ResolveAutomationDraftsAsync(zoneFields.DraftToken, true));
            Assert.Equal("invalid_draft", rejected.Code);
            Assert.True(zoneFields.HasPendingDrafts);
            Assert.Equal(beforeName, doc.SourceWorld.Workspace.Read(manifest, token));
            // MCP discard shares FieldDraft.Discard/Display with the native Escape handler.
            await zoneFields.ResolveAutomationDraftsAsync(zoneFields.DraftToken, false);
            Assert.Equal("5", nameId.Text); Assert.False(zoneFields.HasPendingDrafts);
            zoneName.Text = "Zone five";
            await zoneFields.ResolveAutomationDraftsAsync(zoneFields.DraftToken, true);
            doc = main.OpenPropertiesWindow!.Document!;
            var namedMap = SourceMapZones.Parse(doc.SourceWorld!.Workspace.Read(manifest, token)!, token);
            Assert.Equal("Zone five", namedMap.Label(5)); Assert.Equal(zeroName, namedMap.Label(0));
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            Assert.Equal("Upper platform", SourceMapZones.Parse(File.ReadAllBytes(fixture.Path(manifest)), token).Label(7));
            Assert.Equal(original, File.ReadAllBytes(fixture.Path(geometry)));

            // A manifest-only model identity must be discoverable, addable and undoable through the shared workspace.
            const string alias = "data/m1/models/alias.gltf", aliasRoot = "alias_copy", script = "gamegen/m1.gs";
            var savedMap = SourceMapZones.Parse(File.ReadAllBytes(fixture.Path(manifest)), token);
            var binding = savedMap.Assets.Single(a => a.LogicalPath == geometry);
            Assert.Empty(binding.References);
            fixture.Write(manifest, new SourceMapZones([.. savedMap.Assets, binding with { LogicalPath = alias }], savedMap.Labels).Write(token));
            Assert.False(File.Exists(fixture.Path(alias)));
            doc = Document(await Job("reload_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision }));
            var models = await Call("source_world_models", new() { ["query"] = alias });
            Assert.Equal(alias, Assert.Single(models["items"]!.AsArray())!["path"]!.GetValue<string>());
            byte[] scriptBefore = doc.SourceWorld!.Workspace.Read(script, token)!;
            int leavesBefore = doc.PreviewDocument.Scene!.Nodes.Count(n => n.Name == "flat_a");
            doc = Document((await Job("source_world_add_model", new()
            {
                ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision,
                ["model"] = alias, ["name"] = aliasRoot, ["definitionFiles"] = Array.Empty<string>()
            }))["document"]!);
            Assert.Contains(doc.PreviewDocument.Scene!.Nodes, n => n.Name == aliasRoot);
            Assert.Equal(leavesBefore + 1, doc.PreviewDocument.Scene.Nodes.Count(n => n.Name == "flat_a"));
            doc = Document(await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" }));
            Assert.DoesNotContain(doc.PreviewDocument.Scene!.Nodes, n => n.Name == aliasRoot);
            Assert.Equal(leavesBefore, doc.PreviewDocument.Scene.Nodes.Count(n => n.Name == "flat_a"));
            Assert.Equal(scriptBefore, doc.SourceWorld!.Workspace.Read(script, token));
            Assert.False(doc.SourceWorld.Workspace.IsDirty); Assert.False(File.Exists(fixture.Path(alias)));

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
    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }
    private delegate bool WindowCallback(nint handle, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);
}
