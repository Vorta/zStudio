using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// The docked Properties pane's card for a World tree row that is no scene object: a zone (its label is edited here), an AI
/// network (its header settings are edited here, in its source file) or an animation definition (read-only). Edits are a
/// draft until Apply (Enter in a zone's label), which accepts one undoable source change; Revert or Escape restores.
/// </summary>
internal sealed class SourceSectionCard : Border
{
    private readonly Border tile = new() { Width = 34, Height = 34, CornerRadius = new(6), BorderThickness = new(1), Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock heading = new() { FontWeight = FontWeights.SemiBold, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock subtitle = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    private readonly StackPanel body = new();
    private readonly Border footer = new() { Padding = new(14, 6, 10, 6), BorderThickness = new(0, 1, 0, 0) };
    private readonly TextBlock footerStatus = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Shapes.Ellipse footerDot = new() { Width = 6, Height = 6, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button apply, revert;

    /// <summary>What the card shows: the row's entry (zone, network or definition).</summary>
    internal SectionEntry? Entry { get; private set; }
    internal DocumentModel? Document { get; private set; }
    internal bool Applying { get; private set; }
    internal Func<byte, string, Task>? ApplyLabel { get; set; }
    internal Action<byte>? PaintZone { get; set; }
    internal Func<string, IReadOnlyList<SourceAiNetworkEdits.Change>, Task>? ApplyNetwork { get; set; }
    internal Action<int>? SelectObject { get; set; }
    internal Action? CloseRequested { get; set; }
    /// <summary>The draft opened, changed or closed (the window's draft guards and footer follow).</summary>
    internal event Action? DraftChanged;

    // The zone's label field and the network's fields: each with the text it started from.
    private TextBox? label;
    private string labelInitial = "";
    private readonly List<NetworkInput> inputs = [];
    private sealed record NetworkInput(SourceAiNetworkEdits.Field Field, Control[] Boxes, string[] Initial, TextBlock Problem)
    {
        internal string[] Values => [.. Boxes.Select(Value)];
        internal bool Changed => !Values.SequenceEqual(Initial);
        private static string Value(Control box) => box switch
        {
            TextBox text => text.Text.Trim(),
            ComboBox combo => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
            CheckBox check => check.IsChecked == true ? "1" : "0",
            _ => "",
        };
    }
    private Guid draftId = Guid.NewGuid();

    internal SourceSectionCard()
    {
        Padding = new(12, 12, 8, 10);
        DockPanel title = new() { Margin = new(0, 0, 0, 2) };
        Button close = new() { Content = new PreviewIcon { Kind = "Close", Width = 16, Height = 16 }, ToolTip = "Clear selection (Esc)" };
        close.SetResourceReference(StyleProperty, "CardCloseButton");
        AutomationProperties.SetName(close, "Clear selection");
        close.Click += (_, _) => CloseRequested?.Invoke();
        DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
        tile.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        tile.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        DockPanel.SetDock(tile, Dock.Left); title.Children.Add(tile);
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        StackPanel names = new() { VerticalAlignment = VerticalAlignment.Center }; names.Children.Add(heading); names.Children.Add(subtitle);
        title.Children.Add(names);
        error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        apply = FooterButton("Apply", "Apply", "Apply changes", "AccentButtonStyle", () => _ = ApplyAsync());
        revert = FooterButton("Discard", "Revert", "Revert changes", "DefaultButtonStyle", () => Discard());
        DockPanel foot = new();
        DockPanel.SetDock(apply, Dock.Right); DockPanel.SetDock(revert, Dock.Right);
        apply.Margin = new(6, 0, 0, 0); revert.Margin = new(6, 0, 0, 0);
        foot.Children.Add(apply); foot.Children.Add(revert);
        footerStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        StackPanel status = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; status.Children.Add(footerDot); status.Children.Add(footerStatus);
        foot.Children.Add(status);
        footer.Child = foot; footer.Margin = new(-12, 4, -8, -10);
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Grid layout = new();
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(title);
        StackPanel content = new(); content.Children.Add(error); content.Children.Add(body);
        ScrollViewer scroll = new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 6, 0, 0) };
        scroll.SetResourceReference(Control.TemplateProperty, "InspectionScrollTemplate");
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Child = layout;
        AutomationProperties.SetName(this, "Section properties");
    }

    // ---------------------------------------------------------------- what is shown

    /// <summary>A zone: its label (a draft until Apply or Enter) and what uses it.</summary>
    internal void ShowZone(DocumentModel document, SourceZoneRow zone, string? master, bool painting, bool labelsReadable, string? labelsProblem)
    {
        bool same = Entry is ZoneEntry previous && previous.Zone.Zone == zone.Zone && ReferenceEquals(Document, document);
        string? draft = same && HasDraft ? label?.Text : null;
        Begin(new ZoneEntry(zone), document);
        heading.Text = zone.Zone == 255 ? "Any" : "Zone " + zone.Zone.ToString(CultureInfo.InvariantCulture);
        subtitle.Text = "Map zone" + (master != null ? " · " + System.IO.Path.GetFileName(master) : "");
        subtitle.ToolTip = master != null ? $"Zone labels are saved in the master model {master}" : null;
        tile.Child = new Border { Width = 18, Height = 18, CornerRadius = new(4), Background = ZonePicker.Swatch(zone.Zone), BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF)) };
        var section = Section("Zone");
        if (zone.Zone == 255)
        {
            section.Body.Children.Add(Row("Label", Plain("Any")));
            section.Body.Children.Add(Help("The Any zone keeps its name."));
        }
        else
        {
            label = new TextBox { MaxLength = 256, Height = 32, Text = draft ?? zone.Label ?? "", IsEnabled = labelsReadable };
            labelInitial = zone.Label ?? "";
            AutomationProperties.SetName(label, "Label of " + heading.Text.ToLowerInvariant());
            label.TextChanged += (_, _) => Changed();
            label.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; _ = ApplyAsync(); }
                else if (e.Key == Key.Escape && HasDraft) { e.Handled = true; Discard(); }
            };
            section.Body.Children.Add(Row("Label", WithHint(label, "No label", stretch: true)));
            section.Body.Children.Add(Help(labelsReadable ? "Saved with the map. Empty removes it." : labelsProblem ?? "Zone labels could not be read."));
        }
        var use = Section("Use", info: "Polygons make this zone current when the camera or a vehicle is on them, counted as placed (a model placed twice counts twice). Objects are drawn only while this zone is current.");
        if (!zone.Used) use.Body.Children.Add(Help("Not used by any polygon or object. Its label stays with the map."));
        else
        {
            use.Body.Children.Add(Row("Polygons", Plain(zone.Polygons > 0 ? zone.Polygons.ToString("N0", CultureInfo.CurrentCulture) + " (as placed)" : "None")));
            if (zone.Polygons > 0) use.Body.Children.Add(Row("Placements", Plain($"{zone.Placements:N0}, of {zone.Models:N0} model{(zone.Models == 1 ? "" : "s")}")));
            use.Body.Children.Add(Row("Objects", Plain(zone.Objects > 0 ? $"{zone.Objects:N0} ({zone.OwnObjects:N0} own, {zone.InheritedObjects:N0} inherited)"
                + (zone.EmptyObjects > 0 ? $", including {zone.EmptyObjects:N0} empty node{(zone.EmptyObjects == 1 ? "" : "s")}" : "") : "None")));
        }
        Button paint = new() { Height = 30, Margin = new(76, 8, 0, 2), HorizontalAlignment = HorizontalAlignment.Left, Padding = new(10, 0, 12, 0), IsEnabled = !painting,
            ToolTip = $"Engage Edit zones with {heading.Text.ToLowerInvariant()} (P)" };
        StackPanel paintContent = new() { Orientation = Orientation.Horizontal };
        paintContent.Children.Add(new PreviewIcon { Kind = "Brush", Width = 14, Height = 14, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        paintContent.Children.Add(new TextBlock { Text = painting ? "Painting with this zone" : "Paint with this zone", VerticalAlignment = VerticalAlignment.Center });
        paint.Content = paintContent;
        AutomationProperties.SetName(paint, "Paint with this zone");
        paint.Click += (_, _) => PaintZone?.Invoke(zone.Zone);
        use.Body.Children.Add(paint);
        Finish(editable: zone.Zone != 255);
    }

    /// <summary>An AI network: its header settings as stored, each a field of one draft.</summary>
    internal void ShowNetwork(DocumentModel document, AiNetwork network, SourceAiNetworkEdits.Header header, bool text)
    {
        bool same = Entry is NetworkEntry previous && previous.Network.Id == network.Id && ReferenceEquals(Document, document);
        Dictionary<string, string[]> kept = same && HasDraft ? inputs.Where(i => i.Changed).ToDictionary(i => i.Field.Key, i => i.Values) : [];
        Begin(new NetworkEntry(network), document);
        string stem = SourceMapSections.NetworkStem(network);
        heading.Text = stem;
        subtitle.Text = $"AI network · {network.Nodes.Count} node{(network.Nodes.Count == 1 ? "" : "s")}";
        subtitle.ToolTip = network.Id;
        var color = OutlinerKinds.Frozen(zStudio.Rendering.AiNetworkColors.Hex(network.AttackStrategy));
        tile.Child = new PreviewIcon { Kind = "AiNodes", Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ((PreviewIcon)tile.Child).SetValue(TextElement.ForegroundProperty, color);
        if (!text) Error($"{network.Id} is stored as binary zReader data; its settings can be edited only in a text source.");
        var main = Section("Network");
        TextBlock file = Plain(network.Id); file.FontFamily = new("Cascadia Mono, Consolas"); file.FontSize = 11.5; file.TextWrapping = TextWrapping.Wrap;
        file.ToolTip = $"The network ID is the file's number ({stem.Replace("net_", "", StringComparison.OrdinalIgnoreCase)}). AI vehicles name it in aiv.zrd, so it is not edited here.";
        main.Body.Children.Add(Row("File", file));
        Field(main, "name", header, text, kept, help: $"name · up to {SourceAiNetworkEdits.MaximumNameLength} characters");
        Field(main, "type", header, text, kept, help: "type · the game reads the first two letters");
        var attack = Section("Attack");
        attack.Summary = OutlinerKinds.StrategyName(network.AttackStrategy.Kind);
        Field(attack, "attack_strategy", header, text, kept, help: "attack_strategy · missing or unknown: head-on");
        Field(attack, "attack_rad", header, text, kept, caption: "Radius", help: "attack_rad · 0 → about 38.7");
        Field(attack, "attack_dwell", header, text, kept, caption: "Dwell", help: "attack_dwell · seconds; 0 → 10 s");
        Field(attack, "attack_buddy", header, text, kept, help: "attack_buddy · vehicles on this network alert each other");
        var movement = Section("Movement");
        Field(movement, "path_width", header, text, kept, caption: "Path width", help: "path_width · default 10", placeholder: "10");
        Field(movement, "activate_rad", header, text, kept, caption: "Activation", help: "activate_rad · activation radius");
        Field(movement, "pursuit_params", header, text, kept, caption: "Pursuit", help: (header.Fields["pursuit_params"]?.Key ?? "pursuit_params") + " · two values");
        Field(movement, "not_pursuit_dwell", header, text, kept, caption: "Not-pursuit dwell", help: "not_pursuit_dwell");
        Field(movement, "return_range", header, text, kept, caption: "Return range", help: "return_range");
        Field(movement, "hide_times", header, text, kept, caption: "Hide times", help: "hide_times · when not stored: 8 and 4", placeholders: ["8", "4"]);
        var nodes = Section("Nodes");
        nodes.Summary = $"{network.Nodes.Count} node{(network.Nodes.Count == 1 ? "" : "s")}";
        int links = network.Nodes.Sum(n => n.PreviewLinks.Count(l => l.Target != null)); long slots = network.Nodes.Sum(n => (long)n.LinkCount);
        nodes.Body.Children.Add(Row("Nodes", Plain(network.Nodes.Count.ToString("N0", CultureInfo.CurrentCulture))));
        nodes.Body.Children.Add(Row("Links", Plain($"{links:N0} of {slots:N0} slots")));
        nodes.Body.Children.Add(Row("Diagnostics", Plain(network.Diagnostics.Count == 0 ? "None" :
            string.Join("\n", network.Diagnostics.Take(8).Select(d => JsonData.ShownText(d.Message, 240))) + (network.Diagnostics.Count > 8 ? $"\n{network.Diagnostics.Count - 8} more" : ""))));
        nodes.Body.Children.Add(Help("Move nodes by selecting them in the view."));
        if (header.Other.Count > 0)
        {
            var other = Section("Other stored keys", expanded: false);
            foreach (var (key, value) in header.Other)
                other.Body.Children.Add(Row(key, Plain(key == "activate_buddy" ? value + " (the game never reads it)" : value)));
        }
        Finish(editable: text);
    }

    /// <summary>An animation definition, read-only: its source, what it binds to, its settings, sequences and notes.</summary>
    internal void ShowDefinition(DocumentModel document, DefinitionEntry entry, IReadOnlyList<(int Row, string Name, string Parent)> roots, IReadOnlyList<string> notes, string? rejection, int outlined)
    {
        Begin(entry, document);
        var definition = entry.Definition;
        heading.Text = definition.Name;
        subtitle.Text = $"Animation definition · {System.IO.Path.GetFileName(definition.File)} #{definition.Ordinal + 1}";
        subtitle.ToolTip = $"{definition.File} · definition {definition.Ordinal + 1}" + (entry.Listing.Occurrence > 0 ? " · listed again" : "");
        tile.Child = new PreviewIcon { Kind = "Definition", Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ((PreviewIcon)tile.Child).SetValue(TextElement.ForegroundProperty, OutlinerKinds.SectionBrush(entry));
        var source = Section("Source");
        source.Body.Children.Add(Row("File", Mono(definition.File)));
        source.Body.Children.Add(Row("Definition", Plain($"{definition.Ordinal + 1} in this file")));
        source.Body.Children.Add(Row("Listed by", Mono((entry.Listing.ListedBy ?? "the map") + (entry.Listing.Occurrence > 0 ? " · this file is listed again here" : ""))));
        var binding = Section("Binding");
        binding.Summary = !entry.BindingKnown ? "—" : roots.Count == 1 ? "1 root" : $"{roots.Count:N0} roots";
        binding.Body.Children.Add(Row("Root name", Mono(definition.RootName + (definition.Pattern ? "   (each * is one digit)" : ""))));
        binding.Body.Children.Add(Row("Attach", definition.Attach is { } attach ? Mono(attach) : Plain("Same as the root")));
        if (!entry.BindingKnown) binding.Body.Children.Add(Help("The animations of this map did not build, so binding is unknown." + (rejection != null ? " " + rejection : "")));
        else if (roots.Count == 0) binding.Body.Children.Add(Help($"No node in this world is named {definition.RootName}, so this map has no animation from it."));
        else
        {
            StackPanel links = new();
            foreach (var (row, name, parent) in roots.Take(16))
            {
                DockPanel line = new() { Height = 30 };
                TextBlock under = new() { Text = parent, FontSize = 11, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
                under.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                DockPanel.SetDock(under, Dock.Right); line.Children.Add(under);
                line.Children.Add(new PreviewIcon { Kind = "Cube", Width = 14, Height = 14, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                line.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                Button pick = new() { Content = line, Height = 30, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(8, 0, 8, 0) };
                pick.SetResourceReference(StyleProperty, "CommandButton");
                pick.Width = double.NaN; pick.HorizontalAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetName(pick, "Select " + name);
                pick.Click += (_, _) => SelectObject?.Invoke(row);
                links.Children.Add(pick);
            }
            binding.Body.Children.Add(Row("Bound to", links));
            if (roots.Count > 16) binding.Body.Children.Add(Help($"and {roots.Count - 16:N0} more"));
            if (roots.Count > outlined) binding.Body.Children.Add(Help($"Outlined in the view: the first {outlined:N0} of {roots.Count:N0}."));
        }
        var settings = Section("Settings");
        settings.Body.Children.Add(Row("Activation", Plain(ActivationText(definition.Activation))));
        settings.Body.Children.Add(Row("Health", Plain(definition.Health ?? "Not set")));
        settings.Body.Children.Add(Row("Reset time", Plain(definition.ResetTime ?? "Not set")));
        settings.Body.Children.Add(Row("Execution", Plain(definition.Execution ?? "Not set")));
        settings.Body.Children.Add(Row("Priority", Plain(definition.Priority ?? "Not set")));
        settings.Body.Children.Add(Row("Requires", Plain(definition.Prerequisites ?? "None")));
        var sequences = Section("Sequences");
        sequences.Summary = definition.SequenceCount.ToString(CultureInfo.CurrentCulture);
        sequences.Body.Children.Add(Row("Cleanup", Plain($"RESET_STATE · {Events(definition.CleanupEvents)}")));
        foreach (var sequence in definition.Sequences)
            sequences.Body.Children.Add(Row(sequence.Name.Length == 0 ? "(unnamed)" : sequence.Name, Plain(Events(sequence.Events))));
        if (definition.SequenceCount > definition.Sequences.Count) sequences.Body.Children.Add(Help($"{definition.SequenceCount - definition.Sequences.Count:N0} more"));
        if (definition.Scripts.Count > 0)
            sequences.Body.Children.Add(Row("Keyframe scripts", Mono(string.Join("\n", definition.Scripts) + (definition.ScriptCount > definition.Scripts.Count ? $"\n{definition.ScriptCount - definition.Scripts.Count} more" : ""))));
        if (notes.Count > 0)
        {
            var section = Section("Notes");
            section.Summary = notes.Count.ToString(CultureInfo.CurrentCulture);
            foreach (string note in notes) section.Body.Children.Add(Help(note));
        }
        Finish(editable: false);

        static string Events(int count) => count == 1 ? "1 event" : $"{count:N0} events";
        static string ActivationText(string? keyword) => keyword switch
        {
            "ON_CALL" => "On call · ON_CALL", "ON_STARTUP" => "On startup · ON_STARTUP", "WEAPON_HIT" => "Weapon hit · WEAPON_HIT",
            "COLLIDE_HIT" => "Collision · COLLIDE_HIT", "WEAPON_OR_COLLIDE_HIT" => "Weapon hit or collision · WEAPON_OR_COLLIDE_HIT",
            null => "Not set (weapon hit)", var other => other,
        };
    }

    // ---------------------------------------------------------------- the draft

    /// <summary>The zone's label or a network's field differs from what is stored.</summary>
    internal bool HasDraft => Entry is ZoneEntry && label != null && label.Text.Trim() != labelInitial || Entry is NetworkEntry && inputs.Any(i => i.Changed);
    internal string DraftToken => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{draftId}|{Document?.SessionId}|{Document?.Revision}|{Entry?.Key}|{(label?.Text ?? "")}|{string.Join("\u0001", inputs.Select(i => string.Join("\u0002", i.Values)))}")));
    /// <summary>The changes a network's draft makes, as requested values.</summary>
    internal IReadOnlyList<SourceAiNetworkEdits.Change> NetworkChanges => [.. inputs.Where(i => i.Changed).Select(i => new SourceAiNetworkEdits.Change(i.Field.Key, i.Values))];
    internal object Describe() => new
    {
        token = DraftToken, pending = HasDraft, kind = Entry switch { ZoneEntry => "zone", NetworkEntry => "network", DefinitionEntry => "definition", _ => null },
        target = Entry?.Key, revision = Document?.Revision, applying = Applying,
        label = Entry is ZoneEntry ? label?.Text : null,
        fields = Entry is NetworkEntry ? inputs.Select(i => new { key = i.Field.Key, values = i.Values, stored = i.Initial, changed = i.Changed }).ToArray() : null,
    };
    internal void RequireDraft(string token) { if (!HasDraft || token != DraftToken) throw new StudioCommandException("draft_conflict", "The section card draft changed. Read it again."); }
    /// <summary>Restores every field to what is stored.</summary>
    internal void Discard()
    {
        if (Applying) return;
        if (label != null) label.Text = labelInitial;
        foreach (var input in inputs)
            for (int i = 0; i < input.Boxes.Length; i++) Restore(input.Boxes[i], input.Initial[i]);
        Error(null); Changed();
    }
    /// <summary>Accepts the draft: one undoable source change, the card then shows what was stored.</summary>
    internal async Task ApplyAsync()
    {
        if (!HasDraft || Applying) return;
        Applying = true; Error(null); RefreshFooter();
        try
        {
            if (Entry is ZoneEntry zone && ApplyLabel != null) await ApplyLabel(zone.Zone.Zone, label!.Text.Trim());
            else if (Entry is NetworkEntry network && ApplyNetwork != null) await ApplyNetwork(network.Network.Id, NetworkChanges);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is StudioCommandException or System.IO.InvalidDataException or InvalidOperationException) { Error(ex.Message); }
        finally { Applying = false; RefreshFooter(); DraftChanged?.Invoke(); }
    }
    /// <summary>Asks about a pending draft: Yes applies it, No discards it, Cancel keeps editing.</summary>
    internal async Task<bool> ResolvePendingAsync()
    {
        if (Applying) return false;
        if (!HasDraft) return true;
        var answer = MessageBox.Show(Window.GetWindow(this), (Entry is ZoneEntry ? "Apply the pending zone label?" : "Apply the pending network changes?") + "\nYes: apply · No: discard · Cancel: keep editing",
            Entry is ZoneEntry ? "Zone label draft" : "AI network draft", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) { Discard(); return !HasDraft; }
        await ApplyAsync();
        return !HasDraft;
    }
    internal void FocusFirst() { if (label != null) { label.Focus(); label.SelectAll(); } else if (inputs.FirstOrDefault()?.Boxes[0] is { } first) first.Focus(); }

    // ---------------------------------------------------------------- building

    private void Begin(SectionEntry entry, DocumentModel document)
    {
        if (Entry?.Key != entry.Key || !ReferenceEquals(Document, document)) draftId = Guid.NewGuid();
        Entry = entry; Document = document;
        body.Children.Clear(); inputs.Clear(); label = null; labelInitial = "";
        if (!Applying) Error(null);
    }
    private void Finish(bool editable)
    {
        footer.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        RefreshFooter();
    }
    private void Changed()
    {
        foreach (var input in inputs) Validate(input);
        RefreshFooter(); DraftChanged?.Invoke();
    }
    private void RefreshFooter()
    {
        bool changed = HasDraft;
        apply.IsEnabled = revert.IsEnabled = changed && !Applying;
        footerStatus.Text = Applying ? "Applying…" : changed ? "Unsaved changes" : "No changes";
        if (changed || Applying) footerDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentFillColorDefaultBrush");
        else footerDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextFillColorTertiaryBrush");
    }
    private void Error(string? message) { error.Text = message ?? ""; error.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible; }

    /// <summary>One header field: its control(s), as stored (or empty with the game's value as a hint), and its help line.</summary>
    private void Field(CardSection section, string key, SourceAiNetworkEdits.Header header, bool editable, Dictionary<string, string[]> kept,
        string? caption = null, string help = "", string? placeholder = null, string[]? placeholders = null)
    {
        var field = SourceAiNetworkEdits.FieldOf(key)!;
        var stored = header.Fields[key];
        string[] initial = field.Kind == SourceAiNetworkEdits.FieldKind.Pair ? [stored?.Text(0) ?? "", stored?.Text(1) ?? ""] : [stored?.Text() ?? ""];
        if (field.Kind == SourceAiNetworkEdits.FieldKind.Flag) initial = [stored?.Text() is "1" ? "1" : "0"];
        if (field.Kind == SourceAiNetworkEdits.FieldKind.Text && key is "type" or "attack_strategy") initial = [stored?.Text() ?? ""];
        string[] values = kept.TryGetValue(key, out var draft) ? draft : initial;
        TextBlock problem = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new(76, 2, 0, 0) };
        problem.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        Control[] boxes;
        FrameworkElement shown;
        string title = caption ?? char.ToUpperInvariant(field.Caption[0]) + field.Caption[1..];
        switch (field.Kind)
        {
            case SourceAiNetworkEdits.FieldKind.Flag:
            {
                CheckBox check = new() { Content = "Attack buddy", IsChecked = values[0] == "1", IsEnabled = editable };
                check.Checked += (_, _) => Changed(); check.Unchecked += (_, _) => Changed();
                AutomationProperties.SetName(check, "Attack buddy");
                boxes = [check]; shown = check; title = "";
                break;
            }
            case SourceAiNetworkEdits.FieldKind.Text when key is "type" or "attack_strategy":
            {
                ComboBox combo = new() { Height = 32, MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = editable };
                var known = key == "type" ? SourceAiNetworkEdits.Types : SourceAiNetworkEdits.Strategies;
                // A stored value that is none of them stays first, as it is, until another is chosen.
                if (!known.Contains(values[0]))
                    combo.Items.Add(Item(values[0], key == "type" ? (values[0].Length == 0 ? "Not stored · runs as Standard" : $"{JsonData.ShownText(values[0], 32)} · runs as Standard")
                        : values[0].Length == 0 ? "Not stored · game: head-on" : $"“{JsonData.ShownText(values[0], 32)}” · game: head-on", key == "attack_strategy" ? stored == null ? "#FF6666" : "#A0A0A0" : null));
                foreach (string value in known) combo.Items.Add(Item(value, ValueName(key, value), key == "attack_strategy" ? zStudio.Rendering.AiNetworkColors.Hex(AiAttackStrategy.Stored(value)) : null));
                combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == values[0]);
                combo.SelectionChanged += (_, _) => Changed();
                AutomationProperties.SetName(combo, title);
                boxes = [combo]; shown = combo;
                break;
            }
            case SourceAiNetworkEdits.FieldKind.Pair:
            {
                StackPanel pair = new() { Orientation = Orientation.Horizontal };
                boxes = [.. Enumerable.Range(0, 2).Select(i => (Control)Box(values[i], placeholders?[i], $"{title}, {(i == 0 ? "first" : "second")} value", editable))];
                // Each box sits in its hint's grid: the grids line up side by side.
                foreach (var box in boxes)
                    if (box.Parent is Grid holder) { holder.Margin = new(0, 0, 6, 0); pair.Children.Add(holder); }
                shown = pair;
                break;
            }
            default:
            {
                var box = Box(values[0], placeholder, title, editable, key == "name" ? SourceAiNetworkEdits.MaximumNameLength : 32);
                boxes = [box]; shown = (FrameworkElement)box.Parent ?? box;
                break;
            }
        }
        NetworkInput input = new(field, boxes, initial, problem);
        inputs.Add(input);
        section.Body.Children.Add(title.Length == 0 ? Indented(shown) : Row(title, shown));
        section.Body.Children.Add(problem);
        string note = help + (stored == null && field.Kind != SourceAiNetworkEdits.FieldKind.Flag ? " · not stored" : stored?.Repeated == true ? " · stored more than once" : "");
        section.Body.Children.Add(Help(note, mono: true));
        Validate(input);

        TextBox Box(string value, string? hint, string name, bool enabled, int maximum = 32)
        {
            TextBox box = new() { Text = value, Height = 32, MinWidth = 90, MaxLength = maximum, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = enabled };
            AutomationProperties.SetName(box, name);
            box.TextChanged += (_, _) => Changed();
            box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Entry is NetworkEntry && box.Text != (string)box.Tag!) { e.Handled = true; box.Text = (string)box.Tag!; } };
            box.Tag = value;
            WithHint(box, hint ?? (value.Length == 0 ? "Not stored" : ""));
            return box;
        }
        static ComboBoxItem Item(string value, string text, string? swatch)
        {
            StackPanel content = new() { Orientation = Orientation.Horizontal };
            if (swatch != null) content.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new(3), Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Background = OutlinerKinds.Frozen(swatch) });
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return new() { Content = content, Tag = value };
        }
    }
    private static string ValueName(string key, string value) => key == "type" ? value switch
    {
        "standard" => "Standard", "hide" => "Hide", "fixed" => "Fixed", "defilade" => "Defilade", _ => value,
    } : value switch
    {
        "head_on" => "Head-on · head_on", "circle" => "Circle · circle", "back_forth" => "Back and forth · back_forth", "follow_net" => "Follow network · follow_net",
        "zigzag" => "Zigzag · zigzag", "sit" => "Sit · sit", _ => value,
    };
    private static void Restore(Control box, string value)
    {
        switch (box)
        {
            case TextBox text: text.Text = value; break;
            case CheckBox check: check.IsChecked = value == "1"; break;
            case ComboBox combo: combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value); break;
        }
    }
    /// <summary>Marks a field the game could not take, as Apply would refuse it.</summary>
    private static void Validate(NetworkInput input)
    {
        string? problem = null;
        if (input.Changed)
            try { SourceAiNetworkEdits.Check(input.Field, input.Values); }
            catch (System.IO.InvalidDataException ex) { problem = ex.Message; }
        input.Problem.Text = problem ?? ""; input.Problem.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private CardSection Section(string title, bool expanded = true, string? info = null)
    {
        CardSection section = new(title, expanded);
        if (info != null)
        {
            var glyph = new PreviewIcon { Kind = "Info", Width = 13, Height = 13, ToolTip = info, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
            ToolTipService.SetInitialShowDelay(glyph, 0);
            section.Trailing = new Border { Background = Brushes.Transparent, Child = glyph, Padding = new(4) };
        }
        body.Children.Add(section);
        return section;
    }
    private static Grid Row(string caption, UIElement value)
    {
        Grid row = new() { Margin = new(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new() { Width = new(76) }); row.ColumnDefinitions.Add(new());
        // A caption wraps between words; a word too long for the column (a sequence's name) is trimmed, whole in its tooltip.
        TextBlock key = new() { Text = caption, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.WrapWithOverflow,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 8, 0), ToolTip = caption };
        key.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        row.Children.Add(key);
        Grid.SetColumn((UIElement)value, 1); row.Children.Add(value);
        return row;
    }
    private static FrameworkElement Indented(FrameworkElement value) { value.Margin = new(76, 3, 0, 3); return value; }
    private static TextBlock Plain(string text) => new() { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Mono(string text) { var block = Plain(text); block.FontFamily = new("Cascadia Mono, Consolas"); block.FontSize = 11.5; return block; }
    private static TextBlock Help(string text, bool mono = false)
    {
        TextBlock help = new() { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(76, 1, 0, 4) };
        if (mono) help.FontFamily = new("Cascadia Mono, Segoe UI");
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return help;
    }
    /// <summary>A text box with its hint shown while it is empty (the box joins a grid with the hint).</summary>
    private static Grid WithHint(TextBox box, string text, bool stretch = false)
    {
        TextBlock hint = new() { Text = text, IsHitTestVisible = false, Margin = new(11, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        void Update() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Update(); Update();
        Grid grid = new() { HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left }; grid.Children.Add(box); grid.Children.Add(hint);
        return grid;
    }
    private static Button FooterButton(string kind, string text, string name, string style, Action action)
    {
        Button button = new() { Content = new PreviewIcon { Kind = kind, Width = 16, Height = 16 }, Width = 36, Height = 30, Padding = new(0), IsEnabled = false, ToolTip = text };
        button.SetResourceReference(StyleProperty, style);
        ToolTipService.SetInitialShowDelay(button, 0); ToolTipService.SetBetweenShowDelay(button, 0); ToolTipService.SetShowOnDisabled(button, true);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action(); return button;
    }
}
