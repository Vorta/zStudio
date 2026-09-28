# 3D inspection and mission coordinates

Whole world, model and animation previews share an inspection overlay. The top-right readout separates the pointed triangle's world XYZ from the object's world origin. Surface coordinates disappear over empty space and when the pointer leaves the viewport; a selected-object summary remains. Picking includes transparent triangles and does not test individual texture alpha pixels. Grid, horizon and editor helpers do not supply geometry hits.

Left-click a surface or AI marker to pin a floating card. It follows the selected object and stays inside the viewport, with offscreen/inactive labels when appropriate. Copy buttons preserve numeric round-trip precision and coordinate-space labels. Animation cards distinguish runtime instance IDs from original source nodes; expired instances do not silently bind to another copy. Inspection does not change animation events or playback.

The mouse wheel scrolls the floating card when the pointer is over its contents or scrollbar, including coordinate fields. Reaching a scroll limit does not zoom the scene. Camera gestures begin on the scene outside the card; native card buttons, fields and scrollbar retain their own input.

Closing the card clears its tree selection and Frame selected target. Compatible preview refreshes restore a retained source row's framing and inspection without selecting an arbitrary runtime copy. Isolating a pickup child from the tree keeps the complete placed pickup, consistently through the toolbar and MCP. Geometry shared by multiple mission actors has no uniquely editable placement and stays read-only.

Details include model/material/stored texture and LOD, node/parent identity, soil, CanModify and ClipTo. AI cards retain ordered directed links, negative sentinels and the raw node integer. Pickups include placement identity, amount and respawn metadata. Tanks include their AIV placement, template, heading and difficulty scope. Scene local, rendered world and authored placement coordinates are labeled separately.

## Document scene hierarchy

**Document scene** shows an expandable hierarchy for the active 3D preview, including placed mission objects. With an animation open, it shows the stable bound scene; spawned copies and runtime reparenting remain separate in the runtime inspection card. When no compatible 3D preview is active, it shows the selected file's stored nodes.

Rows include names, classes, node indices and child counts. Expand arrows and indentation show parent-child relationships. Shared references appear beneath each parent and carry a shared label; world-partition references are labeled separately. **Unlinked nodes** keeps disconnected roots and cyclic components accessible. Invalid references, cycles and the 256-level display limit are explicit leaves. Tooltips distinguish tree parents, stored parents, source nodes, models and mission placements.

Select a row to inspect it and make it the Frame selected target. Selection does not move the camera or open/retarget Properties. Nodes without rendered geometry remain selectable and clear an unrelated geometry card. Static-view Isolate includes the selected parent's descendants. Use the row context menu or Alt+Enter for independently pinned Properties. Clicking geometry or selecting a runtime card reveals its scene node without switching Navigator tabs or choosing a different runtime copy. Tree expansion and selection persist through presentation changes and compatible preview refreshes for the document's lifetime; mission matches use provenance rather than clone indices. Presentation-only expansion, collapse and reveal never commit pending input; collapsing a selected branch retains the hidden descendant's source selection. Intentional node selection uses the existing draft guards.

## Editing positions

In Whole world, enable **Unlock editing**, then use **Edit** for supported pickup, AI navigation node and AIV vehicle coordinates, including AI tanks. The toggle starts off for each new map document; checked means editing is enabled and the padlock is open. Turn it off to lock all these coordinate edits. Inspection, selection, copying, framing, undo/redo and saving remain available while locked. Resolve any pending draft before changing the lock; Cancel retains the draft and previous lock state. Other geometry and runtime animation poses remain read-only. Tank templates must resolve unambiguously. Actors without an AIV placement remain inspectable without inventing a placement record.

Enter numbers with a decimal point, then click **✓** or press Enter to apply all three coordinates as one undo step. Cancel or Escape in a coordinate field discards the draft. Incomplete/non-finite values remain uncommitted. Navigation, selection, scene refresh, save and close explicitly resolve pending input; focus loss does not confirm it. Existing Properties remains independently pinned.

