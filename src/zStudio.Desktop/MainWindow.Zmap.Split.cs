using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using zStudio.Automation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private sealed record ZmapSplitDraft(Guid Outline, Guid? First = null, Guid? Second = null, bool PickFirst = true, string? Problem = null)
    {
        public bool CanApply => First != null && Second != null && Problem == null;
    }

    private ZmapSplitDraft? zmapSplit;
    private ComboBox zmapFirst = null!, zmapSecond = null!;
    private RadioButton zmapPickFirst = null!, zmapPickSecond = null!;
    private Button zmapConfirmSplit = null!;
    private Guid? zmapSplitHover;

    /// <summary>The Split outline section of the card: each endpoint's vertex and the toggle that picks it in the view.</summary>
    private void InitializeZmapPathControls(Panel body)
    {
        (zmapFirst, zmapPickFirst) = CreatePicker("A", true);
        (zmapSecond, zmapPickSecond) = CreatePicker("B", false);
        body.Children.Add(ZmapHelp("The cut joins the two endpoints. Both pieces keep the colour and the objective and share the endpoints.", 0));

        (ComboBox, RadioButton) CreatePicker(string endpoint, bool first)
        {
            // Checked while the next click in the view sets this endpoint. The two are one radio pair: the view always
            // picks exactly one endpoint, and pressing the armed one keeps it armed.
            RadioButton pick = new() { Content = new PreviewIcon { Kind = "Pointer", Width = 14, Height = 14 }, GroupName = "ZmapSplitEndpoint",
                ToolTip = $"Pick endpoint {endpoint} in the view", Margin = new(4, 0, 0, 0) };
            pick.SetResourceReference(StyleProperty, "CardSegmentToggle");
            ToolTipService.SetInitialShowDelay(pick, 0);
            System.Windows.Automation.AutomationProperties.SetName(pick, $"Pick endpoint {endpoint}");
            // Arming runs from Checked (so UI Automation's Select works); a press on the control that is already armed
            // raises only Click and runs the same action, which keeps it armed and clears the last refusal and the hover.
            bool armed = false;
            void Arm() => EditZmap(() =>
            {
                if (zmapSplit is not { } draft) return;
                zmapSplit = draft with { PickFirst = first };
                zmapSplitHover = null;
                zmap!.Touch();
                ZmapLayer.Viewport?.FocusView();
            });
            pick.Checked += (_, _) =>
            {
                if (zmapRefreshing) return;
                // The press that checked it raises Click next, in the same call: that Click has nothing left to do.
                armed = true;
                Dispatcher.BeginInvoke(() => armed = false);
                Arm();
            };
            pick.Click += (_, _) => { if (armed) armed = false; else Arm(); };
            // Enter presses it too, as it pressed the buttons these replace (a radio button takes only Space by itself).
            pick.KeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;
                e.Handled = true;
                if (pick.IsChecked == true) Arm(); else pick.IsChecked = true;
            };
            var picker = new ComboBox { DisplayMemberPath = nameof(ZmapVertexRow.Label), SelectedValuePath = nameof(ZmapVertexRow.Id), Height = 32,
                Margin = new(0, 0, 0, 6), ToolTip = "The vertex: its number, then its X and Z" };
            System.Windows.Automation.AutomationProperties.SetName(picker, $"Endpoint {endpoint}");
            picker.SelectionChanged += (_, _) =>
            {
                if (!zmapRefreshing && zmapSplit != null && picker.SelectedValue is Guid vertex)
                    EditZmap(() => PickZmapSplit(vertex, first));
            };
            // The endpoint's badge, as the view draws it on the outline.
            Border badge = new() { Width = 20, Height = 20, CornerRadius = new(10), Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = System.Windows.Media.Brushes.Gold, BorderBrush = System.Windows.Media.Brushes.Black, BorderThickness = new(1.5), ToolTip = $"Endpoint {endpoint}",
                Child = new TextBlock { Text = endpoint, FontSize = 11, Foreground = System.Windows.Media.Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            // The badge, the caption and the Pick control on one line; the picker under it at the card's width, so the
            // endpoint's coordinates show whole.
            TextBlock caption = new() { Text = $"Endpoint {endpoint}", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            DockPanel line = new() { Margin = new(0, 3, 0, 3) };
            DockPanel.SetDock(pick, Dock.Right); line.Children.Add(pick);
            DockPanel.SetDock(badge, Dock.Left); line.Children.Add(badge);
            line.Children.Add(caption);
            body.Children.Add(line);
            body.Children.Add(picker);
            return (picker, pick);
        }
    }

    private void InitializeZmapOutlineMenu()
    {
        var menu = new ContextMenu();
        (Guid Session, Guid Outline)? target = null;
        var frame = Add("Frame outline", () => FrameZmap("outline"));
        var up = Add("Move up", () => MoveZmapOutline(SelectedZmapOutline().Id, -1));
        var down = Add("Move down", () => MoveZmapOutline(SelectedZmapOutline().Id, 1));
        var split = Add("Split outline", BeginZmapSplit);
        var remove = Add("Remove outline", RemoveSelectedZmapOutline);
        zmapOutlines.ContextMenu = menu;
        zmapOutlines.ContextMenuOpening += (_, e) =>
        {
            target = null;
            if (zmapBusy || HasZmapDraft || zmap == null) { e.Handled = true; return; }
            if (e.CursorLeft >= 0)
            {
                var at = zmapOutlines.InputHitTest(System.Windows.Input.Mouse.GetPosition(zmapOutlines)) as DependencyObject;
                if (at == null || ItemsControl.ContainerFromElement(zmapOutlines, at) is not ListBoxItem { DataContext: ZmapOutlineRow row })
                { e.Handled = true; return; }
                zmapOutlines.SelectedValue = row.Id;
            }
            frame.IsEnabled = remove.IsEnabled = zmap.Selected != null;
            split.IsEnabled = zmap.Selected?.Vertices.Length >= 4;
            int place = zmap.Selected is { } chosen ? zmap.RequireMap().Outlines.IndexOf(chosen) : -1;
            up.IsEnabled = place > 0;
            down.IsEnabled = place >= 0 && place < zmap.RequireMap().Outlines.Length - 1;
            if (zmap.Outline is Guid id) target = (zmap.Id, id);
        };

        MenuItem Add(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => EditZmap(() =>
            {
                RequireZmapIdle();
                if (target is not { } captured || zmap is not { } editor || editor.Id != captured.Session)
                    throw new StudioCommandException("stale_record", "The outline menu belongs to a different ZMAP.");
                editor.Select(captured.Outline, editor.Outline == captured.Outline ? editor.Vertex : null);
                action();
            });
            menu.Items.Add(item);
            return item;
        }
    }

    /// <summary>
    /// Moves an outline one place up or down the list, which is the file's order and the order the game draws the outlines
    /// in, and selects it; at an end of the list it stays.
    /// </summary>
    private void MoveZmapOutline(Guid id, int step)
    {
        RequireZmapIdle();
        var outlines = zmap!.RequireMap().Outlines;
        int index = outlines.IndexOf(zmap.FindOutline(id)) + step;
        if (index >= 0 && index < outlines.Length) zmap.MoveOutline(id, index);
    }

    private void RemoveSelectedZmapOutline()
    {
        RequireZmapIdle();
        zmap!.RemoveOutline(SelectedZmapOutline().Id);
    }

    private void BeginZmapSplit()
    {
        RequireZmapIdle();
        var outline = SelectedZmapOutline();
        if (outline.Vertices.Length < 4) throw new InvalidDataException("An outline needs at least four vertices to split.");
        zmapSplitHover = null;
        zmapSplit = new(outline.Id, zmap!.Vertex, PickFirst: zmap.Vertex == null);
        zmap.Touch();
        ZmapLayer.Viewport?.FocusView();
    }

    private void PickZmapSplit(Guid vertex, bool? first = null)
    {
        var draft = zmapSplit ?? throw new StudioCommandException("not_ready", "Start a split first.");
        var outline = zmap!.FindOutline(draft.Outline);
        int picked = ZmapEditorSession.FindVertex(outline, vertex);
        bool pickFirst = first ?? draft.PickFirst;
        string? problem = (pickFirst ? draft.Second : draft.First) is Guid other
            ? ZmapEditorSession.SplitProblem(outline, picked, ZmapEditorSession.FindVertex(outline, other)) : null;
        zmapSplit = pickFirst ? draft with { First = vertex, PickFirst = false, Problem = problem }
            : draft with { Second = vertex, PickFirst = draft.First == null, Problem = problem };
        zmapSplitHover = null;
        zmap.Touch();
    }

    private void FinishZmapSplit()
    {
        if (zmapSplit is not { First: Guid first, Second: Guid second } draft)
            throw new InvalidDataException("Pick both endpoints before splitting.");
        if (draft.Problem is { } problem) throw new InvalidDataException(problem);
        zmap!.Split(draft.Outline, first, second);
        zmapSplit = null;
        zmapSplitHover = null;
    }

    private string SplitHint()
    {
        var draft = zmapSplit!;
        string endpoint = draft.PickFirst ? "A" : "B";
        return draft.Problem ?? (draft.CanApply ? $"Check the cut. Click a vertex to change {endpoint}."
            : $"Pick endpoint {endpoint} on this outline.");
    }

    private void RefreshZmapPathControls()
    {
        zmapRefreshing = true;
        try
        {
            var items = zmapSplit == null ? null : zmapVertices.ItemsSource;
            if (!ReferenceEquals(zmapFirst.ItemsSource, items)) zmapFirst.ItemsSource = zmapSecond.ItemsSource = items;
            zmapFirst.SelectedValue = zmapSplit?.First;
            zmapSecond.SelectedValue = zmapSplit?.Second;
            zmapPickFirst.IsChecked = zmapSplit?.PickFirst == true;
            zmapPickSecond.IsChecked = zmapSplit?.PickFirst == false;
        }
        finally { zmapRefreshing = false; }
        UpdateZmapSplitPreview();
    }

    private void UpdateZmapSplitPreview()
    {
        if (zmapSplit is not { } draft || zmap?.Selected is not { } outline) { ZmapLayer.SplitPreview = null; return; }
        Vector3? a = Position(draft.First), b = Position(draft.Second);
        Vector3? hover = null;
        if (draft.Problem == null && !draft.CanApply && zmapSplitHover is Guid id && (draft.PickFirst ? draft.Second : draft.First) is Guid other &&
            ZmapEditorSession.SplitProblem(outline, ZmapEditorSession.FindVertex(outline, id), ZmapEditorSession.FindVertex(outline, other)) == null)
            hover = Position(id);
        ZmapLayer.SplitPreview = (a, b, hover, draft.Problem == null);
        ZmapLayer.InvalidateVisual();

        Vector3? Position(Guid? id) => id is Guid vertex ? outline.Vertices[ZmapEditorSession.FindVertex(outline, vertex)].Position : null;
    }
}
