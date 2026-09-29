# Changelog

Version numbers follow `MAJOR.MINOR.PATCH`; the 0.x series is under active development. Release tags and downloadable archives use the same version.

## 0.7.1 — 2026-09-29

- Add MechWarrior 3 base-game support through the shared format library: version-27 worlds and mech hierarchies, version-4 motion clips, and version-39 compiled animations. Preserve version-specific layouts, record identities and opaque data, with byte-exact animation/motion no-op writes.
- Preview authored MW3 mission layouts with an explicit mission reader picker, independent actor instances, AI networks and mission-scoped coordinate editing. Retain camera, playhead, draft guards and edit history while switching missions. Keep non-spatial AI edge constraints as ordered metadata.
- Export mech assemblies as assembled and local OBJ/MTL/PNG bundles, including vertex colors and member/local mesh identities. Replace explicit member-local meshes or supported version-27 world models through shared undo and verified saves, preserving unrelated members and hierarchy bounds.
- Play and seek motion clips against explicit mech assemblies; edit loop timing, frame translations and WXYZ quaternions through pinned Properties. Insert/delete frames across all tracks with shared archive history. Retain the stored pose for ambiguous motion bindings, including duplicate tracks.
- Edit version-39 animation sequences, events and keyframes with a versioned catalog, retaining spline bytes and water/lava sequence references. Document approximate preview behavior and reject cross-game world bindings; mission scripts, combat, particles, spline interpolation and gait/IK are not simulated.
- Show authored attack strategy in AI node inspection, Properties and copy actions. Keep the card field among its scrolling details immediately above Status. Share a fixed network color palette between nodes, connections, the tooltip and MCP: absent attack_strategy is red, while empty, unrecognized and invalid values remain grey. Selected markers stay white.
- Add six typed MCP tools for mission selection, motion inspection/editing/playback and mech inspection/replacement. Preserve shared GUI identities, revision/draft checks, asynchronous operations and verified saves.
- Multiply authored vertex RGB by textures while preserving alpha depth behavior. Correct initial and resized render-buffer dimensions under a busy render queue, and keep retained-world rendering from starving preview replacement.
- Integrate authored MW3 AI valves: ordered action blocks, compound conditions, node conditions/unions, edge assignments and objective references. Preserve repeated names as distinct source occurrences. Add typed Properties, paged inspection and reference navigation, atomic edits, undo/redo and verified resource saves.
- Add optional valve outlines and dashed assignment edges in Whole world while preserving attack-strategy colors. Node cards open their valve source in Properties; exact-name highlighting and framing use resolved spatial associations. Runtime valve evaluation and mission-script execution remain outside preview support.
- Add three typed MCP tools for valve records/targets/references, semantic edits and mission selection/visualization, bringing windowless discovery to 82 tools.
- Bound AI constraint previews and mission diagnostics before retaining them, reject placement coordinates beyond the supported preview range, and cache motion hierarchy bindings with frame sampling off the UI thread.
- Inspect large animation keyframe streams through sparse indices and paged fields; serialize edits without eagerly cloning every untouched frame. Keep explicit preview limits visible. Preserve unrelated sequence references when duplicating cleanup.
- Keep RECOIL keyframe streams with authored reversed spans readable, editable and playable using the retail sample cursor; MW3 streams with reversed or negative spans stay editable with an explicit transform-preview diagnostic. MW3 animation node references with ambiguous names stay unresolved during playback.
- Choose valve Properties from authored valve structure, so RECOIL `objectives.zrd` keeps the generic member editor; bound the highlighted valve name in MCP state; keep the highlight when MCP toggles the overlay; recheck revisions after valve forms load.
- Reject difficulty changes for MW3 previews before touching the shared RECOIL preference, and reject automation edits to other documents while the shown preview holds a scene-card draft instead of prompting.
- Keep accepted edits and saves successful when a dependent motion library becomes unavailable; clear motion bindings unless the member identity is proven, and reconcile the displayed motion by member UUID rather than the Assets selection.
- Follow authored MW3 conventions for new version-27 material flags and polygon priority/field24/zone words during mesh replacement; RECOIL version-15 output is unchanged.
- Bound per-record metadata before it is materialized: at most 65,536 entries per archive, texture/script directory and GameZ table, and 262,144 GameZ polygon/light records per file, with cancellation while reading table headers.
- Bound GameZ world partition cells and all node index references (relations, light/sound lists, partition nodes) per file, and keep one diagnostic budget across every AI network member of a snapshot.
- Rebuild a Whole world whose remembered mission never published when that mission is selected again; keep a user's newer mech assembly choice when a delayed library refresh finishes; search mech models by node names and materials by texture names; classify valve Properties off the UI thread.
- Never offer a partially parsed mission reader, report damaged mission resources, and make explicit MW3 animation mission switches fail rather than fall back when the requested reader disappears.
- PR-watch tooling: retry state replacement while another process reads it, report an active watch without a worker, ignore review requests and review-bot status posts when arming feedback notices, honor rate-limit resets only when exhausted, require explicit release authorization on every arm, accept approval only for the newest review summary of the current head, and add a Claude Code channel whose listener prints one notification per settled comment burst.

