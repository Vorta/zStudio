# Source projects

RECOIL's shipped ZBD files are build outputs: the developers built them with a gamegen tool from a source tree (`data\` beside a folder of `.gs`/`.gw` scripts). zStudio reconstructs that source tree from the shipped files once, lets you edit the sources, and builds every game file from them again: resource archives, scripts, sounds, interface images, texture packs, worlds and animations. The sources are what you edit; game files change only when you export them. Exported files work in the game; they are not byte-identical to the shipped ones.

Maps are edited in the [map editor](map-editor.md); [compiled files](compiled-files.md) covers inspecting game files.

## Initialize a project

1. On the welcome screen, under **Work with source project**, choose **Initialize**.
2. Enter the game's zbd folder (the one holding `image.zbd` and the mission folders `m1`, `m2`, …) as the **Retail ZBD folder** and a new or empty project folder, or use **Browse…**.
3. Choose **Initialize and open**. The dialog shows progress and, if a folder is refused, the reason. **Cancel** stops the reconstruction; once the project is written, it leaves the project unopened.

Initialize accepts original RECOIL game files whose resource archives carry the animation definitions every `anim.zbd` was compiled from: the 1998 and 1999 releases and the September 1998 demo. Any other folder, including a zStudio export, is refused with "zStudio can unpack only the original ZBD files." MechWarrior 3 folders and the July and August 1998 demos are refused too. **Keep an untouched copy of the original files**: a folder an export replaced can't be reconstructed again. Only the files in the chosen folder and its mission folders are read. The project folder must be outside the game folder, must not overlap it and must not be or lie below a link. Don't change the game files while it runs. A canceled or failed reconstruction removes what it wrote, so the folder can be used again.

MCP: `zstudio_source_reconstruct`.

## Open a project

Choose **Open** under **Work with source project**, or **File → Open workspace…** (**Ctrl+O**), **File → Recent folders**, or drop the project folder on the window. A source project is a folder with both `data` and `gamegen`; any other folder is refused with what is wrong with it.

