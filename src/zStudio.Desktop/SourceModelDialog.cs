using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// Chooses a project model to load into a mission's source world: the model, its node name, whether it is placed in the
/// world or left for resources to place by name, and the animation definition files other missions list for it.
/// </summary>
internal sealed class SourceModelDialog : Window
{
    public SourceWorldAddition? Result { get; private set; }
    private readonly IReadOnlyList<SourceModelChoice> models;
    private readonly IReadOnlySet<string> worldNames;
    private readonly string mission;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<SourceDefinitionFile>>> definitions;
    private readonly Func<SourceModelAddition, string?> validate;
    private readonly TextBox filter = new() { Margin = new(0, 0, 0, 6) }, name = new(), x = new(), y = new(), z = new(), heading = new();
    private readonly ListBox list = new() { Height = 220 };
    private readonly RadioButton unplaced = new() { IsChecked = true, Margin = new(0, 2, 0, 2) }, placed = new() { Margin = new(0, 2, 0, 2) };
    private readonly TextBlock nameNote = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new(0, 2, 0, 0) }, animationNote = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }, error = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.IndianRed };
    private readonly StackPanel animations = new();
    private readonly Button add = new() { Content = "Add", IsDefault = true, MinWidth = 80, Margin = new(0, 0, 8, 0), IsEnabled = false };
    private readonly DispatcherTimer lookup = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? lookupCancellation;
    private string? suggestedName;
    /// <summary>The node name the shown animation definitions were looked up for.</summary>
    private string? animationsFor;

    public SourceModelDialog(Window owner, string mission, IReadOnlyList<SourceModelChoice> models, IReadOnlySet<string> worldNames, Vector3 position,
        Func<string, CancellationToken, Task<IReadOnlyList<SourceDefinitionFile>>> definitions, Func<SourceModelAddition, string?> validate)
    {
        this.models = models; this.worldNames = worldNames; this.mission = mission; this.definitions = definitions; this.validate = validate;
        Owner = owner; Title = $"Add a model to {mission}"; Width = 620; SizeToContent = SizeToContent.Height; MinWidth = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResizeWithGrip; ShowInTaskbar = false;
        System.Windows.Automation.AutomationProperties.SetName(filter, "Filter models");
        System.Windows.Automation.AutomationProperties.SetName(list, "Models");
        System.Windows.Automation.AutomationProperties.SetName(name, "Node name");
        foreach (var (box, label) in new[] { (x, "X"), (y, "Y"), (z, "Z"), (heading, "Heading in degrees") }) System.Windows.Automation.AutomationProperties.SetName(box, label);
        x.Text = Coordinate(position.X); y.Text = Coordinate(position.Y); z.Text = Coordinate(position.Z); heading.Text = "0";
        unplaced.Content = new TextBlock { Text = "Not placed: a vehicle or other model that resources such as aiv.zrd place copies of by name", TextWrapping = TextWrapping.Wrap };
        placed.Content = new TextBlock { Text = "Placed in the world at", TextWrapping = TextWrapping.Wrap };

        StackPanel panel = new() { Margin = new(14) };
        panel.Children.Add(new TextBlock { Text = $"Load a model from any folder of the project into the {mission} world. Exports of {mission} then include its geometry, materials and textures.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        panel.Children.Add(filter);
        panel.Children.Add(list);
        panel.Children.Add(Label("Node name", 10)); panel.Children.Add(name); panel.Children.Add(nameNote);
        panel.Children.Add(Label("Placement", 10)); panel.Children.Add(unplaced); panel.Children.Add(placed);
        WrapPanel coordinates = new() { Margin = new(22, 2, 0, 0) };
        foreach (var (box, label) in new[] { (x, "X"), (y, "Y"), (z, "Z"), (heading, "Heading°") })
        {
            box.Width = 90; box.Margin = new(4, 0, 12, 4);
            coordinates.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            coordinates.Children.Add(box);
        }
        panel.Children.Add(coordinates);
        panel.Children.Add(Label("Animations", 10)); panel.Children.Add(animationNote); panel.Children.Add(animations);
        panel.Children.Add(error);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        buttons.Children.Add(add); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        filter.TextChanged += (_, _) => Fill();
        list.SelectionChanged += (_, _) => ModelChanged();
        name.TextChanged += (_, _) => { NameChanged(); lookup.Stop(); lookup.Start(); };
        lookup.Tick += async (_, _) => { lookup.Stop(); await LookUpAnimationsAsync(); };
        placed.Checked += (_, _) => Placement(); unplaced.Checked += (_, _) => Placement();
        add.Click += async (_, _) => await AcceptAsync();
        Closed += (_, _) => { lookup.Stop(); lookupCancellation?.Cancel(); };
        Fill(); Placement(); NameChanged();
        Loaded += (_, _) => filter.Focus();
    }

    private static TextBlock Label(string text, double top) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new(0, top, 0, 4) };
    private static string Coordinate(float value) => MathF.Round(value, 1).ToString("0.0", CultureInfo.InvariantCulture);

    private void Fill()
    {
        var selected = (list.SelectedItem as ListBoxItem)?.Tag as SourceModelChoice;
        string text = filter.Text.Trim();
        list.Items.Clear();
        foreach (var model in models.Where(m => text.Length == 0 || m.Path.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(2000))
        {
            // Paths are literal text, not access-key labels.
            ListBoxItem item = new() { Tag = model, Content = new TextBlock { Text = $"{model.Name}   ({model.Folder})" }, ToolTip = model.Path };
            System.Windows.Automation.AutomationProperties.SetName(item, model.Path);
            list.Items.Add(item);
            if (model == selected) item.IsSelected = true;
        }
    }
    private SourceModelChoice? Model => (list.SelectedItem as ListBoxItem)?.Tag as SourceModelChoice;
    private void ModelChanged()
    {
        if (Model is not { } model) { add.IsEnabled = false; return; }
        // Follow the model's name until a name is typed.
        if (name.Text.Length == 0 || name.Text == suggestedName) { suggestedName = model.Name; name.Text = model.Name; }
        add.IsEnabled = true;
    }
    private void NameChanged()
    {
        string text = name.Text.Trim();
        nameNote.Text = text.Length == 0 ? "Resources and animations find the model by this name." :
            worldNames.Contains(text) ? $"The {mission} world already has a node named {text}. Resources and animations bind to one of them; a new name keeps them apart." :
            "Resources and animations find the model by this name.";
    }
    private void Placement() { foreach (var box in new[] { x, y, z, heading }) box.IsEnabled = placed.IsChecked == true; }

    private async Task LookUpAnimationsAsync()
    {
        lookupCancellation?.Cancel();
        string root = name.Text.Trim();
        animations.Children.Clear(); animationsFor = null;
        if (root.Length == 0) { animationNote.Text = ""; animationsFor = root; return; }
        using CancellationTokenSource cancellation = new(); lookupCancellation = cancellation;
        animationNote.Text = $"Looking for animation definitions for {root}…";
        try
        {
            var files = await definitions(root, cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsLoaded) return;
            animationsFor = root;
            animationNote.Text = files.Count == 0 ? $"No other mission lists animation definitions for {root}." : $"Other missions list these animation definitions for {root}. Checked files are added to {mission}'s animation list:";
            foreach (var file in files)
                animations.Children.Add(new CheckBox
                {
                    IsChecked = true, Tag = file.Path, Margin = new(0, 2, 0, 2),
                    Content = new TextBlock { Text = $"{file.Path}  —  {string.Join(", ", file.Animations.Take(6).Select(n => n.Length > 64 ? n[..64] + "…" : n))}{(file.Animations.Count > 6 ? ", …" : "")} (used by {string.Join(", ", file.Missions)})", TextWrapping = TextWrapping.Wrap }
                });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or System.IO.IOException or UnauthorizedAccessException) { if (!cancellation.IsCancellationRequested) animationNote.Text = "Animation definitions could not be read: " + ex.Message; }
        finally { if (lookupCancellation == cancellation) lookupCancellation = null; }
    }

    private async Task AcceptAsync()
    {
        error.Text = "";
        if (animationsFor != name.Text.Trim())
        {
            // The listed definitions belong to another name; show this name's before adding.
            lookup.Stop(); add.IsEnabled = false;
            try { await LookUpAnimationsAsync(); } finally { add.IsEnabled = Model != null; }
            if (animationsFor == name.Text.Trim() && animations.Children.Count > 0) { error.Text = "Review the animation definitions for this name, then choose Add again."; return; }
        }
        if (Model is not { } model) { error.Text = "Choose a model."; return; }
        Vector3? position = null; float angle = 0;
        if (placed.IsChecked == true)
        {
            if (!Number(x, out float px) || !Number(y, out float py) || !Number(z, out float pz) || !Number(heading, out angle)) { error.Text = "Enter numbers for the position and heading."; return; }
            position = new(px, py, pz);
        }
        SourceModelAddition addition = new(model.Path, name.Text.Trim(), position, angle);
        if (validate(addition) is { } problem) { error.Text = problem; return; }
        var files = animations.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray();
        Result = new(addition, files);
        DialogResult = true;
    }
    private static bool Number(TextBox box, out float value) =>
        float.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || float.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
}
