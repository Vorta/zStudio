using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Automation;
using zStudio.Core.Formats;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private void RequireZmapCommand(string name, JsonObject? args = null)
    {
        if (zmap == null || name is "source_zmap" or "camera" or "presentation" || name == "window" && args != null && Text(args, "action") != "close") return;
        if (zmapBusy) throw new StudioCommandException("busy", "A ZMAP file operation is in progress.");
        if (HasZmapDraft) throw new StudioCommandException("pending_drafts", "Resolve ZMAP input with source_zmap.");
        if (zmap.Dirty) throw new StudioCommandException("unsaved_changes", "Save the ZMAP or explicitly discard it with source_zmap exit.");
        throw new StudioCommandException("mode_active", "Leave ZMAP mode with source_zmap exit first.");
    }

    private object ZmapState() => new
    {
        active = zmap != null, available = ZmapAvailable,
        document = zmap?.Document.SessionId, mission = zmap?.Document.SourceWorld?.Mission,
        session = zmap?.Id, revision = zmap?.Revision, opened = zmap?.OpenedPath, path = zmap?.SavePath,
        loaded = zmap?.Map != null, dirty = zmap?.Dirty == true, busy = zmapBusy, canUndo = zmap?.CanUndo == true, canRedo = zmap?.CanRedo == true,
        outline = zmap?.Outline, vertex = zmap?.Vertex, outlineCount = zmap?.Map?.Outlines.Length ?? 0,
        objectives = zmap?.ObjectiveCount, headerWord = zmap?.Map?.HeaderWord,
        bounds = zmap?.Map is { } map ? new[] { Coordinates(map.Minimum), Coordinates(map.Maximum) } : null,
        draft = zmapSplit != null ? "split" : zmapDrawing != null ? "outline" : zmapDrag != null ? "drag" : zmapInsert ? "insert" : zmapFieldsDirty ? "fields" : null,
        split = zmapSplit is { } cut ? new { outline = cut.Outline, first = cut.First, second = cut.Second, canApply = cut.CanApply, endpoint = cut.PickFirst ? "first" : "second", problem = cut.Problem } : null,
        snapStatus = ZmapLayer.Viewport?.ZmapSnapStatus,
        terrainMaxY = ZmapLayer.Viewport?.ZmapTerrainMaxY,
        terrainZones = zmapTerrainChoices.Select(pair => new { zone = pair.Key, label = pair.Value.Label,
            visible = ZmapLayer.Viewport!.IsZmapTerrainZoneVisible(pair.Key) }).ToArray(),
        draftCount = zmapDrawing?.Count, message = zmap != null ? Bounded(zmapStatus.Text, 512) : null,
        fields = zmapFieldsDirty ? new { rgb = zmapColor.Text, objectiveIndex = zmapObjectives.SelectedValue as int?, x = zmapX.Text, y = zmapY.Text, z = zmapZ.Text } : null
    };

    private static float[] Coordinates(Vector3 point) => [point.X, point.Y, point.Z];
    private static StudioParameter PointParameter(string name, string description) =>
        new(name, "array", description, Items: new("", "number", "Finite single-precision coordinate."), MinItems: 3, MaxItems: 3);

    private void RegisterZmapCommand(StudioCommands registry)
    {
        RegisterJob(registry, "source_zmap", "Edit loose RECOIL version-5 HUD outlines over the shown source map. state reports document, session, revision, selection, draft and snapStatus. enter starts in Top with no file. Supply current session and revision for changes. new creates an empty file in memory; open reads an absolute .zmap path. outlines and vertices page identities and authored data. select without IDs clears selection. add_outline takes XYZ points and optional RGB and assignment. insert_vertex uses an edge-start vertex and interior fraction. move_vertex with snap=true finds the nearest rendered map vertex within 12 DIP of position in Top view and retains the moved vertex's Y, including when no target is nearby. set_fields changes RGB, objectiveIndex and/or a vertex position as one edit. No objective is -1; Objective N is N minus 1. draft_split starts a cut on the selected outline, using its selected vertex as the first endpoint if present. split_pick sets a vertex and optionally first or second; the split state reports both endpoints, active endpoint and canApply. draft_apply makes both closed outlines as one undo step; draft_cancel abandons the cut. draft_begin, draft_add, draft_insert and draft_set share the other visible editor drafts. Finish or cancel a path before saving. open sets the active save path; save updates that file using its last verified contents. save_as creates a fresh .zmap and makes it active. An untitled ZMAP needs save_as. new, open and exit require discard=true for unsaved changes and a separate explicit draft decision. terrainZones lists the object zones used by terrain and fixed scenery. terrain_visibility sets visible for a zone, or shows all with visible=true and no zone. terrain_height clips rendered map geometry above world y, or clears with no y; state reports terrainMaxY. Both filters last until exit. Frame targets map, outline or vertex. Pan and zoom through camera.",
        [
            P("document", "string", "Shown source-map document ID from zstudio_state, required except for ZMAP state."),
            P("action", "string", "ZMAP operation.", true, "state", "enter", "exit", "new", "open", "outlines", "vertices", "select", "frame", "terrain_visibility", "terrain_height",
                "add_outline", "insert_vertex", "move_vertex", "remove_vertex", "remove_outline", "set_fields",
                "draft_begin", "draft_add", "draft_insert", "draft_split", "split_pick", "draft_set", "draft_apply", "draft_cancel", "undo", "redo", "save", "save_as"),
            P("session", "string", "Current ZMAP session ID."),
            new("revision", "integer", "Current ZMAP revision.", Minimum: 0),
            P("path", "string", "Absolute .zmap path for open or unused path for save_as."),
            P("discard", "boolean", "Explicitly discard unsaved accepted changes when leaving or replacing the ZMAP."),
            P("outline", "string", "Outline identity."), P("vertex", "string", "Vertex or edge-start identity."),
            P("endpoint", "string", "Endpoint for split_pick; defaults to the draft's active endpoint.", false, "first", "second"),
            P("snap", "boolean", "Snap move_vertex in X/Z using the current viewport. Keep the original Y."),
            P("target", "string", "Frame target; defaults to map.", false, "map", "outline", "vertex"),
            new("zone", "integer", "Object zone from terrainZones for terrain_visibility (255 is Any). Omit to show all.", Minimum: 0, Maximum: 255),
            P("visible", "boolean", "Required for terrain_visibility. True shows terrain, false hides it."),
            P("y", "number", "Maximum visible map geometry world Y for terrain_height. Omit to clear the cutoff."),
            P("rgb", "string", "Authored #RRGGBB."),
            new("objectiveIndex", "integer", "Signed objective index. No objective is -1.", Minimum: int.MinValue, Maximum: int.MaxValue),
            PointParameter("position", "XYZ in game units."),
            new("points", "array", "XYZ vertices for an outline.", Items: PointParameter("", "XYZ in game units."), MinItems: 1, MaxItems: ZmapCodec.MaximumVertices),
            new("fraction", "number", "Position strictly between edge endpoints.", NumberMinimum: 0, NumberMaximum: 1),
            new("offset", "integer", "Zero-based read offset.", Minimum: 0, Maximum: ZmapCodec.MaximumVertices),
            new("limit", "integer", "Page size, default 64.", Minimum: 1, Maximum: 256)
        ], true, async (a, token) =>
        {
            string action = Text(a, "action");
            if (action == "state") return Result(ZmapState());
            if (action == "enter")
            {
                if (TargetDocument(a) != shownDocument) throw new StudioCommandException("stale_document", "Show the source map first.");
                RequireNoDrafts(shownDocument);
                EnterZmap();
                return Result(ZmapState());
            }
            var editor = zmap ?? throw new StudioCommandException("not_ready", "Enter ZMAP mode on a source map first.");
            if (editor.Document != TargetDocument(a)) throw new StudioCommandException("stale_document", "The ZMAP belongs to a different source map.");
            Guid? outline = Identity(a, "outline"), vertex = Identity(a, "vertex");
            int offset = Int(a, "offset"), limit = Int(a, "limit", 64);
            if (action == "outlines")
            {
                var map = editor.RequireMap();
                var page = map.Outlines.Skip(offset).Take(limit).Select(o => new
                { o.Id, rgb = o.Color.ToString(), o.ObjectiveIndex, objective = ZmapObjectiveLabel(o.ObjectiveIndex, editor.ObjectiveCount), vertexCount = o.Vertices.Length });
                return Result(new { state = ZmapState(), total = map.Outlines.Length, offset, outlines = page.ToArray() });
            }
            if (action == "vertices")
            {
                var points = editor.FindOutline(Required(outline, "outline")).Vertices;
                return Result(new { state = ZmapState(), total = points.Length, offset,
                    vertices = points.Skip(offset).Take(limit).Select(p => new { p.Id, position = Coordinates(p.Position) }).ToArray() });
            }
            if (editor.Id != Identity(a, "session") || a["revision"]?.GetValue<long>() != editor.Revision)
                throw new StudioCommandException("stale_revision", "Read source_zmap state and retry against the current session and revision.");
            if (zmapBusy) throw new StudioCommandException("busy", "A ZMAP file operation is in progress.");
            if (action is not ("draft_add" or "draft_apply" or "draft_cancel" or "draft_set" or "split_pick" or "frame" or "terrain_visibility" or "terrain_height") && !(action == "insert_vertex" && zmapInsert))
                RequireZmapIdle();
            if (action is "new" or "open" or "exit" && editor.Dirty && !Flag(a, "discard"))
                throw new StudioCommandException("unsaved_changes", "Save the ZMAP or explicitly discard its edits.");
            try
            {
                var color = a.ContainsKey("rgb") ? ZmapEditorSession.ParseColor(Text(a, "rgb")) : (ZmapColor?)null;
                int? objective = a.ContainsKey("objectiveIndex") ? Int(a, "objectiveIndex") : null;
                Vector3? position = a["position"] is JsonArray p ? ReadZmapPosition(p) : null;
                switch (action)
                {
                    case "exit": LeaveZmap(); break;
                    case "new": editor.Replace(NewZmap()); break;
                    case "open":
                        var opened = await ReadZmapAsync(FullPath(a, "path"), token);
                        token.ThrowIfCancellationRequested();
                        editor.Replace(opened);
                        break;
                    case "select": editor.Select(outline, vertex); break;
                    case "frame": FrameZmap(Text(a, "target", "map")); break;
                    case "terrain_visibility":
                        if (a["visible"] is not JsonValue visible) throw new StudioCommandException("invalid_argument", "Supply visible.");
                        SetZmapTerrainVisibility(a.ContainsKey("zone") ? checked((byte)Int(a, "zone")) : null, visible.GetValue<bool>());
                        break;
                    case "terrain_height": ApplyZmapTerrainHeight(a["y"] is JsonValue height ? (float)height.GetValue<double>() : null); break;
                    case "add_outline":
                        if (a["points"] is not JsonArray rows) throw new StudioCommandException("invalid_argument", "Supply points.");
                        editor.Add(rows.Select(row => ReadZmapPosition(row!.AsArray())).ToImmutableArray(), color ?? ZmapColor.White, objective ?? -1);
                        break;
                    case "move_vertex":
                        Guid owner = Required(outline, "outline"), point = Required(vertex, "vertex");
                        Vector3 target = RequiredPosition(position);
                        if (Flag(a, "snap"))
                        {
                            var shown = editor.FindOutline(owner);
                            target.Y = shown.Vertices[ZmapEditorSession.FindVertex(shown, point)].Position.Y;
                            var viewport = ZmapLayer.Viewport!;
                            if (viewport.TrySnapZmapVertex(target, out var snapped)) target = snapped;
                        }
                        editor.Move(owner, point, target);
                        break;
                    case "insert_vertex":
                        if (zmapInsert && editor.Outline != outline) throw new StudioCommandException("pending_drafts", "Insert on the selected outline.");
                        editor.Insert(Required(outline, "outline"), Required(vertex, "vertex"), Number(a, "fraction"));
                        zmapInsert = false;
                        break;
                    case "remove_vertex": editor.RemoveVertex(Required(outline, "outline"), Required(vertex, "vertex")); break;
                    case "remove_outline": editor.RemoveOutline(Required(outline, "outline")); break;
                    case "set_fields":
                        if ((position != null) != (vertex != null)) throw new StudioCommandException("invalid_argument", "Supply both vertex and position.");
                        editor.SetFields(Required(outline, "outline"), vertex, position, color, objective);
                        break;
                    case "draft_begin": BeginZmapDraw(); break;
                    case "draft_split": BeginZmapSplit(); break;
                    case "split_pick":
                        PickZmapSplit(Required(vertex, "vertex"), a.ContainsKey("endpoint") ? Text(a, "endpoint") == "first" : null);
                        break;
                    case "draft_insert":
                        if (SelectedZmapOutline().Vertices.Length < 2) throw new StudioCommandException("not_ready", "Choose an outline with an edge.");
                        zmapInsert = true;
                        editor.Touch();
                        break;
                    case "draft_add":
                        if (zmapDrawing == null) throw new StudioCommandException("not_ready", "Start an outline draft first.");
                        AddZmapDraftPoint(RequiredPosition(position));
                        break;
                    case "draft_set": StageZmapFields(editor, position, color, objective); break;
                    case "draft_apply":
                        if (zmapSplit != null) FinishZmapSplit();
                        else if (zmapDrawing != null) FinishZmapDraw();
                        else if (zmapFieldsDirty) ApplyZmapFields();
                        else if (HasZmapDraft) throw new StudioCommandException("pending_drafts", "Complete the ZMAP gesture first.");
                        break;
                    case "draft_cancel": CancelZmapDraft(); break;
                    case "undo": ChangeZmapHistory(false); break;
                    case "redo": ChangeZmapHistory(true); break;
                    case "save": await SaveZmapAsync(editor, null, token); break;
                    case "save_as": await SaveZmapAsync(editor, FullPath(a, "path"), token); break;
                }
                CommitRunningJob();
                return Result(ZmapState());
            }
            catch (InvalidDataException error) { throw new StudioCommandException("invalid_argument", error.Message); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new StudioCommandException("file_error", error.Message); }
            finally { RefreshZmap(); }
        });

        static Guid Required(Guid? id, string name) => id ?? throw new StudioCommandException("invalid_argument", $"Supply {name}.");
        static Vector3 RequiredPosition(Vector3? value) => value ?? throw new StudioCommandException("invalid_argument", "Supply position.");
        static Guid? Identity(JsonObject args, string name) => !args.ContainsKey(name) ? null : Guid.TryParse(Text(args, name), out var value)
            ? value : throw new StudioCommandException("invalid_argument", $"Use a valid {name} identity.");
    }

    private static Vector3 ReadZmapPosition(JsonArray array)
    {
        Vector3 position = new((float)array[0]!.GetValue<double>(), (float)array[1]!.GetValue<double>(), (float)array[2]!.GetValue<double>());
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new StudioCommandException("invalid_argument", "Supply finite single-precision XYZ coordinates.");
        return position;
    }

    private void StageZmapFields(ZmapEditorSession editor, Vector3? position, ZmapColor? color, int? objective)
    {
        if (HasZmapDraft && !zmapFieldsDirty) throw new StudioCommandException("pending_drafts", "Finish or cancel the current ZMAP gesture.");
        if (position != null && editor.Vertex == null) throw new StudioCommandException("not_ready", "Select a vertex.");
        var outline = SelectedZmapOutline();
        if (objective is int index && index != outline.ObjectiveIndex && (index < -1 || index >= (editor.ObjectiveCount ?? 10)))
            throw new StudioCommandException("invalid_argument", "Choose No objective or an available mission objective slot.");
        if (color is { } rgb) zmapColor.Text = rgb.ToString();
        if (objective != null) zmapObjectives.SelectedValue = objective;
        if (position is Vector3 value)
        {
            zmapX.Text = value.X.ToString("R", CultureInfo.InvariantCulture);
            zmapY.Text = value.Y.ToString("R", CultureInfo.InvariantCulture);
            zmapZ.Text = value.Z.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
