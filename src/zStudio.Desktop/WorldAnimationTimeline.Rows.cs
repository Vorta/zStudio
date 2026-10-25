using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>One recycled native row: accessible controls on the left, bounded time-window drawing on the right.</summary>
public sealed class WorldAnimationTimelineRowView : Grid
{
    public static readonly DependencyProperty OwnerProperty = DependencyProperty.Register(nameof(Owner), typeof(WorldAnimationTimeline), typeof(WorldAnimationTimelineRowView), new PropertyMetadata(null, OwnerChanged));
    private readonly ColumnDefinition labels = new();
    private readonly Button expand = SmallButton("▸"), eye = SmallButton("◉"), occurrences = SmallButton("⋯");
    private readonly CheckBox include = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(1, 0, 3, 0), MinWidth = 16 };
    private readonly TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock timing = new() { FontSize = 10, Opacity = .72, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel controls = new() { Orientation = Orientation.Horizontal };
    private readonly WorldAnimationRowPlot plot;
    private WorldAnimationTimelineRow? row;
    private bool changing;
    public WorldAnimationTimeline? Owner { get => (WorldAnimationTimeline?)GetValue(OwnerProperty); set => SetValue(OwnerProperty, value); }

    public WorldAnimationTimelineRowView()
    {
        ClipToBounds = true; Height = 28; ColumnDefinitions.Add(labels); ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var left = new DockPanel(); controls.Children.Add(expand); controls.Children.Add(include); controls.Children.Add(eye); DockPanel.SetDock(controls, Dock.Left); left.Children.Add(controls);
        DockPanel.SetDock(occurrences, Dock.Right); left.Children.Add(occurrences);
        var text = new Grid(); text.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); text.ColumnDefinitions.Add(new() { Width = GridLength.Auto, MaxWidth = 85 });
        text.Children.Add(label); Grid.SetColumn(timing, 1); text.Children.Add(timing); left.Children.Add(text); Children.Add(left);
        plot = new(this); Grid.SetColumn(plot, 1); Children.Add(plot);
        plot.SizeChanged += (_, _) => Owner?.AlignRuler(plot);
        DataContextChanged += (_, _) => { row = DataContext as WorldAnimationTimelineRow; Refresh(); };
        Loaded += (_, _) => { if (Owner != null) { Owner.ViewChanged -= Refresh; Owner.ViewChanged += Refresh; } Refresh(); Owner?.AlignRuler(plot); };
        Unloaded += (_, _) => { if (Owner != null) Owner.ViewChanged -= Refresh; };
        expand.Click += (_, _) => { if (row != null) Owner?.Expand(row, !row.Expanded); };
        include.Click += (_, _) => { if (!changing && row != null) Owner?.Include(row, include.IsChecked == true); };
        eye.Click += (_, _) => { if (row != null) Owner?.ShowGuides(row, row.GuidesShown != true); };
        occurrences.Click += (_, _) => ShowOccurrences(0);
    }
    private static Button SmallButton(string value) => new() { Content = value, Padding = new(1, 0, 1, 0), MinWidth = 20, MinHeight = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new(0) };
    private static void OwnerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (WorldAnimationTimelineRowView)d;
        if (e.OldValue is WorldAnimationTimeline old) old.ViewChanged -= control.Refresh;
        if (control.IsLoaded && e.NewValue is WorldAnimationTimeline next) next.ViewChanged += control.Refresh;
        control.Refresh();
    }
    private void Refresh()
    {
        if (row == null || Owner == null) return;
        changing = true;
        try
        {
            labels.Width = new(Owner.LabelWidth); Height = row.IsGroup ? 32 : 28;
            controls.Margin = new(Math.Min(48, Math.Max(0, row.Depth) * 12), 0, 0, 0);
            expand.Content = row.Expanded ? "▾" : "▸"; expand.Visibility = row.Expandable ? Visibility.Visible : Visibility.Hidden;
            include.Visibility = row.Included.HasValue ? Visibility.Visible : Visibility.Collapsed; include.IsChecked = row.Included; include.IsEnabled = row.InclusionEnabled;
            include.ToolTip = row.InclusionEnabled ? "Include this call only; descendants keep their own settings" : row.InclusionReason;
            ToolTipService.SetShowOnDisabled(include, true);
            eye.Visibility = row.GuidesShown.HasValue ? Visibility.Visible : Visibility.Collapsed; eye.Opacity = row.GuidesShown == true ? 1 : .45; eye.ToolTip = "Show guides; this does not evaluate or include a child";
            occurrences.Visibility = row.Marks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            label.Text = WorldAnimationTimeline.Shown(row.Label); label.FontWeight = row.IsGroup ? FontWeights.SemiBold : FontWeights.Normal;
            label.ToolTip = WorldAnimationTimeline.Shown(row.Label) + (row.Detail.Length > 0 ? "\n" + WorldAnimationTimeline.Shown(row.Detail) : "");
            timing.Text = WorldAnimationTimeline.Shown(row.TimingLabel); timing.ToolTip = row.TimingLabel;
            // Source thresholds describe eligibility, not a position on the global ruler. Give
            // them prose space in the empty canvas and keep the label column for event identity.
            timing.Visibility = Owner.ViewMode == "source" && row.Marks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(this, WorldAnimationTimeline.Shown(row.Label + ". " + row.TimingLabel + ". " + row.Detail));
            AutomationProperties.SetName(expand, (row.Expanded ? "Collapse " : "Expand ") + row.Label);
            AutomationProperties.SetName(include, "Include child call " + row.Label); AutomationProperties.SetName(eye, "Show guides for " + row.Label);
            AutomationProperties.SetName(occurrences, "Occurrences for " + row.Label);
            plot.ToolTip = row.IsWindowClock ? row.CanMoveWindow ? "Flight sequence clock. Drag the start grip to propose a verified window move." : "Flight sequence clock. " + row.WindowMoveReason : row.TimingLabel + "\n" + row.Detail;
            plot.InvalidateVisual();
        }
        finally { changing = false; }
    }
    internal WorldAnimationTimelineRow? Row => row;
    private void ShowOccurrences(int offset, string? rowId = null, string? evidence = null, string? projection = null)
    {
        if (row == null || Owner == null) return;
        var owner = Owner; rowId ??= row.Id; evidence ??= owner.State.EvidenceIdentity; projection ??= owner.ViewMode;
        if (owner.State.EvidenceIdentity != evidence || owner.ViewMode != projection || owner.CurrentRow(rowId) is not { } captured) return;
        var menu = new ContextMenu();
        if (offset > 0) AddPage("Previous 100 occurrences", Math.Max(0, offset - 100));
        foreach (var mark in captured.Marks.Skip(offset).Take(100))
        {
            var item = new MenuItem { Header = WorldAnimationTimeline.Shown($"{mark.Start:0.######} s · {mark.Kind} · {mark.Label}"), ToolTip = mark.Label };
            item.Click += (_, _) => owner.SelectCurrentMark(captured.Id, mark.Id, evidence, projection); menu.Items.Add(item);
        }
        if (offset + 100 < captured.Marks.Count) AddPage("Next 100 occurrences", offset + 100);
        menu.PlacementTarget = occurrences; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
        void AddPage(string title, int next) { var item = new MenuItem { Header = title }; item.Click += (_, _) => Dispatcher.BeginInvoke(() => ShowOccurrences(next, captured.Id, evidence, projection)); menu.Items.Add(item); }
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
