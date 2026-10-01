# Source projects

RECOIL's shipped ZBD files are build outputs. The studio originally built them with a gamegen tool from a source tree (`data\` beside a tool folder of `.gs`/`.gw` scripts), repackaging shared textures, models, animations and resources for every mission. zStudio reconstructs that source tree from shipped files, lets you work on the sources, and builds every game file from them again: resource archives, scripts, sounds, interface images, texture packs, worlds and animations.

A source project is separate from editing ZBD files directly. Opening a ZBD file still edits that file. In a source project, the sources are what you edit; game files change only when you export them. Exported files must work in the game; they are not byte-identical to the shipped files.

## Reconstruct, check and export

- **Tools → Reconstruct source project…** asks for the shipped data folder (the folder containing `interp.zbd`, `zrdr.zbd` and `m1\`) and a new or empty destination outside it, then optionally opens the project as the workspace root. MCP: `zstudio_source_reconstruct`.
- **Tools → Check source project** builds every game file in memory and reports failures and warnings without writing. MCP: `zstudio_source_export` without `destination`.
- **Tools → Export all ZBD files…** builds every game file into a folder outside the project. MCP: `zstudio_source_export` with `destination`.
- **Tools → Export ZBD file** lists the game files the project can build; choosing one exports only that file. MCP: `zstudio_source_export` with `outputs`, for example `["m1/zrdr.zbd"]`.
- `zstudio_source_status` lists the game files the project can build and the sources of each, and the project's build profiles.
- **Tools → Build profile** chooses which texture packs exports and checks build (see [Build profiles](#build-profiles)). MCP: `zstudio_source_export` with `profile`.
- **Tools → Open mission world** shows a mission's world as its build script assembles it from the project; its placements, objects, fog and lights are edited there, models are added from any folder of the project, and models round-trip through Blender (see [Mission worlds](#mission-worlds)). MCP: `zstudio_source_world_open` and the commands named there.

The export commands appear when the open folder is a source project, which is any folder with both `data` and `gamegen` subfolders. What it can build is derived from those folders; zStudio's own working data (Blender checkouts, save journals) lives in a separate `zstudio` folder that builds never read (see [The zstudio folder](#the-zstudio-folder)).

When the destination already has some of the selected game files, the GUI asks before replacing them; MCP needs `overwrite`. Without it, a game file that appears in the destination while the export runs is not replaced either. Other files in the destination are left alone, so a game installation can be the destination. All outputs are built, reopened through the shared readers and staged first; if any output fails, nothing is written, and a check reports every output with its failure or warnings. Publication moves replaced files aside and restores them if a later step fails. Unsaved edits to project files, including additions to an open mission world, must be saved or discarded before exporting, because exports read the files on disk.

Exports read every source file once and check that none changed before anything is written; an edit made while an export runs fails the export rather than mixing two states. Opening another folder cancels a running export.

Reconstruction supports RECOIL data and requires RECOIL evidence (prepared scripts, a version-15 world or a version-28 animation program); MechWarrior 3 folders are refused. A canceled or failed reconstruction removes everything it wrote, so the same folder can be used again. Projects and export folders can never be the protected `zbd_1998`/`zbd_1999` corpora, overlap their input (also when spelled through a short name, a SUBST drive or a link above the input), or pass through links. Text sources larger than 16 MiB are refused before they are decoded.

## Mission worlds

In a source project, a mission's world is what its build script (`gamegen\mN.gs`) assembles from the project's sources, so the world editor edits those sources. **Tools → Open mission world** lists the missions with a world script; choosing one builds that mission privately, as the export would (the world, its animations and resources, and a full-quality texture pack), into a temporary folder outside the project, and shows it in Whole world with its mission context. Build problems are listed in Problems under the script's path.

### One workspace per project

All open worlds of a project edit one set of pending changes, the project's workspace:
- **One history.** An edit can change several files, and two missions can share a file, so Undo and Redo work across the whole project, whichever world they are used in.
- **One save.** **Save** writes every changed source file of the project together, and its tooltip lists them. `zstudio_source_changes` lists them too, with the history and a line diff of any file.
- **Rebuilds.** Each edit, undo and redo rebuilds the world it was made in and keeps the camera.
  - An edit the world cannot be built with, or whose rebuild is canceled, is taken back.
  - Until the rebuilt world is shown, the project's other edits, saves and reloads wait (they are disabled, or report that a world is rebuilding).
- **Other open worlds** whose build read a changed file are marked stale. **Reload** rebuilds them.
- **Closing.** Closing one of several open worlds keeps the project's edits. Closing the last one asks whether to save or discard them.
- **Exports** read the files on disk, so pending edits must be saved or discarded first.

### Placements

Unlock editing in Whole world and move pickups, AI vehicles and AI network nodes as in any map:
- The confirmed move changes the coordinate tokens of the text resources the world's archives were built from, for example `data\m1\zrdr\puppies.zrd` and the matching record of `puppies_easy.zrd`.
- Nothing else in those files changes: comments, spacing and other values stay.
- An integer coordinate that moves is written as a float, as in direct editing.

MCP: `zstudio_pickup_lock`, `zstudio_pickup_move`, `zstudio_scene_card`.

### World objects

Every object of the built world knows the source that made it: a node of the mission database (`data\mN\models\mN.gltf`), a node of a model file, or the script instruction that loaded or created it, with the instruction that last set each property.

Properties of an object (or `zstudio_source_world_object`) shows its source, its position, rotation (degrees about Y, then X, then Z) and scale, and its flags:
- standable (altitude surface);
- collision (intersection surface);
- collide by bounding box;
- proximity;
- landmark;
- craters allowed (CanModify);
- no craters (ClipTo).

Editing them (or `zstudio_source_world_object_edit`) changes that source:
- **Database or model nodes:** a database node changes its glTF node's transform or engine flags. A model file's node changes for every load of that file, and Properties says so.
- **Script-placed objects:** the instruction that set the value changes, for example `Object3DTranslate 110.0 5.0 -50.0`. A value no instruction set yet is added after the instruction that created the object.
- **Refused:**
  - an instruction that runs more than once while the world is built, which would change several objects;
  - an instruction that takes its value from a macro.

  Edit those in the script.

**Copy, delete and move to another parent.** Properties of an object offers **Parent** (a node's name), **Copy as** (the copy's name) and **Delete object**; `zstudio_source_world_object_edit` has the actions `duplicate`, `delete` and `parent`. They apply to the whole object: a part of a model a script loaded stands for that load, and Properties names it.

| Object | Copy | Delete | Move to another parent |
| --- | --- | --- | --- |
| A node of the mission database | A copy of the glTF node and its descendants, beside it, sharing its meshes | The glTF node and its descendants leave the file | Moves in the glTF file, under another database node or to the world, keeping its place |
| An object a script loaded | The load is repeated before the world is written: `SetModelDirectory`, `LoadGameGen`, its flag and transform commands, `FindNode` and `AddChild` | Every instruction that loaded, changed or attached it becomes a comment (`# LoadGameGen …`), so the rest of the script runs as before | Its `AddChild` becomes a comment and a new one attaches it before the world is written; its transform changes so it stays in place |

Names are written into scripts and found by name, so a copy needs a name no node has, and a parent found by name must be the only node with it. A template (a loaded model no node holds, which resources place copies of) cannot be copied or moved, and objects other instructions use (a camera's horizon, a world's light) cannot be deleted. Animations and resources that find a deleted object by name no longer find it; Problems lists what the rebuild reports.

**Out-of-date worlds.** When another world's edit or an undo changed a source this world was built from, its node, line and archive references are out of date: edits made from it are refused until it is reloaded. Undo, Add model and Update from Blender export do not depend on them.

Fog, lights and cameras are set by script commands. Properties of the world, a light or a camera lists the commands that set it, for example `WorldSetFogColor 0.5 0.5 0.5`; changing one edits that instruction. `zstudio_source_world_command` sets any of them, adding the command after the instruction that created the node when no instruction set it yet.

### Terrain

Terrain is authored as unsplit surfaces in glTF (from Blender) plus a **terrain recipe** beside them (`name.terrain.json`). The recipe holds the gameplay attributes painted on the surfaces. The build cuts the surfaces into the pieces the engine needs.

- **Creating a terrain.** **Tools → Create terrain…** (or `zstudio_source_terrain_create`) takes a glTF file of the project and the meshes in it that are terrain. The surfaces must be in a file of their own, not in the mission database. It:
  - writes the recipe;
  - adds a marker node to the mission database's roots, whose `extras.recoil.terrain` names the recipe. The pieces take the marker's place in the root order.
- **What the build does** (the splitter):
  1. Cuts each surface exactly at the world's cell lines (`WorldOrigin`, `WorldExtents`, `WorldPartition` must come before the database load).
  2. Cuts along the outlines of painted regions, and gives each part the attributes of the layers covering it: the recipe's defaults, its surface's defaults, then the regions in order.
  3. Groups parts into pieces by surface, cell and node attributes (zone, gate, flags).
  4. Divides any piece that would pass the engine's 921 vertices or normals.

  Every cut point is put into each polygon that shares the edge, so pieces meet without cracks or T-junctions. The same recipe and surfaces always give the same pieces.
- **Attributes:**
  - `zones` (one to three zone numbers, or `any`);
  - `nodeZone` (a number, `any`, or `auto`: the one zone a piece's polygons share, otherwise `any` with the gate on);
  - `nodeGate`, `collision`, `standable`;
  - `craters`: `allowed` (CanModify), `blocked` (ClipTo, "no clip") or `ignored`;
  - `soil`: default, water, seafloor, quicksand, lava, fire, or 6–99;
  - `priority`;
  - `flags`: an exact word of the node flags.

  An attribute a layer does not set comes from the layers before it.
- **Regions** have a name, the surfaces they apply to (all when none are listed) and a shape: polygons with holes in plan view (x, z), optionally limited to a height range. Stacked sheets, such as a cave floor under its ceiling, must be separate surfaces: the engine's altitude probe takes the first polygon of a node.
- **Pieces are build output.** Properties of a terrain piece shows its recipe instead of the piece:
  - recipe and surface defaults;
  - the regions in the order they apply (select, move, delete, add, rename, choose surfaces, set attributes, make one cover its whole surfaces);
  - the **brush**: **Paint in viewport** or **Erase in viewport**, then drag over the terrain.

  Each stroke adds or removes the area a round brush of the given radius covers, as one undoable change, and the world rebuilds. Escape drops a stroke in progress. MCP: `zstudio_source_terrain`, `zstudio_source_terrain_edit`.
- **Viewing.** The Whole world highlight modes show craters allowed (CanModify), no craters (ClipTo), non-default soils and **zones** (each surface in its node zone's colour, grey for any).
- **Convert to editable terrain** (**Tools → Convert to editable terrain…**, MCP `zstudio_source_terrain_convert`) turns a shipped map's hand-cut pieces into terrain:
  - **Which pieces.** The mission database's untransformed mesh roots become surfaces of a recipe beside the database (`mN_terrain.gltf` with `mN_terrain.terrain.json`).
  - **Surfaces.** Pieces with the same node flags and zone share a surface, except where they overlap in plan view: those go to separate surfaces, so stacked sheets stay separate nodes.
  - **What stays.** Every polygon keeps its corners, UVs, normals and material (with its zones and soil), and each surface gets its pieces' exact flags and zone.
  - **Kept as objects:** the horizon and other landmarks, transformed or grouped nodes, references, shared nodes, and any piece a script, resource or animation names or matches by wildcard. The dialog lists them with reasons before anything changes.
  - **Acceptance.** After converting, zStudio compares the altitude probe over the converted area in the world before and after: heights, polygon zones, soils, node flags and zones. On the 1999 data, M1, M5 and M6 give the same results at every sample point.
  - **What is lost:** the original piece layout and names, and possibly how many crater models a crater creates. Undo takes the conversion back.

Not yet:
- tilted region planes for walls;
- zone views and zone probe validation;
- texture page cutting for large painted textures.

### Models from other missions

**Add model** (in the Whole world toolbar, or **Tools → Add model to world…**) loads any glTF model of the project into the world, for example a vehicle that only another mission used:

- **Name.** Choose the model and its node name (the model's name by default).
  - Resources and animations find the model by this name; the dialog says when the world already has a node with it.
  - `AddChild` attaches the newest node with the name, so a placed model cannot use a name one of its own nodes has (the root node of `vtol.gltf` is `vtol`). Such an addition is taken back with a request to choose another name.
- **Not placed** loads the model as a root outside the world, as the shipped scripts load vehicle templates: resources such as `aiv.zrd` place copies of it by name (`ltank_01` places a copy of `ltank`). **Placed in the world** puts it at a position and heading (the orbit point by default).
- **Animations** lists the definition files other missions list with an animation for that name, such as `data\common\zrdr\enemies\ltank.zrd` for `ltank`. Checked files are added to the mission's `data\mN\zrdr\anim.zrd`, keeping its comments and layout.

The edit adds the lines the shipped scripts use to load a model, before the line that writes the world:

```
SetModelDirectory ..\data\m2\models\bft
LoadGameGen ltank.flt ltank
```

and for a placed model its transform and parent:

```
Object3DTranslate 3161.9 33.0 2593.2
Object3DRotate 0.0 -90.0 0.0
FindNode %worldName%
AddChild ltank_wreck
```

Exports of the mission then include the model's geometry and materials, every texture it uses in each of the mission's packs, and its animations.

### Editing models in Blender

The round trip uses only files:

1. **Check out.** **Tools → Edit in Blender…** (or `zstudio_source_blender_checkout`) copies the model of the selected object into the project's `zstudio\export\<checkout>\input` folder: its glTF, buffer and textures, as the workspace holds them.
2. **Edit and export.** Import that glTF in Blender, edit it, and export it with **glTF 2.0**, format **glTF Separate (.gltf + .bin + textures)**, with **Custom Properties** on, into the checkout's `outbox` folder.
3. **Update.** **Tools → Update from Blender export…** (or `zstudio_source_blender_update`) applies the newest export after confirmation.
   - The export is sealed: copied into `sealed\` while checking that Blender finished writing it.
   - It is read as a build reads models, and becomes one undoable change: the model's glTF and buffer, and every texture PNG that is new or changed.
   - A changed texture changes for every model that uses it, and a node the export no longer has (animations and placements find nodes by name) is reported in Problems.
   - Embedded textures (Blender's default `.glb`) are refused, because the engine needs PNG files.

Nothing Blender writes reaches the project until the update is applied, and nothing reaches the disk until **Save**.

### Saving and recovery

**Save** writes the changed files as one recoverable publication:
1. Each file is staged and verified in `zstudio\staging`.
2. A journal in `zstudio\recovery` records what the save replaces.
3. Each original is moved aside and its replacement put in place, unless another program changed or created the file meanwhile.
4. The journal is removed.

If anything fails, the files already replaced are put back. A file changed by another program since the workspace read it is never overwritten: the save stops and says which file.

If zStudio or the computer stops in the middle of a save, opening the project reports the interrupted save (in Problems, and in a dialog). It offers:
- **Roll back**, which restores the files as they were before the save;
- **Complete**, which finishes the save;
- **Keep files**, which leaves them as they are and moves the journal to `zstudio\recovery\abandoned`.

A file another program changed since is left alone. Until the decision, the project cannot be saved. MCP: `zstudio_source_recovery`, `zstudio_source_recovery_resolve`.

### The zstudio folder

zStudio keeps its working data in the project's `zstudio\` folder:
- `export\` holds Blender checkouts;
- `recovery\` holds save journals;
- `staging\` holds files being prepared.

Builds never read it. Leave it out when sharing a project. It is safe to delete when no save was interrupted and no Blender edit is pending.

## Build profiles

A build profile says which mission texture packs an export builds, with what texel budget and largest texture side. The game opens `rtexture<N>.zbd` for the texture memory its card reports (in MB), counting down, so the packs decide what quality each machine gets.

| Profile | Packs | Status |
| --- | --- | --- |
| `original` | What the game shipped: `rtexture2`, `rtexture4` (256-texel textures), `texture2`, `texture4`, `texture6` | Measured |
| `modern` (default) | `original` plus `rtexture8` and `rtexture16` (up to 1024 texels), `texture8` and `texturemax` | Experimental until measured in the game |

A project can add or replace profiles with files in `gamegen\build-profiles\` (builds never read them otherwise); the file name is the profile's name, and one may be the default:

```json
{
  "format": "recoil-build-profile",
  "version": 1,
  "status": "experimental",
  "default": true,
  "description": "Larger textures for modern cards.",
  "texturePacks": [
    { "file": "rtexture2.zbd", "budgetMiB": 2, "maximumDimension": 256 },
    { "file": "rtexture32.zbd", "budgetMiB": 32, "maximumDimension": 2048 },
    { "file": "texture6.zbd" }
  ]
}
```

- `budgetMiB` is the pack's texel budget (textures shrink to fit it); `null` keeps every texture at full size. Omitted values come from the pack's name, as before.
- `maximumDimension` is a power of two from 8 to 4096.
- A profile lists at least one `rtexture` pack, since the Direct3D renderer reads only those.
- Export results and `zstudio_source_export` name the profile used. When the destination already holds a larger `rtexture` pack the profile does not build, the export warns: the game would load that pack instead.

## Layout

```
<project>\
  gamegen\                     the original tool folder: *.gs and support\*.gw, recovered from interp.zbd
  data\                        the original source tree
    common\zrdr\{enemies,explosns,lighting,vtol,weapons}\*.zrd
    common\multi_bft\{zrdr,model,textures}\
    common\{models,textures}\  common\effects\{models,textures}\  effects\{models,textures}\
    common\sounds\*.wav         common\{fonts,images}\*.png
    mN\models\*.gltf/.bin       mN\models\bft\     (the mission database mN.gltf and its loads)
    mN\textures\*.png           mN\textures\bft\
    mN\zrdr\{aipath,envmodels,vtol,bft,choppers,…}\*.zrd, *.zan
    mN\images\*.png             (objective images)
```

Directory placement is recovered from evidence in the shipped files; [recoil-original-worktree.md](recoil-original-worktree.md) reconstructs the complete original tree from it. Every ZAR member records the temporary file its compiler created in the source directory (for example `D:\battlesportdev\data\m1\zrdr\envmodels\fueE3B0.TMP`), so each resource returns to its original folder, including subfolders that member names do not carry. Sound banks carry no source paths; their WAVs go to `data\common\sounds`, the `SOUND_PATH` set by `sounds.zrd`. The prepared-script index names each script (`support\common.gw`, `m1.gs`) and its modification time, which the reconstructed file keeps.

Exported archives record each member's project path (`data\m1\zrdr\envmodels\fuel.zrd`) where the original compiler recorded its temporary file, so reconstructing exported files restores the same tree.

## What is built

| Game file | Sources | Build |
| --- | --- | --- |
| `zrdr.zbd` | `.zrd` files in every `zrdr` folder under `data\common` | compiled, ordered by source path |
| `mN\zrdr.zbd` | `.zrd` files under `data\mN\zrdr` | compiled, ordered by source path |
| `interp.zbd` | `gamegen\*.gs`, `gamegen\support\*.gw` | tokenized; each script records its file's modification time |
| `soundsh.zbd`, `soundsm.zbd`, `soundsl.zbd` | `data\common\sounds\*.wav` | converted to the HIGH/MED/LOW formats of `sounds.zrd` |
| `image.zbd` | `data\common\fonts`, `data\common\images`, `data\mN\images` PNGs | interface images at their authored size in direct colour |
| `mN\rtexture{2,4,8,16}.zbd`, `mN\texture{2,4,6,8,max}.zbd` | the mission's texture folders, and every texture its world uses from elsewhere | each PNG scaled and converted to the pack's budget and colour mode |
| `mN\gamez.zbd` | `gamegen\mN.gs` and the scripts it sources, glTF models, texture names | the build script run with the engine's interpreter rules |
| `mN\anim.zbd` | `data\mN\zrdr\anim.zrd`, the definition files it lists and their `.zan` scripts | compiled against the world this export builds |

Archive members are found by name, so two sources with the same file name in one archive are refused. A `.zrd` source may be text or compiled data.

Each sound's source is its best-quality version across the three shipped banks. `sounds.zrd` declares a rate, sample size and channel count for each bank; the declaration is a ceiling. As in every retail bank, each of the three values is the lower of the source's and the declaration's, so a sound is never raised in quality. Conversion mixes channels, reduces sample size and resamples with a windowed-sinc low-pass filter; cue markers, which the engine turns into playback times, move with the samples. A WAV that `sounds.zrd` does not declare goes into every bank unchanged, with a warning. Only 8- and 16-bit PCM can be converted.

On the 1998 and 1999 retail data, reconstructing, exporting and reconstructing again gives the same tree. The exported archives contain the same members with identical compiled data (apart from the rebuilt animation definitions), `interp.zbd` the same scripts and tokens, `soundsh.zbd` the shipped sounds unchanged, the medium/low banks the shipped formats with the same frame counts (within two frames) and cues, every world the shipped nodes, placements, models and textures, and every `anim.zbd` the shipped entries.

Reconstruction lists shipped files whose family it does not reconstruct. They are not copied into the project.

### Textures

Each texture becomes one PNG from its best-quality stored variant (the largest, preferring direct colour at equal size) in the folder the mission packs' record order places it: `data\effects\textures`, `data\common\textures`, the mission's `textures` and `textures\bft`, or `data\common\multi_bft\textures` for multiplayer missions. Packs are built from the PNGs on export: hardware packs (`rtexture`) as RGB565 with an alpha plane, software packs (`texture`) with shared palettes, and damage masks and player-vehicle skins in direct colour. Besides the shipped 2 and 4 MB packs, exports add `rtexture8`/`rtexture16` and `texture8`/`texturemax`, which the engine loads when the card or the TextureMemory setting allows, so modern cards get every texture at full quality. A texture's edge mode (clamp or wrap) comes from the glTF samplers that use it.

### Worlds and models

A mission world is built by running its script, `gamegen\mN.gs`, as the original build did: the interpreter follows `source`, macros and `ifdef`/`ifndef` (a condition holds when its macro is exactly `TRUE`, conditions combine with `||` and `&&`, and while a block is skipped the first `endif` ends it, as in the retail interpreter), and applies the world, camera, light and node commands with the retail engine's semantics. `LoadGameGen name.flt node` loads `name.gltf` (or `.glb`) from the model directories the scripts set with `SetModelDirectory`, newest first; the file after `GameGenSetWorld` is the mission database, whose top-level nodes join the world. When the script writes the world (`GameZWriteZBDFile`), zStudio runs the engine's update (matrices, bounds, grid partition, single-parent flags) and writes version-15 GameZ.

Models are glTF 2.0 with PNG textures, editable in Blender. Textures are separate PNG files (an image embedded in the file, as in Blender's default `.glb`, is reported and the surface stays untextured). Engine attributes a glTF cannot express live in `extras.recoil`, which Blender keeps as custom properties:

- nodes: flags that differ from the loader's default, zone, LOD ranges, the `name` when a name repeats or looks like a Blender copy (`.001` suffixes are otherwise ignored), `ref` for an OpenFlight external reference (another glTF file, loaded under the node), `instance` for a node shared by several parents (glTF nodes have one parent, so each copy carries the same number and import joins them; the first copy is used), and `model` for a model without polygons (lens-flare points), which a glTF mesh cannot hold;
- meshes: display mode and flags, texture scrolling, morph factor, and point entries (lens flares);
- materials: polygon priority, back faces, zone word, colour, soil and the engine's material flags; textures by name; `normals` when its polygons store normals (Blender writes normals for every surface, so a material without it stays flat);
- primitives: `polygons`, where joining triangles back into the stored polygons would not restore them (a count of fan triangles per polygon, or a corner list);
- the scene: `rootFlags`, the flags of the node a script load creates.

Reconstruction replays each mission's scripts against its shipped world and undoes their edits (attaching, renaming, rearranging) in reverse, so each load's root holds exactly its file's scene. Files go where the original tree had them as far as the scripts show ([recoil-original-worktree.md](recoil-original-worktree.md)): a file whose script chose its folder (`SetModelDirectory`, as `bftN.gw`, `bftmulti.gw` and `weapons.gw` do) goes there; a file a script run by every mission loads identically (the pickups of `pickup.gw`) goes to `data\common\models`; a mission database and the files a mission's own scripts load go to that mission's `models` folder, one copy per mission; a load that finds an identical file in a folder searched earlier uses it. External references (models named inside other models) are written beside every file that references them, so two missions sharing one keep a copy each, as they keep their shared textures. Every folder the scripts search is created, including `data\common\effects\models`, `data\effects\textures` and the vehicle folders of the multiplayer missions, which no shipped file comes from. Polygons keep their authored corners and UVs as the original build stored them (repeated corners, non-planar polygons and UVs are kept; only the tile shift and 1/256 quantization apply).

A world assembled from the reconstructed sources has the shipped nodes, placements, flags, grid cells, models and textures for every 1998 and 1999 mission. A mission's loads of the same file share its models, so it can have fewer model and material records than the shipped file.

**Models from other missions.** A mission can load any model in the project: add a `SetModelDirectory` for the other mission's folder and a `LoadGameGen` to a mission script, or use **Add model** in the mission's world ([Mission worlds](#mission-worlds)). The export then includes the model's geometry and materials in the world, and every texture it uses in the mission's packs, even when the texture lives in another mission's folder; the mission's own folders win for a name both have, as the engine finds the first match.

### Animations

`anim.zbd` is compiled from the mission's `data\mN\zrdr\anim.zrd`, the definition files it lists (a path relative to the gamegen folder, or a bare name found beside the listing file or in `ANIMATION_PATH`; a missing file is skipped with a warning) and the keyframe scripts named by `OBJECT_MOTION_SI_SCRIPT`. Definitions bind to the world this export builds: a `NAME` listing several roots binds to the first the world has, a name with `*` (one digit each) expands to every matching node in name order, and a definition whose root the world lacks is left out, as the shipped files show. A node name the world cannot resolve, or an effect that no `effects.zrd` of the project defines (names match exactly), would make the game reject the whole file, so the export reports it. Values that do not fit their stored field (such as an `EXECUTION_PRIORITY` above 255 or a `LOOP_COUNT` above 65535) and more than 32,767 entries, the most the game reads, are errors. The compiled file carries no source stamps, because the game rejects `anim.zbd` when a stamped source exists with a different time.

Every entry of every shipped `anim.zbd` recompiles from its sources to the same fields. Seven shipped definitions had changed after the animations were compiled (for example `bft_to_5cav` in `m13\zrdr\envmodels\bft_trans.zrd` moves the vehicle 55 units down, where the shipped animation moves it 47); reconstruction rebuilds those from the compiled entries and says so in its notes, so the project reproduces the animations the game shipped. Their archive members differ from the shipped ones, which the engine does not read at run time.

### Known differences

- The original build reused node slots it freed while loading, which exported worlds do not reproduce. The game binds consecutive animations with the same root name to same-named nodes in slot order, so an animation can bind to a different one of two same-named nodes (for example `smoke1` in most missions). Nothing else depends on slot order.
- Shipped name fields keep residue after the terminator; exported files have their own residue.
- `m9\gamez.zbd` names a texture, `surf00`, that no shipped pack holds; the game shows its default texture for it, and so does the exported world.

## Editing sources

Text `.zrd` files open in the shared ZRD viewer and editor (tree, Properties, `zrd_nodes`, `zrd_edit`) and save as text that changes only where the edit does: comments, spacing and the spelling of untouched values stay; new records are written in the canonical layout. A file the project's workspace holds unsaved edits of cannot be edited there until those are saved or undone, and the other way round. Importing a text `.zrd` into an archive compiles it. `.gs`/`.gw` scripts open read-only as token text; edit them in any text editor. WAV sources can be replaced with any 8- or 16-bit PCM file, PNG textures with any PNG, and glTF models with files exported from Blender (keep the custom properties).

## Text formats

### zReader resources (`.zrd`)

The original compiler's text syntax did not survive, and the retail engine only reads compiled data, so zStudio defines a lossless syntax. A file lists the children of its root array.

- `( … )` is an array. A key followed by its value array is written on one line: `GRAVITY ( -9.8 )`.
- Integers are decimal 32-bit values: `42`, `-7`.
- Floats always contain a decimal point or exponent (`2.0`, `-9.8`, `1E-45`). NaN payloads and infinities use raw bits: `f32:7FC00001`. Values round-trip bit-exactly, including `-0.0` and subnormals. The engine's `GetInt` rejects floats, so the distinction matters.
- Strings are bare when they start with a letter or `_` and contain only letters, digits, `_`, `.` and `-`; otherwise they are quoted with `\"`, `\\` and `\xNN` escapes. Files are written as ASCII and read as Latin-1.
- `#` starts a comment outside strings.

### Keyframe scripts (`.zan`)

The original scripts were exported from Softimage and did not survive; zStudio's format lists a track per object, and a definition's `NAME` picks the track:

```
OBJECT copter01
FRAME 0 POSITION 1429.22 56.43 3107.5 VELOCITY 35.18 -8.75 -45.85 ROTATION 0.929 -0.128 -0.319 0.136 SPIN -0.003 0.130 0.020 SCALE 1 1 1 GROWTH 0 0 0
FRAME 5 POSITION 1440.95 53.52 3092.22 ROTATION 0.941 -0.121 -0.279 0.148
FRAME 2599
```

A key's channels start a segment that runs to the next key; the last key's frame ends the track. Frames count at the definition's `SCRIPT_FRAME_RATE` and may go back (the engine plays reversed segments as authored). POSITION and SCALE are XYZ, ROTATION a quaternion W X Y Z (not zero). A channel's rate may follow it: VELOCITY and GROWTH per second, SPIN as a rotation vector (half-angle radians per second). Without a rate, the channel moves to its value at the next key that lists it; two keys at the same frame are a cut, which jumps to the second value. Reconstructed scripts list every rate, so they compile to the shipped keyframes exactly. A file without OBJECT lines is one track any node may use.

### Gamegen scripts (`.gs`, `.gw`)

Scripts use the engine's own tokenizer (`CZInterp::TokenizeLine`, retail 0x4C13C0): `#` ends a line, tokens are separated by comma, space, tab or newline, and ASCII whitespace after a separator is skipped. Only a separator directly after another produces an empty token, so zStudio writes empty tokens with commas (`a,,b`; a trailing empty token needs `a,,`).

## Keep sources away from a retail install

Do not place a source tree inside a game folder. Loose `support\*.gw`/`*.gs` files override `interp.zbd`, and loose `*_easy`/`*_hard` resources change difficulty selection. (The shipped `anim.zbd` files are rejected when a source they stamp exists with a different time; exported ones carry no stamps.) Export into the game folder (or a separate folder) instead.
