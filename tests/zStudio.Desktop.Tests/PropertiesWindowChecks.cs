using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using zStudio.Core;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Desktop.Tests;

// Runs inside the close regression's single STA Application, avoiding competing WPF applications/settings writes.
internal static class PropertiesWindowChecks
{
    internal static async Task Run()
    {
        ComponentFieldChecks.Run();
        await SourcePropertyInputChecks.Run();
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false };
        main.Show();
        var first = Document("one"); var second = Document("two");
        main.ViewModel.Documents.Add(first); main.ViewModel.Documents.Add(second);
        try
        {
            var diagnosticSource = new AssetDocument(Path.Combine(Path.GetTempPath(), "diagnostic-navigation.zbd"), new(256, DateTime.MinValue),
                new(FormatFamily.Unknown, null, Recognition.Unknown, "Navigation fixture"), new byte[256]);
            diagnosticSource.Add(AssetKind.Raw, 0, "first", 0, 128);
            diagnosticSource.Add(AssetKind.Raw, 1, "affected", 128, 128);
            var diagnosticDoc = new DocumentModel(diagnosticSource);
            main.ViewModel.Documents.Add(diagnosticDoc);
            diagnosticDoc.Query = "hidden by filter"; diagnosticDoc.KindFilter = "Texture";
            await main.NavigateProblemAsync(new("Warning", "File / operation", "Interior error", diagnosticDoc.Path, 1, 140));
            await Idle();
            Assert.Same(diagnosticDoc, main.ViewModel.SelectedDocument);
            Assert.Equal(1, diagnosticDoc.SelectedAsset!.Index);
            Assert.Equal("", diagnosticDoc.Query); Assert.Equal("All types", diagnosticDoc.KindFilter);
            Assert.Same(diagnosticDoc.SelectedAsset, ((DataGrid)main.FindName("AssetGrid")).SelectedItem);
            await main.NavigateProblemAsync(new("Warning", "File / operation", "Offset-only error", diagnosticDoc.Path, Offset: 64));
            Assert.Equal(0, diagnosticDoc.SelectedAsset!.Index);
            await main.ViewModel.CloseAsync(diagnosticDoc);

            // Properties is one owned, modeless window that never consumes workspace space; it stays pinned to its
            // document across ordinary selection, remembers its size and closes with its document.
            await main.OpenAssetPropertiesAsync(first, first.Assets[0].Record, CancellationToken.None);
            var popup = main.OpenPropertiesWindow!;
            Assert.True(popup.IsVisible); Assert.True(main.IsEnabled);
            Assert.Null(main.FindName("PropertiesPane"));
            await Idle();
            main.ViewModel.SelectedDocument = second;
            await Idle();
            Assert.Same(first, popup.Document); Assert.Same(popup, main.OpenPropertiesWindow);
            // Keep this persistence fixture inside the desktop's restore constraints.
            var workArea = SystemParameters.WorkArea;
            double savedWidth = Math.Min(700, Math.Floor(workArea.Width));
            double savedHeight = Math.Min(600, Math.Floor(workArea.Height));
            Assert.InRange(savedWidth, popup.MinWidth, workArea.Width);
            Assert.InRange(savedHeight, popup.MinHeight, workArea.Height);
            popup.Width = savedWidth; popup.Height = savedHeight; await Idle();
            var navigation = (TabControl)main.FindName("NavigationTabs"); var tools = (TabControl)main.FindName("ToolTabs");
            navigation.SelectedIndex = 2; tools.SelectedIndex = 2;
            var viewMenu = ((Menu)main.FindName("AppMenu")).Items.OfType<MenuItem>().Single(m => Equals(m.Header, "_View"));
            viewMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Reset layout")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Idle();
            Assert.Equal(0, navigation.SelectedIndex); Assert.Equal(1, tools.SelectedIndex);
            Assert.Equal(0, main.ViewModel.Settings.GetWorkspace().BrowserTab);
            Assert.Equal(1, main.ViewModel.Settings.GetWorkspace().ToolTab);
            Assert.Same(first, popup.Document);
            popup.Close(); await Idle();
            Assert.Null(main.OpenPropertiesWindow);
            Assert.Equal(savedWidth, main.ViewModel.Settings.GetWorkspace().PropertiesWindow.Width);
            Assert.Equal(savedHeight, main.ViewModel.Settings.GetWorkspace().PropertiesWindow.Height);
            await main.OpenAssetPropertiesAsync(first, first.Assets[0].Record, CancellationToken.None);
            var finalPopup = main.OpenPropertiesWindow!;
            Assert.Equal(savedWidth, finalPopup.Width); Assert.Equal(savedHeight, finalPopup.Height);
            await main.ViewModel.CloseAsync(first); await Idle();
            Assert.True(first.IsDisposed); Assert.Null(main.OpenPropertiesWindow); Assert.False(finalPopup.IsVisible);
        }
        finally
        {
            main.OpenPropertiesWindow?.CloseResolved();
            main.Close(); await Idle();
        }
    }
    private static DocumentModel Document(string name)
    {
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
        var entry = new AnimationEntry(new byte[308], 0, 0); entry.SetText(0, name);
        var sequence = new AnimationSequence(new byte[64]) { Name = "sequence" };
        sequence.Events.Add(AnimationCatalog.Create(10)); sequence.Events.Add(AnimationCatalog.Create(10));
        entry.Sequences.Add(sequence); package.Entries.Add(entry);
        var document = new AssetDocument(Path.Combine(Path.GetTempPath(), name + ".zbd"), new(0, DateTime.MinValue), new(FormatFamily.Animation, 28, Recognition.Supported, "Properties fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package };
        document.Add(AssetKind.Raw, 0, "Non-animation preview", 0, 0);
        return new(document);
    }
    private static async Task Idle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(30); }
}