## 0.6.3 — 2026-09-28

- Resize the top-right inspection panel from its bottom grip or keyboard, with a saved shared height, a normal minimum of 216 DIP and a maximum of 80% of the viewport. Keep fixed readout/action headers and scrollable details; hide Rotate for placements without rotation. Share height control/readback through MCP, preserving transform drafts and newer layout choices when an older resize gesture ends.
- Anchor inspection at the top right with a permanent live hover readout and selected-object details expanding below it. Clear hit coordinates to placeholders over empty space, preserve independent selection/drafts and keep the view cube clear. Hide Move/Rotate icons until Edit while retaining their layout space; show all supported pickup XYZ axes and vehicle Y heading.
- Gate Whole world cards and bounds with **Unlock editing**. Show whole-instance selection bounds before Edit; enable Move/Rotate handles only in the card's shared draft. Add pickup XYZ rotation and AIV vehicle Y heading in degrees, preserving native units and untouched bytes. Typed transforms and handle drags preview until one combined confirmation, with drag-only Escape and full-draft Cancel. Extend the shared MCP draft schema and verified archive saves to rotation.
- Keep out-of-range coordinate errors inline, require coordinate provenance when remapping vehicles without a verified counterpart scope, and reject cross-family model snapshots before acceptance. Cover mixed model/placement edits, publication, undo/redo and saves in both edit orders.
- Keep inspection-card Edit/Confirm, Copy all and Close in a fixed header. Enable persistent authored XYZ fields in place, retain read-only world origins, and reserve a separate scrollbar gutter so it cannot overlap copy buttons.

- Keep wheel scrolling and native controls inside floating inspection cards separate from camera navigation, including at scroll limits.
- Rename the map lock to **Unlock editing** and share it across pickup, AI-node and supported AIV tank coordinates, preserving draft guards and the existing MCP command/state identities.
- Add an expandable Document scene hierarchy for active previews, including mission placements, shared/partition references and unlinked nodes. Synchronize viewport selection, preserve expansion by provenance, retain stable animation scene structure, and expose the same hierarchy through `zstudio_scene_tree`.
- Add shared 3D hover information and floating node cards to Whole world, model and animation previews. Show exact surface coordinates separately from object origins, source node/model/material identities, soil and region flags, AI links and mission metadata, with full-precision copy actions.
- Add explicit XYZ editing for mission pickups, AI navigation nodes and supported AIV tanks. Preserve source identities, linked unique difficulty counterparts, pending drafts, chronological undo and verified coordinate-only archive saves.
- Expose inspection, card selection/copy, coordinate drafts and the node hierarchy through three shared MCP tools, with typed validation, revision/draft checks and windowless discovery for all 73 tools.
- Retain all requested destinations after partial coordinate Save As. Ordinary Save retries unpublished copies as new files, keeping source archives and competing files protected.
- Keep ambiguous or shadowed tank records read-only and match difficulty counterparts by original placement and template provenance.
- Frame the selected animation copy, expire inspection targets when runtime slots change source/model, and respect later source-tree selections. Update camera-facing sprite hit transforms before immediate camera queries.
- Restore source framing and inspection after compatible hierarchy refreshes, clear stale tree selection when closing a card, and keep pickup-child isolation consistent between the toolbar and MCP.
- Give reopened XYZ drafts new conflict tokens, reject scene-selection changes during Fly, and keep geometry shared by multiple mission actors read-only instead of choosing an arbitrary editable instance.
- Clear unrelated rendered cards when selecting nodes without geometry, and open Properties for the current source after viewport selection while retaining independently pinned windows until explicitly retargeted.

