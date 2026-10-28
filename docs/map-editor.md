# Map editor

Open a [source project](source-project.md), then a map. You edit the map's sources directly: objects, placements, AI networks, VTOL drop windows, zones and terrain. Every change is one undo step of the project; **Save** writes the changed source files, and only [export](source-project.md#check-and-export) builds game files. Camera controls are in [3D navigation](3d-navigation.md).

## Open a map

The map picker lists the single-player maps (m1–m6) and the multiplayer maps; one map is open at a time. A map whose model or script is broken still has its button; opening it reports the problem. The view shows the mission's starting scene at the chosen **Difficulty** (Easy, Medium or Hard): the units and pickups that difficulty places and the poses its startup animations set. The back arrow left of **Whole world**, or **File → Close file** (**Ctrl+W**), returns to the picker, asking to save or discard unsaved changes.

MCP: `zstudio_source_world_list`, `zstudio_source_world_open`.

## Layout

- **Title:** the map's name, with an **Edited** pill while the project has unsaved changes.
- **Command bar:** the scope (back arrow, **Whole world** and the world's source model file, such as `m1.gltf`); **Save**, **Undo**, **Redo**; **LOD** (LOD 0 is each object's highest detail) and **Difficulty**; then **Camera** (Frame all, Flycam), **Display** (Wireframe, Textures, Bounds, Horizon), **Overlay** (Non-default soils, CanModify, ClipTo, Landmarks, Face orientation) and **Zones** (Edit zones).
- **World tree** (left), the view (middle) and the **Properties** pane (right). Drag the gaps between them to resize.
- **Over the view:** the **Perspective** chip (projection, named views, framing), the navigation cube, the selected object with its handles, and the surface under the pointer with its X/Y/Z.
- **Status bar:** the selection, the mission start and difficulty, the [player tank](#player-tank) while it isn't drawn as at the start, instance and mesh batch counts, **Preview notices**, and toggles for the **World tree**, **Properties** and **Diagnostics** panes. Diagnostics lists the map's file and preview problems.

### World tree

Rows come in a fixed order: **World** (the authored glTF hierarchy, including groups the build removes), **Zones**, **AI Vehicles**, **AI Networks**, **Pickups**, **Animations** (objects placed by animations and the `.zad` definitions the mission lists). Icons show each node's type; the info button beside **Show all** opens a legend.
- Objects show their zone on the right (`Zone: 12`, `Zone: Any`).
- Expand an imported object to browse its groups, nested references, geometry and LOD branches. Clicking a child selects that exact part; clicking geometry in the map still selects the whole object. Hidden parts remain in the tree and can be inspected. Deep branches scroll horizontally to keep names and eye buttons accessible.
- Healthy and destroyed branches keep their initialized visibility: healthy normally has an open eye, destroyed a closed eye. Hide healthy and show destroyed with their separate eyes to inspect the destroyed geometry. These are preview overrides, independent of the stored Visible flag: no source edit or undo step is created. Changing or resetting these eyes keeps the tree at its current scroll position and retains keyboard focus. A child blocked by its parent, isolation or LOD is dimmed; its tooltip explains why. Right-click the object and choose **Reset part visibility** to restore its initialized defaults. Ending isolation preserves the chosen part states. Overrides last for the open map and compatible preview refreshes; changing them stops animation playback, whose own visibility applies when played again.
- **AI Vehicles** and **Pickups** list every difficulty's entries, tagged with the difficulties that place them (`All`, `E/M`, `H`…). Those the shown difficulty doesn't place are dimmed and can't be selected.
- **Pickups** holds three groups: **Weapons**, **Ammo** and **Other** (health and technology), each with its count, Frame and eye. **Group by type** (the toggle on the Pickups row, or its context menu) adds a row per pickup type inside them. A weapon pickup shows the weapon's render from the game manual, ammo its weapon's HUD icon. Pickups are named by type and numbered per type, such as `ERFPG #1` or `Proximity Mine #2`; the game's name (`pu01700`) is in the tooltip.
- An animation icon after a name marks an object a mission animation binds to; at half saturation, an object that receives an animation indirectly (a turret's destruction, a vehicle's startup, a pickup's collection, another animation's call). The tooltip names the animations and why.
- Each row ends with **Frame selection** and an eye. The eye hides the object in the view only (no source change); middle-clicking a world object's eye shows only that object, and again shows everything. **Show all** in the header clears object hiding and isolation; child visibility gates keep their current settings. Use **Reset part visibility** on the object to restore its initialized healthy/destroyed state. The eyes on **Zones**, **AI Vehicles**, **AI Networks** and **Pickups** show or hide everything below them.
- **Collapse all** folds every row. Type in the search box to list matching nodes with their parent paths; every word must match (name, type, pickup kind or game name).
- **Keyboard:** Up/Down, Home/End and Page Up/Down move through the rows; Left/Right reach a row's buttons; **Space** or **Enter** opens a row or presses the button.

MCP: `zstudio_scene_tree`, `zstudio_scene_selection`.

Part visibility is also available through `zstudio_source_map_parts` and `zstudio_source_map_parts_visibility`. Tree-selected parts use the usual Properties controls for supported source edits. Shared-model edits affect every use of the owning source; unsupported part operations are disabled rather than applied to the enclosing object.

## Select and inspect

Click an object in the view or the World tree. The Properties pane shows its card: its icon, name and type (with its source file in the tooltip), and collapsible sections. Clicking a mesh inside a turret or vehicle selects the whole object; the clicked polygon is under **Surface**. Clear the selection with **Escape**, **Clear selection** or a click on empty space. With nothing selected, the pane shows **Recently selected**, the **Player tank** and a summary of the world. **Alt+Enter** focuses the card; it opens the Properties window instead for a terrain piece (its recipe), for a pickup, vehicle, object the startup animations place, AI node or AI path selected in the view, and with nothing selected. When a placement can't be edited, its card's **Placement editing** row says why; a unit selected while hidden becomes editable as soon as it shows again (its eye, the end of an isolation or **Show all**).

MCP: `zstudio_scene_inspect`, `zstudio_scene_card` (`select`, `clear`, `copy`).

## Player tank

The player tank's card (the first vehicle the mission spawns, `bft_00` in the shipped maps) also chooses how the view draws it, marked **Preview**: nothing is saved or undone, and Apply and Revert leave it alone. Its **Master type** and **Weapons** come first; **Details** (the placement, and why it can't be edited where it can't) follows, collapsed until you open it.
- **Master type:** **Tracked**, **Amphibious**, **Hover** or **Sub**. Each shows the end of the mission's own transition from tracked, with the tank parked (no wake, floating or diving). A type the map's tank has no transition for is disabled (shipped: m1 tracked only; m2 adds amphibious; m3 and m4 hover; m5, m6 and the multiplayer maps sub). The note under the radios also gives the game's single-player rules, which allow amphibious from mission 3, hover from 4 and sub in 6 only.
- **Weapons:** **Primary** (RFPG or ERFPG) and **Secondary** (the 16 secondary weapons, by weapon slot). The box under them shows the weapon's render, its manual name and the gun model it draws; several weapons share a model (the remote mines the proximity mines', the missiles and nukes one rail). In sub the beam and fire weapons are disabled, and choosing sub with one selected switches to the weapon the game picks.
- The choice lasts while the map is open (rebuilds, undo, save and difficulty keep it); opening a map starts as a new game of that mission does (Tracked, RFPG and HE Mortar in the shipped maps). While the choice differs from that start, the status bar names it, and with nothing selected the pane's **Player tank** row shows it and selects the tank. A change redraws the view; with a pending edit on the card the map first asks to apply or discard it, and Cancel leaves the tank as it was. A mission whose opening drops the tank in (m2, m4, m5) hides it at the start, as the game does.
- The keyboard: **Tab** enters each group at its chosen radio; the arrow keys, **Home** and **End** move and choose.

