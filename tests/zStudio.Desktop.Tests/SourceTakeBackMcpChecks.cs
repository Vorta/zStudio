using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>
/// Edits a source world's rebuild takes back, through the real named-pipe MCP connection: a move and a copy in the mission
/// database's glTF files that would make a script instruction act on another node. The shown world, the workspace with its
/// undo and redo steps, and Problems stay as they were; the status says the edit was taken back (as it says that a world
/// did not open or reload). An accepted copy shows its notes, and the lookup by name it changed is counted as well as
/// listed in Problems.
/// </summary>
internal static class SourceTakeBackMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        // Two copies of a part (a crate with a lid, and a post) and a second ground, the newest, which the script marks;
        // FindNode spare finds nothing until a node takes that name. The mission's texture effects find the lid.
        fixture.WritePartDatabase(secondGround: true);
        string script = Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs")));
        fixture.Write("gamegen/m1.gs", script.Replace("# no vehicles", "FindNode ground\r\nSetLandmark on\r\nFindNode spare\r\nSetLandmark on", StringComparison.Ordinal));
        fixture.Write("gamegen/m1_zbd.gs", "source support\\tex_fxm1.gw\r\nQuit\r\n");
        fixture.Write("gamegen/support/tex_fxm1.gw", "FindNode lid\r\nQuit\r\n");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var doc = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);
            Assert.StartsWith("Built the m1 world from its sources", main.ViewModel.Status);
            var workspace = doc.SourceWorld!.Workspace;
            await Preview();

            // A step to redo: a flag of the part's post, undone.
            var flagged = Document((await Job("source_world_object_edit", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["node"] = Node(doc, "post"), ["flag"] = "0x10000", ["on"] = true }))["document"]!);
            var shown = Document(await Call("undo_redo", new() { ["document"] = Id(flagged), ["revision"] = flagged.Revision, ["action"] = "undo" }));
            Assert.Equal("Set flag 0x10000 of post", workspace.RedoLabel);
            await Preview();

            // The plain ground moved under the marked one would be the newest ground: the script would mark it instead.
            int marked = Grounds(shown).Single(g => shown.SourceBuild!.Provenance[g].Applied.Count > 0), plain = Grounds(shown).Single(g => g != marked);
            var moved = await TakenBack(new() { ["action"] = "parent", ["node"] = plain, ["parent"] = marked }, "Editing ground was reverted: ");
            Assert.Contains("would act on ground (data/m1/models/m1.gltf node", moved);
            // A copy named spare: the line that found nothing would mark the copy.
            var copied = await TakenBack(new() { ["action"] = "duplicate", ["node"] = Node(shown, "crate"), ["name"] = "spare" }, "Editing crate was reverted: ");
            Assert.Contains("would act on spare (data/m1/models/m1_01.gltf node", copied);
            Assert.Contains("instead of no node", copied);

            // The redo step survived both: it brings the flag back.
            var redone = Document(await Call("undo_redo", new() { ["document"] = Id(shown), ["revision"] = shown.Revision, ["action"] = "redo" }));
            Assert.False(workspace.CanRedo);
            Assert.Equal("Set flag 0x10000 of post", workspace.UndoLabel);
            await Preview();
            var flags = (await Call("source_world_object", new() { ["document"] = Id(redone), ["node"] = Node(redone, "post") }))["editableFlags"]!.AsArray();
            Assert.True(flags.Single(f => f!["bit"]!.GetValue<string>() == "0x10000")!["on"]!.GetValue<bool>());

            // An accepted copy: the status and the result give its notes, and count the lookup it now finds elsewhere.
            var accepted = await Job("source_world_object_edit", new() { ["document"] = Id(redone), ["revision"] = redone.Revision, ["node"] = Node(redone, "crate"), ["action"] = "duplicate", ["name"] = "crate2" });
            var state = accepted["document"]!["sourceWorld"]!;
            var notes = state["editNotes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            Assert.Contains(notes, n => n.Contains("so the world gets 2 nodes named crate2", StringComparison.Ordinal));
            Assert.Equal(notes.Length, state["editNoteCount"]!.GetValue<int>());
            Assert.Equal(1, state["lookupChangeCount"]!.GetValue<int>());
            Assert.Single(main.ViewModel.Problems, p => p.Message.Contains("FindNode lid in gamegen/support/tex_fxm1.gw finds", StringComparison.Ordinal));
            Assert.StartsWith("Rebuilt the m1 world from its sources. The copy shares", main.ViewModel.Status);
            Assert.EndsWith("1 lookup by name now finds another node than when the world was opened or last saved; Problems lists it.", main.ViewModel.Status);

            // A published replacement still waits for its own delayed presentation. Late cancellation must
            // neither retract the edit nor turn its already accepted MCP operation into a canceled result.
            var discover = main.DiscoverTexturePacksAsync;
            await AcceptedPresentation(cancel: true, supersede: false, fail: false);
            await AcceptedPresentation(cancel: false, supersede: true, fail: false);
            await AcceptedPresentation(cancel: false, supersede: false, fail: true);
            main.DiscoverTexturePacksAsync = discover;
            var reloadBefore = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            var reloaded = Document(await Job("reload_document", new() { ["document"] = Id(reloadBefore), ["revision"] = reloadBefore.Revision }));
            Assert.True(reloadBefore.IsDisposed); Assert.False(reloaded.IsDisposed);
            Assert.StartsWith("Rebuilt the m1 world from its sources.", main.ViewModel.Status);

            async Task AcceptedPresentation(bool cancel, bool supersede, bool fail)
            {
                var before = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
                int undo = workspace.UndoCount;
                TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<string[]> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                main.DiscoverTexturePacksAsync = (_, _, _) => { entered.TrySetResult(); return release.Task; };
                var started = await Call("source_world_object_edit", new()
                {
                    ["document"] = Id(before), ["revision"] = before.Revision, ["node"] = Node(before, "post"),
                    ["flag"] = "0x10000", ["on"] = supersede
                });
                await entered.Task.WaitAsync(token);
                var published = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
                Assert.NotSame(before, published); Assert.True(before.IsDisposed);
                Assert.Equal(undo + 1, workspace.UndoCount); Assert.True(workspace.IsDirty);
                string operation = started["id"]!.GetValue<string>();
                Assert.Equal("running", (await Call("operation", new() { ["id"] = operation }))["State"]!.GetValue<string>());
                if (cancel) await Call("operation", new() { ["id"] = operation, ["cancel"] = true });
                string? navigationStatus = null;
                if (supersede)
                {
                    main.ViewModel.SelectedDocument = null;
                    await Preview(); navigationStatus = main.ViewModel.Status;
                }
                if (fail)
                {
                    // A real deferred selection event awaits the same document transition. Its async-void
                    // boundary must observe the fault and leave the transition's single diagnostic intact.
                    var grid = (DataGrid)main.FindName("AssetGrid");
                    grid.SelectedItem = published.SelectedAsset;
                    grid.RaiseEvent(new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), new object[] { published.SelectedAsset! }));
                    release.SetException(new InvalidOperationException("test presentation failure"));
                }
                else release.SetResult([]);
                var completed = await AwaitJob("source_world_object_edit", started, "completed");
                Assert.Equal(Id(published), completed["document"]!["id"]!.GetValue<string>());
                Assert.Same(published, main.ViewModel.Documents.Single(d => d.SourceWorld != null));
                Assert.Equal(undo + 1, workspace.UndoCount); Assert.True(workspace.IsDirty);
                if (supersede) { Assert.Null(main.ViewModel.SelectedDocument); Assert.Equal(navigationStatus, main.ViewModel.Status); }
                else if (fail) Assert.Single(main.ViewModel.Problems, p => p.File == published.Path && p.Message.Contains("test presentation failure", StringComparison.Ordinal));
                else Assert.StartsWith("Rebuilt the m1 world from its sources.", main.ViewModel.Status);
                main.DiscoverTexturePacksAsync = discover;
                // A failed/superseded presentation remains retryable through the same accepted document.
                main.ViewModel.SelectedDocument = null; await Preview();
                main.ViewModel.SelectedDocument = published; await Preview();
                Assert.False(published.IsDisposed); Assert.Same(published, main.ViewModel.SelectedDocument);
            }

            // A world that does not open, and a reload whose build fails, also say so rather than keep the build's progress.
            fixture.Write("gamegen/m2.gs", "Quit\r\n");
            Assert.Equal("build_failed", (await Job("source_world_open", new() { ["mission"] = "m2" }, "failed"))["code"]!.GetValue<string>());
            Assert.StartsWith("The m2 world did not open: ", main.ViewModel.Status);
            fixture.Write("data/m1/models/m1.gltf", "{");
            var current = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            await Job("reload_document", new() { ["document"] = Id(current), ["revision"] = current.Revision }, "failed");
            Assert.StartsWith("The m1 world was not reloaded: ", main.ViewModel.Status);

            // Asks for an edit the rebuild takes back, and checks that everything is as it was.
            async Task<string> TakenBack(Dictionary<string, object?> edit, string reverted)
            {
                var before = Snapshot();
                var failed = await Job("source_world_object_edit", new(edit) { ["document"] = Id(shown), ["revision"] = shown.Revision }, "failed");
                Assert.Equal("invalid_argument", failed["code"]!.GetValue<string>());
                string message = failed["message"]!.GetValue<string>();
                Assert.StartsWith(reverted, message);
                Assert.Contains("the build finds nodes by name, and this change alters which one it finds", message);
                // The world shown is the one before; the workspace, its history and Problems are as they were.
                Assert.False(shown.IsDisposed);
                Assert.Same(shown, main.ViewModel.Documents.Single(d => d.SourceWorld != null));
                Assert.False(shown.SourceInputsChanged());
                Assert.Equal(before, Snapshot());
                // The status says what happened, not what the edit was doing.
                Assert.StartsWith(reverted, main.ViewModel.Status);
                return message;
            }
            string Snapshot() => string.Join("\n", [workspace.UndoCount.ToString(), workspace.UndoLabel ?? "-", workspace.RedoLabel ?? "-",
                .. workspace.History.Select(t => t.Label), .. workspace.DirtyFiles, Encoding.UTF8.GetString(workspace.Read("data/m1/models/m1.gltf")!), Encoding.UTF8.GetString(workspace.Read("data/m1/models/m1_01.gltf")!),
                .. main.ViewModel.Problems.Select(p => $"{p.Severity} {p.File}: {p.Message}")]);
            int[] Grounds(DocumentModel d) => [.. d.PreviewDocument.Scene!.Nodes.Where(n => n.Name == "ground" && d.SourceBuild!.Provenance.ContainsKey(n.Index)).Select(n => n.Index)];
            static int Node(DocumentModel d, string name) => d.PreviewDocument.Scene!.Nodes.First(n => n.Name == name && d.SourceBuild!.Provenance.ContainsKey(n.Index)).Index;
            async Task Preview()
            {
                var work = (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                await work.WaitAsync(token);
            }
            DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == (state["document"]?["id"] ?? state["id"])!.GetValue<string>());
            static string Id(DocumentModel d) => d.SessionId.ToString();
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, bool error = false)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True((result.IsError == true) == error, name + ": " + text);
                return error ? JsonValue.Create(text)! : JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments);
                return await AwaitJob(name, job, expected);
            }
            async Task<JsonNode> AwaitJob(string name, JsonNode job, string expected)
            {
                if (job["id"] == null || job["State"] == null) return job;
                string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, name + ": " + job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
}
