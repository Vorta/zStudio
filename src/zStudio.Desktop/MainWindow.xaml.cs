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
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    private CancellationTokenSource preview = new();
    private CancellationTokenSource? operation;
    private CancellationTokenSource? difficultyRefresh;
    private readonly SemaphoreSlim thumbnailGate = new(2);
    private readonly Queue<AssetItem> thumbnails = new();
    private readonly DispatcherTimer diskTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer audioTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private SceneViewport? scene;
    private AnimationEditor? animation;
    private bool pendingAnimationPlay;
    private bool allowClose, resolvingClose;
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

    public MainWindow()
    {
        InitializeComponent(); DataContext = ViewModel;
        BackupOnSave.IsChecked = ViewModel.Settings.CreateBackupOnSave;
        WorldDifficulty.ItemsSource = MainViewModel.DifficultyChoices;
        ViewModel.PropertyChanged += DifficultyPreferenceChanged;
        ViewModel.ConfirmDiscardAsync = ConfirmDocumentCloseAsync;
        var s = ViewModel.Settings;
        RestoreWindowSize(new(SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
        InitializeWorkspace();
        ApplyTheme(s.Theme); UpdateRecent(); ready = true;
        PreviewKeyDown += Keyboard;
        diskTimer.Tick += (_, _) => ViewModel.CheckExternalChanges(); diskTimer.Start();
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
        if (Directory.Exists(path)) await ViewModel.OpenRootAsync(path);
        else if (File.Exists(path)) { await ViewModel.OpenRootAsync(Path.GetDirectoryName(path)!); await ViewModel.OpenFileAsync(path); }
        else throw new IOException("The supplied path does not exist: " + path);
        UpdateRecent();
    });
    private async Task RunUi(Func<Task> work)
    {
        try { if (animation?.ResolvePendingDrafts() == false) return; await work(); }
        catch (OperationCanceledException) { ViewModel.Status = "Operation canceled"; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Report(ex); }
    }
    private void Report(Exception ex) { ViewModel.Status = ex.Message; ViewModel.AddProblem(ex.Message); Layout.ToolsVisible = true; ToolTabs.SelectedIndex = 2; ArrangeWorkspace(); }
    private async void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new() { Title = "Choose the root of the ZBD folder", InitialDirectory = Directory.Exists(ViewModel.Settings.LastRoot) ? ViewModel.Settings.LastRoot : "" };
        if (dialog.ShowDialog(this) == true) await RunUi(async () => { await ViewModel.OpenRootAsync(dialog.FolderName); UpdateRecent(); });
    }
    private async void OpenFileClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new() { Filter = "Recoil assets|*.zbd;*.zrd;*.wav|All files|*.*" };
        if (dialog.ShowDialog(this) == true) await RunUi(async () => { if (!ViewModel.HasRoot) await ViewModel.OpenRootAsync(Path.GetDirectoryName(dialog.FileName)!); await ViewModel.OpenFileAsync(dialog.FileName); });
    }
    private void UpdateRecent()
    {
        RecentMenu.Items.Clear();
        foreach (string path in ViewModel.Settings.RecentRoots)
        {
            // Folder names are literal text, not menu access-key labels.
            MenuItem item = new() { Header = new TextBlock { Text = path } };
            System.Windows.Automation.AutomationProperties.SetName(item,path);
            item.Click += async (_, _) => await RunUi(async () => { await ViewModel.OpenRootAsync(path); UpdateRecent(); });
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = RecentMenu.HasItems;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        await RunUi(async () => { if (Directory.Exists(paths[0])) { await ViewModel.OpenRootAsync(paths[0]); UpdateRecent(); } else { if (!ViewModel.HasRoot) await ViewModel.OpenRootAsync(Path.GetDirectoryName(paths[0])!); foreach (string path in paths) await ViewModel.OpenFileAsync(path); } });
    }
    private async void FileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var source = e.OriginalSource as DependencyObject; source != null; source = source is Visual ? VisualTreeHelper.GetParent(source) : null) if (source is ButtonBase) return;
        if (FileTree.SelectedItem is FolderNode { File: { } file }) { e.Handled = true; await OpenBrowserFile(file.Path); }
    }
    private async void FileTreeKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && e.OriginalSource is not Button && FileTree.SelectedItem is FolderNode { File: { } file }) { e.Handled = true; await OpenBrowserFile(file.Path); } }
    private Task OpenBrowserFile(string path) => RunUi(async () => { var doc = await ViewModel.OpenFileAsync(path); if (doc != null && ViewModel.SelectedDocument == doc) NavigationTabs.SelectedItem = AssetsTab; });
    private async void SearchDoubleClick(object sender, MouseButtonEventArgs e) { if (SearchList.SelectedItem is SearchHit hit) await Navigate(hit); }
    private async void RelatedDoubleClick(object sender, MouseButtonEventArgs e) { if (RelatedList.SelectedItem is SearchHit hit) await Navigate(hit); }
    private Task Navigate(SearchHit hit) => RunUi(async () => { var doc = await ViewModel.OpenFileAsync(hit.File); if (doc == null) return; doc.Query = ""; doc.KindFilter = "All types"; doc.SelectedAsset = doc.Assets.FirstOrDefault(a => a.Record.Kind == hit.Kind && a.Index == hit.Index); AssetGrid.ScrollIntoView(doc.SelectedAsset); });
    private async void DocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ready || e.PropertyName != nameof(MainViewModel.SelectedDocument)) return;
        var doc = ViewModel.SelectedDocument; if (doc == shownDocument) return;
        if (animation?.ResolvePendingDrafts() == false) { ViewModel.SelectedDocument = shownDocument; return; }
        shownDocument = doc; Workspace.Visibility = doc == null ? Visibility.Collapsed : Visibility.Visible; Welcome.Visibility = doc == null ? Visibility.Visible : Visibility.Collapsed;
        updating = true;
        TexturePackCombo.ItemsSource = doc?.Document.Scene != null ? ViewModel.Resolver?.TexturePacks(doc.Path).Select(p => new PackChoice(Path.GetFileName(p), p)).Prepend(new("Automatic texture variant", null)).ToArray() : null;
        TexturePackCombo.DisplayMemberPath = nameof(PackChoice.Name); TexturePackCombo.SelectedIndex = 0; updating = false;
        if (doc != null) { doc.SelectedAsset ??= doc.Assets.FirstOrDefault(a => a.Record.Content is Recoil.Zbd.Core.Animation.AnimationEntry { RootName.Length: > 0 }) ?? doc.Assets.FirstOrDefault(); await ShowAsset(doc, doc.SelectedAsset?.Record); }
        else { CancelPreview(); shownAsset = null; ViewModel.Status = ViewModel.HasRoot ? $"{ViewModel.Files.Count:N0} files · choose a file to inspect" : "Ready"; }
    }
    private async void AssetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready && ViewModel.SelectedDocument is { } doc && AssetGrid.SelectedItem is AssetItem item && doc.Assets.Contains(item) && !(doc == shownDocument && shownAsset?.Id == item.Record.Id && animation != null)) await ShowAsset(doc, item.Record);
    }
    private void CancelPreview()
    {
        ClearStaticPreviewProblems();
        DetachPickupEditor();
        selectedNode = null; isolatedNode = null; inspectedSceneSource = null;
        difficultyRefresh?.Cancel();
        pendingAnimationPlay = false;
        EndImagePan();
        animation?.Dispose(); animation = null; AnimationHost.Content = null; DetachAnimationWorkspace();
        preview.Cancel(); preview.Dispose(); preview = new(); scene?.Clear(); StopAudio(); decoded = null; TextureImage.Source = null;
    }
    private async Task ShowAsset(DocumentModel doc, AssetRecord? asset)
    {
        if (animation?.ResolvePendingDrafts() == false) { doc.SelectedAsset = doc.Assets.FirstOrDefault(a => a.Record.Id == shownAsset?.Id); return; }
        bool differentAsset = shownAsset?.Id != asset?.Id;
        // Entering the animation viewer starts at Sequences. Consecutive animation
        // selections (including asynchronous replacement) retain the chosen page.
        if (asset?.Kind == AssetKind.Animation && shownAsset?.Kind != AssetKind.Animation) Layout.InspectorTab = 0;
        var previousMission = !differentAsset && asset?.Kind == AssetKind.World ? scene?.Mission : null;
        var previousView = previousMission == null ? null : scene?.CaptureView();
        int? previousSelection = selectedNode, previousIsolate = isolatedNode;
        var previousPickup = selectedNode is int selected ? scene?.PickupAt(selected)?.Pickup?.Source : null;
        CancelPreview(); shownAsset = asset; var token = preview.Token;
        foreach (UIElement element in new UIElement[] { ImageToolbar, ImageScroll, SceneToolbar, SceneHost, AnimationHost, AudioPanel, StructuredPanel, EventsTab }) element.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Visible; EmptyPreview.Text = "Loading preview…"; PreviewInfo.Text = ""; properties = null;
        PreviewTitle.Text = asset?.Name ?? Path.GetFileName(doc.Path); PreviewSubtitle.Text = asset == null ? doc.Description : $"{asset.Kind} #{asset.Index} · {asset.Length:N0} bytes · source 0x{asset.Offset:X}";
        ViewModel.Status = asset == null ? doc.Description : $"{asset.Kind} #{asset.Index}: {asset.Name} · {Path.GetFileName(doc.Path)}";
        try
        {
            properties = asset == null ? (JsonObject)doc.Document.Metadata.DeepClone() : await Task.Run(() => ExportService.AssetJson(doc.Document, asset, token), token);
            token.ThrowIfCancellationRequested(); SetProperties(properties); CentralTree.ItemsSource = asset?.Kind == AssetKind.Zrd && properties["tree"] is JsonNode hierarchy ? ZrdTree(doc,asset,hierarchy) : properties.Select(p => new InspectorNode(p.Key,p.Value)).ToArray();
            ContentText.Text = asset?.Content is ScriptContent script ? script.Text : LimitedJson(properties);
            var bytes = asset == null ? doc.Document.Bytes : doc.Document.Slice(asset.Offset, asset.Length);
            RawText.Text = Hex(bytes.Span[..Math.Min(bytes.Length, 4096)], asset?.Offset ?? 0) + (bytes.Length > 4096 ? "\n… first 4,096 bytes shown. Export for complete data." : "");
            RelatedList.ItemsSource = asset == null ? null : FindRelated(asset, doc).ToArray();
            if (asset?.Kind == AssetKind.Texture)
            {
                var image = await Task.Run(() => TextureDecoder.Decode(doc.Document, asset, token), token);
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
                var info = await Task.Run(() => WaveDecoder.Read(bytes), token); var peaks = await Task.Run(() => WaveDecoder.Peaks(bytes, info), token); token.ThrowIfCancellationRequested();
                waveInfo = info; wave = new(new MemoryStream(bytes.ToArray(), false)); AudioPanel.Visibility = Visibility.Visible;
                AudioDetails.Text = $"{info.SampleRate:N0} Hz · {info.Channels} {(info.Channels == 1 ? "channel" : "channels")} · {info.BitsPerSample}-bit · encoding {info.Encoding}\n{info.Duration:F3} seconds · {info.DataLength:N0} audio bytes";
                Waveform.Set(peaks, info.Duration); AudioSeek.Maximum = info.Duration; AudioCues.ItemsSource = info.Cues.Select(c => new CueChoice(c.Id, c.SampleOffset, (double)c.SampleOffset / info.SampleRate)).ToArray(); UpdateAudioPosition();
            }
            else if (asset != null && (asset.Kind is AssetKind.Model or AssetKind.World || asset.Content is GameNode { Class: "object3d" or "lod" }) && doc.Document.Scene != null && ViewModel.Resolver != null)
            {
                int selectedLod = differentAsset ? 0 : Math.Max(0, LodCombo.SelectedIndex);
                int? root = SceneLods.PreviewRoot(doc.Document.Scene, asset);
                int count = new SceneLods(doc.Document.Scene).Count(asset.Kind == AssetKind.World ? null : root is int r ? [r] : []);
                updating = true; LodCombo.ItemsSource = SceneLods.Choices(count); LodCombo.SelectedIndex = Math.Min(selectedLod, count - 1); LodCombo.IsEnabled = count > 1; updating = false;
                SceneToolbar.Visibility = SceneHost.Visibility = Visibility.Visible;
                WorldDifficultyGroup.Visibility = asset.Kind == AssetKind.World ? Visibility.Visible : Visibility.Collapsed;
                if (scene == null) { scene = new(); scene.Information += s => { PreviewInfo.Text = s; PreviewInfo.ToolTip = s; }; scene.NodeSelected += InspectNode; ConfigurePickupScene(scene); SceneHost.Content = scene; }
                var mission = asset.Kind == AssetKind.World ? await MissionSceneLoader.LoadAsync(doc.Document, ViewModel.Resolver, token: token, difficulty: ViewModel.Difficulty) : null;
                if (mission != null) await doc.GetPickupEditsAsync(ViewModel.Resolver, token);
                await scene.ShowAsync(doc.Document, asset, ViewModel.Resolver, PreferredPack, LodCombo.SelectedIndex, token, BackdropEnabled.IsChecked == true, mission); token.ThrowIfCancellationRequested(); ApplySceneOptions();
                if (mission != null)
                {
                    AttachPickupEditor(doc);
                    if (previousMission != null && previousView != null)
                    {
                        isolatedNode = previousIsolate is int oldIsolate && mission.RemapNodeFrom(previousMission, oldIsolate) is >= 0 and int mappedIsolate ? mappedIsolate : null;
                        if (isolatedNode != null) scene.Isolate(isolatedNode);
                        scene.RestoreView(previousView);
                        selectedNode = RemapPickupSelection(previousPickup, mission) ?? (previousSelection is int oldSelection && mission.RemapNodeFrom(previousMission, oldSelection) is >= 0 and int mapped ? mapped : null);
                        if (selectedNode is int node) InspectNode(node);
                    }
                }
                ShowStaticPreviewProblems(doc, asset);
                if (mission != null) WorldDifficulty.ToolTip = mission.Layout.Description;
                if (mission != null && mission.Layout.Difficulty != ViewModel.Difficulty) await RefreshWorldDifficultyAsync();
            }
            else if (asset?.Kind == AssetKind.Animation && doc.AnimationEdits != null && ViewModel.Resolver != null)
            {
                var editor = new AnimationEditor(doc, asset.Index, ViewModel.Resolver, token, ViewModel); animation = editor;
                if (pendingAnimationPlay) { pendingAnimationPlay = false; editor.TogglePlayback(); }
                editor.StatusChanged += text => { if (!token.IsCancellationRequested) ViewModel.Status = text; };
                editor.InspectionChanged += (json, data) => { if (token.IsCancellationRequested) return; properties = json; var source = editor.SourceByteSelection(); RawText.Text = source.Scope + "\n\n" + (source.Offset >= 0 ? Hex(source.Bytes.Span,source.Offset) : "") + (source.Length > 4096 ? "\n… first 4,096 source bytes shown." : ""); };
                editor.SaveRequested += async () => { await SaveAnimationAsync(doc); };
                AnimationHost.Content = editor; AttachAnimationWorkspace(editor);
                AnimationHost.Visibility = Visibility.Visible;
                await editor.InitializeAsync();
            }
            else
            {
                StructuredPanel.Visibility = Visibility.Visible;
                if (asset?.Kind == AssetKind.Animation) { EventsTab.Visibility = Visibility.Visible; EventGrid.ItemsSource = EventRows(asset.Metadata); StructuredPanel.SelectedIndex = 2; }
                else StructuredPanel.SelectedIndex = asset?.Content is ScriptContent ? 1 : 0;
            }
            token.ThrowIfCancellationRequested(); EmptyPreview.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { if (!token.IsCancellationRequested) { EmptyPreview.Text = "Preview unavailable: " + ex.Message; Report(ex); } }
    }
    private IEnumerable<SearchHit> FindRelated(AssetRecord asset, DocumentModel doc)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { asset.Name };
        foreach (string name in Strings(asset.Metadata)) names.Add(name);
        if (asset.Content is ScriptContent script) foreach (string argument in script.Instructions.SelectMany(i => i)) names.Add(argument);
        if (asset.Content is GameModel model && doc.Document.Scene is { } sceneData)
            foreach (int mat in model.Polygons.Select(p => p.MaterialIndex).Distinct()) if (mat >= 0 && mat < sceneData.Materials.Count) { int tex = sceneData.Materials[mat].Int("texture_index", -1); if (tex >= 0 && tex < sceneData.Textures.Count) names.Add(sceneData.Textures[tex].Text("name")); }
        return names.Where(n => n.Length > 1).SelectMany(n => ViewModel.Related(n, doc.Path)).Distinct().Take(300);
    }
    private static IEnumerable<string> Strings(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out string? text)) { if (text.Length is > 1 and < 128) yield return text; }
        else if (node is JsonObject obj) { foreach (var p in obj) foreach (string s in Strings(p.Value)) yield return s; }
        else if (node is JsonArray array) { foreach (var p in array) foreach (string s in Strings(p)) yield return s; }
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
        item.ThumbnailRequested = true; var token = doc.Lifetime.Token;
        try
        {
            await thumbnailGate.WaitAsync(token);
            try { var bitmap = await Task.Run(() => MakeBitmap(TextureDecoder.Decode(doc.Document, item.Record, token), 0, 48), token); token.ThrowIfCancellationRequested(); item.Thumbnail = bitmap; thumbnails.Enqueue(item); while (thumbnails.Count > 256) { var old = thumbnails.Dequeue(); old.Thumbnail = null; old.ThumbnailRequested = false; } }
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
    private void ApplySceneOptions() { scene?.SetWireframe(Wireframe.IsChecked == true); scene?.SetTextured(TexturesEnabled.IsChecked == true); scene?.SetBounds(BoundsEnabled.IsChecked == true); scene?.SetFly(FlyEnabled.IsChecked == true); }
    private void SceneOptionsChanged(object sender, RoutedEventArgs e) { if (ready) ApplySceneOptions(); }
    private void FrameSceneClick(object sender, RoutedEventArgs e) => scene?.FrameAll();
    private void IsolateClick(object sender, RoutedEventArgs e) { if (selectedNode != null) { isolatedNode = selectedNode; scene?.Isolate(selectedNode); } else ViewModel.Status = "Select a node in the scene or scene tree first"; }
    private void ShowAllClick(object sender, RoutedEventArgs e) { isolatedNode = null; scene?.Isolate(null); }
    private void SceneTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e) { if (e.NewValue is SceneTreeItem item) { InspectNode(item.Node.Index); inspectedSceneSource = item; } }
    private void InspectNode(int index)
    {
        inspectedSceneSource = null;
        if ((scene?.PreviewScene ?? ViewModel.SelectedDocument?.Document.Scene) is not { } data || index < 0 || index >= data.Nodes.Count) return;
        var actor = scene?.PickupAt(index);
        if (actor != null) index = actor.Root;
        scene?.SelectPickup(actor?.Root, actor?.Pickup is { } pickup && pickupDocument?.PickupEdits?.Find(pickup.Source) != null, pickupDocument?.PickupsLocked ?? true);
        selectedNode = index; var properties = (JsonObject)data.Nodes[index].Metadata.DeepClone();
        if (scene?.Mission is { } mission)
        {
            properties["preview_instance"] = data.Nodes[index].Name;
            properties["preview_world_position"] = JsonData.Vector(actor != null ? scene!.PickupPosition(actor.Root) : SceneViewport.WorldTransform(data, index).Translation);
            properties["source_node_index"] = mission.SourceNodes[index];
            properties["preview_layout"] = mission.Layout.Description;
        }
        SetProperties(properties); ViewModel.Status = $"Selected node #{index}: {data.Nodes[index].Name}";
    }
    private static EventRow[] EventRows(JsonObject entry)
    {
        List<EventRow> rows = []; List<JsonNode?> surfaces = [entry["surface_primary"]]; if (entry["surface_runtimes"] is JsonArray others) surfaces.AddRange(others);
        foreach (var surface in surfaces) if (surface?["events"] is JsonArray events) foreach (var ev in events.OfType<JsonObject>()) rows.Add(new(surface.Text("sequence_name"), ev.Text("type"), ev.Text("start_mode"), ev.Text("start_threshold"), ev)); return rows.ToArray();
    }
    private void EventSelected(object sender, SelectionChangedEventArgs e) { if (EventGrid.SelectedItem is EventRow row) SetProperties(row.Data); }
    private void PlayAudioClick(object sender, RoutedEventArgs e)
    {
        try { if (wave == null) return; if (player == null) { player = new WasapiPlayerBuilder().Build(); player.Init(wave); } if (player.PlaybackState == PlaybackState.Playing) player.Pause(); else { if (wave.Position >= wave.Length) wave.Position = 0; player.Play(); } } catch (Exception ex) { Report(ex); player?.Dispose(); player = null; }
    }
    private void StopAudioClick(object sender, RoutedEventArgs e) { player?.Stop(); if (wave != null) wave.Position = 0; UpdateAudioPosition(); }
    private void StopAudio() { player?.Dispose(); player = null; wave?.Dispose(); wave = null; waveInfo = null; }
    private void UpdateAudioPosition() { bool playing = player?.PlaybackState == PlaybackState.Playing; AudioPlayButton.Content = playing ? "Pause" : "Play"; System.Windows.Automation.AutomationProperties.SetName(AudioPlayButton, playing ? "Pause audio" : "Play audio"); if (wave == null) return; seeking = true; AudioSeek.Value = wave.CurrentTime.TotalSeconds; AudioTime.Text = $@"{wave.CurrentTime:mm\:ss\.fff} / {wave.TotalTime:mm\:ss\.fff}"; seeking = false; }
    private void SeekAudio(double seconds) { if (wave != null) wave.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, wave.TotalTime.TotalSeconds)); }
    private void AudioSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (ready && !seeking) SeekAudio(e.NewValue); }
    private void CueDoubleClick(object sender, MouseButtonEventArgs e) { if (AudioCues.SelectedItem is CueChoice cue) SeekAudio(cue.Seconds); }
    private async void ExportSelectedClick(object sender, RoutedEventArgs e) => await Export(false, false);
    private async void ExportAllClick(object sender, RoutedEventArgs e) => await Export(true, false);
    private async void ExportJsonClick(object sender, RoutedEventArgs e) => await Export(false, true);
    private Task Export(bool all, bool json) => RunUi(async () =>
    {
        if (ViewModel.SelectedDocument is not { } doc || ViewModel.Resolver is not { } resolver) return;
        if (operation != null) { ViewModel.Status = "Wait for the current operation or cancel it first"; return; }
        AssetRecord[] assets = all ? doc.Document.Assets.ToArray() : AssetGrid.SelectedItems.Cast<AssetItem>().Select(a => a.Record).ToArray();
        if (assets.Length == 0) { ViewModel.Status = "Select an asset to export"; return; }
        OpenFolderDialog dialog = new() { Title = "Choose an export destination outside the source folder" }; if (dialog.ShowDialog(this) != true) return;
        using var cancellation = new CancellationTokenSource(); operation = cancellation; CancelOperationItem.IsEnabled = true;
        try { string? pack = PreferredPack; int lod = LodCombo.SelectedIndex; var progress = new Progress<ExportProgress>(p => ViewModel.Status = $"Exporting {p.Completed}/{p.Total}: {p.Name}"); var result = await Task.Run(() => new ExportService(resolver).ExportAsync(doc.Document, assets, dialog.FolderName, json, pack, lod, progress, cancellation.Token)); foreach (string error in result.Errors) ViewModel.AddProblem(error, "Error", doc.Path); ViewModel.Status = $"Exported {result.Completed}/{assets.Length} assets to {result.Directory}"; MessageBox.Show(this, ViewModel.Status + (result.Errors.Count > 0 ? $"\n{result.Errors.Count} failures; see Diagnostics and export-report.json." : ""), "Export complete", MessageBoxButton.OK, result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information); }
        finally { operation = null; CancelOperationItem.IsEnabled = false; }
    });
    private async void ValidateClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        if (ViewModel.SelectedDocument is not { } selected || operation != null) return;
        using var cancellation = new CancellationTokenSource(); operation = cancellation; CancelOperationItem.IsEnabled = true;
        try
        {
            ViewModel.Status = "Validating source file on disk…";
            var diagnostics = await Task.Run(async () =>
            {
                var doc = await FormatRegistry.Default.OpenAsync(selected.Path,cancellation.Token);
                var notes = doc.Diagnostics.Select(d => new StudioProblem(d.Severity,"File / operation",d.Message,selected.Path,d.AssetIndex,d.Offset)).ToList();
                foreach (var asset in doc.Assets)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    try
                    {
                        if (asset.Kind == AssetKind.Texture) TextureDecoder.Decode(doc,asset,cancellation.Token);
                        else if (asset.Kind == AssetKind.Zrd) ZrdDecoder.Decode(doc.Slice(asset.Offset,asset.Length),cancellation.Token);
                        else if (asset.Kind == AssetKind.Sound) WaveDecoder.Read(doc.Slice(asset.Offset,asset.Length));
                    }
                    catch (InvalidDataException ex) { notes.Add(new("Error","File / operation",asset.Name + ": " + ex.Message,selected.Path,asset.Index,asset.Offset)); }
                }
                return notes;
            },cancellation.Token);
            foreach (var note in diagnostics) ViewModel.AddProblem(note.Message,note.Severity,note.File,note.AssetIndex,note.Offset);
            ViewModel.Status = $"Source-file validation finished: {diagnostics.Count} diagnostics";
        }
        finally { operation = null; CancelOperationItem.IsEnabled = false; }
    });
    private async void ReloadClick(object sender, RoutedEventArgs e) => await RunUi(() => ViewModel.ReloadAsync());
    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (operation is not { IsCancellationRequested: false }) return;
        operation.Cancel(); CancelOperationItem.IsEnabled = false; ViewModel.Status = "Canceling current operation…";
    }
    private async void CloseFileClick(object sender, RoutedEventArgs e) { e.Handled = true; if ((sender as Button)?.Tag is DocumentModel doc) await RunUi(() => ViewModel.CloseAsync(doc)); }
    private async void CloseCurrentClick(object sender, RoutedEventArgs e) { if (ViewModel.SelectedDocument is { } doc) await RunUi(() => ViewModel.CloseAsync(doc)); }
    private void ExitClick(object sender, RoutedEventArgs e) => Close();
    private void InspectorVisibilityClick(object sender, RoutedEventArgs e) { Layout.InspectorVisible = ((MenuItem)sender).IsChecked; inspectorTemporary = true; ArrangeWorkspace(); }
    private void DiagnosticsClick(object sender, RoutedEventArgs e) { Layout.ToolsVisible = ((MenuItem)sender).IsChecked; ToolTabs.SelectedIndex = 2; ArrangeWorkspace(); }
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
        IsChangingLayout = detachingWorkspace = true;
        try
        {
            var defaults = new WorkspaceLayout(); ViewModel.Settings.Workspace = defaults;
            previousPreset = defaults.Preset;
            navigatorTemporary = inspectorTemporary = toolsMaximized = false;
            NavigationTabs.SelectedIndex = defaults.BrowserTab;
            InspectorTabs.SelectedIndex = animation != null ? defaults.InspectorTab : -1;
            // Static viewers have no Dispatch page; use their normal Related fallback.
            ToolTabs.SelectedIndex = animation != null ? defaults.ToolTab : 4;
            ApplyDensity(); animation?.ResetLayout(); ArrangeWorkspace();
        }
        finally { detachingWorkspace = false; }
        SaveWorkspacePreferences();
    }
    private void CopyPropertiesClick(object sender, RoutedEventArgs e) { if (animation?.ResolvePendingDrafts() == false) return; if (properties != null) Clipboard.SetText(properties.ToJsonString(JsonData.Options)); }
    private void HelpClick(object sender, RoutedEventArgs e) => MessageBox.Show(this, "Open the root containing image.zbd and mission folders. Double-click a file to open it. Filter assets within a tab or search across the whole root.\n\nTextures open at 1:1 (one texture pixel per screen pixel). Wheel zooms; left or middle drag pans. Fit and 1:1 reset the scale. Choose channels or inspect the palette.\n3D: right drag orbits; middle drag or Shift+right drag pans. Wheel zooms toward the surface under the pointer. Frame all resets the camera. Pick a node or use the Scene tree, then Isolate. LOD 0 selects the highest detail for each object. Higher LOD numbers show lower-detail variants. Fly looks around from the camera position; the wheel moves forward/back with smaller steps near surfaces.\nWhole world pickups: click a pickup to select its bounds. Turn off the lock icon to reveal XYZ movement arrows and editable coordinates. Drag an arrow, or open View > Properties (Alt+Enter), type a coordinate and press Enter. Escape cancels a drag. Moves include unambiguously matching difficulty records. Ctrl+Z/Ctrl+Y undo/redo. Ctrl+S saves to the owning archive (often zrdr.zbd); Ctrl+Shift+S saves a new copy and retargets subsequent saves. Reference datasets require Save As. File > Create backup on Save is optional and off by default. Other map objects remain inspectable.\nAudio: Play/pause, seek using waveform or slider; double-click a cue.\nAnimation: Space plays/pauses immediately after selecting an asset. Transport icons and the seek bar share one row. The range follows the calculated duration; indefinite animations show a labeled preview range. The Dispatched events graphic sits below the player. The Sequences tab on the right selects entries, sequences and events. The right tabs are Sequences, Settings and References. Sequences opens first; switching animations retains the selected tab. Right-click an entry, sequence or event and choose Properties, or press Alt+Enter. Properties opens in a separate resizable window and stays on that item while you browse. The same window inspects assets and scene objects. Accepted edits enter the document undo history; Close keeps them and Save writes them to disk. Draft fields validate inline; Escape restores a value. View offers workspace presets, density and Reset layout. Bottom tools contain Dispatch, Event log, Problems, Runtime, Related and original source Bytes. Ctrl+wheel zooms Dispatch. Reset / stop previews cleanup separately. The LOD picker applies to animation and mission context. Sprite textures cycle automatically. Map shows context; Bind chooses a root. Show grid and Flat-ground collision are independent, both enabled by default. Height offsets the animation above or below the plane (−999 to 999). Mute controls audio. Problems and Event log distinguish approximation and unavailable game behavior.\n\nExports create a new folder outside the source tree. Ctrl+E exports selected records. Animation edits support Ctrl+Z/Ctrl+Y and Ctrl+S Save As to a new file. Animation Save As and exports preserve source files. Other format edits are unsupported. F5 reloads; Escape cancels an active export or validation; Ctrl+W closes a file.", "Controls and formats");
    private void AboutClick(object sender, RoutedEventArgs e)
    {
        var assembly = typeof(MainWindow).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version?.ToString(3) ?? "unknown";
        MessageBox.Show(this, $"zStudio {version}\nWindows asset explorer\n\nTexture packs · ZAR archives · prepared scripts · animations · GameZ worlds\n\nBuilt with .NET, WPF, Helix Toolkit, CommunityToolkit.Mvvm, and NAudio. See THIRD-PARTY-NOTICES.md for licenses. Game assets are not included.", "About");
    }
    private void Keyboard(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Enter || e.SystemKey == Key.Enter) && System.Windows.Input.Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; OpenCurrentProperties(); return; }
        if (e.Key == Key.Escape && scene?.CancelPickupDrag() == true) { e.Handled = true; return; }
        if (e.Key == Key.Space && System.Windows.Input.Keyboard.Modifiers == ModifierKeys.None &&
            shownAsset?.Kind == AssetKind.Animation && ViewModel.SelectedDocument?.AnimationEdits != null &&
            AnimationSpaceTarget(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            if (!e.IsRepeat) { if (animation != null) animation.TogglePlayback(); else pendingAnimationPlay = !pendingAnimationPlay; }
            return;
        }
        bool ctrl = KeyboardModifiers(); if (ctrl && e.Key == Key.O) OpenFolderClick(this, e); else if (ctrl && e.Key == Key.E) ExportSelectedClick(this, e); else if (ctrl && e.Key == Key.S) { if ((System.Windows.Input.Keyboard.Modifiers & ModifierKeys.Shift) != 0) SaveCurrentAsClick(this, e); else SaveCurrentClick(this, e); } else if (ctrl && e.Key == Key.Z && e.OriginalSource is not TextBoxBase) UndoAnimationClick(this, e); else if (ctrl && e.Key == Key.Y && e.OriginalSource is not TextBoxBase) RedoAnimationClick(this, e); else if (ctrl && e.Key == Key.W) CloseCurrentClick(this, e); else if (e.Key == Key.F5) ReloadClick(this, e); else if (e.Key == Key.Escape && operation is { IsCancellationRequested: false }) CancelClick(this, e); else return; e.Handled = true;
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
        if (resolvingClose) { e.Cancel = true; return; }
        System.Windows.Input.Keyboard.ClearFocus();
        if (!allowClose && (ViewModel.Documents.Any(d => d.IsDirty) || animation?.HasPendingDrafts == true || propertiesWindow?.HasPendingDrafts == true))
        {
            e.Cancel = true; resolvingClose = true;
            try
            {
                // Discard (and a canceled Save As) can finish synchronously. Leave the
                // original WPF Closing event before showing prompts or calling Close again.
                await Dispatcher.Yield(DispatcherPriority.Normal);
                if (animation?.ResolvePendingDrafts() == false || !ResolvePropertiesDrafts()) return;
                foreach (var document in ViewModel.Documents.ToArray())
                    if (!await ConfirmDocumentCloseAsync(document)) return;
                allowClose = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Report(ex); return; }
            finally { resolvingClose = false; }
            Close();
            return;
        }
        propertiesWindow?.CloseResolved();
        ObserveDocumentCommands(null);
        operation?.Cancel(); preview.Cancel(); difficultyRefresh?.Cancel(); difficultyRefresh?.Dispose(); ViewModel.PropertyChanged -= DifficultyPreferenceChanged; diskTimer.Stop(); audioTimer.Stop(); StopAudio(); animation?.Dispose(); scene?.Dispose(); ViewModel.Dispose();
        var s = ViewModel.Settings; if (WindowState == WindowState.Normal) { s.Width = ActualWidth; s.Height = ActualHeight; }
        SaveWorkspacePreferences();
        try { s.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Settings cannot prevent shutdown. */ }
    }
    private sealed record PackChoice(string Name, string? Path);
    private sealed record EventRow(string Sequence, string Type, string Mode, string Threshold, JsonObject Data);
    private sealed record CueChoice(uint Id, uint Sample, double Seconds) { public override string ToString() => $"Cue {Id} · sample {Sample:N0} · {Seconds:F3} s"; }
}
