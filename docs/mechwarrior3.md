# MechWarrior 3 base game

Open the game's `zbd` folder through **File → Open folder**. Supported structures are identified from binary headers and record layouts: GameZ version 27, compiled animation version 39, version 4 motion members and the model-library archive format. Texture packs, prepared scripts, sounds and typed ZRD resources use the existing shared readers and editors. Recoil's version 15 worlds and version 28 animations retain their own layouts.

## Worlds and missions

Open a map's `gamez.zbd` and select **Whole world**. The mission picker replaces the Recoil difficulty picker. It lists authored campaign, instant-action and multiplayer readers; only the selected reader contributes mission actors and AI networks. Shared map/root resources remain available. The selected reader is remembered per map.

Authored AIV positions and headings place uniquely resolved world templates and base mech assemblies. Repeated placements have independent instance transforms and retain archive/member/record identity. Player inventory, custom player mechs, mission-script activation and combat are not simulated. Missing or ambiguous templates remain inspectable in the AIV resource and produce preview notices.

Actor labels in the scene, hierarchy, cards and Properties use at most 128 characters. Truncated labels disclose the original length; they do not identify an instance. Template matching uses the complete authored name, and full names remain intact in the AIV resource and exports. Rejected placement diagnostics use the same bounded label. Invalid hierarchy clones are discarded completely before processing the next placement.

**Unlock editing** enables object cards and selection bounds. **Edit** enables the existing transform draft and movement/rotation controls. MW3 AIV edits affect that mission record only; Recoil difficulty linking does not apply. AI nodes support translation. Gate and other edge-constraint records have no authored XYZ and remain non-spatial constraints. Switching missions preserves coordinate edits and undo history. Save patches changed coordinate scalars through the existing verified archive service.

A valid version-106 constraint may share a `node_NN` key with a spatial node. Constraints have their own record identity and do not make a unique spatial link ambiguous. Repeated or malformed spatial declarations still prevent ambiguous links from binding silently. Large placement lists publish hierarchy edges in one batch, preserving source order and instance identity. Retrying a mission that is still loading waits for its preview; superseded requests cannot save an obsolete selection.

Rendering supports triangle strips and per-corner RGB colors. OBJ export retains vertex colors. Texture resolution includes shared mech texture packs. Scene inspection and the Document scene tree preserve source/member identity when names repeat.

## AI valves

In **Whole world**, enable AI nodes and use **Valves** to open the mission's valve definitions in the reusable Properties window. The adjacent valve overlay toggle adds white outlines around nodes with authored conditions and dashed lines for uniquely resolved edge assignments. It preserves the network's attack-strategy color. With **Unlock editing** enabled, clicking a valve-bound node also exposes its valve summary and **Valve Properties…** button.

Properties distinguishes occurrences by resource, source order and offset; equal names do not merge records. The editor supports the authored `attack_strategy`, `delayupdate`, `destroy`, `shutdown`, `sound`, `sound_once` and `teleport` actions and `all_zero`, `all_nonzero` and `any_nonzero` compounds. Node `valve` tuples retain their optional third integer, `valveunion` retains ordered tuples, and `valve_assign` retains its edge and assigned integer. Objective `valve_change` and `set_valve` uses are inspectable, editable and linked. Unverified integer operands are labeled as stored values. Unknown layouts stay available in the shared Data tree.

1. Open a valve, version-106 network or objectives resource's **Properties**, or use the map controls above.
2. Select the specific occurrence. Use record, binding-target, operand, action/term and reference pages for longer resources.
3. Edit a typed field and press Enter, or use Add/Duplicate/Delete/Move actions. Each accepted operation is one resource undo step. Strings in existing fields use JSON quotes; the new-record name is plain text.
4. Use **Find uses** for exact-name references in the pinned mission scope, then open a result in the same Properties window. Names may be set by mission scripts without a defining action block; absence of a definition is not treated as an error. **Highlight** and **Frame** locate known map associations without executing the valve.
5. Use Undo/Redo and document-scoped Ctrl+S or Save As. Saves use the owning archive's shared verified resource service. Renames and deletes never silently rewrite other occurrences or their references.

The map overlay is an authored-data aid, not a live open/closed or enabled/disabled state. Node/constraint summaries are bounded to 16/1024 attributes, but exact valve filtering, highlighting and framing search every authored node condition, union term and resolved edge assignment. Drawing is capped at 4096 outlines and 1024 dashed assignments across visible networks after filtering. Cards show a shorter summary. Paged source inspection/editing and export retain all records. Missing or ambiguous edge endpoints never invent geometry. Edited dependencies refresh the map; a stale source/snapshot must be rediscovered before editing through MCP.

## Mech assemblies and replacement

Open the model library and select a model member. Each base, lower-detail or HUD member owns a separate hierarchy. **Export referenced models** exports assembled and local OBJ/MTL/PNG files and a manifest recording the source hash, member, local model indices, nodes and transforms.

To replace a mech part:

