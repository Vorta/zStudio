using System.Windows;
using System.Windows.Controls;

namespace zStudio.Desktop;

internal static class DetailDialog
{
    public static void Show(Window owner, string title, string text)
    {
        TextBox detail = new() { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(12), BorderThickness = new(0) };
        Button close = new() { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(12) };
        DockPanel panel = new(); DockPanel.SetDock(close,Dock.Bottom); panel.Children.Add(close); panel.Children.Add(detail);
        Window dialog = new() { Owner = owner, Title = title, MinWidth = 420, MinHeight = 250, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Content = panel };
        DialogLayout.Constrain(dialog, new(720, 440));
        close.Click += (_,_) => dialog.Close(); dialog.ShowDialog();
    }
}
