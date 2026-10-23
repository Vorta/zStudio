using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

internal sealed partial class SourceSectionCard
{
    internal Func<int, IReadOnlyList<SourceAnimationEdits.Change>, Task>? ApplyAnimation { get; set; }
    private SourceAnimationEdits.Definition? animationDefinition;
    private readonly Dictionary<string, SourceAnimationEdits.Change> animationChanges = [];
    private StackPanel? animationFieldsHost;
    private StackPanel? animationActivationHost;
    private StackPanel? animationSequenceHost;
    private string? animationSequence;
    private long animationRevision = -1;
    private StackPanel? animationPathsHost;
    private Expander? animationSetup;
    private ComboBox? animationSequencePicker;
    private ListBox? animationEventList;
    internal void SetExternalAnimationDraft(bool active)
    {
        if (animationDefinition == null) return;
        footer.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        foreach (var section in body.Children.OfType<CardSection>()) section.IsEnabled = !active || section.Title == "Drop windows";
    }
    private static TextBlock AnimationHelp(string text) { var block = Help(text); block.Margin = new(0, 4, 0, 6); return block; }
    internal bool ShowsAnimation(DocumentModel document, DefinitionEntry entry) => Document == document && Entry?.Key == entry.Key && animationDefinition != null && animationRevision == document.Revision;
    internal void SetAnimationSetup(FrameworkElement? setup, bool expand = false)
    {
        if (animationPathsHost == null) return;
        if (animationSetup != null) { animationSetup.Content = null; animationPathsHost.Children.Remove(animationSetup); animationSetup = null; }
        if (setup != null) { animationSetup = new() { Header = "Preview setup", Content = setup, IsExpanded = expand, MaxHeight = 480 }; animationPathsHost.Children.Add(animationSetup); }
    }
    private string? animationRecord;
    internal object? AnimationDraft => animationDefinition == null ? null : new { definition = animationDefinition.Index, file = animationDefinition.File, ordinal = animationDefinition.Ordinal, missions = animationDefinition.Missions, sequence = animationSequence, record = animationRecord, token = DraftToken, changes = animationChanges.Values.ToArray(), pending = HasDraft };
    internal void SelectAnimationRecord(string id)
    {
        var record = animationDefinition?.Records.FirstOrDefault(r => r.Id == id) ?? throw new InvalidOperationException("The animation record is unavailable.");
        var sequence = animationDefinition!.Records.FirstOrDefault(r => r.Kind == "sequence" && (r.Id == id || id.StartsWith(r.Id + "/event/", StringComparison.Ordinal)))
            ?? throw new InvalidOperationException("Choose a sequence or event record.");
        animationSequencePicker!.SelectedItem = sequence;
        if (record != sequence) animationEventList!.SelectedItem = record;
    }