## 0.6.1 — 2026-09-27

- Base perspective zoom speed on the scene surface under the pointer while moving toward the pointer. Refresh that distance during zoom, retain the last speed over empty space, and continue past the old target without needing a small pan/orbit to restore speed. Wheel, Ctrl + middle drag, numpad and MCP share the behavior and smooth inertia, independently of clipping planes.
- Anchor orthographic zoom at the pointer, including axis views, wheel/drag/keyboard zoom and inertia.
- Preserve rendered viewing direction with short look vectors on large maps, keeping pointer zoom stable after passing the old target.
- Switch middle-button gestures as Shift/Ctrl are pressed or released, regardless of input order, without jumps or stale inertia. Defer orbit picking until movement begins so a middle-first pan keeps the same scale.
- Orbit around the visible scene surface beneath the initial middle-button press without recentering the camera. Retain that world-space point through dragging, inertia and preview refreshes; empty space keeps the previous pivot. Exclude horizon, grid, AI overlays and editing handles.
- Expose surface-pivot picking, pointer-directed zoom and orthographic anchoring through the optional `zstudio_camera` rotate/zoom `screenPoint` parameter, with pivot and zoom-reference readback, typed validation and shared GUI/MCP behavior.

## 0.6.0 — 2026-09-27

- Add direct PNG replacement and addition in texture packs, with explicit sibling variants, original dimensions/storage, alpha-safe resampling and private palette quantization. Keep batch undo, ownership, mirrored views and verified saves consistent across GUI and MCP.
- Edit prepared v7 script entries, ordered instructions and arguments in a virtualized Instructions tab and pinned Properties. Preserve stable IDs, unknown commands, raw timestamps and untouched storage; retain read-only text reconstruction and source bytes.
- Visualize authored AI networks in Whole world with colored node markers, directed connections, source-qualified filters, optional geometry occlusion, pinned read-only Properties and Frame selected. Preserve selection through unchanged-graph preview refreshes and keep overlays outside scene framing, clipping bounds and exports.
- Add ten shared texture/script/AI MCP tools, typed input bounds and asynchronous generated argument actions, bringing discovery to 70 tools. Extend drafts/history/save integration, snapshot-scoped AI identities, per-file partial-save reporting and protocol/corpus regressions.
- Correct inline palette detection to use the retail external-palette flag, and share the texture encoder/writer with existing model import.
- Retain every requested destination after a partial content Save As; ordinary Save retries unpublished copies as new files without falling back to source paths or overwriting competing files. Keep pending copies dirty and reject directory or conflicting source-identity destinations before publication.
- Exclude current Save As aliases from independent texture variant targets, and diagnose malformed AI nodes individually while preserving valid neighbors and ambiguous-link safeguards.
- Refresh only the displayed static scene after content or background resource edits, so switching from a 3D preview into a script or texture editor cannot fail an accepted edit by refreshing the retained, cleared viewport.
- Read edited and mirrored texture palettes from the current snapshot in MCP, matching GUI inspection after replacement, addition, undo/redo and Save As.
- Isolate malformed script records with name/index/offset diagnostics, retaining valid records for inspection and export while keeping the damaged pack read-only.

