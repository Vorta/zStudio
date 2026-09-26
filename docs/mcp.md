# MCP access to the visible workspace

zStudio exposes its current capabilities to local AI agents through Model Context Protocol. GUI and MCP use the same documents, accepted edits, undo history, preview player, renderers and verified save/export paths. This is a local shared desktop workspace; it is not a headless converter or a remote HTTP service.

## Connect

1. Run the MCP-enabled build of zStudio and open **Tools → MCP integration…**. This menu is available on the welcome screen.
2. Check **Enable local MCP access**. The choice persists and is off by default.
3. Use **Copy configuration** to obtain the exact executable path for your installation. Add it to your agent's MCP configuration using its supported configuration format. The standard JSON form is:

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

The connector starts quietly in the background. Initialization, ping, tool/resource discovery and capability-schema inspection do not open or connect to a GUI. The first valid workspace tool call (including `zstudio_state`) or read of `zstudio://state` attaches to a running instance of that installation. If none is running, it asks the Windows desktop shell to launch a visible workspace. Concurrent first requests share one connection/startup. MCP access is checked at use time, so disabling it after agent startup still prevents access. Disabled access permits static discovery only. Disconnecting a client leaves the workspace open, including unsaved edits. The connector writes only MCP traffic to stdout; startup errors go to stderr. If several instances of the same installation are open, the connector refuses to choose silently. Copy the desired instance ID from the integration window and use `"args": ["--mcp", "--instance", "ID"]`. Instance IDs last for that process lifetime.

The integration window shows connected-client count on Refresh, instance identity and the latest 200 activity messages. **Disconnect clients** drops current workspace connections. Reconnect the MCP client afterward; a disconnected connector does not silently reopen the workspace or replay a request. Unchecking **Enable local MCP access** stops the listener and resolves in-flight automation shutdown; recheck it before reconnecting. It does not provide an agent-callable command to enable access. Local processes running as your Windows user can connect while enabled, so connect agents you trust with the open workspace.

## Discover and operate

Start with `zstudio_state` and `zstudio_capabilities`. Standard `tools/list` exposes typed input schemas; `resources/list` exposes `zstudio://state` and `zstudio://capabilities`. Tool results include text JSON and structured content. Capture additionally returns an MCP PNG image block. Array schemas include element types and constraints: camera `position` and `look` each contain exactly three numbers; export `assets` contains objects with a valid `kind` and integer `index`, both required, with no extra fields. Recursive validation rejects malformed arrays with `invalid_argument` before executing a handler or launching/attaching the workspace.

Long-running tools return an operation object with `id`, `Name`, `State`, `Cancellable`, and `result`. The initial state may already be completed. Poll `zstudio_operation` using the ID until `completed`, `failed`, or `canceled`; inspect the nested result. Operations continue after a client disconnects. Only exports and validation accept manual cancellation; workspace replacement and application shutdown still cancel obsolete work automatically. At most 128 operation records are retained, including queued work. Read-only workspace/status tools remain available while an operation awaits background work.

Use document lifetime UUIDs, asset kind/index, sequence/event UUIDs, and complete pickup source identities returned by discovery. Names are labels and are not unique. Each accepted edit increments the document revision. Mutation tools require the expected revision; a stale request returns `revision_conflict`. Preview commands require the current preview lifetime UUID. Re-read state after navigation or changes that replace a static preview. GUI actions can supersede agent work; context changes are reported rather than redirected to a different document.

`zstudio_drafts` reports pending Properties or preview input and a conflict token. Resolve it explicitly with `zstudio_resolve_drafts`, the owning document, current token and `apply` or `discard`. Invalid values remain visible. Accepted edits are separate from drafts: `close_document` requires `discard=true` to abandon unsaved edits. There are no file-picker or save-confirmation dialogs in the MCP workflow.

## Capabilities

All tools below have the `zstudio_` prefix. Call `capabilities` for current schemas and option names.

