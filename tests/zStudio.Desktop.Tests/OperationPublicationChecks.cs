using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using zStudio.Core;
using zStudio.Core.Export;
using Xunit;

namespace zStudio.Desktop.Tests;

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
            foreach (bool export in new[] { true, false })
            {
                var inspected = new AssetDocument(Path.Combine(root, "source.zbd"), new(0, DateTime.MinValue),
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
            main.Close(); Directory.Delete(root, true);
        }
    }
    private sealed class DeferredExporter(Func<IProgress<ExportProgress>?, Task<ExportResult>> export) : IAssetExporter
    {
        public Task<ExportResult> ExportAsync(AssetDocument document, IReadOnlyList<AssetRecord> assets, string destination, bool jsonOnly,
            string? preferredTexturePack = null, int lodLevel = 0, IProgress<ExportProgress>? progress = null, CancellationToken token = default) => export(progress);
    }
}
