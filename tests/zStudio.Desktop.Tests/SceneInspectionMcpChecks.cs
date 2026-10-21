using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Desktop;
using zStudio.Mcp;
using zStudio.Rendering;
using Xunit;
using HelixToolkit;
using HelixToolkit.Wpf.SharpDX;

namespace zStudio.Desktop.Tests;

internal static class SceneInspectionMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string root = Path.Combine(Path.GetTempPath(), "zstudio-inspection-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string archivePath = Path.Combine(root, "resources.zbd");
        var value = A(S("node_00"), A(I(12), A(F(1), F(2), F(3)), A(I(-7), I(-1), I(0))),
            S("node_01"), A(I(13), A(F(5), F(2), F(7)), A(I(0), I(-1), I(-1))), S("attack_strategy"), A(S("Head-on")));
        var pickupRow = A(S("HEMORTAR_AMMO"), I(1), A(F(1), F(2), F(3)), A(F(.125f), F(.3f), F(-.2f)), F(12.5f));
        var members = new[] { ("net_01.zrd", ZrdWriter.Write(value)), ("puppies.zrd", ZrdWriter.Write(A(A(pickupRow)))) };
        using (var data = new MemoryStream())
        {
            using var writer = new BinaryWriter(data, Encoding.Latin1, true);
            foreach (var member in members) writer.Write(member.Item2);
            int offset = 0;
            foreach (var (name, bytes) in members)
            {
                writer.Write(offset); writer.Write(bytes.Length); offset += bytes.Length;
                byte[] entry = new byte[140]; Encoding.Latin1.GetBytes(name).CopyTo(entry, 0); writer.Write(entry);
            }
            writer.Write(1); writer.Write(members.Length); File.WriteAllBytes(archivePath, data.ToArray());
        }
        byte[] original = File.ReadAllBytes(archivePath);
        using var resolver = new AssetResolver(root);
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        using var viewport = new SceneViewport();
        var world = new AssetDocument(Path.Combine(root, "gamez.zbd"), new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
        world.Scene.Nodes.Add(new(0, "world", "world", null, [], [], new(), new()));
        world.Scene.Nodes.Add(new(1, "pickup", "object3d", null, [], [2, 3], new(), new() { ["flags"] = 8 }));
        world.Scene.Nodes.Add(new(2, "pickup child", "object3d", null, [1], [], new(), new() { ["flags"] = 8 }));
        world.Scene.Nodes.Add(new(3, "pickup second child", "object3d", null, [1], [], new(), new() { ["flags"] = 8 }));
        var asset = world.Add(AssetKind.World, 0, "Whole world", 0, 0);
        using var doc = new DocumentModel(world); main.ViewModel.Documents.Add(doc);
        typeof(MainViewModel).GetField("selectedDocument", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main.ViewModel, doc);
        var edits = await doc.GetPickupEditsAsync(resolver, deadline.Token);
        var archive = await resolver.OpenCachedAsync(archivePath, deadline.Token);
        var graph = MissionAiNetworks.Read(archive.Assets.Select(a => (archive, a)));
        var record = Assert.Single(edits.Records);
        var pickup = new MissionPickup(1, record.Type, 1, 1, record.OriginalPosition, record.Rotation, 12.5f, record.Source);
        var mission = new MissionSceneContext(world.Scene, [0, 1, 2, 3], [new(1, 1, "pickup", "fixture", pickup)], [], [], MissionLayoutSelection.For(MissionDifficulty.Medium), 4);
        typeof(MissionSceneContext).GetProperty("AiNetworks")!.SetValue(mission, graph);
        typeof(SceneViewport).GetProperty("Mission")!.SetValue(viewport, mission);
        typeof(SceneViewport).GetProperty(nameof(SceneViewport.PreviewScene))!.SetValue(viewport, mission.Scene);
        var baseTransform = Matrix4x4.CreateFromQuaternion(PlacementTransform.Orientation(PlacementRotationKind.EulerRadians, record.Rotation)) * Matrix4x4.CreateTranslation(record.OriginalPosition);
        var parentTransform = Matrix4x4.CreateScale(-1.5f, 2, 1) * Matrix4x4.CreateRotationZ(.4f);
        world.Scene.Nodes[0] = world.Scene.Nodes[0] with { Class = "object3d", Children = [1], Data = TransformData(parentTransform) };
        world.Scene.Nodes[1] = world.Scene.Nodes[1] with { Parents = [0], Data = TransformData(baseTransform) };
        var firstLocal = Matrix4x4.CreateScale(-2, 3, 4); var secondLocal = Matrix4x4.CreateTranslation(8, 0, 0);
        world.Scene.Nodes[2] = world.Scene.Nodes[2] with { Data = TransformData(firstLocal) };
        world.Scene.Nodes[3] = world.Scene.Nodes[3] with { Data = TransformData(secondLocal) };
        var ownerTransform = baseTransform * parentTransform;
        ScenePlacement[] instances = [new(2, -1, "first", firstLocal * ownerTransform), new(3, -1, "second", secondLocal * ownerTransform)];
        var mesh = new MeshGeometryModel3D { Geometry = new HelixToolkit.SharpDX.MeshGeometry3D { Positions = new Vector3Collection([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]), Indices = new IntCollection([0, 1, 2]) }, Instances = instances.Select(p => p.Transform).ToArray() };
        ((List<MeshGeometryModel3D>)typeof(SceneViewport).GetField("meshes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!).Add(mesh);
        ((Dictionary<HelixToolkit.SharpDX.MeshGeometry3D, (Vector3 Min, Vector3 Max)>)typeof(SceneViewport).GetField("staticMeshBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!).Add((HelixToolkit.SharpDX.MeshGeometry3D)mesh.Geometry!, (Vector3.Zero, new(1, 1, 0)));
        foreach (string key in new[] { "placements", "visiblePlacements" })
            ((Dictionary<MeshGeometryModel3D, ScenePlacement[]>)typeof(SceneViewport).GetField(key, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!).Add(mesh, instances);
        typeof(SceneViewport).GetMethod("RegisterInspectionMesh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewport, [mesh, -1, null, -1, -1]);
        typeof(SceneViewport).GetMethod("ConfigurePickups", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewport, []);
        viewport.RestoreView(new(new(0, 0, 20), new(0, 0, -20), new(0, 1, 0), 60));
        viewport.SetAiNetworks(graph); viewport.SetAiOptions(true, true, null);
        Set("shownDocument", doc); Set("shownAsset", asset); Set("scene", viewport);
        ((ContentControl)main.FindName("SceneHost")).Content = viewport; ((ContentControl)main.FindName("SceneHost")).Visibility = Visibility.Visible;
        ((FrameworkElement)main.FindName("EmptyPreview")).Visibility = Visibility.Collapsed;
        Invoke("ConfigureAiScene", viewport); Invoke("AttachPickupEditor", doc);
        string preview = ((Guid)Get("previewId")!).ToString(); string target = "ai:" + graph.Id + ":" + graph.Networks[0].Nodes[0].Id;
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            // A compiled world is read only: its AI nodes and placements are selected and inspected, never moved.
            string aiId = graph.Networks[0].Nodes[0].Id;
            await Call("ai_selection", new() { ["preview"] = preview, ["snapshot"] = graph.Id, ["action"] = "properties", ["node"] = aiId });
            Assert.Equal(aiId, main.OpenPropertiesWindow!.CurrentJson!["node_id"]!.GetValue<string>());
            main.OpenPropertiesWindow.Close();
            await Call("ai_selection", new() { ["preview"] = preview, ["snapshot"] = graph.Id, ["action"] = "select", ["node"] = aiId });
            Assert.Equal(aiId, viewport.SelectedAiNode); Assert.NotNull(viewport.SelectedInspection);
            await Card("select", new() { ["target"] = target });
            Assert.True(viewport.SelectionBoundsVisible); Assert.False(viewport.TransformHandlesVisible);
            var inspectionCard = (SceneInspectionCard)viewport.InspectionContent!;
            inspectionCard.Measure(new(800, 600)); inspectionCard.Arrange(new Rect(0, 0, 800, 600)); inspectionCard.UpdateLayout();
            var panel = (Border)typeof(SceneInspectionCard).GetField("panel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspectionCard)!;
            var detailPanel = (Border)typeof(SceneInspectionCard).GetField("card", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspectionCard)!;
            var strategyReadout = Fields(inspectionCard).Single(f => f.Inputs.Any(i => AutomationProperties.GetName(i) == "Attack strategy"));
            Assert.Equal(Visibility.Visible, strategyReadout.Visibility);
            var detailRows = (StackPanel)strategyReadout.Parent;
            var statusReadout = Fields(inspectionCard).Single(f => f.Inputs.Any(i => AutomationProperties.GetName(i) == "Status"));
            Assert.Equal(detailRows.Children.IndexOf(strategyReadout) + 1, detailRows.Children.IndexOf(statusReadout));
            Assert.Equal(Visibility.Visible, panel.Visibility); Assert.Equal(Visibility.Visible, detailPanel.Visibility);
            var hovered = viewport.InspectTarget(target)!;
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.HoverInspection))!.SetValue(viewport, hovered);
            await Card("clear", new());
            var hoverRead = await Call("scene_inspect", new() { ["preview"] = preview });
            Assert.Null(hoverRead["selected"]); Assert.NotNull(hoverRead["hover"]);
            Assert.Equal(Visibility.Visible, panel.Visibility); Assert.Equal(Visibility.Collapsed, detailPanel.Visibility);
            Assert.False(strategyReadout.IsVisible);
            await Card("select", new() { ["target"] = target });
            Assert.Equal(Visibility.Visible, detailPanel.Visibility);
            Assert.Same(hovered, viewport.HoverInspection);
            await Card("select", new() { ["target"] = target, ["node"] = 0 }, "invalid_argument");
            var inspected = await Call("scene_inspect", new() { ["preview"] = preview });
            Assert.False(inspected["inspection"]!["Editable"]!.GetValue<bool>());
            Assert.All(Fields(inspectionCard).SelectMany(f => f.Inputs), input => Assert.True(input.IsReadOnly));
            await Edit("begin", error: "read_only");
            await PathAction("enter", new() { ["network"] = viewport.AiNetworks.Networks[0].Id }, "read_only");
            var copy = await Card("copy", new() { ["field"] = "Authored placement XYZ" }); Assert.Contains("1", copy["text"]!.GetValue<string>());
            Assert.Equal("Attack strategy: Head-on", (await Card("copy", new() { ["field"] = "Attack strategy" }))["text"]!.GetValue<string>());
            Assert.Single((await Card("copy", new()))["text"]!.GetValue<string>().Split(Environment.NewLine), line => line.StartsWith("Attack strategy: "));
            long revision = doc.Revision;
            var cameraBeforeResize = viewport.CaptureView();
            var resizeGrip = (Thumb)typeof(SceneInspectionCard).GetField("resizeGrip", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspectionCard)!;
            foreach (double height in new[] { 620d, 1d, 432d })
            {
                // Exercise gesture arbitration without capturing the physical pointer.
                resizeGrip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                resizeGrip.RaiseEvent(new DragDeltaEventArgs(0, -10) { RoutedEvent = Thumb.DragDeltaEvent });
                await Call("workspace_view", new() { ["changes"] = new JsonObject { ["inspectionPanelHeight"] = height } });
                resizeGrip.RaiseEvent(new DragDeltaEventArgs(0, -30) { RoutedEvent = Thumb.DragDeltaEvent });
                resizeGrip.RaiseEvent(new DragCompletedEventArgs(0, -40, true) { RoutedEvent = Thumb.DragCompletedEvent });
                var resized = await Call("scene_inspect", new() { ["preview"] = preview });
                Assert.Equal(Math.Max(216, height), resized["panel"]!["preferredHeight"]!.GetValue<double>());
                Assert.True(resized["panel"]!["expanded"]!.GetValue<bool>());
                Assert.True(resized["panel"]!["effectiveHeight"]!.GetValue<double>() <= resized["panel"]!["maximumHeight"]!.GetValue<double>());
                Assert.Equal(revision, doc.Revision); Assert.False(doc.IsDirty);
                Assert.Equal(cameraBeforeResize, viewport.CaptureView());
                Assert.Equal(Math.Max(216, height), StudioSettings.Load().GetWorkspace().InspectionPanelHeight);
            }
            Assert.Equal(copy["text"]!.GetValue<string>(), (await Card("copy", new() { ["field"] = "Authored placement XYZ" }))["text"]!.GetValue<string>());
            await Card("select", new() { ["node"] = 2 });
            Assert.DoesNotContain(strategyReadout, Fields(inspectionCard));
            Assert.Null((await Call("scene_inspect", new() { ["preview"] = preview }))["attackStrategy"]);
            Assert.True(viewport.SelectionBoundsVisible); Assert.False(viewport.TransformHandlesVisible);
            var box = (LineGeometryModel3D)typeof(SceneViewport).GetField("selectionBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;
            AssertSelectionBox(ownerTransform, new(-2, 0, 0), new(9, 3, 0));
            // The same two parts can belong to a reconstructed synthetic owner.
            // Its common physical ancestor supplies the frame without inventing a node.
            var selected = viewport.SelectedInspection!;
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.SelectedInspection))!.SetValue(viewport, selected with { OwnerNode = -2 });
            viewport.SelectionOwner = n => n is 2 or 3 ? -2 : n;
            RefreshBox(); AssertSelectionBox(ownerTransform, new(-2, 0, 0), new(9, 3, 0));
            var visible = (Dictionary<MeshGeometryModel3D, ScenePlacement[]>)typeof(SceneViewport).GetField("visiblePlacements", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;
            visible[mesh] = [instances[0]]; mesh.Instances = [instances[0].Transform];
            RefreshBox(); AssertSelectionBox(ownerTransform, new(-2, 0, 0), new(0, 3, 0));
            visible[mesh] = instances; mesh.Instances = instances.Select(p => p.Transform).ToArray();
            var secondNode = world.Scene.Nodes[3]; world.Scene.Nodes[3] = secondNode with { Parents = [] };
            RefreshBox();
            var fallbackEdge = box.Geometry!.Positions![1] - box.Geometry.Positions[0];
            Assert.True(fallbackEdge.X > 0); Assert.Equal(0, fallbackEdge.Y); Assert.Equal(0, fallbackEdge.Z);
            world.Scene.Nodes[3] = secondNode; viewport.SelectionOwner = null;
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.SelectedInspection))!.SetValue(viewport, selected);
            RefreshBox();
            await Card("clear", new());
            Assert.Null(viewport.SelectedInspection);

            await Call("workspace_view", new() { ["changes"] = new JsonObject { ["inspectionPanelHeight"] = 620 } });
            using (var nextViewport = new SceneViewport())
            {
                Invoke("AttachInspection", nextViewport);
                var nextPanel = System.Text.Json.JsonSerializer.SerializeToNode(((SceneInspectionCard)nextViewport.InspectionContent!).DescribePanel())!;
                Assert.Equal(620, nextPanel["preferredHeight"]!.GetValue<double>());
                Assert.False(nextPanel["expanded"]!.GetValue<bool>());
            }
            resizeGrip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            await Call("workspace_view", new() { ["changes"] = new JsonObject { ["resetLayout"] = true } });
            resizeGrip.RaiseEvent(new DragCompletedEventArgs(0, 0, true) { RoutedEvent = Thumb.DragCompletedEvent });
            var resetPanel = (await Call("scene_inspect", new() { ["preview"] = preview }))["panel"]!;
            Assert.Equal(432, resetPanel["preferredHeight"]!.GetValue<double>());
            Assert.False(resetPanel["expanded"]!.GetValue<bool>());
            Assert.False(doc.IsDirty); Assert.Equal(original, File.ReadAllBytes(archivePath));

            async Task<JsonNode> PathAction(string action, JsonObject? args = null, string? error = null)
            {
                args ??= new(); args["preview"] = preview; args["snapshot"] = viewport.AiNetworks.Id;
                args["document"] = doc.SessionId.ToString(); args["revision"] = doc.Revision; args["action"] = action;
                var job = await Call("ai_path", args); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running")
                { await Task.Delay(5, deadline.Token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.Equal(error == null ? "completed" : "failed", job["State"]!.GetValue<string>());
                var result = job["result"]!; if (error != null) Assert.Equal(error, result["code"]!.GetValue<string>());
                return result;
            }

            void RefreshBox() => typeof(SceneViewport).GetMethod("RefreshPickupSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewport, []);
            void AssertSelectionBox(Matrix4x4 frame, Vector3 min, Vector3 max)
            {
                Vector3[] expected = [new(min.X,min.Y,min.Z), new(max.X,min.Y,min.Z), new(max.X,max.Y,min.Z), new(min.X,max.Y,min.Z),
                    new(min.X,min.Y,max.Z), new(max.X,min.Y,max.Z), new(max.X,max.Y,max.Z), new(min.X,max.Y,max.Z)];
                var actual = box.Geometry!.Positions!;
                Assert.Equal(8, actual.Count);
                for (int i = 0; i < expected.Length; i++)
                    Assert.True(Vector3.Distance(Vector3.Transform(expected[i], frame), actual[i]) < .0001f,
                        $"Selection corner {i} does not follow the object's frame.");
            }

            Task<JsonNode> Card(string action, JsonObject args, string? error = null) { args["preview"] = preview; args["action"] = action; return Call("scene_card", args, error); }
            Task<JsonNode> Edit(string action, JsonObject? args = null, string? error = null)
            { args ??= new(); args["document"] = doc.SessionId.ToString(); args["revision"] = doc.Revision; return Card(action, args, error); }
            async Task<JsonNode> Call(string name, JsonObject args, string? error = null)
            {
                var response = await client.CallToolAsync("zstudio_" + name, args.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: deadline.Token);
                var json = JsonNode.Parse(response.Content.OfType<TextContentBlock>().Single().Text)!;
                if (error == null) Assert.False(response.IsError == true, json.ToJsonString()); else Assert.Equal(error, json["code"]!.GetValue<string>());
                return json;
            }
        }
        finally { Invoke("CancelAiPathDraft"); main.OpenPropertiesWindow?.Close(); (viewport.InspectionContent as SceneInspectionCard)?.CancelDraft(); Set("scene", null); main.Close(); Directory.Delete(root, true); }
        object? Get(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);
        void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, args);
    }
    private static ZrdNode A(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
    private static JsonObject TransformData(Matrix4x4 m) => new()
    {
        ["flags"] = 0,
        ["transform"] = new JsonArray(m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43)
    };
    private static IEnumerable<SceneInspectionField> Fields(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is SceneInspectionField field) yield return field;
            foreach (var nested in Fields(child)) yield return nested;
        }
    }
    private static ZrdNode S(string value) => ZrdNode.Create(ZrdKind.String) with { Text = value };
    private static ZrdNode I(int value) => ZrdNode.Create(ZrdKind.Int, value.ToString());
    private static ZrdNode F(float value) => ZrdNode.Create(ZrdKind.Float, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