| Area | Tools and behavior |
| --- | --- |
| Workspace | `state`, `capabilities`, `open_root`, `files`, `search`, `open_document`, `assets`, `asset_filter`, `select_asset`, `related`, `reload_document`, `close_document` |
| Inspection | `inspect_asset` returns source metadata/content and separately identifies edited animation data. `source_bytes` reads at most 4096 original bytes. `problems` retains severity/source context. ZRD trees, scripts, model/material metadata and sound cues use the existing inspection/export data. |
| Animation editing | `animation_records`, `event_catalog`, `property_fields`, `property_edit`, `property_action`, `animation_structure`, `references`, `reference_edit`. Fields/actions use the same Properties definitions and validation as the GUI, including compound fields and keyframe segments. Query field IDs for the specific target, segment and revision before editing. Sequence/event lists and reference slots are paged. |
| Animation preview | `preview_state`, `animation_options`, `animation_transport`, `animation_runtime`, `animation_select`. Supports playback/seek/frame stepping, runtime versus cleanup, seed/condition/activation points, root/world binding, range, speed/replay/audio, Map/difficulty, LOD, horizon, effects, followed camera, grid/height/collision, dispatch range and log/problem presentation. Runtime occurrence identities and trace caps remain separate from authored timing. |
| 3D views | `scene_nodes`, `scene_selection`, `scene_options`, `camera`, `scene_properties`. Camera read/set/move/rotate/frame provides the semantic equivalent of navigation without capturing physical keyboard/mouse input. Disable Follow camera before imposing a manual animation camera. |
| Textures | `texture_view` exposes zoom, fit, channels, smoothing, pan and pixel RGBA. Channel indices are 0 RGBA, 1 RGB, 2 Alpha, 3 Red, 4 Green, 5 Blue. `texture_palette` returns header and paged RGB565 palette values. |
| Sound | `sound_transport` reads/plays/pauses/stops/seeks the selected sound; inspection returns waveform/header/cue metadata. |
| Pickups | `pickups`, `pickup_lock`, `pickup_move`. New documents are locked. Movement is one undoable operation, using archive/resource/record identity and existing difficulty-counterpart matching. Exact-coordinate moves are the semantic equivalent of gizmo drags. |
| History/files | `undo_redo`, `save_document`, `export`, `validate`, `operation`. Animation saving requires a new destination outside the source dataset. Pickup saving uses owning archives or explicit source-to-destination mappings with the existing conflict, unrelated-byte and reparse checks. |
| Properties/drafts | `properties_open`, `properties_close`, `properties_state`, `scene_properties`, `drafts`, `resolve_drafts`. Properties remains one pinned, owned window; querying/editing another animation's fields does not retarget it or create a hidden renderer. |
| Presentation | `workspace_view` controls theme, density, panes, tabs, dimensions, presets, backup preference and reset. `window` reads/activates/minimizes/maximizes/restores/closes the app. `capture` returns the current texture/3D preview, main window or Properties window. Window capture is restricted to zStudio-owned windows and requires a non-minimized window. |

Lists use `offset` and `limit` (1–200, default 100) where provided; `nextOffset` identifies another page. Original bytes are bounded to 4096 bytes per request. RPC request lines are capped at 2 MiB, tool arguments at 1 MiB and JSON tool results at 4 MiB; large inspections should use paged properties/records/source bytes or JSON export. For 3D captures, width/height are maximum dimensions (1–4096 per axis); the image preserves its aspect ratio and the live viewport is not resized. Texture/window captures retain native dimensions. Returned PixelWidth/PixelHeight identify the actual output size. For non-image/non-3D previews use `capture` with `target=window`.

`capture` with `target=preview` requires the current `preview` UUID from `state`; missing or replaced identities return `stale_preview`. Successful preview captures include that UUID and the asset identity alongside the image. Window and Properties captures remain independent of preview selection. Explicit camera poses are clamped to the upright ±89° pitch range before returning, so immediate movement is safe even after a vertical requested direction.

Disabling MCP or closing zStudio cancels pending indexing, opening, preview loading/seeking and save work as well as queued operations. Indexing remains unavailable for manual cancellation. Operation cancellation is separate from the loaded workspace and preview lifetimes: stopping access after a completed request does not invalidate the retained preview. Saves retain their existing atomic commit boundaries; shutdown cannot undo an already committed file.

