using System.Windows;
using System.Windows.Controls;
using zStudio.Automation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Whether a tree row's source-only node can be renamed here: a node of the mission database's glTF files that the build
    /// removes in every copy (the planner checks the rest).
    /// </summary>
    private static bool CanRenameRemoved(DocumentModel doc, SourceSelectionNode row) => row.CompiledNode == null && doc.SourceBuild is { } build
        && build.AuthoredHierarchy.Nodes.Where(n => n.SourceNode == row.SourceNode && string.Equals(n.File, row.File, StringComparison.OrdinalIgnoreCase))
            .ToList() is { Count: > 0 } copies && copies.All(n => n.CompiledNode == null && n.Origin.Database && n.Origin.ModelFile != null);

    /// <summary>
    /// Renames the source-only node <paramref name="file"/>'s glTF node <paramref name="gltfNode"/> (a group or reference
    /// holder the build removes, so the game never uses its name) as one undoable source edit. <paramref name="expected"/>,
    /// when given, is the name the caller saw.
    /// </summary>
    private Task<DocumentModel> RenameRemovedSourceNodeAsync(DocumentModel doc, string file, int gltfNode, string? expected, string name, CancellationToken token)
    {
        if (doc.SourceWorld == null || doc.SourceBuild is not { } build) throw new StudioCommandException("unsupported", "This document is not a source world.");
        var world = SourceWorldModel(doc).World;
        List<string> notes = [];
        ScriptLineChanges? verify = null;
        return PrepareSourceWorldEditAsync(doc, $"Renaming {Bounded(expected ?? $"node {gltfNode}", 64)}", (workspace, ct) =>
        {
            var plan = SourceObjectEdits.PlanRenameRemoved(workspace, world, build, file, gltfNode, expected, name, ct);
            notes.AddRange(plan.Notes);
            // Only a glTF node's name changes: every script instruction must still act on the same nodes, compared under the old name.
            verify = plan.Lines;
            return workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), ct);
        }, token, verifyTargets: () => verify, notes: notes);
    }

    private void SceneRenameClick(object sender, RoutedEventArgs e) => _ = RunUi(async () =>
    {
        if (contextScene?.Authored is not { CompiledNode: null } authored || contextScene.Owner != sceneTree || sceneContextDocument is not { IsDisposed: false, SourceWorld: not null } doc || doc != shownDocument) return;
        if (!await ResolvePropertiesDraftsAsync()) return;
        if (PromptName("Rename", $"Rename {authored.Name}. The build removes it, so the game never uses its name.", authored.Name) is not { } name || name == authored.Name) return;
        var renaming = RenameRemovedSourceNodeAsync(doc, authored.File, authored.SourceNode, authored.Name, name, CancellationToken.None);
        sourceWorldWork = renaming; await renaming;
    });

    /// <summary>A small modal name prompt; null when canceled.</summary>
    private string? PromptName(string title, string prompt, string value)
    {
        TextBox input = new() { Text = value, MinWidth = 260, MaxLength = 32, Margin = new(12, 6, 12, 6) };
        Button accept = new() { Content = "Rename", IsDefault = true, Margin = new(6, 6, 12, 12), MinWidth = 80 };
        Button cancel = new() { Content = "Cancel", IsCancel = true, Margin = new(6, 6, 0, 12), MinWidth = 80 };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(accept);
        StackPanel body = new();
        body.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new(12, 12, 12, 0) });
        body.Children.Add(input); body.Children.Add(buttons);
        Window dialog = new() { Owner = this, Title = title, Content = body, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        accept.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
