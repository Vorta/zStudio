# MCP

zStudio lets local AI agents work in its visible workspace through the Model Context Protocol. An agent and you share the same documents, edits, undo history, drafts, previews and verified saves; there is no separate headless mode. The connection is a named pipe restricted to your Windows user, with no network port.

## Connect

1. Open **Tools → MCP integration…** (also on the welcome screen) and check **Enable local MCP access**. Access is off by default; the choice is remembered. An agent cannot turn it on.
2. Click **Copy configuration** and add it to your agent's MCP configuration. The generic JSON form is:

```json
{
  "mcpServers": {
    "zStudio": {
      "command": "D:\\Tools\\zStudio\\zStudio.exe",
      "args": ["--mcp"]
    }
  }
}
```

3. Ask the agent for the workspace state. The first workspace request opens zStudio, or attaches to the running instance of that installation. Starting the agent and discovering tools leaves zStudio closed.

Keep `zStudio.exe` at a stable path. If several instances of the same installation run, add `"--instance", "ID"` to `args`, with the ID shown in the integration window (valid for that running instance). **Disconnect clients** drops current connections; restart the agent's connection afterwards. Unchecking **Enable local MCP access** revokes access. A workspace call that answers `access_disabled` means access is off. After moving or updating zStudio, check the configured path and restart the connection. Any process running as your Windows user can connect while access is enabled.

### Codex

