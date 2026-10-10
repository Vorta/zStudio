using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Shared history must not multiply into an unreadable workspace state when several missions are open.</summary>
internal static class SourceWorkspaceStateBoundsChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        string script = File.ReadAllText(fixture.Path("gamegen/m1.gs"));
        for (int i = 3; i <= 6; i++)
        {
            // These missions load the same real ground from m1, as shared source models allow.
            fixture.Write($"gamegen/m{i}.gs", script.Replace("..\\m1\\gamez.zbd", $"..\\m{i}\\gamez.zbd"));
            fixture.Write($"data/m{i}/models/unused.gltf", "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[]}");
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            for (int i = 1; i <= 6; i++) await Job("source_world_open", new() { ["mission"] = $"m{i}" });
            var workspace = main.ViewModel.Documents[0].SourceWorld!.Workspace;
            Assert.All(main.ViewModel.Documents, d => Assert.Same(workspace, d.SourceWorld!.Workspace));
            var empty = await Call("state", new());
            Assert.All(empty["documents"]!.AsArray(), d =>
            {
                var summary = d!["sourceWorld"]!["workspace"]!;
                Assert.False(summary["historyTruncated"]!.GetValue<bool>());
                Assert.False(summary["dirtyFilesTruncated"]!.GetValue<bool>());
            });

            // Legal paths with escaped JSON characters, 16 files per transaction, below every workspace limit.
            // Full history repeated once per mission previously made zstudio_state fail response_too_large.
            string[] paths = [.. Enumerable.Range(0, 16).Select(i => "data/" + new string('\u00e9', 240) + $"/f{i:D2}.gltf")];
            for (int i = 0; i < 32; i++)
                workspace.Apply("Blender update", paths.Select(p => (p, (byte[]?)Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"extras\":{\"revision\":" + i + "}}"))));
            var state = await Call("state", new());
            Assert.True(state.ToJsonString().Length < 128 * 1024);
            Assert.Equal(6, state["documents"]!.AsArray().Count);
            Assert.Equal(main.ViewModel.Documents.Select(d => d.SessionId.ToString()), state["documents"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()));
            Assert.All(state["documents"]!.AsArray(), d =>
            {
                var summary = d!["sourceWorld"]!["workspace"]!;
                Assert.Equal(workspace.Revision, summary["revision"]!.GetValue<long>());
                Assert.Equal(16, summary["dirtyFileCount"]!.GetValue<int>());
                Assert.Equal(32, summary["historyCount"]!.GetValue<int>());
                Assert.True(summary["canUndo"]!.GetValue<bool>());
                Assert.True(summary["dirtyFilesTruncated"]!.GetValue<bool>());
                Assert.True(summary["historyTruncated"]!.GetValue<bool>());
                Assert.Empty(summary["dirtyFiles"]!.AsArray()); Assert.Empty(summary["history"]!.AsArray());
            });
            // The separate shared details remain usable and retain the complete legal path identities here.
            var changes = await Call("source_changes", new());
            Assert.True(changes.ToJsonString().Length < 1024 * 1024);
            var details = changes["workspace"]!;
            Assert.False(details["historyTruncated"]!.GetValue<bool>());
            Assert.False(details["dirtyFilesTruncated"]!.GetValue<bool>());
            Assert.Equal(paths, details["dirtyFiles"]!.AsArray().Select(p => p!.GetValue<string>()));
            Assert.Equal(32, details["history"]!.AsArray().Count);
            Assert.All(details["history"]!.AsArray(), h => Assert.Equal(paths, h!["files"]!.AsArray().Select(p => p!.GetValue<string>())));
            Assert.Equal(32, workspace.History.Count); Assert.True(workspace.IsDirty);
            foreach (string path in paths) Assert.False(File.Exists(fixture.Path(path)));

            // Fill the workspace envelope, then request the maximum escaped diff through the real transport.
            // Both versions are valid comment-only ZRD text; 500 replacements have 1,000 changed lines.
            for (int group = 1; group < 4; group++)
                workspace.Apply(new string('&', 140), Enumerable.Range(0, 16).Select(i =>
                    ($"data/{new string('&', 240)}/g{group}f{i:D2}.zrd", (byte[]?)Encoding.ASCII.GetBytes("()"))));
            string diffPath = "data/" + new string('&', 190) + "/changes.zrd";
            string[] baselineLines = [.. Enumerable.Range(0, 2000).Select(i => $"#{i:D4} {new string('&', 390)}A")];
            byte[] baseline = Encoding.ASCII.GetBytes(string.Join('\n', baselineLines));
            byte[] edited = Encoding.ASCII.GetBytes(string.Join('\n', baselineLines.Select((line, i) => i % 4 == 0 ? line[..^1] + "B" : line)));
            fixture.Write(diffPath, baseline);
            workspace.Apply("Escaped diff", [(diffPath, edited)]);
            foreach (int? requested in new int?[] { null, 1, 2000 })
            {
                var arguments = new Dictionary<string, object?> { ["file"] = diffPath };
                if (requested != null) arguments["maxLines"] = requested.Value;
                var reply = await Call("source_changes", arguments);
                Assert.True(reply.ToJsonString().Length < 2 * 1024 * 1024);
                Assert.Equal(diffPath, reply["diff"]!["File"]!.GetValue<string>());
                Assert.Equal(1000, reply["diff"]!["ChangedLines"]!.GetValue<int>());
                Assert.True(reply["diff"]!["Truncated"]!.GetValue<bool>());
                int shown = reply["diff"]!["Lines"]!.AsArray().Count;
                if (requested == 2000) Assert.InRange(shown, 201, 1999);
                else Assert.Equal(requested ?? 200, shown);
                Assert.Equal(65, reply["workspace"]!["dirtyFileCount"]!.GetValue<int>());
                Assert.True(reply["workspace"]!["dirtyFilesTruncated"]!.GetValue<bool>());
                Assert.Equal(32, reply["workspace"]!["history"]!.AsArray().Count);
            }
            Assert.Equal(baseline, File.ReadAllBytes(fixture.Path(diffPath)));
            Assert.Equal(edited, workspace.Read(diffPath, token));

            // A read-only diff yields while its summary is frozen. Accepted edits/undo and root replacement
            // must refuse the old read, rather than pair different revisions or projects in one response.
            byte[] revised = edited.ToArray(); revised[^1] = (byte)'C';
            await RefuseChangedDiff(() =>
            {
                workspace.Apply("Concurrent accepted edit", [(diffPath, revised)]);
                return Task.CompletedTask;
            });
            var current = await Call("source_changes", new() { ["file"] = diffPath, ["maxLines"] = 1 });
            Assert.Equal(workspace.Revision, current["workspace"]!["revision"]!.GetValue<long>());
            Assert.Equal(revised.Length, current["diff"]!["WorkingBytes"]!.GetValue<int>());
            await RefuseChangedDiff(async () =>
            {
                var owner = main.ViewModel.Documents.First(d => d.SourceWorld != null);
                await Call("undo_redo", new() { ["document"] = owner.SessionId.ToString(), ["revision"] = owner.Revision, ["action"] = "undo" });
            });
            Assert.Equal(edited, workspace.Read(diffPath, token));
            current = await Call("source_changes", new() { ["file"] = diffPath, ["maxLines"] = 1 });
            Assert.Equal(workspace.Revision, current["workspace"]!["revision"]!.GetValue<long>());

            foreach (var document in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(document);
            using var other = new SourceWorldFixture(); other.Write(diffPath, "()");
            await RefuseChangedDiff(async () => { await Job("open_root", new() { ["path"] = other.Project, ["project"] = true }); });
            current = await Call("source_changes", new() { ["file"] = diffPath });
            Assert.Equal(other.Project, current["project"]!.GetValue<string>());
            Assert.Equal(0, current["diff"]!["ChangedLines"]!.GetValue<int>());

            async Task RefuseChangedDiff(Func<Task> change)
            {
                TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using SemaphoreSlim proceed = new(0);
                main.SourceChangesReading = ct =>
                {
                    Assert.False(main.Dispatcher.CheckAccess()); entered.TrySetResult();
                    Assert.True(proceed.Wait(TimeSpan.FromSeconds(30), ct), "The diff barrier was not released.");
                };
                var pending = client.CallToolAsync("zstudio_source_changes", new Dictionary<string, object?> { ["file"] = diffPath, ["maxLines"] = 1 }, cancellationToken: token);
                try { await entered.Task.WaitAsync(token); await change(); }
                finally { main.SourceChangesReading = null; proceed.Release(); }
                var result = await pending;
                Assert.True(result.IsError);
                Assert.Equal("context_changed", result.StructuredContent!.Value.GetProperty("code").GetString());
            }

            async Task<JsonNode> Call(string name, Dictionary<string, object?> args)
            {
                var result = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: token);
                string text = result.Content.OfType<TextContentBlock>().Single().Text;
                Assert.False(result.IsError == true, text);
                if (name == "source_changes")
                {
                    // The service cap applies to Data; MCP also transports text and structured copies.
                    Assert.True(System.Text.Json.JsonSerializer.Serialize(result).Length < 8 * 1024 * 1024);
                    Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(result.StructuredContent!.Value.GetRawText())));
                }
                return JsonNode.Parse(text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> args)
            {
                var job = await Call(name, args); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running")
                { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.Equal("completed", job["State"]!.GetValue<string>()); return job["result"]!;
            }
        }
        finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); }
    }
}
