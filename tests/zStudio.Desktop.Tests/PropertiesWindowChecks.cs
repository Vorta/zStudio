using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

// Runs inside the close regression's single STA Application, avoiding competing WPF applications/settings writes.
internal static class PropertiesWindowChecks
{
    internal static async Task Run(Application app)
    {
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false };
        main.Show();
        var first = Document("one"); var second = Document("two");
        main.ViewModel.Documents.Add(first); main.ViewModel.Documents.Add(second);
        try
        {
            var diagnosticSource = new ZbdDocument(Path.Combine(Path.GetTempPath(), "diagnostic-navigation.zbd"), new(256, DateTime.MinValue),
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

            main.OpenAnimationProperties(first, 0, Guid.Empty, Guid.Empty);
            var popup = main.OpenPropertiesWindow!;
            var form = popup.AnimationFields!;
            Assert.True(popup.IsVisible); Assert.True(main.IsEnabled);
            Assert.Null(main.FindName("PropertiesPane"));
            await Idle();
            var delay = Descendants(form).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Reset delay (s)");
            delay.Text = "-";
            main.ViewModel.SelectedDocument = second;
            await Idle();
            Assert.Same(first, popup.Document); Assert.Same(form, popup.AnimationFields);
            Assert.True(form.HasPendingDrafts); Assert.False(first.IsDirty);
            popup.Width = 700; popup.Height = 600; await Idle();
            Assert.Equal("-", delay.Text); Assert.Same(delay, Descendants(form).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Reset delay (s)"));
            var navigation = (TabControl)main.FindName("NavigationTabs"); var tools = (TabControl)main.FindName("ToolTabs");
            navigation.SelectedIndex = 2; tools.SelectedIndex = 5;
            var viewMenu = ((Menu)main.FindName("AppMenu")).Items.OfType<MenuItem>().Single(m => Equals(m.Header, "_View"));
            viewMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Reset layout")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Idle();
            Assert.Equal(0, navigation.SelectedIndex); Assert.Equal(4, tools.SelectedIndex);
            Assert.Equal(-1, ((TabControl)main.FindName("InspectorTabs")).SelectedIndex);
            Assert.Equal(0, main.ViewModel.Settings.GetWorkspace().BrowserTab);
            Assert.Equal(4, main.ViewModel.Settings.GetWorkspace().ToolTab);
            Assert.True(form.HasPendingDrafts); Assert.Equal("-", delay.Text);
            delay.Text = "3.25"; Send(delay, Key.Enter); await Idle();
            Assert.Equal(3.25f, first.AnimationEdits!.Package.Entries[0].F32(164)); Assert.False(second.IsDirty);
            Assert.False(((Button)main.FindName("DocumentUndo")).IsEnabled); // Pinned edits do not target the other active document.
            main.ViewModel.SelectedDocument = first; await Idle();
            Assert.Null(((ContentControl)main.FindName("AnimationHost")).Content);
            Descendants(popup).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Undo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.False(first.IsDirty); Assert.Equal("0", delay.Text);
            CheckHistory(false, true);
            delay.Text = "5"; Send(delay, Key.Enter); await Idle();
            CheckHistory(true, false);
            ((Button)main.FindName("DocumentUndo")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            CheckHistory(false, true); Assert.Equal("0", delay.Text);
            ((Button)main.FindName("DocumentRedo")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            CheckHistory(true, false); Assert.Equal("5", delay.Text);
            ((Button)main.FindName("DocumentUndo")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            main.ViewModel.SelectedDocument = second; await Idle(); CheckHistory(false, false);

            void CheckHistory(bool canUndo, bool canRedo)
            {
                Assert.Equal(canUndo, ((Button)main.FindName("DocumentUndo")).IsEnabled);
                Assert.Equal(canRedo, ((Button)main.FindName("DocumentRedo")).IsEnabled);
                Assert.Equal(canUndo, ((MenuItem)main.FindName("UndoMenu")).IsEnabled);
                Assert.Equal(canRedo, ((MenuItem)main.FindName("RedoMenu")).IsEnabled);
            }
            DocumentModel? savedDocument = null;
            popup.SaveRequested = (document, _) => { savedDocument = document; return Task.FromResult(true); };
            // Exercise the retained keyboard command without changing physical keyboard state.
            await (Task)typeof(PropertiesWindow).GetMethod("SaveAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(popup, [false])!;
            Assert.Same(first, savedDocument);

            delay.Text = "invalid";
            using (Answer(app, "Keep editing")) main.OpenAnimationProperties(second, 0, Guid.Empty, Guid.Empty);
            Assert.Same(first, popup.Document); Assert.Equal("invalid", delay.Text);
            // A clean document with only a draft must still run its close guard.
            using (Answer(app, "Keep editing")) await main.ViewModel.CloseAsync(first);
            Assert.False(first.IsDisposed); Assert.Contains(first, main.ViewModel.Documents);
            using (Answer(app, "Keep editing")) { popup.Close(); await Idle(); }
            Assert.Same(popup, main.OpenPropertiesWindow); Assert.True(popup.IsVisible);
            Send(delay, Key.Escape); Assert.False(form.HasPendingDrafts);

            var entry = first.AnimationEdits.Package.Entries[0];
            var sequence = entry.Sequences[0]; var ev = sequence.Events[0];
            main.OpenAnimationProperties(first, 0, sequence.Id, ev.Id);
            Assert.Same(popup, main.OpenPropertiesWindow);
            Assert.Contains("Procedural motion", popup.Title);
            first.AnimationEdits.Apply(0, "Remove inspected event", e => e.Sequences[0].Events.RemoveAll(v => v.Id == ev.Id));
            Assert.False(popup.AnimationFields!.TargetAvailable);
            first.AnimationEdits.Undo(); await Idle();
            Assert.True(popup.AnimationFields.TargetAvailable);
            Assert.Equal(ev.ToJson().ToJsonString(), popup.AnimationFields.Json.ToJsonString());
            first.AnimationEdits.Apply(0, "Rename and reorder", e => { e.Sequences[0].Name = "renamed"; e.Sequences[0].Events.Reverse(); }); await Idle();
            Assert.Contains("renamed", popup.Title); Assert.True(popup.AnimationFields.TargetAvailable);
            first.AnimationEdits.Undo();

            main.OpenAnimationProperties(first, 0, Guid.Empty, Guid.Empty);
            await Idle();
            var reset = Descendants(popup.AnimationFields!).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Reset delay (s)");
            reset.Text = "8"; Send(reset, Key.Enter); await Idle();
            popup.Close(); await Idle();
            Assert.Null(main.OpenPropertiesWindow); Assert.True(first.IsDirty);
            Assert.Equal(8f, first.AnimationEdits.Package.Entries[0].F32(164));
            first.AnimationEdits.Undo();
            main.OpenAnimationProperties(first, 0, Guid.Empty, Guid.Empty);
            var finalPopup = main.OpenPropertiesWindow!;
            Assert.Equal(700, finalPopup.Width); Assert.Equal(600, finalPopup.Height);
            await main.ViewModel.CloseAsync(first); await Idle();
            Assert.True(first.IsDisposed); Assert.Null(main.OpenPropertiesWindow); Assert.False(finalPopup.IsVisible);

            // Root replacement uses the document close guard even when the pinned
            // document is clean/inactive and only the modeless window has a draft.
            string nextRoot = Directory.CreateTempSubdirectory("zstudio-root-draft-").FullName;
            try
            {
                var pinned = Document("root-draft"); main.ViewModel.Documents.Add(pinned);
                main.ViewModel.SelectedDocument = second;
                main.OpenAnimationProperties(pinned, 0, Guid.Empty, Guid.Empty); await Idle();
                var pinnedPopup = main.OpenPropertiesWindow!;
                var input = Descendants(pinnedPopup.AnimationFields!).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Reset delay (s)");
                string oldRoot = main.ViewModel.RootPath;
                input.Text = "-";
                using (Answer(app, "Keep editing")) await main.ViewModel.OpenRootAsync(nextRoot);
                Assert.Equal(oldRoot, main.ViewModel.RootPath); Assert.False(pinned.IsDisposed);
                Assert.Same(pinnedPopup, main.OpenPropertiesWindow); Assert.Equal("-", input.Text);
                Assert.False(pinned.IsDirty); Assert.True(pinnedPopup.HasPendingDrafts);

                input.Text = "4.5";
                using (Answer(app, "Cancel", "Unsaved changes")) await main.ViewModel.OpenRootAsync(nextRoot);
                Assert.Equal(oldRoot, main.ViewModel.RootPath); Assert.False(pinned.IsDisposed);
                Assert.True(pinned.IsDirty); Assert.Equal(4.5f, pinned.AnimationEdits!.Package.Entries[0].F32(164));
                pinned.AnimationEdits.Undo(); await Idle(); Assert.False(pinned.IsDirty);

                input.Text = "-";
                using (Answer(app, "Discard draft")) await main.ViewModel.OpenRootAsync(nextRoot);
                Assert.Equal(nextRoot, main.ViewModel.RootPath); Assert.True(pinned.IsDisposed);
                Assert.True(second.IsDisposed); Assert.Null(main.OpenPropertiesWindow);
            }
            finally { Directory.Delete(nextRoot); }
        }
        finally
        {
            main.OpenPropertiesWindow?.CloseResolved();
            foreach (var doc in main.ViewModel.Documents) doc.AnimationEdits?.MarkSaved();
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
        var document = new ZbdDocument(Path.Combine(Path.GetTempPath(), name + ".zbd"), new(0, DateTime.MinValue), new(FormatFamily.Animation, 28, Recognition.Supported, "Properties fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package };
        document.Add(AssetKind.Raw, 0, "Non-animation preview", 0, 0);
        return new(document);
    }
    private static async Task Idle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(30); }
    private static void Send(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static IDisposable Answer(Application app, string answer, string title = "Resolve property input") => new PromptAnswer(app, answer, title);
    private sealed class PromptAnswer : IDisposable
    {
        private readonly DispatcherTimer timer = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
        public PromptAnswer(Application app, string answer, string title)
        {
            timer.Tick += (_, _) =>
            {
                if (app.Windows.Cast<Window>().FirstOrDefault(w => w.Title == title) is not { } dialog) return;
                var button = ((StackPanel)dialog.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(b => Equals(b.Content, answer));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
        }
        public void Dispose() => timer.Stop();
    }
}