MCP: `zstudio_scene_options` (`bftType`, `bftPrimary`, `bftSecondary`), `zstudio_preview_state` (`bft`).

## Edit objects

A selected object's fields are editable at once: **Name** and **Model** where they can change, **Position**, **Rotation (degrees)**, **Scale**, its flags and its **Zone**. The first change starts a draft (**Unsaved changes**); **Apply** accepts every changed field as one undo step, **Revert** discards them. **Escape** in a field restores that field.
- **Handles:** the Transform header's **Move** and **Rotate** toggles show the handles. Move arrows follow the object's own axes; rotation rings turn it about its origin. Drag an X/Y/Z label sideways to change that number (0.1 per pixel, 0.5° for rotation, 0.01 for scale; **Shift** ×10, **Ctrl** ×0.1).
- **Flags:** standable (altitude surface), collision (intersection surface), collide by bounding box, proximity, landmark, overwrite, CanModify (craters and quicksand allowed) and ClipTo (blocked). CanModify and ClipTo exclude each other.
- **Zone:** **Object zone** is **Inherit** (the parent's zone), **Any** or a zone from 0 to 254; the renderer draws the object only while its zone is current. **Altitude zone check** makes the object ground only while its zone is current, which matters where surfaces overlap (floors, tunnels, bridges). A zone several placements or missions share asks before changing all of them. **Polygon zones** lists the zone sets of the object's polygons; paint them with the [polygon zone tool](#zones).
- **Copy…** asks for the copy's name and places the copy beside the original (in place for a light, camera or empty group). **Delete** removes the object. Groups the build removes can only be renamed: right-click the row and choose **Rename…**.
- A world, light or camera lists the script commands that set it (such as `WorldSetFogColor 0.5 0.5 0.5`); edit them there.

Where an edit lands:

| Object | What changes |
| --- | --- |
| A node of the mission database (`mN.gltf`) or its parts | the glTF node; a part's node changes in every copy of the part |
| A node of a model file | that file, for every load of it (the card says so) |
| An object a script placed | the script instruction that set the value (such as `Object3DTranslate 110.0 5.0 -50.0`), or a new one after the instruction that made it |

An edit is refused when it would change several objects at once (an instruction that runs more than once, or one taking its value from a macro), when a script or animation finds the object by a name the edit changes, or when the rebuilt world would make any script instruction act on another node. The refusal says what to change first.

MCP: `zstudio_scene_card` (`set`, `apply`, `cancel`), `zstudio_source_world_object`, `zstudio_source_world_object_edit` (`rename`, `model`, `duplicate`, `delete`; `parent` moves an object to another parent, MCP only), `zstudio_source_world_command`.

## Placements

Click a pickup, an AI vehicle or an AI network node, change its fields or drag its handles, then **Apply**. Pickups have a position and Euler XYZ rotation in degrees; AI vehicles a position and Y heading; AI nodes a position. The edit changes the owning resource: `puppies*.zrd` for pickups, `aiv*.zrd` for vehicles, `aipath\net_NN.zrd` for AI nodes.
- **Difficulties:** the same pickup on other difficulties (same type, original position and rotation) moves with it; a vehicle's counterparts need the same template, position and heading. The card names the difficulties it changes.
- **Precision:** edited positions and AIV headings are saved with one decimal, pickup rotations (radians) with four, as the shipped files write them. Drags snap to that precision, so the view shows what is saved. Values an edit doesn't change keep their text, and an integer coordinate that moves becomes a float (`12.0`).

**Add a pickup:** **Tools → Add pickup…** or the Pickups row's context menu. Choose a type, position, rotation, amount (default: the type's amount; 0 uses the game's default), respawn delay and **All**, **Easy**, **Medium** or **Hard**. The map must already load that type's template. Positions are stored as entered (rounded to one decimal), without terrain snapping. Adding to one difficulty creates its own resource file when it shared another's; once saved, that addition can't be undone.

MCP: `zstudio_pickup_move`, `zstudio_scene_card`, `zstudio_source_pickup_add`, `zstudio_pickups`.

## AI networks

The **AI Networks** section lists the mission's networks (`net_01`…) with their node counts and attack-strategy colour. Show each with its eye (all start hidden). Nodes and links are drawn through geometry, at half strength where geometry hides them; arrows show link directions.

| Stored strategy | Colour |
| --- | --- |
| `HEA` (head-on) | orange |
| `CIR` (circle) | cyan |
| `BAC` (back and forth) | purple |
| `FOL` (follow network) | green |
| `ZIG` (zigzag) | pink |
| `SIT` (sit) | yellow |
| not stored | red (the game attacks head-on) |
| empty or unrecognized | grey |

Select a network to emphasise it and open its card. The card edits the network header with the names of the developers' own Net Edit dialog: **Net Name**, **Net Type**, **Attack Strategy**, **Pursuit Radius Min** and **Max**, **Activation Radius**, **Attack Radius**, **Return Radius**, **Attack Dwell**, **Post Attack Dwell** and **Buddies Attack**; under **No effect in play**, **Buddies Activate**, **Path Width** and **Hide Time 1** and **2**, which the game never uses. The line under each field says what it does in play. **Apply** changes only those values in `data\mN\zrdr\aipath\net_NN.zrd`.

Moving, rotating or scaling several nodes at once uses AI path Edit mode, which only MCP turns on (`zstudio_ai_path`); then click, Shift-click or drag a rectangle to select nodes and press **G**, **R** or **S** with the pointer (**X**, **Y**, **Z** constrain to an axis), or type values; **Enter** confirms one undo step, **Escape** cancels.

MCP: `zstudio_source_ai_network`, `zstudio_ai_networks`, `zstudio_ai_nodes`, `zstudio_ai_selection`, `zstudio_ai_path`.

## Animations

Select a definition under **Animations** in the World tree. The section contains **Objects placed by animations**, followed by the mission and Common definition files. An object's **Animations affecting this object** links open its exact definition and binding. Selection opens Properties and the structural timeline. It does not play the animation, evaluate paths or move the camera.

### One world, one timeline

Animation playback uses the map's existing viewport, LOD and Difficulty controls. The center bottom dock contains **Timeline** and **Diagnostics**; the compact transport remains available when you switch views or collapse the body. **Play** pins the selected binding as the timeline target. Inspecting another definition leaves that target unchanged; choose **Use selection** to stop and retarget explicitly. **Stop** restores the initialized static map at time zero and retains the target. **Exit** also releases the pinned target.

The **Source** view lists sequences and their events in source order. Sequences can run concurrently. Child calls start collapsed and excluded: expanding a call inspects it, **Include in preview** allows that call path to execute, and the guide eye controls only cached paths. Including a child does not include all its descendants. Thus inspecting or playing `start_north` does not automatically launch its entire animation tree. An excluded call with **Wait for completion** blocks its sequence; other sequences can continue. Unavailable and duplicate child starts are labeled separately.

The **Run** view shows what the current scenario actually executed: instances, event occurrences, calls and blocked or incomplete coverage. A filled mark is an observed instant; an interval shows observed activity; open-ended or blocked marks do not promise a finish time. Source timing badges are eligibility thresholds, not a schedule. For example, successive EVENT_OFFSET values of 10, 5 and 5 do not by themselves establish global dispatch times of 10, 15 and 20 seconds. Missing START_TIME means ANIMATION_OFFSET 0. Motion keys share horizontal script tracks. At normal zoom each square represents 1/40 second, darker squares mark event starts and keys, and frame 1 is time zero (frame 41 is one second). Each row labels its own animation, sequence, previous-event or script clock; equal columns across different clocks do not imply simultaneous execution. Existing keys between frame boundaries remain precise and have an exact-position marker. Click a cell to inspect its event fields, key values and read-only source excerpt. Crowded cells list their contents. If current execution evidence identifies a time, clicking also seeks; repeated occurrences offer a choice, and unavailable times are explained without guessing. Motion keys and event order remain read-only.

In **Scenario → Animations on the shared timeline**, add exact animation bindings and choose each start time in seconds or 40 fps frames. The selected animation is the primary; other rows can be removed. Each animation has a **Start animation** marker that can be dragged on the timeline. These settings affect only the preview. Expand each animation to include its called animations; Include permits a call when the script reaches it and does not trigger it immediately. **Fit scenario** shows the latest start plus 30 seconds, within the one-hour limit.

For M4, add `fill_canal` and include its `fill_can1`–`fill_can9` calls. Start the canal at 0 seconds and `destroy_tr_bridge` at 20 seconds to inspect a collapse while the water rises, or at 35 seconds to use the filled canal. Include the bridge calls you want to preview. Splash sequences wait for contact; their Run marks show the resulting timing rather than an invented fixed source keyframe.

The playback range begins at **0–30 seconds**. The initial view uses individual frame squares; zooming out groups cells into an overview while preserving access to their contents. Zooming and the horizontal scrollbar do not change playback range. Drag the ruler/playhead to seek; click an event to inspect it. **Ctrl+wheel** zooms around the pointer, **Shift+wheel** pans, and the wheel scrolls rows. Manual pan/zoom disables **Follow playhead**. **Fit** targets the selection, evaluated events or playback range. Over the timeline, arrows step one 40 fps editing frame, **Shift+arrows** step ten, **Home/End** seek the range boundaries and **Space** plays or pauses; text fields and other native controls keep their normal keys. The selected editing frame remains on the 40 fps ruler; the preview samples on its existing 60 Hz clock and reports actual evaluated seconds. Runtime seeking supports 0–3,600 seconds. **Estimate duration** is explicit; looping, blocked or gameplay-dependent programs may have no known total.

**Scenario** in Properties contains Runtime/Cleanup phase (Runtime by default), preview-only Height (0 by default), seed, reference position and condition settings. Selecting a cleanup record only inspects it. Scenario, child-inclusion, LOD and Difficulty changes stop/reset the run without replaying it. Scheduled starts ignore their activation prerequisites; animations the run starts are checked as in the game, and a refused start is reported with its reason. Range conditions and range-gated animations measure from the player at start (the opening's drop-off on m2–m6 when the opening is not scheduled), or from the reference position when one is set. Waiting sequences still need their authored releases. It is not a full gameplay simulator. The scenario (starts, included calls, condition overrides, phase, Height, seed, reference position) is kept per map in the project's hidden `.zstudio\settings.json` and comes back when the map opens again; its starts, calls and overrides are kept by their source positions, so they follow Apply, undo and redo, and anything an edit removes is dropped and listed in Problems. Choosing another animation still starts a new scenario. Follow camera starts off; direct navigation disengages it, while Stop and Exit restore the free camera. Analysis and seeking are silent; Pause, Stop and Exit silence playback audio.

### Edit source events

**Events** lists existing operands. **Sequence settings** controls whether a sequence waits for a call. **Activation and prerequisites** controls how the definition starts and its required/optional groups. Optional groups retain their minimum-to-satisfy value. Name lists and object paths use one name per line. Unsupported fields remain read-only.

Drag an editable event-start square to propose its timing, or select an event and use **Edit timing** or **F2** in Properties. Linked frame and seconds fields edit the same draft; fractional frames are accepted. The ruler uses that event's native animation, sequence or previous-event clock; dragging proposes 1/40-second increments, while numeric entry preserves supported precision. Valid negative or off-grid source values remain unchanged when merely viewed. Changing the clock mode is an explicit field edit. Global Run marks cannot be dragged to move source events. Whole-sequence START_TIME is labeled ignored, with no timing handle.

Beginning a source draft stops and detaches the run; previous observations become stale. **Apply** accepts the draft as one project undo step; **Revert** restores its fields. Neither resumes the previous run. Source comments and layout remain intact. A shared definition changes every expansion and every map that includes the file; **Bindings** lists affected maps and Apply validates them before accepting the change. Failed validation keeps the draft and leaves the project unchanged. **Save** writes source files, and only **Export** builds game archives. This version edits existing events and drop windows; event insertion/deletion/reordering and keyframe reshaping remain unavailable.

### Evaluate and show paths

**Evaluate paths** explicitly computes motion guides for the pinned target, or the selected candidate before pinning, using the current scenario and included child paths. It runs independently and silently, leaving the main playhead, playback, audio, camera and dock view unchanged. **Show guides** only shows or hides a matching cached result. If none exists, use Evaluate paths; selection or the eye never runs a simulation.

Object paths are teal and camera paths orange. Crosses mark endpoints, arrows show direction and discontinuous transforms break the line. A path represents one bounded scenario; changes to source, binding, baseline, phase, height or inclusion can make it stale. Partial results show their coverage. Analysis is limited to 128 tracks, 216,001 samples and 15 seconds; up to 32 compatible result sets and 432,002 aggregate points are retained. Dependency inspection pages at most 100 references and retains at most 4,096 call paths; excess work is refused explicitly. Runtime has separate limits, including 256 simultaneous instances/effects and 10,000 dispatches per tick. Trace limits can omit older observations, so absence from Run does not prove nonexecution.

Paths and playback share the same scenario and collision simulation. Source-map gravity motion queries active standable surfaces beneath each moving node origin, excluding its bound assembly. Animated surface transforms, active states and morphs participate, so rising water changes contact timing and hidden healthy geometry stops colliding. Viewport visibility does not change collision. Contact releases waiting sequences and impact sounds; later scripted sinking remains possible. Procedural movement stops at world Y −500 or 500, including Height, without an impact cue; keyframes and cameras remain unrestricted. This is a deterministic 60 Hz animation preview with explicit scheduled starts, not a combat, AI or mission-progression simulation.

### VTOL drop windows

Choose **Analyze drop windows** for a recognized flight. This uses a separate named analysis recipe that admits the recognized flight-to-callback-911 callsites listed in Properties. It leaves the main preview's child inclusion unchanged. The result shows one complete flight cycle, start markers for its drop windows and release markers where cargo is released, not where it lands; a release gated by the player's range happens only while the scenario's player (the player at start, or the reference position the scenario sets) is in range, as in the game. Changing the scenario's player, condition switches or rules makes a shown analysis stale; show the drop windows again to analyze them for the new scenario. On a compiled Whole world, MCP `animation_options activationTarget` sets that player. Limited or incomplete analysis supplies diagnostics without editable windows.

To move a window, select it in the list or at its marker, then use **Move along path**, the **Flight sequence clock** move strip or the start-time control in Properties. Review the validated release markers and choose **Apply** or **Revert**. This guarded move changes exactly the CALL's SEQUENCE_OFFSET start scalar. It preserves the paired STOP's EVENT_OFFSET duration and the other windows; there is no resize grip. A definition expanded onto multiple bindings cannot be moved independently by this adapter.

Ordinary **Edit event** remains available for a recognized CALL or STOP through the shared source draft. That operation can change or remove its drop-window relationship; only **Move drop window** provides the window-preservation guarantees. Beginning an event draft makes previous drop analysis stale, and fresh analysis after editing is explicit. Selecting a STOP marker may open its Properties but does not create an endpoint drag.

MCP uses the same source draft, session, structural inspection, transport, scenario, inclusion, analysis and movement services. `zstudio_animation_timeline` handles structural pages, targets, child inclusion, scenarios, rows and separately paged marks. `zstudio_animation_transport` and `zstudio_animation_runtime` control and inspect the shared run. `zstudio_source_animation` handles source fields, and `zstudio_animation_paths` handles paths and the existing drop-window operations. Inspect the current command catalog for typed options and revision identities.

## Zones

The **Zones** section lists the zones the map uses, each with its colour, label and polygon count. Show a zone's colour with its eye (all start hidden); the header's **Objects | Polygons** switch colours objects by their zone or polygons by their zone sets. Select a zone to edit its **Label** (saved in the map's master model), see what uses it, or **Paint with this zone**.

The **Edit zones** toggle in the Zones group (or **Tools → Paint polygon zones** / **Erase polygon zones**, **P** and **E**) starts the polygon zone tool:
- Drag over the map to paint the chosen zone on every polygon you cross, or erase it; each stroke is one undo step.
- **Ctrl** inverts the mode for a stroke, **Shift**+click picks a zone from the polygon under the pointer, **[** and **]** step through the map's zones, **Escape** drops a stroke, then leaves the tool.
- Paint adds the zone after a polygon's others (a polygon keeps at most three); painting Any replaces them, and a numbered zone replaces Any, as in the shipped maps, where a polygon that lists Any lists nothing else. Erasing the last zone leaves none, which differs from Any.
- Only the map's own models (`data\mN\models`) take zones; shared models, vehicles, units and pickups don't. A placement takes its own zones; when they come to differ from the other placements of the same model, the build gives that placement a model of its own and the world is rebuilt in full.
- A stroke covers at most 4,096 polygons on 1,024 placements.

MCP: `zstudio_source_zones`, `zstudio_source_polygon_zones`, `zstudio_source_zone_tool`, `zstudio_source_zone_label`.

## Terrain

Terrain is authored as whole surfaces in glTF plus a recipe beside them (`name.terrain.json`) that holds the gameplay attributes painted on them; the build cuts the surfaces into the pieces the engine needs.
- **Tools → Create terrain…** takes a glTF file of its own and the meshes in it that are terrain, writes the recipe and adds a terrain marker to the mission database.
- Select a terrain piece and choose **Edit terrain…** on its card: the recipe opens in the Properties window, with its defaults, regions (painted areas, in the order they apply) and the brush: **Paint in viewport** or **Erase in viewport**, then drag over the terrain, or **Paint polygon** / **Erase polygon** with typed corners for exact edges. Each stroke is one undo step.
- Attributes: zones, node zone, altitude gate, collision, standable, craters (allowed, blocked or ignored), soil (default, water, seafloor, quicksand, lava, fire), priority and flags.
- Stacked sheets, such as a cave floor under its ceiling, must be separate surfaces: the engine's ground probe takes the first polygon of a node.
- **Tools → Convert to editable terrain…** turns a shipped map's terrain pieces into surfaces and recipes. It lists what it keeps as objects (landmarks, transformed or named pieces, parts) before changing anything, and compares the ground heights before and after. The original piece layout and names are lost; **Undo** takes the conversion back.

MCP: `zstudio_source_terrain`, `zstudio_source_terrain_create`, `zstudio_source_terrain_edit`, `zstudio_source_terrain_convert`.

## Overlays

The **Overlay** group tints matching surfaces at 50%, keeping their textures:

| Button | Shows |
| --- | --- |
| Non-default soils (yellow) | materials with a soil other than default (water, lava, sand…) |
| CanModify (green) | nodes where craters and quicksand can form |
| ClipTo (red) | nodes where they are blocked |
| Landmarks (magenta) | nodes the engine never culls, and what is below them |
| Face orientation | front faces blue, back faces red |

CanModify and ClipTo combine; the others are exclusive, and so are the zone colours of the [Zones](#zones) section. Overlays don't change source data.

MCP: `zstudio_scene_options` (`highlight`), `zstudio_preview_state`.

## Models from other missions

Adding a model to a world is available through MCP only: `zstudio_source_world_add_model` loads any glTF model of the project, such as a vehicle only another mission uses. Not placed, it loads as a template that resources place copies of by name (`ltank_01` places a copy of `ltank`); placed, it goes at a position and heading. It can also add the animation definitions other missions list for it. The edit adds the script lines the shipped scripts use, before the line that writes the world:

```
SetModelDirectory ..\data\common\models
LoadGameGen ltank.flt ltank
```

An addition is refused when a folder the mission searches first holds another file of the same name; rename the model.

`zstudio_source_map_parts` and `zstudio_source_map_parts_visibility` hide or show parts of a placed reference in the view only.

## Undo and save

**Undo**, **Redo** and **Save** in the command bar (**Ctrl+Z**, **Ctrl+Y**, **Ctrl+S**) apply to the whole project ([Save and recover](source-project.md#save-and-recover)). Simple moves update the view in place; larger edits rebuild the world (a card over the view shows **Rebuilding the world…**), keeping the camera and open World tree rows. When a file changes outside zStudio, the map asks to reload before further edits. **Tools → Validate source file on disk** evaluates the map from the saved files, without your unsaved edits.

## Limits

- AI links, new groups and Add model can't be edited in the GUI; positions that startup animations set can't be moved, nor a unit that two records of the shown difficulty place (m2 Easy lists the player tank twice; its AI Vehicles rows say so in their tooltips) or a unit hidden at the mission's start. The player start can't be moved where the mission's opening holds the controls and releases them from the tank's pose (m1–m6): in m2–m6 the opening also sets where the player starts; in m1 the start is the placed pose. It moves in m7–m13.
- Deleting or copying an object an animation names, or changing the model of an object whose parts a bound animation names, isn't refused: the rebuild and the export report it in Problems.
- The view is the mission's starting scene, without simulating gameplay; not every kind of change has been tested in the game.
- The AI overlay draws at most 16,384 nodes and 32,768 links.
- Terrain recipes have no tilted region planes for walls and no texture page cutting.
