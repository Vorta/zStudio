using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private sealed class ZoneDraft(DocumentModel document)
    {
        internal DocumentModel Document { get; } = document;
        internal long Revision { get; } = document.Revision;
        internal long WorkspaceRevision { get; } = document.SourceWorld!.Workspace.ContentRevision;
        internal string Token { get; set; } = Guid.NewGuid().ToString("N");
        internal bool Faces { get; set; } = true;
        internal byte[] Zones { get; set; } = [0];
        internal bool? Gate { get; set; }
        internal bool Shared { get; set; }
        internal HashSet<SourceZoneSelection> Targets { get; } = [];
        internal Action? Disposing { get; set; }
        /// <summary>Its apply released it to accept the edit (a draft the user discarded meanwhile is not).</summary>
        internal bool Accepting { get; set; }
    }
    private ZoneDraft? zoneDraft;
    private SourceZoneCatalog? zoneCatalog;
    private bool HasZoneDraft => zoneDraft?.Targets.Count > 0;

    private JsonObject ZoneDraftState() => new()
    {
        ["document"] = zoneDraft?.Document.SessionId.ToString(), ["revision"] = zoneDraft?.Revision,
        ["draftToken"] = zoneDraft?.Token, ["faces"] = zoneDraft?.Faces ?? true,
        ["zones"] = new JsonArray((zoneDraft?.Zones ?? [0]).Select(z => (JsonNode)JsonValue.Create(z)!).ToArray()),
        ["gate"] = zoneDraft?.Gate, ["sharedScope"] = zoneDraft?.Shared ?? false,
        ["targetCount"] = zoneDraft?.Targets.Count ?? 0, ["painting"] = scene?.ZonePaintActive == true,
        ["outlineLimited"] = scene?.ZoneOutlineLimited == true,
    };
    private ZoneDraft RequireZoneDraft(DocumentModel document, string? expected = null)
    {
        var draft = zoneDraft;
        if (draft == null || draft.Document != document || document.IsDisposed || document.SourceWorld?.Owner != document)
            throw new StudioCommandException("stale_draft", "Open a zone draft for the current source world.");
        // Targets collected while a world rebuilds would hold its replacement and take the edit back (see OpenZoneEditorAsync).
        RequireSourceWorldIdle(document.SourceWorld);
        if (draft.Revision != document.Revision || draft.WorkspaceRevision != document.SourceWorld.Workspace.ContentRevision || expected != null && expected != draft.Token)
            throw new StudioCommandException("stale_draft", "The zone draft or source world changed. Read the current zone state before retrying.");
        return draft;
    }
    private void TouchZoneDraft(ZoneDraft draft)
    {
        draft.Token = Guid.NewGuid().ToString("N");
        // Targets name source polygons; the outline shows every built face that comes from one.
        var build = draft.Document.SourceBuild;
        scene?.ShowZoneTargets(draft.Targets.Select(t => new ZonePaintTarget(t.SceneNode, t.Polygon)),
            node => build != null && build.Provenance.TryGetValue(node, out var origin) ? origin.ZonePolygons : null);
        if (propertiesWindow?.Document == draft.Document && propertiesWindow.SourceFields is ZonePropertiesEditor fields)
            fields.RefreshState(ZoneDraftState());
        UpdateDocumentCommands();
    }
    private void CancelZoneDraft(bool close = false)
    {
        if (scene != null) { scene.ZonePaintActive = false; scene.ShowZoneTargets([]); }
        if (zoneDraft != null) { zoneDraft.Targets.Clear(); zoneDraft.Token = Guid.NewGuid().ToString("N"); }
        if (close)
        {
            if (zoneDraft?.Disposing is { } disposing) zoneDraft.Document.Disposing -= disposing;
            zoneDraft = null; zoneCatalog = null;
        }
        UpdateDocumentCommands();
    }
    private bool ResolveZoneDrafts(DocumentModel? document = null)
    {
        if (!HasZoneDraft || document != null && zoneDraft!.Document != document) return true;
        var answer = MessageBox.Show(this, "A map zone draft is pending. Discard it and continue?\nChoose No to return to the zone editor and Apply the draft.",
            "Map zone draft", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;
        CancelZoneDraft(close: true); return true;
    }
    private async Task OpenZoneEditorAsync(DocumentModel document, CancellationToken token, bool automation = false)
    {
        var session = (document.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world."));
        // A draft started while a world of the project rebuilds after an edit would be an unresolved draft when the rebuilt
        // world replaces this one, and that edit would be taken back: zone editing waits, as scene-card and Properties input do.
        RequireSourceWorldIdle(session);
        if (!automation && !await ResolvePropertiesDraftsAsync()) return;
        // The zone editor and pinned Properties are shared across documents.
        RequireNoDrafts();
        long revision = session.Workspace.ContentRevision;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, document.Lifetime.Token, ViewModel.WorkspaceToken, shutdownToken);
        var catalog = await Task.Run(() => SourceZoneEdits.Catalog(session.Workspace, session.Mission, linked.Token), linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (document.IsDisposed || session.Owner != document || revision != session.Workspace.ContentRevision)
            throw new StudioCommandException("context_changed", "The source world changed while opening zones.");
        RequireSourceWorldIdle(session);
        RequireNoDrafts();
        CancelZoneDraft(close: true); zoneDraft = new(document); zoneCatalog = catalog;
        zoneDraft.Disposing = () => { if (zoneDraft?.Document == document) CancelZoneDraft(close: true); };
        document.Disposing += zoneDraft.Disposing;
        PresentZoneEditor();
    }
    private void PresentZoneEditor()
    {
        if (zoneDraft is not { } draft || zoneCatalog == null || draft.Document.IsDisposed) return;
        // A completed viewport stroke can arrive while native Properties input is partial.
        // Update the retained draft without destroying that editor or committing its input.
        if (propertiesWindow?.SourceFields is ZonePropertiesEditor pending && pending.HasPendingDrafts)
        { pending.RefreshState(ZoneDraftState()); return; }
        void Update(Action change) { RequireZoneDraft(draft.Document); change(); TouchZoneDraft(draft); }
        var fields = new ZonePropertiesEditor(ZoneDraftState(), zoneCatalog, new(
            faces => Update(() =>
            {
                if (draft.Targets.Count != 0) throw new InvalidDataException("Cancel selected targets before changing between faces and objects.");
                // As MCP set refuses: an object draft holding two or three face zones could never be applied.
                RequireZoneList(faces, draft.Zones, "Enter one zone ID, or none to keep each object's zone, in Zone IDs before choosing objects.");
                draft.Faces = faces;
            }),
            zones => Update(() => { RequireZoneList(draft.Faces, zones); draft.Zones = zones; }),
            gate => Update(() => draft.Gate = gate),
            shared => Update(() => draft.Shared = shared),
            () => { ToggleZonePainting(draft); PresentZoneEditor(); },
            () => { AddSelectedZoneTarget(draft); PresentZoneEditor(); },
            async () => { await ApplyZoneDraftAsync(draft.Document, draft.Token, CancellationToken.None); },
            () => { CancelZoneDraft(); PresentZoneEditor(); },
            async (id, label) => { await NameZoneAsync(draft.Document, id, label, CancellationToken.None); }));
        var window = GetPropertiesWindow(); PresentProperties(window, window.SetSourceObject(draft.Document, fields));
    }
    private void ToggleZonePainting(ZoneDraft draft)
    {
        RequireZoneDraft(draft.Document);
        if (shownDocument != draft.Document || scene == null || shownAsset?.Kind != Recoil.Zbd.Core.AssetKind.World)
            throw new StudioCommandException("not_ready", "Show this source world's Whole world preview before painting.");
        if (draft.Document.PickupsLocked) throw new StudioCommandException("locked", "Unlock world editing before painting zones.");
        bool on = !scene.ZonePaintActive;
        if (on)
        {
            if (scene.IsFlyActive || scene.IsPickupDragging)
                throw new StudioCommandException("busy", "Finish the active navigation or editing gesture before painting zones.");
        }
        scene.ZonePaintFaces = draft.Faces; scene.ZonePaintActive = on;
        scene.ZoneStrokeCompleted -= ZoneStrokeCompleted; scene.ZoneStrokeCompleted += ZoneStrokeCompleted;
        ViewModel.Status = on ? "Painting map zones: drag over source objects or faces." : "Zone painting off.";
    }
    private void AddSelectedZoneTarget(ZoneDraft draft)
    {
        if (shownDocument != draft.Document || scene?.SelectedInspection is not { Active: true, Node: >= 0 } hit)
            throw new StudioCommandException("not_ready", "Select a visible source surface or object first.");
        if (draft.Faces && hit.Polygon == null) throw new StudioCommandException("not_ready", "Click a face in the viewport to select an exact polygon.");
        RequireZoneDraft(draft.Document);
        AddZoneTargets(draft, [PickedZoneTarget(draft, new(hit.Node, draft.Faces ? hit.Polygon : null))]);
    }
    private void ZoneStrokeCompleted(IReadOnlyList<ZonePaintTarget> targets)
    {
        if (zoneDraft is not { } draft) return;
        try
        {
            RequireZoneDraft(draft.Document);
            AddZoneTargets(draft, targets.Select(t => PickedZoneTarget(draft, t)).ToArray()); PresentZoneEditor();
        }
        catch (Exception ex) when (ex is StudioCommandException or InvalidDataException) { Report(ex); }
    }
    /// <summary>A viewport hit names a built polygon; the draft, like source_zone_draft add, takes the source polygon it comes from.</summary>
    private static SourceZoneSelection PickedZoneTarget(ZoneDraft draft, ZonePaintTarget hit)
    {
        try { return SourceZoneEdits.FromCompiled(draft.Document.SourceBuild!, hit.Node, hit.Polygon); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
    }
    private static void RequireZoneList(bool faces, IReadOnlyCollection<byte> zones, string? message = null)
    {
        if (!faces && zones.Count > 1) throw new StudioCommandException("invalid_argument", message ?? "Objects accept one zone ID or an empty list to keep their current zone.");
    }
    private void AddZoneTargets(ZoneDraft draft, IReadOnlyList<SourceZoneSelection> targets)
    {
        RequireZoneDraft(draft.Document);
        if (targets.Count > SourceZoneEdits.MaximumSelections) throw new StudioCommandException("invalid_argument", "At most 4,096 exact targets are accepted.");
        HashSet<SourceZoneSelection> after = new(draft.Targets);
        var build = draft.Document.SourceBuild!;
        foreach (var target in targets)
        {
            if (draft.Faces != (target.Polygon != null) || !build.Provenance.TryGetValue(target.SceneNode, out var origin))
                throw new StudioCommandException("invalid_argument", "Every target must identify a source node and match the draft's face/object mode.");
            if (origin.Terrain != null) throw new StudioCommandException("unsupported", "This is a compiled terrain surface. Open its Terrain properties to paint a scoped zone region in its recipe.");
            after.Add(target);
            if (after.Count > SourceZoneEdits.MaximumSelections) throw new StudioCommandException("invalid_argument", "The zone draft already contains 4,096 targets.");
        }
        // A source polygon index no built face of the node comes from is refused here, before it can reach Apply.
        try { SourceZoneEdits.RequireBuiltPolygons(build, targets); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        draft.Targets.Clear(); draft.Targets.UnionWith(after); TouchZoneDraft(draft);
    }
    private async Task<DocumentModel> ApplyZoneDraftAsync(DocumentModel document, string expected, CancellationToken token)
    {
        var draft = RequireZoneDraft(document, expected);
        if (draft.Targets.Count == 0) throw new StudioCommandException("not_ready", "Select or paint targets before applying zones.");
        if (document.PickupsLocked) throw new StudioCommandException("locked", "Unlock world editing before applying zones.");
        var selections = draft.Targets.ToArray();
        var assignment = draft.Faces ? new SourceZoneAssignment(PolygonZones: draft.Zones.ToArray()) :
            new SourceZoneAssignment(draft.Zones.Length == 1 ? draft.Zones[0] : draft.Zones.Length == 0 ? null : throw new StudioCommandException("invalid_argument", "Choose at most one object zone."), draft.Gate);
        var build = document.SourceBuild!;
        bool shared = draft.Shared;
        var catalog = zoneCatalog;
        DocumentModel next;
        try
        {
            next = await PrepareSourceWorldEditAsync(document, "Editing map zones", (workspace, ct) =>
            {
                var plan = SourceZoneEdits.PlanAssignments(workspace, build, selections, assignment, shared, ct);
                return workspace.Apply(plan.Label, plan.Changes.Select(change => (change.Relative, (byte[]?)change.Content)), ct);
            }, token, committingZones: draft);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // Preparation keeps the draft. Acceptance temporarily releases it so the shared
            // rebuild guard can run; a rolled-back build must return that draft to the user.
            // One discarded during preparation (a prompt's Yes, Properties closed) stays discarded.
            if (draft.Accepting && zoneDraft == null && !shutdownToken.IsCancellationRequested && !ViewModel.WorkspaceToken.IsCancellationRequested &&
                !document.IsDisposed && document.SourceWorld?.Owner == document && ViewModel.Documents.Contains(document) &&
                ReferenceEquals(document.SourceBuild, build) && !document.SourceInputsChanged(verifyContent: false))
            {
                zoneDraft = new(document) { Faces = draft.Faces, Zones = draft.Zones.ToArray(), Gate = draft.Gate, Shared = shared };
                zoneDraft.Targets.UnionWith(selections); zoneCatalog = catalog;
                zoneDraft.Disposing = () => { if (zoneDraft?.Document == document) CancelZoneDraft(close: true); };
                document.Disposing += zoneDraft.Disposing;
                try { TouchZoneDraft(zoneDraft); PresentZoneEditor(); }
                catch (Exception presentation) when (presentation is not (OutOfMemoryException or StackOverflowException))
                { ViewModel.AddProblem("The zone draft was retained, but Properties could not refresh: " + Bounded(presentation.Message, 512), "Warning", document.Path); }
            }
            throw;
        }
        await PresentZonesAfterAcceptedEditAsync(next);
        return next;
    }
    private async Task<DocumentModel> NameZoneAsync(DocumentModel document, byte id, string label, CancellationToken token)
    {
        RequireNoDrafts(document, committing: true);
        string mission = (document.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world.")).Mission;
        var next = await PrepareSourceWorldEditAsync(document, "Naming map zone", (workspace, ct) =>
        {
            var plan = SourceZoneEdits.PlanLabel(workspace, mission, id, label, ct);
            return workspace.Apply(plan.Label, plan.Changes.Select(change => (change.Relative, (byte[]?)change.Content)), ct);
        }, token);
        await PresentZonesAfterAcceptedEditAsync(next);
        return next;
    }
    private async Task PresentZonesAfterAcceptedEditAsync(DocumentModel document)
    {
        if (document.IsDisposed || ViewModel.SelectedDocument != document) return;
        try { await OpenZoneEditorAsync(document, CancellationToken.None, automation: true); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // The source edit is already accepted. A popup failure must not turn it into a failed operation.
            if (!document.IsDisposed) ViewModel.AddProblem("The map zone edit was accepted, but Properties could not reopen: " + Bounded(ex.Message, 512), "Warning", document.Path);
        }
    }
    private async void EditZonesClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        var document = ViewModel.SelectedDocument ?? throw new StudioCommandException("not_ready", "Open a source mission world first.");
        await OpenZoneEditorAsync(document, CancellationToken.None);
    });

    private void RegisterSourceZoneCommands(StudioCommands registry)
    {
        Register(registry, "source_zones", "Inspect the map zone catalog and pinned draft. Zone assignments live in data/mN/meta/zones.json; model geometry is shared. Optional exact targets return a bounded profile/scope page.", false,
            [DocumentParameter, ZoneTargetsParameter(), new("offset", "integer", "Target page offset.", Minimum: 0, Maximum: int.MaxValue)], async (args, token) =>
            {
                var document = TargetDocument(args); var session = (document.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world.")); long revision = session.Workspace.ContentRevision;
                var targets = ParseZoneTargets(args); var build = document.SourceBuild!;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, document.Lifetime.Token, ViewModel.WorkspaceToken, shutdownToken);
                var values = await Task.Run(() => (SourceZoneEdits.Catalog(session.Workspace, session.Mission, linked.Token),
                    targets.Length == 0 ? null : SourceZoneEdits.Inspect(session.Workspace, build, targets, Int(args, "offset"), linked.Token)), linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (document.IsDisposed || session.Owner != document || revision != session.Workspace.ContentRevision) throw new StudioCommandException("context_changed", "The source world changed during zone inspection.");
                return Result(new { document = document.SessionId, revision = document.Revision, catalog = values.Item1, inspection = values.Item2, draft = zoneDraft?.Document == document ? ZoneDraftState() : null });
            });
        RegisterJob(registry, "source_zone_draft", "Open, edit, paint or apply the shared zone draft. begin opens Properties; set configures faces/objects, ordered zones, gate and shared scope; add supplies exact node/polygon targets; paint toggles the explicit GUI brush; apply accepts one undoable edit; cancel discards. Every action after begin requires the current draftToken. Geometry is unchanged by zone-only edits.",
            [DocumentParameter, RevisionParameter, P("action", "string", "Draft operation.", true, "begin", "set", "add", "paint", "apply", "cancel", "label"),
             P("draftToken", "string", "Current draft token from source_zones."), P("faces", "boolean", "True for polygon faces, false for object zones."),
             new("zones", "array", "Zero to three ordered face zone IDs; objects accept one ID or an empty list to keep their current zone; 255 means Any.", Items: new("", "integer", "Zone ID.", Minimum: 0, Maximum: 255), MaxItems: 3),
             P("gate", "string", "Object altitude-probe gate: keep retains each object's value; on/off explicitly set it.", false, "keep", "on", "off"),
             new("zoneId", "integer", "Zone ID to name with action label.", Minimum: 0, Maximum: 255), new("label", "string", "Zone name of 1–256 characters for action label; 255 must remain Any."),
             P("sharedScope", "boolean", "Explicitly permit every use of selected logical model/mesh in this map."), ZoneTargetsParameter()], true,
            async (args, token) =>
            {
                var document = TargetDocument(args);
                if (args["revision"]?.GetValue<long>() != document.Revision) throw new StudioCommandException("revision_conflict", "Read the current document revision.");
                string action = Text(args, "action");
                if (action == "begin") { RequireNoDrafts(document); await OpenZoneEditorAsync(document, token, automation: true); }
                else
                {
                    var draft = RequireZoneDraft(document, Text(args, "draftToken"));
                    // These actions show the zone editor in Properties again, which would commit or ask about unfinished input
                    // of whatever Properties shows now (it may have been retargeted while the draft had no targets).
                    if (action is "set" or "add" or "paint" or "cancel") RequireNoDrafts(committingZones: draft);
                    if (action == "set")
                    {
                        bool faces = Flag(args, "faces", draft.Faces);
                        if (faces != draft.Faces && draft.Targets.Count > 0) throw new StudioCommandException("pending_drafts", "Cancel selected targets before changing target mode.");
                        byte[] zones = args["zones"] is JsonArray ids ? ids.Select(x => x!.GetValue<byte>()).ToArray() : draft.Zones;
                        RequireZoneList(faces, zones);
                        bool? gate = args["gate"] == null ? draft.Gate : Text(args, "gate") switch { "keep" => null, "on" => true, "off" => false, _ => throw new StudioCommandException("invalid_argument", "Use gate keep, on or off.") };
                        draft.Faces = faces; draft.Zones = zones; draft.Gate = gate; draft.Shared = Flag(args, "sharedScope", draft.Shared); TouchZoneDraft(draft); PresentZoneEditor();
                    }
                    else if (action == "add") { AddZoneTargets(draft, ParseZoneTargets(args)); PresentZoneEditor(); }
                    else if (action == "paint") { ToggleZonePainting(draft); PresentZoneEditor(); }
                    else if (action == "cancel") { CancelZoneDraft(); PresentZoneEditor(); }
                    else if (action == "apply") { document = await ApplyZoneDraftAsync(document, draft.Token, token); }
                    else if (action == "label")
                    {
                        if (args["zoneId"] == null || args["label"] == null) throw new StudioCommandException("invalid_argument", "Naming a zone needs zoneId and label.");
                        document = await NameZoneAsync(document, checked((byte)Int(args, "zoneId")), Text(args, "label"), token);
                    }
                    else throw new StudioCommandException("invalid_argument", "Unknown zone draft action.");
                }
                return Result(new { document = DocumentState(document), draft = ZoneDraftState() });
            });
    }
    private static StudioParameter ZoneTargetsParameter() => new("targets", "array", "Exact source scene nodes and, for faces, source polygon indices (as inspection returns them).",
        Items: new("", "object", "Source zone target.", Properties: [new("node", "integer", "Scene node index.", true, Minimum: 0, Maximum: int.MaxValue),
            new("polygon", "integer", "Polygon index in the node's source mesh, the Polygon an inspection target returns; not the built model's polygon index, which differs where the build split or discarded polygons. An index no built face comes from is refused. Omit for object.", Minimum: 0, Maximum: int.MaxValue)]), MaxItems: SourceZoneEdits.MaximumSelections);
    private static SourceZoneSelection[] ParseZoneTargets(JsonObject args) => args["targets"] is JsonArray targets ?
        targets.Select(value => value is JsonObject target ? new SourceZoneSelection(Int(target, "node"), target["polygon"] == null ? null : Int(target, "polygon")) :
            throw new StudioCommandException("invalid_argument", "Each zone target must be an object.")).ToArray() : [];
}
