using System.IO;
using System.IO.Pipes;
using System.Reflection;
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

/// <summary>Checkout work belongs to the root that started it, even when no world document is open.</summary>
internal static class BlenderCheckoutLifetimeChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private enum Transition { OtherRoot, SameRoot, RootDuringCopy, CancelDuringCopy, CancelAfterCopy }

    internal static async Task Run()
    {
        using SourceWorldFixture original = new(), replacement = new();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            var protocol = new Protocol(client, token);
            foreach (bool mcp in new[] { false, true })
            {
                foreach (var transition in Enum.GetValues<Transition>())
                    await Checkout(main, original, replacement, protocol, mcp, transition, token);
                await Listing(main, original, replacement, protocol, mcp, token);
            }
            await ModelIdentity(main, original, protocol, token);
        }
        finally
        {
            main.CheckoutSourceModel = SourceBlender.Checkout;
            main.ReadCheckoutExports = SourceBlender.CheckoutExports;
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }

    private sealed record Checkpoint(SourceWorkspace Workspace, CancellationToken Token, BlenderCheckout? Copy);

    // A repeated helper line is not a model identity: checking out the second load must never copy the first model.
    private static async Task ModelIdentity(MainWindow main, SourceWorldFixture fixture, Protocol protocol, CancellationToken token)
    {
        fixture.Write("gamegen/helper.gs", "LoadGameGen %file%.flt %instance%\n");
        fixture.Write("gamegen/m1.gs", File.ReadAllText(fixture.Path("gamegen/m1.gs")).Replace("# no vehicles", """
            set file first
            set instance first_root
            source helper.gs
            set file second
            set instance second_root
            source helper.gs
            LoadGameGen empty.flt empty_root
            """, StringComparison.Ordinal));
        fixture.Write("data/m1/models/first.gltf", """{"asset":{"version":"2.0"},"nodes":[{"name":"first_child"}],"scenes":[{"nodes":[0]}],"scene":0}""");
        fixture.Write("data/m1/models/second.gltf", """{"asset":{"version":"2.0"},"nodes":[{"name":"second_child"}],"scenes":[{"nodes":[0]}],"scene":0}""");
        fixture.Write("data/m1/models/empty.gltf", """{"asset":{"version":"2.0"},"nodes":[],"scenes":[{"nodes":[]}],"scene":0}""");
        await main.ViewModel.OpenRootAsync(fixture.Project, token);
        await protocol.Finish(await protocol.Call("source_world_open", new() { ["mission"] = "m1" }), "completed");
        var doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
        var nodes = doc.PreviewDocument.Scene!.Nodes;
        int first = nodes.Single(n => n.Name == "first_root").Index, second = nodes.Single(n => n.Name == "second_root").Index;
        Assert.Equal(doc.SourceBuild!.Provenance[first].Created!.Script, doc.SourceBuild.Provenance[second].Created!.Script);
        Assert.Equal(doc.SourceBuild.Provenance[first].Created!.Line, doc.SourceBuild.Provenance[second].Created!.Line);
        foreach (var (name, model) in new[] { ("first_root", "first"), ("second_root", "second"), ("second_child", "second"), ("empty_root", "empty") })
        {
            int node = nodes.Single(n => n.Name == name).Index;
            string expected = $"data/m1/models/{model}.gltf";
            var checkout = await protocol.Finish(await protocol.Call("source_blender_checkout", new() { ["document"] = doc.SessionId.ToString(), ["node"] = node }), "completed");
            Assert.Equal(expected, checkout["model"]!.GetValue<string>());
            // The GUI's selector and the real node-based MCP operation share the stored load identity.
            Assert.Equal(expected, typeof(MainWindow).GetMethod("SourceObjectModel", Private)!.Invoke(main, [doc, node]));
            var input = JsonNode.Parse(File.ReadAllText(checkout["input"]!.GetValue<string>()))!;
            if (model == "empty") Assert.Empty(input["nodes"]!.AsArray());
            else Assert.Equal(model + "_child", input["nodes"]![0]!["name"]!.GetValue<string>());
        }
        main.ViewModel.CloseResolved(doc);
    }

    private static async Task Checkout(MainWindow main, SourceWorldFixture original, SourceWorldFixture replacement,
        Protocol protocol, bool mcp, Transition transition, CancellationToken token)
    {
        await main.ViewModel.OpenRootAsync(original.Project, token);
        Assert.Empty(main.ViewModel.Documents);
        var workspace = (SourceWorkspace)typeof(MainWindow).GetMethod("SourceWorkspaceFor", Private)!.Invoke(main, [original.Project])!;
        long revision = workspace.ContentRevision, generation = main.ViewModel.WorkspaceGeneration;
        string exportRoot = Path.Combine(original.Project, "zstudio", "export");
        string[] foldersBefore = Directory.Exists(exportRoot) ? Directory.GetDirectories(exportRoot).Order(StringComparer.Ordinal).ToArray() : [];
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        using ManualResetEventSlim release = new();
        TaskCompletionSource<Checkpoint> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool completeFirst = transition is not (Transition.RootDuringCopy or Transition.CancelDuringCopy);
        main.CheckoutSourceModel = (captured, model, ct) =>
        {
            Assert.False(main.Dispatcher.CheckAccess());
            try
            {
                var copy = completeFirst ? SourceBlender.Checkout(captured, model, ct) : null;
                entered.TrySetResult(new(captured, ct, copy));
                // Deliberately ignore the work token after a completed copy: cancellation can arrive after its last check.
                Assert.True(release.Wait(TimeSpan.FromSeconds(20), token));
                if (copy != null) return copy;
                ct.ThrowIfCancellationRequested();
                return SourceBlender.Checkout(captured, model, ct);
            }
            catch (Exception ex) { entered.TrySetException(ex); throw; }
        };
        Task<BlenderCheckout>? gui = null;
        JsonNode? operation = null;
        if (mcp) operation = await protocol.Call("source_blender_checkout", new() { ["model"] = original.Tank });
        else gui = GuiCheckout(main, original.Tank, caller.Token);
        Checkpoint checkpoint;
        object? currentWorkspace;
        byte[]? manifest = null, input = null;
        try
        {
            checkpoint = await entered.Task.WaitAsync(token);
            Assert.Same(workspace, checkpoint.Workspace);
            if (checkpoint.Copy is { } written)
            {
                manifest = File.ReadAllBytes(Path.Combine(written.Folder, "manifest.json"));
                input = File.ReadAllBytes(written.Input);
            }
            if (transition is Transition.OtherRoot or Transition.SameRoot or Transition.RootDuringCopy)
            {
                // The real GUI root operation can run while a named-pipe job awaits its worker.
                await main.ViewModel.OpenRootAsync(transition == Transition.SameRoot ? original.Project : replacement.Project, token);
                Assert.True(main.ViewModel.WorkspaceGeneration > generation);
                Assert.Equal(revision, workspace.ContentRevision); // Revision alone cannot detect this transition.
            }
            else if (mcp) await protocol.Call("operation", new() { ["id"] = operation!["id"]!.GetValue<string>(), ["cancel"] = true });
            else caller.Cancel();
            Assert.True(checkpoint.Token.IsCancellationRequested);
            currentWorkspace = typeof(MainWindow).GetField("sourceWorkspace", Private)!.GetValue(main);
        }
        finally { release.Set(); }

        bool lateCancel = transition == Transition.CancelAfterCopy;
        bool canceled = transition is Transition.RootDuringCopy or Transition.CancelDuringCopy;
        if (mcp)
        {
            var result = await protocol.Finish(operation!, lateCancel ? "completed" : canceled ? "canceled" : "failed");
            if (lateCancel) Assert.Equal(checkpoint.Copy!.Id, result["id"]!.GetValue<string>());
            else if (!canceled) Assert.Equal("context_changed", result["code"]!.GetValue<string>());
        }
        else if (lateCancel) Assert.Same(checkpoint.Copy, await gui!);
        else if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await gui!; });
        else
        {
            var error = await Assert.ThrowsAsync<StudioCommandException>(async () => { await gui!; });
            Assert.Equal("context_changed", error.Code);
            Assert.Contains("recorded path", error.Message);
        }
        Assert.Same(currentWorkspace, typeof(MainWindow).GetField("sourceWorkspace", Private)!.GetValue(main));
        Assert.Equal(revision, workspace.ContentRevision);
        if (checkpoint.Copy is { } completed)
        {
            Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(completed.Folder, "manifest.json")));
            Assert.Equal(input, File.ReadAllBytes(completed.Input));
        }
        else Assert.Equal(foldersBefore, Directory.Exists(exportRoot) ? Directory.GetDirectories(exportRoot).Order(StringComparer.Ordinal).ToArray() : []);

        // The next request captures the new active workspace; the failed request did not adopt it or poison the worker.
        main.CheckoutSourceModel = SourceBlender.Checkout;
        string active = main.ViewModel.RootPath!;
        string folder;
        if (mcp)
        {
            var retry = await protocol.Finish(await protocol.Call("source_blender_checkout", new() { ["model"] = original.Tank }), "completed");
            folder = retry["folder"]!.GetValue<string>();
        }
        else folder = (await GuiCheckout(main, original.Tank, token)).Folder;
        Assert.StartsWith(Path.Combine(active, "zstudio", "export") + Path.DirectorySeparatorChar, folder, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(folder, "manifest.json")));
    }

    private static async Task Listing(MainWindow main, SourceWorldFixture original, SourceWorldFixture replacement,
        Protocol protocol, bool mcp, CancellationToken token)
    {
        await main.ViewModel.OpenRootAsync(original.Project, token);
        using ManualResetEventSlim release = new();
        TaskCompletionSource<CancellationToken> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        main.ReadCheckoutExports = (root, ct) =>
        {
            Assert.False(main.Dispatcher.CheckAccess());
            entered.TrySetResult(ct);
            Assert.True(release.Wait(TimeSpan.FromSeconds(20), token));
            return []; // A listing that completed at the cancellation boundary must still not publish in the new root.
        };
        Task<JsonNode>? remote = mcp ? protocol.Call("source_blender_checkouts", new(), "context_changed") : null;
        Task? gui = mcp ? null : (Task)typeof(MainWindow).GetMethod("CheckoutExportsAsync", Private)!.Invoke(main, [original.Project, token])!;
        try
        {
            var captured = await entered.Task.WaitAsync(token);
            await main.ViewModel.OpenRootAsync(replacement.Project, token);
            Assert.True(captured.IsCancellationRequested);
        }
        finally { release.Set(); }
        if (mcp) await remote!;
        else Assert.Equal("context_changed", (await Assert.ThrowsAsync<StudioCommandException>(async () => { await gui!; })).Code);
        main.ReadCheckoutExports = SourceBlender.CheckoutExports;
        if (mcp) await protocol.Call("source_blender_checkouts", new());
        else await (Task)typeof(MainWindow).GetMethod("CheckoutExportsAsync", Private)!.Invoke(main, [replacement.Project, token])!;
    }

    private static Task<BlenderCheckout> GuiCheckout(MainWindow main, string model, CancellationToken token) =>
        (Task<BlenderCheckout>)typeof(MainWindow).GetMethod("CheckoutForBlenderAsync", Private)!.Invoke(main, [model, token])!;

    private sealed class Protocol(McpClient client, CancellationToken token)
    {
        internal async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, string? error = null)
        {
            var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
            var data = JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            if (error == null) Assert.False(result.IsError == true, data.ToJsonString());
            else { Assert.True(result.IsError == true, data.ToJsonString()); Assert.Equal(error, data["code"]!.GetValue<string>()); }
            return data;
        }
        internal async Task<JsonNode> Finish(JsonNode operation, string expected)
        {
            string id = operation["id"]!.GetValue<string>();
            while (operation["State"]!.GetValue<string>() is "queued" or "running")
            {
                await Task.Delay(10, token); // Status polling only; interleavings are controlled by the worker barriers.
                operation = await Call("operation", new() { ["id"] = id });
            }
            Assert.True(operation["State"]!.GetValue<string>() == expected, operation.ToJsonString());
            return operation["result"]!;
        }
    }
}
