# AI networks in Whole world

Open a mission's `gamez.zbd`, select **Whole world**, and turn on **AI nodes** in the viewport toolbar. At narrow widths, use its native overflow menu. The AI button, network picker and **Show AI through geometry** button move together.

- Visualization starts off. When enabled, it initially shows all networks through geometry. Disable **Show AI through geometry** for normal depth occlusion.
- Choose **All networks** or one resource. Entries include the member name, authored name, node count and archive record number. Identical names do not merge networks.
- Diamonds mark nodes; arrows show outgoing connections. Reciprocal edges have arrows in both directions. Networks have stable colors; the selected marker is white. Marker and connection sizes follow the view to remain compact when zooming.
- Hover or select a marker to see its label. Click selects without opening or retargeting Properties. Right-click and choose **Properties**, or use **Alt+Enter** after selection.
- Properties shows archive/member provenance, node number and resource offset, authored XYZ, raw node integer, stored network type, path width and all three ordered link slots. Negative targets are unused slots. Unresolved targets retain their indices and diagnostics.
- **View → 3D Navigation → Frame selected** (Numpad decimal) frames the selected node and its outgoing neighbors. Ordinary Frame/Frame all still frame scene geometry.

AI visualization coexists with soil, CanModify and ClipTo highlights. It does not change source data, document revisions or undo history. Navigation remains Blender-style. Pickup handles take priority; AI picking is inactive during a pickup drag or captured Fly navigation.

Selecting a marker also opens its [floating inspection card](scene-inspection.md). **Edit** starts an explicit XYZ draft; **✓** or Enter confirms one undoable move, while Cancel/Escape discards it. The map's Save command verifies a coordinate-only patch to the owning archive. Links, raw node values and other metadata remain unchanged. The separate Properties window retains read-only snapshot inspection.

Visibility and depth choices last for the application session. Filtering and selection survive LOD, difficulty, texture and horizon refreshes when the graph is unchanged. Changed resource graphs clear the affected selection/filter. Properties retains its original snapshot and is marked stale when its source changes; reopen the node to inspect current data. It is owned by the map document and closes with that document.

## Interpretation and limits

The reader uses shared ZAR/ZRD services and current workspace resource snapshots, including unsaved edits. It discovers `net_NN.zrd` members in the mission resource search paths and preserves every source record independently. This is an authored-resource visualization, not a report of active AI agents or runtime lookup precedence.

Retail `AINet::LoadFromZrd` at `0x403040` verifies optional version 105, canonical keys `node_00` through `node_98`, and each node's integer, float XYZ and three integer neighbor indices. `0x403550` resolves nonnegative indices within that network; negative indices mean absent links. These routines were inspected read-only. The raw node integer's meaning is not asserted. No reciprocal connections, terrain snapping, activation, pathfinding or difficulty-dependent runtime behavior are invented.

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
- `zstudio_ai_selection`: `preview`, `snapshot`, `action` (`select`, `clear`, `properties`) and a node ID except for clear. Selecting requires unlocked Whole world editing, visible AI and a matching filter; locked selection returns `locked` without retaining a hidden marker. Read-only Properties remains available while locked and shares GUI draft guards.
- `zstudio_camera` with `action: frame`, `target: selected` frames the selected AI node without changing source selection.
- `zstudio_scene_inspect` and `zstudio_scene_card` share the floating card, coordinate copy and explicit XYZ draft workflow. Read the current revision and draft token before confirming edits; accepted movement changes the graph snapshot.

Pages have at most 200 records. Handles are snapshot-scoped; rediscover after stale-snapshot/record/preview errors. Discovery never enables visualization or captures input.

## Verification

Core tests cover ordered directed links, negative slots, self-links, duplicate records/indices, missing targets, malformed data, versions, cancellation, stable identities and unsaved edits/undo. `ZSTUDIO_CORPUS` enables read-only corpus checks: each reference root contains 470 networks, 3,717 nodes and 6,240 resolved directed link slots across six network-bearing mission archives.

Named-pipe tests exercise GUI/MCP state agreement, pagination, filtering, stale handles, pinned inspection, framing and invalid-batch atomicity. Run `dotnet run --project tools/zStudio.PreviewCheck -c Release -- --ai-networks <root>` for GPU depth/picking checks, presented-back-buffer idle stability, mission integration and native overflow. Temporary captures stay outside the repository.
