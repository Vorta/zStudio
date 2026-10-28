using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

internal sealed record ScenarioStartRow(Guid Id, int Entry, string Name, string Root, double Seconds, bool Primary);
internal sealed record ScenarioPathRow(string Path, string Label, bool Included, bool BackLink);
internal sealed record ScenarioGroupRow(string Key, string Label, int Included, int Total, bool BackLink, IReadOnlyList<ScenarioPathRow> Paths);
internal sealed record ScenarioOverrideRow(AnimationConditionOverride Override, string Label);
internal sealed record ScenarioBindingChoice(int Entry, string Name, string Detail)
{
    /// <summary>What UI Automation reads for the list item.</summary>
    public override string ToString() => $"{Name}, {Detail}";
}
/// <summary>What the Scenario tab shows: one snapshot of the map's scenario and what the starts reach.</summary>
internal sealed record AnimationScenarioView(string Mission, AnimationScenario Scenario, bool Opened, IReadOnlyList<ScenarioStartRow> Starts,
    IReadOnlyList<ScenarioGroupRow> Groups, int Included, int Reachable, int Unresolved, bool Truncated, string? IncludeResult,
    IReadOnlyList<ScenarioOverrideRow> Overrides)
{
    /// <summary>A compiled animation's isolated preview: its world, bound root and range (design §19); null in a map.</summary>
    public AnimationScenarioCompiled? Compiled { get; init; }
}
/// <summary>The compiled preview's World section: its file, the mission scene (auto or chosen), the bound root (overridden or not) and the range.</summary>
internal sealed record AnimationScenarioCompiled(string File, string World, bool WorldChosen, string Root, bool RootOverridden, string Range, bool CustomRange, double RangeEnd);
/// <summary>The tab's readouts that follow the run: the player now and what each rule affects.</summary>
internal sealed record AnimationScenarioLive(string PlayerAtStart, string Prerequisites, string RangeGates, string ZoneGates);

