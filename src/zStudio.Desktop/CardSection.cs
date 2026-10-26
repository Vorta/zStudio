using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>
/// A collapsible group of the selected-object card: a rounded panel whose header (chevron, title, optional summary and an
/// optional trailing control such as the gizmo mode) shows or hides its body. Whether a title is expanded is remembered
/// for the session, so rebinding the card to another object keeps the user's choice; a section can remember under another
/// key for some objects (<see cref="RememberAs"/>).
/// </summary>
internal sealed class CardSection : Border
{
    private static readonly Dictionary<string, bool> Expanded = new(StringComparer.Ordinal);
    private readonly ToggleButton header;
    private readonly TextBlock summary = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly PreviewIcon chevron = new() { Kind = "Chevron", Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border trailing = new() { VerticalAlignment = VerticalAlignment.Center };
    /// <summary>An element before the summary text, such as a progress ring.</summary>
    private readonly Border summaryLead = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    internal StackPanel Body { get; } = new() { Margin = new(12, 4, 12, 12) };
    internal string Title { get; }
    internal void Expand() => header.IsChecked = true;
    /// <summary>The key its expansion is remembered under: its title, unless <see cref="RememberAs"/> chose another.</summary>
    private string memory;

    internal CardSection(string title, bool expanded = true)
    {
        Title = title; memory = title;
        CornerRadius = new(6); BorderThickness = new(1); Margin = new(0, 0, 0, 8);
        SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        summary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        DockPanel content = new();
        content.Children.Add(chevron);
        content.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(summary, Dock.Right); content.Children.Insert(0, summary);
        DockPanel.SetDock(summaryLead, Dock.Right); content.Children.Insert(1, summaryLead);
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
    /// <summary>A summary with a colour swatch before its last part ("Standard · ■ Head-on").</summary>
    internal void SummaryWithSwatch(string lead, Brush swatch, string text, string? toolTip = null)
    {
        summary.Inlines.Clear();
        summary.Inlines.Add(new System.Windows.Documents.Run(lead));
        summary.Inlines.Add(new System.Windows.Documents.InlineUIContainer(new Border { Width = 10, Height = 10, CornerRadius = new(2), Background = swatch, Margin = new(0, 0, 5, 0) })
            { BaselineAlignment = BaselineAlignment.Center });
        summary.Inlines.Add(new System.Windows.Documents.Run(text));
        summary.ToolTip = toolTip;
        summary.Visibility = Visibility.Visible;
    }
    /// <summary>An element before the summary text (null for none).</summary>
    internal UIElement? SummaryLead { set { summaryLead.Child = value; summaryLead.Visibility = value == null ? Visibility.Collapsed : Visibility.Visible; } }
    /// <summary>A control at the header's right edge (detached from any earlier parent by the caller).</summary>
    internal UIElement? Trailing { get => trailing.Child; set => trailing.Child = value; }
    /// <summary>
    /// Remembers its expansion under <paramref name="key"/> from now on (expanded by default as given), showing what that key
    /// remembers: the same section can keep one choice for most objects and another for some (the player tank's Details).
    /// </summary>
    internal void RememberAs(string key, bool expandedByDefault)
    {
        if (key == memory) return;
        memory = key;
        bool open = Expanded.GetValueOrDefault(key, expandedByDefault);
        if (header.IsChecked == open) Apply(); else header.IsChecked = open;
    }
    private void Apply()
    {
        bool open = header.IsChecked == true;
        Expanded[memory] = open;
        Body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        chevron.RenderTransformOrigin = new(.5, .5);
        chevron.RenderTransform = open ? Transform.Identity : new RotateTransform(-90);
    }
}
