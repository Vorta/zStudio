using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using HelixToolkit.Wpf.SharpDX;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Rendering;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class SceneSelectionMcpChecks
{
    internal static async Task Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
        package.Entries.Add(new(new byte[308], 0, 0));
        var data = new GameScene();
        data.Nodes.Add(new(0, "fixture", "Object3D", null, [], [], new() { ["marker"] = "original" }, new()));
        data.Nodes.Add(new(1, "pickup mesh one", "Object3D", null, [], [], new() { ["marker"] = "child" }, new()));
        data.Nodes.Add(new(2, "pickup mesh two", "Object3D", null, [], [], new(), new()));
        using var doc = new DocumentModel(new ZbdDocument("fixture", new(0, DateTime.MinValue),
            new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package });
        main.ViewModel.Documents.Add(doc);
        using var editor = new AnimationEditor(doc, 0, new AssetResolver(Path.GetTempPath()), CancellationToken.None);
        try
        {
            typeof(MainWindow).GetField("shownDocument", flags)!.SetValue(main, doc);
            typeof(MainWindow).GetField("animation", flags)!.SetValue(main, editor);
            ((FrameworkElement)main.FindName("EmptyPreview")).Visibility = Visibility.Collapsed;
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.PreviewScene))!.SetValue(editor.Viewport, data);
            // A distinct pose detects Isolate/Show all's implicit FrameAll side effect.
            editor.Viewport.RestoreView(new(new(12, 34, 56), new(0, 0, -10), new(0, 1, 0), 60));
            var pose = editor.Viewport.CaptureView();
            var preview = (Guid)typeof(MainWindow).GetField("previewId", flags)!.GetValue(main)!;
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)main.FindName("SceneToolbar")).Visibility);
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            foreach (string action in new[] { "isolate", "show_all" })
            {
                var result = await Call(action);
                Assert.True(result.IsError);
                Assert.Contains("unsupported", result.Content.OfType<TextContentBlock>().Single().Text);
                Assert.Null(typeof(MainWindow).GetField("isolatedNode", flags)!.GetValue(main));
                Assert.Equal(pose, editor.Viewport.CaptureView());
            }
            var inspected = await Call("select");
            Assert.False(inspected.IsError == true);
            Assert.Contains("original", inspected.Content.OfType<TextContentBlock>().Single().Text);
            Assert.Equal(pose, editor.Viewport.CaptureView());
            Assert.Equal(0, doc.Revision);

            // The same actions remain usable in the static viewer where GUI controls exist.
            typeof(MainWindow).GetField("animation", flags)!.SetValue(main, null);
            using var viewport = new SceneViewport();
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.PreviewScene))!.SetValue(viewport, data);
            typeof(MainWindow).GetField("scene", flags)!.SetValue(main, viewport);
            ((FrameworkElement)main.FindName("SceneHost")).Visibility = Visibility.Visible;
            Assert.False((await Call("isolate")).IsError == true);
            Assert.Equal(0, typeof(MainWindow).GetField("isolatedNode", flags)!.GetValue(main));
            Assert.False((await Call("show_all")).IsError == true);
            Assert.Null(typeof(MainWindow).GetField("isolatedNode", flags)!.GetValue(main));

            // A clicked pickup child selects and isolates its entire placed actor,
            // exactly as GUI InspectNode followed by Isolate does.
            var roots = (Dictionary<int, int>)typeof(SceneViewport).GetField("pickupRoots", flags)!.GetValue(viewport)!;
            roots.Add(0, 0); roots.Add(1, 0); roots.Add(2, 0);
            var actors = (Dictionary<int, MissionActor>)typeof(SceneViewport).GetField("pickupActors", flags)!.GetValue(viewport)!;
            actors.Add(0, new(0, 0, "fixture pickup", "fixture"));
            var mesh = new MeshGeometryModel3D();
            ((List<MeshGeometryModel3D>)typeof(SceneViewport).GetField("meshes", flags)!.GetValue(viewport)!).Add(mesh);
            ScenePlacement[] placements = [new(1, 0, "first", Matrix4x4.Identity), new(2, 1, "second", Matrix4x4.Identity)];
            ((Dictionary<MeshGeometryModel3D, ScenePlacement[]>)typeof(SceneViewport).GetField("placements", flags)!.GetValue(viewport)!).Add(mesh, placements);
            ((Dictionary<MeshGeometryModel3D, ScenePlacement[]>)typeof(SceneViewport).GetField("visiblePlacements", flags)!.GetValue(viewport)!).Add(mesh, placements);
            var isolated = await Call("isolate", 1);
            Assert.False(isolated.IsError == true);
            Assert.Contains("original", isolated.Content.OfType<TextContentBlock>().Single().Text);
            Assert.Equal(0, viewport.SelectedPickupRoot);
            Assert.Equal(0, typeof(MainWindow).GetField("selectedNode", flags)!.GetValue(main));
            Assert.Equal(0, typeof(MainWindow).GetField("isolatedNode", flags)!.GetValue(main));
            Assert.NotNull(mesh.Instances);
            Assert.Equal(2, mesh.Instances.Count);

            // Simulate the renderer's in-progress drag state without capturing the
            // physical mouse. Remote selection must not silently cancel that move.
            var drag = typeof(SceneViewport).GetProperty(nameof(SceneViewport.IsPickupDragging))!;
            viewport.RestoreView(pose);
            drag.SetValue(viewport, true);
            try
            {
                foreach (string action in new[] { "select", "isolate", "show_all" })
                    AssertBusy(await Call(action, 1));
                foreach (string action in new[] { "set", "move", "rotate", "frame" })
                    AssertBusy(await Camera(action));
                Assert.False((await Camera("read")).IsError == true);
                Assert.True(viewport.IsPickupDragging);
                Assert.Equal(pose, viewport.CaptureView());
                Assert.Equal(0, viewport.SelectedPickupRoot);
                Assert.Equal(2, mesh.Instances.Count);
                Assert.Equal(0, doc.Revision);
            }
            finally { drag.SetValue(viewport, false); }
            Assert.False((await Call("show_all")).IsError == true);
            Assert.False((await Camera("move")).IsError == true);
            Assert.NotEqual(pose.Position, viewport.CaptureView().Position);
            typeof(MainWindow).GetField("scene", flags)!.SetValue(main, null);

            Task<CallToolResult> Call(string action, int node = 0) => client.CallToolAsync("zstudio_scene_selection",
                new Dictionary<string, object?> { ["preview"] = preview.ToString(), ["action"] = action, ["node"] = node }, cancellationToken: deadline.Token).AsTask();
            Task<CallToolResult> Camera(string action) => client.CallToolAsync("zstudio_camera",
                new Dictionary<string, object?> { ["preview"] = preview.ToString(), ["action"] = action,
                    ["position"] = new[] { 1, 2, 3 }, ["look"] = new[] { 0, 0, -1 }, ["forward"] = 1, ["horizontal"] = 5 }, cancellationToken: deadline.Token).AsTask();
            void AssertBusy(CallToolResult result)
            {
                Assert.True(result.IsError);
                Assert.Contains("busy", result.Content.OfType<TextContentBlock>().Single().Text);
            }
        }
        finally
        {
            typeof(MainWindow).GetField("animation", flags)!.SetValue(main, null);
            typeof(MainWindow).GetField("scene", flags)!.SetValue(main, null);
            main.Close();
        }
    }
}
