using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// The valve card (design/mw3-valves-ui.md): one MW3 valve by name. Value, Kind, Inputs and Reactions are one draft that
/// Apply accepts as one undoable change of valves.zrd; Set by and Used by list what other files say, as links.
/// </summary>
internal sealed partial class SourceSectionCard
{
    /// <summary>What the card needs beside the valve: the mission's names for its pickers, the records as stored, and why it cannot edit (or null).</summary>
    internal sealed record ValveCardContext(Mw3ValveMap Map, Mw3ValveCatalog? Catalog, IReadOnlyList<(string Text, string? Problem)> Source, string? ReadOnly);
    /// <summary>Applies the draft: the valve's name and its changes.</summary>
    internal Func<string, SourceValveEdits.ValveChanges, Task>? ApplyValve { get; set; }
    /// <summary>A Set by or Used by row's link: its vehicle, route node or valve is selected.</summary>
    internal Action<Mw3ValveLink>? ValveLinkInvoked { get; set; }
    /// <summary>Another valve of the mission is selected by name (an input, a timer's or update's target).</summary>
    internal Action<string>? ValveInvoked { get; set; }
    /// <summary>Rename… and Delete (the row's context menu has the same).</summary>
    internal Action<string, string>? ValveCommand { get; set; }
    /// <summary>The Test section's content for a valve: the Test valves button, or while testing its value, timers and activity.</summary>
    internal Func<Mw3ValveInfo, FrameworkElement>? ValveTest { get; set; }
    /// <summary>A Set by row's Fire while testing: the valve takes the value that trigger would give it. Null outside a test.</summary>
    internal Func<Mw3ValveInfo, Mw3ValveLink, Action?>? ValveFire { get; set; }

    private Mw3ValveInfo? valve;
    private ValveCardContext? valveContext;
    private TextBox? valveInitial;
    private ComboBox? valveKind;
    private StackPanel? valveInputs, valveReactions;
    private readonly List<ComboBox> valveInputBoxes = [];
    private readonly List<ValveReactionRow> valveRows = [];
    private Border? valveTestHost;
    private CardSection? valveReactionSection, valveValueSection;
    private Button? valveAddInput;

