using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Microsoft.Win32;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private void InitializeResourceMenus()
    {
        MenuItem archive = new() { Header = "Archive members" }, zrd = new() { Header = "ZRD nodes" };
        EditMenu.Items.Add(new Separator()); EditMenu.Items.Add(archive); EditMenu.Items.Add(zrd);
        EditMenu.SubmenuOpened += (_, e) =>
        {
            if (e.OriginalSource != EditMenu) return;
            var doc = ViewModel.SelectedDocument; var member = doc?.SelectedAsset?.ResourceId;
            PopulateArchiveMenu(archive.Items, doc, member); archive.IsEnabled = doc?.ResourceEdits?.IsArchive == true;
            PopulateZrdMenu(zrd.Items, doc, member, CentralTree.SelectedItem as ResourceTreeItem); zrd.IsEnabled = doc?.ResourceEdits != null && CentralTree.SelectedItem is ResourceTreeItem;
        };
        MenuItem context = new() { Header = "Edit archive member" }; AssetGrid.ContextMenu.Items.Add(context);
        AssetGrid.ContextMenu.Opened += (_, _) =>
        { var doc = assetContextDocument; context.Visibility = doc?.ResourceEdits?.IsArchive == true ? Visibility.Visible : Visibility.Collapsed; PopulateArchiveMenu(context.Items, doc, contextAsset?.ResourceId); };
        ContextMenu treeMenu = new(); CentralTree.ContextMenu = treeMenu;
        ResourceTreeItem? pointer = null;
        CentralTree.PreviewMouseRightButtonDown += (_, e) =>
        { pointer = PropertyContext.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as ResourceTreeItem; };
        treeMenu.Opened += (_, _) => { PopulateZrdMenu(treeMenu.Items, shownDocument, shownDocument?.SelectedAsset?.ResourceId, pointer ?? CentralTree.SelectedItem as ResourceTreeItem); pointer = null; };
        CentralTree.SelectedItemChanged += (_, e) => { selectedResourceNode = (e.NewValue as ResourceTreeItem)?.Node.Id; };
    }
    private void PopulateArchiveMenu(ItemCollection items, DocumentModel? doc, Guid? member)
    {
        items.Clear(); if (doc?.ResourceEdits?.IsArchive != true) return;
        foreach (var (title, action) in new[] { ("Add file…", "add"), ("Add ZRD resource…", "add_zrd"), ("Replace…", "replace"), ("Rename…", "rename"), ("Duplicate…", "duplicate"), ("Delete", "delete"), ("Move up", "up"), ("Move down", "down") })
        {
            MenuItem item = new() { Header = title, IsEnabled = action is "add" or "add_zrd" || member != null };
            item.Click += async (_, _) => await ResourceUiAsync(async () =>
            {
                if (!await ResolvePropertiesDraftsAsync(doc)) return;
                var edits = ResourceSession(doc); var selected = member == null ? null : edits.Member(member.Value); string name = selected?.Name ?? "resource.zrd"; string? path = null;
                if (action is "add" or "replace") { OpenFileDialog dialog = new() { Title = title }; if (dialog.ShowDialog(this) != true) return; path = dialog.FileName; if (action == "add") name = Path.GetFileName(path); }
                if (action is "add" or "add_zrd" or "rename" or "duplicate") { var values = ResourcePrompt(title, ("Name", name)); if (values == null) return; name = values[0]; }
                if (action == "delete" && MessageBox.Show(this, "Delete this member? Undo can restore it until this document is closed.", "Delete " + name, MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                int position = edits.Current.Members.ToList().FindIndex(m => m.Id == member) + (action == "up" ? -1 : 1);
                await ApplyResourceAsync(doc, ct => edits.PrepareArchiveAsync(action is "up" or "down" ? "move" : action, member ?? Guid.Empty, name, path, position, ct), doc.Revision, CancellationToken.None);
            });
            items.Add(item);
        }
    }
    private void PopulateZrdMenu(ItemCollection items, DocumentModel? doc, Guid? member, ResourceTreeItem? selected)
    {
        items.Clear(); if (doc?.ResourceEdits == null || member == null || selected == null) return;
        foreach (var (title, action) in new[] { ("Properties…", "properties"), ("Add child…", "add"), ("Change type…", "type"), ("Duplicate", "duplicate"), ("Delete", "delete"), ("Move up", "up"), ("Move down", "down"), ("Move to array…", "move") })
        {
            bool enabled = action switch { "add" => selected.Node.Kind == ZrdKind.Array, "duplicate" or "delete" or "up" or "down" or "move" => selected.Parent != null, _ => true };
            MenuItem item = new() { Header = title, IsEnabled = enabled };
            item.Click += async (_, _) => await ResourceUiAsync(async () =>
            {
                if (action == "properties") { await OpenResourcePropertiesAsync(doc, member.Value, selected.Node.Id); return; }
                if (!await ResolvePropertiesDraftsAsync(doc)) return;
                var edits = ResourceSession(doc); var root = await ResourceTreeAsync(doc, member.Value, CancellationToken.None);
                var node = root.Find(selected.Node.Id) ?? throw new InvalidDataException("This node was deleted.");
                var parent = ResourceEditSession.FindParent(root, node.Id); int position = parent?.Children.ToList().FindIndex(n => n.Id == node.Id) ?? -1;
                ZrdKind kind = ZrdKind.String; string value = "";
                if (action is "add" or "type")
                {
                    var result = ResourceTypePrompt(title, action == "type" ? node.Kind : ZrdKind.String);
                    if (result == null) return; (kind, value) = result.Value;
                }
                if (action == "delete" && MessageBox.Show(this, "Delete this node and its children? Undo can restore them.", title, MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                if (action == "move")
                {
                    var destination = ResourceMovePrompt(root, node.Id); if (destination == null) return;
                    parent = root.Find(destination.Value.Parent); position = destination.Value.Position;
                }
                if (action == "up") position--; if (action == "down") position++;
                await ApplyResourceAsync(doc, ct => edits.PrepareZrdAsync(member.Value, node.Id, action is "up" or "down" ? "move" : action, kind, value, parent?.Id ?? Guid.Empty, action == "add" ? -1 : position, ct), doc.Revision, CancellationToken.None);
            });
            items.Add(item);
        }
    }
    private async Task ResourceUiAsync(Func<Task> action)
    { try { await action(); } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Report(ex); MessageBox.Show(this, ex.Message, "Resource editing"); } }
    private Window ResourceDialog(string title, StackPanel body)
        => new() { Owner = this, Title = title, Content = body, Width = 540, SizeToContent = SizeToContent.Height, MaxHeight = 760, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    private static void DialogButtons(Window dialog, Panel panel)
    {
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Button apply = new() { Content = "Apply", IsDefault = true, MinWidth = 90, Margin = new(4) }, cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new(4) };
        apply.Click += (_, _) => dialog.DialogResult = true; buttons.Children.Add(apply); buttons.Children.Add(cancel); panel.Children.Add(buttons);
    }
    private string[]? ResourcePrompt(string title, params (string Label, string Value)[] fields)
    {
        StackPanel body = new() { Margin = new(16) }; List<TextBox> boxes = [];
        foreach (var field in fields) { body.Children.Add(new TextBlock { Text = field.Label }); TextBox box = new() { Text = field.Value, Margin = new(0, 4, 0, 12) }; body.Children.Add(box); boxes.Add(box); }
        var dialog = ResourceDialog(title, body); DialogButtons(dialog, body); return dialog.ShowDialog() == true ? boxes.Select(b => b.Text).ToArray() : null;
    }
    private (ZrdKind Kind, string Value)? ResourceTypePrompt(string title, ZrdKind initial)
    {
        StackPanel body = new() { Margin = new(16) };
        body.Children.Add(new TextBlock { Text = "Type" }); ComboBox kind = new() { ItemsSource = Enum.GetValues<ZrdKind>(), SelectedItem = initial, Margin = new(0, 4, 0, 12) }; body.Children.Add(kind);
        body.Children.Add(new TextBlock { Text = "Value (strings in JSON quotes; floats accept 0xXXXXXXXX)" }); TextBox value = new() { Text = initial == ZrdKind.String ? "\"\"" : initial == ZrdKind.Array ? "" : "0", Margin = new(0, 4, 0, 8) }; body.Children.Add(value);
        kind.SelectionChanged += (_, _) => value.Text = (ZrdKind)kind.SelectedItem == ZrdKind.String ? "\"\"" : (ZrdKind)kind.SelectedItem == ZrdKind.Array ? "" : "0";
        body.Children.Add(new TextBlock { Text = "Changing type replaces the previous value and any children. Undo restores them.", TextWrapping = TextWrapping.Wrap });
        var dialog = ResourceDialog(title, body); DialogButtons(dialog, body); return dialog.ShowDialog() == true ? ((ZrdKind)kind.SelectedItem, value.Text) : null;
    }
    private (Guid Parent, int Position)? ResourceMovePrompt(ZrdNode root, Guid moving)
    {
        StackPanel body = new() { Margin = new(16) }; body.Children.Add(new TextBlock { Text = "Select destination array. Index is measured after removing the moved node." , TextWrapping = TextWrapping.Wrap });
        TreeView tree = new() { ItemsSource = new[] { new ResourceTreeItem(root, 0, null, new Dictionary<Guid, bool>()) }, Height = 350, Margin = new(0, 8, 0, 8) }; body.Children.Add(tree);
        body.Children.Add(new TextBlock { Text = "Final child index" }); TextBox index = new() { Text = "0" }; body.Children.Add(index);
        var dialog = ResourceDialog("Move ZRD node", body); DialogButtons(dialog, body);
        if (dialog.ShowDialog() != true) return null;
        if (tree.SelectedItem is not ResourceTreeItem { Node.Kind: ZrdKind.Array } array || array.Node.Id == moving) throw new InvalidDataException("Choose a destination array.");
        return (array.Node.Id, int.Parse(index.Text, CultureInfo.InvariantCulture));
    }
}