## 0.5.4 — 2026-09-27

- Export an animation root or GameZ node's referenced models as an assembled OBJ/MTL/PNG bundle, individual local-space meshes and an identity/transform manifest for remodeling in Blender.
- Replace explicit GameZ v15 models from bounded OBJ/PNG batches, with shared undo/redo, edited previews, texture-variant updates and verified saves. Preserve node transforms, unrelated records and material-pool links; rebuild solid-model bounds and expand ancestor bounds using retail rules.
- Add general ZAR member import, replacement, rename, duplication, deletion and reordering, including creation of empty ZRD resources.
- Edit standalone and embedded ZRD values, raw float bits, types and ordered array structure through the Data tree and pinned Properties window. Preserve stable record identities, untouched bytes and unknown data.
- Share resource edits across inspection, export and dependent previews, with asynchronous property drafts, document undo/redo, Save As retargeting, external-change checks and verified atomic archive saves. Prevent conflicting pickup and direct-resource writers from owning the same archive.
- Add eight shared MCP tools for model bundles/replacement and archive/ZRD editing, bringing the discovery catalog to 60 tools. Extend typed schemas, GUI parity mappings and real named-pipe regression coverage.
- Bound large ZRD string formatting before JSON escaping in node listings and Data tree labels, and follow only current model/texture destinations when checking external changes after Save As.
- Guard shared model/texture ownership before edits and saves, enforce output size limits before allocation, and retain typed ZRD editing across member renames and reopening. Keep resolver publication atomic, bound large Properties/inspection and paged results, and reload current Save As destinations.
- Validate standalone ZRD before enabling edits, share decoding for aliased archive payloads, and keep edited model records synchronized across Assets, inspection, Document scene and pinned Properties. Preserve original byte ranges separately; expose new records through GUI/MCP and restore them correctly through undo/redo.
- Document supported import/editing formats and limits, and keep the latest validated portable build in `artifacts/zStudio-win-x64` for local use.

## 0.5.2 — 2026-09-26

- Add Blender-style navigation to model, Whole world and animation previews: middle-mouse orbit, Shift+middle pan, Ctrl+middle centered zoom, Ctrl+Shift+middle dolly, and numpad view/projection/framing shortcuts with retained smooth inertia.
- Add exact orthographic axis views, a game-axis-aligned view cube and View > 3D Navigation actions. Retain explicitly selected orthographic projection while orbiting, preserve camera state through preview refreshes, and exit animation Follow camera on manual navigation.
- Keep the horizon and camera-facing animation effects synchronized during pan/zoom/dolly inertia; update built-in Help and guides to the new controls.
- Highlight Whole world non-default soils in yellow, CanModify regions in green or ClipTo regions in red, preserving texture transparency and source data.
- Validate complete MCP scene-option batches before applying changes, and preserve the current highlight on failed/canceled refreshes or newer GUI input.
- Place Files in its own resizable column to the left of Assets, Search and Document scene when the window is wide enough; retain the Files tab at narrower widths and preserve navigation, focus and saved layout preferences.
- Restore native Windows 11 rounded window corners while retaining standard maximized/Snap behavior.
- Expose navigation, highlighting and responsive layout through shared MCP commands, typed schemas, discovery metadata and GUI/protocol regression checks.

## 0.5.0 — 2026-09-26

- Cancel retained preview lifetimes before draining MCP on accepted application close, while preserving canceled-close and MCP-only recovery behavior; serialize overlapping MCP teardown.
- Restrict isolation to GUI-supported model/Whole world previews, isolate complete pickups when child meshes are selected, and protect active pickup drags from MCP scene/camera changes.

- Retain the current document and preview until a reload replacement is parsed and accepted; reject canceled, malformed, superseded or newly edited/drafted replacements.
- Tie inspection to document lifetime and revision, freeze edited snapshots, and keep original animation data separate from working edits across Undo/Redo.
- Revalidate asynchronous navigation and draft publication, exclude MCP mutations during saves/modal decisions, apply approved preview settings as a batch, and suppress obsolete pickup/export/validation results.