Add this to `%USERPROFILE%\.codex\config.toml` (or your `CODEX_HOME`'s), updating any `mcp_servers.zstudio` section already there rather than adding a second:

```toml
[mcp_servers.zstudio]
command = 'D:\Tools\zStudio\zStudio.exe'
args = ["--mcp"]
enabled = true
startup_timeout_sec = 60
tool_timeout_sec = 120
```

Single quotes keep the backslashes. Alternatively run `codex mcp add zstudio -- 'D:\Tools\zStudio\zStudio.exe' --mcp`, then `codex mcp get zstudio` to check it. Restart Codex after changing its configuration.

## Discovery

`tools/list` returns every tool with a typed input schema and a description; the descriptions are the reference for each tool's behaviour and results. `resources/list` offers `zstudio://state` and `zstudio://capabilities`. Initialization, discovery and schema reads never open a window. Start a session with `zstudio_state` and `zstudio_capabilities`.

Arguments are validated against their schemas before anything runs; a malformed request answers `invalid_argument`. File and folder arguments (`open_root`, `open_document`, export destinations) must be full paths. Project paths in source tools (`model`, `recipe`, `file`) are relative to the project.

## Workspace model

- **Source project**: a folder with `data` and `gamegen`, opened with `zstudio_open_root` and `project: true` (or **File → Open workspace…**), or created with `zstudio_source_reconstruct`. One map is open at a time: list maps with `zstudio_source_world_list` and open one with `zstudio_source_world_open`. A source project is where editing happens; game files are produced only by `zstudio_source_export`.
- **Compiled root**: a folder of shipped game files, opened only through `zstudio_open_root` without `project`. It is read-only: inspect, preview and export its assets (see [Compiled game files](compiled-files.md)).

## Identities and revisions

- Use the identities that tools return: document UUIDs, asset kind and index, sequence and event UUIDs, node indices, pickup source identities, row identities in `zstudio_scene_tree`. Names are labels and are not unique.
- Every accepted edit increments the document revision. Mutations take the expected `revision`; a stale one answers `revision_conflict`.
- Preview commands take the current `preview` UUID from `zstudio_state`; a replaced preview answers `stale_preview`. Tree and inspection reads return a `context` token that later actions check.
- The GUI can act between your calls. Re-read state after an edit or navigation; a request whose target changed answers `context_changed` rather than acting on something else.

## Changing a source project

- **Transactions.** An edit is prepared and evaluated in memory before it is accepted, as one undo step. A change that cannot be evaluated answers `build_failed`; a file changed by another program answers `external_change`. Neither changes anything. Each edit returns the document to use next.
- **Drafts.** Unfinished GUI input (a Properties field, a scene card, a zone or AI network card) blocks writes with `pending_drafts`. Read it with `zstudio_drafts` and apply or discard it with `zstudio_resolve_drafts`; nothing is committed or discarded implicitly.
- **Placement precision.** Positions of pickups, AIV vehicles and AI network nodes, and AIV headings, are stored with one decimal; pickup rotations in radians with four. Only changed values are rewritten; a change below that precision changes nothing.
- **Busy.** While the map is being evaluated, saved, or a GUI dialog waits for the user, mutations answer `busy`; reads and `zstudio_operation` keep working.
- **Undo and save.** `zstudio_undo_redo` steps through the project's shared history. `zstudio_save_document` writes every changed source file as one recoverable publication, after checking for external changes. `zstudio_source_changes` lists unsaved files, the history and a line diff. `zstudio_close_document` needs `discard: true` to drop unsaved edits.
- **Recovery.** An interrupted save is listed in `zstudio_problems` when the project opens; decide it with `zstudio_source_recovery` and `zstudio_source_recovery_resolve` (roll back, complete or abandon). MCP shows no dialogs.
- **Export.** `zstudio_source_export` builds game files from the saved project (save or discard edits first). Without `destination` it only checks that everything builds. With a destination outside the project it stages and re-parses every output (or the `outputs` you list) and writes nothing if one fails; replacing game files already in the destination needs `overwrite`, and they are restored if writing fails. `zstudio_source_export_selection` shares the GUI checkbox choices across projects and restarts: `action: get` returns paged output paths, groups and selected states; `select` or `clear` updates specified `outputs`, or every current output if omitted. These actions never export. Pass the desired checked paths explicitly to `source_export`; its omitted `outputs` still means all files.

## Operations

Long tools (those whose description says they return an operation ID, including `zstudio_ai_path` and VTOL route analysis) return an operation object (`id`, `Name`, `State`, `Cancellable`, `result`), possibly already completed. Poll `zstudio_operation` until `completed`, `failed` or `canceled`, then read `result`. Exports, validation, source project and map operations and world comparisons can be cancelled with `zstudio_operation` `cancel`; opening another root or closing zStudio cancels obsolete work. Operations continue after the client disconnects; the newest 128 are kept.

## Paging and limits

- Lists take `offset` and `limit` and return `nextOffset` (null at the end). A page can hold fewer rows than `limit` so that complete identities and paths fit; follow `nextOffset`. A single row too large to return answers `too_large`.
- Requests are limited to 2 MiB per message and 1 MiB of arguments; results to 4 MiB. Long values are shortened with an explicit `…Truncated` flag and the full count; identities and paths are never shortened. Use paging, `zstudio_source_bytes` (at most 4,096 original bytes) or `zstudio_export` for complete data.
- `zstudio_capture` returns a PNG of the current preview or of a zStudio window (never other applications); 3D captures fit within `width` and `height` (at most 4,096) without resizing the view.

## Errors

| Code | Meaning and what to do |
| --- | --- |
| `invalid_argument` | Bad or out-of-range input; correct it. |
| `revision_conflict` | The document changed; read `zstudio_state` and retry with the current revision. |
| `stale_document`, `stale_record`, `stale_preview`, `stale_context` | The target was replaced or closed; read it again. |
| `context_changed` | The GUI or a newer request changed the target meanwhile; read again and retry. |
| `pending_drafts`, `draft_conflict` | Unfinished input; resolve it with `zstudio_resolve_drafts`. |
| `busy` | Evaluation, a save or a dialog is in progress; retry later. |
| `not_ready` | The view or data the command needs is not shown yet (for example while a map reloads). |
| `unsaved_changes` | Save, or close with `discard: true`. |
| `build_failed` | The edit would not build or the game would reject it; the message says why. |
| `external_change` | A source file changed on disk; reopen or reload the map. |
| `read_only`, `unsupported` | The target cannot be changed this way (compiled files are read-only). |
| `source_map_required`, `no_project`, `not_project`, `no_workspace` | Open a source project (a folder with `data` and `gamegen`) or a map first. |
| `too_large` | A row or result exceeds the limits; narrow the request. |
| `io_failed` | A file could not be read or written. |
| `access_disabled` | MCP access is off in **Tools → MCP integration…**. |

## Viewport

The camera is moved semantically: `zstudio_camera` reads the pose and orbits, pans, zooms, dollies, frames or sets views and projections, as the mouse does in the GUI ([3D navigation](3d-navigation.md)); an optional `screenPoint` picks the surface under that point as orbit pivot or zoom target. MCP never captures your mouse or keyboard. Manual camera moves leave an animation's Follow camera. `zstudio_presentation` toggles Fullscreen (**F11**) and Hide UI (**F10**). `zstudio_scene_options` sets what the toolbar sets (LOD, difficulty, textures, wireframe, face orientation, bounds, horizon, surface highlights, AI overlays) and, in a map, how the view draws the [player tank](map-editor.md#player-tank): `bftType` (`tracked`, `amphibious`, `hover`, `sub`), `bftPrimary` (`RFPG`, `ERFPG`) and `bftSecondary` (the pickup catalog's names without `_WEAPON`, such as `HEMORTAR` or `NAPALM`), applied together; a type the map's tank has no transition for, a beam or fire weapon in sub, a compiled world or a difficulty without a player tank is refused (`unsupported`) with the reason, and a change while the map is still opening is `not_ready`. `zstudio_preview_state` reports the shown preview's options, camera, playback, the state of AI path editing and VTOL routes, and `bft`: the tank's row node, the choice, what the view draws (`shown`) and the mission's start (`defaults`, from its data), each type's availability and reason, the secondaries, the gun models drawn, `posing` while the view rebuilds with the choice, `rebuilding` while a change would be refused as busy, and the card's note (compiled worlds report their defaults with `adjustable: false`; a tank that could not be prepared reports only `adjustable: false`, its `vehicle` and the `failure`). `zstudio_scene_tree` marks the tank's row with `playerTank: true`. In a map, `zstudio_scene_tree` `show` (and an `isolate` that ends or moves an isolation) and `zstudio_scene_selection` `show_all` and `isolate` also select in the view an object that was selected in the tree while hidden: the view selection, the Properties pane and `zstudio_scene_inspect` `selected` change, and its placement can be edited. Selecting it again does the same once the view shows it (the GUI's "Click it in the view"): `zstudio_scene_card` `select` with its `node` or `target`, or `zstudio_scene_tree` `select` of its row. While a draft is open those selects and `zstudio_scene_selection` are refused with `pending_drafts`; `zstudio_scene_tree` `show` and an `isolate` that ends an isolation still change the view but leave the selection as it is (with a card draft open, starting or moving an isolation is refused too). Only an open `zstudio_ai_path` or `zstudio_animation_paths` draft is followed up: once the application is idle after its confirm or cancel returned, the object is selected, so read `zstudio_scene_inspect` or `zstudio_preview_state` afterwards. In a map, units and pickups are never isolated (`zstudio_scene_selection` `isolate` refuses them as `unsupported`).

