using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private SceneInspectionCard? inspectionDraft;
    private SceneInspectionCard? CurrentInspectionCard => (motion?.Viewport ?? animation?.Viewport ?? scene)?.InspectionContent as SceneInspectionCard;
    private bool HasInspectionDraft => inspectionDraft?.HasDraft == true;

    private void AttachInspection(SceneViewport viewport)
    {
        var card = new SceneInspectionCard(viewport, item => DescribeInspection(viewport, item), BeginInspectionEdit, ApplyInspectionEdit);
        card.ValvesRequested += async id => await ResourceUiAsync(() => OpenNodeValvesAsync(id));
        card.SetPanelHeight(Layout.InspectionPanelHeight);
        card.PanelHeightChanged += height => Layout.InspectionPanelHeight = height;
        card.DraftClosed += () => { if (ReferenceEquals(inspectionDraft, card)) inspectionDraft = null; };
        viewport.InspectionContent = card;
        viewport.CanChangeInspection = () => ResolveInspectionDrafts();
        viewport.InspectionChanged += () => SceneTreeInspectionChanged(viewport);
        viewport.InspectionSelectionCleared += () => ClearSceneTreeSelection(viewport);
    }
    private bool ResolveInspectionDrafts(DocumentModel? doc = null)
        => !HasInspectionDraft || doc != null && inspectionDraft!.DraftDocument != doc || inspectionDraft!.ResolvePending();

    private MissionPickupSource? InspectionSource(SceneViewport viewport, SceneInspection inspection)
    {
        if (viewport != scene || animation != null || shownAsset?.Kind != AssetKind.World || !inspection.Active) return null;
        if (inspection.AiNode is { } id && viewport.AiNetworks.Find(id) is { } ai)
            return new(System.IO.Path.GetFullPath(ai.Network.Archive).ToUpperInvariant(), ai.Network.MemberIndex, ai.Network.Member.ToUpperInvariant(), checked((int)ai.Node.SourceOffset));
        var actor = viewport.ActorAt(inspection.Node);
        if (actor == null || viewport.Mission!.Actors.Count(a => a.Root == actor.Root) != 1) return null;
        return actor.Pickup?.Source ?? actor.CoordinateSource;
    }
    private JsonObject DescribeInspection(SceneViewport viewport, SceneInspection item)
    {
        static string Short(string text) => text.Length > 2048 ? text[..2048] + "…" : text;
        var data = viewport.PreviewScene;
        var node = item.Node >= 0 && item.Node < data?.Nodes.Count ? data.Nodes[item.Node] : null;
        int sourceNode = item.Node >= 0 && item.Node < viewport.Mission?.SourceNodes.Count ? viewport.Mission.SourceNodes[item.Node] : item.Node;
        JsonObject info = new()
        {
            ["Node"] = node == null ? $"Model #{item.Model}" : Short(node.Name) + $" · {node.Class} #{item.Node}",
            ["Target"] = item.Target,
            ["Status"] = item.Active ? "Visible" : "Inactive runtime instance / unavailable geometry",
        };
        if (item.Surface is { } hit) info["Surface world XYZ"] = JsonData.Vector(hit);
        if (item.Normal is { } normal) info["Surface normal"] = JsonData.Vector(normal);
        if (item.Origin is { } origin) info["Object world origin XYZ"] = JsonData.Vector(origin);
        if (viewport.InspectionSourcePath is { } path) info["Source scene"] = Short(path);
        if (node != null)
        {
            info["Source node"] = sourceNode;
            info["Parents"] = new JsonArray(node.Parents.Take(32).Select(i => JsonValue.Create(i)).ToArray());
            info["Scene local XYZ"] = JsonData.Vector(SceneBuilder.LocalTransform(node).Translation);
            info["Model"] = item.Model;
            if (item.Model >= 0 && item.Model < data!.Models.Count)
                info["Model geometry"] = $"{data.Models[item.Model].Vertices.Length} vertices · {data.Models[item.Model].Polygons.Length} polygons";
            info["LOD rank"] = motion?.Lod ?? animation?.PreviewLod ?? LodCombo.SelectedIndex;
            info["Material"] = item.Material;
            var flags = WorldSurfaceHighlights.NodeKind(data!, item.Node);
            info["CanModify"] = flags.HasFlag(WorldSurfaceKind.CanModify); info["ClipTo"] = flags.HasFlag(WorldSurfaceKind.ClipTo);
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
        var actor = viewport.ActorAt(item.Node);
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
        }
        var edits = shownDocument?.PickupEdits;
        bool known = source != null && edits != null && (edits.Find(source) != null || edits.Coordinate(source) != null);
        if (known)
        {
            info["Authored placement XYZ"] = JsonData.Vector(edits!.Position(source!));
            info["Source archive"] = Short(source!.ArchivePath); info["Source resource"] = source.ResourceName + $" · member #{source.AssetIndex} · record #{source.RecordIndex}";
            info["Save archive"] = Short(edits.TargetPath(source.ArchivePath));
            info["Edit scope"] = edits.Scope(source).Description;
            var rotation = edits.Rotation(source);
            if (edits.RotationKind(source) == PlacementRotationKind.EulerRadians)
            {
                info["Rotation radians"] = JsonData.Vector(rotation);
                info[SceneInspectionCard.AuthoredRotation] = new JsonObject { ["x"] = rotation.X * (180 / Math.PI), ["y"] = rotation.Y * (180 / Math.PI), ["z"] = rotation.Z * (180 / Math.PI) };
            }
            if (edits.Coordinate(source) is { Kind: "tank" } tank) { info["Template"] = tank.Template; info[SceneInspectionCard.AuthoredHeading] = rotation.Y; }
        }
        if (known && edits!.Coordinate(source!) is { Kind: "tank" } vehicle && (!vehicle.MissionSpecific && (vehicle.TemplateSourceNode == null || vehicle.Difficulties.Count == 0))) known = false;
        bool locked = known && shownDocument!.PickupsLocked;
        info["Editable"] = known && !locked;
        info["Rotation axes"] = known ? edits!.RotationKind(source!) switch { PlacementRotationKind.EulerRadians => "XYZ", PlacementRotationKind.HeadingDegrees => "Y", _ => "None" } : "None";
        info["Editing"] = locked ? "Unlock editing to select and edit objects" : known ? "Edit position and supported rotation; confirm together as one undo step" : "Read-only inspection";
        info["Document revision"] = shownDocument?.Revision;
        return info;
    }
    private void BeginInspectionEdit(SceneInspectionCard card)
    {
        if (scene != null && shownDocument?.PickupsLocked == true) throw new StudioCommandException("locked", "Unlock editing first.");
        if (card != CurrentInspectionCard || scene == null || shownDocument is not { IsDisposed: false } doc || card.Selection is not { } selection)
            throw new StudioCommandException("not_ready", "Select a mission placement first.");
        if (!ResolvePropertiesDrafts(doc)) return;
        var source = InspectionSource(scene, selection);
        var edits = doc.PickupEdits;
        if (source == null || edits == null || edits.Find(source) == null && edits.Coordinate(source) == null)
            throw new StudioCommandException("read_only", "This node has no verified editable placement.");
        if (edits.Coordinate(source) is { Kind: "tank" } vehicle && (!vehicle.MissionSpecific && (vehicle.TemplateSourceNode == null || vehicle.Difficulties.Count == 0)))
            throw new StudioCommandException("read_only", "The tank template is missing or ambiguous.");
        if (doc.PickupsLocked) throw new StudioCommandException("locked", "Unlock editing first.");
        if (edits.HasExternalChanges()) throw new StudioCommandException("external_change", "An owning archive changed. Reload or preserve your existing edits with Save As.");
        inspectionDraft = card; card.StartDraft(doc, source, edits.Position(source));
    }
    private void ApplyInspectionEdit(SceneInspectionCard card)
    {
        if (scene?.IsPickupDragging == true) throw new StudioCommandException("busy", "Finish or cancel the active transform drag before confirming.");
        var doc = card.DraftDocument;
        if (!card.HasDraft || doc == null || doc.IsDisposed || doc != shownDocument || card != CurrentInspectionCard || card.Selection?.Target != card.DraftTarget)
            throw new StudioCommandException("stale_record", "The draft's document or selected instance is no longer available.");
        if (doc.Revision != card.DraftRevision) throw new StudioCommandException("revision_conflict", "The document changed. Discard this draft and start again.");
        var edits = doc.PickupEdits!;
        if (doc.PickupsLocked) throw new StudioCommandException("locked", "Coordinate editing is locked.");
        if (edits.HasExternalChanges()) throw new StudioCommandException("external_change", "An owning archive changed outside zStudio.");
        edits.TransformTo(card.DraftSource!, card.DraftTransform());
        card.CancelDraft(); UpdateDocumentCommands();
    }

    private void RegisterInspectionCommands(StudioCommands commands)
    {
        Register(commands, "scene_inspect", "Read independent hover/selection information from the fixed top-right inspection panel, or query an explicit viewport point/target without moving the mouse. The permanent hover readout clears its coordinates over empty space. Coordinates distinguish triangle hits, object origins and authored placement positions. AI results include attackStrategy/hoverAttackStrategy with stored value, status, key, character count, truncation and network RGB hex color. Panel state includes preferred/effective heights, viewport limits and expanded state.", false,
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
            else if (a["target"] != null) item = viewport.InspectTarget(Text(a, "target")) ?? throw new StudioCommandException("stale_record", "Inspection target expired.");
            else item = viewport.SelectedInspection ?? viewport.HoverInspection;
            return Result(new { preview = previewId, document = shownDocument?.SessionId, revision = shownDocument?.Revision,
                inspection = item == null ? null : DescribeInspection(viewport, item), selected = viewport.SelectedInspection?.Target,
                hover = viewport.HoverInspection == null ? null : DescribeInspection(viewport, viewport.HoverInspection),
                attackStrategy = InspectionAiStrategy(viewport, item), hoverAttackStrategy = InspectionAiStrategy(viewport, viewport.HoverInspection),
                draft = (viewport.InspectionContent as SceneInspectionCard)?.DescribeDraft(),
                panel = (viewport.InspectionContent as SceneInspectionCard)?.DescribePanel() });
        });
        Register(commands, "scene_card", "Expand/clear selected-object details below the permanent top-right hover readout, copy fields, or begin/set/apply/cancel the shared position/rotation draft. Whole world selection requires unlocked editing. Move and supported Rotate controls and handles appear only during an edit draft; unsupported Rotate stays hidden; transforms preview until apply creates one undo step. Edit actions require document/revision; set/apply/cancel require the current draft token. Copy optionally writes the clipboard.", true,
            [PreviewParameter, P("action", "string", "Card action.", true, "select", "clear", "copy", "begin", "set", "apply", "cancel"),
                P("target", "string", "Opaque inspection target for select."), P("node", "integer", "Alternative scene node for selection."),
                P("runtime", "string", "Optional runtime instance ID with node."), P("field", "string", "Copy field label; omitted copies all details."), P("clipboard", "boolean", "Write copy text to clipboard; default false."),
                P("document", "string", "Owning document ID for edits."), new("revision", "integer", "Expected document revision for edits.", Minimum: 0, Maximum: long.MaxValue),
                P("token", "string", "Current draft lifetime and input token. Reopening a draft creates a new token."), new("position", "array", "Three coordinate input strings for set; maximum 64 characters each; permits temporary incomplete drafts.", Items: new("", "string", "Coordinate input."), MinItems: 3, MaxItems: 3),
                new("rotationDegrees", "array", "Pickup XYZ Euler input strings in degrees; maximum 64 characters each.", Items: new("", "string", "Angle input."), MinItems: 3, MaxItems: 3),
                new("headingDegrees", "string", "Vehicle Y heading input in degrees; maximum 64 characters."), P("transformMode", "string", "Draft handle mode; rotate requires supported axes.", false, "move", "rotate")], a =>
        {
            var viewport = TargetViewport(a); var card = viewport.InspectionContent as SceneInspectionCard ?? throw new StudioCommandException("not_ready", "Inspection card unavailable.");
            string action = Text(a, "action");
            if (action is "select" or "clear")
            {
                if (action == "select" && !viewport.InspectionSelectionEnabled) throw new StudioCommandException("locked", "Unlock editing before opening a Whole world object card.");
                if (action == "select" && (a["target"] != null && a["node"] != null || a["runtime"] != null && (a["node"] == null || a["target"] != null)))
                    throw new StudioCommandException("invalid_argument", "Select by target, or by node with an optional runtime instance.");
                RequireNoDrafts(); bool selected;
                if (action == "clear") selected = viewport.SelectInspection(null, false);
                else if (a["target"] != null) selected = viewport.SelectInspection(Text(a, "target"), false);
                else if (a["node"] != null)
                {
                    long? runtime = a["runtime"] == null ? null : long.TryParse(Text(a, "runtime"), out long id) ? id : throw new StudioCommandException("invalid_argument", "Invalid runtime instance.");
                    selected = viewport.SelectInspectionNode(Int(a, "node"), runtime);
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
                    card.RequireDraft(Text(a, "token"));
                    if (action == "set") card.SetDraft(Text(a, "token"), (a["position"] as JsonArray)?.Select(p => p!.GetValue<string>()).ToArray(),
                        (a["rotationDegrees"] as JsonArray)?.Select(p => p!.GetValue<string>()).ToArray(), a["headingDegrees"]?.GetValue<string>(), a["transformMode"]?.GetValue<string>());
                    else if (action == "apply") ApplyInspectionEdit(card);
                    else card.CancelDraft();
                }
            }
            return Result(new { selected = viewport.SelectedInspection?.Target, draft = card.DescribeDraft(), revision = shownDocument?.Revision });
        });
    }
    private static JsonObject? InspectionAiStrategy(SceneViewport viewport, SceneInspection? item)
        => item?.AiNode is { } id && viewport.AiNetworks.Find(id) is { } ai ? DescribeAiStrategy(ai.Network) : null;
}
