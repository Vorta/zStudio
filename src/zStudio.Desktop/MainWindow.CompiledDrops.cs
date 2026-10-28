using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>
/// A compiled Whole world's VTOL drop windows (design §14, §19): read only, as the chip over the view whose flyout holds the
/// Drop windows section a source map shows in its Inspector (Analyze drop windows, the flight, its callsites, the window list
/// that selects a window's markers, the result). Moves, Apply and history belong to a source project.
/// </summary>
public partial class MainWindow
{
    private ViewportChip? dropChip;
    private Button? dropChipButton;
    private TextBlock? dropChipWord;
    private Popup? dropPopup;
    private Border? dropFlyout;

    private void EnsureDropChip()
    {
        if (dropChip != null) return;
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        content.Children.Add(new PreviewIcon { Kind = "Parachute", Width = 14, Height = 14, Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = "Drop windows", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        dropChipWord = new() { FontSize = 12, Margin = new(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        dropChipWord.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        content.Children.Add(dropChipWord);
        content.Children.Add(new PreviewIcon { Kind = "Chevron", Width = 12, Height = 12, Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = .65 });
        dropChipButton = new() { Content = content, Height = 28, Padding = new(10, 0, 8, 0), ToolTip = "VTOL drop windows of this world: analyze them and show their markers (read only)" };
        dropChipButton.SetResourceReference(StyleProperty, "CommandButton"); dropChipButton.Width = double.NaN; dropChipButton.Height = 28;
        AutomationProperties.SetName(dropChipButton, "Drop windows");
        dropChipButton.Click += (_, _) => { HostParachutePanel(inCard: false); if (dropPopup != null) dropPopup.IsOpen = true; UpdateParachutePanel(); };
        dropChip = new() { Content = dropChipButton, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Padding = new(3), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(dropChip, "Drop windows");
        dropFlyout = new() { CornerRadius = new(8), BorderThickness = new(1), Padding = new(12), Width = 380 };
        dropFlyout.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        dropFlyout.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        AutomationProperties.SetName(dropFlyout, "Drop windows");
        dropPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PlacementTarget = dropChip, VerticalOffset = 4, Child = dropFlyout };
        if (ViewModeChip.Parent is Panel parent) parent.Children.Insert(parent.Children.IndexOf(ViewModeChip) + 1, dropChip);
        ViewModeChip.SizeChanged += (_, _) => PlaceDropChip();
    }
    private void PlaceDropChip()
    {
        if (dropChip == null) return;
        double left = ViewModeChip.Margin.Left + (ViewModeChip.ActualWidth > 0 ? ViewModeChip.ActualWidth : 104) + 8;
        dropChip.Margin = new(left, ViewModeChip.Margin.Top, 10, 10);
    }
    /// <summary>The chip shows over a compiled Whole world: "Drop windows · not analyzed", "· 6 windows", "· analyzing…".</summary>
    private void RefreshDropChip()
    {
        bool shown = CompiledWorldShown && !presentationUiHidden && ViewModeChip.Visibility == Visibility.Visible;
        if (!shown && dropChip == null) return;
        EnsureDropChip();
        dropChip!.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) { if (dropPopup != null) dropPopup.IsOpen = false; return; }
        PlaceDropChip();
        dropChipWord!.Text = "· " + (parachuteLoading != null ? "analyzing…" : parachutes?.Analysis is { } analysis ? AnimationNotice.Plural(analysis.Windows.Count, "1 window", "windows")
            : parachutes != null ? "no flight" : "not analyzed");
    }
    /// <summary>Hosts the Drop windows section in the chip's flyout (the compiled Whole world's only host).</summary>
    private void HostParachuteInChip()
    {
        EnsureDropChip();
        if (ParachutePanel.Parent == dropFlyout) return;
        switch (ParachutePanel.Parent)
        {
            case Panel parent: parent.Children.Remove(ParachutePanel); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl control: control.Content = null; break;
        }
        ParachutePanel.Width = double.NaN; ParachutePanel.Margin = new(0); ParachutePanel.Padding = new(0);
        ParachutePanel.HorizontalAlignment = HorizontalAlignment.Stretch; ParachutePanel.VerticalAlignment = VerticalAlignment.Top;
        ParachutePanel.BorderThickness = new(0); ParachutePanel.Background = Brushes.Transparent;
        dropFlyout!.Child = ParachutePanel;
    }
}