## Updates

`zstudio_state` reports `update`: whether a newer zStudio release is published (`status` `available`, `current`, `unknown` with a `reason`, `disabled` or `checking`), the `current` and `latest` versions, the release page `url` and when a check last got the latest release from GitHub (`checkedUtc`). `zstudio_update_check` reads the same, asks GitHub now (`check`, as **Help → Check for updates**; this works with automatic checks off, because you or the agent asked) or turns automatic checks on or off (`automatic`, as **Help → Check for updates automatically**). MCP never opens the release page; the status bar's **Update available** does that when you click it.

World objects in `zstudio_scene_tree` expand into their authored child objects, geometry and LOD branches, including initialized hidden states. Selecting a child selects that exact node; map picks still select the whole placed object. Child `hide`/`show` changes a preview-only visibility gate and can replace the preview, so retain the returned `context` and `preview`. Each physical part row reports `visibility` (`DefaultVisible`, `Visible`, `Override`, `EffectiveVisible`, `Reason`); a visible gate can still be blocked by its parent, isolation or LOD. `zstudio_source_map_parts_visibility` with `action: "reset"` restores the object's initialized gates. **Show all** clears ordinary row hiding and isolation while retaining these part gates.

For exact child inspection and supported source edits, pass `scope: "node"` to `zstudio_source_world_object`, `zstudio_source_world_command` or `zstudio_source_world_object_edit`. The default `scope: "object"` retains whole-object targeting. Unsupported child copy, delete or reparent operations are refused instead of targeting the parent. Source edits to a shared model still affect every load of that model; visibility overrides apply only to the current preview occurrence.

