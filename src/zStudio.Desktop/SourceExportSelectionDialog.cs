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

/// <summary>Why Export cannot run now: the info bar's message and Export's tooltip.</summary>
internal sealed record SourceExportBlock(string Message, string Reason);

/// <summary>What the Export ZBD files window uses of the workspace: the plan, the shared settings it edits, and the checks it shows.</summary>
internal sealed class SourceExportWindowServices
{
    public required Func<CancellationToken, Task<SourceExportSelection>> Load { get; init; }
    /// <summary>Remembers the choices (when a plan was read) and the destination; called on close and before reading again.</summary>
    public required Action<SourceExportSelection?, string?> Remember { get; init; }
    /// <summary>The project's texture dithering, null when its settings file cannot be read.</summary>
    public Func<CancellationToken, Task<TextureDithering?>>? ReadDithering { get; init; }
    public Func<TextureDithering, CancellationToken, Task>? SetDithering { get; init; }
    /// <summary>How many dithering changes the workspace has started (the menu, MCP and this window's): a choice still
    /// queued here is dropped when another started after it.</summary>
    public Func<long>? DitheringRequests { get; init; }
    /// <summary>What would refuse an export now (another operation, drafts, unsaved edits); asked on the UI thread while the window is open.</summary>
    public Func<SourceExportBlock?>? Blocker { get; init; }
    /// <summary>Runs off the UI thread.</summary>
    public Func<string?, IReadOnlyCollection<string>, CancellationToken, SourceExportDestination>? CheckDestination { get; init; }
    public string? Destination { get; init; }
}

