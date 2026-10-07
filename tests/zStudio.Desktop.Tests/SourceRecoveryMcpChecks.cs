using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>
/// Resolving an interrupted save of a source project through the real named-pipe MCP connection and the GUI's path: the
/// files are read and moved off the UI thread while the workspace is disabled, an operation that can be cancelled stops
/// between two files, and closing zStudio waits for it to stop, so the journal always agrees with the files.
/// </summary>
internal static class SourceRecoveryMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Closed += (_, _) => closed.TrySetResult();
        SemaphoreSlim entered = new(0), proceed = new(0);
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            // A save that finished but was not cleaned up is reported (without a dialog) when the project opens; the
            // operation completes it.
            var cleanup = new SourcePublisher(fixture.Project) { Fault = (step, _) => { if (step == "cleanup") throw new SourcePublisher.Crash(); } };
            Assert.Throws<SourcePublisher.Crash>(() => cleanup.Publish([new("gamegen/note.gs", null, Encoding.ASCII.GetBytes("# note\r\n"))], "Committed save", token));
            fixture.Write("data/ordinary.lock", "keep visible");
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            Assert.DoesNotContain(main.ViewModel.Files, f => f.Path == fixture.Path(SourcePublisher.LockFile));
            Assert.Contains(main.ViewModel.Files, f => f.Path == fixture.Path("data/ordinary.lock"));
            for (int wait = 0; wait < 500 && !main.ViewModel.Problems.Any(p => p.Message.Contains("interrupted save", StringComparison.Ordinal)); wait++) await Task.Delay(10, token);
            string committed = (await Call("source_recovery", new()))["saves"]![0]!["id"]!.GetValue<string>();
            Assert.True((await Job("source_recovery_resolve", new() { ["save"] = committed, ["action"] = "complete" }))["resolved"]!.GetValue<bool>());
            Assert.DoesNotContain(main.ViewModel.Problems, p => p.Message.Contains("interrupted save", StringComparison.Ordinal));

            // An interrupted save of two new files, rolled back while the resolution is held at its first file (the last one).
            string a = fixture.Path("gamegen/a.gs"), b = fixture.Path("gamegen/b.gs");
            string first = Interrupt(fixture.Project, "gamegen/a.gs", "gamegen/b.gs");
            var resolve = main.ResolveSourceSave;
            bool? onUiThread = null;
            main.ResolveSourceSave = (root, id, action, t) => new SourcePublisher(root)
            {
                Fault = (step, index) =>
                {
                    if (step != "undo" || index != 1) return;
                    onUiThread = main.Dispatcher.CheckAccess(); entered.Release();
                    Assert.True(proceed.Wait(TimeSpan.FromSeconds(30)));
                },
            }.Resolve(id, action, t);
            var operation = await Call("source_recovery_resolve", new() { ["save"] = first, ["action"] = "roll_back" });
            string operationId = operation["id"]!.GetValue<string>();
            Assert.True(operation["Cancellable"]!.GetValue<bool>());
            await entered.WaitAsync(token);
            // The files are moved off the UI thread, which still answers; the workspace waits as during a save.
            Assert.False(onUiThread);
            Assert.Equal("running", (await Call("operation", new() { ["id"] = operationId }))["State"]!.GetValue<string>());
            Assert.NotNull(await Call("state", new()));
            Assert.False(main.IsEnabled);
            // The GUI's path is refused meanwhile rather than resolving the same files at once.
            var concurrent = Assert.IsType<StudioCommandException>(await Assert.ThrowsAnyAsync<Exception>(() => ResolveFromGui(main, fixture.Project, first)));
            Assert.Equal("busy", concurrent.Code);
            // Cancelled: the file being restored is finished, the next one is not touched.
            await Call("operation", new() { ["id"] = operationId, ["cancel"] = true });
            proceed.Release();
            var canceled = await Await(operationId, "canceled");
            Assert.Equal("canceled", canceled["code"]!.GetValue<string>());
            Assert.Contains("changed 1 file (gamegen/b.gs)", canceled["message"]!.GetValue<string>());
            Assert.Contains(main.ViewModel.Problems, p => p.Severity == "Warning" && p.Message.Contains("changed 1 file (gamegen/b.gs)", StringComparison.Ordinal));
            Assert.True(main.IsEnabled);
            Assert.True(File.Exists(a)); Assert.False(File.Exists(b));
            // The save still needs a decision, with the journal agreeing with the files; any action resolves it from there.
            var listed = (await Call("source_recovery", new()))["saves"]!.AsArray().Single()!;
            Assert.Equal(first, listed["id"]!.GetValue<string>());
            Assert.Equal(["gamegen/a.gs:After", "gamegen/b.gs:Before"], listed["files"]!.AsArray().Select(f => f!["file"]!.GetValue<string>() + ":" + f["state"]!.GetValue<string>()));
            main.ResolveSourceSave = resolve;
            var resolved = await Job("source_recovery_resolve", new() { ["save"] = first, ["action"] = "roll_back" });
            Assert.True(resolved["resolved"]!.GetValue<bool>());
            Assert.Equal(["gamegen/a.gs"], resolved["changed"]!.AsArray().Select(c => c!.GetValue<string>()));
            Assert.False(File.Exists(a));
            Assert.Equal(0, (await Call("source_recovery", new()))["saveCount"]!.GetValue<int>());

            // Maximum returned page with long, JSON-escaped paths. Journals are local fixtures; no source paths are created.
            var budgetFolders = new List<string>();
            try
            {
                string prefix = "data/" + string.Join('/', Enumerable.Repeat(new string('\u0401', 100), 20));
                for (int j = 0; j < 32; j++)
                {
                    string id = $"20260101T000000000Z-{j:x8}";
                    string folder = fixture.Path($"zstudio/recovery/{id}"); Directory.CreateDirectory(folder); budgetFolders.Add(folder);
                    var files = new JsonArray(Enumerable.Range(0, 64).Select(i => (JsonNode)new JsonObject
                    {
                        ["relative"] = $"{prefix}/{j}-{i}.zrd", ["expected"] = null,
                        ["content"] = new JsonObject { ["length"] = 1, ["sha256"] = new string('a', 64) },
                    }).ToArray());
                    File.WriteAllText(Path.Combine(folder, "manifest.json"), new JsonObject
                    {
                        ["format"] = 1, ["saveId"] = id, ["description"] = "budget", ["createdUtc"] = "2026-01-01T00:00:00Z",
                        ["files"] = files, ["folders"] = new JsonArray(),
                    }.ToJsonString());
                }
                var page = await Call("source_recovery", new());
                Assert.Equal(32, page["saveCount"]!.GetValue<int>());
                foreach (var save in page["saves"]!.AsArray())
                {
                    Assert.Equal(64, save!["fileCount"]!.GetValue<int>());
                    Assert.All(save["files"]!.AsArray(), row => { Assert.True(row!["fileTruncated"]!.GetValue<bool>()); Assert.InRange(row["file"]!.GetValue<string>().Length, 1, 257); });
                }
                Assert.InRange(Encoding.UTF8.GetByteCount(page.ToJsonString()), 1, 4 * 1024 * 1024);
            }
            finally { foreach (string folder in budgetFolders) Directory.Delete(folder, true); }

            // Closing while the GUI's path resolves another save waits until it has stopped between two files.
            string second = Interrupt(fixture.Project, "gamegen/c.gs", "gamegen/d.gs");
            onUiThread = null;
            main.ResolveSourceSave = (root, id, action, t) => new SourcePublisher(root)
            {
                Fault = (step, index) =>
                {
                    if (step != "undo" || index != 1) return;
                    onUiThread = main.Dispatcher.CheckAccess(); entered.Release();
                    Assert.True(proceed.Wait(TimeSpan.FromSeconds(30)));
                },
            }.Resolve(id, action, t);
            var gui = ResolveFromGui(main, fixture.Project, second);
            await entered.WaitAsync(token);
            Assert.False(onUiThread);
            main.Close();
            await Task.Delay(100, token);
            Assert.False(closed.Task.IsCompleted);
            proceed.Release();
            var stopped = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gui);
            Assert.Contains("changed 1 file (gamegen/d.gs)", stopped.Message);
            await closed.Task.WaitAsync(token);
            var left = Assert.Single(new SourcePublisher(fixture.Project).FindInterrupted(token));
            Assert.Equal(second, left.SaveId);
            Assert.Equal([new("gamegen/c.gs", SourceRecoveryFileState.After, false), new("gamegen/d.gs", SourceRecoveryFileState.Before, false)], left.Files);
            Assert.True(new SourcePublisher(fixture.Project).Resolve(second, SourceRecoveryAction.RollBack, token).Resolved);

            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, bool error = false)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True((result.IsError == true) == error, name + ": " + text);
                return error ? JsonValue.Create(text)! : JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Await(string id, string expected)
            {
                JsonNode job = await Call("operation", new() { ["id"] = id });
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed") => await Await((await Call(name, arguments))["id"]!.GetValue<string>(), expected);
        }
        finally
        {
            // Nothing stays held, whatever failed.
            proceed.Release(4);
            if (!closed.Task.IsCompleted)
            {
                foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
                main.Close();
            }
        }
    }

    /// <summary>A save of new files that stopped before it was committed: every file has the content it wrote.</summary>
    private static string Interrupt(string project, params string[] files)
    {
        var crashing = new SourcePublisher(project) { Fault = (step, _) => { if (step == "commit") throw new SourcePublisher.Crash(); } };
        Assert.Throws<SourcePublisher.Crash>(() => crashing.Publish([.. files.Select(f => new SourceFileWrite(f, null, Encoding.ASCII.GetBytes("# " + f + "\r\n")))], "Interrupted save", CancellationToken.None));
        return new SourcePublisher(project).FindInterrupted().Single(c => !c.Committed).SaveId;
    }

    /// <summary>Tools → Resolve interrupted save's roll back, as its dialog runs it.</summary>
    private static Task<SourceRecoveryResult> ResolveFromGui(MainWindow main, string project, string save) =>
        (Task<SourceRecoveryResult>)typeof(MainWindow).GetMethod("ResolveSourceRecoveryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(main, [project, save, SourceRecoveryAction.RollBack, CancellationToken.None])!;
}
