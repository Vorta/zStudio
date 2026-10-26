using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace zStudio.Desktop;

internal sealed class SourceExportSelectionDialog : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { Text = "Reading source project…", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
    private readonly Button export = new() { Content = "Export selected…", IsEnabled = false, IsDefault = true, MinWidth = 120, Margin = new(8, 0, 0, 0) };
    internal SourceExportSelection? Selection { get; private set; }
    internal SourceExportSelectionDialog(Window owner, Func<CancellationToken, Task<SourceExportSelection>> load, Action<SourceExportSelection> remember)
    {
        Owner = owner; Title = "Export ZBD files"; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        MinWidth = 380; MinHeight = 300; ResizeMode = ResizeMode.CanResize;
        Grid body = new(); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new()); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.Children.Add(status);
        StackPanel tools = new() { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 10) };
        Button all = new() { Content = "Select all", IsEnabled = false }, none = new() { Content = "Clear all", IsEnabled = false, Margin = new(8, 0, 0, 0) };
        tools.Children.Add(all); tools.Children.Add(none); Grid.SetRow(tools, 1); body.Children.Add(tools);
        ListBox list = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        list.SetResourceReference(StyleProperty, "DefaultListBoxStyle");
        Style itemStyle = new(typeof(ListBoxItem), (Style)FindResource("DefaultListBoxItemStyle"));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(MinHeightProperty, 0d));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.ItemContainerStyle = itemStyle;
        AutomationProperties.SetName(list, "ZBD files to export");
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var check = new FrameworkElementFactory(typeof(SourceExportCheckBox));
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(SourceExportSelectionRow.IsChecked)) { Mode = BindingMode.TwoWay });
        check.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(SourceExportSelectionRow.Path)));
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding(nameof(SourceExportSelectionRow.Path))); label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); check.AppendChild(label);
        Style style = new(typeof(CheckBox), (Style)FindResource("DefaultCheckBoxStyle")); style.Setters.Add(new Setter(MarginProperty, new Thickness(24, 2, 4, 2)));
        DataTrigger heading = new() { Binding = new Binding(nameof(SourceExportSelectionRow.IsGroup)), Value = true };
        heading.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold)); heading.Setters.Add(new Setter(MarginProperty, new Thickness(4, 14, 4, 6))); style.Triggers.Add(heading); check.SetValue(StyleProperty, style);
        list.ItemTemplate = new DataTemplate { VisualTree = check }; Grid.SetRow(list, 2); body.Children.Add(list);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 90 }; actions.Children.Add(cancel); actions.Children.Add(export); Grid.SetRow(actions, 3); body.Children.Add(actions);
        foreach (var button in new[] { all, none, cancel, export }) button.SetResourceReference(StyleProperty, "DefaultButtonStyle");
        body.Margin = new(16); Content = body; DialogLayout.Constrain(this, new(660, 720));
        all.Click += (_, _) => Selection?.SetAll(true); none.Click += (_, _) => Selection?.SetAll(false);
        export.Click += (_, _) => { if (Selection is { SelectedCount: > 0 } model && model.IsCurrent()) DialogResult = true; else status.Text = "The project or build profile changed. Close this window and choose the export again."; };
        Closing += (_, _) =>
        {
            lifetime.Cancel();
            if (Selection is { } model && model.IsCurrent()) remember(model);
        };
        Loaded += async (_, _) =>
        {
            try
            {
                var model = await load(lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                Selection = model; list.ItemsSource = model.Rows; all.IsEnabled = none.IsEnabled = model.Files.Count > 0;
                void Update() { status.Text = model.Files.Count == 0 ? "Nothing to build yet." : $"{model.SelectedCount:N0} of {model.Files.Count:N0} files selected · {model.Profile}"; export.IsEnabled = model.SelectedCount > 0; }
                model.Changed += Update; Update();
            }
            catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) status.Text = "The project changed. Close this window and choose the export again."; }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) status.Text = ex.Message.Length <= 1024 ? ex.Message : ex.Message[..1024] + "…"; }
        };
    }
}

internal sealed class SourceExportCheckBox : CheckBox
{
    protected override void OnToggle() => SetCurrentValue(IsCheckedProperty, IsChecked != true);
}
