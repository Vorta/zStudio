# Architecture

## Layers

| Project | Owns | Never |
| --- | --- | --- |
| `zStudio.Core` | Format readers and writers, source projects (reconstruction, evaluation, edits, builds, export), preview simulation (mission start, animation player) | UI, rendering or Python dependencies |
| `zStudio.Rendering` | In-house Direct3D 11 renderer (Vortice.Windows) and its device resources; `SceneViewport` (picking, gizmos, overlays, navigation math) | Document lifetimes, settings |
| `zStudio.Application` | The UI-independent command contract: `StudioCommands` registry, schemas, structured results, `StudioCommandException` codes; `StudioProfile`; the update check (`UpdateChecker`: GitHub's latest release, `ReleaseVersion` SemVer comparison, the release page rule) | Workspace state |
| `zStudio.Mcp` | The official C# MCP SDK host on a same-user named pipe, the stdio connector, the embedded `CommandCatalog.json` | Editing logic |
| `zStudio.Desktop` | WPF Fluent workbench, documents, dialogs, audio (NAudio, WASAPI), settings, command handlers marshalled to the dispatcher | Format parsing |

- `zStudio.exe` is the .NET single-file bundle of `zStudio.Desktop`, renamed after publishing; packaging rules: [releasing](releasing.md#package).
- WPF Fluent runtime theme switching is experimental upstream: the `WPF0001` opt-in is scoped to the theme-switch method.
- Tests run on Microsoft.Testing.Platform (`dotnet test --solution`).

## Formats and evidence

- `FormatRegistry` probes structure and version, never names alone. Five families: texture packs, ZAR archives (typed ZRD data inside or standalone), prepared scripts, animation packages, GameZ worlds. A sound ZAR starts with a RIFF member: its trailing index wins over standalone WAV detection. Unsupported versions stay available as raw bytes.
- Documents are immutable snapshots: source identity, record index, offsets, metadata, diagnostics. A size or write-time change shows a reload banner.
- GameZ: version 15 (retail, September 1998 demo) and 13 (July–August 1998 demos, read-only, read into the same model; layout in [engine evidence](engine-evidence.md#gamez-world-format)); MechWarrior 3 version 27. `GameZWriter` writes version 15. Version-28 animations bind to version-15 or -13 worlds; MechWarrior 3 uses version 39.
- `field-layouts.json` (embedded, checked in) defines fixed layouts and the initial event catalog. Edit it directly against engine evidence; no generation step. Its source hash records provenance from the archived Python reference.
- Variable-length readers validate their own ranges and count budgets. Nonfinite float bit patterns stay inspectable as hex. Pointer words are metadata, never process pointers.
- Evidence order: engine code and decompilation, then corpus bytes, then historical notes. Each conclusion names the operation it establishes. The retail executable shows run-time behaviour, not how the lost build tool wrote files: build behaviour comes from the shipped files (the runtime polygon routine drops colinear corners, the shipped models keep them, so the importer keeps them).
- Private references, external and read-only, never distributed or required to build or test: the RecoilRebuild `src/GameZRecoil` reconstruction and the `Recoil.bndb` Binary Ninja database. Addresses and symbol names in the docs record what was inspected.

## MCP

- Desktop registers every command once in `StudioCommands`; GUI handlers and MCP tools call the same services. Discovery metadata is generated from the registry into `src/zStudio.Mcp/CommandCatalog.json` (command: [AGENTS.md](../../AGENTS.md#build-test-package)). The parity test compares the whole catalog (names, descriptions, annotations, nested parameters) with the live registry.
- `docs/mcp-capabilities.json` is the GUI action inventory: `interactions[]` and `handlers[]` rows `{ gui, tools[], note }`. `McpParityChecks` fails on an XAML action handler without a row and on a row naming a missing tool. It cannot see bindings, dynamically built controls or renderer interactions: review those. `FieldEditor` fields have no generic MCP route; each needs its own tool ([AGENTS.md](../../AGENTS.md#engineering-invariants)).
- The visible workspace owns documents and renderers; connections never create parallel sessions. Reads may run beside asynchronous operations; mutations are serialized. Document revisions, preview lifetime UUIDs, context tokens and draft tokens guard against concurrent GUI activity.
- Initialization and discovery are windowless. The first workspace request attaches to the running instance or launches it through the desktop shell automation object, so a client killing its connector's process tree cannot kill the workspace (on a private test profile through a detached `cmd /c start` instead, which inherits that profile: [identity and settings](#identity-and-settings)).
- Transport: SID-restricted named pipe, network logons denied, no port; stdout carries only protocol; startup errors go to stderr. Access is opt-in (off in a fresh profile); no tool enables it in the user's profile (`--mcp-stdio` enables it only in its own private one). Limits: 2 MiB request line, 1 MiB arguments, 4 MiB results; long work returns operation handles (128 kept). No dialogs over MCP: drafts, unsaved changes and interrupted saves are explicit decisions, with explicit draft and conflict errors; MCP never discards input or captures physical input. Shutdown awaits the MCP server's stop while the WPF window is still alive.
- The SDK version is pinned in `Directory.Packages.props`.

## Source editing model

- **Sources are authoritative.** An edit becomes source changes, evaluated with the real build (`SourceBuilder`, `WorldAssembler`, `AnimationCompiler`, resource compilers); no editor-only world, no saved scene. Edit the source that owns the concept: a placement, a shared model and an initialization animation are different commands.
- **No inverse compiler.** Each edit kind plans: direct replacement when ownership is unique, a local refactor when its effects are established, otherwise `ambiguous` or `unsupported`. When provenance cannot establish ownership, the edit is unavailable rather than guessed.
- **The full rebuild is the correctness reference**; faster previews use gesture overrides and caching, never a second implementation.
- **Lossless.** Untouched bytes, comments, order, scalar kinds and token spelling survive (`ZrdTextSyntax`, `GameGenScriptSyntax`, the `WorldGltf` codec). Normalization is an explicit command.
- **Preview contexts are labelled.** Authored assembly, mission start and animation sandbox are distinct; runtime state (startup animations, cleanup, difficulty fallbacks) is never written to sources.

| State | Holds |
| --- | --- |
| Saved sources | The project on disk; exports read a frozen snapshot |
| Working sources | Accepted, undoable, unsaved edits in `SourceWorkspace`, shared by every view of the project |
| Compiled mission snapshot | Immutable build of one working revision (`SourceSceneSnapshot`): world, animations, resources, provenance |
| Presentation | Camera, selection, drafts, mission-start initialization, simulation; never written |

- Plan, preview, apply: preview evaluates a plan's patches on a private candidate; apply rechecks revision, baselines and drafts and records one history entry; a stale plan is replanned, never forced.
- Components: `SourceWorkspace` (buffers, baselines, one project-wide history, Apply/Undo/Redo/Save), `SourcePublisher` (journaled multi-file save and recovery), `SourceWorlds`/`SourceSceneSnapshot` (in-memory evaluation), `SourceObjectEdits` (object edits), `PickupPlacementEditSession` (pickup, AIV and AI-node placement records, difficulty counterparts, `PlacementPrecision`), Desktop `SourceWorldSession` (the open map).
- Where each edit lands, save and recovery: [sources](sources.md#edit-semantics), [workspace](sources.md#workspace-save-and-recovery).

## Saves and exports

- Asset exports name files by source record ID, reject destination traversal and links, create a new unique folder and write each file atomically. They never change source files. A cancelled batch may leave completed files in its new folder. JSON exports are an inspection schema, not an import format.
- `source_export`: [builds and export](sources.md#builds-and-export).
- Windows: temporary save files keep their creating handle through verification and publication; cleanup deletes through that handle, never by reopening the name (Blender-copy cleanup also checks file identity and digest; reconstruction and export cleanup report a holding file they could not delete).

## Rendering pipeline

- Geometry: concave polygons triangulated, indices validated, per-corner UVs kept, missing normals derived. OBJ export bakes instance transforms, uses inverse-transpose normals, fixes mirrored winding and flips V.
- Shading: textured surfaces unlit diffuse; untextured surfaces gentle view-dependent shading. Static views simulate no game effects; the animation viewport adds instance transforms, opacity, texture cycles, morphs, facades, lighting and fog.
- Surface highlights: world batches split by the CanModify/ClipTo bits while sharing geometry; a highlight swaps matching batches to cached unlit materials (alpha cutouts kept) without replacing meshes or moving the camera.
- Frame pipeline (`RenderView.FrameUpdate`, once per rendered frame before the camera is read): camera inertia and navigation, then pose normalization, facade/fog/horizon refresh and clipping, then camera-matrix capture. Camera notifications only invalidate, never reapply animation poses. Rendering is on demand. A frame is drawn into a private render target and handed to the D3DImage's shared surface in one copy at its end: WPF reads that surface with a StretchRect on the view's Direct3D 9Ex device (taken from the surface) on its render thread, unordered with the renderer's Direct3D 11 work, so drawing into it directly shows half-drawn frames; this way WPF sees a finished frame, the previous one until the copy completes (at worst a rare tear between two finished frames). While WPF reports the front buffer unavailable (software composition as over Remote Desktop, a display change, a locked session, a reset device) the view keeps drawing and the D3DImage's software fallback shows its frames; it never sets a null back buffer then (that detaches WPF's bitmap, which would never report the buffer available again), and rebuilds its surface once the buffer is back. Only a removed Direct3D 11 device recreates the devices. A forced capture first runs the pending layout pass and sizes the view from it, so it never waits on the queued resize; it errors (naming why) rather than return an older frame. Regression capture reads the presented back buffer; sized exports scale a current-size bitmap, never resizing the render target.
- Clipping: near/far come from visible transformed geometry; cached local bounds reject broadly, intersecting bounds refine against triangles clipped to the four side planes and the navigation floor, in double precision, with a guard band. Invisible meshes, the horizon and the grid never count. The navigation floor (0.001–0.05 scene units) is independent of the near plane.
- Texture lookup: sibling hardware variants, then software, then the root image pack; an explicit variant can be chosen. Caches are bounded and checked against file stamps. Ambiguous names are reported; a missing mission texture stays untextured, never substituted from another mission.
- Compiled-folder inventories share 128 Mi units and 250,000 visited entries per operation, admitted before collections are retained.
- Camera and navigation semantics: [preview](preview.md#cameras), [user guide](../3d-navigation.md).

## Compiled roots

- Opened only by MCP `open_root` without `project`; read-only: inspection, preview, export. No compiled document has an edit session, a dirty state, Save, Undo or editable Properties; those exist for source maps only.
- Compiled previews read the parsed document directly; compiled Whole world loads placement records read-only for identities, pickups and difficulty remapping. Animation inspection tools (`animation_records`, `references`) answer compiled documents only.
- User-facing description: [compiled game files](../compiled-files.md).

## Identity and settings

- Solution `zStudio.slnx`; projects, assemblies, namespaces, embedded resources and friend assemblies use `zStudio.*`.
- `%LOCALAPPDATA%/zStudio` holds settings (`StudioSettings`), the error log and local MCP registrations; `StudioProfile` (zStudio.Application) resolves the folder for every reader and writer. No settings import from other versions, no runtime aliases. A fresh profile keeps MCP disabled. Settings load with default `System.Text.Json` options, so unknown keys are ignored and dropped on the next save.
- Tests and tools only, not a user feature (the user guides don't mention it): `ZSTUDIO_PROFILE`, an absolute folder read once, replaces the profile for the process and the processes it starts ([testing](testing.md#environment)). A private profile never contacts GitHub: its update checker has no connection, and tests give the window one over a fake handler ([workbench](workbench.md#update-check)). On a private profile a connector that has to start the workspace does so through a short-lived `cmd.exe /c start` instead of Explorer, so the workspace inherits the profile and the desktop and outlives a parent-tree kill of the connector (not a job-object kill without breakaway, which the Explorer launch survives).
- A project's only zStudio data is its hidden `.zstudio/` folder ([sources](sources.md); settings: `SourceProjectSettings`).
