using System.Globalization;
using System.IO;
using System.Windows.Controls;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Animation;
namespace Recoil.Zbd.Desktop;
public sealed partial class AnimationPropertiesEditor
{
    private int selectedSegment;
    private AnimationEvent? keyframeSource;
    private IReadOnlyList<AnimationKeyframe> inspectedKeyframes = [];
    // Edits/history replace the event snapshot. All field getters for one snapshot
    // share its decoded stream; reading one field must not copy every record again.
    private IReadOnlyList<AnimationKeyframe> ReadKeyframes()
    {
        var source = Event!;
        if (!ReferenceEquals(source, keyframeSource))
        {
            var frames = source.Keyframes();
            inspectedKeyframes = frames; keyframeSource = source;
        }
        return inspectedKeyframes;
    }
    internal void SelectAutomationSegment(int index)
    {
        int count = 0;
        if (Event?.Type == 12)
        {
            try { count = ReadKeyframes().Count; }
            catch (InvalidDataException) { /* Scheduling/catalog fields remain inspectable, as in RefreshProperties. */ }
        }
        if (index < 0 || index >= Math.Max(1, count)) throw new StudioCommandException("invalid_argument", "Keyframe segment is unavailable; use segment 0 to inspect the record's available fields.");
        selectedSegment = index; fieldsShape = ""; RefreshProperties();
    }
    private string KeyframeShape()
    {
        if (Event?.Type != 12) return "";
        try { var frames = ReadKeyframes(); return $"{frames.Count}/{selectedSegment}/{(frames.Count == 0 ? 0 : frames[Math.Clamp(selectedSegment, 0, frames.Count - 1)].Flags)}/{Event!.KeyframePreviewDiagnostic() != null}"; }
        catch (InvalidDataException) { return "malformed"; }
    }
    private AnimationKeyframe CurrentSegment(int index) => ReadKeyframes()[index];
    private void Keyframes(StackPanel panel, IReadOnlyList<AnimationKeyframe> segments)
    {
        Label(panel, "Keyframe segments", true); Label(panel, "Times are local to this event. XYZ channels store a base and rate per second. Rotation stores W, X, Y, Z and the engine rotation-vector rate.");
        WrapPanel tools = new(); panel.Children.Add(tools);
        Button(tools, "+ segment", () => EditKeyframes("Add keyframe", list => { var next = AnimationKeyframe.Create(7); next.Start = list.Count > 0 ? list[^1].End : 0; next.End = next.Start + 1; list.Add(next); }));
        const int pageSize = 64;
        int first = Math.Clamp(selectedSegment, 0, Math.Max(0, segments.Count - 1)) / pageSize * pageSize;
        KeyframeChoice[] Choices(IReadOnlyList<AnimationKeyframe> frames) => Enumerable.Range(first, Math.Max(0, Math.Min(pageSize, frames.Count - first))).Select(index => { var frame = frames[index]; return new KeyframeChoice(index, $"{index}: {frame.Start:R}–{frame.End:R} s · channels 0x{frame.Flags:X}"); }).ToArray();
        var items = Choices(segments);
        Button(tools, "Previous segments", () => { if (ResolvePendingDrafts()) SelectAutomationSegment(Math.Max(0, first - pageSize)); });
        Button(tools, "Next segments", () => { if (ResolvePendingDrafts()) SelectAutomationSegment(Math.Min(Math.Max(0, segments.Count - 1), first + pageSize)); });
        Label(panel, $"{segments.Count:N0} segments · showing {first}–{Math.Max(first, first + items.Length - 1)}");
        ListBox list = new() { ItemsSource = items, DisplayMemberPath = nameof(KeyframeChoice.Label), MaxHeight = 140, MinHeight = 48, SelectedIndex = Math.Clamp(selectedSegment - first, 0, Math.Max(0, items.Length - 1)) };
        panel.Children.Add(list);
        valueRefresh.Add(() => { int at = list.SelectedIndex; list.ItemsSource = Choices(ReadKeyframes()); list.SelectedIndex = at; });
        list.SelectionChanged += (_, _) =>
        {
            if (refreshingFields || list.SelectedItem is not KeyframeChoice choice || choice.Index == selectedSegment) return;
            if (!ResolvePendingDrafts()) { list.SelectedIndex = selectedSegment - first; return; }
            selectedSegment = choice.Index; fieldsShape = ""; RefreshProperties();
        };
        if (segments.Count == 0) return;
        int index = Math.Clamp(selectedSegment, 0, segments.Count - 1); var segment = segments[index]; StackPanel contents = new(); panel.Children.Add(contents);
            Input(contents, "Start (s)", segment.Start.ToEditorText(), text => EditKeyframes("Edit keyframe start", list => list[index].Start = float.Parse(text, CultureInfo.InvariantCulture)),getter: () => CurrentSegment(index).Start.ToEditorText());
            Input(contents, "End (s)", segment.End.ToEditorText(), text => EditKeyframes("Edit keyframe end", list => list[index].End = float.Parse(text, CultureInfo.InvariantCulture)),getter: () => CurrentSegment(index).End.ToEditorText());
            Choice(contents, "Channels", Enumerable.Range(1,7).Select(f => new ChoiceValue(f, string.Join(" + ", new[] { (1,"Position"),(2,"Rotation"),(4,"Scale") }.Where(c => (f & c.Item1) != 0).Select(c => c.Item2)))), segment.Flags & 7, flags => EditKeyframes("Change keyframe channels", list =>
            {
                list[index] = list[index].WithChannels(flags, Event!.Version);
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
                        float[] values = ComponentText.ParseFinite(text, 4);
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
    private void EditKeyframes(string description, Action<IList<AnimationKeyframe>> change) => ChangeEvents(description, events =>
    {
        int at = events.FindIndex(e => e.Id == selectedEvent); var frames = new KeyframeEditList(events[at].Keyframes());
        change(frames); events[at] = events[at].WithKeyframes(frames);
    });
}
