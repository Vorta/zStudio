using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using zStudio.Core;

namespace zStudio.Desktop;

/// <summary>
/// The player tank's Master type and Weapons sections of its Properties card (docs/agents/workbench.md): preview-only radios that
/// choose how the view draws the tank. One panel lives for the window; each rebuilt card hosts it again, so the radios keep
/// their keyboard focus across the rebuild a choice starts. Each group is one tab stop entered at its checked radio; arrows
/// move and select (Left/Right within a row, Up/Down within a column, Home/End), skipping disabled radios.
/// </summary>
internal sealed class PlayerTankPanel : StackPanel
{
    /// <param name="State">The choice the radios show (a newer request while posing).</param>
    /// <param name="Posing">Which parts of <paramref name="State"/> the view does not show yet: type, weapons.</param>
    internal sealed record View(MissionPlayerTank Tank, PlayerTankState State, string Mission, bool PosingType, bool PosingWeapons, bool Rebuilding,
        string? Failure, string? Substitution);

    private readonly CardSection typeSection = new("Master type"), weaponsSection = new("Weapons");
    private readonly RadioGrid types, primaries, secondaries;
    private readonly TextBlock note = Line(), hiddenNote = Line(), substitution = Line();
    private readonly Grid noteRow, hiddenRow, substitutionRow;
    private readonly TextBlock failure = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border readout = new GroupBorder { Margin = new(0, 8, 0, 0), Padding = new(10, 8, 10, 8), CornerRadius = new(4), BorderThickness = new(1) };
    private readonly Image render = new() { Width = 48, Height = 30, Stretch = Stretch.Uniform, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock readName = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock readModel = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    private View? view;
    private bool narrow;
    private RadioButton? lastFocused;
    private bool restoreFocus;
    /// <summary>Each header's progress ring, made once: its animation runs only while the ring shows.</summary>
    private readonly FrameworkElement typeSpinner = Spinner(), weaponsSpinner = Spinner();
    /// <summary>The user chose a radio: the whole new state.</summary>
    internal event Action<PlayerTankState>? Chosen;

    internal PlayerTankPanel()
    {
        types = new("Master type, preview only", PlayerTankPresentation.Types.Select(t => (PlayerTankPresentation.Id(t), PlayerTankPresentation.Label(t))));
        primaries = new("Primary weapon, preview only", PlayerTankWeapon.Primaries.Select(w => (w.Id, PlayerTankPresentation.ShortName(w.Id))));
        secondaries = new("Secondary weapon, preview only", PlayerTankWeapon.Secondaries.Select(w => (w.Id, PlayerTankPresentation.ShortName(w.Id))));
        types.Picked += id => Choose(view?.State is { } state ? state with { Type = PlayerTankPresentation.Parse(id)!.Value } : null);
        primaries.Picked += id => Choose(view?.State is { } state ? state with { Primary = id } : null);
        secondaries.Picked += id => Choose(view?.State is { } state ? state with { Secondary = id } : null);
        foreach (var grid in new[] { types, primaries, secondaries }) grid.Focused += radio => lastFocused = radio;
        noteRow = NoteRow(note, "Info"); hiddenRow = NoteRow(hiddenNote, "Info"); substitutionRow = NoteRow(substitution, "Info");
        typeSection.Trailing = PreviewTag(); weaponsSection.Trailing = PreviewTag();
        typeSection.Body.Children.Add(types); typeSection.Body.Children.Add(noteRow); typeSection.Body.Children.Add(hiddenRow); typeSection.Body.Children.Add(failure);
        weaponsSection.Body.Children.Add(Heading("Primary", 0)); weaponsSection.Body.Children.Add(primaries);
        weaponsSection.Body.Children.Add(Heading("Secondary", 8)); weaponsSection.Body.Children.Add(secondaries);
        weaponsSection.Body.Children.Add(substitutionRow);
        DockPanel well = new(); DockPanel.SetDock(render, Dock.Left); well.Children.Add(render);
        StackPanel names = new() { VerticalAlignment = VerticalAlignment.Center }; names.Children.Add(readName); names.Children.Add(readModel); well.Children.Add(names);
        readout.Child = well;
        readModel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        // Theme brushes: an inset well (the design's .src block) and the critical text colour (#FF99A4 dark, #C42B1C light).
        readout.SetResourceReference(Border.BackgroundProperty, "ControlAltFillColorSecondaryBrush");
        readout.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        failure.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        RenderOptions.SetBitmapScalingMode(render, BitmapScalingMode.HighQuality);
        AutomationProperties.SetLiveSetting(readModel, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(readout, "Secondary weapon readout");
        weaponsSection.Body.Children.Add(readout);
        Children.Add(typeSection); Children.Add(weaponsSection);
        // Below 300 DIP of pane width each grid has one column (the docked pane; elsewhere the panel's own width plus the
        // card's margins, which the pane adds around it).
        SizeChanged += (_, e) =>
        {
            bool wasNarrow = narrow; narrow = (PaneWidth() ?? e.NewSize.Width + 48) < 300;
            foreach (var grid in new[] { types, primaries, secondaries }) grid.Columns = narrow ? 1 : 2;
            // A narrow header keeps its title: the weapons' summary (the radios show them) gives way.
            if (narrow != wasNarrow && view is { } shown) Summary(weaponsSection, weaponsSpinner, shown.PosingWeapons, narrow ? "" : PlayerTankPresentation.WeaponsSummary(shown.State));
        };
        Loaded += (_, _) =>
        {
            if (!restoreFocus) return;
            restoreFocus = false;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { if (lastFocused is { IsVisible: true, IsEnabled: true } radio) radio.Focus(); });
        };
    }

    /// <summary>Before a card hosts the panel: whether its radios had the keyboard, to give it back once hosted again.</summary>
    internal void PrepareMove() { if (IsKeyboardFocusWithin) restoreFocus = true; }
    /// <summary>The next <see cref="Show"/> draws again even with the same inputs (the radios return to them after a refused click).</summary>
    internal void Invalidate() => view = null;
    /// <summary>The docked Properties pane's width, or null outside it.</summary>
    private double? PaneWidth()
    {
        for (DependencyObject? at = this; at != null; at = VisualTreeHelper.GetParent(at))
            if (at is FrameworkElement { Name: "SourcePropertiesPane" } pane) return pane.ActualWidth;
        return null;
    }

    private void Choose(PlayerTankState? next) { if (next != null && view != null && next != view.State) Chosen?.Invoke(next); }

    internal void Show(View shown)
    {
        // The card refreshes on every hover; only a changed state redraws the sections.
        if (shown == view) return;
        view = shown;
        var tank = shown.Tank; var state = shown.State;
        bool sub = state.Type == PlayerTankType.Sub;
        types.Set(PlayerTankPresentation.Id(state.Type), id =>
        {
            var type = PlayerTankPresentation.Parse(id)!.Value;
            if (shown.Rebuilding) return (false, PlayerTankPresentation.RebuildingToolTip);
            return PlayerTankPresentation.DisabledToolTip(shown.Mission, tank, type) is { } reason ? (false, reason) : (true, PlayerTankPresentation.TypeToolTip(type, tank));
        });
        primaries.Set(state.Primary, id => shown.Rebuilding ? (false, PlayerTankPresentation.RebuildingToolTip)
            : (true, PlayerTankPresentation.WeaponToolTip(tank, PlayerTankWeapon.Find(id)!)));
        secondaries.Set(state.Secondary, id => shown.Rebuilding ? (false, PlayerTankPresentation.RebuildingToolTip)
            : sub && !tank.Model(id).AllowedInSub ? (false, PlayerTankPresentation.SubForbidden)
            : (true, PlayerTankPresentation.WeaponToolTip(tank, PlayerTankWeapon.Find(id)!)));
        Summary(typeSection, typeSpinner, shown.PosingType, PlayerTankPresentation.Label(state.Type));
        Summary(weaponsSection, weaponsSpinner, shown.PosingWeapons, narrow ? "" : PlayerTankPresentation.WeaponsSummary(state));
        note.Text = PlayerTankPresentation.Note(shown.Mission, tank);
        hiddenNote.Text = PlayerTankPresentation.HiddenNote;
        hiddenRow.Visibility = tank.HiddenAtStart ? Visibility.Visible : Visibility.Collapsed;
        failure.Text = shown.Failure ?? "";
        failure.Visibility = shown.Failure == null ? Visibility.Collapsed : Visibility.Visible;
        substitution.Text = shown.Substitution ?? "";
        substitutionRow.Visibility = shown.Substitution == null ? Visibility.Collapsed : Visibility.Visible;
        // The readout: the chosen secondary's render, manual name and the gun model it draws (shared or missing).
        string id = state.Secondary;
        render.Source = PlayerTankPresentation.Render(id);
        readName.Text = PlayerTankPresentation.ManualName(id);
        string before = readModel.Text;
        ModelInlines(tank, id);
        if (before.Length > 0 && before != readModel.Text && UIElementAutomationPeer.FromElement(readModel) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ModelInlines(MissionPlayerTank tank, string id)
    {
        readModel.Inlines.Clear();
        var model = tank.Model(id);
        Run Mono(string text) { Run run = new(text) { FontFamily = new("Cascadia Mono, Consolas") }; return run; }
        if (!model.Listed || model.Model == null) { readModel.Inlines.Add(new Run($"This tank has no {PlayerTankPresentation.ShortName(id)}, so no secondary weapon is drawn.")); return; }
        if (!model.Present)
        {
            readModel.Inlines.Add(new Run("This tank's gun has no ")); readModel.Inlines.Add(Mono(model.Model)); readModel.Inlines.Add(new Run(" model, so no secondary weapon is drawn."));
            return;
        }
        readModel.Inlines.Add(new Run("Gun model ")); readModel.Inlines.Add(Mono(model.Model));
        var shared = PlayerTankPresentation.SharedWith(tank, id);
        if (shared.Count > 0) readModel.Inlines.Add(new Run(", shared with " + (shared.Count == 1 ? shared[0] : string.Join(", ", shared.Take(shared.Count - 1)) + " and " + shared[^1])));
    }

    private static void Summary(CardSection section, FrameworkElement spinner, bool posing, string text)
    {
        section.Summary = posing ? "Posing…" : text;
        var turn = (RotateTransform)spinner.RenderTransform;
        bool showing = spinner.Parent != null;
        if (posing && !showing) turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        else if (!posing && showing) turn.BeginAnimation(RotateTransform.AngleProperty, null);
        section.SummaryLead = posing ? spinner : null;
    }
    private static FrameworkElement Spinner()
    {
        System.Windows.Shapes.Ellipse ring = new() { Width = 12, Height = 12, StrokeThickness = 1.5, StrokeDashArray = [5, 3], RenderTransformOrigin = new(.5, .5), RenderTransform = new RotateTransform() };
        ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentFillColorDefaultBrush");
        AutomationProperties.SetName(ring, "Posing");
        return ring;
    }
    private static Border PreviewTag()
    {
        TextBlock text = new() { Text = "Preview", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Border tag = new GroupBorder { Child = text, Height = 20, Padding = new(8, 0, 8, 0), CornerRadius = new(10), Margin = new(8, 0, 0, 0), ToolTip = PlayerTankPresentation.PreviewToolTip, VerticalAlignment = VerticalAlignment.Center };
        tag.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        ToolTipService.SetInitialShowDelay(tag, 0);
        AutomationProperties.SetName(tag, "Preview only");
        AutomationProperties.SetHelpText(tag, PlayerTankPresentation.PreviewToolTip);
        return tag;
    }
    private static TextBlock Heading(string text, double top)
    {
        TextBlock heading = new() { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Height = 20, Margin = new(0, top, 0, 0) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return heading;
    }
    private static TextBlock Line()
    {
        TextBlock line = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, LineHeight = 15 };
        line.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return line;
    }
    private static Grid NoteRow(TextBlock text, string icon)
    {
        Grid row = new() { Margin = new(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new());
        PreviewIcon glyph = new() { Kind = icon, Width = 14, Height = 14, Margin = new(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top };
        glyph.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
        Grid.SetColumn(text, 1); row.Children.Add(glyph); row.Children.Add(text);
        return row;
    }

    /// <summary>A named group for UI Automation: panels and borders have no peer of their own, so their names would never be announced.</summary>
    private sealed class GroupAutomationPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => Owner.GetType().Name;
        protected override bool IsControlElementCore() => true;
    }
    private sealed class GroupBorder : Border { protected override AutomationPeer OnCreateAutomationPeer() => new GroupAutomationPeer(this); }

    /// <summary>A radio group laid out in a grid of one or two columns, one tab stop, arrow keys moving and selecting.</summary>
    private sealed class RadioGrid : Grid
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new GroupAutomationPeer(this);
        private readonly List<RadioButton> radios = [];
        private int columns = 2;
        private bool updating;
        internal event Action<string>? Picked;
        internal event Action<RadioButton>? Focused;
        internal RadioGrid(string name, IEnumerable<(string Id, string Label)> items)
        {
            AutomationProperties.SetName(this, name);
            ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new());
            foreach (var (id, label) in items)
            {
                RadioButton radio = new() { Tag = id, Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, FontSize = 13, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(radio, label);
                ToolTipService.SetShowOnDisabled(radio, true); ToolTipService.SetShowsToolTipOnKeyboardFocus(radio, true);
                radio.Checked += (_, _) => { TabStops(); if (!updating) Picked?.Invoke(id); };
                radio.PreviewKeyDown += Navigate;
                radio.GotKeyboardFocus += (_, _) => Focused?.Invoke(radio);
                radios.Add(radio); Children.Add(radio);
            }
            Arrange();
        }
        internal int Columns { get => columns; set { if (columns == value) return; columns = value; Arrange(); } }
        private void Arrange()
        {
            ColumnDefinitions[1].Width = columns == 2 ? new(1, GridUnitType.Star) : new(0);
            RowDefinitions.Clear();
            for (int row = 0; row < (radios.Count + columns - 1) / columns; row++) RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int i = 0; i < radios.Count; i++)
            {
                SetRow(radios[i], i / columns); SetColumn(radios[i], i % columns);
                radios[i].Margin = columns == 2 && i % 2 == 1 ? new(8, 0, 0, 0) : new(0);
            }
        }
        /// <summary>Checks <paramref name="id"/> without reporting a choice; each radio's availability and tooltip (also its help text).</summary>
        internal void Set(string id, Func<string, (bool Enabled, string ToolTip)> describe)
        {
            updating = true;
            try
            {
                foreach (var radio in radios)
                {
                    var (enabled, tip) = describe((string)radio.Tag);
                    radio.IsEnabled = enabled; radio.ToolTip = tip;
                    AutomationProperties.SetHelpText(radio, tip);
                    bool chosen = (string)radio.Tag == id;
                    if (radio.IsChecked != chosen) radio.IsChecked = chosen;
                }
            }
            finally { updating = false; }
            TabStops();
        }
        // One tab stop: the checked radio, else the first that can take the keyboard.
        private void TabStops()
        {
            var entry = radios.FirstOrDefault(r => r.IsChecked == true && r.IsEnabled) ?? radios.FirstOrDefault(r => r.IsEnabled) ?? radios.FirstOrDefault(r => r.IsChecked == true);
            foreach (var radio in radios) radio.IsTabStop = ReferenceEquals(radio, entry);
        }
        private void Navigate(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End) || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            int index = radios.IndexOf((RadioButton)sender), row = index / columns, column = index % columns;
            int target = e.Key switch
            {
                Key.Home => radios.FindIndex(r => r.IsEnabled),
                Key.End => radios.FindLastIndex(r => r.IsEnabled),
                Key.Left => column > 0 && radios[index - 1].IsEnabled ? index - 1 : -1,
                Key.Right => column + 1 < columns && index + 1 < radios.Count && radios[index + 1].IsEnabled ? index + 1 : -1,
                Key.Up => Enumerable.Range(0, row).Reverse().Select(r => r * columns + column).FirstOrDefault(i => radios[i].IsEnabled, -1),
                _ => Enumerable.Range(row + 1, Math.Max(0, (radios.Count - 1) / columns - row)).Select(r => r * columns + column).Where(i => i < radios.Count).FirstOrDefault(i => radios[i].IsEnabled, -1),
            };
            if (target < 0 || target == index) return;
            // Selection follows focus.
            radios[target].Focus();
            radios[target].IsChecked = true;
        }
    }
}
