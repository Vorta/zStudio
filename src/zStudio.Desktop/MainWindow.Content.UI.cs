using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

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
        scriptGrid.MouseDoubleClick += async (_, e) => { if (PropertyContext.FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is ScriptRow row && shownDocument?.SelectedAsset?.ResourceId is Guid entry) await ResourceUiAsync(async () => { await OpenScriptPropertiesAsync(shownDocument, entry, row.Id); }); };
        ContextMenu instructions = new(); scriptGrid.ContextMenu = instructions;
        ScriptRow? pointer = null;
        scriptGrid.PreviewMouseRightButtonDown += (_, e) => pointer = PropertyContext.FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as ScriptRow;
        instructions.Opened += (_, _) => { PopulateInstructionMenu(instructions.Items, shownDocument, shownDocument?.SelectedAsset?.ResourceId, pointer ?? scriptGrid.SelectedItem as ScriptRow); pointer = null; };
        MenuItem textures = new() { Header = "Textures" }, scripts = new() { Header = "Scripts" }, tokens = new() { Header = "Instructions" };
        EditMenu.Items.Add(textures); EditMenu.Items.Add(scripts); EditMenu.Items.Add(tokens);
        EditMenu.SubmenuOpened += (_, e) =>
        {
            if (e.OriginalSource != EditMenu) return;
            var doc = ViewModel.SelectedDocument;
            PopulateTextureMenu(textures.Items, doc, doc?.SelectedAsset); textures.IsEnabled = doc?.TextureEdits != null && !doc.IsContentMirror;
            PopulateScriptMenu(scripts.Items, doc, doc?.SelectedAsset?.ResourceId); scripts.IsEnabled = doc?.ScriptEdits != null;
            PopulateInstructionMenu(tokens.Items, doc, doc?.SelectedAsset?.ResourceId, scriptGrid.SelectedItem as ScriptRow); tokens.IsEnabled = doc?.ScriptEdits != null && doc.SelectedAsset != null;
        };
        MenuItem textureContext = new() { Header = "Edit texture" }, scriptContext = new() { Header = "Edit script" };
        AssetGrid.ContextMenu.Items.Add(textureContext); AssetGrid.ContextMenu.Items.Add(scriptContext);
        AssetGrid.ContextMenu.Opened += (_, _) =>
        {
            var doc = assetContextDocument;
            textureContext.Visibility = doc?.TextureEdits != null ? Visibility.Visible : Visibility.Collapsed; textureContext.IsEnabled = doc?.IsContentMirror != true;
            scriptContext.Visibility = doc?.ScriptEdits != null ? Visibility.Visible : Visibility.Collapsed;
            PopulateTextureMenu(textureContext.Items, doc, contextAsset); PopulateScriptMenu(scriptContext.Items, doc, contextAsset?.ResourceId);
        };
    }
    private void RefreshScriptGrid(DocumentModel doc, AssetRecord? asset)
    {
        if (doc.ScriptEdits == null || asset?.Kind != AssetKind.Script)
        { scriptTab.Visibility = Visibility.Collapsed; scriptGrid.ItemsSource = null; scriptGridTarget = null; return; }
        var entry = doc.ScriptEdits.Package.Entries[asset.Index]; var target = (doc.SessionId, entry.Id);
        Guid? selected = scriptGridTarget == target ? (scriptGrid.SelectedItem as ScriptRow)?.Id : null;
        scriptGridTarget = target; scriptTab.Visibility = Visibility.Visible;
        scriptGrid.ItemsSource = entry.Instructions.Select((i, index) => new ScriptRow(i.Id, index, BriefToken(i.Tokens.FirstOrDefault() ?? ""), string.Join(" ", i.Tokens.Skip(1).Take(15).Select(BriefToken)))).ToArray();
        scriptGrid.SelectedItem = scriptGrid.Items.OfType<ScriptRow>().FirstOrDefault(r => r.Id == selected);
    }
    private static string BriefToken(string text) => JsonSerializer.Serialize(text.Length > 128 ? text[..128] + "…" : text);
    private void PopulateTextureMenu(ItemCollection items, DocumentModel? doc, AssetItem? asset)
    {
        items.Clear(); if (doc?.TextureEdits == null) return;
        foreach (bool add in new[] { false, true })
        {
            MenuItem item = new() { Header = add ? "Add PNG…" : "Replace from PNG…", IsEnabled = !doc.IsContentMirror && (add || asset?.Record.Kind == AssetKind.Texture) };
            item.Click += async (_, _) => await ResourceUiAsync(async () =>
            {
                if (!await ResolvePropertiesDraftsAsync(doc)) return;
                OpenFileDialog input = new() { Title = add ? "Add texture" : "Replace texture", Filter = "PNG image|*.png" };
                if (input.ShowDialog(this) != true) return;
                string name = ""; IReadOnlyList<TextureTarget>? targets = null; var edits = TextureSession(doc); long revision = doc.Revision;
                if (add) { var values = ResourcePrompt("New texture", ("Name (1–31 Latin-1 characters)", Path.GetFileNameWithoutExtension(input.FileName))); if (values == null) return; name = values[0]; }
                else
                {
                    var candidates = await edits.DiscoverTargetsAsync(asset!.Index, ViewModel.Resolver!, doc.Lifetime.Token); CheckResourceContext(doc, revision);
                    targets = TextureTargetsPrompt(doc.Path, asset.Index, candidates); if (targets == null) return;
                }
                await ApplyContentAsync(doc, ct => edits.PrepareAsync(input.FileName, add ? null : asset!.Index, name, targets, ViewModel.Resolver!, ct), revision, CancellationToken.None);
                ViewModel.Status = "Texture edit applied. Save writes all affected packs; Undo restores the batch.";
            }); items.Add(item);
        }
    }
    private IReadOnlyList<TextureTarget>? TextureTargetsPrompt(string path, int index, IReadOnlyList<TextureTargetCandidate> candidates)
    {
        StackPanel body = new() { Margin = new(16) };
        body.Children.Add(new TextBlock { Text = "This pack is always included. Optionally select sibling variants below. The PNG must match this texture's dimensions; selected variants retain their own dimensions.", TextWrapping = TextWrapping.Wrap });
        StackPanel rows = new(); body.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        List<(string Path, CheckBox Enabled, ComboBox Records)> choices = [];
        foreach (var group in candidates.Where(c => !c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            var available = group.Where(c => c.Index != null && c.Problem == null).ToArray();
            CheckBox enabled = new() { Content = new TextBlock { Text = Path.GetFileName(group.Key) + (available.Length == 0 ? " — unavailable" : ""), ToolTip = group.Key }, IsEnabled = available.Length > 0, Margin = new(0, 12, 0, 4) }; rows.Children.Add(enabled);
            ComboBox records = new() { ItemsSource = available.Select(c => new { Label = $"#{c.Index} · {c.Width}×{c.Height} · {c.Name}", Candidate = c }), DisplayMemberPath = "Label", SelectedValuePath = "Candidate", SelectedIndex = available.Length == 1 ? 0 : -1, ToolTip = "Choose a record explicitly when names are duplicated." }; rows.Children.Add(records);
            choices.Add((group.Key, enabled, records));
        }
        var dialog = ResourceDialog("Texture replacement targets — default: this pack", body); DialogButtons(dialog, body);
        if (dialog.ShowDialog() != true) return null;
        List<TextureTarget> targets = [new(path, index)];
        foreach (var choice in choices.Where(c => c.Enabled.IsChecked == true))
        {
            if (choice.Records.SelectedValue is not TextureTargetCandidate { Index: int target }) throw new InvalidDataException("Choose a specific record for " + choice.Path);
            targets.Add(new(choice.Path, target));
        }
        return targets;
    }
    private void PopulateScriptMenu(ItemCollection items, DocumentModel? doc, Guid? entry)
    {
        items.Clear(); if (doc?.ScriptEdits == null) return;
        foreach (var (label, action) in new[] { ("Add script…","add"),("Properties…","properties"),("Rename…","rename"),("Duplicate…","duplicate"),("Delete","delete"),("Move up","up"),("Move down","down") })
        {
            var item = new MenuItem { Header = label, IsEnabled = action == "add" || entry != null };
            item.Click += async (_, _) => await ResourceUiAsync(async () =>
            {
                if (action == "properties") { await OpenScriptPropertiesAsync(doc, entry!.Value, null); return; }
                if (!await ResolvePropertiesDraftsAsync(doc)) return;
                var edits = ScriptSession(doc); string name = entry == null ? "script.zrd" : edits.Entry(entry.Value).Name;
                if (action is "add" or "rename" or "duplicate") { var fields = ResourcePrompt(label, ("Script name",name)); if (fields == null) return; name = fields[0]; }
                int position = edits.Package.Entries.ToList().FindIndex(s => s.Id == entry) + (action == "up" ? -1 : 1);
                await ApplyContentAsync(doc, ct => edits.PrepareEntryAsync(action is "up" or "down" ? "move" : action, entry ?? Guid.Empty, name, position, token: ct), doc.Revision, CancellationToken.None);
            }); items.Add(item);
        }
    }
    private void PopulateInstructionMenu(ItemCollection items, DocumentModel? doc, Guid? entry, ScriptRow? row)
    {
        items.Clear(); if (doc?.ScriptEdits == null || entry == null) return;
        foreach (var (label,action) in new[] { ("Insert instruction…","add"),("Properties…","properties"),("Replace instruction…","set"),("Duplicate","duplicate"),("Delete","delete"),("Move up","up"),("Move down","down") })
        {
            var item = new MenuItem { Header = label, IsEnabled = action == "add" || row != null };
            item.Click += async (_, _) => await ResourceUiAsync(async () =>
            {
                if (action == "properties") { await OpenScriptPropertiesAsync(doc, entry.Value, row!.Id); return; }
                if (!await ResolvePropertiesDraftsAsync(doc)) return;
                var edits = ScriptSession(doc); var list = edits.Entry(entry.Value).Instructions; int position = list.ToList().FindIndex(i => i.Id == row?.Id); string[]? tokens = null;
                if (action == "add") { var values = ResourcePrompt("Insert instruction", ("Command (add arguments in Properties)","")); if (values == null) return; tokens = [values[0]]; }
                if (action == "set") { var values = ResourcePrompt("Replace instruction", ("Command and arguments as a JSON array (up to 16 strings)", "[\"Command\"]")); if (values == null) return; tokens = JsonSerializer.Deserialize<string[]>(values[0]) ?? throw new InvalidDataException("Supply an array of command and argument strings."); }
                if (action == "up") position--; if (action == "down") position++;
                await ApplyContentAsync(doc, ct => edits.PrepareInstructionAsync(entry.Value, action is "up" or "down" ? "move" : action, row?.Id ?? Guid.Empty, tokens, position, ct), doc.Revision, CancellationToken.None);
            }); items.Add(item);
        }
    }
}