    internal void ShowAnimation(DocumentModel document, DefinitionEntry entry, SourceAnimationEdits.Definition definition, FrameworkElement paths, FrameworkElement drops)
    {
        var kept = Entry?.Key == entry.Key && Document == document ? animationChanges.Values.ToArray() : [];
        string? selected = animationRecord;
        Begin(entry, document); animationDefinition = definition; animationRevision = document.Revision;
        foreach (var change in kept) animationChanges[change.Field] = change;
        heading.Text = definition.Name; subtitle.Text = "Animation · " + System.IO.Path.GetFileName(definition.File);
        subtitle.ToolTip = definition.File;
        var dropSection = Section("Drop windows");
        dropSection.Body.Children.Add(drops);
        dropSection.SetBinding(VisibilityProperty, new System.Windows.Data.Binding("Visibility") { Source = drops });
        var events = Section("Events");
        var sequences = definition.Records.Where(r => r.Kind == "sequence").ToArray();
        ComboBox sequence = new() { ItemsSource = sequences, DisplayMemberPath = "Name", Margin = new(0, 0, 0, 6) };
        AutomationProperties.SetName(sequence, "Animation sequence");
        ListBox list = new() { DisplayMemberPath = "Name", MaxHeight = 210, Margin = new(0, 0, 0, 8) };
        animationSequencePicker = sequence; animationEventList = list;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(list, "Animation events");
        events.Body.Children.Add(sequence);
        animationSequenceHost = new(); events.Body.Children.Add(new Expander { Header = "Sequence settings", Content = animationSequenceHost, IsExpanded = false, Margin = new(0, 0, 0, 6) });
        events.Body.Children.Add(list);
        animationFieldsHost = new(); events.Body.Children.Add(animationFieldsHost);
        list.SelectionChanged += (_, _) => { animationRecord = (list.SelectedItem as SourceAnimationEdits.Record)?.Id; RefreshAnimationFields(); };
        sequence.SelectionChanged += (_, _) =>
        {
            string? id = (sequence.SelectedItem as SourceAnimationEdits.Record)?.Id;
            animationSequence = id;
            list.ItemsSource = definition.Records.Where(r => id != null && r.Id.StartsWith(id + "/event/", StringComparison.Ordinal)).ToArray();
            list.SelectedItem = list.Items.Cast<SourceAnimationEdits.Record>().FirstOrDefault(r => r.Id == selected) ?? list.Items.Cast<SourceAnimationEdits.Record>().FirstOrDefault();
        };
        sequence.SelectedItem = sequences.FirstOrDefault(s => selected?.StartsWith(s.Id + "/", StringComparison.Ordinal) == true) ?? sequences.FirstOrDefault();
        var pathSection = Section("Paths"); pathSection.Body.Children.Add(paths); animationPathsHost = pathSection.Body;
        var activation = Section("Activation and prerequisites", false);
        animationActivationHost = activation.Body;
        RefreshAnimationFields();
        var binding = Section("Bindings", false);
        binding.Body.Children.Add(AnimationHelp($"Root: {entry.Definition.RootName}\n{entry.Definition.Entries.Count} compiled bindings. Edits apply to every use of this source definition.\nAffected maps: {string.Join(", ", definition.Missions)}"));
        var details = Section("Details", false); details.Body.Children.Add(AnimationHelp(definition.File + $"\nDefinition {definition.Ordinal + 1}"));
        Finish(editable: true);
    }

