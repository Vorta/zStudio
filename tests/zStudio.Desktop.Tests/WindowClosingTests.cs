using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using zStudio.Core;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Desktop.Tests;

public sealed class WindowClosingTests
{
    [Fact]
    public async Task DiscardAndCancelUseTheRealWindowCloseLifecycle()
    {
        // WPF permits one Application per process and requires its own STA dispatcher.
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string phase = "window close lifecycle";
        Thread thread = new(() =>
        {
            string settings = zStudio.Desktop.StudioSettings.SettingsPath;
            byte[]? previousSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
            // Match the Fluent theme used by the production application.
#pragma warning disable WPF0001
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
#pragma warning restore WPF0001
            // These are the only application resources needed by the shell. No game data,
            // rendering or production unhandled-error dialog is involved in this fixture.
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/zStudio.Desktop;component/PreviewStyles.xaml", UriKind.Relative) });
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
                    Volatile.Write(ref phase, "Properties, inspection and mission workspace");
                    await PropertiesWindowChecks.Run();
                    await PresentationMcpChecks.Run(app);
                    await McpWorkspaceChecks.Run(app);
                    await SceneInspectionInputChecks.Run();
                    await SceneInspectionMcpChecks.Run();
                    await InspectionResultMcpChecks.Run();
                    await SceneTreeMcpChecks.Run();
                    await SceneSelectionMcpChecks.Run();
                    await Mw3MissionMcpChecks.Run();
                    await MissionSelectionChecks.Run();
                    await MissionSelectionChecks.RunSupersedingRefresh();
                    await MissionSelectionChecks.RunUnavailableSelection();
                    await MissionSelectionChecks.RunRetryWithoutPreview();
                    await MissionSelectionChecks.RunWithoutReaders();
                    await MissionSelectionChecks.RunDeletedReaderAtStartup();
                    Volatile.Write(ref phase, "source project, recovery and model workflows");
                    await SourceProjectMcpChecks.Run();
                    await LinkedSourceProjectChecks.Run();
                    await ExportSafetyMcpChecks.Run();
                    await SourceWorldMcpChecks.Run();
                    await SourceDefinitionPagingChecks.Run();
                    await SourceWorkspaceStateBoundsChecks.Run();
                    await FileResultBoundsMcpChecks.Run();
                    await SourceEditingMcpChecks.Run();
                    await ParachuteMcpChecks.Run();
                    await SourceAnimationMcpChecks.Run();
                    await SourceRecoveryMcpChecks.Run();
                    await SourceProjectReadMcpChecks.Run();
                    await SourceDecisionDialogChecks.Run();
                    await SourceSaveMcpChecks.Run();
                    await SourceModelDialogChecks.Run();
                    await ScriptScanRound6McpChecks.Run();
                    await SourceTakeBackMcpChecks.Run();
                    await SourcePreviewChangeMcpChecks.Run();
                    Volatile.Write(ref phase, "terrain, map zones and world editing");
                    await SourceTerrainMcpChecks.Run();
                    await SourceZoneMcpChecks.Run();
                    await SourceTerrainReferenceCapacityChecks.Run();
                    await SourceRound30PlanningChecks.Run();
                    await WorldCompareMcpChecks.Run();
                    await WorldCompareBoundsChecks.Run();
                    await WorldCompareBudgetChecks.Run();
                    await LookupMcpChecks.Run();
                    await SourceObjectPlanMcpChecks.Run();
                    await SourceWorldTreeChecks.Run();
                    Volatile.Write(ref phase, "navigation, lifecycle and content editing");
                    await ResponsiveNavigatorChecks.Run(app);
                    await WorldHighlightMcpChecks.Run();
                    await AiNetworkMcpChecks.Run();
                    await BlenderNavigationChecks.Run();
                    await ShutdownMcpChecks.Run();
                    await PropertiesMcpChecks.Run();
                    await OperationPublicationChecks.Run();
                    await BlenderCheckoutLifetimeChecks.Run();
                    await AssetInspectionMcpChecks.Run();
                    await MotionMcpChecks.Run();
                    await MotionDeferredSelectionChecks.Run();
                    Volatile.Write(ref phase, "animation, drafts and document preparation");
                    await AnimationFogChecks.Run();
                    await WorldAnimationAttachmentChecks.Run();
                    await WorldAnimationTimelineChecks.Run();
                    await AnimationTextureQueueChecks.Run();
                    await NavigationMcpChecks.Run();
                    await GuiNavigationChecks.Run();
                    await DraftResolutionMcpChecks.Run();
                    await AnimationMcpCancellationChecks.Run();
                    await ReloadChecks.Run();
                    await DocumentPreparationChecks.Run();
                    Volatile.Write(ref phase, "compiled asset inventory");
                    await CompiledInventoryChecks.Run();
                }
                catch (Exception ex) { failure ??= ex; }
                finally
                {
                    Volatile.Write(ref phase, "workspace shutdown and settings restoration");
                    foreach (Window window in app.Windows.Cast<Window>().ToArray())
                    {
                        if (window is MainWindow main)
                            foreach (var doc in main.ViewModel.Documents) if (doc.SourceWorld?.Workspace is { IsDirty: true } workspace) workspace.Discard();
                        window.Close();
                    }
                    app.Shutdown();
                    foreach (var fixture in fixtures) fixture.Dispose();
                }

                async Task Check(string[] answers, int documentCount, bool closes, bool repeatClose = false)
                {
                    var window = new MainWindow { Left = -12000, ShowInTaskbar = false };
                    // Reproduce small CI/remote desktops even on a large local display.
                    window.RestoreWindowSize(new Size(1024, 600));
                    Assert.InRange(window.Width, 1, 1024);
                    Assert.InRange(window.Height, 1, 600);
                    Assert.True(window.MinWidth <= window.Width);
                    Assert.True(window.MinHeight <= window.Height);
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
                            var buttons = ((Grid)dialog.Content).Children.OfType<WrapPanel>().Single();
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
        // All workspace fixtures share one STA/Application and run serially.
        // CI runs test modules serially so Core allocation tests do not compete
        // with this dispatcher/rendering chain. Individual operations retain
        // their deadlines; this outer limit remains a hang guard.
        try { await completion.Task.WaitAsync(TimeSpan.FromMinutes(6), TestContext.Current.CancellationToken); }
        catch (TimeoutException ex) when (!completion.Task.IsCompleted)
        { throw new TimeoutException($"Desktop workspace checks timed out during {Volatile.Read(ref phase)}.", ex); }
    }

    private static readonly List<zStudio.Tests.SourceWorldFixture> fixtures = [];

    /// <summary>A source world of its own project with one unsaved edit (Undo on its workspace makes it clean): each one asks about its changes when it closes.</summary>
    internal static DocumentModel DirtyDocument(int index)
    {
        zStudio.Tests.SourceWorldFixture fixture = new(); fixtures.Add(fixture);
        SourceWorkspace workspace = new(fixture.Project);
        workspace.Apply("Change the world script", [("gamegen/m1.gs", System.Text.Encoding.Latin1.GetBytes(File.ReadAllText(fixture.Path("gamegen/m1.gs")) + "# edited\r\n"))]);
        var document = new DocumentModel(new AssetDocument(fixture.Path("gamegen/m1.gs"), new(0, DateTime.MinValue),
            new(FormatFamily.Unknown, null, Recognition.Supported, "Close fixture"), ReadOnlyMemory<byte>.Empty), new SourceWorldSession(workspace, "m1"), null);
        Assert.True(document.IsDirty);
        return document;
    }
}
