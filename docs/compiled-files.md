# Compiled game files

zStudio edits [source projects](source-project.md). A folder of compiled game files (a shipped `zbd` folder, or files exported from a project) is read-only: an AI agent can open it through MCP to inspect, preview and export its contents. It has no Save, Undo or Redo. To change game data, initialize a source project from the original files, edit it, and export.

## Open compiled files

An agent opens it with [`zstudio_open_root`](mcp.md#workspace-model), its full path and no `project`; **File → Open workspace…**, recent folders, dropped folders and the command line open source projects only.

In the opened folder, **Files** lists every file, including unknown ones (shown as raw bytes). Double-click a file to open it; open files are marked in Files and have a close button. **Assets** lists the open file's records, **Search** searches the whole folder, and **Document scene** shows the node hierarchy of a world or of an animation's bound scene. MCP: `zstudio_files`, `zstudio_open_document`, `zstudio_assets`, `zstudio_search`, `zstudio_select_asset`, `zstudio_scene_tree`.

## Workspace

- **Properties** (a record's context menu **Properties…**, or **Alt+Enter**) opens one reusable window showing the record's stored data, read-only. It stays on its record while you browse; close it with its X. Sequence, event and node detail is in the viewers' trees and in MCP (`zstudio_animation_records`, `zstudio_zrd_nodes`). MCP: `zstudio_properties_open`, `zstudio_properties_state`.
- The bottom tools hold **Dispatch**, **Event log**, **Problems**, **Runtime**, **Related** and **Bytes** (the selected record's original bytes, at most 4,096). Problems keeps each issue's severity and source.
- **View → Workspace preset** offers Inspect, Edit, Debug and Focus preview layouts; **View → Reset layout** restores the default. Pane sizes and tabs are remembered.
- A file changed on disk shows a reload banner; **Tools → Reload current file** (**F5**) reads it again.

## Viewers

| Content | What you see | MCP |
| --- | --- | --- |
| Texture packs | Each texture with zoom, **Fit**, **1:1**, **Smooth**, channels (**RGBA**, **RGB**, **Alpha**, **Red**, **Green**, **Blue**), pixel colours and **Palette / header** | `zstudio_texture_view`, `zstudio_texture_palette` |
| Sounds | Waveform, header and cue points; play, pause and seek | `zstudio_sound_transport` |
| Prepared scripts (`interp.zbd`) | **Instructions** in stored order, a bounded **Text** reconstruction, **Bytes** | `zstudio_script_records`, `zstudio_script_select` |
| Archives and resources (ZAR, ZRD) | Members in stored order in Assets; a resource's typed values in the **Data** tree | `zstudio_archive_members`, `zstudio_zrd_nodes`, `zstudio_resource_select` |
| Models and worlds (`gamez.zbd`) | A 3D view with LOD, textures, wireframe, face orientation, surface highlights and horizon; **Whole world** shows the mission | `zstudio_scene_options`, `zstudio_scene_nodes`, `zstudio_scene_selection`, `zstudio_camera` |
| Any record | Its metadata and content | `zstudio_inspect_asset` |

A damaged record is reported in Problems; the other records of the file stay available.

## Whole world

Open a mission's `gamez.zbd` and choose **Whole world**. The view shows the single-player starting layout: AI vehicles, pickups, turrets and the objects startup animations place. The toolbar begins with **LOD**, the difficulty picker (**Easy**, **Medium**, **Hard**; shared with animation previews and remembered) and **Frame**. Hover or click an object for its card and selection bounds; nothing can be moved or changed. Navigate with the mouse or the [Fly camera](3d-navigation.md#fly-camera). The AI node overlay shows the authored networks, and the VTOL drop-window button analyses a flight's drop windows and conditional release positions; windows can't be selected or moved. MCP: `zstudio_scene_inspect`, `zstudio_scene_card`, `zstudio_pickups`, `zstudio_ai_networks`, `zstudio_ai_nodes`, `zstudio_parachute`.

## Animation preview

Open a mission's `anim.zbd` and select an animation in Assets. Its world, textures, effects and sounds resolve automatically; **Scene → Choose GameZ…** picks another world, **Scene → Bind root…** another root node when a name matches several.

- **Toolbar**: **LOD**, difficulty, **Frame**, **Face orientation** (front faces blue, back faces red), then **Map**, **Grid**, **Height** (−999 to 999, raising or lowering the animation; the grid stays at Y = 0), **Ground collision** (a flat plane at Y = 0), **Horizon**, **Effects** and **Follow camera**.
- **Transport**: **Space** (with Assets or the preview focused) plays and pauses; **Play/Pause**, **Stop**, **Previous frame** and **Next frame** sit beside the seek bar, which counts 60 Hz frames. Below it: **Preview phase** (**Runtime**, or **Reset / stop** for the cleanup sequence), **Range…** (the preview range), speed, **Replay preview**, mute and volume.
- **Sequences** lists the entry, its Cleanup sequence, the runtime sequences and their events with their stored operands. The timing under an event is a start threshold, not a duration; **Dispatch** and **Event log** show when events actually ran.
- **Settings** shows the binding and mission, and sets the seed, forced conditions, activation points and **Range in seconds** (**Auto** restores the calculated range, labelled **Looping**, **Open-ended** or **Partial** when there is no fixed end).
- **References** lists the stored reference tables.

The preview is not the game: terrain and object collision, AI, gameplay prerequisites, exact lighting, fog and translucency sorting are approximated or left out, as the support notices in Problems say. MCP: `zstudio_animation_records`, `zstudio_animation_select`, `zstudio_animation_transport`, `zstudio_animation_options`, `zstudio_animation_runtime`, `zstudio_references`, `zstudio_event_catalog`.

## Export

Select records in Assets and choose **Export selected…** (textures as PNG with alpha, sounds as WAV, scripts as text, models and worlds as OBJ with materials and textures, other records as their stored bytes) or **Export selected as JSON…** (an inspection format). Exports go to a new folder outside the opened one. MCP: `zstudio_export`.

`zstudio_model_bundle_export` (MCP only) exports every model under a node or an animation's root for use in Blender: `assembled.obj` with its materials and textures, `local/model_INDEX.obj` per distinct model without node transforms, and `manifest.json` with the world's SHA-256, model and node indices and both local and assembled transforms. Coordinates are game units, **+Y up** and **−Z forward**. A bundle holds at most 10,000 nodes and 1,024 models.

## Other versions and games

- **July and August 1998 demos** (version-13 worlds): their worlds and animations preview like the retail ones; Initialize refuses them. The September 1998 demo can be reconstructed as a source project.
- **MechWarrior 3**: its base-game worlds, animations, motion clips and model library can be inspected and previewed. A mission picker replaces the difficulty picker. MCP: `zstudio_missions`, `zstudio_motion_records`, `zstudio_motion_preview`, `zstudio_mech_models`, `zstudio_ai_valves`, `zstudio_ai_valve_selection`. Further MechWarrior 3 support is deferred.
