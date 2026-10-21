# Map editor

Open a [source project](source-project.md), then a map. You edit the map's sources directly: objects, placements, AI networks, VTOL drop windows, zones and terrain. Every change is one undo step of the project; **Save** writes the changed source files, and only [export](source-project.md#check-and-export) builds game files. Camera controls are in [3D navigation](3d-navigation.md).

## Open a map

The map picker lists the single-player maps (m1–m6) and the multiplayer maps; one map is open at a time. A map whose model or script is broken still has its button; opening it reports the problem. The view shows the mission's starting scene at the chosen **Difficulty** (Easy, Medium or Hard): the units and pickups that difficulty places and the poses its startup animations set. The back arrow left of **Whole world**, or **File → Close file** (**Ctrl+W**), returns to the picker, asking to save or discard unsaved changes.

MCP: `zstudio_source_world_list`, `zstudio_source_world_open`.

## Layout

- **Title:** the map's name, with an **Edited** pill while the project has unsaved changes.
- **Command bar:** the scope (back arrow, **Whole world** and the world's source model file, such as `m1.gltf`); **Save**, **Undo**, **Redo**; **LOD** (LOD 0 is each object's highest detail) and **Difficulty**; then **Camera** (Frame all, Flycam), **Display** (Wireframe, Textures, Bounds, Horizon), **Overlay** (Non-default soils, CanModify, ClipTo, Landmarks, Face orientation), **Show** (VTOL route) and **Zones** (Edit zones).
- **World tree** (left), the view (middle) and the **Properties** pane (right). Drag the gaps between them to resize.
- **Over the view:** the **Perspective** chip (projection, named views, framing), the navigation cube, the selected object with its handles, and the surface under the pointer with its X/Y/Z.
- **Status bar:** the selection, the mission start and difficulty, instance and mesh batch counts, **Preview notices**, and toggles for the **World tree**, **Properties** and **Diagnostics** panes. Diagnostics lists the map's file and preview problems.

### World tree

Rows come in a fixed order: **World** (the authored glTF hierarchy, including groups the build removes), **Zones**, **AI Vehicles**, **AI Networks**, **Pickups**, **Animations (keyframed)** (objects the startup animations place) and **Animations (definitions)** (the `.zad` files the mission lists). Icons show each node's type; the info button beside **Show all** opens a legend.
- Objects show their zone on the right (`Zone: 12`, `Zone: Any`).
- **AI Vehicles** and **Pickups** list every difficulty's entries, tagged with the difficulties that place them (`All`, `E/M`, `H`…). Those the shown difficulty doesn't place are dimmed and can't be selected.
- **Pickups** holds three groups: **Weapons**, **Ammo** and **Other** (health and technology), each with its count, Frame and eye. **Group by type** (the toggle on the Pickups row, or its context menu) adds a row per pickup type inside them. A weapon pickup shows the weapon's render from the game manual, ammo its weapon's HUD icon. Pickups are named by type and numbered per type, such as `ERFPG #1` or `Proximity Mine #2`; the game's name (`pu01700`) is in the tooltip.
- An animation icon after a name marks an object a mission animation binds to; at half saturation, an object that receives an animation indirectly (a turret's destruction, a vehicle's startup, a pickup's collection, another animation's call). The tooltip names the animations and why.
- Each row ends with **Frame selection** and an eye. The eye hides the object in the view only (no source change); middle-clicking a world object's eye shows only that object, and again shows everything. **Show all** in the header shows every hidden object. The eyes on **Zones**, **AI Vehicles**, **AI Networks** and **Pickups** show or hide everything below them.
- **Collapse all** folds every row. Type in the search box to list matching nodes with their parent paths; every word must match (name, type, pickup kind or game name).
- **Keyboard:** Up/Down, Home/End and Page Up/Down move through the rows; Left/Right reach a row's buttons; **Space** or **Enter** opens a row or presses the button.

MCP: `zstudio_scene_tree`, `zstudio_scene_selection`.

## Select and inspect

Click an object in the view or the World tree. The Properties pane shows its card: its icon, name and type (with its source file in the tooltip), and collapsible sections. Clicking a mesh inside a turret or vehicle selects the whole object; the clicked polygon is under **Surface**. Clear the selection with **Escape**, **Clear selection** or a click on empty space. With nothing selected, the pane shows **Recently selected** and a summary of the world. **Alt+Enter** focuses the card; it opens the Properties window instead for a terrain piece (its recipe), for a pickup, vehicle, object the startup animations place, AI node or AI path selected in the view, and with nothing selected.

MCP: `zstudio_scene_inspect`, `zstudio_scene_card` (`select`, `clear`, `copy`).

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

## VTOL routes

**Show → VTOL route** draws a VTOL flight and its drop windows: the blue line is one flight cycle, start markers show the drop windows and release markers the positions where the VTOL can release its cargo (not where the cargo lands). Choose the flight in the selector.

To move a drop window, select it in the list or at its marker, choose **Move along path** (follow the pointer along the path) or **Set start time** (seconds into the cycle), check the release markers, then **Confirm** (one undo step) or **Cancel**. The edit changes the window's `START_TIME` in the owning `.zad` definition and nothing else; a definition other entries share can't be moved this way. Reopen **VTOL route** after the world rebuilds to continue.

MCP: `zstudio_parachute`.

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

- AI links, new groups and Add model can't be edited in the GUI; positions that startup animations set can't be moved.
- Deleting or copying an object an animation names, or changing the model of an object whose parts a bound animation names, isn't refused: the rebuild and the export report it in Problems.
- The view is the mission's starting scene, without simulating gameplay; not every kind of change has been tested in the game.
- The AI overlay draws at most 16,384 nodes and 32,768 links.
- Terrain recipes have no tilted region planes for walls and no texture page cutting.
