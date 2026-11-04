using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private static StudioParameter CreationParentParameter => new("parent", "object", "Optional source group from scene_tree; omitted means World. Shared source edits affect every copy.", Properties:
        [P("sourceFile", "string", "Group source file.", true), P("gltfNode", "integer", "Group glTF node index.", true), P("sourceOccurrence", "string", "Group sourceOccurrence from scene_tree.", true)]);
    private static SourceCreationParent? CreationParentArgument(JsonObject a) => a["parent"] is JsonObject p
        ? new(Text(p, "sourceFile"), Int(p, "gltfNode"), Text(p, "sourceOccurrence")) : null;
    private SourceCreationParent? ContextCreationParent()
    {
        if (sceneContextDocument != shownDocument || contextScene?.Owner != sceneTree) throw new StudioCommandException("stale_context", "Read the current World tree.");
        return contextScene!.Authored is { } group ? new(group.File, group.SourceNode, contextScene.StateKey)
            : contextScene.Node?.Class == "world" ? null : throw new StudioCommandException("unsupported", "Choose World or a source group.");
    }
    private void ValidateCreationParent(DocumentModel doc, SourceCreationParent? parent)
    {
        if (doc != shownDocument || doc.SourceWorld == null) throw new StudioCommandException("stale_document", "Choose a group in the shown source map.");
        if (parent != null && (sceneTreeDocument != doc || sceneTree?.CreatedRows.Any(r => r.Authored is { } a && a.File.Equals(parent.SourceFile, StringComparison.OrdinalIgnoreCase)
            && a.SourceNode == parent.GltfNode && r.StateKey == parent.SourceOccurrence && CanRenameRemoved(doc, a)) != true))
            throw new StudioCommandException("stale_context", "The parent group changed; read scene_tree for its current sourceOccurrence.");
    }
    private static string CreationScopeText(SourceCreationScope scope) => $"Inside {scope.Name}." + (scope.Copies > 1 || scope.Missions.Count > 1
        ? $" This source is shared: the change affects all {scope.Copies} copies in this map and maps {string.Join(", ", scope.Missions)}." : "");

    private async Task<DocumentModel> CreateSourceChildAsync(DocumentModel doc, SourceCreationParent? parent, string name, SourceModelAddition? model, CancellationToken token,
        IReadOnlyDictionary<string, string>? frozen = null)
    {
        ValidateCreationParent(doc, parent);
        Stack<string> parentPath = new();
        for (var row = parent == null ? null : sceneTree!.CreatedRows.First(r => r.StateKey == parent.SourceOccurrence); row != null; row = row.Parent)
            parentPath.Push(LayoutPath(row));
        var build = doc.SourceBuild!; var world = SourceWorldModel(doc).World;
        SourceCreationPlan? creation = null; SourceWorkspace? candidate = null; IReadOnlyDictionary<string, byte[]>? before = null;
        List<string> notes = [];
        var next = await PrepareSourceWorldEditAsync(doc, $"Adding {name}", (workspace, ct) =>
        {
            if (frozen != null)
                foreach (var (file, hash) in frozen)
                    if (workspace.Read(file, ct) is not { } bytes || SourceProject.Sha256(bytes) != hash)
                        throw new StudioCommandException("external_change", $"{file} changed after placement was prepared; choose the object again.");
            candidate = workspace; before = workspace.Overlay();
            var scope = SourceObjectEdits.CreationScope(workspace, build, parent, ct);
            creation = SourceObjectEdits.PlanCreate(workspace, world, build, scope, name, model, ct);
            notes.AddRange(creation.Edit.Notes);
            return workspace.Apply(creation.Edit.Label, creation.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), ct);
        }, token, notes: notes, verifyPrepared: (built, ct) =>
        {
            var actual = SourceObjectEdits.ValidateCreation(candidate!, before!, creation!, built, model, ct);
            if (frozen != null)
                foreach (var (file, hash) in frozen)
                    if (!creation!.Edit.Changes.Any(c => c.Relative.Equals(file, StringComparison.OrdinalIgnoreCase)) && (!actual.TryGetValue(file, out var now) || now != hash))
                        throw new StudioCommandException("external_change", $"{file} changed while confirming placement; choose the object again.");
        });
        RevealCreatedChild(next, creation!, parentPath.ToArray(), model != null);
        return next;
    }
    private void RevealCreatedChild(DocumentModel doc, SourceCreationPlan creation, IReadOnlyList<string> parentPath, bool model)
    {
        if (sceneTreeDocument != doc || sceneTree == null) return;
        var level = sceneTree.Roots;
        foreach (string path in parentPath)
        {
            var group = level.FirstOrDefault(r => LayoutPath(r) == path);
            if (group == null) return;
            group.IsExpanded = true; level = group.Children;
        }
        if (parentPath.Count == 0) level = sceneTree.Roots.FirstOrDefault(r => r.Node?.Class == "world")?.Children ?? [];
        var row = level.FirstOrDefault(r => r.Authored is { } a && a.File.Equals(creation.Parent.File, StringComparison.OrdinalIgnoreCase) && creation.Nodes.Contains(a.SourceNode));
        if (row == null) return;
        sceneTree.Select(row); SceneTreeModel.ExpandAncestors(row); ScheduleSceneTreeReveal(row);
        if (model && row.Index is int node) FocusSourceObjectCard(doc, node);
    }
    private async void SceneAddGroupClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        var doc = sceneContextDocument ?? throw new StudioCommandException("not_ready", "Choose a group first.");
        var parent = ContextCreationParent(); RequireNoDrafts();
        long revision = doc.Revision;
        var scope = await Task.Run(() => SourceObjectEdits.CreationScope(doc.SourceWorld!.Workspace, doc.SourceBuild!, parent));
        if (doc.IsDisposed || doc.Revision != revision) throw new StudioCommandException("stale_document", "The map changed; choose the parent again.");
        var nameWorld = SourceWorldModel(doc).World; var nameBuild = doc.SourceBuild!;
        string? name = PromptName("Add group", CreationScopeText(scope), "", "Add group", n => SourceObjectEdits.CreationNameProblem(nameWorld, nameBuild, n));
        if (name != null) { if (doc.Revision != revision) throw new StudioCommandException("revision_conflict", "The map changed while naming the group."); await CreateSourceChildAsync(doc, parent, name, null, CancellationToken.None); }
    });
    private async void SceneAddObjectClick(object sender, RoutedEventArgs e) => await RunUi(() => AddSourceModelInteractiveAsync(ContextCreationParent()));
    private void RegisterGroupCreationCommands(StudioCommands registry)
    {
        RegisterJob(registry, "source_group_add", "Add an empty named group inside World or a source group as one undoable glTF source edit. Parent identity comes from scene_tree. Shared-source changes affect all copies and maps using that source. Save writes the sources; exports compile the group's objects normally.",
            [DocumentParameter, RevisionParameter, CreationParentParameter, P("name", "string", "Unused group name, with the same rules as Rename.", true)], true,
            async (a, ct) =>
            {
                var doc = TargetDocument(a, true);
                if (InAnimationMode) throw new StudioCommandException("not_ready", "Leave Animation mode before adding a group.");
                var next = await CreateSourceChildAsync(doc, CreationParentArgument(a), Text(a, "name"), null, ct);
                return Result(new { document = DocumentState(next) });
            });
    }
}
