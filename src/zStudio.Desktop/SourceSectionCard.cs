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
/// network (its header settings are edited here, in its source file) or an animation definition (existing events and activation). Edits are a
/// draft until Apply (Enter in a zone's label), which accepts one undoable source change; Revert or Escape restores.
/// </summary>
internal sealed partial class SourceSectionCard : Border
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
    /// <summary>The applied edit was prepared and checked: its draft is being accepted (it no longer holds anything back).</summary>
    internal bool Accepting { get; private set; }
    internal void ReleaseForAcceptance() { if (Applying) Accepting = true; }
    internal Func<byte, string, Task>? ApplyLabel { get; set; }
    internal Action<byte>? PaintZone { get; set; }
    internal Func<string, IReadOnlyList<SourceAiNetworkEdits.Change>, Task>? ApplyNetwork { get; set; }
    internal Action<int>? SelectObject { get; set; }
    internal Action? CloseRequested { get; set; }
    /// <summary>The draft opened, changed or closed (the window's draft guards and footer follow).</summary>
    internal event Action? DraftChanged;

    // The zone's label field and the network's fields: each with the text it started from, what is stored and, when the
    // edit would refuse it, why it is fixed in a text editor instead. A network's rows: one per value (a pair has two).
    private TextBox? label;
    private string labelInitial = "";
    private readonly List<NetworkInput> inputs = [];
    private sealed record NetworkInput(SourceAiNetworkEdits.Field Field, Control[] Boxes, string[] Initial, SourceAiNetworkEdits.StoredField? Stored, string? Locked)
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
    private sealed record NetworkRow(NetworkInput Input, int Part, TextBlock Label, TextBlock Help);
    private readonly List<NetworkRow> networkRows = [];
    private Border? buddiesBar;
    private bool activateWritten;
    private string networkNumber = "";
    private const double NetworkLabelWidth = 116;
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

    /// <summary>
    /// An AI network: its header settings as stored, each a field of one draft, named as the developers' Net Edit dialog
    /// names them; the line under each says what it does in play, following the draft's type and strategy.
    /// </summary>
    internal void ShowNetwork(DocumentModel document, AiNetwork network, SourceAiNetworkEdits.Header header, bool text)
    {
        bool same = Entry is NetworkEntry previous && previous.Network.Id == network.Id && ReferenceEquals(Document, document);
        Dictionary<string, string[]> kept = same && HasDraft ? inputs.Where(i => i.Changed).ToDictionary(i => i.Field.Key, i => i.Values) : [];
        Begin(new NetworkEntry(network), document);
        string stem = SourceMapSections.NetworkStem(network);
        networkNumber = stem.StartsWith("net_", StringComparison.OrdinalIgnoreCase) ? stem[4..] : stem;
        heading.Text = stem;
        subtitle.Text = $"AI network · {network.Nodes.Count} node{(network.Nodes.Count == 1 ? "" : "s")}";
        subtitle.ToolTip = $"{network.Id} · network ID " + (int.TryParse(networkNumber, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number.ToString(CultureInfo.InvariantCulture) : networkNumber);
        // The tile and the Behaviour summary show the applied strategy, as the map's markers do.
        var applied = network.AttackStrategy;
        var appliedKind = applied.Kind == AiAttackStrategyKind.Unknown ? AiAttackStrategyKind.HEA : applied.Kind;
        tile.Child = new PreviewIcon { Kind = "AiNodes", Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ((PreviewIcon)tile.Child).SetValue(TextElement.ForegroundProperty, TileBrush(zStudio.Rendering.AiNetworkColors.Hex(applied)));
        if (!text) Error($"{System.IO.Path.GetFileName(network.Id)} is stored as binary zReader data, so its settings can be edited only in a text source.");
        var main = Section("Network");
        TextBlock file = Mono(network.Id);
        file.ToolTip = $"The network ID is the file's number ({networkNumber}). AI vehicles name it in aiv.zrd, so it is not edited here.";
        main.Body.Children.Add(Row("File", file, NetworkLabelWidth));
        NetworkField(main, "name", header, text, kept);
        var behaviour = Section("Behaviour");
        string appliedType = AiNetworkCardText.TypeName(AiNetworkCardText.GameType(header.Fields["type"]?.Text()));
        string appliedStrategy = AiNetworkCardText.StrategyName(appliedKind);
        behaviour.SummaryWithSwatch(appliedType + " · ", OutlinerKinds.Frozen(zStudio.Rendering.AiNetworkColors.Hex(applied)), appliedStrategy,
            $"Net Type {appliedType} · Attack Strategy {appliedStrategy}" + (applied.State == AiAttackStrategyState.Missing ? " (not stored: the game attacks head-on)" : ""));
        NetworkField(behaviour, "type", header, text, kept);
        NetworkField(behaviour, "attack_strategy", header, text, kept);
        NetworkField(behaviour, "pursuit_params", header, text, kept);
        Divider(behaviour);
        NetworkField(behaviour, "activate_rad", header, text, kept);
        NetworkField(behaviour, "attack_rad", header, text, kept);
        NetworkField(behaviour, "return_range", header, text, kept);
        Divider(behaviour);
        NetworkField(behaviour, "attack_dwell", header, text, kept);
        NetworkField(behaviour, "not_pursuit_dwell", header, text, kept);
        Divider(behaviour);
        NetworkField(behaviour, "attack_buddy", header, text, kept);
        buddiesBar = BuddiesBar(text);
        behaviour.Body.Children.Add(buddiesBar);
        Divider(behaviour);
        StackPanel unused = new() { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 6) };
        unused.Children.Add(new TextBlock { Text = "No effect in play", FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = .7, VerticalAlignment = VerticalAlignment.Center });
        var info = new PreviewIcon { Kind = "Info", Width = 12, Height = 12, Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
        unused.Children.Add(new Border { Background = Brushes.Transparent, Child = info, ToolTip = AiNetworkCardText.NoEffectInfo });
        ToolTipService.SetInitialShowDelay(unused.Children[1], 0);
        behaviour.Body.Children.Add(unused);
        NetworkField(behaviour, "activate_buddy", header, text, kept);
        NetworkField(behaviour, "path_width", header, text, kept);
        NetworkField(behaviour, "hide_times", header, text, kept);
        var nodes = Section("Nodes");
        nodes.Summary = $"{network.Nodes.Count} node{(network.Nodes.Count == 1 ? "" : "s")}";
        int links = network.Nodes.Sum(n => n.PreviewLinks.Count(l => l.Target != null)); long slots = network.Nodes.Sum(n => (long)n.LinkCount);
        nodes.Body.Children.Add(Row("Nodes", Plain(network.Nodes.Count.ToString("N0", CultureInfo.CurrentCulture)), NetworkLabelWidth));
        nodes.Body.Children.Add(Row("Links", Plain($"{links:N0} of {slots:N0} slots"), NetworkLabelWidth));
        nodes.Body.Children.Add(Row("Diagnostics", Plain(network.Diagnostics.Count == 0 ? "None" :
            string.Join("\n", network.Diagnostics.Take(8).Select(d => JsonData.ShownText(d.Message, 240))) + (network.Diagnostics.Count > 8 ? $"\n{network.Diagnostics.Count - 8} more" : "")), NetworkLabelWidth));
        HelpLine(nodes).Text = "Move nodes by selecting them in the view.";
        if (header.Other.Count > 0)
        {
            var other = Section("Other stored keys", expanded: false);
            other.Summary = header.Other.Count.ToString(CultureInfo.CurrentCulture);
            foreach (var (key, value) in header.Other) other.Body.Children.Add(Row(key, Plain(value), NetworkLabelWidth));
            HelpLine(other).Text = "Kept as they are. The game ignores them, except version.";
        }
        Finish(editable: text);
        RefreshNetwork();
    }

    /// <summary>Loading/fallback summary of an animation definition: its source, bindings, settings, sequences and notes.</summary>
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
    internal bool HasDraft => !Accepting && (Entry is ZoneEntry && label != null && label.Text.Trim() != labelInitial || Entry is NetworkEntry && inputs.Any(i => i.Changed) || Entry is DefinitionEntry && animationChanges.Count > 0);
    internal string DraftToken => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{draftId}|{Document?.SessionId}|{Document?.Revision}|{Entry?.Key}|{(label?.Text ?? "")}|{string.Join("\u0001", inputs.Select(i => string.Join("\u0002", i.Values)))}|{System.Text.Json.JsonSerializer.Serialize(animationChanges)}")));
    /// <summary>The changes a network's draft makes, as requested values.</summary>
    internal IReadOnlyList<SourceAiNetworkEdits.Change> NetworkChanges => [.. inputs.Where(i => i.Changed).Select(i => new SourceAiNetworkEdits.Change(i.Field.Key, i.Values))];
    internal object Describe() => new
    {
        token = DraftToken, pending = HasDraft, kind = Entry switch { ZoneEntry => "zone", NetworkEntry => "network", DefinitionEntry => "definition", _ => null },
        target = Entry?.Key, revision = Document?.Revision, applying = Applying,
        label = Entry is ZoneEntry ? label?.Text : null,
        animation = AnimationDraft,
        fields = Entry is NetworkEntry ? inputs.Select(i => new { key = i.Field.Key, values = i.Values, stored = i.Initial, changed = i.Changed }).ToArray() : null,
        // What each row shows: its label, help line (or the message of an invalid value), grey hint, and any warning.
        rows = Entry is NetworkEntry ? networkRows.Select(r => new
        {
            key = r.Input.Field.Key, part = r.Input.Field.Kind == SourceAiNetworkEdits.FieldKind.Pair ? r.Part : (int?)null, label = r.Input.Field.Part(r.Part),
            help = r.Help.Text, error = Problem(r) != null,
            hint = r.Input.Stored == null && r.Input.Boxes[r.Part] is TextBox ? AiNetworkCardText.Hint(r.Input.Field.Key, r.Part) : null,
            inactive = r.Input.Field.Key == "pursuit_params" && AiNetworkCardText.PursuitHelp(AiNetworkCardText.GameStrategy(Draft("attack_strategy")), r.Part).Inactive,
            enabled = r.Input.Boxes[r.Part].IsEnabled,
            warning = r.Input.Locked ?? (r.Input.Field.Key == "attack_buddy" && buddiesBar?.Visibility == Visibility.Visible ? AiNetworkCardText.BuddiesIgnored + "Store it checks Buddies Activate." : null),
        }).ToArray() : null,
    };
    internal void RequireDraft(string token) { if (!HasDraft || token != DraftToken) throw new StudioCommandException("draft_conflict", "The section card draft changed. Read it again."); }
    /// <summary>Restores every field to what is stored.</summary>
    internal void Discard()
    {
        if (Applying) return;
        animationChanges.Clear(); RefreshAnimationFields();
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
        var applied = Entry;
        try
        {
            if (Entry is ZoneEntry zone && ApplyLabel != null) await ApplyLabel(zone.Zone.Zone, label!.Text.Trim());
            else if (Entry is NetworkEntry network && ApplyNetwork != null) await ApplyNetwork(network.Network.Id, NetworkChanges);
            else if (Entry is DefinitionEntry definition && ApplyAnimation != null) await ApplyAnimation(definition.Definition.Index, animationChanges.Values.ToArray());
            // Accepted: what was applied is now stored (until the rebuilt map shows the card again with its own values).
            if (ReferenceEquals(Entry, applied)) Adopt();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { Error(ex.Message); }
        finally { Applying = Accepting = false; RefreshFooter(); DraftChanged?.Invoke(); }
    }
    /// <summary>The shown values become the stored ones (after an accepted edit).</summary>
    private void Adopt()
    {
        animationChanges.Clear();
        if (label != null) labelInitial = label.Text.Trim();
        // The edit stored Buddies Activate when it was checked, or when Buddies Attack was turned on (Plan adds it then).
        var attack = inputs.FirstOrDefault(i => i.Field.Key == "attack_buddy");
        if (inputs.FirstOrDefault(i => i.Field.Key == "activate_buddy") is { Stored: null } activate && (activate.Changed || attack is { Changed: true } && attack.Values[0] == "1"))
            activateWritten = true;
        for (int i = 0; i < inputs.Count; i++)
        {
            var adopted = inputs[i] with { Initial = inputs[i].Values };
            for (int r = 0; r < networkRows.Count; r++)
                if (ReferenceEquals(networkRows[r].Input, inputs[i])) networkRows[r] = networkRows[r] with { Input = adopted };
            inputs[i] = adopted;
            foreach (var box in adopted.Boxes.OfType<TextBox>()) box.Tag = box.Text.Trim();
        }
        RefreshNetwork();
    }
    /// <summary>Asks about a pending draft: Yes applies it, No discards it, Cancel keeps editing (an edit being accepted holds nothing back).</summary>
    internal async Task<bool> ResolvePendingAsync()
    {
        if (Applying) return Accepting;
        if (!HasDraft) return true;
        string subject = Entry switch { ZoneEntry => "zone label", DefinitionEntry => "animation changes", _ => "network changes" };
        string title = Entry switch { ZoneEntry => "Zone label draft", DefinitionEntry => "Animation draft", _ => "AI network draft" };
        var answer = MessageBox.Show(Window.GetWindow(this), $"Apply the pending {subject}?\nYes: apply · No: discard · Cancel: keep editing",
            title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) { Discard(); return !HasDraft; }
        await ApplyAsync();
        return !HasDraft;
    }
    internal void FocusFirst()
    {
        if (label != null) { label.Focus(); label.SelectAll(); }
        else if (Entry is DefinitionEntry) animationSequencePicker?.Focus();
        else if (inputs.FirstOrDefault()?.Boxes[0] is { } first) first.Focus();
    }

    // ---------------------------------------------------------------- building

    private void Begin(SectionEntry entry, DocumentModel document)
    {
        animationChanges.Clear(); animationDefinition = null; animationFieldsHost = null;
        if (Entry?.Key != entry.Key || !ReferenceEquals(Document, document)) draftId = Guid.NewGuid();
        Entry = entry; Document = document;
        body.Children.Clear(); inputs.Clear(); networkRows.Clear(); buddiesBar = null; activateWritten = false; label = null; labelInitial = "";
        if (!Applying) Error(null);
    }
    private void Finish(bool editable)
    {
        footer.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        RefreshFooter();
    }
    private void Changed()
    {
        RefreshNetwork();
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

    /// <summary>
    /// One header field as stored (or as the kept draft): a dropdown with its label above, a check box, or a labelled box per
    /// value (a pair's values are two rows), each followed by its help line. An empty box shows the game's value as a hint.
    /// </summary>
    private void NetworkField(CardSection section, string key, SourceAiNetworkEdits.Header header, bool editable, Dictionary<string, string[]> kept)
    {
        var field = SourceAiNetworkEdits.FieldOf(key)!;
        var stored = header.Fields[key];
        int count = field.Kind == SourceAiNetworkEdits.FieldKind.Pair ? 2 : 1;
        string[] initial = field.Kind switch
        {
            SourceAiNetworkEdits.FieldKind.Pair => [stored?.Text(0) ?? "", stored?.Text(1) ?? ""],
            SourceAiNetworkEdits.FieldKind.Flag => [stored?.On == true ? "1" : "0"],
            _ => [stored?.Text() ?? ""],
        };
        // A value the edit would refuse is fixed in a text editor: its field is read-only here, and keeps no draft.
        string? locked = stored == null ? null
            : stored.BothNames ? "Stored under both of its names. Fix it in a text editor to edit Min and Max here."
            : stored.Repeated ? "Stored twice in the file; the game reads the first. Fix it in a text editor to edit it here."
            : stored.Values.Count != count || stored.Values.Any(v => v.Kind is not (zStudio.Core.Formats.ZrdKind.String or zStudio.Core.Formats.ZrdKind.Int or zStudio.Core.Formats.ZrdKind.Float))
                ? "Not stored as plain values. Fix it in a text editor to edit it here." : null;
        string[] values = locked == null && kept.TryGetValue(key, out var draft) ? draft : initial;
        bool enabled = editable && locked == null, unused = AiNetworkCardText.NoEffect(key);
        Control[] boxes = new Control[count];
        List<(int Part, TextBlock Label, TextBlock Help)> made = [];
        switch (field.Kind)
        {
            case SourceAiNetworkEdits.FieldKind.Text when key is "type" or "attack_strategy":
            {
                var caption = Caption(field.Caption, Tip(field, stored, 0), unused);
                caption.Margin = new(0, 0, 0, 5);
                ComboBox combo = new() { Height = 32, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, IsEnabled = enabled };
                var known = key == "type" ? SourceAiNetworkEdits.Types : SourceAiNetworkEdits.Strategies;
                // A stored value that is none of them (or no value) stays first, as it is, with what the game makes of it.
                if (!known.Contains(initial[0]))
                {
                    string? value = stored == null ? null : initial[0];
                    string game = key == "type" ? AiNetworkCardText.GameType(value) : AiNetworkCardText.StrategyName(AiNetworkCardText.GameStrategy(value)).ToLowerInvariant();
                    combo.Items.Add(Item(initial[0], value == null ? "Not stored" : $"“{JsonData.ShownText(value, 32)}”", " · game: " + game,
                        key == "attack_strategy" ? zStudio.Rendering.AiNetworkColors.Hex(value == null ? AiAttackStrategy.Missing : AiAttackStrategy.Stored(value)) : null));
                }
                foreach (string value in known)
                    combo.Items.Add(Item(value, key == "type" ? AiNetworkCardText.TypeName(value) : AiNetworkCardText.StrategyName(value), null,
                        key == "attack_strategy" ? zStudio.Rendering.AiNetworkColors.Hex(AiAttackStrategy.Stored(value)) : null));
                combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == values[0]) ?? combo.Items[0];
                combo.SelectionChanged += (_, _) => Changed();
                AutomationProperties.SetName(combo, field.Caption);
                section.Body.Children.Add(caption); section.Body.Children.Add(combo);
                boxes[0] = combo;
                made.Add((0, caption, HelpLine(section)));
                break;
            }
            case SourceAiNetworkEdits.FieldKind.Flag:
            {
                TextBlock caption = new() { Text = field.Caption, FontSize = 13 };
                if (unused) caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                CheckBox check = new() { Content = caption, IsChecked = values[0] == "1", IsEnabled = enabled, MinHeight = 32, ToolTip = Tip(field, stored, 0) };
                ToolTipService.SetInitialShowDelay(check, 0);
                check.Checked += (_, _) => Changed(); check.Unchecked += (_, _) => Changed();
                AutomationProperties.SetName(check, field.Caption);
                section.Body.Children.Add(check);
                boxes[0] = check;
                var help = HelpLine(section); help.Margin = new(28, 0, 0, 10);
                made.Add((0, caption, help));
                break;
            }
            default:
                for (int part = 0; part < count; part++)
                {
                    var caption = Caption(field.Part(part), Tip(field, stored, part), unused);
                    var box = NetworkBox(values[part], initial[part], stored == null ? AiNetworkCardText.Hint(key, part) : "", field.Part(part), enabled,
                        key == "name" ? SourceAiNetworkEdits.MaximumNameLength : 32, AiNetworkCardText.Seconds(key));
                    Grid row = new() { MinHeight = 32 };
                    row.ColumnDefinitions.Add(new() { Width = new(NetworkLabelWidth) }); row.ColumnDefinitions.Add(new());
                    row.Children.Add(caption);
                    var holder = (FrameworkElement)box.Parent;
                    Grid.SetColumn(holder, 1); row.Children.Add(holder);
                    section.Body.Children.Add(row);
                    boxes[part] = box;
                    made.Add((part, caption, HelpLine(section)));
                }
                break;
        }
        NetworkInput input = new(field, boxes, initial, stored, locked);
        inputs.Add(input);
        foreach (var (part, caption, help) in made) networkRows.Add(new(input, part, caption, help));
        if (locked != null) section.Body.Children.Add(Caution(locked));

        ValueTextBox NetworkBox(string value, string storedText, string hint, string name, bool enabled, int maximum, bool seconds)
        {
            ValueTextBox box = new() { Text = value, Height = 32, MaxLength = maximum, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = enabled,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new(10, 0, seconds ? 26 : 10, 0) };
            AutomationProperties.SetName(box, name);
            box.TextChanged += (_, _) => Changed();
            // Escape restores what is stored, also when the box shows a kept draft; Enter applies the draft.
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && Entry is NetworkEntry && box.Text != (string)box.Tag!) { e.Handled = true; box.Text = (string)box.Tag!; }
                else if (e.Key == Key.Enter && Entry is NetworkEntry && HasDraft) { e.Handled = true; _ = ApplyAsync(); }
            };
            // A value the chosen strategy ignores is drawn softer, except while it is being typed.
            box.GotKeyboardFocus += (_, _) => RefreshNetwork(); box.LostKeyboardFocus += (_, _) => RefreshNetwork(); box.IsEnabledChanged += (_, _) => RefreshNetwork();
            box.Tag = storedText;
            var grid = WithHint(box, hint, stretch: true);
            if (seconds)
            {
                TextBlock unit = new() { Text = "s", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0), IsHitTestVisible = false };
                unit.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                grid.Children.Add(unit);
            }
            return box;
        }
    }
    /// <summary>A network field's label: secondary, or tertiary for the fields the game never uses; its tooltip says how it is stored.</summary>
    private static TextBlock Caption(string text, string toolTip, bool unused)
    {
        TextBlock caption = new() { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.WrapWithOverflow,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 8, 0), ToolTip = toolTip };
        caption.SetResourceReference(TextBlock.ForegroundProperty, unused ? "TextFillColorTertiaryBrush" : "TextFillColorSecondaryBrush");
        ToolTipService.SetInitialShowDelay(caption, 0);
        return caption;
    }
    private static string Tip(SourceAiNetworkEdits.Field field, SourceAiNetworkEdits.StoredField? stored, int part)
    {
        if (stored == null) return $"Not stored · Apply writes {field.Key}";
        if (stored.BothNames) return $"Stored as both {field.Key} and {field.Alias}";
        if (stored.Repeated) return $"Stored twice as {stored.Key}";
        string what = field.Key switch
        {
            "type" => " · the game reads its first two letters", "attack_strategy" => " · the game reads its first three letters",
            "name" or "attack_buddy" or "activate_buddy" => "",
            _ => AiNetworkCardText.Seconds(field.Key) ? " · seconds" : " · a distance in game units",
        };
        return $"Stored as {stored.Key}" + (field.Kind == SourceAiNetworkEdits.FieldKind.Pair ? $", {(part == 0 ? "first" : "second")} value" : "") + what;
    }
    /// <summary>A help line across the block, under its field.</summary>
    private static TextBlock HelpLine(CardSection section)
    {
        TextBlock help = new() { FontSize = 11.5, LineHeight = 15, TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 10) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        section.Body.Children.Add(help);
        return help;
    }
    private static void Divider(CardSection section)
    {
        Border line = new() { Height = 1, Margin = new(0, 2, 0, 12) };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        section.Body.Children.Add(line);
    }
    /// <summary>A caution under a field's help line: a glyph and its text.</summary>
    private static Grid Caution(string text)
    {
        Grid line = new() { Margin = new(0, -6, 0, 10) };
        line.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); line.ColumnDefinitions.Add(new());
        var glyph = new PreviewIcon { Kind = "Warning", Width = 12, Height = 12, Margin = new(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top };
        glyph.SetResourceReference(TextElement.ForegroundProperty, "SystemFillColorCautionBrush");
        line.Children.Add(glyph);
        TextBlock message = new() { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(message, 1); line.Children.Add(message);
        return line;
    }
    /// <summary>The bar under Buddies Attack while the game ignores it: Store it checks Buddies Activate in the draft.</summary>
    private Border BuddiesBar(bool editable)
    {
        Grid content = new();
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new());
        var glyph = new PreviewIcon { Kind = "Warning", Width = 14, Height = 14, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        glyph.SetResourceReference(TextElement.ForegroundProperty, "SystemFillColorCautionBrush");
        content.Children.Add(glyph);
        Hyperlink store = new(new Run("Store it")) { IsEnabled = editable };
        AutomationProperties.SetName(store, "Store Buddies Activate");
        // The bar goes away with its link: the keyboard moves on to the check box.
        store.Click += (_, _) => { if (inputs.FirstOrDefault(i => i.Field.Key == "activate_buddy")?.Boxes[0] is CheckBox { IsEnabled: true } check) { check.IsChecked = true; check.Focus(); } };
        TextBlock message = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        message.Inlines.Add(new Run(AiNetworkCardText.BuddiesIgnored)); message.Inlines.Add(store);
        Grid.SetColumn(message, 1); content.Children.Add(message);
        Border bar = new() { CornerRadius = new(4), Padding = new(8, 6, 8, 6), Margin = new(0, -4, 0, 10), Child = content, Visibility = Visibility.Collapsed };
        bar.SetResourceReference(Border.BackgroundProperty, "SystemFillColorCautionBackgroundBrush");
        return bar;
    }
    private string? Draft(string key) => inputs.FirstOrDefault(i => i.Field.Key == key)?.Values[0];
    /// <summary>
    /// The network rows' help lines and styles, following the draft's type and strategy (a value the strategy ignores is
    /// drawn softer); an invalid value's message replaces its line. Buddies Attack's bar shows while the game would ignore it.
    /// </summary>
    private void RefreshNetwork()
    {
        if (Entry is not NetworkEntry) return;
        string type = AiNetworkCardText.GameType(Draft("type"));
        var strategy = AiNetworkCardText.GameStrategy(Draft("attack_strategy"));
        var attack = inputs.FirstOrDefault(i => i.Field.Key == "attack_buddy");
        var activate = inputs.FirstOrDefault(i => i.Field.Key == "activate_buddy");
        bool attackOn = attack?.Values[0] == "1", activateStored = activate?.Stored != null || activateWritten;
        // Apply stores Buddies Activate when it is checked, or when the draft turns Buddies Attack on (the edit adds it then).
        bool storing = activate?.Values[0] == "1" || attack is { Changed: true } && attackOn;
        foreach (var row in networkRows)
        {
            string key = row.Input.Field.Key;
            var (help, inactive) = key switch
            {
                "name" => (AiNetworkCardText.NameHelp(networkNumber), false),
                "type" => (AiNetworkCardText.TypeHelp(type), false),
                "attack_strategy" => (AiNetworkCardText.StrategyHelp(strategy), false),
                "pursuit_params" => AiNetworkCardText.PursuitHelp(strategy, row.Part),
                "activate_buddy" => (AiNetworkCardText.BuddiesActivateHelp(activateStored, storing), false),
                _ => (AiNetworkCardText.FieldHelp(key, row.Part), false),
            };
            string? problem = Problem(row);
            row.Help.Text = problem ?? help;
            row.Help.SetResourceReference(TextBlock.ForegroundProperty, problem != null ? "SystemFillColorCriticalBrush" : "TextFillColorTertiaryBrush");
            var box = row.Input.Boxes[row.Part];
            if (key == "pursuit_params")
            {
                row.Label.SetResourceReference(TextBlock.ForegroundProperty, inactive ? "TextFillColorTertiaryBrush" : "TextFillColorSecondaryBrush");
                // A disabled box keeps the theme's disabled text.
                if (inactive && box.IsEnabled && !box.IsKeyboardFocusWithin) box.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
                else box.ClearValue(Control.ForegroundProperty);
            }
            AutomationProperties.SetHelpText(box, problem ?? help);
        }
        if (buddiesBar != null)
            buddiesBar.Visibility = attackOn && attack!.Initial[0] == "1" && !activateStored && !storing ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>Why a row's changed value cannot be applied (as the edit would refuse it), or null.</summary>
    private static string? Problem(NetworkRow row)
    {
        var input = row.Input;
        if (!input.Changed) return null;
        var field = input.Field;
        string[] values = input.Values;
        // A pair is stored together: a half-filled one that is not stored asks for its other value.
        if (field.Kind == SourceAiNetworkEdits.FieldKind.Pair && input.Stored == null && values.Count(v => v.Length == 0) == 1)
        {
            if (values[row.Part].Length == 0) return $"Enter {field.Part(row.Part)} too: both are stored together.";
            // The filled half is checked on its own (beside a valid other half).
            try { SourceAiNetworkEdits.Check(field, row.Part == 0 ? [values[0], "0"] : ["0", values[1]]); return null; }
            catch (System.IO.InvalidDataException ex) { return ex.Message; }
        }
        try { SourceAiNetworkEdits.Check(field, values); return null; }
        catch (System.IO.InvalidDataException ex)
        {
            if (field.Kind != SourceAiNetworkEdits.FieldKind.Pair) return ex.Message;
            // The message names the value it is about.
            int part = ex.Message.Contains(field.Part(1), StringComparison.Ordinal) ? 1 : 0;
            return part == row.Part ? ex.Message : null;
        }
    }
    private static Brush TileBrush(string hex)
    {
        // On the light theme the glyph is the strategy's colour darkened, so yellow and green stay readable.
        var color = (Color)ColorConverter.ConvertFromString(hex);
        if (OutlinerKinds.LightTheme) color = Color.FromRgb((byte)(color.R * .55), (byte)(color.G * .55), (byte)(color.B * .55));
        SolidColorBrush brush = new(color); brush.Freeze();
        return brush;
    }
    private static ComboBoxItem Item(string value, string text, string? after, string? swatch)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        if (swatch != null) content.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new(3), Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Background = OutlinerKinds.Frozen(swatch) });
        TextBlock name = new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (after != null)
        {
            Run rest = new(after);
            rest.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
            name.Inlines.Add(rest);
        }
        content.Children.Add(name);
        ComboBoxItem item = new() { Content = content, Tag = value };
        AutomationProperties.SetName(item, text + after);
        TextSearch.SetText(item, text);
        return item;
    }
    private static void Restore(Control box, string value)
    {
        switch (box)
        {
            case TextBox text: text.Text = value; break;
            case CheckBox check: check.IsChecked = value == "1"; break;
            case ComboBox combo: combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value); break;
        }
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
    private static Grid Row(string caption, UIElement value, double width = 76)
    {
        Grid row = new() { Margin = new(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new() { Width = new(width) }); row.ColumnDefinitions.Add(new());
        // A caption wraps between words; a word too long for the column (a sequence's name) is trimmed, whole in its tooltip.
        TextBlock key = new() { Text = caption, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.WrapWithOverflow,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 8, 0), ToolTip = caption };
        key.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        row.Children.Add(key);
        Grid.SetColumn((UIElement)value, 1); row.Children.Add(value);
        return row;
    }
    private static TextBlock Plain(string text) => new() { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Mono(string text) { var block = Plain(text); block.FontFamily = new("Cascadia Mono, Consolas"); block.FontSize = 11.5; return block; }
    private static TextBlock Help(string text)
    {
        TextBlock help = new() { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(76, 1, 0, 4) };
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
