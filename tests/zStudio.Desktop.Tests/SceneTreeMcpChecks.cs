using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using zStudio.Mcp;
using zStudio.Rendering;
using Xunit;

namespace zStudio.Desktop.Tests;

internal static class SceneTreeMcpChecks
{
    internal static async Task Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        var data = Scene();
        var source = new AssetDocument("hierarchy-fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = data };
        using var doc = new DocumentModel(source);
        using var viewport = new SceneViewport();
        try
        {
            Set("ready", false); main.ViewModel.Documents.Add(doc); main.ViewModel.SelectedDocument = doc; Set("ready", true);
            Set("shownDocument", doc); Set("scene", viewport);
            typeof(MainWindow).GetMethod("AttachInspection", flags)!.Invoke(main, [viewport]);
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.PreviewScene))!.SetValue(viewport, data);
            ((FrameworkElement)main.FindName("EmptyPreview")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)main.FindName("SceneHost")).Visibility = Visibility.Visible;
            string preview = ((Guid)Get("previewId")!).ToString();
            var camera = viewport.CaptureView();
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            var read = await Call(new());
            string context = read["context"]!.GetValue<string>(), root = read["children"]!["items"]![0]!["row"]!.GetValue<string>();
            Assert.Equal("bound preview scene", read["hierarchy"]!.GetValue<string>());
            var children = await Call(Args("read", root));
            Assert.Equal(2, children["children"]!["total"]!.GetValue<int>());
            Assert.All(children["children"]!["items"]!.AsArray(), row => Assert.Equal(root, row!["parent"]!.GetValue<string>()));
            var selected = await Call(Args("select", node: 1));
            string selectedId = selected["selected"]!.GetValue<string>();
            Assert.Equal(1, viewport.FramingSelection); Assert.Null(viewport.SelectedInspection);
            Assert.Equal(camera, viewport.CaptureView()); Assert.Equal(0, doc.Revision);
            var revealed = await Call(Args("reveal", node: 2));
            Assert.Equal(selectedId, revealed["selected"]!.GetValue<string>()); Assert.Equal(1, viewport.FramingSelection);
            var tree = (TreeView)main.FindName("DocumentSceneTree");
            var rowRoot = Assert.IsType<SceneTreeItem>(tree.Items[0]);
            Assert.True(rowRoot.IsExpanded);
            var invalid = Args("collapse", root); invalid["limit"] = 0;
            await Call(invalid, "invalid_argument"); Assert.True(rowRoot.IsExpanded);
            invalid = Args("select", node: 2); invalid["context"] = "obsolete";
            await Call(invalid, "stale_context"); Assert.Equal(1, viewport.FramingSelection);
            // Use the actual GUI selection path, preserving an independently pinned Properties window.
            var selectedRow = rowRoot.Children[0];
            await Call(Args("properties", selectedId));
            var properties = (PropertiesWindow)Get("propertiesWindow")!;
            var pinned = Assert.IsType<JsonObject>(properties.CurrentJson).DeepClone();
            rowRoot.Children[1].IsSelected = true;
            typeof(MainWindow).GetMethod("SceneTreeSelected", flags)!.Invoke(main, [tree, new RoutedPropertyChangedEventArgs<object>(selectedRow, rowRoot.Children[1])]);
            Assert.Equal(2, viewport.FramingSelection); Assert.True(JsonNode.DeepEquals(pinned, properties.CurrentJson));
            Assert.Equal(camera, viewport.CaptureView()); Assert.Equal(0, doc.Revision);
            // A renderer pick after a tree selection must not leave the old row
            // as the target of an explicit Properties request.
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.SelectedInspection))!.SetValue(viewport,
                new SceneInspection("picked-node-one", 1, -1, -1, 42, null, null, null, null, true, 0));
            typeof(MainWindow).GetMethod("SceneTreeInspectionChanged", flags)!.Invoke(main, [viewport]);
            main.OpenCurrentProperties();
            Assert.Equal(1, properties.CurrentJson!["node_index"]!.GetValue<int>());
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.SelectedInspection))!.SetValue(viewport, null);
            await Call(Args("select", node: 2));
            await Call(Args("properties", node: 1));
            pinned = properties.CurrentJson!.DeepClone();
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.PreviewScene))!.SetValue(viewport, Scene());
            viewport.SelectFramingNode(null); // Renderer replacement resets the old pose target.
            read = await Call(new()); Assert.NotEqual(context, read["context"]!.GetValue<string>());
            Assert.Equal(2, viewport.FramingSelection);
            Assert.NotNull(Get("inspectedSceneSource"));
            await Call(Args("select", root), "stale_context");
            Assert.True(JsonNode.DeepEquals(pinned, properties.CurrentJson));
            Assert.True(viewport.SelectInspection(null, false)); // Explicitly closing the card clears source selection too.
            read = await Call(new()); Assert.Null(read["selected"]); Assert.Null(Get("selectedNode")); Assert.Null(Get("inspectedSceneSource"));
            ((FrameworkElement)main.FindName("EmptyPreview")).Visibility = Visibility.Visible;
            read = await Call(new()); Assert.Equal("stored scene", read["hierarchy"]!.GetValue<string>()); Assert.Null(read["preview"]);
            context = read["context"]!.GetValue<string>();
            await Call(Args("properties", node: 1));
            Assert.Equal("Stored document scene", properties.CurrentJson!["hierarchy"]!.GetValue<string>());
            Assert.False(doc.IsDirty);

            // A deleted external source group has a discoverable owner identity, not a fabricated compiled node.
            ((FrameworkElement)main.FindName("EmptyPreview")).Visibility = Visibility.Collapsed;
            data = viewport.PreviewScene!;
            var reference = new WorldNodeProvenance { ModelFile = "data/m1/models/m1.gltf", ModelNode = 7, ModelNodeName = "region" };
            var ownership = SourceSelectionIndex.Build(new Dictionary<int, WorldNodeProvenance>
            {
                [0] = new(),
                [1] = new() { ModelFile = "data/m1/models/region.gltf", ModelNode = 0, ReferencedBy = reference },
                [2] = new() { ModelFile = "data/m1/models/region.gltf", ModelNode = 1, ReferencedBy = reference },
            }, deadline.Token);
            var associations = new MissionSceneContext(data, [0, 1, 2], [], [], [], MissionLayoutSelection.For(MissionDifficulty.Medium), 3)
            {
                AnimationUses = new Dictionary<int, MissionAnimationUses>
                {
                    [0] = new([new(1, "indirect", "indirect", "Turret destruction, ai.zrd default")], false, false),
                    [1] = new(Enumerable.Range(1, 8).Select(i => new MissionAnimationUse(i, "indirect" + i, "indirect", "Assigned by resource")).ToArray(), false, false),
                    [2] = new([new(9, "direct", "initialRoot", "Initial animation root")], true, false)
                }
            };
            var projection = new SourceMapSelection(data, associations, ownership, i => new("node:" + i, i, null), deadline.Token);
            viewport.SelectionOwner = projection.Owner;
            var associationTree = new SceneTreeModel(data, "fixture", true, new(), projection: projection);
            Set("sceneTree", associationTree);
            Assert.Equal("#FF89B5BC", associationTree.Roots[0].AnimationBrush.ToString());
            Assert.Equal("#FF6FC8D6", associationTree.Roots[0].Children[0].AnimationBrush.ToString());
            Assert.Contains("Receives animation indirectly", associationTree.Roots[0].AnimationsToolTip);
            Assert.Contains("Initial animation root", associationTree.Roots[0].Children[0].AnimationsToolTip);
            read = await Call(new()); context = read["context"]!.GetValue<string>();
            root = read["children"]!["items"]![0]!["row"]!.GetValue<string>();
            children = await Call(Args("read", root));
            var sourceGroup = Assert.Single(children["children"]!["items"]!.AsArray())!;
            Assert.Null(sourceGroup["node"]); Assert.True(sourceGroup["selectionOwner"]!.GetValue<int>() < -1);
            Assert.Equal("reference", sourceGroup["nodeType"]!.GetValue<string>());
            Assert.True(sourceGroup["animationHasInitialRoot"]!.GetValue<bool>());
            Assert.True(sourceGroup["animationHasIndirect"]!.GetValue<bool>());
            Assert.True(sourceGroup["animationUsesTruncated"]!.GetValue<bool>());
            Assert.Equal(8, sourceGroup["animationUses"]!.AsArray().Count);
            Assert.Equal("indirect", sourceGroup["animationUses"]![0]!["origin"]!.GetValue<string>());
            var searched = await Call(new() { ["search"] = "region" });
            Assert.True(JsonNode.DeepEquals(sourceGroup["animationUses"], searched["matches"]!["items"]![0]!["item"]!["animationUses"]));
            string groupRow = sourceGroup["row"]!.GetValue<string>();
            await Call(Args("select", groupRow));
            Assert.Equal(sourceGroup["selectionOwner"]!.GetValue<int>(), viewport.FramingSelection);
            await Call(Args("properties", groupRow));
            Assert.False(properties.CurrentJson!["editable"]!.GetValue<bool>());
            int owner = sourceGroup["selectionOwner"]!.GetValue<int>();
            // The direct Properties route shares the GUI's atomic row, whether addressed by its signed
            // owner or a physical child. Both inspection and opening remain safe for a source-only group.
            foreach (int target in new[] { owner, 1 })
                foreach (bool open in new[] { false, true })
                {
                    var describedOwner = await Tool("scene_properties", new() { ["preview"] = preview, ["node"] = target, ["open"] = open });
                    Assert.Null(describedOwner["Index"]); Assert.Equal(owner, describedOwner["selectionOwner"]!.GetValue<int>());
                    Assert.False(describedOwner["Metadata"]!["editable"]!.GetValue<bool>());
                    if (open) Assert.True(JsonNode.DeepEquals(describedOwner["Metadata"], properties.CurrentJson));
                }
            await Call(Args("reveal", node: owner));
            // This fixture has no geometry: signed framing reaches the renderer's no-geometry refusal.
            await Tool("camera", new() { ["preview"] = preview, ["action"] = "frame", ["node"] = owner }, "not_ready");
            await Tool("scene_properties", new() { ["preview"] = preview, ["node"] = -1 }, "stale_record");
            // Authored database groups survive compilation in the tree, not in the renderer's slot namespace.
            var group42 = new WorldNodeProvenance { ModelFile = "data/m1/models/m1.gltf", ModelNode = 5, ModelNodeName = "group42", Database = true };
            var group43 = new WorldNodeProvenance { ModelFile = "data/m1/models/m1.gltf", ModelNode = 6, ModelNodeName = "group43", Database = true };
            var firstPart = new WorldNodeProvenance { ModelFile = "data/m1/models/region.gltf", ModelNode = 0, ReferencedBy = reference };
            var secondPart = new WorldNodeProvenance { ModelFile = "data/m1/models/region.gltf", ModelNode = 1, ReferencedBy = reference };
            SourceAuthoredHierarchy authored = new([
                new(0, null, "group42", group42.ModelFile!, 5, true, [1], group42),
                new(1, null, "group43", group43.ModelFile!, 6, true, [2], group43),
                new(2, null, "region", reference.ModelFile!, 7, false, [3, 4], reference),
                new(3, 1, "part1", firstPart.ModelFile!, 0, false, [], firstPart),
                new(4, 2, "part2", secondPart.ModelFile!, 1, false, [], secondPart)], [new(0, [0])])
                { FlattenedEdges = [new(0, 1), new(0, 2)] };
            ownership = SourceSelectionIndex.Build(new Dictionary<int, WorldNodeProvenance> { [0] = new(), [1] = firstPart, [2] = secondPart }, deadline.Token, authored: authored);
            projection = new SourceMapSelection(data, null, ownership, i => new("node:" + i, i, null), deadline.Token);
            viewport.SelectionOwner = projection.Owner;
            Set("sceneTree", new SceneTreeModel(data, "fixture", true, new(), projection: projection));
            read = await Call(new()); context = read["context"]!.GetValue<string>();
            root = read["children"]!["items"]![0]!["row"]!.GetValue<string>();
            var firstGroup = Assert.Single((await Call(Args("read", root)))["children"]!["items"]!.AsArray())!;
            Assert.Equal("group42", firstGroup["Label"]!.GetValue<string>()); Assert.Equal("group", firstGroup["nodeType"]!.GetValue<string>());
            Assert.Null(firstGroup["node"]); Assert.Null(firstGroup["selectionOwner"]); Assert.Null(firstGroup["compiledNode"]);
            Assert.Equal(5, firstGroup["gltfNode"]!.GetValue<int>());
            string firstGroupRow = firstGroup["row"]!.GetValue<string>();
            await Call(Args("select", firstGroupRow)); Assert.Null(viewport.FramingSelection);
            await Call(Args("properties", firstGroupRow)); Assert.False(properties.CurrentJson!["editable"]!.GetValue<bool>());
            // Isolating a source group (no node of its own) shows what the tree holds below it, not an empty view.
            await Call(Args("isolate", firstGroupRow));
            var isolatedNodes = (HashSet<int>)typeof(SceneViewport).GetField("isolatedNodes", flags)!.GetValue(viewport)!;
            Assert.Contains(1, isolatedNodes); Assert.Contains(2, isolatedNodes);
            await Call(Args("isolate", firstGroupRow)); Assert.Null(Get("isolatedNode"));
            Assert.Equal("World", properties.CurrentJson["parent"]!.GetValue<string>());
            var secondGroup = Assert.Single((await Call(Args("read", firstGroupRow)))["children"]!["items"]!.AsArray())!;
            string secondGroupRow = secondGroup["row"]!.GetValue<string>();
            var referenceRow = Assert.Single((await Call(Args("read", secondGroupRow)))["children"]!["items"]!.AsArray())!;
            owner = referenceRow["selectionOwner"]!.GetValue<int>();
            Assert.Equal("group43", projection.DisplayName(projection.Parent(owner)!.Value));
            await Call(Args("reveal", node: 1));
            await Call(Args("properties", referenceRow["row"]!.GetValue<string>()));
            Assert.Equal("group43", properties.CurrentJson!["parent"]!.GetValue<string>());
            // Script-created shared parents survive alongside the original authored path. A script edge
            // to another physical part of this owner also retains the explicit world occurrence.
            data.Nodes[0] = data.Nodes[0] with { Children = [1, 2, 3] };
            data.Nodes.Add(new(3, "extra", "object3d", null, [0], [1], new(), new()));
            var additionalOrigins = new Dictionary<int, WorldNodeProvenance> { [0] = new(), [1] = firstPart, [2] = secondPart, [3] = new() };
            ownership = SourceSelectionIndex.Build(additionalOrigins, deadline.Token, authored: authored);
            var sharedProjection = new SourceMapSelection(data, null, ownership, i => new("node:" + i, i, null), deadline.Token);
            Assert.Contains(sharedProjection.Children(3), edge => edge.Node == sharedProjection.Owner(1));
            Assert.DoesNotContain(sharedProjection.Children(0), edge => edge.Node == sharedProjection.Owner(1));
            ownership = SourceSelectionIndex.Build(additionalOrigins, deadline.Token,
                authored: authored with { FlattenedEdges = [new(0, 1)], ScriptEdges = [new(0, 2)] });
            sharedProjection = new SourceMapSelection(data, null, ownership, i => new("node:" + i, i, null), deadline.Token);
            Assert.Contains(sharedProjection.Children(0), edge => edge.Node == sharedProjection.Owner(1));
            // A detached component can retain a physical imported child after its original root was deleted.
            // It has an owner but no path in the world tree: reject before changing selection, never index -2.
            data = new();
            data.Nodes.Add(new(0, "world", "world", null, [], [], new(), new()));
            data.Nodes.Add(new(1, "library", "object3d", null, [], [2], new(), new()));
            data.Nodes.Add(new(2, "part", "object3d", null, [1], [], new(), new()));
            ownership = SourceSelectionIndex.Build(new Dictionary<int, WorldNodeProvenance>
            {
                [0] = new(), [1] = new(),
                [2] = new() { ModelFile = "data/m1/models/region.gltf", ModelNode = 0, ReferencedBy = reference },
            }, deadline.Token);
            projection = new SourceMapSelection(data, null, ownership, i => new("node:" + i, i, null), deadline.Token);
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.PreviewScene))!.SetValue(viewport, data);
            viewport.SelectionOwner = projection.Owner; viewport.SelectFramingNode(null);
            Set("sceneTree", new SceneTreeModel(data, "fixture", true, new(), projection: projection));
            await Tool("scene_selection", new() { ["preview"] = preview, ["action"] = "select", ["node"] = 2 }, "stale_record");
            Assert.Null(viewport.FramingSelection);
            await Tool("scene_properties", new() { ["preview"] = preview, ["node"] = 2, ["open"] = true }, "stale_record");

            JsonObject Args(string action, string? row = null, int? node = null)
            {
                JsonObject args = new() { ["action"] = action, ["context"] = context, ["preview"] = preview };
                if (row != null) args["row"] = row; if (node != null) args["node"] = node.Value; return args;
            }
            async Task<JsonNode> Call(JsonObject args, string? error = null)
            {
                args["document"] = doc.SessionId.ToString();
                return await Tool("scene_tree", args, error);
            }
            async Task<JsonNode> Tool(string command, JsonObject args, string? error = null)
            {
                var reply = await client.CallToolAsync("zstudio_" + command, args.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: deadline.Token);
                var result = JsonNode.Parse(reply.Content.OfType<TextContentBlock>().Single().Text)!;
                if (error == null) Assert.False(reply.IsError == true, result.ToJsonString()); else Assert.Equal(error, result["code"]!.GetValue<string>());
                return result;
            }
        }
        finally { Set("scene", null); main.Close(); }
        object? Get(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(main);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(main, value);
        static GameScene Scene()
        {
            GameScene scene = new(); scene.Nodes.Add(new(0, "world", "world", null, [], [1, 2], new(), new()));
            scene.Nodes.Add(new(1, "same", "object3d", null, [0], [], new() { ["marker"] = 1 }, new() { ["flags"] = 8 }));
            scene.Nodes.Add(new(2, "same", "object3d", null, [0], [], new() { ["marker"] = 2 }, new() { ["flags"] = 8 })); return scene;
        }
    }
}
