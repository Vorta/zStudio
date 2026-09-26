using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class AssetInspectionMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        var originalLoader = main.LoadAssetPropertiesAsync;
        try
        {
            await CheckPickupPublicationAsync(deadline.Token);
            foreach (string scenario in new[] { "revision", "closed", "removed", "canceled", "success" })
            {
                var doc = NewDocument(); main.ViewModel.Documents.Add(doc);
                var target = Target(doc);
                TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken observedToken = default;
                main.LoadAssetPropertiesAsync = async (source, asset, token) =>
                {
                    observedToken = token; started.SetResult();
                    await release.Task.WaitAsync(token);
                    return await originalLoader(source, asset, token);
                };
                using var request = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var inspecting = main.Commands.ExecuteAsync("zstudio_inspect_asset", target, request.Token);
                await started.Task.WaitAsync(deadline.Token);
                if (scenario == "revision") doc.AnimationEdits!.Apply(0, "Concurrent edit", e => e.SetFloat(164, 5));
                if (scenario == "closed") main.ViewModel.CloseResolved(doc);
                if (scenario == "removed") main.ViewModel.Documents.Remove(doc);
                if (scenario == "canceled") request.Cancel();
                release.SetResult();
                if (scenario == "success")
                {
                    var result = await inspecting.WaitAsync(deadline.Token);
                    Assert.Equal(doc.SessionId.ToString(), result.Data["document"]!.GetValue<string>());
                    Assert.Equal(doc.Revision, result.Data["Revision"]!.GetValue<long>());
                    Assert.Equal(Convert.ToHexStringLower(doc.Document.Animations!.Entries[0].Bytes), result.Data["source"]!["properties"]!["header_hex"]!.GetValue<string>());
                    Assert.Equal(Convert.ToHexStringLower(doc.AnimationEdits!.Package.Entries[0].Bytes), result.Data["edited"]!["header_hex"]!.GetValue<string>());
                    Assert.NotEqual(result.Data["source"]!["properties"]!["header_hex"]!.GetValue<string>(), result.Data["edited"]!["header_hex"]!.GetValue<string>());
                    string sourceHex = result.Data["source"]!["properties"]!["header_hex"]!.GetValue<string>();
                    main.LoadAssetPropertiesAsync = originalLoader;
                    doc.AnimationEdits.Undo();
                    var undone = await main.Commands.ExecuteAsync("zstudio_inspect_asset", target, deadline.Token);
                    Assert.Equal(sourceHex, undone.Data["source"]!["properties"]!["header_hex"]!.GetValue<string>());
                    Assert.Equal(sourceHex, undone.Data["edited"]!["header_hex"]!.GetValue<string>());
                    doc.AnimationEdits.Redo();
                    var redone = await main.Commands.ExecuteAsync("zstudio_inspect_asset", target, deadline.Token);
                    Assert.Equal(sourceHex, redone.Data["source"]!["properties"]!["header_hex"]!.GetValue<string>());
                    Assert.Equal(result.Data["edited"]!["header_hex"]!.GetValue<string>(), redone.Data["edited"]!["header_hex"]!.GetValue<string>());
                }
                else if (scenario == "canceled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspecting);
                else
                {
                    var error = await Assert.ThrowsAsync<StudioCommandException>(() => inspecting);
                    Assert.Equal(scenario == "revision" ? "revision_conflict" : "stale_document", error.Code);
                }
                if (scenario is "closed" or "canceled") Assert.True(observedToken.IsCancellationRequested);
                doc.AnimationEdits!.MarkSaved();
                main.ViewModel.Documents.Remove(doc); doc.Dispose();
            }

            // A real MCP transport disconnect reaches this non-job decoder too.
            var live = NewDocument(); main.ViewModel.Documents.Add(live);
            TaskCompletionSource decoding = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            main.LoadAssetPropertiesAsync = async (_, _, token) =>
            {
                decoding.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); return new(); }
                catch (OperationCanceledException) { canceled.SetResult(); throw; }
            };
            var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            var call = client.CallToolAsync("zstudio_inspect_asset", new Dictionary<string, object?>
            { ["document"] = live.SessionId.ToString(), ["kind"] = "Animation", ["index"] = 0 }, cancellationToken: deadline.Token).AsTask();
            await decoding.Task.WaitAsync(deadline.Token);
            await host.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            await canceled.Task.WaitAsync(deadline.Token);
            try { await call; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ModelContextProtocol.McpException) { }
            Assert.False(live.IsDisposed);
            main.LoadAssetPropertiesAsync = originalLoader;
            var recovered = await main.Commands.ExecuteAsync("zstudio_inspect_asset", Target(live), deadline.Token);
            Assert.Equal(live.Revision, recovered.Data["Revision"]!.GetValue<long>());
            live.AnimationEdits!.MarkSaved(); main.ViewModel.CloseResolved(live);
        }
        finally
        {
            foreach (var doc in main.ViewModel.Documents) doc.AnimationEdits?.MarkSaved();
            main.Close();
        }
    }

    private static JsonObject Target(DocumentModel doc) => new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Animation", ["index"] = 0 };
    private static async Task CheckPickupPublicationAsync(CancellationToken token)
    {
        using var resolver = new AssetResolver(Path.GetTempPath());
        foreach (bool closeDocument in new[] { true, false })
        {
            using var doc = NewDocument();
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            TaskCompletionSource<PickupPlacementEditSession> loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(DocumentModel).GetField("pickupLoading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(doc, loaded.Task);
            var loading = doc.GetPickupEditsAsync(resolver, request.Token);
            var edits = PickupPlacementEditSession.Create([], token: token);
            // Complete the background work, then invalidate it before its queued
            // dispatcher continuation can attach the session to the document.
            loaded.SetResult(edits);
            if (closeDocument) doc.Dispose(); else request.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
            Assert.Null(doc.PickupEdits);
            if (!closeDocument)
            {
                // Cancellation belongs to the waiter, not the shared warm load.
                Assert.Same(edits, await doc.GetPickupEditsAsync(resolver, token));
                Assert.Same(edits, doc.PickupEdits);
            }
        }
    }
    private static DocumentModel NewDocument()
    {
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
        package.Entries.Add(new(new byte[308], 0, 0));
        var source = new ZbdDocument(Path.Combine(Path.GetTempPath(), "inspection-mcp.zbd"), new(0, DateTime.MinValue),
            new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package };
        source.Add(AssetKind.Animation, 0, "animation", 0, 0);
        var doc = new DocumentModel(source);
        doc.AnimationEdits!.Apply(0, "Edited fixture", e => e.SetFloat(164, 2));
        doc.AnimationEdits.MarkSaved();
        return doc;
    }
}
