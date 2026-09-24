using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

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
    public event Action<Guid, Guid>? PropertiesRequested;
    public Func<bool>? ResolvePropertyDrafts { get; set; }
    public (Guid Sequence, Guid Event) PropertySelection => (selectedSequence, selectedEvent);
    private ProgramItem? propertyContextTarget;
    private bool pointerContext;
    private void PropertiesClick(object sender, RoutedEventArgs e)
    { if (propertyContextTarget is { } target) PropertiesRequested?.Invoke(target.Sequence, target.Event); }
    public event Action? SourceSelectionChanged;
    public event Action? CommandsChanged;
    public event Action? SetupRequested;
    public event Action<int,Guid,Guid>? SourceNavigationRequested;
    internal bool CanRunCommand(string command) => !disposed && command switch
    {
        "add-event" => Sequence?.IsEditable == true,
        "add-sequence" => Entry.Sequences.Count < 255 && Entry.AllSequences.All(s => s.IsEditable),
        "duplicate" => Event != null ? Sequence?.IsEditable == true : Sequence != null && Entry.AllSequences.All(s => s.IsEditable),
        "delete" => Event != null ? Sequence?.IsEditable == true : Sequence != null && Sequence != Entry.Primary && Entry.AllSequences.All(s => s.IsEditable),
        "up" => Event != null ? SelectedEventIndex > 0 && Sequence!.IsEditable : Entry.Sequences.FindIndex(s => s.Id == selectedSequence) > 0,
        "down" => Event != null ? SelectedEventIndex + 1 < Sequence!.Events.Count && Sequence.IsEditable : Entry.Sequences.FindIndex(s => s.Id == selectedSequence) is >= 0 and int index && index + 1 < Entry.Sequences.Count,
        "copy-event" => Event != null,
        _ => true
    };
    private readonly ContentControl referencesPage = new();
    private readonly ObservableCollection<ProgramItem> program = [];
    private readonly List<(Expander Group,bool Default)> setupGroups = [];
    private void InitializeWorkspaceViews()
    {
        // Each page has exactly one host. Tab/preset changes do not own preview lifetimes.
        DetachedPages.Children.Clear(); EditorRoot.Children.Remove(DetachedPages);
        ProgramTree.ItemsSource = program;
        ProgramTree.PreviewMouseRightButtonDown += (_, e) =>
        {
            propertyContextTarget = PropertyContext.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as ProgramItem;
            pointerContext = true; e.Handled = true;
            if (propertyContextTarget is { } item) SelectSource(item.Sequence, item.Event);
        };
        ProgramTree.ItemTemplate = (HierarchicalDataTemplate)Resources[new DataTemplateKey(typeof(ProgramItem))];
        ProgramTree.ContextMenu.Opened += (_,_) =>
        {
            if (!pointerContext) propertyContextTarget = ProgramTree.SelectedItem as ProgramItem;
            pointerContext = false;
            foreach (var item in ProgramTree.ContextMenu.Items.OfType<MenuItem>())
                item.IsEnabled = item.Tag is string command
                    ? propertyContextTarget is { } target && target.Sequence == selectedSequence && target.Event == selectedEvent && CanRunCommand(command)
                    : propertyContextTarget != null;
        };
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
            AddEventButton.IsEnabled = CanRunCommand("add-event"); AddSequenceButton.IsEnabled = CanRunCommand("add-sequence");
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
        AddEventButton.IsEnabled = CanRunCommand("add-event"); AddSequenceButton.IsEnabled = CanRunCommand("add-sequence");
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
    public void AddSequence() => AddSequenceClick(this, new());
    public void DuplicateRecord() { if (Event != null) CopyEventClick(this, new()); else if (Sequence != null) CopySequenceClick(this, new()); }
    public void DeleteRecord() { if (Event != null) DeleteEventClick(this, new()); else if (Sequence != null) DeleteSequenceClick(this, new()); }
    private void DuplicateRecordClick(object sender, RoutedEventArgs e) => DuplicateRecord();
    private void DeleteRecordClick(object sender, RoutedEventArgs e) => DeleteRecord();
    private void MoveRecordUpClick(object sender, RoutedEventArgs e) => MoveRecord(-1);
    private void MoveRecordDownClick(object sender, RoutedEventArgs e) => MoveRecord(1);
    private void MoveRecord(int direction) { if (Event != null) MoveEvent(direction); else if (Sequence != null) TryEdit(() => edits.MoveSequence(entryIndex, selectedSequence, direction)); }
    public void CopySelectedEvent()
    {
        if (!ResolvePendingDrafts()) return;
        if (Event is { } ev) Clipboard.SetText(ev.ToJson().ToJsonString(JsonData.Options));
        else Note("Select an event in Sequences to copy its JSON.");
    }
    private void ChooseEventClick(object sender, RoutedEventArgs e) => ChooseEvent();
    public void ChooseEvent()
    {
        if (!ResolvePendingDrafts()) return;
        if (Sequence == null) { Note("Select a sequence or event in Sequences to choose the insertion destination."); return; }
        if (!Sequence.IsEditable) { Note("This sequence has an unsupported tail and cannot accept new events."); return; }
        Guid destination = selectedSequence;
        StackPanel header = new() { Margin = new(8) };
        header.Children.Add(new TextBlock { Text = $"{Sequence.Name}: {(Event == null ? "append to sequence" : "insert after " + Event.Name)}", TextWrapping = TextWrapping.Wrap });
        TextBox query = new() { Margin = new(0,8,0,4), ToolTip = "Search event names, IDs and categories" }; header.Children.Add(query);
        ComboBox category = new() { ItemsSource = new[] { "All" }.Concat(AnimationCatalog.Events.Select(e => AnimationFieldPresentation.Category(e.Type)).Distinct()), SelectedIndex = 0 }; header.Children.Add(category);
        ListBox list = new() { Margin = new(8), DisplayMemberPath = nameof(EventPickerItem.Label) };
        void Filter() => list.ItemsSource = AnimationCatalog.Events.Where(e => (category.SelectedIndex == 0 || AnimationFieldPresentation.Category(e.Type) == category.SelectedItem?.ToString()) && (e.Name + $" 0x{e.Type:X2} " + AnimationFieldPresentation.Category(e.Type)).Contains(query.Text, StringComparison.OrdinalIgnoreCase)).Select(e => new EventPickerItem(e, $"{e.Name} · 0x{e.Type:X2} · {AnimationFieldPresentation.Category(e.Type)}")).ToArray();
        query.TextChanged += (_, _) => Filter(); category.SelectionChanged += (_, _) => Filter(); Filter();
        Button add = new() { Content = "Insert event", Margin = new(8), IsDefault = true, IsEnabled = false };
        list.SelectionChanged += (_, _) => add.IsEnabled = list.SelectedItem != null;
        DockPanel panel = new(); DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(add, Dock.Bottom); panel.Children.Add(header); panel.Children.Add(add); panel.Children.Add(list);
        Window dialog = new() { Owner = Window.GetWindow(this), Title = "Add event", Width = 610, Height = 540, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        add.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() == true && !disposed && destination == selectedSequence && list.SelectedItem is EventPickerItem picked) InsertEvent(picked.Spec);
    }
    private sealed record EventPickerItem(AnimationEventSpec Spec, string Label);
    internal (ReadOnlyMemory<byte> Bytes,long Offset,long Length,string Scope) SourceByteSelection()
    {
        // Asset content retains the original records; edit sessions replace entry snapshots.
        var original = document.Document.Assets.FirstOrDefault(a => a.Kind == AssetKind.Animation && a.Index == entryIndex)?.Content as AnimationEntry;
        var sequence = original?.AllSequences.FirstOrDefault(s => s.Id == selectedSequence);
        var ev = sequence?.Events.FirstOrDefault(e => e.Id == selectedEvent);
        long offset = selectedEvent != Guid.Empty ? ev?.SourceOffset ?? -1 : selectedSequence != Guid.Empty ? sequence?.SourceOffset ?? -1 : original?.SourceOffset ?? -1;
        long length = selectedEvent != Guid.Empty ? ev?.Bytes.Length ?? 0 : selectedSequence != Guid.Empty ? sequence == null ? 0 : 64L + sequence.I32(60) : original?.SourceLength ?? 0;
        string owner = $"Entry #{entryIndex} · {Entry.Name}";
        if (Sequence != null) owner += $" / Sequence {Sequence.Name}";
        if (Event != null) owner += $" / Event #{SelectedEventIndex} · {Event.Name}";
        if (selectedSequence != Guid.Empty) owner += $"\nSequence ID: {selectedSequence}";
        if (selectedEvent != Guid.Empty) owner += $"\nEvent ID: {selectedEvent}";
        if (offset >= 0 && length >= 0 && offset <= document.Document.Bytes.Length && length <= document.Document.Bytes.Length - offset)
            return (document.Document.Bytes.Slice((int)offset,(int)Math.Min(length,4096)),offset,length,"Original source bytes · " + owner + "\nPending edits are shown in Properties, not in this source-byte view.");
        return (ReadOnlyMemory<byte>.Empty,-1,0,"New record · " + owner + "\nThis record has no original source range. Copy its JSON to inspect current serialized fields.");
    }
    internal void ShowInspectorReplacement(bool show) => ViewportArea.Visibility = show ? Visibility.Hidden : Visibility.Visible;
    internal FrameworkElement ViewportRegion => ViewportArea;
}
