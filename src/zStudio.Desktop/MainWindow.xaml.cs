using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Formats;
using zStudio.Rendering;

using zStudio.Core.Animation;

namespace zStudio.Desktop;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly CancellationToken shutdownToken;
    private CancellationTokenSource preview;
    private CancellationTokenSource? operation;
    private readonly SemaphoreSlim thumbnailGate = new(2);
    private readonly Queue<AssetItem> thumbnails = new();
    private readonly DispatcherTimer diskTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer audioTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private SceneViewport? scene;
    private AnimationEditor? animation;
    private bool pendingAnimationPlay;
    private bool allowClose, resolvingClose;
    /// <summary>The app is closing every document; a source project's edits cannot stay with another of its worlds.</summary>
    private bool closingAllDocuments;
    /// <summary>A source project whose unsaved edits the user chose to discard while closing; its other worlds close without asking again.</summary>
    /// <summary>A Discard the user chose for a project's edits, valid only while the workspace is as it was then (its revision).</summary>
    private (zStudio.Core.Sources.SourceWorkspace Workspace, long Revision)? discardApprovedWorkspace;
    private DecodedImage? decoded;
    private JsonObject? properties;
    private WaveFileReader? wave;
    private WasapiPlayer? player;
    private WaveInfo? waveInfo;
    private int? selectedNode;
    private int? isolatedNode;
    private bool updating, seeking, ready;
    private Point? panStart;
    private MouseButton? panButton;
    private Point scrollStart;
    private DocumentModel? shownDocument;
    private AssetRecord? shownAsset;
    private Task previewWork = Task.CompletedTask;
    private Guid previewId = Guid.NewGuid();

    public MainWindow()
    {
        shutdownToken = shutdown.Token;
        preview = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        InitializeComponent(); DataContext = ViewModel;
        WorldDifficulty.ItemsSource = MainViewModel.DifficultyChoices;
        ViewModel.PropertyChanged += DifficultyPreferenceChanged;
        ViewModel.ConfirmDiscardAsync = ConfirmDocumentCloseAsync;
        ViewModel.ResolveDraftsAsync = async () =>
        {
            if (animation?.ResolvePendingDrafts() == false || !await ResolvePropertiesDraftsAsync() || !await ResolveInspectionDraftsAsync()) return false;
            // An applied scene-card edit (or any other) rebuilds its world without the caller awaiting it; decisions wait for it.
            await SourceWorldsIdleAsync();
            return true;
        };
        ViewModel.CloseDecisionsStarting = ForgetStaleDiscardApproval;
        ViewModel.ValidateNavigationPublication = closesDocuments =>
        {
            RequireAutomationMutationAvailable();
            if (closesDocuments) RequireNoDrafts();
            else if (animation?.HasAutomationDrafts == true || HasInspectionDraft || HasAiPathDraft || parachuteDraft != null || HasSectionDraft())
                throw new zStudio.Automation.StudioCommandException("pending_drafts", "Resolve unfinished preview input before changing documents.");
        };
        ViewModel.ValidateReload = doc => { RequireAutomationMutationAvailable(); RequireNoDrafts(doc); };
        ViewModel.ReloadSourceWorld = ReloadSourceWorldAsync;
        var s = ViewModel.Settings;
        RestoreWindowSize(new(SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
        InitializeWorkspace();
        InitializeContentEditors();
        InitializeSourceEditor();
        ApplyTheme(s.Theme); UpdateRecent(); ready = true;
        PreviewKeyDown += Keyboard; PreviewKeyUp += ZoneToolKeyUp; PreviewKeyUp += (_, e) => PresentationKeyUp(e);
        // After the focused control: a dropdown, a field's restore, a stroke or a grip uses Escape first.
        KeyDown += (_, e) => { if (!e.Handled) SourceEscape(e); };
        PreviewKeyUp += (_, e) => flyCamera?.HandleKey(e, false);
        diskTimer.Tick += (_, _) => { ViewModel.CheckExternalChanges(); CheckAiPropertiesSnapshot(); }; diskTimer.Start();
        audioTimer.Tick += (_, _) => UpdateAudioPosition(); audioTimer.Start();
        Waveform.SeekRequested += SeekAudio;
    }
    internal void RestoreWindowSize(Size available)
    {
        // Small or scaled desktops can be below the preferred minimum. Relax it
        // before clamping saved bounds so startup cannot create an inverted range.
        MinWidth = Math.Min(MinWidth, available.Width);
        MinHeight = Math.Min(MinHeight, available.Height);
        Width = Math.Clamp(ViewModel.Settings.Width, MinWidth, available.Width);
        Height = Math.Clamp(ViewModel.Settings.Height, MinHeight, available.Height);
    }
    public async void OpenStartupPath(string path) => await RunUi(async () =>
    {
        // The GUI opens source projects only, as File → Open workspace… does (MCP open_root still opens other folders).
        if (Directory.Exists(path)) { await RequireSourceProjectAsync(path, CancellationToken.None); await ViewModel.OpenRootAsync(path); }
        else if (File.Exists(path)) throw new zStudio.Automation.StudioCommandException("unsupported", "zStudio opens source projects: give the project folder (with data and gamegen) instead of a file.");
        else throw new IOException("The supplied path does not exist: " + path);
        UpdateRecent();
    });
    private Task RunUi(Func<Task> work) => ReportUi(async () => { if (!await ResolveInspectionDraftsAsync() || animation?.ResolvePendingDrafts() == false) return; await work(); });
    /// <summary>Runs GUI work, reporting its failure; <see cref="RunUi"/> first resolves pending preview input.</summary>
    private async Task ReportUi(Func<Task> work)
    {
        try { await work(); }
        catch (zStudio.Automation.StudioCommandException ex) when (ex.Code == "context_changed") { }
        catch (OperationCanceledException) { ViewModel.Status = "Operation canceled"; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Report(ex); }
    }
    private void Report(Exception ex) => Report(ex.Message);
    /// <summary>A refused user action: listed like a failure in a source map (whose status bar shows no messages), otherwise the status.</summary>
    private void Notice(string message)
    {
        if (ViewModel.SelectedDocument?.SourceWorld == null) { ViewModel.Status = message; return; }
        // A refusal, not a failure: one warning however often it is repeated.
        ViewModel.Status = message;
        if (ViewModel.Problems.LastOrDefault() is not { } last || last.Message != message) ViewModel.AddProblem(message, "Warning");
        SetToolsVisible(true, problems: true);
    }
    /// <summary>The scene's controls: the toolbar, and in a source map the command bar's pickers and groups moved out of it.</summary>
    private void SetSceneControlsEnabled(bool enabled)
    {
        SceneToolbar.IsEnabled = enabled;
        foreach (var part in new FrameworkElement[] { SourcePickers, SourceCameraGroup, SourceDisplayGroup, SourceOverlayGroup, SourceShowGroup, SourceZoneOverlayItems }) part.IsEnabled = enabled;
    }
    /// <summary>
    /// A failure the user must see: in Problems (and Diagnostics in a map), not only in the status, which a source map's
    /// status bar does not show.
    /// </summary>
    private void Report(string message)
    {
        ViewModel.Status = message; ViewModel.AddProblem(message);
        // A source project's map picker has no Diagnostics pane (its own message line shows what failed there): showing it
        // would only change the project's preference.
        if (!ViewModel.IsSourceProject || ViewModel.SelectedDocument?.SourceWorld != null) SetToolsVisible(true, problems: true);
    }
    internal async Task OpenFilesAsync(IReadOnlyList<string> paths, bool forceRoot = false)
    {
        if (paths.Count == 0) return;
        await ViewModel.EnsureRootForFileAsync(paths[0], forceRoot);
        foreach (string path in paths)
        {
            var document = await ViewModel.OpenFileAsync(path);
            if (document == null) return;
            if (ViewModel.SelectedDocument != document)
                throw new zStudio.Automation.StudioCommandException("context_changed", "A newer selection superseded opening files.");
        }
    }
    private void UpdateRecent()
    {
        RecentMenu.Items.Clear();
        foreach (string path in ViewModel.Settings.RecentRoots)
        {
            // Folder names are literal text, not menu access-key labels.
            MenuItem item = new() { Header = new TextBlock { Text = path } };
            System.Windows.Automation.AutomationProperties.SetName(item,path);
            item.Click += async (_, _) => await RunUi(async () =>
            {
                // A folder that is (no longer) a source project leaves the list; one that is unavailable (an offline
                // share or a removed drive) stays for later.
                try { await RequireSourceProjectAsync(path, CancellationToken.None); }
                catch (zStudio.Automation.StudioCommandException ex) when (ex.Code != "context_changed")
                {
                    if (await Task.Run(() => Directory.Exists(path))) { ViewModel.Settings.RecentRoots.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)); UpdateRecent(); }
                    throw;
                }
                await ViewModel.OpenRootAsync(path); UpdateRecent();
            });
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = RecentMenu.HasItems;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        await RunUi(async () =>
        {
            if (!Directory.Exists(paths[0])) throw new zStudio.Automation.StudioCommandException("unsupported", "Drop a source project folder (one with data and gamegen); zStudio does not open single files.");
            await RequireSourceProjectAsync(paths[0], CancellationToken.None); await ViewModel.OpenRootAsync(paths[0]); UpdateRecent();
        });
    }
    private async void FileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var source = e.OriginalSource as DependencyObject; source != null; source = source is Visual ? VisualTreeHelper.GetParent(source) : null) if (source is ButtonBase) return;
        if (FileTree.SelectedItem is FolderNode { File: { } file }) { e.Handled = true; await OpenBrowserFile(file.Path); }
    }
    private async void FileTreeKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && e.OriginalSource is not Button && FileTree.SelectedItem is FolderNode { File: { } file }) { e.Handled = true; await OpenBrowserFile(file.Path); } }
    private Task OpenBrowserFile(string path) => RunUi(async () => { var doc = await ViewModel.OpenFileAsync(path); if (doc != null && ViewModel.SelectedDocument == doc) SelectNavigatorSection(1); });
    private async void SearchDoubleClick(object sender, MouseButtonEventArgs e) { if (SearchList.SelectedItem is SearchHit hit) await Navigate(hit); }
    private async void RelatedDoubleClick(object sender, MouseButtonEventArgs e) { if (RelatedList.SelectedItem is SearchHit hit) await Navigate(hit); }
    private Task Navigate(SearchHit hit) => RunUi(async () => { var doc = await ViewModel.OpenFileAsync(hit.File); if (doc == null) return; doc.Query = ""; doc.KindFilter = "All types"; doc.SelectedAsset = doc.Assets.FirstOrDefault(a => a.Record.Kind == hit.Kind && a.Index == hit.Index); if (doc.SelectedAsset is { } selected) await EnsureAssetPreviewAsync(doc, selected.Record); AssetGrid.ScrollIntoView(doc.SelectedAsset); });
    private async void DocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ready || e.PropertyName != nameof(MainViewModel.SelectedDocument)) return;
        var doc = ViewModel.SelectedDocument; if (doc == shownDocument) return;
        var resolving = ResolveInspectionDraftsAsync();
        if (!resolving.IsCompleted)
        {
            // Yes applied a source world's draft, which rebuilds the shown world: stay on it until that finishes, then go on
            // to the document asked for, unless another was chosen meanwhile.
            ViewModel.SelectedDocument = shownDocument;
            if (await resolving && ViewModel.SelectedDocument == shownDocument && doc is { IsDisposed: false } && ViewModel.Documents.Contains(doc)) ViewModel.SelectedDocument = doc;
            return;
        }
        if (!await resolving || animation?.ResolvePendingDrafts() == false) { ViewModel.SelectedDocument = shownDocument; return; }
        // A source world rebuilt after an edit keeps its old scene on screen until the new one is ready (see RetireScene).
        bool handoff = doc != null && ReferenceEquals(sceneHandoff, doc) && HasPublishedStaticScene;
        sceneHandoff = null;
        if (handoff) RetireScene(); else DropRetiringScene();
        var retired = handoff ? retiringScene : null;
        CancelPreview(); // Release old-document interactions before worker discovery can yield.
        if (!handoff)
        {
            foreach (UIElement element in new UIElement[] { ImageToolbar, ImageScroll, SceneToolbar, SceneHost, AnimationHost, AudioPanel, StructuredPanel, EventsTab }) element.Visibility = Visibility.Collapsed;
            EmptyPreview.Visibility = Visibility.Visible; EmptyPreview.Text = "Loading preview…";
        }
        shownDocument = doc; UpdateReloadingView(); Workspace.Visibility = doc == null ? Visibility.Collapsed : Visibility.Visible; Welcome.Visibility = doc == null && !ViewModel.IsSourceProject ? Visibility.Visible : Visibility.Collapsed;
        // Publish the whole transition before starting any work: selection events must not replace its task
        // with an asset-only preview while texture discovery is still pending.
        TaskCompletionSource transition = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingPreviewDocument = doc; pendingDocumentPreview = transition.Task; previewWork = transition.Task;
        long generation = ++documentPreviewGeneration;
        try { await PrepareDocumentPreviewAsync(doc, generation); transition.SetResult(); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            // An accepted document remains open even if its presentation fails. Report here for GUI-only
            // navigation too; command waiters observe the fault without retracting the document.
            if (error is not OperationCanceledException && generation == documentPreviewGeneration && ViewModel.SelectedDocument == doc)
                ViewModel.AddProblem("Preview unavailable: " + JsonData.ShownText(error.Message, 512), file: doc?.Path);
            transition.TrySetException(error);
        }
        finally
        {
            if (ReferenceEquals(pendingDocumentPreview, transition.Task)) { pendingDocumentPreview = null; pendingPreviewDocument = null; }
            // A transition that ended before its scene loaded (texture discovery failed) must not leave the frozen one shown.
            if (retired != null && ReferenceEquals(retiringScene, retired))
            {
                DropRetiringScene();
                SceneHost.Visibility = Visibility.Collapsed; EmptyPreview.Visibility = Visibility.Visible;
                UpdateReloadingView();
            }
            // Preparation can finish before ShowAssetCore (for example canceled texture discovery).
            // The whole current transition has settled now, including any same-document asset retries.
            RefreshPresentation(previewSettled: generation == documentPreviewGeneration && ViewModel.SelectedDocument == doc && shownDocument == doc);
            ArrangeWorkspace();
        }
    }
    private long documentPreviewGeneration;
    private DocumentModel? pendingPreviewDocument;
    private Task? pendingDocumentPreview;
    private long preparingAssetGeneration;
    private AssetRecord? preparingAsset;
    private bool preparingAssetSuperseded;
    internal Func<AssetResolver, string, CancellationToken, Task<string[]>> DiscoverTexturePacksAsync { get; set; } =
        static (resolver, path, token) => Task.Run(() => resolver.TexturePacks(path, token), token).WaitAsync(token);
    private async Task PrepareDocumentPreviewAsync(DocumentModel? doc, long generation)
    {
        if (doc != null)
            doc.SelectedAsset ??= doc.Assets.FirstOrDefault(a => a.Record.Content is zStudio.Core.Animation.AnimationEntry { RootName.Length: > 0 }) ?? doc.Assets.FirstOrDefault();
        string[]? packs = null;
        if (doc?.Document.Scene != null && ViewModel.Resolver is { } resolver)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(doc.Lifetime.Token, ViewModel.WorkspaceToken, shutdownToken, preview.Token);
            try { packs = await DiscoverTexturePacksAsync(resolver, doc.Path, cancellation.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (generation == documentPreviewGeneration && !doc.IsDisposed && ViewModel.SelectedDocument == doc && shownDocument == doc)
                    ViewModel.AddProblem("Texture variants: " + JsonData.ShownText(error.Message, 512), file: doc.Path);
            }
            if (cancellation.IsCancellationRequested || generation != documentPreviewGeneration || doc.IsDisposed || ViewModel.SelectedDocument != doc || shownDocument != doc) return;
        }
        if (generation != documentPreviewGeneration || ViewModel.SelectedDocument != doc || shownDocument != doc) return;
        updating = true;
        TexturePackCombo.ItemsSource = packs?.Select(p => new PackChoice(Path.GetFileName(p), p)).Prepend(new("Automatic texture variant", null)).ToArray();
        TexturePackCombo.DisplayMemberPath = nameof(PackChoice.Name); TexturePackCombo.SelectedIndex = 0; updating = false;
        if (doc != null)
        {
            AssetRecord? selected;
            bool superseded;
            do
            {
                selected = doc.SelectedAsset?.Record;
                preparingAssetGeneration = generation; preparingAsset = selected; preparingAssetSuperseded = false;
                try { await ShowAssetCore(doc, selected); }
                finally
                {
                    superseded = preparingAssetGeneration == generation && preparingAssetSuperseded;
                    if (preparingAssetGeneration == generation) { preparingAssetGeneration = 0; preparingAsset = null; }
                }
            }
            while (generation == documentPreviewGeneration && !doc.IsDisposed && ViewModel.SelectedDocument == doc && (superseded || !ReferenceEquals(selected, doc.SelectedAsset?.Record)));
        }
        else { CancelPreview(); shownAsset = null; ViewModel.Status = ViewModel.HasRoot ? $"{ViewModel.Files.Count:N0} files · choose a file to inspect" : "Ready"; }
    }
    private async void AssetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A binding can realize a programmatic selection after its preview has already started. That is presentation
        // of the same snapshot, not another navigation request; do not cancel/reload the preview it is catching up with.
        if (ready && ViewModel.SelectedDocument is { } doc && AssetGrid.SelectedItem is AssetItem item && doc.Assets.Contains(item) &&
            !IsShownAssetSnapshot(doc, item.Record))
        {
            var transition = pendingPreviewDocument == doc ? pendingDocumentPreview : null;
            Task? work = null;
            try { work = ShowAsset(doc, item.Record); await work; }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                // DocumentChanged owns errors from the whole-document transition. AssetGrid can receive a deferred
                // realization event for that same task, and an async-void handler must observe its fault rather than
                // rethrow it through WPF. Avoid duplicating the bounded diagnostic already emitted by that transition.
                if ((work == null || !ReferenceEquals(work, transition)) && !preview.IsCancellationRequested && !doc.IsDisposed && ViewModel.SelectedDocument == doc && shownDocument == doc)
                    ViewModel.AddProblem("Preview unavailable: " + JsonData.ShownText(error.Message, 512), file: doc.Path);
            }
        }
    }
    private bool IsShownAssetSnapshot(DocumentModel doc, AssetRecord asset) => doc == shownDocument &&
        (ReferenceEquals(shownAsset, asset) || shownAsset?.Id == asset.Id && animation != null);
    private Task EnsureAssetPreviewAsync(DocumentModel doc, AssetRecord asset)
    {
        // SelectedItem's binding may be deferred while the Assets page is unrealized/reparented. Navigation owns its
        // preview task directly instead of accidentally awaiting the previously selected asset's completed task.
        return IsShownAssetSnapshot(doc, asset) ? previewWork : ShowAsset(doc, asset);
    }
    private void CancelPreview()
    {
        ++staticRefreshGeneration; staticRefresh?.Cancel(); staticRefreshWork = null; publishedStaticOptions = null;
        previewId = Guid.NewGuid();
        flyRequest++; flyCamera?.End(); SynchronizeFly();
        ClearStaticPreviewProblems();
        DetachPickupEditor();
        selectedNode = null; isolatedNode = null; inspectedSceneSource = null;
        // A source rebuild replaces the map's document: what was hidden follows by identity (see RestoreHiddenIdentities), and
        // the outliner's open rows by their source paths (RestoreTreeLayout).
        CaptureTreeLayout(); CaptureHiddenIdentities(); hiddenRows.Clear();
        pendingAnimationPlay = false;
        EndImagePan();
        animation?.Dispose(); animation = null; AnimationHost.Content = null; DetachAnimationWorkspace();
        motion?.Dispose(); motion = null;
        preview.Cancel(); preview.Dispose(); preview = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken); scene?.Clear(); StopAudio(); decoded = null; TextureImage.Source = null;
        RefreshSceneTree();
    }
    private Task ShowAsset(DocumentModel doc, AssetRecord? asset)
    {
        if (pendingPreviewDocument == doc && pendingDocumentPreview is { IsCompleted: false } pending)
        {
            // Discovery only defers selection; once an asset starts loading, preserve immediate supersession.
            if (preparingAssetGeneration == documentPreviewGeneration && !ReferenceEquals(preparingAsset, asset))
            { preparingAssetSuperseded = true; preview.Cancel(); }
            return pending;
        }
        return previewWork = ShowAssetCore(doc, asset);
    }
    private async Task ShowAssetCore(DocumentModel doc, AssetRecord? asset)
    {
        if (shutdownToken.IsCancellationRequested) return;
        if (!ResolveInspectionDrafts() || animation?.ResolvePendingDrafts() == false) { doc.SelectedAsset = doc.Assets.FirstOrDefault(a => a.Record.Id == shownAsset?.Id); return; }
        asset = asset == null ? null : doc.Document.Assets.SingleOrDefault(a => a.Id == asset.Id);
        bool differentAsset = shownAsset?.Id != asset?.Id;
        SourceMapsButton.Visibility = doc.SourceWorld != null ? Visibility.Visible : Visibility.Collapsed;
        PreviewTitle.Text = asset?.Name ?? Path.GetFileName(doc.Path); PreviewSubtitle.Text = SourceWorldFile(doc) is { } worldFile ? Path.GetFileName(worldFile) : asset == null ? doc.Description : $"{asset.Kind} #{asset.Index} · {asset.Length:N0} bytes · source 0x{asset.Offset:X}";
        ViewModel.Status = asset == null ? doc.Description : $"{asset.Kind} #{asset.Index}: {asset.Name} · {Path.GetFileName(doc.Path)}";
        if (asset?.Kind == AssetKind.Motion && shownDocument == doc && motion != null && doc.ResourceMembers?.Members[asset.Index].Id == motion.MemberId)
        { shownAsset = asset; await motion.RefreshClipAsync(); await RefreshAssetInspectionAsync(doc, asset, preview.Token); return; }
        if (!differentAsset && asset != null && shownDocument == doc && HasPublishedStaticScene && ViewModel.Resolver != null)
        { shownAsset = asset; await RefreshStaticSceneAsync(doc, asset); return; }
        // Entering the animation viewer starts at Sequences. Consecutive animation
        // selections (including asynchronous replacement) retain the chosen page.
        if (asset?.Kind == AssetKind.Animation && shownAsset?.Kind != AssetKind.Animation) Layout.InspectorTab = 0;
        var previousMission = !differentAsset && asset?.Kind == AssetKind.World ? scene?.Mission : null;
        var previousView = previousMission == null ? null : scene?.CaptureView();
        int? previousSelection = selectedNode, previousIsolate = isolatedNode;
        var previousHidden = hiddenRows.ToArray();
        var previousPickup = selectedNode is int selected ? scene?.PickupAt(selected)?.Pickup?.Source : null;
        // A rebuilt source world's scene loads behind the retiring one (see RetireScene); anything else replaces it now.
        bool handoff = retiringScene != null && asset?.Kind == AssetKind.World && doc.Document.Scene != null && ViewModel.Resolver != null;
        var retired = handoff ? retiringScene : null;
        if (!handoff) DropRetiringScene();
        CancelPreview(); shownAsset = asset; var previewLifetime = preview.Token;
        using var loading = PreviewOperation.Link(previewLifetime);
        var token = loading.Token;
        if (!handoff)
        {
            foreach (UIElement element in new UIElement[] { ImageToolbar, ImageScroll, SceneToolbar, SceneHost, AnimationHost, AudioPanel, StructuredPanel, EventsTab }) element.Visibility = Visibility.Collapsed;
            EmptyPreview.Visibility = Visibility.Visible;
        }
        EmptyPreview.Text = "Loading preview…"; PreviewInfo.Text = ""; properties = null;
        try
        {
            var snapshot = doc.Document;
            await RefreshAssetInspectionAsync(doc, asset, token);
            var bytes = asset == null ? snapshot.Bytes : snapshot.Slice(asset.Offset, asset.Length);
            if (asset?.Kind == AssetKind.Texture)
            {
                var image = await Task.Run(() => TextureDecoder.Decode(snapshot, asset, token), token);
                token.ThrowIfCancellationRequested();
                decoded = image;
                ZoomSlider.Value = 1;
                ImageToolbar.Visibility = ImageScroll.Visibility = Visibility.Visible;
                UpdateImage();
                ImageScroll.ScrollToHorizontalOffset(0);
                ImageScroll.ScrollToVerticalOffset(0);
            }
            else if (asset?.Kind == AssetKind.Sound)
            {
                var info = await Task.Run(() => WaveDecoder.Read(bytes, token), token); var peaks = await Task.Run(() => WaveDecoder.Peaks(bytes, info), token); token.ThrowIfCancellationRequested();
                waveInfo = info; wave = new(new MemoryStream(bytes.ToArray(), false)); AudioPanel.Visibility = Visibility.Visible;
                AudioDetails.Text = $"{info.SampleRate:N0} Hz · {info.Channels} {(info.Channels == 1 ? "channel" : "channels")} · {info.BitsPerSample}-bit · encoding {info.Encoding}\n{info.Duration:F3} seconds · {info.DataLength:N0} audio bytes";
                Waveform.Set(peaks, info.Duration); AudioSeek.Maximum = info.Duration; AudioCues.ItemsSource = info.Cues.Select(c => new CueChoice(c.Id, c.SampleOffset, (double)c.SampleOffset / info.SampleRate)).ToArray(); UpdateAudioPosition();
            }
            else if (asset != null && (asset.Kind is AssetKind.Model or AssetKind.World || asset.Content is GameNode { Class: "object3d" or "lod" }) && doc.Document.Scene != null && ViewModel.Resolver != null)
            {
                int selectedLod = differentAsset ? 0 : Math.Max(0, LodCombo.SelectedIndex);
                int? root = SceneLods.PreviewRoot(doc.Document.Scene!, asset);
                int count = new SceneLods(doc.Document.Scene!).Count(asset.Kind == AssetKind.World ? null : root is int r ? [r] : []);
                updating = true; LodCombo.ItemsSource = SceneLods.Choices(count); LodCombo.SelectedIndex = Math.Min(selectedLod, count - 1); LodCombo.IsEnabled = count > 1; updating = false;
                SceneToolbar.Visibility = SceneHost.Visibility = Visibility.Visible;
                WorldHighlights.Visibility = asset.Kind == AssetKind.World ? Visibility.Visible : Visibility.Collapsed;
                SceneMenus.Visibility = doc.SourceWorld != null ? Visibility.Collapsed : Visibility.Visible; WorldDifficultyGroup.Visibility = asset.Kind == AssetKind.World && snapshot.Game != GameVariant.MechWarrior3 ? Visibility.Visible : Visibility.Collapsed; WorldMission.Visibility = Visibility.Collapsed;
                if (scene == null)
                {
                    scene = new(); scene.Information += s => { PreviewInfo.Text = s; PreviewInfo.ToolTip = s; }; scene.NodeSelected += InspectNode;
                    var restored = scene; scene.PreviewRestored += () => { if (ReferenceEquals(restored, scene)) ShowPreviewFooter(restored); }; ConfigureAiScene(scene); ConfigurePickupScene(scene);
                    // During a handoff the retiring scene stays in the host until this one has loaded.
                    if (retiringScene == null) SceneHost.Content = scene;
                    ConfigureFlyScene(scene);
                    scene.SetPresentationUiHidden(presentationUiHidden);
                }
                if (asset.Kind == AssetKind.World) await PopulateWorldMissionsAsync(doc, token);
                string? exactMission = ExactMissionFor(doc.Path, ViewModel.Resolver.SelectedMission(doc.Path));
                var mission = asset.Kind == AssetKind.World ? await MissionSceneLoader.LoadAsync(doc.Document, ViewModel.Resolver, token: token, difficulty: ViewModel.Difficulty, mission: exactMission, exactMission: exactMission != null) : null;
                // A source world's placement edits are carried back to its sources (see MoveSourcePlacementAsync).
                if (mission != null) { await doc.GetPickupEditsAsync(ViewModel.Resolver, token); await PopulateWorldMissionsAsync(doc, token); }
                if (mission != null) await PrepareSourceMapSelectionAsync(doc, mission.Scene, mission, token);
                ConfigureSourceMapPresentation(scene, doc, mission);
                await scene.ShowAsync(doc.Document, asset, ViewModel.Resolver, PreferredPack, LodCombo.SelectedIndex, token, BackdropEnabled.IsChecked == true, mission); token.ThrowIfCancellationRequested(); ApplySceneOptions(); RefreshZoneToolScene();
                PublishSourceMapParts(scene, doc);
                CompleteSceneHandoff();
                // A rebuilt source world keeps the camera of the build it replaced.
                if (pendingSourceView is { } rebuilt && rebuilt.Document == doc) { pendingSourceView = null; scene.RestoreView(rebuilt.View); }
                var shownOptions = ReadStaticSceneOptions();
                publishedStaticOptions = shownOptions with { Difficulty = mission is { Layout.DifficultyApplies: true } ? mission.Layout.Difficulty : ViewModel.Difficulty, Mission = mission?.Layout.MissionArchive ?? shownOptions.Mission };
                ApplyTerrainBrush(scene);
                if (mission != null) ViewModel.AdoptMissionFallback(doc.Path, mission.Layout);
                if (asset.Kind == AssetKind.World) AttachPickupEditor(doc);
                if (mission != null)
                {
                    if (previousMission != null && previousView != null)
                    {
                        isolatedNode = !isolatedGroup && previousIsolate is int oldIsolate && mission.RemapNodeFrom(previousMission, oldIsolate) is >= 0 and int mappedIsolate ? mappedIsolate : null;
                        ApplyIsolation(); RefreshRowEyes();
                        RemapHidden(previousHidden, old => mission.RemapNodeFrom(previousMission, old) is >= 0 and int mappedHidden ? mappedHidden : null);
                        scene.RestoreView(previousView);
                        selectedNode = RemapPickupSelection(previousPickup, mission) ?? (previousSelection is int oldSelection && mission.RemapNodeFrom(previousMission, oldSelection) is >= 0 and int mapped ? mapped : null);
                        if (selectedNode is int node) InspectNode(node);
                    }
                }
                ShowStaticPreviewProblems(doc, asset);
                if (mission != null) WorldDifficulty.ToolTip = mission.Layout.Description;
                if (mission != null && mission.Layout.DifficultyApplies && mission.Layout.MissionArchive == null && mission.Layout.Difficulty != ViewModel.Difficulty) await RefreshWorldDifficultyAsync();
            }
            else if (asset?.Kind == AssetKind.Motion && doc.ResourceMembers != null && ViewModel.Resolver != null)
            {
                var editor = new MotionEditor(doc, doc.ResourceMembers.Members[asset.Index].Id, ViewModel.Resolver, previewLifetime); motion = editor;
                editor.SceneChanged += () => { AttachInspection(editor.Viewport); editor.Viewport.NodeSelected -= InspectNode; editor.Viewport.NodeSelected += InspectNode; RefreshSceneTree(); };
                editor.StatusChanged += text => { if (!previewLifetime.IsCancellationRequested) ViewModel.Status = text; };
                // Hide UI applies before the editor shows its first frame.
                ApplyRetainedPresentation();
                AnimationHost.Content = editor; AnimationHost.Visibility = Visibility.Visible;
                await editor.InitializeAsync(token);
            }
            else if (asset?.Kind == AssetKind.Animation && doc.Document.Animations != null && ViewModel.Resolver != null)
            {
                var editor = new AnimationEditor(doc, asset.Index, ViewModel.Resolver, previewLifetime, ViewModel); animation = editor; editor.MissionRequested = path => SelectMissionAsync(path, false, CancellationToken.None); editor.ExactMission = PendingExactMission;
                if (pendingAnimationPlay) { pendingAnimationPlay = false; editor.TogglePlayback(); }
                editor.StatusChanged += text => { if (!previewLifetime.IsCancellationRequested) ViewModel.Status = text; };
                editor.InspectionChanged += (json, data) => { if (previewLifetime.IsCancellationRequested) return; properties = json; var source = editor.SourceByteSelection(); RawText.Text = source.Scope + "\n\n" + (source.Offset >= 0 ? Hex(source.Bytes.Span,source.Offset) : "") + (source.Length > 4096 ? "\n… first 4,096 source bytes shown." : ""); };
                ApplyRetainedPresentation();
                AnimationHost.Content = editor; AttachAnimationWorkspace(editor);
                AnimationHost.Visibility = Visibility.Visible;
                await editor.InitializeAsync();
            }
            else
            {
                StructuredPanel.Visibility = Visibility.Visible;
                if (asset?.Content is AnimationEntry entry) { EventsTab.Visibility = Visibility.Visible; EventsTab.Header = entry.AllSequences.Sum(s => (long)s.Events.Count) > 200 ? "Events (first 200)" : "Events"; EventGrid.ItemsSource = EventRows(entry); StructuredPanel.SelectedIndex = 2; }
                else if (doc.Document.Scripts != null && asset?.Content is ScriptContent) StructuredPanel.SelectedItem = scriptTab;
                else StructuredPanel.SelectedIndex = asset?.Content is ScriptContent ? 1 : 0;
            }
            token.ThrowIfCancellationRequested(); EmptyPreview.Visibility = Visibility.Collapsed;
            RefreshSceneTree();
        }
        catch (OperationCanceledException)
        {
            // MCP shutdown cancels the request, not the selection retained by the
            // GUI. Rebuild that selection outside its canceled operation scope.
            // Navigation, document disposal and shutdown cancel the lifetime and
            // must never resurrect an obsolete preview.
            if (loading.IsCancellationRequested && !previewLifetime.IsCancellationRequested && !doc.IsDisposed && shownDocument == doc &&
                ViewModel.SelectedDocument == doc && shownAsset?.Id == asset?.Id)
            {
                using var recovery = PreviewOperation.Begin(CancellationToken.None);
                // This is the current transition's own recovery, not a new selection waiting for that transition.
                await ShowAssetCore(doc, asset);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { if (!token.IsCancellationRequested) { EmptyPreview.Text = "Preview unavailable: " + ex.Message; Report(ex); } }
        finally
        {
            // A completed handoff has dropped the retiring scene; one still here means the rebuilt world could not be shown
            // (failure or cancellation), so the frozen old world must not stay on screen.
            if (retired != null && ReferenceEquals(retiringScene, retired))
            {
                DropRetiringScene();
                SceneHost.Visibility = Visibility.Collapsed; EmptyPreview.Visibility = Visibility.Visible;
                if (!EmptyPreview.Text.StartsWith("Preview unavailable", StringComparison.Ordinal)) EmptyPreview.Text = "Loading preview…";
            }
            UpdateReloadingView();
            RefreshPresentation(previewSettled: !token.IsCancellationRequested && shownDocument == doc && shownAsset?.Id == asset?.Id);
            ArrangeWorkspace();
        }
    }
    private async Task RefreshAssetInspectionAsync(DocumentModel doc, AssetRecord? asset, CancellationToken token)
    {
        long revision = doc.Revision; var snapshot = doc.Document;
        var json = asset == null ? (JsonObject)snapshot.Metadata.DeepClone() : await LoadAssetPropertiesAsync(snapshot, asset, token);
        var root = asset?.Kind == AssetKind.Zrd && doc.ResourceMembers is { } resources
            ? await ResourceTreeAsync(doc, resources.Members[asset.Index].Id, token) : null;
        token.ThrowIfCancellationRequested();
        if (doc.IsDisposed || doc.Revision != revision || shownDocument != doc || shownAsset?.Id != asset?.Id)
            throw new OperationCanceledException("Asset inspection was superseded.");
        SetProperties(json);
        if (root != null)
        {
            var item = new ResourceTreeItem(root, 0, null, resourceExpansion);
            Guid? retained = selectedResourceNode; CentralTree.ItemsSource = new[] { item };
            if (retained is Guid id) SelectResourceNode(item, id);
        }
        else CentralTree.ItemsSource = asset?.Kind == AssetKind.Zrd && json["tree"] is JsonNode hierarchy ? ZrdTree(doc, asset, hierarchy) : json.Select(p => new InspectorNode(p.Key, p.Value)).ToArray();
        ContentText.Text = asset?.Content is ScriptContent script ? script.PreviewText + (script.TextTruncated ? "\n… first 65,536 characters; export for full text." : "") : LimitedJson(json);
        RefreshScriptGrid(doc, asset);
        var original = asset == null ? null : doc.OriginalAsset(asset);
        var sourceBytes = asset == null ? doc.Document.Bytes : original == null ? ReadOnlyMemory<byte>.Empty : doc.Document.Slice(original.Offset, original.Length);
        RawText.Text = Hex(sourceBytes.Span[..Math.Min(sourceBytes.Length, 4096)], original?.Offset ?? 0) + (sourceBytes.Length > 4096 ? "\n… first 4,096 source bytes shown." : "");
        PreviewSubtitle.Text = SourceWorldFile(doc) is { } worldFile ? Path.GetFileName(worldFile) : asset == null ? doc.Description : $"{asset.Kind} #{asset.Index} · {asset.Length:N0} bytes" + (original == null ? "" : $" · source 0x{original.Offset:X}");
        var related = asset == null ? null : FindRelated(asset, doc);
        RelatedList.ItemsSource = related?.Items;
        RelatedLimit.Visibility = related?.Truncated == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private MainViewModel.RelatedMatches FindRelated(AssetRecord asset, DocumentModel doc)
    {
        bool deep = false;
        var result = ViewModel.Related(Names(), doc.Path);
        return deep ? result with { Truncated = true } : result;
        IEnumerable<string> Names()
        {
            yield return asset.Name;
            foreach (string name in Strings(asset.Metadata, 0, () => deep = true)) yield return name;
            if (asset.Content is ScriptContent script) foreach (var instruction in script.Instructions)
            {
                yield return ""; // Empty instructions still consume the passive inspection work budget.
                foreach (string argument in instruction) yield return argument;
            }
            if (asset.Content is GameModel model && doc.Document.Scene is { } sceneData)
                foreach (var polygon in model.Polygons)
                {
                    int mat = polygon.MaterialIndex;
                    int tex = mat >= 0 && mat < sceneData.Materials.Count ? sceneData.Materials[mat].Int("texture_index", -1) : -1;
                    yield return tex >= 0 && tex < sceneData.Textures.Count ? sceneData.Textures[tex].Text("name") : "";
                }
        }
    }
    private static IEnumerable<string> Strings(JsonNode? node, int depth, Action truncated)
    {
        // Count containers and non-text leaves too: a large metadata tree with no names must not bypass the work bound.
        yield return node is JsonValue value && value.TryGetValue<string>(out string? text) && text.Length is > 1 and < 128 ? text : "";
        if (node is not (JsonObject or JsonArray)) yield break;
        if (depth >= 32) { truncated(); yield break; }
        if (node is JsonObject obj) { foreach (var p in obj) foreach (string s in Strings(p.Value, depth + 1, truncated)) yield return s; }
        else if (node is JsonArray array) { foreach (var p in array) foreach (string s in Strings(p, depth + 1, truncated)) yield return s; }
    }
    private void SetProperties(JsonObject value) { properties = value; }
    private static string LimitedJson(JsonObject value) { string text = value.ToJsonString(JsonData.Options); return text.Length > 500_000 ? text[..500_000] + "\n… export JSON for the complete document." : text; }
    private static string Hex(ReadOnlySpan<byte> bytes, long offset)
    {
        StringBuilder result = new();
        for (int i = 0; i < bytes.Length; i += 16)
        {
            var row = bytes.Slice(i, Math.Min(16, bytes.Length - i)); result.Append($"{offset + i:X8}  ");
            foreach (byte value in row) result.Append(value.ToString("X2")).Append(' ');
            result.AppendLine();
        }
        return result.ToString();
    }
    private async void AssetLoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is not AssetItem { Record.Kind: AssetKind.Texture } item || item.ThumbnailRequested || ViewModel.SelectedDocument is not { } doc || !doc.Assets.Contains(item)) return;
        item.ThumbnailRequested = true; var token = doc.Lifetime.Token; var thumbnailSnapshot = doc.Document; var thumbnailRecord = item.Record; long thumbnailRevision = doc.Revision;
        try
        {
            await thumbnailGate.WaitAsync(token);
            try { var bitmap = await Task.Run(() => MakeBitmap(TextureDecoder.Decode(thumbnailSnapshot, thumbnailRecord, token), 0, 48), token); token.ThrowIfCancellationRequested(); if (doc.Revision != thumbnailRevision) return; item.Thumbnail = bitmap; thumbnails.Enqueue(item); while (thumbnails.Count > 256) { var old = thumbnails.Dequeue(); old.Thumbnail = null; old.ThumbnailRequested = false; } }
            finally { thumbnailGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException) { }
    }
    private static BitmapSource MakeBitmap(DecodedImage image, int channel, int maximum = int.MaxValue)
    {
        double scale = Math.Min(1, (double)maximum / Math.Max(image.Width, image.Height)); int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale)); byte[] bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int source = ((int)(y / scale) * image.Width + (int)(x / scale)) * 4, p = (y * w + x) * 4; if (channel >= 2) { int c = channel switch { 2 => 3, 3 => 0, 4 => 1, _ => 2 }; bgra[p] = bgra[p + 1] = bgra[p + 2] = image.Rgba[source + c]; } else { bgra[p] = image.Rgba[source + 2]; bgra[p + 1] = image.Rgba[source + 1]; bgra[p + 2] = image.Rgba[source]; } bgra[p + 3] = channel == 0 ? image.Rgba[source + 3] : (byte)255; }
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4); bitmap.Freeze(); return bitmap;
    }
    private void UpdateImage() { if (decoded == null) return; TextureImage.Source = MakeBitmap(decoded, ChannelCombo.SelectedIndex); RenderOptions.SetBitmapScalingMode(TextureImage, SmoothImage.IsChecked == true ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor); SetImageSize(); }
    private void SetImageSize()
    {
        if (decoded == null) return;
        var dpi = VisualTreeHelper.GetDpi(TextureImage);
        // WPF sizes are device-independent units; zoom is measured in physical pixels.
        TextureImage.Width = decoded.Width * ZoomSlider.Value / dpi.DpiScaleX;
        TextureImage.Height = decoded.Height * ZoomSlider.Value / dpi.DpiScaleY;
        TextureZoomLabel.Text = $"{ZoomSlider.Value:P0}"; PreviewInfo.Text = $"{decoded.Width} × {decoded.Height} · {ZoomSlider.Value:P0}";
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (!ready) return;
        EndImagePan();
        // Wait until WPF has propagated the new DPI to the image's visual subtree.
        Dispatcher.InvokeAsync(SetImageSize, DispatcherPriority.Loaded);
    }
    private void FitImage()
    {
        if (decoded == null || ImageScroll.ViewportWidth <= 0 || ImageScroll.ViewportHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(TextureImage);
        ZoomSlider.Value = Math.Clamp(Math.Min(ImageScroll.ViewportWidth * dpi.DpiScaleX / decoded.Width,
            ImageScroll.ViewportHeight * dpi.DpiScaleY / decoded.Height), ZoomSlider.Minimum, ZoomSlider.Maximum);
    }
    private void FitImageClick(object sender, RoutedEventArgs e) => FitImage();
    private void ActualImageClick(object sender, RoutedEventArgs e) => ZoomSlider.Value = 1;
    private void ImageOptionsChanged(object sender, RoutedEventArgs e) { if (ready) UpdateImage(); }
    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (ready) SetImageSize(); }
    private void ImageWheel(object sender, MouseWheelEventArgs e) { ZoomSlider.Value *= e.Delta > 0 ? 1.2 : 1 / 1.2; e.Handled = true; }
    private void ImagePanStart(object sender, MouseButtonEventArgs e)
    {
        if (decoded == null || panStart != null || e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        if (!ImageSurface.CaptureMouse()) return;
        panStart = e.GetPosition(ImageScroll);
        panButton = e.ChangedButton;
        scrollStart = new(ImageScroll.HorizontalOffset, ImageScroll.VerticalOffset);
        ImageSurface.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }
    private void ImagePanEnd(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != panButton) return;
        EndImagePan();
        e.Handled = true;
    }
    private void ImagePanCanceled(object sender, RoutedEventArgs e) => EndImagePan();
    private void EndImagePan()
    {
        panStart = null;
        panButton = null;
        ImageSurface.Cursor = null;
        if (ImageSurface.IsMouseCaptured) ImageSurface.ReleaseMouseCapture();
    }
    private void ImagePanMove(object sender, MouseEventArgs e)
    {
        if (panStart is not Point start) return;
        bool pressed = panButton == MouseButton.Left ? e.LeftButton == MouseButtonState.Pressed : e.MiddleButton == MouseButtonState.Pressed;
        if (!pressed) { EndImagePan(); return; }
        var current = e.GetPosition(ImageScroll);
        ImageScroll.ScrollToHorizontalOffset(scrollStart.X + start.X - current.X);
        ImageScroll.ScrollToVerticalOffset(scrollStart.Y + start.Y - current.Y);
    }
    private void ImagePixelMove(object sender, MouseEventArgs e)
    {
        if (decoded == null) return;
        Point p = e.GetPosition(TextureImage);
        if (p.X < 0 || p.Y < 0 || p.X >= TextureImage.ActualWidth || p.Y >= TextureImage.ActualHeight) return;
        int x = (int)(p.X * decoded.Width / TextureImage.ActualWidth), y = (int)(p.Y * decoded.Height / TextureImage.ActualHeight);
        int i = (y * decoded.Width + x) * 4;
        PreviewInfo.Text = $"{decoded.Width} × {decoded.Height} · {ZoomSlider.Value:P0} · ({x}, {y}) RGBA {decoded.Rgba[i]}, {decoded.Rgba[i + 1]}, {decoded.Rgba[i + 2]}, {decoded.Rgba[i + 3]}";
    }
    private void PaletteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDocument is not { } doc || shownAsset?.Content is not TextureInfo t) return;
        StackPanel content = new() { Margin = new(18) }; content.Children.Add(new TextBlock { Text = $"{t.Width} × {t.Height} · flags 0x{t.Flags:X2}\n{t.PaletteCount} palette entries · page {t.PalettePage}\n" + shownAsset.Metadata.ToJsonString(JsonData.Options), TextWrapping = TextWrapping.Wrap });
        if (t.PaletteOffset >= 0) { WrapPanel colors = new(); var bytes = doc.Document.Bytes.Span.Slice(t.PaletteOffset, t.PaletteLength); for (int i = 0; i < bytes.Length / 2; i++) { ushort value = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]); byte r = (byte)(value >> 11), g = (byte)((value >> 5) & 63), b = (byte)(value & 31); colors.Children.Add(new Border { Width = 24, Height = 24, Background = new SolidColorBrush(Color.FromRgb((byte)((r << 3) | (r >> 2)), (byte)((g << 2) | (g >> 4)), (byte)((b << 3) | (b >> 2)))), ToolTip = $"Index {i} · RGB565 0x{value:X4}" }); } content.Children.Add(colors); }
        new Window { Owner = this, Title = "Texture palette and header", Width = 480, Height = 650, Content = new ScrollViewer { Content = content }, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
    }
    private string? PreferredPack => (TexturePackCombo.SelectedItem as PackChoice)?.Path;
    private async void SceneSourceChanged(object sender, RoutedEventArgs e) { if (ready && !updating && SceneHost.Visibility == Visibility.Visible && ViewModel.SelectedDocument is { } doc && shownAsset != null) await ShowAsset(doc, shownAsset); }
    private void ApplySceneOptions() { scene?.SetFaceOrientation(FaceOrientation.IsChecked == true && !ZoneToolEngaged); scene?.SetWireframe(Wireframe.IsChecked == true); scene?.SetTextured(TexturesEnabled.IsChecked == true); scene?.SetBounds(BoundsEnabled.IsChecked == true); ApplyWorldHighlight(); ApplyAiOptions(); }
    private void SceneOptionsChanged(object sender, RoutedEventArgs e) { if (ready) ApplySceneOptions(); }
    private void FrameSceneClick(object sender, RoutedEventArgs e) => RunCameraNavigation("frameAsset");
    private void IsolateClick(object sender, RoutedEventArgs e)
    {
        if (selectedNode is not int node) { ViewModel.Status = "Select a node in the scene or scene tree first"; return; }
        isolatedNode = scene?.PickupAt(node)?.Root ?? node;
        ApplyIsolation(); CurrentInspectionCard?.Refresh(); RefreshRowEyes();
    }
    private void ShowAllClick(object sender, RoutedEventArgs e) { isolatedNode = null; scene?.Isolate(null); hiddenRows.Clear(); pendingHidden = null; ApplyHiddenRows(); if (sourceChromeActive) ShowAllSections(); CurrentInspectionCard?.Refresh(); }
    private void InspectNode(int index)
    {
        var scene = motion?.Viewport ?? this.scene;
        inspectedSceneSource = null;
        if (shownDocument is { SourceWorld: not null } sourceDocument && SourceSelection(sourceDocument, scene?.PreviewScene) is { } projection)
        {
            index = projection.Owner(index);
            if (index < 0)
            {
                RevealSceneNode(index);
                selectedNode = index; scene?.SelectFramingNode(index);
                if (sceneTree?.Selected is { Synthetic: true } row) { inspectedSceneSource = row; SetProperties(SceneTreeProperties(row)); }
                ViewModel.Status = "Selected source reference group";
                return;
            }
        }
        if ((scene?.PreviewScene ?? ViewModel.SelectedDocument?.Document.Scene) is not { } data || index < 0 || index >= data.Nodes.Count) return;
        RevealSceneNode(index);
        var actor = scene?.PickupAt(index);
        if (actor != null) index = actor.Root;
        scene?.SelectPickup(actor?.Root);
        selectedNode = index; scene?.SelectFramingNode(index); var properties = JsonData.PreviewObject(data.Nodes[index].Metadata);
        if (scene?.Mission is { } mission)
        {
            properties["preview_instance"] = data.Nodes[index].Name;
            properties["preview_world_position"] = JsonData.Vector(actor != null ? scene!.PickupPosition(actor.Root) : SceneViewport.WorldTransform(data, index).Translation);
            properties["source_node_index"] = mission.SourceNodes[index];
            properties["preview_layout"] = mission.Layout.Description;
        }
        SetProperties(properties); ViewModel.Status = $"Selected node #{index}: {data.Nodes[index].Name}";
    }
    private static EventRow[] EventRows(AnimationEntry entry)
    {
        return entry.AllSequences.SelectMany(s => s.Events.Select(e => (Sequence: s, Event: e))).Take(200)
            .Select(p => { var data = p.Event.ToPreviewJson(); return new EventRow(p.Sequence.Name, p.Event.Name, data.Text("start_mode"), data.Text("start_threshold"), data); }).ToArray();
    }
    private void EventSelected(object sender, SelectionChangedEventArgs e) { if (EventGrid.SelectedItem is EventRow row) SetProperties(row.Data); }
    private void PlayAudioClick(object sender, RoutedEventArgs e)
    {
        try { if (player?.PlaybackState == PlaybackState.Playing) player.Pause(); else PlaySound(); } catch (Exception ex) { Report(ex); player?.Dispose(); player = null; }
    }
    private void PlaySound()
    {
        if (wave == null) return;
        if (player == null) { player = new WasapiPlayerBuilder().Build(); player.Init(wave); }
        if (wave.Position >= wave.Length) wave.Position = 0;
        player.Play();
    }
    private void StopAudioClick(object sender, RoutedEventArgs e) { player?.Stop(); if (wave != null) wave.Position = 0; UpdateAudioPosition(); }
    private void StopAudio() { player?.Dispose(); player = null; wave?.Dispose(); wave = null; waveInfo = null; }
    private void UpdateAudioPosition() { bool playing = player?.PlaybackState == PlaybackState.Playing; AudioPlayButton.Content = playing ? "Pause" : "Play"; System.Windows.Automation.AutomationProperties.SetName(AudioPlayButton, playing ? "Pause audio" : "Play audio"); if (wave == null) return; seeking = true; AudioSeek.Value = wave.CurrentTime.TotalSeconds; AudioTime.Text = $@"{wave.CurrentTime:mm\:ss\.fff} / {wave.TotalTime:mm\:ss\.fff}"; seeking = false; }
    private void SeekAudio(double seconds) { if (wave != null) wave.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, wave.TotalTime.TotalSeconds)); }
    private void AudioSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (ready && !seeking) SeekAudio(e.NewValue); }
    private void CueDoubleClick(object sender, MouseButtonEventArgs e) { if (AudioCues.SelectedItem is CueChoice cue) SeekAudio(cue.Seconds); }
    private async void ExportSelectedClick(object sender, RoutedEventArgs e) => await Export(false, false);
    private async void ExportJsonClick(object sender, RoutedEventArgs e) => await Export(false, true);
    private Task Export(bool all, bool json) => RunUi(async () =>
    {
        if (ViewModel.SelectedDocument is not { } doc || ViewModel.Resolver is not { } resolver) return;
        if (operation != null) { ViewModel.Status = "Wait for the current operation or cancel it first"; return; }
        AssetRecord[] assets = all ? doc.Document.Assets.ToArray() : AssetGrid.SelectedItems.Cast<AssetItem>().Select(a => a.Record).ToArray();
        if (assets.Length == 0) { ViewModel.Status = "Select an asset to export"; return; }
        OpenFolderDialog dialog = new() { Title = "Choose an export destination outside the source folder" }; if (dialog.ShowDialog(this) != true) return;
        var result = await ExportAssetsAsync(doc, assets, dialog.FolderName, json, PreferredPack, Math.Max(0,LodCombo.SelectedIndex), CancellationToken.None);
        MessageBox.Show(this, ViewModel.Status + (result.Errors.Count > 0 ? $"\n{result.Errors.Count} failures; see Problems and export-report.json." : ""), "Export complete", MessageBoxButton.OK, result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    });
    private async void ValidateClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        if (ViewModel.SelectedDocument is not { } doc) return;
        var notes = await ValidateDocumentSourceAsync(doc, CancellationToken.None);
        // A source map's status bar shows no messages: the outcome is shown, and notes open Diagnostics.
        if (doc.SourceWorld != null)
        {
            if (notes.Count > 0) SetToolsVisible(true, problems: true);
            MessageBox.Show(this, notes.Count == 0 ? "The map's source files on disk build without notes."
                : $"The map's source files on disk build with {notes.Count} note{(notes.Count == 1 ? "" : "s")}, listed in Diagnostics.", "Validate source files", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    });
    private async void ReloadClick(object sender, RoutedEventArgs e) => await RunUi(() => ViewModel.ReloadAsync());
    /// <summary>Whether an export, validation, reconstruction or planning operation runs that Escape can still cancel.</summary>
    internal bool OperationRunning => operation is { IsCancellationRequested: false };
    /// <summary>Escape: cancels the running export, validation, reconstruction or planning operation.</summary>
    internal void CancelRunningOperation()
    {
        if (operation is not { IsCancellationRequested: false }) return;
        operation.Cancel(); ViewModel.Status = "Canceling current operation…";
    }
    private async void CloseFileClick(object sender, RoutedEventArgs e) { e.Handled = true; if ((sender as Button)?.Tag is DocumentModel doc) await RunUi(() => ViewModel.CloseAsync(doc)); }
    private async void CloseCurrentClick(object sender, RoutedEventArgs e) { if (ViewModel.SelectedDocument is { } doc) await RunUi(() => ViewModel.CloseAsync(doc)); }
    private void ExitClick(object sender, RoutedEventArgs e) => Close();
    private void ThemeClick(object sender, RoutedEventArgs e) => ApplyTheme(((MenuItem)sender).Header.ToString()!);
    // .NET 10 still marks runtime Fluent theme switching as experimental.
#pragma warning disable WPF0001
    private void ApplyTheme(string theme)
    {
        var offsets = WorkspaceScrollers(Shell).Select(s => (Owner: s.TemplatedParent is ItemsControl items ? (DependencyObject)items : s, s.HorizontalOffset, s.VerticalOffset)).ToArray();
        Application.Current.ThemeMode = theme switch { "Dark" => ThemeMode.Dark, "Light" => ThemeMode.Light, _ => ThemeMode.System }; ViewModel.Settings.Theme = theme;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            UpdateLayout();
            foreach (var (owner,horizontal,vertical) in offsets)
            {
                var scroller = owner as ScrollViewer ?? WorkspaceScrollers(owner).FirstOrDefault();
                scroller?.ScrollToHorizontalOffset(horizontal); scroller?.ScrollToVerticalOffset(vertical);
            }
        });
    }
    private static IEnumerable<ScrollViewer> WorkspaceScrollers(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root,i);
            if (child is ScrollViewer scroller) yield return scroller;
            foreach (var descendant in WorkspaceScrollers(child)) yield return descendant;
        }
    }
