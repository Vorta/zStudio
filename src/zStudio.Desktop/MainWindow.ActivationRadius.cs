using System.Globalization;
using System.Windows;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>
/// Activation radii of AI vehicles (design/activation-radius.md §5–7): which vehicles' rings the view shows (by World tree row
/// identity, for the session, per map), the rows' and the vehicle card's data, and the presentation pushed to the view. The
/// radius is the vehicle's AI network's <c>activate_rad</c>; editing it is a value of the vehicle card's placement draft.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The AI vehicles whose ring shows (all hidden at first), by placement key: the vehicle's AI Vehicles entry key as the tree
    /// links the entry across difficulties, the same on every difficulty and across coordinate edits, undo and LOD rebuilds (the
    /// entry's first linked AIV resource, by difficulty then record order, with its content fingerprinted with coordinates and
    /// headings masked, and the record's ordinal in it). Anything else changing in that resource (records reordered, renamed,
    /// added or removed outside zStudio) gives its entries new keys, so their rings are dropped rather than passed to another entry.
    /// A placed row without a placement key (a record not matched across the difficulty resources) is keyed by its row identity.
    /// </summary>
    private readonly HashSet<string> shownActivationRadii = new(StringComparer.Ordinal);
    private (MissionSceneContext? Mission, AiNetworkSnapshot? Networks, PickupPlacementEditSession? Edits, Dictionary<int, AiVehicleActivation?> Found) activationCache = (null, null, null, []);
    /// <summary>A tree's AI Vehicles rows: the placed ones' roots by key and keys by root, and every difficulty's placement keys.</summary>
    private (SceneTreeModel? Tree, Dictionary<string, int> Roots, Dictionary<int, string> Keys, HashSet<string> Listed) activationRows = (null, [], [], []);
    /// <summary>A vehicle row's ring key (see <see cref="shownActivationRadii"/>).</summary>
    private static string ActivationKey(SceneTreeModel tree, SourceMapPassEntry entry, int row) => entry.Key.Length > 0 ? entry.Key : "row:" + tree.Identity(row).Key;
    /// <summary>The network card's draft Activation Radius, which the shown rings of its network preview; null without one.</summary>
    private (string Network, float Radius)? sectionActivationPreview;

    /// <summary>
    /// The World tree whose rows resolve to the view's roots: the shown map's projected tree built from the scene the view shows.
    /// Null while a viewport swap or a rebuild has one without the other (the old tree's rows would name the new scene's roots):
    /// then nothing of the activation state is read or changed, and the push at the end of the tree rebuild does the work.
    /// </summary>
    private SceneTreeModel? ActivationTree => sceneTree is { Projection: not null } tree && sceneTreeDocument == shownDocument && scene is { PreviewScene: { } shown }
        && ReferenceEquals(tree.Hierarchy.Scene, shown) ? tree : null;
    AiVehicleActivation? ISourceSectionState.VehicleActivation(int root) => ActivationTree != null ? VehicleActivation(root) : null;
    bool ISourceSectionState.ActivationRadiiTruncated => sourceChromeActive && scene?.ActivationRadiiTruncated == true;
    bool ISourceSectionState.ActivationRadiusShown(SceneTreeItem row) => row.Owner == ActivationTree && row.Placement is { } entry && row.Index is int node && ActivationShown(ActivationKey(row.Owner, entry, node), node);
    /// <summary>
    /// The one definition of a shown ring, for the row button, the master, the card's toggle, scene_tree, preview_state and the
    /// rings pushed to the view: the vehicle's ring is chosen and its current root (placed on the shown difficulty) can show one.
    /// A choice whose vehicle can't (its network went missing) is kept but not shown.
    /// </summary>
    private bool ActivationShown(string key, int root) => root >= 0 && shownActivationRadii.Contains(key) && CanShowActivation(root);

    /// <summary>A placed vehicle of the shown RECOIL source map (an actor root): its network and radius; null for other nodes.</summary>
    private AiVehicleActivation? VehicleActivation(int root)
    {
        if (!sourceChromeActive || scene is not { Mission: { } mission } viewport || shownDocument is not { SourceWorld: not null } doc || doc.PickupEdits is not { } edits) return null;
        if (!ReferenceEquals(activationCache.Mission, mission) || !ReferenceEquals(activationCache.Networks, viewport.AiNetworks) || !ReferenceEquals(activationCache.Edits, edits))
            activationCache = (mission, viewport.AiNetworks, edits, []);
        if (activationCache.Found.TryGetValue(root, out var known)) return known;
        var found = mission.ActorAt(root) is { CoordinateSource: { } source } actor && actor.Root == root ? AiVehicleActivation.Resolve(edits.Coordinate(source), viewport.AiNetworks) : null;
        if (activationCache.Found.Count < 65_536) activationCache.Found[root] = found;
        return found;
    }
    /// <summary>
    /// Drops the caches of a retired tree, mission or placements (another tree, a closed map, a document without a source map);
    /// they fill again on demand. The ring choices are not touched: they belong to the map, across compatible rebuilds.
    /// </summary>
    private void ReleaseActivationCaches()
    {
        if (!ReferenceEquals(activationRows.Tree, sceneTree)) activationRows = (null, [], [], []);
        if (shownDocument?.SourceWorld == null || !ReferenceEquals(activationCache.Mission, scene?.Mission) || !ReferenceEquals(activationCache.Edits, shownDocument.PickupEdits))
            activationCache = (null, null, null, []);
    }
    private bool CanShowActivation(int root) => SceneTreeItem.CanShowActivation(VehicleActivation(root));
    /// <summary>The AI Vehicles rows the shown difficulty places, by ring key: their vehicle's root (-1 when two rows share one key).</summary>
    private Dictionary<string, int> ActivationRows(SceneTreeModel tree)
    {
        if (ReferenceEquals(activationRows.Tree, tree)) return activationRows.Roots;
        Dictionary<string, int> roots = new(StringComparer.Ordinal);
        Dictionary<int, string> keys = [];
        HashSet<string> listed = new(StringComparer.Ordinal);
        foreach (var pass in tree.Projection?.Passes.Where(p => p.Kind == "aiv") ?? [])
            foreach (var entry in pass.Entries)
            {
                // The placement key names the vehicle on every difficulty (empty when its records are not matched).
                if (entry.Key.Length > 0) listed.Add(entry.Key);
                if (entry.Row is not int row) continue;
                string key = ActivationKey(tree, entry, row); roots[key] = roots.ContainsKey(key) ? -1 : row; keys[row] = key;
            }
        activationRows = (tree, roots, keys, listed);
        return roots;
    }
    /// <summary>The ring key of a placed vehicle (an actor root) of the tree, or null.</summary>
    private string? ActivationKeyOf(SceneTreeModel tree, int root) { ActivationRows(tree); return activationRows.Keys.GetValueOrDefault(root); }
    /// <summary>
    /// The AI Vehicles row's Activation radius follows the vehicles of the shown difficulty that can show a ring, with the eyes'
    /// master rule: it turns on once all of them show theirs and off once none does (rings chosen on another difficulty do not count).
    /// </summary>
    private void ReconcileActivationMaster(SceneTreeModel tree)
    {
        bool any = false, all = true, none = true;
        foreach (var (key, root) in ActivationRows(tree))
        {
            if (root < 0 || !CanShowActivation(root)) continue;
            bool shown = ActivationShown(key, root); any = true; all &= shown; none &= !shown;
        }
        if (any && (all || none)) masterShown["aivRadius"] = all;
    }
    /// <summary>How many vehicles the shown difficulty places have their ring shown (preview_state).</summary>
    private int ShownActivationRadiiCount => ActivationTree is { } tree ? ActivationRows(tree).Count(p => ActivationShown(p.Key, p.Value)) : 0;
    /// <summary>
    /// Forgets the rings of vehicles no longer in the map: kept are those the shown difficulty places and those another
    /// difficulty still lists. A key that changed (see <see cref="shownActivationRadii"/>) is gone, so its ring goes with it.
    /// </summary>
    private void PruneActivationRadii(SceneTreeModel tree)
    {
        var roots = ActivationRows(tree);
        shownActivationRadii.RemoveWhere(key => !roots.ContainsKey(key) && !activationRows.Listed.Contains(key));
    }
    /// <summary>Why a vehicle shows no ring, or null when it can.</summary>
    private static string? ActivationRefusal(AiVehicleActivation activation) => activation.State switch
    {
        AiActivationState.NoNetwork => $"Network {activation.NetworkNumber}: this vehicle has no AI network, so it has no activation radius.",
        AiActivationState.MissingNetwork => $"net_{(activation.NetworkNumber ?? 0).ToString("00", CultureInfo.InvariantCulture)}.zrd is not in this mission: the vehicle stays inert.",
        AiActivationState.Unavailable => "Activation radii belong to RECOIL AI vehicles: this row has none.",
        _ => null,
    };

    /// <summary>
    /// The Activation radius button (GUI and MCP scene_tree showRadius/hideRadius): a vehicle row shows or hides its ring
    /// (<paramref name="show"/> null: toggles it), the AI Vehicles row every ring (null: what its master does next).
    /// Presentation only: no draft is asked about or changed.
    /// </summary>
    internal void SetActivationRadiusShown(SceneTreeItem row, bool? show = null)
    {
        if (row.Owner != sceneTree || !row.IsSourceMap) throw new StudioCommandException("stale_record", "The tree row is no longer available.");
        if (scene is not { } viewport || sceneTreeDocument != shownDocument) throw new StudioCommandException("not_ready", "Show the map first.");
        if (viewport.IsActivationRadiusDragging) throw new StudioCommandException("busy", SceneInspectionCard.ActivationDragRefusal);
        if (ActivationTree != row.Owner) throw new StudioCommandException("not_ready", "The World tree is still being rebuilt for the shown map; try again once it shows.");
        ResetSectionStateFor(shownDocument);
        var rows = ActivationRows(row.Owner);
        void Show(string key) => shownActivationRadii.Add(key);
        if (row.Pass is { Kind: "aiv" })
        {
            if (!row.HasActivationRadius) throw new StudioCommandException("unsupported", "No AI vehicle the shown difficulty places has an AI network, so there is no activation radius to show.");
            bool showAll = show ?? !MasterShown("aivRadius");
            // The master acts on the shown difficulty's vehicles that can show a ring: other choices stay as they are.
            foreach (var (key, root) in rows)
                if (root < 0 || !CanShowActivation(root)) continue;
                else if (!showAll) shownActivationRadii.Remove(key);
                else Show(key);
            masterShown["aivRadius"] = showAll;
        }
        else
        {
            if (row.IsOtherDifficulty && row.Parent?.Pass is { Kind: "aiv" })
                throw new StudioCommandException("unsupported", "The shown difficulty does not place this vehicle; choose one that does (zstudio_scene_options difficulty) to show its activation radius.");
            if (row.VehicleActivation is not { } activation || row.Index is not int node || row.Placement is not { } entry) throw new StudioCommandException("unsupported", "Activation radii belong to RECOIL AI vehicles: this row has none.");
            if (ActivationRefusal(activation) is { } reason) throw new StudioCommandException("unsupported", reason);
            string key = ActivationKey(row.Owner, entry, node);
            if (show ?? !ActivationShown(key, node)) Show(key); else shownActivationRadii.Remove(key);
        }
        ReconcileMasters();
        ApplySectionPresentation();
        // The vehicle card's header toggle follows.
        CurrentInspectionCard?.Refresh();
    }
    private void OutlinerActivationRadiusClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not SceneTreeItem { HasActivationRadius: true } row) return;
        try { SetActivationRadiusShown(row); }
        catch (StudioCommandException ex) { Report(ex); }
    }

    /// <summary>
    /// The rings to the view (map layout only: none in Animation mode or ZMAP mode): the shown vehicles, the selected one's handle
    /// while its card's field is editable, the selected network's (else the selected vehicle's) emphasis and a draft's preview.
    /// Choices for vehicles no longer in the map drop out. A ring budget overflow shows on the AI Vehicles row's tooltip.
    /// </summary>
    private void PushActivationRadii(SceneViewport viewport)
    {
        if (viewport != scene) return;
        HashSet<int> roots = [];
        bool mapLayout = !InAnimationMode && zmap == null;
        // The AI Vehicles row shows the master and the truncation: it follows when a push changes either.
        (bool Master, bool Truncated) before = (MasterShown("aivRadius"), viewport.ActivationRadiiTruncated);
        // Only from a tree of the scene the view shows: during a swap or rebuild no rings are pushed and no choice or master changes.
        if (ActivationTree is { } tree && shownDocument is { SourceWorld.IsRebuilding: false, PickupEdits: not null })
        {
            PruneActivationRadii(tree);
            // Only the shown difficulty's vehicles draw; another difficulty's chosen rings wait for it. The master follows the
            // shown difficulty's (after a difficulty switch or an edit, too).
            ReconcileActivationMaster(tree);
            var rows = ActivationRows(tree);
            if (mapLayout) foreach (var (key, root) in rows) if (ActivationShown(key, root)) roots.Add(root);
        }
        var card = viewport.InspectionContent as SceneInspectionCard;
        int? handle = mapLayout && card is { ActivationEditable: true, Selection: { } selected } && viewport.Mission?.ActorAt(selected.Node)?.Root is int vehicle && roots.Contains(vehicle) ? vehicle : null;
        string? emphasized = SectionSelection?.Section is NetworkEntry network ? network.Network.Id : card?.Selection != null ? card.ActivationNetwork : null;
        viewport.SetActivationRadiusPresentation(roots, handle, emphasized);
        var preview = ActivationPreviewWanted();
        viewport.SetActivationRadiusPreview(preview?.Network, preview?.Radius);
        if (before != (MasterShown("aivRadius"), viewport.ActivationRadiiTruncated))
            foreach (var row in sceneTree?.Roots.Where(r => r.Pass is { Kind: "aiv" }) ?? []) row.NotifyHidden();
    }
    /// <summary>
    /// Pushes the rings after a change of one of their inputs outside the section presentation: the layout (Animation mode entered,
    /// left or switched, ZMAP), a shown or replaced view, source chrome on or off, a rebuilt or replaced tree.
    /// </summary>
    private void RefreshActivationPresentation() { if (scene is { } viewport) PushActivationRadii(viewport); }
    /// <summary>The draft radius the shown rings preview: the vehicle card's, else the network card's.</summary>
    /// <remarks>The network card's counts only while its draft is still open on that network's selected row: a tree replaced,
    /// a map closed or another selection made without a draft change leaves no stale preview behind.</remarks>
    private (string Network, float Radius)? ActivationPreviewWanted() => (scene?.InspectionContent as SceneInspectionCard)?.ActivationPreview
        ?? (sectionActivationPreview is { } section && SectionSelection?.Section is NetworkEntry selected && selected.Network.Id == section.Network
            && sectionCard is { HasDraft: true, Entry: NetworkEntry shown } && shown.Network.Id == section.Network ? section : null);
    /// <summary>The network card's draft changed or another entry shows: its Activation Radius previews while it parses (an invalid text keeps the last).</summary>
    private void RefreshSectionActivationPreview()
    {
        (string Network, float Radius)? next = null;
        if (sectionCard is { Entry: NetworkEntry entry, ActivationRadiusDraft: { } text } && SectionSelection?.Section is NetworkEntry)
            next = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) ? (entry.Network.Id, Math.Abs(value))
                : sectionActivationPreview is { } kept && kept.Network == entry.Network.Id ? kept : null;
        // Pushed even when unchanged: a remembered preview the view dropped (a rebuild, another network snapshot) comes back.
        sectionActivationPreview = next;
        if (scene is { } viewport && sourceChromeActive && ActivationPreviewWanted() is var preview) viewport.SetActivationRadiusPreview(preview?.Network, preview?.Radius);
    }

    // ---------------------------------------------------------------- the vehicle card

    /// <summary>The selected AI vehicle's Activation section in the map layout of a RECOIL source map; null for anything else.</summary>
    private ActivationCardInfo? ActivationCardInfoFor(SceneViewport viewport, SceneInspection selected)
    {
        if (viewport != scene || !sourceChromeActive || InAnimationMode || selected.AiNode != null || shownDocument is not { SourceWorld: not null } doc) return null;
        if (viewport.Mission?.ActorAt(selected.Node) is not { CoordinateSource: not null } actor || VehicleActivation(actor.Root) is not { State: not AiActivationState.Unavailable } activation) return null;
        string? stored = null, locked = null;
        if (activation.Network is { } network) (stored, locked) = ActivationStored(doc, network);
        int vehicles = 0;
        if (activation.Network != null && viewport.Mission is { } mission)
            foreach (int root in mission.Actors.Select(a => a.Root).Distinct()) if (VehicleActivation(root) is { Network: { } other } && other.Id == activation.Network.Id) vehicles++;
        string? key = ActivationTree is { } tree ? ActivationKeyOf(tree, actor.Root) : null;
        return new(activation.State, activation.Network?.Id, activation.Network is { } shown ? SourceMapSections.NetworkStem(shown) : null, activation.NetworkNumber, stored, vehicles,
            key != null && ActivationShown(key, actor.Root), key != null && SceneTreeItem.CanShowActivation(activation), locked);
    }
    /// <summary>A network's <c>activate_rad</c> as its file stores it (bounded; null when not stored), and why the vehicle card can't edit it (or null).</summary>
    private static (string? Stored, string? Locked) ActivationStored(DocumentModel doc, AiNetwork network)
    {
        var resource = (doc.Document as SourceSceneSnapshot)?.Resources.Values.FirstOrDefault(r => string.Equals(r.Path, network.Id, StringComparison.OrdinalIgnoreCase));
        string file = System.IO.Path.GetFileName(network.Id);
        var source = network.Source ?? resource?.Root;
        var field = source != null ? SourceAiNetworkEdits.Read(source).Fields["activate_rad"] : null;
        string? stored = field is { } value ? JsonData.ShownText(value.Text(), 64) : null;
        string? locked = resource == null ? $"{file} is not a source file of this map, so its activation radius can't be edited here."
            : resource.Root == null ? $"{file} could not be read, so its activation radius can't be edited here; the map's notes say why."
            : resource.Syntax == null ? $"{file} is stored as binary zReader data, so its activation radius can be edited only in a text source."
            : field is { Repeated: true } ? "Stored twice in the file; the game reads the first. Fix it in a text editor to edit it here."
            : field != null && (field.Values.Count != 1 || field.Values.Any(v => v.Kind is not (ZrdKind.String or ZrdKind.Int or ZrdKind.Float))) ? "Not stored as plain values. Fix it in a text editor to edit it here."
            : null;
        return (stored, locked);
    }
    /// <summary>The card's header toggle: the selected vehicle's row's button.</summary>
    private void ShowSelectedActivation(SceneViewport viewport, SceneInspection selected, bool show)
    {
        if (viewport != scene || viewport.Mission?.ActorAt(selected.Node) is not { } actor || ActivationTree is not { } tree)
            throw new StudioCommandException("not_ready", "Select an AI vehicle of the shown map first.");
        SceneTreeItem? row;
        changingSceneTree = true;
        try { row = tree.Reveal(actor.Root, expand: false); }
        finally { changingSceneTree = false; }
        SetActivationRadiusShown(row ?? throw new StudioCommandException("not_ready", "The vehicle's World tree row is not available."), show);
    }
    /// <summary>The card's network link: the network's row is selected (its card shows).</summary>
    /// <remarks>A draft applied on the way (Yes) rebuilds the map: the link waits for it, then selects the network's row of the
    /// tree shown then, if the same map is still shown.</remarks>
    private void SelectActivationNetwork(string network) => _ = ReportUi(async () =>
    {
        string? map = shownDocument?.SourceWorld != null ? shownDocument.Path : null;
        if (map == null || !await ResolveSelectionDraftsAsync(sceneTreeDocument)) return;
        if (!sourceChromeActive || shownDocument is not { SourceWorld: not null } doc || !string.Equals(doc.Path, map, StringComparison.OrdinalIgnoreCase)
            || sceneTreeDocument != doc || sceneTree?.FindSection("network:" + network) is not { } row) return;
        if (sceneTree.Selected != row) SelectSectionRow(row);
    });
    /// <summary>scene_inspect's details of an AI vehicle: its network and the radius as stored (the Activation section's rows).</summary>
    private void DescribeActivation(System.Text.Json.Nodes.JsonObject info, SceneViewport viewport, SceneInspection item)
    {
        if (ActivationCardInfoFor(viewport, item) is not { } card) return;
        info[SceneInspectionCard.ActivationNetworkDetail] = card.State switch
        {
            AiActivationState.NoNetwork => $"None (network {card.NetworkNumber})",
            AiActivationState.MissingNetwork => $"net_{(card.NetworkNumber ?? 0).ToString("00", CultureInfo.InvariantCulture)}.zrd · not in this mission: the vehicle stays inert",
            _ => $"{card.NetworkStem} · {JsonData.ShownText(card.NetworkId!, 512)}",
        };
        info[SceneInspectionCard.ActivationRadiusDetail] = card.State switch
        {
            AiActivationState.Radius => card.Stored,
            AiActivationState.Asleep => $"{card.Stored ?? "Not stored"} · wakes only when alerted",
            _ => "None",
        };
    }
    /// <summary>A scene_tree vehicle row's activation (null where the feature does not apply).</summary>
    private static object? DescribeActivationRow(SceneTreeItem row) => row.VehicleActivation is not { } activation ? null : new
    {
        state = activation.State switch { AiActivationState.Radius => "radius", AiActivationState.Asleep => "asleep", AiActivationState.NoNetwork => "noNetwork", _ => "missingNetwork" },
        network = activation.Network is { } network ? JsonData.ShownText(network.Id, 512) : null, networkNumber = activation.NetworkNumber,
        radius = SceneTreeItem.CanShowActivation(activation) ? activation.Radius : (float?)null,
        shown = SceneTreeItem.CanShowActivation(activation) && row.ActivationRadiusOn,
    };
}