Pickup counterparts retain the existing original-type/position/rotation matching. Tanks require a unique original template and starting XYZ/heading in each effective difficulty resource. Links survive movement; the same displacement applies to linked records, shared fallback resources move once, and ambiguous/missing counterparts stay unchanged. The card shows affected difficulties before confirmation. AI edits affect one exact source record.

The map document owns these edits. Its Undo/Redo/Save commands handle them chronologically. Pickup, AI and tank coordinate patches share archive baselines and save together. Only coordinate nodes change; unrelated bytes, AI links, headings, unknown values and archive ordering are preserved. Saves check external changes, stage/reparse/read back archives, and verify unrelated-byte preservation before atomic replacement. Protected datasets require Save As; the new destination becomes the subsequent save target, shown as **Save archive** in the card. Partial multi-file publication reports which files remain dirty and retains every requested destination. Ordinary Save retries unpublished copies as new files without returning to source paths or overwriting competing files. Working snapshots feed dependent previews without changing cached mission baselines.

## MCP

`zstudio_scene_inspect` requires the current `preview` UUID and accepts either an opaque `target` or `screenPoint: [x,y]` in viewport DIP. By default it reads the selected card, falling back to hover; hover state is also returned separately. Results include document/revision, target and draft state. Queries never move/capture the physical pointer. Targets expire with their render instance or AI snapshot.

`zstudio_scene_card` provides these actions:

- `select`: supply `target`, or scene `node` with optional string `runtime` ID. `clear` closes the selection.
- `copy`: optional `field` label; omitted copies all details. Returns GUI copy text; `clipboard: true` also writes the clipboard.
- `begin`: requires owning `document` and expected `revision`.
- `set`: requires document/revision, current `token` and three `position` strings, including temporary incomplete numeric input.
- `apply` / `cancel`: require document/revision and current draft token. Apply additionally checks the revision captured at draft creation.

`drafts` and `resolve_drafts` accept `target: "scene"`; resolution requires the exact draft token. Existing `undo_redo` and `save_document` handle accepted edits. Stale targets/revisions/drafts, locked coordinate edits and conflicting archive owners are rejected. Discovery remains windowless; local MCP stays opt-in.

The compatibility-named `zstudio_pickup_lock` command controls the shared lock for pickups, AI nodes and supported AIV tanks. Pass `locked: false` to enable the GUI's **Unlock editing** toggle, or `true` to turn it off. The existing `PickupsLocked` document-state field reports this shared lock. Lock changes require the current document revision and explicitly resolved drafts. Card scrolling is presentation-only; MCP inspection/copy already exposes its complete contents.

Tokens identify both the draft lifetime and its input: canceling and reopening identical coordinates produces a new token. Old requests cannot apply, change or cancel the new draft. Scene selection and isolation reject active Fly or pickup-drag interactions before changing selection or the camera.

`zstudio_scene_tree` shares Document scene. Supply `document` and default/explicit `action: "read"` for paged roots. The result identifies the hierarchy `context`, optional active `preview`, source, selection and `children` page. Read a row's direct children with its opaque `row` and current context. Rows expose parent occurrence, relation, node/source-node/model identity, shared/reference warnings, child count and expansion/selection state. Use the usual `offset`, `limit` (at most 200) and `query` parameters; queries filter the requested level before paging.

Actions `expand` and `collapse` require a row; `reveal`, `select` and `properties` accept a row or a node index. All actions require the current context and, for preview hierarchies, the returned preview ID. Reveal only expands ancestors and scrolls the row into view; select additionally uses the shared inspection/draft guards. Properties opens the same independently pinned window as the row context menu, including stored nodes without a current preview, and rejects unresolved drafts. Stale contexts and row IDs are rejected. The existing flat `scene_nodes` query remains available.
