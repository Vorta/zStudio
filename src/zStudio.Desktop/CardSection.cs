using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>
/// A collapsible group of the selected-object card: a rounded panel whose header (chevron, title, optional summary and an
/// optional trailing control such as the gizmo mode) shows or hides its body. Whether a title is expanded is remembered
/// for the session, so rebinding the card to another object keeps the user's choice.
/// </summary>
internal sealed class CardSection : Border
{
    private static readonly Dictionary<string, bool> Expanded = new(StringComparer.Ordinal);
    private readonly ToggleButton header;
    private readonly TextBlock summary = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
    private readonly PreviewIcon chevron = new() { Kind = "Chevron", Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border trailing = new() { VerticalAlignment = VerticalAlignment.Center };
    internal StackPanel Body { get; } = new() { Margin = new(12, 4, 12, 12) };
    internal string Title { get; }

    internal CardSection(string title, bool expanded = true)
    {
        Title = title;
        CornerRadius = new(6); BorderThickness = new(1); Margin = new(0, 0, 0, 8);
        SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        summary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        DockPanel content = new();
        content.Children.Add(chevron);
        content.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(summary, Dock.Right); content.Children.Insert(0, summary);
        header = new() { Content = content, IsChecked = Expanded.GetValueOrDefault(title, expanded) };
        header.SetResourceReference(StyleProperty, "CardSectionHeader");
        AutomationProperties.SetName(header, title);
        header.Checked += (_, _) => Apply(); header.Unchecked += (_, _) => Apply();
        DockPanel top = new() { Margin = new(4, 4, 6, 4), MinHeight = 32 };
        DockPanel.SetDock(trailing, Dock.Right); top.Children.Add(trailing); top.Children.Add(header);
        StackPanel layout = new(); layout.Children.Add(top); layout.Children.Add(Body);
        Child = layout;
        Apply();
    }
    /// <summary>A short note at the header's right, such as how many flags are on.</summary>
    internal string Summary { set { summary.Text = value; summary.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible; } }
    /// <summary>A control at the header's right edge (detached from any earlier parent by the caller).</summary>
    internal UIElement? Trailing { get => trailing.Child; set => trailing.Child = value; }
    private void Apply()
    {
        bool open = header.IsChecked == true;
        Expanded[Title] = open;
        Body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        chevron.RenderTransformOrigin = new(.5, .5);
        chevron.RenderTransform = open ? Transform.Identity : new RotateTransform(-90);
    }
}