1. Export its referenced models and edit the corresponding **local** OBJ in Blender. Use game-local +Y up and −Z forward; keep node transforms out of the mesh.
2. Export a triangulated OBJ with UVs and normals. Optional RGB vertex colors use values from 0 to 1.
3. Select the owning library member and choose **Replace models**. Choose the member-local mesh and its shared material, then select the OBJ.
4. Inspect the result. The shared resource editor reparses the member, updates hierarchy bounds and accepts one undo step. The active mech preview and dependent previews refresh automatically with the camera retained, including Undo/Redo. Other archive members and shared materials are preserved.
5. Use **Save As** for a working copy. Replace the material's texture through explicit texture-pack records in the existing texture editor. Mesh replacement does not silently replace textures shared by other parts.

GameZ version-27 replacement also supports the existing source-hash-bound OBJ/PNG batch manifest, updating the world and all local texture variants. Morph/light-bearing models and expansion beyond world-partition bounds are rejected rather than losing authored data.

## Motion clips

Open the motion archive and select a **Motion** asset. The viewer suggests a unique base assembly from the clip/assembly naming or leaves the choice explicit. The assembly picker can bind another library member. Rapid assembly changes retain the playback intent from before loading; an explicit Play/Pause during loading takes precedence, including after cancellation. Accepted motion edits and Undo/Redo immediately refresh the displayed sampler while retaining the camera and playback state. Missing/ambiguous track names keep the stored node pose and produce a support notice.

Assembly binding indexes the selected member's node names once, with ordinal matching and duplicate-name protection. Binding runs off the UI thread and observes cancellation while indexing and binding tracks; superseded requests cannot replace the current selection, including when edits require rebinding during a pending load.

Prepared LOD hierarchies are reused between frames; sampling runs off the UI thread with superseded results rejected. Motion preview supports up to 16,384 visible model placements per assembly. Larger authored libraries remain inspectable/exportable. Preview notices retain 256 bounded messages plus an omitted-count summary.

Bindings to edited libraries retain the selected member UUID from the same snapshot as its geometry, including duplicated/imported members with no original-file index. Renaming, reordering, model replacement and Undo/Redo keep that member bound with camera/playback retained. Selecting another member replaces the binding identity only after a successful load. Deleting the selected member clears the binding instead of selecting another member at its former index.

If an edit or Undo/Redo overtakes library loading, identity lookup uses that exact decoded snapshot in retained edit history. A separately parsed document with identical names/bytes does not share those session identities.

Deleting the displayed motion or replacing it with non-motion data closes that motion viewer and shows the archive's resulting selection, or archive information when empty. Undo/Redo updates the preview as well, including when another Navigator tab is open. Renaming or reordering a surviving motion retains its viewer, playback and camera by member UUID; duplicate names and reused row indices do not identify the same clip. The separate Properties window keeps its pinned identity.

Play/pause, the loop-time seeker, LOD and Frame use the shared renderer. Space works from Assets or passive preview content. Blender-style navigation, scene inspection and the tree apply to the selected assembly. Other library members are excluded from enumeration, selection, Properties and node framing; source node indices remain unchanged when switching assemblies. Playback interpolates translations and normalized quaternions through the hierarchy; aiming, gait correction and inverse kinematics are not simulated.

Open **Properties** for a motion member to edit loop duration or a part/frame's translation and WXYZ quaternion. Indices are zero-based. Insertion/deletion changes every track together while retaining loop duration. The separate closing sample is preserved exactly through edits to frame zero and insertion/deletion; it may intentionally differ from frame zero. It remains readable through motion records and is outside the editable frame range. Draft validation, pinned identity, undo/redo, external-change checks and verified Save As are shared with the archive editor.

## Compiled animations

Version-39 entries use the existing sequence/event editor, Properties, transport, seek, audio preparation, camera preview, export and verified Save As. Catalogs are version-specific. Larger light/procedural records, water/lava collision fields, keyframe blocks, weapon events and particle records retain serialized sizes and opaque data. No-op writes preserve original bytes and record order.

Opening and passive inspection retain bounded metadata, without expanding full payloads or opaque tails into hex. Counts and truncation flags identify omitted data; individual records remain available through the editor and paged MCP commands. Explicit JSON exports include the complete data. The same protection applies to Recoil version 28.

Version-39 transform events require the stored keyframe count to describe the entire payload exactly. Negative/excessive counts, missing records and trailing bytes or records make the keyframe stream unavailable for editing or playback. Inspection exposes a diagnostic and preserves the original bytes; scheduling fields remain inspectable/editable. Version-28's reserved field is not treated as a count.

Both versions validate channel flags, finite nonnegative ordered times and finite active base/rate operands while preserving unused vector padding and extended channel bytes. Some shipped MW3 streams contain reversed time spans: their runtime semantics are not established here, so they retain source bytes and expose a preview diagnostic rather than being normalized.

Keyframe inspection validates the stream with a sparse offset index and displays 64 segments per page. Preview execution accepts up to 16,384 segments per transform event; larger streams remain inspectable/editable/exportable and report a preview limitation.