## Example

Move an object in a map and check that the project still builds:

1. `zstudio_open_root` with the project folder and `project: true`; poll the operation.
2. `zstudio_source_world_list`, then `zstudio_source_world_open` with `mission: "m1"`; poll it and keep the returned document UUID and revision.
3. `zstudio_scene_tree` with the document and a `search`, to find the object's row and `node`.
4. `zstudio_source_world_object` with the node, to read its transform and where it comes from.
5. `zstudio_source_world_object_edit` with `document`, `revision`, `node`, `action: "transform"` and `position`; poll it and keep the new revision.
6. `zstudio_source_changes` to review, `zstudio_save_document` to save.
7. `zstudio_source_export` without `destination` to check the build, then with a destination folder to write game files.

## Tool areas

All tools are named `zstudio_*`. "MCP only" marks tools without a GUI path. The GUI side is described in [Source projects](source-project.md), [Map editor](map-editor.md) and [Compiled game files](compiled-files.md).

| Area | Tools | In the GUI |
| --- | --- | --- |
| Help | `help` | **Help** menu: the online guides and release notes of the installed version, **About zStudio** |
| Workspace | `state`, `capabilities`, `window`, `workspace_view`, `presentation`, `open_root`, `files`, `search`, `open_document`, `close_document`, `reload_document`, `assets`, `asset_filter`, `select_asset`, `related`, `problems`, `operation` | Window, **File** and **View** menus, Navigator |
| Updates | `update_check` (also `state`'s `update`) | Status bar **Update available**, **Help → Check for updates** / **Check for updates automatically** |
| Source project | `source_reconstruct`, `source_status`, `source_profile`, `source_dithering`, `source_export_selection`, `source_export`, `source_changes`, `source_bytes`, `source_recovery`, `source_recovery_resolve`, `validate` | Welcome screen **Initialize**, **Export** menu, **Tools → Validate source file on disk** |
| Maps | `source_world_list`, `source_world_open`, `scene_tree`, `scene_inspect`, `scene_card`, `scene_properties`, `scene_nodes`, `scene_selection`, `scene_options` | Map picker, World tree, Properties pane, command bar |
| Objects | `source_world_object`, `source_world_object_edit`, `source_world_command`, `source_world_models`, `source_world_definitions` | Properties pane (reparenting with `parent` is MCP only) |
| Adding models | `source_world_add_model`, `source_map_parts`, `source_map_parts_visibility` | MCP only |
| Pickups and units | `pickups`, `pickup_move`, `source_pickup_add` | **Tools → Add pickup…**, scene card |
| AI networks | `ai_networks`, `ai_nodes`, `ai_selection`, `source_ai_network`, `ai_path` | AI Networks section and network card; whole-path editing (`ai_path`) is MCP only |
| Animations | `animation_mode`, `animation_scenario`, `source_animation`, `animation_timeline`, `animation_paths`, `animation_transport`, `animation_options`, `animation_runtime`, `animation_select` | Animation mode (command bar, animation panel, Scenario tab), the definition card's Events, Activation, Paths and Drop windows |
| Zones | `source_zones`, `source_zone_label`, `source_zone_tool`, `source_polygon_zones` | Zones section, **Tools → Paint polygon zones** / **Erase polygon zones** |
| Terrain | `source_terrain`, `source_terrain_create`, `source_terrain_edit`, `source_terrain_convert` | **Tools → Create terrain…** / **Convert to editable terrain…**, terrain Properties |
| Blender | `source_blender_extension`, `source_blender_checkout`, `source_blender_checkouts`, `source_blender_update` | **Tools → Edit in Blender…** / **Update from Blender export…** |
| Edits | `drafts`, `resolve_drafts`, `undo_redo`, `save_document`, `properties_open`, `properties_close`, `properties_state` | **Edit** menu, the map command bar's Undo, Redo and Save, Properties window |
| Compare worlds | `world_compare`, `world_compare_tree` | **Tools → Compare worlds…** |
| View | `camera`, `capture`, `preview_state` | Viewport, toolbar |
| Compiled files | `inspect_asset`, `archive_members`, `zrd_nodes`, `resource_select`, `script_records`, `script_select`, `texture_view`, `texture_palette`, `sound_transport`, `export`, `model_bundle_export` | Viewers and **Export selected…** of a compiled root ([Compiled game files](compiled-files.md)); `model_bundle_export` is MCP only |
| Animation preview | `animation_records`, `animation_select`, `animation_transport`, `animation_options`, `animation_runtime`, `event_catalog`, `references` | Animation viewer of a compiled root |
| MechWarrior 3 | `missions`, `motion_records`, `motion_preview`, `mech_models`, `ai_valves`, `ai_valve_selection` | Viewers of a compiled MechWarrior 3 root |

Animation path metadata includes `Kind` (`object` or `camera`), shown as teal or orange guides. Source paths and playback query current active standable surfaces beneath moving node origins, excluding the bound assembly; runtime transforms and morphs update collision. Procedural object movement stops at world Y −500 or 500 without impact cues; keyframes and cameras are unrestricted. `preview_state.animation` reports `collisionMode` (`terrain`, `plane`, `none`), `proceduralYLimits`, and limit diagnostics in `status`; path diagnostics appear in `animationPaths`. `animation_options.collision` controls the isolated flat plane and is refused in source-map preview, where terrain collision is automatic.

In a source map, `animation_mode` is Animation mode: `state` reports whether it is `active`, the `opened` definition and binding, Back/Forward, the `keptScenario` summary and the `layout`. `open` takes `document`, `revision` and a `definition` (its binding: the first, or `entry`); it resolves no drafts (pending ones are refused), never plays, evaluates or moves the camera, keeps the scenario with the opened binding as its primary (the previous primary stays as a start; refused at 32 starts) and returns a new `preview` id. `exit` stops the run, keeps the scenario and selects the definition. `back` and `forward` walk the opened definitions; `binding` takes `entry`. `layout` sets `view` (`structure`, `timing`, `split`; split needs a 1,300 DIP panel), `panelHeight` (at least 240), `rightTab` (`inspector`, `scenario`), `tree` and `diagnostics`. `animation_transport` drives the opened binding's run with seconds: Play prepares lazily, Pause keeps a silent complete frame, Seek evaluates silently at 1/60 s ticks, `previous`/`next` step one tick, and Stop restores the initialized static world, including the chosen player tank.

`animation_scenario` reads and changes the map's one scenario, the object the Scenario tab shows (a change shows there at once). `read` returns the starts, the call paths summary, condition overrides, inputs, rules and whether it is customized. `starts` replaces the schedule with up to 32 `{id, entry, seconds}` records (primary id `00000000-0000-0000-0000-000000000000`, the opened binding; 0–3,600 seconds). `include` takes canonical call `paths` and `included`: including adds the calls leading to a path, excluding removes the paths below it. `includeAll` includes every reachable call except back-links and returns what it did; `excludeAll` clears them. `conditions` takes `{instancePath, entry, event, value}` records, `value` `game` (remove the override), `true` or `false`, on an IF or ELSEIF event. `inputs` takes `player` (`{kind: atStart}`, `{kind: custom, x, y, z}` or `{kind: none}`), `seed`, `effectsDetail` (`High`, `Medium`, `Low`), `phase`, `height` and `beamOrigin` (`[x, y, z]`, or empty for the root). `pick` places `target` (`player`, `beamOrigin`) at the surface under `screenPoint` (viewport pixels), as Pick in view does. `rules` takes `prerequisites` (`check`, `ignoreStarts`, `ignore`), `rangeGates` (`game`, `ignore`) and `zoneGates` (`ignore`, `view`). `reset` restores the defaults with the opened binding as primary; `fitRange` sets the playback range to the latest start plus 30 seconds. Every change stops playback and re-evaluates up to the playhead before the call returns; it never edits source files and makes shown VTOL drop analysis stale.

`animation_timeline structure` pages `entries`, `sequences`, `events`, `calls`, `keyframes` and `ignored` records without running animation. Pages contain at most 100 rows; use the returned `NextOffset` (or `nextOffset` for entries), and the exact sequence/event GUIDs and source-field provenance. `calls` reads only direct references. Its canonical `Path` identifies a particular callsite lineage, including repeated or cyclic references. `animation_scenario include` enables that path; children start excluded, so selecting an animation such as `start_north` does not run its child chain. An excluded waited call blocks its calling sequence; an included unresolved call reports unavailable and advances; an included duplicate retains the runtime's no-new-instance behavior.

`animation_timeline options` takes typed `changes`. Source/Run projection (`view`), `followPlayhead`, `visibleRange`, `labelWidth` (80–720 DIP) and `fit` control presentation; `speed`, `volume`, `mute`, `replay`, `followCamera`, `effects` and the playback `range` are the transport's. The visible range is independent of the playback `range`, with playback initially 0–30 seconds and the visible range initially at cell zoom; runtime navigation is bounded to 0–3600 seconds. Scenario fields moved to `animation_scenario`; `options` refuses them. `rows` pages the displayed source/run row metadata with `markCount`, omitting mark collections; `marks` pages up to 100 marks for an exact `row`, with `items` and `nextOffset`. Both pages apply their metadata budget before conversion and can return fewer rows. `animation_runtime` also exposes `instances` and scoped `conditions` for source runs. `expand` changes a row's expansion, and `row` selects it or its retained `occurrence` without seeking (selecting a Load more row extends its page). `select` inspects an exact sequence/event and never seeks or changes Runtime/Cleanup phase. `duration` explicitly performs bounded duration analysis.

`animation_paths analyze` with a definition explicitly evaluates guides in independent silent state using the current scenario and inclusion policy. It does not pin, seek or stop the main run, change the camera, or switch the dock. `show` only displays compatible cached guides; `hide` retains the cache and `clear` releases it. Selecting or expanding an animation never evaluates paths. Keyframes are inspected as read-only compiled samples; their shapes are not edited in this version.

The timeline (Animation mode's Timing view) still uses 40 fps editing cells with frame 1 at time zero; `animation_mode`, `animation_scenario` and the transport use seconds. `animation_timeline cell` takes exact `row` and `mark` identities and optional `seek`; it returns bounded source context and current occurrence choices (paged with `offset`), and an optional `occurrence` disambiguates repeated execution. It seeks only current evidence. `frame` takes a one-based, possibly fractional `frame` within 1–144001 and seeks its time; presentation reports `RequestedFrame`, `FrameRate`, `FrameOrigin`, and the result reports actual evaluated seconds. `timing` takes `row` and `frame` and proposes the existing event threshold in its native clock, using the ordinary source draft. `source_animation context` takes a source `record` and optional `scriptTime`, returning read-only excerpts with filenames, line numbers and truncation indicators. Viewing never snaps authored times.
Timing authoring uses `source_animation` and its existing shared draft. The Properties numeric field and native-clock ruler preserve negative and off-grid thresholds; runtime seek limits do not constrain authored time. Starting a draft detaches playback and makes prior drop analysis stale. Apply accepts one project-overlay history step; `save_document` writes sources, and export remains explicit. The separate VTOL analysis (`animation_paths` without a definition) has its own purpose and snapshot and admits only the recognized release calls. Moving a certified drop window uses its checked `set`/`confirm` pipeline and changes only the CALL start offset, preserving the STOP duration. Generic CALL/STOP timing editing remains available, but can change or remove the window relationship; analyze again before a verified window move.

Additional starts have canonical path prefix `/@<guid:N>`; optional `activation` on structure/select accepts paths relative to that start. Included call paths and condition `instancePath` use the full canonical paths, keeping repeated bindings distinct. Removing a start removes its policy paths. Source rows expose `ActivationId` for draggable start markers; Run marks retain actual shared-clock dispatch times. Calls remain excluded by default and only launch when their authored sequence reaches them. All scheduled animations share world state, collision, audio and analysis.
