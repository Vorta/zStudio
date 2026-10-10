using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
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
/// Saving a source project through the real named-pipe MCP connection and the GUI's Save: the files are replaced off the
/// UI thread, which keeps answering operation and state requests; the project accepts no other change meanwhile and says it
/// is being saved; a disk check during the save does not take the half-written files for another program's change; and
/// closing zStudio waits for the save to finish before deciding the (then saved) documents.
/// </summary>
internal static class SourceSaveMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false };
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Closed += (_, _) => closed.TrySetResult();
        SemaphoreSlim entered = new(0), proceed = new(0);
        bool hold = false; bool? onUiThread = null;
        // The project's saves use the real publisher; a held one stops as it starts replacing its second file, after the first
        // was replaced (past the point where cancellation is honoured).
        main.SourceSaver = (writes, description, t) => new SourcePublisher(fixture.Project)
        {
            Fault = (step, index) =>
            {
                if (!hold || step != "intent" || index != 1) return;
                onUiThread = main.Dispatcher.CheckAccess(); entered.Release();
                // A save on the UI thread blocks the test that would release it: fail instead of hanging.
                if (!proceed.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("The held save was never released.");
            },
        }.Publish([.. writes.Select(w => new SourceFileWrite(w.Relative, w.Expected, w.Content))], description, t).Written;
        main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            string script = fixture.Path("gamegen/m1.gs"), list = fixture.Path("data/m1/zrdr/anim.zad");
            byte[] scriptBefore = await File.ReadAllBytesAsync(script, token), listBefore = await File.ReadAllBytesAsync(list, token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var opened = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);
            // The addition changes the script and the animation list: a save of two files.
            var added = Document((await Job("source_world_add_model", new() { ["document"] = Id(opened), ["revision"] = opened.Revision, ["model"] = fixture.Tank, ["name"] = "tank" }))["document"]!);
            Assert.Equal(["data/m1/zrdr/anim.zad", "gamegen/m1.gs"], added.SourceWorld!.Workspace.DirtyFiles);

            // MCP save_document: the operation and reads are answered while the files are replaced.
            hold = true;
            string operation = (await Call("save_document", new() { ["document"] = Id(added), ["revision"] = added.Revision }))["id"]!.GetValue<string>();
            await entered.WaitAsync(token);
            Assert.False(onUiThread);
            Assert.Equal("running", (await Call("operation", new() { ["id"] = operation }))["State"]!.GetValue<string>());
            var shown = (await Call("state", new()))["documents"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == Id(added))!;
            Assert.True(shown["sourceWorld"]!["workspace"]!["saving"]!.GetValue<bool>());
            Assert.True(added.IsDirty); Assert.False(main.IsEnabled);
            await Refused(added);
            // The first file is already replaced; a disk check meanwhile is not another program's change.
            Assert.NotEqual(listBefore, await File.ReadAllBytesAsync(list, token));
            await main.ViewModel.CheckExternalChangesAsync();
            Assert.False(added.IsStale);
            // MCP stopping now (it cancels its operations) no longer stops a save that is replacing files: it finishes, and
            // the operation reports what it wrote.
            var operations = (System.Collections.IDictionary)typeof(MainWindow).GetField("automationOperations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            var job = operations[Guid.Parse(operation)]!;
            ((CancellationTokenSource)job.GetType().GetProperty("Cancellation")!.GetValue(job)!).Cancel();
            proceed.Release();
            var saved = await Await(operation, "completed");
            Assert.Equal(["data/m1/zrdr/anim.zad", "gamegen/m1.gs"], saved["written"]!.AsArray().Select(w => w!.GetValue<string>()).Order(StringComparer.Ordinal));
            Assert.False(added.IsDirty); Assert.False(added.SourceWorld.Workspace.IsSaving); Assert.True(main.IsEnabled);
            Assert.Contains("LoadGameGen tank.gltf tank\r\n", await File.ReadAllTextAsync(script, token));
            await main.ViewModel.CheckExternalChangesAsync();
            Assert.False(added.IsStale);
            Assert.Empty(new SourcePublisher(fixture.Project).FindInterrupted(token));

            // The GUI's Save (title bar, Ctrl+S, Properties and close prompts): MCP changes are refused meanwhile, and closing
            // zStudio waits for the save before deciding the documents, which are then saved.
            var undone = Document(await Call("undo_redo", new() { ["document"] = Id(added), ["revision"] = added.Revision, ["action"] = "undo" }));
            Assert.True(undone.IsDirty);
            onUiThread = null;
            var gui = SourceTask<bool>("SaveCurrentAsync", undone, false);
            await entered.WaitAsync(token);
            Assert.False(onUiThread); Assert.False(main.IsEnabled);
            Assert.False(((UIElement)main.FindName("DocumentSave")).IsEnabled); Assert.False(((UIElement)main.FindName("DocumentUndo")).IsEnabled);
            Assert.StartsWith("Saving 2 changed source files", main.ViewModel.Status);
            string refused = (await Call("undo_redo", new() { ["document"] = Id(undone), ["revision"] = undone.Revision, ["action"] = "redo" }, error: true)).GetValue<string>();
            Assert.Contains("\"busy\"", refused);
            Assert.True((await Call("source_changes", new()))["workspace"]!["saving"]!.GetValue<bool>());
            await Refused(undone);
            main.Close();
            await Task.Delay(200, token);
            // Neither closed nor asking: the documents are decided once the save has finished.
            Assert.False(closed.Task.IsCompleted); Assert.Empty(main.OwnedWindows);
            Assert.Contains("save to finish", main.ViewModel.Status);
            proceed.Release();
            Assert.True(await gui.WaitAsync(token));
            await closed.Task.WaitAsync(token);
            Assert.Equal(scriptBefore, await File.ReadAllBytesAsync(script, token));
            Assert.Equal(listBefore, await File.ReadAllBytesAsync(list, token));
            Assert.Empty(new SourcePublisher(fixture.Project).FindInterrupted(token));

            // While the project is saved, the GUI's own edit paths refuse with the reason.
            async Task Refused(DocumentModel doc)
            {
                foreach (var refusal in new Func<Task>[]
                {
                    () => SourceTask<DocumentModel>("UndoSourceWorldAsync", doc, false, CancellationToken.None),
                    () => SourceTask<DocumentModel>("AddSourceModelAsync", doc, new SourceWorldAddition(new(fixture.Tank, "tank2"), []), CancellationToken.None),
                    () => SourceTask<DocumentModel>("ReloadSourceWorldAsync", doc, false, CancellationToken.None),
                    () => SourceTask<IReadOnlyList<string>>("SaveSourceWorldAsync", doc, CancellationToken.None),
                })
                {
                    var busy = await Assert.ThrowsAsync<StudioCommandException>(refusal);
                    Assert.Equal("busy", busy.Code);
                    Assert.Contains("being saved", busy.Message);
                }
            }
            Task<T> SourceTask<T>(string method, params object[] arguments)
            {
                try { return (Task<T>)typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, arguments)!; }
                catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException); throw; }
            }
            DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == state["id"]!.GetValue<string>());
            static string Id(DocumentModel d) => d.SessionId.ToString();
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
            // Nothing stays held or asking, whatever failed.
            hold = false; proceed.Release(4);
            foreach (Window owned in main.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            if (!closed.Task.IsCompleted)
            {
                foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
                main.Close();
            }
        }
    }
}
