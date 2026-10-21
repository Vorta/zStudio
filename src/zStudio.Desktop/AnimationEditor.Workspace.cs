using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using zStudio.Core;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

public partial class AnimationEditor
{
    public FrameworkElement ProgramView => ProgramPage;
    public FrameworkElement PreviewSetupView => SetupPage;
    public FrameworkElement ReferencesView => referencesPage;
    public FrameworkElement DispatchView => DispatchedEventsPanel;
    public FrameworkElement EventLogView => LogPage;
    public FrameworkElement RuntimeView => RuntimePage;
    public FrameworkElement ProblemsView => ProblemPage;
    public long SelectedSourceOffset => Event?.SourceOffset ?? Sequence?.SourceOffset ?? Entry.SourceOffset;
    public (Guid Sequence, Guid Event) PropertySelection => (selectedSequence, selectedEvent);
    public event Action? SourceSelectionChanged;
    internal event Action? SceneHierarchyChanged;
    private GameScene? publishedHierarchy;
    public event Action? SetupRequested;
    public event Action<int,Guid,Guid>? SourceNavigationRequested;
    private readonly ContentControl referencesPage = new();
    private readonly ObservableCollection<ProgramItem> program = [];
    private readonly List<(Expander Group,bool Default)> setupGroups = [];
    private void InitializeWorkspaceViews()
    {
        // Each page has exactly one host. Tab/preset changes do not own preview lifetimes.
        DetachedPages.Children.Clear(); EditorRoot.Children.Remove(DetachedPages);
        ProgramTree.ItemsSource = program;
        ProgramTree.ItemTemplate = (HierarchicalDataTemplate)Resources[new DataTemplateKey(typeof(ProgramItem))];
        foreach (var group in ((StackPanel)SetupPage.Content).Children.OfType<Expander>())
        {
            string key = "setup/" + group.Header; setupGroups.Add((group,group.IsExpanded));
            var groups = preferences?.Settings.GetWorkspace().Groups;
            bool fallback = group.Header as string == "Activation points" ? groups?.GetValueOrDefault("setup/Placement and ground", group.IsExpanded) ?? group.IsExpanded : group.IsExpanded;
            group.IsExpanded = groups?.GetValueOrDefault(key, fallback) ?? fallback;
            group.Expanded += (_,_) => { if (preferences != null) preferences.Settings.GetWorkspace().Groups[key] = true; };
            group.Collapsed += (_,_) => { if (preferences != null) preferences.Settings.GetWorkspace().Groups[key] = false; };
        }
        InitializeActivationForm(); RefreshReferences();
    }
    internal void ResetLayout() { foreach (var item in setupGroups) item.Group.IsExpanded = item.Default; }
    private void RefreshProgram()
    {
        changing = true;
        try
        {
            if (program.Count == 0) program.Add(new() { IsExpanded = true, IsSelected = true });
            var root = program[0]; root.Label = Entry.Name + $" · #{entryIndex}"; root.Detail = $"{Entry.Sequences.Count} concurrent runtime sequence{(Entry.Sequences.Count == 1 ? "" : "s")}"; root.Description = $"Entry #{entryIndex} · source 0x{Entry.SourceOffset:X}";
            var sequences = Entry.AllSequences.ToArray();
            Timeline.SequenceLabels = sequences.Select((s,i) => (s.Id, Label: i == 0 ? "Cleanup" : $"{i - 1}: {s.Name}")).ToDictionary(s => s.Id,s => s.Label);
            foreach (var removed in root.Children.Where(p => !sequences.Any(s => s.Id == p.Sequence)).ToArray()) root.Children.Remove(removed);
            for (int i = 0; i < sequences.Length; i++)
            {
                var sequence = sequences[i]; var item = root.Children.FirstOrDefault(p => p.Sequence == sequence.Id);
                if (item == null) { item = new() { Sequence = sequence.Id, IsExpanded = sequences.Length <= 4 }; root.Children.Insert(i, item); }
                else if (root.Children.IndexOf(item) != i) root.Children.Move(root.Children.IndexOf(item), i);
                item.Label = sequence == Entry.Primary ? "Cleanup · protected" : $"{i - 1}: {sequence.Name}";
                item.Detail = $"{sequence.Events.Count} events · Initial: {AnimationEventPresentation.ResetState(sequence.ResetMode)}";
                item.Description = (sequence == Entry.Primary ? "Cleanup instructions, separate from the concurrent runtime sequences. Protected from deletion; selecting this record does not change the preview phase." : "Events execute in list order within this sequence. Runtime sequences execute concurrently.")
                    + $"\nStored initial / reset state: {sequence.ResetMode}\n{sequence.Id} · source 0x{sequence.SourceOffset:X}";
                foreach (var removed in item.Children.Where(p => !sequence.Events.Any(ev => ev.Id == p.Event)).ToArray()) item.Children.Remove(removed);
                for (int j = 0; j < sequence.Events.Count; j++)
                {
                    var ev = sequence.Events[j]; var eventItem = item.Children.FirstOrDefault(p => p.Event == ev.Id);
                    if (eventItem == null) { eventItem = new() { Sequence = sequence.Id, Event = ev.Id }; item.Children.Insert(j, eventItem); }
                    else if (item.Children.IndexOf(eventItem) != j) item.Children.Move(item.Children.IndexOf(eventItem), j);
                    var presentation = AnimationEventPresentation.Create(Entry, ev);
                    eventItem.Label = $"{j}: {ev.Name}";
                    eventItem.Summary = presentation.Summary;
                    eventItem.Detail = presentation.Timing;
                    eventItem.Description = $"{presentation.Description}\n\n{ev.Id} · source 0x{ev.SourceOffset:X}";
                }
            }
            if (selectedSequence != Guid.Empty && Sequence == null) { selectedSequence = selectedEvent = Guid.Empty;  }
            if (selectedEvent != Guid.Empty && Event == null) { selectedEvent = Guid.Empty;  }
            SyncProgramSelection();
        }
        finally { changing = false; }
    }
    private IEnumerable<ProgramItem> ProgramItems() => program.SelectMany(p => new[] { p }.Concat(p.Children.SelectMany(s => new[] { s }.Concat(s.Children))));
    private void SyncProgramSelection()
    {
        foreach (var item in ProgramItems()) item.IsSelected = item.Sequence == selectedSequence && item.Event == selectedEvent;
    }
    private void ProgramSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (changing || !ready || e.NewValue is not ProgramItem item) return;
        // Reopening the tab realizes the retained selected TreeViewItem. That is
        // presentation work, not a new source selection or a reason to leave Program.
        if (item.Sequence == selectedSequence && item.Event == selectedEvent) return;
        if (!ResolvePendingDrafts()) { changing = true; SyncProgramSelection(); changing = false; return; }
        SelectSource(item.Sequence, item.Event);
    }
    internal void SelectSource(Guid sequence, Guid ev = default)
    {
        if (!ResolvePendingDrafts()) return;
        selectedSequence = sequence; selectedEvent = ev;
        if (program.Count > 0) { program[0].IsExpanded = true; foreach (var parent in program[0].Children.Where(p => p.Sequence == sequence)) parent.IsExpanded = true; }
        changing = true; SyncProgramSelection(); changing = false;
        RefreshProperties(); SourceSelectionChanged?.Invoke();
        // Only explicit authored selection scrolls Program; playback cursors never do.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (disposed || selectedSequence != sequence || selectedEvent != ev) return;
            ItemsControl parent = ProgramTree;
            foreach (var item in new[] { program.FirstOrDefault(), program.FirstOrDefault()?.Children.FirstOrDefault(p => p.Sequence == sequence), program.FirstOrDefault()?.Children.FirstOrDefault(p => p.Sequence == sequence)?.Children.FirstOrDefault(p => p.Event == ev) })
            {
                if (item == null) break;
                parent.UpdateLayout();
                if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) break;
                container.BringIntoView(); parent = container;
            }
        });
    }
    private void SetupClick(object sender, RoutedEventArgs e) => SetupRequested?.Invoke();
    public void Stop() { if (ResolvePendingDrafts()) { pendingPlay = false; _ = SeekAsync(0); } }
    public void Step(int direction) { if (ResolvePendingDrafts()) _ = SeekAsync(Math.Max(0, (frame?.Time ?? 0) + direction * AnimationPlayer.StepSeconds)); }
    public void CopySelectedEvent()
    {
        if (!ResolvePendingDrafts()) return;
        if (Event is { } ev) Clipboard.SetText(ev.ToJson().ToJsonString(JsonData.Options));
        else Note("Select an event in Sequences to copy its JSON.");
    }
    internal (ReadOnlyMemory<byte> Bytes,long Offset,long Length,string Scope) SourceByteSelection()
    {
        var original = document.Document.Assets.FirstOrDefault(a => a.Kind == AssetKind.Animation && a.Index == entryIndex)?.Content as AnimationEntry;
        var sequence = original?.AllSequences.FirstOrDefault(s => s.Id == selectedSequence);
        var ev = sequence?.Events.FirstOrDefault(e => e.Id == selectedEvent);
        long offset = selectedEvent != Guid.Empty ? ev?.SourceOffset ?? -1 : selectedSequence != Guid.Empty ? sequence?.SourceOffset ?? -1 : original?.SourceOffset ?? -1;
        long length = selectedEvent != Guid.Empty ? ev?.Bytes.Length ?? 0 : selectedSequence != Guid.Empty ? sequence == null ? 0 : 64L + sequence.I32(60) : original?.SourceLength ?? 0;
        string owner = $"Entry #{entryIndex} · {Entry.Name}";
        if (Sequence != null) owner += $" / Sequence {Sequence.Name}";
        if (Event != null) owner += $" / Event #{Sequence!.Events.FindIndex(e => e.Id == selectedEvent)} · {Event.Name}";
        if (selectedSequence != Guid.Empty) owner += $"\nSequence ID: {selectedSequence}";
        if (selectedEvent != Guid.Empty) owner += $"\nEvent ID: {selectedEvent}";
        if (offset >= 0 && length >= 0 && offset <= document.Document.Bytes.Length && length <= document.Document.Bytes.Length - offset)
            return (document.Document.Bytes.Slice((int)offset,(int)Math.Min(length,4096)),offset,length,"Original source bytes · " + owner);
        return (ReadOnlyMemory<byte>.Empty,-1,0,"No source range · " + owner);
    }
    private bool inspectorReplacement;
    internal bool PresentationUiHidden { get; private set; }
    internal void SetPresentationUiHidden(bool hidden)
    {
        PresentationUiHidden = hidden;
        ViewOptionsRow.Visibility = PlayerTransport.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        ViewportArea.Visibility = !hidden && inspectorReplacement ? Visibility.Hidden : Visibility.Visible;
    }
    internal void ShowInspectorReplacement(bool show)
    {
        inspectorReplacement = show;
        ViewportArea.Visibility = !PresentationUiHidden && show ? Visibility.Hidden : Visibility.Visible;
    }
    internal FrameworkElement ViewportRegion => ViewportArea;
}
