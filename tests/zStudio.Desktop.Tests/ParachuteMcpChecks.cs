using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>The two owning-document paths share real protocol jobs and the visible draft's actual buttons.</summary>
internal static class ParachuteMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        fixture.Write(VtolDropFixture.Root, VtolDropFixture.Definitions());
        fixture.Write("data/m1/zrdr/flight.zan", VtolDropFixture.MotionScript);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);

            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var world = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);
            await Preview();
            // Keep an independent compiled baseline before source confirmation replaces/removes the preview build.
            string compiled = Path.Combine(fixture.Root, "compiled");
            foreach (string path in Directory.EnumerateFiles(world.SourceBuild!.Folder, "*", SearchOption.AllDirectories))
            {
                string copy = Path.Combine(compiled, Path.GetRelativePath(world.SourceBuild.Folder, path));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(path, copy);
            }
            var state = (await PathAction("show"))["parachute"]!;
            Assert.Equal(2, state["windows"]!.AsArray().Count);
            Assert.True(state["cycleSeconds"]!.GetValue<double>() >= 10);
            Assert.Null(state["selected"]);
            await Unlock();
            await SelectFirstWindow();
            state = (await PathAction("begin"))["parachute"]!;
            string draft = state["draft"]!["token"]!.GetValue<string>();
            await PathAction("set", new() { ["token"] = draft, ["start"] = 2 });

            // Escape while the source worker is held is an explicit discard, not a failed acceptance to restore.
            TaskCompletionSource preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var release = new SemaphoreSlim(0))
            {
                main.SourceEditPreparing = ct => { preparing.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(20), ct)); };
                var canceled = Job("parachute", await Arguments("confirm", new() { ["token"] = draft }), "failed");
                try
                {
                    var first = await Task.WhenAny(preparing.Task, canceled).WaitAsync(token);
                    if (first == canceled) Assert.Fail("Confirmation did not reach source preparation: " + (await canceled).ToJsonString());
                    var automation = (AsyncLocal<bool>)typeof(MainWindow).GetField("automationRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    bool previous = automation.Value; automation.Value = true;
                    try
                    {
                        System.Windows.Input.Keyboard.ClearFocus();
                        ((UIElement)((ContentControl)main.FindName("SceneHost")).Content).RaiseEvent(new System.Windows.Input.KeyEventArgs(
                            System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), Environment.TickCount, System.Windows.Input.Key.Escape)
                            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                    }
                    finally { automation.Value = previous; }
                    release.Release();
                    Assert.Equal("no_draft", (await canceled)["code"]!.GetValue<string>());
                    Assert.Null((await State())!["draft"]);
                    Assert.False(world.IsDirty); Assert.Empty(world.SourceWorld!.Workspace.History);
                }
                finally { release.Release(); main.SourceEditPreparing = null; }
            }

            state = (await PathAction("begin"))["parachute"]!;
            draft = state["draft"]!["token"]!.GetValue<string>();
            await PathAction("set", new() { ["token"] = draft, ["start"] = 2 });
            long revisionBeforeFailure = world.Revision;
            var prepareModel = main.PreparingSourceWorldModel;
            main.PreparingSourceWorldModel = _ => throw new InvalidDataException("Parachute rebuild failure fixture");
            try
            {
                var failed = await Job("parachute", await Arguments("confirm", new() { ["token"] = draft }), "failed");
                Assert.Equal("build_failed", failed["code"]!.GetValue<string>());
                Assert.False(world.IsDisposed); Assert.False(world.IsDirty); Assert.True(world.Revision > revisionBeforeFailure);
                Assert.Empty(world.SourceWorld!.Workspace.History);
                state = (await State())!;
                Assert.Equal(world.Revision, state["revision"]!.GetValue<long>());
                Assert.Equal(draft, state["draft"]!["token"]!.GetValue<string>());
                Assert.Equal("2", state["draft"]!["input"]!.GetValue<string>());
                Assert.True(state["draft"]!["ready"]!.GetValue<bool>());
                Assert.Equal(VtolDropFixture.Definitions(), Encoding.Latin1.GetString(world.SourceWorld.Workspace.Read(VtolDropFixture.Root)!));
            }
            finally { main.PreparingSourceWorldModel = prepareModel; }
            // Retry uses the same restored intent/token and the proven rollback's current document revision.
            var before = world;
            var moved = await PathAction("confirm", new() { ["token"] = draft });
            world = Document(moved["document"]!); await Preview();
            Assert.True(before.IsDisposed);
            string expected = VtolDropFixture.Definitions().Replace("SEQUENCE_OFFSET 1", "SEQUENCE_OFFSET 2.0", StringComparison.Ordinal);
            Assert.Equal(expected, Encoding.Latin1.GetString(world.SourceWorld!.Workspace.Read(VtolDropFixture.Root)!));
            Assert.Single(world.SourceWorld.Workspace.History);
            Assert.Equal(VtolDropFixture.Definitions(), File.ReadAllText(fixture.Path(VtolDropFixture.Root)));
            // Source confirmation is one project history step, not a mutation of the cached animation archive.
            world = Document(await Call("undo_redo", new() { ["document"] = Id(world), ["revision"] = world.Revision, ["action"] = "undo" }));
            await Preview();
            Assert.False(world.IsDirty);
            Assert.Equal(VtolDropFixture.Definitions(), Encoding.Latin1.GetString(world.SourceWorld!.Workspace.Read(VtolDropFixture.Root)!));
            foreach (var document in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(document);

            await main.ViewModel.OpenRootAsync(compiled, token);
            world = Document(await Job("open_document", new() { ["path"] = Path.Combine(compiled, "m1", "gamez.zbd") }));
            await Preview();
            state = (await PathAction("show"))["parachute"]!;
            var owner = main.ViewModel.Documents.Single(d => Id(d) == state["owner"]!.GetValue<string>());
            Assert.NotSame(world, owner); Assert.Same(world, main.ViewModel.SelectedDocument);
            Assert.EndsWith("anim.zbd", owner.Path, StringComparison.OrdinalIgnoreCase);
            byte[] original = File.ReadAllBytes(owner.Path);
            long revision = owner.Revision;
            await Unlock();
            await SelectFirstWindow();

            var staleOwner = await Arguments("begin"); staleOwner["ownerRevision"] = owner.Revision + 1;
            Assert.Equal("revision_conflict", (await Job("parachute", staleOwner, "failed"))["code"]!.GetValue<string>());
            state = (await PathAction("begin"))["parachute"]!;
            draft = state["draft"]!["token"]!.GetValue<string>();
            Assert.Equal("pending_drafts", (await Job("parachute", await Arguments("hide"), "failed"))["code"]!.GetValue<string>());
            // GUI cancellation is the same draft, with no preliminary discard dialog or accepted edit.
            Click("cancel");
            Assert.Null((await State())!["draft"]); Assert.Equal(revision, owner.Revision);

            state = (await PathAction("begin"))["parachute"]!;
            draft = state["draft"]!["token"]!.GetValue<string>();
            var stale = await Arguments("set", new() { ["token"] = "expired", ["start"] = 2 });
            Assert.Equal("stale_draft", (await Job("parachute", stale, "failed"))["code"]!.GetValue<string>());
            // Real TextChanged + routed Confirm protect GUI/protocol parity, not only the underlying command.
            ((TextBox)main.FindName("ParachuteStart")).Text = "2";
            await Until(() => ((Button)main.FindName("ParachuteConfirm")).IsEnabled);
            Click("confirm");
            await Until(() => owner.Revision == revision + 1);
            await UntilAsync(async () => (await State())?["ownerRevision"]?.GetValue<long>() == owner.Revision);
            Assert.False(world.IsDirty); Assert.True(owner.IsDirty); Assert.Same(world, main.ViewModel.SelectedDocument);
            Assert.Equal(2, FirstWindow(owner).Start);
            Assert.Equal(original, File.ReadAllBytes(owner.Path));
            await PathAction("undo"); Assert.Equal(1, FirstWindow(owner).Start); Assert.False(owner.IsDirty);
            await PathAction("redo"); Assert.Equal(2, FirstWindow(owner).Start);
            string saved = Path.Combine(fixture.Root, "animation-copy.zbd");
            await PathAction("save", new() { ["destination"] = saved });
            var reopened = await FormatRegistry.Default.OpenAsync(saved, token);
            Assert.Equal(2, Assert.Single(VtolDropWindows.Discover(reopened.Animations!, token).Candidates).Windows[0].Start);
            Assert.Equal(original, File.ReadAllBytes(owner.Path)); Assert.False(owner.IsDirty);

            await PathAction("show");
            await SelectFirstWindow();
            state = (await PathAction("begin"))["parachute"]!;
            var oldArguments = await Arguments("confirm", new() { ["token"] = state["draft"]!["token"]!.GetValue<string>() });
            // An independent accepted owner edit expires the overlay and draft instead of retaining a stale write plan.
            owner.AnimationEdits!.Apply(1, "Change owner", e => e.SetFloat(164, e.F32(164) + 1));
            Assert.False((await State())!["visible"]!.GetValue<bool>());
            Assert.Equal("not_ready", (await Job("parachute", oldArguments, "failed"))["code"]!.GetValue<string>());

            async Task Unlock() => await Call("pickup_lock", new() { ["document"] = Id(world), ["revision"] = world.Revision, ["locked"] = false });
            async Task SelectFirstWindow() => await PathAction("select", new() { ["window"] = (await State())!["windows"]![0]!["id"]!.GetValue<string>() });
            async Task<JsonNode?> State()
            {
                var current = await Call("state", new());
                return (await Call("preview_state", new() { ["preview"] = current["preview"]!.GetValue<string>() }))["parachute"];
            }
            async Task<Dictionary<string, object?>> Arguments(string action, Dictionary<string, object?>? extra = null)
            {
                var current = await Call("state", new());
                var args = extra ?? new(); args["action"] = action; args["preview"] = current["preview"]!.GetValue<string>();
                args["document"] = Id(world); args["revision"] = world.Revision;
                if (action != "show")
                {
                    var shown = (await State())!; args["snapshot"] = shown["snapshot"]!.GetValue<string>();
                    args["ownerRevision"] = shown["ownerRevision"]!.GetValue<long>();
                }
                return args;
            }
            async Task<JsonNode> PathAction(string action, Dictionary<string, object?>? extra = null) => await Job("parachute", await Arguments(action, extra));
            void Click(string action)
            {
                var panel = (DependencyObject)main.FindName("ParachutePanel");
                var button = Descendants(panel).OfType<Button>().Single(b => b.Tag as string == action);
                Assert.True(button.IsEnabled); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            }
            async Task Until(Func<bool> ready) { while (!ready()) await Task.Delay(10, token); }
            async Task UntilAsync(Func<Task<bool>> ready) { while (!await ready()) await Task.Delay(10, token); }
            async Task<JsonNode> Call(string name, Dictionary<string, object?> args)
            {
                var result = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.False(result.IsError == true, name + ": " + text); return JsonNode.Parse(text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> args, string expectedState = "completed")
            {
                var job = await Call(name, args);
                if (job["State"] == null) return job;
                string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expectedState, name + ": " + job.ToJsonString()); return job["result"]!;
            }
            DocumentModel Document(JsonNode json) => main.ViewModel.Documents.Single(d => Id(d) == (json["document"]?["id"] ?? json["id"])!.GetValue<string>());
            async Task Preview() => await ((Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).WaitAsync(token);
        }
        finally
        {
            typeof(MainWindow).GetMethod("CloseParachutes", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, []);
            foreach (var document in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(document);
            main.Close();
        }
    }
    private static string Id(DocumentModel document) => document.SessionId.ToString();
    private static VtolDropWindow FirstWindow(DocumentModel owner) => Assert.Single(VtolDropWindows.Discover(owner.AnimationEdits!.Package).Candidates).Windows[0];
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
