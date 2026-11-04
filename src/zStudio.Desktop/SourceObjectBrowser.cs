using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>A project model browser; acceptance prepares a placement, never edits source files.</summary>
internal sealed class SourceObjectBrowser : Window
{
    private readonly ListBox list = new() { MinHeight = 150 };
    private readonly TextBox filter = new(), name = new(), error = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    private readonly Button place = new() { Content = "Place on map", IsDefault = true, MinWidth = 112 };
    private readonly TextBlock selectedModel = new() { Visibility = Visibility.Collapsed, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 0, 8) };
    private readonly CancellationTokenSource stopped = new();
    private IReadOnlyList<SourceModelChoice> models = [];
    private string? selectedPath, suggested;
    private bool working;

    internal SourceObjectBrowser(Window owner, string root, string mission,
        Func<CancellationToken, Task<IReadOnlyList<SourceModelChoice>>> discover,
        Func<string, string?, CancellationToken, Task> prepare, string? parentDescription = null, bool commonOnly = false)
    {
        Owner = owner; Title = $"Add object to {mission}"; Width = 660; Height = 650; MinWidth = 520; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        Grid panel = new() { Margin = new(16) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) panel.RowDefinitions.Add(new() { Height = height });
        void Row(UIElement control, int row) { Grid.SetRow(control, row); panel.Children.Add(control); }
        string folders = commonOnly
            ? "This group is used by several maps. Choose a model in data/common/models/ with external PNG textures in data/common/textures/. Subfolders are allowed."
            : $"Choose a model in data/common/models/ or data/{mission}/models/. Its external PNG textures must be in data/common/textures/ or data/{mission}/textures/. Subfolders are allowed.";
        Row(new TextBlock { Text = (parentDescription == null ? "" : parentDescription + "\n\n") + folders, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) }, 0);
        DockPanel search = new() { Margin = new(0, 0, 0, 8) };
        var browse = new Button { Content = "Browse…", Margin = new(8, 0, 0, 0) };
        var refresh = new Button { Content = "Refresh", Margin = new(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right); DockPanel.SetDock(refresh, Dock.Right);
        search.Children.Add(browse); search.Children.Add(refresh); search.Children.Add(filter); Row(search, 1);
        System.Windows.Automation.AutomationProperties.SetName(filter, "Search project models");
        System.Windows.Automation.AutomationProperties.SetName(list, "Project models");
        list.DisplayMemberPath = nameof(SourceModelChoice.Path);
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        Row(list, 2);
        StackPanel naming = new() { Margin = new(0, 10, 0, 8) };
        naming.Children.Add(selectedModel);
        naming.Children.Add(new TextBlock { Text = "Object name", Margin = new(0, 0, 0, 4) }); naming.Children.Add(name); Row(naming, 3);
        System.Windows.Automation.AutomationProperties.SetName(name, "Object name");
        Row(error, 4);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Button cancel = new() { Content = "Cancel", IsCancel = true, Margin = new(8, 0, 0, 0), MinWidth = 80 };
        buttons.Children.Add(place); buttons.Children.Add(cancel); Row(buttons, 5); Content = panel;
        Closed += (_, _) => stopped.Cancel();
        filter.TextChanged += (_, _) => Fill();
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is SourceModelChoice model) Choose(model.Path); };
        browse.Click += (_, _) =>
        {
            Microsoft.Win32.OpenFileDialog dialog = new() { Title = "Choose a project glTF object", Filter = "glTF models (*.gltf;*.glb)|*.gltf;*.glb", InitialDirectory = Path.Combine(root, "data", commonOnly ? "common" : mission, "models") };
            if (dialog.ShowDialog(this) != true) return;
            Choose(Path.GetRelativePath(root, dialog.FileName).Replace('\\', '/'));
        };
        refresh.Click += async (_, _) => await Load();
        place.Click += async (_, _) =>
        {
            if (selectedPath == null || working) return;
            Busy(true); error.Visibility = Visibility.Visible; error.Text = "Checking assets and preparing placement… Esc cancels.";
            try
            {
                await prepare(selectedPath, name.Text == suggested ? null : name.Text.Trim(), stopped.Token);
                if (!stopped.IsCancellationRequested) DialogResult = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or zStudio.Automation.StudioCommandException)
            { if (!stopped.IsCancellationRequested) error.Text = ex.Message; }
            finally { if (!stopped.IsCancellationRequested) Busy(false); }
        };
        async Task Load()
        {
            Busy(true);
            try { models = await discover(stopped.Token); Fill(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or zStudio.Automation.StudioCommandException) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            finally { if (!stopped.IsCancellationRequested) Busy(false); }
        }
        void Busy(bool value) { working = value; list.IsEnabled = filter.IsEnabled = name.IsEnabled = browse.IsEnabled = refresh.IsEnabled = !value; place.IsEnabled = !value && selectedPath != null; }
        Loaded += async (_, _) => { await Load(); filter.Focus(); };
    }
    private void Choose(string path)
    {
        if (selectedPath == path) return;
        selectedPath = path; suggested = Path.GetFileNameWithoutExtension(path) + "_new";
        selectedModel.Text = "Selected: " + path; selectedModel.ToolTip = path; selectedModel.Visibility = Visibility.Visible;
        error.Text = ""; error.Visibility = Visibility.Collapsed;
        name.Text = suggested; place.IsEnabled = !working;
    }
    private void Fill()
    {
        var selected = selectedPath;
        list.ItemsSource = models.Where(m => m.Path.Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        list.SelectedItem = models.FirstOrDefault(m => m.Path == selected);
    }
}
