using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class AiValveMcpChecks
{
    internal static async Task Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-valves-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var token = timeout.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            ZrdNode A(params ZrdNode[] c) => ZrdNode.Create(ZrdKind.Array) with { Children = c };
            ZrdNode S(string s) => ZrdNode.Create(ZrdKind.String) with { Text = s };
            ZrdNode I(int i) => ZrdNode.Create(ZrdKind.Int) with { Bits = unchecked((uint)i) };
            ZrdNode F() => ZrdNode.Create(ZrdKind.Float);
            string longName = new string('x', 1_000_000) + "needle_tail";
            var definitions = A(S("go"), A(S("sound"), A(S("sample"), I(1))), S("go"), A(S("all_nonzero"), S("namelist"), A(S("ready"))), S(longName), A(S("sound"), A(S("sample"), I(1))));
            var network = A([S("version"), A(I(106)), ..Enumerable.Range(0, 40).SelectMany(i => new[] { S("node_" + i.ToString("00")), A(I(1), A(F(), F(), F()), A(I(-1)), S("valve"), A(I(1), S("go"))) })]);
            var objectives = A(S("objective"), A(S("set_valve"), A(S("go"), I(1))));
            byte[] original = MotionFixture.Archive(("valves.zrd", ZrdWriter.Write(definitions, token)), ("net_01.zrd", ZrdWriter.Write(network, token)), ("objectives.zrd", ZrdWriter.Write(objectives, token)), ("unknown", new byte[] { 9, 3, 7 }));
            string path = Path.Combine(folder, "reader.zbd"), saved = Path.Combine(folder, "saved.zbd"); await File.WriteAllBytesAsync(path, original, token);
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await Job("open_document", new() { ["path"] = path }); var doc = main.ViewModel.Documents.Single(); var edits = doc.ResourceEdits!;
            Guid member = edits.Current.Members[0].Id, net = edits.Current.Members[1].Id;
            var rejected = await Job("ai_valve_edit", Args(("member", member), ("action", "add_record"), ("value", "bad"), ("kind", "valve_assign")), "failed");
            Assert.Contains("supported valve action or compound condition", rejected.ToJsonString());
            Assert.Equal(0, doc.Revision); Assert.False(doc.IsDirty);
            Assert.Equal(original, edits.Current.Document.Bytes.ToArray());
            var page = await Job("ai_valves", Args(("member", member)));
            var rows = page["records"]!["items"]!.AsArray(); Assert.Equal(3, rows.Count);
            var found = await Job("ai_valves", Args(("member", member), ("query", "NEEDLE_TAIL"), ("limit", 1)));
            var longRow = Assert.Single(found["records"]!["items"]!.AsArray())!;
            Assert.Equal(longName.Length, longRow["nameCharacters"]!.GetValue<int>());
            Assert.True(longRow["name"]!.GetValue<string>().Length <= 257);
            Guid first = Guid.Parse(rows[0]!["record"]!.GetValue<string>()), second = Guid.Parse(rows[1]!["record"]!.GetValue<string>()); Assert.NotEqual(first, second);
            await Job("ai_valve_edit", Args(("member", member), ("record", first), ("action", "add_action"), ("kind", "delayupdate")));
            Assert.Equal(1, doc.Revision);
            var record = MissionAiValves.Records("valves.zrd", edits.Tree(edits.Member(member), token), token).First();
            await Job("ai_valve_edit", Args(("member", member), ("record", first), ("operand", record.Value.Children[3].Children[0].Id), ("action", "set"), ("value", "\"ready\"")));
            Assert.Equal(2, doc.Revision);
            var stale = Args(("member", member), ("record", first), ("action", "delete")); stale["revision"] = 0;
            Assert.Equal("revision_conflict", (await Job("ai_valve_edit", stale, "failed"))["code"]!.GetValue<string>());
            await Job("resource_properties", Args(("member", member), ("node", first), ("valves", true), ("action", "open")));
            var pinned = main.OpenPropertiesWindow!.ResourceFields!; Assert.True(pinned.ValveMode); Assert.Equal(first.ToString(), pinned.Json["valveRecord"]!.GetValue<string>());
            Button FindButton(string label) => Descendants(main.OpenPropertiesWindow!.ResourceFields!).OfType<Button>().Single(b => Equals(b.Content, label));
            FindButton("Find uses: go").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Descendants(pinned).OfType<Button>().Any(b => Equals(b.Content, "Next references")));
            FindButton("Next references").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Descendants(pinned).OfType<Button>().Any(b => b.Content?.ToString()?.StartsWith("objectives.zrd", StringComparison.Ordinal) == true));
            Descendants(pinned).OfType<Button>().Single(b => b.Content?.ToString()?.StartsWith("objectives.zrd", StringComparison.Ordinal) == true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => main.OpenPropertiesWindow?.ResourceFields?.MemberId == edits.Current.Members[2].Id);
            Assert.Equal(doc, main.OpenPropertiesWindow!.Document);
            await Job("ai_valve_edit", Args(("member", member), ("action", "add_record"), ("value", "valveunion"), ("kind", "sound")));
            var named = MissionAiValves.Records("valves.zrd", edits.Tree(edits.Member(member), token), token).Last();
            await Job("resource_properties", Args(("member", member), ("node", named.Id), ("valves", true), ("action", "open")));
            Assert.DoesNotContain(Descendants(main.OpenPropertiesWindow.ResourceFields!).OfType<Button>(), b => Equals(b.Content, "Append term"));
            await Job("ai_valve_edit", Args(("member", member), ("record", named.Id), ("operand", named.Value.Children[0].Id), ("action", "delete_item")));
            await Until(() => Descendants(main.OpenPropertiesWindow.ResourceFields!).OfType<Button>().Any(b => Equals(b.Content, "Append action")));
            long beforeAppend = doc.Revision;
            var append = FindButton("Append action");
            append.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            // Acceptance changes Revision before dependent preview refresh finishes.
            // The button is re-enabled only after that GUI transaction releases its guard.
            await Until(() => doc.Revision == beforeAppend + 1 && append.IsEnabled);
            Assert.Equal(2, MissionAiValves.Records("valves.zrd", edits.Tree(edits.Member(member), token), token).Last().Value.Children.Count);
            await Job("resource_properties", Args(("member", net), ("valves", true), ("action", "open")));
            pinned = main.OpenPropertiesWindow.ResourceFields!;
            FindButton("Next binding targets").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Descendants(pinned).OfType<Button>().Any(b => b.Content?.ToString()?.StartsWith("Add valve to node_16 ", StringComparison.Ordinal) == true));
            var targets = await Job("ai_valves", Args(("member", net), ("section", "targets"), ("offset", 16), ("limit", 16)));
            Assert.Equal(16, targets["records"]!["items"]!.AsArray().Count);
            Guid target = Guid.Parse(targets["records"]!["items"]![0]!["Id"]!.GetValue<string>());
            await Job("ai_valve_edit", Args(("member", net), ("action", "add_binding"), ("operand", target)));
            Assert.Equal(41, MissionAiValves.Records("net_01.zrd", edits.Tree(edits.Member(net), token), token).Count());
            await Call("undo_redo", Args(("action", "undo")));
            Assert.Equal(40, MissionAiValves.Records("net_01.zrd", edits.Tree(edits.Member(net), token), token).Count());
            await Call("undo_redo", Args(("action", "redo")));
            await Job("save_document", Args(("destination", saved))); Assert.False(doc.IsDirty);
            Assert.Equal(original, await File.ReadAllBytesAsync(path, token));
            var reopened = await FormatRegistry.Default.OpenAsync(saved, token);
            Assert.Equal(new byte[] { 9, 3, 7 }, reopened.Slice(reopened.Assets[3].Offset, reopened.Assets[3].Length).ToArray());
            Assert.Equal(41, MissionAiValves.Records("net_01.zrd", (ZrdNode)reopened.Assets[1].Content!, token).Count());
            // Asset Properties picks the semantic valve editor from authored valve structure, not a shared member name.
            await Call("properties_open", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Zrd", ["index"] = 2 });
            Assert.True(main.OpenPropertiesWindow!.ResourceFields!.ValveMode);
            var recoilObjectives = A(S("objective"), A(S("text"), S("Destroy the base"), S("kill"), A(S("target"), I(1))));
            using (var recoil = new DocumentModel(FormatRegistry.Default.OpenBytes(Path.Combine(folder, "zrdr.zbd"), MotionFixture.Archive(("objectives.zrd", ZrdWriter.Write(recoilObjectives, token))), token: token)))
            {
                main.ViewModel.Documents.Add(recoil);
                try
                {
                    await Call("properties_open", new() { ["document"] = recoil.SessionId.ToString(), ["kind"] = "Zrd", ["index"] = 0 });
                    var generic = main.OpenPropertiesWindow!.ResourceFields!; Assert.False(generic.ValveMode);
                    var described = System.Text.Json.JsonSerializer.SerializeToNode(generic.DescribeAutomationFields())!;
                    Assert.Contains(described["fields"]!.AsArray(), f => f!["Label"]!.GetValue<string>() == "Name");
                }
                finally { main.ViewModel.CloseResolved(recoil); }
            }
            await Call("close_document", Args()); Assert.Null(main.OpenPropertiesWindow);

            async Task Until(Func<bool> ready) { while (!ready()) await Task.Delay(10, token); }
            Dictionary<string, object?> Args(params (string Key, object Value)[] values)
            { var args = new Dictionary<string, object?> { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision }; foreach (var (key, value) in values) args[key] = value is Guid g ? g.ToString() : value; return args; }
            async Task<JsonNode> Call(string tool, Dictionary<string, object?> args)
            {
                var result = await client.CallToolAsync("zstudio_" + tool, args, cancellationToken: token);
                string json = result.Content.OfType<TextContentBlock>().Single().Text; Assert.False(result.IsError == true, json); return JsonNode.Parse(json)!;
            }
            async Task<JsonNode> Job(string tool, Dictionary<string, object?> args, string expected = "completed")
            {
                if (tool == "ai_valves") args.Remove("revision");
                var result = await Call(tool, args); string id = result["id"]!.GetValue<string>();
                while (result["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); result = await Call("operation", new() { ["id"] = id }); }
                Assert.True(result["State"]!.GetValue<string>() == expected, result.ToJsonString()); return result["result"]!;
            }
        }
        finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); Directory.Delete(folder, true); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
}
