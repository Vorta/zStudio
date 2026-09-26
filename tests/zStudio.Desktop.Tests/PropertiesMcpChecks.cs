using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class PropertiesMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
        var entry = new AnimationEntry(new byte[308], 0, 0); package.Entries.Add(entry);
        var source = new ZbdDocument(Path.Combine(Path.GetTempPath(), "property-mcp.zbd"), new(0, DateTime.MinValue),
            new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package };
        source.Add(AssetKind.Raw, 0, "raw target", 0, 0); source.Add(AssetKind.Animation, 0, "animation target", 0, 0);
        var doc = new DocumentModel(source); main.ViewModel.Documents.Add(doc);
        JsonObject Target() => new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Raw", ["index"] = 0 };
        Task<StudioResult> Open(CancellationToken token) => main.Commands.ExecuteAsync("zstudio_properties_open", Target(), token);
        try
        {
            main.OpenAnimationProperties(doc, 0, Guid.Empty, Guid.Empty);
            foreach (string key in new[] { "sequence", "event" })
            {
                var invalid = Target(); invalid["kind"] = "Animation"; invalid[key] = Guid.NewGuid().ToString();
                Assert.Equal("stale_record", (await Assert.ThrowsAsync<StudioCommandException>(() =>
                    main.Commands.ExecuteAsync("zstudio_properties_open", invalid, deadline.Token))).Code);
                Assert.NotNull(main.OpenPropertiesWindow!.AnimationFields);
            }
            foreach (string scenario in new[] { "superseded", "canceled", "draft", "decode_error", "success" })
            {
                TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<JsonObject> decoded = new(TaskCreationOptions.RunContinuationsAsynchronously);
                main.LoadAssetPropertiesAsync = async (_, _, token) => { started.SetResult(); return await decoded.Task.WaitAsync(token); };
                using var request = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var opening = Open(request.Token); await started.Task.WaitAsync(deadline.Token);
                var previous = main.OpenPropertiesWindow!.CurrentJson!.ToJsonString();
                TextBox? draft = null;
                if (scenario == "superseded") main.OpenAnimationProperties(doc, 0, Guid.Empty, Guid.Empty);
                if (scenario == "canceled") request.Cancel();
                if (scenario == "draft")
                {
                    main.OpenPropertiesWindow!.UpdateLayout();
                    draft = Descendants(main.OpenPropertiesWindow!.AnimationFields!).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Reset delay (s)");
                    draft.Text = "-";
                }
                if (scenario == "decode_error") decoded.SetException(new InvalidDataException("fixture decode error"));
                else decoded.TrySetResult(new() { ["fixture"] = "raw properties" });
                if (scenario == "success")
                {
                    var result = await opening;
                    Assert.Equal("Raw", result.Data["asset"]!["Kind"]!.GetValue<string>());
                    Assert.Equal("raw properties", main.OpenPropertiesWindow!.CurrentJson!["fixture"]!.GetValue<string>());
                }
                else
                {
                    if (scenario == "canceled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
                    else if (scenario == "decode_error") Assert.Equal("fixture decode error", (await Assert.ThrowsAsync<InvalidDataException>(() => opening)).Message);
                    else Assert.Equal(scenario == "draft" ? "pending_drafts" : "context_changed", (await Assert.ThrowsAsync<StudioCommandException>(() => opening)).Code);
                    Assert.Equal(previous, main.OpenPropertiesWindow!.CurrentJson!.ToJsonString());
                }
                if (draft != null) { Assert.Equal("-", draft.Text); draft.Text = "0"; }
            }

            // Work authored after the deferred MCP close request must be retained
            // without opening the GUI's save/discard confirmation dialog.
            await main.Commands.ExecuteAsync("zstudio_window", new() { ["action"] = "close" }, deadline.Token);
            doc.AnimationEdits!.Apply(0, "Edit during close", e => e.SetInt(164, 1));
            await Task.Delay(350, deadline.Token);
            Assert.True(main.IsVisible); Assert.StartsWith("Close canceled:", main.ViewModel.Status);
            doc.AnimationEdits.Undo();
            main.OpenAnimationProperties(doc, 0, Guid.Empty, Guid.Empty); main.OpenPropertiesWindow!.UpdateLayout();
            await main.Commands.ExecuteAsync("zstudio_window", new() { ["action"] = "close" }, deadline.Token);
            var closingDraft = Descendants(main.OpenPropertiesWindow.AnimationFields!).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Reset delay (s)");
            closingDraft.Text = "-";
            await Task.Delay(350, deadline.Token);
            Assert.True(main.IsVisible); Assert.Equal("-", closingDraft.Text); Assert.True(main.OpenPropertiesWindow.HasPendingDrafts);
            closingDraft.Text = "0";
            main.LoadAssetPropertiesAsync = (_, _, _) => Task.FromResult(new JsonObject { ["fixture"] = "raw properties" });
            await main.OpenAssetPropertiesAsync(doc, source.Assets.First(), deadline.Token);

            // Real transport shutdown must reach non-job property generation.
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
            var call = client.CallToolAsync("zstudio_properties_open", new Dictionary<string, object?> { ["document"] = doc.SessionId.ToString(), ["kind"] = "Raw", ["index"] = 0 }, cancellationToken: deadline.Token).AsTask();
            await decoding.Task.WaitAsync(deadline.Token);
            await host.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            await canceled.Task.WaitAsync(deadline.Token);
            try { await call; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ModelContextProtocol.McpException) { }
            Assert.Equal("raw properties", main.OpenPropertiesWindow!.CurrentJson!["fixture"]!.GetValue<string>());

            TaskCompletionSource disposedStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
            main.LoadAssetPropertiesAsync = async (_, _, token) => { disposedStart.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(); };
            var disposedOpen = Open(deadline.Token); await disposedStart.Task.WaitAsync(deadline.Token);
            main.ViewModel.CloseResolved(doc);
            Assert.Equal("context_changed", (await Assert.ThrowsAsync<StudioCommandException>(() => disposedOpen)).Code);
            Assert.Null(main.OpenPropertiesWindow);
        }
        finally { main.OpenPropertiesWindow?.CloseResolved(); main.Close(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
