using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using zStudio.Automation;
using zStudio.Core.Formats;

namespace zStudio.Desktop;

/// <summary>
/// ZMAP mode's chrome, a layout of the source map editor (design/zmap-editor-ui.md): the command bar's scope slot becomes
/// Exit ZMAP with the file's title block, the Outlines pane takes the World tree's place, the selected outline's card the
/// Properties pane's, and chips over the locked Top view name the view, the active tool and the last message.
/// </summary>
public partial class MainWindow
{
    private TextBlock zmapBannerDot = null!, zmapHintBelow = null!, zmapCaption = null!, zmapOutlineCount = null!, zmapOutlinesEmptyTitle = null!, zmapOutlinesEmptyText = null!,
        zmapCardHeading = null!, zmapCardSubtitle = null!, zmapHeroTitle = null!, zmapHeroText = null!, zmapDrawCount = null!,
        zmapBannerTitle = null!, zmapFooterStatus = null!, zmapVertexHint = null!;
    private Border zmapEdited = null!, zmapCardTile = null!, zmapColorSwatch = null!, zmapFooter = null!;
    private FrameworkElement zmapOutlinesEmpty = null!, zmapHero = null!, zmapSummary = null!;
    private Grid zmapSummaryTable = null!;
    private CardSection zmapDrawSection = null!, zmapSplitSection = null!, zmapOutlineSection = null!, zmapVertexSection = null!;
    private Button zmapAddOutline = null!, zmapEmptyOpen = null!, zmapEmptyNew = null!, zmapEmptyDraw = null!, zmapCardFrame = null!, zmapCardClose = null!,
        zmapPreviousVertex = null!, zmapNextVertex = null!, zmapFrameVertex = null!;
    private ViewportChip zmapBanner = null!, zmapMessageChip = null!, zmapViewChip = null!;
    private PreviewIcon zmapBannerIcon = null!, zmapMessageIcon = null!;
    private StackPanel zmapBannerKeys = null!;
    private System.Windows.Shapes.Ellipse zmapFooterDot = null!;
    private string? zmapBannerShown, zmapBannerSaid, zmapSummaryShown, zmapTileShown;
    /// <summary>The narrowest the outline's card gets (the Properties pane's own minimum is 260): its full-width pickers then show "172 · X, Z" whole beside the card's scrollbar.</summary>
    private const double ZmapCardMinimum = 300;

    // ---------------------------------------------------------------- the command bar