Supported shared animation operations have explicit approximation notices. Stored spline coefficients are preserved; preview sampling uses base/rate channels. Weapon gameplay and particle simulation are not executed. Version-39 gameplay markers 41/42 retain their dispatch traces and support notices, then allow subsequent events to run in both ordinary and cleanup sequences. Water/lava contact behavior, AI activation and mission-script execution are not reproduced. Recoil engine evidence does not establish MW3 runtime compatibility. Bind version-39 animations to a version-27 world; cross-game bindings are rejected. Ambiguous MW3 roots require an explicit root binding.

## MCP

GUI and MCP share the visible workspace, parser, identities, edit sessions, previews and saves.

For MW3 actors, `scene_nodes` returns a bounded `Name` on the node and its `actor`, with `actor.NameCharacters`/`NameTruncated`. Node metadata also records the original name length, subject to the inspection budget. `scene_properties` and pinned Properties use bounded node metadata; the object card discloses truncation and authored length. Scene queries match the published 128-character prefix. Use the archive/member/record identity to distinguish equal labels, and inspect or export the source AIV resource for the complete name.

Motion state responses (`motion_preview` and `preview_state.motion`) show at most 32 diagnostics with 512-character prefixes plus `diagnosticCount`/`diagnosticsTruncated`. The GUI support notice also discloses shortened output; explicit JSON export retains complete authored part names. State previews at most 32 assembly choices with `assemblyCount`/`assembliesTruncated`; `motion_preview action: "assemblies"` pages all choices using `offset`, `limit` and a case-insensitive name `query`.

Motion inspection metadata previews at most 32 parts and 128 characters per name, with full counts and explicit truncation flags. Use `zstudio_motion_records` to page all parts and frame samples; part names use 512-character previews with `nameCharacters`/`nameTruncated`, while queries match full authored names. Explicit JSON exports retain the full part list and names. Model rows from `zstudio_mech_models` preview at most 32 node references and material indices, with `nodeCount`/`nodesTruncated` and `materialCount`/`materialsTruncated`. For the complete lists, supply `section: "nodes"` or `"materials"` and the member's `localModel`, then use `offset`/`limit`. Nodes require `localModel`; materials without it lists all shared materials. Queries match node names, `Model N` or `Material N` labels before pagination.

| Command | Purpose |
| --- | --- |
| `zstudio_ai_valves` | Page edited valve records, references and eligible binding targets by document/member UUID. |
| `zstudio_ai_valve_edit` | Apply one shared semantic resource edit with a current revision. |
| `zstudio_ai_valve_selection` | Discover mission sources, inspect source occurrences, open pinned Properties, toggle/highlight/frame the overlay. |
| `zstudio_missions` | List/select authored readers for the visible MW3 world or animation. |
| `zstudio_motion_records` | Page motion parts or frames, including the closing sample. |
| `zstudio_motion_edit` | Edit timing/transforms or insert/delete frames as one archive undo step. |
| `zstudio_motion_preview` | Control visible motion playback, seek, assembly, LOD and framing. |
| `zstudio_mech_models` | Page member-local models, shared materials and node references. |
| `zstudio_mech_model_replace` | Replace one explicit member-local mesh. |

`zstudio_motion_preview` state distinguishes actual `playing`, `loading` and `playbackRequested`, so a temporarily suspended assembly load does not look like a new pause request. `zstudio_resource_properties` accepts motion `part`/`frame` targets and generated actions. `zstudio_event_catalog` accepts `version: 28` or `39`. `zstudio_ai_nodes` exposes edge constraints with `section: "constraints"`; a record may contain multiple ordered attributes, identified by its record index and `AttributeIndex`. Existing resource, camera, inspection, export, texture and save commands apply. Revision and draft guards remain mandatory for mutations. Discovery remains windowless and access remains opt-in.

## Validation

Whole world coordinate editing reuses typed AIV/network resources and one archive baseline, with indexed member-overlap checks. Editing diagnostics retain 256 messages plus an omitted-count summary. AI nodes and mission actors reject positions outside ±1e12 before preview/history; unsupported values cannot be saved through their transform controls. Valve-source attachment indexes archive/member identities, and queries match full names without constructing combined search strings. Animation sound aliases use the existing typed resources; MW3 animation setup does not expand unused effect definitions.

Set `ZSTUDIO_MW3_CORPUS` to a base-game `zbd` folder for optional corpus checks with `dotnet test --solution zStudio.slnx`. They cover no-op preservation, malformed input, motion history, mission isolation, hierarchy sampling and member-local replacement. The portable live check is `dotnet run --project tools/zStudio.PreviewCheck -c Release -- --mcp-stdio artifacts/zStudio-win-x64/zStudio.exe <zbd-root>`; it requires existing user-enabled MCP access. Assets and reports are not checked in. Parsing, readback and preview checks do not establish original-game compatibility.

The renderer's buffer-size regression is `dotnet run --project tools/zStudio.PreviewCheck -c Release -- --viewport-size <zbd-root>`. It checks first-load and resized GPU surfaces under a busy render queue, including display DPI and camera retention.
