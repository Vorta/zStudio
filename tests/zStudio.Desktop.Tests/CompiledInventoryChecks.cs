using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Runs on the existing STA application; no window or physical input is required.</summary>
internal static class CompiledInventoryChecks
{
    internal static async Task Run()
    {
        var token = TestContext.Current.CancellationToken;
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "zstudio-root-inventory-" + Guid.NewGuid().ToString("N"))).FullName;
        using MainViewModel view = new();
        try
        {
            string oldRoot = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
            string nextRoot = Directory.CreateDirectory(Path.Combine(root, "next")).FullName;
            byte[] archive = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(archive, 1);
            string oldFile = Path.Combine(oldRoot, "old.zbd"); File.WriteAllBytes(oldFile, archive);
            string nested = Directory.CreateDirectory(Path.Combine(nextRoot, "nested", "deeper")).FullName;
            string two = Path.Combine(nested, "file2.zbd"), ten = Path.Combine(nested, "file10.zbd");
            File.WriteAllBytes(ten, archive); File.WriteAllBytes(two, archive);
            int published = 0; view.RootPublished += () => published++;
            await view.OpenRootAsync(oldRoot, token);
            var document = Assert.IsType<DocumentModel>(await view.OpenFileAsync(oldFile, token));
            var oldFiles = view.Files; var oldTree = Assert.Single(view.Folders); var oldResolver = view.Resolver;
            var read = view.ReadRootInventory;
            view.ReadRootInventory = (path, ct) => RootFileInventory.Read(path, StringComparer.Ordinal, ct, new CompiledInventory(0, 10, ct));
            await Assert.ThrowsAsync<CompiledInventoryCapacityException>(() => view.OpenRootAsync(nextRoot, token));
            Preserved();

            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
            view.ReadRootInventory = (path, ct) => { canceled.Cancel(); return read(path, ct); };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.OpenRootAsync(nextRoot, canceled.Token));
            Preserved();

            // The same full scan succeeds with a fresh operation after both failures.
            view.ReadRootInventory = read;
            await view.OpenRootAsync(nextRoot, token);
            Assert.Equal(2, published); Assert.Equal(nextRoot, view.RootPath); Assert.True(document.IsDisposed);
            Assert.Empty(view.Documents); Assert.Equal([two, ten], view.Files.Select(f => f.Path));
            var folder = Assert.Single(Assert.Single(view.Folders).Children);
            Assert.Equal("nested", folder.Name);
            var deeper = Assert.Single(folder.Children); Assert.Equal("deeper", deeper.Name);
            Assert.Equal([two, ten], deeper.Children.Select(n => n.Path));

            // Tree construction and ignored entries share the same owner, rather than resetting after listing.
            var comparer = CultureInfo.InvariantCulture.CompareInfo.GetStringComparer(CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
            Assert.Throws<CompiledInventoryCapacityException>(() => RootFileInventory.Read(nextRoot, comparer, token, new CompiledInventory(1024 * 1024, 3, token)));
            var complete = RootFileInventory.Read(nextRoot, comparer, token);
            Assert.Equal([two, ten], complete.Files.Select(f => f.Path)); Assert.Equal(2, complete.FileNodes.Count);
            var trailing = RootFileInventory.Read(nextRoot + Path.DirectorySeparatorChar, comparer, token);
            Assert.Equal([two, ten], trailing.Files.Select(f => f.Path)); Assert.Equal(2, trailing.FileNodes.Count);

            // A close decision can create an output in the folder being opened (including reopening this root).
            await view.OpenFileAsync(two, token);
            string saved = Path.Combine(nextRoot, "saved.zbd");
            view.ConfirmDiscardAsync = _ => { File.WriteAllBytes(saved, archive); return Task.FromResult(true); };
            await view.OpenRootAsync(nextRoot + Path.DirectorySeparatorChar, token);
            Assert.Contains(view.Files, f => f.Path == saved);
            await TexturePicker(oldRoot, oldFile);

            void Preserved()
            {
                Assert.Equal(1, published); Assert.Equal(oldRoot, view.RootPath); Assert.Same(oldFiles, view.Files);
                Assert.Same(oldTree, Assert.Single(view.Folders)); Assert.Same(oldResolver, view.Resolver);
                Assert.Same(document, Assert.Single(view.Documents)); Assert.Same(document, view.SelectedDocument); Assert.False(document.IsDisposed);
            }
        }
        finally { view.Dispose(); Directory.Delete(root, true); }
    }

    private static async Task TexturePicker(string root, string file)
    {
        var token = TestContext.Current.CancellationToken;
        MainWindow window = new();
        try
        {
            var empty = FormatRegistry.Default.OpenBytes(file, File.ReadAllBytes(file), token: token);
            var members = new[] { "first.bin", "second.bin" }.Select(name => new ResourceMember(Guid.NewGuid(), null, name, new byte[] { 1 }, new byte[148])).ToArray();
            File.WriteAllBytes(file, ArchiveWriter.Write(empty, members, token));
            await window.ViewModel.OpenRootAsync(root, token);
            var load = window.ViewModel.LoadDocumentAsync;
            window.ViewModel.LoadDocumentAsync = async (path, ct) =>
            {
                var source = await load(path, ct);
                // Empty presentation scene reaches the ordinary picker without constructing a GPU preview.
                // Reader admission/pack semantics are independently exercised by CompiledInventoryTests.
                source.Scene = new(); return source;
            };
            var discover = window.DiscoverTexturePacksAsync;
            window.DiscoverTexturePacksAsync = (resolver, path, ct) => Task.Run(() => resolver.TexturePacks(path, new CompiledInventory(0, 10, ct)), ct);
            var doc = Assert.IsType<DocumentModel>(await window.ViewModel.OpenFileAsync(file, token));
            await Work().WaitAsync(token);
            Assert.Contains(window.ViewModel.Problems, p => p.Message.Contains("compiled-file inventory", StringComparison.Ordinal));
            var combo = (ComboBox)window.FindName("TexturePackCombo"); Assert.Null(combo.ItemsSource);
            Assert.Same(doc, window.ViewModel.SelectedDocument); Assert.False(doc.IsDisposed);

            window.ViewModel.SelectedDocument = null; await Work().WaitAsync(token);
            TaskCompletionSource<string[]> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.DiscoverTexturePacksAsync = (_, _, _) => pending.Task;
            window.ViewModel.SelectedDocument = doc;
            Task pendingPreview = Work(); Assert.False(pendingPreview.IsCompleted);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("SceneHost")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("StructuredPanel")).Visibility);
            var grid = (DataGrid)window.FindName("AssetGrid");
            // ComboBox clears SelectedItem while its ItemsSource is rebound. That transient
            // null must not replace the owning document's valid filter and hide every asset.
            string filter = doc.KindFilter;
            doc.KindFilter = null!;
            Assert.Equal(filter, doc.KindFilter);
            SelectAsset(doc.Assets[1]);
            Assert.Same(pendingPreview, Work()); Assert.False(Work().IsCompleted);
            pending.SetResult([]); await pendingPreview.WaitAsync(token);
            Assert.Same(doc.Assets[1].Record, typeof(MainWindow).GetField("shownAsset", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window));

            window.ViewModel.SelectedDocument = null; await Work().WaitAsync(token);
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ViewModel.SelectedDocument = doc; pendingPreview = Work(); Assert.False(pendingPreview.IsCompleted);
            window.ViewModel.SelectedDocument = null; await Work().WaitAsync(token);
            pending.SetResult([file]); await pendingPreview.WaitAsync(token);
            Assert.Null(combo.ItemsSource); // An old worker cannot publish into the replacement selection.

            window.DiscoverTexturePacksAsync = discover; window.ViewModel.SelectedDocument = doc;
            await Work().WaitAsync(token);
            Assert.Single(combo.Items); // Successful retry retains the automatic choice even with no packs.

            window.ViewModel.SelectedDocument = null; await Work().WaitAsync(token);
            var properties = window.LoadAssetPropertiesAsync;
            TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int reads = 0;
            window.LoadAssetPropertiesAsync = async (source, asset, ct) =>
            {
                if (++reads == 1) { loading.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
                return await properties(source, asset, ct);
            };
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            using (PreviewOperation.Begin(request.Token))
            {
                window.ViewModel.SelectedDocument = doc;
                await loading.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                Task transition = Work(); Assert.False(transition.IsCompleted);
                request.Cancel();
                await transition.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
            Assert.Equal(2, reads); Assert.Same(doc, window.ViewModel.SelectedDocument);
            Assert.Same(doc.SelectedAsset!.Record, typeof(MainWindow).GetField("shownAsset", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window));
            window.LoadAssetPropertiesAsync = properties;

            window.ViewModel.SelectedDocument = null; await Work().WaitAsync(token);
            doc.SelectedAsset = doc.Assets[0];
            TaskCompletionSource oldLoading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken oldLifetime = default;
            window.LoadAssetPropertiesAsync = async (source, asset, ct) =>
            {
                if (asset.Index == 0) { oldLifetime = ct; oldLoading.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
                return await properties(source, asset, ct);
            };
            window.ViewModel.SelectedDocument = doc;
            await oldLoading.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Task supersededTransition = Work();
            Assert.Same(doc.Assets[0].Record, typeof(MainWindow).GetField("preparingAsset", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window));
            SelectAsset(doc.Assets[1]);
            Assert.True(oldLifetime.IsCancellationRequested); Assert.Same(supersededTransition, Work());
            await supersededTransition.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Same(doc.Assets[1].Record, typeof(MainWindow).GetField("shownAsset", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window));
            window.LoadAssetPropertiesAsync = properties;
            Task Work() => (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            void SelectAsset(AssetItem item)
            {
                // This window is deliberately never shown. Realize the production bindings explicitly, rather
                // than relying on deferred hidden-tab binding work or replacing SelectedItem's two-way binding.
                var content = (FrameworkElement)((TabItem)window.FindName("AssetsTab")).Content;
                content.GetBindingExpression(FrameworkElement.DataContextProperty)!.UpdateTarget();
                grid.GetBindingExpression(ItemsControl.ItemsSourceProperty)!.UpdateTarget();
                grid.GetBindingExpression(Selector.SelectedItemProperty)!.UpdateTarget();
                Assert.Same(doc, grid.DataContext);
                Assert.True(doc.FilteredAssets.Contains(item), $"Document filter excluded {item.Name}; kind={doc.KindFilter ?? "<null>"}, query={doc.Query ?? "<null>"}, assets={doc.Assets.Count}.");
                Assert.Contains(item, grid.Items.Cast<AssetItem>());
                grid.SetCurrentValue(Selector.SelectedItemProperty, item);
                grid.GetBindingExpression(Selector.SelectedItemProperty)!.UpdateSource();
                Assert.Same(item, grid.SelectedItem); Assert.Same(item, doc.SelectedAsset);
                grid.RaiseEvent(new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), new object[] { item }));
            }
        }
        finally { window.ViewModel.Dispose(); window.Close(); }
    }
}
