using zStudio.Core;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    /// <summary>Accepts prepared source transforms while retaining the camera, mesh buffers, texture cache and picking identities.</summary>
    public bool ApplySourceTransforms(SceneTransformUpdate update, MissionSceneContext mission, PickupPlacementEditSession edits, Action accept, Action<string>? refused = null)
    {
        bool Refuse(string reason) { refused?.Invoke(reason); return false; }
        if (animationContext != null) return Refuse("renderer has animation context");
        if (HasTransformDraft || IsPickupDragging) return Refuse("renderer has active placement draft");
        if (HasParachutePreview) return Refuse("renderer has active VTOL preview");
        // Normalize provisional rows without clearing the preview: clearing publishes inspection callbacks.
        // All fallible identity checks and row allocation happen before accepting either side of the update.
        ScenePlacement Original(ScenePlacement row) => objectPreviewOriginals?.GetValueOrDefault(row, row) ?? row;
        if (placements.Values.Concat(visiblePlacements.Values).Concat(boundsPlacements.Values).SelectMany(rows => rows).Any(row => !update.Placements.ContainsKey(Original(row)))) return Refuse("renderer placement is absent from prepared update");
        var nextMeshes = placements.ToDictionary(pair => pair.Key, pair => pair.Value.Select(row => update.Placements[Original(row)]).ToArray());
        var nextVisible = visiblePlacements.ToDictionary(pair => pair.Key, pair => pair.Value.Select(row => update.Placements[Original(row)]).ToArray());
        var nextBounds = boundsPlacements.ToDictionary(pair => pair.Key, pair => pair.Value.Select(row => update.Placements[Original(row)]).ToArray());
        objectPreviewMeshes = null; objectPreviewBounds = null; objectPreviewOriginals = null; ObjectTransformPreviewNode = null; objectPreviewToken = 0;
        Mission = mission; PreviewScene = mission.Scene;
        ConfigureHorizon(mission.Scene);
        RefreshZoneModels(update.ZoneModels);
        placementDeltas.Clear(); pickupBasePlacements.Clear();
        foreach (var (mesh, rows) in nextMeshes) { placements[mesh] = rows; pickupBasePlacements[mesh] = rows; }
        foreach (var (mesh, rows) in nextVisible)
        { visiblePlacements[mesh] = rows; mesh.Instances = rows.Select(row => HorizonTransform(row.NodeIndex, row.Transform)).ToList(); }
        foreach (var (box, rows) in nextBounds) boundsPlacements[box] = rows;
        foreach (var actor in mission.Actors.Where(actor => actor.Pickup != null))
        { pickupActors[actor.Root] = actor; pickupPositions[actor.Root] = WorldTransform(mission.Scene, actor.Root).Translation; }
        // Boxes outline what the view shows (hidden and isolated objects excluded).
        RefreshBoundsBoxes();
        // Instance dictionaries and source state are committed without a dispatcher yield. Only then may
        // coordinates/inspection publish callbacks, which can read both the document and this viewport.
        accept();
        SetMissionCoordinates(edits);
        RefreshObjectPreview();
        return true;
    }
}
