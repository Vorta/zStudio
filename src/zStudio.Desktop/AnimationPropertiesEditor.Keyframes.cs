using System.Globalization;
using System.IO;
using System.Windows.Controls;
using Recoil.Zbd.Core.Animation;
namespace Recoil.Zbd.Desktop;
public sealed partial class AnimationPropertiesEditor
{
    private int selectedSegment;
    private string KeyframeShape()
    {
        if (Event?.Type != 12) return "";
        try { return string.Join(',',Event.Keyframes().Select(f => f.Flags)); }
        catch (InvalidDataException) { return "malformed"; }
    }
    private AnimationKeyframe CurrentSegment(int index) => Event!.Keyframes()[index];
    private void Keyframes(StackPanel panel, IReadOnlyList<AnimationKeyframe> segments)
    {
        Label(panel, "Keyframe segments", true); Label(panel, "Times are local to this event. XYZ channels store a base and rate per second. Rotation stores W, X, Y, Z and the engine rotation-vector rate.");
        WrapPanel tools = new(); panel.Children.Add(tools);
        Button(tools, "+ segment", () => EditKeyframes("Add keyframe", list => { var next = AnimationKeyframe.Create(7); next.Start = list.Count > 0 ? list[^1].End : 0; next.End = next.Start + 1; list.Add(next); }));
        var items = segments.Select((segment, index) => new KeyframeChoice(index, $"{index}: {segment.Start:R}–{segment.End:R} s · channels 0x{segment.Flags:X}")).ToArray();
        ListBox list = new() { ItemsSource = items, DisplayMemberPath = nameof(KeyframeChoice.Label), MaxHeight = 140, MinHeight = 48, SelectedIndex = Math.Clamp(selectedSegment, 0, Math.Max(0, items.Length - 1)) };
        panel.Children.Add(list);
        valueRefresh.Add(() => { int at = list.SelectedIndex; list.ItemsSource = Event!.Keyframes().Select((f,i) => new KeyframeChoice(i,$"{i}: {f.Start:R}–{f.End:R} s · channels 0x{f.Flags:X}")).ToArray(); list.SelectedIndex = at; });
        list.SelectionChanged += (_, _) =>
        {
            if (refreshingFields || list.SelectedItem is not KeyframeChoice choice || choice.Index == selectedSegment) return;
            if (!ResolvePendingDrafts()) { list.SelectedIndex = selectedSegment; return; }
            selectedSegment = choice.Index; fieldsShape = ""; RefreshProperties();
        };
        if (segments.Count == 0) return;
        int index = Math.Clamp(selectedSegment, 0, segments.Count - 1); var segment = segments[index]; StackPanel contents = new(); panel.Children.Add(contents);
            Input(contents, "Start (s)", segment.Start.ToEditorText(), text => EditKeyframes("Edit keyframe start", list => list[index].Start = float.Parse(text, CultureInfo.InvariantCulture)),getter: () => CurrentSegment(index).Start.ToEditorText());
            Input(contents, "End (s)", segment.End.ToEditorText(), text => EditKeyframes("Edit keyframe end", list => list[index].End = float.Parse(text, CultureInfo.InvariantCulture)),getter: () => CurrentSegment(index).End.ToEditorText());
            Choice(contents, "Channels", Enumerable.Range(1,7).Select(f => new ChoiceValue(f, string.Join(" + ", new[] { (1,"Position"),(2,"Rotation"),(4,"Scale") }.Where(c => (f & c.Item1) != 0).Select(c => c.Item2)))), segment.Flags & 7, flags => EditKeyframes("Change keyframe channels", list =>
            {
                var old = list[index]; var next = AnimationKeyframe.Create(flags); next.SetInt(0, (old.Flags & ~7) | flags); next.Start = old.Start; next.End = old.End;
                for (int c = 0; c < 3; c++) if (old.ChannelOffset(c) is int from && from >= 0 && next.ChannelOffset(c) is int to && to >= 0) old.Bytes.AsSpan(from,28).CopyTo(next.Bytes.AsSpan(to));
                list[index] = next;
            }),getter: () => CurrentSegment(index).Flags & 7,fullWidth:true);
            for (int channel = 0; channel < 3; channel++)
            {
                int c = channel, offset = segment.ChannelOffset(c); if (offset < 0) continue;
                string name = new[] { "Position", "Rotation", "Scale" }[c];
                if (c == 1)
                {
                    string quaternion = string.Join(", ", Enumerable.Range(0,4).Select(n => segment.F32(offset + n * 4).ToEditorText()));
                    Input(contents, "Rotation base W, X, Y, Z", quaternion, text => EditKeyframes("Edit rotation base", list =>
                    {
                        float[] values = text.Split([',',' '],StringSplitOptions.RemoveEmptyEntries).Select(s => float.Parse(s,CultureInfo.InvariantCulture)).ToArray();
                        if (values.Length != 4 || values.Any(v => !float.IsFinite(v)) || values.Sum(v => v * v) < 1e-12f) throw new InvalidDataException("Enter a finite, nonzero quaternion: W, X, Y, Z.");
                        for (int n = 0; n < 4; n++) list[index].SetFloat(list[index].ChannelOffset(c) + n * 4, values[n]);
                    }),getter: () => string.Join(", ",Enumerable.Range(0,4).Select(n => CurrentSegment(index).F32(CurrentSegment(index).ChannelOffset(c) + n * 4).ToEditorText())),components:["W","X","Y","Z"]);
                }
                else Input(contents, name + " base XYZ", new AnimationField("",offset,AnimationFieldKind.Vector).FormatForEditor(segment), text => EditKeyframes("Edit keyframe base", list => new AnimationField("",list[index].ChannelOffset(c),AnimationFieldKind.Vector).Write(list[index],text)),getter: () => new AnimationField("",CurrentSegment(index).ChannelOffset(c),AnimationFieldKind.Vector).FormatForEditor(CurrentSegment(index)),components:["X","Y","Z"]);
                Input(contents, name + " rate XYZ", new AnimationField("",offset + 16,AnimationFieldKind.Vector).FormatForEditor(segment), text => EditKeyframes("Edit keyframe rate", list => new AnimationField("",list[index].ChannelOffset(c) + 16,AnimationFieldKind.Vector).Write(list[index],text)),getter: () => new AnimationField("",CurrentSegment(index).ChannelOffset(c) + 16,AnimationFieldKind.Vector).FormatForEditor(CurrentSegment(index)),components:["X","Y","Z"]);
            }
            WrapPanel actions = new(); contents.Children.Add(actions);
            Button(actions, "Copy", () => EditKeyframes("Duplicate keyframe", list => list.Insert(index + 1,new((byte[])list[index].Bytes.Clone()))));
            Button(actions, "Delete", () => EditKeyframes("Delete keyframe", list => list.RemoveAt(index)));
            Button(actions, "↑", () => EditKeyframes("Move keyframe", list => { if (index > 0) (list[index-1],list[index]) = (list[index],list[index-1]); }));
            Button(actions, "↓", () => EditKeyframes("Move keyframe", list => { if (index + 1 < list.Count) (list[index+1],list[index]) = (list[index],list[index+1]); }));
    }
    private sealed record KeyframeChoice(int Index, string Label);
    private void EditKeyframes(string description, Action<List<AnimationKeyframe>> change) => ChangeEvents(description, events =>
    {
        int at = events.FindIndex(e => e.Id == selectedEvent); var frames = events[at].Keyframes().Select(f => new AnimationKeyframe((byte[])f.Bytes.Clone())).ToList();
        change(frames); foreach (var f in frames) f.Validate(); events[at] = events[at].WithKeyframes(frames);
    });
}