Opening a project shows the map picker ([Open a map](map-editor.md#open-a-map)).

MCP: `zstudio_open_root` with `project`, `zstudio_source_world_list`, `zstudio_source_world_open`.

## Project layout

```
<project>\
  gamegen\              the build scripts: *.gs and support\*.gw
  data\
    common\zrdr\…       shared resources (.zrd), in their recorded folders
    common\models\      shared models: vehicles several missions load, turrets, pickups
    common\textures\    common\effects\{models,textures}\   common\multi_bft\…
    common\sounds\      every sound, as its best-quality WAV
    common\{fonts,images}\   interface images (PNG)
    mN\models\          the mission database mN.gltf and the models its scripts load (bft\ for its vehicles)
    mN\textures\        the mission's textures (PNG)
    mN\zrdr\            the mission's resources (.zrd), animation definitions (.zad) and keyframe scripts (.zan)
    mN\images\          objective images
  .zstudio\             zStudio's working data (below)
```

Every file goes where the original tree had it, as far as the shipped files record it. Animation definitions are `.zad` files: each mission's list `data\mN\zrdr\anim.zad` and the files it names. `pickup.zrd` keeps its pickup data; its pickup animation is `pickup.zad`.

| Game file | Built from |
| --- | --- |
| `zrdr.zbd`, `mN\zrdr.zbd` | the `.zrd` files in the common or mission `zrdr` folders, compiled |
| `interp.zbd` | `gamegen\*.gs` and `gamegen\support\*.gw`, tokenized |
| `soundsh.zbd`, `soundsm.zbd`, `soundsl.zbd` | `data\common\sounds\*.wav`, converted to the formats `sounds.zrd` declares |
| `image.zbd` | the interface PNGs |
| `mN\rtexture*.zbd`, `mN\texture*.zbd` | the mission's textures and every texture its world uses, per the [build profile](#build-profiles) |
| `mN\gamez.zbd` | `gamegen\mN.gs` and the scripts it sources, run with the engine's rules, and the glTF models |
| `mN\anim.zbd` | `data\mN\zrdr\anim.zad`, the definitions it lists and their keyframe scripts |

## The .zstudio folder

zStudio writes into a project only its hidden `.zstudio\` folder beside `data` and `gamegen`:
- `settings.json`: how the project was last viewed (Diagnostics shown or closed and its height, the mission **Difficulty**, Animation mode's panel height and Diagnostics strip), each map's animation preview scenario and the project's [texture dithering](#texture-dithering);
- `export\`: Blender checkouts and their exports;
- `recovery\` and `staging\`: save journals and files being saved.

Builds never read it. Leave it out when you share a project. You can delete it when no save was interrupted and no Blender checkout is still needed; the remembered view and dithering go with it.

## Save and recover

The open map edits the project's one workspace: Undo and Redo apply to the project's source changes, and **Save** (**Ctrl+S**) writes every changed source file together; its tooltip lists them. A save stages and verifies each file in `.zstudio\staging`, records what it replaces in a journal, puts the files in place and removes the journal. If anything fails, the files already replaced are put back. A file another program changed since zStudio read it is never overwritten: the save stops and names it.

If zStudio or the computer stops during a save, opening the project reports the interrupted save and offers **Roll back** (the files as they were before), **Complete** (finish the save), **Keep files** (leave them, and move the journal aside) or **Later** (decide later; the project can't be saved meanwhile).

**Reload** (**F5**) reads the project again; a file with both outside changes and unsaved edits asks to save or discard first.

MCP: `zstudio_save_document`, `zstudio_undo_redo`, `zstudio_source_changes`, `zstudio_reload_document`, `zstudio_source_recovery`, `zstudio_source_recovery_resolve`.

## Check and export

The **Export** menu builds game files from the project's files on disk, so save or discard unsaved changes first.
- **Check source project** builds everything in memory and reports failures and warnings without writing.
- **Export all ZBD files…** builds every game file into a folder outside the project: an empty folder, or the zbd folder of a copy of the game.
- **Export ZBD files…** opens a checkbox window with all buildable files, grouped into shared files and numbered missions. Select individual files, use a mission's checkbox, or choose **Select all** / **Clear all**. **Export selected…** asks for the destination and builds only the checked files. All files start checked; the latest choices are remembered across projects and restarts by relative output path, even when you cancel or close the window. Newly encountered files start checked.

A progress window shows the file being built; **Cancel** or **Escape** stops it. Every output is built, read back and staged first; if any fails, nothing is written. When the destination already holds some of the game files, one confirmation lists them: **Yes** replaces them, **No** cancels the export. Other files in the destination stay. A texture pack there that the export doesn't build (such as an `rtexture8.zbd` the profile leaves out) stays too, and the game may load it instead; the export warns about it. An export canceled while it replaces files finishes or restores them. Replaced original files are gone for good.

Check and export also list in Problems each node the game finds by name as a mission loads (animation roots, texture-effect `FindNode`), with the node it finds. Where several nodes share a name the game takes the one made last; an export that changes which node a lookup finds warns about it.

MCP: `zstudio_source_export` (without `destination` it checks; `outputs` selects files; `overwrite` allows replacing), `zstudio_source_status`, and `zstudio_source_export_selection` (read or change the remembered checkboxes without exporting).

## Build profiles

**Export → Build profile** chooses which texture packs exports and checks build. The game opens the `rtexture<N>.zbd` pack that fits the texture memory the graphics card reports, so the packs decide each machine's texture quality.

| Profile | Packs |
| --- | --- |
| `original` | what each mission shipped with: `rtexture2`, `rtexture4`, `texture2`, `texture4`, and `texture6`/`texture8` where the mission had them |
| `modern` (default) | `rtexture2`, `rtexture4`, `texture2`, `texture4` and `texture6` in every mission, plus `rtexture8`, `rtexture16` (up to 1024 texels), `texture8`, `texturemax`, and an automatic `rtexture<N>` (up to 256 MB) that holds every texture at full size when a mission outgrows `rtexture16` |

`texture*` packs serve only the software renderer; Direct3D loads `rtexture*` packs. A project can add or replace profiles with JSON files in `gamegen\build-profiles\` (the file name is the profile name):

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
    { "file": "texture6.zbd" },
    { "file": "texture8.zbd", "missions": ["m6"] }
  ]
}
```

- `budgetMiB` is the pack's texel budget (textures shrink to fit); `null` keeps every texture at full size; omitted values come from the pack's name.
- `"file": "rtexture*.zbd"` is the automatic pack; its `budgetMiB` (1–1024, default 256) is the largest it makes.
- `maximumDimension` is a power of two from 8 to 4096, at most 1024 for software packs.
- `missions` limits a pack to some missions.
- Every profile needs an `rtexture` pack for every mission. A file that breaks a rule is shown with its reason in the menu and fails the exports that use it; a file that isn't valid JSON, or a broken or second default, can stop every export and check until you fix it. The message names the file.

MCP: `zstudio_source_profile`; `zstudio_source_export` with `profile` overrides it for one export.

## Texture dithering

The packs store 16-bit colour. **Export → Texture dithering** chooses how exports and checks convert each PNG's 8-bit channels:

| Choice | Result |
| --- | --- |
| **None** (default) | each texel rounds to the nearest stored colour; smooth gradients can band |
| **Ordered** | a fixed 4×4 pattern; textures that tile stay seamless |
| **Error diffusion** | Floyd–Steinberg; the smoothest gradients, but a tiling texture can show a seam |

A colour a pack stores exactly is kept (error diffusion can still spread a neighbour's error into it), so reconstructed textures build the same in the `rtexture` packs with every choice; in the paletted `texture` packs they can change with the choice. Interface images are not dithered. The choice belongs to the project and is kept in `.zstudio\settings.json`.

MCP: `zstudio_source_dithering`.

## Keep sources away from the game

Don't put a project inside a game folder: loose `support\*.gw`/`*.gs` files override `interp.zbd`, and loose `*_easy`/`*_hard` resources change which difficulty files the game uses. Export into a copy of the game, or a separate folder, instead.

## Edit source files by hand

Edit sources in zStudio or Blender where you can; other editors work too.
- **Resources (`.zrd`) and animation definitions (`.zad`)** are text. A file lists the children of its root array. `( … )` is an array, and a key and its values go on one line: `GRAVITY ( -9.8 )`. Integers are decimal (`42`); floats always have a decimal point or exponent (`2.0`, `1E-45`), because the game tells them apart; `f32:7FC00001` writes raw float bits. Strings are bare when they start with a letter or `_` and hold only letters, digits, `_`, `.` and `-`, otherwise quoted with `\"`, `\\` and `\xNN` escapes. `#` starts a comment. Two sources with the same file name in one archive are refused; a mission `.zrd` named like a common one is never read by the game (the build warns).
- **Keyframe scripts (`.zan`)** use the Softimage "SI Animation Script" format the originals were written in: frame blocks with each object's `Scaling`, `Rotation` (radians) and `Translation`. Keyframes no SI script reproduces exactly are written in zStudio's keyframe format, a track per object: `OBJECT name`, then `FRAME n` lines with `POSITION`, `ROTATION` (quaternion W X Y Z), `SCALE` and optional rates `VELOCITY`, `SPIN`, `GROWTH`. Both compile.
- **Scripts (`.gs`, `.gw`)** use the game's tokenizer: `#` ends a line; tokens are separated by commas, spaces or tabs; write an empty token with commas (`a,,b`).
- **Sounds** can be any 8- or 16-bit PCM WAV. `sounds.zrd` declares each bank's format as a ceiling: a lower rate, sample size or channel count of the source is kept. A WAV it doesn't declare goes into every bank unchanged (the build warns).
- **Textures** can be any PNG.
- **Models** are glTF 2.0 with external PNG textures. Engine attributes (flags, zones, references) live in `extras.recoil`; change them in zStudio or with the [Blender extension](../blender/zstudio_recoil/README.md), which keeps the linked model graph that ordinary glTF import/export loses. A model must:
  - hold at most 921 vertices (corners within 0.001 units merge); split larger meshes;
  - use triangles, strips or fans, with external PNG base-colour textures (no embedded images, normal, occlusion, emissive or metallic/roughness maps; metallic 0, roughness 1);
  - have no skinning, animation channels or vertex colours (bake them; author animation in `.zad`/`.zan`);
  - use at most one morph target, with position deltas only;
  - use repeat or clamp texture wrapping (not mirrored), the same for every material of one texture;
  - name nodes in at most 35 Latin-1 characters.

## Compare worlds

**Tools → Compare worlds…** shows two `gamez.zbd` worlds, such as a shipped one and an export, as one tree merged by parent-child structure, whatever the order of their nodes. Rows are marked `·` (same), `≠` (changed), `−` (only in the first) or `+` (only in the second); `⚑` marks a node a lookup by name finds in one world but not the other. **Differences only** hides unchanged rows and **Filter** finds rows by name; selecting a row shows both worlds' values. Either world may be one of the 1998 demos.

MCP: `zstudio_world_compare`, `zstudio_world_compare_tree`.

## Differences from the shipped files

Exports match the shipped files in meaning: every resource, script token, sound, world node, placement, model, texture and animation entry. They differ where the shipped files carry what the game doesn't read:
- Name fields carry other leftover bytes after the name.
- Software packs (`texture*`) keep textures in direct colour by rule (damage masks and the horizon's textures), so a few textures the developers chose by hand differ between paletted and direct colour.
- `m9\gamez.zbd` names a texture (`surf00`) that no pack holds; the game shows its default texture, in the export too.
- Five 1999 animation definitions (one of 1998) are older than the ones the shipped animations were compiled from. Reconstruction rebuilds them from `anim.zbd` and notes what the newer version changes, so the project reproduces the animations the game shipped.
- Reconstruction rebuilds each texture from its best copy: the shape the pack with the most room keeps, in direct colour where a copy of that shape has it (the 1999 strips such as `sonic06`, 128 × 16, stay strips).
