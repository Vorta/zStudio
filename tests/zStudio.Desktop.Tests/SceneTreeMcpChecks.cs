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
using Recoil.Zbd.Rendering;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class SceneTreeMcpChecks
{
    internal static async Task Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        var data = Scene();
        var source = new ZbdDocument("hierarchy-fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = data };
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

            JsonObject Args(string action, string? row = null, int? node = null)
            {
                JsonObject args = new() { ["action"] = action, ["context"] = context, ["preview"] = preview };
                if (row != null) args["row"] = row; if (node != null) args["node"] = node.Value; return args;
            }
            async Task<JsonNode> Call(JsonObject args, string? error = null)
            {
                args["document"] = doc.SessionId.ToString();
                var reply = await client.CallToolAsync("zstudio_scene_tree", args.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: deadline.Token);
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