Canceling a level refresh clears its loading overlay when that request still owns it, allowing subsequent scene options and transport without reselecting the animation. Audio preparation retries also observe operation cancellation; the editor remains available for another retry. Malformed keyframe payloads remain read-only, but `property_fields` with the default segment 0 still exposes available scheduling/catalog fields and a read-only Keyframe diagnostic. Nonzero segment requests are rejected when the payload cannot be decoded.

Static model/Whole world LOD, horizon, texture-pack and Whole world difficulty refreshes prepare a replacement before publishing it. Cancellation or load failure retains the displayed scene, camera, selection and preview UUID, and restores the option controls and shared saved difficulty to their published values. A successful replacement receives a new preview UUID; read `state` again before subsequent preview commands. This behavior is shared by GUI and MCP option changes.

List `query` filters apply case-insensitively before pagination; totals refer to matching records. Reference indices, sequence/event UUIDs, pickup source identities and dispatch occurrences remain unchanged by filtering. Runtime pages also return `offset` and `nextOffset`. Search uses names/paths or displayed text appropriate to each list (event operands and diagnostic text are searchable). `preview_state` reports the active viewer's LOD/horizon and uses null for unavailable 3D controls in texture, sound or text views. A `workspace_view` request with an unavailable tab fails before applying any presentation changes. Structural edits with an unavailable sequence/event return `stale_record`; refresh `animation_records` before retrying.

Tools expose **capabilities that actually exist in this version**. MCP does not add texture replacement/import, arbitrary binary patching or editing of read-only format families. Game callbacks and other engine-dependent preview approximations retain their existing support diagnostics. Exports use the source document; saving animation edits and inspecting edited properties are explicitly separate operations.

## Example editing workflow

1. `open_root` with the root path; await its operation. `open_document` with the full animation archive path; await it.
2. Read `assets`, retaining the returned document UUID and animation entry index. `select_asset` with those identities and `kind="Animation"` loads the shared preview.
3. Read `animation_records` for sequence/event UUIDs. Read `property_fields` for that exact target (and `segment` for keyframes).
4. Call `property_edit` with the document UUID, current revision, target, returned field ID and invariant text value. Compound fields use the delimiter shown in their current value. Read state again before the next accepted edit.
5. Preview with `animation_transport`; inspect diagnostics and `capture` output. Save through `save_document` with an explicit new destination, then await and inspect its operation result.

## Development contract

`zStudio.Application` owns the UI-independent command/schema contract. `zStudio.Mcp` adapts it to the official C# MCP SDK and a same-user named pipe; network logons are denied. `zStudio.Desktop` marshals command execution to the WPF dispatcher and coordinates mutations, async jobs and existing services. No port is opened and no game assets or external decompilation references are exposed as server configuration.

Every future user-facing feature requires MCP parity, typed schemas, documentation and meaningful tests in the same change. Discovery metadata is generated directly from the Desktop command registry into `src/zStudio.Mcp/CommandCatalog.json` and embedded in the connector; it contains no workspace data or duplicate editing implementation. After changing commands, regenerate with `dotnet run --project tools/zStudio.PreviewCheck -c Release -- --mcp-catalog src/zStudio.Mcp/CommandCatalog.json`. The parity test compares the entire generated catalog (names, descriptions, annotations and nested typed parameters) with the live registry; stale metadata fails CI. `docs/mcp-capabilities.json` is the independently maintained GUI action inventory. Tests compare actual XAML event handlers with it and verify that mapped tools exist. This is a review guard, not proof of behavior: bound controls, dynamically built forms and renderer interactions also require review and protocol tests. FieldEditor-generated properties and actions inherit exposure; binary validation remains shared. See CONTRIBUTING and AGENTS.

The SDK is pinned to [ModelContextProtocol.Core 2.2.0](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.2.0). Visible startup uses the desktop-hosted shell automation object so clients which terminate their connector process tree cannot kill the user's shared workspace; the approach follows [Microsoft's desktop-shell guidance](https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643).
