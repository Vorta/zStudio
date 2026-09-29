# MechWarrior 3 base game

Open the game's `zbd` folder through **File → Open folder**. Supported structures are identified from binary headers and record layouts: GameZ version 27, compiled animation version 39, version 4 motion members and the model-library archive format. Texture packs, prepared scripts, sounds and typed ZRD resources use the existing shared readers and editors. Recoil's version 15 worlds and version 28 animations retain their own layouts.

## Worlds and missions

Open a map's `gamez.zbd` and select **Whole world**. The mission picker replaces the Recoil difficulty picker. It lists authored campaign, instant-action and multiplayer readers; only the selected reader contributes mission actors and AI networks. Shared map/root resources remain available. The selected reader is remembered per map.

Authored AIV positions and headings place uniquely resolved world templates and base mech assemblies. Repeated placements have independent instance transforms and retain archive/member/record identity. Player inventory, custom player mechs, mission-script activation and combat are not simulated. Missing or ambiguous templates remain inspectable in the AIV resource and produce preview notices.

**Unlock editing** enables object cards and selection bounds. **Edit** enables the existing transform draft and movement/rotation controls. MW3 AIV edits affect that mission record only; Recoil difficulty linking does not apply. AI nodes support translation. Gate and other edge-constraint records have no authored XYZ and remain non-spatial constraints. Switching missions preserves coordinate edits and undo history. Save patches changed coordinate scalars through the existing verified archive service.

Rendering supports triangle strips and per-corner RGB colors. OBJ export retains vertex colors. Texture resolution includes shared mech texture packs. Scene inspection and the Document scene tree preserve source/member identity when names repeat.

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

Deleting the displayed motion or replacing it with non-motion data closes that motion viewer and shows the archive's resulting selection, or archive information when empty. Undo/Redo updates the preview as well, including when another Navigator tab is open. Renaming or reordering a surviving motion retains its viewer, playback and camera by member UUID; duplicate names and reused row indices do not identify the same clip. The separate Properties window keeps its pinned identity.

Play/pause, the loop-time seeker, LOD and Frame use the shared renderer. Space works from Assets or passive preview content. Blender-style navigation, scene inspection and the tree apply to the visible posed geometry. Playback interpolates translations and normalized quaternions through the hierarchy; aiming, gait correction and inverse kinematics are not simulated.

Open **Properties** for a motion member to edit loop duration or a part/frame's translation and WXYZ quaternion. Indices are zero-based. Insertion/deletion changes every track together while retaining loop duration. The separate closing sample survives no-op writes and unrelated edits; editing the first frame or changing frame structure closes the loop against the first sample. Draft validation, pinned identity, undo/redo, external-change checks and verified Save As are shared with the archive editor.

## Compiled animations

Version-39 entries use the existing sequence/event editor, Properties, transport, seek, audio preparation, camera preview, export and verified Save As. Catalogs are version-specific. Larger light/procedural records, water/lava collision fields, keyframe blocks, weapon events and particle records retain serialized sizes and opaque data. No-op writes preserve original bytes and record order.

Supported shared animation operations have explicit approximation notices. Stored spline coefficients are preserved; preview sampling uses base/rate channels. Weapon gameplay and particle simulation are not executed. Version-39 gameplay markers 41/42 retain their dispatch traces and support notices, then allow subsequent events to run in both ordinary and cleanup sequences. Water/lava contact behavior, AI activation and mission-script execution are not reproduced. Recoil engine evidence does not establish MW3 runtime compatibility. Bind version-39 animations to a version-27 world; cross-game bindings are rejected. Ambiguous MW3 roots require an explicit root binding.

## MCP

GUI and MCP share the visible workspace, parser, identities, edit sessions, previews and saves.

Motion inspection metadata previews at most 32 parts and 128 characters per name, with full counts and explicit truncation flags. Use `zstudio_motion_records` to page all parts with complete names; explicit JSON exports also retain the full part list. Model rows from `zstudio_mech_models` preview at most 32 node references and material indices, with `nodeCount`/`nodesTruncated` and `materialCount`/`materialsTruncated`. For the complete lists, supply `section: "nodes"` or `"materials"` and the member's `localModel`, then use `offset`/`limit`. Nodes require `localModel`; materials without it lists all shared materials. Queries match node names, `Model N` or `Material N` labels before pagination.

| Command | Purpose |
| --- | --- |
| `zstudio_missions` | List/select authored readers for the visible MW3 world or animation. |
| `zstudio_motion_records` | Page motion parts or frames, including the closing sample. |
| `zstudio_motion_edit` | Edit timing/transforms or insert/delete frames as one archive undo step. |
| `zstudio_motion_preview` | Control visible motion playback, seek, assembly, LOD and framing. |
| `zstudio_mech_models` | Page member-local models, shared materials and node references. |
| `zstudio_mech_model_replace` | Replace one explicit member-local mesh. |

`zstudio_motion_preview` state distinguishes actual `playing`, `loading` and `playbackRequested`, so a temporarily suspended assembly load does not look like a new pause request. `zstudio_resource_properties` accepts motion `part`/`frame` targets and generated actions. `zstudio_event_catalog` accepts `version: 28` or `39`. `zstudio_ai_nodes` exposes edge constraints with `section: "constraints"`; a record may contain multiple ordered attributes, identified by its record index and `AttributeIndex`. Existing resource, camera, inspection, export, texture and save commands apply. Revision and draft guards remain mandatory for mutations. Discovery remains windowless and access remains opt-in.

## Validation

Set `ZSTUDIO_MW3_CORPUS` to a base-game `zbd` folder for optional corpus checks with `dotnet test --solution zStudio.slnx`. They cover no-op preservation, malformed input, motion history, mission isolation, hierarchy sampling and member-local replacement. The portable live check is `dotnet run --project tools/zStudio.PreviewCheck -c Release -- --mcp-stdio artifacts/zStudio-win-x64/zStudio.exe <zbd-root>`; it requires existing user-enabled MCP access. Assets and reports are not checked in. Parsing, readback and preview checks do not establish original-game compatibility.

The renderer's buffer-size regression is `dotnet run --project tools/zStudio.PreviewCheck -c Release -- --viewport-size <zbd-root>`. It checks first-load and resized GPU surfaces under a busy render queue, including display DPI and camera retention.
