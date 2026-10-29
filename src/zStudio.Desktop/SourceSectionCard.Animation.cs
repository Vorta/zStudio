using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// The definition's card: in the map layout a read-mostly summary with Open in Animation mode (design §6), in Animation mode the
/// Inspector card of the selection (§11). The window builds what it shows (<see cref="AnimationInspection"/>); the card draws
/// it, edits the definition's source fields through its one draft (Apply = one undo step) and raises the scenario, graph and
/// transport intents of its switches, include check boxes and links.
/// </summary>
internal sealed partial class SourceSectionCard
{
    internal Func<int, IReadOnlyList<SourceAnimationEdits.Change>, Task>? ApplyAnimation { get; set; }
    /// <summary>The map layout card's primary action: Open in Animation mode on the binding (-1: the definition's default).</summary>
    internal Action<DefinitionEntry, int>? OpenAnimation { get; set; }
    /// <summary>A link or button of a card: select, object, open, expand, seek, tab, frame, include.</summary>
    internal Action<InspectorLink>? InspectorLinkInvoked { get; set; }
    internal Action<InspectorSwitch, AnimationConditionChoice>? InspectorConditionChosen { get; set; }
    internal Action<InspectorInclude, bool>? InspectorIncludeChosen { get; set; }
    /// <summary>The Bindings section: Animation mode plays another binding (opens it).</summary>
    internal Action<int>? InspectorBindingChosen { get; set; }
    /// <summary>The header changed (Animation mode shows it above the Inspector | Scenario tabs).</summary>
    internal event Action? HeaderChanged;
    internal string HeaderTitle => heading.Text;
    internal string HeaderSubtitle => subtitle.Text;
    internal string HeaderGlyph { get; private set; } = "Definition";
    private bool titleShown = true;
    /// <summary>Whether the card shows its own title row (Animation mode shows it above the tabs instead).</summary>
    internal bool TitleShown
    {
        get => titleShown;
        set { titleShown = value; titleRow.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    }
    private bool openAnimationShown = true;
    /// <summary>Whether Open in Animation mode shows (the map layout); Animation mode itself hides it.</summary>
    internal bool OpenAnimationShown
    {
        get => openAnimationShown;
        set { openAnimationShown = value; if (openAnimation != null) openAnimation.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    }
    private Button? openAnimation;
    private void AddOpenAnimation(DefinitionEntry entry)
    {
        // Offered whenever the World tree row and the Animation menu offer it: an unbound definition opens with its empty states.
        if (OpenAnimation == null || !entry.BindingKnown) { openAnimation = null; return; }
        openAnimation = PrimaryButton("Open in Animation mode", "Open this definition in Animation mode (Enter on its World tree row). The camera stays where it is; nothing plays until you play it.");
        openAnimation.Visibility = openAnimationShown ? Visibility.Visible : Visibility.Collapsed;
        openAnimation.Click += (_, _) => OpenAnimation?.Invoke(entry, -1);
        body.Children.Add(openAnimation);
    }
    private static Button PrimaryButton(string text, string tip)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        content.Children.Add(new PreviewIcon { Kind = "Open", Width = 12, Height = 12, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        Button button = new() { Content = content, Height = 32, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 2, 0, 10), ToolTip = tip };
        button.SetResourceReference(StyleProperty, "AccentButtonStyle");
        AutomationProperties.SetName(button, text);
        return button;
    }

    private SourceAnimationEdits.Definition? animationDefinition;
    private readonly Dictionary<string, SourceAnimationEdits.Change> animationChanges = [];
    /// <summary>A threshold value box being typed in: the field's draft change before the typing (null: none), which Escape restores.</summary>
    private readonly Dictionary<string, SourceAnimationEdits.Change?> typedFrom = new(StringComparer.Ordinal);
    private string? animationRecord;
    private long animationRevision = -1;
    private FrameworkElement? animationDrops;
    private AnimationInspection? inspection;
    private SourceAnimationEdits.Definition? inspectedModel;
    private bool inspectorMode;
    private readonly Dictionary<string, CardSection> inspectorSections = new(StringComparer.Ordinal);
    // A field's ✓/○ column, and for a one-per-line list its state column (both direct children of the field's grid).
    private readonly Dictionary<string, (Panel Glyphs, Panel? States)> fieldMarks = new(StringComparer.Ordinal);
    private (AnimationThresholdEditor Editor, InspectorThreshold Item)? thresholdShown;
    private readonly HashSet<string> animationWindowRecords = [];
    private bool externalDraft;

    /// <summary>The definition's source model the card edits (null until it is read).</summary>
    internal SourceAnimationEdits.Definition? AnimationModel => animationDefinition;
    internal long ModelRevision => animationRevision;
    /// <summary>What the card shows now (the Inspector or the map card), for MCP.</summary>
    internal AnimationInspection? Inspection => inspection;

    internal void SetAnimationWindowRecords(IEnumerable<string> records)
    {
        animationWindowRecords.Clear();
        foreach (string record in records) animationWindowRecords.Add(record);
        if (inspection != null) Render(inspection, keepScroll: true);
    }
    /// <summary>A drop-window move is a draft of its own: the card's other sections wait for it.</summary>
    internal void SetExternalAnimationDraft(bool active)
    {
        externalDraft = active;
        if (animationDefinition == null) return;
        RefreshFooterVisibility();
        foreach (var section in body.Children.OfType<CardSection>()) section.IsEnabled = !active || section.Title == "Drop windows";
    }
    internal bool ShowsAnimation(DocumentModel document, DefinitionEntry entry) => Document == document && Entry?.Key == entry.Key && animationDefinition != null && animationRevision == document.Revision;
    internal object? AnimationDraft => animationDefinition == null ? null : new { definition = animationDefinition.Index, file = animationDefinition.File, ordinal = animationDefinition.Ordinal, missions = animationDefinition.Missions, sequence = SelectedSequence, record = animationRecord, token = DraftToken, changes = animationChanges.Values.ToArray(), pending = HasDraft };
    private string? SelectedSequence => animationRecord is { } record && animationDefinition?.Records.FirstOrDefault(r => r.Kind == "sequence" && (r.Id == record || record.StartsWith(r.Id + "/event/", StringComparison.Ordinal))) is { } sequence ? sequence.Id : null;
    /// <summary>The record the card's selection edits (an event or a sequence): source_animation select, and what set's startSeconds targets.</summary>
    internal void SelectAnimationRecord(string id)
    {
        if (animationDefinition?.Records.FirstOrDefault(r => r.Id == id) is not { } record) throw new InvalidOperationException("The animation record is unavailable.");
        if (record.Kind is "settings" or "prerequisites") throw new InvalidOperationException("Choose a sequence or event record.");
        animationRecord = id;
    }
    /// <summary>F2: the shown event's Start threshold (its value, or Add threshold), once the card is laid out.</summary>
    internal void FocusAnimationTiming() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
    {
        if (inspectorSections.TryGetValue("threshold", out var section)) section.Expand();
        UpdateLayout();
        if (thresholdShown is { } shown && shown.Editor.IsVisible) { shown.Editor.BringIntoView(); shown.Editor.FocusValue(); return; }
        if (inspectorSections.TryGetValue("threshold", out var found)) { found.BringIntoView(); found.Body.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); }
    });
    /// <summary>Expands a section of the shown card and scrolls it into view (a drop window picked in the view: Drop windows).</summary>
    internal void RevealSection(string id) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
    {
        if (!inspectorSections.TryGetValue(id, out var section)) return;
        section.Expand(); UpdateLayout();
        section.BringIntoView(new Rect(0, 0, 1, Math.Min(section.ActualHeight, 240)));
    });
    /// <summary>Alt+Enter: the card's first field (an editable box, combo, check box or switch), else its first control.</summary>
    internal bool FocusFirstField()
    {
        UpdateLayout();
        var field = Descendants(body).OfType<Control>().FirstOrDefault(c => c is TextBox { IsReadOnly: false } or ComboBox or CheckBox or RadioButton && c.IsEnabled && c.IsVisible && c.Focusable);
        if (field != null) { field.BringIntoView(); return field.Focus(); }
        return body.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>The source model is read: the card keeps it (and the draft of the same definition) until the window shows what to draw.</summary>
    internal void ShowAnimation(DocumentModel document, DefinitionEntry entry, SourceAnimationEdits.Definition definition, FrameworkElement drops)
    {
        var kept = Entry?.Key == entry.Key && Document == document ? animationChanges.Values.ToArray() : [];
        string? selected = animationRecord;
        Begin(entry, document); animationDefinition = definition; animationRevision = document.Revision; animationWindowRecords.Clear();
        foreach (var change in kept) animationChanges[change.Field] = change;
        if (selected != null && definition.Records.Any(r => r.Id == selected)) animationRecord = selected;
        animationDrops = drops;
        heading.Text = definition.Name; subtitle.Text = "Definition · " + System.IO.Path.GetFileName(definition.File);
        subtitle.ToolTip = definition.File;
        SetTile("Definition", OutlinerKinds.SectionBrush(entry));
        AddOpenAnimation(entry);
        body.Children.Add(Help("Reading the definition…"));
        Finish(editable: false);
        HeaderChanged?.Invoke();
    }

    // ---------------------------------------------------------------- drawing what the window built

    /// <summary>
    /// Shows <paramref name="shown"/>: the same card again (its key and source model unchanged) refreshes only what follows the
    /// run (its live sections, prerequisite marks, the observed threshold), so a field being typed in keeps its focus.
    /// </summary>
    internal void Inspect(AnimationInspection shown, bool animationMode)
    {
        if (animationDefinition == null || Entry is not DefinitionEntry) return;
        bool same = inspection != null && inspection.Key == shown.Key && ReferenceEquals(inspectedModel, animationDefinition) && inspectorMode == animationMode
            && inspection.Sections.Select(s => s.Id).SequenceEqual(shown.Sections.Select(s => s.Id));
        inspectorMode = animationMode;
        if (same) { Refresh(shown); return; }
        bool keepScroll = inspection != null && inspection.Key == shown.Key;
        Render(shown, keepScroll);
    }
    /// <summary>
    /// A compiled animation's card (design §19): the same sections drawn read only, without a source model, a draft or a footer;
    /// its switches, include boxes and links still raise their intents (the compiled scenario and selection).
    /// </summary>
    internal void InspectCompiled(AnimationInspection shown)
    {
        readOnlyInspection = true;
        bool same = inspection != null && inspection.Key == shown.Key && inspectorMode
            && inspection.Sections.Select(s => s.Id).SequenceEqual(shown.Sections.Select(s => s.Id));
        inspectorMode = true;
        if (same) { Refresh(shown); return; }
        Render(shown, keepScroll: inspection != null && inspection.Key == shown.Key);
    }
    private bool readOnlyInspection;

    private void Render(AnimationInspection shown, bool keepScroll)
    {
        double offset = contentScroll.VerticalOffset;
        bool focused = IsKeyboardFocusWithin;
        inspection = shown; inspectedModel = animationDefinition;
        body.Children.Clear(); inspectorSections.Clear(); fieldMarks.Clear(); thresholdShown = null; openAnimation = null;
        heading.Text = shown.Title; heading.ToolTip = shown.Title;
        subtitle.Text = shown.Subtitle; subtitle.ToolTip = animationDefinition?.File;
        if (shown.Glyph.StartsWith("graph:", StringComparison.Ordinal)) SetGraphTile(shown.Glyph["graph:".Length..]);
        else SetTile(shown.Glyph, Entry is DefinitionEntry definition ? OutlinerKinds.SectionBrush(definition) : null);
        foreach (var section in shown.Sections)
        {
            if (section.Title.Length == 0) { foreach (var item in section.Items) body.Children.Add(RenderItem(item, section)); continue; }
            var card = Section(section.Title, section.Expanded);
            card.Summary = section.Summary ?? "";
            card.Tag = section.Id;
            inspectorSections[section.Id] = card;
            foreach (var item in section.Items) card.Body.Children.Add(RenderItem(item, section));
            if (externalDraft && section.Title != "Drop windows") card.IsEnabled = false;
        }
        if (shown.Kind == "map" && openAnimation != null) openAnimation.Visibility = openAnimationShown ? Visibility.Visible : Visibility.Collapsed;
        RefreshFooterVisibility();
        RefreshFooter();
        HeaderChanged?.Invoke();
        if (keepScroll) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => contentScroll.ScrollToVerticalOffset(offset));
        else if (shown.ScrollTo is { } target && inspectorSections.TryGetValue(target, out var scrollTo))
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { scrollTo.Expand(); scrollTo.BringIntoView(new Rect(0, 0, 1, scrollTo.ActualHeight)); });
        else contentScroll.ScrollToTop();
        if (focused && !IsKeyboardFocusWithin) Focus();
    }
    /// <summary>The run moved (or the scenario changed): live sections, prerequisite marks and the observed threshold follow.</summary>
    private void Refresh(AnimationInspection shown)
    {
        var previous = inspection;
        inspection = shown;
        foreach (var section in shown.Sections)
        {
            if (!inspectorSections.TryGetValue(section.Id, out var card)) continue;
            if (section.Id != "threshold") card.Summary = section.Summary ?? "";
            if (section.Live)
            {
                // A live section's controls are drawn again; the focused one gets its focus back by name.
                string? focusedName = card.IsKeyboardFocusWithin && Keyboard.FocusedElement is DependencyObject element ? AutomationProperties.GetName(element) : null;
                card.Body.Children.Clear();
                foreach (var item in section.Items) card.Body.Children.Add(RenderItem(item, section));
                if (focusedName is { Length: > 0 })
                {
                    card.UpdateLayout();
                    if (Descendants(card.Body).OfType<UIElement>().FirstOrDefault(e => e.Focusable && AutomationProperties.GetName(e) == focusedName) is { } again) again.Focus();
                }
                continue;
            }
            // The source excerpt arrives after the card: its section is drawn again once it changed.
            if (section.Id == "source" && previous?.Sections.FirstOrDefault(p => p.Id == "source")?.Items.OfType<InspectorSource>().FirstOrDefault() is { } was
                && section.Items.OfType<InspectorSource>().FirstOrDefault() is { } now && (was.Line != now.Line || was.Excerpt != now.Excerpt || was.Note != now.Note))
            {
                card.Body.Children.Clear();
                foreach (var item in section.Items) card.Body.Children.Add(RenderItem(item, section));
                continue;
            }
            foreach (var item in section.Items)
            {
                if (item is InspectorField { Marks: { } marks } field && fieldMarks.TryGetValue(field.FieldId, out var column)) FillMarks(column.Glyphs, column.States, marks);
                else if (item is InspectorThreshold threshold && thresholdShown is { } current && current.Item.FieldId == threshold.FieldId)
                {
                    // The sentence follows the draft; only what was observed follows the run.
                    thresholdShown = (current.Editor, threshold);
                    current.Editor.SetObserved(threshold.Observed);
                }
            }
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private void RefreshFooterVisibility()
    {
        if (readOnlyInspection) { footer.Visibility = Visibility.Collapsed; return; }
        bool editable = inspectorMode && inspection?.Kind is "definition" or "activation" or "sequence" or "event" or "condition" or "call" && !externalDraft;
        footer.Visibility = editable || HasDraft && !externalDraft ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SetTile(string kind, Brush? brush)
    {
        var icon = new PreviewIcon { Kind = kind, Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (brush != null) icon.SetValue(TextElement.ForegroundProperty, brush); else icon.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        tile.Child = icon; HeaderGlyph = kind;
    }
    private void SetGraphTile(string glyph)
    {
        var palette = AnimationGraphPalette.For(!OutlinerKinds.LightTheme);
        Brush brush = glyph switch
        {
            "state" => palette.State, "motion" or "key" => palette.Motion, "camera" => palette.Camera, "sound" => palette.Sound, "effect" => palette.Effect,
            "release" => palette.Release, "stop" or "invalidate" => palette.Stop, _ => palette.T2,
        };
        var icon = new AnimationGraphGlyph { Glyph = glyph, Size = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        TextElement.SetForeground(icon, brush);
        tile.Child = icon; HeaderGlyph = "graph:" + glyph;
    }

    private UIElement RenderItem(InspectorItem item, InspectorSection section) => item switch
    {
        InspectorText text => TextItem(text),
        InspectorPair pair => PairItem(pair),
        InspectorValue value => ValueItem(value),
        InspectorLink { Primary: true } link => LinkButton(link, primary: true),
        InspectorLink link => LinkLine(link),
        InspectorActions actions => ActionsItem(actions),
        InspectorField field => FieldItem(field),
        InspectorThreshold threshold => ThresholdItem(threshold),
        InspectorSwitch choice => SwitchItem(choice),
        InspectorInclude include => IncludeItem(include),
        InspectorTable table => TableItem(table),
        InspectorBindings bindings => BindingsItem(bindings),
        InspectorSource source => SourceItem(source),
        InspectorHost host => HostItem(host),
        InspectorNoticeGroup group => NoticeGroupItem(group),
        _ => new TextBlock(),
    };

    private static UIElement TextItem(InspectorText text)
    {
        switch (text.Style)
        {
            case "caution": return Notice(text.Text, "Warning", "SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush");
            case "info": return Notice(text.Text, "Info", "TextFillColorTertiaryBrush", "ControlFillColorDefaultBrush");
            case "mono":
                {
                    var mono = Mono(text.Text); mono.Margin = new(0, 2, 0, 6);
                    mono.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                    return mono;
                }
            case "body":
                return new TextBlock { Text = text.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 6) };
            default:
                {
                    TextBlock help = new() { Text = text.Text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 6) };
                    help.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                    return help;
                }
        }
    }
    private static Border Notice(string text, string icon, string iconBrush, string background)
    {
        Grid content = new();
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new());
        var glyph = new PreviewIcon { Kind = icon, Width = 14, Height = 14, Margin = new(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        glyph.SetResourceReference(TextElement.ForegroundProperty, iconBrush);
        content.Children.Add(glyph);
        TextBlock message = new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 16 };
        Grid.SetColumn(message, 1); content.Children.Add(message);
        Border box = new() { CornerRadius = new(4), Padding = new(8, 6, 8, 6), Margin = new(0, 2, 0, 6), Child = content };
        box.SetResourceReference(Border.BackgroundProperty, background);
        AutomationProperties.SetName(box, text);
        return box;
    }
    private UIElement PairItem(InspectorPair pair)
    {
        Grid row = new() { Margin = new(0, 2, 0, 4) };
        row.ColumnDefinitions.Add(new() { Width = new(108) }); row.ColumnDefinitions.Add(new());
        TextBlock key = new() { Text = pair.Label, FontSize = 12.5, TextWrapping = TextWrapping.WrapWithOverflow, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        key.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        row.Children.Add(key);
        UIElement value = pair.Link is { } link ? LinkText(link, pair.Value) : new TextBlock { Text = pair.Value, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(value, 1); row.Children.Add(value);
        return row;
    }
    /// <summary>A stored value: its caption, then a selectable read-only field (a unit at its right; monospace for keywords and bytes).</summary>
    private static UIElement ValueItem(InspectorValue item)
    {
        StackPanel panel = new() { Margin = new(0, 0, 0, 4) };
        panel.Children.Add(FieldCaption(item.Label));
        bool lines = item.Value.Contains('\n');
        TextBox box = new()
        {
            Text = item.Value, IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = lines ? TextWrapping.NoWrap : TextWrapping.Wrap, MinHeight = 30,
            Padding = new(8, 4, item.Suffix == null ? 8 : 44, 4), VerticalContentAlignment = VerticalAlignment.Center, AcceptsReturn = lines,
            HorizontalScrollBarVisibility = lines ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = lines ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            MaxHeight = lines ? 260 : double.PositiveInfinity, FontSize = item.Mono ? 12 : 12.5,
        };
        if (item.Mono) box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        AutomationProperties.SetName(box, item.Label);
        Grid field = new(); field.Children.Add(box);
        if (item.Suffix != null)
        {
            TextBlock unit = new() { Text = item.Suffix, FontSize = 12, Margin = new(0, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            unit.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            field.Children.Add(unit);
        }
        panel.Children.Add(field);
        if (item.Help is { Length: > 0 } help) panel.Children.Add(TextItem(new InspectorText(help)));
        return panel;
    }
    /// <summary>A link as literal text (an animation name keeps its underscores).</summary>
    private TextBlock LinkText(InspectorLink link, string? text = null)
    {
        TextBlock block = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Hyperlink hyperlink = new(new Run(text ?? link.Text)) { ToolTip = link.Tip };
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        AutomationProperties.SetName(hyperlink, text ?? link.Text);
        hyperlink.Click += (_, _) => InspectorLinkInvoked?.Invoke(link);
        block.Inlines.Add(hyperlink);
        return block;
    }
    private UIElement LinkLine(InspectorLink link) { var line = LinkText(link); line.Margin = new(0, 2, 0, 6); return line; }
    private Button LinkButton(InspectorLink link, bool primary)
    {
        if (primary && link.Action == "open" && inspection?.Kind == "map")
        {
            var button = PrimaryButton(link.Text, link.Tip ?? link.Text);
            button.Click += (_, _) => InspectorLinkInvoked?.Invoke(link);
            openAnimation = button;
            return button;
        }
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        if (link.Action == "open") content.Children.Add(new PreviewIcon { Kind = "Open", Width = 11, Height = 11, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = link.Text, VerticalAlignment = VerticalAlignment.Center });
        Button plain = new() { Content = content, Height = 30, Padding = new(10, 0, 12, 0), Margin = new(0, 2, 6, 2), ToolTip = link.Tip };
        AutomationProperties.SetName(plain, link.Text);
        plain.Click += (_, _) => InspectorLinkInvoked?.Invoke(link);
        return plain;
    }
    private UIElement ActionsItem(InspectorActions actions)
    {
        WrapPanel row = new() { Margin = new(0, 6, 0, 2) };
        foreach (var link in actions.Links) row.Children.Add(LinkButton(link, false));
        return row;
    }

    // ---------------------------------------------------------------- source fields (the draft)

    private static TextBlock FieldCaption(string text)
    {
        TextBlock caption = new() { Text = text, FontSize = 12, Margin = new(0, 6, 0, 4), TextWrapping = TextWrapping.Wrap };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return caption;
    }
    private SourceAnimationEdits.Field? FindField(string id) => animationDefinition?.Records.SelectMany(r => r.Fields).FirstOrDefault(f => f.Id == id);
    /// <summary>A field's values as the draft has them, and whether it is present.</summary>
    private (string[] Values, bool Present) Current(SourceAnimationEdits.Field field)
    {
        var changed = animationChanges.GetValueOrDefault(field.Id);
        bool present = changed == null ? field.Present : changed.Values != null;
        return (changed?.Values?.ToArray() ?? field.Values.ToArray(), present);
    }
    private void Change(SourceAnimationEdits.Field field, IReadOnlyList<string>? values)
    {
        if (Applying) return;
        // Names are written without surrounding spaces (Core trims them too and refuses a blank one).
        values = values?.Select(v => v.Trim()).ToArray();
        if (values == null ? !field.Present : field.Present && values.SequenceEqual(field.Values)) animationChanges.Remove(field.Id);
        else animationChanges[field.Id] = new(field.Id, values);
        Changed();
    }

    private UIElement FieldItem(InspectorField item)
    {
        if (FindField(item.FieldId) is not { } field || inspection == null || animationDefinition?.Index != inspection.Definition)
            return TextItem(new InspectorText((item.Label ?? "A field") + ": loading the source…"));
        string label = item.Label ?? field.Label;
        StackPanel panel = new() { Margin = new(0, 0, 0, 4) };
        var (values, present) = Current(field);
        bool locked = field.ReadOnly != null || Document?.Revision != animationRevision;
        if (item.Layout == "check")
        {
            CheckBox check = new() { Content = new TextBlock { Text = label }, IsChecked = present, IsEnabled = !locked, Margin = new(0, 6, 0, 2), MinHeight = 28 };
            AutomationProperties.SetName(check, label);
            check.Checked += (_, _) => Change(field, field.Values.Length > 0 ? field.Values : field.Choices.Length > 0 ? [field.Choices[0]] : ["ON_CALL"]);
            check.Unchecked += (_, _) => Change(field, null);
            panel.Children.Add(check);
            if (item.Help != null) { var help = TextItem(new InspectorText(item.Help)); ((FrameworkElement)help).Margin = new(28, 0, 0, 6); panel.Children.Add(help); }
            if (field.ReadOnly != null) panel.Children.Add(TextItem(new InspectorText(field.ReadOnly)));
            return panel;
        }
        if (item.Layout == "list")
        {
            panel.Children.Add(FieldCaption(label));
            Grid grid = new();
            // The names keep most of the width; each line's state is its first clause (the whole in its tooltip).
            grid.ColumnDefinitions.Add(new() { Width = new(20) }); grid.ColumnDefinitions.Add(new() { MinWidth = 140 }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto, MaxWidth = 96 });
            TextBox list = new() { Text = string.Join(Environment.NewLine, values), AcceptsReturn = true, MinHeight = 60, MaxHeight = 220, MaxLength = 65536, IsEnabled = !locked,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Cascadia Mono, Consolas"), FontSize = 12, Padding = new(6, 4, 6, 4) };
            TextBlock.SetLineHeight(list, 18); TextBlock.SetLineStackingStrategy(list, LineStackingStrategy.BlockLineHeight);
            AutomationProperties.SetName(list, label);
            list.TextChanged += (_, _) => Change(field, list.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Grid.SetColumn(list, 1); grid.Children.Add(list);
            // The ✓/○ satisfaction beside each line, and its state at the right.
            StackPanel marks = new() { Margin = new(0, 5, 0, 0) };
            StackPanel states = new() { Margin = new(8, 5, 0, 0) };
            Grid.SetColumn(states, 2); grid.Children.Add(marks); grid.Children.Add(states);
            fieldMarks[item.FieldId] = (marks, states);
            FillMarks(marks, states, item.Marks ?? []);
            panel.Children.Add(grid);
            if (item.Help != null) panel.Children.Add(TextItem(new InspectorText(item.Help)));
            return panel;
        }
        // auto: an optional field's Use check box, then its boxes (a combo for choices, X/Y/Z for a vector, a name box).
        CheckBox? use = null;
        if (field.Optional)
        {
            use = new() { Content = new TextBlock { Text = label }, IsChecked = present, IsEnabled = !locked, Margin = new(0, 6, 0, 4), MinHeight = 24, ToolTip = "Use: write " + field.Key + " in the source" };
            AutomationProperties.SetName(use, "Use " + label); panel.Children.Add(use);
        }
        else panel.Children.Add(FieldCaption(label));
        List<Control> controls = [];
        void Update()
        {
            if (Applying) return;
            bool on = use?.IsChecked ?? true;
            string[] next = values.ToArray();
            for (int i = 0; i < controls.Count; i++)
            {
                controls[i].IsEnabled = on && !locked;
                next[i] = controls[i] is ComboBox combo ? (string?)combo.SelectedItem ?? "" : ((TextBox)controls[i]).Text;
            }
            values = next;
            Change(field, on ? next : null);
        }
        Control Box(int i)
        {
            if (i == 0 && field.Choices.Length > 0)
            {
                ComboBox combo = new() { ItemsSource = field.Choices.Contains(values[i]) ? field.Choices : [values[i], .. field.Choices], SelectedItem = values[i], Height = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
                combo.SelectionChanged += (_, _) => Update();
                return combo;
            }
            TextBox box = new() { Text = values[i], MaxLength = 512, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
            box.TextChanged += (_, _) => Update();
            return box;
        }
        bool vector = values.Length >= 3 && values.Length <= 4 && field.Kinds.Length == values.Length && field.Kinds.TakeLast(3).All(k => k is "number" or "integer")
            && (values.Length == 3 || field.Kinds[0] == "text");
        if (vector && values.Length == 4)
        {
            var name = Box(0); name.Margin = new(0, 0, 0, 4); AutomationProperties.SetName(name, label + " node"); controls.Add(name); panel.Children.Add(name);
        }
        if (vector)
        {
            Grid xyz = new();
            string[] axes = ["X", "Y", "Z"]; string[] colors = ["#FF7A7A", "#7BD88F", "#6CC4FF"];
            for (int a = 0; a < 3; a++)
            {
                xyz.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                int i = values.Length - 3 + a;
                var box = (TextBox)Box(i); box.Padding = new(20, 0, 4, 0); box.Margin = new(a == 0 ? 0 : 3, 0, a == 2 ? 0 : 3, 0);
                AutomationProperties.SetName(box, $"{label} {axes[a]}");
                Grid holder = new(); holder.Children.Add(box);
                holder.Children.Add(new TextBlock { Text = axes[a], FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = OutlinerKinds.Frozen(colors[a]), Margin = new(8 + (a == 0 ? 0 : 3), 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
                Grid.SetColumn(holder, a); xyz.Children.Add(holder);
                controls.Add(box);
            }
            panel.Children.Add(xyz);
        }
        else if (values.Length > 0)
        {
            Grid row = new();
            for (int i = 0; i < values.Length; i++)
            {
                row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                var control = Box(i);
                control.Margin = new(i == 0 ? 0 : 3, 0, i == values.Length - 1 ? 0 : 3, 0);
                AutomationProperties.SetName(control, label + (values.Length > 1 ? $" {i + 1}" : ""));
                Grid.SetColumn(control, i); row.Children.Add(control); controls.Add(control);
            }
            panel.Children.Add(row);
        }
        foreach (var control in controls) control.IsEnabled = (use?.IsChecked ?? true) && !locked;
        if (use != null) { use.Checked += (_, _) => Update(); use.Unchecked += (_, _) => Update(); }
        if (item.Help != null) panel.Children.Add(TextItem(new InspectorText(item.Help)));
        if (field.ReadOnly != null) panel.Children.Add(TextItem(new InspectorText(field.ReadOnly)));
        if (item.Marks is { Count: > 0 } objectMarks)
        {
            StackPanel line = new() { Orientation = Orientation.Horizontal, Margin = new(0, 2, 0, 4) };
            fieldMarks[item.FieldId] = (line, null);
            FillMarks(line, null, objectMarks);
            panel.Children.Add(line);
        }
        if (inspection?.Record is { } record && animationWindowRecords.Contains(record) && field.Key != "START_TIME" && controls.Count > 0 && ReferenceEquals(item, FirstField()))
            panel.Children.Insert(0, TextItem(new InspectorText("Editing this event can change or remove its drop-window relationship. Verified window-move guarantees apply only to Move drop window.", "caution")));
        return panel;
        InspectorField? FirstField() => inspection?.Sections.SelectMany(s => s.Items).OfType<InspectorField>().FirstOrDefault();
    }
    private static void FillMarks(Panel holder, Panel? states, IReadOnlyList<InspectorMark> marks)
    {
        var palette = AnimationGraphPalette.For(!OutlinerKinds.LightTheme);
        if (states != null)
        {
            var glyphs = holder;
            glyphs.Children.Clear(); states.Children.Clear();
            foreach (var mark in marks.Take(256))
            {
                glyphs.Children.Add(new TextBlock { Text = mark.Mark, FontSize = 12, Height = 18, Foreground = mark.Mark == "✓" ? palette.Run : palette.T3, ToolTip = mark.Status });
                int clause = mark.Status.IndexOf(" · ", StringComparison.Ordinal);
                var state = new TextBlock { Text = clause > 0 ? mark.Status[..clause] : mark.Status, ToolTip = mark.Status, FontSize = 11, Height = 18, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = mark.Status.StartsWith("unmet", StringComparison.Ordinal) ? palette.Caution : palette.T3 };
                states.Children.Add(state);
            }
            return;
        }
        holder.Children.Clear();
        foreach (var mark in marks)
        {
            holder.Children.Add(new TextBlock { Text = mark.Mark + " ", FontSize = 12, Foreground = mark.Mark == "✓" ? palette.Run : palette.T3 });
            holder.Children.Add(new TextBlock { Text = mark.Status, FontSize = 11.5, Foreground = palette.T3 });
        }
    }

    private UIElement ThresholdItem(InspectorThreshold item)
    {
        StackPanel panel = new();
        var field = item.FieldId == null ? null : FindField(item.FieldId);
        bool modelReady = field != null && inspection != null && animationDefinition?.Index == inspection.Definition;
        bool editable = modelReady && item.ReadOnly == null && Document?.Revision == animationRevision;
        string[] values; bool present;
        if (field == null) { values = [item.Clock, item.Seconds.ToString("R", CultureInfo.InvariantCulture)]; present = item.Present; }
        else (values, present) = Current(field);
        string clock = present && values.Length == 2 ? values[0] : "ANIMATION_OFFSET";
        bool numeric = double.TryParse(present && values.Length == 2 ? values[1] : "0", NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds);
        if (!present)
        {
            panel.Children.Add(PairItem(new InspectorPair("Clock", "None (animation clock 0)")));
            Button add = new() { Content = "Add threshold", Height = 30, Padding = new(10, 0, 12, 0), Margin = new(0, 6, 0, 4), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = editable,
                ToolTip = "Write START_TIME for this event (a draft until Apply)" };
            AutomationProperties.SetName(add, "Add threshold");
            add.Click += (_, _) => { if (field != null) { Change(field, ["ANIMATION_OFFSET", "0"]); RerenderSection("threshold"); } };
            panel.Children.Add(add);
            panel.Children.Add(TextItem(new InspectorText(item.Sentence)));
            if (item.Note != null) panel.Children.Add(TextItem(new InspectorText(item.Note)));
        }
        else
        {
            AnimationThresholdEditor editor = new();
            editor.SetValue(clock, numeric ? seconds : 0, editable && numeric);
            editor.SetText(numeric ? Sentence(clock, seconds) : "The stored value is not a number; edit it in the value box.", item.Observed, item.Note);
            if (field != null)
            {
                editor.TypingBegan += () => typedFrom[field.Id] = animationChanges.GetValueOrDefault(field.Id);
                editor.TextEdited += text => Change(field, [Current(field).Values.ElementAtOrDefault(0) ?? clock, text]);
                // Escape in the value box: the field's draft change goes back to what it was before the typing (P1: Apply never
                // writes text the field no longer shows).
                editor.TypingReverted += () =>
                {
                    if (Applying || !typedFrom.Remove(field.Id, out var before)) return;
                    if (before == null) animationChanges.Remove(field.Id); else animationChanges[field.Id] = before;
                    Changed();
                };
                editor.ValueProposed += value =>
                {
                    typedFrom.Remove(field.Id);
                    string mode = Current(field).Values.ElementAtOrDefault(0) ?? clock;
                    Change(field, [mode, value.ToString("R", CultureInfo.InvariantCulture)]);
                    editor.SetValue(mode, value, editable);
                    editor.SetText(Sentence(mode, value), item.Observed, item.Note);
                };
                editor.ClockProposed += mode =>
                {
                    typedFrom.Remove(field.Id);
                    var current = Current(field).Values;
                    Change(field, [mode, current.ElementAtOrDefault(1) ?? "0"]);
                    editor.SetValue(mode, double.TryParse(current.ElementAtOrDefault(1), NumberStyles.Float, CultureInfo.InvariantCulture, out double kept) && double.IsFinite(kept) ? kept : 0, editable);
                    editor.SetText(Sentence(mode, kept), item.Observed, item.Note);
                };
            }
            thresholdShown = (editor, item);
            panel.Children.Add(editor);
            if (field is { Optional: true })
            {
                Button clear = new() { Content = "Clear", Height = 28, Padding = new(10, 0, 12, 0), Margin = new(0, 6, 0, 2), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = editable,
                    ToolTip = "Remove START_TIME: the event may start as soon as the sequence reaches it (animation clock 0)" };
                AutomationProperties.SetName(clear, "Clear threshold");
                clear.Click += (_, _) => { Change(field, null); RerenderSection("threshold"); };
                panel.Children.Add(clear);
            }
        }
        if (item.ReadOnly != null && modelReady) panel.Children.Add(TextItem(new InspectorText(item.ReadOnly)));
        return panel;
        static string Sentence(string mode, double value)
        {
            byte clock = mode switch { "SEQUENCE_OFFSET" => 2, "EVENT_OFFSET" => 3, _ => 1 };
            string words = AnimationPhrases.ThresholdWords(clock, (float)value);
            return char.ToUpperInvariant(words[0]) + words[1..] + ". Earlier events, waits and conditions can delay it.";
        }
    }
    private void RerenderSection(string id)
    {
        if (inspection == null || !inspectorSections.TryGetValue(id, out var card) || inspection.Sections.FirstOrDefault(s => s.Id == id) is not { } section) return;
        card.Body.Children.Clear();
        foreach (var item in section.Items) card.Body.Children.Add(RenderItem(item, section));
        card.Summary = id == "threshold" && thresholdShown is { } shown ? shown.Editor.ClockMode switch { "SEQUENCE_OFFSET" => "seq", "EVENT_OFFSET" => "prev", _ => "anim" } + " " + shown.Editor.Value.ToString("0.###", CultureInfo.InvariantCulture) + " s"
            : id == "threshold" ? "none" : section.Summary ?? "";
    }

    // ---------------------------------------------------------------- scenario controls

    private UIElement SwitchItem(InspectorSwitch item)
    {
        StackPanel panel = new() { Margin = new(0, 2, 0, 8) };
        string current = item.Choice switch { AnimationConditionChoice.True => "True", AnimationConditionChoice.False => "False", _ => "Game" };
        ChoiceGroup group = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(group, $"{item.Label}: {current}");
        AutomationProperties.SetHelpText(group, "For " + item.Scope + ". Game: the preview applies the game's rule with the scenario's inputs; True or False force it for this call path.");
        Border frame = new() { Child = group, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (choice, text) in new[] { (AnimationConditionChoice.Game, "Game"), (AnimationConditionChoice.True, "True"), (AnimationConditionChoice.False, "False") })
        {
            RadioButton option = new() { Content = text, IsChecked = choice == item.Choice, Width = 58, Height = 26, FontSize = 12, Tag = text.ToLowerInvariant(), GroupName = "switch" + item.Path + item.Event.ToString("N"),
                ToolTip = choice == AnimationConditionChoice.Game ? "Game: the preview applies the game's rule with the scenario's inputs (G)" : $"Force {text.ToLowerInvariant()} for {item.Scope} ({text[0]})" };
            option.SetResourceReference(StyleProperty, "GraphSwitchSegment");
            AutomationProperties.SetName(option, text);
            option.Click += (_, _) => { if (choice != item.Choice) InspectorConditionChosen?.Invoke(item, choice); };
            group.Children.Add(option);
        }
        panel.Children.Add(frame);
        TextBlock scope = new() { Text = item.Scope + (item.Outcome != null ? " · " + item.Outcome : ""), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
        scope.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        panel.Children.Add(scope);
        return panel;
    }
    /// <summary>The condition switch as UI Automation reads it: a group of radio buttons named by its question and choice.</summary>
    private sealed class ChoiceGroup : StackPanel
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
        private sealed class GroupPeer(ChoiceGroup owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
            protected override string GetClassNameCore() => "ConditionSwitch";
        }
    }
    private UIElement IncludeItem(InspectorInclude item)
    {
        StackPanel panel = new() { Margin = new(0, 2, 0, 6) };
        CheckBox check = new() { Content = new TextBlock { Text = item.Label, TextWrapping = TextWrapping.Wrap }, IsThreeState = false, IsChecked = item.Included, IsEnabled = item.Enabled, MinHeight = 28,
            ToolTip = item.Help };
        AutomationProperties.SetName(check, item.Label);
        AutomationProperties.SetHelpText(check, item.Help);
        check.Click += (_, _) => { bool value = item.Included != true; check.IsChecked = item.Included; InspectorIncludeChosen?.Invoke(item, value); };
        panel.Children.Add(check);
        var help = TextItem(new InspectorText(item.Help)); ((FrameworkElement)help).Margin = new(28, 0, 0, 2);
        panel.Children.Add(help);
        return panel;
    }

    private UIElement TableItem(InspectorTable table)
    {
        StackPanel panel = new() { Margin = new(0, 2, 0, 4) };
        if (table.Rows.Count == 0) { panel.Children.Add(TextItem(new InspectorText(table.Empty ?? "None."))); return panel; }
        bool seek = table.Rows.Any(r => r.Seek != null);
        Grid grid = new();
        int columns = table.Columns.Count + (seek ? 1 : 0);
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new() { Width = c == 0 ? new(34) : c == columns - 1 && seek ? GridLength.Auto : new(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (int c = 0; c < table.Columns.Count; c++)
        {
            TextBlock head = new() { Text = table.Columns[c], FontSize = 11.5, Margin = new(0, 0, 6, 4) };
            head.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            Grid.SetColumn(head, c); grid.Children.Add(head);
        }
        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            if (row.Selected)
            {
                Border mark = new() { CornerRadius = new(3) };
                mark.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                Grid.SetRow(mark, r + 1); Grid.SetColumnSpan(mark, columns); grid.Children.Add(mark);
            }
            for (int c = 0; c < row.Cells.Count && c < table.Columns.Count; c++)
            {
                TextBlock cell = new() { Text = row.Cells[c], FontSize = 12, Margin = new(0, 2, 6, 2), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = row.Cells[c] };
                if (c == 2 && table.Columns[c] == "Value") cell.FontWeight = FontWeights.SemiBold;
                System.Windows.Documents.Typography.SetNumeralAlignment(cell, FontNumeralAlignment.Tabular);
                Grid.SetRow(cell, r + 1); Grid.SetColumn(cell, c); grid.Children.Add(cell);
            }
            if (row.Seek is double at)
            {
                var link = LinkText(new InspectorLink("Seek", "seek", at.ToString("R", CultureInfo.InvariantCulture)) { Tip = "Move the playhead to " + AnimationSeconds.Format(at) });
                link.FontSize = 12; link.Margin = new(0, 2, 0, 2);
                AutomationProperties.SetName(link, "Seek to " + AnimationSeconds.Format(at));
                Grid.SetRow(link, r + 1); Grid.SetColumn(link, columns - 1); grid.Children.Add(link);
            }
        }
        panel.Children.Add(grid);
        if (table.More > 0) panel.Children.Add(TextItem(new InspectorText($"+{table.More:N0} more")));
        return panel;
    }

    private UIElement BindingsItem(InspectorBindings bindings)
    {
        StackPanel panel = new();
        foreach (var binding in bindings.Rows)
        {
            DockPanel line = new() { Height = 30 };
            if (binding.Frame)
            {
                Button frame = new() { Content = new PreviewIcon { Kind = "FrameSelection", Width = 14, Height = 14 }, ToolTip = "Frame " + binding.Label };
                frame.SetResourceReference(StyleProperty, "CommandButton"); frame.Width = 28; frame.Height = 28;
                AutomationProperties.SetName(frame, "Frame " + binding.Label);
                frame.Click += (_, _) => InspectorLinkInvoked?.Invoke(new InspectorLink(binding.Label, "frame", binding.Entry.ToString(CultureInfo.InvariantCulture)));
                DockPanel.SetDock(frame, Dock.Right); line.Children.Add(frame);
            }
            if (bindings.Choose)
            {
                RadioButton plays = new() { IsChecked = binding.Plays, GroupName = "bindings", VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Margin = new(0, 0, 4, 0),
                    ToolTip = binding.Plays ? "Animation mode plays this binding" : "Play this binding in Animation mode" };
                AutomationProperties.SetName(plays, "Animation mode plays " + binding.Label);
                plays.Click += (_, _) => { if (!binding.Plays) { plays.IsChecked = false; InspectorBindingChosen?.Invoke(binding.Entry); } };
                DockPanel.SetDock(plays, Dock.Left); line.Children.Add(plays);
            }
            var cube = new PreviewIcon { Kind = "Cube", Width = 13, Height = 13, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            cube.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            DockPanel.SetDock(cube, Dock.Left); line.Children.Add(cube);
            UIElement label = binding.Root is int root
                ? LinkText(new InspectorLink(binding.Label, "object", root.ToString(CultureInfo.InvariantCulture)) { Tip = "Select the bound object in the view" })
                : new TextBlock { Text = binding.Label, FontSize = 12.5 };
            ((FrameworkElement)label).VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(label);
            panel.Children.Add(line);
        }
        if (bindings.More > 0) panel.Children.Add(TextItem(new InspectorText($"and {bindings.More:N0} more bindings (the binding picker in the command bar lists them all)")));
        return panel;
    }

    private UIElement SourceItem(InspectorSource source)
    {
        StackPanel panel = new();
        string where = source.File + (source.Line is int line ? $" · line {line}" : "") + (source.Missions.Count > 0 ? " · Affected maps: " + string.Join(", ", source.Missions) : "");
        var head = Mono(where); head.TextWrapping = TextWrapping.Wrap; head.Margin = new(0, 0, 0, 6);
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(head);
        if (source.Excerpt is { } excerpt)
        {
            TextBox text = new() { Text = Dedent(excerpt), IsReadOnly = true, AcceptsReturn = true, FontFamily = new("Cascadia Mono, Consolas"), FontSize = 11.5, MaxHeight = 240,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
            AutomationProperties.SetName(text, "Animation source excerpt"); panel.Children.Add(text);
            if (source.Truncated) panel.Children.Add(TextItem(new InspectorText("Excerpt: the first lines of the record.")));
        }
        if (source.Note != null) panel.Children.Add(TextItem(new InspectorText(source.Note)));
        return panel;
    }

    /// <summary>The excerpt without the indentation its lines share (the record's own; the first line starts mid-line).</summary>
    private static string Dedent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int common = lines.Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart(' ', '\t').Length).DefaultIfEmpty(0).Min();
        return string.Join(Environment.NewLine, lines.Select((l, i) => i == 0 ? l.TrimStart() : l.Length >= common ? l[common..] : l.TrimStart()));
    }
    /// <summary>Whether the card's "n preview approximations" line is open (kept while the same card refreshes with the run).</summary>
    private bool noticeGroupOpen;
    private string? noticeGroupKey;
    private UIElement NoticeGroupItem(InspectorNoticeGroup group)
    {
        if (noticeGroupKey != inspection?.Key) { noticeGroupKey = inspection?.Key; noticeGroupOpen = false; }
        StackPanel panel = new() { Margin = new(0, 0, 0, 4) };
        StackPanel lines = new() { Margin = new(0, 4, 0, 0), Visibility = noticeGroupOpen ? Visibility.Visible : Visibility.Collapsed };
        foreach (string line in group.Lines) lines.Children.Add(Notice(line, "Info", "TextFillColorTertiaryBrush", "ControlFillColorDefaultBrush"));
        TextBlock label = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        void Show() => label.Text = group.Label + (noticeGroupOpen ? " ▾" : " ▸");
        Show();
        Button toggle = new() { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Padding = new(6, 0, 8, 0), Margin = new(-6, 0, 0, 0),
            ToolTip = "How the preview approximates the game for this animation's kinds of events (not about one item)" };
        toggle.SetResourceReference(StyleProperty, "RowLinkButton"); toggle.Width = double.NaN; toggle.Height = 26; toggle.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(toggle, group.Label);
        AutomationProperties.SetHelpText(toggle, noticeGroupOpen ? "Expanded: click to collapse" : "Collapsed: click to list them");
        toggle.Click += (_, _) =>
        {
            noticeGroupOpen = !noticeGroupOpen; lines.Visibility = noticeGroupOpen ? Visibility.Visible : Visibility.Collapsed; Show();
            AutomationProperties.SetHelpText(toggle, noticeGroupOpen ? "Expanded: click to collapse" : "Collapsed: click to list them");
        };
        panel.Children.Add(toggle); panel.Children.Add(lines);
        return panel;
    }
    private UIElement HostItem(InspectorHost host)
    {
        var content = host.Key == "drops" ? animationDrops : null;
        if (content == null) return TextItem(new InspectorText(host.Note ?? ""));
        switch (content.Parent)
        {
            case Panel parent: parent.Children.Remove(content); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl control: control.Content = null; break;
        }
        if (host.Key == "drops")
        {
            // The drop windows' panel hides itself when the definition has no flight to callback 911.
            Border holder = new() { Child = content };
            return holder;
        }
        return content;
    }

    // ---------------------------------------------------------------- drafts from elsewhere (MCP, Timing)

    /// <summary>
    /// The START_TIME change of an event record (the selected one when <paramref name="record"/> is null): its clock
    /// (animation, sequence or previous) and seconds, each kept from the draft or the source when not given.
    /// </summary>
    internal SourceAnimationEdits.Change StartTimeChange(string? record, string? clock, double? seconds)
    {
        if (animationDefinition == null) throw new InvalidOperationException("Select an animation definition first.");
        string id = record ?? animationRecord ?? throw new InvalidOperationException("Select an event first.");
        var item = animationDefinition.Records.FirstOrDefault(r => r.Id == id) ?? throw new InvalidOperationException("The event record is unavailable.");
        var field = item.Fields.FirstOrDefault(f => f.Key == "START_TIME") ?? throw new InvalidOperationException("This record has no start threshold.");
        if (field.ReadOnly != null) throw new InvalidOperationException(field.ReadOnly);
        string[] current = animationChanges.GetValueOrDefault(field.Id)?.Values?.ToArray() ?? (field.Present && field.Values.Length == 2 ? field.Values.ToArray() : ["ANIMATION_OFFSET", "0"]);
        string mode = clock switch
        {
            null => current[0], "animation" => "ANIMATION_OFFSET", "sequence" => "SEQUENCE_OFFSET", "previous" => "EVENT_OFFSET",
            _ => throw new ArgumentException("The clock is animation, sequence or previous."),
        };
        string value = seconds is double given ? given.ToString("R", CultureInfo.InvariantCulture) : current.ElementAtOrDefault(1) ?? "0";
        return new(field.Id, [mode, value]);
    }

    internal void SetAnimationChanges(IReadOnlyList<SourceAnimationEdits.Change> changes)
    {
        if (animationDefinition == null || Applying) throw new InvalidOperationException("Select an animation definition first.");
        SourceAnimationEdits.ValidateChanges(animationDefinition, changes);
        if (animationChanges.Keys.Concat(changes.Select(c => c.Field)).Distinct().Count() > 256) throw new InvalidOperationException("Apply the current changes before editing more fields.");
        foreach (var requested in changes)
        {
            typedFrom.Remove(requested.Field);
            var field = animationDefinition.Records.SelectMany(r => r.Fields).Single(f => f.Id == requested.Field);
            // The draft holds the values as they will be written: names without surrounding spaces.
            var change = requested.Values == null ? requested : requested with { Values = SourceAnimationEdits.Trimmed(field, requested.Values) };
            if (change.Values == null && !field.Present || change.Values != null && field.Present && change.Values.SequenceEqual(field.Values)) animationChanges.Remove(change.Field);
            else animationChanges[change.Field] = change;
        }
        RerenderAnimation(); Changed();
    }
    /// <summary>The theme changed: what is drawn with the graph palette (marks, glyphs) is drawn again.</summary>
    internal void RefreshTheme() { if (inspection != null && animationDefinition != null) Render(inspection, keepScroll: true); }
    /// <summary>The draft changed from outside the fields (MCP, Revert, a Timing drag): the shown fields follow it.</summary>
    private void RerenderAnimation()
    {
        if (inspection != null && animationDefinition != null) Render(inspection, keepScroll: true);
    }
}
