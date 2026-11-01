using System.IO;
using System.Reflection;
using System.Windows.Threading;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Desktop.Tests;

internal static class ReloadChecks
{
    internal static async Task Run()
    {
        foreach (string scenario in new[] { "missing", "parse", "unknown", "cancel", "draft", "navigation", "active", "background" })
        {
            using var vm = new MainViewModel();
            var original = new DocumentModel(Source("reload.zbd"));
            var other = new DocumentModel(Source("other.zbd"));
            vm.Documents.Add(original); vm.Documents.Add(other);
            original.SelectedAsset = original.Assets.Single(); original.Query = "entry";
            vm.SelectedDocument = scenario == "background" ? other : original;
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<AssetDocument> parsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.LoadDocumentAsync = async (_, token) => { started.SetResult(); return await parsed.Task.WaitAsync(token); };
            bool draft = false;
            vm.ValidateReload = _ => { if (draft) throw new StudioCommandException("pending_drafts", "fixture draft"); };
            using var cancellation = new CancellationTokenSource();
            var pending = vm.ReloadDocumentAsync(original, original.Revision, cancellationToken: cancellation.Token);
            await started.Task;
            Assert.False(original.IsDisposed); Assert.Contains(original, vm.Documents);
            if (scenario == "cancel") cancellation.Cancel();
            if (scenario == "draft") draft = true;
            if (scenario == "navigation") vm.SelectedDocument = other;
            var replacement = Source(original.Path);
            if (scenario == "parse") replacement.Diagnostics.Add(new("Error", "Parsing stopped: malformed fixture"));
            if (scenario == "unknown") replacement = new(original.Path, new(0, DateTime.MinValue), new(FormatFamily.Unknown, null, Recognition.Unknown, "raw"), ReadOnlyMemory<byte>.Empty);
            if (scenario == "missing") parsed.SetException(new FileNotFoundException("fixture disappeared"));
            else parsed.TrySetResult(replacement);
            if (scenario is "active" or "background")
            {
                var published = await pending;
                Assert.True(original.IsDisposed); Assert.DoesNotContain(original, vm.Documents);
                Assert.Same(published, vm.Documents[0]); Assert.Equal("entry", published.Query);
                Assert.Equal(original.SelectedAsset!.Record.Kind, published.SelectedAsset!.Record.Kind);
                Assert.Equal(original.SelectedAsset.Record.Index, published.SelectedAsset.Record.Index);
                Assert.Same(scenario == "active" ? published : other, vm.SelectedDocument);
            }
            else
            {
                if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                else
                {
                    string code = scenario switch { "draft" => "pending_drafts", "navigation" => "context_changed", _ => "open_failed" };
                    Assert.Equal(code, (await Assert.ThrowsAsync<StudioCommandException>(() => pending)).Code);
                }
                Assert.False(original.IsDisposed); Assert.Same(original, vm.Documents[0]);
                Assert.Same(scenario == "navigation" ? other : original, vm.SelectedDocument);
            }
        }
        // An obsolete reload failure does not report into a newer selection.
        using var gui = new MainViewModel();
        var reloaded = new DocumentModel(Source("reloaded.zbd")); gui.Documents.Add(reloaded); gui.SelectedDocument = reloaded;
        TaskCompletionSource parsing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<AssetDocument> failedParse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gui.LoadDocumentAsync = (_, _) => { parsing.SetResult(); return failedParse.Task; };
        var obsoleteReload = gui.ReloadAsync(); await parsing.Task;
        var newer = new DocumentModel(Source("newer.zbd")); gui.Documents.Add(newer); gui.SelectedDocument = newer;
        gui.Status = "New navigation"; int problems = gui.Problems.Count;
        failedParse.SetException(new FileNotFoundException("obsolete failure"));
        await obsoleteReload;
        Assert.Equal("New navigation", gui.Status); Assert.Equal(problems, gui.Problems.Count);
        await CompiledFilePolling();
    }

