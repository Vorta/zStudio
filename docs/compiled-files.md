# Compiled game files

zStudio edits [source projects](source-project.md). A folder of compiled game files (a shipped `zbd` folder, or files exported from a project) is read-only: an AI agent can open it through MCP to inspect, preview and export its contents. It has no Save, Undo or Redo. To change game data, initialize a source project from the original files, edit it, and export.

## Open compiled files

An agent opens it with [`zstudio_open_root`](mcp.md#workspace-model), its full path and no `project`; **File → Open workspace…**, recent folders, dropped folders and the command line open source projects only.

In the opened folder, **Files** lists every file, including unknown ones (shown as raw bytes). Double-click a file to open it; open files are marked in Files and have a close button. **Assets** lists the open file's records, **Search** searches the whole folder, and **Document scene** shows the node hierarchy of a world or of an animation's bound scene. MCP: `zstudio_files`, `zstudio_open_document`, `zstudio_assets`, `zstudio_search`, `zstudio_select_asset`, `zstudio_scene_tree`.

## Workspace

- **Properties** (a record's context menu **Properties…**, or **Alt+Enter**) opens one reusable window showing the record's stored data, read-only. It stays on its record while you browse; close it with its X. An animation shows the same in its [Inspector](#animation-preview) instead. Node detail is in the viewers' trees and in MCP (`zstudio_animation_records`, `zstudio_zrd_nodes`). MCP: `zstudio_properties_open`, `zstudio_properties_state`.
- The bottom tools hold **Problems**, **Related** and **Bytes** (the selected record's original bytes, at most 4,096). Problems keeps each issue's severity and source.
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

Open a mission's `gamez.zbd` and choose **Whole world**. The view shows the single-player starting layout: AI vehicles, pickups, turrets and the objects startup animations place. The toolbar begins with **LOD**, the difficulty picker (**Easy**, **Medium**, **Hard**; shared with animation previews and remembered) and **Frame**. Hover or click an object for its card and selection bounds; nothing can be moved or changed. Navigate with the mouse or the [Fly camera](3d-navigation.md#fly-camera). The AI node overlay shows the authored networks. The **Drop windows** chip at the view's top left analyses a VTOL flight's drop windows and conditional release positions and shows their markers, read only: windows can't be moved; the analysis follows the game's rules from the scenario's player (a custom player is set through MCP). A mission without a VTOL flight says so, with no flight to choose or analyze again. MCP: `zstudio_scene_inspect`, `zstudio_scene_card`, `zstudio_pickups`, `zstudio_ai_networks`, `zstudio_ai_nodes`, `zstudio_animation_paths`, `zstudio_animation_scenario`.

## Animation preview

Open a mission's `anim.zbd` and select an animation in Assets. It opens read only with the components of a source map's [Animation mode](map-editor.md#animation-mode): the command bar names it (**destroy_tr_bridge** · Animation · `m4/anim.zbd` · entry · size) with a **Read only** tag and no Save, Undo or Redo; the isolated 3D view sits above one panel; the Inspector and the Scenario tab sit on the right. Its world, textures, effects and sounds resolve automatically.

- **Command bar**: **LOD**, difficulty, **Camera** (**Frame**, **Follow camera**), **Display** (**Map**, **Horizon**, **Effects**, **Face orientation**: front faces blue, back faces red) and **Ground** (**Grid**, **Ground collision**: a flat plane at Y = 0, **Height**: −999 to 999, raising or lowering the animation; the grid stays at Y = 0), then the scenario chip.
- **Panel**: one [transport](map-editor.md#transport) in seconds (its range flyout offers **Auto range**, the measured duration, and **Estimate duration**), then **Structure**, **Timing** and **Split** (Split needs a wide panel). The [keys](map-editor.md#keys-and-exit) are Animation mode's, apart from **Alt+←/→** and **Shift+Esc**: another animation opens from Assets, and there is no mode to leave. **Esc** ends what is in progress (**Pick in view**, a flyout), then clears the selection, wherever the focus is in the preview (a text box or list keeps it); **F2** on an event shows its read-only start threshold. The view's tools that do not fit a narrow panel move to **⋯** beside the notices chip. **Tools** (the status bar) shows or hides **Diagnostics** under the panel: the preview's problems (the run's notes, a failed preparation, audio), errors and warnings first, with the file's; past 500 rows it says how many more there are (MCP `animation_runtime problems` lists every one), and a note says how many the preview left out after 2,000 distinct ones. Structure is the animation's graph, its sequences numbered as stored (**sequence 6 · move_piece3**); Timing shows when each sequence and event ran, with the Log. The graph's condition switches and call ports change the preview only; start thresholds and starts are read only.
- **Inspector** (**Alt+Enter**, or **Properties…** on the animation's row): the selected definition, sequence or event as Animation mode shows it (an event's **Occurrences** too: when it ran on the shared clock and its own, with the result), every field read only and selectable, plus **Stored** (type, size, offsets, flags in hex), **Bytes** (the record's original source bytes, at most 4,096), **References** (the stored tables with their indices; a reserved 0 is marked) and **Related** (name matches).
- **Scenario** (**Animation → Scenario**, or the chip): the start (the animation at 0 s), **Called animations** (every call runs, calls back into the chain too; switch one off to leave it out, with **Include all** and **Exclude all**; animations that keep calling each other run until the chain is too deep for the preview, where Timing's Log shows that call as `excluded · call path too deep`), the player at start, a custom player (**Pick in view**) or none, the random seed, effects detail, condition overrides, the rules, the phase (**Runtime**, or **Cleanup** for the cleanup sequence alone), height, beam origin, **World** (**Choose GameZ…**), **Bound root** (**Bind root…** when a name matches several, **Clear override**) and **Range** (**Auto** or a custom end). The inputs, rules, world and the view's toggles are kept while you select the file's other animations (never saved); call switches, condition overrides, a bound-root override and a custom range belong to the animation shown, so another one starts with every call, no override and **Auto**. **Reset…** returns everything here, the world, bound root and range included, to the defaults; the chip's dot shows when anything differs.

If the preview can't be prepared, the view says why; Structure and the Inspector still show the stored data, and **Choose GameZ…** picks the mission scene. Procedural object movement stops at world Y −500 or 500, including the Height offset; keyframes and cameras are unrestricted. The preview is not the game: terrain and object collision, AI, gameplay prerequisites, exact lighting, fog and translucency sorting are approximated or left out, as the support notices in Diagnostics say. Source-map previews separately use standable map terrain. MCP: `zstudio_animation_graph`, `zstudio_animation_timing`, `zstudio_animation_scenario`, `zstudio_animation_transport`, `zstudio_animation_options`, `zstudio_animation_runtime`, `zstudio_animation_mode`, `zstudio_animation_records`, `zstudio_references`, `zstudio_related`, `zstudio_event_catalog`.

## Export

Select records in Assets and choose **Export selected…** (textures as PNG with alpha, sounds as WAV, scripts as text, models and worlds as OBJ with materials and textures, other records as their stored bytes) or **Export selected as JSON…** (an inspection format). Exports go to a new folder outside the opened one. MCP: `zstudio_export`.

`zstudio_model_bundle_export` (MCP only) exports every model under a node or an animation's root for use in Blender: `assembled.obj` with its materials and textures, `local/model_INDEX.obj` per distinct model without node transforms, and `manifest.json` with the world's SHA-256, model and node indices and both local and assembled transforms. Coordinates are game units, **+Y up** and **−Z forward**. A bundle holds at most 10,000 nodes and 1,024 models.

## Other versions and games

- **July and August 1998 demos** (version-13 worlds): their worlds preview like the retail ones, and the August demo's animations too (the July demos' animation programs, versions 26 and 27, are not read).
- **MechWarrior 3**: its base-game worlds, animations, motion clips and model library can be inspected and previewed. A mission picker replaces the difficulty picker. MCP: `zstudio_missions`, `zstudio_motion_records`, `zstudio_motion_preview`, `zstudio_mech_models`, `zstudio_ai_valves`, `zstudio_ai_valve_selection`. Further MechWarrior 3 support is deferred.
