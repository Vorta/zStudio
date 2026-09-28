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
    private MotionPreview? sampler;
    private MotionClip? clip;
    private bool syncing, disposed;
    private double seconds, startedAt;
    private long generation;
    private CancellationTokenSource? load;
    private int? selectedMember;
    private Guid? selectedIdentity;
    public SceneViewport Viewport { get; private set; } = new();
    public event Action? SceneChanged;
    public event Action<string>? StatusChanged;
    public int Lod => Math.Max(0, lod.SelectedIndex);
    public bool IsPlaying { get; private set; }
    public int? AssemblyMember => selectedMember;
    public object State => new { member, seconds, playing = IsPlaying, loopSeconds = clip?.LoopTime, frameCount = clip?.FrameCount, lod = Lod, library = library?.Path, assembly = AssemblyMember,
        assemblies = library?.Assets.Where(a => a.Content is MechAssembly).Select(a => new { member = a.Index, a.Name }).ToArray(), diagnostics = sampler?.Diagnostics };
    public MotionEditor(DocumentModel document, Guid member, AssetResolver resolver, CancellationToken lifetime)
    {
        this.document = document; this.member = member; this.resolver = resolver; this.lifetime = lifetime;
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
        timer.Tick += (_, _) => { if (lifetime.IsCancellationRequested) { Pause(); return; } if (IsPlaying && clip != null) { seconds = (startedAt + elapsed.Elapsed.TotalSeconds) % clip.LoopTime; Present(); } }; timer.Start();
    }
    private MotionClip CurrentClip()
    {
        var snapshot = document.ResourceEdits?.Current ?? throw new InvalidDataException("The motion archive is unavailable.");
        int index = snapshot.Members.ToList().FindIndex(m => m.Id == member);
        return index >= 0 && snapshot.Document.Assets[index].Content is MotionClip motion ? motion : throw new InvalidDataException("The selected motion member is no longer available.");
    }
    public async Task InitializeAsync(CancellationToken token)
    {
        clip = CurrentClip(); library = await MotionLibrary.LoadAsync(document.Path, resolver, token);
        token.ThrowIfCancellationRequested(); if (disposed) return;
        syncing = true; assembly.ItemsSource = library.Assets.Where(a => a.Content is MechAssembly).ToArray(); syncing = false;
        var suggestion = MotionLibrary.SuggestedMember(document.ResourceEdits!.Member(member).Name, library);
        if (suggestion is int index) await SelectAssemblyAsync(index, token);
        else { support.Text = "Choose a mech assembly to preview this motion. No unique assembly matches the clip name."; RefreshClip(); }
    }
    public async Task SelectAssemblyAsync(int index, CancellationToken token = default)
    {
        var asset = library?.Assets.SingleOrDefault(a => a.Index == index && a.Content is MechAssembly) ?? throw new InvalidDataException("Choose a current mech member index.");
        long request = ++generation; load?.Cancel(); load?.Dispose(); load = CancellationTokenSource.CreateLinkedTokenSource(lifetime, token);
        var ct = load.Token; bool playing = IsPlaying; Pause();
        var selected = (MechAssembly)asset.Content!; var nextClip = CurrentClip();
        var next = new MotionPreview(nextClip, library!, selected);
        var choices = SceneLods.Choices(new SceneLods(library!.Scene!).Count([selected.RootNode]));
        int nextLod = Math.Min(Lod, choices.Length - 1);
        var context = new AnimationPreviewContext { World = library, Package = new AnimationPackage { Prefix = new byte[72], Tail = [] } };
        SceneViewport? replacement = new();
        try
        {
            await replacement.ShowAnimationAsync(context, next.At(seconds, nextLod, ct), resolver, false, ct, previewLifetime: lifetime);
            ct.ThrowIfCancellationRequested(); if (disposed || request != generation) return;
            var previous = Viewport; Viewport = replacement; replacement = null;
            root.Children.Remove(previous); root.Children.Add(Viewport); previous.Dispose();
            if (selectedMember != index) selectedIdentity = null;
            selectedMember = index; clip = nextClip; sampler = next;
            syncing = true; assembly.SelectedItem = asset; lod.ItemsSource = choices; lod.SelectedIndex = nextLod; syncing = false;
            support.Text = string.Join("\n", next.Diagnostics.Take(4)); Present(); SceneChanged?.Invoke(); if (playing) Play();
        }
        catch
        {
            if (!disposed && request == generation)
            {
                syncing = true; assembly.SelectedItem = assembly.Items.OfType<AssetRecord>().FirstOrDefault(a => a.Index == selectedMember); syncing = false;
                if (playing) Play();
            }
            throw;
        }
        finally { replacement?.Dispose(); }
    }
    public async Task RefreshLibraryAsync(ResourceEditSession? edits)
    {
        if (disposed || library == null) return;
        int? target = selectedMember; bool isLibrary = edits != null && (edits.Current.Document.Path.Equals(library.Path, StringComparison.OrdinalIgnoreCase) || edits.TargetPath.Equals(library.Path, StringComparison.OrdinalIgnoreCase));
        if (isLibrary && target is int oldIndex)
        {
            var old = library.Assets[oldIndex];
            var member = selectedIdentity is Guid id ? edits!.Current.Members.SingleOrDefault(m => m.Id == id) :
                edits!.Current.Members.SingleOrDefault(m => m.SourceIndex == oldIndex && edits.OriginalAsset(m) is { } original && original.Offset == old.Offset && original.Length == old.Length && original.Name == old.Name);
            selectedIdentity = member?.Id;
            target = member == null ? null : edits.Current.Members.ToList().IndexOf(member);
        }
        var nextLibrary = await MotionLibrary.LoadAsync(document.Path, resolver, lifetime);
        lifetime.ThrowIfCancellationRequested(); if (disposed) return;
        var previousLibrary = library; var view = Viewport.CaptureView();
        library = nextLibrary;
        try
        {
            syncing = true; assembly.ItemsSource = library.Assets.Where(a => a.Content is MechAssembly).ToArray(); syncing = false;
            if (target is int index && library.Assets.Any(a => a.Index == index && a.Content is MechAssembly))
            {
                var identity = selectedIdentity; await SelectAssemblyAsync(index, lifetime); selectedIdentity = identity; Viewport.RestoreView(view);
            }
            else
            {
                Pause(); sampler = null; selectedMember = null; selectedIdentity = null; Viewport.Clear(); SceneChanged?.Invoke();
                support.Text = "The bound mech member is no longer available. Choose an assembly.";
            }
        }
        catch { library = previousLibrary; syncing = true; assembly.ItemsSource = library.Assets.Where(a => a.Content is MechAssembly).ToArray(); assembly.SelectedItem = library.Assets.FirstOrDefault(a => a.Index == selectedMember); syncing = false; throw; }
    }
    public void RefreshClip()
    {
        if (disposed) return;
        clip = CurrentClip(); seconds = Math.Min(seconds, clip.LoopTime);
        if (library != null && assembly.SelectedItem is AssetRecord { Content: MechAssembly selected }) sampler = new(clip, library, selected);
        startedAt = seconds; elapsed.Restart(); Present();
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
    public void TogglePlayback() { if (IsPlaying) Pause(); else Play(); }
    public void Play() { if (sampler == null || disposed) return; startedAt = seconds; elapsed.Restart(); IsPlaying = true; play.Content = "⏸"; }
    public void Pause() { IsPlaying = false; elapsed.Stop(); play.Content = "▶"; }
    private void Present()
    {
        if (disposed || clip == null) return;
        if (sampler != null && !lifetime.IsCancellationRequested) Viewport.UpdateAnimationFrame(sampler.At(seconds, Lod, lifetime));
        syncing = true; seeker.Maximum = clip.LoopTime; seeker.Value = seconds; syncing = false;
        time.Text = $"{seconds:F3} / {clip.LoopTime:F3} s";
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; generation++; load?.Cancel(); load?.Dispose(); timer.Stop(); elapsed.Stop(); Viewport.Dispose(); GC.SuppressFinalize(this);
    }
}
