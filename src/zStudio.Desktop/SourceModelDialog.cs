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
    /// <summary>The most models the list shows for a filter (it is virtualized); the filter narrows a longer list, which a note discloses.</summary>
    internal const int MaximumListedModels = 2000;
    private readonly TextBox filter = new() { Margin = new(0, 0, 0, 6) }, name = new(), x = new(), y = new(), z = new(), heading = new();
    private readonly ListBox list = new() { Height = 220 };
    private readonly RadioButton unplaced = new() { IsChecked = true, Margin = new(0, 2, 0, 2) }, placed = new() { Margin = new(0, 2, 0, 2) };
    private readonly TextBlock listNote = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new(0, 2, 0, 0) }, nameNote = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new(0, 2, 0, 0) }, animationNote = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }, error = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.IndianRed };
    private readonly StackPanel animations = new();
    private readonly TextBox animationFilter = new();
    private readonly TextBlock animationCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button animationPrevious = new() { Content = "Previous", MinWidth = 80 }, animationNext = new() { Content = "Next", MinWidth = 80, Margin = new(6, 0, 0, 0) };
    private IReadOnlyList<SourceDefinitionFile> animationFiles = [];
    private readonly HashSet<string> chosenAnimations = new(StringComparer.Ordinal);
    private int animationPage;
    private readonly Button add = new() { Content = "Add", IsDefault = true, MinWidth = 80, Margin = new(0, 0, 8, 0), IsEnabled = false };
    private readonly DispatcherTimer lookup = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? lookupCancellation;
    private string? suggestedName;
    /// <summary>The node name the shown animation definitions were looked up for.</summary>
    private string? animationsFor;
    private bool closed;
    private bool accepting;
    private long inputRevision, nameRevision;

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
        panel.Children.Add(new TextBlock { Text = $"Load a model from any folder of the project into the {mission} world. Physical models and logical aliases are listed. A path already bound in {mission} uses this map's geometry and zones; other paths import their donor bindings, refusing conflicts. Exports include the model's geometry, materials and textures.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        panel.Children.Add(filter);
        // Rows are data, so only the visible ones get controls; paths are literal text, not access-key labels.
        FrameworkElementFactory row = new(typeof(TextBlock));
        row.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ModelRow.Label)));
        list.ItemTemplate = new DataTemplate { VisualTree = row };
        Style rowStyle = new(typeof(ListBoxItem));
        rowStyle.Setters.Add(new Setter(ToolTipProperty, new System.Windows.Data.Binding(nameof(ModelRow.Path))));
        rowStyle.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty, new System.Windows.Data.Binding(nameof(ModelRow.Path))));
        list.ItemContainerStyle = rowStyle;
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        panel.Children.Add(list);
        panel.Children.Add(listNote);
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
        panel.Children.Add(Label("Animations", 10)); panel.Children.Add(animationNote);
        System.Windows.Automation.AutomationProperties.SetName(animationFilter, "Filter animation definition files");
        panel.Children.Add(animationFilter); panel.Children.Add(animationCount); panel.Children.Add(animations);
        StackPanel animationPages = new() { Orientation = Orientation.Horizontal };
        animationPages.Children.Add(animationPrevious); animationPages.Children.Add(animationNext); panel.Children.Add(animationPages);
        animationFilter.TextChanged += (_, _) => { animationPage = 0; FillAnimations(); };
        animationPrevious.Click += (_, _) => { animationPage--; FillAnimations(); };
        animationNext.Click += (_, _) => { animationPage++; FillAnimations(); };
        panel.Children.Add(error);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        buttons.Children.Add(add); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        filter.TextChanged += (_, _) => Fill();
        list.SelectionChanged += (_, _) => ModelChanged();
        name.TextChanged += (_, _) => { NameChanged(); lookup.Stop(); lookup.Start(); };
        foreach (var box in new[] { x, y, z, heading }) box.TextChanged += (_, _) => inputRevision++;
        lookup.Tick += async (_, _) => { lookup.Stop(); await LookUpAnimationsAsync(); };
        placed.Checked += (_, _) => Placement(); unplaced.Checked += (_, _) => Placement();
        add.Click += async (_, _) => await AcceptAsync();
        Closed += (_, _) => { closed = true; lookup.Stop(); lookupCancellation?.Cancel(); };
        Fill(); Placement(); NameChanged();
        Loaded += (_, _) => filter.Focus();
    }

    private static TextBlock Label(string text, double top) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new(0, top, 0, 4) };
    private static string Coordinate(float value) => MathF.Round(value, 1).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>A model's row in the list.</summary>
    private sealed record ModelRow(SourceModelChoice Model)
    {
        public string Label => $"{Model.Name}   ({Model.Folder})";
        public string Path => Model.Path;
    }
    private void Fill()
    {
        var selected = Model;
        string text = filter.Text.Trim();
        int matching = 0; List<ModelRow> rows = [];
        foreach (var model in models)
        {
            if (text.Length != 0 && !model.Path.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            if (++matching <= MaximumListedModels) rows.Add(new(model));
        }
        list.ItemsSource = rows;
        if (selected != null && rows.FirstOrDefault(r => r.Model == selected) is { } kept) list.SelectedItem = kept;
        listNote.Text = matching > rows.Count ? $"Showing {rows.Count:N0} of {matching:N0} models; type part of a path to find the others." : "";
        listNote.Visibility = listNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private SourceModelChoice? Model => (list.SelectedItem as ModelRow)?.Model;
    private void ModelChanged()
    {
        inputRevision++;
        if (Model is not { } model) { add.IsEnabled = false; return; }
        // Follow the model's name until a name is typed.
        if (name.Text.Length == 0 || name.Text == suggestedName) { suggestedName = model.Name; name.Text = model.Name; }
        add.IsEnabled = !accepting;
    }
    private void NameChanged()
    {
        inputRevision++; nameRevision++;
        lookupCancellation?.Cancel();
        animationsFor = null; ClearAnimations(); animationNote.Text = "";
        string text = name.Text.Trim();
        nameNote.Text = text.Length == 0 ? "Resources and animations find the model by this name." :
            worldNames.Contains(text) ? $"The {mission} world already has a node named {text}. Resources and animations bind to one of them; a new name keeps them apart." :
            "Resources and animations find the model by this name.";
    }
    private void Placement() { inputRevision++; foreach (var box in new[] { x, y, z, heading }) box.IsEnabled = placed.IsChecked == true; }

    private async Task LookUpAnimationsAsync()
    {
        lookupCancellation?.Cancel();
        string root = name.Text.Trim();
        long revision = nameRevision;
        ClearAnimations(); animationsFor = null;
        if (root.Length == 0) { animationNote.Text = ""; animationsFor = root; return; }
        using CancellationTokenSource cancellation = new(); lookupCancellation = cancellation;
        bool Current() => !closed && IsLoaded && !cancellation.IsCancellationRequested && lookupCancellation == cancellation && nameRevision == revision && name.Text.Trim() == root;
        animationNote.Text = $"Looking for animation definitions for {root}…";
        try
        {
            var files = await definitions(root, cancellation.Token);
            if (!Current()) return;
            animationsFor = root;
            // The selection cap is independent of discovery: every file is reachable through filtering and pages.
            animationFiles = files;
            bool choose = files.Count > SourceWorlds.MaximumDefinitionChoices;
            if (!choose) foreach (var file in files) chosenAnimations.Add(file.Path);
            animationNote.Text = files.Count == 0 ? $"No other mission lists animation definitions for {root}."
                : choose ? $"{files.Count:N0} definition files of other missions list animations for {root}; filter or browse all files and choose up to {SourceWorlds.MaximumDefinitionChoices}. Checked files are added to {mission}'s animation list."
                : $"Other missions list these animation definitions for {root}. Checked files are added to {mission}'s animation list:";
            FillAnimations();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or System.IO.IOException or UnauthorizedAccessException) { if (Current()) animationNote.Text = "Animation definitions could not be read: " + ex.Message; }
        finally { if (lookupCancellation == cancellation) lookupCancellation = null; }
    }

    private void ClearAnimations()
    {
        animationFiles = []; chosenAnimations.Clear(); animationPage = 0;
        animationFilter.Text = ""; FillAnimations();
    }
    private void FillAnimations()
    {
        var matches = animationFiles.Where(f => f.Path.Contains(animationFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        int size = SourceWorlds.MaximumDefinitionChoices;
        animationPage = Math.Clamp(animationPage, 0, Math.Max(0, (matches.Length - 1) / size));
        animations.Children.Clear();
        foreach (var file in matches.Skip(animationPage * size).Take(size))
        {
            CheckBox box = new()
            {
                IsChecked = chosenAnimations.Contains(file.Path), Tag = file.Path, Margin = new(0, 2, 0, 2),
                Content = new TextBlock { Text = $"{file.Path}  —  {string.Join(", ", file.Animations.Take(6).Select(n => n.Length > 64 ? n[..64] + "…" : n))}{(file.Animations.Count > 6 ? ", …" : "")} (used by {string.Join(", ", file.Missions.Take(8))}{(file.Missions.Count > 8 ? $" and {file.Missions.Count - 8} more" : "")})", TextWrapping = TextWrapping.Wrap }
            };
            box.Checked += (_, _) =>
            {
                if (chosenAnimations.Count >= size && !chosenAnimations.Contains(file.Path))
                { box.IsChecked = false; error.Text = $"Choose at most {size} animation definition files."; return; }
                chosenAnimations.Add(file.Path); error.Text = ""; Count();
            };
            box.Unchecked += (_, _) => { chosenAnimations.Remove(file.Path); error.Text = ""; Count(); };
            animations.Children.Add(box);
        }
        animationPrevious.IsEnabled = animationPage > 0; animationNext.IsEnabled = (animationPage + 1) * size < matches.Length;
        Count();
        void Count() => animationCount.Text = $"Showing {Math.Min(animationPage * size + 1, matches.Length)}–{Math.Min((animationPage + 1) * size, matches.Length)} of {matches.Length:N0} matching files ({animationFiles.Count:N0} total). {chosenAnimations.Count} of {size} selected.";
    }

    private async Task AcceptAsync()
    {
        if (accepting || closed) return;
        accepting = true; add.IsEnabled = false;
        try { await AcceptCurrentAsync(); }
        finally { accepting = false; if (!closed) add.IsEnabled = Model != null; }
    }
    private async Task AcceptCurrentAsync()
    {
        error.Text = "";
        long revision = inputRevision;
        if (animationsFor != name.Text.Trim())
        {
            // The listed definitions belong to another name; show this name's before adding.
            lookup.Stop();
            await LookUpAnimationsAsync();
            // Canceling the dialog during the lookup ends it; a closed dialog has no result to set.
            if (closed) return;
            if (revision != inputRevision) { error.Text = "The model or its settings changed while looking up animations; review them and choose Add again."; return; }
            if (animationsFor != name.Text.Trim()) { error.Text = "Animation definitions could not be read for this name; retry before adding the model."; return; }
            if (animationFiles.Count > 0) { error.Text = "Review the animation definitions for this name, then choose Add again."; return; }
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
        var files = animationFiles.Where(f => chosenAnimations.Contains(f.Path)).Select(f => f.Path).ToArray();
        Result = new(addition, files);
        DialogResult = true;
    }
    private static bool Number(TextBox box, out float value) =>
        float.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || float.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
}
