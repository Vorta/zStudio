using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace zStudio.Desktop;

/// <summary>A value field with an optional clear action that stays visible while it contains text.</summary>
public sealed class ValueTextBox : TextBox
{
    private UIElement? clearButton;
    public bool ShowClearWhenNonEmpty { get; init; }

    public ValueTextBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsEnabledChanged += (_, _) =>
        {
            if (ShowClearWhenNonEmpty && Text.Length > 0) CommandManager.InvalidateRequerySuggested();
        };
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // A local value takes precedence over Fluent's focus/hover triggers. Apply
        // it again when the theme replaces the template; retain native editing,
        // scrolling, focus visuals and single-line keyboard behavior.
        clearButton = GetTemplateChild("DeleteButton") as UIElement;
        RefreshClearButton();
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        if (ShowClearWhenNonEmpty) RefreshClearButton();
    }

    private void RefreshClearButton()
    {
        if (clearButton == null) return;
        var visibility = ShowClearWhenNonEmpty && Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var changed = clearButton.Visibility != visibility;
        clearButton.Visibility = visibility;
        // Native Clear can still hold the last CanExecute result when text changes outside keyboard input.
        if (ShowClearWhenNonEmpty && changed) CommandManager.InvalidateRequerySuggested();
    }
}
