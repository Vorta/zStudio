using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

public sealed partial class AnimationPropertiesEditor : FieldEditor, IDisposable
{
    private readonly AnimationEditSession edits;
    private readonly MainViewModel? preferences;
    private readonly int entryIndex;
    private readonly Guid selectedSequence, selectedEvent;
    private readonly ContentControl fields = new();
    private readonly List<(Expander Group, bool Default)> propertyGroups = [];
    private string fieldsShape = "";
    private AnimationEntry Entry => edits.Package.Entries[entryIndex];
    private AnimationSequence? Sequence => Entry.AllSequences.FirstOrDefault(s => s.Id == selectedSequence);
    private AnimationEvent? Event => Sequence?.Events.FirstOrDefault(e => e.Id == selectedEvent);
    private int SelectedEventIndex => Sequence?.Events.FindIndex(e => e.Id == selectedEvent) ?? -1;
    public bool TargetAvailable => selectedSequence == Guid.Empty || Sequence != null && (selectedEvent == Guid.Empty || Event != null);
    public event Action? Changed;
    public event Action? Editing;
    public string TargetLabel => Entry.Name + $" · #{entryIndex}" + (selectedSequence == Guid.Empty ? "" : " → " + (Sequence == null ? selectedSequence.ToString() : Sequence == Entry.Primary ? "Cleanup" : Sequence.Name)) + (selectedEvent == Guid.Empty ? "" : $" → {SelectedEventIndex}: {Event?.Name ?? selectedEvent.ToString()}");
    public JsonObject Json => !TargetAvailable ? new JsonObject { ["unavailable"] = true } : Event?.ToJson() ?? (Sequence is { } sequence ? new JsonObject { ["name"] = sequence.Name, ["id"] = sequence.Id.ToString(), ["resetState"] = sequence.ResetMode, ["eventCount"] = sequence.Events.Count, ["sourceOffset"] = sequence.SourceOffset, ["headerHex"] = Convert.ToHexString(sequence.Bytes), ["opaqueTailHex"] = Convert.ToHexString(sequence.OpaqueTail), ["events"] = new JsonArray(sequence.Events.Select(e => (JsonNode)e.ToJson()).ToArray()) } : Entry.ToJson());
    public AnimationPropertiesEditor(DocumentModel document, int entry, Guid sequence, Guid ev, MainViewModel? preferences = null)
    {
        edits = document.AnimationEdits!; entryIndex = entry;
        selectedSequence = sequence; selectedEvent = ev; this.preferences = preferences;
        Content = fields; edits.Changed += RefreshProperties; RefreshProperties();
    }
    private void TryEdit(Action action)
    {
        if (disposed || !TargetAvailable || (!committingDraft && !ResolvePendingDrafts())) return;
        try { Editing?.Invoke(); action(); }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        {
            if (committingDraft) throw;
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Property edit", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    private void ChangeEvents(string description, Action<List<AnimationEvent>> change) => TryEdit(() => edits.Apply(entryIndex, description, e =>
    {
        var sequence = AnimationEditSession.FindSequence(e, selectedSequence); AnimationEditSession.EnsureEditable(sequence); change(sequence.Events);
    }));
    private void MoveRecord(int direction) => TryEdit(() => edits.MoveSequence(entryIndex, selectedSequence, direction));
    public void Dispose() { if (disposed) return; disposed = true; edits.Changed -= RefreshProperties; draftInputs.Clear(); Content = null; GC.SuppressFinalize(this); }
    protected override void RefreshProperties()
    {
        if (disposed || refreshingFields || committingDraft) return;
        if (!TargetAvailable)
        {
            fieldsShape = "unavailable"; valueRefresh.Clear(); draftInputs.Clear();
            fields.Content = new TextBlock { Text = "This record is no longer available. Undo its deletion to restore it.", TextWrapping = TextWrapping.Wrap, Margin = new(16) };
            Changed?.Invoke(); return;
        }
        string shape = $"{selectedSequence}/{selectedEvent}/{Event?.Type}/{Event?.Bytes.Length}/{(Event?.Type is 30 or 31 or 33 ? Event.U32(12) : 0)}/{selectedSegment}/{KeyframeShape()}";
        if (shape == fieldsShape) { foreach (var refresh in valueRefresh.ToArray()) refresh(); Changed?.Invoke(); return; }
        refreshingFields = true;
        try
        {
            fieldsShape = shape; draftInputs.RemoveAll(d => d.Scope == "properties"); valueRefresh.Clear(); propertyGroups.Clear(); inputScope = "properties";
            StackPanel panel = new() { Margin = new(10) };
            fields.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            if (Event is { } ev)
            {
                Label(panel, ev.Spec?.Support ?? "Unknown event: preserved read-only");
                bool editable = Sequence!.IsEditable && ev.Spec != null && ev.Bytes.Length >= ev.Spec.Size;
                var scheduling = Group(panel, "Scheduling");
                var modes = Enumerable.Range(1, 3).Select(i => new ChoiceValue(i, AnimationCatalog.ModeName(i))).ToList();
                if (!modes.Any(v => v.Value == ev.StartMode)) modes.Add(new(ev.StartMode, AnimationCatalog.ModeName(ev.StartMode)));
                Choice(scheduling, "Start clock", modes, ev.StartMode, value => ChangeEvent("Change start clock", record => record.StartMode = (byte)value), !editable, () => Event?.StartMode ?? 0);
                Input(scheduling, "Threshold (s)", ev.Threshold.ToEditorText(), text => ChangeEvent("Change threshold", record => record.Threshold = float.Parse(text, CultureInfo.InvariantCulture)), !editable, getter: () => Event!.Threshold.ToEditorText());
                Label(scheduling, "Events execute in authored order. Thresholds use the selected clock; they do not reorder instructions.");
                if (ev.Spec != null)
                {
                    CheckBox showAll = new() { Content = "Show all stored fields", Margin = new(0,4,0,6), ToolTip = "Expand inactive parameter groups without changing their values" }; panel.Children.Add(showAll);
                    foreach (var grouping in ev.Spec.Fields.Where(f => f.Offset + f.Size <= ev.Bytes.Length).GroupBy(f => AnimationFieldPresentation.Group(ev.Type, f)))
                    {
                        uint relevance = AnimationFieldPresentation.RelevanceBit(ev.Type, grouping.Key);
                        bool inactive = relevance != 0 && (ev.U32(12) & relevance) == 0;
                        bool nonzero = inactive && grouping.Any(f => ev.Bytes.AsSpan(f.Offset, f.Size).ContainsAnyExcept((byte)0));
                        var group = Group(panel, grouping.Key + (inactive ? nonzero ? " · inactive, stored values present" : " · inactive" : ""), grouping.Key != "Serialized state / provenance" && !inactive);
                        var expander = (Expander)group.Parent;
                        if (relevance != 0)
                        {
                            bool wasInactive = inactive;
                            showAll.Checked += (_,_) => expander.IsExpanded = true;
                            showAll.Unchecked += (_,_) => { if ((Event!.U32(12) & relevance) == 0) expander.IsExpanded = false; };
                            valueRefresh.Add(() =>
                            {
                                bool nowInactive = (Event!.U32(12) & relevance) == 0;
                                bool stored = nowInactive && grouping.Any(f => Event.Bytes.AsSpan(f.Offset,f.Size).ContainsAnyExcept((byte)0));
                                expander.Header = grouping.Key + (nowInactive ? stored ? " · inactive, stored values present" : " · inactive" : "");
                                if (wasInactive && !nowInactive) expander.IsExpanded = true;
                                wasInactive = nowInactive;
                            });
                        }
                        var members = grouping.ToArray();
                        if (ev.Type == 11 && members.Length == 3 && members.All(f => f.Kind == AnimationFieldKind.Vector)) { TransformTable(group,members,editable); continue; }
                        if (ev.Type == 11 && grouping.Key == "Morph") { MultiFields(group,"Morph",[members[0],members[2],members[1]],["Start","Rate","End"],editable); continue; }
                        for (int i = 0; i < members.Length; i++)
                        {
                            var field = members[i];
                            if (ev.Type == 10 && field.Offset is 32 or 40 or 48 or 56 && i + 1 < members.Length)
                            {
                                var pair = new[] { field, members[++i] };
                                MultiFields(group, field.Name.Replace(" minimum", ""), pair, ["Min", "Max"], editable); continue;
                            }
                            AddCatalogField(group, ev, field, editable);
                        }
                    }
                    if (ev.Type == 12 && editable)
                    {
                        try { Keyframes(Group(panel, "Keyframe segments"), ev.Keyframes()); }
                        catch (InvalidDataException ex) { Label(panel, ex.Message + " Keyframe payload remains read-only."); }
                    }
                }
                var provenance = Group(panel, "Record identity / provenance", false);
                ReadOnlyText(provenance, $"Event {ev.Id}\nType 0x{ev.Type:X2} · {ev.Bytes.Length} bytes\nSource offset 0x{ev.SourceOffset:X}");
            }
            else if (Sequence is { } sequence)
            {
                Label(panel, sequence == Entry.Primary ? "Cleanup sequence" : "Runtime sequence", true);
                Label(panel, sequence == Entry.Primary ? "Runs during cleanup; selecting this record does not change the preview phase." : "Runtime sequences execute concurrently.");
                Input(panel, "Name", sequence.Name, text => TryEdit(() => edits.RenameSequence(entryIndex, selectedSequence, text)), !sequence.IsEditable, getter: () => Sequence!.Name);
                var states = new List<ChoiceValue> { new(0,"Ready"),new(1,"Running"),new(2,"Stopped"),new(3,"Waiting for release") };
                if (!states.Any(v => v.Value == sequence.ResetMode)) states.Add(new(sequence.ResetMode, $"Unknown ({sequence.ResetMode})"));
                Choice(panel, "Initial / reset state", states, sequence.ResetMode, value => TryEdit(() => edits.Apply(entryIndex,"Change reset state", e => { var s = AnimationEditSession.FindSequence(e,selectedSequence); AnimationEditSession.EnsureEditable(s); s.ResetMode = (byte)value; })), !sequence.IsEditable, () => Sequence!.ResetMode);
                Label(panel, "Waiting sequences need Release waiting sequence. On completion, state 3 returns to waiting. This is stored state; see Runtime for current execution.");
                WrapPanel order = new(); panel.Children.Add(order); Button(order,"Move up", () => MoveRecord(-1)); Button(order,"Move down", () => MoveRecord(1));
                ReadOnlyText(Group(panel,"Serialized state / provenance",false), $"{sequence.Id}\n{sequence.Events.Count} events · source 0x{sequence.SourceOffset:X}\n{sequence.OpaqueTail.Length} opaque tail bytes");
            }
            else
            {
                Label(panel, "Animation entry", true);
                ReadOnlyText(panel, $"Name: {Entry.Name}\nRoot: {Entry.RootName}\nAttachment: {Entry.AttachName}");
                Input(panel,"Reset delay (s)",Entry.F32(164).ToEditorText(),text => TryEdit(() => edits.Apply(entryIndex,"Edit reset delay",e => e.SetFloat(164,float.Parse(text,CultureInfo.InvariantCulture)))),getter: () => Entry.F32(164).ToEditorText());
                Label(panel,"A negative reset delay disables automatic reset. Root and attachment are informational stored metadata; use Settings for a temporary root binding.");
                ReadOnlyText(Group(panel,"Serialized state / provenance",false),$"Entry #{entryIndex} · source 0x{Entry.SourceOffset:X}\n{Entry.Sequences.Count} runtime sequences + cleanup");
            }
            Changed?.Invoke();
        }
        finally { refreshingFields = false; }
    }
    private void AddCatalogField(StackPanel panel, AnimationEvent ev, AnimationField source, bool editable)
    {
        var field = source;
        if (ev.Type == 30 && field.Offset == 16)
        {
            bool timed = (ev.U32(12) & 1) == 0 && (ev.U32(12) & 2) != 0;
            field = field with { Name = timed ? "Loop duration (s; negative = infinite)" : "Loop count (65535 = infinite)", Kind = timed ? AnimationFieldKind.Float : AnimationFieldKind.Integer };
        }
        if (ev.Type is 31 or 33 && field.Offset == 20) field = field with { Name = (ev.U32(12) & 4) != 0 ? "Minimum effects level" : "Threshold", Kind = (ev.U32(12) & 4) != 0 ? AnimationFieldKind.Integer : AnimationFieldKind.Float };
        string Read() => field.FormatForEditor(Event!);
        void Write(string text) => TryEdit(() => edits.EditField(entryIndex,selectedSequence,selectedEvent,field,text));
        if (field.ReferenceTable >= 0 && !field.ReadOnly)
        {
            var choices = References(field.ReferenceTable); int index = field.Kind == AnimationFieldKind.Short ? ev.I16(field.Offset) : ev.I32(field.Offset);
            if (!choices.Any(c => c.Value == index)) choices.Add(new(index,$"{index}: unresolved"));
            Choice(panel,field.Name,choices,index,value => Write(value.ToString(CultureInfo.InvariantCulture)),!editable,() => field.Kind == AnimationFieldKind.Short ? Event!.I16(field.Offset) : Event!.I32(field.Offset),true); return;
        }
        if (field.Kind == AnimationFieldKind.Flags && editable)
        {
            System.Windows.Controls.Primitives.UniformGrid flags = new() { Columns = 2 }; panel.Children.Add(flags);
            foreach (var (bit,name) in AnimationFieldPresentation.Flags(ev.Type,field.Offset))
            {
                CheckBox box = new() { Content = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap }, IsChecked = (ev.U32(field.Offset) & bit) != 0, Margin = new(2,3,2,3), MinWidth = 0 };
                AutomationProperties.SetName(box,name); flags.Children.Add(box);
                box.Click += (_,_) => { if (!ResolvePendingDrafts()) { box.IsChecked = !box.IsChecked; return; } uint old = Event!.U32(field.Offset); Write($"0x{(box.IsChecked == true ? old | bit : old & ~bit):X8}"); };
                valueRefresh.Add(() => box.IsChecked = (Event!.U32(field.Offset) & bit) != 0);
            }
        }
        string? hint = field.Hint.Length > 0 ? field.Hint : $"{field.Kind} · byte {field.Offset}";
        if (field.Kind == AnimationFieldKind.Text && (ev.Type is 22 or 23 || ev.Type == 10 && field.Offset == 208))
            TextChoice(panel,field.Name,Read(),Entry.Sequences.Select(s => s.Name),Write,!editable,Read);
        else if (field.Kind == AnimationFieldKind.Text && ev.Type is 19 or 24 or 25 or 26 or 27)
            TextChoice(panel,field.Name,Read(),edits.Package.Entries.Where(e => e.Name.Length > 0).Select(e => e.Name),Write,!editable,Read);
        else Input(panel,field.Name,Read(),Write,!editable || field.ReadOnly,hint,Read,field.Kind == AnimationFieldKind.Vector ? AnimationFieldPresentation.Components(ev.Type,field) : null);
    }
    private void MultiFields(StackPanel panel,string label,AnimationField[] members,string[] components,bool editable)
    {
        string Read() => string.Join("|", members.Select(f => f.FormatForEditor(Event!)));
        Input(panel,label,Read(),text => TryEdit(() => edits.EditFields(entryIndex,selectedSequence,selectedEvent,"Edit " + label,members.Zip(text.Split('|'),(f,v) => (f,v)).ToArray())),!editable,getter:Read,components:components,separator:"|");
    }
    private void TransformTable(StackPanel panel,AnimationField[] members,bool editable)
    {
        // Catalog order is start/end/rate. No value or rate is calculated by the UI.
        AnimationField[] ordered = [members[0],members[2],members[1]];
        string Read() => string.Join("|",Enumerable.Range(0,3).SelectMany(axis => ordered.Select(f => Event!.F32(f.Offset + axis * 4).ToEditorText())));
        string[] captions = ["X · Start","X · Rate","X · End","Y · Start","Y · Rate","Y · End","Z · Start","Z · Rate","Z · End"];
        Input(panel,"Start / rate per second / end",Read(),text => TryEdit(() => edits.Apply(entryIndex,"Edit transform channel",entry =>
        {
            var sequence = AnimationEditSession.FindSequence(entry,selectedSequence); AnimationEditSession.EnsureEditable(sequence);
            var record = sequence.Events.Single(e => e.Id == selectedEvent); string[] values = text.Split('|');
            if (values.Length != 9) throw new InvalidDataException("Enter all nine channel components.");
            for (int axis = 0; axis < 3; axis++) for (int column = 0; column < 3; column++) record.SetFloat(ordered[column].Offset + axis * 4,float.Parse(values[axis * 3 + column],CultureInfo.InvariantCulture));
        })),!editable,getter:Read,components:captions,separator:"|",componentColumns:3);
        Label(panel,"Rate is stored independently; changing duration never recalculates this table.");
    }
    private void ChangeEvent(string description,Action<AnimationEvent> change) => ChangeEvents(description,list => change(list.Single(e => e.Id == selectedEvent)));
    private StackPanel Group(StackPanel panel,string name,bool expanded = true)
    {
        StackPanel contents = new() { Margin = new(2,4,2,8) };
        string key = $"{Event?.Type.ToString() ?? "record"}/{name}";
        var settings = preferences?.Settings.GetWorkspace();
        Expander expander = new() { Header = name, Content = contents, IsExpanded = settings?.Groups.GetValueOrDefault(key,expanded) ?? expanded, Margin = new(0,4,0,2) };
        propertyGroups.Add((expander,expanded));
        expander.Expanded += (_,_) => { if (preferences != null) preferences.Settings.GetWorkspace().Groups[key] = true; };
        expander.Collapsed += (_,_) => { if (preferences != null) preferences.Settings.GetWorkspace().Groups[key] = false; };
        panel.Children.Add(expander); return contents;
    }
    private List<ChoiceValue> References(int table)
    {
        List<ChoiceValue> choices = [];
        if (table == 1) { choices.Add(new(-100,"−100: bound root")); choices.Add(new(-200,"−200: activation reference (root in preview)")); }
        for (int i = 0; i < Entry.References[table].Count; i++) choices.Add(new(i,$"{i}: {(i == 0 ? "(reserved)" : Entry.References[table][i].Text(0,32))}"));
        if (choices.All(c => c.Value != 0)) choices.Add(new(0,"0: none")); return choices;
    }
}
