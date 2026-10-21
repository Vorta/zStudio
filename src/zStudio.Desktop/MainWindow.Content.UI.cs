using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using zStudio.Core;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private sealed record ScriptRow(Guid Id, int Index, string Command, string Arguments);
    private readonly DataGrid scriptGrid = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly TabItem scriptTab = new() { Header = "Instructions", Visibility = Visibility.Collapsed };
    private (Guid Document, Guid Script)? scriptGridTarget;
    private void InitializeContentEditors()
    {
        scriptGrid.Columns.Add(new DataGridTextColumn { Header = "Index", Binding = new Binding("Index"), Width = 70 });
        scriptGrid.Columns.Add(new DataGridTextColumn { Header = "Command", Binding = new Binding("Command"), Width = 220 });
        scriptGrid.Columns.Add(new DataGridTextColumn { Header = "Arguments", Binding = new Binding("Arguments"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        VirtualizingPanel.SetVirtualizationMode(scriptGrid, VirtualizationMode.Recycling);
        scriptTab.Content = scriptGrid; StructuredPanel.Items.Add(scriptTab);
        // The Data tree keeps the node the user chose across refreshes.
        CentralTree.SelectedItemChanged += (_, e) => selectedResourceNode = (e.NewValue as ResourceTreeItem)?.Node.Id;
    }
    private void RefreshScriptGrid(DocumentModel doc, AssetRecord? asset)
    {
        if (doc.Document.Scripts is not { } scripts || asset?.Kind != AssetKind.Script)
        { scriptTab.Visibility = Visibility.Collapsed; scriptGrid.ItemsSource = null; scriptGridTarget = null; return; }
        var entry = scripts.Entries[asset.Index]; var target = (doc.SessionId, entry.Id);
        Guid? selected = scriptGridTarget == target ? (scriptGrid.SelectedItem as ScriptRow)?.Id : null;
        scriptGridTarget = target; scriptTab.Visibility = Visibility.Visible;
        scriptGrid.ItemsSource = entry.Instructions.Select((i, index) => new ScriptRow(i.Id, index, BriefToken(i.Tokens.FirstOrDefault() ?? ""), string.Join(" ", i.Tokens.Skip(1).Take(15).Select(BriefToken)))).ToArray();
        scriptGrid.SelectedItem = scriptGrid.Items.OfType<ScriptRow>().FirstOrDefault(r => r.Id == selected);
    }
    private static string BriefToken(string text) => JsonSerializer.Serialize(text.Length > 128 ? text[..128] + "…" : text);
}
