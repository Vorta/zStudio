using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Runs on the existing STA test dispatcher; barriers stop only the preparation worker.</summary>
internal static class DocumentPreparationChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await ResourceOpen(deadline.Token);
        await ResourceReload(deadline.Token);
        await SourceWorld(deadline.Token);
    }

    private static ZbdDocument Resource(string path, CancellationToken token) => FormatRegistry.Default.OpenBytes(
        Path.GetFullPath(path), Encoding.ASCII.GetBytes("# retained\r\n( VALUE ( 7 ) OTHER ( 9 ) )\r\n"), token: token);

    private static async Task ResourceOpen(CancellationToken token)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        foreach (string scenario in new[] { "success", "cancel", "navigation", "failure" })
        {
            using var vm = new MainViewModel();
            var source = Resource("preparation-open.zrd", token);
            vm.LoadDocumentAsync = (_, _) => Task.FromResult(source);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            using ManualResetEventSlim release = new(false);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PreparingDocument = (document, ct) =>
            {
                Assert.False(dispatcher.CheckAccess()); Assert.Same(source, document);
                started.SetResult(); release.Wait(ct);
                if (scenario == "failure") throw new InvalidDataException("preparation refusal");
            };
            var opening = vm.OpenFileAsync(source.Path, request.Token);
            try
            {
                await started.Task.WaitAsync(token);
                Assert.False(opening.IsCompleted); Assert.Empty(vm.Documents);
                // This continuation executes while the worker is blocked, proving the dispatcher is available.
                Assert.True(dispatcher.CheckAccess());
                if (scenario == "cancel") request.Cancel();
                if (scenario == "navigation")
                {
                    var other = new DocumentModel(new ZbdDocument(Path.GetFullPath("other.raw"), new(0, DateTime.MinValue),
                        new(FormatFamily.Unknown, null, Recognition.Unknown, "raw"), ReadOnlyMemory<byte>.Empty));
                    vm.Documents.Add(other); vm.SelectedDocument = other;
                    // Navigation cancels cold preparation, so it must finish without releasing the barrier.
                    Assert.Null(await opening.WaitAsync(token));
                    Assert.Same(other, vm.SelectedDocument); Assert.Single(vm.Documents);
                    continue;
                }
                if (scenario == "cancel")
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(token));
                    Assert.Empty(vm.Documents); continue;
                }
                release.Set();
                var opened = await opening.WaitAsync(token);
                if (scenario == "failure") { Assert.Null(opened); Assert.Empty(vm.Documents); }
                else
                {
                    Assert.NotNull(opened); Assert.Same(source, opened.Document);
                    Assert.Same(source, opened.ResourceEdits!.Current.Document);
                    Assert.Equal(source.Bytes.ToArray(), opened.ResourceEdits.Current.Document.Bytes.ToArray());
                    Assert.Equal(Assert.Single(opened.ResourceEdits.Current.Members).Id, Assert.Single(opened.Assets).ResourceId);
                }
            }
            finally { release.Set(); }
            if (scenario == "failure")
            {
                vm.PreparingDocument = null;
                Assert.NotNull(await vm.OpenFileAsync(source.Path, token));
            }
        }
    }

    private static async Task ResourceReload(CancellationToken token)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        foreach (string scenario in new[] { "success", "cancel", "revision", "failure", "close" })
        {
            using var vm = new MainViewModel();
            var source = Resource("preparation-reload.zrd", token);
            var original = new DocumentModel(source); vm.Documents.Add(original); vm.SelectedDocument = original;
            // Keep a cleared selection in the revision case, so the accepted edit changes the revision
            // independently of a selection/navigation change (covered by the open case above).
            if (scenario != "revision") original.SelectedAsset = original.Assets.Single();
            var replacement = Resource(source.Path, token);
            vm.LoadDocumentAsync = (_, _) => Task.FromResult(replacement);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            using ManualResetEventSlim release = new(false);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PreparingDocument = (_, ct) =>
            {
                Assert.False(dispatcher.CheckAccess()); started.SetResult(); release.Wait(ct);
                if (scenario == "failure") throw new InvalidDataException("preparation refusal");
            };
            long revision = original.Revision;
            var reload = vm.ReloadDocumentAsync(original, revision, cancellationToken: request.Token);
            try
            {
                await started.Task.WaitAsync(token);
                Assert.False(reload.IsCompleted); Assert.False(original.IsDisposed); Assert.Same(original, vm.SelectedDocument);
                if (scenario == "cancel") request.Cancel();
                if (scenario == "close") vm.CloseResolved(original);
                if (scenario == "revision")
                {
                    var edits = original.ResourceEdits!; var member = Assert.Single(edits.Current.Members);
                    var scalar = edits.Tree(member, token).Children[0].Children[1].Children[0];
                    edits.Accept(await edits.PrepareZrdAsync(member.Id, scalar.Id, "set", value: "8", token: token));
                }
                release.Set();
                if (scenario == "success")
                {
                    var published = await reload.WaitAsync(token);
                    Assert.True(original.IsDisposed); Assert.Same(published, vm.SelectedDocument);
                    Assert.Same(replacement, published.ResourceEdits!.Current.Document);
                }
                else if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reload.WaitAsync(token));
                else
                {
                    var error = await Assert.ThrowsAsync<StudioCommandException>(() => reload.WaitAsync(token));
                    Assert.Equal(scenario == "revision" ? "revision_conflict" : scenario == "close" ? "context_changed" : "open_failed", error.Code);
                }
                if (scenario is not ("success" or "close"))
                {
                    Assert.False(original.IsDisposed); Assert.Same(original, vm.SelectedDocument);
                    Assert.Equal(scenario == "revision", original.IsDirty);
                }
            }
            finally { release.Set(); }
        }
    }

    private static async Task SourceWorld(CancellationToken token)
    {
        using var fixture = new SourceWorldFixture();
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            using ManualResetEventSlim release = new(false);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            main.PreparingSourceWorldModel = ct =>
            {
                Assert.False(main.Dispatcher.CheckAccess()); started.SetResult(); release.Wait(ct);
            };
            var method = typeof(MainWindow).GetMethod("OpenSourceWorldAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Task<DocumentModel> Open(CancellationToken ct) => (Task<DocumentModel>)method.Invoke(main, ["m1", ct, false])!;
            var opening = Open(request.Token);
            try
            {
                await started.Task.WaitAsync(token); Assert.Empty(main.ViewModel.Documents);
                request.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(token));
                Assert.Empty(main.ViewModel.Documents);
            }
            finally { release.Set(); }

            int preparations = 0;
            main.PreparingSourceWorldModel = _ => { Assert.False(main.Dispatcher.CheckAccess()); Interlocked.Increment(ref preparations); };
            var doc = await Open(token);
            Assert.Equal(1, preparations);
            var getter = typeof(MainWindow).GetMethod("SourceWorldModel", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var model = getter.Invoke(main, [doc]);
            Assert.Same(model, getter.Invoke(main, [doc]));
            int node = doc.SourceBuild!.Provenance.Keys.First();
            string name = doc.PreviewDocument.Scene!.Nodes.Single(n => n.Index == node).Name;
            var copies = typeof(MainWindow).GetMethod("SourceCopiesNamed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal(doc.PreviewDocument.Scene.Nodes.Where(n => n.Name == name).Select(n => n.Index).OrderDescending(),
                (IEnumerable<int>)copies.Invoke(main, [doc, name, null])!);
            var result = await main.Commands.ExecuteAsync("zstudio_source_world_object", new JsonObject
            { ["document"] = doc.SessionId.ToString(), ["node"] = node }, token);
            Assert.NotNull(result.Data); Assert.Equal(1, preparations);
            // A failed replacement must retain the published model, document and clean workspace.
            main.PreparingSourceWorldModel = _ => throw new InvalidDataException("model preparation refusal");
            var error = await Assert.ThrowsAsync<StudioCommandException>(() => main.ViewModel.ReloadDocumentAsync(doc, doc.Revision, cancellationToken: token));
            Assert.Equal("build_failed", error.Code);
            Assert.False(doc.IsDisposed); Assert.Same(doc, main.ViewModel.SelectedDocument);
            Assert.Same(model, getter.Invoke(main, [doc])); Assert.False(doc.IsDirty);
            main.PreparingSourceWorldModel = _ => { Assert.False(main.Dispatcher.CheckAccess()); Interlocked.Increment(ref preparations); };
            var reloaded = await main.ViewModel.ReloadDocumentAsync(doc, doc.Revision, cancellationToken: token);
            Assert.True(doc.IsDisposed); Assert.NotSame(model, getter.Invoke(main, [reloaded])); Assert.Equal(2, preparations);
        }
        finally
        {
            main.PreparingSourceWorldModel = null;
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
}