    // A metadata query can block indefinitely inside Windows. The barrier models that read without relying on an
    // unavailable share, and protects dispatcher responsiveness, bounded polling and obsolete close/reload results.
    private static async Task CompiledFilePolling()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token = deadline.Token;
        var dispatcher = Dispatcher.CurrentDispatcher;
        string root = Path.Combine(Path.GetTempPath(), "zstudio-stamp-polling-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (string scenario in new[] { "unchanged", "changed", "failure", "close", "reload", "root", "dispose" })
            {
                using var vm = new MainViewModel();
                var original = new DocumentModel(Source(Path.Combine(root, "polling.zbd"))) { IsStale = scenario == "unchanged" };
                vm.Documents.Add(original); vm.SelectedDocument = original;
                using ManualResetEventSlim release = new(false);
                TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                int reads = 0;
                vm.ReadExternalFileStamp = _ =>
                {
                    Assert.False(dispatcher.CheckAccess()); Interlocked.Increment(ref reads); started.TrySetResult();
                    release.Wait(token);
                    if (scenario == "failure") throw new FileNotFoundException("fixture file disappeared");
                    return scenario == "unchanged" ? original.Document.Stamp : new(1, DateTime.UtcNow);
                };
                var pending = vm.CheckExternalChangesAsync();
                var worker = (Task)typeof(MainViewModel).GetField("compiledFileCheckWorker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
                try
                {
                    await started.Task.WaitAsync(token);
                    Assert.False(pending.IsCompleted);
                    await dispatcher.InvokeAsync(() => Assert.True(dispatcher.CheckAccess()), DispatcherPriority.Background, token);
                    var repeated = Enumerable.Range(0, 8).Select(_ => vm.CheckExternalChangesAsync()).ToArray();
                    Assert.All(repeated, task => { Assert.Same(pending, task); Assert.False(task.IsCompleted); }); Assert.Equal(1, Volatile.Read(ref reads));
                    bool retired = scenario is "close" or "reload" or "root" or "dispose";
                    if (scenario == "close") vm.CloseResolved(original);
                    if (scenario == "reload")
                    {
                        vm.LoadDocumentAsync = (path, _) => Task.FromResult(Source(path));
                        await vm.ReloadDocumentAsync(original, original.Revision, cancellationToken: token);
                    }
                    if (scenario == "root") await vm.OpenRootAsync(root, token);
                    if (scenario == "dispose") vm.Dispose();
                    if (retired)
                    {
                        // Closing the snapshot stops waiting immediately, while its uninterruptible read still owns
                        // the one worker slot. New documents and repeated timer ticks cannot queue blocked reads.
                        await pending.WaitAsync(token); await Task.WhenAll(repeated).WaitAsync(token);
                        Assert.False(release.IsSet); Assert.False(original.IsStale);
                        if (scenario is "close" or "root") vm.AddDocument(new DocumentModel(Source(original.Path)), true);
                        await vm.CheckExternalChangesAsync().WaitAsync(token); Assert.Equal(1, Volatile.Read(ref reads));
                        Assert.All(vm.Documents, doc => Assert.False(doc.IsStale));
                    }
                    release.Set();
                    if (retired) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(token));
                    else await worker.WaitAsync(token);
                    await pending.WaitAsync(token); await Task.WhenAll(repeated).WaitAsync(token);
                    if (retired)
                    {
                        Assert.False(original.IsStale); Assert.All(vm.Documents, doc => Assert.False(doc.IsStale));
                    }
                    else Assert.Equal(scenario != "unchanged", original.IsStale);
                    if (scenario != "dispose")
                    {
                        var stamp = vm.SelectedDocument!.Document.Stamp;
                        vm.ReadExternalFileStamp = _ => { Interlocked.Increment(ref reads); return stamp; };
                        await vm.CheckExternalChangesAsync().WaitAsync(token);
                        Assert.Equal(2, Volatile.Read(ref reads)); Assert.All(vm.Documents, doc => Assert.False(doc.IsStale));
                    }
                }
                finally { release.Set(); }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static AssetDocument Source(string path)
    {
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
        package.Entries.Add(new(new byte[308], 0, 0));
        var source = new AssetDocument(path, new(0, DateTime.MinValue), new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package };
        source.Add(AssetKind.Animation, 0, "entry", 0, 0);
        return source;
    }
}
