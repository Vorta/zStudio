# AI networks in Whole world

The viewport overlay draws at most 16,384 nodes and 32,768 links; `preview_state.ai` reports `renderedNodes`, `renderedLinks` and `renderTruncated`, and the network picker tooltip and status bar disclose a limited overlay. Only drawn markers are pickable, while the complete graph remains available through AI Properties and `ai_nodes`. MW3 link visualization and node inspection preview the first 32 authored slots per node. `link_count` and `links_truncated` disclose the full count and omitted slots; the selection card and Problems also report the limit. Slot order and negative/unresolved targets are retained within the preview. Complete authored lists remain available through the ZRD editor, paged `zrd_nodes`, or resource export; visualization never changes those source records. Network/preview state distinguishes resolved preview `links` from authored `linkSlots` and reports `linksTruncated`.

AI node queries match the first 70 characters of the network member/name or `node_NN`; network queries also match the type prefix or source archive path. Filtering precedes pagination and is skipped for an empty query. Node inspection caps displayed network name/type at 256 characters and exposes their original character counts and truncation flags.

In a source map, the World tree's **AI Networks** row shows them: each network is shown or hidden on its own with its eye (all start hidden), the row's master eye shows or hides them all, and they are always drawn through geometry (at half strength where it hides them); selecting a network shows it emphasised and opens its card, which edits the header settings in its `net_NN.zrd` ([source map editor](scene-inspection.md#source-map-editor); MCP `source_ai_network`). Editing compiled ZBD assets is being retired in favour of [source projects](source-project.md); a compiled root opens only through MCP `open_root` (the GUI opens only source projects). There, open a mission's `gamez.zbd`, select **Whole world**, and turn on **AI nodes** in the viewport toolbar; at narrow widths the AI button, network picker and **Show AI through geometry** button move together into its native overflow menu.

- Visualization starts off. When enabled, it initially shows all networks through geometry. Disable **Show AI through geometry** for normal depth occlusion.
- Choose **All networks** or one resource. Entries include the member name, authored name, node count and archive record number. Identical names do not merge networks.
- Diamonds mark nodes; arrows show outgoing connections. Reciprocal edges have arrows in both directions. Nodes and arrows share a fixed attack-strategy color; networks with the same strategy share the same color. The selected marker is white. Marker and connection sizes follow the view to remain compact when zooming.
- Hover or select a marker to see its label. Click selects without opening or retargeting Properties. Right-click and choose **Properties**, or use **Alt+Enter** after selection.
- Properties shows archive/member provenance, node number and resource offset, authored XYZ, raw node integer, stored network type and attack strategy, path width and ordered link slots (three for RECOIL; variable for MW3). Negative targets are unused slots. Unresolved targets retain their indices and diagnostics.
- **Frame selected** (Numpad decimal; in a source map also the view's **Perspective** chip) frames the selected node and its outgoing neighbors. Ordinary Frame/Frame all still frame scene geometry.

AI visualization coexists with soil, CanModify and ClipTo highlights. It does not change source data, document revisions or undo history. Navigation remains Blender-style. Pickup handles take priority; AI picking is inactive during a pickup drag or captured Fly navigation.

Selecting a marker also opens its [inspection card](scene-inspection.md) (floating over a compiled world, the docked Properties pane in a source map). In a compiled world, once **Unlock editing** is on, the card's XYZ fields take input at once; **Apply** or Enter accepts one undoable move, while **Revert** or Escape discards it. The map's Save command verifies a coordinate-only patch to the owning archive. A source map's card edits the node's XYZ at once and accepts it with **Apply** (see [Placements](source-project.md#placements)). Links, raw node values and other metadata remain unchanged. The separate Properties window retains read-only snapshot inspection.

The card's read-only **Attack strategy** field appears with the scrolling information below **This AI network node**, immediately above **Status**. It uses the same field style and copy button as the other details, is included once when `scene_card` copies all details, and remains read-only during transform editing. Strategy is stored once per network, not per node. Edit the authored string through the existing ZRD Data/Properties editor when needed; accepted resource edits and Undo refresh the preview through the shared workspace snapshot.

| Stored strategy prefix | Nodes and arrows |
| --- | --- |
| `HEA` | Orange `#FFA640` |
| `CIR` | Cyan `#33D9FF` |
| `BAC` | Purple `#B380FF` |
| `FOL` | Green `#4DFFA6` |
| `ZIG` | Pink `#FF66B3` |
| `SIT` | Yellow `#F2F24D` |
| Missing (`attack_strategy` is absent) | Red `#FF6666` |
| Empty, unrecognized or invalid | Gray `#A0A0A0` |

In the compiled toolbar, the **AI nodes** tooltip includes this key. Colors do not depend on network identity, order, filtering, mission or difficulty. Source names and the network picker distinguish networks sharing a color. The exact stored text is preserved, including unfamiliar values and capitalization; an empty stored string is shown as `"" (empty)`. Missing metadata shows **Not stored** and uses red nodes and connections. Empty, unrecognized, malformed and duplicate strategy fields remain gray; malformed/duplicate fields show **Unavailable (invalid data)** and source-specific diagnostics without hiding valid nodes or links. No gameplay default is inferred for red or gray networks.

Visibility and depth choices last for the application session. Filtering and selection survive LOD, difficulty, texture and horizon refreshes when the graph is unchanged. Changed resource graphs clear the affected selection/filter. Properties retains its original snapshot and is marked stale when its source changes; reopen the node to inspect current data. It is owned by the map document and closes with that document.

## Edit a whole path

In a compiled Whole world, unlock editing, select a node or choose one network, then click **Edit path** (or press **Tab** with the viewport focused). A source map has no lock, and its GUI has no **Edit path** button or Tab shortcut: edit its AI paths through MCP `zstudio_ai_path`, whose Edit mode shows the same panel, clicks and keys described here; leave it with `exit`, or with Escape once no draft is pending (Escape first cancels a draft). Click nodes to replace the selection, Shift-click to add, Ctrl-click to toggle, or drag a rectangle from empty space. Box selection obeys the same visibility and depth setting as clicks. Only this network participates; record identities, not names, identify its nodes.

Use **G**, **R** or **S** to move, rotate or scale the selection with the pointer. **X**, **Y** and **Z** constrain the transform to a world axis. Rotation and scaling use the selection centre; unconstrained rotation uses world Y. Enter or left-click confirms one undo step; Escape or right-click cancels. The panel also offers numeric transforms and explicit **Confirm**/**Cancel**, without pointer input. Move fields are XYZ deltas, scale fields are XYZ factors, and rotation uses the first field as degrees. A constrained transform leaves other axes unchanged. Text fields and native controls retain their own shortcuts.

The preview is a draft until confirmed. Navigation, save, undo and other edits require explicit draft resolution. Links, node integers, record order and all non-coordinate fields remain intact. Compiled worlds use the existing verified owning-archive save; protected retail sources still require Save As. Source worlds change only the relevant text tokens through the shared project workspace and rebuild after confirmation. The rebuilt source world exits Edit mode explicitly; select its current path to continue. Edit mode and drafts are not saved preferences.

**Properties** pins the selected identities. One node keeps its full inspection; multiple nodes show their shared fields, with **Mixed** for differing values. Later selection does not retarget that window. The first 256 IDs are shown with counts and an omission flag; the transform retains the complete selection, up to 16,384 nodes.

## Interpretation and limits

The reader uses shared ZAR/ZRD services and current workspace resource snapshots, including unsaved edits. It discovers `net_NN.zrd` members in the mission resource search paths and preserves every source record independently. This is an authored-resource visualization, not a report of active AI agents or runtime lookup precedence.

RECOIL retail `AINet::LoadFromZrd` at `0x403040` verifies optional version 105, canonical keys `node_00` through `node_98`, and each node's integer, float XYZ and three integer neighbor indices. `0x403550` resolves nonnegative indices within that network; negative indices mean absent links. These routines were inspected read-only. The raw node integer's meaning is not asserted. No reciprocal connections, terrain snapping, activation, pathfinding or difficulty-dependent runtime behavior are invented.

MW3 base-game version-106 networks use variable link counts and may also contain edge-constraint records with multiple ordered attributes. Constraints have no authored XYZ and are inspectable as metadata rather than invented spatial nodes. Only the selected mission reader contributes MW3 networks; see [MechWarrior 3](mechwarrior3.md).

In the game, a network whose `attack_strategy` is missing or unrecognized attacks head on (`HEA`): the loader leaves the zeroed default in place ([Engine evidence](engine-evidence.md#resource-schemas)). The colors above still distinguish those cases from a stored `HEA`, because they describe the authored data. Attack-strategy classification follows the retail loader's uppercase/three-character comparisons at `0x403367–0x40342E`. The six string literals at `0x4DA0F8–0x4DA10C` and `attack_strategy` key at `0x4DA110` were checked against the original binary and read-only reconstruction. Stored strings are not trimmed or rewritten. This classifies authored metadata; it does not report a unit's live combat state.

Malformed nodes and unsupported versions produce source-specific preview diagnostics. Valid parts remain visible; missing or ambiguous endpoints do not produce guessed connections. Self-links use a small loop. Positions outside the finite ±1e12 preview range cannot render. Large text fields are truncated in bounded inspection results, with stored lengths retained.

A node with an incorrect ZRD value type is diagnosed individually. It does not hide valid neighboring nodes, and a malformed duplicate still makes links to that numeric index ambiguous.

Normal depth mode uses the existing opaque depth buffer. Transparent surfaces retain their no-depth-write behavior. AI geometry does not participate in ordinary framing, navigation clearance, scene clipping bounds, isolation or exports. Missions without supported nodes show an explicit empty message.

## MCP

Read `zstudio_preview_state` with the current preview ID. Its `ai` object contains `snapshot`, `visible`, `throughGeometry`, `network`, `selectedNode` and counts; it is null outside Whole world.

`zstudio_scene_options.changes` accepts:

| Option | Value |
| --- | --- |
| `aiNodes` | Boolean visibility |
| `aiThroughGeometry` | Boolean; true ignores geometry occlusion |
| `aiNetwork` | `all` or a discovered network ID |
| `aiSnapshot` | Expected graph snapshot; required with a specific network ID |

The whole option batch is validated before changing controls. AI settings publish after requested scene refreshes succeed. Newer GUI choices or changed graphs reject superseded requests.

- `zstudio_ai_networks`: paginated discovery with `preview`, optional `offset`, `limit` and `query`. Rows include snapshot and source identity.
- `zstudio_ai_nodes`: paginated inspection with `preview`, `snapshot`, optional `network` (`all` by default), `offset`, `limit` and `query`.
- `zstudio_ai_selection`: `preview`, `snapshot`, `action` (`select`, `clear`, `properties`) and a node ID except for clear. Selecting requires unlocked editing in a compiled Whole world (source maps have no lock), visible AI and a matching filter; locked selection returns `locked` without retaining a hidden marker. Read-only Properties remains available while locked and shares GUI draft guards.
- `zstudio_camera` with `action: frame`, `target: selected` frames the selected AI node without changing source selection.
- `zstudio_scene_inspect` and `zstudio_scene_card` share the inspection card, coordinate copy and XYZ draft workflow. Read the current revision and draft token before confirming edits; accepted movement changes the graph snapshot.

Pages have at most 200 records. Handles are snapshot-scoped; rediscover after stale-snapshot/record/preview errors. Discovery never enables visualization or captures input.

Network rows, node rows, node selection and pinned Properties include `attack_strategy`: `{ value, status, key, characters, truncated, color }`. `status` is `stored`, `missing` or `invalid`; `key` is one of the six prefixes or `unknown`; `color` is the network's `#RRGGBB` color (the selected white marker does not change it). Absent/invalid values and character counts are null. Values retain at most 1024 original characters plus a truncation marker. `scene_inspect.attackStrategy` and `hoverAttackStrategy` return the same metadata for the queried/selected and hovered AI nodes, or null for non-AI targets. The readable card row uses the existing 2048-character presentation bound. Network results preview 8 diagnostic messages of 128 characters each, retaining severity/source offsets and reporting `diagnosticCount`/`diagnosticsTruncated`. These limits also keep a full 200-row page bounded when authored strings require JSON escaping. Existing input schemas and tool names are unchanged.

`zstudio_ai_path` returns a cancellable operation handle (poll `zstudio_operation`) and provides enter/exit, replace/add/toggle selection, viewport rectangle selection, begin/set/confirm/cancel transforms and pinned Properties. Every request supplies the preview, graph snapshot, document and revision; draft changes also supply its token. `preview_state.ai.path` reports mode, selected IDs/count/omission, numeric input, pivot and validation errors. Source confirmation returns the replacement document. It never captures physical input or writes game files directly.

## Verification

Core tests cover ordered directed links, negative slots, self-links, duplicate records/indices, missing targets, malformed data, versions, cancellation, stable identities and unsaved edits/undo. `ZSTUDIO_CORPUS` enables read-only corpus checks: each reference root contains 470 networks, 3,717 nodes and 6,240 resolved directed link slots across six network-bearing mission archives.

Named-pipe tests exercise GUI/MCP state agreement, pagination, filtering, stale handles, pinned inspection, framing and invalid-batch atomicity. Run `dotnet run --project tools/zStudio.PreviewCheck -c Release -- --ai-networks <root>` for GPU depth/picking checks, presented-back-buffer idle stability, mission integration and native overflow. Temporary captures stay outside the repository.
