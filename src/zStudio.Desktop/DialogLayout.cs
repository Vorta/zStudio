using System.Windows;
using System.Windows.Controls;

namespace Recoil.Zbd.Desktop;

/// <summary>Finite owner-monitor dimensions and a decision strip outside all variable dialog content.</summary>
internal static class DialogLayout
{
    internal static void Constrain(Window dialog, Size preferred, Size? available = null)
    {
        Apply();
        // Some callers assign Owner in an object initializer after constructing the dialog.
        dialog.SourceInitialized += (_, _) => Apply();
        void Apply()
        {
            Size work = available ?? MainWindow.DialogWorkArea(dialog.Owner ?? dialog);
            double width = Math.Max(1, work.Width - 32), height = Math.Max(1, work.Height - 32);
            dialog.SizeToContent = SizeToContent.Manual;
            dialog.MinWidth = Math.Min(dialog.MinWidth, width); dialog.MinHeight = Math.Min(dialog.MinHeight, height);
            dialog.MaxWidth = width; dialog.MaxHeight = height;
            dialog.Width = Math.Min(preferred.Width, width); dialog.Height = Math.Min(preferred.Height, height);
        }
    }

    internal static Grid WithActions(UIElement body, UIElement actions, double margin = 16)
    {
        Grid layout = new() { Margin = new(margin) };
        layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetRow(actions, 1); layout.Children.Add(actions);
        return layout;
    }
}