/// <summary>
/// The Scenario tab (design/animation-redesign.md §12): starts, called animations, conditions and inputs, rules and the world
/// inputs of the map's one scenario, with Reset. It shows <see cref="AnimationScenarioView"/> snapshots and raises requests
/// named as animation_scenario's actions; the window applies them to the scenario every view and MCP share.
/// </summary>
internal sealed class AnimationScenarioPanel : UserControl
{
    private readonly StackPanel body = new() { Margin = new(10, 8, 6, 10) };
    private readonly ScrollViewer scroll;
    private readonly TextBlock header = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border resetConfirm = new() { CornerRadius = new(6), BorderThickness = new(1), Padding = new(10, 8, 10, 8), Margin = new(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    private readonly TextBlock resetText = new() { Text = "Reset the scenario? Starts, included calls, overrides and inputs go back to defaults.", TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    private readonly Button reset;
    private TextBlock? playerNow, prerequisitesCount, rangeCount, zoneCount;
    private AnimationScenarioView? view;
    private readonly HashSet<string> expandedGroups = new(StringComparer.Ordinal);

    /// <summary>A request in animation_scenario's words: startTime, removeStart, addStart, fitRange, includeAll, excludeAll, include, player, pick, seed, effectsDetail, removeOverride, goTo, prerequisites, rangeGates, zoneGates, phase, height, clearBeamOrigin, reset.</summary>
    internal event Action<string, object?>? Requested;
    internal Func<IReadOnlyList<ScenarioBindingChoice>>? BindingChoices { get; set; }

    internal AnimationScenarioPanel()
    {
        AutomationProperties.SetName(this, "Scenario");
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        reset = new() { Content = "Reset…", Height = 28, Padding = new(10, 0, 10, 0), ToolTip = "Return the scenario to its defaults" };
        AutomationProperties.SetName(reset, "Reset the scenario");
        reset.Click += (_, _) => { resetConfirm.Visibility = Visibility.Visible; reset.Visibility = Visibility.Collapsed; };
        DockPanel top = new() { Margin = new(12, 10, 10, 6) };
        DockPanel.SetDock(reset, Dock.Right); top.Children.Add(reset); top.Children.Add(header);
        resetConfirm.SetResourceReference(Border.BackgroundProperty, "SystemFillColorCautionBackgroundBrush");
        resetConfirm.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        StackPanel confirm = new();
        confirm.Children.Add(resetText);
        StackPanel confirmButtons = new() { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 0) };
        Button doReset = new() { Content = "Reset", Height = 28, Padding = new(12, 0, 12, 0), Margin = new(0, 0, 6, 0) };
        doReset.SetResourceReference(StyleProperty, "AccentButtonStyle");
        Button cancel = new() { Content = "Cancel", Height = 28, Padding = new(12, 0, 12, 0) };
        AutomationProperties.SetName(doReset, "Reset the scenario now"); AutomationProperties.SetName(cancel, "Keep the scenario");
        doReset.Click += (_, _) => { HideConfirm(); Requested?.Invoke("reset", null); };
        cancel.Click += (_, _) => HideConfirm();
        confirmButtons.Children.Add(doReset); confirmButtons.Children.Add(cancel); confirm.Children.Add(confirmButtons);
        resetConfirm.Child = confirm;
        StackPanel content = new(); content.Children.Add(resetConfirm); content.Children.Add(body);
        resetConfirm.Margin = new(12, 0, 10, 8);
        scroll = new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SetResourceReference(TemplateProperty, "InspectionScrollTemplate");
        DockPanel layout = new();
        DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top); layout.Children.Add(scroll);
        Content = layout;
        void HideConfirm() { resetConfirm.Visibility = Visibility.Collapsed; reset.Visibility = Visibility.Visible; }
    }

    internal AnimationScenarioView? View => view;

    /// <summary>
    /// Shows a scenario snapshot. The scroll position stays, and keyboard focus returns to the same field (by its name and
    /// its place among fields of that name), so a change made from the keyboard keeps its place.
    /// </summary>
    internal void SetView(AnimationScenarioView value)
    {
        view = value;
        double offset = scroll.VerticalOffset;
        var focus = FocusedField();
        resetText.Text = value.Compiled != null ? "Reset the scenario? Calls, overrides and inputs, the world, the bound root and the range go back to defaults." : "Reset the scenario? Starts, included calls, overrides and inputs go back to defaults.";
        header.Text = value.Compiled is { } compiled ? $"Preview settings of {compiled.File} · kept across assets" : $"Kept for {value.Mission} · saved with the project";
        header.ToolTip = value.Compiled != null ? "The compiled preview's scenario: its inputs, rules and world are kept while this file is open, for every animation you select in it (never saved; the file is read only); call switches, condition overrides, a bound-root override and a custom range belong to the animation shown. A change stops playback and re-evaluates the run up to the playhead."
            : "The scenario is saved with the map in the project's .zstudio/settings.json. A change stops playback and re-evaluates the run up to the playhead.";
        // The fields being replaced commit nothing more (their typed text was committed or is shown again).
        pendingCommits.Clear();
        body.Children.Clear();
        playerNow = prerequisitesCount = rangeCount = zoneCount = null;
        BuildStarts(value); BuildCalls(value); BuildConditions(value); BuildRules(value); BuildWorld(value);
        scroll.UpdateLayout(); scroll.ScrollToVerticalOffset(offset);
        if (focus is { } field) RestoreFocus(field);
    }

    /// <summary>Commits every field whose typed text is not applied yet (Exit commits before it leaves the mode).</summary>
    internal void CommitPending()
    {
        foreach (var commit in pendingCommits.ToArray()) commit();
    }
    private readonly List<Action> pendingCommits = [];

    private (string Name, Type Type, int Index)? FocusedField()
    {
        if (Keyboard.FocusedElement is not Visual focused || !IsAncestorOf(focused)) return null;
        for (DependencyObject? at = focused; at != null && at != this; at = VisualTreeHelper.GetParent(at))
            if (at is Control control && AutomationProperties.GetName(control) is { Length: > 0 } name)
            {
                int index = Fields(body).Where(c => c.GetType() == control.GetType() && AutomationProperties.GetName(c) == name).TakeWhile(c => c != control).Count();
                return (name, control.GetType(), index);
            }
        return null;
    }
    private void RestoreFocus((string Name, Type Type, int Index) field)
    {
        var match = Fields(body).Where(c => c.GetType() == field.Type && AutomationProperties.GetName(c) == field.Name).ElementAtOrDefault(field.Index)
            ?? Fields(body).FirstOrDefault(c => c.GetType() == field.Type && AutomationProperties.GetName(c) == field.Name);
        if (match is { IsVisible: true, Focusable: true }) Keyboard.Focus(match);
    }
    private static IEnumerable<Control> Fields(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Control control) yield return control;
            foreach (var nested in Fields(child)) yield return nested;
        }
    }

    /// <summary>Updates the readouts that follow the run without rebuilding the tab.</summary>
    internal void SetLive(AnimationScenarioLive live)
    {
        if (playerNow != null) playerNow.Text = live.PlayerAtStart;
        if (prerequisitesCount != null) prerequisitesCount.Text = live.Prerequisites;
        if (rangeCount != null) rangeCount.Text = live.RangeGates;
        if (zoneCount != null) zoneCount.Text = live.ZoneGates;
    }

    // ---------------------------------------------------------------- Starts

    private void BuildStarts(AnimationScenarioView value)
    {
        if (value.Compiled != null)
        {
            var one = Section("Starts", "1");
            one.Body.Children.Add(Help("The compiled preview plays one animation from 0 s: the one selected in Assets."));
            return;
        }
        var section = Section("Starts", $"{value.Starts.Count} of {AnimationSchedule.MaximumStarts}");
        if (!value.Opened) { section.Body.Children.Add(Help("Open an animation in Animation mode: it becomes the primary start.")); return; }
        foreach (var start in value.Starts)
        {
            DockPanel row = new() { Margin = new(0, 2, 0, 2), MinHeight = 32 };
            Button remove = SmallIcon("Close", "Remove this start", "Remove the start of " + start.Name);
            remove.Click += (_, _) => Requested?.Invoke("removeStart", start.Id);
            remove.Visibility = start.Primary ? Visibility.Hidden : Visibility.Visible;
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            TextBlock unit = Tertiary("s"); unit.Margin = new(4, 0, 6, 0);
            DockPanel.SetDock(unit, Dock.Right); row.Children.Add(unit);
            var seconds = new ValueTextBox { Width = 78, Height = 30, MinHeight = 30, TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, Text = AnimationSeconds.Value(start.Seconds).Replace(",", "") };
            System.Windows.Documents.Typography.SetNumeralAlignment(seconds, FontNumeralAlignment.Tabular);
            AutomationProperties.SetName(seconds, start.Name + " start seconds");
            seconds.ToolTip = "When this start runs on the shared clock, in seconds";
            string applied = seconds.Text;
            void Commit()
            {
                if (seconds.Text == applied) return;
                if (double.TryParse(seconds.Text.Trim().TrimEnd('s').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double time) && double.IsFinite(time))
                { applied = seconds.Text; Requested?.Invoke("startTime", (start.Id, Math.Clamp(time, 0, AnimationSeconds.Maximum))); }
                else seconds.Text = applied;
            }
            seconds.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { seconds.Text = applied; e.Handled = true; } };
            seconds.LostKeyboardFocus += (_, _) => Commit();
            pendingCommits.Add(Commit);
            DockPanel.SetDock(seconds, Dock.Right); row.Children.Add(seconds);
            TextBlock mark = new() { Text = start.Primary ? "★" : "▶", Width = 16, FontSize = start.Primary ? 13 : 10, VerticalAlignment = VerticalAlignment.Center, ToolTip = start.Primary ? "Primary: the opened binding" : "A start you scheduled" };
            mark.SetResourceReference(TextBlock.ForegroundProperty, start.Primary ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
            row.Children.Add(mark);
            TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5, ToolTip = $"{start.Name} · {start.Root} #{start.Entry}" + (start.Primary ? " · primary" : "") };
            name.Inlines.Add(new System.Windows.Documents.Run(start.Name));
            var detail = new System.Windows.Documents.Run($"  {start.Root} #{start.Entry}" + (start.Primary ? " · primary" : "")) { FontSize = 11.5 };
            detail.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
            name.Inlines.Add(detail);
            row.Children.Add(name);
            section.Body.Children.Add(row);
        }
        WrapPanel actions = new() { Margin = new(0, 8, 0, 0) };
        Button add = TextButton("+ Add start…", "Schedule another binding of this map on the shared clock");
        add.IsEnabled = value.Starts.Count < AnimationSchedule.MaximumStarts;
        add.Click += (_, _) => ShowAddStart(add);
        Button fit = TextButton("Fit range to starts", "The playback range from 0 s to the latest start plus 30 s");
        fit.Click += (_, _) => Requested?.Invoke("fitRange", null);
        actions.Children.Add(add); actions.Children.Add(fit);
        section.Body.Children.Add(actions);
    }

    private void ShowAddStart(Button anchor)
    {
        var choices = BindingChoices?.Invoke() ?? [];
        TextBox search = new() { MinHeight = 30, Margin = new(0, 0, 0, 6) };
        AutomationProperties.SetName(search, "Find a binding by name, root or number");
        ListBox list = new() { Height = 300, Width = 320, BorderThickness = new(0), Background = Brushes.Transparent };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        AutomationProperties.SetName(list, "Bindings to start");
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(MarginProperty, new Thickness(2, 3, 2, 3));
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ScenarioBindingChoice.Name)));
        var subtitle = new FrameworkElementFactory(typeof(TextBlock)); subtitle.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ScenarioBindingChoice.Detail)));
        subtitle.SetValue(TextBlock.FontSizeProperty, 11.5); subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.AppendChild(title); panel.AppendChild(subtitle);
        list.ItemTemplate = new DataTemplate { VisualTree = panel };
        void Filter()
        {
            string query = search.Text.Trim();
            list.ItemsSource = query.Length == 0 ? choices : choices.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Entry.ToString(CultureInfo.InvariantCulture) == query.TrimStart('#')).ToArray();
        }
        search.TextChanged += (_, _) => Filter(); Filter();
        StackPanel content = new() { Margin = new(10) };
        content.Children.Add(new TextBlock { Text = "Add a start", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) });
        content.Children.Add(search); content.Children.Add(list);
        content.Children.Add(Help($"{choices.Count:N0} bindings in this map. The start runs at 0 s; change its time in the list."));
        Border flyout = new() { Child = content, CornerRadius = new(8), BorderThickness = new(1) };
        flyout.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        flyout.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        Popup popup = new() { Child = flyout, StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PlacementTarget = anchor, VerticalOffset = 4 };
        void Choose()
        {
            if (list.SelectedItem is not ScenarioBindingChoice choice) return;
            popup.IsOpen = false; Requested?.Invoke("addStart", choice.Entry);
        }
        list.MouseLeftButtonUp += (_, _) => Choose();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Choose(); e.Handled = true; } };
        search.KeyDown += (_, e) => { if (e.Key == Key.Enter && list.Items.Count > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex); Choose(); e.Handled = true; } else if (e.Key == Key.Down && list.Items.Count > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex); (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as UIElement)?.Focus(); e.Handled = true; } };
        popup.IsOpen = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => search.Focus());
    }

    // ---------------------------------------------------------------- Called animations

    private void BuildCalls(AnimationScenarioView value)
    {
        var section = Section("Called animations", value.Opened ? $"{value.Included:N0} of {value.Reachable:N0} included" : "");
        if (!value.Opened) { section.Body.Children.Add(Help("The starts' calls are listed once an animation is open.")); return; }
        WrapPanel actions = new() { Margin = new(0, 0, 0, 6) };
        Button all = TextButton("Include all", "Include every call the starts reach, except back-links (calls back into their own chain)");
        all.Click += (_, _) => Requested?.Invoke("includeAll", null);
        Button none = TextButton("Exclude all", "Exclude every call: only the starts run");
        none.Click += (_, _) => Requested?.Invoke("excludeAll", null);
        actions.Children.Add(all); actions.Children.Add(none);
        section.Body.Children.Add(actions);
        if (value.IncludeResult != null) section.Body.Children.Add(Help(value.IncludeResult));
        if (value.Groups.Count == 0) section.Body.Children.Add(Help("The starts call no other animation."));
        foreach (var group in value.Groups.Take(200))
        {
            DockPanel row = new() { MinHeight = 30, Margin = new(0, 1, 0, 1) };
            TextBlock count = Tertiary($"{group.Included} of {group.Total}");
            count.Margin = new(8, 0, 2, 0);
            DockPanel.SetDock(count, Dock.Right); row.Children.Add(count);
            ToggleButton expand = new() { Content = new PreviewIcon { Kind = "Chevron", Width = 11, Height = 11 }, Width = 22, Height = 24, IsChecked = expandedGroups.Contains(group.Key), ToolTip = "Show each call path of " + group.Label };
            expand.SetResourceReference(StyleProperty, "CardSegmentToggle"); expand.Width = 22; expand.Height = 24;
            ((PreviewIcon)expand.Content).RenderTransformOrigin = new(.5, .5);
            ((PreviewIcon)expand.Content).RenderTransform = expand.IsChecked == true ? Transform.Identity : new RotateTransform(-90);
            AutomationProperties.SetName(expand, "Show the call paths of " + group.Label);
            row.Children.Add(expand);
            CheckBox check = new() { IsThreeState = false, IsChecked = group.Included == group.Total ? true : group.Included == 0 ? false : null, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Margin = new(2, 0, 0, 0) };
            TextBlock label = new() { Text = group.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
            check.Content = label;
            // The label trims: its tooltip starts with the whole of it.
            check.ToolTip = $"{group.Label} ({group.Included} of {group.Total} included)\n" + (group.BackLink ? "Calls back into their own chain (a cycle): the game refuses a call to an animation that is running or invalidated; including them shows the refusal."
                : "Include or exclude these call paths. Excluding a path also excludes every path below it; including one includes the calls that lead to it.");
            AutomationProperties.SetName(check, "Include " + group.Label);
            check.Click += (_, _) => Requested?.Invoke("include", (group.Paths.Select(p => p.Path).ToArray(), group.Included != group.Total));
            row.Children.Add(check);
            section.Body.Children.Add(row);
            StackPanel paths = new() { Margin = new(24, 0, 0, 4), Visibility = expand.IsChecked == true ? Visibility.Visible : Visibility.Collapsed };
            foreach (var path in group.Paths.Take(100))
            {
                CheckBox pathCheck = new() { IsChecked = path.Included, Margin = new(0, 1, 0, 1), MinWidth = 0, Content = new TextBlock { Text = path.Label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }, ToolTip = path.Label + "\n" + path.Path };
                AutomationProperties.SetName(pathCheck, "Include " + path.Label);
                pathCheck.Click += (_, _) => Requested?.Invoke("include", (new[] { path.Path }, !path.Included));
                paths.Children.Add(pathCheck);
            }
            if (group.Paths.Count > 100) paths.Children.Add(Help($"and {group.Paths.Count - 100:N0} more (use the group's box)"));
            expand.Click += (_, _) =>
            {
                if (expand.IsChecked == true) expandedGroups.Add(group.Key); else expandedGroups.Remove(group.Key);
                paths.Visibility = expand.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                ((PreviewIcon)expand.Content).RenderTransform = expand.IsChecked == true ? Transform.Identity : new RotateTransform(-90);
            };
            section.Body.Children.Add(paths);
        }
        if (value.Groups.Count > 200) section.Body.Children.Add(Help($"and {value.Groups.Count - 200:N0} more groups"));
        string note = value.Compiled != null ? "The compiled preview runs every call, back-links too (a call back to a running or invalidated animation is refused, as in the game); switch one off here or on its port in Structure. Include all brings every call back."
            : "Include all leaves back-links excluded: a call back to a running or invalidated animation is refused.";
        if (value.Unresolved > 0) note += $" {value.Unresolved:N0} call{(value.Unresolved == 1 ? "" : "s")} name a missing animation.";
        if (value.Truncated) note += $" The list stops at {AnimationExecutionPolicy.MaximumPaths:N0} paths.";
        section.Body.Children.Add(Help(note));
    }

    // ---------------------------------------------------------------- Conditions and inputs

    private void BuildConditions(AnimationScenarioView value)
    {
        var scenario = value.Scenario;
        var section = Section("Conditions", "");
        section.Body.Children.Add(Caption("Player"));
        playerNow = Help("");
        playerNow.Margin = new(24, 0, 0, 2);
        AddRadio(section.Body, "player", "At start", "The first AIV spawn; callbacks 10 and 17 move it, 11 freezes it, as in the game.", scenario.Player.Kind == AnimationPlayerReferenceKind.AtStart, AnimationPlayerReferenceKind.AtStart);
        section.Body.Children.Add(playerNow);
        string custom = scenario.CustomPlayer is { } point ? string.Create(CultureInfo.InvariantCulture, $"Point {point.X:0.0}, {point.Y:0.0}, {point.Z:0.0}") : "A point you pick in the view.";
        AddRadio(section.Body, "player", "Custom", custom, scenario.Player.Kind == AnimationPlayerReferenceKind.Custom, AnimationPlayerReferenceKind.Custom);
        Button pick = TextButton("Pick in view", "Click a surface in the 3D view to place the player (Esc cancels)", "Pin");
        pick.Margin = new(24, 2, 0, 6); pick.HorizontalAlignment = HorizontalAlignment.Left;
        pick.Click += (_, _) => Requested?.Invoke("pick", "player");
        section.Body.Children.Add(pick);
        AddRadio(section.Body, "player", "No player", "Range conditions are false; range-gated animations never run.", scenario.Player.Kind == AnimationPlayerReferenceKind.None, AnimationPlayerReferenceKind.None);

        section.Body.Children.Add(Caption("Random seed", top: 10));
        DockPanel seedRow = new();
        var seed = new ValueTextBox { Width = 90, Height = 30, MinHeight = 30, Text = scenario.Seed.ToString(CultureInfo.InvariantCulture), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(seed, "Random seed");
        seed.ToolTip = "The preview's seeded random table, so a run repeats";
        string seedApplied = seed.Text;
        void CommitSeed()
        {
            if (seed.Text == seedApplied) return;
            if (int.TryParse(seed.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) { seedApplied = seed.Text; Requested?.Invoke("seed", number); }
            else seed.Text = seedApplied;
        }
        seed.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitSeed(); e.Handled = true; } else if (e.Key == Key.Escape) { seed.Text = seedApplied; e.Handled = true; } };
        seed.LostKeyboardFocus += (_, _) => CommitSeed();
        pendingCommits.Add(CommitSeed);
        DockPanel.SetDock(seed, Dock.Left); seedRow.Children.Add(seed);
        var seedHelp = Help("Preview only: random in the game (p ≈ w)"); seedHelp.Margin = new(10, 0, 0, 0); seedHelp.VerticalAlignment = VerticalAlignment.Center;
        seedRow.Children.Add(seedHelp);
        section.Body.Children.Add(seedRow);

        section.Body.Children.Add(Caption("Effects detail", top: 10));
        section.Body.Children.Add(Segmented("Effects detail", [("High", "High"), ("Medium", "Medium"), ("Low", "Low")], scenario.EffectsDetail.ToString(), v => Requested?.Invoke("effectsDetail", Enum.Parse<AnimationEffectsDetail>(v))));
        section.Body.Children.Add(Help("The game option ANIMATION_LOD tests: High with Direct3D, Medium with the software renderer."));

        section.Body.Children.Add(Caption($"Overrides ({value.Overrides.Count})", top: 10));
        if (value.Overrides.Count == 0) section.Body.Children.Add(Help("None. A condition's Game · True · False switch forces it for one call path."));
        foreach (var item in value.Overrides.Take(100))
        {
            DockPanel row = new() { MinHeight = 28 };
            Button remove = SmallIcon("Close", "Remove the override (back to Game)", "Remove the override " + item.Label);
            remove.Click += (_, _) => Requested?.Invoke("removeOverride", item.Override);
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            Button go = TextButton("Go to", "Show this condition in the Inspector");
            go.Height = 24; go.Padding = new(8, 0, 8, 0); go.Margin = new(4, 0, 2, 0);
            go.Click += (_, _) => Requested?.Invoke("goTo", item.Override);
            DockPanel.SetDock(go, Dock.Right); row.Children.Add(go);
            row.Children.Add(new TextBlock { Text = item.Label + " = " + (item.Override.Value ? "True" : "False"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, ToolTip = item.Label + (item.Override.InstancePath.Length > 0 ? "\nCall path " + item.Override.InstancePath : "\nThe opened binding") });
            section.Body.Children.Add(row);
        }
        if (value.Overrides.Count > 100) section.Body.Children.Add(Help($"and {value.Overrides.Count - 100:N0} more"));
    }

    // ---------------------------------------------------------------- Rules

    private void BuildRules(AnimationScenarioView value)
    {
        var scenario = value.Scenario;
        var section = Section("Rules", "");
        prerequisitesCount = RuleCaption(section, "Prerequisites");
        section.Body.Children.Add(Segmented("Prerequisites", [("check", "Check"), ("ignoreStarts", "Ignore for starts"), ("ignore", "Ignore")], RuleName(scenario.Prerequisites), v => Requested?.Invoke("prerequisites", v),
            ["Check: as the game, every start and call is checked; unmet means refused", "Ignore for scenario starts: the starts you schedule run even with unmet prerequisites; everything the run starts is checked as in the game", "Ignore: nothing is checked"]));
        rangeCount = RuleCaption(section, "Range gates");
        section.Body.Children.Add(Segmented("Range gates", [("game", "As the game"), ("ignore", "Ignore")], scenario.RangeGates == AnimationRangeGateRule.Game ? "game" : "ignore", v => Requested?.Invoke("rangeGates", v),
            ["As the game: a range-gated animation runs only while the player is in range of its attach node; its clocks freeze meanwhile", "Ignore: range gates never pause"]));
        zoneCount = RuleCaption(section, "Zone gates");
        section.Body.Children.Add(Segmented("Zone gates", [("view", "Use the view's zones"), ("ignore", "Ignore")], scenario.ZoneGates == AnimationZoneGateRule.View ? "view" : "ignore", v => Requested?.Invoke("zoneGates", v),
            ["Use the view's zones: as the game, with the 3D camera standing in for the game camera", "Ignore: the preview camera is anywhere (default)"]));
        section.Body.Children.Add(Help("The game checks prerequisites on every start and pauses range- and zone-gated animations."));
    }
    internal static string RuleName(AnimationPrerequisiteRule rule) => rule switch
    {
        AnimationPrerequisiteRule.Check => "check", AnimationPrerequisiteRule.Ignore => "ignore", _ => "ignoreStarts",
    };

    // ---------------------------------------------------------------- World

    private void BuildWorld(AnimationScenarioView value)
    {
        var scenario = value.Scenario;
        var section = Section("World", "");
        section.Body.Children.Add(Caption("Phase"));
        section.Body.Children.Add(Segmented("Phase", [("runtime", "Runtime"), ("cleanup", "Cleanup")], scenario.Phase == AnimationScenarioPhase.Cleanup ? "cleanup" : "runtime", v => Requested?.Invoke("phase", v),
            ["Runtime: the opened binding's sequences", "Cleanup: its RESET_STATE alone"]));
        section.Body.Children.Add(Caption("Height", top: 10));
        DockPanel heightRow = new();
        var height = new ValueTextBox { Width = 90, Height = 30, MinHeight = 30, Text = scenario.Height.ToString("G9", CultureInfo.InvariantCulture), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(height, "Height");
        string heightApplied = height.Text;
        void CommitHeight()
        {
            if (height.Text == heightApplied) return;
            if (float.TryParse(height.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) && float.IsFinite(number) && number is >= -999 and <= 999) { heightApplied = height.Text; Requested?.Invoke("height", number); }
            else height.Text = heightApplied;
        }
        height.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitHeight(); e.Handled = true; } else if (e.Key == Key.Escape) { height.Text = heightApplied; e.Handled = true; } };
        height.LostKeyboardFocus += (_, _) => CommitHeight();
        pendingCommits.Add(CommitHeight);
        DockPanel.SetDock(height, Dock.Left); heightRow.Children.Add(height);
        var heightHelp = Help("Preview only: lowers or raises the animation; −999 to 999."); heightHelp.Margin = new(10, 0, 0, 0); heightHelp.VerticalAlignment = VerticalAlignment.Center;
        heightRow.Children.Add(heightHelp);
        section.Body.Children.Add(heightRow);
        section.Body.Children.Add(Caption("Beam origin", top: 10));
        DockPanel beam = new();
        Button pickBeam = TextButton("Pick in view", "Click a surface in the 3D view for beams that start at the activation's input position (Esc cancels)", "Pin");
        pickBeam.Click += (_, _) => Requested?.Invoke("pick", "beamOrigin");
        DockPanel.SetDock(pickBeam, Dock.Right); beam.Children.Add(pickBeam);
        if (scenario.BeamOrigin != null)
        {
            Button clear = SmallIcon("Close", "Back to the root (default)", "Clear the beam origin");
            clear.Click += (_, _) => Requested?.Invoke("clearBeamOrigin", null);
            DockPanel.SetDock(clear, Dock.Right); beam.Children.Add(clear);
        }
        beam.Children.Add(new TextBlock { Text = scenario.BeamOrigin is { } origin ? string.Create(CultureInfo.InvariantCulture, $"{origin.X:0.0}, {origin.Y:0.0}, {origin.Z:0.0}") : "Root (default)", VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 });
        section.Body.Children.Add(beam);
        if (value.Compiled is { } compiled) BuildCompiledWorld(section, compiled);
    }

    /// <summary>
    /// A compiled preview's world (design §19): the mission scene it binds in (Choose GameZ…, automatic by default), the bound
    /// root (Bind root… and Clear override) and the playback range (Auto, or a custom end).
    /// </summary>
    private void BuildCompiledWorld(CardSection section, AnimationScenarioCompiled compiled)
    {
        section.Body.Children.Add(Caption("World", top: 10));
        DockPanel world = new();
        Button choose = TextButton("Choose GameZ…", "Choose the mission scene (gamez.zbd) this animation binds in");
        DockPanel.SetDock(choose, Dock.Right); world.Children.Add(choose);
        choose.Click += (_, _) => Requested?.Invoke("world", null);
        if (compiled.WorldChosen)
        {
            Button automatic = SmallIcon("Close", "Back to the automatic scene (the file's own folder)", "Use the automatic mission scene");
            automatic.Click += (_, _) => Requested?.Invoke("world", "");
            DockPanel.SetDock(automatic, Dock.Right); world.Children.Add(automatic);
        }
        world.Children.Add(new TextBlock { Text = compiled.World + (compiled.WorldChosen ? "" : " (auto)"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = compiled.World });
        section.Body.Children.Add(world);

        section.Body.Children.Add(Caption("Bound root", top: 10));
        TextBlock root = new() { Text = compiled.Root + (compiled.RootOverridden ? " · override" : ""), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 4) };
        section.Body.Children.Add(root);
        WrapPanel rootActions = new();
        Button bind = TextButton("Bind root…", "Bind this animation's root to another node of the scene (a preview override)");
        bind.Click += (_, _) => Requested?.Invoke("bindRoot", null);
        Button clear = TextButton("Clear override", "Bind the root as the game does again (the first node with the animation's root name)");
        clear.IsEnabled = compiled.RootOverridden;
        clear.Click += (_, _) => Requested?.Invoke("clearRoot", null);
        rootActions.Children.Add(bind); rootActions.Children.Add(clear);
        section.Body.Children.Add(rootActions);

        section.Body.Children.Add(Caption("Range", top: 10));
        section.Body.Children.Add(Segmented("Range", [("auto", "Auto"), ("custom", "Custom")], compiled.CustomRange ? "custom" : "auto",
            v => Requested?.Invoke(v == "auto" ? "autoRange" : "range", v == "auto" ? null : compiled.RangeEnd),
            ["Auto: the measured duration (a looping or open-ended animation previews a bounded range)", "Custom: the range ends where you say"]));
        DockPanel end = new() { Margin = new(0, 6, 0, 0) };
        var endBox = new ValueTextBox { Width = 90, Height = 30, MinHeight = 30, Text = AnimationSeconds.Value(compiled.RangeEnd).Replace(",", ""), VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        AutomationProperties.SetName(endBox, "Range end seconds");
        endBox.ToolTip = "The playback range's end in seconds (it starts at 0 s); Enter applies it as a custom range";
        string endApplied = endBox.Text;
        void CommitEnd()
        {
            if (endBox.Text == endApplied) return;
            if (double.TryParse(endBox.Text.Trim().TrimEnd('s').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds) && seconds > 0 && seconds <= AnimationSeconds.Maximum)
            { endApplied = endBox.Text; Requested?.Invoke("range", seconds); }
            else endBox.Text = endApplied;
        }
        endBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitEnd(); e.Handled = true; } else if (e.Key == Key.Escape) { endBox.Text = endApplied; e.Handled = true; } };
        endBox.LostKeyboardFocus += (_, _) => CommitEnd();
        pendingCommits.Add(CommitEnd);
        DockPanel.SetDock(endBox, Dock.Left); end.Children.Add(endBox);
        var rangeHelp = Help(compiled.Range + " · from 0 s"); rangeHelp.Margin = new(10, 0, 0, 0); rangeHelp.VerticalAlignment = VerticalAlignment.Center;
        end.Children.Add(rangeHelp);
        section.Body.Children.Add(end);
    }

    // ---------------------------------------------------------------- building

    private CardSection Section(string title, string summary)
    {
        CardSection section = new(title) { Summary = summary };
        section.Margin = new(0, 0, 0, 8);
        body.Children.Add(section);
        return section;
    }
    private static TextBlock Caption(string text, double top = 0)
    {
        TextBlock caption = new() { Text = text, FontSize = 12, Margin = new(0, top, 0, 4) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return caption;
    }
    private static TextBlock RuleCaption(CardSection section, string text)
    {
        DockPanel row = new() { Margin = new(0, section.Body.Children.Count == 0 ? 0 : 10, 0, 4) };
        TextBlock count = new() { FontSize = 11.5 };
        count.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        DockPanel.SetDock(count, Dock.Right); row.Children.Add(count);
        TextBlock caption = new() { Text = text, FontSize = 12 };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        row.Children.Add(caption);
        section.Body.Children.Add(row);
        return count;
    }
    private static TextBlock Help(string text)
    {
        TextBlock help = new() { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 4) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return help;
    }
    private static TextBlock Tertiary(string text)
    {
        TextBlock block = new() { Text = text, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return block;
    }
    private void AddRadio(Panel host, string group, string label, string help, bool chosen, AnimationPlayerReferenceKind kind)
    {
        StackPanel content = new();
        content.Children.Add(new TextBlock { Text = label, FontSize = 12.5 });
        RadioButton radio = new() { GroupName = group + GetHashCode().ToString(CultureInfo.InvariantCulture), IsChecked = chosen, Content = content, Margin = new(0, 2, 0, 0), MinWidth = 0 };
        AutomationProperties.SetName(radio, "Player " + label);
        radio.ToolTip = help;
        radio.Checked += (_, _) => { if (!chosen) Requested?.Invoke("player", kind); };
        host.Children.Add(radio);
        if (kind != AnimationPlayerReferenceKind.AtStart) { var note = Help(help); note.Margin = new(24, 0, 0, 2); host.Children.Add(note); }
    }
    /// <summary>A segmented choice: one raised option, the rest plain (Effects detail, the rules, Phase).</summary>
    private static Border Segmented(string name, (string Value, string Label)[] options, string chosen, Action<string> choose, string[]? tips = null)
    {
        Border frame = new() { CornerRadius = new(5), BorderThickness = new(1), Padding = new(2), HorizontalAlignment = HorizontalAlignment.Stretch };
        frame.SetResourceReference(Border.BackgroundProperty, "ControlFillColorTertiaryBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        AnimationSegmentRow grid = new();
        for (int i = 0; i < options.Length; i++)
        {
            var (value, label) = options[i];
            // Every label stays whole: a narrow pane wraps a label of several words (never inside a word), it never trims it.
            ToggleButton option = new() { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.WrapWithOverflow, TextAlignment = TextAlignment.Center }, IsChecked = value == chosen, ToolTip = tips?[i] ?? label, Padding = new(6, 2, 6, 2) };
            option.SetResourceReference(StyleProperty, "SegmentOption");
            option.Height = double.NaN; option.MinHeight = 26; option.VerticalAlignment = VerticalAlignment.Stretch;
            AutomationProperties.SetName(option, name + ": " + label);
            // Through Checked, so a click and UI Automation's Toggle both request it; the chosen option cannot be unchecked.
            option.Checked += (_, _) => { if (value != chosen) choose(value); };
            option.Unchecked += (_, _) => { if (value == chosen) option.IsChecked = true; };
            grid.Children.Add(option);
        }
        frame.Child = grid;
        AutomationProperties.SetName(frame, name);
        return frame;
    }
    private static Button TextButton(string text, string tip, string? icon = null)
    {
        object content = text;
        if (icon != null)
        {
            StackPanel stack = new() { Orientation = Orientation.Horizontal };
            stack.Children.Add(new PreviewIcon { Kind = icon, Width = 13, Height = 13, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = stack;
        }
        Button button = new() { Content = content, Height = 28, Padding = new(10, 0, 10, 0), Margin = new(0, 0, 6, 4), ToolTip = tip, FontSize = 12.5 };
        AutomationProperties.SetName(button, text);
        return button;
    }
    private static Button SmallIcon(string icon, string tip, string name)
    {
        Button button = new() { Content = new PreviewIcon { Kind = icon, Width = 12, Height = 12 }, ToolTip = tip };
        button.SetResourceReference(StyleProperty, "CardIconButton"); button.Width = 26; button.Height = 26;
        AutomationProperties.SetName(button, name);
        return button;
    }
}