    private void BuildZmapCommandBar()
    {
        WrapPanel items = new() { VerticalAlignment = VerticalAlignment.Center };
        // The scope slot, as Animation mode's: Exit, then the file's title block with its Edited pill.
        StackPanel exitContent = new() { Orientation = Orientation.Horizontal };
        exitContent.Children.Add(new PreviewIcon { Kind = "Back", Width = 14, Height = 14, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        exitContent.Children.Add(new TextBlock { Text = "Exit ZMAP", VerticalAlignment = VerticalAlignment.Center });
        Button exit = new() { Content = exitContent, Height = 32, Padding = new(10, 0, 14, 0), Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Return to the map layout. Unsaved ZMAP changes are asked about first." };
        AutomationProperties.SetName(exit, "Exit ZMAP");
        exit.Click += (_, _) => ZmapUiAsync(() => ResolveZmapExitAsync());
        zmapName = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 };
        StackPanel pill = new() { Orientation = Orientation.Horizontal };
        System.Windows.Shapes.Ellipse dot = new() { Width = 6, Height = 6, Margin = new(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentFillColorDefaultBrush");
        TextBlock edited = new() { Text = "Edited", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        edited.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        pill.Children.Add(dot); pill.Children.Add(edited);
        zmapEdited = new() { Child = pill, CornerRadius = new(10), Height = 20, Padding = new(8, 0, 8, 0), Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "The ZMAP has unsaved changes", Visibility = Visibility.Collapsed };
        zmapEdited.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        AutomationProperties.SetName(zmapEdited, "Edited");
        StackPanel nameLine = new() { Orientation = Orientation.Horizontal };
        nameLine.Children.Add(zmapName); nameLine.Children.Add(zmapEdited);
        zmapCaption = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 };
        zmapCaption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        StackPanel title = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 0, 0) };
        title.Children.Add(nameLine); title.Children.Add(zmapCaption);
        StackPanel scope = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 3, 0, 3) };
        scope.Children.Add(exit); scope.Children.Add(title);
        AutomationProperties.SetName(scope, "ZMAP mode");
        items.Children.Add(scope);
        items.Children.Add(Divider());

        var file = Strip();
        Command(file, "NewFile", "New ZMAP", "New ZMAP (Ctrl+N) — an empty HUD map as large as this map", () => ZmapUiAsync(() => ChangeZmapUiAsync(null, true)));
        Command(file, "OpenFile", "Open ZMAP", "Open ZMAP… (Ctrl+O)", () => ZmapUiAsync(OpenZmapUiAsync));
        zmapSave = Command(file, "Save", "Save", "Save (Ctrl+S)", () => ZmapUiAsync(() => SaveZmapUiAsync(false)));
        zmapSaveAs = Command(file, "SaveAs", "Save As", "Save As… (Ctrl+Shift+S) — a copy that becomes the open file", () => ZmapUiAsync(() => SaveZmapUiAsync(true)));
        items.Children.Add(Divider());
        var history = Strip();
        zmapUndo = Command(history, "Undo", "Undo", "Undo (Ctrl+Z)", () => EditZmap(() => ChangeZmapHistory(false)));
        zmapRedo = Command(history, "Redo", "Redo", "Redo (Ctrl+Y)", () => EditZmap(() => ChangeZmapHistory(true)));
        items.Children.Add(Divider());

        // The tool and the camera, in labelled groups as the map layout's.
        var draw = Group("Draw");
        zmapDraw = new() { Content = new PreviewIcon { Kind = "DrawOutline", Width = 16, Height = 16 }, ToolTip = "Draw outline — click points in the view; Enter finishes, Esc cancels" };
        zmapDraw.SetResourceReference(StyleProperty, "CommandToggle");
        AutomationProperties.SetName(zmapDraw, "Draw outline");
        // From its checked state, so UI Automation's Toggle works as a click.
        RoutedEventHandler toggled = (_, _) =>
        {
            if (zmapRefreshing) return;
            EditZmap(() => { if (zmapDraw.IsChecked != true) { if (zmapDrawing != null) CancelZmapDraft(); } else if (zmapDrawing == null) BeginZmapDraw(); });
        };
        zmapDraw.Checked += toggled; zmapDraw.Unchecked += toggled;
        draw.Children.Add(zmapDraw);
        var camera = Group("Camera");
        Command(camera, "Frame", "Frame map", "Frame map — fit the visible map in the view (Home)", () => EditZmap(() => FrameZmap("map")), "CommandGroupButton");
        // Also while another outline is drawn or this one is split, when the card shows that draft instead.
        zmapFrameOutline = Command(camera, "FrameSelection", "Frame outline", "Frame outline — fit the selected outline in the view (numpad . frames its selected vertex, else the outline)",
            () => EditZmap(() => FrameZmap("outline")), "CommandGroupButton");
        DockPanel bar = new();
        bar.Children.Add(items);
        ZmapBar.Content = bar;

        StackPanel Strip()
        {
            StackPanel strip = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 3, 0, 3) };
            items.Children.Add(strip);
            return strip;
        }
        Border Divider() { Border line = new(); line.SetResourceReference(StyleProperty, "CommandSeparator"); return line; }
        StackPanel Group(string label)
        {
            StackPanel row = new() { Orientation = Orientation.Horizontal };
            TextBlock caption = new() { Text = label };
            caption.SetResourceReference(StyleProperty, "CommandGroupLabel");
            StackPanel commands = new() { Orientation = Orientation.Horizontal };
            row.Children.Add(caption); row.Children.Add(commands);
            Border group = new() { Child = row };
            group.SetResourceReference(StyleProperty, "CommandGroup");
            AutomationProperties.SetName(group, label);
            items.Children.Add(group);
            return commands;
        }
        static Button Command(Panel parent, string icon, string name, string tip, Action action, string style = "CommandButton")
        {
            Button button = new() { Content = new PreviewIcon { Kind = icon, Width = 16, Height = 16 }, ToolTip = tip };
            button.SetResourceReference(StyleProperty, style);
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => action();
            parent.Children.Add(button);
            return button;
        }
    }

    // ---------------------------------------------------------------- the Outlines pane

    private void BuildZmapOutlinesPane()
    {
        DockPanel pane = new();
        DockPanel header = new() { Margin = new(14, 8, 8, 4), MinHeight = 30 };
        zmapAddOutline = ZmapIconButton("Add", "CommandButton", "Draw outline", "Draw outline — click points in the view; Enter finishes", () => EditZmap(BeginZmapDraw));
        zmapAddOutline.Width = zmapAddOutline.Height = 30;
        DockPanel.SetDock(zmapAddOutline, Dock.Right); header.Children.Add(zmapAddOutline);
        header.Children.Add(new TextBlock { Text = "Outlines", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        zmapOutlineCount = new() { FontSize = 12, Margin = new(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        zmapOutlineCount.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        header.Children.Add(zmapOutlineCount);
        DockPanel.SetDock(header, Dock.Top); pane.Children.Add(header);
        var terrain = BuildZmapTerrainSection();
        DockPanel.SetDock(terrain, Dock.Bottom); pane.Children.Add(terrain);
        // The Outlines list keeps its room in a short pane: the terrain rows take at most two fifths of it, in whole rows
        // (two to seven), and scroll.
        pane.SizeChanged += (_, e) => zmapTerrainScroll.MaxHeight = Math.Clamp(Math.Floor(e.NewSize.Height * .4 / 28) * 28, 56, 196);

        zmapOutlines = new()
        {
            ItemTemplate = (DataTemplate)ZmapOutlinesPane.FindResource("ZmapOutlineTemplate"), SelectedValuePath = nameof(ZmapOutlineRow.Id),
            Margin = new(6, 0, 6, 8), Padding = new(0), BorderThickness = new(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        zmapOutlines.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "OutlineListRow");
        zmapOutlines.Resources.Add(typeof(ScrollViewer), FindResource("OutlinerScrollStyle"));
        ScrollViewer.SetHorizontalScrollBarVisibility(zmapOutlines, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetVirtualizationMode(zmapOutlines, VirtualizationMode.Recycling);
        AutomationProperties.SetName(zmapOutlines, "ZMAP outlines");
        // A row's Frame: select it and frame it, as the World tree's.
        zmapOutlines.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not Button { Tag: Guid id }) return;
            e.Handled = true;
            EditZmap(() =>
            {
                // Not while a file is read or written (the other Frame buttons are disabled then); an unapplied field
                // draft of the selected outline does not stand in the way of framing it.
                if (zmapBusy) throw new StudioCommandException("busy", "A ZMAP file operation is in progress.");
                if (zmap!.Outline != id) { RequireZmapIdle(); zmap.Select(id, null); }
                FrameZmap("outline");
            });
        }));

        // What the list says while it has nothing to list, with the commands that fill it.
        zmapOutlinesEmptyTitle = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        zmapOutlinesEmptyText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 10) };
        zmapOutlinesEmptyText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        WrapPanel choices = new();
        zmapEmptyOpen = ZmapTextButton("OpenFile", "Open ZMAP…", "Open a version-5 .zmap file (Ctrl+O)", () => ZmapUiAsync(OpenZmapUiAsync));
        zmapEmptyOpen.SetResourceReference(StyleProperty, "AccentButtonStyle");
        zmapEmptyNew = ZmapTextButton("NewFile", "New ZMAP", "Start an empty HUD map as large as this map (Ctrl+N)", () => ZmapUiAsync(() => ChangeZmapUiAsync(null, true)));
        zmapEmptyDraw = ZmapTextButton("DrawOutline", "Draw outline", "Click points in the view; Enter finishes, Esc cancels", () => EditZmap(BeginZmapDraw));
        choices.Children.Add(zmapEmptyOpen); choices.Children.Add(zmapEmptyNew); choices.Children.Add(zmapEmptyDraw);
        StackPanel empty = new() { Margin = new(14, 6, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        empty.Children.Add(zmapOutlinesEmptyTitle); empty.Children.Add(zmapOutlinesEmptyText); empty.Children.Add(choices);
        zmapOutlinesEmpty = empty;
        Grid body = new();
        body.Children.Add(zmapOutlines); body.Children.Add(empty);
        pane.Children.Add(body);
        ZmapOutlinesContent.Content = pane;
    }

    // ---------------------------------------------------------------- the outline's card

    private void BuildZmapCard()
    {
        // The title row: a tile in the outline's colour, its name and what it is, Frame and Close.
        DockPanel title = new() { Margin = new(0, 0, 0, 2) };
        zmapCardClose = ZmapIconButton("Close", "CardCloseButton", "Clear selection", "Clear selection (Esc)", () => EditZmap(() => { RequireZmapIdle(); zmap!.Select(null, null); }));
        DockPanel.SetDock(zmapCardClose, Dock.Right); title.Children.Add(zmapCardClose);
        zmapCardFrame = ZmapIconButton("FrameSelection", "CardIconButton", "Frame outline", "Frame this outline in the view", () => EditZmap(() => FrameZmap("outline")));
        DockPanel.SetDock(zmapCardFrame, Dock.Right); title.Children.Add(zmapCardFrame);
        zmapCardTile = new() { Width = 34, Height = 34, CornerRadius = new(6), BorderThickness = new(1), Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        zmapCardTile.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        zmapCardTile.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        DockPanel.SetDock(zmapCardTile, Dock.Left); title.Children.Add(zmapCardTile);
        zmapCardHeading = new() { FontWeight = FontWeights.SemiBold, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
        zmapCardSubtitle = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        zmapCardSubtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        StackPanel names = new() { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(zmapCardHeading); names.Children.Add(zmapCardSubtitle);
        title.Children.Add(names);

        StackPanel body = new();
        // Nothing selected: the hint, then the file's summary (as the Properties pane's World summary).
        StackPanel hero = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new(20, 22, 20, 18) };
        Grid art = new() { Width = 56, Height = 56, Margin = new(0, 0, 0, 14) };
        System.Windows.Shapes.Rectangle dashes = new() { RadiusX = 12, RadiusY = 12, StrokeThickness = 1.5, StrokeDashArray = [4, 3] };
        dashes.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextFillColorDisabledBrush");
        var pointer = new PreviewIcon { Kind = "Pointer", Width = 24, Height = 24 };
        pointer.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
        art.Children.Add(dashes); art.Children.Add(pointer);
        hero.Children.Add(art);
        zmapHeroTitle = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        zmapHeroText = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 250, Margin = new(0, 6, 0, 0) };
        zmapHeroText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        hero.Children.Add(zmapHeroTitle); hero.Children.Add(zmapHeroText);
        zmapHero = hero; body.Children.Add(hero);
        zmapSummaryTable = new() { Margin = new(8, 0, 8, 6) };
        zmapSummaryTable.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); zmapSummaryTable.ColumnDefinitions.Add(new());
        StackPanel summaryRows = new() { Margin = new(4, 0, 4, 6) };
        summaryRows.Children.Add(new TextBlock { Text = "HUD map", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new(8, 10, 8, 4) });
        summaryRows.Children.Add(zmapSummaryTable);
        Border summary = new() { Child = summaryRows, CornerRadius = new(6), BorderThickness = new(1), Margin = new(0, 0, 0, 6) };
        summary.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        summary.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        zmapSummary = summary; body.Children.Add(summary);

        // While an outline is drawn.
        zmapDrawSection = new("New outline");
        zmapDrawCount = ZmapPlain("");
        zmapDrawSection.Body.Children.Add(ZmapRow("Vertices", zmapDrawCount));
        zmapDrawSection.Body.Children.Add(ZmapHelp("Click in the view to place each vertex. Enter finishes the outline and Esc cancels it. It starts white with no objective; set both once it is finished.", 0));
        body.Children.Add(zmapDrawSection);

        // While an outline is split: its two endpoints.
        zmapSplitSection = new("Split outline");
        InitializeZmapPathControls(zmapSplitSection.Body);
        body.Children.Add(zmapSplitSection);

        // The outline: its authored colour and its objective.
        zmapOutlineSection = new("Outline");
        // ValueTextBox: Fluent's clear button would take the value's room while the box has the focus.
        zmapColor = new ValueTextBox { MaxLength = 7, Height = 32, Padding = new(28, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "The outline's authored colour, as #RRGGBB" };
        AutomationProperties.SetName(zmapColor, "Colour (#RRGGBB)");
        // The swatch sits inside the box, as an axis letter does, so the box keeps the row's width for the value: a narrow
        // pane still shows the whole hex.
        zmapColorSwatch = new() { Width = 14, Height = 14, CornerRadius = new(3), BorderThickness = new(1), Margin = new(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        // A stroke that shows on both themes: authored colours are often white.
        zmapColorSwatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrongStrokeColorDefaultBrush");
        Grid colour = ZmapWithHint(zmapColor, "#RRGGBB", 28);
        colour.Children.Add(zmapColorSwatch);
        zmapOutlineSection.Body.Children.Add(ZmapField("Colour", colour));
        zmapObjectives = new() { DisplayMemberPath = nameof(ZmapChoice.Label), SelectedValuePath = nameof(ZmapChoice.Value), Height = 32 };
        AutomationProperties.SetName(zmapObjectives, "Objective");
        zmapOutlineSection.Body.Children.Add(ZmapField("Objective", zmapObjectives));
        zmapOutlineSection.Body.Children.Add(ZmapHelp("The game recolours an objective's outlines while it is active and once it is completed.", 0));
        WrapPanel outlineActions = new() { Margin = new(0, 8, 0, 0) };
        zmapSplitButton = ZmapTextButton("SplitOutline", "Split outline", "Cut this outline in two between two of its vertices", () => EditZmap(BeginZmapSplit));
        zmapRemoveOutline = ZmapTextButton("Delete", "Remove outline", "Remove this outline and its vertices", () => EditZmap(RemoveSelectedZmapOutline));
        outlineActions.Children.Add(zmapSplitButton); outlineActions.Children.Add(zmapRemoveOutline);
        zmapOutlineSection.Body.Children.Add(outlineActions);
        body.Children.Add(zmapOutlineSection);

        // Its vertices: the selected one's exact position, and the edits that change their number.
        zmapVertexSection = new("Vertices");
        zmapVertices = new() { DisplayMemberPath = nameof(ZmapVertexRow.Label), SelectedValuePath = nameof(ZmapVertexRow.Id), Height = 32,
            ToolTip = "The vertex: its number, then its X and Z" };
        AutomationProperties.SetName(zmapVertices, "Vertex");
        zmapVertexHint = new() { Text = "Click a vertex or choose one", IsHitTestVisible = false, Margin = new(11, 0, 30, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        zmapVertexHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        zmapPreviousVertex = ZmapIconButton("ChevronLeft", "CardIconButton", "Previous vertex", "Previous vertex", () => StepZmapVertex(-1));
        zmapNextVertex = ZmapIconButton("ChevronRight", "CardIconButton", "Next vertex", "Next vertex", () => StepZmapVertex(1));
        zmapFrameVertex = ZmapIconButton("FrameSelection", "CardIconButton", "Frame vertex", "Frame the selected vertex in the view", () => EditZmap(() => FrameZmap("vertex")));
        zmapPreviousVertex.Width = zmapNextVertex.Width = zmapFrameVertex.Width = 26;
        // The picker and the position take the card's width: coordinates are read and typed in full here. Previous, next
        // and Frame vertex sit in a row of their own under the picker, at its right.
        Grid picker = new() { Margin = new(0, 3, 0, 3) };
        picker.Children.Add(zmapVertices); picker.Children.Add(zmapVertexHint);
        zmapVertexSection.Body.Children.Add(picker);
        StackPanel steps = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 0, 0, 3) };
        steps.Children.Add(zmapPreviousVertex); steps.Children.Add(zmapNextVertex); steps.Children.Add(zmapFrameVertex);
        zmapVertexSection.Body.Children.Add(steps);
        Grid axes = new() { Margin = new(0, 3, 0, 3) };
        zmapX = Axis(0); zmapY = Axis(1); zmapZ = Axis(2);
        // Stacked, each at the card's width, so a coordinate shows whole; side by side only once every box has room for
        // one (about 80 DIP of text after the axis letter and the box's padding: a pane wider than its default). The same
        // boxes move, so a draft, the caret and the tab order stay.
        axes.SizeChanged += (_, e) =>
        {
            bool stack = e.NewSize.Width < 348;
            if (stack == (axes.ColumnDefinitions.Count == 1)) return;
            axes.ColumnDefinitions.Clear(); axes.RowDefinitions.Clear();
            for (int i = 0; i < (stack ? 1 : 3); i++) axes.ColumnDefinitions.Add(new());
            for (int i = 0; i < (stack ? 3 : 1); i++) axes.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int i = 0; i < 3; i++)
            {
                var holder = (FrameworkElement)axes.Children[i];
                Grid.SetColumn(holder, stack ? 0 : i); Grid.SetRow(holder, stack ? i : 0);
                holder.Margin = stack ? new(0, i == 0 ? 0 : 3, 0, 0) : new(i == 0 ? 0 : 3, 0, 0, 0);
            }
        };
        zmapVertexSection.Body.Children.Add(axes);
        zmapVertexSection.Body.Children.Add(ZmapHelp("Drag a vertex in the view to move it. Hold Ctrl to snap it to a map vertex.", 0));
        WrapPanel vertexActions = new() { Margin = new(0, 8, 0, 0) };
        zmapInsertButton = ZmapTextButton("InsertVertex", "Insert vertex", "Add a vertex on an edge of this outline: click the edge in the view", () => EditZmap(() =>
        {
            RequireZmapIdle();
            zmapInsert = true;
            zmap!.Touch();
            ZmapLayer.Viewport?.FocusView();
        }));
        zmapRemove = ZmapTextButton("RemoveVertex", "Remove vertex", "Remove the selected vertex (Delete)", () => EditZmap(RemoveZmapVertex));
        vertexActions.Children.Add(zmapInsertButton); vertexActions.Children.Add(zmapRemove);
        zmapVertexSection.Body.Children.Add(vertexActions);
        body.Children.Add(zmapVertexSection);

        // The footer, as every card's: the draft's state, Revert and Apply.
        zmapApply = ZmapIconButton("Apply", "AccentButtonStyle", "Apply changes", "Apply (Enter in a text box)", () => EditZmap(ApplyZmapFields));
        zmapRevert = ZmapIconButton("Discard", "DefaultButtonStyle", "Revert changes", "Revert (Esc)", () => EditZmap(CancelZmapDraft));
        foreach (var button in new[] { zmapApply, zmapRevert }) { button.Width = 36; button.Height = 30; button.Margin = new(6, 0, 0, 0); button.IsEnabled = false; }
        DockPanel foot = new();
        DockPanel.SetDock(zmapApply, Dock.Right); DockPanel.SetDock(zmapRevert, Dock.Right);
        foot.Children.Add(zmapApply); foot.Children.Add(zmapRevert);
        zmapFooterDot = new() { Width = 6, Height = 6, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        zmapFooterStatus = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        zmapFooterStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        StackPanel state = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        state.Children.Add(zmapFooterDot); state.Children.Add(zmapFooterStatus);
        foot.Children.Add(state);
        zmapFooter = new() { Child = foot, Padding = new(14, 6, 10, 6), BorderThickness = new(0, 1, 0, 0), Margin = new(-12, 4, -8, -10) };
        zmapFooter.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        Grid layout = new();
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(title);
        ScrollViewer scroll = new() { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 6, 0, 0) };
        scroll.SetResourceReference(Control.TemplateProperty, "InspectionScrollTemplate");
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        Grid.SetRow(zmapFooter, 2); layout.Children.Add(zmapFooter);
        Border card = new() { Child = layout, Padding = new(12, 12, 8, 10) };
        AutomationProperties.SetName(card, "Outline properties");
        ZmapPaneContent.Content = card;

        // An axis box with its letter inside, coloured like the handles (the object card's layout).
        TextBox Axis(int axis)
        {
            string letter = "XYZ"[axis].ToString();
            axes.ColumnDefinitions.Add(new());
            ValueTextBox box = new() { MaxLength = 32, Height = 32, Padding = new(20, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(box, letter);
            Grid holder = new() { Margin = new(axis == 0 ? 0 : 3, 0, 0, 0) };
            holder.Children.Add(box);
            holder.Children.Add(new TextBlock { Text = letter, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = FieldEditor.AxisBrushes[axis], Margin = new(8, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
            Grid.SetColumn(holder, axis); axes.Children.Add(holder);
            return box;
        }
    }

    // ---------------------------------------------------------------- the chips over the view

    private void BuildZmapViewChrome()
    {
        // The view is locked: say which one it is where the map layout's view menu sits.
        StackPanel view = new() { Orientation = Orientation.Horizontal };
        view.Children.Add(new TextBlock { Text = "Top", VerticalAlignment = VerticalAlignment.Center });
        TextBlock locked = new() { Text = "· Orthographic · locked", Margin = new(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        locked.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        view.Children.Add(locked);
        zmapViewChip = new() { Content = view, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(10), IsHitTestVisible = false, Focusable = false };
        AutomationProperties.SetName(zmapViewChip, "Top view, orthographic, locked");
        ZmapChrome.Children.Add(zmapViewChip);

        // The active tool's banner, as the polygon zone tool's: what it does next, the keys, its actions.
        zmapBannerIcon = new() { Width = 15, Height = 15, Margin = new(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        zmapBannerIcon.SetResourceReference(TextElement.ForegroundProperty, "AccentFillColorDefaultBrush");
        zmapBannerTitle = BannerText("", bold: true);
        zmapHint = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0) };
        // In a narrow view the hint takes a line of its own and wraps: it is never cut (a refused cut says why there).
        zmapHintBelow = new() { TextWrapping = TextWrapping.Wrap, Margin = new(24, 0, 8, 8), Visibility = Visibility.Collapsed };
        zmapBannerKeys = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        zmapFinish = BannerButton("Finish", "Finish outline", () => EditZmap(FinishZmapDraw), accent: true);
        zmapConfirmSplit = BannerButton("Split", "Split outline", () => EditZmap(FinishZmapSplit), accent: true);
        zmapCancel = BannerButton("Cancel", "Cancel", () => EditZmap(CancelZmapDraft), accent: false);
        StackPanel line = new() { Orientation = Orientation.Horizontal, MinHeight = 34 };
        zmapBannerDot = BannerText("·", "TextFillColorTertiaryBrush");
        line.Children.Add(zmapBannerIcon); line.Children.Add(zmapBannerTitle); line.Children.Add(zmapBannerDot);
        line.Children.Add(zmapHint); line.Children.Add(zmapBannerKeys);
        line.Children.Add(zmapFinish); line.Children.Add(zmapConfirmSplit); line.Children.Add(zmapCancel);
        StackPanel banner = new();
        banner.Children.Add(line); banner.Children.Add(zmapHintBelow);
        // Only the buttons take the pointer: a click, a wheel turn or a middle-drag anywhere else on the banner reaches the
        // view under it (its chip style lets them through, and its texts are no hit targets).
        foreach (var text in new FrameworkElement[] { zmapBannerTitle, zmapBannerDot, zmapHint, zmapHintBelow, zmapBannerKeys })
        {
            text.IsHitTestVisible = false;
            // What the tool expects next is a live region: a screen reader hears it change.
            if (text == zmapHint || text == zmapHintBelow) AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        }
        zmapBanner = new() { Content = banner, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 12, 0, 0), MinHeight = 36,
            Padding = new(12, 0, 4, 0), FontSize = 13, Visibility = Visibility.Collapsed, Focusable = false };
        zmapBanner.SetResourceReference(StyleProperty, "PassThroughChip");
        AutomationProperties.SetName(zmapBanner, "ZMAP tool");
        ZmapChrome.Children.Add(zmapBanner);

        // The last message: why an action was refused, or what a save did. The next action clears it.
        zmapMessageIcon = new() { Width = 14, Height = 14, Margin = new(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        zmapStatus = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        DockPanel message = new();
        DockPanel.SetDock(zmapMessageIcon, Dock.Left);
        message.Children.Add(zmapMessageIcon); message.Children.Add(zmapStatus);
        zmapMessageChip = new() { Content = message, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(10), MaxWidth = 560,
            Padding = new(10, 7, 12, 7), IsHitTestVisible = false, Focusable = false, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(zmapMessageChip, "ZMAP message");
        AutomationProperties.SetLiveSetting(zmapStatus, AutomationLiveSetting.Polite);
        ZmapChrome.Children.Add(zmapMessageChip);

        static Button BannerButton(string text, string name, Action action, bool accent)
        {
            Button button = new() { Content = text, Height = 28, Padding = new(12, 0, 12, 0), Margin = new(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            if (accent) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => action();
            return button;
        }
    }

    /// <summary>Whether a pointer event started on one of the chips over the view (their buttons keep their clicks).</summary>
    private bool OnZmapChrome(object source)
    {
        for (var current = source as DependencyObject; current != null && current != PreviewHost;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current == ZmapChrome) return true;
        return false;
    }

    // ---------------------------------------------------------------- what the chrome shows

    /// <summary>Shows the session's state in the command bar, both panes, the banner and the status bar.</summary>
    private void RefreshZmapPresentation(ZmapEditorSession editor)
    {
        var map = editor.Map;
        var outline = editor.Selected;
        string? path = editor.SavePath;
        int outlines = map?.Outlines.Length ?? 0, vertices = map?.Outlines.Sum(o => o.Vertices.Length) ?? 0;
        string file = map == null ? "No ZMAP open" : path == null ? "Untitled ZMAP" : Path.GetFileName(path);
        string counts = $"{outlines:N0} outline{(outlines == 1 ? "" : "s")} · {vertices:N0} {(vertices == 1 ? "vertex" : "vertices")}";
        string mission = editor.Document.SourceWorld?.Mission ?? "";
        int index = outline == null ? -1 : map!.Outlines.IndexOf(outline);
        int vertex = outline != null && editor.Vertex is Guid point ? ZmapEditorSession.FindVertex(outline, point) : -1;
        bool drawing = zmapDrawing != null, splitting = zmapSplit != null, idle = !zmapBusy && !HasZmapDraft;

        // The command bar's title block.
        zmapName.Text = file; zmapName.ToolTip = path ?? (map == null ? null : "Not saved yet: Save asks where to put it");
        zmapEdited.Visibility = editor.Dirty ? Visibility.Visible : Visibility.Collapsed;
        zmapCaption.Text = map == null ? $"HUD map · {mission}" : $"HUD map · {mission} · {counts}";

        // The Outlines pane.
        zmapOutlineCount.Text = map == null ? "" : outlines.ToString("N0", CultureInfo.CurrentCulture);
        zmapAddOutline.IsEnabled = zmapEmptyDraw.IsEnabled = idle && map != null;
        zmapEmptyOpen.IsEnabled = zmapEmptyNew.IsEnabled = !zmapBusy;
        zmapOutlinesEmpty.Visibility = outlines == 0 && !drawing ? Visibility.Visible : Visibility.Collapsed;
        zmapOutlinesEmptyTitle.Text = map == null ? "No ZMAP open" : "No outlines yet";
        zmapOutlinesEmptyText.Text = map == null ? "Open a version-5 HUD map to draw its outlines over the map, or start a new one."
            : "Draw the first outline: click its points in the view, then press Enter.";
        zmapEmptyOpen.Visibility = zmapEmptyNew.Visibility = map == null ? Visibility.Visible : Visibility.Collapsed;
        zmapEmptyDraw.Visibility = map != null ? Visibility.Visible : Visibility.Collapsed;

        // The card: a drawn outline, the selected outline (its split while it is cut), or nothing selected.
        bool inspect = outline != null && !drawing;
        zmapCardFrame.Visibility = zmapCardClose.Visibility = inspect ? Visibility.Visible : Visibility.Collapsed;
        zmapCardFrame.IsEnabled = !zmapBusy && zmapDrag == null;
        zmapCardClose.IsEnabled = idle;
        if (drawing)
        {
            zmapCardHeading.Text = "New outline"; zmapCardSubtitle.Text = "HUD outline · being drawn";
            SetTile("draw", () => TileIcon("DrawOutline", "AccentFillColorDefaultBrush"));
            zmapDrawCount.Text = zmapDrawing!.Count.ToString("N0", CultureInfo.CurrentCulture);
        }
        else if (outline != null)
        {
            int count = outline.Vertices.Length;
            zmapCardHeading.Text = $"Outline {index + 1}";
            zmapCardSubtitle.Text = $"HUD outline · {count:N0} {(count == 1 ? "vertex" : "vertices")}";
            SetTile(outline.Color.ToString(), () =>
            {
                Border swatch = new() { Width = 18, Height = 18, CornerRadius = new(4), Background = ZmapOverlay.Swatch(outline.Color), BorderThickness = new(1) };
                swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrongStrokeColorDefaultBrush");
                return swatch;
            });
            zmapOutlineSection.Summary = ZmapObjectiveLabel(outline.ObjectiveIndex, editor.ObjectiveCount);
            zmapVertexSection.Summary = vertex >= 0 ? $"{vertex + 1} of {count:N0}" : count.ToString("N0", CultureInfo.CurrentCulture);
        }
        else
        {
            zmapCardHeading.Text = "Properties"; zmapCardSubtitle.Text = "Nothing selected";
            SetTile("none", () => TileIcon("Pointer", "TextFillColorTertiaryBrush"));
            zmapHeroTitle.Text = "Nothing selected";
            zmapHeroText.Text = map == null || outlines == 0 ? "An outline you select shows its colour, objective and vertices here."
                : "Click an outline in the view or in the Outlines list to edit its colour, objective and vertices.";
            RefreshZmapSummary(map == null ? null : [("File", path == null ? "Untitled" : Path.GetFileName(path)), ("Outlines", outlines.ToString("N0", CultureInfo.CurrentCulture)),
                ("Vertices", $"{vertices:N0} of {ZmapCodec.MaximumVertices:N0}"),
                ("Mission objectives", editor.ObjectiveCount is int objectives ? objectives.ToString(CultureInfo.CurrentCulture) : "Not found")]);
        }
        zmapHero.Visibility = !drawing && outline == null ? Visibility.Visible : Visibility.Collapsed;
        zmapSummary.Visibility = !drawing && outline == null && map != null ? Visibility.Visible : Visibility.Collapsed;
        zmapDrawSection.Visibility = drawing ? Visibility.Visible : Visibility.Collapsed;
        zmapSplitSection.Visibility = splitting ? Visibility.Visible : Visibility.Collapsed;
        zmapOutlineSection.Visibility = zmapVertexSection.Visibility = zmapFooter.Visibility = inspect && !splitting ? Visibility.Visible : Visibility.Collapsed;
        zmapVertexHint.Visibility = editor.Vertex == null ? Visibility.Visible : Visibility.Collapsed;
        zmapPreviousVertex.IsEnabled = zmapNextVertex.IsEnabled = idle && outline != null;
        zmapFrameVertex.IsEnabled = editor.Vertex != null && zmapDrag == null;
        zmapFooterStatus.Text = zmapFieldsDirty ? "Unsaved changes" : "No changes";
        zmapFooterDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, zmapFieldsDirty ? "AccentFillColorDefaultBrush" : "TextFillColorTertiaryBrush");
        RefreshZmapSwatch();

        // The status bar.
        ZmapStatusSelection.Text = drawing ? "Drawing an outline" : outline == null ? "No selection"
            : $"Outline {index + 1}" + (vertex >= 0 ? $" · vertex {vertex + 1} of {outline.Vertices.Length:N0}" : "");
        // The name is trimmed when it is long (whole, with its folder, in the tooltip); what follows it keeps its place.
        ZmapStatusFile.Text = file;
        ZmapStatusFile.ToolTip = path ?? (map == null ? null : file);
        ZmapStatusCounts.Text = map == null ? "" : $"{(editor.Dirty ? " (edited)" : "")} · {counts}";
        RefreshZmapBanner();

        // The tile is replaced only when what it shows changes (this runs on every refresh).
        void SetTile(string key, Func<UIElement> content)
        {
            if (key == zmapTileShown) return;
            zmapTileShown = key; zmapCardTile.Child = content();
        }
        static PreviewIcon TileIcon(string kind, string brush)
        {
            PreviewIcon icon = new() { Kind = kind, Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextElement.ForegroundProperty, brush);
            return icon;
        }
    }

    /// <summary>The colour box's swatch follows what is typed while it reads as a colour.</summary>
    private void RefreshZmapSwatch()
    {
        try { zmapColorSwatch.Background = ZmapOverlay.Swatch(ZmapEditorSession.ParseColor(zmapColor.Text)); zmapColorSwatch.Opacity = 1; }
        catch (InvalidDataException) { zmapColorSwatch.Opacity = .35; }
    }

    private void RefreshZmapSummary(IReadOnlyList<(string Label, string Value)>? rows)
    {
        string signature = rows == null ? "" : string.Join("\u0001", rows.Select(row => row.Label + "\u0002" + row.Value));
        if (signature == zmapSummaryShown) return;
        zmapSummaryShown = signature;
        zmapSummaryTable.Children.Clear(); zmapSummaryTable.RowDefinitions.Clear();
        for (int i = 0; i < (rows?.Count ?? 0); i++)
        {
            zmapSummaryTable.RowDefinitions.Add(new() { Height = GridLength.Auto });
            TextBlock key = new() { Text = rows![i].Label, FontSize = 12.5, Margin = new(0, 3, 12, 3) };
            key.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            TextBlock value = new() { Text = rows[i].Value, FontSize = 12.5, Margin = new(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = rows[i].Value };
            Grid.SetRow(key, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
            zmapSummaryTable.Children.Add(key); zmapSummaryTable.Children.Add(value);
        }
    }

    /// <summary>
    /// The banner over the view while a tool or a drag is active: what the next click does, the keys that end it and its
    /// actions. Rebuilt only when what it says changes (a drag asks on every pointer move).
    /// </summary>
    private void RefreshZmapBanner()
    {
        string? icon = null, title = null, hint = null, enter = null;
        // A press on a vertex selects it; the banner waits for the drag to move.
        if (zmapDrag != null && zmapDragMoved)
        {
            (icon, title) = ("Move", "Move vertex");
            hint = !zmapDragSnap ? "Hold Ctrl to snap to a map vertex" : ZmapLayer.Viewport?.ZmapSnapStatus switch
            {
                "preparing" => "Preparing map snap targets…",
                "unavailable" => "Map snap targets are unavailable",
                _ => ZmapLayer.SnapTarget != null ? "Snapped to a map vertex" : "No map vertex nearby",
            };
        }
        else if (zmapDrag != null) { }
        else if (zmapSplit != null) { (icon, title, hint) = ("SplitOutline", "Split outline", SplitHint()); enter = zmapSplit.CanApply ? "splits" : null; }
        else if (zmapDrawing != null)
        {
            (icon, title) = ("DrawOutline", "Draw outline");
            hint = zmapDrawing.Count == 0 ? "Click in the view to place the first vertex" : $"{zmapDrawing.Count:N0} {(zmapDrawing.Count == 1 ? "vertex" : "vertices")} · click to add the next";
            enter = zmapDrawing.Count > 0 ? "finishes" : null;
        }
        else if (zmapInsert) (icon, title, hint) = ("InsertVertex", "Insert vertex", "Click an edge of the selected outline");
        zmapBanner.Visibility = title != null ? Visibility.Visible : Visibility.Collapsed;
        // The banner is centred over the view: in a narrow one it would cover the view's chip, which gives way.
        zmapViewChip.Visibility = title != null && PreviewHost.ActualWidth < 1280 ? Visibility.Collapsed : Visibility.Visible;
        zmapFinish.Visibility = zmapDrawing != null ? Visibility.Visible : Visibility.Collapsed;
        zmapFinish.IsEnabled = zmapDrawing is { Count: > 0 };
        zmapConfirmSplit.Visibility = zmapSplit != null ? Visibility.Visible : Visibility.Collapsed;
        zmapConfirmSplit.IsEnabled = zmapSplit?.CanApply == true;
        zmapCancel.Visibility = zmapDrawing != null || zmapInsert || zmapSplit != null ? Visibility.Visible : Visibility.Collapsed;
        if (title == null) { zmapBannerShown = zmapBannerSaid = null; return; }
        double width = PreviewHost.ActualWidth;
        bool narrow = width > 0 && width < 820, problem = zmapSplit?.Problem != null;
        // The hint sits in the banner's line while the view has room for it; under 700 it takes a line of its own, as wide as
        // the view allows, and wraps.
        bool below = width > 0 && width < 700;
        double room = below ? Math.Max(160, width - 60) : 0;
        string shown = string.Join("\u0001", icon, title, hint, enter, narrow, problem, Math.Round(room));
        if (shown == zmapBannerShown) return;
        zmapBannerShown = shown;
        zmapHint.Visibility = zmapBannerDot.Visibility = below ? Visibility.Collapsed : Visibility.Visible;
        zmapHintBelow.Visibility = below ? Visibility.Visible : Visibility.Collapsed;
        zmapHintBelow.MaxWidth = below ? room : double.PositiveInfinity;
        zmapBannerIcon.Kind = icon!; zmapBannerTitle.Text = title; zmapHint.Text = zmapHintBelow.Text = hint;
        string said = $"{title}: {hint}";
        foreach (var text in new[] { zmapHint, zmapHintBelow })
        {
            text.SetResourceReference(TextBlock.ForegroundProperty, problem ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
            AutomationProperties.SetName(text, said);
        }
        // Announced when what it says changes, not when the view's width moves it.
        if (said != zmapBannerSaid) { zmapBannerSaid = said; SourceExportSelectionDialog.LiveRegionChanged(below ? zmapHintBelow : zmapHint); }
        // A drag has no buttons: its text ends the chip.
        zmapBanner.Padding = new(12, 0, zmapDrag != null ? 6 : 4, 0);
        zmapBannerKeys.Children.Clear();
        if (narrow) return;
        if (enter != null) { zmapBannerKeys.Children.Add(Keycap("Enter")); zmapBannerKeys.Children.Add(BannerText(enter, "TextFillColorTertiaryBrush")); }
        zmapBannerKeys.Children.Add(Keycap("Esc")); zmapBannerKeys.Children.Add(BannerText("cancels", "TextFillColorTertiaryBrush"));
        if (zmapBannerKeys.Children[^1] is FrameworkElement last) last.Margin = new(0, 0, 10, 0);
    }

    private void StepZmapVertex(int step) => EditZmap(() =>
    {
        RequireZmapIdle();
        var outline = SelectedZmapOutline();
        int count = outline.Vertices.Length;
        int at = zmap!.Vertex is Guid vertex ? ZmapEditorSession.FindVertex(outline, vertex) + step : step > 0 ? 0 : count - 1;
        zmap.Select(outline.Id, outline.Vertices[(at % count + count) % count].Id);
    });

    // ---------------------------------------------------------------- the cards' shared parts

    private static Button ZmapIconButton(string icon, string style, string name, string tip, Action action)
    {
        Button button = new() { Content = new PreviewIcon { Kind = icon, Width = 16, Height = 16 }, Padding = new(0), ToolTip = tip };
        button.SetResourceReference(StyleProperty, style);
        ToolTipService.SetInitialShowDelay(button, 0); ToolTipService.SetBetweenShowDelay(button, 0); ToolTipService.SetShowOnDisabled(button, true);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>A card or pane action: its glyph and its words (the zone card's Paint with this zone).</summary>
    private static Button ZmapTextButton(string icon, string text, string tip, Action action)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        content.Children.Add(new PreviewIcon { Kind = icon, Width = 14, Height = 14, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        Button button = new() { Content = content, Height = 30, Padding = new(10, 0, 12, 0), Margin = new(0, 0, 6, 6), ToolTip = tip };
        ToolTipService.SetShowOnDisabled(button, true);
        AutomationProperties.SetName(button, text.TrimEnd('…'));
        button.Click += (_, _) => action();
        return button;
    }

    private static Grid ZmapRow(string caption, UIElement value, double width = 76)
    {
        Grid row = new() { Margin = new(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new() { Width = new(width) }); row.ColumnDefinitions.Add(new());
        TextBlock key = new() { Text = caption, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.WrapWithOverflow,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 8, 0), ToolTip = caption };
        key.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        row.Children.Add(key);
        Grid.SetColumn(value, 1); row.Children.Add(value);
        return row;
    }

    /// <summary>
    /// A field of the outline's card with its caption above it, at the card's width: the pane can be as narrow as 260 DIP,
    /// and the value (a hex colour, the chosen objective, a coordinate) still shows whole.
    /// </summary>
    private static StackPanel ZmapField(string caption, UIElement field)
    {
        TextBlock key = new() { Text = caption, FontSize = 12.5, Margin = new(0, 0, 0, 3) };
        key.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        StackPanel panel = new() { Margin = new(0, 3, 0, 5) };
        panel.Children.Add(key); panel.Children.Add(field);
        return panel;
    }

    private static TextBlock ZmapPlain(string text) => new() { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock ZmapHelp(string text, double indent = 76)
    {
        TextBlock help = new() { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(indent, 1, 0, 4) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return help;
    }

    /// <summary>A text box with its hint shown while it is empty.</summary>
    private static Grid ZmapWithHint(TextBox box, string text, double inset = 11)
    {
        TextBlock hint = new() { Text = text, IsHitTestVisible = false, Margin = new(inset, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        void Update() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Update(); Update();
        Grid grid = new(); grid.Children.Add(box); grid.Children.Add(hint);
        return grid;
    }
}
