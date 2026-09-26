using System.IO;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class GuiNavigationChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string root = Path.Combine(Path.GetTempPath(), "zstudio-gui-navigation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string oldPath = Path.Combine(root, "initial.zbd"), newerPath = Path.Combine(root, "newer.zbd");
        File.WriteAllBytes(oldPath, [1, 0, 0, 0, 0, 0, 0, 0]);
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        TaskCompletionSource<ZbdDocument> newerLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DocumentModel?>? newerOpen = null;
        System.ComponentModel.PropertyChangedEventHandler navigate = (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.Status) || main.ViewModel.Status != "Scanning files…" || newerOpen != null) return;
            newerOpen = main.ViewModel.OpenFileAsync(newerPath, deadline.Token);
        };
        try
        {
            main.ViewModel.LoadDocumentAsync = (path, token) =>
            { Assert.Equal(newerPath, path); return newerLoaded.Task.WaitAsync(token); };
            main.ViewModel.PropertyChanged += navigate;
            var conflict = await Assert.ThrowsAsync<StudioCommandException>(() => main.OpenFilesAsync([oldPath]).WaitAsync(deadline.Token));
            Assert.Equal("context_changed", conflict.Code); Assert.NotNull(newerOpen);
            Assert.Null(main.ViewModel.SelectedDocument);
            newerLoaded.SetResult(Source(newerPath));
            Assert.Same(await newerOpen.WaitAsync(deadline.Token), main.ViewModel.SelectedDocument);
            main.ViewModel.PropertyChanged -= navigate;

            // Startup's forced root replacement and ordinary first-file opening
            // both use the same guard without rejecting an uncontested request.
            main.ViewModel.ConfirmDiscardAsync = _ => Task.FromResult(true);
            main.ViewModel.LoadDocumentAsync = (path, _) => Task.FromResult(Source(path));
            await main.OpenFilesAsync([oldPath], forceRoot: true).WaitAsync(deadline.Token);
            Assert.Equal(oldPath, main.ViewModel.SelectedDocument!.Path);

            TaskCompletionSource<ZbdDocument> firstLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            string firstPath = Path.Combine(root, "drop-first.zbd"), secondPath = Path.Combine(root, "drop-second.zbd");
            main.ViewModel.LoadDocumentAsync = (path, _) =>
            { Assert.Equal(firstPath, path); return firstLoaded.Task; };
            var drop = main.OpenFilesAsync([firstPath, secondPath]);
            main.ViewModel.SelectedDocument = null;
            firstLoaded.SetResult(Source(firstPath));
            Assert.Equal("context_changed", (await Assert.ThrowsAsync<StudioCommandException>(() => drop.WaitAsync(deadline.Token))).Code);
            Assert.DoesNotContain(main.ViewModel.Documents, d => d.Path == firstPath || d.Path == secondPath);
        }
        finally
        {
            main.ViewModel.PropertyChanged -= navigate;
            newerLoaded.TrySetResult(Source(newerPath));
            if (newerOpen != null) await newerOpen;
            foreach (var document in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(document);
            main.Close(); Directory.Delete(root, true);
        }
    }
    private static ZbdDocument Source(string path) => new(path, new(0, DateTime.MinValue),
        new(FormatFamily.Unknown, null, Recognition.Unknown, "GUI navigation fixture"), ReadOnlyMemory<byte>.Empty);
}
