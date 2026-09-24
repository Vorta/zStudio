using System.Windows;
using System.Windows.Controls;

namespace Recoil.Zbd.Desktop;

/// <summary>A value field without the Fluent theme's clear-text action.</summary>
public sealed class ValueTextBox : TextBox
{
    public ValueTextBox() => SetResourceReference(StyleProperty, typeof(TextBox));
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // A local value takes precedence over Fluent's focus/hover triggers. Apply
        // it again when the theme replaces the template; retain native editing,
        // scrolling, focus visuals and single-line keyboard behavior.
        if (GetTemplateChild("DeleteButton") is UIElement clearButton)
            clearButton.Visibility = Visibility.Collapsed;
    }
}
