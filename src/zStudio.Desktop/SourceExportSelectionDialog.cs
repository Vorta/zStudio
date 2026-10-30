using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using zStudio.Core.Formats;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>The project's build profiles and the one exports build (Export → Build profile).</summary>
internal sealed record SourceExportProfiles(IReadOnlyList<BuildProfile> List, string Chosen);
/// <summary>Why Export cannot run now: the info bar's message and Export's tooltip.</summary>
internal sealed record SourceExportBlock(string Message, string Reason);

/// <summary>What the Export ZBD files window uses of the workspace: the plan, the shared settings it edits, and the checks it shows.</summary>
internal sealed class SourceExportWindowServices
{
    public required Func<CancellationToken, Task<SourceExportSelection>> Load { get; init; }
    /// <summary>Remembers the choices (when a plan was read) and the destination; called on close and before reading again.</summary>
    public required Action<SourceExportSelection?, string?> Remember { get; init; }
    public Func<CancellationToken, Task<SourceExportProfiles>>? Profiles { get; init; }
    public Func<string, CancellationToken, Task>? SelectProfile { get; init; }
    /// <summary>The project's texture dithering, null when its settings file cannot be read.</summary>
    public Func<CancellationToken, Task<TextureDithering?>>? ReadDithering { get; init; }
    public Func<TextureDithering, CancellationToken, Task>? SetDithering { get; init; }
    /// <summary>What would refuse an export now (another operation, drafts, unsaved edits); asked on the UI thread while the window is open.</summary>
    public Func<SourceExportBlock?>? Blocker { get; init; }
    /// <summary>Runs off the UI thread.</summary>
    public Func<string?, IReadOnlyCollection<string>, CancellationToken, SourceExportDestination>? CheckDestination { get; init; }
    public string? Destination { get; init; }
}