- Verify Properties publication and preserve input entered during decoding; propagate request cancellation through queued UI work and large property/JSON expansions.
- Define pickup identity and destination-map schemas, enforce integer bounds, report failed/superseded document reloads explicitly, and preserve new edits/input during a deferred MCP close.
- Recover animation scene bindings and late renderer refreshes after MCP cancellation, retaining usable playback and reporting unavailable or superseded work. Keep LOD/map geometry consistent across concurrent changes, apply the latest Map/Horizon choices and preserve playback or explicit Pause through refreshes.

- Recover the still-selected preview after MCP cancels asset/document loading, and reject superseded scene-option jobs instead of returning an unpublished preview identity.

- Describe and validate MCP array items recursively, including three-number camera vectors and export asset identities; reject malformed input before workspace connection.
- Retain loaded model/Whole world scenes during LOD, horizon, texture-pack and difficulty refreshes; publish only completed replacements and restore option controls after cancellation or load failure.
- Recover animation controls after canceled level refreshes, cancel pending audio retries when MCP stops, and retain MCP inspection/editing of valid fields on malformed keyframe events with a read-only payload diagnostic.
- Guard preview captures with their lifetime identity, clamp explicit camera poses before movement, and propagate MCP shutdown cancellation through pending workspace/preview/save work without canceling retained preview lifetimes.
- Correct active-view MCP state after viewer changes, apply list queries before paging, reject invalid layout batches before changing preferences, report stale structural targets explicitly, and preserve 3D capture proportions. Add packaged stdio corpus regressions.

- Add opt-in local MCP access to the visible zStudio workspace through a stdio connector and same-user named pipe, including on-demand visible startup (handshake and discovery stay windowless), connection controls and activity history.
- Expose browsing/inspection, animation properties and structure, preview and camera controls, texture/audio inspection, pickup editing, undo/redo, verified saves, exports, diagnostics and captures. Retain document revisions, explicit draft/unsaved decisions and asynchronous operation tracking.
- Require MCP parity for future GUI features; add an independent action inventory, schema/protocol tests and real-file/portable-connector verification tools.

## 0.4.8 — 2026-09-25

- Replace model/Whole world Fly camera with captured freecam: WASD movement, Space/C vertical movement, mouse-look, wheel speed adjustment and Escape to release controls. Movement is time-based and independent of surface distance; diagonal input is normalized and the camera stays upright.
- Release freecam input on focus/capture loss, hidden or replaced previews and shutdown. Show current speed and controls in the viewer, support toolbar overflow, and retain normal orbit/pickup controls after exit. Animation navigation is unchanged.

## 0.4.7 — 2026-09-24