#pragma warning restore WPF0001
    private void ResetLayoutClick(object sender, RoutedEventArgs e)
    {
        using var lease = BeginLayoutChange();
        detachingWorkspace = true;
        try
        {
            var defaults = new WorkspaceLayout(); ViewModel.Settings.Workspace = defaults;
            CurrentInspectionCard?.SetPanelHeight(defaults.InspectionPanelHeight);
            previousPreset = defaults.Preset;
            navigatorTemporary = inspectorTemporary = toolsMaximized = false;
            SelectNavigatorSection(defaults.BrowserTab);
            InspectorTabs.SelectedIndex = animation != null ? defaults.InspectorTab : -1;
            // Static viewers have no Dispatch page; use their normal Related fallback.
            ToolTabs.SelectedIndex = animation != null ? defaults.ToolTab : 4;
            ApplyMetrics(); animation?.ResetLayout(); ArrangeWorkspace();
        }
        finally { detachingWorkspace = false; }
        SaveWorkspacePreferences();
    }
    private void HelpClick(object sender, RoutedEventArgs e) => MessageBox.Show(this, "Workspace: Initialize unpacks the retail ZBD files once into a source project (a folder with data and gamegen); File > Open workspace (Ctrl+O) opens one. Choose a map to edit it; the back arrow returns to the maps. Ctrl+Z/Ctrl+Y undo and redo, Ctrl+S saves the project's changed files, Ctrl+W closes the map. Export builds game files from the sources into a folder outside the project.\n\nView: middle drag orbits; Shift+middle drag pans; Ctrl+middle drag or the wheel zooms toward the pointer (orthographic views scale about it); Ctrl+Shift+middle drag dollies. Numpad 1/3/7 selects front/right/top (Ctrl for the opposite views), Numpad 5 toggles perspective/orthographic, Home frames everything and Numpad decimal frames the selection; the Perspective chip offers the same. F10 hides or shows the UI (the view alone, for footage; clicks then select nothing) and F11 toggles fullscreen. Flycam: WASD moves along the view, Space/C moves up/down, the mouse looks around and the wheel changes speed; Escape returns to normal controls.\n\nObjects: click one in the view or the World tree to select it; its Properties pane edits it. Move and Rotate show handles; Apply accepts the changes as one undo step, Revert discards them. Escape or a click on empty space clears the selection. Copy… asks for the copy's name and places it beside the original (in place when it shows nothing with a width).\n\nPolygon zones: the Zones group's Edit zones tool (P paints, E erases) adds a zone to, or removes it from, every polygon you drag over in the map's own models; Ctrl inverts it for a stroke, Shift+click picks a polygon's zone, [ and ] step through the map's zones, and Escape leaves the tool.\n\nWorld tree: Zones, AI Networks and Animations (definitions) list the map's zones, AI networks and animation definitions; selecting one opens its card in the Properties pane (a zone's label and a network's settings are edited there). Zones and AI networks are shown one by one with their eyes (all start hidden); the eyes on the Zones, AI Networks, AI Vehicles and Pickups rows show or hide everything below them. Middle-clicking a world object's eye shows only it. Up/Down move through the rows (a row that can be selected becomes the selection), Left/Right reach a row's buttons, Space or Enter opens or closes a row or presses the button.", "Controls and formats");
    private void AboutClick(object sender, RoutedEventArgs e)
    {
        var assembly = typeof(MainWindow).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version?.ToString(3) ?? "unknown";
        MessageBox.Show(this, $"zStudio {version}\nWindows asset explorer\n\nTexture packs · ZAR archives · prepared scripts · animations · GameZ worlds\n\nBuilt with .NET, WPF, Helix Toolkit, CommunityToolkit.Mvvm, and NAudio. See THIRD-PARTY-NOTICES.md for licenses. Game assets are not included.", "About");
    }
    private void Keyboard(object sender, KeyEventArgs e)
    {
        if (PresentationKey(e)) return;
        if (flyCamera?.HandleKey(e, true) == true) return;
        if (ZoneToolKey(e)) return;
        if (ParachuteKeyboard(e)) return;
        if (AiPathKeyboard(e)) return;
        if (CameraKeyboard(e)) return;
        if ((e.Key == Key.Enter || e.SystemKey == Key.Enter) && System.Windows.Input.Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; OpenCurrentProperties(); return; }
        // F2 on a selected zone or network row: its label or name.
        if (e.Key == Key.F2 && System.Windows.Input.Keyboard.Modifiers == ModifierKeys.None && DocumentSceneTree.IsKeyboardFocusWithin && SectionSelection is { Section: ZoneEntry or NetworkEntry })
        { e.Handled = true; sectionCard?.FocusFirst(); return; }
        if (e.Key == Key.Escape && (scene?.CancelPickupDrag() == true || scene?.CancelTerrainStroke() == true)) { e.Handled = true; return; }
        if (e.Key == Key.Space && System.Windows.Input.Keyboard.Modifiers == ModifierKeys.None &&
            (shownAsset?.Kind == AssetKind.Animation && ViewModel.SelectedDocument?.Document.Animations != null || motion != null) &&
            AnimationSpaceTarget(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            if (!e.IsRepeat) { if (motion != null) motion.TogglePlayback(); else if (animation != null) animation.TogglePlayback(); else pendingAnimationPlay = !pendingAnimationPlay; }
            return;
        }
        bool ctrl = KeyboardModifiers(); if (ctrl && e.Key == Key.O) OpenWorkspaceClick(this, e); else if (ctrl && e.Key == Key.S) SaveCurrentClick(this, e); else if (ctrl && e.Key == Key.Z && e.OriginalSource is not TextBoxBase) UndoDocumentClick(this, e); else if (ctrl && e.Key == Key.Y && e.OriginalSource is not TextBoxBase) RedoDocumentClick(this, e); else if (ctrl && e.Key == Key.W) CloseCurrentClick(this, e); else if (e.Key == Key.F5) ReloadClick(this, e); else if (e.Key == Key.Escape && OperationRunning && !zoneTool.Applying) CancelRunningOperation(); else return; e.Handled = true;
    }
    private bool AnimationSpaceTarget(DependencyObject? source)
    {
        for (var current = source; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            // Let text entry and controls with their own Space action keep normal keyboard behavior.
            if (current is TextBoxBase or PasswordBox or ComboBox or ButtonBase or MenuItem or Slider or Thumb) return false;
            if (current == AssetGrid || current == AnimationHost || current == ProgramHost) return true;
        }
        return false;
    }
    private static bool KeyboardModifiers() => (System.Windows.Input.Keyboard.Modifiers & ModifierKeys.Control) != 0;
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        flyRequest++; flyCamera?.End();
        scene?.CancelNavigation(); animation?.Viewport.CancelNavigation(); motion?.Viewport.CancelNavigation();
        if (resolvingClose)
        {
            e.Cancel = true;
            // Closing again while the exit waits for a rebuild cancels it (its edit is taken back); the exit then continues.
            if (sourceWorkspaceBusy) { CancelSourceBuilds(); ViewModel.Status = "Cancelling the rebuild so the application can close…"; }
            return;
        }
        if (automationCloseRequested && (ViewModel.Documents.Any(d => d.IsDirty) || HasInspectionDraft || HasAiPathDraft || parachuteDraft != null || HasSectionDraft() || animation?.HasAutomationDrafts == true || propertiesWindow?.HasPendingDrafts == true || scene?.IsPickupDragging == true || scene?.IsZonePainting == true || zoneTool.Applying))
        {
            automationCloseRequested = false; e.Cancel = true;
            ViewModel.Status = "Close canceled: the workspace has new edits or unfinished input. Resolve them before closing.";
            return;
        }
        System.Windows.Input.Keyboard.ClearFocus();
        // Released polygon zone strokes apply first (a second or two): closing would discard them.
        if (!allowClose && (scene?.IsZonePainting == true || zoneTool.Applying)) { e.Cancel = true; ShowZoneRefusal("Polygon zone strokes are still applying; close again when they are done."); return; }
        if (!allowClose && (ViewModel.Documents.Any(d => d.IsDirty) || HasInspectionDraft || HasAiPathDraft || parachuteDraft != null || HasSectionDraft() || animation?.HasPendingDrafts == true || propertiesWindow?.HasPendingDrafts == true))
        {
            e.Cancel = true; resolvingClose = true;
            try
            {
                // Discard can finish synchronously. Leave the
                // original WPF Closing event before showing prompts or calling Close again.
                await Dispatcher.Yield(DispatcherPriority.Normal);
                if (animation?.ResolvePendingDrafts() == false || !await ResolvePropertiesDraftsAsync() || !await ResolveInspectionDraftsAsync()) return;
                if (sourceWorkspaceBusy) ViewModel.Status = "Waiting for the source world to finish rebuilding before closing…";
                // A save replacing the project's files finishes (or is undone) first; its documents are then decided as saved or not.
                else if (!sourceSaveWork.IsCompleted) ViewModel.Status = "Waiting for the source project's save to finish before closing…";
                await SourceWorldsIdleAsync();
                ForgetStaleDiscardApproval();
                closingAllDocuments = true;
                foreach (var document in ViewModel.Documents.ToArray())
                {
                    document.ApprovedCloseRevision = null;
                    if (!await ConfirmDocumentCloseAsync(document)) return;
                }
                // Each answer covers the state its question showed: a document changed since (work that finished while a later
                // document was asked about), or opened and edited meanwhile, keeps the application open.
                if (ViewModel.Documents.FirstOrDefault(d => d.IsDirty && d.ApprovedCloseRevision != d.Revision) is { } changed)
                {
                    ViewModel.Status = "Close canceled: " + changed.Title.TrimEnd(' ', '*') + " changed after its close decision; its edits were kept.";
                    return;
                }
                allowClose = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Report(ex); return; }
            finally { resolvingClose = closingAllDocuments = false; discardApprovedWorkspace = null; }
            Close();
            return;
        }
        // Closing is now accepted. Cancel the retained preview/editor lifetimes
        // before canceling MCP requests: request-only cancellation deliberately
        // recovers an open preview, and awaiting that recovery would stall exit.
        // A canceled close never reaches this irreversible lifetime boundary.
        allowClose = true; automationCloseRequested = false; IsEnabled = false;
        shutdown.Cancel(); operation?.Cancel();
        // An interrupted save being resolved stops between two files, so the files and its journal agree when the process ends;
        // a source project save stops before it replaces a file, or finishes (or is undone).
        if (!sourceRecoveryWork.IsCompleted || !sourceSaveWork.IsCompleted)
        {
            e.Cancel = true; resolvingClose = true;
            await Dispatcher.Yield(DispatcherPriority.Normal);
            try { await Task.WhenAll(sourceRecoveryWork, sourceSaveWork).ContinueWith(static _ => { }, TaskScheduler.Default); }
            finally { resolvingClose = false; }
            Close(); return;
        }
        if (mcpHost != null || mcpStopTask is { IsCompleted: false } || automationOperations.Values.Any(j => !j.Work.IsCompleted))
        {
            e.Cancel = true; resolvingClose = true;
            await Dispatcher.Yield(DispatcherPriority.Normal);
            try { await StopMcpAsync(); }
            finally { resolvingClose = false; }
            Close(); return;
        }
        propertiesWindow?.CloseResolved();
        flyCamera?.Dispose();
        ObserveDocumentCommands(null);
        operation?.Cancel(); preview.Cancel(); ViewModel.PropertyChanged -= DifficultyPreferenceChanged; diskTimer.Stop(); audioTimer.Stop(); StopAudio(); animation?.Dispose(); motion?.Dispose(); scene?.Dispose(); retiringScene?.Dispose(); retiringScene = null; ViewModel.Dispose(); shutdown.Dispose();
        var s = ViewModel.Settings;
        if (ordinaryWindow is { } ordinary) { s.Width = ordinary.SavedWidth; s.Height = ordinary.SavedHeight; }
        else if (WindowState == WindowState.Normal) { s.Width = ActualWidth; s.Height = ActualHeight; }
        SaveWorkspacePreferences();
        try { s.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Settings cannot prevent shutdown. */ }
    }
    private sealed record PackChoice(string Name, string? Path);
    private sealed record EventRow(string Sequence, string Type, string Mode, string Threshold, JsonObject Data);
    private sealed record CueChoice(uint Id, uint Sample, double Seconds) { public override string ToString() => $"Cue {Id} · sample {Sample:N0} · {Seconds:F3} s"; }
}
