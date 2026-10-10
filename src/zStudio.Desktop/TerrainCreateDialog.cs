using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Terrain;

namespace Recoil.Zbd.Desktop;

/// <summary>Chooses which mesh nodes of a glTF file become terrain surfaces, and the recipe's project path.</summary>
internal sealed class TerrainCreateDialog : Window
{
    internal const int PageSize = 64;
    private readonly IReadOnlyList<string> nodes;
    private readonly HashSet<string> chosen = new(StringComparer.Ordinal);
    private readonly TextBox filter = new(), recipe = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel list = new();
    private readonly TextBlock count = new() { Margin = new(0, 6, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly Button previous = new() { Content = "Previous", MinWidth = 80 }, next = new() { Content = "Next", MinWidth = 80, Margin = new(6, 0, 0, 0) };
    private int page;
    public IReadOnlyList<string> Chosen => nodes.Where(chosen.Contains).ToArray();
    /// <summary>The recipe's project path: beside the file (name.terrain.json) unless another is given, as source_terrain_create's recipe.</summary>
    public string Recipe => recipe.Text.Trim();

    public TerrainCreateDialog(string file, IReadOnlyList<string> nodes, Size? available = null)
    {
        this.nodes = nodes;
        if (nodes.Count == 1) chosen.Add(nodes[0]);
        Title = "Create terrain"; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        StackPanel panel = new();
        panel.Children.Add(new TextBlock { Text = "Surfaces of", FontWeight = FontWeights.SemiBold });
        TextBox modelPath = new() { Text = file, IsReadOnly = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(modelPath, "Terrain source model path");
        panel.Children.Add(modelPath);
        panel.Children.Add(new TextBlock
        {
            Text = "The chosen meshes become terrain: a recipe is created and a marker in the mission database places its pieces. Keep floors, ceilings, walls, water and seafloor as separate meshes.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new(0, 6, 0, 10)
        });
        // Another name lets a second mission's terrain use the same surfaces file, or a terrain be created again once its
        // marker is gone while the old recipe remains.
        panel.Children.Add(new TextBlock { Text = "Recipe", FontWeight = FontWeights.SemiBold });
        int dot = file.LastIndexOf('.');
        recipe.Text = (dot > 0 ? file[..dot] : file) + TerrainRecipe.Extension;
        System.Windows.Automation.AutomationProperties.SetName(recipe, "Terrain recipe path");
        recipe.TextChanged += (_, _) => error.Text = "";
        panel.Children.Add(recipe);
        panel.Children.Add(new TextBlock { Text = $"A project path ending in {TerrainRecipe.Extension} that does not exist yet.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new(0, 2, 0, 10) });
        System.Windows.Automation.AutomationProperties.SetName(filter, "Filter terrain mesh nodes");
        panel.Children.Add(filter); panel.Children.Add(count);
        panel.Children.Add(list);
        StackPanel pages = new() { Orientation = Orientation.Horizontal, Margin = new(0, 6, 0, 0) };
        pages.Children.Add(previous); pages.Children.Add(next); panel.Children.Add(pages); panel.Children.Add(error);
        filter.TextChanged += (_, _) => { page = 0; Fill(); };
        previous.Click += (_, _) => { page--; Fill(); };
        next.Click += (_, _) => { page++; Fill(); };
        WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Button ok = new() { Content = "Create", IsDefault = true, MinWidth = 88, Margin = new(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            if (Chosen.Count == 0) { MessageBox.Show(this, "Choose at least one surface.", Title); return; }
            // The rest of the path's rules (inside the data folder, not existing) are the shared creation's, as for MCP.
            if (!Recipe.EndsWith(TerrainRecipe.Extension, StringComparison.OrdinalIgnoreCase)) { error.Text = $"A recipe's name ends with {TerrainRecipe.Extension}."; recipe.Focus(); return; }
            DialogResult = true;
        };
        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        Content = DialogLayout.WithActions(panel, buttons);
        DialogLayout.Constrain(this, new(460, 640), available);
        Fill();
    }

    /// <summary>All mesh identities remain selectable; only this page gets controls, and choices survive filtering.</summary>
    private void Fill()
    {
        string query = filter.Text.Trim();
        var matches = nodes.Where(n => n.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        page = Math.Clamp(page, 0, Math.Max(0, (matches.Length - 1) / PageSize));
        list.Children.Clear();
        foreach (string node in matches.Skip(page * PageSize).Take(PageSize))
        {
            CheckBox box = new() { Content = new TextBlock { Text = JsonData.ShownText(node, 128), TextTrimming = TextTrimming.CharacterEllipsis }, Tag = node, IsChecked = chosen.Contains(node), Margin = new(0, 2, 0, 2) };
            box.Checked += (_, _) =>
            {
                if (chosen.Count >= TerrainRecipe.MaximumSurfaces && !chosen.Contains(node))
                { box.IsChecked = false; error.Text = $"Choose at most {TerrainRecipe.MaximumSurfaces} surfaces for one recipe."; return; }
                chosen.Add(node); error.Text = ""; ShowCount();
            };
            box.Unchecked += (_, _) => { chosen.Remove(node); error.Text = ""; ShowCount(); };
            list.Children.Add(box);
        }
        previous.IsEnabled = page > 0; next.IsEnabled = (page + 1) * PageSize < matches.Length;
        ShowCount();
        void ShowCount() => count.Text = $"Showing {Math.Min(page * PageSize + 1, matches.Length)}–{Math.Min((page + 1) * PageSize, matches.Length)} of {matches.Length:N0} matching meshes ({nodes.Count:N0} total). {chosen.Count} of {TerrainRecipe.MaximumSurfaces} surfaces selected.";
    }
}
