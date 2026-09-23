using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

public partial class AnimationEditor
{
    private bool refreshingFields;
    private void RefreshProperties()
    {
        if (refreshingFields) return;
        refreshingFields = true;
        try
        {
            int tab = fields.SelectedIndex; fields.Items.Clear();
            StackPanel eventPanel = Panel("Event"), sequencePanel = Panel("Sequence"), referencePanel = Panel("References"), previewPanel = Panel("Preview");
            Label(previewPanel, "Activation points", true);
            Label(previewPanel, "These replace the origin/target normally supplied by gameplay. Empty uses the bound root and a target 10 units along Z. Coordinates are world XYZ; changes affect preview only.");
            Input(previewPanel, "Origin XYZ", activationStart?.ToString().Trim('<','>') ?? "", text => SetActivation(text, true));
            Input(previewPanel, "Target XYZ / distance-test point", activationTarget?.ToString().Trim('<','>') ?? "", text => SetActivation(text, false));
            Label(previewPanel, "Rendering", true); Label(previewPanel, "Textures use neutral colors. Effects enables approximate light/fog tint and screen overlays. Collision, gameplay callbacks, spatial audio and inherited launch velocity are not reproduced by this preview. The trace identifies affected events.");
            if (Event is { } ev)
            {
                Label(eventPanel, ev.Name, true); Label(eventPanel, ev.Spec?.Support ?? "Unknown event: preserved read-only");
                Label(eventPanel, $"{ev.Bytes.Length} bytes · source 0x{ev.SourceOffset:X}");
                bool editable = Sequence!.IsEditable && ev.Spec != null && ev.Bytes.Length >= ev.Spec.Size;
                Choice(eventPanel, "Start clock", Enumerable.Range(1, 3).Select(i => new ChoiceValue(i, AnimationCatalog.ModeName(i))).ToArray(), ev.StartMode,
                    value => ChangeEvent("Change start clock", record => record.StartMode = (byte)value), !editable);
                Input(eventPanel, "Threshold (seconds)", ev.Threshold.ToString("R", CultureInfo.InvariantCulture), text => ChangeEvent("Change threshold", record => record.Threshold = float.Parse(text, CultureInfo.InvariantCulture)), !editable);
                if (ev.Spec != null) foreach (var field in ev.Spec.Fields.Where(f => f.Offset + f.Size <= ev.Bytes.Length))
                {
                    if (ev.Type == 30 && field.Offset == 16)
                    {
                        bool timed = (ev.U32(12) & 1) == 0 && (ev.U32(12) & 2) != 0;
                        var valueField = field with { Name = timed ? "Loop duration (s; negative = infinite)" : "Loop count (65535 = infinite)", Kind = timed ? AnimationFieldKind.Float : AnimationFieldKind.Integer };
                        Input(eventPanel,valueField.Name,valueField.Format(ev),text => TryEdit(() => edits.EditField(entryIndex,selectedSequence,selectedEvent,valueField,text)),!editable); continue;
                    }
                    if (ev.Type is 31 or 33 && field.Offset == 20)
                    {
                        var valueField = field with { Name = (ev.U32(12) & 4) != 0 ? "Minimum effects level" : "Threshold", Kind = (ev.U32(12) & 4) != 0 ? AnimationFieldKind.Integer : AnimationFieldKind.Float };
                        Input(eventPanel,valueField.Name,valueField.Format(ev),text => TryEdit(() => edits.EditField(entryIndex,selectedSequence,selectedEvent,valueField,text)),!editable); continue;
                    }
                    if (field.ReferenceTable >= 0 && !field.ReadOnly)
                    {
                        var choices = References(field.ReferenceTable); int index = field.Kind == AnimationFieldKind.Short ? ev.I16(field.Offset) : ev.I32(field.Offset);
                        if (!choices.Any(c => c.Value == index)) choices.Add(new(index, $"{index}: unresolved"));
                        Choice(eventPanel, field.Name, choices, index, value => TryEdit(() => edits.EditField(entryIndex, selectedSequence, selectedEvent, field, value.ToString(CultureInfo.InvariantCulture))), !editable);
                    }
                    else if (field.Kind == AnimationFieldKind.Text && (ev.Type is 22 or 23 || ev.Type == 10 && field.Offset == 208))
                    {
                        TextChoice(eventPanel, field.Name, ev.Text(field.Offset, field.Size), Entry.Sequences.Select(s => s.Name), text => TryEdit(() => edits.EditField(entryIndex, selectedSequence, selectedEvent, field, text)), !editable);
                    }
                    else if (field.Kind == AnimationFieldKind.Text && ev.Type is 19 or 24 or 25 or 26 or 27)
                        TextChoice(eventPanel, field.Name, ev.Text(field.Offset, field.Size), edits.Package.Entries.Where(e => e.Name.Length > 0).Select(e => e.Name), text => TryEdit(() => edits.EditField(entryIndex, selectedSequence, selectedEvent, field, text)), !editable);
                    else Input(eventPanel, field.Name, field.Format(ev), text => TryEdit(() => edits.EditField(entryIndex, selectedSequence, selectedEvent, field, text)), !editable || field.ReadOnly, field.Hint.Length > 0 ? field.Hint : $"{field.Kind} · byte {field.Offset}");
                }
                if (ev.Type == 12 && editable)
                {
                    try { Keyframes(eventPanel, ev.Keyframes()); }
                    catch (InvalidDataException ex) { Label(eventPanel, ex.Message + " Keyframe payload is read-only."); }
                }
                InspectionChanged?.Invoke(ev.ToJson(), ev.Bytes);
            }
            else Label(eventPanel, "Select an event to edit its timing and parameters.");
            if (Sequence is { } sequence)
            {
                Label(sequencePanel, sequence == Entry.Primary ? "Reset / stop sequence" : "Runtime sequence", true);
                Label(sequencePanel, sequence == Entry.Primary ? "Runs during cleanup. Preview it using Reset / stop phase." : "Runtime sequences execute concurrently when the animation starts.");
                Input(sequencePanel, "Name", sequence.Name, text => TryEdit(() => edits.RenameSequence(entryIndex, selectedSequence, text)), !sequence.IsEditable);
                Choice(sequencePanel, "Initial / reset state", [new(0,"Ready"),new(1,"Running"),new(2,"Stopped"),new(3,"Waiting for release")], sequence.ResetMode,
                    value => TryEdit(() => edits.Apply(entryIndex, "Change reset state", e => { var s = AnimationEditSession.FindSequence(e, selectedSequence); AnimationEditSession.EnsureEditable(s); s.ResetMode = (byte)value; })), !sequence.IsEditable);
                Label(sequencePanel, "Waiting sequences need a Release waiting sequence event. On completion, state 3 returns to waiting.");
                WrapPanel order = new(); sequencePanel.Children.Add(order);
                Button(order, "Move ↑", () => TryEdit(() => edits.MoveSequence(entryIndex, selectedSequence, -1)));
                Button(order, "Move ↓", () => TryEdit(() => edits.MoveSequence(entryIndex, selectedSequence, 1)));
                Label(sequencePanel, $"{sequence.Events.Count} events · source 0x{sequence.SourceOffset:X} · {sequence.OpaqueTail.Length} opaque bytes");
                Label(sequencePanel, "Entry: " + Entry.Name, true); Label(sequencePanel, "Root: " + Entry.RootName + "\nAttachment: " + Entry.AttachName);
                Input(sequencePanel, "Reset delay (seconds; negative disables)", Entry.F32(164).ToString("R", CultureInfo.InvariantCulture), text => TryEdit(() => edits.Apply(entryIndex, "Edit reset delay", e => e.SetFloat(164, float.Parse(text, CultureInfo.InvariantCulture)))));
            }
            Label(referencePanel, "Reference tables", true);
            Label(referencePanel, "Event target menus use these stable indices. Pointer/cache values and unverified fields remain read-only. Root binding changes preview context only.");
            string[] tables = ["Tracked nodes", "Scene nodes", "Lights", "Sound nodes", "Samples", "Effect templates", "Activation conditions", "Child slots"];
            for (int t = 0; t < tables.Length; t++)
            {
                int table = t; StackPanel contents = new(); referencePanel.Children.Add(new Expander { Header = $"{tables[t]} ({Entry.References[t].Count})", Content = contents, Margin = new(0,4,0,4) });
                for (int i = 0; i < Entry.References[t].Count; i++)
                {
                    int index = i; var record = Entry.References[t][i]; string name = record.Text(0, Math.Min(32, record.Bytes.Length));
                    // Only name-based tables have a verified editable string contract.
                    Input(contents, $"[{i}] name", name, text => TryEdit(() => edits.Apply(entryIndex, "Retarget reference", e => e.References[table][index].SetText(0, text))), t is 0 or 6 or 7 || i == 0);
                }
            }
            fields.SelectedIndex = Math.Clamp(tab, 0, fields.Items.Count - 1);
        }
        finally { refreshingFields = false; }
    }
    private void SetActivation(string text, bool start)
    {
        System.Numerics.Vector3? point = null;
        if (!string.IsNullOrWhiteSpace(text)) { AnimationRecord record = new(new byte[12]); new AnimationField("",0,AnimationFieldKind.Vector).Write(record,text); point = record.Vector(0); }
        if (start) activationStart = point; else activationTarget = point; resetSimulation = true; _ = SeekAsync(frame?.Time ?? 0);
    }
    private void ChangeEvent(string description, Action<AnimationEvent> change) => ChangeEvents(description, list => change(list.Single(e => e.Id == selectedEvent)));
    private StackPanel Panel(string title)
    {
        StackPanel panel = new() { Margin = new(8) }; fields.Items.Add(new TabItem { Header = title, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }); return panel;
    }
    private static void Label(StackPanel panel, string text, bool title = false) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = title ? 13 : 11, FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal, Opacity = title ? 1 : .75, Margin = new(0, 3, 0, 6) });
    private void Input(StackPanel panel, string label, string value, Action<string> commit, bool readOnly = false, string? hint = null)
    {
        Label(panel, label + (readOnly ? " · read-only" : ""));
        TextBox input = new() { Text = value, IsReadOnly = readOnly, Margin = new(0,0,0,5), ToolTip = hint ?? label, MinWidth = 30 };
        if (readOnly) input.Opacity = .65;
        panel.Children.Add(input); string original = value;
        void Commit()
        {
            if (refreshingFields || readOnly || disposed || input.Text == original) return;
            string text = input.Text; TryEdit(() => commit(text));
            // A rejected value stays visible until the user fixes it or restores the field.
            original = text;
        }
        input.LostKeyboardFocus += (_, _) => Commit();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { input.Text = value; e.Handled = true; } };
    }
    private void Choice(StackPanel panel, string label, IEnumerable<ChoiceValue> values, int value, Action<int> commit, bool readOnly = false)
    {
        Label(panel, label); var items = values.ToArray(); ComboBox choice = new() { ItemsSource = items, DisplayMemberPath = nameof(ChoiceValue.Label), SelectedItem = items.FirstOrDefault(c => c.Value == value), IsEnabled = !readOnly, Margin = new(0,0,0,5) }; panel.Children.Add(choice);
        choice.SelectionChanged += (_, _) => { if (!refreshingFields && !disposed && choice.SelectedItem is ChoiceValue selected && selected.Value != value) commit(selected.Value); };
    }
    private void TextChoice(StackPanel panel, string label, string value, IEnumerable<string> choices, Action<string> commit, bool readOnly)
    {
        Label(panel, label); ComboBox choice = new() { IsEditable = true, IsEnabled = !readOnly, ItemsSource = choices.Distinct().Order().ToArray(), Text = value, Margin = new(0,0,0,5) }; panel.Children.Add(choice);
        void Commit() { if (!refreshingFields && !disposed && choice.Text != value) { string next = choice.Text; commit(next); } }
        choice.LostKeyboardFocus += (_, _) => Commit(); choice.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
    }
    private List<ChoiceValue> References(int table)
    {
        List<ChoiceValue> choices = [];
        if (table == 1) { choices.Add(new(-100,"−100: bound root")); choices.Add(new(-200,"−200: activation reference (root in preview)")); }
        for (int i = 0; i < Entry.References[table].Count; i++) choices.Add(new(i, $"{i}: {(i == 0 ? "(reserved)" : Entry.References[table][i].Text(0,32))}"));
        if (choices.All(c => c.Value != 0)) choices.Add(new(0,"0: none")); return choices;
    }
    private void Keyframes(StackPanel panel, IReadOnlyList<AnimationKeyframe> segments)
    {
        Label(panel, "Keyframe segments", true); Label(panel, "Times are local to this event. XYZ channels store a base and rate per second. Rotation stores W, X, Y, Z and the engine rotation-vector rate.");
        WrapPanel tools = new(); panel.Children.Add(tools);
        Button(tools, "+ segment", () => EditKeyframes("Add keyframe", list => { var next = AnimationKeyframe.Create(7); next.Start = list.Count > 0 ? list[^1].End : 0; next.End = next.Start + 1; list.Add(next); }));
        for (int i = 0; i < segments.Count; i++)
        {
            int index = i; var segment = segments[i]; StackPanel contents = new();
            Expander expander = new() { Header = $"Segment {i} · {segment.Start:0.###}–{segment.End:0.###} s", IsExpanded = segments.Count < 4, Content = contents, Margin = new(0,5,0,5) }; panel.Children.Add(expander);
            Input(contents, "Start (s)", segment.Start.ToString("R", CultureInfo.InvariantCulture), text => EditKeyframes("Edit keyframe start", list => list[index].Start = float.Parse(text, CultureInfo.InvariantCulture)));
            Input(contents, "End (s)", segment.End.ToString("R", CultureInfo.InvariantCulture), text => EditKeyframes("Edit keyframe end", list => list[index].End = float.Parse(text, CultureInfo.InvariantCulture)));
            Choice(contents, "Channels", Enumerable.Range(1,7).Select(f => new ChoiceValue(f, string.Join(" + ", new[] { (1,"Position"),(2,"Rotation"),(4,"Scale") }.Where(c => (f & c.Item1) != 0).Select(c => c.Item2)))), segment.Flags & 7, flags => EditKeyframes("Change keyframe channels", list =>
            {
                var old = list[index]; var next = AnimationKeyframe.Create(flags); next.SetInt(0, (old.Flags & ~7) | flags); next.Start = old.Start; next.End = old.End;
                for (int c = 0; c < 3; c++) if (old.ChannelOffset(c) is int from && from >= 0 && next.ChannelOffset(c) is int to && to >= 0) old.Bytes.AsSpan(from,28).CopyTo(next.Bytes.AsSpan(to));
                list[index] = next;
            }));
            for (int channel = 0; channel < 3; channel++)
            {
                int c = channel, offset = segment.ChannelOffset(c); if (offset < 0) continue;
                string name = new[] { "Position", "Rotation", "Scale" }[c];
                if (c == 1)
                {
                    string quaternion = string.Join(", ", Enumerable.Range(0,4).Select(n => segment.F32(offset + n * 4).ToString("R", CultureInfo.InvariantCulture)));
                    Input(contents, "Rotation base W, X, Y, Z", quaternion, text => EditKeyframes("Edit rotation base", list =>
                    {
                        float[] values = text.Split([',',' '],StringSplitOptions.RemoveEmptyEntries).Select(s => float.Parse(s,CultureInfo.InvariantCulture)).ToArray();
                        if (values.Length != 4 || values.Any(v => !float.IsFinite(v)) || values.Sum(v => v * v) < 1e-12f) throw new InvalidDataException("Enter a finite, nonzero quaternion: W, X, Y, Z.");
                        for (int n = 0; n < 4; n++) list[index].SetFloat(list[index].ChannelOffset(c) + n * 4, values[n]);
                    }));
                }
                else Input(contents, name + " base XYZ", new AnimationField("",offset,AnimationFieldKind.Vector).Format(segment), text => EditKeyframes("Edit keyframe base", list => new AnimationField("",list[index].ChannelOffset(c),AnimationFieldKind.Vector).Write(list[index],text)));
                Input(contents, name + " rate XYZ", new AnimationField("",offset + 16,AnimationFieldKind.Vector).Format(segment), text => EditKeyframes("Edit keyframe rate", list => new AnimationField("",list[index].ChannelOffset(c) + 16,AnimationFieldKind.Vector).Write(list[index],text)));
            }
            WrapPanel actions = new(); contents.Children.Add(actions);
            Button(actions, "Copy", () => EditKeyframes("Duplicate keyframe", list => list.Insert(index + 1,new((byte[])list[index].Bytes.Clone()))));
            Button(actions, "Delete", () => EditKeyframes("Delete keyframe", list => list.RemoveAt(index)));
            Button(actions, "↑", () => EditKeyframes("Move keyframe", list => { if (index > 0) (list[index-1],list[index]) = (list[index],list[index-1]); }));
            Button(actions, "↓", () => EditKeyframes("Move keyframe", list => { if (index + 1 < list.Count) (list[index+1],list[index]) = (list[index],list[index+1]); }));
        }
    }
    private void EditKeyframes(string description, Action<List<AnimationKeyframe>> change) => ChangeEvents(description, events =>
    {
        int at = events.FindIndex(e => e.Id == selectedEvent); var frames = events[at].Keyframes().Select(f => new AnimationKeyframe((byte[])f.Bytes.Clone())).ToList();
        change(frames); foreach (var f in frames) f.Validate(); events[at] = events[at].WithKeyframes(frames);
    });
    private static void Button(Panel panel, string text, Action action) { Button button = new() { Content = text, Margin = new(2), Padding = new(6,3,6,3) }; button.Click += (_,_) => action(); panel.Children.Add(button); }
    private sealed record ChoiceValue(int Value, string Label);
}
