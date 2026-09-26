using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls.Primitives;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Rendering;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class WorldHighlightMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        using var viewport = new SceneViewport();
        var source = new ZbdDocument("highlight-fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty);
        var world = source.Add(AssetKind.World, 0, "Whole world", 0, 0);
        using var doc = new DocumentModel(source);
        main.ViewModel.Documents.Add(doc);
        Set("shownDocument", doc); Set("shownAsset", world); Set("scene", viewport);
        ((FrameworkElement)main.FindName("SceneHost")).Visibility = Visibility.Visible;
        ((FrameworkElement)main.FindName("EmptyPreview")).Visibility = Visibility.Collapsed;
        var preview = (Guid)typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        viewport.RestoreView(new(new(12, 34, 56), new(0, 0, -10), new(0, 1, 0), 60));
        var pose = viewport.CaptureView();
        var buttons = new[] { "HighlightSoils", "HighlightCanModify", "HighlightClipTo" }.Select(n => (ToggleButton)main.FindName(n)).ToArray();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            Assert.Equal("none", (await State())["highlight"]!.GetValue<string>());
            string[] modes = ["nonDefaultSoils", "canModify", "clipTo", "none"];
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
                buttons[i].IsChecked = true;
                Assert.Equal(modes[i], (await State())["highlight"]!.GetValue<string>());
                Assert.Single(buttons, b => b.IsChecked == true);
            }
            buttons[2].IsChecked = false;
            Assert.Equal("none", (await State())["highlight"]!.GetValue<string>());
            await Options(new() { ["highlight"] = "CANMODIFY" }, "invalid_argument");
            await Options(new() { ["highlight"] = true }, "invalid_argument");
            // Dynamic constraints must reject the whole batch in either JSON order.
            await Options(new() { ["highlight"] = "canModify" });
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
                Assert.Equal(WorldHighlightMode.CanModify, viewport.HighlightMode);
                Assert.True(buttons[1].IsChecked); Assert.False(buttons[0].IsChecked); Assert.False(buttons[2].IsChecked);
                Assert.False(((ToggleButton)main.FindName("Wireframe")).IsChecked);
                Assert.Equal(pose, viewport.CaptureView()); Assert.Equal(0, doc.Revision);
            }
            // A valid option can still fail during refresh. Keep the existing
            // highlight and native toggles when this synthetic document cannot load.
            Set("publishedStaticOptions", typeof(MainWindow).GetMethod("ReadStaticSceneOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null));
            var beforeFailure = await State();
            await Options(new() { ["highlight"] = "clipTo", ["horizon"] = !beforeFailure["horizon"]!.GetValue<bool>() }, "preview_unavailable");
            Assert.True(JsonNode.DeepEquals(beforeFailure, await State()));
            Assert.Equal(WorldHighlightMode.CanModify, viewport.HighlightMode);
            Assert.True(buttons[1].IsChecked); Assert.False(buttons[2].IsChecked);
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
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);
    }
}
