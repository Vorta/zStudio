using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class WindowClosingTests
{
    [Fact]
    public async Task DiscardAndCancelUseTheRealWindowCloseLifecycle()
    {
        // WPF permits one Application per process and requires its own STA dispatcher.
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
            byte[]? previousSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
            // Match the Fluent theme used by the production application.
#pragma warning disable WPF0001
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
#pragma warning restore WPF0001
            // These are the only application resources needed by the shell. No game data,
            // rendering or production unhandled-error dialog is involved in this fixture.
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Recoil.Zbd.Studio;component/PreviewStyles.xaml", UriKind.Relative) });
            app.Resources["BoolVisibility"] = new BooleanToVisibilityConverter();
            app.Resources["Checker"] = Brushes.Transparent;
            Exception? failure = null;
            app.DispatcherUnhandledException += (_, e) => { failure ??= e.Exception; e.Handled = true; };
            app.Startup += async (_, _) =>
            {
                try
                {
                    await Check(["Discard"], 1, closes: true);
                    await Check(["Cancel"], 1, closes: false);
                    await Check(["Discard", "Discard"], 2, closes: true);
                    await Check(["Discard", "Cancel"], 2, closes: false);
                    await Check(["Discard"], 1, closes: true, repeatClose: true);
                    await Check([], 0, closes: true);
                    await PropertiesWindowChecks.Run(app);
                }
                catch (Exception ex) { failure ??= ex; }
                finally
                {
                    foreach (Window window in app.Windows.Cast<Window>().ToArray())
                    {
                        if (window is MainWindow main)
                            foreach (var doc in main.ViewModel.Documents) doc.AnimationEdits?.MarkSaved();
                        window.Close();
                    }
                    app.Shutdown();
                }

                async Task Check(string[] answers, int documentCount, bool closes, bool repeatClose = false)
                {
                    var window = new MainWindow { Left = -12000, ShowInTaskbar = false };
                    window.Show();
                    var documents = Enumerable.Range(0, documentCount).Select(DirtyDocument).ToArray();
                    var lifetimes = documents.Select(d => d.Lifetime.Token).ToArray();
                    foreach (var document in documents) window.ViewModel.Documents.Add(document);
                    bool closed = false;
                    window.Closed += (_, _) => closed = true;
                    int prompts = 0;
                    var pending = new Queue<string>(answers);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
                    timer.Tick += (_, _) =>
                    {
                        var dialog = app.Windows.Cast<Window>().FirstOrDefault(w => w.Owner == window && w.Title == "Unsaved changes");
                        if (dialog == null) return;
                        try
                        {
                            timeout.Token.ThrowIfCancellationRequested();
                            if (repeatClose)
                            {
                                // A second request while the modal confirmation is open must
                                // neither dispose the document nor create another prompt.
                                window.Close();
                                Assert.False(closed);
                                Assert.All(lifetimes, lifetime => Assert.False(lifetime.IsCancellationRequested));
                            }
                            Assert.NotEmpty(pending);
                            string answer = pending.Dequeue(); prompts++;
                            var buttons = ((StackPanel)dialog.Content).Children.OfType<WrapPanel>().Single();
                            buttons.Children.OfType<Button>().Single(b => Equals(b.Content, answer))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        catch (Exception ex)
                        {
                            failure ??= ex;
                            // Release ShowDialog's nested dispatcher even when an assertion
                            // fails, so the fixture can restore settings and shut down.
                            dialog.Close();
                        }
                    };
                    timer.Start();
                    try
                    {
                        if (documentCount == 1)
                            ((Button)window.FindName("CaptionClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else window.Close();
                        while (failure == null && (prompts < answers.Length || closes && !closed))
                            await Task.Delay(10, timeout.Token);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        if (failure != null) throw new InvalidOperationException("Window close raised an unhandled exception.", failure);
                        Assert.Equal(answers.Length, prompts);
                        Assert.Equal(closes, closed);
                        Assert.All(lifetimes, lifetime => Assert.Equal(closes, lifetime.IsCancellationRequested));
                        if (!closes)
                        {
                            Assert.All(documents, document => Assert.True(document.IsDirty));
                            // Cancel must also release the close guard so a later Discard works.
                            pending = new Queue<string>(Enumerable.Repeat("Discard", documentCount));
                            window.Close();
                            while (failure == null && !closed) await Task.Delay(10, timeout.Token);
                            if (failure != null) throw failure;
                            Assert.Empty(pending);
                            Assert.All(lifetimes, lifetime => Assert.True(lifetime.IsCancellationRequested));
                        }
                    }
                    finally { timer.Stop(); }
                }
            };
            try { app.Run(); }
            catch (Exception ex) { failure ??= ex; }
            finally
            {
                if (previousSettings != null) File.WriteAllBytes(settings, previousSettings);
                else if (File.Exists(settings)) File.Delete(settings);
            }
            if (failure == null) completion.SetResult(); else completion.SetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
    }

    private static DocumentModel DirtyDocument(int index)
    {
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
        var entry = new AnimationEntry(new byte[308], 0, 0);
        package.Entries.Add(entry);
        var document = new DocumentModel(new ZbdDocument(Path.Combine(Path.GetTempPath(), $"close-fixture-{index}.zbd"),
            new(0, DateTime.MinValue), new(FormatFamily.Animation, 28, Recognition.Supported, "Close fixture"), ReadOnlyMemory<byte>.Empty)
        { Animations = package });
        document.AnimationEdits!.Apply(0, "Change reset delay", e => e.SetFloat(164, 1));
        Assert.True(document.IsDirty);
        return document;
    }
}