/// <summary>
/// Export → Export ZBD files…: the project's exact output plan as a mission × file grid of checkboxes (the remembered
/// choices), beside the build profile, texture dithering and destination exports use. Export closes the window; the caller
/// exports every file (no outputs list) when all are selected, otherwise the checked ones (design/export-zbd-files.md).
/// </summary>
internal sealed partial class SourceExportSelectionDialog : Window
{
    private enum Phase { Reading, Ready, Rereading, Nothing, Failed }
    private const double PanelWidth = 272, PanelGap = 16;
    private readonly SourceExportWindowServices services;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(400) }, destinationDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Phase phase = Phase.Reading;
    private long loadRequest;
    private string? stateKey;
    private SourceExportBlock? block;
    private bool stale, exporting, quiet, applyingProfile;
    /// <summary>The writes of the dithering choices; Export waits for them, since the export reads the saved one.</summary>
    private Task? ditheringApply;
    /// <summary>Why Export cannot run now (its tooltip), null when it can.</summary>
    private string? exportReason;

    // The card: info bar, All files row, shared row, then the grid or a state message.
    private readonly Grid root = new() { Margin = new(16) };
    private readonly Border card = new() { BorderThickness = new(1), CornerRadius = new(8), Padding = new(8, 4, 8, 6) };
    private readonly ContentControl info = new() { Focusable = false, IsTabStop = false, Visibility = Visibility.Collapsed };
    private readonly SourceExportCheckBox allFiles = new() { Content = "_All files", IsEnabled = false };
    private readonly TextBlock allCount = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 4, 0) };
    private readonly StackPanel rereading = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock rereadingText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid sharedRow = new() { Visibility = Visibility.Collapsed };
    private readonly Border sharedDivider = Divider();
    private readonly ContentControl body = new() { Focusable = false, IsTabStop = false };
    private readonly TextBlock legend = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(2, 6, 2, 0), Visibility = Visibility.Collapsed };
    private SourceExportGrid? grid;
    private SourceExportSharedRow? shared;

    // The panel.
    private readonly ComboBox profiles = new() { IsEnabled = false };
    private readonly TextBlock profileCaption = Caption();
    private readonly ComboBox dithering = new() { IsEnabled = false };
    private readonly TextBlock ditheringCaption = Caption();
    private readonly TextBox destination = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock placeholder = new() { Text = "A folder outside the project", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(11, 0, 0, 0) };
    private readonly Button browse = new() { Width = 32, Height = 32, Padding = new(0), Margin = new(6, 0, 0, 0) };
    private readonly TextBlock destinationNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0), MinHeight = 32 };
    private readonly StackPanel summary = new();
    private readonly TextBlock summaryTitle = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock summaryDetail = new() { FontSize = 12, Margin = new(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 90 };
    private readonly Button export = new() { Content = new AccessText { Text = "_Export" }, IsDefault = true, MinWidth = 120, IsEnabled = false, Margin = new(8, 0, 0, 0) };
    private IReadOnlyList<BuildProfile> profileList = [];
    private string? chosenProfile;
    private TextureDithering? ditheringChoice;
    private CancellationTokenSource? destinationCheck;
    private SourceExportDestination? destinationState;
    /// <summary>The destination text and selection the shown note was checked for.</summary>
    private (string? Text, long Version)? destinationChecked;
    private long selectionVersion;

    /// <summary>The plan the window shows; Export closes the window with it.</summary>
    internal SourceExportSelection? Selection { get; private set; }
    /// <summary>The destination folder Export uses; null when empty (the caller asks for a folder).</summary>
    internal string? Destination => SourceExportDestination.Clean(destination.Text);

    internal SourceExportSelectionDialog(Window owner, SourceExportWindowServices services)
    {
        this.services = services;
        Owner = owner; Title = "Export ZBD files"; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        MinWidth = 940; MinHeight = 500; ResizeMode = ResizeMode.CanResize;
        root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new() { Width = new(PanelGap) });
        root.ColumnDefinitions.Add(new() { Width = new(PanelWidth) });
        root.Children.Add(BuildLeft());
        var panel = BuildPanel(); Grid.SetColumn(panel, 2); root.Children.Add(panel);
        Content = root;
        DialogLayout.Constrain(this, new(1000, 640));

        allFiles.SetResourceReference(StyleProperty, "DefaultCheckBoxStyle"); Labeled(allFiles);
        AutomationProperties.SetName(allFiles, "All files"); AutomationProperties.SetAutomationId(allFiles, "AllFiles");
        allFiles.ToolTip = "Select or clear every file (Alt+A)";
        destination.Text = services.Destination ?? "";
        destination.TextChanged += (_, _) => { placeholder.Visibility = destination.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; ScheduleDestinationCheck(); UpdateState(); };
        placeholder.Visibility = destination.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        browse.Click += (_, _) => Browse();
        export.Click += async (_, _) => await ExportAsync();
        profiles.SelectionChanged += async (_, _) => await ProfileChangedAsync();
        dithering.SelectionChanged += async (_, _) => await DitheringChangedAsync();
        poll.Tick += (_, _) => UpdateState();
        destinationDelay.Tick += async (_, _) => { destinationDelay.Stop(); await CheckDestinationAsync(); };
        // Enter exports from anywhere but a button (a focused box does not toggle with it); Space toggles.
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is CheckBox) { e.Handled = true; await ExportAsync(); }
            else if (e.Key == Key.Space && phase == Phase.Rereading && Keyboard.FocusedElement is CheckBox) e.Handled = true;
        };
        Closing += (_, _) => RememberOnClose();
        Loaded += async (_, _) =>
        {
            allFiles.Focus();
            poll.Start();
            ScheduleDestinationCheck(immediate: true);
            _ = LoadProfilesAsync(); _ = LoadDitheringAsync();
            await LoadAsync(Phase.Reading);
        };
    }

    /// <summary>Closing remembers the choices and the destination, whether or not the window exports (owner decision).</summary>
    private void RememberOnClose()
    {
        lifetime.Cancel(); poll.Stop(); destinationDelay.Stop(); destinationCheck?.Cancel();
        services.Remember(Selection, RememberedDestination());
    }
    /// <summary>The field's folder when it is empty or a full path; otherwise the folder remembered before.</summary>
    private string? RememberedDestination()
    {
        string? folder = Destination;
        return folder == null || folder.Length <= StudioSettings.MaximumExportDestination && System.IO.Path.IsPathFullyQualified(folder) ? folder : services.Destination;
    }

    private UIElement BuildLeft()
    {
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        allCount.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        Typography.SetNumeralAlignment(allCount, FontNumeralAlignment.Tabular);
        rereadingText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var ring = new AnimationBusyRing { Width = 16, Height = 16, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(ring, "Reading");
        rereading.Children.Add(ring); rereading.Children.Add(rereadingText);
        DockPanel allRow = new() { Height = 36, LastChildFill = false };
        DockPanel.SetDock(allCount, Dock.Right); DockPanel.SetDock(rereading, Dock.Right);
        allRow.Children.Add(allCount); allRow.Children.Add(rereading);
        allFiles.VerticalAlignment = VerticalAlignment.Center; allRow.Children.Add(allFiles);
        DockPanel content = new();
        foreach (var top in new UIElement[] { info, allRow, Divider(), sharedRow, sharedDivider }) { DockPanel.SetDock(top, Dock.Top); content.Children.Add(top); }
        sharedDivider.Visibility = Visibility.Collapsed;
        content.Children.Add(body);
        card.Child = content;
        legend.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        foreach (var (term, meaning) in new[] { ("gamez", " world · "), ("anim", " animations · "), ("zrdr", " resources · "), ("rtexture", " Direct3D and "), ("texture", " software texture packs, by the MB of texture memory they are for") })
        {
            Run bold = new(term) { FontWeight = FontWeights.SemiBold }; bold.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
            legend.Inlines.Add(bold); legend.Inlines.Add(new Run(meaning));
        }
        return new SourceExportStack { Children = { card, legend } };
    }

    private UIElement BuildPanel()
    {
        Grid panel = new();
        foreach (var height in new GridLength[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new(16), GridLength.Auto, GridLength.Auto, GridLength.Auto, new(20), GridLength.Auto, GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            panel.RowDefinitions.Add(new() { Height = height });
        void Add(UIElement element, int row) { Grid.SetRow(element, row); panel.Children.Add(element); }
        profiles.SetResourceReference(StyleProperty, "DefaultComboBoxStyle"); dithering.SetResourceReference(StyleProperty, "DefaultComboBoxStyle");
        profiles.HorizontalContentAlignment = dithering.HorizontalContentAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(profiles, "Build profile"); AutomationProperties.SetAutomationId(profiles, "Profile");
        AutomationProperties.SetName(dithering, "Texture dithering"); AutomationProperties.SetAutomationId(dithering, "Dithering");
        Add(Header("Build _profile", profiles), 0); Add(profiles, 1); Add(profileCaption, 2);
        foreach (var (label, tag, tip) in new[] { ("None", "none", "Round each texel to the nearest colour on its own"), ("Ordered", "ordered", "A fixed 4×4 pattern: smooth gradients that tile seamlessly"), ("Error diffusion", "diffusion", "Floyd–Steinberg: the smoothest gradients, as fine noise; tiling textures can show a seam") })
            dithering.Items.Add(new ComboBoxItem { Content = label, Tag = tag, ToolTip = tip });
        dithering.ToolTip = "How exports and checks convert texture colours to the packs' 16-bit (RGB565) or palette colours; remembered in the project's hidden .zstudio folder";
        Add(Header("Texture _dithering", dithering), 4); Add(dithering, 5); Add(ditheringCaption, 6);
        Add(Header("Desti_nation", destination), 8);
        AutomationProperties.SetName(destination, "Destination"); AutomationProperties.SetAutomationId(destination, "Destination");
        AutomationProperties.SetHelpText(destination, "A folder outside the project, such as the zbd folder of a copy of the game");
        destination.ToolTip = "The folder the game files are exported to, remembered for every project; empty: Export asks for one";
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        Grid field = new() { Children = { destination, placeholder } };
        browse.SetResourceReference(StyleProperty, "DefaultButtonStyle");
        TextBlock folder = new() { Text = "", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        folder.SetResourceReference(TextBlock.FontFamilyProperty, "SymbolThemeFontFamily");
        browse.Content = folder; browse.ToolTip = "Browse… (Alt+B)";
        AutomationProperties.SetName(browse, "Browse"); AutomationProperties.SetAutomationId(browse, "Browse");
        AccessKeyManager.Register("b", browse);
        // The field before Browse, in the tab order too.
        Grid destinationRow = new() { ColumnDefinitions = { new() { Width = new(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } } };
        Grid.SetColumn(browse, 1); destinationRow.Children.Add(field); destinationRow.Children.Add(browse);
        Add(destinationRow, 9); Add(destinationNote, 10);
        AutomationProperties.SetLiveSetting(destinationNote, AutomationLiveSetting.Polite);
        summaryDetail.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        summary.Children.Add(summaryTitle); summary.Children.Add(summaryDetail);
        // A panel has no automation peer: the title is the live region and carries the whole summary as its name.
        AutomationProperties.SetLiveSetting(summaryTitle, AutomationLiveSetting.Polite);
        Add(summary, 12);
        cancel.SetResourceReference(StyleProperty, "DefaultButtonStyle"); export.SetResourceReference(StyleProperty, "AccentButtonStyle");
        cancel.Height = export.Height = 32;
        AutomationProperties.SetAutomationId(cancel, "Cancel"); AutomationProperties.SetAutomationId(export, "Export");
        ToolTipService.SetShowOnDisabled(export, true);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0), Children = { cancel, export } };
        Add(buttons, 13);
        return panel;
    }
    private static Label Header(string text, UIElement target) => new() { Content = text, Target = target, Padding = new(0), Margin = new(0, 0, 0, 8), FontSize = 14 };
    private static TextBlock Caption()
    {
        TextBlock caption = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0), MaxHeight = 32, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return caption;
    }
    internal static Border Divider()
    {
        Border line = new() { Height = 1, Margin = new(-8, 0, -8, 0), SnapsToDevicePixels = true };
        line.SetResourceReference(Border.BackgroundProperty, SystemParameters.HighContrast ? SystemColors.GrayTextBrushKey : "DividerStrokeColorDefaultBrush");
        return line;
    }
    /// <summary>A labelled box in a row: Fluent's checkbox without its 120 × 32 minimum, centred on its text.</summary>
    internal static void Labeled(CheckBox box)
    {
        box.MinWidth = 0; box.MinHeight = 0; box.Padding = new(8, 0, 0, 0); box.VerticalContentAlignment = VerticalAlignment.Center;
    }

    // ---------- reading the plan ----------

    private async Task LoadAsync(Phase reading)
    {
        long request = ++loadRequest;
        SetPhase(reading);
        try
        {
            var model = await services.Load(lifetime.Token);
            if (request != loadRequest || lifetime.IsCancellationRequested) return;
            Show(model);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (request != loadRequest || lifetime.IsCancellationRequested) return;
            ShowFailure(ex is OperationCanceledException ? "The project changed while its game files were listed." : ex.Message);
        }
    }

    private void Show(SourceExportSelection model)
    {
        if (Selection != null) Selection.Changed -= SelectionChanged;
        Selection = model; model.Changed += SelectionChanged;
        allFiles.SetBinding(BoxChecked, new Binding(nameof(SourceExportSet.IsChecked)) { Source = model.All, Mode = BindingMode.TwoWay });
        if (model.Files.Count == 0) { grid = null; shared = null; SetPhase(Phase.Nothing); return; }
        shared = model.SharedFiles.Count > 0 ? new SourceExportSharedRow(model) : null;
        sharedRow.Children.Clear();
        if (shared != null) sharedRow.Children.Add(shared);
        grid = new SourceExportGrid(model);
        SetPhase(Phase.Ready);
        // On open the focus is on All files (it was disabled while reading), so Export → Export ZBD files… → Enter exports everything.
        if (!IsKeyboardFocusWithin || Keyboard.FocusedElement == this) allFiles.Focus();
    }

    private void ShowFailure(string message)
    {
        if (Selection != null) Selection.Changed -= SelectionChanged;
        Selection = null; grid = null; shared = null;
        BindingOperations.ClearBinding(allFiles, BoxChecked); allFiles.IsChecked = false;
        string text = message.Length <= 1024 ? message : message[..1024] + "…";
        StackPanel failure = new() { Margin = new(0, 6, 0, 0) };
        failure.Children.Add(InfoBar(error: true, "Can’t list the game files.", text, "Try again", () => _ = LoadAsync(Phase.Reading)));
        failure.Children.Add(Message(null, "Fix the file and choose Try again, or choose another build profile.", 220));
        body.Content = failure;
        SetPhase(Phase.Failed);
        Announce(failure.Children[0]);
    }

    private static readonly DependencyProperty BoxChecked = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private void SetPhase(Phase value)
    {
        phase = value;
        if (value is Phase.Reading or Phase.Failed) { grid = null; shared = null; }
        bool loaded = value is Phase.Ready or Phase.Rereading;
        allFiles.IsEnabled = value == Phase.Ready;
        rereading.Visibility = value == Phase.Rereading ? Visibility.Visible : Visibility.Collapsed;
        allCount.Visibility = value == Phase.Ready ? Visibility.Visible : Visibility.Collapsed;
        sharedRow.Visibility = sharedDivider.Visibility = loaded && shared != null ? Visibility.Visible : Visibility.Collapsed;
        legend.Visibility = loaded && grid != null ? Visibility.Visible : Visibility.Collapsed;
        if (!loaded && value != Phase.Failed) { BindingOperations.ClearBinding(allFiles, BoxChecked); allFiles.IsChecked = false; }
        foreach (var part in new FrameworkElement?[] { grid, shared })
            if (part != null) { part.Opacity = value == Phase.Rereading ? 0.45 : 1; part.IsHitTestVisible = value == Phase.Ready; }
        switch (value)
        {
            case Phase.Reading: body.Content = Message(null, "Reading the source project…", 300, ring: true); root.MaxWidth = double.PositiveInfinity; break;
            case Phase.Nothing: body.Content = Message("Nothing to build yet", "This project has no files zStudio can build into game files.", 300); root.MaxWidth = double.PositiveInfinity; break;
            case Phase.Ready: body.Content = grid; break;
            case Phase.Failed: root.MaxWidth = double.PositiveInfinity; break;
        }
        if (grid != null && value is Phase.Ready or Phase.Rereading)
        {
            grid.WidthsChanged = ApplyWidths;
            root.MaxWidth = grid.MaximumWidth + 2 + 18 + PanelGap + PanelWidth;
        }
        stateKey = null;
        SelectionChanged();
    }

    private void ApplyWidths(double rowHeader) { shared?.SetHeaderWidth(rowHeader); }

    private static FrameworkElement Message(string? title, string text, double height, bool ring = false)
    {
        StackPanel message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (ring) { var busy = new AnimationBusyRing { Width = 24, Height = 24, Margin = new(0, 0, 0, 10) }; AutomationProperties.SetName(busy, "Reading"); message.Children.Add(busy); }
        if (title != null) message.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 10) });
        TextBlock line = new() { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 380, LineHeight = 19 };
        line.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        message.Children.Add(line);
        return new Grid { MinHeight = height, Children = { message } };
    }

    /// <summary>The Fluent InfoBar look: severity colour, icon, message and one action.</summary>
    private static Border InfoBar(bool error, string? title, string text, string? action, Action? act)
    {
        Border bar = new() { BorderThickness = new(1), CornerRadius = new(4), Padding = new(12, 10, 12, 10) };
        bar.SetResourceReference(Border.BackgroundProperty, error ? "SystemFillColorCriticalBackgroundBrush" : "SystemFillColorCautionBackgroundBrush");
        bar.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        DockPanel content = new();
        Grid icon = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 2, 10, 0) };
        Ellipse disc = new(); disc.SetResourceReference(Shape.FillProperty, error ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush");
        icon.Children.Add(disc);
        icon.Children.Add(new TextBlock { Text = error ? "✕" : "!", FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon);
        if (action != null && act != null)
        {
            Button button = new() { Content = action, Margin = new(10, -4, -2, -4), VerticalAlignment = VerticalAlignment.Top };
            button.SetResourceReference(StyleProperty, "DefaultButtonStyle");
            button.Click += (_, _) => act();
            DockPanel.SetDock(button, Dock.Right); content.Children.Add(button);
        }
        TextBlock message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 20, VerticalAlignment = VerticalAlignment.Center };
        if (title != null) message.Inlines.Add(new Run(title + " ") { FontWeight = FontWeights.SemiBold });
        message.Inlines.Add(new Run(text));
        AutomationProperties.SetLiveSetting(message, error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        AutomationProperties.SetName(message, (title == null ? "" : title + " ") + text);
        content.Children.Add(message);
        bar.Child = content;
        return bar;
    }
    private static void Announce(UIElement bar)
    {
        var text = bar is Border { Child: DockPanel panel } ? panel.Children.OfType<TextBlock>().LastOrDefault() : null;
        if (text == null) return;
        (UIElementAutomationPeer.FromElement(text) ?? UIElementAutomationPeer.CreatePeerForElement(text))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // ---------- the state shown: info bar, counts, summary, Export ----------

    private void SelectionChanged()
    {
        selectionVersion++;
        var model = Selection;
        bool loaded = phase is Phase.Ready or Phase.Rereading && model != null;
        int selected = loaded ? model!.SelectedCount : 0, total = loaded ? model!.Files.Count : 0;
        allCount.Text = loaded ? $"{selected:N0} of {total:N0}" : "";
        if (!loaded) { summaryTitle.Text = phase == Phase.Nothing ? "No files to build" : " "; summaryDetail.Text = phase == Phase.Reading ? "Reading the source project…" : ""; }
        else if (selected == 0) { summaryTitle.Text = "No files selected"; summaryDetail.Text = "Select files to export"; }
        else
        {
            int sharedSelected = model!.Shared?.Selected ?? 0, missionFiles = selected - sharedSelected, missions = model.Missions.Count(m => m.Selected > 0);
            summaryTitle.Text = selected == total ? $"All {total:N0} files" : $"{selected:N0} of {total:N0} files";
            summaryDetail.Text = string.Join(" · ", new[] { sharedSelected > 0 ? $"{sharedSelected:N0} shared" : null, missionFiles > 0 ? $"{missionFiles:N0} from {missions:N0} mission{(missions == 1 ? "" : "s")}" : null }.OfType<string>());
        }
        string spoken = summaryDetail.Text.Length > 0 ? $"{summaryTitle.Text.Trim()}, {summaryDetail.Text}" : summaryTitle.Text.Trim();
        if (AutomationProperties.GetName(summaryTitle) != spoken)
        {
            AutomationProperties.SetName(summaryTitle, spoken);
            (UIElementAutomationPeer.FromElement(summaryTitle) ?? UIElementAutomationPeer.CreatePeerForElement(summaryTitle))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        if (loaded) ScheduleDestinationCheck();
        UpdateState();
    }

    private void UpdateState()
    {
        if (lifetime.IsCancellationRequested) return;
        var model = Selection;
        stale = phase == Phase.Ready && model != null && !model.IsCurrent();
        // A profile chosen here reads the plan again as an operation of its own; it is not another export.
        block = phase is Phase.Rereading ? null : services.Blocker?.Invoke();
        string key = $"{phase}|{stale}|{block?.Message}";
        if (key != stateKey)
        {
            stateKey = key;
            UIElement? bar = stale ? InfoBar(false, null, "The project or its build profile changed after this list was read.", "Read again", ReadAgain)
                : block != null && phase is Phase.Ready or Phase.Nothing ? InfoBar(false, null, block.Message, null, null) : null;
            info.Content = bar; info.Visibility = bar == null ? Visibility.Collapsed : Visibility.Visible;
            info.Margin = new(0, 4, 0, 6);
            if (bar != null) Announce(bar);
        }
        int selected = phase == Phase.Ready && model != null ? model.SelectedCount : 0, total = model?.Files.Count ?? 0;
        string ellipsis = Destination == null ? "…" : "";
        string label = selected == 0 ? "_Export" : selected == total ? $"_Export all {total:N0} files{ellipsis}" : $"_Export {selected:N0} file{(selected == 1 ? "" : "s")}{ellipsis}";
        // The accent style's presenter does not read access keys from a string: Alt+E needs the AccessText itself.
        if ((export.Content as AccessText)?.Text != label) export.Content = new AccessText { Text = label };
        string? reason = phase switch
        {
            Phase.Reading => "The list of game files is not ready",
            Phase.Failed => "The list of game files could not be read",
            Phase.Nothing => "This project has nothing to build yet",
            Phase.Rereading => "Reading the profile's files",
            _ => stale ? "Read the list again first"
                : block?.Reason
                ?? (selected == 0 ? "Select files to export" : destinationState?.Problem != null ? "Choose another destination folder" : null),
        };
        exportReason = reason;
        export.IsEnabled = reason == null && !exporting;
        string tip = reason ?? (Destination == null ? "Export (Enter); a folder picker asks where" : "Export (Enter)");
        if (export.ToolTip as string != tip) export.ToolTip = tip;
    }

    private void ReadAgain()
    {
        if (Selection != null) services.Remember(Selection, RememberedDestination());
        _ = LoadAsync(Selection != null ? Phase.Rereading : Phase.Reading);
        rereadingText.Text = "Reading the list again…";
    }

    // ---------- build profile and texture dithering ----------

    private async Task LoadProfilesAsync()
    {
        if (services.Profiles == null) return;
        try
        {
            var result = await services.Profiles(lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            profileList = result.List; chosenProfile = result.Chosen;
            quiet = true;
            try
            {
                profiles.Items.Clear();
                foreach (var profile in result.List)
                {
                    ComboBoxItem item = new() { Tag = profile.Name, Content = new TextBlock { Text = profile.Name + (profile.IsDefault ? " (default)" : "") + (profile.Error != null ? " · cannot be used" : "") } };
                    AutomationProperties.SetName(item, profile.Name);
                    if (profile.Error is { } error) { item.IsEnabled = false; item.ToolTip = error.Length <= 512 ? error : error[..512] + "…"; ToolTipService.SetShowOnDisabled(item, true); }
                    else item.ToolTip = profile.Description;
                    profiles.Items.Add(item);
                }
                SelectProfileItem(result.Chosen);
            }
            finally { quiet = false; }
            profiles.IsEnabled = true;
            ShowProfileCaption(null);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { profiles.IsEnabled = false; ShowProfileCaption(ex.Message); }
    }
    private void SelectProfileItem(string? name)
    {
        bool wasQuiet = quiet; quiet = true;
        try { profiles.SelectedItem = profiles.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, name, StringComparison.OrdinalIgnoreCase)); }
        finally { quiet = wasQuiet; }
    }
    private void ShowProfileCaption(string? problem)
    {
        var profile = profileList.FirstOrDefault(p => p.Name.Equals(chosenProfile, StringComparison.OrdinalIgnoreCase));
        if (problem != null) { profileCaption.Text = problem.Length <= 512 ? problem : problem[..512] + "…"; profileCaption.ToolTip = problem; profileCaption.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush"); }
        else if (profile == null) { profileCaption.Text = ""; profileCaption.ToolTip = null; }
        else
        {
            profileCaption.Text = profile.Error ?? ProfileCaption(profile);
            profileCaption.ToolTip = profile.Error ?? profile.Description;
            profileCaption.SetResourceReference(TextBlock.ForegroundProperty, profile.Error != null ? "SystemFillColorCriticalBrush" : "TextFillColorTertiaryBrush");
            profiles.ToolTip = profile.Description;
        }
        profileCaption.Visibility = profileCaption.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>"10 texture packs; automatic rtexture up to 256 MB · experimental", "Up to 6 texture packs per mission · measured".</summary>
    internal static string ProfileCaption(BuildProfile profile)
    {
        var packs = profile.TexturePacks;
        int everywhere = packs.Count(p => p.Missions == null);
        string count = packs.All(p => p.Missions == null) ? $"{packs.Count} texture pack{(packs.Count == 1 ? "" : "s")}"
            : $"Up to {everywhere + packs.Where(p => p.Missions != null).SelectMany(p => p.Missions!).GroupBy(m => m, StringComparer.OrdinalIgnoreCase).Select(g => g.Count()).DefaultIfEmpty(0).Max()} texture packs per mission";
        string automatic = packs.FirstOrDefault(p => p.Automatic) is { BudgetBytes: long budget } ? $"; automatic rtexture up to {budget / (1024 * 1024)} MB" : "";
        return $"{count}{automatic} · {profile.Status}";
    }

    private async Task ProfileChangedAsync()
    {
        // One choice is applied at a time; a choice made meanwhile is applied when the earlier one has been. Closing the
        // window does not cancel them: the profile shown is kept on Cancel, as the menu's choice is (Export stays disabled
        // until the plan is read again after them).
        if (quiet || services.SelectProfile == null || applyingProfile) return;
        applyingProfile = true;
        long request = 0;
        bool hadPlan = false;
        try
        {
            while (profiles.SelectedItem is ComboBoxItem { Tag: string name } && !name.Equals(chosenProfile, StringComparison.OrdinalIgnoreCase))
            {
                if (!lifetime.IsCancellationRequested)
                {
                    // The choices carry over by path: remember them before the plan is read for the other profile.
                    if (Selection != null) services.Remember(Selection, RememberedDestination());
                    request = ++loadRequest;
                    hadPlan = phase is Phase.Ready or Phase.Rereading && grid != null;
                    SetPhase(hadPlan ? Phase.Rereading : Phase.Reading);
                    rereadingText.Text = $"Reading the {name} profile…";
                }
                try { await services.SelectProfile(name, CancellationToken.None); }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    SelectProfileItem(chosenProfile);
                    ShowProfileCaption(ex.Message);
                    break;
                }
                chosenProfile = name; ShowProfileCaption(null);
            }
        }
        finally { applyingProfile = false; }
        if (request != 0 && request == loadRequest && !lifetime.IsCancellationRequested) await LoadAsync(hadPlan ? Phase.Rereading : Phase.Reading);
    }

    private async Task LoadDitheringAsync()
    {
        if (services.ReadDithering == null) return;
        try
        {
            ditheringChoice = await services.ReadDithering(lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            SelectDithering(ditheringChoice);
            dithering.IsEnabled = services.SetDithering != null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { ShowDitheringCaption(ex.Message); }
    }
    private void SelectDithering(TextureDithering? value)
    {
        bool wasQuiet = quiet; quiet = true;
        try { dithering.SelectedItem = value is { } choice ? dithering.Items.OfType<ComboBoxItem>().FirstOrDefault(i => SourceProjectSettings.Parse((string)i.Tag) == choice) : null; }
        finally { quiet = wasQuiet; }
    }
    private void ShowDitheringCaption(string? problem)
    {
        ditheringCaption.Text = problem == null ? "" : problem.Length <= 512 ? problem : problem[..512] + "…";
        ditheringCaption.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        ditheringCaption.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
    }
    private Task DitheringChangedAsync()
    {
        if (quiet || services.SetDithering is not { } set || ditheringApply is { IsCompleted: false }) return Task.CompletedTask;
        return ditheringApply = ApplyDitheringAsync(set);
    }
    /// <summary>As with the profile, one choice is written at a time, the last one shown is the one kept, and closing the window does not cancel it.</summary>
    private async Task ApplyDitheringAsync(Func<TextureDithering, CancellationToken, Task> set)
    {
        while (dithering.SelectedItem is ComboBoxItem { Tag: string name } && SourceProjectSettings.Parse(name) is { } value && value != ditheringChoice)
        {
            try { await set(value, CancellationToken.None); ditheringChoice = value; ShowDitheringCaption(null); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { SelectDithering(ditheringChoice); ShowDitheringCaption(ex.Message); return; }
        }
    }

    // ---------- destination ----------

    private void Browse()
    {
        OpenFolderDialog dialog = new() { Title = "Choose the folder the game files are exported to" };
        // Start at the typed folder, or at its nearest existing parent when it is a new folder.
        try
        {
            for (string? folder = Destination is { } typed && System.IO.Path.IsPathFullyQualified(typed) ? System.IO.Path.GetFullPath(typed) : null; folder != null; folder = System.IO.Path.GetDirectoryName(folder))
                if (Directory.Exists(folder)) { dialog.InitialDirectory = folder; break; }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { }
        if (dialog.ShowDialog(this) == true) destination.Text = dialog.FolderName;
    }

    private void ScheduleDestinationCheck(bool immediate = false)
    {
        destinationDelay.Stop();
        if (immediate) _ = CheckDestinationAsync(); else destinationDelay.Start();
    }

    /// <summary>Checks the typed folder off the UI thread, as the export would; a newer check cancels it.</summary>
    private async Task<SourceExportDestination?> CheckDestinationAsync()
    {
        if (services.CheckDestination is not { } check || lifetime.IsCancellationRequested) return null;
        destinationCheck?.Cancel();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); destinationCheck = cancellation;
        string? text = Destination; long version = selectionVersion;
        IReadOnlyCollection<string> selected = phase == Phase.Ready && Selection != null ? Selection.SelectedPaths : [];
        SourceExportDestination result;
        try { result = await Task.Run(() => check(text, selected, cancellation.Token), cancellation.Token); }
        catch (OperationCanceledException) { return null; }
        finally { if (destinationCheck == cancellation) destinationCheck = null; }
        if (cancellation.IsCancellationRequested || text != Destination) return null;
        destinationState = result; destinationChecked = (text, version);
        ShowDestinationNote(result);
        UpdateState();
        return result;
    }

    private void ShowDestinationNote(SourceExportDestination state)
    {
        string key = "TextFillColorSecondaryBrush";
        int selected = state.Selected;
        if (state.Problem != null) { destinationNote.Text = state.Problem; key = "SystemFillColorCriticalBrush"; }
        else if (state.Path == null) { destinationNote.Text = "The zbd folder of a copy of the game, or an empty folder. Export asks for one while this is empty."; key = "TextFillColorTertiaryBrush"; }
        else if (state.Exists == false) destinationNote.Text = "This folder does not exist yet; the export creates it.";
        else if (phase != Phase.Ready || selected == 0) destinationNote.Text = "An existing folder.";
        else if (state.Existing is not > 0) destinationNote.Text = selected == 1 ? "The selected file is not here yet." : $"None of the {selected:N0} selected files are here yet.";
        else
        {
            int existing = state.Existing.Value, added = selected - existing;
            destinationNote.Text = selected == 1 ? "The selected file is already here; you confirm before it is replaced."
                : (existing == selected ? $"All {selected:N0} selected files are already here; you confirm before they are replaced."
                : $"{existing:N0} of the {selected:N0} selected files {(existing == 1 ? "is" : "are")} already here; you confirm before {(existing == 1 ? "it is" : "they are")} replaced. {added:N0} {(added == 1 ? "is" : "are")} new.");
        }
        destinationNote.SetResourceReference(TextBlock.ForegroundProperty, key);
    }

    // ---------- export ----------

    private async Task ExportAsync()
    {
        UpdateState();
        if (exporting || !export.IsEnabled || Selection is not { } model) return;
        exporting = true;
        try
        {
            // What the export would refuse anyway is said here, so the window stays open to change it.
            // Export's own check replaces a scheduled one (a box toggled just before Enter), which would cancel it.
            destinationDelay.Stop();
            if (!CheckedAsShown()) await CheckDestinationAsync();
            if (ditheringApply is { IsCompleted: false } writing) await writing;
            if (lifetime.IsCancellationRequested) return;
            UpdateState();
            // The field and the boxes stay editable during the check: export only what a finished check saw.
            if (exportReason != null || Selection != model || !CheckedAsShown()) return;
            DialogResult = true;
        }
        finally { exporting = false; if (!lifetime.IsCancellationRequested) UpdateState(); }
    }
    /// <summary>Whether the shown destination note was checked for the field's folder and the boxes as they are now.</summary>
    private bool CheckedAsShown() => services.CheckDestination == null || destinationChecked is { } done && done.Text == Destination && done.Version == selectionVersion;
}

/// <summary>A checkbox that selects all of a mixed or clear set's files and clears a checked one.</summary>
internal sealed class SourceExportCheckBox : CheckBox
{
    protected override void OnToggle() => SetCurrentValue(IsCheckedProperty, IsChecked != true);
}

/// <summary>The card above the legend: the card takes the height left once the legend is placed, and only what it needs.</summary>
internal sealed class SourceExportStack : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        if (Children.Count < 2) return default;
        UIElement card = Children[0], legend = Children[1];
        legend.Measure(new Size(available.Width, double.PositiveInfinity));
        card.Measure(new Size(available.Width, Math.Max(0, available.Height - legend.DesiredSize.Height)));
        return new Size(Math.Max(card.DesiredSize.Width, legend.DesiredSize.Width), card.DesiredSize.Height + legend.DesiredSize.Height);
    }
    protected override Size ArrangeOverride(Size final)
    {
        if (Children.Count < 2) return final;
        UIElement card = Children[0], legend = Children[1];
        double cardHeight = Math.Min(card.DesiredSize.Height, Math.Max(0, final.Height - legend.DesiredSize.Height));
        card.Arrange(new Rect(0, 0, final.Width, cardHeight));
        legend.Arrange(new Rect(0, cardHeight, final.Width, legend.DesiredSize.Height));
        return final;
    }
}