/// <summary>
/// Export → Export ZBD files…: the project's exact output plan as a mission × file grid of checkboxes (the remembered
/// choices), beside the Direct3D, software and 32-bit pack boxes, the texture dithering and the destination exports use. Export closes
/// the window; the caller exports every file (no outputs list) when all are selected, otherwise the checked ones
/// (design/export-zbd-files.md, design/export-zbd-files-rgba.md).
/// </summary>
internal sealed partial class SourceExportSelectionDialog : Window
{
    private enum Phase { Reading, Ready, Rereading, Nothing, Failed }
    private const double PanelWidth = 272, PanelGap = 16;
    /// <summary>
    /// The default size shows every box of the retail project (176 files, the seven shared files on one line) without
    /// scrolling; at the minimum the body scrolls under its frozen headers, never sideways (the retail grid needs a 1,056-DIP window),
    /// and the panel fits at its fullest: three pack boxes, a two-line dithering caption (a settings problem), a two-line
    /// destination note and a three-line caution about leftover packs (about 516 DIP of panel, measured in WPF at 150 %, in a
    /// 586-DIP window; 590 leaves room for layout rounding at other scales; design/export-zbd-files-rgba.md §3 asked for 570,
    /// which holds the note or the dithering problem at two lines with the caution, not both, and allowed up to 20 more).
    /// </summary>
    internal const double DefaultWidth = 1100, DefaultHeight = 640, MinimumWidth = 1060, MinimumHeight = 590;
    private readonly SourceExportWindowServices services;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(400) }, destinationDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Phase phase = Phase.Reading;
    private long loadRequest;
    private string? stateKey;
    private SourceExportBlock? block;
    private bool stale, exporting, quiet;
    /// <summary>The writes of the dithering choices; Export waits for them, since the export reads the saved one.</summary>
    private Task? ditheringApply;
    /// <summary>The workspace's dithering change count when this window last chose or applied a dithering.</summary>
    private long ditheringSeen;
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
    /// <summary>
    /// A pack box of the panel: its box, "N of M" count and caption, the games its files serve (the caption, before the size),
    /// its set in the model, and its help text around the size (", about N MB", or nothing when not every file is estimated).
    /// </summary>
    private sealed record PackBox(SourceExportCheckBox Box, TextBlock Count, TextBlock Caption, string Games, string Id, string Name, Func<SourceExportSelection, SourceExportSet> Set, Func<string, string> Help, string Tip);
    /// <summary>The panel's pack boxes in the grid's section order: Direct3D (Alt+R; Alt+D is Texture dithering's), software (Alt+S), 32-bit (Alt+3).</summary>
    private readonly PackBox[] packs =
    [
        new(new() { Content = "Di_rect3D packs", IsEnabled = false }, PackCount(), PackCaption(), "Recoil, reRECOIL v1 and v2", "Direct3DPacks", "Direct3D packs", m => m.Direct3DPacks,
            size => $"rtexture packs of every mission and image.zbd, the interface images{size}; read by Recoil, reRECOIL v1 and reRECOIL v2",
            "Select or clear every Direct3D pack: rtexture packs of every mission and image.zbd, the interface images; read by Recoil, reRECOIL v1 and reRECOIL v2 (Alt+R)"),
        new(new() { Content = "_Software packs", IsEnabled = false }, PackCount(), PackCaption(), "Recoil's software mode only", "SoftwarePacks", "Software packs", m => m.SoftwarePacks,
            size => $"texture packs of every mission{size}; read only by the original Recoil in software mode; reRECOIL has no software renderer",
            "Select or clear every software pack: texture packs of every mission; read only by the original Recoil in software mode; reRECOIL has no software renderer (Alt+S)"),
        new(new() { Content = "_32-bit packs", IsEnabled = false }, PackCount(), PackCaption(), "reRECOIL v2 only", "ThirtyTwoBitPacks", "32-bit packs for reRECOIL v2", m => m.ThirtyTwoBitPacks,
            size => $"rgbatexture packs of every mission and rgbaimage.zbd{size}; only reRECOIL v2 reads them",
            "Select or clear every 32-bit pack: rgbatexture in every mission, and rgbaimage.zbd. Only reRECOIL v2 reads them; they keep the PNGs' exact pixels (Alt+3)"),
    ];
    private readonly ComboBox dithering = new() { IsEnabled = false };
    private readonly TextBlock ditheringCaption = Caption();
    /// <summary>Why the dithering could not be read or set, shown in its caption's place; null when it was.</summary>
    private string? ditheringProblem;
    private readonly TextBox destination = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock placeholder = new() { Text = "A folder outside the project", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(11, 0, 0, 0) };
    private readonly Button browse = new() { Width = 32, Height = 32, Padding = new(0), Margin = new(6, 0, 0, 0) };
    private readonly TextBlock destinationNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
    /// <summary>
    /// The destination note and caution last announced. Only a check of the boxes as they are, made while the list is ready,
    /// is announced (a problem always is), and only when it says something new.
    /// </summary>
    private string? announcedNote, announcedCaution;
    /// <summary>
    /// The caution under the note: larger packs left in the destination that the game would load instead; at most three lines
    /// (its tooltip and accessible name carry the whole text), so the panel fits at the window's minimum size.
    /// </summary>
    private readonly TextBlock destinationCaution = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0), MaxHeight = 48, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
    private readonly StackPanel summary = new();
    private readonly TextBlock summaryTitle = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock summaryDetail = new() { FontSize = 12, Margin = new(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 90 };
    private readonly Button export = new() { Content = new AccessText { Text = "_Export" }, IsDefault = true, MinWidth = 120, IsEnabled = false, Margin = new(8, 0, 0, 0) };
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
        MinWidth = MinimumWidth; MinHeight = MinimumHeight; ResizeMode = ResizeMode.CanResize;
        root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new() { Width = new(PanelGap) });
        root.ColumnDefinitions.Add(new() { Width = new(PanelWidth) });
        root.Children.Add(BuildLeft());
        var panel = BuildPanel(); Grid.SetColumn(panel, 2); root.Children.Add(panel);
        Content = root;
        DialogLayout.Constrain(this, new(DefaultWidth, DefaultHeight));

        allFiles.SetResourceReference(StyleProperty, "DefaultCheckBoxStyle"); Labeled(allFiles);
        AutomationProperties.SetName(allFiles, "All files"); AutomationProperties.SetAutomationId(allFiles, "AllFiles");
        allFiles.ToolTip = "Select or clear every file (Alt+A)";
        destination.Text = services.Destination ?? "";
        destination.TextChanged += (_, _) => { placeholder.Visibility = destination.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; ScheduleDestinationCheck(); UpdateState(); };
        placeholder.Visibility = destination.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        browse.Click += (_, _) => Browse();
        export.Click += async (_, _) => await ExportAsync();
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
            _ = LoadDitheringAsync();
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
        foreach (var (before, term, meaning) in new[] { ("", "gamez", " world · "), ("", "anim", " animations · "), ("", "zrdr", " resources · "), ("16-bit ", "rtexture", " Direct3D and "),
            ("", "texture", " software packs, and "), ("32-bit ", "rgbatexture", " packs for reRECOIL v2, by the MB of texture memory they are for; "), ("", "full", " keeps every texture at its authored size") })
        {
            Run bold = new(term) { FontWeight = FontWeights.SemiBold }; bold.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
            if (before.Length > 0) legend.Inlines.Add(new Run(before));
            legend.Inlines.Add(bold); legend.Inlines.Add(new Run(meaning));
        }
        return new SourceExportStack { Children = { card, legend } };
    }

    private UIElement BuildPanel()
    {
        Grid panel = new();
        foreach (var height in new GridLength[] { GridLength.Auto, new(16), GridLength.Auto, GridLength.Auto, GridLength.Auto, new(20), GridLength.Auto, GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            panel.RowDefinitions.Add(new() { Height = height });
        void Add(UIElement element, int row) { Grid.SetRow(element, row); panel.Children.Add(element); }
        Add(BuildPacks(), 0);
        dithering.SetResourceReference(StyleProperty, "DefaultComboBoxStyle");
        dithering.HorizontalContentAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(dithering, "Texture dithering"); AutomationProperties.SetAutomationId(dithering, "Dithering");
        foreach (var (label, tag, tip) in new[] { ("None", "none", "No dithering: round each colour independently and preserve 8-bit alpha"), ("Ordered", "ordered", "A fixed 4×4 pattern: smooth gradients that tile seamlessly"), ("Error diffusion", "diffusion", "Floyd–Steinberg: the smoothest gradients, as fine noise; tiling textures can show a seam") })
            dithering.Items.Add(new ComboBoxItem { Content = label, Tag = tag, ToolTip = tip });
        dithering.ToolTip = "How exports and checks dither colour and Direct3D alpha in the Direct3D and software packs, including image.zbd; software packs retain 8-bit alpha. The 32-bit packs keep the PNGs' exact pixels. Remembered in the project's hidden .zstudio folder";
        Add(Header("Texture _dithering", dithering), 2); Add(dithering, 3); Add(ditheringCaption, 4);
        AutomationProperties.SetLiveSetting(ditheringCaption, AutomationLiveSetting.Polite);
        Add(Header("Desti_nation", destination), 6);
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
        Add(destinationRow, 7);
        destinationCaution.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        AutomationProperties.SetLiveSetting(destinationNote, AutomationLiveSetting.Polite); AutomationProperties.SetLiveSetting(destinationCaution, AutomationLiveSetting.Polite);
        // Two lines are kept for the note and its caution together, so a one-line note is followed by the caution directly.
        Add(new StackPanel { MinHeight = 32, Children = { destinationNote, destinationCaution } }, 8);
        summaryDetail.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        summary.Children.Add(summaryTitle); summary.Children.Add(summaryDetail);
        // A panel has no automation peer: the title is the live region and carries the whole summary as its name.
        AutomationProperties.SetLiveSetting(summaryTitle, AutomationLiveSetting.Polite);
        Add(summary, 10);
        cancel.SetResourceReference(StyleProperty, "DefaultButtonStyle"); export.SetResourceReference(StyleProperty, "AccentButtonStyle");
        cancel.Height = export.Height = 32;
        AutomationProperties.SetAutomationId(cancel, "Cancel"); AutomationProperties.SetAutomationId(export, "Export");
        ToolTipService.SetShowOnDisabled(export, true);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0), Children = { cancel, export } };
        Add(buttons, 11);
        return panel;
    }

    /// <summary>
    /// Texture and image packs: the Direct3D, software and 32-bit pack boxes (tri-state sets of every pack of their kind, "N of
    /// M" while mixed), each with a caption saying which games read them and what they take. A tab stop each; Alt+R, Alt+S and
    /// Alt+3.
    /// </summary>
    private UIElement BuildPacks()
    {
        SourceExportGroup group = new();
        AutomationProperties.SetName(group, "Texture and image packs"); AutomationProperties.SetAutomationId(group, "Packs");
        group.Children.Add(new TextBlock { Text = "Texture and image packs", FontSize = 14, Margin = new(0, 0, 0, 8) });
        foreach (var pack in packs)
        {
            var (box, count, caption) = (pack.Box, pack.Count, pack.Caption);
            box.SetResourceReference(StyleProperty, "DefaultCheckBoxStyle"); Labeled(box);
            box.VerticalAlignment = VerticalAlignment.Center; box.ToolTip = pack.Tip;
            AutomationProperties.SetName(box, pack.Name); AutomationProperties.SetAutomationId(box, pack.Id);
            ShowPackSize(pack, null);
            DockPanel row = new() { Height = 22, LastChildFill = false };
            DockPanel.SetDock(count, Dock.Right); row.Children.Add(count); row.Children.Add(box);
            // While the box is disabled (reading, nothing to build, an error, no pack of its kind), its count and caption
            // dim with its label instead of reading as live text.
            box.IsEnabledChanged += (_, _) => DimPack(box, count, caption);
            DimPack(box, count, caption);
            group.Children.Add(new StackPanel { Margin = new(0, pack == packs[0] ? 0 : 6, 0, 0), Children = { row, caption } });
        }
        return group;
    }
    /// <summary>A pack box's caption and help text with the estimated size of its files, or without a size (<paramref name="bytes"/> null).</summary>
    private static void ShowPackSize(PackBox pack, long? bytes)
    {
        string size = bytes is long known ? SourceExportSelection.Size(known) : "";
        pack.Caption.Text = size.Length > 0 ? $"{pack.Games} · about {size}" : pack.Games;
        AutomationProperties.SetHelpText(pack.Box, pack.Help(size.Length > 0 ? $", about {size}" : ""));
    }
    /// <summary>A pack box's count and caption in the tertiary text colour while the box is enabled, the disabled one while it is not.</summary>
    private static void DimPack(CheckBox box, TextBlock count, TextBlock caption)
    {
        string brush = box.IsEnabled ? "TextFillColorTertiaryBrush" : "TextFillColorDisabledBrush";
        count.SetResourceReference(TextBlock.ForegroundProperty, brush); caption.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }
    private static TextBlock PackCount()
    {
        TextBlock count = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        count.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        Typography.SetNumeralAlignment(count, FontNumeralAlignment.Tabular);
        return count;
    }
    private static TextBlock PackCaption()
    {
        TextBlock caption = new() { FontSize = 12, Margin = new(28, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return caption;
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
        foreach (var pack in packs)
        {
            var set = pack.Set(model);
            pack.Box.SetBinding(BoxChecked, new Binding(nameof(SourceExportSet.IsChecked)) { Source = set, Mode = BindingMode.TwoWay });
            pack.Count.SetBinding(TextBlock.TextProperty, new Binding(nameof(SourceExportSet.Count)) { Source = set });
            ShowPackSize(pack, SourceExportSelection.EstimatedBytes(set));
        }
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
        ClearPacks();
        string text = message.Length <= 1024 ? message : message[..1024] + "…";
        StackPanel failure = new() { Margin = new(0, 6, 0, 0) };
        failure.Children.Add(InfoBar(error: true, "Can’t list the game files.", text, "Try again", () => _ = LoadAsync(Phase.Reading)));
        failure.Children.Add(Message(null, "Fix the file and choose Try again.", 220));
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
        // A pack box is usable while it has files, as All files is.
        foreach (var pack in packs) pack.Box.IsEnabled = value == Phase.Ready && Selection is { } shown && pack.Set(shown).Files.Count > 0;
        rereading.Visibility = value == Phase.Rereading ? Visibility.Visible : Visibility.Collapsed;
        allCount.Visibility = value == Phase.Ready ? Visibility.Visible : Visibility.Collapsed;
        sharedRow.Visibility = sharedDivider.Visibility = loaded && shared != null ? Visibility.Visible : Visibility.Collapsed;
        legend.Visibility = loaded && grid != null ? Visibility.Visible : Visibility.Collapsed;
        if (!loaded && value != Phase.Failed) { BindingOperations.ClearBinding(allFiles, BoxChecked); allFiles.IsChecked = false; ClearPacks(); }
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

    /// <summary>The grid sized its columns: the shared row's header follows, and the block's width follows the grid's maximum (its tail column is known once it is loaded).</summary>
    private void ApplyWidths(double rowHeader)
    {
        shared?.SetHeaderWidth(rowHeader);
        if (grid != null && phase is Phase.Ready or Phase.Rereading) root.MaxWidth = grid.MaximumWidth + 2 + 18 + PanelGap + PanelWidth;
    }
    /// <summary>The pack boxes while no plan is shown: clear, without counts, their captions and help texts without sizes.</summary>
    private void ClearPacks()
    {
        foreach (var pack in packs)
        {
            BindingOperations.ClearBinding(pack.Box, BoxChecked); pack.Box.IsChecked = false;
            BindingOperations.ClearBinding(pack.Count, TextBlock.TextProperty); pack.Count.Text = "";
            ShowPackSize(pack, null);
        }
    }

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
        if (text != null) LiveRegionChanged(text);
    }
    /// <summary>
    /// Tells screen readers that a live region's text changed (WPF raises no such event by itself), once layout has run: an
    /// element just attached or made visible is not yet in the tree a screen reader sees.
    /// </summary>
    private static void LiveRegionChanged(UIElement element) =>
        element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Not for a window closed (Export) or an element hidden again in the meantime.
            if (PresentationSource.FromVisual(element) == null || !element.IsVisible) return;
            (UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });

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
            LiveRegionChanged(summaryTitle);
        }
        if (loaded) ScheduleDestinationCheck();
        ShowDitheringCaption();
        UpdateState();
    }

    private void UpdateState()
    {
        if (lifetime.IsCancellationRequested) return;
        var model = Selection;
        stale = phase == Phase.Ready && model != null && !model.IsCurrent();
        block = services.Blocker?.Invoke();
        string key = $"{phase}|{stale}|{block?.Message}";
        if (key != stateKey)
        {
            stateKey = key;
            UIElement? bar = stale ? InfoBar(false, null, "The project changed after this list was read.", "Read again", ReadAgain)
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
            Phase.Rereading => "The list of game files is being read again",
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

    // ---------- texture dithering ----------

    private async Task LoadDitheringAsync()
    {
        if (services.ReadDithering == null) return;
        try
        {
            ditheringChoice = await services.ReadDithering(lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            ditheringProblem = null; ShowDitheringCaption();
            SelectDithering(ditheringChoice);
            dithering.IsEnabled = services.SetDithering != null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { ditheringProblem = ex.Message; ShowDitheringCaption(); }
    }
    private void SelectDithering(TextureDithering? value)
    {
        bool wasQuiet = quiet; quiet = true;
        try { dithering.SelectedItem = value is { } choice ? dithering.Items.OfType<ComboBoxItem>().FirstOrDefault(i => SourceProjectSettings.Parse((string)i.Tag) == choice) : null; }
        finally { quiet = wasQuiet; }
    }
    /// <summary>
    /// The dithering's caption: a settings problem when there is one; otherwise its scope, the Direct3D and software packs, and
    /// that it changes nothing while neither is selected. One line either way, so the panel does not move. A polite live region.
    /// </summary>
    private void ShowDitheringCaption()
    {
        string? problem = ditheringProblem;
        bool none = phase is Phase.Ready or Phase.Rereading && Selection is { SixteenBitPacks: { Files.Count: > 0, Selected: 0 } };
        string text = problem != null ? problem.Length <= 512 ? problem : problem[..512] + "…"
            : none ? "Nothing to dither: no Direct3D or software pack." : "Only Direct3D and software packs are dithered.";
        ditheringCaption.SetResourceReference(TextBlock.ForegroundProperty, problem != null ? "SystemFillColorCriticalBrush" : "TextFillColorTertiaryBrush");
        ditheringCaption.Visibility = Visibility.Visible;
        if (ditheringCaption.Text == text) return;
        ditheringCaption.Text = text;
        LiveRegionChanged(ditheringCaption);
    }
    private Task DitheringChangedAsync()
    {
        if (quiet || services.SetDithering is not { } set) return Task.CompletedTask;
        ditheringSeen = services.DitheringRequests?.Invoke() ?? 0;
        if (ditheringApply is { IsCompleted: false }) return Task.CompletedTask;
        return ditheringApply = ApplyDitheringAsync(set);
    }
    /// <summary>
    /// One choice is written at a time, the last one shown is the one kept, closing the window does not cancel it, and a
    /// choice made elsewhere after this window's last one wins (the window then shows it).
    /// </summary>
    private async Task ApplyDitheringAsync(Func<TextureDithering, CancellationToken, Task> set)
    {
        while (dithering.SelectedItem is ComboBoxItem { Tag: string name } && SourceProjectSettings.Parse(name) is { } value && value != ditheringChoice)
        {
            if (services.DitheringRequests is { } requests && requests() != ditheringSeen) { if (!lifetime.IsCancellationRequested) await LoadDitheringAsync(); return; }
            try
            {
                Task write = set(value, CancellationToken.None);
                ditheringSeen = services.DitheringRequests?.Invoke() ?? 0;
                await write; ditheringChoice = value; ditheringProblem = null; ShowDitheringCaption();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { SelectDithering(ditheringChoice); ditheringProblem = ex.Message; ShowDitheringCaption(); return; }
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
        ShowDestinationNote(result, current: version == selectionVersion);
        UpdateState();
        return result;
    }

    /// <summary>
    /// Shows a destination check's note and caution. <paramref name="current"/>: the check saw the boxes as they are now; an
    /// older one (begun while the window read, or before a box changed) is shown but not announced, since a newer check follows.
    /// </summary>
    private void ShowDestinationNote(SourceExportDestination state, bool current)
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
        bool settled = current && phase == Phase.Ready;
        if ((settled || state.Problem != null) && destinationNote.Text != announcedNote) { announcedNote = destinationNote.Text; LiveRegionChanged(destinationNote); }
        // Before export, a larger pack left in the destination that the game would load instead of an exported one. Several
        // name two packs: with paths that wrap whole, a third would take the caution past its three lines.
        string caution = state.Problem != null || phase != Phase.Ready || state.ShadowCount == 0 ? ""
            : state.ShadowCount == 1 && state.Shadows is [var one]
                ? $"{one.Path[..one.Path.IndexOf('/')]} also holds {one.Path[(one.Path.IndexOf('/') + 1)..]}, which this export doesn't replace: {one.Game} would load it instead of {one.Instead[(one.Instead.IndexOf('/') + 1)..]}."
                : $"{state.ShadowCount:N0} larger packs here would be loaded instead of the exported ones: {Listed(state.Shadows.Take(2).Select(s => s.Path).ToList(), state.ShadowCount)}.";
        destinationCaution.Visibility = caution.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Shown with a word joiner (U+2060) after each "/", so "m6/rgbatexture512.zbd" wraps whole; the tooltip and the
        // accessible name keep the plain text.
        string shown = caution.Replace("/", "/\u2060", StringComparison.Ordinal);
        if (destinationCaution.Text != shown)
        {
            destinationCaution.Text = shown;
            destinationCaution.ToolTip = caution.Length > 0 ? caution : null; AutomationProperties.SetName(destinationCaution, caution);
        }
        // A caution the window hid only while it read again is not announced again; one cleared by a change of the boxes or the folder is.
        if (caution.Length > 0 && settled && caution != announcedCaution) { announcedCaution = caution; LiveRegionChanged(destinationCaution); }
        else if (caution.Length == 0 && (settled || state.Problem != null)) announcedCaution = null;
    }

    /// <summary>
    /// The named paths as an English list ("a and b", "a, b and c"), or, when <paramref name="count"/> is larger than the
    /// names given, the names and how many more ("a, b, c and 2 more").
    /// </summary>
    private static string Listed(IReadOnlyList<string> names, int count) =>
        count > names.Count ? $"{string.Join(", ", names)} and {count - names.Count:N0} more"
        : names.Count < 2 ? string.Join("", names)
        : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

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

/// <summary>A panel exposed to UI Automation as a named group (the panel's Texture and image packs).</summary>
internal sealed class SourceExportGroup : StackPanel
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
    private sealed class GroupPeer(SourceExportGroup owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(SourceExportGroup);
    }
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