    internal Mw3ValveInfo? ShownValve => Entry is ValveEntry ? valve : null;
    private string ValveKindValue => (valveKind?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private string[] ValveInputValues => [.. valveInputBoxes.Select(b => b.Text.Trim()).Where(t => t.Length > 0)];
    /// <summary>The draft was accepted: the card waits for the rebuilt map (or is shown again) and holds no draft.</summary>
    private bool valveApplied;
    private bool ValveHasDraft => !valveApplied && Entry is ValveEntry && valve != null && valveInitial != null && (valveInitial.Text.Trim() != valve.Initial.ToString(CultureInfo.InvariantCulture)
        || ValveKindValue != (valve.Condition ?? "") || ValveKindValue.Length > 0 && !ValveInputValues.SequenceEqual(valve.Inputs) || valveRows.Any(r => r.Changed));
    private string ValveDraftFingerprint => Entry is ValveEntry && valveInitial != null
        ? string.Join("\u0001", [valveInitial.Text, ValveKindValue, .. ValveInputValues, .. valveRows.Select(r => r.Fingerprint)]) : "";
    /// <summary>The draft as the changes Apply sends (throws what a field refuses).</summary>
    internal SourceValveEdits.ValveChanges ValveChanges
    {
        get
        {
            if (valve == null || valveInitial == null) return new();
            string initial = valveInitial.Text.Trim(), kind = ValveKindValue;
            bool kindChanged = kind != (valve.Condition ?? ""), inputsChanged = kind.Length > 0 && !ValveInputValues.SequenceEqual(valve.Inputs);
            return new(initial != valve.Initial.ToString(CultureInfo.InvariantCulture) ? initial : null, kindChanged ? kind : null, kindChanged && kind.Length > 0 || inputsChanged ? ValveInputValues : null,
                [.. valveRows.Where(r => r.Changed).Select(r => new SourceValveEdits.ReactionChange(r.Original?.Occurrence, r.Original?.Spec.Kind, r.Removed ? null : r.Spec))]);
        }
    }
    private object? ValveDraft => Entry is ValveEntry && valve != null && valveInitial != null ? new
    {
        name = JsonData.ShownText(valve.Name, 256), initial = valveInitial.Text, condition = ValveKindValue.Length == 0 ? null : ValveKindValue,
        inputs = ValveInputValues.Take(64).Select(i => JsonData.ShownText(i, 256)).ToArray(),
        reactions = valveRows.Take(64).Select(r => new
        {
            occurrence = r.Original?.Occurrence, storedKind = r.Original?.Spec.Kind, kind = r.Spec.Kind, target = r.Spec.Target is { } target ? JsonData.ShownText(target, 256) : null,
            operand = r.Spec.Operand, when = r.Spec.When, x = r.Spec.X, y = r.Spec.Y, z = r.Spec.Z, yaw = r.Spec.Yaw, added = r.Original == null ? true : (bool?)null,
            removed = r.Removed ? true : (bool?)null, changed = r.Changed,
        }).ToArray(),
        editable = valveContext?.ReadOnly == null,
    } : null;

    /// <summary>A valve: its value, what sets it, its reactions and what waits on it. The same valve with a draft keeps the draft; only Test refreshes.</summary>
    internal void ShowValve(DocumentModel document, ValveEntry entry, ValveCardContext context)
    {
        if (Entry?.Key == entry.Key && ReferenceEquals(Document, document) && Applying) { valveContext = context; RefreshValveTest(); return; }
        string? lost = null;
        if (Entry?.Key == entry.Key && ReferenceEquals(Document, document) && HasDraft)
        {
            // The draft names reactions by their place in the file: it stays only while the valve is stored as it was.
            if (valve != null && SameStored(valve, entry.Valve)) { valve = entry.Valve; valveContext = context; RefreshValveTest(); return; }
            lost = "This valve changed in its file, so the unapplied changes on this card were dropped.";
        }
        Begin(entry, document);
        if (lost != null) Error(lost);
        valve = entry.Valve; valveContext = context;
        var info = entry.Valve; bool editable = context.ReadOnly == null;
        heading.Text = info.Name; heading.ToolTip = info.Name;
        subtitle.Text = "Valve · " + (info.Condition != null ? "computed" : "switch") + (info.Defined ? "" : " · no record yet");
        subtitle.ToolTip = info.Defined ? string.Join("\n", info.Records.Select(r => r.Source).Distinct(StringComparer.OrdinalIgnoreCase)) : "No valves.zrd record names it: the game makes it at 0 when a file first mentions it.";
        tile.Child = new PreviewIcon { Kind = info.Condition != null ? "ValveComputed" : "Valve", Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ((PreviewIcon)tile.Child).SetValue(TextElement.ForegroundProperty, OutlinerKinds.ValveBrush);
        if (context.ReadOnly != null) Error(lost == null ? context.ReadOnly : lost + " " + context.ReadOnly);
        foreach (string problem in info.Problems.Take(4)) body.Children.Add(ValveNotice(problem));

        valveTestHost = new Border { Margin = new(0, 0, 0, 8) };
        body.Children.Add(valveTestHost);

        // ---- Value
        var value = valveValueSection = Section("Value", info: "A valve holds a whole number; the game's files use 0 (off) and 1 (on). A computed valve has no value of its own: the game works it out from its inputs each time it is read.");
        valveInitial = new TextBox { Text = info.Initial.ToString(CultureInfo.InvariantCulture), Height = 30, Width = 84, MaxLength = 11, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = editable };
        AutomationProperties.SetName(valveInitial, "Starting value");
        valveInitial.TextChanged += (_, _) => { RefreshValveKind(); Changed(); };
        value.Body.Children.Add(Row("Starts at", valveInitial));
        valveKind = new ComboBox { Height = 30, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = editable };
        AutomationProperties.SetName(valveKind, "Kind");
        foreach (var (tag, text) in new[] { ("", "Switch"), ("any_nonzero", "On while any input is on"), ("any_zero", "On while any input is off"), ("all_nonzero", "On while all inputs are on"), ("all_zero", "On while all inputs are off") })
        {
            ComboBoxItem item = new() { Content = text, Tag = tag, ToolTip = tag.Length == 0 ? "A plain valve: triggers, timers and routes set it." : $"Stored as {tag} with a namelist." };
            valveKind.Items.Add(item);
            if (tag == (info.Condition ?? "")) valveKind.SelectedItem = item;
        }
        value.Body.Children.Add(Row("Kind", valveKind));
        valveInputs = new StackPanel();
        value.Body.Children.Add(valveInputs);
        valveInputBoxes.Clear();
        foreach (string input in info.Inputs) AddValveInput(input, editable);
        valveAddInput = ValveTextButton("Add", "Add input", "Add an input valve", () => { AddValveInput("", true).Focus(); Changed(); });
        valveAddInput.Margin = new(76, 2, 0, 2); valveAddInput.IsEnabled = editable;
        value.Body.Children.Add(valveAddInput);
        valveKind.SelectionChanged += (_, _) => { RefreshValveKind(); Changed(); };
        RefreshValveKind();

        // ---- Set by
        var setBy = Section("Set by", info: "What gives this valve a value: vehicle triggers (aiv.zrd), a target's health (targets.zrd), objectives, routes, other valves' timers, "
            + "and animations whose events 42–49 run on the scene node of this name. The game's own code can set a valve too; that is not listed.");
        setBy.Summary = info.SetBy.Count.ToString(CultureInfo.CurrentCulture);
        valveSetBy = setBy.Body;
        BuildValveSetBy();

        // ---- Reactions
        var reactions = valveReactionSection = Section("Reactions", info: "What happens when the valve changes. Each reaction waits for one value (\"when it becomes\"); timers and updates run on every change. "
            + "Reactions run in the game's fixed order: sounds, animations, scans, power, destroy, team, strategy, teleport, mission, updates, timers.");
        valveReactions = new StackPanel(); valveRows.Clear();
        reactions.Body.Children.Add(valveReactions);
        foreach (var reaction in info.Reactions.Take(64)) AddValveReaction(reaction, null, editable);
        if (info.Reactions.Count > 64) HelpLine(reactions).Text = $"and {info.Reactions.Count - 64:N0} more: edit those in a text editor";
        var add = ValveTextButton("Add", "Add reaction", "Add a reaction to this valve", () =>
        {
            AddValveReaction(null, new("sound", "", When: "1"), true).FocusFirst();
            Changed();
        });
        add.IsEnabled = editable; add.Margin = new(0, 4, 0, 0);
        reactions.Body.Children.Add(add);
        RefreshValveReactionSummary();

        // ---- Used by
        var usedBy = Section("Used by", info: "What waits on this valve: route gates (a vehicle passes only while the valve holds the value), objectives, and computed valves that read it.");
        usedBy.Summary = info.UsedBy.Count.ToString(CultureInfo.CurrentCulture);
        if (info.UsedBy.Count == 0) HelpLine(usedBy).Text = info.Reactions.Count == 0 ? "Nothing waits on it and it has no reactions: it changes nothing in play." : "No route, objective or computed valve waits on it.";
        foreach (var link in info.UsedBy.Take(24)) usedBy.Body.Children.Add(ValveLinkRow(info, link, sets: false));
        if (info.UsedBy.Count > 24) HelpLine(usedBy).Text = $"and {info.UsedBy.Count - 24:N0} more (source_ai_valve lists them all)";

        // ---- Source
        var source = Section("Source", expanded: false);
        source.Summary = info.Records.Count == 0 ? "no record" : info.Records.Count == 1 ? "1 record" : $"{info.Records.Count:N0} records";
        if (info.Records.Count == 0) HelpLine(source).Text = "No valves.zrd record yet. Apply a change, or a reaction, to write one.";
        foreach (var (text, problem) in context.Source.Take(16))
        {
            if (problem != null) source.Body.Children.Add(ValveNotice(problem));
            var block = Mono(text); block.Margin = new(0, 2, 0, 6); block.TextWrapping = TextWrapping.Wrap;
            source.Body.Children.Add(block);
        }
        if (info.Records.Count > 16) HelpLine(source).Text = $"and {info.Records.Count - 16:N0} more";
        StackPanel commands = new() { Orientation = Orientation.Horizontal, Margin = new(0, 4, 0, 0) };
        var rename = ValveTextButton("Edit", "Rename…", "Rename the valve in every file of the mission that names it", () => ValveCommand?.Invoke("rename", info.Name));
        var delete = ValveTextButton("Delete", "Delete", "Delete its valves.zrd records", () => ValveCommand?.Invoke("delete", info.Name));
        rename.IsEnabled = editable; delete.IsEnabled = editable && info.Defined; delete.Margin = new(8, 0, 0, 0);
        commands.Children.Add(rename); commands.Children.Add(delete);
        source.Body.Children.Add(commands);
        HelpLine(source).Text = "Unknown tags and comments are kept: the card changes only the values it shows.";

        RefreshValveTest();
        Finish(editable);
    }

    private static bool SameStored(Mw3ValveInfo a, Mw3ValveInfo b) => a.Initial == b.Initial && a.Condition == b.Condition && a.Inputs.SequenceEqual(b.Inputs)
        && a.Reactions.Select(r => (r.Occurrence, r.Spec)).SequenceEqual(b.Reactions.Select(r => (r.Occurrence, r.Spec)));

    /// <summary>The Test section and the Set by rows' Fire follow the test (started, ended, a value changed, time passed) without touching the draft's fields.</summary>
    internal void RefreshValveTest()
    {
        if (valveTestHost == null || valve == null || Entry is not ValveEntry) return;
        // While the test runs the same section comes back for the same valve: it moves here from the host of an earlier
        // showing of the card (an element has one parent).
        var content = ValveTest?.Invoke(valve);
        if (!ReferenceEquals(valveTestHost.Child, content))
        {
            if (content?.Parent is Border previous && !ReferenceEquals(previous, valveTestHost)) previous.Child = null;
            valveTestHost.Child = content;
        }
        valveTestHost.Visibility = valveTestHost.Child == null ? Visibility.Collapsed : Visibility.Visible;
        // A row offers Fire only while the test runs.
        bool firing = valve.SetBy.Any(link => ValveFire?.Invoke(valve, link) != null);
        if (firing != valveSetByFiring) BuildValveSetBy();
    }
    private StackPanel? valveSetBy;
    private bool valveSetByFiring;
    private void BuildValveSetBy()
    {
        if (valveSetBy == null || valve == null) return;
        valveSetBy.Children.Clear();
        valveSetByFiring = valve.SetBy.Any(link => ValveFire?.Invoke(valve, link) != null);
        if (valve.SetBy.Count == 0) valveSetBy.Children.Add(ValveHelp("Nothing in the mission's files or animations sets it. The game itself may."));
        foreach (var link in valve.SetBy.Take(24)) valveSetBy.Children.Add(ValveLinkRow(valve, link, sets: true));
        if (valve.SetBy.Count > 24) valveSetBy.Children.Add(ValveHelp($"and {valve.SetBy.Count - 24:N0} more (source_ai_valve lists them all)"));
    }
    private static TextBlock ValveHelp(string text) { var help = Help(text); help.Margin = new(0, 1, 0, 4); return help; }

    private void RefreshValveKind()
    {
        if (valveInputs == null || valveInitial == null || valveAddInput == null) return;
        bool computed = ValveKindValue.Length > 0;
        valveInputs.Visibility = valveAddInput.Visibility = computed ? Visibility.Visible : Visibility.Collapsed;
        // A computed valve ignores its stored start.
        valveInitial.Opacity = computed ? .55 : 1;
        valveInitial.ToolTip = computed ? "A computed valve ignores its starting value." : null;
        if (computed && valveInputBoxes.Count == 0) AddValveInput("", valveContext?.ReadOnly == null);
        if (valveValueSection != null) valveValueSection.Summary = computed ? Mw3ValveMap.ConditionText(ValveKindValue) : "starts at " + valveInitial.Text.Trim();
    }
    private ComboBox AddValveInput(string name, bool editable)
    {
        var box = ValveNameBox(name, valveContext?.Map.Valves.Select(v => v.Name).Where(n => n != valve?.Name), "Input valve");
        box.IsEnabled = editable;
        // Typing or choosing another input is a change of the draft, as any field's.
        box.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Changed()));
        box.SelectionChanged += (_, _) => Changed();
        valveInputBoxes.Add(box);
        DockPanel line = new() { Margin = new(76, 2, 0, 2) };
        var remove = ValveIconButton("Close", "Remove this input", () => { valveInputBoxes.Remove(box); valveInputs!.Children.Remove(line); Changed(); });
        remove.IsEnabled = editable;
        DockPanel.SetDock(remove, Dock.Right); line.Children.Add(remove);
        var open = ValveIconButton("GoTo", "Show this valve", () => { if (box.Text.Trim() is { Length: > 0 } target) ValveInvoked?.Invoke(target); });
        DockPanel.SetDock(open, Dock.Right); line.Children.Add(open);
        line.Children.Add(box);
        valveInputs!.Children.Add(line);
        return box;
    }
    private ValveReactionRow AddValveReaction(Mw3ValveReaction? original, Mw3ValveReactionSpec? spec, bool editable)
    {
        ValveReactionRow row = new(this, original, spec ?? original!.Spec, editable);
        valveRows.Add(row); valveReactions!.Children.Add(row.Panel);
        return row;
    }
    private void RefreshValveReactionSummary()
    {
        if (valveReactionSection == null) return;
        int count = valveRows.Count(r => !r.Removed);
        valveReactionSection.Summary = count.ToString(CultureInfo.CurrentCulture);
    }
    private void RestoreValve()
    {
        if (valve == null || valveInitial == null || valveKind == null || valveInputs == null || valveReactions == null) return;
        bool editable = valveContext?.ReadOnly == null;
        valveInitial.Text = valve.Initial.ToString(CultureInfo.InvariantCulture);
        valveInputBoxes.Clear(); valveInputs.Children.Clear();
        foreach (string input in valve.Inputs) AddValveInput(input, editable);
        valveKind.SelectedItem = valveKind.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == (valve.Condition ?? ""));
        valveRows.Clear(); valveReactions.Children.Clear();
        foreach (var reaction in valve.Reactions.Take(64)) AddValveReaction(reaction, null, editable);
        RefreshValveKind(); RefreshValveReactionSummary();
    }

    /// <summary>Stages changes on the open valve card as its draft (MCP source_ai_valve draft): the fields the card shows.</summary>
    internal void SetValveDraft(SourceValveEdits.ValveChanges changes)
    {
        if (Applying || !IsEnabled) throw new StudioCommandException("busy", "Finish the current source edit before changing the valve draft.");
        if (Entry is not ValveEntry || valve == null || valveInitial == null || valveKind == null) throw new StudioCommandException("not_ready", "Select the valve's World tree row first.");
        if (valveContext?.ReadOnly is { } reason) throw new StudioCommandException("unsupported", reason);
        if (changes.Initial != null) valveInitial.Text = changes.Initial;
        if (changes.Condition != null)
            valveKind.SelectedItem = valveKind.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == changes.Condition) ?? throw new StudioCommandException("invalid_argument", "condition is any_nonzero, any_zero, all_nonzero, all_zero or empty for a switch.");
        if (changes.Inputs != null)
        {
            valveInputBoxes.Clear(); valveInputs!.Children.Clear();
            foreach (string input in changes.Inputs.Take(256)) AddValveInput(input, true);
        }
        foreach (var change in changes.Reactions ?? [])
        {
            if (change.Occurrence is int occurrence)
            {
                var row = valveRows.FirstOrDefault(r => r.Original?.Occurrence == occurrence && r.Original.Spec.Kind == change.Kind) ?? throw new StudioCommandException("stale_record", "The valve has no such reaction. Read the valve again.");
                if (change.Spec == null) row.SetRemoved(true); else { row.SetRemoved(false); row.Show(change.Spec); }
            }
            else AddValveReaction(null, change.Spec ?? throw new StudioCommandException("invalid_argument", "A new reaction needs its fields."), true);
        }
        RefreshValveKind(); RefreshValveReactionSummary(); Changed();
    }

    // ---------------------------------------------------------------- rows and small controls

    private static Border ValveNotice(string text)
    {
        DockPanel content = new();
        var icon = new PreviewIcon { Kind = "Warning", Width = 13, Height = 13, Margin = new(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetValue(TextElement.ForegroundProperty, OutlinerKinds.Frozen(OutlinerKinds.LightTheme ? "#8A6A00" : "#FCE100"));
        DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Border notice = new() { Child = content, Padding = new(10, 7, 10, 7), CornerRadius = new(6), Margin = new(0, 0, 0, 8), BorderThickness = new(1) };
        notice.SetResourceReference(BackgroundProperty, "SubtleFillColorSecondaryBrush");
        notice.SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return notice;
    }
    private static string ValveLinkIcon(Mw3ValveLink link) => link.Role switch
    {
        "trigger" or "health" => "Tank", "timer" => "Timer", "fire" => "Valve", "computed" => "ValveComputed", "objective" => "Pin", "animation" => "Animation", _ => "AiNodes",
    };
    /// <summary>A Set by or Used by row: its kind's icon, who (the vehicle, node, objective or valve), what in secondary text, and its link.</summary>
    private FrameworkElement ValveLinkRow(Mw3ValveInfo info, Mw3ValveLink link, bool sets)
    {
        DockPanel row = new() { Margin = new(0, 2, 0, 2), MinHeight = 28 };
        bool linked = link.Node != null || link.Actor != null || link.Valve != null || link.Entry != null;
        if (linked)
        {
            string what = link.Valve != null ? "Show valve " + JsonData.ShownText(link.Valve, 64) : link.Entry != null ? "Show animation " + JsonData.ShownText(link.Owner, 64)
                : link.Actor != null ? "Select " + JsonData.ShownText(link.Actor, 64) : "Select " + link.Owner;
            var open = ValveIconButton("GoTo", what, () => ValveLinkInvoked?.Invoke(link));
            DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
        }
        if (sets && ValveFire?.Invoke(info, link) is { } fire)
        {
            Button button = new() { Content = "Fire", Height = 26, Padding = new(10, 0, 10, 0), Margin = new(6, 0, 0, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Test: set {JsonData.ShownText(info.Name, 64)} to {link.Value}, as this would" };
            AutomationProperties.SetName(button, $"Fire {link.Owner}");
            button.Click += (_, _) => fire();
            DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
        }
        var icon = new PreviewIcon { Kind = ValveLinkIcon(link), Width = 14, Height = 14, Margin = new(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        TextBlock text = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new Run(JsonData.ShownText(link.Owner, 96)));
        Run rest = new("  " + JsonData.ShownText(link.Text, 200)) { FontSize = 12 };
        rest.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.Inlines.Add(rest);
        text.ToolTip = link.Source.Length > 0 ? link.Source : null;
        row.Children.Add(text);
        return row;
    }
    private static Button ValveIconButton(string kind, string name, Action action)
    {
        Button button = new() { Content = new PreviewIcon { Kind = kind, Width = 13, Height = 13 }, Width = 28, Height = 28, Padding = new(0), Margin = new(4, 0, 0, 0), ToolTip = name, VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(StyleProperty, "CommandButton");
        ToolTipService.SetInitialShowDelay(button, 0);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }
    internal static Button ValveTextButton(string icon, string text, string name, Action action, bool accent = false)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        content.Children.Add(new PreviewIcon { Kind = icon, Width = 13, Height = 13, Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        Button button = new() { Content = content, Height = 30, Padding = new(10, 0, 12, 0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = name };
        if (accent) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
        AutomationProperties.SetName(button, text.TrimEnd('…'));
        button.Click += (_, _) => action();
        return button;
    }
    /// <summary>An editable name with the mission's own names to choose from; typed names are accepted (a file may be edited later).</summary>
    private static ComboBox ValveNameBox(string value, IEnumerable<string>? names, string name)
    {
        ComboBox box = new() { IsEditable = true, IsTextSearchEnabled = false, Height = 30, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 320,
            ItemsSource = names?.Order(StringComparer.OrdinalIgnoreCase).Take(4096).ToArray() ?? [], Text = value };
        AutomationProperties.SetName(box, name);
        return box;
    }

    /// <summary>What a reaction can be, by what it does; the stored tag is in each item's tooltip.</summary>
    private static readonly (string Tag, string Text)[] ValveReactionKinds =
    [
        ("sound", "Play a sound"), ("animation", "Start an animation"), ("shutdown", "Wake or power down a mech"), ("destroy", "Destroy"), ("teleport", "Teleport"),
        ("setteam", "Change team"), ("attack_strategy", "Change attack strategy"), ("scan_aim", "AI switch: aim scan"), ("scan_hit", "AI switch: hit scan"),
        ("scan_time", "AI switch: timed scan"), ("canleave", "AI switch: may leave its network"), ("delayupdate", "Set another valve later"),
        ("update", "Fire another valve's reactions"), ("missionabort", "Abort the mission"), ("missionreset", "Mission reset (no effect in the game)"), ("missioncomplete", "Mission complete (no effect in the game)"),
    ];

    /// <summary>One reaction's block: its kind, then the fields that kind stores. A new one has no original; a removed one stays, struck out, until Apply.</summary>
    private sealed class ValveReactionRow
    {
        private readonly SourceSectionCard card;
        private readonly ComboBox kind = new() { Height = 30, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly StackPanel fields = new();
        private readonly Button remove;
        private ComboBox? target, choice;
        private TextBox? operand, when, x, y, z, yaw;
        private CheckBox? once;
        private bool showing;
        internal Mw3ValveReaction? Original { get; }
        internal Border Panel { get; }
        internal bool Removed { get; private set; }
        internal bool Changed => Original == null ? !Removed : Removed || Spec != Original.Spec;
        internal string Fingerprint => $"{Original?.Occurrence}|{Original?.Spec.Kind}|{Removed}|{Spec}";
        private string BaseKind => (kind.SelectedItem as ComboBoxItem)?.Tag as string ?? "sound";

        internal ValveReactionRow(SourceSectionCard card, Mw3ValveReaction? original, Mw3ValveReactionSpec spec, bool editable)
        {
            this.card = card; Original = original;
            foreach (var (tag, text) in ValveReactionKinds)
            {
                // The two reactions without an effect are offered only where a file already stores them.
                if (tag is "missionreset" or "missioncomplete" && spec.Kind != tag) continue;
                kind.Items.Add(new ComboBoxItem { Content = text, Tag = tag, ToolTip = "Stored as " + tag + (tag is "sound" or "animation" ? $" (or {tag}_once)" : "") });
            }
            AutomationProperties.SetName(kind, "Reaction");
            remove = ValveIconButton("Close", "Remove this reaction", () => { SetRemoved(!Removed); card.RefreshValveReactionSummary(); card.Changed(); });
            DockPanel top = new();
            DockPanel.SetDock(remove, Dock.Right); top.Children.Add(remove); top.Children.Add(kind);
            StackPanel content = new(); content.Children.Add(top); content.Children.Add(fields);
            Panel = new Border { Child = content, Padding = new(8, 8, 6, 6), Margin = new(0, 0, 0, 6), CornerRadius = new(5), BorderThickness = new(1) };
            Panel.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            Panel.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            if (original?.Problem != null) Panel.ToolTip = original.Problem;
            kind.IsEnabled = remove.IsEnabled = editable;
            Show(spec, editable);
            kind.SelectionChanged += (_, _) => { if (showing) return; Build(new(BaseKind, Spec.Target, null, BaseKind is "update" or "delayupdate" ? null : Spec.When ?? "1"), true); card.Changed(); };
        }
        internal void FocusFirst() => kind.Focus();
        internal void SetRemoved(bool removed)
        {
            Removed = removed;
            fields.Opacity = kind.Opacity = removed ? .4 : 1;
            fields.IsEnabled = kind.IsEnabled = !removed;
            remove.ToolTip = removed ? "Keep this reaction" : "Remove this reaction";
            AutomationProperties.SetName(remove, (string)remove.ToolTip);
            ((PreviewIcon)remove.Content).Kind = removed ? "Undo" : "Close";
            // A new reaction that is removed again simply goes.
            if (removed && Original == null) { card.valveRows.Remove(this); card.valveReactions?.Children.Remove(Panel); }
        }
        internal void Show(Mw3ValveReactionSpec spec, bool editable = true)
        {
            showing = true;
            try
            {
                string tag = spec.Kind is "sound_once" ? "sound" : spec.Kind is "animation_once" ? "animation" : spec.Kind;
                kind.SelectedItem = kind.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
                Build(spec, editable);
            }
            finally { showing = false; }
        }
        /// <summary>The fields as the reaction they describe.</summary>
        internal Mw3ValveReactionSpec Spec
        {
            get
            {
                string tag = BaseKind;
                if (tag is "sound" or "animation" && once?.IsChecked == true) tag += "_once";
                string? Text(TextBox? box) => box?.Text.Trim();
                return new(tag, target?.Text.Trim(), choice != null ? (choice.SelectedItem as ComboBoxItem)?.Tag as string : Text(operand), Text(when), Text(x), Text(y), Text(z), Text(yaw));
            }
        }
        private void Build(Mw3ValveReactionSpec spec, bool editable)
        {
            fields.Children.Clear(); fields.IsEnabled = editable && !Removed;
            target = null; choice = null; operand = when = x = y = z = yaw = null; once = null;
            var catalog = card.valveContext?.Catalog; var valves = card.valveContext?.Map.Valves.Select(v => v.Name);
            string tag = BaseKind;
            void Target(string caption, IEnumerable<string>? names, bool isValve = false)
            {
                target = ValveNameBox(spec.Target ?? "", names, caption);
                target.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => card.Changed()));
                FrameworkElement value = target;
                if (isValve)
                {
                    DockPanel line = new();
                    var box = target;
                    var open = ValveIconButton("GoTo", "Show this valve", () => { if (box.Text.Trim() is { Length: > 0 } name) card.ValveInvoked?.Invoke(name); });
                    DockPanel.SetDock(open, Dock.Right); line.Children.Add(open); line.Children.Add(target);
                    value = line;
                }
                fields.Children.Add(Field(caption, value));
            }
            TextBox Number(string caption, string? text, string name, string? suffix = null, double width = 84)
            {
                TextBox box = new() { Text = text ?? "", Height = 30, Width = width, MaxLength = 24, HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetName(box, name);
                box.TextChanged += (_, _) => card.Changed();
                FrameworkElement value = box;
                if (suffix != null)
                {
                    StackPanel line = new() { Orientation = Orientation.Horizontal }; line.Children.Add(box);
                    TextBlock unit = new() { Text = suffix, FontSize = 12, Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    unit.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                    line.Children.Add(unit); value = line;
                }
                fields.Children.Add(Field(caption, value));
                return box;
            }
            void Choice(string caption, (string Value, string Text)[] options)
            {
                choice = new ComboBox { Height = 30, HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(choice, caption);
                foreach (var (value, text) in options) choice.Items.Add(new ComboBoxItem { Content = text, Tag = value });
                // A stored value the choice does not name (2, say) is kept and shown as it is.
                if (spec.Operand != null && options.All(o => o.Value != spec.Operand)) choice.Items.Add(new ComboBoxItem { Content = "Stored: " + spec.Operand, Tag = spec.Operand });
                choice.SelectedItem = choice.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (spec.Operand ?? options[0].Value));
                choice.SelectionChanged += (_, _) => card.Changed();
                fields.Children.Add(Field(caption, choice));
            }
            void When() => when = Number("When it becomes", spec.When ?? "1", "Triggering value");
            switch (tag)
            {
                case "sound" or "animation":
                    // The mission's sound banks are not read for the card: the picker offers the sounds its valves already play.
                    Target(tag == "sound" ? "Sound" : "Animation", tag == "sound" ? catalog?.Sounds ?? card.valveContext?.Map.Valves.SelectMany(v => v.Reactions)
                        .Where(r => r.Spec.Kind is "sound" or "sound_once" && r.Spec.Target is { Length: > 0 }).Select(r => r.Spec.Target!).Distinct(StringComparer.Ordinal) : catalog?.Animations);
                    When();
                    once = new CheckBox { Content = "Only the first time", IsChecked = spec.Kind.EndsWith("_once", StringComparison.Ordinal), Margin = new(108, 2, 0, 0), FontSize = 12.5 };
                    once.Checked += (_, _) => card.Changed(); once.Unchecked += (_, _) => card.Changed();
                    fields.Children.Add(once);
                    break;
                case "shutdown": Target("Mech", catalog?.Vehicles); Choice("Do", [("0", "Wake (power up)"), ("1", "Power down")]); When(); break;
                case "destroy": Target("Vehicle or target", catalog?.Vehicles); When(); break;
                case "teleport":
                    Target("Vehicle", catalog?.Vehicles);
                    x = Number("X", spec.X ?? "0", "X"); y = Number("Y", spec.Y ?? "0", "Y"); z = Number("Z", spec.Z ?? "0", "Z");
                    yaw = Number("Heading", spec.Yaw ?? "0", "Heading"); When();
                    break;
                case "setteam": Target("Vehicle or target", catalog?.Vehicles); operand = Number("Team", spec.Operand ?? "0", "Team"); When(); break;
                case "attack_strategy": Target("Vehicle", catalog?.Vehicles); operand = Number("Strategy", spec.Operand ?? "0", "Attack strategy"); When(); break;
                case "scan_aim" or "scan_hit" or "scan_time" or "canleave": Target("Vehicle", catalog?.Vehicles); Choice("Switch", [("1", "On"), ("0", "Off")]); When(); break;
                case "delayupdate":
                    Target("Valve", valves, isValve: true); operand = Number("After", spec.Operand ?? "1", "Delay in seconds", "s");
                    operand.ToolTip = "Seconds after this valve changes. The other valve then takes the value this one had.";
                    break;
                case "update": Target("Valve", valves, isValve: true); break;
                default: When(); break;
            }
        }
        private static Grid Field(string caption, FrameworkElement value)
        {
            Grid row = Row(caption, value, 108); row.Margin = new(0, 4, 0, 0);
            return row;
        }
    }
}
