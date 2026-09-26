using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class OperationPublicationChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string root = Path.Combine(Path.GetTempPath(), "zstudio-operation-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await main.ViewModel.OpenRootAsync(root, deadline.Token);
            var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
            package.Entries.Add(new AnimationEntry(new byte[308], 0, 0));
            var source = new ZbdDocument(Path.Combine(root, "animation.zbd"), new(0, DateTime.MinValue),
                new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package };
            var doc = new DocumentModel(source); main.ViewModel.Documents.Add(doc);
            doc.AnimationEdits!.Apply(0, "Initial edit", e => e.SetFloat(164, 2));
            long revision = doc.Revision;
            Exception? modalFailure = null;
            var modal = new System.Windows.Window { Owner = main, Left = -12000, Width = 100, Height = 100, ShowInTaskbar = false };
            modal.Loaded += async (_, _) =>
            {
                try
                {
                    var change = new JsonObject { ["document"] = doc.SessionId.ToString(), ["revision"] = revision, ["action"] = "undo" };
                    Assert.Equal("busy", (await Assert.ThrowsAsync<StudioCommandException>(() => main.Commands.ExecuteAsync("zstudio_undo_redo", change, deadline.Token))).Code);
                }
                catch (Exception ex) { modalFailure = ex; }
                finally { modal.Close(); }
            };
            modal.ShowDialog();
            if (modalFailure != null) throw modalFailure;
            Assert.Equal(revision, doc.Revision);
            TaskCompletionSource saving = new(TaskCreationOptions.RunContinuationsAsynchronously), releaseSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[]? written = null;
            main.WriteAnimationArchiveAsync = async (data, _, _, _, token) =>
            {
                saving.SetResult(); await releaseSave.Task.WaitAsync(token);
                written = AnimationWriter.Write(data, token);
            };
            // This is the shared helper called by GUI Save after its file dialog;
            // GUI work does not acquire the MCP operation semaphore.
            var save = main.SaveAnimationToPathAsync(doc, Path.Combine(Path.GetTempPath(), "fixture-output.zbd"), deadline.Token);
            await saving.Task.WaitAsync(deadline.Token);
            Assert.False(main.IsEnabled);
            var target = new JsonObject { ["document"] = doc.SessionId.ToString(), ["revision"] = revision, ["action"] = "undo" };
            Assert.Equal("busy", (await Assert.ThrowsAsync<StudioCommandException>(() => main.Commands.ExecuteAsync("zstudio_undo_redo", target, deadline.Token))).Code);
            var close = new JsonObject { ["document"] = doc.SessionId.ToString(), ["revision"] = revision, ["discard"] = true };
            Assert.Equal("busy", (await Assert.ThrowsAsync<StudioCommandException>(() => main.Commands.ExecuteAsync("zstudio_close_document", close, deadline.Token))).Code);
            var reload = await main.Commands.ExecuteAsync("zstudio_reload_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = revision }, deadline.Token);
            var failed = await Finish(reload.Data);
            Assert.Equal("failed", failed["State"]!.GetValue<string>()); Assert.Equal("busy", failed["result"]!["code"]!.GetValue<string>());
            Assert.Equal(revision, doc.Revision); Assert.True(doc.IsDirty);
            releaseSave.SetResult(); await save.WaitAsync(deadline.Token);
            Assert.Equal(AnimationWriter.Write(doc.AnimationEdits.Package, deadline.Token), written);
            Assert.False(doc.IsDirty); Assert.True(main.IsEnabled);
            target["revision"] = doc.Revision;
            await main.Commands.ExecuteAsync("zstudio_undo_redo", target, deadline.Token);
            Assert.True(doc.IsDirty); // Later accepted changes are still unsaved.
            doc.AnimationEdits.MarkSaved(); main.ViewModel.CloseResolved(doc);

            foreach (bool export in new[] { true, false })
            {
                var inspected = new ZbdDocument(Path.Combine(root, "source.zbd"), new(0, DateTime.MinValue),
                    new(FormatFamily.Unknown, null, Recognition.Unknown, "fixture"), ReadOnlyMemory<byte>.Empty);
                var owner = new DocumentModel(inspected); main.ViewModel.Documents.Add(owner);
                TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                IProgress<ExportProgress>? progress = null;
                if (export) main.CreateAssetExporter = _ => new DeferredExporter(async p =>
                {
                    progress = p; started.SetResult(); await release.Task;
                    return new(root, 0, ["obsolete diagnostic"]);
                });
                else main.ReadValidationSourceAsync = async (_, _) =>
                {
                    started.SetResult(); await release.Task;
                    inspected.Diagnostics.Add(new("Error", "obsolete diagnostic"));
                    return inspected;
                };
                var args = new JsonObject { ["document"] = owner.SessionId.ToString() };
                if (export) args["destination"] = Path.GetTempPath();
                var running = await main.Commands.ExecuteAsync(export ? "zstudio_export" : "zstudio_validate", args, deadline.Token);
                await started.Task.WaitAsync(deadline.Token);
                main.ViewModel.CloseResolved(owner);
                main.ViewModel.Status = "New workspace state";
                progress?.Report(new(1, 1, "obsolete progress"));
                release.SetResult();
                var completed = await Finish(running.Data);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("canceled", completed["State"]!.GetValue<string>());
                Assert.Equal("New workspace state", main.ViewModel.Status);
                Assert.DoesNotContain(main.ViewModel.Problems, p => p.Message.Contains("obsolete", StringComparison.Ordinal));
            }

            async Task<JsonNode> Finish(JsonNode operation)
            {
                while (operation["State"]!.GetValue<string>() is "queued" or "running")
                {
                    await Task.Delay(1, deadline.Token);
                    operation = (await main.Commands.ExecuteAsync("zstudio_operation", new() { ["id"] = operation["id"]!.GetValue<string>() }, deadline.Token)).Data;
                }
                return operation;
            }
        }
        finally
        {
            foreach (var doc in main.ViewModel.Documents) doc.AnimationEdits?.MarkSaved();
            main.Close(); Directory.Delete(root, true);
        }
    }
    private sealed class DeferredExporter(Func<IProgress<ExportProgress>?, Task<ExportResult>> export) : IAssetExporter
    {
        public Task<ExportResult> ExportAsync(ZbdDocument document, IReadOnlyList<AssetRecord> assets, string destination, bool jsonOnly,
            string? preferredTexturePack = null, int lodLevel = 0, IProgress<ExportProgress>? progress = null, CancellationToken token = default) => export(progress);
    }
}
