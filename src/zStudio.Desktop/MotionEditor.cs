using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

/// <summary>Authored motion transport sharing the normal renderer, resource history and Properties editor.</summary>
public sealed class MotionEditor : UserControl, IDisposable
{
    private readonly DocumentModel document;
    private readonly Guid member;
    private readonly AssetResolver resolver;
    private readonly CancellationToken lifetime;
    private readonly Func<ZbdDocument, (ResourceEditSession Edits, ResourceSnapshot Snapshot)?> resolveLibrarySnapshot;
    private readonly DockPanel root = new();
    private readonly ComboBox assembly = new() { MinWidth = 140, MaxWidth = 260, DisplayMemberPath = nameof(AssetRecord.Name) };
    private readonly ComboBox lod = new() { MinWidth = 80 };
    private readonly Slider seeker = new() { Minimum = 0, MinWidth = 40, Margin = new(8, 0, 8, 0) };
    private readonly Button play = new() { Content = "▶", ToolTip = "Play / pause (Space)", MinWidth = 32 };
    private readonly TextBlock time = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 110 };
    private readonly TextBlock support = new() { TextWrapping = TextWrapping.Wrap, Margin = new(8), Opacity = .75 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch elapsed = new();
    private ZbdDocument? library;
    private (ResourceEditSession Edits, ResourceSnapshot Snapshot)? librarySnapshot;
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
    private Guid? selectedIdentity;
    // The assembly an in-flight SelectAssemblyAsync is loading, so a concurrent library refresh keeps that choice.
    private int? requestedMember;
    private Guid? requestedIdentity;
    private IReadOnlyList<string> librarySkipped = [];
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
                diagnostics = notes.Items, diagnosticCount = notes.Count, diagnosticsTruncated = notes.Truncated, librarySkipped };
        }
    }
    private void RefreshSupport()
    {
        var notes = DiagnosticPreview();
        support.Text = string.Join("\n", notes.Items.Take(4));
        if (librarySkipped.Count > 0) support.Text += $"\nUnreadable archives skipped while resolving the mech library: {string.Join("; ", librarySkipped.Take(2))}";
        if (notes.Count > 4 || notes.Truncated) support.Text += $"\nDiagnostic preview · {notes.Count} notices; shortened text/list. Inspect motion parts for complete authored names.";
    }
    public MotionEditor(DocumentModel document, Guid member, AssetResolver resolver, CancellationToken lifetime, Func<ZbdDocument, (ResourceEditSession Edits, ResourceSnapshot Snapshot)?> resolveLibrarySnapshot)
    {
        this.document = document; this.member = member; this.resolver = resolver; this.lifetime = lifetime; this.resolveLibrarySnapshot = resolveLibrarySnapshot;
        AutomationProperties.SetName(assembly, "Motion mech assembly"); AutomationProperties.SetName(lod, "Motion LOD");
        AutomationProperties.SetName(seeker, "Motion playhead"); AutomationProperties.SetName(play, "Motion play or pause");
        Content = root;
        ToolBar toolbar = new(); DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        toolbar.Items.Add(lod); toolbar.Items.Add(assembly);
        Button frame = new() { Content = new PreviewIcon { Kind = "Frame", Width = 16, Height = 16 }, ToolTip = "Frame motion", MinWidth = 32 };
        AutomationProperties.SetName(frame, "Frame motion"); frame.Click += (_, _) => Viewport.FrameAll(); toolbar.Items.Add(frame);
        DockPanel bottom = new() { Margin = new(6) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
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
        var snapshot = document.ResourceEdits?.Current ?? throw new InvalidDataException("The motion archive is unavailable.");
        int index = snapshot.Members.ToList().FindIndex(m => m.Id == member);
        return index >= 0 && snapshot.Document.Assets[index].Content is MotionClip motion ? motion : throw new InvalidDataException("The selected motion member is no longer available.");
    }
    public async Task InitializeAsync(CancellationToken token)
    {
        clip = CurrentClip(); syncing = true; seeker.Maximum = clip.LoopTime; syncing = false;
        List<string> skipped = []; library = await MotionLibrary.LoadAsync(document.Path, resolver, token, skipped); librarySkipped = skipped;
        token.ThrowIfCancellationRequested(); if (disposed) return;
        librarySnapshot = resolveLibrarySnapshot(library);
        syncing = true; assembly.ItemsSource = library.Assets.Where(a => a.Content is MechAssembly).ToArray(); syncing = false;
        var suggestion = MotionLibrary.SuggestedMember(document.ResourceEdits!.Member(member).Name, library);
        if (suggestion is int index) await SelectAssemblyAsync(index, token);
        else { support.Text = "Choose a mech assembly to preview this motion. No unique assembly matches the clip name."; await RefreshClipAsync(); }
    }
    public async Task SelectAssemblyAsync(int index, CancellationToken token = default)
    {
        var asset = library?.Assets.SingleOrDefault(a => a.Index == index && a.Content is MechAssembly) ?? throw new InvalidDataException("Choose a current mech member index.");
        // Capture the UUID from the same frozen snapshot as the selected geometry.
        // A newly added/duplicated member has no original-file index to recover later.
        Guid? identity = librarySnapshot is { } snapshot && index < snapshot.Snapshot.Members.Count ? snapshot.Snapshot.Members[index].Id : null;
        long request = ++generation; load?.Cancel(); requestedMember = index; requestedIdentity = identity;
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
            // A resource edit may have refreshed the clip while mesh/texture preparation was pending.
            while (!ReferenceEquals(nextClip, CurrentClip()))
            {
                nextClip = CurrentClip();
                next = await Task.Run(() => new MotionPreview(nextClip, selectedLibrary, selected, ct), ct);
                ct.ThrowIfCancellationRequested(); if (disposed || request != generation) return;
            }
            seconds = Math.Min(seconds, nextClip.LoopTime);
            var previous = Viewport; Viewport = replacement; replacement = null;
            root.Children.Remove(previous); root.Children.Add(Viewport); previous.Dispose();
            ++clipGeneration; selectedIdentity = identity; selectedMember = index; clip = nextClip; sampler = next;
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
                load = null; requestedMember = null; requestedIdentity = null; bool resume = pendingPlayback == true; pendingPlayback = null;
                if (resume && !disposed && !lifetime.IsCancellationRequested) Play(); else Pause();
            }
        }
    }
    public async Task RefreshLibraryAsync()
    {
        if (disposed) return;
        ZbdDocument nextLibrary;
        List<string> skipped = [];
        try { nextLibrary = await MotionLibrary.LoadAsync(document.Path, resolver, lifetime, skipped); librarySkipped = skipped; }
        catch (OperationCanceledException) when (disposed || lifetime.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Accepted edits and saves elsewhere remain successful; only this dependent preview loses its binding.
            if (disposed || lifetime.IsCancellationRequested) return;
            library = null; librarySnapshot = null; syncing = true; assembly.ItemsSource = Array.Empty<AssetRecord>(); syncing = false;
            ClearBinding("The mech library is unavailable: " + ex.Message); return;
        }
        if (disposed || lifetime.IsCancellationRequested) return;
        // Read the binding only after the await: an assembly chosen meanwhile, loaded or still loading, is the current intent.
        var previousLibrary = library; var previousSnapshot = librarySnapshot;
        bool loading = load != null && requestedMember != null;
        int? target = loading ? requestedMember : selectedMember; Guid? identity = loading ? requestedIdentity : selectedIdentity;
        // An unchanged library needs nothing: no binding, or an in-flight choice that already loads from it.
        if ((target == null || loading) && ReferenceEquals(previousLibrary, nextLibrary)) return;
        var nextSnapshot = resolveLibrarySnapshot(nextLibrary);
        if (target is int oldIndex && previousLibrary != null && !ReferenceEquals(previousLibrary, nextLibrary))
            target = RemapMember(oldIndex, identity, previousLibrary, previousSnapshot, nextLibrary, nextSnapshot);
        var view = Viewport.CaptureView();
        library = nextLibrary; librarySnapshot = nextSnapshot;
        syncing = true; assembly.ItemsSource = library.Assets.Where(a => a.Content is MechAssembly).ToArray(); syncing = false;
        if (target is not int index || !library.Assets.Any(a => a.Index == index && a.Content is MechAssembly))
        { ClearBinding(selectedMember == null ? "Choose a mech assembly to preview this motion." : "The bound mech member is no longer available. Choose an assembly.", notify: selectedMember != null); return; }
        try { await SelectAssemblyAsync(index, lifetime); Viewport.RestoreView(view); }
        catch (OperationCanceledException) when (disposed || lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { if (!disposed) ClearBinding("The bound mech member cannot be previewed: " + ex.Message); }
    }
    /// <summary>Only exact identities survive a library change; never select another member at the former index.</summary>
    private static int? RemapMember(int oldIndex, Guid? identity, ZbdDocument previous, (ResourceEditSession Edits, ResourceSnapshot Snapshot)? before, ZbdDocument next, (ResourceEditSession Edits, ResourceSnapshot Snapshot)? after)
    {
        static bool Same(AssetRecord a, AssetRecord b) => a.Name == b.Name && a.Offset == b.Offset && a.Length == b.Length;
        if (oldIndex < 0 || oldIndex >= previous.Assets.Count) return null;
        var old = previous.Assets[oldIndex];
        if (after is { } current)
        {
            var members = current.Snapshot.Members;
            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                // Session members keep their UUIDs; a selection from the unedited file maps through its original directory entry.
                if (identity is Guid id ? m.Id == id : before == null && m.SourceIndex == oldIndex && current.Edits.OriginalAsset(m) is { } original && Same(original, old)) return i;
            }
            return null;
        }
        if (!previous.Path.Equals(next.Path, StringComparison.OrdinalIgnoreCase) || oldIndex >= next.Assets.Count || next.Assets[oldIndex].Name != old.Name) return null;
        // A closed session whose current snapshot was verified-saved to this unchanged file has identical member order.
        if (before is { } prior) return !prior.Edits.IsDirty && ReferenceEquals(prior.Snapshot, prior.Edits.Current) && prior.Edits.TargetPath.Equals(next.Path, StringComparison.OrdinalIgnoreCase) && prior.Edits.TargetStamp == next.Stamp ? oldIndex : null;
        // An unedited library keeps indices only while the file itself is unchanged.
        return identity == null && previous.Stamp == next.Stamp && Same(next.Assets[oldIndex], old) ? oldIndex : null;
    }
    private void ClearBinding(string message, bool notify = true)
    {
        // Supersede any assembly load from the previous library; its completion no longer owns playback state.
        generation++; load?.Cancel(); load = null; requestedMember = null; requestedIdentity = null; Pause(); pendingPlayback = null; sampler = null; selectedMember = null; selectedIdentity = null; ++clipGeneration;
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
        catch (InvalidDataException ex) { if (!disposed && request == sampleRequest && ReferenceEquals(source, sampler)) { Pause(); StatusChanged?.Invoke(ex.Message); } }
        finally { if (sampling.IsCompleted) sampling = Task.CompletedTask; }
        return false;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; generation++; load?.Cancel(); pendingPlayback = null; timer.Stop(); elapsed.Stop(); Viewport.Dispose(); GC.SuppressFinalize(this);
    }
}
