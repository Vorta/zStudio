using System.Windows;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Automation;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using zStudio.Desktop;
using zStudio.Mcp;
using zStudio.Rendering;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

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
            const string geometry = "data/m1/models/m1.gltf", buffer = "data/m1/models/m1.bin";
            byte[] original = File.ReadAllBytes(fixture.Path(geometry));
            byte[] originalBuffer = File.ReadAllBytes(fixture.Path(buffer));
            int node = doc.SourceBuild!.Provenance.Single(p => p.Value.ModelNodeName == "flat_a").Key;
            // Use the actual rendered hit tester, without synthesizing input or capturing the mouse.
            // The upper face overlaps flat_a in XZ: a pick must not select the hidden lower face.
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var view = (SceneViewport)typeof(MainWindow).GetField("scene", fields)!.GetValue(main)!;
            var viewport = (Viewport3DX)view.RenderSurface;
            int upper = doc.SourceBuild.Provenance.Single(p => p.Value.ModelNodeName == "flat_over").Key;
            int side = doc.SourceBuild.Provenance.Single(p => p.Value.ModelNodeName == "flat_b").Key;
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
            var scope = (await Call("source_zones", new()
            {
                ["document"] = doc.SessionId.ToString(), ["targets"] = new[] { new { node, polygon = 0 } }
            }))["inspection"]!;
            Assert.Equal(geometry, scope["MasterPath"]!.GetValue<string>());
            Assert.Contains(geometry, scope["SourcePaths"]!.AsArray().Select(p => p!.GetValue<string>()));
            var exactTarget = Assert.Single(scope["Targets"]!.AsArray())!["Target"]!;
            Assert.Equal(geometry, exactTarget["OwnerPath"]!.GetValue<string>());
            Assert.Empty(exactTarget["OwnerReferences"]!.AsArray());

            // The tool: engaging pauses the chosen overlay (it draws its own zone view) and lists the map's zones as placed in the world.
            var setHighlight = typeof(MainWindow).GetMethod("SetWorldHighlightMode", fields)!;
            setHighlight.Invoke(main, [zStudio.Core.WorldHighlightMode.Landmarks]);
            var tool = await Call("source_zone_tool", new() { ["document"] = doc.SessionId.ToString(), ["action"] = "engage", ["mode"] = "paint", ["zone"] = 7 });
            Assert.True(tool["engaged"]!.GetValue<bool>()); Assert.Equal(7, tool["zone"]!.GetValue<int>());
            Assert.Equal(4, tool["zones"]!.AsArray().Single(z => z!["zone"]!.GetValue<int>() == 3)!["polygons"]!.GetValue<long>());
            Assert.Equal(1, tool["zones"]!.AsArray().Single(z => z!["zone"]!.GetValue<int>() == 255)!["polygons"]!.GetValue<long>());
            Assert.True(view.ZonePaintActive); Assert.Equal(zStudio.Core.WorldHighlightMode.None, view.HighlightMode);
            // What the tool cannot paint lets the pointer through to the polygon behind it.
            var realBrush = view.ZoneBrush!;
            view.SetZoneBrush(new PolygonZoneBrush { Reach = n => n == upper ? PolygonZoneReach.PassThrough : realBrush.Reach(n), SourcePolygons = realBrush.SourcePolygons, Site = realBrush.Site, SiteNodes = realBrush.SiteNodes });
            object?[] probe = [viewport.Project(new Point3D(220, 10, 320)), false, null];
            var behind = (ZonePaintHover?)typeof(SceneViewport).GetMethod("ProbeZone", fields)!.Invoke(view, probe);
            Assert.NotNull(behind); Assert.Equal(node, behind.Node); Assert.Equal(new byte[] { 3 }, behind.Zones); Assert.Equal(PolygonZoneOutcome.Changed, behind.Outcome);
            view.SetZoneBrush(realBrush);

            // A stroke applies as one undoable change, without evaluating the world again.
            List<string> evaluations = []; main.SourceEditEvaluation = evaluations.Add;
            var paint = await Job("source_polygon_zones", Paint("paint", 7, (node, 0), (upper, 0)));
            doc = Document(paint["document"]!);
            Assert.Equal(("Paint zone 7 on 2 polygons", 2), (paint["Label"]!.GetValue<string>(), paint["Changed"]!.GetValue<int>()));
            Assert.Equal(["incremental"], evaluations);
            Assert.Same(view, typeof(MainWindow).GetField("scene", fields)!.GetValue(main));
            Assert.Equal(new byte[] { 3, 7 }, Zones(node)); Assert.Equal(new byte[] { 3, 7 }, Zones(upper));
            Assert.Equal(new[] { geometry }, doc.SourceWorld!.Workspace.DirtyFiles);
            Assert.Equal(original, File.ReadAllBytes(fixture.Path(geometry)));
            // Nothing to change accepts nothing.
            string? undoLabel = doc.SourceWorld.Workspace.UndoLabel;
            var again = await Job("source_polygon_zones", Paint("paint", 7, (node, 0)));
            Assert.Equal((0, 1), (again["Changed"]!.GetValue<int>(), again["Unchanged"]!.GetValue<int>()));
            Assert.Equal(undoLabel, doc.SourceWorld.Workspace.UndoLabel);
            // A full polygon is skipped and reported; Any stands alone.
            doc = Document((await Job("source_polygon_zones", Paint("paint", 8, (node, 0))))["document"]!);
            var full = await Job("source_polygon_zones", Paint("paint", 9, (node, 0), (upper, 0)));
            doc = Document(full["document"]!);
            Assert.Equal((1, 1), (full["Changed"]!.GetValue<int>(), full["full"]!.GetValue<int>()));
            Assert.Equal(node, Assert.Single(full["fullTargets"]!.AsArray())!["node"]!.GetValue<int>());
            Assert.Equal(new byte[] { 3, 7, 8 }, Zones(node));
            doc = Document((await Job("source_polygon_zones", Paint("paint", 255, (upper, 0))))["document"]!);
            Assert.Equal(new byte[] { 255 }, Zones(upper));
            doc = Document((await Job("source_polygon_zones", Paint("erase", 255, (upper, 0))))["document"]!);
            Assert.Empty(Zones(upper));
            // Exact targets: an index no built face comes from, a stale identity and an old revision are refused.
            Assert.Equal("invalid_argument", (await Job("source_polygon_zones", Paint("paint", 5, (node, 4095)), "failed"))["code"]!.GetValue<string>());
            var oldRevision = Paint("paint", 5, (node, 0)); oldRevision["revision"] = doc.Revision - 1;
            Assert.Equal("revision_conflict", (await Job("source_polygon_zones", oldRevision, "failed"))["code"]!.GetValue<string>());
            var otherDocument = Paint("paint", 5, (node, 0)); otherDocument["document"] = other.SessionId.ToString(); otherDocument["revision"] = other.Revision;
            Assert.True(other.IsDisposed);
            Assert.Equal("stale_document", (await Job("source_polygon_zones", otherDocument, "failed"))["code"]!.GetValue<string>());

            // GUI strokes: released while an earlier one applies, a stroke waits its turn, also when the tool is left
            // meanwhile; each applies through the same edit, and Undo takes them back.
            evaluations.Clear();
            var finished = typeof(MainWindow).GetMethod("ZoneStrokeFinished", fields)!;
            finished.Invoke(main, [view, new ZonePaintStroke(1000, 5, false, [new(side, 0)], 1, 0, 0, [], false, false)]);
            finished.Invoke(main, [view, new ZonePaintStroke(1001, 6, false, [new(side, 0)], 1, 0, 0, [], false, false)]);
            // Leaving the tool at once (the second stroke still waits) keeps the queue.
            Assert.Single((System.Collections.IEnumerable)typeof(MainWindow).GetField("zoneStrokes", fields)!.GetValue(main)!);
            typeof(MainWindow).GetMethod("LeaveZoneTool", fields)!.Invoke(main, []);
            var toolState = typeof(MainWindow).GetField("zoneTool", fields)!.GetValue(main)!;
            var applying = toolState.GetType().GetProperty("Applying", fields)!;
            while ((bool)applying.GetValue(toolState)!) await Task.Delay(10, token);
            doc = main.ViewModel.SelectedDocument!;
            Assert.Equal(new byte[] { 3, 5, 6 }, Zones(side)); Assert.Equal(["incremental", "incremental"], evaluations);
            Assert.Equal("Paint zone 6 on 1 polygon", doc.SourceWorld!.Workspace.UndoLabel);
            doc = Document(await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" }));
            doc = Document(await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" }));
            Assert.Equal(new byte[] { 3 }, Zones(side)); Assert.Equal(4, evaluations.Count(e => e == "incremental"));
            main.SourceEditEvaluation = null;
            await Call("source_zone_tool", new() { ["document"] = doc.SessionId.ToString(), ["action"] = "engage" });

            // Labels: named, renamed, removed (each one undoable change), and saved with the map.
            doc = Document((await Job("source_zone_label", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["zoneId"] = 7, ["label"] = "Upper platform" }))["document"]!);
            doc = Document((await Job("source_zone_label", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["zoneId"] = 5, ["label"] = "Zone five" }))["document"]!);
            doc = Document((await Job("source_zone_label", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["zoneId"] = 5, ["label"] = "" }))["document"]!);
            Assert.Equal("Remove the label of zone 5", doc.SourceWorld!.Workspace.UndoLabel);
            tool = await Call("source_zone_tool", new() { ["document"] = doc.SessionId.ToString(), ["action"] = "state" });
            Assert.Equal("Upper platform", tool["zones"]!.AsArray().Single(z => z!["zone"]!.GetValue<int>() == 7)!["label"]!.GetValue<string>());
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            SourceWorkspace reopened = new(fixture.Project);
            var labels = SourceZoneEdits.Catalog(reopened, "m1", token).Labels;
            Assert.Equal("Upper platform", labels.Single(l => l.Id == 7).Label); Assert.DoesNotContain(labels, l => l.Id == 5 && l.Authored);
            Assert.NotEqual(original, File.ReadAllBytes(fixture.Path(geometry)));
            Assert.Equal(originalBuffer, File.ReadAllBytes(fixture.Path(buffer)));
            Assert.False(File.Exists(fixture.Path("data/m1/meta/zones.json")));

            // Leaving the tool resumes the chosen overlay.
            tool = await Call("source_zone_tool", new() { ["document"] = doc.SessionId.ToString(), ["action"] = "exit" });
            Assert.False(tool["engaged"]!.GetValue<bool>()); Assert.False(view.ZonePaintActive);
            Assert.Equal(zStudio.Core.WorldHighlightMode.Landmarks, view.HighlightMode);
            setHighlight.Invoke(main, [zStudio.Core.WorldHighlightMode.None]);

            // Hide UI (F10) leaves the map's view alone: it leaves the tool, which waits until the UI shows again.
            await Call("source_zone_tool", new() { ["document"] = doc.SessionId.ToString(), ["action"] = "engage" });
            await Call("presentation", new() { ["uiHidden"] = true });
            Assert.False(view.ZonePaintActive);
            Assert.All(new[] { "SourceCommandBar", "SourceOutlinerPane", "SourcePropertiesPane" }, name => Assert.Equal(Visibility.Collapsed, ((UIElement)main.FindName(name)).Visibility));
            await Call("source_zone_tool", new() { ["document"] = doc.SessionId.ToString(), ["action"] = "engage" }, "busy");
            await Call("presentation", new() { ["uiHidden"] = false });
            Assert.All(new[] { "SourceCommandBar", "SourceOutlinerPane", "SourcePropertiesPane" }, name => Assert.Equal(Visibility.Visible, ((UIElement)main.FindName(name)).Visibility));

            // The World tree's Zones section lists the map's zones with their labels; a zone's eye hides only its colour, and
            // selecting a zone opens its card (the label is its draft). Zones start hidden: a zone's eye shows its colour alone (the
            // zone overlay turns on), and the Zones row's master eye shows or hides every zone.
            var zoneRoots = await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString() });
            string zoneContext = zoneRoots["context"]!.GetValue<string>(), zonePreview = zoneRoots["preview"]!.GetValue<string>();
            var zonesRow = zoneRoots["children"]!["items"]!.AsArray().Single(r => r!["sectionRow"]?["section"]?.GetValue<string>() == "zones")!;
            var zoneRows = (await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString(), ["context"] = zoneContext, ["row"] = zonesRow["row"]!.GetValue<string>() }))["children"]!["items"]!.AsArray();
            var upperZone = zoneRows.Single(z => z!["sectionRow"]?["zoneId"]?.GetValue<int>() == 7)!;
            Assert.Equal("Upper platform", upperZone["sectionRow"]!["label"]!.GetValue<string>());
            Assert.True(upperZone["sectionRow"]!["polygons"]!.GetValue<long>() > 0);
            async Task<JsonNode> ZoneState() => (await Call("preview_state", new() { ["preview"] = zonePreview }))["sections"]!["zones"]!;
            var zoneState = await ZoneState();
            Assert.Empty(zoneState["shown"]!.AsArray()); Assert.False(zoneState["master"]!.GetValue<bool>()); Assert.False(zoneState["overlay"]!.GetValue<bool>());
            await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString(), ["context"] = zoneContext, ["preview"] = zonePreview, ["action"] = "show", ["row"] = upperZone["row"]!.GetValue<string>() });
            zoneState = await ZoneState();
            Assert.Equal([7], zoneState["shown"]!.AsArray().Select(z => z!.GetValue<int>())); Assert.True(zoneState["overlay"]!.GetValue<bool>());
            Assert.DoesNotContain((byte)7, view.HiddenZones); Assert.Contains((byte)8, view.HiddenZones);
            string zonesRowId = zonesRow["row"]!.GetValue<string>();
            await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString(), ["context"] = zoneContext, ["preview"] = zonePreview, ["action"] = "show", ["row"] = zonesRowId });
            zoneState = await ZoneState();
            Assert.True(zoneState["master"]!.GetValue<bool>()); Assert.Contains(7, zoneState["shown"]!.AsArray().Select(z => z!.GetValue<int>()));
            await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString(), ["context"] = zoneContext, ["preview"] = zonePreview, ["action"] = "hide", ["row"] = zonesRowId });
            zoneState = await ZoneState();
            Assert.Empty(zoneState["shown"]!.AsArray()); Assert.False(zoneState["master"]!.GetValue<bool>()); Assert.False(zoneState["overlay"]!.GetValue<bool>());
            await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString(), ["context"] = zoneContext, ["preview"] = zonePreview, ["action"] = "select", ["row"] = upperZone["row"]!.GetValue<string>() });
            var zoneDraft = (await Call("drafts", new() { ["target"] = "section" }))["drafts"]!;
            Assert.Equal("zone", zoneDraft["kind"]!.GetValue<string>()); Assert.Equal("Upper platform", zoneDraft["label"]!.GetValue<string>());
            // The card's Apply accepts its label as one undoable change; the rebuilt map selects the zone again, its card
            // showing the stored label with nothing pending.
            var card = typeof(MainWindow).GetField("sectionCard", fields)!.GetValue(main)!;
            ((System.Windows.Controls.TextBox)card.GetType().GetField("label", fields)!.GetValue(card)!).Text = "Upper deck";
            var pending = (await Call("drafts", new() { ["target"] = "section" }))["drafts"]!;
            Assert.True(pending["pending"]!.GetValue<bool>());
            doc = Document(await Call("resolve_drafts", new() { ["document"] = doc.SessionId.ToString(), ["target"] = "section", ["token"] = pending["token"]!.GetValue<string>(), ["action"] = "apply" }));
            Assert.Equal("Upper deck", ZoneLabel(7));
            var applied = (await Call("drafts", new() { ["target"] = "section" }))["drafts"]!;
            Assert.False(applied["pending"]!.GetValue<bool>()); Assert.Equal("Upper deck", applied["label"]!.GetValue<string>());
            string appliedPreview = ((Guid)typeof(MainWindow).GetField("previewId", fields)!.GetValue(main)!).ToString();
            Assert.Equal("zone", (await Call("preview_state", new() { ["preview"] = appliedPreview }))["sections"]!["selection"]!["kind"]!.GetValue<string>());
            doc = Document(await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" }));
            Assert.Equal("Upper platform", ZoneLabel(7));

            // A dependency edit stales the build's exact identities until the world is reloaded.
            node = doc.SourceBuild!.Provenance.First(p => p.Value.ModelNodeName == "flat_a").Key;
            byte[] dependency = doc.SourceWorld!.Workspace.Read("gamegen/m2.gs", token)!;
            doc.SourceWorld.Workspace.Apply("Changed script dependency", [("gamegen/m2.gs", (byte[]?)[.. dependency, (byte)'\n'])], token);
            Assert.Contains("gamegen/m2.gs", doc.SourceBuild!.Dependencies);
            Assert.Equal("stale_document", (await Job("source_polygon_zones", Paint("paint", 6, (node, 0)), "failed"))["code"]!.GetValue<string>());
            await Call("source_zones", new() { ["document"] = doc.SessionId.ToString(), ["targets"] = new[] { new { node, polygon = 0 } } }, "stale_document");

            string? ZoneLabel(byte zone) => SourceZoneEdits.Catalog(doc.SourceWorld!.Workspace, "m1", token).Labels.SingleOrDefault(l => l.Id == zone)?.Label;
            byte[] Zones(int scene) => SourceZoneEdits.PolygonZoneIds(Assert.Single(SourceZoneEdits.Inspect(doc.SourceWorld!.Workspace, doc.SourceBuild!, [new(scene, 0)], token: token).Targets).Word)!;
            Dictionary<string, object?> Paint(string action, int zone, params (int Node, int Polygon)[] targets) => new()
            {
                ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = action, ["zone"] = zone,
                ["targets"] = targets.Select(t => new { node = t.Node, polygon = t.Polygon }).ToArray(),
            };
            DocumentModel Document(JsonNode value) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == value["id"]!.GetValue<string>());
            async Task<JsonNode> Call(string name, Dictionary<string, object?> args, string? error = null)
            {
                var result = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True((result.IsError == true) == (error != null), text);
                var data = JsonNode.Parse(text)!;
                if (error != null) Assert.Equal(error, data["code"]!.GetValue<string>());
                return data;
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
