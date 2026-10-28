using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>
/// The World tree's keyboard. Up/Down, Home/End and Page Up/Down visit every shown row, never a row's buttons: a row that can
/// be the selection becomes it (through the usual draft guard), any other row (a section, folder, file, pass or note) only takes
/// focus, its outline showing while the selection keeps its highlight and card. Left/Right step through the row's buttons, left
/// to right (Left from the first returns to the row; Left on the row does nothing). Space or Enter opens or closes a row with
/// children, or presses a focused button; Enter on a definition or binding row opens it in Animation mode; Escape on a button
/// returns to its row.
/// </summary>
public partial class MainWindow
{
    /// <summary>Set while a rebuild disables the tree that had keyboard focus: the rebuilt tree gives it back to the selection.</summary>
    private bool outlinerFocusPending;

    private void OutlinerKeyDown(object sender, KeyEventArgs e)
    {
        if (!sourceChromeActive || sceneTree is not { Projection: not null } model || System.Windows.Input.Keyboard.Modifiers != ModifierKeys.None) return;
        // The tree's context menu routes its keys through the tree: they are the menu's.
        if (e.OriginalSource is not Visual source || !DocumentSceneTree.IsAncestorOf(source)) return;
        var focused = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
        var item = focused == null ? null : RowContainer(focused);
        var row = item?.DataContext as SceneTreeItem;
        if (row != null && row.Owner != model) row = null;
        var button = row != null ? focused as ButtonBase : null;
        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown:
                e.Handled = true;
                MoveOutlinerFocus(row ?? model.Selected, e.Key);
                break;
            case Key.Left or Key.Right when item != null && row != null:
                e.Handled = true;
                var buttons = RowButtons(item);
                int next = (button == null ? -1 : buttons.IndexOf(button)) + (e.Key == Key.Right ? 1 : -1);
                if (next >= 0 && next < buttons.Count) FocusRowButton(buttons[next], item);
                else if (next < 0 && button != null) FocusRow(item);
                break;
            case Key.Space or Key.Enter when button != null:
                // Space presses the button itself (on release); Enter clicks it.
                if (e.Key == Key.Enter) { e.Handled = true; if (!e.IsRepeat) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button)); }
                break;
            // Enter on a definition or binding row opens it in Animation mode (Space still opens or closes its bindings).
            case Key.Enter when row is { CanOpenAnimation: true }:
                e.Handled = true;
                if (!e.IsRepeat) OpenAnimationRow(row);
                break;
            case Key.Space or Key.Enter when row is { ChildCount: > 0 }:
                e.Handled = true;
                if (!e.IsRepeat) row.IsExpanded = !row.IsExpanded;
                break;
            case Key.Escape when button != null && item != null:
                e.Handled = true;
                FocusRow(item);
                break;
        }
    }

    /// <summary>Moves the keyboard to the shown row a key leads to from <paramref name="from"/> (or the first row).</summary>
    private void MoveOutlinerFocus(SceneTreeItem? from, Key key)
    {
        if (sceneTree is not { } model) return;
        List<SceneTreeItem> rows = [];
        Stack<SceneTreeItem> pending = new(model.Roots.Reverse());
        while (pending.TryPop(out var shown))
        {
            rows.Add(shown);
            if (shown.IsExpanded && shown.ChildCount > 0) foreach (var child in shown.Children.Reverse()) pending.Push(child);
        }
        if (rows.Count == 0) return;
        int at = from == null ? -1 : rows.IndexOf(from);
        int page = Math.Max(1, (int)(DocumentSceneTree.ActualHeight / 28) - 1);
        int target = at < 0 ? 0 : key switch
        {
            Key.Up => at - 1, Key.Down => at + 1, Key.Home => 0, Key.End => rows.Count - 1,
            Key.PageUp => at - page, _ => at + page,
        };
        target = Math.Clamp(target, 0, rows.Count - 1);
        if (RealizeRow(rows[target]) is { } container) FocusRow(container);
    }

    /// <summary>
    /// Gives a row keyboard focus: a row that can be the selection takes it itself, and so becomes the selection; another row's
    /// content takes it, which leaves the selection as it is.
    /// </summary>
    private static void FocusRow(TreeViewItem item)
    {
        if (item.DataContext is SceneTreeItem { Selectable: true } && item.Focusable) { item.Focus(); return; }
        if (RowContent(item) is { } content) FocusTemporarily(content);
    }

    /// <summary>The row's container, created (and scrolled to) when the tree has not made it yet.</summary>
    private TreeViewItem? RealizeRow(SceneTreeItem row)
    {
        Stack<SceneTreeItem> path = new(); for (var at = row; at != null; at = at.Parent) path.Push(at);
        ItemsControl parent = DocumentSceneTree;
        TreeViewItem? container = null;
        while (path.TryPop(out var item))
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem)
            {
                parent.ApplyTemplate();
                var presenter = VisualChild<ItemsPresenter>(parent); presenter?.ApplyTemplate();
                if (presenter != null && VisualTreeHelper.GetChildrenCount(presenter) > 0 && VisualTreeHelper.GetChild(presenter, 0) is VirtualizingPanel panel)
                    panel.BringIndexIntoViewPublic(parent.Items.IndexOf(item));
                parent.UpdateLayout();
            }
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem found) return null;
            parent = container = found;
        }
        container?.BringIntoView();
        return container;
    }

    /// <summary>The tree row an element belongs to: its nearest TreeViewItem.</summary>
    private static TreeViewItem? RowContainer(DependencyObject element)
    {
        for (var at = element; at != null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at is TreeViewItem item) return item;
        return null;
    }

    /// <summary>The row's own content (the source map row of its template), not its children's.</summary>
    private static FrameworkElement? RowContent(TreeViewItem item)
    {
        Stack<DependencyObject> pending = new([item]);
        while (pending.TryPop(out var at))
        {
            if (at is FrameworkElement { Name: "SourceRow" } content) return content;
            for (int i = VisualTreeHelper.GetChildrenCount(at) - 1; i >= 0; i--)
                if (VisualTreeHelper.GetChild(at, i) is var child && child is not TreeViewItem) pending.Push(child);
        }
        return null;
    }

    /// <summary>The row's shown, enabled buttons, left to right.</summary>
    private static List<ButtonBase> RowButtons(TreeViewItem item)
    {
        List<ButtonBase> found = [];
        if (RowContent(item) is not { } content) return found;
        Stack<DependencyObject> pending = new([content]);
        while (pending.TryPop(out var at))
        {
            if (at is ButtonBase { IsVisible: true, IsEnabled: true } button) { found.Add(button); continue; }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(at); i++) pending.Push(VisualTreeHelper.GetChild(at, i));
        }
        return [.. found.OrderBy(b => b.TranslatePoint(new Point(0, 0), content).X)];
    }

    /// <summary>Gives a row button keyboard focus; disabled or hidden while focused, it hands focus back to its row.</summary>
    private static void FocusRowButton(ButtonBase button, TreeViewItem item)
    {
        DependencyPropertyChangedEventHandler gone = (_, e) => { if (e.NewValue is false && button.IsKeyboardFocused) FocusRow(item); };
        button.IsEnabledChanged += gone; button.IsVisibleChanged += gone;
        FocusTemporarily(button, () => { button.IsEnabledChanged -= gone; button.IsVisibleChanged -= gone; });
    }

    /// <summary>
    /// Focuses an element that takes focus only while the keyboard is on it (a row's buttons and content), so the tree's own
    /// keys and the mouse pass it by.
    /// </summary>
    private static void FocusTemporarily(UIElement element, Action? released = null)
    {
        element.Focusable = true;
        element.LostKeyboardFocus += Release;
        if (!element.Focus()) Release(element, null);
        void Release(object sender, KeyboardFocusChangedEventArgs? e)
        {
            // The event bubbles: a row's button losing focus (to the row itself) is not the row losing it.
            if (e != null && !ReferenceEquals(e.OldFocus, element)) return;
            element.LostKeyboardFocus -= Release;
            element.ClearValue(FocusableProperty);
            released?.Invoke();
        }
    }

    /// <summary>After a rebuild, the tree that had keyboard focus gives it to the selected row (unless focus went elsewhere meanwhile).</summary>
    private void RestoreOutlinerFocus(TreeViewItem container)
    {
        if (!outlinerFocusPending) return;
        outlinerFocusPending = false;
        var focused = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
        if (focused == null || focused == this || focused is DependencyObject inside && DocumentSceneTree.IsAncestorOf(inside)) FocusRow(container);
    }
}
