using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Desktop;

public sealed record ValveReferenceTarget(AiValveSource Source, Guid Record, string Name, string Role);

public sealed partial class ResourcePropertiesEditor
{
    private readonly Func<AiValveEdit, Task>? valveEdit;
    private readonly Func<string, int, Task<IReadOnlyList<ValveReferenceTarget>>>? valveReferences;
    private readonly Func<ValveReferenceTarget, Task>? valveNavigate;
    private readonly Action<string, bool>? valveHighlight;
    public bool ValveMode { get; }
    private static string Offset(long offset) => offset >= 0 ? $"@{offset:X}" : "(new)";
    internal Task ValveWork { get; private set; } = Task.CompletedTask;
    private Guid? valveRecord;
    private string valveQuery = "", newValveName = "new_valve", newValveKind = "delayupdate", appendValveKind = "delayupdate";
    private int valveOffset, operandOffset, targetOffset, itemOffset, referenceOffset;
    private long valveGeneration;
    private string? valveForm;
    private sealed record ValveChoice(AiValveRecord Record)
    { public override string ToString() => $"{MissionAiValves.Short(Record.Name, 80)} · {Record.Kind}" + (Record.NodeIndex is int n ? $" · node_{n:00}" : Record.From is int f ? $" · {f} → {Record.To}" : "") + (Record.SourceOffset >= 0 ? $" · @{Record.SourceOffset:X}" : " · new"); }
    private void RefreshValves()
    {
        var snapshot = document.ResourceEdits!.Current;
        string shape = ValveShape(snapshot);
        if (valveForm == shape) return;
        valveForm = shape; ValveWork = LoadValvesAsync(snapshot, ++valveGeneration);
    }
    private string ValveShape(ResourceSnapshot snapshot) => $"{snapshot.Hash}/{valveRecord}/{valveQuery}/{valveOffset}/{operandOffset}/{targetOffset}/{itemOffset}/{referenceOffset}";
    private async Task LoadValvesAsync(ResourceSnapshot snapshot, long request)
    {
        try
        {
            var member = snapshot.Members.SingleOrDefault(m => m.Id == memberId);
            if (member == null) { draftInputs.Clear(); valueRefresh.Clear(); ClearAutomationFields("properties"); ClearAutomationFields("valveReferences"); Content = new TextBlock { Text = "This resource was deleted. Undo restores it." }; return; }
            var token = document.Lifetime.Token;
            string query = valveQuery; int offset = valveOffset, fieldsOffset = operandOffset, targetsOffset = targetOffset, refsOffset = referenceOffset; Guid? selected = valveRecord;
            var result = await Task.Run(() =>
            {
                var root = document.ResourceEdits!.Tree(member, token);
                var rows = MissionAiValves.Records(member.Name, root, token).Where(r => query.Length == 0 || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || r.NodeIndex?.ToString(CultureInfo.InvariantCulture) == query).Skip(offset).Take(101).ToArray();
                var current = selected == null ? rows.FirstOrDefault() : MissionAiValves.Records(member.Name, root, token).FirstOrDefault(r => r.Id == selected);
                var leaves = current == null ? [] : ValveScalars(current, token).Skip(fieldsOffset).Take(33).ToArray();
                var references = current == null ? [] : MissionAiValves.References(current, token).Skip(refsOffset).Take(9).ToArray();
                var targets = MissionAiNetworks.IsCandidate(member.Name) ? MissionAiValves.Targets(root, token).Skip(targetsOffset).Take(17).ToArray() : [];
                return (rows, current, leaves, references, targets, problem: current == null ? null : MissionAiValves.Problem(current, token));
            }, token);
            if (disposed || request != valveGeneration || !ReferenceEquals(snapshot, document.ResourceEdits!.Current) || HasPendingDrafts) { valveForm = null; return; }
            draftInputs.Clear(); valueRefresh.Clear(); ClearAutomationFields("properties"); ClearAutomationFields("valveReferences");
            StackPanel panel = new() { Margin = new(12) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Label(panel, "AI valves", true); Label(panel, "Authored data · Enter applies one undo step · Ctrl+S saves this resource. Runtime states are not simulated.");
            Input(panel, "Filter records", valveQuery, _ => { }, asyncCommit: async value => { valveQuery = value; valveRecord = null; valveOffset = operandOffset = itemOffset = referenceOffset = 0; valveForm = null; });
            var choices = result.rows.Take(100).Select(r => new ValveChoice(r)).ToArray();
            ComboBox picker = new() { ItemsSource = choices, SelectedItem = choices.FirstOrDefault(c => c.Record.Id == result.current?.Id), MaxDropDownHeight = 280, Margin = new(0, 4, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(picker, "Valve record"); panel.Children.Add(picker);
            bool selecting = false;
            picker.SelectionChanged += async (_, _) =>
            {
                if (selecting || picker.SelectedItem is not ValveChoice choice || choice.Record.Id == valveRecord) return;
                selecting = true; picker.IsEnabled = false;
                try
                {
                    if (!await ResolvePendingDraftsAsync()) { picker.SelectedItem = choices.FirstOrDefault(c => c.Record.Id == valveRecord); return; }
                    if (disposed) return;
                    valveRecord = choice.Record.Id; operandOffset = itemOffset = referenceOffset = 0; valveForm = null; RefreshValves(); await ValveWork;
                }
                finally { selecting = false; picker.IsEnabled = true; }
            };
            WrapPanel pages = new(); panel.Children.Add(pages);
            AsyncButton(pages, "Previous valve records", () => PageRecords(Math.Max(0, valveOffset - 100)));
            AsyncButton(pages, "Next valve records", () => PageRecords(result.rows.Length > 100 ? valveOffset + 100 : valveOffset));
            Label(panel, $"Source records {valveOffset}–{valveOffset + choices.Length - 1}" + (result.rows.Length > 100 ? " · more available" : ""));
            Task Apply(AiValveEdit edit) => valveEdit?.Invoke(edit) ?? throw new InvalidDataException("Valve editing is unavailable.");
            if (member.Name.Equals("valves.zrd", StringComparison.OrdinalIgnoreCase))
            {
                Input(panel, "New valve name", newValveName, value => newValveName = value);
                TextChoice(panel, "New action / condition", newValveKind, MissionAiValves.Actions.Concat(MissionAiValves.Conditions), value => newValveKind = value, false);
                AsyncButton(panel, "Add valve record", () => Apply(new("add_record", Value: newValveName, Kind: newValveKind)));
            }
            if (result.targets.Length > 0 || targetOffset > 0)
            {
                foreach (var target in result.targets.Take(16))
                {
                    AsyncButton(panel, $"Add valve to {MissionAiValves.Short(target.Name, 32)} {Offset(target.SourceOffset)}", () => Apply(new("add_binding", Operand: target.Id, Kind: target.Spatial ? "valve" : "valve_assign")));
                    if (target.Spatial) AsyncButton(panel, $"Add union to {MissionAiValves.Short(target.Name, 32)} {Offset(target.SourceOffset)}", () => Apply(new("add_binding", Operand: target.Id, Kind: "valveunion")));
                }
                if (targetOffset > 0) AsyncButton(panel, "Previous binding targets", () => { targetOffset = Math.Max(0, targetOffset - 16); valveForm = null; return Task.CompletedTask; });
                if (result.targets.Length > 16) AsyncButton(panel, "Next binding targets", () => { targetOffset += 16; valveForm = null; return Task.CompletedTask; });
                Label(panel, $"Binding targets {targetOffset}–{targetOffset + Math.Min(16, result.targets.Length) - 1} · source-record order");
            }
            if (result.current is not { } record) { Label(panel, selected == null ? "No valve records in this resource." : "This valve occurrence was deleted. Undo restores its identity."); Changed?.Invoke(); return; }
            valveRecord = record.Id;
            valveForm = ValveShape(snapshot);
            Label(panel, new ValveChoice(record).ToString(), true);
            if (result.problem is { } problem) Label(panel, problem);
            Label(panel, "Changing names affects only the selected occurrence. References elsewhere are not renamed or deleted automatically.");
            if (record.Kind == "definition") ScalarField(record.NameNode, "Trigger / output name");
            foreach (var (node, label) in result.leaves.Take(32)) ScalarField(node, label);
            if (result.leaves.Length > 32 || operandOffset > 0)
            {
                AsyncButton(panel, "Previous operands", () => PageOperands(Math.Max(0, operandOffset - 32)));
                AsyncButton(panel, "Next operands", () => PageOperands(result.leaves.Length > 32 ? operandOffset + 32 : operandOffset));
            }
            void ScalarField(ZrdNode node, string label)
            {
                var preview = node.PreviewValue(1024);
                Input(panel, label + " · " + node.Kind, preview.Value, _ => { }, preview.Truncated,
                    hint: "Stored typed value. Strings use JSON quotes. Unverified integers retain their authored meaning.", asyncCommit: value => Apply(new("set", record.Id, node.Id, value)));
            }
            WrapPanel operations = new(); panel.Children.Add(operations);
            AsyncButton(operations, "Duplicate record", () => Apply(new("duplicate", record.Id)));
            AsyncButton(operations, "Delete record", () => Apply(new("delete", record.Id)));
            int first = record.Kind == "node" ? 3 : record.Kind == "edge" ? 1 : 0;
            int position = (record.ChildIndex - first) / 2;
            if (position > 0) AsyncButton(operations, "Move record up", () => Apply(new("move", record.Id, Index: position - 1)));
            AsyncButton(operations, "Move record down", () => Apply(new("move", record.Id, Index: position + 1)));
            if (record.Kind == "definition" && record.Value.Kind == ZrdKind.Array && record.Value.Children.Count % 2 == 0 && (record.Value.Children.Count == 0 || result.problem == null))
            {
                TextChoice(panel, "Action to append", appendValveKind, MissionAiValves.Actions, value => appendValveKind = value, false);
                AsyncButton(panel, "Append action", () => Apply(new("add_action", record.Id, Kind: appendValveKind)));
            }
            bool compound = record.Kind == "definition" && record.Value.Children.Count == 3 && record.Value.Children[1].Text == "namelist" && record.Value.Children[2].Kind == ZrdKind.Array && MissionAiValves.Conditions.Contains(record.Value.Children[0].Text);
            if (record is { Kind: "node", Name: "valveunion", Value.Kind: ZrdKind.Array } || compound) AsyncButton(panel, "Append term", () => Apply(new("add_term", record.Id)));
            if (record.Kind == "node" && record.Name == "valve" && record.Value.Children.Count is 2 or 3)
                AsyncButton(panel, record.Value.Children.Count == 2 ? "Add optional integer" : "Remove optional integer", () => Apply(new(record.Value.Children.Count == 2 ? "add_option" : "remove_option", record.Id)));
            var items = compound ? record.Value.Children[2].Children : record.Value.Children;
            int width = record.Kind == "definition" && !compound ? 2 : 1;
            if (record.Kind == "definition" || record is { Kind: "node", Name: "valveunion" })
            {
                for (int i = itemOffset * width; i < Math.Min(items.Count, (itemOffset + 16) * width); i += width)
                {
                    var item = items[i]; int index = i / width;
                    WrapPanel actions = new(); panel.Children.Add(actions); Label(panel, $"{(compound || record is { Kind: "node", Name: "valveunion" } ? "Term" : "Action")} {index}: {item.PreviewValue(80).Value}");
                    AsyncButton(actions, $"Duplicate item {index}", () => Apply(new("duplicate_item", record.Id, item.Id)));
                    AsyncButton(actions, $"Delete item {index}", () => Apply(new("delete_item", record.Id, item.Id)));
                    if (index > 0) AsyncButton(actions, $"Move item {index} up", () => Apply(new("move_item", record.Id, item.Id, Index: index - 1)));
                    if (i + width < items.Count) AsyncButton(actions, $"Move item {index} down", () => Apply(new("move_item", record.Id, item.Id, Index: index + 1)));
                    if (record is { Kind: "node", Name: "valveunion" } && item.Children.Count is 2 or 3)
                        AsyncButton(actions, $"{(item.Children.Count == 2 ? "Add" : "Remove")} optional integer {index}", () => Apply(new(item.Children.Count == 2 ? "add_option" : "remove_option", record.Id, item.Id)));
                }
                if (itemOffset > 0) AsyncButton(panel, "Previous items", () => { itemOffset = Math.Max(0, itemOffset - 16); valveForm = null; return Task.CompletedTask; });
                if ((itemOffset + 16) * width < items.Count) AsyncButton(panel, "Next items", () => { itemOffset += 16; valveForm = null; return Task.CompletedTask; });
            }
            Label(panel, "Valve references", true);
            foreach (var reference in result.references.Take(8)) ReadOnlyText(panel, $"{reference.Role}: {MissionAiValves.Short(reference.Name)}");
            if (referenceOffset > 0) AsyncButton(panel, "Previous reference names", () => { referenceOffset = Math.Max(0, referenceOffset - 8); valveForm = null; return Task.CompletedTask; });
            if (result.references.Length > 8) AsyncButton(panel, "Next reference names", () => { referenceOffset += 8; valveForm = null; return Task.CompletedTask; });
            if (valveReferences != null)
            {
                StackPanel related = new();
                foreach (string name in result.references.Take(8).Select(r => r.Name).Distinct(StringComparer.Ordinal))
                {
                    AsyncButton(panel, "Find uses: " + MissionAiValves.Short(name, 70), () => ShowReferences(name, 0));
                    if (valveHighlight != null)
                    {
                        AsyncButton(panel, "Highlight: " + MissionAiValves.Short(name, 70), () => { valveHighlight(name, false); return Task.CompletedTask; });
                        AsyncButton(panel, "Frame: " + MissionAiValves.Short(name, 70), () => { valveHighlight(name, true); return Task.CompletedTask; });
                    }
                }
                panel.Children.Add(related);
                async Task ShowReferences(string name, int first)
                {
                    var found = await valveReferences(name, first);
                    if (disposed || request != valveGeneration) return;
                    related.Children.Clear(); ClearAutomationFields("valveReferences"); var scope = inputScope; inputScope = "valveReferences";
                    try
                    {
                        Label(related, "Exact-name uses · actions without a defining block may still be valid mission valves.", true);
                        foreach (var target in found.Take(32))
                            AsyncButton(related, $"{target.Source.Member} #{target.Source.MemberIndex} · {target.Role} · {System.IO.Path.GetFileName(target.Source.Archive)}", () => valveNavigate!(target));
                        if (first > 0) AsyncButton(related, "Previous references", () => ShowReferences(name, Math.Max(0, first - 32)));
                        if (found.Count > 32) AsyncButton(related, "Next references", () => ShowReferences(name, first + 32));
                        if (found.Count == 0) Label(related, "No matching authored operands in this mission scope.");
                    }
                    finally { inputScope = scope; }
                }
            }
            Input(panel, "Save to", document.ResourceEdits!.TargetPath, _ => { }, true); Changed?.Invoke();
        }
        catch (OperationCanceledException) when (document.Lifetime.IsCancellationRequested || disposed) { }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            if (disposed || request != valveGeneration) return;
            draftInputs.Clear(); valueRefresh.Clear(); ClearAutomationFields("properties"); ClearAutomationFields("valveReferences");
            Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new(12) }; Changed?.Invoke();
        }
    }
    private Task PageRecords(int offset) { valveOffset = offset; valveRecord = null; operandOffset = itemOffset = referenceOffset = 0; valveForm = null; return Task.CompletedTask; }
    private Task PageOperands(int offset) { operandOffset = offset; valveForm = null; return Task.CompletedTask; }
    private static IEnumerable<(ZrdNode Node, string Label)> ValveScalars(AiValveRecord record, CancellationToken token)
    {
        var c = record.Value.Children;
        bool actions = record.Kind == "definition" && c.Count % 2 == 0;
        if (!actions) { foreach (var leaf in Scalars(record.Value, record.Name == "valve" ? "Condition (stored integer, valve name, optional integer)" : "Parameters", token)) yield return leaf; yield break; }
        for (int i = 0; i + 1 < c.Count; i += 2)
        {
            yield return (c[i], $"Action {i / 2} kind");
            string[] labels = c[i].Text switch
            {
                "delayupdate" => ["Valve name", "Delay"],
                "teleport" => ["Actor name", "Position XYZ", "Heading", "Stored integer"],
                "attack_strategy" => ["Target name", "Stored integer 1", "Stored integer 2"],
                "shutdown" => ["Actor name", "Stored integer 1", "Stored integer 2"],
                "destroy" => ["Actor name", "Stored integer"],
                "sound" or "sound_once" => ["Sound name", "Stored number"], _ => []
            };
            for (int p = 0; p < c[i + 1].Children.Count; p++)
                foreach (var leaf in Scalars(c[i + 1].Children[p], $"Action {i / 2} · {(p < labels.Length ? labels[p] : "Operand " + p)}", token)) yield return leaf;
        }
    }
    private static IEnumerable<(ZrdNode Node, string Label)> Scalars(ZrdNode node, string label, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node.Kind != ZrdKind.Array) { yield return (node, label); yield break; }
        for (int i = 0; i < node.Children.Count; i++) foreach (var child in Scalars(node.Children[i], label + $"[{i}]", token)) yield return child;
    }
}
