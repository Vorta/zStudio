using System.Windows;
using System.Windows.Controls;

namespace Recoil.Zbd.Desktop;

/// <summary>Chooses which mesh nodes of a glTF file become terrain surfaces.</summary>
internal sealed class TerrainCreateDialog : Window
{
    private readonly List<CheckBox> boxes = [];
    public IReadOnlyList<string> Chosen => boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToArray();

    public TerrainCreateDialog(string file, IReadOnlyList<string> nodes)
    {
        Title = "Create terrain"; Width = 460; SizeToContent = SizeToContent.Height; MaxHeight = 640; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        StackPanel panel = new() { Margin = new(16) };
        panel.Children.Add(new TextBlock { Text = $"Surfaces of {file}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "The chosen meshes become terrain: a recipe is created beside the file and a marker in the mission database places its pieces. Keep floors, ceilings, walls, water and seafloor as separate meshes.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new(0, 6, 0, 10)
        });
        StackPanel list = new();
        foreach (string node in nodes)
        {
            // Names are literal text, not access-key labels.
            CheckBox box = new() { Content = new TextBlock { Text = node }, Tag = node, IsChecked = nodes.Count == 1, Margin = new(0, 2, 0, 2) };
            boxes.Add(box); list.Children.Add(box);
        }
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Button ok = new() { Content = "Create", IsDefault = true, MinWidth = 88, Margin = new(0, 0, 8, 0) };
        ok.Click += (_, _) => { if (Chosen.Count == 0) { MessageBox.Show(this, "Choose at least one surface.", Title); return; } DialogResult = true; };
        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
