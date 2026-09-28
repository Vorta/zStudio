using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Rendering;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class AiNetworkMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        using var viewport = new SceneViewport();
        var source = new ZbdDocument("ai-fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty);
        var world = source.Add(AssetKind.World, 0, "Whole world", 0, 0);
        using var doc = new DocumentModel(source); main.ViewModel.Documents.Add(doc);
        Set("shownDocument", doc); Set("shownAsset", world); Set("scene", viewport);
        ((FrameworkElement)main.FindName("SceneHost")).Visibility = Visibility.Visible;
        ((FrameworkElement)main.FindName("EmptyPreview")).Visibility = Visibility.Collapsed;
        string preview = ((Guid)Get("previewId")!).ToString();
        AiNode a = new("a", 0, 12, Vector3.Zero, 12, [new(0, 1, "b", null), new(1, -7, null, null), new(2, -1, null, null)]);
        AiNode b = new("b", 1, 12, new(30, 0, 0), 64, [new(0, -1, null, null), new(1, -1, null, null), new(2, -1, null, null)]);
        var first = new AiNetwork("first", "fixture.zbd", 0, "net_01.zrd", "same_name", "standard", 10, [a, b], []) { AttackStrategy = AiAttackStrategy.Stored("cIrClE") };
        var second = first with { Id = "second", MemberIndex = 1, Nodes = [a with { Id = "duplicate", Links = [] }] };
        viewport.SetAiNetworks(new("graph1", [first, second]));
        Invoke("ConfigureAiScene", viewport); Invoke("ApplyAiOptions");
        var enabled = (ToggleButton)main.FindName("AiEnabled"); var through = (ToggleButton)main.FindName("AiThroughGeometry");
        var filter = (ComboBox)main.FindName("AiNetworkCombo");
        viewport.RestoreView(new(new(10, 20, 80), new(0, 0, -50), new(0, 1, 0), 45)); var initialPose = viewport.CaptureView();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            Assert.False((await State())["ai"]!["visible"]!.GetValue<bool>()); Assert.True(through.IsChecked);
            var list = await Call("ai_networks", new() { ["preview"] = preview, ["limit"] = 1 });
            Assert.Equal(2, list["total"]!.GetValue<int>()); Assert.Equal(1, list["nextOffset"]!.GetValue<int>());
            Assert.Equal("first", list["items"]![0]!["Id"]!.GetValue<string>());
            var strategy = list["items"]![0]!["attack_strategy"]!;
            Assert.Equal("cIrClE", strategy["value"]!.GetValue<string>()); Assert.Equal("stored", strategy["status"]!.GetValue<string>());
            Assert.Equal("CIR", strategy["key"]!.GetValue<string>()); Assert.Equal("#33D9FF", strategy["color"]!.GetValue<string>());
            Assert.Contains("CIR — Cyan (#33D9FF)", enabled.Tag.ToString());
            Assert.Contains("Missing — Red (#FF6666)", enabled.Tag.ToString());
            Assert.Contains("Empty / unrecognized / invalid — Gray (#A0A0A0)", enabled.Tag.ToString());
            var nodes = await Call("ai_nodes", Args(("network", "first"), ("query", "node_00")));
            Assert.Single(nodes["items"]!.AsArray()); Assert.Equal(-7, nodes["items"]![0]!["links"]![1]!["target_index"]!.GetValue<int>());
            Assert.True(JsonNode.DeepEquals(strategy, nodes["items"]![0]!["attack_strategy"]));
            await Select("select", "a", "not_ready");
            enabled.IsChecked = true; Assert.True((await State())["ai"]!["visible"]!.GetValue<bool>());
            through.IsChecked = false; Assert.False((await State())["ai"]!["throughGeometry"]!.GetValue<bool>());
            await Options(new() { ["aiThroughGeometry"] = true, ["aiNetwork"] = "first", ["aiSnapshot"] = "graph1", ["highlight"] = "canModify" });
            Assert.Equal(1, filter.SelectedIndex); Assert.True(through.IsChecked); Assert.Equal(WorldHighlightMode.CanModify, viewport.HighlightMode);
            await Select("select", "duplicate", "not_ready");
            await Select("select", "a"); Assert.Equal("a", viewport.SelectedAiNode); Assert.Null(Get("selectedNode"));
            await Select("properties", "a"); var pinned = main.OpenPropertiesWindow!;
            Assert.Equal("a", pinned.CurrentJson!["node_id"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(strategy, pinned.CurrentJson!["attack_strategy"]));
            var inspected = await Call("scene_inspect", new() { ["preview"] = preview });
            Assert.Equal("cIrClE", inspected["inspection"]!["Attack strategy"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(strategy, inspected["attackStrategy"]));
            var copied = await Call("scene_card", new() { ["preview"] = preview, ["action"] = "copy", ["field"] = "Attack strategy" });
            Assert.Equal("Attack strategy: cIrClE", copied["text"]!.GetValue<string>());
            await Select("select", "b"); Assert.Same(pinned, main.OpenPropertiesWindow); Assert.Equal("a", pinned.CurrentJson!["node_id"]!.GetValue<string>());
            await Call("camera", new() { ["preview"] = preview, ["action"] = "frame", ["target"] = "selected" });
            Assert.NotEqual(initialPose, viewport.CaptureView()); viewport.RestoreView(initialPose);
            filter.SelectedIndex = 2; Assert.Null(viewport.SelectedAiNode); Assert.Equal("second", (await State())["ai"]!["network"]!.GetValue<string>());
            await Options(new() { ["aiNetwork"] = "all" });
            foreach (var invalid in new JsonObject[] { new() { ["aiNodes"] = "yes" }, new() { ["aiNetwork"] = "first" },
                new() { ["aiNetwork"] = "missing", ["aiSnapshot"] = "graph1" }, new() { ["aiSnapshot"] = "old" } })
            {
                var before = await State(); invalid["wireframe"] = true;
                await Options(invalid, invalid["aiNodes"] != null ? "invalid_argument" : invalid["aiNetwork"]?.GetValue<string>() == "missing" ? "stale_record" : "stale_snapshot");
                Assert.True(JsonNode.DeepEquals(before, await State())); Assert.False(((ToggleButton)main.FindName("Wireframe")).IsChecked);
            }
            viewport.SetAiNetworks(new("graph2", [first with { AttackStrategy = AiAttackStrategy.Stored("zigzag") }])); Invoke("ApplyAiOptions");
            Assert.Null(viewport.SelectedAiNode); Assert.Equal("all", (await State())["ai"]!["network"]!.GetValue<string>());
            Assert.Contains("Source resources changed", pinned.CurrentJson!["snapshot_status"]!.GetValue<string>());
            await Call("ai_nodes", Args(), "stale_snapshot"); await Select("select", "a", "stale_snapshot");
            var updated = await Call("ai_networks", new() { ["preview"] = preview });
            Assert.Equal("#FF66B3", updated["items"]![0]!["attack_strategy"]!["color"]!.GetValue<string>());
            viewport.SetAiNetworks(new("graph1", [first, second])); Invoke("ApplyAiOptions");
            Assert.True(JsonNode.DeepEquals(strategy, (await Call("ai_networks", new() { ["preview"] = preview }))["items"]![0]!["attack_strategy"]));
            await Select("clear", ""); Assert.Null(viewport.SelectedAiNode);
            int variantIndex = 0;
            foreach (var (variant, expectedText) in new[]
            {
                (AiAttackStrategy.Missing, "Not stored"), (AiAttackStrategy.Invalid, "Unavailable (invalid data)"),
                (AiAttackStrategy.Stored("future mode"), "future mode"), (AiAttackStrategy.Stored(""), "\"\" (empty)"),
                (AiAttackStrategy.Stored(new string('x', 100_000)), new string('x', 2048) + "… [truncated]")
            })
            {
                viewport.SetAiNetworks(new("variant-" + variantIndex++, [first with { AttackStrategy = variant }])); Invoke("ApplyAiOptions");
                Assert.True(viewport.SelectAiNode("a"));
                var read = await Call("scene_inspect", new() { ["preview"] = preview });
                Assert.Equal(expectedText, read["inspection"]!["Attack strategy"]!.GetValue<string>());
                Assert.Equal(variant.State == AiAttackStrategyState.Missing ? "#FF6666" : "#A0A0A0", read["attackStrategy"]!["color"]!.GetValue<string>());
                Assert.Equal(variant.State.ToString().ToLowerInvariant(), read["attackStrategy"]!["status"]!.GetValue<string>());
                Assert.Equal("unknown", read["attackStrategy"]!["key"]!.GetValue<string>());
                Assert.Equal(variant.Value?.Length, read["attackStrategy"]!["characters"]?.GetValue<int>());
                Assert.True(JsonNode.DeepEquals(read["attackStrategy"], (await Call("ai_networks", new() { ["preview"] = preview }))["items"]![0]!["attack_strategy"]));
                var variantArgs = new JsonObject { ["preview"] = preview, ["snapshot"] = viewport.AiNetworks.Id, ["network"] = "first" };
                Assert.True(JsonNode.DeepEquals(read["attackStrategy"], (await Call("ai_nodes", variantArgs))["items"]![0]!["attack_strategy"]));
                await Call("ai_selection", new() { ["preview"] = preview, ["snapshot"] = viewport.AiNetworks.Id, ["action"] = "properties", ["node"] = "a" });
                Assert.True(JsonNode.DeepEquals(read["attackStrategy"], main.OpenPropertiesWindow!.CurrentJson!["attack_strategy"]));
            }
            enabled.IsChecked = false; Assert.False((await State())["ai"]!["visible"]!.GetValue<bool>());
            Set("shownAsset", source.Add(AssetKind.Model, 0, "model", 0, 0));
            Assert.Null((await State())["ai"]); await Options(new() { ["aiNodes"] = true, ["wireframe"] = true }, "unsupported");
            await Call("ai_networks", new() { ["preview"] = preview }, "unsupported");
            Assert.Equal(initialPose, viewport.CaptureView()); Assert.False(doc.IsDirty); Assert.Equal(0, doc.Revision);
            pinned.Close();

            JsonObject Args(params (string Key, string Value)[] pairs)
            { JsonObject args = new() { ["preview"] = preview, ["snapshot"] = "graph1" }; foreach (var (key, value) in pairs) args[key] = value; return args; }
            Task<JsonNode> Select(string action, string node, string? error = null) => Call("ai_selection", Args(("action", action), ("node", node)), error);
            Task<JsonNode> State() => Call("preview_state", new() { ["preview"] = preview });
            Task<JsonNode> Options(JsonObject changes, string? error = null) => Call("scene_options", new() { ["preview"] = preview, ["changes"] = changes }, error);
            async Task<JsonNode> Call(string name, JsonObject args, string? error = null)
            {
                var result = await client.CallToolAsync("zstudio_" + name, args.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: deadline.Token);
                var data = JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
                if (name != "operation" && data["State"] != null)
                {
                    string id = data["id"]!.GetValue<string>();
                    while (data["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(5, deadline.Token); data = await Call("operation", new() { ["id"] = id }); }
                    Assert.Equal(error == null ? "completed" : "failed", data["State"]!.GetValue<string>()); data = data["result"]!;
                }
                if (error != null) Assert.Equal(error, data["code"]!.GetValue<string>()); else Assert.False(result.IsError == true, data.ToJsonString());
                return data;
            }
        }
        finally { Set("scene", null); main.Close(); }
        object? Get(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);
        void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, args);
    }
}
