using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NAudio.Wave;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Desktop.Audio;
using Recoil.Zbd.Mcp;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class ShutdownMcpChecks
{
    internal static async Task Run(Application app)
    {
        foreach (bool cancelClose in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
            var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
            package.Entries.Add(new(new byte[308], 0, 0));
            var source = new ZbdDocument(Path.Combine(Path.GetTempPath(), "shutdown-mcp.zbd"), new(0, DateTime.MinValue),
                new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package };
            source.Add(AssetKind.Raw, 0, "first", 0, 0);
            source.Add(AssetKind.Raw, 1, "blocked", 0, 0);
            var doc = new DocumentModel(source);
            main.ViewModel.Documents.Add(doc); main.ViewModel.SelectedDocument = doc;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            ((TabControl)main.FindName("NavigationTabs")).SelectedItem = main.FindName("AssetsTab");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await ((Task)typeof(MainWindow).GetField("previewWork", flags)!.GetValue(main)!).WaitAsync(deadline.Token);
            var loader = main.LoadAssetPropertiesAsync;
            TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource recovering = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource releaseRecovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            main.Closed += (_, _) => closed.TrySetResult();
            int calls = 0;
            CancellationToken loadToken = default;
            main.LoadAssetPropertiesAsync = async (document, asset, token) =>
            {
                if (++calls == 1)
                {
                    loadToken = token; loading.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                // If accepted closing wrongly invokes operation-only recovery,
                // it cannot finish until this independently controlled gate opens.
                recovering.TrySetResult();
                await releaseRecovery.Task.WaitAsync(token);
                return await loader(document, asset, token);
            };
            var host = new LocalMcpHost(main.Commands, "test");
            typeof(MainWindow).GetField("mcpHost", flags)!.SetValue(main, host);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            TaskCompletionSource closeCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            timer.Tick += (_, _) =>
            {
                var dialog = app.Windows.Cast<Window>().FirstOrDefault(w => w.Owner == main && w.Title == "Unsaved changes");
                if (dialog == null) return;
                var buttons = ((StackPanel)dialog.Content).Children.OfType<WrapPanel>().Single();
                buttons.Children.OfType<Button>().Single(b => Equals(b.Content, "Cancel"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                closeCanceled.TrySetResult(); timer.Stop();
            };
            try
            {
                await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(deadline.Token);
                await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
                var response = await client.CallToolAsync("zstudio_select_asset", new Dictionary<string, object?>
                { ["document"] = doc.SessionId.ToString(), ["kind"] = "Raw", ["index"] = 1 }, cancellationToken: deadline.Token);
                Assert.False(response.IsError == true);
                string operationId = JsonNode.Parse(response.Content.OfType<TextContentBlock>().Single().Text)!["id"]!.GetValue<string>();
                await loading.Task.WaitAsync(deadline.Token);
                var previewLifetime = ((CancellationTokenSource)typeof(MainWindow).GetField("preview", flags)!.GetValue(main)!).Token;
                if (cancelClose)
                {
                    doc.AnimationEdits!.Apply(0, "Unsaved fixture", e => e.SetFloat(164, 1));
                    timer.Start(); main.Close();
                    await closeCanceled.Task.WaitAsync(deadline.Token);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.False(closed.Task.IsCompleted);
                    Assert.False(previewLifetime.IsCancellationRequested);
                    Assert.False(loadToken.IsCancellationRequested);
                    Assert.False(doc.IsDisposed);
                    // Disabling MCP alone still recovers the retained GUI selection.
                    var stopping = main.StopMcpAsync();
                    await recovering.Task.WaitAsync(deadline.Token);
                    Assert.Same(stopping, main.StopMcpAsync());
                    Assert.False(((CancellationTokenSource)typeof(MainWindow).GetField("preview", flags)!.GetValue(main)!).IsCancellationRequested);
                    releaseRecovery.SetResult();
                    await stopping.WaitAsync(deadline.Token);
                    Assert.Equal(2, calls);
                    Assert.Equal(Visibility.Collapsed, ((TextBlock)main.FindName("EmptyPreview")).Visibility);
                    var state = await main.Commands.ExecuteAsync("zstudio_operation", new JsonObject { ["id"] = operationId }, deadline.Token);
                    Assert.Equal("canceled", state.Data["State"]!.GetValue<string>());
                    doc.AnimationEdits.MarkSaved(); main.Close();
                    await closed.Task.WaitAsync(deadline.Token);
                }
                else
                {
                    main.Close();
                    Task winner = await Task.WhenAny(closed.Task, recovering.Task).WaitAsync(deadline.Token);
                    Assert.Same(closed.Task, winner);
                    Assert.True(previewLifetime.IsCancellationRequested);
                    Assert.True(loadToken.IsCancellationRequested);
                    Assert.True(doc.IsDisposed);
                    Assert.Equal(1, calls);
                }
            }
            finally
            {
                timer.Stop(); releaseRecovery.TrySetResult();
                main.LoadAssetPropertiesAsync = loader;
                if (!doc.IsDisposed) doc.AnimationEdits!.MarkSaved();
                if (!closed.Task.IsCompleted)
                {
                    await main.StopMcpAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    main.Close();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
        await CheckAnimationSeek();
        await CheckAlreadyDrainingInspection();
    }

    private static async Task CheckAnimationSeek()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] }; package.Entries.Add(new(new byte[308], 0, 0));
        var doc = new DocumentModel(new ZbdDocument("shutdown-seek", new(0, DateTime.MinValue),
            new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package });
        main.ViewModel.Documents.Add(doc);
        using var resolver = new AssetResolver(Path.GetTempPath());
        var context = new AnimationPreviewContext { Package = doc.AnimationEdits!.Package,
            World = new ZbdDocument("world", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() } };
        var previewToken = ((CancellationTokenSource)typeof(MainWindow).GetField("preview", flags)!.GetValue(main)!).Token;
        using var audio = new AnimationAudio(() => new SilentOutput());
        using var editor = new AnimationEditor(doc, 0, resolver, previewToken, null, audio);
        var player = new AnimationPlayer(context, 0);
        typeof(AnimationEditor).GetField("context", flags)!.SetValue(editor, context);
        typeof(AnimationEditor).GetField("player", flags)!.SetValue(editor, player);
        typeof(AnimationEditor).GetField("frame", flags)!.SetValue(editor, player.Frame());
        ((FrameworkElement)editor.FindName("LoadingPanel")).Visibility = Visibility.Collapsed;
        typeof(MainWindow).GetField("animation", flags)!.SetValue(main, editor);
        typeof(MainWindow).GetField("shownDocument", flags)!.SetValue(main, doc);
        var gate = (SemaphoreSlim)typeof(AnimationEditor).GetField("simulationGate", flags)!.GetValue(editor)!;
        await gate.WaitAsync(deadline.Token);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Closed += (_, _) => closed.TrySetResult();
        var host = new LocalMcpHost(main.Commands, "test");
        typeof(MainWindow).GetField("mcpHost", flags)!.SetValue(main, host);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            var response = await client.CallToolAsync("zstudio_animation_transport", new Dictionary<string, object?>
            { ["preview"] = typeof(MainWindow).GetField("previewId", flags)!.GetValue(main)!.ToString(), ["action"] = "seek", ["seconds"] = .5 }, cancellationToken: deadline.Token);
            Assert.False(response.IsError == true);
            Assert.Equal(1L, (long)typeof(AnimationEditor).GetField("seekGeneration", flags)!.GetValue(editor)!);
            main.Close();
            // Simulate a GUI save's finally restoring enabled state while close
            // drains: the terminal lifetime still excludes fresh mutations.
            main.IsEnabled = true;
            Assert.Equal("shutting_down", (await Assert.ThrowsAsync<StudioCommandException>(() => main.Commands.ExecuteAsync("zstudio_undo_redo",
                new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" }, deadline.Token))).Code);
            await closed.Task.WaitAsync(deadline.Token);
            Assert.True(previewToken.IsCancellationRequested);
            Assert.Equal(1L, (long)typeof(AnimationEditor).GetField("seekGeneration", flags)!.GetValue(editor)!);
            bool enabled = main.ViewModel.Settings.McpEnabled;
            try
            {
                main.ViewModel.Settings.McpEnabled = true; main.InitializeMcp();
                Assert.Null(typeof(MainWindow).GetField("mcpHost", flags)!.GetValue(main));
            }
            finally { main.ViewModel.Settings.McpEnabled = enabled; }
        }
        finally
        {
            gate.Release();
            if (!closed.Task.IsCompleted) { await main.StopMcpAsync().WaitAsync(TimeSpan.FromSeconds(5)); main.Close(); }
            await audio.DisposeAsync();
        }
    }

    private static async Task CheckAlreadyDrainingInspection()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        var source = new ZbdDocument("shutdown-inspection", new(0, DateTime.MinValue),
            new(FormatFamily.Unknown, null, Recognition.Unknown, "fixture"), ReadOnlyMemory<byte>.Empty);
        source.Add(AssetKind.Raw, 0, "blocked", 0, 0);
        var doc = new DocumentModel(source); main.ViewModel.Documents.Add(doc);
        TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Closed += (_, _) => closed.TrySetResult();
        main.LoadAssetPropertiesAsync = async (_, _, token) =>
        {
            loading.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { canceled.SetResult(); await release.Task; throw; }
            return new();
        };
        var host = new LocalMcpHost(main.Commands, "test");
        typeof(MainWindow).GetField("mcpHost", flags)!.SetValue(main, host);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            var call = client.CallToolAsync("zstudio_inspect_asset", new Dictionary<string, object?>
            { ["document"] = doc.SessionId.ToString(), ["kind"] = "Raw", ["index"] = 0 }, cancellationToken: deadline.Token).AsTask();
            await loading.Task.WaitAsync(deadline.Token);
            var stopping = main.StopMcpAsync(); await canceled.Task.WaitAsync(deadline.Token);
            Assert.False(stopping.IsCompleted); main.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(closed.Task.IsCompleted); Assert.False(doc.IsDisposed);
            release.SetResult(); await closed.Task.WaitAsync(deadline.Token);
            Assert.True(doc.IsDisposed);
            try { await call; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ModelContextProtocol.McpException) { }
        }
        finally
        {
            release.TrySetResult();
            if (!closed.Task.IsCompleted) { await main.StopMcpAsync().WaitAsync(TimeSpan.FromSeconds(5)); main.Close(); }
        }
    }

    private sealed class SilentOutput : IAnimationAudioOutput
    {
        public event Action<string>? Failed { add { } remove { } }
        public void Start(ISampleProvider source) { }
        public void Dispose() { }
    }
}
