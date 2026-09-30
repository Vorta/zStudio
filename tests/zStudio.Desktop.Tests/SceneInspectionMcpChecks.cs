using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Threading;
using System.Windows.Media;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Rendering;
using Xunit;
using HelixToolkit;
using HelixToolkit.Wpf.SharpDX;

namespace Recoil.Zbd.Desktop.Tests;

internal static class SceneInspectionMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string root = Path.Combine(Path.GetTempPath(), "zstudio-inspection-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string archivePath = Path.Combine(root, "resources.zbd");
        var value = A(S("node_00"), A(I(12), A(F(1), F(2), F(3)), A(I(-7), I(-1), I(0))), S("attack_strategy"), A(S("Head-on")));
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
        var world = new ZbdDocument(Path.Combine(root, "gamez.zbd"), new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
        world.Scene.Nodes.Add(new(0, "world", "world", null, [], [], new(), new()));
        world.Scene.Nodes.Add(new(1, "pickup", "object3d", null, [], [2, 3], new(), new() { ["flags"] = 8 }));
        world.Scene.Nodes.Add(new(2, "pickup child", "object3d", null, [1], [], new(), new() { ["flags"] = 8 }));
        world.Scene.Nodes.Add(new(3, "pickup second child", "object3d", null, [1], [], new(), new() { ["flags"] = 8 }));
        var asset = world.Add(AssetKind.World, 0, "Whole world", 0, 0);
        // Real GameZ documents also have a model-edit session. Coordinate undo must
        // not take its model-replacement refresh path or expire the preview.
        typeof(ZbdDocument).GetProperty(nameof(ZbdDocument.GameZLayout))!.SetValue(world, new GameZSourceLayout(0, 0, 0, 0, 0, 0, 0, []));
        using var doc = new DocumentModel(world); doc.AttachResolver(resolver); main.ViewModel.Documents.Add(doc);
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
        ScenePlacement[] instances = [new(2, -1, "first", Matrix4x4.CreateScale(2, 3, 4) * baseTransform), new(3, -1, "second", Matrix4x4.CreateTranslation(8, 0, 0) * baseTransform)];
        var mesh = new MeshGeometryModel3D { Geometry = new HelixToolkit.SharpDX.MeshGeometry3D { Positions = new Vector3Collection([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]), Indices = new IntCollection([0, 1, 2]) }, Instances = instances.Select(p => p.Transform).ToArray() };
        ((List<MeshGeometryModel3D>)typeof(SceneViewport).GetField("meshes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!).Add(mesh);
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
            await Card("select", new() { ["target"] = target }, "locked");
            Assert.Null(viewport.SelectedInspection);
            string aiId = graph.Networks[0].Nodes[0].Id;
            Assert.False(viewport.SelectAiNode(aiId)); Assert.Null(viewport.SelectedAiNode);
            await Call("ai_selection", new() { ["preview"] = preview, ["snapshot"] = graph.Id, ["action"] = "select", ["node"] = aiId }, "locked");
            Assert.Null(viewport.SelectedAiNode); Assert.Null(viewport.SelectedInspection);
            Assert.False(viewport.SelectionBoundsVisible); Assert.False(viewport.TransformHandlesVisible);
            await Call("ai_selection", new() { ["preview"] = preview, ["snapshot"] = graph.Id, ["action"] = "properties", ["node"] = aiId });
            Assert.Equal(aiId, main.OpenPropertiesWindow!.CurrentJson!["node_id"]!.GetValue<string>());
            main.OpenPropertiesWindow.Close(); Assert.Null(viewport.SelectedAiNode);
            await Lock(false);
            Assert.Null(viewport.SelectedInspection); Assert.Null(viewport.SelectedAiNode); // Unlock alone never selects.
            Assert.False(viewport.SelectionBoundsVisible);
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
            var moveMode = (ToggleButton)typeof(SceneInspectionCard).GetField("moveMode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspectionCard)!;
            var rotateMode = (ToggleButton)typeof(SceneInspectionCard).GetField("rotateMode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspectionCard)!;
            Assert.Equal(Visibility.Visible, panel.Visibility); Assert.Equal(Visibility.Visible, detailPanel.Visibility);
            Assert.Equal(Visibility.Hidden, ((StackPanel)moveMode.Parent).Visibility);
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
            var inlinePosition = Fields(inspectionCard).Single(f => f.Binding == SceneInspectionBinding.AuthoredPosition);
            Assert.All(inlinePosition.Inputs, input => Assert.True(input.IsReadOnly));
            await Card("select", new() { ["target"] = target, ["node"] = 0 }, "invalid_argument");
            var inspected = await Call("scene_inspect", new() { ["preview"] = preview });
            Assert.True(inspected["inspection"]!["Editable"]!.GetValue<bool>());
            var unlock = (ToggleButton)main.FindName("EditingUnlocked");
            await Lock(true); Assert.Null(viewport.SelectedInspection);
            Assert.False(unlock.IsChecked); Assert.True(doc.PickupsLocked);
            Assert.Equal("Unlock editing", AutomationProperties.GetName(unlock));
            Assert.Equal("Unlock", ((PreviewIcon)unlock.Content).Kind);
            await Edit("begin", error: "locked");
            await Lock(false);
            await Card("select", new() { ["target"] = target });
            Assert.True(unlock.IsChecked); Assert.True(((PreviewIcon)unlock.Content).IsChecked);
            Assert.True((await Call("scene_inspect", new() { ["preview"] = preview }))["inspection"]!["Editable"]!.GetValue<bool>());
            unlock.IsChecked = false; Assert.True(doc.PickupsLocked);
            await Edit("begin", error: "locked");
            unlock.IsChecked = true; Assert.False(doc.PickupsLocked);
            await Card("select", new() { ["target"] = target });
            var copy = await Card("copy", new() { ["field"] = "Authored placement XYZ" }); Assert.Contains("1", copy["text"]!.GetValue<string>());
            var tree = await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString() });
            var begun = await Edit("begin"); string token = begun["draft"]!["token"]!.GetValue<string>();
            Assert.True(viewport.TransformHandlesVisible);
            Assert.Equal(Visibility.Visible, ((StackPanel)moveMode.Parent).Visibility);
            Assert.True(moveMode.IsEnabled); Assert.False(rotateMode.IsEnabled);
            Assert.Equal(Visibility.Hidden, rotateMode.Visibility);
            Assert.Same(inlinePosition, Fields(inspectionCard).Single(f => f.Binding == SceneInspectionBinding.AuthoredPosition));
            Assert.All(inlinePosition.Inputs, input => Assert.False(input.IsReadOnly));
            Assert.All(Fields(inspectionCard).Where(f => f.Binding == SceneInspectionBinding.None).SelectMany(f => f.Inputs), input => Assert.True(input.IsReadOnly));
            var strategyField = Fields(inspectionCard).Single(f => f.Inputs.Any(i => AutomationProperties.GetName(i) == "Attack strategy"));
            Assert.True(strategyField.Inputs[0].IsReadOnly); Assert.Equal("Head-on", strategyField.Inputs[0].Text);
            Assert.Same(strategyReadout, strategyField); Assert.Equal(Visibility.Visible, strategyField.Visibility);
            Assert.Equal("Attack strategy: Head-on", (await Card("copy", new() { ["field"] = "Attack strategy" }))["text"]!.GetValue<string>());
            Assert.Single((await Card("copy", new()))["text"]!.GetValue<string>().Split(Environment.NewLine), line => line.StartsWith("Attack strategy: "));
            await Edit("cancel", new() { ["token"] = token });
            Assert.False(viewport.TransformHandlesVisible); Assert.True(viewport.SelectionBoundsVisible);
            Assert.Equal(Visibility.Hidden, ((StackPanel)moveMode.Parent).Visibility);
            Assert.All(inlinePosition.Inputs, input => Assert.True(input.IsReadOnly));
            var restarted = await Edit("begin");
            Assert.NotEqual(token, restarted["draft"]!["token"]!.GetValue<string>());
            await Edit("cancel", new() { ["token"] = token }, "draft_conflict");
            token = restarted["draft"]!["token"]!.GetValue<string>();
            await Lock(true, "pending_drafts");
            Assert.False(doc.PickupsLocked); Assert.True(unlock.IsChecked);
            Assert.Equal(token, ((SceneInspectionCard)viewport.InspectionContent!).DraftToken);
            // Answer only this fixture thread's native XYZ prompt, without
            // moving/capturing the physical mouse or synthesizing keyboard input.
            bool canceled = false;
            var answer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            answer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (handle, _) =>
            {
                var title = new StringBuilder(256); GetWindowText(handle, title, title.Capacity);
                if (title.ToString() != "Node transform draft") return true;
                canceled = true; SendMessage(handle, 0x0111, 2, 0); return false; // WM_COMMAND, IDCANCEL
            }, 0);
            answer.Start();
            try { unlock.IsChecked = false; }
            finally { answer.Stop(); }
            Assert.True(canceled); Assert.False(doc.PickupsLocked); Assert.True(unlock.IsChecked);
            Assert.Equal(token, ((SceneInspectionCard)viewport.InspectionContent!).DraftToken);
            // Applying rechecks the lock even if it changed outside the GUI/MCP guard.
            doc.PickupsLocked = true;
            await Edit("apply", new() { ["token"] = token }, "locked");
            Assert.False(doc.IsDirty); doc.PickupsLocked = false;
            JsonObject TreeArgs(string action) => new() { ["document"] = doc.SessionId.ToString(), ["context"] = tree["context"]!.GetValue<string>(),
                ["preview"] = preview, ["action"] = action, ["node"] = 0 };
            await Call("scene_tree", TreeArgs("select"), "pending_drafts");
            await Call("scene_tree", TreeArgs("properties"), "pending_drafts");
            await Call("scene_tree", TreeArgs("reveal")); // Presentation must neither commit nor reject the draft.
            Assert.True((viewport.InspectionContent as SceneInspectionCard)!.HasDraft); Assert.False(doc.IsDirty);
            await Card("clear", new(), "pending_drafts");
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" }, "pending_drafts");
            await Call("close_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["discard"] = true }, "pending_drafts");
            // A resource edit elsewhere refreshes this preview. Its draft needs an explicit decision, never a modal prompt mid-request.
            string otherPath = Path.Combine(root, "other.zbd"); File.WriteAllBytes(otherPath, MotionFixture.Archive(("data.zrd", ZrdWriter.Write(A(S("value"))))));
            using (var other = new DocumentModel(await FormatRegistry.Default.OpenAsync(otherPath, deadline.Token)))
            {
                main.ViewModel.Documents.Add(other);
                try
                {
                    var rename = await Call("archive_edit", new() { ["document"] = other.SessionId.ToString(), ["revision"] = other.Revision, ["action"] = "rename",
                        ["member"] = other.ResourceEdits!.Current.Members[0].Id.ToString(), ["name"] = "renamed.zrd" });
                    for (string id = rename["id"]!.GetValue<string>(); rename["State"]!.GetValue<string>() is "queued" or "running";)
                    { await Task.Delay(10, deadline.Token); rename = await Call("operation", new() { ["id"] = id }); }
                    Assert.True(rename["State"]!.GetValue<string>() == "failed", rename.ToJsonString());
                    Assert.Equal("pending_drafts", rename["result"]!["code"]!.GetValue<string>());
                    Assert.Equal(0, other.Revision); Assert.False(other.IsDirty); Assert.True((viewport.InspectionContent as SceneInspectionCard)!.HasDraft);
                }
                finally { main.ViewModel.CloseResolved(other); }
            }
            await Edit("set", new() { ["token"] = token, ["position"] = new JsonArray(new string('1', 65), "6", "7") }, "invalid_argument");
            var outOfRange = await Edit("set", new() { ["token"] = token, ["position"] = new JsonArray("2000000000000", "2", "3") });
            token = outOfRange["draft"]!["token"]!.GetValue<string>();
            var confirm = (Button)typeof(SceneInspectionCard).GetField("edit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspectionCard)!;
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(inspectionCard.HasDraft); Assert.False(doc.IsDirty);
            var validation = (TextBlock)typeof(SceneInspectionCard).GetField("error", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspectionCard)!;
            Assert.Equal(Visibility.Visible, validation.Visibility); Assert.Contains("preview range", validation.Text);
            await Edit("set", new() { ["token"] = token, ["transformMode"] = "rotate" }, "read_only");
            await Edit("set", new() { ["token"] = token, ["headingDegrees"] = "90" }, "read_only");
            await Edit("set", new() { ["token"] = "old", ["position"] = new JsonArray("5", "6", "7") }, "draft_conflict");
            var partial = await Edit("set", new() { ["token"] = token, ["position"] = new JsonArray("-", "6", "7") });
            token = partial["draft"]!["token"]!.GetValue<string>();
            Assert.False(viewport.TransformHandlesVisible); Assert.True(viewport.SelectionBoundsVisible);
            inspectionCard.Refresh();
            Assert.Equal(new[] { "-", "6", "7" }, inlinePosition.Inputs.Select(input => input.Text).ToArray());
            long resizeRevision = doc.Revision;
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
                Assert.Equal(token, inspectionCard.DraftToken); Assert.Equal(resizeRevision, doc.Revision); Assert.False(doc.IsDirty);
                Assert.Equal(new[] { "-", "6", "7" }, inlinePosition.Inputs.Select(input => input.Text).ToArray());
                Assert.Equal(cameraBeforeResize, viewport.CaptureView());
                Assert.Equal(Math.Max(216, height), StudioSettings.Load().GetWorkspace().InspectionPanelHeight);
            }
            Assert.Equal(copy["text"]!.GetValue<string>(), (await Card("copy", new() { ["field"] = "Authored placement XYZ" }))["text"]!.GetValue<string>());
            await Edit("apply", new() { ["token"] = token }, "invalid_argument"); Assert.False(doc.IsDirty);
            var valid = await Edit("set", new() { ["token"] = token, ["position"] = new JsonArray("5.125", "6", "7") });
            token = valid["draft"]!["token"]!.GetValue<string>();
            Assert.False(doc.IsDirty); Assert.Equal(new Vector3(1, 2, 3), edits.Position(edits.OtherCoordinates[0].Source));
            Assert.Equal(new Vector3(5.125f, 6, 7), viewport.AiNetworks.Networks[0].Nodes[0].Position);
            Assert.Equal(graph.Id, viewport.AiNetworks.Id); // Drafts do not expire source identities.
            await Edit("apply", new() { ["token"] = token });
            Assert.All(inlinePosition.Inputs, input => Assert.True(input.IsReadOnly));
            Assert.Equal(new[] { "5.125", "6", "7" }, inlinePosition.Inputs.Select(input => input.Text).ToArray());
            Assert.Null(Get("inspectionDraft")); // Finished drafts must not retain a replaced viewport.
            Assert.True(doc.IsDirty); Assert.Equal(new Vector3(5.125f, 6, 7), edits.Position(edits.OtherCoordinates[0].Source));
            Assert.Equal(original, File.ReadAllBytes(archivePath));
            Assert.Equal(new Vector3(5.125f, 6, 7), viewport.AiNetworks.Networks[0].Nodes[0].Position);
            await Lock(true); Assert.False(unlock.IsChecked);
            await Edit("begin", error: "locked");
            await Call("scene_inspect", new() { ["preview"] = preview, ["target"] = target }, "stale_record");
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" });
            Assert.Equal(preview, ((Guid)Get("previewId")!).ToString());
            Assert.False(doc.IsDirty); Assert.Equal(new Vector3(1, 2, 3), viewport.AiNetworks.Networks[0].Nodes[0].Position);
            await Lock(false);
            await Card("select", new() { ["target"] = target });
            begun = await Edit("begin"); token = begun["draft"]!["token"]!.GetValue<string>();
            File.WriteAllBytes(archivePath, [.. original, 1]);
            await Edit("apply", new() { ["token"] = token }, "external_change");
            Assert.True((viewport.InspectionContent as SceneInspectionCard)!.HasDraft);
            await Edit("cancel", new() { ["token"] = token });
            File.WriteAllBytes(archivePath, original); File.SetLastWriteTimeUtc(archivePath, archive.Stamp.LastWriteUtc);
            await Card("select", new() { ["node"] = 2 });
            Assert.DoesNotContain(strategyReadout, Fields(inspectionCard));
            Assert.Null((await Call("scene_inspect", new() { ["preview"] = preview }))["attackStrategy"]);
            Assert.True(viewport.SelectionBoundsVisible); Assert.False(viewport.TransformHandlesVisible);
            var box = (LineGeometryModel3D)typeof(SceneViewport).GetField("selectionBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;
            var boxMax = box.Geometry!.Positions!.Aggregate(Vector3.Max);
            Assert.True(boxMax.X > 8); // Both children belong to this instance's bounds.
            var unchanged = await Edit("begin");
            token = unchanged["draft"]!["token"]!.GetValue<string>();
            await Edit("apply", new() { ["token"] = token });
            Assert.False(doc.IsDirty); Assert.Equal(record.Rotation, edits.Rotation(record.Source));
            begun = await Edit("begin"); token = begun["draft"]!["token"]!.GetValue<string>();
            var rotated = await Edit("set", new() { ["token"] = token, ["position"] = new JsonArray("4", "5", "6"), ["rotationDegrees"] = new JsonArray("10", "40", "-20"), ["transformMode"] = "rotate" });
            Assert.True(rotateMode.IsEnabled); Assert.Equal("XYZ", rotated["draft"]!["rotationAxes"]!.GetValue<string>());
            Assert.Equal(Visibility.Visible, rotateMode.Visibility);
            token = rotated["draft"]!["token"]!.GetValue<string>();
            Assert.True(viewport.TransformHandlesVisible); Assert.Equal("rotate", viewport.TransformMode);
            Assert.False(doc.IsDirty); Assert.Equal(record.OriginalPosition, edits.Position(record.Source));
            var pending = inspectionCard.DraftTransform();
            var expectedDelta = pending.DeltaFrom(new(record.OriginalPosition, record.Rotation), PlacementRotationKind.EulerRadians);
            Assert.Equal(instances.Select(p => p.Transform * expectedDelta), mesh.Instances);
            // Exercise the same drag publication/cancel path without capturing the physical mouse.
            typeof(SceneViewport).GetField("transformDragStart", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewport, pending);
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.IsPickupDragging))!.SetValue(viewport, true);
            typeof(SceneViewport).GetMethod("PublishTransformDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewport, [pending.RotateWorld(PlacementRotationKind.EulerRadians, Vector3.UnitY, .2f)]);
            Assert.False(doc.IsDirty); Assert.True(viewport.CancelPickupDrag());
            Assert.True(Vector3.Distance(pending.Rotation, inspectionCard.DraftTransform().Rotation) < 1e-6);
            var draftPositions = mesh.Instances.ToArray();
            token = inspectionCard.DraftToken;
            rotated = await Edit("set", new() { ["token"] = token, ["rotationDegrees"] = new JsonArray("-", "40", "-20") });
            Assert.False(viewport.TransformHandlesVisible); Assert.Equal(draftPositions, mesh.Instances);
            await Edit("apply", new() { ["token"] = rotated["draft"]!["token"]!.GetValue<string>() }, "invalid_argument");
            await Edit("cancel", new() { ["token"] = rotated["draft"]!["token"]!.GetValue<string>() });
            Assert.Equal(instances.Select(p => p.Transform), mesh.Instances); Assert.False(doc.IsDirty);
            begun = await Edit("begin"); token = begun["draft"]!["token"]!.GetValue<string>();
            rotated = await Edit("set", new() { ["token"] = token, ["position"] = new JsonArray("4", "5", "6"), ["rotationDegrees"] = new JsonArray("10", "40", "-20") });
            long revision = doc.Revision;
            await Edit("apply", new() { ["token"] = rotated["draft"]!["token"]!.GetValue<string>() });
            Assert.Equal(revision + 1, doc.Revision); Assert.True(doc.IsDirty);
            Assert.Equal(new Vector3(4, 5, 6), edits.Position(record.Source));
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" });
            Assert.False(doc.IsDirty); Assert.Equal(record.Rotation, edits.Rotation(record.Source));
            Assert.Equal(instances.Select(p => p.Transform), mesh.Instances);
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.IsFlyActive))!.SetValue(viewport, true);
            try { await Lock(true); Assert.Null(viewport.SelectedInspection); Assert.False(viewport.SelectionBoundsVisible); Assert.False(viewport.TransformHandlesVisible); }
            finally { typeof(SceneViewport).GetProperty(nameof(SceneViewport.IsFlyActive))!.SetValue(viewport, false); }
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

            Task<JsonNode> Card(string action, JsonObject args, string? error = null) { args["preview"] = preview; args["action"] = action; return Call("scene_card", args, error); }
            Task<JsonNode> Lock(bool locked, string? error = null) => Call("pickup_lock", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["locked"] = locked }, error);
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
        finally { (viewport.InspectionContent as SceneInspectionCard)?.CancelDraft(); while (edits.CanUndo) edits.Undo(); Set("scene", null); main.Close(); Directory.Delete(root, true); }
        object? Get(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);
        void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, args);
    }
    private static ZrdNode A(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
    private static IEnumerable<SceneInspectionField> Fields(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is SceneInspectionField field) yield return field;
            foreach (var nested in Fields(child)) yield return nested;
        }
    }
    private delegate bool WindowCallback(nint handle, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);
    private static ZrdNode S(string value) => ZrdNode.Create(ZrdKind.String) with { Text = value };
    private static ZrdNode I(int value) => ZrdNode.Create(ZrdKind.Int, value.ToString());
    private static ZrdNode F(float value) => ZrdNode.Create(ZrdKind.Float, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
