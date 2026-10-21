using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Desktop;
using Xunit;

namespace zStudio.Desktop.Tests;

internal static class NavigationMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string root = Path.Combine(Path.GetTempPath(), "zstudio-navigation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        var originalLoad = main.ViewModel.LoadDocumentAsync;
        var originalExists = main.ViewModel.CheckRootExistsAsync;
        try
        {
            await main.ViewModel.OpenRootAsync(root, deadline.Token);
            var first = new DocumentModel(Source(Path.Combine(root, "first.zbd")));
            var second = new DocumentModel(Source(Path.Combine(root, "second.zbd")));
            main.ViewModel.Documents.Add(first); main.ViewModel.Documents.Add(second);
            main.ViewModel.SelectedDocument = first;
            // MCP publication must reject unfinished work instead of invoking the
            // GUI save/discard callback after its original clean-workspace check.
            main.ViewModel.ConfirmDiscardAsync = _ => throw new InvalidOperationException("MCP opened a discard prompt");

            foreach (string scenario in new[] { "selection", "failed_after_selection", "asset", "save", "success" })
            {
                main.ViewModel.SelectedDocument = first; first.SelectedAsset = first.Assets[0];
                TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<AssetDocument> decoded = new(TaskCreationOptions.RunContinuationsAsynchronously);
                main.ViewModel.LoadDocumentAsync = async (_, token) => { started.SetResult(); return await decoded.Task.WaitAsync(token); };
                string path = Path.Combine(root, "opened-" + scenario + ".zbd");
                var queued = await main.Commands.ExecuteAsync("zstudio_open_document", new() { ["path"] = path }, deadline.Token);
                await started.Task.WaitAsync(deadline.Token);
                if (scenario is "selection" or "failed_after_selection") main.ViewModel.SelectedDocument = second;
                if (scenario == "asset") first.SelectedAsset = first.Assets[1];
                if (scenario == "save") main.IsEnabled = false;
                int problems = main.ViewModel.Problems.Count;
                if (scenario == "failed_after_selection") { main.ViewModel.Status = "Newer selection retained"; decoded.SetException(new FileNotFoundException("Obsolete open failed")); }
                else decoded.SetResult(Source(path));
                var result = await Finish(queued.Data["id"]!.GetValue<Guid>().ToString());
                if (scenario == "success")
                {
                    Assert.Equal("completed", result["State"]!.GetValue<string>());
                    Assert.Equal(path, main.ViewModel.SelectedDocument!.Path);
                }
                else
                {
                    Assert.Equal("failed", result["State"]!.GetValue<string>());
                    Assert.Equal(scenario == "save" ? "busy" : "context_changed", result["result"]!["code"]!.GetValue<string>());
                    Assert.DoesNotContain(main.ViewModel.Documents, d => d.Path == path);
                    Assert.Same(scenario is "selection" or "failed_after_selection" ? second : first, main.ViewModel.SelectedDocument);
                    if (scenario == "asset") Assert.Same(first.Assets[1], first.SelectedAsset);
                    if (scenario == "failed_after_selection") { Assert.Equal("Newer selection retained", main.ViewModel.Status); Assert.Equal(problems, main.ViewModel.Problems.Count); }
                }
                main.IsEnabled = true;
            }
            // Exercise the GUI error wrapper as well as the service: an obsolete
            // decoder failure must not report into the newer selection's UI.
            main.ViewModel.SelectedDocument = first;
            TaskCompletionSource<AssetDocument> obsoleteDecode = new(TaskCreationOptions.RunContinuationsAsynchronously);
            main.ViewModel.LoadDocumentAsync = (_, _) => obsoleteDecode.Task;
            var runUi = typeof(MainWindow).GetMethod("RunUi", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var obsoleteGuiOpen = (Task)runUi.Invoke(main, new object[] { (Func<Task>)(async () => await main.ViewModel.OpenFileAsync(Path.Combine(root, "obsolete-gui.zbd"))) })!;
            main.ViewModel.SelectedDocument = second; main.ViewModel.Status = "Newer GUI navigation";
            int oldProblemCount = main.ViewModel.Problems.Count;
            obsoleteDecode.SetException(new FileNotFoundException("Obsolete GUI failure"));
            await obsoleteGuiOpen.WaitAsync(deadline.Token);
            Assert.Equal("Newer GUI navigation", main.ViewModel.Status);
            Assert.Equal(oldProblemCount, main.ViewModel.Problems.Count);
            main.ViewModel.LoadDocumentAsync = originalLoad;
            main.ViewModel.SelectedDocument = first;
            foreach (string scenario in new[] { "save", "newer_root" })
            {
                string requested = Path.Combine(root, "requested-" + scenario); Directory.CreateDirectory(requested);
                TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> exists = new(TaskCreationOptions.RunContinuationsAsynchronously);
                main.ViewModel.CheckRootExistsAsync = async (path, token) =>
                {
                    if (path != requested) return await originalExists(path, token);
                    started.SetResult(); return await exists.Task.WaitAsync(token);
                };
                var queued = await main.Commands.ExecuteAsync("zstudio_open_root", new() { ["path"] = requested }, deadline.Token);
                await started.Task.WaitAsync(deadline.Token);
                if (scenario == "save") main.IsEnabled = false;
                string expectedRoot = root;
                if (scenario == "newer_root")
                {
                    expectedRoot = Path.Combine(root, "newer"); Directory.CreateDirectory(expectedRoot);
                    // All documents are clean; this models an explicitly accepted
                    // GUI navigation while the older MCP preflight is still waiting.
                    main.ViewModel.ConfirmDiscardAsync = _ => Task.FromResult(true);
                    await main.ViewModel.OpenRootAsync(expectedRoot, deadline.Token);
                }
                exists.SetResult(true);
                var result = await Finish(queued.Data["id"]!.GetValue<Guid>().ToString());
                Assert.Equal("failed", result["State"]!.GetValue<string>());
                Assert.Equal(scenario == "save" ? "busy" : "context_changed", result["result"]!["code"]!.GetValue<string>());
                Assert.Equal(expectedRoot, main.ViewModel.RootPath);
                if (scenario != "newer_root") Assert.False(first.IsDisposed);
                main.IsEnabled = true;
            }

            var fresh = new MainWindow { Left = -12000, ShowInTaskbar = false }; fresh.Show();
            string implicitRoot = Path.Combine(root, "implicit"); Directory.CreateDirectory(implicitRoot);
            string oldPath = Path.Combine(implicitRoot, "old.zbd"), guiPath = Path.Combine(implicitRoot, "gui.zbd");
            File.WriteAllBytes(oldPath, [1, 0, 0, 0, 0, 0, 0, 0]);
            TaskCompletionSource guiStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<AssetDocument> guiLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<DocumentModel?>? guiOpening = null;
            fresh.ViewModel.LoadDocumentAsync = async (path, token) =>
            {
                Assert.Equal(guiPath, path); guiStarted.SetResult(); return await guiLoaded.Task.WaitAsync(token);
            };
            System.ComponentModel.PropertyChangedEventHandler beginGui = (_, e) =>
            {
                if (guiOpening != null || e.PropertyName != nameof(MainViewModel.Status) || fresh.ViewModel.Status != "Scanning files…") return;
                using var gui = PreviewOperation.Begin(CancellationToken.None);
                guiOpening = fresh.ViewModel.OpenFileAsync(guiPath, deadline.Token);
            };
            fresh.ViewModel.PropertyChanged += beginGui;
            try
            {
                var queued = await fresh.Commands.ExecuteAsync("zstudio_open_document", new() { ["path"] = oldPath }, deadline.Token);
                await guiStarted.Task.WaitAsync(deadline.Token);
                var rejected = await Finish(queued.Data["id"]!.GetValue<Guid>().ToString(), fresh);
                Assert.Equal("context_changed", rejected["result"]!["code"]!.GetValue<string>());
                Assert.Null(fresh.ViewModel.SelectedDocument);
                guiLoaded.SetResult(Source(guiPath));
                var guiDocument = await guiOpening!.WaitAsync(deadline.Token);
                Assert.Same(guiDocument, fresh.ViewModel.SelectedDocument);
                Assert.DoesNotContain(fresh.ViewModel.Documents, d => d.Path == oldPath);
                TaskCompletionSource<AssetDocument> rootSupersededDecode = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fresh.ViewModel.LoadDocumentAsync = (_, _) => rootSupersededDecode.Task;
                var obsoleteRootOpen = (Task)runUi.Invoke(fresh, new object[] { (Func<Task>)(async () => await fresh.ViewModel.OpenFileAsync(Path.Combine(implicitRoot, "obsolete-root.zbd"))) })!;
                fresh.ViewModel.ConfirmDiscardAsync = _ => Task.FromResult(true);
                string replacementRoot = Path.Combine(root, "replacement-root"); Directory.CreateDirectory(replacementRoot);
                await fresh.ViewModel.OpenRootAsync(replacementRoot, deadline.Token);
                string replacementStatus = fresh.ViewModel.Status; int replacementProblems = fresh.ViewModel.Problems.Count;
                rootSupersededDecode.SetException(new FileNotFoundException("Decoder failed after root replacement"));
                await obsoleteRootOpen.WaitAsync(deadline.Token);
                Assert.Equal(replacementStatus, fresh.ViewModel.Status); Assert.Equal(replacementProblems, fresh.ViewModel.Problems.Count);

                // Inject a scan-publication failure at its observable boundary,
                // after a reentrant newer root has committed. No timing race or
                // separate production scanning implementation is needed.
                string failedScanRoot = Path.Combine(root, "failed-scan"); Directory.CreateDirectory(failedScanRoot);
                string retainedScanRoot = Path.Combine(root, "retained-scan"); Directory.CreateDirectory(retainedScanRoot);
                fresh.ViewModel.CheckRootExistsAsync = (path, _) => Task.FromResult(Directory.Exists(path));
                Task? newerScan = null;
                System.ComponentModel.PropertyChangedEventHandler failScan = (_, e) =>
                {
                    if (e.PropertyName != nameof(MainViewModel.Status) || fresh.ViewModel.RootPath != failedScanRoot ||
                        !fresh.ViewModel.Status.Contains("ZBDs · indexing names", StringComparison.Ordinal)) return;
                    newerScan = fresh.ViewModel.OpenRootAsync(retainedScanRoot, deadline.Token);
                    Assert.Equal(retainedScanRoot, fresh.ViewModel.RootPath);
                    throw new IOException("Obsolete scan failed after root replacement");
                };
                fresh.ViewModel.PropertyChanged += failScan;
                try
                {
                    await (Task)runUi.Invoke(fresh, new object[] { (Func<Task>)(() => fresh.ViewModel.OpenRootAsync(failedScanRoot, deadline.Token)) })!;
                    Assert.NotNull(newerScan); await newerScan.WaitAsync(deadline.Token);
                    Assert.Equal(retainedScanRoot, fresh.ViewModel.RootPath);
                    Assert.DoesNotContain(fresh.ViewModel.Problems, p => p.Message.Contains("Obsolete scan", StringComparison.Ordinal));
                    Assert.DoesNotContain("Obsolete scan", fresh.ViewModel.Status);
                    Assert.False(fresh.ViewModel.IsBusy);
                }
                finally { fresh.ViewModel.PropertyChanged -= failScan; }
            }
            finally
            {
                fresh.ViewModel.PropertyChanged -= beginGui;
                guiLoaded.TrySetResult(Source(guiPath));
                if (guiOpening != null) await guiOpening;
                foreach (var document in fresh.ViewModel.Documents.ToArray()) fresh.ViewModel.CloseResolved(document);
                fresh.Close();
            }
        }
        finally
        {
            main.IsEnabled = true;
            main.ViewModel.LoadDocumentAsync = originalLoad; main.ViewModel.CheckRootExistsAsync = originalExists;
            main.OpenPropertiesWindow?.CloseResolved();
            foreach (var document in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(document);
            main.Close(); Directory.Delete(root, true);
        }

        async Task<JsonNode> Finish(string id, MainWindow? owner = null)
        {
            while (true)
            {
                var result = await (owner ?? main).Commands.ExecuteAsync("zstudio_operation", new() { ["id"] = id }, deadline.Token);
                if (result.Data["State"]!.GetValue<string>() is not ("queued" or "running")) return result.Data;
                await Task.Delay(5, deadline.Token);
            }
        }
    }
    private static AssetDocument Source(string path)
    {
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
        package.Entries.Add(new(new byte[308], 0, 0));
        var source = new AssetDocument(path, new(0, DateTime.MinValue), new(FormatFamily.Unknown, null, Recognition.Unknown, "navigation fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package };
        source.Add(AssetKind.Raw, 0, "first", 0, 0); source.Add(AssetKind.Raw, 1, "second", 0, 0); return source;
    }
}
