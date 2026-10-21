using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>Authored motion transport sharing the normal renderer.</summary>
public sealed class MotionEditor : UserControl, IDisposable
{
    private readonly DocumentModel document;
    private readonly Guid member;
    private readonly AssetResolver resolver;
    private readonly CancellationToken lifetime;
    private readonly DockPanel root = new();
    private readonly ToolBar presentationToolbar = new();
    private readonly DockPanel presentationTransport = new() { Margin = new(6) };
    internal bool PresentationUiHidden { get; private set; }
    internal void SetPresentationUiHidden(bool hidden)
    {
        PresentationUiHidden = hidden;
        presentationToolbar.Visibility = presentationTransport.Visibility = support.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
    }
    private readonly ComboBox assembly = new() { MinWidth = 140, MaxWidth = 260, DisplayMemberPath = nameof(AssetRecord.Name) };
    private readonly ComboBox lod = new() { MinWidth = 80 };
    private readonly Slider seeker = new() { Minimum = 0, MinWidth = 40, Margin = new(8, 0, 8, 0) };
    private readonly Button play = new() { Content = "▶", ToolTip = "Play / pause (Space)", MinWidth = 32 };
    private readonly TextBlock time = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 110 };
    private readonly TextBlock support = new() { TextWrapping = TextWrapping.Wrap, Margin = new(8), Opacity = .75 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch elapsed = new();
    private AssetDocument? library;
    private MotionPreview? sampler;
    private MotionClip? clip;
    private bool syncing, disposed;
    private double seconds, startedAt;
    private long generation;
    private long sampleRequest;
    private long clipGeneration;
    private Task sampling = Task.CompletedTask;
    public Task<bool> PresentationWork { get; private set; } = Task.FromResult(true);
    private CancellationTokenSource? load;
    private bool? pendingPlayback;
    private int? selectedMember;
    private IReadOnlyList<string> librarySkipped = [];
    private string? previewFailure;
    public SceneViewport Viewport { get; private set; } = new();
    public event Action? SceneChanged;
    public event Action<string>? StatusChanged;
    public int Lod => Math.Max(0, lod.SelectedIndex);
    public bool IsPlaying { get; private set; }
    internal Guid MemberId => member;
    public int? AssemblyMember => selectedMember;
    internal IEnumerable<AssetRecord> Assemblies => library?.Assets.Where(a => a.Content is MechAssembly) ?? [];
    private (string[] Items, int Count, bool Truncated) DiagnosticPreview()
    {
        var all = sampler?.Diagnostics ?? [];
        return (all.Take(32).Select(s => s.Length > 512 ? s[..512] + "…" : s).ToArray(), sampler?.DiagnosticCount ?? 0,
            all.Count > 32 || all.Take(32).Any(s => s.Length > 512));
    }
    public object State
    {
        get
        {
            var notes = DiagnosticPreview(); int count = Assemblies.Count();
            return new { member, seconds, playing = IsPlaying, loading = load != null, playbackRequested = pendingPlayback ?? IsPlaying,
                loopSeconds = clip?.LoopTime, frameCount = clip?.FrameCount, lod = Lod, library = library?.Path, assembly = AssemblyMember,
                assemblies = Assemblies.Take(32).Select(a => new { member = a.Index, a.Name }).ToArray(), assemblyCount = count, assembliesTruncated = count > 32,
                diagnostics = notes.Items, diagnosticCount = notes.Count, diagnosticsTruncated = notes.Truncated, librarySkipped, previewFailure };
        }
    }
    private void RefreshSupport()
    {
        var notes = DiagnosticPreview();
        support.Text = string.Join("\n", notes.Items.Take(4));
        if (librarySkipped.Count > 0) support.Text += $"\nUnreadable archives skipped while resolving the mech library: {string.Join("; ", librarySkipped.Take(2))}";
        if (notes.Count > 4 || notes.Truncated) support.Text += $"\nDiagnostic preview · {notes.Count} notices; shortened text/list. Inspect motion parts for complete authored names.";
    }
    public MotionEditor(DocumentModel document, Guid member, AssetResolver resolver, CancellationToken lifetime)
    {
        this.document = document; this.member = member; this.resolver = resolver; this.lifetime = lifetime;
        AutomationProperties.SetName(assembly, "Motion mech assembly"); AutomationProperties.SetName(lod, "Motion LOD");
        AutomationProperties.SetName(seeker, "Motion playhead"); AutomationProperties.SetName(play, "Motion play or pause");
        Content = root;
        var toolbar = presentationToolbar; DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        toolbar.Items.Add(lod); toolbar.Items.Add(assembly);
        Button frame = new() { Content = new PreviewIcon { Kind = "Frame", Width = 16, Height = 16 }, ToolTip = "Frame motion", MinWidth = 32 };
        AutomationProperties.SetName(frame, "Frame motion"); frame.Click += (_, _) => Viewport.FrameAll(); toolbar.Items.Add(frame);
        var bottom = presentationTransport; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(play); DockPanel.SetDock(time, Dock.Right); bottom.Children.Add(time); bottom.Children.Add(seeker);
        DockPanel.SetDock(support, Dock.Bottom); root.Children.Add(support); root.Children.Add(Viewport);
        play.Click += (_, _) => TogglePlayback();
        seeker.ValueChanged += (_, _) => { if (!syncing) Seek(seeker.Value); };
        lod.SelectionChanged += (_, _) => { if (!syncing) Present(); };
        assembly.SelectionChanged += async (_, _) =>
        {
            if (syncing || disposed || assembly.SelectedItem is not AssetRecord asset) return;
            try { await SelectAssemblyAsync(asset.Index); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { StatusChanged?.Invoke(ex.Message); }
        };
        timer.Tick += (_, _) => { if (lifetime.IsCancellationRequested) { Pause(); return; } if (IsPlaying && clip != null) { seconds = (startedAt + elapsed.Elapsed.TotalSeconds) % clip.LoopTime; Present(tick: true); } }; timer.Start();
    }
    private MotionClip CurrentClip()
    {
        var resources = document.ResourceMembers ?? throw new InvalidDataException("The motion archive is unavailable.");
        int index = resources.Members.ToList().FindIndex(m => m.Id == member);
        return index >= 0 && resources.Document.Assets[index].Content is MotionClip motion ? motion : throw new InvalidDataException("The selected motion member is no longer available.");
    }
    public async Task InitializeAsync(CancellationToken token)
    {
        clip = CurrentClip(); syncing = true; seeker.Maximum = clip.LoopTime; syncing = false;
        List<string> skipped = []; library = await MotionLibrary.LoadAsync(document.Path, resolver, token, skipped); librarySkipped = skipped;
        token.ThrowIfCancellationRequested(); if (disposed) return;
        syncing = true; assembly.ItemsSource = library.Assets.Where(a => a.Content is MechAssembly).ToArray(); syncing = false;
        var suggestion = MotionLibrary.SuggestedMember(document.ResourceMembers!.Member(member).Name, library);
        if (suggestion is int index) await SelectAssemblyAsync(index, token);
        else { support.Text = "Choose a mech assembly to preview this motion. No unique assembly matches the clip name."; await RefreshClipAsync(); }
    }
    public async Task SelectAssemblyAsync(int index, CancellationToken token = default)
    {
        var asset = library?.Assets.SingleOrDefault(a => a.Index == index && a.Content is MechAssembly) ?? throw new InvalidDataException("Choose a current mech member index.");
        long request = ++generation; load?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime, token); load = cancellation;
        var ct = cancellation.Token; pendingPlayback ??= IsPlaying; SuspendPlayback();
        SceneViewport? replacement = null;
        try
        {
            var selected = (MechAssembly)asset.Content!; var nextClip = CurrentClip();
            var selectedLibrary = library!;
            var next = await Task.Run(() => new MotionPreview(nextClip, selectedLibrary, selected, ct), ct);
            ct.ThrowIfCancellationRequested(); if (disposed || request != generation) return;
            var choices = SceneLods.Choices(new SceneLods(selectedLibrary.Scene!).Count([selected.RootNode]));
            int nextLod = Math.Min(Lod, choices.Length - 1);
            var context = new AnimationPreviewContext { World = selectedLibrary, InspectionNodes = next.InspectionNodes, Package = new AnimationPackage { Prefix = new byte[72], Tail = [] } };
            replacement = new();
            var initialFrame = await Task.Run(() => next.At(seconds, nextLod, ct), ct);
            await replacement.ShowAnimationAsync(context, initialFrame, resolver, false, ct, previewLifetime: lifetime);
            ct.ThrowIfCancellationRequested(); if (disposed || request != generation) return;
            seconds = Math.Min(seconds, nextClip.LoopTime);
            // Assembly loading can finish after Hide UI changed. Inherit the current override
            // before the replacement is attached or can present its first frame.
            replacement.SetPresentationUiHidden(PresentationUiHidden);
            var previous = Viewport; Viewport = replacement; replacement = null;
            root.Children.Remove(previous); root.Children.Add(Viewport); previous.Dispose();
            ++clipGeneration; selectedMember = index; clip = nextClip; sampler = next;
            previewFailure = null; time.ToolTip = null;
            syncing = true; assembly.SelectedItem = asset; lod.ItemsSource = choices; lod.SelectedIndex = nextLod; syncing = false;
            RefreshSupport(); Present(); await PresentationWork; SceneChanged?.Invoke();
        }
        catch
        {
            if (!disposed && request == generation)
            {
                syncing = true; assembly.SelectedItem = assembly.Items.OfType<AssetRecord>().FirstOrDefault(a => a.Index == selectedMember); syncing = false;
            }
            throw;
        }
        finally
        {
            replacement?.Dispose();
            if (request == generation)
            {
                load = null; bool resume = pendingPlayback == true; pendingPlayback = null;
                if (resume && !disposed && !lifetime.IsCancellationRequested) Play(); else Pause();
            }
        }
    }
    private void ClearBinding(string message, bool notify = true)
    {
        // Supersede any assembly load; its completion no longer owns playback state.
        generation++; load?.Cancel(); load = null; Pause(); pendingPlayback = null; sampler = null; selectedMember = null; ++clipGeneration;
        syncing = true; assembly.SelectedItem = null; syncing = false;
        Viewport.Clear(); SceneChanged?.Invoke(); support.Text = message; if (notify) StatusChanged?.Invoke(message);
    }
    public async Task RefreshClipAsync()
    {
        if (disposed) return;
        clip = CurrentClip(); seconds = Math.Min(seconds, clip.LoopTime);
        if (library?.Assets.FirstOrDefault(a => a.Index == selectedMember)?.Content is MechAssembly selected)
        {
            var current = clip; var bound = library; long request = ++clipGeneration;
            var next = await Task.Run(() => new MotionPreview(current, bound, selected, lifetime), lifetime);
            if (disposed || request != clipGeneration || !ReferenceEquals(current, clip) || !ReferenceEquals(bound, library)) return;
            sampler = next; RefreshSupport();
        }
        startedAt = seconds; elapsed.Restart(); Present(); await PresentationWork;
    }
    public void SetLod(int value)
    {
        if (value < 0 || value >= lod.Items.Count) throw new InvalidDataException("LOD is unavailable for this assembly.");
        lod.SelectedIndex = value;
    }
    public void Seek(double value)
    {
        if (!double.IsFinite(value) || value < 0 || clip == null || value > clip.LoopTime) throw new InvalidDataException("Seek within this motion's loop duration.");
        seconds = value; startedAt = seconds; elapsed.Restart(); Present();
    }
    public void TogglePlayback() { if (pendingPlayback ?? IsPlaying) Pause(); else Play(); }
    public void Play()
    {
        if (disposed) return;
        if (pendingPlayback != null) { pendingPlayback = true; play.Content = "⏸"; return; }
        if (sampler == null) return;
        startedAt = seconds; elapsed.Restart(); IsPlaying = true; play.Content = "⏸";
    }
    public void Pause() { if (pendingPlayback != null) pendingPlayback = false; SuspendPlayback(); play.Content = "▶"; }
    private void SuspendPlayback() { IsPlaying = false; elapsed.Stop(); }
    private void Present(bool tick = false)
    {
        if (disposed || clip == null) return;
        if (sampler == null && previewFailure != null)
        {
            // A seek cannot revive a refused binding or publish a time that
            // was never presented. Choosing an assembly is the retry boundary.
            seconds = startedAt = 0;
            syncing = true; seeker.Value = 0; syncing = false;
            time.Text = "Preview unavailable";
            PresentationWork = Task.FromResult(false);
            return;
        }
        if (sampler != null && !lifetime.IsCancellationRequested && (!tick || PresentationWork.IsCompleted))
            PresentationWork = PresentAsync(++sampleRequest, sampler, seconds, Lod);
        syncing = true; seeker.Maximum = clip.LoopTime; seeker.Value = seconds; syncing = false;
        time.Text = $"{seconds:F3} / {clip.LoopTime:F3} s";
    }
    private async Task<bool> PresentAsync(long request, MotionPreview source, double at, int level)
    {
        try
        {
            await sampling;
            if (disposed || request != sampleRequest || !ReferenceEquals(source, sampler) || lifetime.IsCancellationRequested) return false;
            var work = Task.Run(() => source.At(at, level, lifetime), lifetime); sampling = work;
            var frame = await work;
            if (disposed || request != sampleRequest || !ReferenceEquals(source, sampler) || lifetime.IsCancellationRequested) return false;
            Viewport.UpdateAnimationFrame(frame);
            return true;
        }
        catch (OperationCanceledException) when (disposed || lifetime.IsCancellationRequested) { }
        catch (SceneViewport.RenderLimitException ex)
        {
            if (!disposed && !lifetime.IsCancellationRequested && request == sampleRequest && ReferenceEquals(source, sampler))
            {
                // The sampled time was rejected. Invalidate this binding and any
                // deferred resume, retaining the authored clip/library for an
                // explicit assembly retry; MCP receives PresentationWork=false.
                previewFailure = ex.Message.Length > 512 ? ex.Message[..512] + "…" : ex.Message;
                seconds = startedAt = 0;
                syncing = true; seeker.Value = 0; syncing = false;
                time.Text = "Preview unavailable"; time.ToolTip = previewFailure;
                ClearBinding("Motion preview unavailable: " + previewFailure + " Choose an assembly to retry.");
            }
        }
        catch (InvalidDataException ex) { if (!disposed && request == sampleRequest && ReferenceEquals(source, sampler)) { Pause(); StatusChanged?.Invoke(ex.Message); } }
        finally { if (sampling.IsCompleted) sampling = Task.CompletedTask; }
        return false;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; generation++; load?.Cancel(); pendingPlayback = null; timer.Stop(); elapsed.Stop(); Viewport.Dispose(); GC.SuppressFinalize(this);
    }
}
