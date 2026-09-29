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
        var first = new AiNetwork("first", "fixture.zbd", 0, "net_01.zrd", "same_name", "standard", 10, [a, b], [])
        {
            AttackStrategy = AiAttackStrategy.Stored("cIrClE"),
            Constraints = [new(46, 0, 1, "scan_time", 96, new()), new(46, 0, 1, "canleave", 108, new()) { AttributeIndex = 1 }, new(47, 1, 0, "scan_time", 120, new())]
        };
        var second = first with { Id = "second", MemberIndex = 1, Member = "net_02.zrd", Nodes = [a with { Id = "duplicate", Links = [] }], Constraints = [new(48, 0, 0, "scan_time", 96, new())] };
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
            var constraintArgs = Args(("section", "constraints"), ("query", "SCAN_TIME")); constraintArgs["limit"] = 1;
            var constraints = await Call("ai_nodes", constraintArgs);
            Assert.Equal(3, constraints["total"]!.GetValue<int>()); Assert.Equal(1, constraints["nextOffset"]!.GetValue<int>());
            Assert.Equal(46, Assert.Single(constraints["items"]!.AsArray())!["constraint"]!["Index"]!.GetValue<int>());
            constraintArgs["offset"] = 1;
            constraints = await Call("ai_nodes", constraintArgs);
            Assert.Equal(3, constraints["total"]!.GetValue<int>()); Assert.Equal(2, constraints["nextOffset"]!.GetValue<int>());
            Assert.Equal(47, Assert.Single(constraints["items"]!.AsArray())!["constraint"]!["Index"]!.GetValue<int>());
            constraintArgs["offset"] = 2;
            constraints = await Call("ai_nodes", constraintArgs);
            Assert.Equal("second", Assert.Single(constraints["items"]!.AsArray())!["network"]!.GetValue<string>()); Assert.Null(constraints["nextOffset"]);
            foreach (var (network, query, total) in new[] { ("first", "scan_time", 2), ("all", "CANLEAVE", 1), ("all", "NET_02.ZRD", 1), ("all", "same_NAME", 4), ("all", "EDGE_46", 2), ("all", "ATTRIBUTE_01", 1), ("all", "NODE_01", 3), ("all", "missing-constraint", 0), ("first", "net_02.zrd", 0), ("all", "", 4) })
            {
                constraints = await Call("ai_nodes", Args(("section", "constraints"), ("network", network), ("query", query)));
                Assert.Equal(total, constraints["total"]!.GetValue<int>()); Assert.Equal(total, constraints["items"]!.AsArray().Count); Assert.Null(constraints["nextOffset"]);
                if (query == "CANLEAVE")
                {
                    var row = constraints["items"]![0]!["constraint"]!;
                    Assert.Equal(1, row["AttributeIndex"]!.GetValue<int>()); Assert.False(row["KindTruncated"]!.GetValue<bool>());
                    Assert.False(row["ParametersTruncated"]!.GetValue<bool>()); Assert.Empty(row["Parameters"]!.AsObject());
                }
            }
            constraintArgs["offset"] = 3;
            constraints = await Call("ai_nodes", constraintArgs);
            Assert.Equal(3, constraints["total"]!.GetValue<int>()); Assert.Empty(constraints["items"]!.AsArray()); Assert.Null(constraints["nextOffset"]);
            string hugeKind = new string('k', 100_000) + "only-in-omitted-suffix";
            JsonObject hugeParameters = new() { ["children"] = new JsonArray(Enumerable.Range(0, 1000).Select(_ => (JsonNode)new JsonObject { ["value"] = new string('x', 10_000) }).ToArray()) };
            var large = new AiConstraint(46, 0, 1, hugeKind, 96, hugeParameters);
            viewport.SetAiNetworks(new("large-constraints", [first with { Name = new string('n', 100_000), Constraints = Enumerable.Repeat(large, 200).ToArray() }]));
            var boundedArgs = Args(("section", "constraints"), ("query", "kkkk"), ("snapshot", "large-constraints")); boundedArgs["limit"] = 200;
            constraints = await Call("ai_nodes", boundedArgs);
            Assert.Equal(200, constraints["total"]!.GetValue<int>()); Assert.True(constraints.ToJsonString().Length < 1_000_000);
            var bounded = constraints["items"]![0]!["constraint"]!;
            Assert.Equal(256, bounded["Kind"]!.GetValue<string>().Length); Assert.True(bounded["KindTruncated"]!.GetValue<bool>());
            Assert.Equal(hugeKind.Length, bounded["KindCharacters"]!.GetValue<int>()); Assert.True(bounded["ParametersTruncated"]!.GetValue<bool>());
            constraints = await Call("ai_nodes", Args(("section", "constraints"), ("query", "only-in-omitted-suffix"), ("snapshot", "large-constraints")));
            Assert.Equal(0, constraints["total"]!.GetValue<int>()); Assert.Equal(1000, hugeParameters["children"]!.AsArray().Count);
            Assert.Equal(hugeKind, large.Kind);
            JsonObject deep = new() { ["value"] = "leaf" }; for (int i = 0; i < 100; i++) deep = new() { ["children"] = new JsonArray(deep) };
            var numeric = new JsonObject { ["children"] = new JsonArray(Enumerable.Range(0, 1000).Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()) };
            viewport.SetAiNetworks(new("bounded-parameters", [first with { Constraints = [large with { Kind = "deep", Parameters = deep }, large with { Kind = "wide", Parameters = numeric }, large with { Kind = "previously truncated", Parameters = new() { ["value_truncated"] = true, ["value"] = "prefix" } }] }]));
            constraints = await Call("ai_nodes", Args(("section", "constraints"), ("snapshot", "bounded-parameters")));
            Assert.Equal(3, constraints["total"]!.GetValue<int>()); Assert.True(constraints.ToJsonString().Length < 5000);
            Assert.All(constraints["items"]!.AsArray(), row => Assert.True(row!["constraint"]!["ParametersTruncated"]!.GetValue<bool>()));
            string hugeName = new string('n', 100_000) + "only-in-omitted-name";
            viewport.SetAiNetworks(new("large-names", [first with { Name = hugeName, Type = hugeName, Nodes = Enumerable.Range(0, 99).Select(i => a with { Id = "large-" + i, Index = i, Links = [] }).ToArray() }]));
            foreach (var (query, count) in new[] { ("", 99), ("NNNN", 99), ("NODE_98", 1), ("only-in-omitted-name", 0) })
            {
                var args = Args(("snapshot", "large-names"), ("query", query)); args["limit"] = 1;
                nodes = await Call("ai_nodes", args); Assert.Equal(count, nodes["total"]!.GetValue<int>());
                Assert.True(nodes.ToJsonString().Length < 4000);
            }
            list = await Call("ai_networks", new() { ["preview"] = preview, ["query"] = "only-in-omitted-name" }); Assert.Equal(0, list["total"]!.GetValue<int>());
            string escaped = new('\u0001', 100_000);
            var pageNodes = Enumerable.Range(0, 99).Select(i => a with { Id = "page-" + i, Index = i, Links = Enumerable.Range(0, 32).Select(slot => new AiLink(slot, -1, null, null)).ToArray() }).ToArray();
            var worst = first with { Name = escaped, Type = escaped, AttackStrategy = AiAttackStrategy.Stored(escaped), Nodes = pageNodes };
            viewport.SetAiNetworks(new("full-node-page", Enumerable.Range(0, 3).Select(i => worst with { Id = "network-" + i }).ToArray()));
            var fullPage = Args(("snapshot", "full-node-page")); fullPage["limit"] = 200;
            nodes = await Call("ai_nodes", fullPage); Assert.Equal(200, nodes["items"]!.AsArray().Count); Assert.True(nodes.ToJsonString().Length < 3_000_000);
            worst = worst with { Nodes = [], Diagnostics = Enumerable.Repeat(new Diagnostic("Warning", escaped), 32).ToArray() };
            viewport.SetAiNetworks(new("full-network-page", Enumerable.Range(0, 200).Select(i => worst with { Id = "network-" + i }).ToArray()));
            list = await Call("ai_networks", new() { ["preview"] = preview, ["limit"] = 200 });
            Assert.Equal(200, list["items"]!.AsArray().Count); Assert.True(list.ToJsonString().Length < 3_000_000);
            Assert.True(list["items"]![0]!["diagnosticsTruncated"]!.GetValue<bool>()); Assert.Equal(32, list["items"]![0]!["diagnosticCount"]!.GetValue<int>());
            Assert.Equal(100_000, nodes["items"]![0]!["attack_strategy"]!["characters"]!.GetValue<int>());
            var targets = Enumerable.Range(1, 98).Select(i => b with { Id = "target-" + i, Index = i, Position = new(30 + i, 0, 0), Links = [] }).ToArray();
            var manyLinks = Enumerable.Range(0, 100_000).Select(i => new AiLink(i, i % 98 + 1, targets[i % 98].Id, null)).ToArray();
            viewport.SetAiNetworks(new("large-links", [first with { Nodes = [a with { Links = manyLinks }, .. targets] }]));
            nodes = await Call("ai_nodes", Args(("snapshot", "large-links"), ("query", "node_00")));
            var limited = Assert.Single(nodes["items"]!.AsArray())!;
            Assert.Equal(100_000, limited["link_count"]!.GetValue<int>()); Assert.True(limited["links_truncated"]!.GetValue<bool>()); Assert.Equal(32, limited["links"]!.AsArray().Count);
            Assert.True(nodes.ToJsonString().Length < 10_000);
            viewport.SetAiOptions(true, true, null);
            var geometry = (System.Collections.IDictionary)typeof(SceneViewport).GetField("aiLinks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;
            var linksMesh = Assert.Single(geometry.Keys.Cast<HelixToolkit.Wpf.SharpDX.MeshGeometryModel3D>());
            Assert.Equal(32 * 9, linksMesh.Geometry!.Positions!.Count);
            Assert.True((await State())["ai"]!["linksTruncated"]!.GetValue<bool>());
            viewport.SetAiOptions(false, true, null);
            viewport.SetAiNetworks(new("graph1", [first, second]));
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
