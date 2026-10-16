using System.Windows;
using System.Windows.Controls;

namespace zStudio.Desktop;

/// <summary>A small rounded panel over the 3D view (view menu, selection, surface under the cursor, Flycam): see PreviewStyles.</summary>
public sealed class ViewportChip : ContentControl
{
}

/// <summary>Marks a plain command button as switched on (the command bar's Parachute path), drawn like a checked toggle.</summary>
public static class CommandState
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached("IsOn", typeof(bool), typeof(CommandState), new FrameworkPropertyMetadata(false));
    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);
    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);
}
