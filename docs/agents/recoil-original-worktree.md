# RECOIL's original build work tree

The source tree the developers built the shipped ZBD files from, as far as the shipped files show it. Source projects follow this layout with glTF/PNG in place of OpenFlight `.flt`/TIFF `.tif` and `.zad` animation definitions in place of their `.zrd` (see [sources](sources.md#reconstruction)); this page keeps the original file types. The backbone is `interp.zbd`: 122 prepared gamegen scripts (same names in 1998 and 1999) whose commands name every folder and many files. The 1999 release is described; 1998 ships the same scripts and data for m1–m6.

## Evidence

| Mark | Meaning |
| --- | --- |
| **script** | `interp.zbd`: a script sets the folder, then loads or names the file (`bft1.gw`: `SetModelDirectory ..\data\m1\models\bft`, `LoadGameGen vtol.flt vtol1`). |
| **search** | `interp.zbd` names the file but it was found through a folder list; any folder earlier in the list is equally consistent. |
| **reference** | Named inside another model (an OpenFlight external reference, surviving as a node named after the file in `gamez.zbd`). |
| **packs** | Texture packs list records sorted by full lowercase source path (`…\m1\textures\bar1` < `…\m1\textures\bft\tankback` < `…\m1\textures\bigwav01`), which places each texture's folder. |
| **archive** | ZAR members (`zrdr.zbd`) record the temporary file their compiler wrote in the source folder (`D:\battlesportdev\data\m1\zrdr\envmodels\frcE3B0.TMP`). |
| **stamp** | Each `anim.zbd` records full path and time of every definition and keyframe script it compiled (`..\data\m1\zrdr\vtol\m1pickup.zan`). |
| **sounds.zrd** | `SOUND_PATH` gives the sound folder. |
| **image order** | `image.zbd` records are sorted by source path; runs give its folders, resources name some (`IMAGE_PATH`). |

## The tree

```
<gamegen work folder>\            scripts run from here; ..\data is beside it
  default.gs                      builds m2 (runs m2.gs)
  testdb.gs                       test world from ..\data\m2\models\testdb.flt into zbd\gamez.zbd
  m1.gs … m13.gs                  build a mission: world and animations
  m1_zbd.gs … m13_zbd.gs          run by the game: read the built world, set up texture effects
  support\
    common.gw                     paths, outputs, shared settings (sourced by every commonmN.gw)
    commonm1.gw … commonm13.gw    set MISSION_DIR, then source common.gw
    loadm1.gw … loadm13.gw        world settings and the mission database (LoadGameGen mN.flt)
    displaym1.gw … displaym13.gw  window, camera, display
    initm1.gw … initm13.gw        run by the game first: node, model, material pool sizes
    mission1.gw … mission13.gw    mission objects
    tex_fx.gw, tex_fxm1.gw …      texture cycles and damage masks (after the world is written)
    bft1.gw … bft6.gw             campaign vehicles, from ..\data\mN\models\bft
    bftmulti.gw                   multiplayer vehicles (m7–m13), from ..\data\common\multi_bft\model
    world.gw, weapons.gw, vehicles.gw, pickup.gw, morfutil.gw
    setup.gw                      gamegen tool settings (SetPaletteName mk.act); no build sources it
    testdb.gw                     (bft.gw: sourced by testdb.gs, not in interp.zbd)
  zbd\m1\ … zbd\m13\              outputs: gamez.zbd (GameZWriteZBDFile), anim.zbd (AnimSetZBDFile)
..\data\
  common\models\                  pickups loaded by pickup.gw (search)
  common\textures\                (packs)
  common\zrdr\{enemies,explosns,lighting,vtol,weapons}\   resources, definitions (archive, stamp)
  common\effects\models\          searched; no script names it
  common\effects\textures\        includes pock1–3, lflare1–4 (packs)
  common\multi_bft\{model,textures,zrdr}\   bft_multi.flt, regenerate.flt, vtol.flt; bftmulti.zrd, deathmulti.zrd
  common\sounds\                  265 .wav (sounds.zrd)
  common\fonts\, common\images\   interface images (image order)
  effects\models\                 weapon/effect models weapons.gw loads (script; uncertain)
  effects\textures\               searched; no packed texture came from it
  mN\models\, mN\models\bft\      database mN.flt, mission objects (search), references; vehicles (script)
  mN\textures\, mN\textures\bft\  (packs)
  mN\zrdr\{aipath,bft,choppers,envmodels,vtol,…}\   resources, anim.zrd, definitions (archive, stamp)
  mN\images\                      objective images (m1–m6; image order)
```

m7–m13 are multiplayer (vehicles from `common\multi_bft`); their `models\bft` and `textures\bft` are searched but empty.

## How the scripts run

The build runs `mN.gs`; the game, loading a mission, runs `support\initmN.gw` then `mN_zbd.gs`. `m1.gs` order (indentation = `source` nesting; `*` after the world is written): `commonm1.gw` → `common.gw`; `loadm1.gw` → `world.gw`, `displaym1.gw`, `weapons.gw`, `bft1.gw`, `vehicles.gw`, `mission1.gw`, `pickup.gw`; `tex_fxm1.gw` * → `tex_fx.gw` *.

`common.gw` sets the folders every mission searches (with `MISSION_DIR`); folders are added under the engine's search-path rule ([engine evidence](engine-evidence.md#common-rules)). `weapons.gw` and `bft1.gw` name folders `common.gw` listed with the same text, so the order stays: m1 searches, from the database through the pickups (the game's own run of `common.gw` in `m1_zbd.gs` sets the same texture order):

| Models | Textures |
| --- | --- |
| `data\m1\models\bft` | `data\common\effects\textures` |
| `data\common\effects\models` | `data\common\textures` |
| `data\common\models` | `data\effects\textures` |
| `data\effects\models` | `data\m1\textures\bft` |
| `data\m1\models` | `data\m1\textures` |
| | `zbd\m1` |

`bftmulti.gw` names `data\common\multi_bft\model` and `\textures`, which no earlier script lists, so they go to the head for later loads. Resources are found through `RdrSetPath ..\data\common\zrdr;..\data\%MISSION_DIR%\zrdr;..\data\%MISSION_DIR%\zrdr\aipath`. Folders named: `common.gw` names every folder above (with `%MISSION_DIR%`) and `zbd\mN`; `weapons.gw` also names `data\effects\models`/`\textures`; `bft1.gw`–`bft6.gw` name `data\mN\models\bft`/`textures\bft`; `bftmulti.gw` the `multi_bft` folders.

## Models

`LoadGameGen file.flt node` loads a model: 185 distinct files are loaded by scripts, 303 more named only inside other models.

- **script:** `bft1.gw`–`bft6.gw`, `bftmulti.gw` and `weapons.gw` set their own folder and then load (1,488 of 2,033 loads in the 13 builds). For `bftN.gw` vehicles this is not certain: worlds record no paths and those loads also search `data\common\models`, so one shared copy there builds the same worlds; the packs show the vehicles' textures were one copy in `data\common\textures` (each mission's player tank has its own in `mN\textures\bft`). Every vehicle is identical in all missions that load it, in both releases. For `weapons.gw` it is not certain either: naming `..\data\effects\models` again leaves it behind `data\common\effects\models`, searched first, and its textures came from `data\common\effects\textures`.
- **search:** the mission databases (`loadmN.gw`: `set dbName mN.flt`, `LoadGameGen %dbName%`), objects of `mission1.gw`, `mission3.gw`, `mission6.gw`, `mission9.gw`, `mission13.gw`, and the pickups of `pickup.gw` load through the whole list. A search load finds a file an earlier folder holds (`mission1.gw` loads `miss_lo.flt`, which `data\effects\models` holds).
- **reference:** shown beside every model referencing it; a model two missions' databases reference is in both missions' folders, as their shared textures are.
- m3 references two versions of `lturret.flt` and of `pturret.flt` (the second by another path, cached again); the versions differ only in polygon zones.
- Single-mission vehicle scripts: `support\bft6.gw` sets `..\data\m6\models\bft` and loads `lasdesig.flt` as `ldestank`; `support\bft4.gw` sets `..\data\m4\models\bft`, loads `trnsplsh.flt`, `train.flt` and `rcar1.flt` three times; train and carriage reference `tturret.flt`.
- `data\m2\models\testdb.flt` is loaded by `testdb.gs`. No script names a model in `common\effects\models`, searched before `common\models`.
- Source-project placement of these files follows the [owner decisions](sources.md#owner-decisions), not recovered physical paths.

## Textures, resources, sounds and images

- Scripts name 311 textures: 304 animation frames (`CycleTextureSetMap` in `tex_fx.gw`, `tex_fxmN.gw`), damage masks `pock1.tif`–`pock3.tif` (`WriteTextureSetMap` in `weapons.gw`, `tex_fx.gw`), lens flares `lflare1`–`lflare4` (`LensFlareTexture` in `common.gw`). Models name the rest; every texture's folder comes from pack record order. The masks were found in `data\common\effects\textures` (the pack group sorting before `data\common\textures`); no pack has a group between `data\common\textures` and `data\mN\textures`, where `data\effects\textures` would sort. A texture in several missions' packs under their own folders was a copy in each (100 textures in both `m6\textures` and `m13\textures`).
- Scripts name only resource folders (`RdrSetPath`). The 997 resource files and their subfolders come from archive recorded folders and animation stamps; 77 keyframe scripts (`.zan`) from stamps alone. Each `anim.zbd` also stamps `..\data\common\zrdr\anim.zrd`.
- `data\common\sounds\`: 265 sounds placed by `sounds.zrd`. Interface images: `data\common\fonts`, `data\common\images\dialog\<screen>`, `data\common\images\hud`, `data\mN\images` (objective images, m1–m6), from record order and `IMAGE_PATH`. `mk.act` (the palette `setup.gw` names) has no recorded folder.

## How the shipped files were built

**Source and archive mode.** The retail executable keeps the developers' switch. With archives on (retail default; its menu item is always removed), a level load runs `mN_zbd.gs`, reading `gamez.zbd`, and mounts `zbd\mN\zrdr.zbd`. With archives off it ran `mN.gs`, building the world from scripts and models and writing `gamez.zbd`, with resources from the `..\data\...\zrdr` folders; the same load then reached the animations (the loader still rejects an `anim.zbd` whose stamped sources changed and is still passed `anim.zrd`, the remains of a path that compiled `anim.zbd` there). Packing `zrdr.zbd` and the sound banks was a separate step.

The release mixes runs: reloading a mission rewrote its world and animations, not its archive; no script a shipped world ran is newer than the world. The 1999 file dates (a constant 9-hour shift, checked against the times recorded inside the files) show five archived definitions older than the versions their animations were compiled from:

| Definition | Archived text packed | `anim.zbd` compiled from a version saved | What the newer version changed |
| --- | --- | --- | --- |
| `common\zrdr\vtol\gen_vtol.zrd` (`vtol_destruction*`) | 31 Oct (1998 release), again 9 Dec (1999) | 4 Nov | reset time 0.5 s → 0; healthy model hidden at reset |
| `m1\zrdr\envmodels\frcgate.zrd` (`destroy_the_gen`) | 10 Nov | 16 Nov | the generator's explosion also calls `blowup_semi_tractr` |
| `m1\zrdr\semi\semideath.zrd` (`blowup_semi_tractr`) | 10 Nov | 16 Nov | plays once (invalidates itself) |
| `m1\zrdr\envmodels\vwrebel.zrd` (`rebel_vw`) | 10 Nov | 16 Nov | invalidates itself when the bus moves |
| `m13\zrdr\envmodels\bft_trans.zrd` (`bft_to_5cav`) | 10 Nov | 11 Dec | moves the vehicle to y −47 instead of −55 |

The game never reads these definitions; reconstruction rebuilds them from the compiled entries ([sources](sources.md#reconstruction)).

**What the packer took:** every `.zrd` under the `zrdr` folders and nothing else: all 371 definition files the animation compilers read are in their archives, none of the 77 keyframe scripts is, and unused files were packed too (the never-requested files in [resource archives](engine-evidence.md#resource-archives), definitions no `anim.zrd` lists).

**A cut feature:** `m5\zrdr\subhut\sbbelt.zrd` defines the sub hut's conveyor belt (`sub_convey_belt`, `stop_the_belt`); m5's `anim.zrd` does not list it and no world has its objects, but the compiled `sbelev.zrd` still calls `stop_the_belt`; the game finds nothing and carries on.

**Unknowable from the shipped files:** which folder each **search** model was in; where external references were (paths live inside the unshipped `.flt` files; beside the referencing file is the usual MultiGen practice); whether effect models were in `data\effects\models` (where `weapons.gw` points) or `data\common\effects\models` with their textures (`common.gw` alone spells that line `..\\data\\common\\effects\\models`, suggesting a later addition); `support\bft.gw` and `testdb.flt` did not ship.
