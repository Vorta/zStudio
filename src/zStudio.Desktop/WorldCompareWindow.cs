using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using Microsoft.Win32;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// Compares two GameZ worlds, such as a retail world and one rebuilt from a source project: their node trees merged by
/// parent-child structure, each row marked as the same, changed, or only in one world, with both node slots and the
/// fields that differ. One window, owned by the main window; MCP drives the same comparison (zstudio_world_compare).
/// </summary>
internal sealed class WorldCompareWindow : Window
{
    public delegate Task<WorldCompareView> Comparer(string retail, string rebuilt, CancellationToken token);

    /// <summary>The comparison shown, once one has finished.</summary>
    public WorldCompareView? View { get; private set; }
    /// <summary>A comparison finished: its two paths.</summary>
    public event Action<string, string>? Compared;
    private readonly Comparer compare;
    private readonly TextBox retail = new(), rebuilt = new(), filter = new() { MinWidth = 220 };
    private readonly Button browseRetail = new() { Content = "Browse…", MinWidth = 80, Margin = new(8, 0, 0, 0) }, browseRebuilt = new() { Content = "Browse…", MinWidth = 80, Margin = new(8, 0, 0, 0) };
    private readonly Button run = new() { Content = "Compare", IsDefault = true, MinWidth = 96, Padding = new(12, 4, 12, 4) };
    private readonly CheckBox differencesOnly = new() { Content = "Differences only", VerticalAlignment = VerticalAlignment.Center, Margin = new(16, 0, 0, 0) };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) }, status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TreeView tree = new();
    private readonly TextBlock heading = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, path = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new(0, 2, 0, 8) };
    private readonly DataGrid details = new() { Background = System.Windows.Media.Brushes.Transparent, RowBackground = System.Windows.Media.Brushes.Transparent, AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
    private CancellationTokenSource? running;

    public WorldCompareWindow(Window owner, string retailPath, string rebuiltPath, Comparer compare)
    {
        this.compare = compare;
        Owner = owner; Title = "Compare worlds"; Width = 1180; Height = 780; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        retail.Text = retailPath; rebuilt.Text = rebuiltPath;
        AutomationProperties.SetName(retail, "Retail world file"); AutomationProperties.SetName(rebuilt, "Rebuilt world file");
        AutomationProperties.SetName(browseRetail, "Browse for the retail world file"); AutomationProperties.SetName(browseRebuilt, "Browse for the rebuilt world file");
        AutomationProperties.SetName(filter, "Filter nodes by name"); AutomationProperties.SetName(tree, "Merged node tree"); AutomationProperties.SetName(details, "Compared properties");

        Grid files = new() { Margin = new(0, 0, 0, 8) };
        files.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); files.ColumnDefinitions.Add(new()); files.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        files.RowDefinitions.Add(new()); files.RowDefinitions.Add(new() { Height = new(6) }); files.RowDefinitions.Add(new());
        void File(int row, string label, TextBox box, Button browse)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0) };
            Grid.SetRow(text, row); Grid.SetRow(box, row); Grid.SetColumn(box, 1); Grid.SetRow(browse, row); Grid.SetColumn(browse, 2);
            files.Children.Add(text); files.Children.Add(box); files.Children.Add(browse);
        }
        File(0, "Retail world", retail, browseRetail); File(2, "Rebuilt world", rebuilt, browseRebuilt);

        DockPanel actions = new() { LastChildFill = false };
        actions.Children.Add(run); actions.Children.Add(differencesOnly);
        actions.Children.Add(new TextBlock { Text = "Filter", VerticalAlignment = VerticalAlignment.Center, Margin = new(16, 0, 6, 0) }); actions.Children.Add(filter);

        // The tree: a status glyph (· same, ≠ changed, − only retail, + only rebuilt, ⚑ a whole-world lookup of its name finds another node), the
        // name, then class, slots and what differs.
        tree.ItemTemplate = (HierarchicalDataTemplate)XamlReader.Parse("""
            <HierarchicalDataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" ItemsSource="{Binding Visible}">
              <StackPanel Orientation="Horizontal" ToolTip="{Binding ToolTip}">
                <TextBlock Text="{Binding Glyph}" Foreground="{Binding GlyphBrush}" MinWidth="20" FontWeight="SemiBold"/>
                <TextBlock Text="{Binding Name}"/>
                <TextBlock Text="{Binding Detail}" Opacity="0.65" Margin="10,0,0,0"/>
              </StackPanel>
            </HierarchicalDataTemplate>
            """);
        Style item = new(typeof(TreeViewItem), TryFindResource(typeof(TreeViewItem)) as Style);
        item.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(WorldCompareRow.IsExpanded)) { Mode = BindingMode.TwoWay }));
        // Selection flows one way, from the comparison; the tree's own changes come back only through SelectedItemChanged.
        item.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding(nameof(WorldCompareRow.IsSelected)) { Mode = BindingMode.OneWay }));
        tree.Resources.Add(typeof(TreeViewItem), item);
        VirtualizingPanel.SetIsVirtualizing(tree, true); VirtualizingPanel.SetVirtualizationMode(tree, VirtualizationMode.Recycling);

        details.Columns.Add(new DataGridTextColumn { Header = "Property", Binding = new Binding(nameof(WorldCompareDetail.Field)), Width = new(130) });
        details.Columns.Add(new DataGridTextColumn { Header = "Retail", Binding = new Binding(nameof(WorldCompareDetail.Retail)), Width = new(1, DataGridLengthUnitType.Star), ElementStyle = Wrapping() });
        details.Columns.Add(new DataGridTextColumn { Header = "Rebuilt", Binding = new Binding(nameof(WorldCompareDetail.Rebuilt)), Width = new(1, DataGridLengthUnitType.Star), ElementStyle = Wrapping() });
        Style differing = new(typeof(DataGridRow), TryFindResource(typeof(DataGridRow)) as Style);
        DataTrigger differs = new() { Binding = new Binding(nameof(WorldCompareDetail.Differs)), Value = true };
        differs.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        differs.Setters.Add(new Setter(Control.ForegroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x8A, 0x1E))));
        differing.Triggers.Add(differs); details.RowStyle = differing;

        DockPanel side = new() { Margin = new(10, 0, 0, 0) };
        DockPanel.SetDock(heading, Dock.Top); DockPanel.SetDock(path, Dock.Top);
        side.Children.Add(heading); side.Children.Add(path); side.Children.Add(details);
        Grid main = new();
        main.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star), MinWidth = 260 }); main.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); main.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star), MinWidth = 240 });
        GridSplitter splitter = new() { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter, 1); Grid.SetColumn(side, 2);
        main.Children.Add(tree); main.Children.Add(splitter); main.Children.Add(side);

        DockPanel root = new() { Margin = new(14) };
        foreach (var top in new FrameworkElement[] { files, actions, summary, status }) { DockPanel.SetDock(top, Dock.Top); root.Children.Add(top); }
        root.Children.Add(main);
        Content = root;
        ShowSelection();

        browseRetail.Click += (_, _) => Browse(retail, "Choose the retail world (gamez.zbd)");
        browseRebuilt.Click += (_, _) => Browse(rebuilt, "Choose the rebuilt world (gamez.zbd)");
        run.Click += async (_, _) =>
        {
            try { await CompareAsync(retail.Text.Trim(), rebuilt.Text.Trim(), CancellationToken.None); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
        };
        differencesOnly.Checked += (_, _) => ApplyFilter(); differencesOnly.Unchecked += (_, _) => ApplyFilter();
        filter.TextChanged += (_, _) => ApplyFilter();
        // The tree's selection follows the comparison's: the tree moves its own when it refills (a filter, MCP), so only a
        // selection made in the tree (clicked or keyed, the tree having focus) chooses the row.
        tree.SelectedItemChanged += (_, e) => { if (tree.IsKeyboardFocusWithin && e.NewValue is WorldCompareRow row && View != null && row.Owner == View) View.Select(row); };
        Closing += (_, _) => running?.Cancel();
    }

    private static Style Wrapping()
    {
        Style style = new(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        return style;
    }

    private void Browse(TextBox box, string title)
    {
        OpenFileDialog dialog = new() { Title = title, Filter = "GameZ worlds (gamez.zbd)|gamez.zbd|ZBD files (*.zbd)|*.zbd|All files (*.*)|*.*" };
        try { if (Path.GetDirectoryName(box.Text.Trim()) is { Length: > 0 } folder && Directory.Exists(folder)) dialog.InitialDirectory = folder; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
    }

    /// <summary>Compares the two files and shows the result; a newer comparison supersedes one still running.</summary>
    public async Task<WorldCompareView> CompareAsync(string retailPath, string rebuiltPath, CancellationToken token)
    {
        retail.Text = retailPath; rebuilt.Text = rebuiltPath;
        running?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); running = cancellation;
        run.IsEnabled = false; status.Text = "Comparing…";
        try
        {
            var view = await compare(retailPath, rebuiltPath, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            Show(view);
            status.Text = "";
            Compared?.Invoke(retailPath, rebuiltPath);
            return view;
        }
        catch (OperationCanceledException) when (running != cancellation) { throw new OperationCanceledException("A newer comparison replaced this one."); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            if (running == cancellation) status.Text = ex.Message;
            throw;
        }
        finally { if (running == cancellation) { running = null; run.IsEnabled = true; if (status.Text == "Comparing…") status.Text = ""; } }
    }

    private void Show(WorldCompareView view)
    {
        if (View != null) { View.Selection -= ShowSelection; View.FilterChanged -= ShowRoots; }
        View = view;
        view.Selection += ShowSelection; view.FilterChanged += ShowRoots;
        view.SetFilter(differencesOnly.IsChecked == true, filter.Text);
        ShowRoots();
        var c = view.Comparison;
        int elsewhere = c.Bindings.Count(b => !b.Same);
        summary.Text = string.Create(CultureInfo.CurrentCulture,
            $"Retail {view.RetailNodes:N0} nodes{Version(view.RetailVersion)}, rebuilt {view.RebuiltNodes:N0}{Version(view.RebuiltVersion)}. Merged tree: {c.Counts[WorldComparisonStatus.Same]:N0} the same, {c.Counts[WorldComparisonStatus.Changed]:N0} changed, {c.Counts[WorldComparisonStatus.OnlyExpected]:N0} only in retail, {c.Counts[WorldComparisonStatus.OnlyActual]:N0} only in rebuilt. ") +
            (c.Bindings.Count == 0 ? "No name is shared by several nodes." : string.Create(CultureInfo.CurrentCulture, $"Of {c.Bindings.Count:N0} names several nodes share, {elsewhere:N0} find another node in a whole-world lookup of the rebuilt world (highest slot first; ⚑ marks the node). Animations bind their roots and fall back to such lookups, but search their own subtrees first, so not every ⚑ changes behaviour.")) +
            (c.Truncated ? string.Create(CultureInfo.CurrentCulture, $" The merged tree is too large to show whole: it stops at {WorldComparer.MaximumTreeNodes:N0} rows or 256 levels, and rows whose children it leaves out say so; the rows and differences counted here are those shown.") : "") +
            (c.PairingTruncated ? " Not every node below that was matched, so some names' lookups may be marked ⚑ without reason." : "") +
            (c.ApproximatePairing ? " Some copies of repeated names were too many to pair by position and were paired in order, so some of their differences and ⚑ may come from the pairing." : "") +
            (c.UncheckedBindings > 0 ? string.Create(CultureInfo.CurrentCulture, $" {c.UncheckedBindings:N0} of the ⚑ names were not checked for an indistinguishable copy (too many checks), so they may find the same thing.") : "");
        ShowSelection();
    }
    /// <summary>A world's version when it is not the releases' version 15.</summary>
    private static string Version(uint version) => version == 15 ? "" : string.Create(CultureInfo.CurrentCulture, $" (version {version}{(version == 13 ? ", a 1998 demo world" : "")})");
    private void ShowRoots()
    {
        if (View == null) return;
        tree.ItemsSource = View.VisibleRoots;
        ShowSelection();
    }
    private void ApplyFilter() => View?.SetFilter(differencesOnly.IsChecked == true, filter.Text);

    private void ShowSelection()
    {
        if (View?.Selected is not { } row) { heading.Text = View == null ? "Choose two worlds and compare them." : "Select a node to see its properties in both worlds."; path.Text = ""; details.ItemsSource = null; return; }
        heading.Text = $"{row.Name} · {WorldCompareView.Status(row.Source)}" + (row.Source.BindsElsewhere ? " · a whole-world lookup of its name finds another node in the rebuilt world" : "");
        path.Text = WorldCompareView.Short(row.Source.Path, 2048);
        details.ItemsSource = View.Details(row);
    }

    /// <summary>The window's filter, as its controls hold it.</summary>
    internal (bool DifferencesOnly, string Query) Filter => (differencesOnly.IsChecked == true, filter.Text.Trim());
}
