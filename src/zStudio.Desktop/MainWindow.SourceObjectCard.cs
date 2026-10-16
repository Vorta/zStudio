using System.Windows;
using System.Text.Json.Nodes;
using zStudio.Automation;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private void AttachSourceObjectCard(SceneViewport viewport, SceneInspectionCard card)
    {
        card.IsSourceWorld = shownDocument?.SourceWorld != null;
        card.SourceObject = () =>
        {
            card.TerrainAvailable = false;
            if (viewport != scene || shownDocument is not { SourceWorld: not null, IsDisposed: false } doc || card.Selection is not { } selected) return null;
            try
            {
                int node = EditableSourceSelectionNode(doc, selected.Node);
                var state = DescribeSourceObject(doc, node);
                if (state.Origin.Terrain != null) { card.TerrainAvailable = true; return null; }
                return new SceneInspectionCard.ObjectBinding(doc, state);
            }
            catch (StudioCommandException) { return null; }
        };
        card.SourceDraftStarted = current =>
        {
            if (current != CurrentInspectionCard || shownDocument is not { SourceWorld: not null } doc || current.ObjectNode == null)
                throw new StudioCommandException("context_changed", "The source object is no longer selected.");
            RequireSourceWorldIdle(doc.SourceWorld); RequireNoDrafts(doc, committing: true, committingCard: current);
            inspectionDraft = current;
        };
        card.BrowseModel = () =>
        {
            // A model inside this project, as the project path its Model field stores.
            string root = System.IO.Path.GetFullPath(ViewModel.RootPath);
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a model", Filter = "glTF models (*.gltf)|*.gltf",
                InitialDirectory = System.IO.Directory.Exists(System.IO.Path.Combine(root, "data")) ? System.IO.Path.Combine(root, "data") : root };
            if (dialog.ShowDialog(this) != true) return null;
            string relative = System.IO.Path.GetRelativePath(root, System.IO.Path.GetFullPath(dialog.FileName));
            relative = relative.Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative) || !relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
            { ViewModel.Status = "Choose a .gltf model in this project's data folder."; return null; }
            return relative;
        };
        card.SourceAction = async (action, name) =>
        {
            if (shownDocument is not { SourceWorld: not null } doc || card.ObjectEditor is not { } original || CurrentInspectionCard?.ObjectEditor is not { } current)
                throw new StudioCommandException("context_changed", "Select an object first.");
            if (card.ObjectDocument?.SourceWorld is not { } originalWorld || originalWorld.Workspace != doc.SourceWorld.Workspace || originalWorld.Mission != doc.SourceWorld.Mission)
                throw new StudioCommandException("context_changed", "The source world changed.");
            var expected = SourceIdentity.Of(original.State.Node, original.State.Origin);
            var actual = SourceIdentity.Of(current.State.Node, current.State.Origin);
            if (!new SourceIdentityComparer().Equals(expected, actual)) throw new StudioCommandException("context_changed", "The selected source object changed.");
            int node = current.State.Node;
            RequireNoDrafts();
            var origin = SourceCopyOrigin(doc, node);
            if (action == "duplicate")
            {
                var next = await DuplicateSourceObjectAsync(doc, node, name!, null, CancellationToken.None);
                if (shownDocument == next && SourceCopiesNamed(next, name!, origin) is [int copy, ..]) FocusSourceObjectCard(next, copy);
            }
            else await DeleteSourceObjectAsync(doc, node, CancellationToken.None);
        };
        card.EditTerrain = () => _ = RunUi(async () =>
        {
            if (shownDocument is not { SourceWorld: not null } doc || card.Selection is not { } selected) return;
            var state = DescribeSourceObject(doc, EditableSourceSelectionNode(doc, selected.Node));
            if (state.Origin.Terrain is { } recipe) await ShowTerrainPropertiesAsync(doc, recipe, state.Origin.TerrainSurface, null, null, CancellationToken.None);
        });
        card.Refresh();
    }
    private bool IsSourceTerrain(DocumentModel doc, int? node) => node is int n && doc.SourceBuild?.Provenance.GetValueOrDefault(n)?.Terrain != null;
    private async Task<bool> OpenSourceTerrainAsync(DocumentModel doc, int node, CancellationToken token)
    {
        var origin = doc.SourceBuild!.Provenance[node];
        return await ShowTerrainPropertiesAsync(doc, origin.Terrain!, origin.TerrainSurface, null, null, token);
    }
    private bool FocusSourceObjectCard(DocumentModel doc, int node)
    {
        if (shownDocument != doc || scene == null || CurrentInspectionCard is not { } card) return false;
        if (scene.IsFlyActive || scene.IsPickupDragging) throw new StudioCommandException("busy", "Finish the transform drag or exit Fly before selecting a source object.");
        if (card.Selection?.SelectionNode != node)
        {
            if (!ResolveInspectionDrafts(doc)) return false;
            card.ClearSourceNode();
            if (!scene.SelectInspectionNode(node, null))
            {
                var projection = SourceSelection(doc, scene.PreviewScene);
                if (!SourceWorldModel(doc).Slots.ContainsKey(node) && projection?.AuthoredNodes.ContainsKey(node) != true && projection?.Boundaries.ContainsKey(node) != true) return false;
                if (!scene.SelectInspection(null, false)) return false;
                card.SelectSourceNode(new($"source:{doc.SessionId}:{node}", node, -1, -1, null, null, null, null, null, false, null));
                if (sceneTreeDocument == doc && sceneTree is { } tree)
                {
                    bool changing = changingSceneTree; changingSceneTree = true;
                    try
                    {
                        var row = tree.Selected?.Index == node ? tree.Selected : tree.Reveal(node);
                        if (row != null)
                        {
                            tree.Select(row); SceneTreeModel.ExpandAncestors(row); inspectedSceneSource = row;
                            selectedNode = row.SourceGroup ? null : node; scene.SelectFramingNode(selectedNode); ScheduleSceneTreeReveal(row);
                        }
                    }
                    finally { changingSceneTree = changing; }
                }
            }
        }
        card.Refresh(); card.FocusObject(); return true;
    }
    /// <summary>Answers whether a zone change may change every use of a shared assignment; tests replace the dialog.</summary>
    internal Func<string, bool>? ConfirmSharedZone { get; set; }
    /// <param name="sharedZones">Consent to change every use of a shared zone assignment: true or false from automation, null to ask the user.</param>
    private async Task<DocumentModel> ApplySourceObjectCardAsync(SceneInspectionCard card, CancellationToken token, bool? sharedZones = false)
    {
        var doc = card.DraftDocument!; int node = card.ObjectNode!.Value;
        var editor = card.ObjectEditor!; var state = editor.State;
        var identity = SourceIdentity.Of(node, state.Origin);
        int occurrence = Math.Max(0, SourceWorldModel(doc).Sources.Matching(identity).IndexOf(node));
        var restore = (JsonArray)editor.Describe()["fields"]!.DeepClone();
        var patch = editor.Patch(text =>
        {
            int? parent = SourceParentNode(doc, text, node);
            if (parent is not int p) return null;
            p = EditableSourceSelectionNode(doc, p);
            var into = SourceWorldModel(doc).Slots[p];
            return into.Class == WorldNodeClass.World ? null : into;
        });
        var zone = editor.ZoneAssignment();
        ScriptLineChanges? lines = null; List<string> notes = [];
        long preview = 0; var viewport = scene;
        string? originalTarget = card.Selection?.Target;
        card.ObjectApplying = true; card.Refresh();
        // No handle gestures while the edit is prepared; its own preview shows the change.
        if (viewport?.HasTransformDraft == true) viewport.EndTransformDraft();
        try
        {
            // The plan refuses a shared assignment unless the user (or automation) consented to changing every use of it.
            bool shareZone = sharedZones == true;
            if (zone != null && sharedZones != true)
            {
                // A zone kept in a model several placements, copies or missions use changes all of them: say so before applying.
                var session = doc.SourceWorld!; var build = doc.SourceBuild!;
                var scope = await Task.Run(() =>
                {
                    RequireCurrentZoneSources(doc, token);
                    try { return SourceZoneEdits.Inspect(session.Workspace, build, [new(node)], 0, token); }
                    catch (System.IO.InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
                }, token);
                if (scope.RequiresSharedScope)
                {
                    string uses = $"This zone is shared: changing it changes {scope.AffectedCount:N0} object{(scope.AffectedCount == 1 ? "" : "s")} in this world" +
                        (scope.AffectedMissionCount > 0 ? $", across {string.Join(", ", scope.AffectedMissions.Take(8))}{(scope.AffectedMissionCount > 8 ? ", …" : "")}" : "") + ".";
                    if (sharedZones == false) throw new StudioCommandException("shared_scope", uses + " Use scene_card apply with sharedScope true to change them all.");
                    if (!(ConfirmSharedZone?.Invoke(uses) ?? MessageBox.Show(this, uses + "\n\nChange all of them?", "Zone", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes))
                        return doc;
                    shareZone = true;
                }
            }
            if (zone != null) patch = patch with { Zone = new(doc.SourceBuild!, node, zone, shareZone) };
            if (!patch.Reparent && patch.Model == null && state.Transform is { } before && (patch.Position != null || patch.RotationDegrees != null || patch.Scale != null)
                && SourceObjectEdits.CopiesOf(state.Origin, SourceWorldProvenance(doc).Values).Take(2).Count() == 1)
            {
                var after = before with { Position = patch.Position ?? before.Position, RotationDegrees = patch.RotationDegrees ?? before.RotationDegrees, Scale = patch.Scale ?? before.Scale };
                preview = viewport?.PreviewObjectTransform(node, before.Matrix(), after.Matrix(), before.RotationDegrees == after.RotationDegrees && before.Scale == after.Scale) ?? 0;
            }
            var next = await PrepareSourceWorldEditAsync(doc, $"Editing {state.Name}", (workspace, ct) =>
            {
                var plan = SourceObjectEdits.PlanPatch(SourceObjectTargetFor(doc, node, workspace), patch, ct);
                lines = plan.Lines; notes.AddRange(plan.Notes);
                return workspace.Apply(plan.Label, plan.Changes.Select(p => (p.Relative, (byte[]?)p.Content)), ct);
            }, token, verifyTargets: () => lines, notes: notes, committingCard: card);
            if (next == doc && preview != 0) viewport?.ClearObjectTransformPreview(preview);
            if (shownDocument == next)
            {
                var matches = SourceWorldModel(next).Sources.Matching(identity);
                if (matches.Count > 0) FocusSourceObjectCard(next, matches[Math.Min(occurrence, matches.Count - 1)]);
            }
            return next;
        }
        catch (Exception ex)
        {
            if (preview != 0) viewport?.ClearObjectTransformPreview(preview);
            if (!doc.IsDisposed && shownDocument == doc && card == CurrentInspectionCard && !card.HasObjectDraft && card.ObjectReleasedForAcceptance
                && ReferenceEquals(card.ObjectEditor, editor) && card.Selection?.Target == originalTarget)
            {
                var changed = new JsonArray(restore.Where(v => v?["pending"]?.GetValue<bool>() == true)
                    .Select(v => (JsonNode)new JsonObject { ["field"] = v!["field"]!.DeepClone(), ["value"] = v["value"]!.DeepClone() }).ToArray());
                card.ObjectApplying = false; card.Refresh();
                // Restoring is best effort: the edit's own error is the one to report.
                if (changed.Count > 0)
                    try { card.SetObjectFields(changed); }
                    catch (Exception refused) when (refused is StudioCommandException or InvalidOperationException) { }
            }
            card.ShowSourceError(ex.Message); throw;
        }
        finally { card.ObjectApplying = false; if (card == CurrentInspectionCard) { card.Refresh(); card.RefreshObjectHandles(); } }
    }
}