    private void RefreshAnimationFields()
    {
        if (animationFieldsHost == null || animationDefinition == null) return;
        animationActivationHost?.Children.Clear();
        if (animationActivationHost != null)
            foreach (var item in animationDefinition.Records.Where(r => r.Kind is "settings" or "prerequisites"))
            {
                if (item.Kind == "prerequisites") animationActivationHost.Children.Add(AnimationHelp(item.Name));
                foreach (var field in item.Fields) AnimationField(animationActivationHost, field);
            }
        animationSequenceHost?.Children.Clear();
        if (animationSequenceHost != null && animationDefinition.Records.FirstOrDefault(r => r.Id == animationSequence) is { } sequence)
            foreach (var field in sequence.Fields.Where(f => f.Key != "NAME")) AnimationField(animationSequenceHost, field);
        animationFieldsHost.Children.Clear();
        if (animationDefinition.Records.FirstOrDefault(r => r.Id == animationRecord) is not { } record) return;
        animationFieldsHost.Children.Add(new TextBlock { Text = record.Name, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) });
        animationFieldsHost.ToolTip = record.Kind;
        foreach (var field in record.Fields) AnimationField(animationFieldsHost, field);
    }

    private void AnimationField(StackPanel panel, SourceAnimationEdits.Field field)
    {
        var changed = animationChanges.GetValueOrDefault(field.Id);
        bool present = changed == null ? field.Present : changed.Values != null;
        string[] values = changed?.Values?.ToArray() ?? field.Values.ToArray();
        if (field.Key is "ANIMATION_LIST" or "OBJECT_ACTIVE_LIST")
        {
            panel.Children.Add(new TextBlock { Text = field.Label, Margin = new(0, 4, 0, 4) });
            var list = new TextBox { Text = string.Join(Environment.NewLine, values), AcceptsReturn = true, MinHeight = 60, MaxHeight = 160, MaxLength = 65536, IsEnabled = field.ReadOnly == null, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetName(list, field.Label);
            list.TextChanged += (_, _) =>
            {
                string[] next = list.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
                if (next.SequenceEqual(field.Values)) animationChanges.Remove(field.Id); else animationChanges[field.Id] = new(field.Id, next);
                Changed();
            };
            panel.Children.Add(list); panel.Children.Add(AnimationHelp("One name per line, in source order.")); return;
        }
        StackPanel line = new() { Margin = new(0, 4, 0, 6) };
        CheckBox? enabled = null;
        if (field.Optional)
        {
            enabled = new() { Content = field.Label, IsChecked = present, IsEnabled = field.ReadOnly == null, Margin = new(0, 0, 0, 4) };
            AutomationProperties.SetName(enabled, "Use " + field.Label); line.Children.Add(enabled);
        }
        else line.Children.Add(new TextBlock { Text = field.Label, Margin = new(0, 0, 0, 4) });
        List<Control> controls = [];
        void Update()
        {
            if (Applying) return;
            bool on = enabled?.IsChecked ?? true;
            for (int i = 0; i < controls.Count; i++)
            {
                controls[i].IsEnabled = on && field.ReadOnly == null;
                values[i] = controls[i] is ComboBox combo ? (string?)combo.SelectedItem ?? "" : ((TextBox)controls[i]).Text;
            }
            if (on == field.Present && values.SequenceEqual(field.Values)) animationChanges.Remove(field.Id);
            else animationChanges[field.Id] = new(field.Id, on ? values.ToArray() : null);
            Changed();
        }
        for (int i = 0; i < values.Length; i++)
        {
            Control control;
            if (i == 0 && field.Choices.Length > 0)
            {
                ComboBox box = new() { ItemsSource = field.Choices.Contains(values[i]) ? field.Choices : [values[i], .. field.Choices], SelectedItem = values[i] };
                box.SelectionChanged += (_, _) => Update(); control = box;
            }
            else
            {
                TextBox box = new() { Text = values[i], MaxLength = 512 };
                box.TextChanged += (_, _) => Update(); control = box;
            }
            control.IsEnabled = (enabled?.IsChecked ?? true) && field.ReadOnly == null;
            control.Margin = new(0, 0, 0, 3); control.MinHeight = 30;
            AutomationProperties.SetName(control, field.Label + (values.Length > 1 ? $" {i + 1}" : ""));
            controls.Add(control); line.Children.Add(control);
        }
        if (enabled != null) { enabled.Checked += (_, _) => Update(); enabled.Unchecked += (_, _) => Update(); }
        if (field.ReadOnly != null) line.Children.Add(AnimationHelp(field.ReadOnly));
        if (field.Key == "START_TIME") line.Children.Add(AnimationHelp("Stored clock threshold; earlier events, waits and conditions can delay dispatch."));
        panel.Children.Add(line);
    }

    internal void SetAnimationChanges(IReadOnlyList<SourceAnimationEdits.Change> changes)
    {
        if (animationDefinition == null || Applying) throw new InvalidOperationException("Select an animation definition first.");
        SourceAnimationEdits.ValidateChanges(animationDefinition, changes);
        if (animationChanges.Keys.Concat(changes.Select(c => c.Field)).Distinct().Count() > 256) throw new InvalidOperationException("Apply the current changes before editing more fields.");
        foreach (var change in changes)
        {
            var field = animationDefinition.Records.SelectMany(r => r.Fields).Single(f => f.Id == change.Field);
            if (change.Values == null && !field.Present || change.Values != null && field.Present && change.Values.SequenceEqual(field.Values)) animationChanges.Remove(change.Field);
            else animationChanges[change.Field] = change;
        }
        RefreshAnimationFields(); Changed();
    }
}
