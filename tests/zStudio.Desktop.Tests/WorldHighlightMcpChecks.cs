using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls.Primitives;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core;
using zStudio.Desktop;
using zStudio.Mcp;
using zStudio.Rendering;
using Xunit;

namespace zStudio.Desktop.Tests;

internal static class WorldHighlightMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        using var viewport = new SceneViewport();
        var source = new AssetDocument("highlight-fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty);
        var world = source.Add(AssetKind.World, 0, "Whole world", 0, 0);
        using var doc = new DocumentModel(source);
        main.ViewModel.Documents.Add(doc);
        Set("shownDocument", doc); Set("shownAsset", world); Set("scene", viewport);
        ((FrameworkElement)main.FindName("SceneHost")).Visibility = Visibility.Visible;
        ((FrameworkElement)main.FindName("EmptyPreview")).Visibility = Visibility.Collapsed;
        var preview = (Guid)typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        viewport.RestoreView(new(new(12, 34, 56), new(0, 0, -10), new(0, 1, 0), 60));
        var pose = viewport.CaptureView();
        var buttons = new[] { "HighlightSoils", "HighlightCanModify", "HighlightClipTo", "HighlightZones" }.Select(n => (ToggleButton)main.FindName(n)).ToArray();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            Assert.Equal("none", (await State())["highlight"]!.GetValue<string>());
            await Options(new() { ["wireframe"] = true, ["faceOrientation"] = true });
            Assert.True((await State())["faceOrientation"]!.GetValue<bool>());
            Assert.True(viewport.FaceOrientationEnabled);
            Assert.True(((ToggleButton)main.FindName("Wireframe")).IsChecked);
            ((ToggleButton)main.FindName("FaceOrientation")).IsChecked = false;
            Assert.False((await State())["faceOrientation"]!.GetValue<bool>());
            Assert.False(viewport.FaceOrientationEnabled);
            Assert.True(((ToggleButton)main.FindName("Wireframe")).IsChecked);
            await Options(new() { ["wireframe"] = false });
            await Options(new() { ["faceOrientation"] = "blue" }, "invalid_argument");
            Assert.Equal(pose, viewport.CaptureView()); Assert.Equal(0, doc.Revision);
            string[] modes = ["nonDefaultSoils", "canModify", "clipTo", "zones", "none"];
            for (int i = 0; i < modes.Length; i++)
            {
                await Options(new() { ["highlight"] = modes[i] });
                Assert.Equal(modes[i], (await State())["highlight"]!.GetValue<string>());
                for (int j = 0; j < buttons.Length; j++) Assert.Equal(i == j, buttons[j].IsChecked);
                Assert.Equal(pose, viewport.CaptureView()); Assert.Equal(0, doc.Revision); Assert.False(doc.IsDirty);
                Assert.Equal(preview.ToString(), (await Call("state", new()))["preview"]!.GetValue<string>());
            }
            // The real WPF handlers share state with MCP, including click-again to turn off.
            for (int i = 0; i < buttons.Length; i++)
            {
                await Options(new() { ["highlight"] = "none" });
                buttons[i].IsChecked = true;
                Assert.Equal(modes[i], (await State())["highlight"]!.GetValue<string>());
                Assert.Single(buttons, b => b.IsChecked == true);
            }
            buttons[3].IsChecked = false;
            Assert.Equal("none", (await State())["highlight"]!.GetValue<string>());
            foreach (int first in new[] { 1, 2 })
            {
                buttons[first].IsChecked = true;
                buttons[3 - first].IsChecked = true;
                Assert.Equal("canModifyAndClipTo", (await State())["highlight"]!.GetValue<string>());
                Assert.True(buttons[1].IsChecked); Assert.True(buttons[2].IsChecked);
                buttons[first].IsChecked = false;
                Assert.Equal(modes[3 - first], (await State())["highlight"]!.GetValue<string>());
                buttons[3 - first].IsChecked = false;
                Assert.Equal("none", (await State())["highlight"]!.GetValue<string>());
            }
            await Options(new() { ["highlight"] = "canModifyAndClipTo" });
            Assert.Equal(WorldHighlightMode.CanModifyAndClipTo, viewport.HighlightMode);
            Assert.True(buttons[1].IsChecked); Assert.True(buttons[2].IsChecked);
            buttons[0].IsChecked = true;
            Assert.Equal("nonDefaultSoils", (await State())["highlight"]!.GetValue<string>());
            Assert.False(buttons[1].IsChecked); Assert.False(buttons[2].IsChecked);
            await Options(new() { ["highlight"] = "CANMODIFY" }, "invalid_argument");
            await Options(new() { ["highlight"] = true }, "invalid_argument");
            // Dynamic constraints must reject the whole batch in either JSON order.
            await Options(new() { ["highlight"] = "canModifyAndClipTo" });
            foreach (var invalid in new JsonObject[] { new() { ["lod"] = 999 }, new() { ["lod"] = long.MaxValue },
                new() { ["texturePack"] = "unavailable-texture-pack" }, new() { ["difficulty"] = "Impossible" } })
            foreach (bool highlightFirst in new[] { true, false })
            {
                var before = await State();
                var changes = new JsonObject();
                if (highlightFirst) changes["highlight"] = "clipTo";
                changes["wireframe"] = true;
                foreach (var (name, value) in invalid) changes[name] = value!.DeepClone();
                if (!highlightFirst) changes["highlight"] = "clipTo";
                await Options(changes, "invalid_argument");
                Assert.True(JsonNode.DeepEquals(before, await State()));
                Assert.Equal(WorldHighlightMode.CanModifyAndClipTo, viewport.HighlightMode);
                Assert.True(buttons[1].IsChecked); Assert.False(buttons[0].IsChecked); Assert.True(buttons[2].IsChecked);
                Assert.False(((ToggleButton)main.FindName("Wireframe")).IsChecked);
                Assert.Equal(pose, viewport.CaptureView()); Assert.Equal(0, doc.Revision);
            }
            // A valid option can still fail during refresh. Keep the existing
            // highlight and native toggles when this synthetic document cannot load.
            Set("publishedStaticOptions", typeof(MainWindow).GetMethod("ReadStaticSceneOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null));
            var beforeFailure = await State();
            await Options(new() { ["highlight"] = "clipTo", ["horizon"] = !beforeFailure["horizon"]!.GetValue<bool>() }, "preview_unavailable");
            Assert.True(JsonNode.DeepEquals(beforeFailure, await State()));
            Assert.Equal(WorldHighlightMode.CanModifyAndClipTo, viewport.HighlightMode);
            Assert.True(buttons[1].IsChecked); Assert.True(buttons[2].IsChecked);
            await Options(new() { ["highlight"] = "none" });
            await Call("scene_options", new() { ["preview"] = Guid.NewGuid().ToString(), ["changes"] = new JsonObject { ["highlight"] = "clipTo" } }, "stale_preview");
            foreach (var kind in new[] { AssetKind.Model, AssetKind.Node, AssetKind.Animation, AssetKind.Texture })
            {
                Set("shownAsset", source.Add(kind, 0, "Other viewer", 0, 0));
                Assert.Null((await State())["highlight"]);
                await Options(new() { ["wireframe"] = true, ["highlight"] = "canModify" }, "unsupported");
                Assert.False(((ToggleButton)main.FindName("Wireframe")).IsChecked);
                Assert.Equal(WorldHighlightMode.None, viewport.HighlightMode);
            }
            Set("shownAsset", world);
            await Options(new() { ["highlight"] = "clipTo" });
            Assert.Equal(WorldHighlightMode.ClipTo, viewport.HighlightMode);
            Assert.Equal(pose, viewport.CaptureView()); Assert.Equal(0, doc.Revision);

            Task<JsonNode> State() => Call("preview_state", new() { ["preview"] = preview.ToString() });
            Task<JsonNode> Options(JsonObject changes, string? error = null) => Call("scene_options", new() { ["preview"] = preview.ToString(), ["changes"] = changes }, error);
            async Task<JsonNode> Call(string name, JsonObject args, string? error = null)
            {
                var result = await client.CallToolAsync("zstudio_" + name, args.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: deadline.Token);
                var data = JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
                if (name != "operation" && data["State"] != null)
                {
                    string id = data["id"]!.GetValue<string>();
                    while (data["State"]!.GetValue<string>() is "queued" or "running")
                    {
                        await Task.Delay(5, deadline.Token);
                        data = await Call("operation", new() { ["id"] = id });
                    }
                    Assert.Equal(error == null ? "completed" : "failed", data["State"]!.GetValue<string>());
                    data = data["result"]!;
                }
                if (error != null) Assert.Equal(error, data["code"]!.GetValue<string>());
                else Assert.False(result.IsError == true, data.ToJsonString());
                return data;
            }
        }
        finally { Set("scene", null); main.Close(); }
        await SharedModelZones();
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);
    }

    private static async Task SharedModelZones()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var material = new zStudio.Core.Worlds.WorldMaterial();
        var builder = new zStudio.Core.Worlds.ModelBuilder();
        builder.Add(new([new(0,0,0),new(0,0,1),new(1,0,1)], [], [], [], material));
        var root = new zStudio.Core.Worlds.WorldNode("world", zStudio.Core.Worlds.WorldNodeClass.World);
        var world = new zStudio.Core.Worlds.GameZWorld(); world.Nodes.Add(root); world.Models.Add(builder.Model); world.Materials.Add(material);
        for (uint zone = 1; zone <= 2; zone++)
        {
            var node = new zStudio.Core.Worlds.WorldNode("part" + zone, zStudio.Core.Worlds.WorldNodeClass.Object3D)
            { Model = builder.Model, Zone = zone, Flags = zStudio.Core.Worlds.WorldGltf.DefaultCarried | 4 };
            node.SetPayloadInt(0, 0x28); node.Parents.Add(root); root.Children.Add(node); world.Nodes.Add(node);
        }
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-zone-batches-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = zStudio.Core.Formats.FormatRegistry.Default.OpenBytes(Path.Combine(folder,"gamez.zbd"), zStudio.Core.Worlds.GameZWriter.Write(world, token), token: token);
            using var resolver = new AssetResolver(folder);
            using var viewport = new SceneViewport();
            await viewport.ShowAsync(source, source.Assets.Single(a => a.Kind == AssetKind.World), resolver, null, 0, token);
            viewport.SetWorldHighlightMode(WorldHighlightMode.Zones);
            var batches = (System.Collections.IDictionary)typeof(SceneViewport).GetField("placements", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewport)!;
            Assert.Equal(2, batches.Count);
            var colors = new HashSet<string>();
            foreach (System.Collections.DictionaryEntry batch in batches)
            {
                var mesh = (HelixToolkit.Wpf.SharpDX.MeshGeometryModel3D)batch.Key;
                var placement = Assert.Single((ScenePlacement[])batch.Value!);
                int zone = WorldSurfaceHighlights.NodeZone(source.Scene!, placement.NodeIndex);
                var actual = PreviewMaterials.OverlayColor(Assert.IsType<HelixToolkit.Wpf.SharpDX.DiffuseMaterial>(mesh.Material));
                var expected = WorldSurfaceHighlights.ZoneColor(zone);
                Assert.Equal(expected.R, actual.Red); Assert.Equal(expected.G, actual.Green); Assert.Equal(expected.B, actual.Blue); Assert.Equal(.5f, actual.Alpha);
                colors.Add(actual.ToString()); Assert.Single(mesh.Instances!);
            }
            Assert.Equal(2, colors.Count);
        }
        finally { Directory.Delete(folder, true); }
    }
}
