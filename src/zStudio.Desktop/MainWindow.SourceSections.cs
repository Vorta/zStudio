using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>
/// The World tree's sections beyond the scene: Zones (the zone overlay, each zone's colour and its label), AI Networks (the
/// AI overlay, each network's markers and its header settings) and Animations (definitions) (read-only, outlining what each
/// binds to). Their eyes are view only and last for the session; their cards edit the sources through the shared edit path.
/// </summary>
public partial class MainWindow : ISourceSectionState
{
    private readonly ConditionalWeakTable<GameScene, SourceMapSections> mapSections = new();
    /// <summary>The zone overlay shown when the Zones eye turns on: polygon zones until another kind is chosen.</summary>
    private WorldHighlightMode zoneOverlayKind = WorldHighlightMode.PolygonZones;
    private readonly HashSet<byte> hiddenZones = [];
    private byte? isolatedZone;
    private readonly HashSet<string> hiddenNetworks = new(StringComparer.Ordinal);
    /// <summary>The map (its path) the zone and network eyes belong to: another map starts with every zone and network shown.</summary>
    private string? sectionStateMap;
    private SourceSectionCard? sectionCard;

    // ---------------------------------------------------------------- what the rows show

    bool ISourceSectionState.ZoneOverlayOn => ZoneOverlayOn;
    private bool ZoneOverlayOn => worldHighlightMode is WorldHighlightMode.Zones or WorldHighlightMode.PolygonZones;
    bool ISourceSectionState.ZoneOverlayPolygons => (ZoneOverlayOn ? worldHighlightMode : zoneOverlayKind) == WorldHighlightMode.PolygonZones;
    bool ISourceSectionState.ZoneToolEngaged => ZoneToolEngaged;
    byte? ISourceSectionState.PaintZone => ZoneToolEngaged ? zoneTool.Zone : null;
    bool ISourceSectionState.ZoneHidden(byte zone) => hiddenZones.Contains(zone);
    bool ISourceSectionState.ZoneIsolated(byte zone) => isolatedZone == zone;
    bool ISourceSectionState.AiOn => aiVisible;
    bool ISourceSectionState.AiSeeThrough => aiThroughGeometry;
    bool ISourceSectionState.NetworkHidden(string network) => hiddenNetworks.Contains(network);
    bool ISourceSectionState.NetworkIsolated(string network) => aiNetworkFilter == network;

    private SceneTreeItem? SectionSelection => sceneTree?.Selected is { IsSectionItem: true } row ? row : null;

    // ---------------------------------------------------------------- reading the sections

