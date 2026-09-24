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

public partial class AnimationEditor
{
    protected override void RefreshProperties()
    {
        foreach (var refresh in referenceRefresh.ToArray()) refresh();
        UpdateInspection();
    }
    private void UpdateInspection()
    {
        if (Event is { } ev) InspectionChanged?.Invoke(ev.ToJson(),ev.Bytes);
        else if (Sequence is { } sequence) InspectionChanged?.Invoke(new JsonObject { ["name"] = sequence.Name,["id"] = sequence.Id.ToString(),["resetState"] = sequence.ResetMode,["eventCount"] = sequence.Events.Count,["sourceOffset"] = sequence.SourceOffset,["headerHex"] = Convert.ToHexString(sequence.Bytes),["opaqueTailHex"] = Convert.ToHexString(sequence.OpaqueTail),["events"] = new JsonArray(sequence.Events.Select(e => (JsonNode)e.ToJson()).ToArray()) },sequence.Bytes);
        else InspectionChanged?.Invoke(Entry.ToJson(),Entry.Bytes);
    }
    private List<ChoiceValue> References(int table)
    {
        List<ChoiceValue> choices = [];
        if (table == 1) { choices.Add(new(-100,"−100: bound root")); choices.Add(new(-200,"−200: activation reference (root in preview)")); }
        for (int i = 0; i < Entry.References[table].Count; i++) choices.Add(new(i,$"{i}: {(i == 0 ? "(reserved)" : Entry.References[table][i].Text(0,32))}"));
        if (choices.All(c => c.Value != 0)) choices.Add(new(0,"0: none")); return choices;
    }
    private void InitializeActivationForm()
    {
        inputScope = "preview"; StackPanel panel = new(); ActivationHost.Content = panel;
        Label(panel,"Optional activation points",true);
        Label(panel,"Blank points use each event handler’s defaults, not zero coordinates. Origin can fall back to the bound root; beams use a target 10 units along +Z; distance tests require an explicit target.");
        Input(panel,"Origin XYZ","",text => SetActivation(text,true),components:["X","Y","Z"]);
        Input(panel,"Target XYZ","",text => SetActivation(text,false),components:["X","Y","Z"]); inputScope = "properties";
    }
    private void SetActivation(string text,bool start)
    {
        Vector3? point = null;
        if (text.Replace(",", "").Trim().Length != 0) { AnimationRecord record = new(new byte[12]); new AnimationField("",0,AnimationFieldKind.Vector).Write(record,text); point = record.Vector(0); }
        if (start) activationStart = point; else activationTarget = point; resetSimulation = true; _ = SeekAsync(frame?.Time ?? 0);
    }
    private void RefreshReferences()
    {
        string[] names = ["Tracked nodes","Scene nodes","Lights","Sound nodes","Samples","Effect templates","Activation conditions","Child slots"];
        DockPanel page = new() { Margin = new(8) }; StackPanel header = new(); DockPanel.SetDock(header,Dock.Top); page.Children.Add(header);
        Label(header,"Stored references",true); Label(header,"Stable table indices. Reserved index 0 and unverified tables are read-only.");
        Label(header,"Changing a verified name retargets every event in this entry that uses that table slot. Indices and ordering stay unchanged.");
        ComboBox category = new() { ItemsSource = names.Select((n,i) => $"{n} ({Entry.References[i].Count})").ToArray(),SelectedIndex = 1,Margin = new(0,0,0,6) }; header.Children.Add(category);
        Grid body = new(); body.RowDefinitions.Add(new()); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); page.Children.Add(body);
        ListBox list = new() { DisplayMemberPath = nameof(ChoiceValue.Label) }; body.Children.Add(list);
        StackPanel edit = new(); Grid.SetRow(edit,1); body.Children.Add(edit); int table = 1, selectedIndex = -1; bool syncing = false;
        void RefreshLabels()
        {
            var values = References(table).Where(v => v.Value >= 0).ToArray();
            if (list.Items.OfType<ChoiceValue>().SequenceEqual(values)) return;
            syncing = true; list.ItemsSource = values; list.SelectedItem = values.FirstOrDefault(v => v.Value == selectedIndex); syncing = false;
        }
        void LoadTable() { selectedIndex = -1; RefreshLabels(); list.SelectedIndex = 0; }
        list.SelectionChanged += (_,_) =>
        {
            if (syncing || list.SelectedItem is not ChoiceValue selected) return;
            if (!ResolvePendingDrafts()) { syncing = true; list.SelectedItem = list.Items.OfType<ChoiceValue>().FirstOrDefault(v => v.Value == selectedIndex); syncing = false; return; }
            draftInputs.RemoveAll(d => d.Scope == "references"); referenceRefresh.Clear(); referenceRefresh.Add(RefreshLabels); edit.Children.Clear(); int t = table,index = selected.Value; selectedIndex = index;
            if (index >= Entry.References[t].Count) return;
            inputScope = "references";
            string Read() => Entry.References[t][index].Text(0,Math.Min(32,Entry.References[t][index].Bytes.Length));
            Input(edit,$"[{index}] name",Read(),text => TryEdit(() => edits.Apply(entryIndex,"Retarget reference",e => e.References[t][index].SetText(0,text))),t is 0 or 6 or 7 || index == 0,getter:Read);
            inputScope = "properties";
        };
        category.SelectionChanged += (_,_) => { if (category.SelectedIndex == table) return; if (!ResolvePendingDrafts()) { category.SelectedIndex = table; return; } table = category.SelectedIndex; LoadTable(); };
        LoadTable(); referencesPage.Content = page;
    }
}
