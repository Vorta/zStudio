using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Worlds;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private SceneInspectionCard? inspectionDraft;
    private SceneInspectionCard? CurrentInspectionCard => (motion?.Viewport ?? animation?.Viewport ?? scene)?.InspectionContent as SceneInspectionCard;
    private bool HasInspectionDraft => inspectionDraft?.HasDraft == true;

    private void AttachInspection(SceneViewport viewport)
    {
        var card = new SceneInspectionCard(viewport, item => DescribeInspection(viewport, item), BeginInspectionEdit, ApplyInspectionEdit);
        card.SetPanelHeight(Layout.InspectionPanelHeight);
        card.PanelHeightChanged += height => Layout.InspectionPanelHeight = height;
        card.DraftClosed += () => { if (ReferenceEquals(inspectionDraft, card)) inspectionDraft = null; };
        viewport.InspectionContent = card;
        card.Refreshed += () => { if (sourceChromeActive && ReferenceEquals(scene?.InspectionContent, card)) RefreshSourceStatus(); };
        card.CanIsolate = () => viewport == scene && animation == null && motion == null;
        card.IsIsolated = () => viewport == scene && isolatedNode != null;
        card.ToggleIsolation = current => ToggleCardIsolation(viewport, current);
        AttachSourceObjectCard(viewport, card);
        viewport.CanChangeInspection = () => ResolveInspectionDrafts();
        viewport.InspectionChanged += () => SceneTreeInspectionChanged(viewport);
        viewport.InspectionSelectionCleared += () => { card.ClearSourceNode(); ClearSceneTreeSelection(viewport); };
        // In a source map, a click on empty space clears the selection like Escape (asking about a pending edit first).
        viewport.EmptySpaceClicked += () =>
        {
            // The click that only dismissed the icon legend is not a click on the view.
            if (Environment.TickCount64 - legendClosedAt < 500) return;
            if (sourceChromeActive && viewport == scene && SectionSelection != null) ClearSectionSelection();
            else if (sourceChromeActive && viewport == scene && card.Selection != null) card.ClearSelection();
        };
    }
    /// <summary>The card's Isolate: shows only the selected object (a pickup's whole placement), or everything again.</summary>
    private void ToggleCardIsolation(SceneViewport viewport, SceneInspectionCard card)
    {
        if (viewport != scene || animation != null || motion != null) throw new StudioCommandException("unsupported", "Isolation is available only in model and Whole world previews.");
        if (viewport.IsPickupDragging || viewport.IsFlyActive) throw new StudioCommandException("busy", "Finish the transform drag or exit Fly first.");
        if (isolatedNode != null) { isolatedNode = null; viewport.Isolate(null); RefreshRowEyes(); return; }
        if (card.HasDraft) throw new StudioCommandException("pending_drafts", "Apply or discard the edit first.");
        if (card.Selection is not { AiNode: null } selected) throw new StudioCommandException("not_ready", "Select an object first.");
        int node = selected.SelectionNode;
        node = viewport.PickupAt(node)?.Root ?? node;
        isolatedNode = node; ApplyIsolation(viewport); RefreshRowEyes();
    }
    private bool ResolveInspectionDrafts(DocumentModel? doc = null)
        => ResolveParachuteDraft(doc) && ResolveAiPathDraft(doc) && (!HasInspectionDraft || doc != null && inspectionDraft!.DraftDocument != doc || inspectionDraft!.ResolvePending()) && ResolveSectionDraft(doc);
    /// <summary>Like <see cref="ResolveInspectionDrafts"/>, but a Yes that applies a source world's draft completes with that apply (its rebuild).</summary>
    private async Task<bool> ResolveInspectionDraftsAsync(DocumentModel? doc = null)
        => ResolveParachuteDraft(doc) && ResolveAiPathDraft(doc) && (!HasInspectionDraft || doc != null && inspectionDraft!.DraftDocument != doc || await inspectionDraft!.ResolvePendingAsync()) && await ResolveSectionDraftAsync(doc);

    private MissionPickupSource? InspectionSource(SceneViewport viewport, SceneInspection inspection)
    {
        if (viewport != scene || animation != null || shownAsset?.Kind != AssetKind.World || !inspection.Active) return null;
        if (inspection.AiNode is { } id && viewport.AiNetworks.Find(id) is { } ai)
            return new(System.IO.Path.GetFullPath(ai.Network.Archive).ToUpperInvariant(), ai.Network.MemberIndex, ai.Network.Member.ToUpperInvariant(), checked((int)ai.Node.SourceOffset));
        var actor = viewport.ActorAt(inspection.Node);
        if (actor == null || viewport.Mission!.Actors.Count(a => a.Root == actor.Root) != 1) return null;
        return actor.Pickup?.Source ?? actor.CoordinateSource;
    }
    /// <summary>
    /// Why a source map's placed object can't be edited now: its selection boundary's reason (repeated, hidden at the start,
    /// missing template, startup animation), else a passing cause (an animation preview, hidden with a World tree eye,
    /// placements loading); null for an object of the map's own sources.
    /// </summary>
    private string? PlacementReadOnlyReason(SceneViewport viewport, SceneInspection item, SourceMapSelection selection)
    {
        if (selection.Boundaries.GetValueOrDefault(selection.Owner(item.Node))?.ReadOnlyReason is { } reason) return reason;
        if (viewport.Mission?.InspectedActorAt(item.Node) is not { } actor) return null;
        if (animation != null || motion != null || viewport != scene) return "Not editable while an animation preview is open.";
        if (!item.Active && viewport.PreviewScene is { } data && viewport.HiddenNodes.Count > 0)
            for (int node = item.Node, depth = 0; depth < 1024 && node >= 0 && node < data.Nodes.Count; depth++, node = data.Nodes[node].Parents.FirstOrDefault(-1))
                if (viewport.HiddenNodes.Contains(node) || viewport.HiddenNodes.Contains(selection.Owner(node))) return SourceMapSelection.HiddenInTree;
        if (!item.Active) return "It isn't shown, so its placement can't be edited.";
        if (shownDocument?.PickupEdits == null) return "Placements are still loading.";
        return $"The placement record of {JsonData.ShownText(actor.Name, 64)} could not be matched, so it can't be edited.";
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameScene, Dictionary<int, JsonObject>> objectPolygonZones = new();
    private JsonObject DescribeInspection(SceneViewport viewport, SceneInspection item)
    {
        static string Short(string text) => text.Length > 2048 ? text[..2048] + "…" : text;
        var data = viewport.PreviewScene;
        var node = item.Node >= 0 && item.Node < data?.Nodes.Count ? data.Nodes[item.Node] : null;
        int sourceNode = item.Node >= 0 && item.Node < viewport.Mission?.SourceNodes.Count ? viewport.Mission.SourceNodes[item.Node] : item.Node;
        int owner = item.SelectionNode;
        SourceMapSelection? sourceSelection = null;
        JsonObject info = new()
        {
            ["Node"] = node == null ? $"Model #{item.Model}" : Short(node.Name) + $" · {node.Class} #{item.Node}",
            ["Target"] = item.Target,
            ["Status"] = item.Active ? "Visible" : "Inactive runtime instance / unavailable geometry",
        };
        if (node == null && shownDocument is { SourceWorld: not null } sourceOnly && SourceSelection(sourceOnly, data) is { } sourceProjection && sourceProjection.AuthoredNodes.TryGetValue(item.Node, out var sourceRow))
        {
            info["Node"] = sourceProjection.DisplayName(item.Node);
            info["Parent"] = sourceProjection.Parent(item.Node) is int parent ? sourceProjection.DisplayName(parent) : "None";
            info["Editing"] = "This source group is removed when the game world is built; rename it from the tree (right-click). Other edits are unavailable.";
        }
        if (node != null && shownDocument is { SourceWorld: not null } sourceDocument && SourceSelection(sourceDocument, data) is { } selection)
        {
            sourceSelection = selection;
            owner = selection.Owner(item.Node);
            var boundary = selection.Boundaries.GetValueOrDefault(owner);
            string? name = boundary?.Name ?? (owner >= 0 && owner < data!.Nodes.Count ? data.Nodes[owner].Name : null);
            if (name != null)
            {
                info["Node"] = Short(name);
                info["Selected object"] = Short(name);
                info["Selection owner"] = owner;
                info["Selection kind"] = boundary?.Synthetic == true ? "source reference group" : "scene object";
                if (boundary?.ReadOnlyReason is { } reason) info["Placement editing"] = reason;
                if (boundary?.Source is { } model) info["Object source"] = Short(model);
            }
            int? parent = sceneTreeDocument == sourceDocument && sceneTree?.Projection == selection && sceneTree.Selected?.Index == owner
                ? sceneTree.Selected.Parent?.Index : selection.Parent(owner);
            info["Parent"] = parent is int parentId ? SourceParent(parentId, selection) : selection.PassOf(owner) is { } pass ? pass.Label + " · placed after the world loads" : "None";
            if (selection.AuthoredNodes.TryGetValue(owner, out var authored))
            {
                info["Source file"] = Short(authored.File);
                info["glTF node index"] = authored.SourceNode;
            }
        }
        if (item.Surface is { } hit) info["Surface world XYZ"] = JsonData.Vector(hit);
        if (item.Normal is { } normal) info["Surface normal"] = JsonData.Vector(normal);
        if (shownDocument?.SourceWorld == null && item.Origin is { } origin) info["Object world origin XYZ"] = JsonData.Vector(origin);
        if (viewport.InspectionSourcePath is { } path) info["Source scene"] = Short(path);
        if (node != null)
        {
            var selected = owner >= 0 && owner < data!.Nodes.Count ? data.Nodes[owner] : null;
            if (sourceSelection == null && selected != null) DescribeParents("", selected.Parents);
            if (sourceSelection != null && selected?.Parents.Length > 1) DescribeParents("Compiled ", selected.Parents);
            if (owner != item.Node)
            {
                info["Surface node"] = NodeName(item.Node);
                DescribeParents("Surface ", node.Parents);
                info["Surface compiled source node"] = sourceNode;
            }
            if (owner >= 0) info["Compiled source node"] = owner < viewport.Mission?.SourceNodes.Count ? viewport.Mission.SourceNodes[owner] : owner;
            info["Scene local XYZ"] = JsonData.Vector(SceneBuilder.LocalTransform(node).Translation);
            info["Model"] = item.Model;
            if (item.Model >= 0 && item.Model < data!.Models.Count)
                info["Model geometry"] = $"{data.Models[item.Model].Vertices.Length} vertices · {data.Models[item.Model].Polygons.Length} polygons";
            info["LOD rank"] = motion?.Lod ?? animation?.PreviewLod ?? LodCombo.SelectedIndex;
            info["Material"] = item.Material;
            var flags = WorldSurfaceHighlights.NodeKind(data!, item.Node);
            info["CanModify"] = flags.HasFlag(WorldSurfaceKind.CanModify); info["ClipTo"] = flags.HasFlag(WorldSurfaceKind.ClipTo);
            DescribeZones(owner >= 0 && owner < data!.Nodes.Count ? owner : null);
        }
        if (data != null && item.Material >= 0 && item.Material < data.Materials.Count)
        {
            var material = data.Materials[item.Material]; uint soil = material.UInt("soil");
            string[] soils = ["default", "water", "seafloor", "quicksand", "lava", "fire"];
            info["Soil"] = soil + " · " + (soil < soils.Length ? soils[soil] : "unknown");
            int texture = material.Int("texture_index", -1);
            if (texture >= 0 && texture < data.Textures.Count) info["Stored texture"] = Short(data.Textures[texture].Text("name")) + $" · #{texture}";
        }
        if (item.RuntimeInstance is { } runtime)
        {
            info["Runtime instance"] = runtime.ToString(CultureInfo.InvariantCulture);
            info["Playhead seconds"] = item.Time;
            info["Pose"] = "Rendered animation pose; source coordinates are separate";
            // An assembled node can already have mission-start transforms. Do not label it serialized.
            info.Remove("Scene local XYZ");
        }
        var source = InspectionSource(viewport, item);
        if (item.AiNode is { } aiId && viewport.AiNetworks.Find(aiId) is { } ai)
        {
            info["Node"] = $"AI {ai.Network.Member} · node_{ai.Node.Index:00}";
            info["Network"] = Short(ai.Network.Name); info["Network type"] = Short(ai.Network.Type);
            info["Attack strategy"] = AiStrategyText(ai.Network.AttackStrategy);
            if (ai.Network.IsMw3)
            {
                var valves = MissionAiValves.ForNode(ai.Network, ai.Node).Take(9).ToArray();
                info["AI valves"] = valves.Length == 0 ? "No authored valve conditions or assignments" : string.Join("\n", valves.Take(8).Select(r => r.Name + ": " + string.Join(", ", MissionAiValves.References(r).Take(4).Select(v => MissionAiValves.Short(v.Name, 96))))) + (valves.Length > 8 ? "\nMore in Valve Properties…" : "");
            }
            info["Path width"] = ai.Network.PathWidth; info["Raw node value"] = ai.Node.RawValue;
            info["Directed link slots"] = new JsonArray(ai.Node.PreviewLinks.Select(l => (JsonNode)new JsonObject { ["slot"] = l.Slot, ["target"] = l.TargetIndex, ["problem"] = l.Problem }).ToArray());
            info["Authored link slot count"] = ai.Node.LinkCount;
            if (ai.Node.LinksTruncated) info["Link preview"] = $"First {AiNode.MaximumPreviewLinks} slots shown. Inspect/export the ZRD resource for the complete list.";
        }
        // A root several records place (m2 Easy) shows its first record's placement, read only.
        var actor = viewport.Mission?.InspectedActorAt(item.Node);
        if (actor != null)
        {
            info["Placed instance"] = Short(actor.Name) + $" · root #{actor.Root}";
            if (actor.NameTruncated) { info["Name truncated"] = true; info["Authored name characters"] = actor.NameCharacters; }
            info["Template source node"] = actor.SourceRoot; info["Layout"] = actor.PlacementSource;
            info.Remove("Scene local XYZ");
            if (actor.Pickup is { } pickup)
            {
                info["Pickup type"] = pickup.LogicalName; info["Amount"] = pickup.EffectiveAmount;
                info["Authored amount"] = pickup.AuthoredAmount; info["Respawn seconds"] = pickup.RespawnDelay;
                info["Rotation radians"] = JsonData.Vector(pickup.Rotation);
            }
            if (actor.CoordinateSource is { } record && shownDocument?.PickupEdits?.Coordinate(record) is { Kind: "tank" } placed) info["Template"] = placed.Template;
        }
        var edits = shownDocument?.PickupEdits;
        bool known = source != null && edits != null && (edits.Find(source) != null || edits.Coordinate(source) != null);
        if (known)
        {
            info["Authored placement XYZ"] = JsonData.Vector(edits!.Position(source!));
            if (shownDocument?.SourceWorld != null)
            {
                info["Source file"] = Short(source!.ArchivePath);
                info["Source record"] = source.RecordIndex;
            }
            else
            {
                info["Source archive"] = Short(source!.ArchivePath); info["Source resource"] = source.ResourceName + $" · member #{source.AssetIndex} · record #{source.RecordIndex}";
            }
            info["Edit scope"] = edits.Scope(source).Description;
            var rotation = edits.Rotation(source);
            if (edits.RotationKind(source) == PlacementRotationKind.EulerRadians)
            {
                info["Rotation radians"] = JsonData.Vector(rotation);
                // Degrees to two decimals, as the card's fields show them (radians are stored with four, about 0.006°).
                info[SceneInspectionCard.AuthoredRotation] = new JsonObject { ["x"] = Math.Round(rotation.X * (180 / Math.PI), 2), ["y"] = Math.Round(rotation.Y * (180 / Math.PI), 2), ["z"] = Math.Round(rotation.Z * (180 / Math.PI), 2) };
            }
            if (edits.Coordinate(source) is { Kind: "tank" } tank) { info["Template"] = tank.Template; info[SceneInspectionCard.AuthoredHeading] = rotation.Y; }
        }
        if (known && edits!.Coordinate(source!) is { Kind: "tank" } vehicle && (!vehicle.MissionSpecific && (vehicle.TemplateSourceNode == null || vehicle.Difficulties.Count == 0))) known = false;
        // A compiled world is read only: its placements are inspected, never moved.
        bool readOnly = shownDocument?.SourceWorld == null;
        info["Editable"] = known && !readOnly;
        info["Rotation axes"] = known ? edits!.RotationKind(source!) switch { PlacementRotationKind.EulerRadians => "XYZ", PlacementRotationKind.HeadingDegrees => "Y", _ => "None" } : "None";
        info["Editing"] = readOnly ? "Read only: edit the source project instead" : known ? "Edit position and supported rotation; confirm together as one undo step" : "Read-only inspection";
        if (!known && shownDocument is { SourceWorld: not null } sourceDoc && SourceSelection(sourceDoc, data) is { } editSelection)
        {
            // A placement's reason shows in the card as its Placement editing row (Editing is the footer's tooltip).
            string? reason = PlacementReadOnlyReason(viewport, item, editSelection);
            info["Editing"] = reason ?? "Edit this object in the selected-object panel.";
            if (reason != null) info["Placement editing"] = reason;
        }
        info["Document revision"] = shownDocument?.Revision;
        return info;

        // The built zone of the selected object and, separately, of a clicked internal mesh and its polygon; for a source
        // world, what the glTF authored (inherit, an ID or any) and which node or placement supplied the value.
        // A selected reference group without a compiled node of its own (a negative owner) has only its clicked surface's zones.
        void DescribeZones(int? owned)
        {
            bool Gate(int index) => (data!.Nodes[index].Metadata.UInt("flags") & WorldGltf.ZoneGate) != 0;
            // Only nodes that store a zone word (GameZ worlds) have one; others must not read as Any.
            bool Stored(int index) => data!.Nodes[index].Metadata["zone_id"] != null;
            int selected = owned ?? item.Node;
            // Only for the selection, the viewport's or the card's (from the tree), not hovering; each object is walked once per scene.
            if (owned is { } polygonOwner && (viewport.SelectedInspection?.Target == item.Target || (viewport.InspectionContent as SceneInspectionCard)?.Selection?.Target == item.Target))
            {
                var cached = objectPolygonZones.GetOrCreateValue(data!);
                if (!cached.TryGetValue(polygonOwner, out var objectZones)) cached[polygonOwner] = objectZones = ObjectPolygonZones(polygonOwner);
                info["Object polygon zones"] = objectZones.DeepClone();
            }
            if (owned is { } whole && Stored(whole))
            {
                // A placed runtime copy (a mission vehicle or pickup) has no source of its own; its template's is not its placement's.
                bool sourceNode = viewport.Mission == null || whole >= viewport.Mission.SourceNodes.Count || viewport.Mission.SourceNodes[whole] == whole;
                var origin = sourceNode && shownDocument?.SourceBuild is { } build && build.Provenance.TryGetValue(whole, out var found) ? found : null;
                info["Zone"] = ZoneReadout.Zone((uint)WorldSurfaceHighlights.NodeZone(data!, whole), origin?.ImportedZoneInherited);
                info["Altitude zone check"] = Gate(whole);
                if (origin != null && ZoneReadout.Source(origin) is { } source) info["Zone source"] = source;
            }
            if ((owned == null || selected != item.Node) && Stored(item.Node))
            {
                info["Surface zone"] = ZoneReadout.Zone((uint)WorldSurfaceHighlights.NodeZone(data!, item.Node));
                info["Surface altitude zone check"] = Gate(item.Node);
            }
            if (item.Polygon is { } polygon && item.Model >= 0 && item.Model < data!.Models.Count && polygon >= 0 && polygon < data.Models[item.Model].Polygons.Length
                && ZoneReadout.PolygonZones(data.Models[item.Model].Polygons[polygon].Metadata["zone_set"]) is { } zones)
                info["Polygon zones"] = zones;
        }
        // How many polygons of the object's meshes (its own and those below it) carry each zone set, most common first, bounded:
        // rows of stored zones (255 is Any; empty has no zones), then the further sets together and the unreadable polygons.
        JsonObject ObjectPolygonZones(int root)
        {
            const int MaximumNodes = 4096, MaximumPolygons = 200_000, MaximumRows = 16;
            Dictionary<string, (int[] Zones, int Count)> sets = []; int nodes = 0, polygons = 0, unknown = 0; string? truncated = null;
            HashSet<int> seen = []; Stack<int> pending = new([root]);
            while (pending.TryPop(out int index))
            {
                if (index < 0 || index >= data!.Nodes.Count || !seen.Add(index)) continue;
                if (++nodes > MaximumNodes) { truncated = "objects"; break; }
                var node = data.Nodes[index];
                if (node.ModelIndex is int model && model >= 0 && model < data.Models.Count)
                    foreach (var polygon in data.Models[model].Polygons)
                    {
                        if (polygons >= MaximumPolygons) { truncated = "polygons"; break; }
                        polygons++;
                        if (ZoneReadout.PolygonZoneIds(polygon.Metadata["zone_set"]) is { } zones)
                        {
                            string key = string.Join(',', zones);
                            sets[key] = (zones, (sets.TryGetValue(key, out var set) ? set.Count : 0) + 1);
                        }
                        else unknown++;
                    }
                if (truncated != null) break;
                foreach (int child in SceneBuilder.Children(node)) pending.Push(child);
            }
            var ordered = sets.Values.OrderByDescending(s => s.Count).ThenBy(s => string.Join(',', s.Zones), StringComparer.Ordinal).ToArray();
            JsonArray rows = [.. ordered.Take(MaximumRows).Select(s => (JsonNode)new JsonObject { ["zones"] = new JsonArray([.. s.Zones.Select(z => (JsonNode)z)]), ["polygons"] = s.Count })];
            if (ordered.Length > MaximumRows) rows.Add(new JsonObject { ["moreSets"] = ordered.Length - MaximumRows, ["polygons"] = ordered.Skip(MaximumRows).Sum(s => s.Count) });
            if (unknown > 0) rows.Add(new JsonObject { ["unknown"] = true, ["polygons"] = unknown });
            return new JsonObject { ["rows"] = rows, ["total"] = polygons, ["truncated"] = truncated };
        }
        string NodeName(int index) => index >= 0 && index < data!.Nodes.Count
            ? JsonData.ShownText(data.Nodes[index].Name, 160) + $" · #{index}"
            : $"Unavailable node #{index}";
        string SourceParent(int index, SourceMapSelection projection)
        {
            string name = JsonData.ShownText(projection.DisplayName(index), 160);
            return projection.AuthoredNodes.TryGetValue(index, out var authored)
                ? name + " · " + JsonData.ShownText(authored.File, 512) + $" · glTF node {authored.SourceNode}"
                : name;
        }
        void DescribeParents(string prefix, IReadOnlyList<int> parents)
        {
            string singular = prefix.Length == 0 ? "Parent" : prefix + "parent";
            if (parents.Count <= 1) info[singular] = parents.Count == 0 ? "None" : NodeName(parents[0]);
            else
            {
                info[singular + "s"] = new JsonArray(parents.Take(32).Select(i => JsonValue.Create(NodeName(i))).ToArray());
                if (parents.Count > 32) info[prefix + "parent count"] = parents.Count;
            }
        }
    }
    private void BeginInspectionEdit(SceneInspectionCard card)
    {
        // A draft started now would count as unfinished input when the rebuild finishes, taking its edit back.
        if (shownDocument?.SourceWorld != null && sourceWorkspaceBusy) throw new StudioCommandException("busy", "The world is rebuilding after an edit; edit placements when it is shown.");
        if (scene != null && shownDocument is { SourceWorld: null }) throw new StudioCommandException("read_only", "This world is read only: edit its source project instead.");
        if (card != CurrentInspectionCard || scene == null || shownDocument is not { IsDisposed: false } doc || card.Selection is not { } selection)
            throw new StudioCommandException("not_ready", "Select a mission placement first.");
        if (!ResolvePropertiesDrafts(doc)) return;
        if (card.ObjectEditor != null) { card.BeginObjectEdit(); return; }
        var source = InspectionSource(scene, selection);
        var edits = doc.PickupEdits;
        if (source == null || edits == null || edits.Find(source) == null && edits.Coordinate(source) == null)
            throw new StudioCommandException("read_only", SourceSelection(doc, scene.PreviewScene) is { } projection && PlacementReadOnlyReason(scene, selection, projection) is { } reason
                ? reason : "This node has no verified editable placement.");
        if (edits.Coordinate(source) is { Kind: "tank" } vehicle && (!vehicle.MissionSpecific && (vehicle.TemplateSourceNode == null || vehicle.Difficulties.Count == 0)))
            throw new StudioCommandException("read_only", SourceMapSelection.MissingTankTemplate);
        inspectionDraft = card; card.StartDraft(doc, source, edits.Position(source));
    }
    private Task? ApplyInspectionEdit(SceneInspectionCard card)
    {
        // The edit keeps this draft until its preparation succeeds: its failure is reported, but its draft is not asked about.
        if (ApplyInspectionEditCore(card, sharedZones: null) is { } rebuilding) return sourceWorldWork = ReportUi(() => rebuilding);
        return null;
    }
    /// <summary>Accepts the card's draft as one edit; in a source world the edit changes the sources and returns the rebuild that shows it.</summary>
    /// <param name="sharedZones">For a source object's zone change: consent to change a shared assignment, or null to ask the user.</param>
    private Task<DocumentModel>? ApplyInspectionEditCore(SceneInspectionCard card, CancellationToken token = default, bool? sharedZones = false)
    {
        if (card.ObjectApplying) throw new StudioCommandException("busy", "Wait for the current object edit to finish before applying again.");
        if (scene?.IsPickupDragging == true) throw new StudioCommandException("busy", "Finish or cancel the active transform drag before confirming.");
        var doc = card.DraftDocument;
        if (!card.HasDraft || doc == null || doc.IsDisposed || doc != shownDocument || card != CurrentInspectionCard || card.Selection?.Target != card.DraftTarget)
            throw new StudioCommandException("stale_record", "The draft's document or selected instance is no longer available.");
        if (doc.Revision != card.DraftRevision) throw new StudioCommandException("revision_conflict", "The document changed. Discard this draft and start again.");
        // Edit mode without a change: Apply only leaves it; a placement draft that stores nothing ends the same way.
        if (card.ObjectEditing && !card.HasObjectDraft) { card.CancelDraft(); return null; }
        if (!card.HasObjectDraft && card.DraftSource != null && !card.HasChanges) { card.CancelDraft(); return null; }
        if (card.HasObjectDraft) return ApplySourceObjectCardAsync(card, token, sharedZones);
        _ = doc.PickupEdits ?? throw new StudioCommandException("not_ready", "The map's placements are reloading. Apply again when the map is shown.");
        if (doc.SourceWorld == null) throw new StudioCommandException("read_only", "This world is read only: edit its source project instead.");
        if (doc.SourceWorld.IsRebuilding || sourceWorkspaceBusy) throw new StudioCommandException("busy", "The world is rebuilding after another edit; apply when it is shown.");
        // Keep this exact draft until worker preparation and its source/ownership checks succeed.
        return MoveSourcePlacementAsync(doc, card.DraftSource!, card.DraftTransform(), token, card);
    }

    private void RegisterInspectionCommands(StudioCommands commands)
    {
        Register(commands, "scene_inspect", "Read independent hover/selection information from the fixed top-right inspection panel (a source map docks it as the Properties pane, with the hover readout as the surface chip over the view), or query an explicit viewport point/target without moving the mouse. Parent(s) describes the selected object or authored source-tree occurrence; Surface node and Surface parent(s) separately describe a clicked internal mesh. glTF node indices and compiled source slots are distinct. The permanent hover readout clears its coordinates over empty space. Coordinates distinguish triangle hits, object origins and authored placement positions. Zone is the selected object's built zone ID (Any is 255), as Inherit (ID) when its source node inherits it, with its altitude zone check; Surface zone and Polygon zones separately describe a clicked internal mesh and its polygon (an empty polygon set has no zones); Object polygon zones (the selection only) counts the zone sets of the object's polygons and those below it: rows of zones (255 is Any, empty none) or moreSets or unknown, each with polygons, then total and truncated. In source worlds, Zone source names the glTF node, reference placement, ancestor or load root that supplied it. AI results include attackStrategy/hoverAttackStrategy with stored value, status, key, character count, truncation and network RGB hex color. Panel state includes preferred/effective heights, viewport limits, expanded state and whether a source map docks it as the Properties pane. In a source map, recentSelections lists the empty pane's Recently selected objects (newest first) with the node scene_tree select takes for each (null when it is no longer in the world).", false,
            [PreviewParameter, P("target", "string", "Opaque target from a previous inspection."), new("screenPoint", "array", "Viewport DIP [x,y].", Items: new("", "number", "Coordinate.", NumberMinimum: 0), MinItems: 2, MaxItems: 2)], a =>
        {
            var viewport = TargetViewport(a); SceneInspection? item;
            if (a["target"] != null && a["screenPoint"] != null) throw new StudioCommandException("invalid_argument", "Choose a target or a screen point.");
            if (a["screenPoint"] is JsonArray p)
            {
                Point point = new(p[0]!.GetValue<double>(), p[1]!.GetValue<double>());
                if (!viewport.IsNavigationPointInside(point)) throw new StudioCommandException("invalid_argument", "Point is outside this viewport.");
                item = viewport.ProbeInspection(point);
            }
            else if (a["target"] != null) item = ((viewport.InspectionContent as SceneInspectionCard)?.Selection is { } selectedItem && selectedItem.Target == Text(a, "target") ? selectedItem : viewport.InspectTarget(Text(a, "target"))) ?? throw new StudioCommandException("stale_record", "Inspection target expired.");
            else item = (viewport.InspectionContent as SceneInspectionCard)?.Selection ?? viewport.HoverInspection;
            return Result(new { preview = previewId, document = shownDocument?.SessionId, revision = shownDocument?.Revision,
                inspection = item == null ? null : DescribeInspection(viewport, item), selected = (viewport.InspectionContent as SceneInspectionCard)?.Selection?.Target,
                hover = viewport.HoverInspection == null ? null : DescribeInspection(viewport, viewport.HoverInspection),
                attackStrategy = InspectionAiStrategy(viewport, item), hoverAttackStrategy = InspectionAiStrategy(viewport, viewport.HoverInspection),
                draft = (viewport.InspectionContent as SceneInspectionCard)?.DescribeDraft(),
                panel = (viewport.InspectionContent as SceneInspectionCard)?.DescribePanel(),
                recentSelections = sourceChromeActive && recentDocument == shownDocument ? recentSelections.Select(s => new { name = s.Name, parent = s.Parent, category = s.Category, node = RecentRow(s)?.Index }).ToArray() : null });
        });
        Register(commands, "scene_card", "Expand/clear selected-object details below the permanent top-right hover readout, copy fields, or begin/set/apply/cancel the shared draft. Source objects expose local transform, name, model, flags and command fields in this panel, editable at once: the first change opens the draft (begin only checks an edit could start); set takes fields and/or transformMode (move/rotate handles, move arrows along the object's own axes, or none), Apply accepts one atomic source transaction and cancel is the footer's Revert. Placements start their draft with begin, a GUI field change or showing handles. Placement values are stored as the shipped files write them: positions and AIV headings with one decimal, pickup rotations in radians with four; a change below that precision changes nothing (apply then ends the draft without an edit and returns no document). Compiled worlds are read only: their cards show values, and begin/apply are refused. Move and supported Rotate toggles in the transform header show or hide their handles; unsupported Rotate stays hidden; Apply and Revert (footer) are enabled only once a draft changes something; transforms preview until apply creates one undo step. Edit actions require document/revision; set/apply/cancel require the current draft token. Copy optionally writes the clipboard.", true,
            [PreviewParameter, P("action", "string", "Card action.", true, "select", "clear", "copy", "begin", "set", "apply", "cancel"),
                P("target", "string", "Opaque inspection target for select."), P("node", "integer", "Alternative scene node for selection."),
                P("runtime", "string", "Optional runtime instance ID with node."), P("field", "string", "Copy field label; omitted copies all details."), P("clipboard", "boolean", "Write copy text to clipboard; default false."),
                P("sharedScope", "boolean", "Apply: consent to change every use of a shared source zone assignment (default false refuses with shared_scope)."),
                P("document", "string", "Owning document ID for edits."), new("revision", "integer", "Expected document revision for edits.", Minimum: 0, Maximum: long.MaxValue),
                P("token", "string", "Current draft lifetime and input token. Reopening a draft creates a new token."), new("position", "array", "Three coordinate input strings for set; maximum 64 characters each; permits temporary incomplete drafts.", Items: new("", "string", "Coordinate input."), MinItems: 3, MaxItems: 3),
                new("rotationDegrees", "array", "Pickup XYZ Euler input strings in degrees; maximum 64 characters each.", Items: new("", "string", "Angle input."), MinItems: 3, MaxItems: 3),
                new("headingDegrees", "string", "Vehicle Y heading input in degrees; maximum 64 characters."),
                new("fields", "array", "Source-object draft fields; strings retain incomplete input until Apply. Field keys come from scene_inspect.draft.content.fields.", Items: new("", "object", "Source field", Properties: [new("field", "string", "Field key", true), new("value", "string", "Draft input, at most 4096 characters", true)]), MinItems: 1, MaxItems: 32), P("transformMode", "string", "Handles shown, as the transform header's toggles: move, rotate (requires supported axes) or none to hide them.", false, "move", "rotate", "none")], async (a, token) =>
        {
            var viewport = TargetViewport(a); var card = viewport.InspectionContent as SceneInspectionCard ?? throw new StudioCommandException("not_ready", "Inspection card unavailable.");
            string action = Text(a, "action");
            if (action is "select" or "clear")
            {
                if (viewport.IsFlyActive || viewport.IsPickupDragging) throw new StudioCommandException("busy", "Finish the transform drag or exit Fly before changing selection.");
                if (action == "select" && (a["target"] != null && a["node"] != null || a["runtime"] != null && (a["node"] == null || a["target"] != null)))
                    throw new StudioCommandException("invalid_argument", "Select by target, or by node with an optional runtime instance.");
                RequireNoSelectionDrafts(); bool selected;
                if (action == "clear") { selected = viewport.SelectInspection(null, false); if (selected) { card.ClearSourceNode(); card.Refresh(); } }
                else if (a["target"] != null) selected = card.Selection?.Target == Text(a, "target") || viewport.SelectInspection(Text(a, "target"), false);
                else if (a["node"] != null)
                {
                    long? runtime = a["runtime"] == null ? null : long.TryParse(Text(a, "runtime"), out long id) ? id : throw new StudioCommandException("invalid_argument", "Invalid runtime instance.");
                    selected = runtime == null && shownDocument is { SourceWorld: not null } sourceDocument
                        ? FocusSourceObjectCard(sourceDocument, Int(a, "node")) : viewport.SelectInspectionNode(Int(a, "node"), runtime);
                }
                else throw new StudioCommandException("invalid_argument", "Selection requires target or node.");
                if (!selected) throw new StudioCommandException("stale_record", "Target unavailable, or viewport interaction is busy.");
            }
            else if (action == "copy") return Result(new { text = card.Copy(a["field"] == null ? null : Text(a, "field"), Flag(a, "clipboard")) });
            else
            {
                var doc = TargetDocument(a);
                if (doc != shownDocument) throw new StudioCommandException("context_changed", "This is not the inspected document.");
                if (a["revision"]?.GetValue<long>() != doc.Revision) throw new StudioCommandException("revision_conflict", "Read the current document revision.");
                if (action == "begin") { RequireNoDrafts(); BeginInspectionEdit(card); }
                else
                {
                    if (action == "set" && card.ObjectEditor != null)
                    {
                        if (Text(a, "token") != card.DraftToken) throw new StudioCommandException("draft_conflict", "Read the current object draft.");
                        if (a["fields"] is not JsonArray fields && a["transformMode"] == null) throw new StudioCommandException("invalid_argument", "Source objects take fields and/or transformMode.");
                        string? mode = a["transformMode"]?.GetValue<string>();
                        if (mode != null && card.ObjectEditor.State.Transform == null) throw new StudioCommandException("read_only", "This object has no transform.");
                        if (mode is not (null or "move" or "rotate" or "none")) throw new StudioCommandException("invalid_argument", "Invalid transform mode.");
                        if (a["fields"] is JsonArray changed) card.SetObjectFields(changed);
                        if (mode != null) card.SetObjectMode(mode);
                        return Result(new { selected = card.Selection?.Target, draft = card.DescribeDraft(), revision = shownDocument?.Revision });
                    }
                    card.RequireDraft(Text(a, "token"));
                    if (action == "set") card.SetDraft(Text(a, "token"), (a["position"] as JsonArray)?.Select(p => p!.GetValue<string>()).ToArray(),
                        (a["rotationDegrees"] as JsonArray)?.Select(p => p!.GetValue<string>()).ToArray(), a["headingDegrees"]?.GetValue<string>(), a["transformMode"]?.GetValue<string>());
                    else if (action == "apply")
                    {
                        if (ApplyInspectionEditCore(card, token, Flag(a, "sharedScope")) is { } rebuilding)
                        {
                            var next = await rebuilding;
                            return Result(new { selected = CurrentInspectionCard?.Selection?.Target, draft = CurrentInspectionCard?.DescribeDraft(), revision = (long?)next.Revision, document = DocumentState(next) });
                        }
                    }
                    else card.CancelDraft();
                }
            }
            return Result(new { selected = card.Selection?.Target, draft = card.DescribeDraft(), revision = shownDocument?.Revision });
        });
    }
    private static JsonObject? InspectionAiStrategy(SceneViewport viewport, SceneInspection? item)
        => item?.AiNode is { } id && viewport.AiNetworks.Find(id) is { } ai ? DescribeAiStrategy(ai.Network) : null;
}