    /// <summary>The shown map's zones, AI networks and definitions, read once per shown scene.</summary>
    private SourceMapSections? MapSections(DocumentModel doc, GameScene data, MissionSceneContext? mission, SourceMapSelection projection)
    {
        if (mapSections.TryGetValue(data, out var known)) return known;
        if (doc.Document is not SourceSceneSnapshot snapshot || doc.SourceBuild is not { } build || doc.SourceWorld is not { } session) return null;
        long[] polygons = new long[256]; int[] placements = new int[256]; HashSet<string>?[] models = new HashSet<string>[256]; long none = 0;
        foreach (var (node, slot) in zStudio.Core.Worlds.GameZWriter.NodeSlots(snapshot.World))
        {
            if (node.Model is not { } model) continue;
            var origin = build.Provenance.GetValueOrDefault(slot);
            var mapping = origin?.ZonePolygons;
            HashSet<int> seen = []; HashSet<byte> touched = [];
            // Each placement counts its source polygons once (a build can split one into several).
            for (int i = 0; i < model.Polygons.Count; i++)
            {
                if (mapping != null && (i >= mapping.Count || !seen.Add(mapping[i]))) continue;
                if (SourceZoneEdits.PolygonZoneIds(model.Polygons[i].Zone) is not { } ids) continue;
                if (ids.Length == 0) { none++; continue; }
                foreach (byte zone in ids) { polygons[zone]++; touched.Add(zone); }
            }
            foreach (byte zone in touched) { placements[zone]++; (models[zone] ??= new(StringComparer.OrdinalIgnoreCase)).Add(origin?.ModelFile ?? "slot:" + slot.ToString(CultureInfo.InvariantCulture)); }
        }
        // The objects, geometry and empty nodes whose zone it is, as the outliner shows them (the world and the objects startup
        // animations place).
        int[] own = new int[256], inherited = new int[256], empties = new int[256];
        Stack<int> pending = new(projection.Roots.Concat(projection.Passes.Where(p => p.Kind == "anim").SelectMany(p => p.Roots)));
        HashSet<int> visited = [];
        while (pending.TryPop(out int row) && visited.Count < 200_000)
        {
            if (!visited.Add(row)) continue;
            foreach (var child in projection.Children(row)) pending.Push(child.Node);
            if (row < 0 || row >= data.Nodes.Count || projection.IsRuntimeCopy(row)) continue;
            var node = data.Nodes[row];
            bool empty = !projection.IsBoundary(row) && node.Class == "object3d" && node.ModelIndex == null && projection.Children(row).Count == 0;
            if (!projection.IsBoundary(row) && !(node.Class == "object3d" && node.ModelIndex != null) && !empty) continue;
            if (node.Metadata["zone_id"] is not JsonValue value || !(value.TryGetValue<uint>(out uint word) || value.TryGetValue<int>(out int signed) && (word = unchecked((uint)signed)) == word)) continue;
            int source = mission != null && row < mission.SourceNodes.Count ? mission.SourceNodes[row] : row;
            bool isInherited = build.Provenance.TryGetValue(source, out var origin) && origin.ImportedZoneInherited == true;
            if (isInherited) inherited[word & 0xFF]++; else own[word & 0xFF]++;
            if (empty) empties[word & 0xFF]++;
        }
        var labels = ZoneLabels(session);
        List<SourceZoneRow> zones = [];
        foreach (int zone in Enumerable.Range(0, 255).Append(255))
            if (polygons[zone] > 0 || own[zone] + inherited[zone] > 0 || zone != 255 && labels.ContainsKey((byte)zone))
                zones.Add(new((byte)zone, zone == 255 ? null : labels.GetValueOrDefault((byte)zone), polygons[zone], placements[zone], models[zone]?.Count ?? 0, own[zone], inherited[zone], empties[zone]));
        // The objects each definition binds to: the nodes its entries bind to, as tree rows.
        var entryRoots = mission?.AnimationEntryRoots ?? new Dictionary<int, int>();
        Dictionary<int, int[]> definitionRoots = [];
        foreach (var definition in snapshot.AnimationDefinitions.Definitions)
        {
            if (definition.Entries.Count == 0) continue;
            int[] rows = [.. definition.Entries.Select(e => entryRoots.TryGetValue(e, out int bound) ? projection.Owner(bound) : (int?)null).OfType<int>().Distinct()];
            if (rows.Length > 0) definitionRoots[definition.Index] = rows;
        }
        SourceMapSections sections = new()
        {
            Mission = session.Mission, Zones = zones, NoZonePolygons = none, Networks = mission?.AiNetworks.Networks ?? [],
            Definitions = snapshot.AnimationDefinitions, DefinitionRoots = definitionRoots,
        };
        mapSections.AddOrUpdate(data, sections);
        return sections;
    }

    // ---------------------------------------------------------------- presentation

    /// <summary>A map's zone and network eyes are its own: another map starts with everything shown.</summary>
    private void ResetSectionStateFor(DocumentModel? doc)
    {
        string? map = doc?.SourceWorld != null ? doc.Path : null;
        if (string.Equals(map, sectionStateMap, StringComparison.OrdinalIgnoreCase)) return;
        sectionStateMap = map; hiddenZones.Clear(); isolatedZone = null; hiddenNetworks.Clear();
    }