- Redesign the Windows 11 Fluent workspace with resizable navigation, preview, Inspector and bottom tool panes, saved layouts, workspace presets, and Compact/Comfortable density.
- Keep a single title/menu row with full-height window controls and centered document name plus Undo/Redo/Save icons. Preserve native caption dragging, resizing and the maximize/Snap target.
- Hide empty workspace controls before opening a folder. Use Files for open-document navigation, dirty markers and per-file close buttons; place search in Search and show Assets/Document scene only when applicable. Render recent-folder paths literally, including underscores.
- Organize animation tools as Sequences, Settings and References. Open Sequences initially and retain the selected tab when switching animations. Describe event operands, referenced animations and scheduling thresholds in the sequence tree.
- Open Properties in one reusable, resizable window pinned to its owning document and record. Keep icon Undo/Redo beside its breadcrumb, preserve unfinished drafts with inline validation and Escape restoration, and retain document-scoped save/history commands.
- Display numeric property values in plain decimal notation without losing float precision; continue accepting exponent input.
- Consolidate animation and 3D preview options into accessible icon toggles with accent-filled active states and tooltips. Put LOD, difficulty and Frame first; remove duplicate Settings controls and group Grid, Height and independent ground collision together in overflow.
- Support live signed preview Height from −999 to 999 in a compact field, with preserved playhead/playback and partial input during layout changes. Explain collision relative to authored coordinates as Y=−Height.
- Separate observed Dispatch/Event log, structured Problems, current Runtime, Related references and original-source Bytes tools. Retain source identity and distinguish stored data from preview state.
- Fit restored window bounds to desktops smaller than the preferred minimum, avoiding a startup exception on small or scaled displays.
- Keep pinned Properties drafts untouched while browsing the world; resolve them only for actual pickup-handle clicks, and consume rejected handle clicks without starting a drag.
- Navigate Problems to the supplied asset identity even when the error offset is inside its record; use source-range containment for offset-only diagnostics and reveal the selected asset through filters.
- Refresh Whole world preview notices, counts and summary after successful difficulty changes while retaining unrelated operation diagnostics.
- Refresh title-bar and menu Undo/Redo directly from the active document history, including pinned Properties edits while a non-animation asset is being viewed.
- Apply Reset layout to live tab selections and persist the defaults without committing Properties drafts or restarting the preview.
- Fix the reentrant window-close error when discarding changes, and retain save/discard guards for pending property drafts and edited documents.

## 0.3.0 — 2026-09-24

- Introduce the first 3D editing workflow: move mission pickups in Whole world; select an instance, see its bounds, unlock world-axis arrows, or enter exact XYZ coordinates in Properties.
- Enlarge pickup XYZ handles and use generous, DPI-aware screen-space click targets for their shafts and tips. Prioritize handles over scene geometry and show a hand cursor over a draggable target.
- Keep maps locked by default; add one-action drag undo/redo, cancellation, and preserved edits/selection across difficulty and LOD changes.
- Cancel pickup drags when the pointer leaves the viewport, including captured movement or release outside its bounds, restoring the starting position without an undo entry.
- Apply moves to uniquely matching difficulty records while retaining amounts, rotations and respawn metadata. Report ambiguous or unmatched counterparts without changing them.
- Save surgical coordinate patches to the owning ZBD archive, with verified Save As, external-change checks, atomic file replacement and optional backups (off by default). Preserve protected reference datasets.
- Resolve Windows destination aliases before checking protected folders, including substituted drives and new Save As directories; retain edits and report an error if the destination cannot be verified.
- Reject the current save target during pickup Save As; only ordinary Save may replace it.
- Include pickup edits in document dirty-state, close/reload prompts, and the shared Save/Undo/Redo commands.

## 0.2.18 — 2026-09-23

- Publish the open-source repository with contribution guidance, protected main/release tags, and confidential vulnerability reporting.
- Refresh repository documentation and bundled guides for public use.
- Rename the solution to `zStudio.slnx` and all project folders/files to `zStudio.*`; update build, CI, packaging and documentation references while preserving assembly names, namespaces and existing settings.

## 0.2.17 — 2026-09-23

First zStudio repository release. Earlier builds were developed locally as Recoil ZBD Studio; their unavailable binaries are not reconstructed as releases.

- Rebrand the desktop application and portable executable to zStudio, use the updated Studio icon, and retain existing user settings.
- Start a fresh desktop-project history and archive the previous Python CLI separately. Keep the optional independent export checker self-contained.
- Add contributor documentation, bug/feature/compatibility issue forms, PR guidance, dependency updates, Windows CI and version-tagged ZIP releases with SHA-256 checksums.
- Include the complete existing Recoil viewer/editor: five ZBD format families; texture, audio, script and data inspection; model/whole-world previews and exports; version-28 animation editing with undo and verified Save As.
- Retain animation playback/audio, texture cycles, alpha rendering, per-object LOD choices, mission difficulty, initialized turrets and pickups, camera following, ground grid/height, and resizable sidebar sections with the dispatch graphic below the player.

Other Zipper Interactive games and non-animation content editing remain future work. See the README and animation guide for preview approximations and compatibility limits.
