using System.IO;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class ReloadChecks
{
    internal static async Task Run()
    {
        foreach (string scenario in new[] { "missing", "parse", "unknown", "cancel", "revision", "draft", "navigation", "active", "background" })
        {
            using var vm = new MainViewModel();
            var original = new DocumentModel(Source("reload.zbd"));
            var other = new DocumentModel(Source("other.zbd"));
            vm.Documents.Add(original); vm.Documents.Add(other);
            original.SelectedAsset = original.Assets.Single(); original.Query = "entry";
            vm.SelectedDocument = scenario == "background" ? other : original;
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<ZbdDocument> parsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.LoadDocumentAsync = async (_, token) => { started.SetResult(); return await parsed.Task.WaitAsync(token); };
            bool draft = false;
            vm.ValidateReload = _ => { if (draft) throw new StudioCommandException("pending_drafts", "fixture draft"); };
            using var cancellation = new CancellationTokenSource();
            var pending = vm.ReloadDocumentAsync(original, original.Revision, cancellationToken: cancellation.Token);
            await started.Task;
            Assert.False(original.IsDisposed); Assert.Contains(original, vm.Documents);
            if (scenario == "cancel") cancellation.Cancel();
            if (scenario == "revision") original.AnimationEdits!.Apply(0, "Concurrent edit", e => e.SetInt(164, 1));
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
                    string code = scenario switch { "revision" => "revision_conflict", "draft" => "pending_drafts", "navigation" => "context_changed", _ => "open_failed" };
                    Assert.Equal(code, (await Assert.ThrowsAsync<StudioCommandException>(() => pending)).Code);
                }
                Assert.False(original.IsDisposed); Assert.Same(original, vm.Documents[0]);
                Assert.Same(scenario == "navigation" ? other : original, vm.SelectedDocument);
                if (scenario == "revision") Assert.True(original.IsDirty);
            }
        }
        // GUI discard authorization applies only at successful publication.
        using var gui = new MainViewModel();
        var dirty = new DocumentModel(Source("dirty.zbd")); gui.Documents.Add(dirty); gui.SelectedDocument = dirty;
        dirty.AnimationEdits!.Apply(0, "Before reload", e => e.SetInt(164, 1));
        gui.ConfirmDiscardAsync = _ => Task.FromResult(true);
        gui.LoadDocumentAsync = (_, _) => throw new FileNotFoundException("fixture disappeared");
        await gui.ReloadAsync();
        Assert.Same(dirty, gui.SelectedDocument); Assert.False(dirty.IsDisposed); Assert.True(dirty.IsDirty);
        gui.LoadDocumentAsync = (_, _) => Task.FromResult(Source(dirty.Path));
        await gui.ReloadAsync();
        Assert.True(dirty.IsDisposed); Assert.NotSame(dirty, gui.SelectedDocument); Assert.False(gui.SelectedDocument!.IsDirty);

        TaskCompletionSource parsing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ZbdDocument> failedParse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gui.LoadDocumentAsync = (_, _) => { parsing.SetResult(); return failedParse.Task; };
        var obsoleteReload = gui.ReloadAsync(); await parsing.Task;
        var newer = new DocumentModel(Source("newer.zbd")); gui.Documents.Add(newer); gui.SelectedDocument = newer;
        gui.Status = "New navigation"; int problems = gui.Problems.Count;
        failedParse.SetException(new FileNotFoundException("obsolete failure"));
        await obsoleteReload;
        Assert.Equal("New navigation", gui.Status); Assert.Equal(problems, gui.Problems.Count);
    }

    private static ZbdDocument Source(string path)
    {
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
        package.Entries.Add(new(new byte[308], 0, 0));
        var source = new ZbdDocument(path, new(0, DateTime.MinValue), new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package };
        source.Add(AssetKind.Animation, 0, "entry", 0, 0);
        return source;
    }
}