    /// <summary>Applies the zone and network eyes, the selection's emphasis and a definition's outlines to the view; the rows follow.</summary>
    private void ApplySectionPresentation()
    {
        ResetSectionStateFor(shownDocument);
        if (scene is not { } viewport || shownDocument?.SourceWorld == null) { RefreshRowEyes(); return; }
        // Zones and networks no longer in the map drop out.
        if (sceneTree?.Sections is { } sections)
        {
            hiddenNetworks.RemoveWhere(id => sections.Network(id) == null);
            if (isolatedZone is byte isolated && sections.Zone(isolated) == null) isolatedZone = null;
        }
        HashSet<byte> hidden = isolatedZone is byte only ? [.. Enumerable.Range(0, 256).Select(z => (byte)z).Where(z => z != only)] : [.. hiddenZones];
        // A hidden zone or network is not emphasised (the others would all turn faint around nothing).
        byte? zone = ZoneOverlayOn && SectionSelection?.Section is ZoneEntry selectedZone && !hidden.Contains(selectedZone.Zone.Zone) ? selectedZone.Zone.Zone : null;
        viewport.SetZonePresentation(hidden, zone);
        // A selected network, or the network of a selected node, is emphasised.
        string? network = SectionSelection?.Section is NetworkEntry selectedNetwork ? selectedNetwork.Network.Id
            : viewport.SelectedAiNode is { } node && viewport.AiNetworks.Find(node) is { } found ? found.Network.Id : null;
        viewport.SetAiNetworkPresentation(hiddenNetworks, network != null && hiddenNetworks.Contains(network) ? null : network);
        int[] outlined = SectionSelection?.Section is DefinitionEntry definition && sceneTree?.Sections?.DefinitionRoots.GetValueOrDefault(definition.Definition.Index) is { } roots
            ? [.. roots.Take(SceneViewport.MaximumOutlinedNodes)] : [];
        if (!outlined.SequenceEqual(viewport.OutlinedNodes)) viewport.SetOutlinedNodes(outlined);
        RefreshRowEyes(); RefreshViewportChips();
    }

    // ---------------------------------------------------------------- eyes and header controls

