using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Mcp;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>
/// The World tree's Animations section over MCP (design/world-tree-animations.md §12): the map's files directly under the
/// section and Common after them, how each definition starts and what it acts on, binding rows by the name calls use, a
/// repeated name, Group by start, what a search matched, the selected object's mark, Frame refused without a drawn object,
/// and the opened and kept marks. Must-have: the GUI/MCP contract of the redesigned section, on a fixture map.
/// </summary>
internal static class AnimationTreeMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        // ground, and sgate1 and sgate2 sharing a gate: a startup animation calls the first binding of a pattern on the gates; a
        // definition takes damage; sink is defined twice; lost binds nothing; Common's tank_die binds nothing in m1.
        fixture.WriteSharedDatabase();
        fixture.Write("data/m1/zrdr/anim.zad", """
            ( ANIMATION_DEFINITIONS ( GRAVITY ( -9.8 ) ANIMATION_LIST (
              ANIMATION_DEFINITION_FILE ( "..\\data\\m1\\zrdr\\gates.zad" )
              ANIMATION_DEFINITION_FILE ( "..\\data\\m1\\zrdr\\sink.zad" )
              ANIMATION_DEFINITION_FILE ( "..\\data\\common\\zrdr\\enemies\\tank.zad" ) ) ) )
            """);
        fixture.Write("data/m1/zrdr/gates.zad", """
            ( ANIMATION_DEFINITIONS ( ANIMATION_LIST (
              ANIMATION_DEFINITION ( NAME ( ground ) ANIMATION_NAME ( start_gates ) ACTIVATION ( ON_STARTUP )
                SEQUENCE_DEFINITION ( NAME ( go ) CALL_ANIMATION ( NAME ( open_gate1 ) ) ) )
              ANIMATION_DEFINITION ( NAME ( "sgate*" ) ANIMATION_NAME ( "open_gate*" ) ACTIVATION ( ON_CALL )
                SEQUENCE_DEFINITION ( NAME ( lift ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( INACTIVE ) ) ) )
              ANIMATION_DEFINITION ( NAME ( ground ) ANIMATION_NAME ( hit_ground ) HEALTH ( 1 )
                SEQUENCE_DEFINITION ( NAME ( crack ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( INACTIVE ) ) ) ) ) ) )
            """);
        fixture.Write("data/m1/zrdr/sink.zad", """
            ( ANIMATION_DEFINITIONS ( ANIMATION_LIST (
              ANIMATION_DEFINITION ( NAME ( ground ) ANIMATION_NAME ( sink ) ACTIVATION ( ON_CALL )
                SEQUENCE_DEFINITION ( NAME ( down ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( INACTIVE ) ) ) )
              ANIMATION_DEFINITION ( NAME ( ground ) ANIMATION_NAME ( sink ) ACTIVATION ( ON_CALL )
                SEQUENCE_DEFINITION ( NAME ( down ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( INACTIVE ) ) ) )
              ANIMATION_DEFINITION ( NAME ( nowhere ) ANIMATION_NAME ( lost ) ACTIVATION ( ON_CALL )
                SEQUENCE_DEFINITION ( NAME ( none ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( INACTIVE ) ) ) ) ) ) )
            """);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            await Job("source_world_open", new() { ["mission"] = "m1" });
            await Ready();

            // No map scope row: the map's files, then Common, then Objects placed by animations.
            var top = await Children(await Section());
            Assert.Equal(["gates.zad", "sink.zad", "Common · 1", "Objects placed by animations · 0"], top.Select(Label));
            Assert.Equal("Common", top[2]!["sectionRow"]!["definitionScope"]!.GetValue<string>());
            var gates = await Children(top[0]!);
            JsonNode Definition(JsonArray rows, string name) => rows.Single(r => Label(r) == name)!["sectionRow"]!;
            var start = Definition(gates, "start_gates");
            Assert.Equal("startup", start["start"]!["kind"]!.GetValue<string>());
            Assert.Equal("ground", start["target"]!["text"]!.GetValue<string>());
            var open = Definition(gates, "open_gate*");
            Assert.Equal("called", open["start"]!["kind"]!.GetValue<string>());
            Assert.Equal("start_gates", open["start"]!["callers"]![0]!["name"]!.GetValue<string>());
            Assert.Equal(["objects", "sgate1–2"], new[] { open["target"]!["kind"]!.GetValue<string>(), open["target"]!["text"]!.GetValue<string>() });
            Assert.Equal(2, open["bindings"]!.GetValue<int>());
            Assert.Equal("damage", Definition(gates, "hit_ground")["start"]!["kind"]!.GetValue<string>());
            // Binding rows lead with the name calls use.
            var bindings = await Children(gates.Single(r => Label(r) == "open_gate*")!);
            Assert.Equal(["open_gate1", "open_gate2"], bindings.Select(b => b!["sectionRow"]!["name"]!.GetValue<string>()));
            Assert.Equal(["open_gate1", "open_gate2"], bindings.Select(Label));
            // The second sink: an earlier definition has its name. lost binds nothing in this map, and has nothing to frame.
            var sink = await Children(top[1]!);
            Assert.Null(sink[0]!["sectionRow"]!["duplicateOf"]);
            Assert.Equal(0, sink[1]!["sectionRow"]!["duplicateOf"]!["ordinal"]!.GetValue<int>());
            var lost = sink.Single(r => Label(r) == "lost")!;
            Assert.Equal(["none", "not in this map", "none"], new[] { lost["sectionRow"]!["start"]!["kind"]!.GetValue<string>(), lost["sectionRow"]!["target"]!["text"]!.GetValue<string>(), lost["sectionRow"]!["target"]!["kind"]!.GetValue<string>() });
            var refused = await Refused("scene_tree", Tree(new() { ["action"] = "frame", ["row"] = lost["row"]!.GetValue<string>() }));
            Assert.Equal("not_ready", refused["code"]!.GetValue<string>());
            Assert.Contains("drawn in the view", refused["message"]!.GetValue<string>(), StringComparison.Ordinal);

            // Group by start: one row per kind, the map's and then Common's.
            await Job("scene_options", new() { ["preview"] = PreviewId(), ["changes"] = new Dictionary<string, object?> { ["animationsByStart"] = true } });
            Assert.Contains("\"animationsByStart\":true", (await Call("preview_state", new() { ["preview"] = PreviewId() })).ToJsonString(), StringComparison.Ordinal);
            var groups = (await Children(await Section())).Where(r => r!["sectionRow"]?["kind"]?.GetValue<string>() == "definitionGroup").ToArray();
            Assert.Equal(["startup 1", "damage 1", "called 1", "none 3"], groups.Select(g => $"{g!["sectionRow"]!["start"]} {g["sectionRow"]!["count"]}"));
            Assert.All(groups, g => Assert.Equal("map", g!["sectionRow"]!["scope"]!.GetValue<string>()));
            await Job("scene_options", new() { ["preview"] = PreviewId(), ["changes"] = new Dictionary<string, object?> { ["animationsByStart"] = false } });

            // A search says what matched: NAME (sgate*) first.
            var found = await Call("scene_tree", new() { ["document"] = Id(), ["search"] = "sgate" });
            var match = found["matches"]!["items"]!.AsArray().Single(m => Label(m!["item"]) == "open_gate*")!;
            Assert.Equal("rootName", match["matchedBy"]![0]!.GetValue<string>());

            // The selected object marks the definitions bound to it (and the closed file holding them).
            int gate = (await Call("scene_tree", new() { ["document"] = Id(), ["search"] = "sgate1", ["types"] = "group,object,node,empty,geometry" }))["matches"]!["items"]!.AsArray()
                .First(m => Label(m!["item"]) == "sgate1")!["item"]!["node"]!.GetValue<int>();
            await Job("scene_tree", Tree(new() { ["action"] = "select", ["node"] = gate }));
            top = await Children(await Section());
            Assert.True(top[0]!["sectionRow"]!["affectsSelection"]!.GetValue<bool>());
            gates = await Children(top[0]!);
            Assert.True(Definition(gates, "open_gate*")["affectsSelection"]!.GetValue<bool>());
            Assert.False(Definition(gates, "start_gates")["affectsSelection"]!.GetValue<bool>());

            // Open presses its row's Open; after Exit its row carries the kept scenario.
            int opened = Definition(gates, "open_gate*")["definition"]!.GetValue<int>();
            await Mode("open", new() { ["definition"] = opened });
            Assert.True(Definition(await Children((await Children(await Section()))[0]!), "open_gate*")["opened"]!.GetValue<bool>());
            await Mode("exit");
            var kept = Definition(await Children((await Children(await Section()))[0]!), "open_gate*");
            Assert.True(kept["kept"]!.GetValue<bool>()); Assert.False(kept["opened"]!.GetValue<bool>());

            string Id() => main.ViewModel.Documents.Single(d => d.SourceWorld != null).SessionId.ToString();
            string PreviewId() => typeof(MainWindow).GetField("previewId", flags)!.GetValue(main)!.ToString()!;
            string Context() => ((SceneTreeModel)typeof(MainWindow).GetField("sceneTree", flags)!.GetValue(main)!).Context;
            Dictionary<string, object?> Tree(Dictionary<string, object?> args) { args["document"] = Id(); args["context"] = Context(); args["preview"] = PreviewId(); return args; }
            static string Label(JsonNode? row) => row?["Label"]?.GetValue<string>() ?? "";
            async Task<JsonNode> Section() => (await Call("scene_tree", Tree(new())))["children"]!["items"]!.AsArray().Single(r => r!["sectionRow"]?["section"]?.GetValue<string>() == "definitions")!;
            async Task<JsonArray> Children(JsonNode row) => (await Call("scene_tree", Tree(new() { ["row"] = row["row"]!.GetValue<string>() })))["children"]!["items"]!.AsArray();
            async Task Mode(string action, Dictionary<string, object?>? extra = null)
            {
                var args = extra ?? new();
                args["document"] = Id(); args["revision"] = main.ViewModel.Documents.Single(d => d.SourceWorld != null).Revision; args["preview"] = PreviewId(); args["action"] = action;
                await Job("animation_mode", args);
            }
            async Task Ready() => await ((Task)typeof(MainWindow).GetField("previewWork", flags)!.GetValue(main)!).WaitAsync(token);
            async Task<JsonNode> Call(string name, Dictionary<string, object?> args)
            {
                var result = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.False(result.IsError == true, name + ": " + text); return JsonNode.Parse(text)!;
            }
            async Task<JsonNode> Refused(string name, Dictionary<string, object?> args)
            {
                var result = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True(result.IsError == true, name + ": " + text); return JsonNode.Parse(text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> args, string expected = "completed")
            {
                var job = await Call(name, args);
                if (job["State"] == null) return job;
                string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            foreach (var document in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(document);
            main.Close();
        }
    }
}