    /// <summary>A section row's eye: the Zones or AI Networks overlay on or off, a zone's colour or a network's markers.</summary>
    internal void ToggleSectionEye(SceneTreeItem row, bool? hide = null)
    {
        if (row.Owner != sceneTree) throw new StudioCommandException("stale_record", "The tree row is no longer available.");
        switch (row.Section)
        {
            case SectionHeader { Section: "zones" }:
                if (ZoneToolEngaged) throw new StudioCommandException("busy", "Zone colours are paused while Edit zones is on: the tool shows its own zone view.");
                bool on = !(hide ?? ZoneOverlayOn);
                if (on != ZoneOverlayOn) SetWorldHighlightMode(on ? zoneOverlayKind : WorldHighlightMode.None);
                break;
            case SectionHeader { Section: "aiNetworks" }:
                bool visible = !(hide ?? aiVisible);
                if (visible != aiVisible) { if (!ResolveInspectionDrafts()) return; SetAiOptions(visible, aiThroughGeometry, aiNetworkFilter); }
                break;
            case ZoneEntry zone:
                if (ZoneToolEngaged) throw new StudioCommandException("busy", "Zone colours are paused while Edit zones is on.");
                if (hide ?? !hiddenZones.Contains(zone.Zone.Zone))
                {
                    hiddenZones.Add(zone.Zone.Zone);
                    // Hiding the isolated zone ends its isolation (as for a network).
                    if (isolatedZone == zone.Zone.Zone) isolatedZone = null;
                }
                else hiddenZones.Remove(zone.Zone.Zone);
                break;
            case NetworkEntry network:
                string id = network.Network.Id;
                if (hide ?? !hiddenNetworks.Contains(id))
                {
                    // Hiding ends AI path editing on it: its pending transform is decided first.
                    if (HasAiPathDraft) throw new StudioCommandException("pending_drafts", "Confirm or cancel the AI path transform before hiding a network.");
                    hiddenNetworks.Add(id);
                    // Hiding the isolated network ends its isolation.
                    if (aiNetworkFilter == id) SetAiOptions(aiVisible, aiThroughGeometry, null);
                }
                else hiddenNetworks.Remove(id);
                break;
            default: throw new StudioCommandException("unsupported", "This row has nothing to hide in the view.");
        }
        ApplySectionPresentation();
    }
    /// <summary>Middle-click on a zone's or network's eye: only its colour or markers, or everything again on the isolated one.</summary>
    internal void ToggleSectionIsolation(SceneTreeItem row)
    {
        if (row.Owner != sceneTree) throw new StudioCommandException("stale_record", "The tree row is no longer available.");
        switch (row.Section)
        {
            case ZoneEntry zone:
                if (ZoneToolEngaged) throw new StudioCommandException("busy", "Zone colours are paused while Edit zones is on.");
                isolatedZone = isolatedZone == zone.Zone.Zone ? null : zone.Zone.Zone;
                break;
            case NetworkEntry network:
                string id = network.Network.Id;
                if (aiNetworkFilter == id) SetAiOptions(aiVisible, aiThroughGeometry, null);
                else { hiddenNetworks.Remove(id); SetAiOptions(aiVisible, aiThroughGeometry, id); }
                break;
            default: throw new StudioCommandException("unsupported", "Only a zone's or an AI network's row can be isolated.");
        }
        ApplySectionPresentation();
    }
    /// <summary>The Zones header's Objects | Polygons switch: chooses the overlay, turning it on when it was off.</summary>
    internal void ChooseZoneOverlay(bool polygons)
    {
        if (ZoneToolEngaged) throw new StudioCommandException("busy", "Zone colours are paused while Edit zones is on.");
        var mode = polygons ? WorldHighlightMode.PolygonZones : WorldHighlightMode.Zones;
        zoneOverlayKind = mode;
        if (worldHighlightMode != mode) SetWorldHighlightMode(mode);
        else ApplySectionPresentation();
    }
    private void OutlinerZoneKindClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        try { ChooseZoneOverlay((sender as FrameworkElement)?.Name == "ZoneKindPolygons"); }
        catch (StudioCommandException ex) { Report(ex); }
    }
    private void OutlinerSeeThroughClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!ResolveInspectionDrafts()) { RefreshRowEyes(); return; }
        SetAiOptions(aiVisible, !aiThroughGeometry, aiNetworkFilter);
        ApplySectionPresentation();
    }
    /// <summary>A click on a section, scope or file row opens or closes it; those rows are never the selection.</summary>
    private void OutlinerRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        // A press on a row's button (a disabled one does not take it) is not a click on the row; a double-click toggles once.
        for (var at = e.OriginalSource as DependencyObject; at != null && at != sender; at = at is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at is System.Windows.Controls.Primitives.ButtonBase) { e.Handled = true; return; }
        if (e.ClickCount > 1 && (sender as FrameworkElement)?.DataContext is SceneTreeItem { IsSourceMap: true, Selectable: false }) { e.Handled = true; return; }
        if ((sender as FrameworkElement)?.DataContext is not SceneTreeItem { Section: SectionHeader or DefinitionScopeEntry or DefinitionFileEntry } row && (sender as FrameworkElement)?.DataContext is not SceneTreeItem { Pass: not null }) return;
        var item = (SceneTreeItem)((FrameworkElement)sender).DataContext;
        if (item.ChildCount == 0) return;
        e.Handled = true;
        item.IsExpanded = !item.IsExpanded;
    }

    // ---------------------------------------------------------------- selection and the card

    /// <summary>
    /// Selects a zone, network or definition row: the object selection ends and the row's card shows in Properties. Restoring
    /// the selection a rebuild replaced only selects it again (the view and the zone tool stay as they are).
    /// </summary>
    private void SelectSectionRow(SceneTreeItem row, bool restoring = false)
    {
        if (row.Owner != sceneTree || !row.IsSectionItem || sceneTreeDocument is not { IsDisposed: false } doc)
            throw new StudioCommandException("stale_record", "The tree row is no longer available.");
        var viewport = sceneTree.IsPreview ? TreeViewport : null;
        if (viewport?.IsPickupDragging == true || viewport?.IsFlyActive == true) throw new StudioCommandException("busy", "Finish the pickup drag or exit Fly before selecting.");
        changingSceneTree = true;
        try
        {
            selectedNode = null; inspectedSceneSource = null; treeInspectionTarget = null;
            if (viewport != null)
            {
                if (viewport.SelectedAiNode != null) viewport.SelectAiNode(null);
                viewport.SelectInspection(null, false); viewport.SelectFramingNode(null);
            }
            CurrentInspectionCard?.ClearSourceNode();
            sceneTree.Select(row); SceneTreeModel.ExpandAncestors(row);
        }
        finally { changingSceneTree = false; }
        switch (restoring ? null : row.Section)
        {
            // Selecting a network shows it: the overlay turns on, its eye opens and another network's isolation ends.
            case NetworkEntry network:
                hiddenNetworks.Remove(network.Network.Id);
                if (!aiVisible || aiNetworkFilter != null && aiNetworkFilter != network.Network.Id) SetAiOptions(true, aiThroughGeometry, aiNetworkFilter == network.Network.Id ? aiNetworkFilter : null);
                ViewModel.Status = $"Selected AI network {SourceMapSections.NetworkStem(network.Network)}";
                break;
            // While Edit zones is engaged, a zone row is the zone to paint.
            case ZoneEntry zone:
                if (ZoneToolEngaged) RunZoneTool(() => EngageZoneTool(doc, zone: zone.Zone.Zone));
                ViewModel.Status = $"Selected {row.OutlinerLabel}";
                break;
            case DefinitionEntry definition:
                ViewModel.Status = $"Selected animation definition {definition.Definition.Name}";
                break;
        }
        ShowSectionCard(row);
        ApplySectionPresentation();
        if (!restoring) RememberSelection(row);
        ScheduleSceneTreeReveal();
    }
    /// <summary>Clears a zone, network or definition selection (Escape, a click on empty space, the card's ✕), asking about its draft first.</summary>
    private bool ClearSectionSelection()
    {
        if (SectionSelection == null) return false;
        if (!ResolveSectionDraft()) return true;
        changingSceneTree = true;
        try { sceneTree!.Select(null); }
        finally { changingSceneTree = false; }
        return true;
    }
    /// <summary>The tree's selection changed (a row, the view or a rebuild): the pane shows the right card and the view follows.</summary>
    private void SceneTreeSelectionChanged()
    {
        if (!sourceChromeActive) return;
        if (SectionSelection is { } row) ShowSectionCard(row);
        ApplyCardDocking(); ApplySectionPresentation(); RefreshSourceStatus();
    }

    private SourceSectionCard SectionCard()
    {
        if (sectionCard != null) return sectionCard;
        sectionCard = new SourceSectionCard { IsEnabled = !sourceWorkspaceBusy };
        sectionCard.CloseRequested = () => ClearSectionSelection();
        sectionCard.ApplyLabel = async (zone, label) =>
        {
            if (sectionCard.Document is not { IsDisposed: false } doc) throw new StudioCommandException("stale_document", "The map was closed.");
            await NameZoneAsync(LiveDocument(doc), zone, label, CancellationToken.None, committingSection: true);
        };
        sectionCard.ApplyNetwork = async (network, changes) =>
        {
            if (sectionCard.Document is not { IsDisposed: false } doc) throw new StudioCommandException("stale_document", "The map was closed.");
            await EditNetworkAsync(LiveDocument(doc), network, changes, CancellationToken.None, committingSection: true);
        };
        sectionCard.PaintZone = zone => { if (sectionCard.Document is { IsDisposed: false } doc) RunZoneTool(() => EngageZoneTool(LiveDocument(doc), erase: false, zone: zone)); };
        sectionCard.SelectObject = row =>
        {
            if (!ResolveSelectionDrafts(sceneTreeDocument) || sceneTree?.Reveal(row) is not { Selectable: true } found) return;
            try { SelectSceneTreeRow(found); }
            catch (StudioCommandException ex) { Report(ex); }
        };
        sectionCard.DraftChanged += UpdateDocumentCommands;
        return sectionCard;
    }
    /// <summary>Shows a section row's card (again, after an edit published a new tree: the draft stays with its zone or network).</summary>
    private void ShowSectionCard(SceneTreeItem row)
    {
        if (sceneTreeDocument is not { } doc || doc.SourceWorld is not { } session) return;
        var card = SectionCard();
        switch (row.Section)
        {
            case ZoneEntry zone:
                string? master = null;
                try { master = doc.SourceBuild is { } build ? ZoneMaster(session, build) : null; } catch (StudioCommandException) { }
                card.ShowZone(doc, zone.Zone, master, ZoneToolEngaged && zoneTool.Zone == zone.Zone.Zone, true, null);
                break;
            case NetworkEntry network:
                var header = network.Network.Source is { } source ? SourceAiNetworkEdits.Read(source) : new(new Dictionary<string, SourceAiNetworkEdits.StoredField?>(SourceAiNetworkEdits.Fields.Select(f => KeyValuePair.Create(f.Key, (SourceAiNetworkEdits.StoredField?)null))), []);
                bool text = doc.Document is SourceSceneSnapshot snapshot && snapshot.Resources.Values.FirstOrDefault(r => string.Equals(r.Path, network.Network.Id, StringComparison.OrdinalIgnoreCase)) is { Syntax: not null };
                card.ShowNetwork(doc, network.Network, header, text);
                break;
            case DefinitionEntry definition:
                var projection = sceneTree?.Projection;
                int[] roots = sceneTree?.Sections?.DefinitionRoots.GetValueOrDefault(definition.Definition.Index) ?? [];
                var named = roots.Select(r => (r, projection?.DisplayName(r) ?? r.ToString(CultureInfo.InvariantCulture),
                    projection?.Parent(r) is int parent ? projection.DisplayName(parent) : projection?.PassOf(r)?.Label ?? "")).ToArray();
                var notes = doc.Document is SourceSceneSnapshot built ? built.Notes.Where(n => n.StartsWith(definition.Definition.File, StringComparison.OrdinalIgnoreCase)).Take(8).Select(n => JsonData.ShownText(n, 400)).ToArray() : [];
                card.ShowDefinition(doc, definition, named, notes, sceneTree?.Sections?.Definitions.Rejection, Math.Min(roots.Length, SceneViewport.MaximumOutlinedNodes));
                break;
        }
    }
    /// <summary>Asks about the section card's draft (Yes applies, No discards, Cancel keeps editing).</summary>
    private bool ResolveSectionDraft(DocumentModel? doc = null)
    {
        if (sectionCard is not { } card || doc != null && card.Document != doc) return true;
        if (card.Applying) return card.Accepting;
        if (!card.HasDraft) return true;
        var resolving = card.ResolvePendingAsync();
        return resolving.IsCompleted && resolving.GetAwaiter().GetResult();
    }
    private async Task<bool> ResolveSectionDraftAsync(DocumentModel? doc = null)
    {
        if (sectionCard is not { } card || doc != null && card.Document != doc) return true;
        return await card.ResolvePendingAsync();
    }
    private bool HasSectionDraft(DocumentModel? doc = null) => sectionCard is { } card && (card.HasDraft || card.Applying && !card.Accepting) && (doc == null || card.Document == doc);
    /// <summary>After an edit's preparation: the card's draft is still the one being applied, now released for acceptance.</summary>
    private void ReleaseSectionDraft(DocumentModel doc)
    {
        if (sectionCard is not { Applying: true } card || card.Document != doc)
            throw new StudioCommandException("context_changed", "The zone or AI network card changed while the edit was prepared.");
        card.ReleaseForAcceptance(); UpdateDocumentCommands();
    }

    // ---------------------------------------------------------------- edits

    /// <summary>Edits an AI network's header in its source file: one undoable change (GUI card and MCP alike).</summary>
    private async Task<DocumentModel> EditNetworkAsync(DocumentModel document, string network, IReadOnlyList<SourceAiNetworkEdits.Change> changes, CancellationToken token, bool committingSection = false)
    {
        RequireNoDrafts(document, committing: true, committingSection: committingSection);
        if (document.SourceWorld == null || document.Document is not SourceSceneSnapshot snapshot) throw new StudioCommandException("unsupported", "This document is not a source map.");
        if (!snapshot.Resources.Values.Any(r => string.Equals(r.Path, network, StringComparison.OrdinalIgnoreCase)) || !MissionAiNetworks.IsCandidate(System.IO.Path.GetFileName(network)))
            throw new StudioCommandException("stale_record", "The AI network is no longer in this map. Read its current networks.");
        if (changes.Count == 0) throw new StudioCommandException("invalid_argument", "Name at least one field to change.");
        try
        {
            return await PrepareSourceWorldEditAsync(document, "Editing AI network", (workspace, ct) =>
            {
                byte[] bytes = workspace.Read(network, ct) ?? throw new StudioCommandException("stale_record", $"{network} is no longer in the project.");
                SourceAiNetworkEdits.Edit plan;
                try { plan = SourceAiNetworkEdits.Plan(network, bytes, changes, ct); }
                catch (System.IO.InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
                catch (InvalidOperationException) { throw new StudioCommandException("unchanged", "Nothing changed."); }
                return workspace.Apply(plan.Label, [(network, (byte[]?)plan.Content)], ct);
            }, token, committingSection: committingSection);
        }
        catch (StudioCommandException ex) when (ex.Code == "unchanged") { return document; }
    }

    // ---------------------------------------------------------------- MCP

    private void RegisterSourceSectionCommands(StudioCommands registry)
    {
        RegisterJob(registry, "source_ai_network", "Read or edit an AI network's header in its source file (data/mN/zrdr/aipath/net_NN.zrd): name (at most 19 characters: letters, digits, _ - .), type (standard, hide, fixed, defilade; the game reads the first two letters), attack_strategy (head_on, circle, back_forth, follow_net, zigzag, sit; missing or unknown means head-on), attack_rad, attack_dwell, attack_buddy (0 or 1), path_width (above 0), activate_rad, pursuit_params (two numbers; a stored pursuit_range alias is kept), not_pursuit_dwell, return_range and hide_times (two numbers). The World tree's AI network card edits the same fields. Only the edited values change: a stored value is replaced in place and a missing key is added before the nodes, keeping comments and layout; one undoable change; the file (the network's ID, which vehicles name) is never renamed. Read returns the stored values (null when not stored). A binary source, a key stored twice and an invalid value are refused; nothing to change accepts no edit.",
            [DocumentParameter, RevisionParameter with { Required = false }, P("network", "string", "The network's ID: its project path, as ai_networks and scene_tree rows give it.", true),
             new("changes", "array", "edit: the fields to change (omit to read).", Items: new("", "object", "One field.", Properties: [P("key", "string", "The stored key.", true, [.. SourceAiNetworkEdits.Fields.Select(f => f.Key)]),
                new("values", "array", "Its value(s) as text: one, two for a pair, 0 or 1 for attack_buddy.", true, Items: new("", "string", "A value."), MaxItems: 2)]), MaxItems: 16)], false,
            async (args, token) =>
            {
                var document = TargetDocument(args);
                string id = Text(args, "network");
                var network = (scene?.AiNetworks.Networks ?? []).FirstOrDefault(n => n.Id == id && document == shownDocument)
                    ?? throw new StudioCommandException("stale_record", "The AI network is not in the shown map. Read its current networks.");
                if (args["changes"] is not JsonArray changes) return Result(new { document = DocumentState(document), network = id, fields = NetworkFields(network) });
                if (args["revision"]?.GetValue<long>() != document.Revision) throw new StudioCommandException("revision_conflict", "Read the current document revision.");
                var requested = changes.OfType<JsonObject>().Select(c => new SourceAiNetworkEdits.Change(Text(c, "key"), c["values"] is JsonArray values ? [.. values.Select(v => v?.GetValue<string>() ?? "")] : [])).ToArray();
                var next = await EditNetworkAsync(document, id, requested, token);
                return Result(new { document = DocumentState(next), network = id });
            });
    }
    private static JsonObject NetworkFields(AiNetwork network)
    {
        JsonObject fields = [];
        if (network.Source is not { } source) return fields;
        var header = SourceAiNetworkEdits.Read(source);
        foreach (var field in SourceAiNetworkEdits.Fields)
            fields[field.Key] = header.Fields[field.Key] is { } stored ? new JsonObject
            {
                ["storedKey"] = stored.Key, ["values"] = new JsonArray([.. stored.Values.Select((_, i) => (JsonNode?)JsonData.ShownText(stored.Text(i), 256))]), ["repeated"] = stored.Repeated ? true : null,
            } : null;
        return fields;
    }
    /// <summary>The section a scene_tree row reports: its kind and identity, what it shows and its eye.</summary>
    private object? DescribeSectionRow(SceneTreeItem row) => row.Section switch
    {
        SectionHeader { Section: "zones" } => new { section = "zones", overlay = new { visible = ZoneOverlayOn, kind = (ZoneOverlayOn ? worldHighlightMode : zoneOverlayKind) == WorldHighlightMode.PolygonZones ? "polygons" : "objects", paused = ZoneToolEngaged } },
        SectionHeader { Section: "aiNetworks" } => new { section = "aiNetworks", ai = new { visible = aiVisible, throughGeometry = aiThroughGeometry } },
        SectionHeader header => new { section = header.Section },
        ZoneEntry zone => new { zoneId = zone.Zone.Zone, label = zone.Zone.Label is { } text ? JsonData.ShownText(text, 256) : null, polygons = zone.Zone.Polygons, placements = zone.Zone.Placements, models = zone.Zone.Models,
            objects = zone.Zone.Objects, ownObjects = zone.Zone.OwnObjects, inheritedObjects = zone.Zone.InheritedObjects, emptyObjects = zone.Zone.EmptyObjects, used = zone.Zone.Used },
        NoZonesEntry none => new { noZones = true, polygons = none.Polygons },
        NetworkEntry network => new { network = network.Network.Id, name = JsonData.ShownText(network.Network.Name, 256), nodes = network.Network.Nodes.Count, attackStrategy = DescribeAiStrategy(network.Network), diagnosticCount = network.Network.Diagnostics.Count },
        DefinitionScopeEntry scope => new { definitionScope = scope.Name, folder = scope.Folder, definitions = scope.Count },
        DefinitionFileEntry file => new { definitionFile = file.Listing.File, occurrence = file.Listing.Occurrence, listedBy = file.Listing.ListedBy, definitions = file.Definitions.Count },
        DefinitionEntry definition => new { definitionFile = definition.Definition.File, ordinal = definition.Definition.Ordinal, occurrence = definition.Listing.Occurrence, name = definition.Definition.Name,
            rootName = definition.Definition.RootName, roots = definition.BindingKnown ? definition.Roots : (int?)null, bound = definition.BindingKnown ? definition.Roots > 0 : (bool?)null,
            rootNodes = sceneTree?.Sections?.DefinitionRoots.GetValueOrDefault(definition.Definition.Index)?.Take(64).ToArray() },
        MissingFileEntry missing => new { definitionFile = missing.Listing.File, missing = true, listedBy = missing.Listing.ListedBy },
        NoteEntry note => new { note = note.Text },
        _ => null,
    };
    /// <summary>preview_state's view of the sections: the zone eyes, the network eyes and the selection's emphasis or outlines.</summary>
    private object? SectionPreviewState() => scene is not { } viewport || shownDocument?.SourceWorld == null ? null : new
    {
        zones = new { overlay = ZoneOverlayOn, kind = (ZoneOverlayOn ? worldHighlightMode : zoneOverlayKind) == WorldHighlightMode.PolygonZones ? "polygons" : "objects", paused = ZoneToolEngaged,
            hidden = hiddenZones.Order().Select(z => (int)z).ToArray(), isolated = (int?)isolatedZone, emphasis = (int?)viewport.ZoneSelection },
        networks = new { hidden = hiddenNetworks.Order(StringComparer.Ordinal).Take(256).ToArray(), hiddenCount = hiddenNetworks.Count, isolated = aiNetworkFilter, emphasis = viewport.AiEmphasizedNetwork },
        outlines = new { roots = viewport.OutlinedNodes.Count, boxes = viewport.OutlinedBoxes },
        selection = SectionSelection is { } row ? new { row = row.Id, kind = row.Category, key = row.Section!.Key } : null,
    };
}
