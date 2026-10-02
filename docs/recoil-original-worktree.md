# RECOIL's original build work tree

This is the source tree RECOIL's developers built the shipped ZBD files from, reconstructed as far as the shipped files allow. The backbone is `interp.zbd`: it holds the prepared gamegen scripts (122 in both the 1998 and 1999 releases, under the same names), and their commands name every folder of the tree and many of its files. Where the scripts name a file but not its folder, or do not name it at all, other recorded evidence fills the gap; each placement below says which. It describes the 1999 release; the 1998 release ships the same scripts and data for m1–m6 only.

zStudio's source projects (see [source-project.md](source-project.md)) follow this layout with glTF models and PNG textures in place of OpenFlight (`.flt`) and TIFF (`.tif`) files. This page keeps the original file types, as the scripts name them.

## Evidence

| Mark | Meaning |
| --- | --- |
| **script** | `interp.zbd`: a script sets the folder and then loads or names the file (for example `bft1.gw`: `SetModelDirectory ..\data\m1\models\bft`, then `LoadGameGen vtol.flt vtol1`). |
| **search** | `interp.zbd` names the file, but it was found through a list of folders; the file is shown in the most likely one and the alternatives are listed. |
| **reference** | Named inside another model (an OpenFlight external reference, which survives as a node named after the file in `gamez.zbd`), not by a script. Shown beside the models that reference it. |
| **packs** | Texture packs list their records sorted by full source path, lowercase (for example `…\m1\textures\bar1` < `…\m1\textures\bft\tankback` < `…\m1\textures\bigwav01`), which places each texture's folder. |
| **archive** | ZAR archive members (`zrdr.zbd`) record the temporary file their compiler wrote, in the source folder (`D:\battlesportdev\data\m1\zrdr\envmodels\frcE3B0.TMP`). |
| **stamp** | Each mission's `anim.zbd` records the full path and time of every definition and keyframe script it was compiled from (`..\data\m1\zrdr\vtol\m1pickup.zan`). |
| **sounds.zrd** | The sound definitions give the sound folder (`SOUND_PATH`). |
| **image order** | `image.zbd` records are sorted by source path too; the runs give its folders, and resources name some of them (`IMAGE_PATH`). |

## The tree

Counts are distinct files. `mN` stands for each mission; m1 is shown in full, the others follow the same pattern.

```
<gamegen work folder>\                  the scripts run from here; ..\data is beside it
│
├─ default.gs                           builds m2 (runs m2.gs)
├─ testdb.gs                            builds a test world from ..\data\m2\models\testdb.flt into zbd\gamez.zbd
├─ m1.gs … m13.gs                       build a mission: world and animations
├─ m1_zbd.gs … m13_zbd.gs               run by the game: read the built world, set up texture effects
│
├─ support\
│  ├─ common.gw                         paths, output files, shared settings (sourced by every commonmN.gw)
│  ├─ commonm1.gw … commonm13.gw        set MISSION_DIR, then source common.gw
│  ├─ loadm1.gw … loadm13.gw            world settings and the mission database (LoadGameGen mN.flt)
│  ├─ displaym1.gw … displaym13.gw      window, camera and display
│  ├─ initm1.gw … initm13.gw            run by the game first: node, model and material pool sizes
│  ├─ mission1.gw … mission13.gw        mission objects
│  ├─ tex_fx.gw, tex_fxm1.gw … tex_fxm13.gw   texture cycles and damage masks (after the world is written)
│  ├─ bft1.gw … bft6.gw                 campaign vehicles, from ..\data\mN\models\bft
│  ├─ bftmulti.gw                       multiplayer vehicles (m7–m13), from ..\data\common\multi_bft\model
│  ├─ world.gw, weapons.gw, vehicles.gw, pickup.gw, morfutil.gw
│  ├─ setup.gw                          gamegen tool settings (flat shading, keyboard, SetPaletteName mk.act); no build sources it
│  ├─ testdb.gw
│  └─ (bft.gw)                          sourced by testdb.gs, not in interp.zbd
│
└─ zbd\                                output (mkdir zbd, mkdir zbd\mN)
   ├─ m1\ … m13\
   │  ├─ gamez.zbd                      GameZWriteZBDFile %MissionZBDFile%
   │  ├─ anim.zbd                       AnimSetZBDFile
   │  └─ …                            the game's zbd folder has this layout, with the mission's texture packs and zrdr.zbd beside them
   └─ gamez.zbd, anim.zbd               testdb output (the game's zbd folder instead holds interp.zbd, zrdr.zbd, image.zbd and the sound banks here)

..\data\
│
├─ common\
│  ├─ models\                          36 pickups loaded by pickup.gw (search)
│  ├─ textures\                        270 textures (packs)
│  ├─ zrdr\                            resources and animation definitions (archive, stamp)
│  │  ├─ enemies\, explosns\, lighting\, vtol\, weapons\
│  │  └─ …
│  ├─ effects\
│  │  ├─ models\                       searched, but no file can be placed here (see below)
│  │  └─ textures\                     196 textures, including pock1–3 and lflare1–4 (packs)
│  ├─ multi_bft\
│  │  ├─ model\                        3 models: bft_multi.flt, regenerate.flt, vtol.flt (script, reference)
│  │  ├─ textures\                     29 textures (packs)
│  │  └─ zrdr\                         bftmulti.zrd, deathmulti.zrd (stamp)
│  ├─ sounds\                          265 .wav (sounds.zrd)
│  ├─ fonts\, images\                  interface images of image.zbd (image order)
│
├─ effects\
│  ├─ models\                          99 weapon and effect models loaded by weapons.gw (script)
│  └─ textures\                        searched, but no packed texture came from here (packs)
│
├─ m1\
│  ├─ models\                          m1.flt, 4 objects loaded by mission1.gw (search), 55 referenced by m1.flt (reference)
│  │  └─ bft\                          10 vehicles loaded by bft1.gw (script), 2 referenced by them
│  ├─ textures\                        242 textures (packs)
│  │  └─ bft\                          12 vehicle textures (packs)
│  ├─ zrdr\                            resources, anim.zrd and definition folders (archive, stamp)
│  │  ├─ aipath\                       AI paths and aiv*.zrd
│  │  └─ bft\, beach\, choppers\, envmodels\, pier\, ruins\, semi\, vtol\ …
│  └─ images\                          objective images of image.zbd (image order)
│
├─ m2\ … m6\                            as m1 (campaign missions, with models\bft and textures\bft)
└─ m7\ … m13\                           as m1 without vehicles (multiplayer: common\multi_bft); models\bft and
                                        textures\bft are searched but empty
```

## How the scripts run

The build (the gamegen tool) runs `mN.gs`; the game, when it loads a mission, runs `support\initmN.gw` and then `mN_zbd.gs`. The order of `m1.gs` (indentation is `source` nesting; `*` runs after the world is written):

```
  support\commonm1.gw
    support\common.gw
  support\loadm1.gw
    support\world.gw
    support\displaym1.gw
    support\weapons.gw
    support\bft1.gw
    support\vehicles.gw
    support\mission1.gw
    support\pickup.gw
  support\tex_fxm1.gw *
    support\tex_fx.gw *
```

`common.gw` sets the folders every mission searches, with `MISSION_DIR` from `commonmN.gw`. Each `SetModelDirectory`/`SetTextureDirectory` inserts its folders at the head of the list, so the most recent is searched first. For m1 the model folders are searched in this order when the database loads (`loadm1.gw`) and when the pickups load (`pickup.gw`, after `weapons.gw` and `bft1.gw` added theirs):

| Mission database (`loadm1.gw`) | Pickups (`pickup.gw`) |
| --- | --- |
| `data\m1\models\bft` | `data\m1\models\bft` |
| `data\common\effects\models` | `data\effects\models` |
| `data\common\models` | `data\common\effects\models` |
| `data\effects\models` | `data\common\models` |
| `data\m1\models` | `data\m1\models` |

Texture folders, when `weapons.gw` names the damage masks and when the game's `tex_fx.gw` does:

| Build (`weapons.gw`) | Game (`m1_zbd.gs` → `tex_fx.gw`) |
| --- | --- |
| `data\effects\textures` | `data\common\effects\textures` |
| `data\common\effects\textures` | `data\common\textures` |
| `data\common\textures` | `data\effects\textures` |
| `data\m1\textures\bft` | `data\m1\textures\bft` |
| `data\m1\textures` | `data\m1\textures` |
| `zbd\m1` | `zbd\m1` |

Resource files (`.zrd`) are found through `RdrSetPath ..\data\common\zrdr;..\data\%MISSION_DIR%\zrdr;..\data\%MISSION_DIR%\zrdr\aipath`.

Folders named by each script (`common.gw` lines use `%MISSION_DIR%`, so they name the folder for every mission):

| Folder | Named by |
| --- | --- |
| `data\common\effects\models` | common.gw |
| `data\common\effects\textures` | common.gw |
| `data\common\models` | common.gw |
| `data\common\multi_bft\model` | bftmulti.gw |
| `data\common\multi_bft\textures` | bftmulti.gw |
| `data\common\textures` | common.gw |
| `data\common\zrdr` | common.gw |
| `data\effects\models` | common.gw, weapons.gw |
| `data\effects\textures` | common.gw, weapons.gw |
| `data\mN\models` | common.gw |
| `data\mN\models\bft` | bft1.gw–bft6.gw, common.gw |
| `data\mN\textures` | common.gw |
| `data\mN\textures\bft` | bft1.gw–bft6.gw, common.gw |
| `data\mN\zrdr` | common.gw |
| `data\mN\zrdr\aipath` | common.gw |
| `zbd\mN` | common.gw |

## Models

`LoadGameGen file.flt node` loads a model. 185 distinct model files are loaded by the scripts, and 303 more are named only inside other models. Placement by what the scripts record:

- **script:** `bft1.gw`–`bft6.gw`, `bftmulti.gw` and `weapons.gw` set their own model folder and then load, so their models were in that folder (1,488 of 2,033 loads in the 13 builds).
- **search:** the mission databases (`loadmN.gw`: `set dbName mN.flt`, `LoadGameGen %dbName%`), the objects of `mission1.gw`, `mission3.gw`, `mission6.gw`, `mission9.gw` and `mission13.gw`, and the pickups of `pickup.gw` load through the whole list. Mission scripts' models are shown in `mN\models`, pickups (the same in every mission) in `common\models`. Any earlier folder in the list is equally consistent with the scripts.
- **reference:** models referenced from inside another model are shown beside every model that references it, so a model two missions' databases reference is in both missions' folders, as their shared textures are.

**`data\common\models`** (36)

- search: `arcammo.flt`, `arcgun.flt`, `chipamphib.flt`, `chiphover.flt`, `chipsub.flt`, `erfpgammo.flt`, `erfpggun.flt`, `freonammo.flt`, `freongun.flt`, `gmiss.flt`, `gmissammo.flt`, `gnuke.flt`, `gnukeammo.flt`, `lazerammo.flt`, `lazerdammo.flt`, `lazerdes.flt`, `lazergun.flt`, `lomiss.flt`, `lomissammo.flt`, `mineammo1.flt`, `mineammo2.flt`, `mineammo3.flt`, `mineammo4.flt`, `minegun.flt`, `minnan.flt`, `mortammo1.flt`, `mortammo2.flt`, `mortgun.flt`, `nanite100.flt`, `nanite2.flt`, `napalmammo.flt`, `napalmgun.flt`, `nuke.flt`, `nukeammo.flt`, `sonicammo.flt`, `sonicgun.flt`

**`data\common\multi_bft\model`** (3)

- script: `bft_multi.flt`, `vtol.flt`
- reference: `regenerate.flt`

**`data\effects\models`** (99)

- script: `arc.flt`, `ash_dirt.flt`, `beam_red.flt`, `beam_yel.flt`, `bftmbrst.flt`, `bftsplsh.flt`, `boxexp.flt`, `bsmoke_ring.flt`, `bsplsh.flt`, `bubbles.flt`, `bxcrsh.flt`, `bxxpld.flt`, `deadman1.flt`, `dmsquish1.flt`, `erfpg_mzl.flt`, `exhaust.flt`, `exhaustfade.flt`, `exp_blue.flt`, `exp_fireup.flt`, `exp_medium.flt`, `exp_yellow.flt`, `expblueparts.flt`, `expbunk.flt`, `expcrate.flt`, `expgrnmed.flt`, `expgrnparts.flt`, `expnuke.flt`, `exporangeparts.flt`, `expredmed.flt`, `expredparts.flt`, `expsmorangeparts.flt`, `expyelparts.flt`, `fire1.flt`, `fire_bft.flt`, `fire_hulk.flt`, `fire_trail.flt`, `firering.flt`, `flare_blue.flt`, `flare_orange.flt`, `flare_white.flt`, `freon_fo.flt`, `freon_shatter.flt`, `generic6.flt`, `gsxpld.flt`, `he_dirt.flt`, `he_morta.flt`, `hulk_big.flt`, `hulk_small.flt`, `kill_arc.flt`, `kill_lasdes.flt`, `lav_dirt.flt`, `lg_impact.flt`, `lite_ref.flt`, `litening.flt`, `md_impact.flt`, `mine_ph1.flt`, `mine_ph2.flt`, `mine_re1.flt`, `mine_re2.flt`, `miss_launch.flt`, `miss_lo.flt`, `miss_tg.flt`, `muzzleburst.flt`, `napalm.flt`, `nuke_lo.flt`, `nuke_tg.flt`, `pchute.flt`, `redsprks.flt`, `rfpg_grn.flt`, `rfpg_mzl.flt`, `rfpg_red.flt`, `ric_blue.flt`, `ric_green.flt`, `ric_red.flt`, `rsmoke_ring.flt`, `sizzle.flt`, `sm_impact.flt`, `smktrl1.flt`, `smoke1.flt`, `smoke2.flt`, `smoke_damage.flt`, `smoke_destroy.flt`, `smoke_trail1.flt`, `smxplsn.flt`, `snow_dirt.flt`, `sonicfly.flt`, `spark_blue.flt`, `spark_green.flt`, `spark_orange.flt`, `spark_red.flt`, `spl_dam.flt`, `splash1.flt`, `sulph_dirt.flt`, `videxp.flt`, `wake.flt`, `watblast.flt`, `watexp.flt`, `wsmoke_ring.flt`, `xplflare.flt`

**`data\m1\models`** (60)

- search: `m1.flt`, `tractor.flt`, `trailer.flt`, `vw_bus.flt`, `vw_bus_rebel.flt`
- reference: `barrel.flt`, `box2.flt`, `cavefire.flt`, `caveruss1.flt`, `crbox.flt`, `floodlight.flt`, `frcgen.flt`, `fuel.flt`, `fuel1.flt`, `gscn.flt`, `gullfly.flt`, `gullfly2.flt`, `hivolt.flt`, `holo.flt`, `hshit1.flt`, `liteh.flt`, `m1gate1.flt`, `m1strut.flt`, `nbsign.flt`, `ohead1.flt`, `palm01.flt`, `palm02.flt`, `palm03.flt`, `palm04.flt`, `palm05.flt`, `palm06.flt`, `palm07.flt`, `palm08.flt`, `pbrlb.flt`, `pier.flt`, `pier_2.flt`, `pierbom.flt`, `pierbox.flt`, `piercrn.flt`, `piergull.flt`, `piergull_2.flt`, `powerbay.flt`, `prtires.flt`, `prtires_2.flt`, `pturret.flt`, `rktbody.flt`, `rktdra.flt`, `rktdrb.flt`, `rktpad.flt`, `samsite1.flt`, `sandbag.flt`, `sandbag1.flt`, `shak.flt`, `smkring.flt`, `veg1.flt`, `veg1_2.flt`, `veg2.flt`, `veg2_2.flt`, `walldes1.flt`, `walldes2.flt`

**`data\m1\models\bft`** (12)

- script: `bft_m1.flt`, `comanche.flt`, `drone.flt`, `lockon.flt`, `minelay.flt`, `ntank.flt`, `rumv.flt`, `scout.flt`, `trenchr.flt`, `vtol.flt`
- reference: `hturret.flt`, `regenerate.flt`

**`data\m2\models`** (49)

- search: `m2.flt`
- reference: `bouy1.flt`, `bridge2.flt`, `bunker1.flt`, `bunker2.flt`, `canalend.flt`, `combox1.flt`, `combox2.flt`, `combox3.flt`, `comfence.flt`, `comradar.flt`, `comstat.flt`, `comstat1.flt`, `digger.flt`, `doorchp.flt`, `doorfac.flt`, `doorla.flt`, `doorlb.flt`, `facbrdg.flt`, `faccart1.flt`, `faccart2.flt`, `facflow.flt`, `facgen.flt`, `facpow.flt`, `facsmk.flt`, `fturret.flt`, `gardgate.flt`, `gardgate2.flt`, `grflash.flt`, `helip.flt`, `labbx2.flt`, `labfan.flt`, `labfan2.flt`, `labwall.flt`, `machine.flt`, `maingate.flt`, `minecart.flt`, `minepipe.flt`, `nturret.flt`, `nturret1.flt`, `nturret2.flt`, `pcore.flt`, `pdwall1.flt`, `pkeys.flt`, `powdoor.flt`, `pturret.flt`, `samsite1.flt`, `warn10.flt`, `warn11.flt`

**`data\m2\models\bft`** (15)

- script: `amphib.flt`, `bft_m2.flt`, `comanche.flt`, `drone.flt`, `ftank.flt`, `lockon.flt`, `ltank.flt`, `minelay.flt`, `ntank.flt`, `rumv.flt`, `scout.flt`, `trenchr.flt`, `vtol.flt`
- reference: `hturret.flt`, `regenerate.flt`

**`data\m3\models`** (27)

- search: `lavbub.flt`, `m3.flt`
- reference: `b_pipe01.flt`, `b_pipe02.flt`, `b_pipe03.flt`, `b_pipe05.flt`, `b_pipe06.flt`, `boilelev1.flt`, `bovalve.flt`, `capcrane.flt`, `conegate.flt`, `ctrdish.flt`, `decks.flt`, `frntbrdg.flt`, `gtherm.flt`, `helip.flt`, `hoverarm.flt`, `lavagate.flt`, `lavcone.flt`, `lavpot.flt`, `left1.flt`, `lturret.flt`, `mgstrut.flt`, `nturret.flt`, `pturret.flt`, `right1.flt`, `tower.flt`

**`data\m3\models\bft`** (17)

- script: `amphib.flt`, `arcsaber.flt`, `bft_m3.flt`, `comanche.flt`, `drone.flt`, `ftank.flt`, `hov.flt`, `lockon.flt`, `ltank.flt`, `minelay.flt`, `ntank.flt`, `rumv.flt`, `scout.flt`, `trenchr.flt`, `vtol.flt`
- reference: `hturret.flt`, `regenerate.flt`

**`data\m4\models`** (54)

- search: `m4.flt`
- reference: `arcturt.flt`, `bigbridge.flt`, `bldg01.flt`, `bldg02.flt`, `bldg03.flt`, `cathbrdg.flt`, `cathdrl.flt`, `chemarch.flt`, `chempipl.flt`, `chemplnt.flt`, `chemvlv.flt`, `dambrok.flt`, `damface.flt`, `endleft.flt`, `endright.flt`, `finalerock.flt`, `fntn.flt`, `fturret.flt`, `fueltank.flt`, `gargoyle.flt`, `grating.flt`, `handcar.flt`, `housruin1.flt`, `hydrant.flt`, `middoor.flt`, `midsect.flt`, `pbrlb.flt`, `pipcon1.flt`, `rail_dam.flt`, `railwall.flt`, `rckcontrol1.flt`, `rdrail11.flt`, `rdrail21.flt`, `rdrail31.flt`, `rdrail41.flt`, `rockt.flt`, `rrties.flt`, `ruin01.flt`, `ruin02.flt`, `signal.flt`, `st_bridg.flt`, `stainwin.flt`, `stk1.flt`, `tower.flt`, `tower2.flt`, `tri_arch.flt`, `walldes1.flt`, `walturm4.flt`, `warefill.flt`, `warehous1.flt`, `warehous2.flt`, `warehous3.flt`, `warehous4.flt`

**`data\m4\models\bft`** (22)

- script: `amphib.flt`, `arcsaber.flt`, `bft_m4.flt`, `comanche.flt`, `drone.flt`, `ftank.flt`, `hov.flt`, `lockon.flt`, `ltank.flt`, `minelay.flt`, `ntank.flt`, `rcar1.flt`, `rumv.flt`, `scout.flt`, `sonic.flt`, `train.flt`, `trenchr.flt`, `trnsplsh.flt`, `vtol.flt`
- reference: `hturret.flt`, `regenerate.flt`, `tturret.flt`

**`data\m5\models`** (36)

- search: `m5.flt`
- reference: `arcturt.flt`, `btundr1.flt`, `btundr1_2.flt`, `btundr2.flt`, `cloncage.flt`, `clone_burger.flt`, `clonedoor.flt`, `cloneship1.flt`, `frcfield.flt`, `frcsub.flt`, `helip.flt`, `icedoor1.flt`, `icedoor2.flt`, `icolumn1.flt`, `icolumn2.flt`, `icolumn3.flt`, `lturret.flt`, `lzsuz1.flt`, `pcover.flt`, `powerstat.flt`, `psbox1.flt`, `psmain1.flt`, `psmain2.flt`, `pspipe1.flt`, `pstunnel.flt`, `ptrig1.flt`, `pturret.flt`, `radiotwr.flt`, `scontain1.flt`, `scontain2.flt`, `scontain3.flt`, `scontain4.flt`, `sonictur.flt`, `subarm.flt`, `subelev.flt`

**`data\m5\models\bft`** (19)

- script: `amphib.flt`, `arcsaber.flt`, `bft_m5.flt`, `comanche.flt`, `drone.flt`, `ftank.flt`, `hov.flt`, `lockon.flt`, `ltank.flt`, `minelay.flt`, `ntank.flt`, `rumv.flt`, `scout.flt`, `sonic.flt`, `sub.flt`, `trenchr.flt`, `vtol.flt`
- reference: `hturret.flt`, `regenerate.flt`

**`data\m6\models`** (67)

- search: `celldest.flt`, `m6.flt`, `xposwall.flt`
- reference: `a1boss.flt`, `a1dflctr.flt`, `a2doorb.flt`, `a2doors.flt`, `a2gun.flt`, `a3boss.flt`, `a3door.flt`, `a3wind1.flt`, `bldg01.flt`, `bldg02.flt`, `bldg03.flt`, `bosprt1.flt`, `bosprt2.flt`, `bosprt3.flt`, `bosprt4.flt`, `boss2.flt`, `braintur.flt`, `cell.flt`, `cells.flt`, `citybrl.flt`, `citybrl_2.flt`, `clontree1.flt`, `clontree2.flt`, `clontree3.flt`, `cluster1.flt`, `cluster2.flt`, `cluster3.flt`, `curb1.flt`, `curb2.flt`, `curb3.flt`, `cwall1.flt`, `cwall2.flt`, `cwall24.flt`, `cwall25.flt`, `cwall26.flt`, `cwall3.flt`, `cwall4.flt`, `cwall5.flt`, `cwall6.flt`, `cwall7.flt`, `cwall8.flt`, `cwall9.flt`, `dcube.flt`, `dmpstr.flt`, `dr02.flt`, `gen_box.flt`, `hydrant.flt`, `lturret.flt`, `phone.flt`, `polgat11.flt`, `pwraltr.flt`, `ruin01.flt`, `ruin02.flt`, `slitbdy1.flt`, `sontrig1.flt`, `stplite.flt`, `stubpost.flt`, `tport7.flt`, `wlpart1.flt`, `wlpart10.flt`, `wlpart11.flt`, `wlpart2.flt`, `wlpart3.flt`, `wlpart4.flt`

**`data\m6\models\bft`** (20)

- script: `amphib.flt`, `arcsaber.flt`, `bft_m6.flt`, `comanche.flt`, `drone.flt`, `ftank.flt`, `hov.flt`, `lasdesig.flt`, `lockon.flt`, `ltank.flt`, `minelay.flt`, `ntank.flt`, `rumv.flt`, `scout.flt`, `sonic.flt`, `sub.flt`, `trenchr.flt`, `vtol.flt`
- reference: `hturret.flt`, `regenerate.flt`

**`data\m7\models`** (4)

- search: `m7.flt`
- reference: `bridgem7.flt`, `bunkerm7.flt`, `canendm7.flt`

**`data\m8\models`** (11)

- search: `m8.flt`
- reference: `cratbrdg11.flt`, `cratbrdg21.flt`, `cratbrdg31.flt`, `cratbrdg41.flt`, `decks.flt`, `fortbrdg1.flt`, `fortbrdg2.flt`, `fortbrdg3.flt`, `frntbrdg.flt`, `mgstrut.flt`

**`data\m9\models`** (15)

- search: `cbarrel.flt`, `cbarrier1.flt`, `chaybale.flt`, `m9.flt`
- reference: `arrowl.flt`, `arrowr.flt`, `barrier1.flt`, `chkfinish.flt`, `chkpoint1.flt`, `chkpoint2.flt`, `chkpoint3.flt`, `chkstart1.flt`, `haybale.flt`, `hbarrel.flt`, `scurve.flt`

**`data\m10\models`** (16)

- search: `m10.flt`
- reference: `bballhoop.flt`, `bbdoor1.flt`, `bbdoor2.flt`, `bbdoor3.flt`, `citybarrel.flt`, `dumpster.flt`, `fireindahood.flt`, `hydrant.flt`, `phone.flt`, `slightbox.flt`, `snakesign.flt`, `snaketooth1.flt`, `snaketooth2.flt`, `stoplight.flt`, `tower.flt`

**`data\m11\models`** (7)

- search: `m11.flt`
- reference: `m11_crate1.flt`, `m11_crate2.flt`, `m11_crate3.flt`, `m11_ice1.flt`, `m11_ice2.flt`, `m11_ice3.flt`

**`data\m12\models`** (11)

- search: `m12.flt`
- reference: `endleft.flt`, `endright.flt`, `middoor.flt`, `midsect.flt`, `vendude1.flt`, `warefill.flt`, `warehous1.flt`, `warehous2.flt`, `warehous3.flt`, `warehous4.flt`

**`data\m13\models`** (25)

- search: `m13.flt`, `xposwall.flt`
- reference: `clontree1.flt`, `clontree2.flt`, `clontree3.flt`, `cwall1.flt`, `cwall2.flt`, `cwall24.flt`, `cwall25.flt`, `cwall26.flt`, `cwall3.flt`, `cwall4.flt`, `cwall5.flt`, `cwall6.flt`, `cwall7.flt`, `cwall8.flt`, `cwall9.flt`, `walldes1.flt`, `walldes2.flt`, `wlpart1.flt`, `wlpart10.flt`, `wlpart11.flt`, `wlpart2.flt`, `wlpart3.flt`, `wlpart4.flt`

A search load finds a file an earlier folder holds: `mission1.gw` loads `miss_lo.flt`, which `data\effects\models` holds (searched before the mission's own folder).

m3 references two different versions of `lturret.flt` and of `pturret.flt`; the shipped world keeps both, so one folder cannot have held a single file for each. zStudio writes the second as `lturret_2` and `pturret_2`.

`data\m2\models\testdb.flt` is loaded by `testdb.gs` (script). No script names a model in `common\effects\models`; common.gw searches it before `common\models`, so the pickups, and any database or mission object, could have been there instead.

## Textures

Scripts name 311 textures: 304 animation frames (`CycleTextureSetMap` in `tex_fx.gw` and `tex_fxmN.gw`), the damage masks `pock1.tif`–`pock3.tif` (`WriteTextureSetMap` in `weapons.gw` and `tex_fx.gw`) and the lens flares `lflare1`–`lflare4` (`LensFlareTexture` in `common.gw`). Models name the rest. The scripts give only the search list, so every texture's folder comes from the packs' record order.

The packs contradict one reading of the scripts: `weapons.gw` sets `..\data\effects\textures` and then names `pock1.tif`, so the masks would be found there first, yet every mission's packs list them with the effect textures, a group that sorts before `data\common\textures` and so can only be `data\common\effects\textures`. No mission's packs have a group between `data\common\textures` and `data\mN\textures`, where `data\effects\textures` would sort. The folder was searched, but none of the packed textures came from it.

A texture in several missions' packs under their own folders was a copy in each (for example 100 textures in both `m6\textures` and `m13\textures`, since m13 searches only its own mission folders).

| Folder | Textures |
| --- | --- |
| `data\common\effects\textures` | 196 |
| `data\common\multi_bft\textures` | 29 |
| `data\common\textures` | 270 |
| `data\m1\textures` | 242 |
| `data\m1\textures\bft` | 12 |
| `data\m2\textures` | 379 |
| `data\m2\textures\bft` | 14 |
| `data\m3\textures` | 246 |
| `data\m3\textures\bft` | 15 |
| `data\m4\textures` | 229 |
| `data\m4\textures\bft` | 15 |
| `data\m5\textures` | 167 |
| `data\m5\textures\bft` | 29 |
| `data\m6\textures` | 376 |
| `data\m6\textures\bft` | 29 |
| `data\m7\textures` | 50 |
| `data\m8\textures` | 31 |
| `data\m9\textures` | 64 |
| `data\m10\textures` | 62 |
| `data\m11\textures` | 17 |
| `data\m12\textures` | 62 |
| `data\m13\textures` | 123 |

## Resources and animations

Scripts name only the resource folders (`RdrSetPath`). The 997 resource files and their subfolders come from the archives' recorded source folders and the animation stamps, and 77 keyframe scripts (`.zan`) from the stamps alone. Each mission's `anim.zbd` also stamps `..\data\common\zrdr\anim.zrd`.

| Folder | Resources | Keyframe scripts |
| --- | --- | --- |
| `data\common\multi_bft\zrdr` | 2 | 0 |
| `data\common\zrdr` | 25 | 0 |
| `data\common\zrdr\enemies` | 20 | 0 |
| `data\common\zrdr\explosns` | 59 | 0 |
| `data\common\zrdr\lighting` | 2 | 0 |
| `data\common\zrdr\vtol` | 2 | 0 |
| `data\common\zrdr\weapons` | 17 | 0 |
| `data\m1\zrdr` | 12 | 0 |
| `data\m1\zrdr\aipath` | 94 | 0 |
| `data\m1\zrdr\beach` | 7 | 0 |
| `data\m1\zrdr\bft` | 1 | 0 |
| `data\m1\zrdr\choppers` | 1 | 1 |
| `data\m1\zrdr\envmodels` | 17 | 1 |
| `data\m1\zrdr\pier` | 5 | 0 |
| `data\m1\zrdr\ruins` | 4 | 0 |
| `data\m1\zrdr\semi` | 3 | 4 |
| `data\m1\zrdr\vtol` | 5 | 6 |
| `data\m2\zrdr` | 11 | 0 |
| `data\m2\zrdr\aipath` | 86 | 0 |
| `data\m2\zrdr\bft` | 2 | 0 |
| `data\m2\zrdr\choppers` | 1 | 3 |
| `data\m2\zrdr\comstat` | 4 | 0 |
| `data\m2\zrdr\envmodels` | 7 | 0 |
| `data\m2\zrdr\factory` | 10 | 0 |
| `data\m2\zrdr\mine` | 8 | 1 |
| `data\m2\zrdr\power` | 8 | 0 |
| `data\m2\zrdr\techlab` | 9 | 0 |
| `data\m2\zrdr\vtol` | 5 | 9 |
| `data\m3\zrdr` | 11 | 0 |
| `data\m3\zrdr\aipath` | 95 | 0 |
| `data\m3\zrdr\bft` | 2 | 0 |
| `data\m3\zrdr\boiler` | 3 | 0 |
| `data\m3\zrdr\choppers` | 1 | 1 |
| `data\m3\zrdr\envmodels` | 12 | 0 |
| `data\m3\zrdr\hover` | 3 | 0 |
| `data\m3\zrdr\vtol` | 4 | 10 |
| `data\m4\zrdr` | 11 | 0 |
| `data\m4\zrdr\aipath` | 72 | 0 |
| `data\m4\zrdr\bft` | 2 | 0 |
| `data\m4\zrdr\cathdrl` | 1 | 0 |
| `data\m4\zrdr\chem` | 2 | 0 |
| `data\m4\zrdr\choppers` | 1 | 1 |
| `data\m4\zrdr\envmodels` | 19 | 4 |
| `data\m4\zrdr\vtol` | 4 | 9 |
| `data\m5\zrdr` | 12 | 0 |
| `data\m5\zrdr\aipath` | 58 | 0 |
| `data\m5\zrdr\bft` | 2 | 0 |
| `data\m5\zrdr\choppers` | 1 | 2 |
| `data\m5\zrdr\envmodels` | 15 | 0 |
| `data\m5\zrdr\subhut` | 4 | 0 |
| `data\m5\zrdr\vtol` | 4 | 9 |
| `data\m6\zrdr` | 12 | 0 |
| `data\m6\zrdr\aipath` | 83 | 0 |
| `data\m6\zrdr\bft` | 2 | 0 |
| `data\m6\zrdr\brain` | 10 | 3 |
| `data\m6\zrdr\choppers` | 1 | 2 |
| `data\m6\zrdr\elevator` | 5 | 0 |
| `data\m6\zrdr\envmodels` | 12 | 0 |
| `data\m6\zrdr\vtol` | 2 | 7 |
| `data\m7\zrdr` | 8 | 0 |
| `data\m7\zrdr\aipath` | 1 | 0 |
| `data\m7\zrdr\envmodels` | 3 | 0 |
| `data\m7\zrdr\vtol` | 2 | 1 |
| `data\m8\zrdr` | 8 | 0 |
| `data\m8\zrdr\aipath` | 1 | 0 |
| `data\m8\zrdr\envmodels` | 2 | 0 |
| `data\m8\zrdr\vtol` | 2 | 1 |
| `data\m9\zrdr` | 9 | 0 |
| `data\m9\zrdr\aipath` | 1 | 0 |
| `data\m9\zrdr\envmodels` | 6 | 0 |
| `data\m10\zrdr` | 8 | 0 |
| `data\m10\zrdr\aipath` | 1 | 0 |
| `data\m10\zrdr\envmodels` | 11 | 0 |
| `data\m11\zrdr` | 8 | 0 |
| `data\m11\zrdr\aipath` | 1 | 0 |
| `data\m11\zrdr\envmodels` | 2 | 0 |
| `data\m11\zrdr\vtol` | 2 | 1 |
| `data\m12\zrdr` | 8 | 0 |
| `data\m12\zrdr\aipath` | 1 | 0 |
| `data\m12\zrdr\envmodels` | 2 | 0 |
| `data\m12\zrdr\vtol` | 2 | 1 |
| `data\m13\zrdr` | 8 | 0 |
| `data\m13\zrdr\aipath` | 1 | 0 |
| `data\m13\zrdr\envmodels` | 6 | 0 |

## Sounds, interface images and other files

- `data\common\sounds\`: 265 sounds, placed by `sounds.zrd`. The scripts do not name sounds.
- Interface images (`image.zbd`): `data\common\fonts`, `data\common\images\dialog\<screen>`, `data\common\images\hud` and `data\mN\images` (objective images, m1–m6), from the order of its records and the resources' `IMAGE_PATH` values; the folder names below `data\common\images\dialog` follow the resources.
- `mk.act`: the palette `setup.gw` names for the gamegen tool; no evidence gives its folder.

## How the shipped files were built

The file dates of the 1999 release survived with a constant 9-hour shift: each of the 14 `zrdr.zbd` files is dated 9 hours (±2 s) before the last member packed inside it, and the shifted dates fall seconds after times recorded inside other files (`anim.zbd` stamps, script times). Times below are UTC after that shift, all in 1998 unless noted.

**Source and archive mode.** The retail executable still has the developers' switch. With archives on (the retail default; the menu item that toggles it is always removed), a level load runs `mN_zbd.gs`, which reads `gamez.zbd`, and mounts `zbd\mN\zrdr.zbd`. With archives off it ran `mN.gs`, which builds the world from the scripts and models and writes `gamez.zbd` (`GameZWriteZBDFile`), and resources came from the `..\data\...\zrdr` folders. The same load then reached the animations: the retail loader still rejects an `anim.zbd` whose stamped sources changed and is still passed `anim.zrd`, the remains of a development path that compiled `anim.zbd` at that point. Each mission's `anim.zbd` was written 16–30 s after its `gamez.zbd`, the time between those two steps of one load. Packing `zrdr.zbd` and the sound banks was a separate step.

| When | What | Shipped files it left |
| --- | --- | --- |
| 3 Sep 21:18–21:51 | texture packs, in mission order | packs of m2, m3, m5, m8, m9, m12, m13 (the others were rebuilt one by one, 10 Sep – 18 Nov) |
| 31 Oct 20:58 | last script change: the newest of the 122 scripts in `interp.zbd` is `bft2.gw` | — |
| 10 Nov 23:36–23:43 | every mission loaded in source mode, in order (each `mNvtols.zrd` of m2–m5 rewritten seconds apart first) | worlds and animations of m3, m4, m8, m9, m11, m12 |
| 10 Nov 23:46:58–23:48:46 | resource archives packed, 1–13 s each | archives of m1–m5, m7, m8, m10–m13 |
| 12 Nov | m6 archive (17:34); sound banks (17:56, 17:59, 18:02); m2 loaded (21:08) | those files |
| 13 Nov – 11 Dec | single missions loaded again: m6 (13 Nov), m1 (16 Nov, 34 s after `semideath.zrd` was saved), m7 (30 Nov), m5 and m10 (4 Dec), m13 (11 Dec) | those missions' worlds and animations; `interp.zbd` (4 s before m13's world) |
| 4 Dec, 9 Dec | m9 archive and the common archive, packed in `E:\RecoilFull` (the others in `D:\battlesportdev`) | those archives |
| 7 Jan 1999 | `image.zbd` | — |

The release is therefore a mix of runs: reloading a mission rewrote its world and animations but not its resource archive. No script a shipped world ran is newer than the world, so worlds and scripts agree. Five archived animation definitions do not: their archives were packed before the definitions the animations were compiled from were saved.

| Definition | Archived text packed | `anim.zbd` compiled from a version saved | What the newer version changed |
| --- | --- | --- | --- |
| `common\zrdr\vtol\gen_vtol.zrd` (`vtol_destruction*`) | 31 Oct (1998 release), the same text again 9 Dec (1999) | 4 Nov | reset time 0.5 s → 0; healthy model hidden at reset |
| `m1\zrdr\envmodels\frcgate.zrd` (`destroy_the_gen`) | 10 Nov | 16 Nov | the generator's explosion also calls `blowup_semi_tractr` |
| `m1\zrdr\semi\semideath.zrd` (`blowup_semi_tractr`) | 10 Nov | 16 Nov | plays once (invalidates itself) |
| `m1\zrdr\envmodels\vwrebel.zrd` (`rebel_vw`) | 10 Nov | 16 Nov | invalidates itself when the bus moves |
| `m13\zrdr\envmodels\bft_trans.zrd` (`bft_to_5cav`) | 10 Nov | 11 Dec | moves the vehicle to y −47 instead of −55 |

The game never reads these definitions, so the mismatch had no effect in play; reconstruction rebuilds them from the compiled entries ([source-project.md](source-project.md#animations)).

**What the packer took.** Every `.zrd` under the `zrdr` folders and nothing else: all 371 definition files the animation compilers read are in their archives, none of the 77 keyframe scripts beside them is, and files nothing uses were packed as well (`location.zrd` in every mission, `aiv_easy.zrd`/`aiv_hard.zrd`, `cockpit.zrd`, `fonts.zrd`, `rcochet1.zrd`, and definitions no `anim.zrd` lists). Of the 997 members, 403 (1.9 of 3.3 MB) are never requested by the game.

**A cut feature.** `m5\zrdr\subhut\sbbelt.zrd` defines the sub hut's conveyor belt (`sub_convey_belt`, looping two belt segments with a factory sound, and `stop_the_belt`). m5's `anim.zrd` does not list it and no world has its objects (`sbbelt`, `sbblt1`, `sbblt2`), but the compiled `sbelev.zrd` still calls `stop_the_belt` when the elevator shuts the hut down; the game finds no such animation and carries on.

## What remains open

- Which folder each **search** model was in: the scripts only give the order of the folders searched.
- Where external references were: OpenFlight stores their paths inside the `.flt` files, which did not ship; beside the referencing file is the usual MultiGen practice.
- What `data\common\effects\models` and `data\effects\textures` held: both are searched, but no shipped file shows a model or packed texture from them.
- `support\bft.gw` (sourced by `testdb.gs`) and `testdb.flt` did not ship.

## File lists

<details><summary>Models by folder</summary>

- `data\common\models`: `arcammo.flt`, `arcgun.flt`, `chipamphib.flt`, `chiphover.flt`, `chipsub.flt`, `erfpgammo.flt`, `erfpggun.flt`, `freonammo.flt`, `freongun.flt`, `gmiss.flt`, `gmissammo.flt`, `gnuke.flt`, `gnukeammo.flt`, `lazerammo.flt`, `lazerdammo.flt`, `lazerdes.flt`, `lazergun.flt`, `lomiss.flt`, `lomissammo.flt`, `mineammo1.flt`, `mineammo2.flt`, `mineammo3.flt`, `mineammo4.flt`, `minegun.flt`, `minnan.flt`, `mortammo1.flt`, `mortammo2.flt`, `mortgun.flt`, `nanite100.flt`, `nanite2.flt`, `napalmammo.flt`, `napalmgun.flt`, `nuke.flt`, `nukeammo.flt`, `sonicammo.flt`, `sonicgun.flt`
- `data\common\multi_bft\model`: `bft_multi.flt`, `regenerate.flt`, `vtol.flt`
- `data\effects\models`: `arc.flt`, `ash_dirt.flt`, `beam_red.flt`, `beam_yel.flt`, `bftmbrst.flt`, `bftsplsh.flt`, `boxexp.flt`, `bsmoke_ring.flt`, `bsplsh.flt`, `bubbles.flt`, `bxcrsh.flt`, `bxxpld.flt`, `deadman1.flt`, `dmsquish1.flt`, `erfpg_mzl.flt`, `exhaust.flt`, `exhaustfade.flt`, `exp_blue.flt`, `exp_fireup.flt`, `exp_medium.flt`, `exp_yellow.flt`, `expblueparts.flt`, `expbunk.flt`, `expcrate.flt`, `expgrnmed.flt`, `expgrnparts.flt`, `expnuke.flt`, `exporangeparts.flt`, `expredmed.flt`, `expredparts.flt`, `expsmorangeparts.flt`, `expyelparts.flt`, `fire1.flt`, `fire_bft.flt`, `fire_hulk.flt`, `fire_trail.flt`, `firering.flt`, `flare_blue.flt`, `flare_orange.flt`, `flare_white.flt`, `freon_fo.flt`, `freon_shatter.flt`, `generic6.flt`, `gsxpld.flt`, `he_dirt.flt`, `he_morta.flt`, `hulk_big.flt`, `hulk_small.flt`, `kill_arc.flt`, `kill_lasdes.flt`, `lav_dirt.flt`, `lg_impact.flt`, `lite_ref.flt`, `litening.flt`, `md_impact.flt`, `mine_ph1.flt`, `mine_ph2.flt`, `mine_re1.flt`, `mine_re2.flt`, `miss_launch.flt`, `miss_lo.flt`, `miss_tg.flt`, `muzzleburst.flt`, `napalm.flt`, `nuke_lo.flt`, `nuke_tg.flt`, `pchute.flt`, `redsprks.flt`, `rfpg_grn.flt`, `rfpg_mzl.flt`, `rfpg_red.flt`, `ric_blue.flt`, `ric_green.flt`, `ric_red.flt`, `rsmoke_ring.flt`, `sizzle.flt`, `sm_impact.flt`, `smktrl1.flt`, `smoke1.flt`, `smoke2.flt`, `smoke_damage.flt`, `smoke_destroy.flt`, `smoke_trail1.flt`, `smxplsn.flt`, `snow_dirt.flt`, `sonicfly.flt`, `spark_blue.flt`, `spark_green.flt`, `spark_orange.flt`, `spark_red.flt`, `spl_dam.flt`, `splash1.flt`, `sulph_dirt.flt`, `videxp.flt`, `wake.flt`, `watblast.flt`, `watexp.flt`, `wsmoke_ring.flt`, `xplflare.flt`
- `data\m1\models`: `barrel.flt`, `box2.flt`, `cavefire.flt`, `caveruss1.flt`, `crbox.flt`, `floodlight.flt`, `frcgen.flt`, `fuel.flt`, `fuel1.flt`, `gscn.flt`, `gullfly.flt`, `gullfly2.flt`, `hivolt.flt`, `holo.flt`, `hshit1.flt`, `liteh.flt`, `m1.flt`, `m1gate1.flt`, `m1strut.flt`, `nbsign.flt`, `ohead1.flt`, `palm01.flt`, `palm02.flt`, `palm03.flt`, `palm04.flt`, `palm05.flt`, `palm06.flt`, `palm07.flt`, `palm08.flt`, `pbrlb.flt`, `pier.flt`, `pier_2.flt`, `pierbom.flt`, `pierbox.flt`, `piercrn.flt`, `piergull.flt`, `piergull_2.flt`, `powerbay.flt`, `prtires.flt`, `prtires_2.flt`, `pturret.flt`, `rktbody.flt`, `rktdra.flt`, `rktdrb.flt`, `rktpad.flt`, `samsite1.flt`, `sandbag.flt`, `sandbag1.flt`, `shak.flt`, `smkring.flt`, `tractor.flt`, `trailer.flt`, `veg1.flt`, `veg1_2.flt`, `veg2.flt`, `veg2_2.flt`, `vw_bus.flt`, `vw_bus_rebel.flt`, `walldes1.flt`, `walldes2.flt`
- `data\m1\models\bft`: `bft_m1.flt`, `comanche.flt`, `drone.flt`, `hturret.flt`, `lockon.flt`, `minelay.flt`, `ntank.flt`, `regenerate.flt`, `rumv.flt`, `scout.flt`, `trenchr.flt`, `vtol.flt`
- `data\m2\models`: `bouy1.flt`, `bridge2.flt`, `bunker1.flt`, `bunker2.flt`, `canalend.flt`, `combox1.flt`, `combox2.flt`, `combox3.flt`, `comfence.flt`, `comradar.flt`, `comstat.flt`, `comstat1.flt`, `digger.flt`, `doorchp.flt`, `doorfac.flt`, `doorla.flt`, `doorlb.flt`, `facbrdg.flt`, `faccart1.flt`, `faccart2.flt`, `facflow.flt`, `facgen.flt`, `facpow.flt`, `facsmk.flt`, `fturret.flt`, `gardgate.flt`, `gardgate2.flt`, `grflash.flt`, `helip.flt`, `labbx2.flt`, `labfan.flt`, `labfan2.flt`, `labwall.flt`, `m2.flt`, `machine.flt`, `maingate.flt`, `minecart.flt`, `minepipe.flt`, `nturret.flt`, `nturret1.flt`, `nturret2.flt`, `pcore.flt`, `pdwall1.flt`, `pkeys.flt`, `powdoor.flt`, `pturret.flt`, `samsite1.flt`, `warn10.flt`, `warn11.flt`
- `data\m2\models\bft`: `amphib.flt`, `bft_m2.flt`, `comanche.flt`, `drone.flt`, `ftank.flt`, `hturret.flt`, `lockon.flt`, `ltank.flt`, `minelay.flt`, `ntank.flt`, `regenerate.flt`, `rumv.flt`, `scout.flt`, `trenchr.flt`, `vtol.flt`
- `data\m3\models`: `b_pipe01.flt`, `b_pipe02.flt`, `b_pipe03.flt`, `b_pipe05.flt`, `b_pipe06.flt`, `boilelev1.flt`, `bovalve.flt`, `capcrane.flt`, `conegate.flt`, `ctrdish.flt`, `decks.flt`, `frntbrdg.flt`, `gtherm.flt`, `helip.flt`, `hoverarm.flt`, `lavagate.flt`, `lavbub.flt`, `lavcone.flt`, `lavpot.flt`, `left1.flt`, `lturret.flt`, `m3.flt`, `mgstrut.flt`, `nturret.flt`, `pturret.flt`, `right1.flt`, `tower.flt`
- `data\m3\models\bft`: `amphib.flt`, `arcsaber.flt`, `bft_m3.flt`, `comanche.flt`, `drone.flt`, `ftank.flt`, `hov.flt`, `hturret.flt`, `lockon.flt`, `ltank.flt`, `minelay.flt`, `ntank.flt`, `regenerate.flt`, `rumv.flt`, `scout.flt`, `trenchr.flt`, `vtol.flt`
- `data\m4\models`: `arcturt.flt`, `bigbridge.flt`, `bldg01.flt`, `bldg02.flt`, `bldg03.flt`, `cathbrdg.flt`, `cathdrl.flt`, `chemarch.flt`, `chempipl.flt`, `chemplnt.flt`, `chemvlv.flt`, `dambrok.flt`, `damface.flt`, `endleft.flt`, `endright.flt`, `finalerock.flt`, `fntn.flt`, `fturret.flt`, `fueltank.flt`, `gargoyle.flt`, `grating.flt`, `handcar.flt`, `housruin1.flt`, `hydrant.flt`, `m4.flt`, `middoor.flt`, `midsect.flt`, `pbrlb.flt`, `pipcon1.flt`, `rail_dam.flt`, `railwall.flt`, `rckcontrol1.flt`, `rdrail11.flt`, `rdrail21.flt`, `rdrail31.flt`, `rdrail41.flt`, `rockt.flt`, `rrties.flt`, `ruin01.flt`, `ruin02.flt`, `signal.flt`, `st_bridg.flt`, `stainwin.flt`, `stk1.flt`, `tower.flt`, `tower2.flt`, `tri_arch.flt`, `walldes1.flt`, `walturm4.flt`, `warefill.flt`, `warehous1.flt`, `warehous2.flt`, `warehous3.flt`, `warehous4.flt`
- `data\m4\models\bft`: `amphib.flt`, `arcsaber.flt`, `bft_m4.flt`, `comanche.flt`, `drone.flt`, `ftank.flt`, `hov.flt`, `hturret.flt`, `lockon.flt`, `ltank.flt`, `minelay.flt`, `ntank.flt`, `rcar1.flt`, `regenerate.flt`, `rumv.flt`, `scout.flt`, `sonic.flt`, `train.flt`, `trenchr.flt`, `trnsplsh.flt`, `tturret.flt`, `vtol.flt`
- `data\m5\models`: `arcturt.flt`, `btundr1.flt`, `btundr1_2.flt`, `btundr2.flt`, `cloncage.flt`, `clone_burger.flt`, `clonedoor.flt`, `cloneship1.flt`, `frcfield.flt`, `frcsub.flt`, `helip.flt`, `icedoor1.flt`, `icedoor2.flt`, `icolumn1.flt`, `icolumn2.flt`, `icolumn3.flt`, `lturret.flt`, `lzsuz1.flt`, `m5.flt`, `pcover.flt`, `powerstat.flt`, `psbox1.flt`, `psmain1.flt`, `psmain2.flt`, `pspipe1.flt`, `pstunnel.flt`, `ptrig1.flt`, `pturret.flt`, `radiotwr.flt`, `scontain1.flt`, `scontain2.flt`, `scontain3.flt`, `scontain4.flt`, `sonictur.flt`, `subarm.flt`, `subelev.flt`
- `data\m5\models\bft`: `amphib.flt`, `arcsaber.flt`, `bft_m5.flt`, `comanche.flt`, `drone.flt`, `ftank.flt`, `hov.flt`, `hturret.flt`, `lockon.flt`, `ltank.flt`, `minelay.flt`, `ntank.flt`, `regenerate.flt`, `rumv.flt`, `scout.flt`, `sonic.flt`, `sub.flt`, `trenchr.flt`, `vtol.flt`
- `data\m6\models`: `a1boss.flt`, `a1dflctr.flt`, `a2doorb.flt`, `a2doors.flt`, `a2gun.flt`, `a3boss.flt`, `a3door.flt`, `a3wind1.flt`, `bldg01.flt`, `bldg02.flt`, `bldg03.flt`, `bosprt1.flt`, `bosprt2.flt`, `bosprt3.flt`, `bosprt4.flt`, `boss2.flt`, `braintur.flt`, `cell.flt`, `celldest.flt`, `cells.flt`, `citybrl.flt`, `citybrl_2.flt`, `clontree1.flt`, `clontree2.flt`, `clontree3.flt`, `cluster1.flt`, `cluster2.flt`, `cluster3.flt`, `curb1.flt`, `curb2.flt`, `curb3.flt`, `cwall1.flt`, `cwall2.flt`, `cwall24.flt`, `cwall25.flt`, `cwall26.flt`, `cwall3.flt`, `cwall4.flt`, `cwall5.flt`, `cwall6.flt`, `cwall7.flt`, `cwall8.flt`, `cwall9.flt`, `dcube.flt`, `dmpstr.flt`, `dr02.flt`, `gen_box.flt`, `hydrant.flt`, `lturret.flt`, `m6.flt`, `phone.flt`, `polgat11.flt`, `pwraltr.flt`, `ruin01.flt`, `ruin02.flt`, `slitbdy1.flt`, `sontrig1.flt`, `stplite.flt`, `stubpost.flt`, `tport7.flt`, `wlpart1.flt`, `wlpart10.flt`, `wlpart11.flt`, `wlpart2.flt`, `wlpart3.flt`, `wlpart4.flt`, `xposwall.flt`
- `data\m6\models\bft`: `amphib.flt`, `arcsaber.flt`, `bft_m6.flt`, `comanche.flt`, `drone.flt`, `ftank.flt`, `hov.flt`, `hturret.flt`, `lasdesig.flt`, `lockon.flt`, `ltank.flt`, `minelay.flt`, `ntank.flt`, `regenerate.flt`, `rumv.flt`, `scout.flt`, `sonic.flt`, `sub.flt`, `trenchr.flt`, `vtol.flt`
- `data\m7\models`: `bridgem7.flt`, `bunkerm7.flt`, `canendm7.flt`, `m7.flt`
- `data\m8\models`: `cratbrdg11.flt`, `cratbrdg21.flt`, `cratbrdg31.flt`, `cratbrdg41.flt`, `decks.flt`, `fortbrdg1.flt`, `fortbrdg2.flt`, `fortbrdg3.flt`, `frntbrdg.flt`, `m8.flt`, `mgstrut.flt`
- `data\m9\models`: `arrowl.flt`, `arrowr.flt`, `barrier1.flt`, `cbarrel.flt`, `cbarrier1.flt`, `chaybale.flt`, `chkfinish.flt`, `chkpoint1.flt`, `chkpoint2.flt`, `chkpoint3.flt`, `chkstart1.flt`, `haybale.flt`, `hbarrel.flt`, `m9.flt`, `scurve.flt`
- `data\m10\models`: `bballhoop.flt`, `bbdoor1.flt`, `bbdoor2.flt`, `bbdoor3.flt`, `citybarrel.flt`, `dumpster.flt`, `fireindahood.flt`, `hydrant.flt`, `m10.flt`, `phone.flt`, `slightbox.flt`, `snakesign.flt`, `snaketooth1.flt`, `snaketooth2.flt`, `stoplight.flt`, `tower.flt`
- `data\m11\models`: `m11.flt`, `m11_crate1.flt`, `m11_crate2.flt`, `m11_crate3.flt`, `m11_ice1.flt`, `m11_ice2.flt`, `m11_ice3.flt`
- `data\m12\models`: `endleft.flt`, `endright.flt`, `m12.flt`, `middoor.flt`, `midsect.flt`, `vendude1.flt`, `warefill.flt`, `warehous1.flt`, `warehous2.flt`, `warehous3.flt`, `warehous4.flt`
- `data\m13\models`: `clontree1.flt`, `clontree2.flt`, `clontree3.flt`, `cwall1.flt`, `cwall2.flt`, `cwall24.flt`, `cwall25.flt`, `cwall26.flt`, `cwall3.flt`, `cwall4.flt`, `cwall5.flt`, `cwall6.flt`, `cwall7.flt`, `cwall8.flt`, `cwall9.flt`, `m13.flt`, `walldes1.flt`, `walldes2.flt`, `wlpart1.flt`, `wlpart10.flt`, `wlpart11.flt`, `wlpart2.flt`, `wlpart3.flt`, `wlpart4.flt`, `xposwall.flt`

</details>

<details><summary>Textures named by the scripts</summary>

- `common.gw`: `lflare1.tif`, `lflare2.tif`, `lflare3.tif`, `lflare4.tif`
- `tex_fx.gw`: `blst0001.tif`, `blst0003.tif`, `blst0005.tif`, `fire101.tif`, `fire102.tif`, `fire103.tif`, `fire104.tif`, `fire105.tif`, `fire106.tif`, `fire107.tif`, `fire108.tif`, `fire109.tif`, `fire110.tif`, `fire111.tif`, `fire112.tif`, `firing01.tif`, `firing03.tif`, `firing05.tif`, `heatshm1.tif`, `heatshm2.tif`, `heatshm3.tif`, `nuke00.tif`, `nuke01.tif`, `nuke02.tif`, `nuke03.tif`, `nuke04.tif`, `nuke05.tif`, `nuke06.tif`, `nuke07.tif`, `nuke08.tif`, `nuke09.tif`, `nuke10.tif`, `nuke11.tif`, `nuke12.tif`, `nuke13.tif`, `nuke14.tif`, `nuke15.tif`, `nuke16.tif`, `nuke17.tif`, `nuke18.tif`, `nuke19.tif`, `nuke20.tif`, `pock1.tif`, `pock2.tif`, `pock3.tif`, `regen01.tif`, `regen05.tif`, `tanktrak.tif`, `tanktrak1.tif`, `tanktrak2.tif`, `tanktrak3.tif`, `tsmok1.tif`, `tsmok2.tif`, `tsmok3.tif`, `turb01.tif`, `turb02.tif`, `turb03.tif`, `turb04.tif`, `turb05.tif`, `turb06.tif`, `turb07.tif`, `turb08.tif`, `turb09.tif`, `turb10.tif`
- `tex_fxm1.gw`: `gfly01.tif`, `gfly02.tif`, `gfly03.tif`, `gfly04.tif`, `rokt01.tif`, `rokt02.tif`, `rokt03.tif`, `rokt04.tif`, `rokt05.tif`, `rokt06.tif`, `surf01.tif`, `surf02.tif`, `surf03.tif`, `surf04.tif`, `surf05.tif`, `surf06.tif`, `surf07.tif`, `surf08.tif`, `surf09.tif`, `surf10.tif`, `surf11.tif`, `surf12.tif`, `surf13.tif`
- `tex_fxm11.gw`: `caust01.tif`, `caust02.tif`, `caust03.tif`, `caust04.tif`, `caust05.tif`, `prop01.tif`, `prop02.tif`, `prop03.tif`, `prop04.tif`
- `tex_fxm12.gw`: `refl0000.tif`, `refl0010.tif`, `refl0020.tif`, `refl0030.tif`, `refl0040.tif`, `refl0050.tif`, `refl0060.tif`, `refl0070.tif`, `refl0080.tif`, `rusty01.tif`, `rusty02.tif`, `rusty03.tif`, `rusty04.tif`, `rusty05.tif`, `rusty06.tif`, `rusty07.tif`, `spray01.tif`, `spray02.tif`, `spray03.tif`, `spray04.tif`, `spray05.tif`, `spray06.tif`, `watr01.tif`, `watr02.tif`, `watr03.tif`, `watr04.tif`, `watr05.tif`, `watr06.tif`, `watr07.tif`, `watr08.tif`, `watr09.tif`, `watr10.tif`
- `tex_fxm13.gw`: `bsteam1.tif`, `bsteam2.tif`, `bsteam3.tif`, `bsteam4.tif`, `caust01.tif`, `caust02.tif`, `caust03.tif`, `caust04.tif`, `caust05.tif`, `clwatr01.tif`, `clwatr02.tif`, `clwatr03.tif`, `clwatr04.tif`, `clwatr05.tif`, `clwatr06.tif`, `clwatr07.tif`, `clwatr08.tif`, `clwatr09.tif`, `clwatr10.tif`, `prop01.tif`, `prop02.tif`, `prop03.tif`, `prop04.tif`, `watrct01.tif`, `watrct02.tif`, `watrct03.tif`, `watrct04.tif`, `watrct05.tif`, `watrct06.tif`, `watrct07.tif`, `watrct08.tif`, `watrct09.tif`, `watrct10.tif`, `watrcv01.tif`, `watrcv02.tif`, `watrcv03.tif`, `watrcv04.tif`, `watrcv05.tif`, `watrcv06.tif`, `watrcv07.tif`, `watrcv08.tif`, `watrcv09.tif`, `watrcv10.tif`, `watrp01.tif`, `watrp02.tif`, `watrp03.tif`, `watrp04.tif`, `watrp05.tif`, `watrp06.tif`, `watrp07.tif`, `watrp08.tif`, `watrp09.tif`, `watrp10.tif`
- `tex_fxm2.gw`: `bzzt01.tif`, `bzzt03.tif`, `bzzt05.tif`, `cab02k1.tif`, `cab02k2.tif`, `cab02k3.tif`, `cab02w1.tif`, `cab02w2.tif`, `fact01y1.tif`, `fact01y4.tif`, `lakecy10.tif`, `lakecyc1.tif`, `lakecyc2.tif`, `lakecyc3.tif`, `lakecyc4.tif`, `lakecyc5.tif`, `lakecyc6.tif`, `lakecyc7.tif`, `lakecyc8.tif`, `lakecyc9.tif`, `orb01.tif`, `orb03.tif`, `orb06.tif`, `psteam1.tif`, `psteam2.tif`, `psteam3.tif`, `psteam4.tif`, `reflec10.tif`, `reflect1.tif`, `reflect2.tif`, `reflect3.tif`, `reflect4.tif`, `reflect5.tif`, `reflect6.tif`, `reflect7.tif`, `reflect8.tif`, `reflect9.tif`, `rmolt01.tif`, `rmolt03.tif`, `rmolt05.tif`, `rmolt07.tif`, `rmolt09.tif`, `smk01.tif`, `smk02.tif`, `smk03.tif`, `smk04.tif`, `smk05.tif`, `smk06.tif`, `warn01.tif`, `warn02.tif`
- `tex_fxm3.gw`: `cgate02.tif`, `cgate10.tif`, `hovr12a.tif`, `hovr12b.tif`, `hovr12c.tif`, `hovr12d.tif`, `lav00001.tif`, `lav00002.tif`, `lav00003.tif`, `lav00004.tif`, `lav00005.tif`, `lav00006.tif`, `lav00007.tif`, `lav00008.tif`, `lav00009.tif`, `lav00010.tif`, `lav00011.tif`, `lav00012.tif`, `lav00013.tif`, `lav00014.tif`, `lav00015.tif`, `lav00016.tif`, `lav00017.tif`, `lavylw01.tif`, `lavylw02.tif`, `lavylw03.tif`, `lavylw04.tif`, `lavylw05.tif`, `lavylw06.tif`, `lavylw07.tif`, `lavylw08.tif`, `lavylw09.tif`, `lavylw10.tif`, `lavylw11.tif`, `lavylw12.tif`, `lavylw13.tif`, `lavylw14.tif`, `lavylw15.tif`, `lavylw16.tif`, `lavylw17.tif`, `vcap14.tif`, `vcap15.tif`, `vcap16.tif`, `vcap17.tif`, `vcap30.tif`, `vcap31.tif`, `vcap32.tif`, `vcap33.tif`, `weld01.tif`, `weld02.tif`, `weld03.tif`
- `tex_fxm4.gw`: `chem07.tif`, `chem07b.tif`, `refl0000.tif`, `refl0010.tif`, `refl0020.tif`, `refl0030.tif`, `refl0040.tif`, `refl0050.tif`, `refl0060.tif`, `refl0070.tif`, `refl0080.tif`, `rokt01.tif`, `rokt02.tif`, `rokt03.tif`, `rokt04.tif`, `rokt05.tif`, `rokt06.tif`, `rusty01.tif`, `rusty02.tif`, `rusty03.tif`, `rusty04.tif`, `rusty05.tif`, `rusty06.tif`, `rusty07.tif`, `smk01.tif`, `smk02.tif`, `smk03.tif`, `smk04.tif`, `smk05.tif`, `smk06.tif`, `watr01.tif`, `watr02.tif`, `watr03.tif`, `watr04.tif`, `watr05.tif`, `watr06.tif`, `watr07.tif`, `watr08.tif`, `watr09.tif`, `watr10.tif`
- `tex_fxm5.gw`: `alien01.tif`, `alien02.tif`, `alien03.tif`, `alien04.tif`, `alien05.tif`, `alien06.tif`, `alien07.tif`, `alien08.tif`, `alien09.tif`, `caust01.tif`, `caust02.tif`, `caust03.tif`, `caust04.tif`, `caust05.tif`, `prop01.tif`, `prop02.tif`, `prop03.tif`, `prop04.tif`, `subsh17a.tif`, `subsh17b.tif`, `subsh17c.tif`
- `tex_fxm6.gw`: `bsteam1.tif`, `bsteam2.tif`, `bsteam3.tif`, `bsteam4.tif`, `caust01.tif`, `caust02.tif`, `caust03.tif`, `caust04.tif`, `caust05.tif`, `clwatr01.tif`, `clwatr02.tif`, `clwatr03.tif`, `clwatr04.tif`, `clwatr05.tif`, `clwatr06.tif`, `clwatr07.tif`, `clwatr08.tif`, `clwatr09.tif`, `clwatr10.tif`, `prop01.tif`, `prop02.tif`, `prop03.tif`, `prop04.tif`, `watrcar01.tif`, `watrcar02.tif`, `watrcar03.tif`, `watrcar04.tif`, `watrcar05.tif`, `watrcar06.tif`, `watrcar07.tif`, `watrcar08.tif`, `watrcar09.tif`, `watrcar10.tif`, `watrcarv01.tif`, `watrcarv02.tif`, `watrcarv03.tif`, `watrcarv04.tif`, `watrcarv05.tif`, `watrcarv06.tif`, `watrcarv07.tif`, `watrcarv08.tif`, `watrcarv09.tif`, `watrcarv10.tif`, `watrct01.tif`, `watrct02.tif`, `watrct03.tif`, `watrct04.tif`, `watrct05.tif`, `watrct06.tif`, `watrct07.tif`, `watrct08.tif`, `watrct09.tif`, `watrct10.tif`, `watrcv01.tif`, `watrcv02.tif`, `watrcv03.tif`, `watrcv04.tif`, `watrcv05.tif`, `watrcv06.tif`, `watrcv07.tif`, `watrcv08.tif`, `watrcv09.tif`, `watrcv10.tif`, `watrp01.tif`, `watrp02.tif`, `watrp03.tif`, `watrp04.tif`, `watrp05.tif`, `watrp06.tif`, `watrp07.tif`, `watrp08.tif`, `watrp09.tif`, `watrp10.tif`
- `tex_fxm7.gw`: `lakecy10.tif`, `lakecyc1.tif`, `lakecyc2.tif`, `lakecyc3.tif`, `lakecyc4.tif`, `lakecyc5.tif`, `lakecyc6.tif`, `lakecyc7.tif`, `lakecyc8.tif`, `lakecyc9.tif`
- `tex_fxm9.gw`: `surf01.tif`, `surf02.tif`, `surf03.tif`, `surf04.tif`, `surf05.tif`, `surf06.tif`, `surf07.tif`, `surf08.tif`, `surf09.tif`, `surf10.tif`, `surf11.tif`, `surf12.tif`, `surf13.tif`
- `weapons.gw`: `pock1.tif`, `pock2.tif`, `pock3.tif`

</details>

<details><summary>Textures by folder</summary>

- `data\common\effects\textures`: `ash_dirt.tif`, `bardest.tif`, `blst0001.tif`, `blst0003.tif`, `blst0005.tif`, `boxexp01.tif`, `boxexp02.tif`, `boxexp03.tif`, `boxexp04.tif`, `boxexp05.tif`, `boxexp06.tif`, `boxexp07.tif`, `boxexp08.tif`, `boxexp09.tif`, `boxexp10.tif`, `bsmkring.tif`, `bsmok1.tif`, `bsmok2.tif`, `bsmok3.tif`, `bsplsh02.tif`, `bsplsh04.tif`, `bsplsh06.tif`, `bsplsh08.tif`, `bsplsh10.tif`, `bsplsh12.tif`, `bsplsh14.tif`, `bsplsh16.tif`, `bsplsh18.tif`, `bsplsh20.tif`, `bubble.tif`, `bunk01.tif`, `bunk02.tif`, `bunk03.tif`, `bunk04.tif`, `bunk05.tif`, `bunk06.tif`, `bunk07.tif`, `bunk08.tif`, `bunk09.tif`, `bunk10.tif`, `bunk11.tif`, `bxxpld.tif`, `crate01.tif`, `crate02.tif`, `crate03.tif`, `crate04.tif`, `crate05.tif`, `crate06.tif`, `crate07.tif`, `crate08.tif`, `crate09.tif`, `crate10.tif`, `crate11.tif`, `crate12.tif`, `crate13.tif`, `crate14.tif`, `dsquish1.tif`, `exb01.tif`, `exg01.tif`, `exhaust.tif`, `exp_med01.tif`, `exp_med02.tif`, `exp_med03.tif`, `exp_med04.tif`, `exp_med05.tif`, `exp_med06.tif`, `exp_med07.tif`, `expf01.tif`, `expf02.tif`, `expf03.tif`, `expf04.tif`, `expf05.tif`, `expf06.tif`, `expf07.tif`, `expf08.tif`, `expf09.tif`, `expf10.tif`, `expf11.tif`, `expflash.tif`, `exr01.tif`, `exy01.tif`, `fire101.tif`, `fire102.tif`, `fire103.tif`, `fire104.tif`, `fire105.tif`, `fire106.tif`, `fire107.tif`, `fire108.tif`, `fire109.tif`, `fire110.tif`, `fire111.tif`, `fire112.tif`, `firing01.tif`, `firing03.tif`, `firing05.tif`, `flareblue.tif`, `flarefirey.tif`, `freon_fo.tif`, `lav_dirt.tif`, `ldeshocker.tif`, `ldesphere.tif`, `lflare1.tif`, `lflare2.tif`, `lflare3.tif`, `lflare4.tif`, `lo_nuk01.tif`, `mine_rem.tif`, `mortar.tif`, `mortgun2.tif`, `muzz_grn.tif`, `muzz_red.tif`, `nlitening.tif`, `nuke00.tif`, `nuke01.tif`, `nuke02.tif`, `nuke03.tif`, `nuke04.tif`, `nuke05.tif`, `nuke06.tif`, `nuke07.tif`, `nuke08.tif`, `nuke09.tif`, `nuke10.tif`, `nuke11.tif`, `nuke12.tif`, `nuke13.tif`, `nuke14.tif`, `nuke15.tif`, `nuke16.tif`, `nuke17.tif`, `nuke18.tif`, `nuke19.tif`, `nuke20.tif`, `parachute.tif`, `pock1.tif`, `pock2.tif`, `pock3.tif`, `powerdstb.tif`, `powrdest.tif`, `regen01.tif`, `regen05.tif`, `rokt01.tif`, `rsmkring.tif`, `shatter1.tif`, `shatter2.tif`, `shatter3.tif`, `sizzle.tif`, `snow_dirt.tif`, `sonicfly.tif`, `splashbase.tif`, `splashup.tif`, `sulph_dirt.tif`, `tg_nuk01.tif`, `tg_nuk02.tif`, `tg_nuk03.tif`, `tracer.tif`, `tracer1.tif`, `trnchr2.tif`, `tsmok1.tif`, `tsmok2.tif`, `tsmok3.tif`, `turb01.tif`, `turb02.tif`, `turb03.tif`, `turb04.tif`, `turb05.tif`, `turb06.tif`, `turb07.tif`, `turb08.tif`, `turb09.tif`, `turb10.tif`, `vid01.tif`, `vid02.tif`, `vid03.tif`, `vid04.tif`, `vid05.tif`, `vid06.tif`, `vid07.tif`, `vid08.tif`, `vid09.tif`, `watexp01.tif`, `watexp02.tif`, `watexp03.tif`, `watexp04.tif`, `watexp05.tif`, `watexp06.tif`, `watexp07.tif`, `watexp08.tif`, `watexp09.tif`, `watexp10.tif`, `watexp11.tif`, `wsmkring.tif`, `wsmok1.tif`, `wsmok2.tif`, `wsmok3.tif`
- `data\common\multi_bft\textures`: `caust01.tif`, `caust02.tif`, `caust03.tif`, `caust04.tif`, `caust05.tif`, `hovr.tif`, `prop01.tif`, `prop02.tif`, `prop03.tif`, `prop04.tif`, `sub03.tif`, `sub04.tif`, `sub05.tif`, `sub06.tif`, `sub07.tif`, `tankback.tif`, `tankbotm.tif`, `tankgn01.tif`, `tankgn02.tif`, `tankgn03.tif`, `tankgn04.tif`, `tankgn05.tif`, `tanklite.tif`, `tankpon1.tif`, `tankpon2.tif`, `tankside.tif`, `tanktop.tif`, `tanktrks.tif`, `tankwing.tif`
- `data\common\textures`: `ammoarcgun.tif`, `ammolasdes.tif`, `ammolaser.tif`, `ammolock.tif`, `ammosonic.tif`, `amphchip.tif`, `amphib01.tif`, `amphib02.tif`, `amphib03.tif`, `amphib04.tif`, `amphib05.tif`, `amphib06.tif`, `amphib07.tif`, `amphib09.tif`, `arcgun01.tif`, `arcgun02.tif`, `arcs01.tif`, `arcs02.tif`, `arcs03.tif`, `arcs04.tif`, `arcs05.tif`, `arcs06.tif`, `arctur01.tif`, `arctur02.tif`, `arctur03.tif`, `arctur04.tif`, `arctur05.tif`, `arctur06.tif`, `beamred.tif`, `beamyelo.tif`, `brntur01.tif`, `com01.tif`, `com02.tif`, `com03.tif`, `com04.tif`, `com05.tif`, `com06.tif`, `com07.tif`, `com08.tif`, `d_wtur01.tif`, `drone01.tif`, `drone02.tif`, `drone03.tif`, `drone04.tif`, `drone05.tif`, `drone06.tif`, `drone07.tif`, `drone08.tif`, `drone09.tif`, `drone10.tif`, `erfpgscl.tif`, `esub01.tif`, `esub02.tif`, `esub03.tif`, `esub04.tif`, `esub05.tif`, `esub06.tif`, `esub07.tif`, `esub08.tif`, `esub09.tif`, `freon01.tif`, `freon03.tif`, `freon04.tif`, `freon05.tif`, `freon06.tif`, `freonammo.tif`, `ftur01.tif`, `ftur02.tif`, `ftur03.tif`, `ftur04.tif`, `ftur05.tif`, `ftur07.tif`, `ftur08.tif`, `ftur09.tif`, `grey.tif`, `gun_fre1.tif`, `gun_nap1.tif`, `gun_nap4.tif`, `gun_nap5.tif`, `gun_son1.tif`, `gun_son2.tif`, `gun_son3.tif`, `hand1.tif`, `hand2.tif`, `heatshm1.tif`, `heatshm2.tif`, `heatshm3.tif`, `hovchp01.tif`, `hovchp02.tif`, `hover01.tif`, `hover02.tif`, `hover03.tif`, `hover04.tif`, `hover05.tif`, `hover06.tif`, `hover07.tif`, `laz01.tif`, `laz02.tif`, `laz03.tif`, `laz04.tif`, `laz05.tif`, `laztrak1.tif`, `laztrak2.tif`, `ldgun01.tif`, `ldgun02.tif`, `ldgun03.tif`, `ldgun04.tif`, `ldtank01.tif`, `ldtank02.tif`, `ldtank03.tif`, `ldtank04.tif`, `ldtank05.tif`, `ldtank06.tif`, `lgun01.tif`, `lgun02.tif`, `lgun03.tif`, `lgun04.tif`, `lockon01.tif`, `lockon02.tif`, `lockon03.tif`, `lockon04.tif`, `lockon05.tif`, `lockon06.tif`, `lockon07.tif`, `lockon08.tif`, `lockon09.tif`, `lockon10.tif`, `lockon11.tif`, `lockon12.tif`, `lockon13.tif`, `locktrak.tif`, `lomissammo1.tif`, `lomissammo2.tif`, `mine_ph1.tif`, `mine_ph2.tif`, `minelay1.tif`, `minelay2.tif`, `minelay3.tif`, `minelay4.tif`, `missil01.tif`, `missil02.tif`, `missil03.tif`, `missil04.tif`, `missil05.tif`, `mort01.tif`, `mort02.tif`, `mort03.tif`, `mortammo1.tif`, `mortammo2.tif`, `nanite.tif`, `nanite100.tif`, `nanocan.tif`, `napammo.tif`, `napm01.tif`, `napm02.tif`, `napm03.tif`, `napm04.tif`, `napm05.tif`, `napm06.tif`, `ntur01.tif`, `ntur01g.tif`, `ntur02.tif`, `ntur02g.tif`, `ntur03.tif`, `ntur03g.tif`, `ntur04.tif`, `ntur04g.tif`, `ntur05.tif`, `ntur06.tif`, `ntur07.tif`, `ntur08.tif`, `ntur09.tif`, `ntur10.tif`, `ntur11.tif`, `phemgun1.tif`, `ptlite01.tif`, `ptur01.tif`, `ptur02.tif`, `ptur03.tif`, `ptur04.tif`, `ptur05.tif`, `ptur06.tif`, `ptur07.tif`, `ptur08.tif`, `ptur09.tif`, `ptur_d01.tif`, `ptur_d02.tif`, `ptur_d03.tif`, `qsnd01.tif`, `qsnd02.tif`, `qsnd03.tif`, `qsnd04.tif`, `qsnd05.tif`, `qsnd06.tif`, `qsnd07.tif`, `rarm.tif`, `rbicep.tif`, `rcalf.tif`, `rfpgscl.tif`, `rhelm.tif`, `rthigh.tif`, `rtorso.tif`, `sam01.tif`, `sam02.tif`, `sam03.tif`, `sam04.tif`, `sam05.tif`, `sam06.tif`, `sam07.tif`, `sam08.tif`, `sam09.tif`, `sam10.tif`, `sam11.tif`, `sam12.tif`, `scout01.tif`, `scout02.tif`, `scout03.tif`, `scout04.tif`, `scout05.tif`, `scout06.tif`, `scout07.tif`, `sonic01.tif`, `sonic02.tif`, `sonic03.tif`, `sonic04.tif`, `sonic05.tif`, `sonic06.tif`, `sonic07.tif`, `sonic08.tif`, `sonic09.tif`, `sonic10.tif`, `sontur01.tif`, `sontur02.tif`, `sontur03.tif`, `sontur04.tif`, `sontur05.tif`, `sontur06.tif`, `subchp01.tif`, `tanktrak.tif`, `tanktrak1.tif`, `tanktrak2.tif`, `tanktrak3.tif`, `trnch01.tif`, `trnch02.tif`, `trnch03.tif`, `trnch04.tif`, `trnch05.tif`, `trnch06.tif`, `trnch09.tif`, `trnch10.tif`, `trnch11.tif`, `vtol01.tif`, `vtol02.tif`, `vtol03.tif`, `vtol04.tif`, `vtol05.tif`, `vtol06.tif`, `vtol07.tif`, `vtol08.tif`, `vtol09.tif`, `vtol10.tif`, `vtol11.tif`, `vtol12.tif`, `vtol13.tif`, `vtol14.tif`, `vtol15.tif`, `wtur01.tif`, `wtur02.tif`, `wtur03.tif`, `wtur04.tif`
- `data\m10\textures`: `barrier.tif`, `barrier1.tif`, `barrier2.tif`, `bcourt01.tif`, `bcourt02.tif`, `bhoop.tif`, `bldg_a01.tif`, `bldg_a02.tif`, `bldg_b01.tif`, `bldg_b02.tif`, `bldg_c01.tif`, `bldg_c02.tif`, `bldg_d01.tif`, `bldg_e01.tif`, `bldg_f01.tif`, `bldg_g01.tif`, `bldg_h01.tif`, `bldg_h02.tif`, `bldg_i01.tif`, `bldg_j01.tif`, `bldg_k01.tif`, `bldg_l01.tif`, `bldg_l02.tif`, `bldg_top.tif`, `citybrl.tif`, `cratrcrd.tif`, `croad01.tif`, `croad02.tif`, `dumpster.tif`, `dusksky1.tif`, `dusksky2.tif`, `dusksky3.tif`, `elev04.tif`, `elev05.tif`, `elev06.tif`, `elev08.tif`, `elev09.tif`, `elev10.tif`, `elev11.tif`, `elev12.tif`, `elev14.tif`, `hydrant.tif`, `signsup.tif`, `slightback.tif`, `slightbase.tif`, `slightpole.tif`, `snake1.tif`, `snake2.tif`, `snake3.tif`, `sp_a.tif`, `sp_e.tif`, `sp_i.tif`, `sp_k.tif`, `sp_n.tif`, `sp_p.tif`, `sp_s.tif`, `sp_t.tif`, `stoplight.tif`, `tbooth.tif`, `tile01.tif`, `tile02.tif`, `tower01.tif`
- `data\m11\textures`: `crate_dest.tif`, `cratersnow.tif`, `ice_ocean.tif`, `icecol1.tif`, `icemound.tif`, `m11_berg.tif`, `m11_ice1.tif`, `m11_oceanbot.tif`, `m11_wall1.tif`, `m11_wall3.tif`, `m11_water1.tif`, `m11_water2.tif`, `m11_water3.tif`, `m11_water4.tif`, `m11crate01.tif`, `m11crate02.tif`, `m11sky.tif`
- `data\m12\textures`: `brcktrim.tif`, `brick02.tif`, `brick05.tif`, `chem15.tif`, `chem19.tif`, `damflow.tif`, `grass1.tif`, `m4canl3.tif`, `medwall1.tif`, `rail01.tif`, `refl0000.tif`, `refl0010.tif`, `refl0020.tif`, `refl0030.tif`, `refl0040.tif`, `refl0050.tif`, `refl0060.tif`, `refl0070.tif`, `refl0080.tif`, `road1.tif`, `road2.tif`, `roadtdir.tif`, `rusty01.tif`, `rusty02.tif`, `rusty03.tif`, `rusty04.tif`, `rusty05.tif`, `rusty06.tif`, `rusty07.tif`, `skym4.tif`, `streets1.tif`, `tryard1.tif`, `tryard3.tif`, `tryard31.tif`, `ven1.tif`, `ven2.tif`, `ven3.tif`, `ven31.tif`, `ven4.tif`, `ven41.tif`, `ven5.tif`, `ven6.tif`, `ven61.tif`, `ven7.tif`, `ven71.tif`, `ven8.tif`, `ven81.tif`, `ven9.tif`, `ven91.tif`, `vendude1.tif`, `ware02.tif`, `ware04.tif`, `watr01.tif`, `watr02.tif`, `watr03.tif`, `watr04.tif`, `watr05.tif`, `watr06.tif`, `watr07.tif`, `watr08.tif`, `watr09.tif`, `watr10.tif`
- `data\m13\textures`: `bwall01.tif`, `bwall04.tif`, `bwallbad.tif`, `cave01.tif`, `cave02.tif`, `cave03.tif`, `cave04.tif`, `cave05.tif`, `cavebh1.tif`, `caveceil.tif`, `cavefl1.tif`, `cemcol3.tif`, `cemcol4.tif`, `cemcol5.tif`, `cemcol6.tif`, `cemcol8.tif`, `cemwall6.tif`, `cemwall9.tif`, `clondest.tif`, `clontrbs.tif`, `clontree.tif`, `clontrnk.tif`, `clwatr01.tif`, `clwatr02.tif`, `clwatr03.tif`, `clwatr04.tif`, `clwatr05.tif`, `clwatr06.tif`, `clwatr07.tif`, `clwatr08.tif`, `clwatr09.tif`, `clwatr10.tif`, `cratercave.tif`, `cratersand.tif`, `frcgate1.tif`, `intun1.tif`, `intun5.tif`, `m1cliff1.tif`, `mnsand1.tif`, `mnsroad9.tif`, `nceil.tif`, `piptil10.tif`, `piptil11.tif`, `piptil12.tif`, `piptil13.tif`, `piptile1.tif`, `piptile5.tif`, `piptile6.tif`, `piptile7.tif`, `piptile8.tif`, `piptile9.tif`, `polbeam1.tif`, `poldest1.tif`, `polite1.tif`, `polite2.tif`, `polroad1.tif`, `polroad3.tif`, `polroad4.tif`, `polroad5.tif`, `polwall1.tif`, `polwall2.tif`, `polwall4.tif`, `polwall6.tif`, `polwall7.tif`, `polwall8.tif`, `powrstak.tif`, `powrwal2.tif`, `powrwal3.tif`, `reta10.tif`, `skym6.tif`, `tele17.tif`, `tele31.tif`, `tele33.tif`, `tele34.tif`, `tele36.tif`, `tele51.tif`, `tele54.tif`, `tfram1.tif`, `tp1sc.tif`, `tp2sc.tif`, `tp3sc.tif`, `tp4sc.tif`, `tp5sc.tif`, `tran01.tif`, `tran03.tif`, `tran06.tif`, `tran07.tif`, `tran08.tif`, `tran09.tif`, `tran10.tif`, `tran11.tif`, `tran12.tif`, `tstatic.tif`, `watrct01.tif`, `watrct02.tif`, `watrct03.tif`, `watrct04.tif`, `watrct05.tif`, `watrct06.tif`, `watrct07.tif`, `watrct08.tif`, `watrct09.tif`, `watrct10.tif`, `watrcv01.tif`, `watrcv02.tif`, `watrcv03.tif`, `watrcv04.tif`, `watrcv05.tif`, `watrcv06.tif`, `watrcv07.tif`, `watrcv08.tif`, `watrcv09.tif`, `watrcv10.tif`, `watrp01.tif`, `watrp02.tif`, `watrp03.tif`, `watrp04.tif`, `watrp05.tif`, `watrp06.tif`, `watrp07.tif`, `watrp08.tif`, `watrp09.tif`, `watrp10.tif`
- `data\m1\textures`: `aztec01.tif`, `aztec02.tif`, `aztec03.tif`, `aztec04.tif`, `aztec05.tif`, `aztec06.tif`, `aztec07.tif`, `bar1.tif`, `bigwav01.tif`, `bigwav02.tif`, `bigwav03.tif`, `burnt1.tif`, `burnt2.tif`, `burnt3.tif`, `burnt4.tif`, `bwall01.tif`, `bwall04.tif`, `bwallbad.tif`, `cemwall1.tif`, `cemwall2.tif`, `cemwall3.tif`, `cemwall4.tif`, `cemwall5.tif`, `cemwall6.tif`, `cemwall9.tif`, `comstat4.tif`, `craterroad.tif`, `cratersand.tif`, `dhint1.tif`, `dhint2.tif`, `dhint3.tif`, `dhint4.tif`, `dockp01a.tif`, `dockp01b.tif`, `dockp01c.tif`, `dockp01d.tif`, `dockp01e.tif`, `dockp01g.tif`, `dockp01j.tif`, `dockp01k.tif`, `dockp01l.tif`, `dockp01m.tif`, `dockp01n.tif`, `dockp01o.tif`, `dockp01p.tif`, `dockp01q.tif`, `dockp01r.tif`, `dockp01s.tif`, `dockp01t.tif`, `dockp01u.tif`, `dockp01v2.tif`, `dockp01v4.tif`, `dockp01v5.tif`, `dockp01v6.tif`, `dockp01w1.tif`, `frcgate1.tif`, `frcgate2.tif`, `frcgate3.tif`, `frcgate4.tif`, `frcgate5.tif`, `frcgate6.tif`, `frcgate7.tif`, `frcgate8.tif`, `frcgen1.tif`, `frcgen2.tif`, `frcgen3.tif`, `frcgen4.tif`, `fuel01.tif`, `fuel02.tif`, `fuel03.tif`, `fuel04.tif`, `fuel05.tif`, `fuel06.tif`, `fuel08.tif`, `fuel10.tif`, `fuel11.tif`, `fuel12.tif`, `fuel13.tif`, `fuel16.tif`, `fuel18.tif`, `fueld01.tif`, `fueld02.tif`, `fueld03.tif`, `fueld04.tif`, `fueld05.tif`, `fueld06.tif`, `fueld07.tif`, `fueld08.tif`, `gas6.tif`, `gas9.tif`, `gfly01.tif`, `gfly02.tif`, `gfly03.tif`, `gfly04.tif`, `gull01.tif`, `hel01a.tif`, `hel01b.tif`, `hel01d.tif`, `hel01e.tif`, `hel01f.tif`, `heli02a.tif`, `heli03b.tif`, `heli05a.tif`, `heli12.tif`, `heli13a.tif`, `heli15a.tif`, `heli16a.tif`, `heli17b.tif`, `holobeam.tif`, `holorec1.tif`, `holoside1.tif`, `holoside2.tif`, `holoside3.tif`, `holsheld.tif`, `hotchik1.tif`, `hotchik2.tif`, `hwall2.tif`, `hyroglyf.tif`, `intun1.tif`, `intun2.tif`, `intun5.tif`, `intun6.tif`, `liteh02.tif`, `liteh03.tif`, `m1cliff1.tif`, `m1gate01.tif`, `m1gate03.tif`, `m1gate04.tif`, `m1gate05.tif`, `m1gate06.tif`, `m1gate07.tif`, `m1gate08.tif`, `m1gate09.tif`, `m1gate10.tif`, `m1gate11.tif`, `m1gate12.tif`, `m1gate13.tif`, `m1gate15.tif`, `m1gate16.tif`, `m1gate19.tif`, `m1rock1.tif`, `m1rock2.tif`, `m1sky1.tif`, `mnsand1.tif`, `mnsand2.tif`, `mnsand5.tif`, `mnsrd10.tif`, `mnsroad1.tif`, `mnsroad2.tif`, `mnsroad3.tif`, `mnsroad9.tif`, `nbeach1.tif`, `nbeach2.tif`, `nbeach3.tif`, `nosoilwater.tif`, `ocean1.tif`, `palm01.tif`, `palm02.tif`, `palm03.tif`, `palm04.tif`, `pblb.tif`, `pier.tif`, `powrband.tif`, `powrstak.tif`, `powrwal1.tif`, `powrwal2.tif`, `powrwal3.tif`, `prjctr02.tif`, `prjctr04.tif`, `prjctr06.tif`, `reta01.tif`, `reta03.tif`, `reta05.tif`, `reta06.tif`, `reta07.tif`, `reta10.tif`, `rkt01b.tif`, `rkt01c.tif`, `rkt01d.tif`, `rkt01e.tif`, `rkt01f.tif`, `rkt01g.tif`, `rkt01i.tif`, `rokt02.tif`, `rokt03.tif`, `rokt04.tif`, `rokt05.tif`, `rokt06.tif`, `sandbag.tif`, `sandstan.tif`, `sandtopb.tif`, `sandveg1.tif`, `scliftrn1.tif`, `scliftrn2.tif`, `sechall1.tif`, `sechall2.tif`, `sechall3.tif`, `semi00.tif`, `semi01.tif`, `semi02.tif`, `semi03.tif`, `semi04.tif`, `semi05.tif`, `semi06.tif`, `semi07.tif`, `semi08.tif`, `semi09.tif`, `semi10.tif`, `semi11.tif`, `semi12.tif`, `semi13.tif`, `semi14.tif`, `semi15.tif`, `semi16.tif`, `semi17.tif`, `semi18.tif`, `semi19.tif`, `semi20.tif`, `semi21.tif`, `store01.tif`, `store02.tif`, `surf01.tif`, `surf02.tif`, `surf03.tif`, `surf04.tif`, `surf05.tif`, `surf06.tif`, `surf07.tif`, `surf08.tif`, `surf09.tif`, `surf10.tif`, `surf11.tif`, `surf12.tif`, `surf13.tif`, `tra.tif`, `vw_dest1.tif`, `vw_map01.tif`, `vw_map02.tif`, `vw_map03.tif`, `vw_map04.tif`, `vw_map05.tif`, `vw_map06.tif`
- `data\m1\textures\bft`: `tankback.tif`, `tankbotm.tif`, `tankgn01.tif`, `tankgn02.tif`, `tankgn03.tif`, `tankgn04.tif`, `tankgn05.tif`, `tanklite.tif`, `tankside.tif`, `tanktop.tif`, `tanktrks.tif`, `tankwing.tif`
- `data\m2\textures`: `beach1.tif`, `beam01.tif`, `beam02.tif`, `beam03.tif`, `beam04.tif`, `beam05.tif`, `beam06.tif`, `block01.tif`, `bore01.tif`, `bore02.tif`, `bore03.tif`, `bore04.tif`, `bore05.tif`, `bore06.tif`, `bore07.tif`, `bore08.tif`, `bore09.tif`, `bore10.tif`, `bore11.tif`, `bore12.tif`, `bore15.tif`, `bore16.tif`, `bore17.tif`, `bore18.tif`, `bore20.tif`, `bore21.tif`, `bore22.tif`, `bore24.tif`, `bouyref1.tif`, `bouytop1.tif`, `brain06a.tif`, `brdgdck2.tif`, `brdgdeck.tif`, `brdgrmp.tif`, `brdgrmp2.tif`, `brglok1.tif`, `brglok2a.tif`, `brglok2b.tif`, `brglok2c.tif`, `brigsup1.tif`, `brigsup2.tif`, `brigsup3.tif`, `brigsup4.tif`, `brigsup5.tif`, `brigsup6.tif`, `brigsup7.tif`, `brigsup8.tif`, `brigsup9.tif`, `brkndeck.tif`, `bunker1.tif`, `bunker2.tif`, `bunker3.tif`, `bunker4a.tif`, `burst1.tif`, `bzzt01.tif`, `bzzt03.tif`, `bzzt05.tif`, `cab01b.tif`, `cab01c.tif`, `cab01d.tif`, `cab01e1.tif`, `cab01e2.tif`, `cab01e3.tif`, `cab01e5.tif`, `cab01e6.tif`, `cab01e7.tif`, `cab01e8.tif`, `cab01f.tif`, `cab02a.tif`, `cab02b.tif`, `cab02c.tif`, `cab02d.tif`, `cab02e.tif`, `cab02f.tif`, `cab02g.tif`, `cab02h.tif`, `cab02i.tif`, `cab02j.tif`, `cab02k1.tif`, `cab02k2.tif`, `cab02k3.tif`, `cab02k4.tif`, `cab02k5.tif`, `cab02k6.tif`, `cab02k7.tif`, `cab02l.tif`, `cab02m.tif`, `cab02n.tif`, `cab02o.tif`, `cab02p.tif`, `cab02p1.tif`, `cab02p2.tif`, `cab02p3.tif`, `cab02p4.tif`, `cab02p5.tif`, `cab02p6.tif`, `cab02p7.tif`, `cab02p8.tif`, `cab02p9.tif`, `cab02q.tif`, `cab02q2.tif`, `cab02r.tif`, `cab02s.tif`, `cab02t.tif`, `cab02v1.tif`, `cab02v4.tif`, `cab02v5.tif`, `cab02w1.tif`, `cab02w2.tif`, `cab02w3.tif`, `cab02x.tif`, `cab02y1.tif`, `cab02y2.tif`, `cab03a.tif`, `cab03b.tif`, `cab03c.tif`, `cab03d.tif`, `cab03e.tif`, `cab03g.tif`, `canrock1.tif`, `canyon1.tif`, `canyon5.tif`, `cavern1.tif`, `cavern2.tif`, `cement02.tif`, `cement03.tif`, `cement05.tif`, `comstat1.tif`, `comstat10.tif`, `comstat11.tif`, `comstat12.tif`, `comstat13.tif`, `comstat14.tif`, `comstat15.tif`, `comstat16.tif`, `comstat17.tif`, `comstat18.tif`, `comstat19.tif`, `comstat20.tif`, `comstat4.tif`, `comstat5.tif`, `comstat6.tif`, `comstat7.tif`, `comstat8.tif`, `comstat9.tif`, `craterdirt.tif`, `cratergrass.tif`, `craterroad.tif`, `craterrock.tif`, `digr01.tif`, `digr02.tif`, `digr03.tif`, `digr04.tif`, `digr05.tif`, `digr06.tif`, `digr07.tif`, `digr08.tif`, `droad1.tif`, `fact01a.tif`, `fact01b.tif`, `fact01c.tif`, `fact01d.tif`, `fact01g.tif`, `fact01g1.tif`, `fact01h.tif`, `fact01j.tif`, `fact01j1.tif`, `fact01j2.tif`, `fact01l.tif`, `fact01m.tif`, `fact01p.tif`, `fact01q.tif`, `fact01q1.tif`, `fact01r.tif`, `fact01s.tif`, `fact01s1.tif`, `fact01t.tif`, `fact01t1.tif`, `fact01t2.tif`, `fact01t3.tif`, `fact01u.tif`, `fact01v.tif`, `fact01w.tif`, `fact01w1.tif`, `fact01x1.tif`, `fact01x2.tif`, `fact01x3.tif`, `fact01x4.tif`, `fact01x5.tif`, `fact01x6.tif`, `fact01y1.tif`, `fact01y2.tif`, `fact01y3.tif`, `fact01y4.tif`, `fact01z.tif`, `fact01z1.tif`, `fact01z2.tif`, `fact01z3.tif`, `fact01z3a.tif`, `fact01z4.tif`, `fact01z5.tif`, `fact01z6.tif`, `fact01z7.tif`, `fact01z7a.tif`, `fact01z8.tif`, `fact01z9.tif`, `gate1.tif`, `genf01a.tif`, `genf01b.tif`, `genf01c.tif`, `genf01d.tif`, `genf01e.tif`, `genf01f1.tif`, `genf01f2.tif`, `genf01k.tif`, `genf01m.tif`, `genf01n.tif`, `grass01.tif`, `grate1.tif`, `grate2.tif`, `grate3.tif`, `grate4.tif`, `grated1.tif`, `grdgate2.tif`, `grdgate3.tif`, `grdgate4.tif`, `grdgate5.tif`, `grlite1.tif`, `grlite2.tif`, `grnring.tif`, `hel01b.tif`, `hel01c.tif`, `hel01d.tif`, `hel01e.tif`, `hill1.tif`, `lab01b.tif`, `lab01c.tif`, `lab01d.tif`, `lab01e.tif`, `lab01e1.tif`, `lab01e2.tif`, `lab01f.tif`, `lab01i.tif`, `lab01i3.tif`, `lab01i7.tif`, `lab01j.tif`, `lab01k.tif`, `lab01n.tif`, `lab01o.tif`, `lab01q.tif`, `lab02a.tif`, `lab02c.tif`, `lab02e.tif`, `lab02g.tif`, `lab02h.tif`, `lab02i.tif`, `lab02j.tif`, `labcrate.tif`, `labcrated.tif`, `lakecy10.tif`, `lakecyc1.tif`, `lakecyc2.tif`, `lakecyc3.tif`, `lakecyc4.tif`, `lakecyc5.tif`, `lakecyc6.tif`, `lakecyc7.tif`, `lakecyc8.tif`, `lakecyc9.tif`, `minecar1.tif`, `minecar2.tif`, `minecar3.tif`, `minecar4.tif`, `minecar5.tif`, `minepip1.tif`, `minepip2.tif`, `minepip3.tif`, `minepip4.tif`, `minepit1.tif`, `minetun1.tif`, `minetun2.tif`, `minetun3.tif`, `minetun4.tif`, `minetun5.tif`, `minetun6.tif`, `minetun7.tif`, `minetun8.tif`, `minstrt1.tif`, `minstrt2.tif`, `minstrt3.tif`, `minstrt4.tif`, `minstrt5.tif`, `minstrt6.tif`, `mwall01.tif`, `mwall02.tif`, `mwall04.tif`, `mwall05.tif`, `orb01.tif`, `orb03.tif`, `orb06.tif`, `pad1.tif`, `padedg1.tif`, `padedg2.tif`, `pipe1.tif`, `pipe2.tif`, `psteam1.tif`, `psteam2.tif`, `psteam3.tif`, `psteam4.tif`, `rail1.tif`, `rail2.tif`, `rail3.tif`, `rail4.tif`, `ravine1.tif`, `ravine2.tif`, `redh02.tif`, `redh03.tif`, `redh07a3.tif`, `redh07b.tif`, `redh07c.tif`, `redh07c1.tif`, `redh07d.tif`, `redh07e.tif`, `redh07eh.tif`, `redh07f.tif`, `redh07g.tif`, `redh09a.tif`, `redh09b.tif`, `redh09c.tif`, `redh09d.tif`, `redh09e.tif`, `redh09f.tif`, `redh09g.tif`, `reflec10.tif`, `reflect1.tif`, `reflect2.tif`, `reflect3.tif`, `reflect4.tif`, `reflect5.tif`, `reflect6.tif`, `reflect7.tif`, `reflect8.tif`, `reflect9.tif`, `rmolt01.tif`, `rmolt03.tif`, `rmolt05.tif`, `rmolt07.tif`, `rmolt09.tif`, `rmolts.tif`, `road4.tif`, `road6.tif`, `sand4.tif`, `sand5.tif`, `signback.tif`, `sky2.tif`, `sky2lite.tif`, `smk01.tif`, `smk02.tif`, `smk03.tif`, `smk04.tif`, `smk05.tif`, `smk06.tif`, `spout1.tif`, `stack.tif`, `tunceil1.tif`, `tunroad1.tif`, `tunwall1.tif`, `vid01a.tif`, `vid01b.tif`, `vid01b3.tif`, `vid01c.tif`, `vid01c1.tif`, `vid01c3.tif`, `vid01e.tif`, `vid01f1.tif`, `vid01h.tif`, `vid01i.tif`, `warn01.tif`, `warn02.tif`
- `data\m2\textures\bft`: `tankback.tif`, `tankbotm.tif`, `tankgn01.tif`, `tankgn02.tif`, `tankgn03.tif`, `tankgn04.tif`, `tankgn05.tif`, `tanklite.tif`, `tankpon1.tif`, `tankpon2.tif`, `tankside.tif`, `tanktop.tif`, `tanktrks.tif`, `tankwing.tif`
- `data\m3\textures`: `bascone1.tif`, `boil01.tif`, `boil02.tif`, `boil03.tif`, `boil04.tif`, `boil06.tif`, `boil07.tif`, `boil08.tif`, `boil10.tif`, `boil11.tif`, `boil12.tif`, `boil13.tif`, `boil14.tif`, `boil16.tif`, `boil17a.tif`, `boil17e.tif`, `boil17f.tif`, `boil19.tif`, `boil20.tif`, `boil21.tif`, `boil22.tif`, `boil23.tif`, `boil25.tif`, `boil26a.tif`, `boil26b.tif`, `boil27.tif`, `boil28.tif`, `boil29.tif`, `boil30.tif`, `boil31a.tif`, `boil31b.tif`, `boil32.tif`, `boil33.tif`, `boil34a.tif`, `boil34b.tif`, `boil36.tif`, `boilb1.tif`, `cgate01.tif`, `cgate02.tif`, `cgate04.tif`, `cgate05.tif`, `cgate06.tif`, `cgate07.tif`, `cgate09.tif`, `cgate10.tif`, `cgate11.tif`, `cgate12.tif`, `cgate14.tif`, `cgate15.tif`, `cgate16.tif`, `cgate17.tif`, `comstat16.tif`, `comstat20.tif`, `cone1.tif`, `cone10.tif`, `cone11.tif`, `cone12.tif`, `cone2.tif`, `cone3.tif`, `cone4.tif`, `cone6.tif`, `cone7.tif`, `cone8.tif`, `cone9.tif`, `craterash.tif`, `cratrim1.tif`, `cratrim2.tif`, `geo01.tif`, `geo02.tif`, `geo03.tif`, `geo04.tif`, `geo05.tif`, `geo06.tif`, `geo07.tif`, `geo10a.tif`, `geo10b.tif`, `hel01a.tif`, `hel01b.tif`, `hel01c.tif`, `hel01e.tif`, `heldoor1.tif`, `heldoor2.tif`, `heldoor3.tif`, `heldoor4.tif`, `heldoor5.tif`, `heldoor6.tif`, `hovr01.tif`, `hovr02.tif`, `hovr03.tif`, `hovr04.tif`, `hovr05.tif`, `hovr06.tif`, `hovr07.tif`, `hovr08.tif`, `hovr09.tif`, `hovr10.tif`, `hovr11.tif`, `hovr12a.tif`, `hovr12b.tif`, `hovr12c.tif`, `hovr12d.tif`, `hovr13.tif`, `hovr14.tif`, `hovr15a.tif`, `hovr15b.tif`, `hovr16.tif`, `hovr17.tif`, `hovr18.tif`, `hovr18a.tif`, `hovr19.tif`, `hovr20.tif`, `hovr21.tif`, `hovr22.tif`, `hovr23.tif`, `hovr24.tif`, `hovr25.tif`, `hovr26.tif`, `hovr27.tif`, `hovr28.tif`, `hovr29.tif`, `hovr31a.tif`, `hovr31b.tif`, `hovr32.tif`, `hovr33.tif`, `hovr34.tif`, `hovr35.tif`, `lav00001.tif`, `lav00002.tif`, `lav00003.tif`, `lav00004.tif`, `lav00005.tif`, `lav00006.tif`, `lav00007.tif`, `lav00008.tif`, `lav00009.tif`, `lav00010.tif`, `lav00011.tif`, `lav00012.tif`, `lav00013.tif`, `lav00014.tif`, `lav00015.tif`, `lav00016.tif`, `lav00017.tif`, `lavaedg1.tif`, `lavaedg3.tif`, `lavblen1.tif`, `lavblen2.tif`, `lavhbl1.tif`, `lavhot1.tif`, `lavrock2.tif`, `lavsta1.tif`, `lavylw01.tif`, `lavylw02.tif`, `lavylw03.tif`, `lavylw04.tif`, `lavylw05.tif`, `lavylw06.tif`, `lavylw07.tif`, `lavylw08.tif`, `lavylw09.tif`, `lavylw10.tif`, `lavylw11.tif`, `lavylw12.tif`, `lavylw13.tif`, `lavylw14.tif`, `lavylw15.tif`, `lavylw16.tif`, `lavylw17.tif`, `lcrat01.tif`, `lndlava1.tif`, `m1gate15.tif`, `m3brig02.tif`, `m3door01.tif`, `m3door02.tif`, `m3hall01.tif`, `m3hall02.tif`, `m3sec01.tif`, `m3sec02.tif`, `m3sec03.tif`, `mgat01.tif`, `mgat05.tif`, `mgat08.tif`, `mgat10.tif`, `mgat14.tif`, `mgat15.tif`, `nsolid1.tif`, `pipes04.tif`, `pipes07.tif`, `pipes11.tif`, `psuprt1.tif`, `psuprt2.tif`, `retain01.tif`, `retain02.tif`, `retain03.tif`, `retain04.tif`, `retain05.tif`, `retain06.tif`, `skym3.tif`, `vcap01.tif`, `vcap02.tif`, `vcap03.tif`, `vcap04.tif`, `vcap05.tif`, `vcap06.tif`, `vcap07.tif`, `vcap08.tif`, `vcap09.tif`, `vcap10.tif`, `vcap11.tif`, `vcap12.tif`, `vcap14.tif`, `vcap15.tif`, `vcap16.tif`, `vcap17.tif`, `vcap18.tif`, `vcap19.tif`, `vcap20.tif`, `vcap21.tif`, `vcap22.tif`, `vcap23.tif`, `vcap24.tif`, `vcap25.tif`, `vcap26.tif`, `vcap27.tif`, `vcap28.tif`, `vcap29.tif`, `vcap30.tif`, `vcap31.tif`, `vcap32.tif`, `vcap33.tif`, `vcap34.tif`, `vcap35.tif`, `vcap36.tif`, `vcap37.tif`, `vcap38.tif`, `vcap39.tif`, `vcap40.tif`, `vcap41.tif`, `vcap42.tif`, `vcap43.tif`, `vcap44.tif`, `vcap45.tif`, `vcap46.tif`, `weld01.tif`, `weld02.tif`, `weld03.tif`
- `data\m3\textures\bft`: `hovr.tif`, `tankback.tif`, `tankbotm.tif`, `tankgn01.tif`, `tankgn02.tif`, `tankgn03.tif`, `tankgn04.tif`, `tankgn05.tif`, `tanklite.tif`, `tankpon1.tif`, `tankpon2.tif`, `tankside.tif`, `tanktop.tif`, `tanktrks.tif`, `tankwing.tif`
- `data\m4\textures`: `abridg01.tif`, `bigbridg.tif`, `brcktrim.tif`, `brick02.tif`, `brick03.tif`, `brick05.tif`, `briklite.tif`, `brpost.tif`, `build02.tif`, `build03.tif`, `bwallbad.tif`, `cath01.tif`, `cath02.tif`, `cath03.tif`, `cath04.tif`, `cath05.tif`, `cath05c.tif`, `cath05d.tif`, `cath05e.tif`, `cath05f.tif`, `cath06.tif`, `cath07.tif`, `cath08.tif`, `cath09.tif`, `cath10.tif`, `cath11.tif`, `cath12.tif`, `cath13.tif`, `cath14.tif`, `cath15.tif`, `cath16.tif`, `cath18.tif`, `cath19.tif`, `cath21.tif`, `cath22.tif`, `cath23.tif`, `cath24.tif`, `cath25.tif`, `cath26.tif`, `cath27.tif`, `cath28b.tif`, `cath29.tif`, `chem01.tif`, `chem03.tif`, `chem04.tif`, `chem05.tif`, `chem06.tif`, `chem07.tif`, `chem07a.tif`, `chem07b.tif`, `chem08.tif`, `chem09.tif`, `chem10.tif`, `chem11.tif`, `chem12.tif`, `chem13.tif`, `chem14.tif`, `chem15.tif`, `chem17.tif`, `chem19.tif`, `chem20.tif`, `chem21.tif`, `chem22.tif`, `chem23.tif`, `craterb4.tif`, `cratercem.tif`, `cratergrass.tif`, `cratergrav.tif`, `craterroad.tif`, `craterston.tif`, `dam01.tif`, `dam02.tif`, `dam03.tif`, `dam04.tif`, `dam05.tif`, `dam06.tif`, `damflow.tif`, `damrush.tif`, `dbrick01.tif`, `fence.tif`, `fntn01.tif`, `fntn03.tif`, `fntn04.tif`, `fntn05.tif`, `fntn06.tif`, `fntn07.tif`, `fntn08.tif`, `frock1a.tif`, `frock1b.tif`, `frock1c.tif`, `frock1d.tif`, `frock2a.tif`, `frock2b.tif`, `frock3a.tif`, `frock3b.tif`, `frock3c.tif`, `frock3d.tif`, `frock4a.tif`, `frock4b.tif`, `frock5a.tif`, `frock5b.tif`, `frock5c.tif`, `frock5d.tif`, `frock6a.tif`, `frock6b.tif`, `frock7.tif`, `ftank01.tif`, `ftank01d.tif`, `ftank02d.tif`, `girder01.tif`, `grass1.tif`, `gravel.tif`, `hel01a.tif`, `hydrant.tif`, `m4canl3.tif`, `medwall0.tif`, `medwall1.tif`, `movie1.tif`, `pblb.tif`, `rail01.tif`, `rail02.tif`, `railc01.tif`, `raildam1.tif`, `raildam2.tif`, `refl0000.tif`, `refl0010.tif`, `refl0020.tif`, `refl0030.tif`, `refl0040.tif`, `refl0050.tif`, `refl0060.tif`, `refl0070.tif`, `refl0080.tif`, `retain03.tif`, `road1.tif`, `road2.tif`, `roadtdir.tif`, `rockt1c.tif`, `rockt3b.tif`, `rockt4b.tif`, `rokt02.tif`, `rokt03.tif`, `rokt04.tif`, `rokt05.tif`, `rokt06.tif`, `rokt6.tif`, `rokt7g.tif`, `rooftil1.tif`, `rrtie1.tif`, `rrtie2.tif`, `rrtie3.tif`, `rubble2.tif`, `rustplg.tif`, `rusty01.tif`, `rusty02.tif`, `rusty03.tif`, `rusty04.tif`, `rusty05.tif`, `rusty06.tif`, `rusty07.tif`, `signal1.tif`, `signal2.tif`, `signal3.tif`, `signal4.tif`, `signal5.tif`, `skym4.tif`, `smk01.tif`, `smk02.tif`, `smk03.tif`, `smk04.tif`, `smk05.tif`, `smk06.tif`, `stackc.tif`, `stbed01.tif`, `streets1.tif`, `streets4.tif`, `subcas10.tif`, `subcase2.tif`, `subcase3.tif`, `subcase4.tif`, `subcase5.tif`, `subcase7.tif`, `subcase9.tif`, `subway01.tif`, `subway02.tif`, `subway03.tif`, `subway04.tif`, `subway05.tif`, `subway06.tif`, `subway07.tif`, `subway08.tif`, `subway09.tif`, `subway10.tif`, `subway11.tif`, `subway12.tif`, `subway13.tif`, `subway14.tif`, `subway15.tif`, `subway16.tif`, `train00.tif`, `train01.tif`, `train02.tif`, `train03.tif`, `train04.tif`, `train05.tif`, `train06.tif`, `train10.tif`, `tryard1.tif`, `tryard2.tif`, `tryard3.tif`, `tunnel01.tif`, `tunnel02.tif`, `tunnel03.tif`, `tunnel04.tif`, `tunnel05.tif`, `tunnel06.tif`, `ware02.tif`, `ware04.tif`, `watr01.tif`, `watr02.tif`, `watr03.tif`, `watr04.tif`, `watr05.tif`, `watr06.tif`, `watr07.tif`, `watr08.tif`, `watr09.tif`, `watr10.tif`, `watrfall.tif`
- `data\m4\textures\bft`: `hovr.tif`, `tankback.tif`, `tankbotm.tif`, `tankgn01.tif`, `tankgn02.tif`, `tankgn03.tif`, `tankgn04.tif`, `tankgn05.tif`, `tanklite.tif`, `tankpon1.tif`, `tankpon2.tif`, `tankside.tif`, `tanktop.tif`, `tanktrks.tif`, `tankwing.tif`
- `data\m5\textures`: `alien01.tif`, `alien02.tif`, `alien03.tif`, `alien04.tif`, `alien05.tif`, `alien06.tif`, `alien07.tif`, `alien08.tif`, `alien09.tif`, `base.tif`, `berglow5r4.tif`, `berglowr.tif`, `berglowru5.tif`, `berglowt.tif`, `boxside6.tif`, `btundr.tif`, `btunsub.tif`, `cavernroof.tif`, `cavernroof4.tif`, `cbox7.tif`, `cboxoff2.tif`, `ceil.tif`, `clfield1.tif`, `clfield3.tif`, `cloncage.tif`, `clondor1.tif`, `clone01.tif`, `clone03.tif`, `clone04.tif`, `clone05.tif`, `clone06.tif`, `clone07.tif`, `clone08.tif`, `clone09.tif`, `clone10.tif`, `clone11.tif`, `clone12.tif`, `cloneund2.tif`, `clonewin8.tif`, `clonflor.tif`, `clonpowr.tif`, `clship01.tif`, `clship02.tif`, `clship03.tif`, `clship04.tif`, `cratersnow.tif`, `frcsub12.tif`, `frcsub13.tif`, `frcsub14.tif`, `frcsub2.tif`, `frcsub3.tif`, `frcsub4.tif`, `frcsub6.tif`, `frcsub7.tif`, `frcsub8.tif`, `frice1.tif`, `hamband.tif`, `hamblast.tif`, `hamdest.tif`, `helim5.tif`, `hivolt.tif`, `iceberg1.tif`, `iceblue.tif`, `icecave1.tif`, `icecol1.tif`, `icepk1.tif`, `inpart.tif`, `inpart2.tif`, `iwall01.tif`, `iwall02.tif`, `iwall03.tif`, `iwall04.tif`, `iwalld01.tif`, `lightedics.tif`, `lightedics2.tif`, `lightedics4.tif`, `lightedics5.tif`, `linesnow.tif`, `m5sky.tif`, `oceanbot.tif`, `oceantop.tif`, `ptop1.tif`, `ptop12.tif`, `ptop2.tif`, `ptop3.tif`, `ptop4.tif`, `ptop5.tif`, `ptop6.tif`, `ptop7.tif`, `pwsorbg.tif`, `pwstbo.tif`, `pwstbo2.tif`, `pwstbp.tif`, `pwstcorc.tif`, `pwstfl4.tif`, `pwstfritb.tif`, `pwstfritc.tif`, `pwstltb.tif`, `pwstlty.tif`, `pwstnft.tif`, `pwstpa.tif`, `pwstpb.tif`, `pwstpg.tif`, `radiosi.tif`, `radiox.tif`, `rdiolod.tif`, `right1.tif`, `scroll2.tif`, `shipcont01.tif`, `shipcont02.tif`, `shipcont03.tif`, `shipcont04.tif`, `snow1.tif`, `snow2.tif`, `spinend.tif`, `spinends.tif`, `spinsidf.tif`, `spinsidi.tif`, `subsh01.tif`, `subsh02.tif`, `subsh03.tif`, `subsh04.tif`, `subsh05.tif`, `subsh06.tif`, `subsh07.tif`, `subsh08.tif`, `subsh09.tif`, `subsh10.tif`, `subsh11.tif`, `subsh12.tif`, `subsh13.tif`, `subsh14.tif`, `subsh15.tif`, `subsh16.tif`, `subsh17a.tif`, `subsh17b.tif`, `subsh17c.tif`, `subsh18.tif`, `subsh19.tif`, `subsh20.tif`, `subsh21.tif`, `subsh23.tif`, `subsh24.tif`, `subsh25.tif`, `subsh26.tif`, `subsh29.tif`, `subsh30.tif`, `subsh31.tif`, `subsh32.tif`, `subsh33.tif`, `swall1.tif`, `swall2.tif`, `swall3.tif`, `swall4.tif`, `tunclne1.tif`, `tunclne2.tif`, `tunflr2.tif`, `tunglow1.tif`, `tunsnw1.tif`, `video1.tif`, `video2.tif`, `wall5.tif`, `wat.tif`, `wat2.tif`, `wat3.tif`, `wat4.tif`, `watrcold.tif`
- `data\m5\textures\bft`: `caust01.tif`, `caust02.tif`, `caust03.tif`, `caust04.tif`, `caust05.tif`, `hovr.tif`, `prop01.tif`, `prop02.tif`, `prop03.tif`, `prop04.tif`, `sub03.tif`, `sub04.tif`, `sub05.tif`, `sub06.tif`, `sub07.tif`, `tankback.tif`, `tankbotm.tif`, `tankgn01.tif`, `tankgn02.tif`, `tankgn03.tif`, `tankgn04.tif`, `tankgn05.tif`, `tanklite.tif`, `tankpon1.tif`, `tankpon2.tif`, `tankside.tif`, `tanktop.tif`, `tanktrks.tif`, `tankwing.tif`
- `data\m6\textures`: `a1_cave2.tif`, `a1_clif1.tif`, `a1_ocean.tif`, `a1_rock1.tif`, `a1_sand1.tif`, `a1_sand2.tif`, `a1_shore.tif`, `a1_ucav1.tif`, `a1_ucav2.tif`, `a1tpaccess.tif`, `a200.tif`, `a200a.tif`, `a200b.tif`, `a200c.tif`, `a201.tif`, `a201a.tif`, `a202.tif`, `a202a.tif`, `a203.tif`, `a203a.tif`, `a203b.tif`, `a203c.tif`, `a203d.tif`, `a203e.tif`, `a205.tif`, `a205a.tif`, `a206.tif`, `a207.tif`, `a207a.tif`, `a208.tif`, `a209.tif`, `a211.tif`, `a213.tif`, `a214.tif`, `a215.tif`, `a217.tif`, `a220.tif`, `a226.tif`, `a229.tif`, `a232.tif`, `a236.tif`, `a240.tif`, `a241.tif`, `a242.tif`, `a243.tif`, `a244.tif`, `a251.tif`, `a253.tif`, `a253a.tif`, `a253b.tif`, `a253c.tif`, `a256.tif`, `a257.tif`, `a258.tif`, `a259.tif`, `a260.tif`, `a262.tif`, `a266.tif`, `a270.tif`, `a273.tif`, `a274.tif`, `a275.tif`, `arb01.tif`, `arb03.tif`, `arb04.tif`, `arb06.tif`, `arb102.tif`, `arb107.tif`, `arb119.tif`, `arb120.tif`, `arb125.tif`, `arb13.tif`, `arb133.tif`, `arb140.tif`, `arb143.tif`, `arb145.tif`, `arb146.tif`, `arb147.tif`, `arb148.tif`, `arb15.tif`, `arb150.tif`, `arb152.tif`, `arb153.tif`, `arb156.tif`, `arb157.tif`, `arb162.tif`, `arb164.tif`, `arb165.tif`, `arb30.tif`, `arb45.tif`, `arb47.tif`, `arb49.tif`, `arb60.tif`, `arb63.tif`, `arb65.tif`, `arb66.tif`, `arb67.tif`, `arb68.tif`, `arb70.tif`, `arb73.tif`, `arb75b.tif`, `arb87.tif`, `arb88.tif`, `arb93.tif`, `arb94.tif`, `arb95.tif`, `arb96.tif`, `arb99.tif`, `arena305.tif`, `arena307.tif`, `arena308.tif`, `arena317.tif`, `azdef01.tif`, `aztec01.tif`, `aztec03.tif`, `aztec04.tif`, `aztec05.tif`, `aztec06.tif`, `aztec11.tif`, `aztec12.tif`, `aztec13.tif`, `aztec15.tif`, `barrier.tif`, `bldg_a01.tif`, `bldg_a03.tif`, `bldg_b01.tif`, `bldg_b03.tif`, `bldg_c02.tif`, `bldg_d03.tif`, `bldg_e02.tif`, `bldg_g01.tif`, `bldg_g05.tif`, `bldg_h01.tif`, `bldg_h02.tif`, `bldg_n01.tif`, `bldg_n04.tif`, `bldg_p06.tif`, `bldg_s06.tif`, `bldg_s07.tif`, `bldg_top.tif`, `blood01.tif`, `boil04.tif`, `boil06.tif`, `brain00.tif`, `brain01.tif`, `brain02.tif`, `brain03.tif`, `brain04.tif`, `brain05.tif`, `brain06.tif`, `brain07.tif`, `brain08.tif`, `brain09.tif`, `brain10.tif`, `brain11.tif`, `brain12.tif`, `brain13.tif`, `brain14.tif`, `brain15.tif`, `brain16.tif`, `brain18.tif`, `bsteam1.tif`, `bsteam2.tif`, `bsteam3.tif`, `bsteam4.tif`, `build02.tif`, `build03.tif`, `cave01.tif`, `cave02.tif`, `cave03.tif`, `cave04.tif`, `cave05.tif`, `cavebh1.tif`, `caveceil.tif`, `cavefl1.tif`, `cblast.tif`, `cblast1.tif`, `cblast3.tif`, `celldest.tif`, `cemcol3.tif`, `cemcol4.tif`, `cemcol5.tif`, `cemcol6.tif`, `citybrl.tif`, `clondest.tif`, `clontrbs.tif`, `clontree.tif`, `clontrnk.tif`, `clwatr01.tif`, `clwatr02.tif`, `clwatr03.tif`, `clwatr04.tif`, `clwatr05.tif`, `clwatr06.tif`, `clwatr07.tif`, `clwatr08.tif`, `clwatr09.tif`, `clwatr10.tif`, `conduit1.tif`, `conduit2.tif`, `conduit3.tif`, `craterash.tif`, `craterdirt.tif`, `cratergrass.tif`, `craterroad.tif`, `cratersand.tif`, `craterston.tif`, `crathlav.tif`, `cratrcrd.tif`, `croad01.tif`, `croad02.tif`, `cruins01.tif`, `def17.tif`, `def24.tif`, `def33.tif`, `def34.tif`, `def38.tif`, `def39.tif`, `def44.tif`, `def9.tif`, `dmpstr.tif`, `dmpstrs.tif`, `dmpstrt.tif`, `elev01.tif`, `elev02.tif`, `elev04.tif`, `elev05.tif`, `elev06.tif`, `elev07.tif`, `elev08.tif`, `elev09.tif`, `elev10.tif`, `elev11.tif`, `elev12.tif`, `elev13.tif`, `elev14.tif`, `hydrant.tif`, `ltbaseb.tif`, `ltbasebd.tif`, `ltbaset.tif`, `piptil10.tif`, `piptil11.tif`, `piptil12.tif`, `piptil13.tif`, `piptil14.tif`, `piptile1.tif`, `piptile2.tif`, `piptile3.tif`, `piptile4.tif`, `piptile5.tif`, `piptile6.tif`, `piptile7.tif`, `piptile8.tif`, `piptile9.tif`, `polbeam1.tif`, `poldest1.tif`, `polite1.tif`, `polite2.tif`, `polroad1.tif`, `polroad3.tif`, `polroad4.tif`, `polroad5.tif`, `polwall1.tif`, `polwall2.tif`, `polwall4.tif`, `polwall6.tif`, `polwall7.tif`, `polwall8.tif`, `skym6.tif`, `skym6a1.tif`, `skym6a2.tif`, `skym6a3.tif`, `slite3.tif`, `slitebk.tif`, `stub01.tif`, `stub02.tif`, `stub04.tif`, `stub05.tif`, `stub06.tif`, `stub07.tif`, `stub08.tif`, `stub10.tif`, `stub11.tif`, `stub12.tif`, `t14b.tif`, `tele15.tif`, `tele17.tif`, `tele29.tif`, `tele30.tif`, `tele31.tif`, `tele33.tif`, `tele34.tif`, `tele36.tif`, `tele37.tif`, `tele38.tif`, `tele39.tif`, `tele41.tif`, `tele42.tif`, `tele50.tif`, `tele51.tif`, `tele52.tif`, `tele53.tif`, `tele54.tif`, `tfram1.tif`, `tp7sc.tif`, `tpaccess.tif`, `tptoa1.tif`, `tptoa2.tif`, `tptoa3.tif`, `tptobrain.tif`, `tran01.tif`, `tran02.tif`, `tran03.tif`, `tran04.tif`, `tran05.tif`, `tran06.tif`, `tran07.tif`, `tran08.tif`, `tran09.tif`, `tran10.tif`, `tran11.tif`, `tran12.tif`, `troprfl1.tif`, `troprfl2.tif`, `troprfl3.tif`, `tstatic.tif`, `watrcar01.tif`, `watrcar02.tif`, `watrcar03.tif`, `watrcar04.tif`, `watrcar05.tif`, `watrcar06.tif`, `watrcar07.tif`, `watrcar08.tif`, `watrcar09.tif`, `watrcar10.tif`, `watrcarv01.tif`, `watrcarv02.tif`, `watrcarv03.tif`, `watrcarv04.tif`, `watrcarv05.tif`, `watrcarv06.tif`, `watrcarv07.tif`, `watrcarv08.tif`, `watrcarv09.tif`, `watrcarv10.tif`, `watrct01.tif`, `watrct02.tif`, `watrct03.tif`, `watrct04.tif`, `watrct05.tif`, `watrct06.tif`, `watrct07.tif`, `watrct08.tif`, `watrct09.tif`, `watrct10.tif`, `watrcv01.tif`, `watrcv02.tif`, `watrcv03.tif`, `watrcv04.tif`, `watrcv05.tif`, `watrcv06.tif`, `watrcv07.tif`, `watrcv08.tif`, `watrcv09.tif`, `watrcv10.tif`, `watrp01.tif`, `watrp02.tif`, `watrp03.tif`, `watrp04.tif`, `watrp05.tif`, `watrp06.tif`, `watrp07.tif`, `watrp08.tif`, `watrp09.tif`, `watrp10.tif`
- `data\m6\textures\bft`: `caust01.tif`, `caust02.tif`, `caust03.tif`, `caust04.tif`, `caust05.tif`, `hovr.tif`, `prop01.tif`, `prop02.tif`, `prop03.tif`, `prop04.tif`, `sub03.tif`, `sub04.tif`, `sub05.tif`, `sub06.tif`, `sub07.tif`, `tankback.tif`, `tankbotm.tif`, `tankgn01.tif`, `tankgn02.tif`, `tankgn03.tif`, `tankgn04.tif`, `tankgn05.tif`, `tanklite.tif`, `tankpon1.tif`, `tankpon2.tif`, `tankside.tif`, `tanktop.tif`, `tanktrks.tif`, `tankwing.tif`
- `data\m7\textures`: `beam01.tif`, `beam02.tif`, `beam03.tif`, `beam04.tif`, `beam05.tif`, `beam06.tif`, `block01.tif`, `brdgdck2.tif`, `brdgdeck.tif`, `brdgrmp.tif`, `brigsup1.tif`, `brigsup2.tif`, `brigsup3.tif`, `brigsup4.tif`, `brigsup5.tif`, `brigsup6.tif`, `brigsup7.tif`, `brigsup8.tif`, `brigsup9.tif`, `brkndeck.tif`, `bunker1.tif`, `canrock1.tif`, `canyon2.tif`, `cement02.tif`, `cement03.tif`, `cement05.tif`, `craterdirt.tif`, `droad1.tif`, `grate1.tif`, `grate2.tif`, `grate3.tif`, `grate4.tif`, `grated1.tif`, `hill1.tif`, `lakecy10.tif`, `lakecyc1.tif`, `lakecyc2.tif`, `lakecyc3.tif`, `lakecyc4.tif`, `lakecyc5.tif`, `lakecyc6.tif`, `lakecyc7.tif`, `lakecyc8.tif`, `lakecyc9.tif`, `minepit2.tif`, `padedg2.tif`, `road4.tif`, `road6.tif`, `sand4.tif`, `sky2.tif`
- `data\m8\textures`: `cone12.tif`, `cone8.tif`, `craterash.tif`, `cratrim1.tif`, `cratrim2.tif`, `lavahil1.tif`, `lavahil2.tif`, `lavblen1.tif`, `lavblen2.tif`, `lavhot1.tif`, `lndlava1.tif`, `m1gate15.tif`, `m3brig02.tif`, `m3hall02.tif`, `m3sec01.tif`, `m3sec02.tif`, `m3sec03.tif`, `mgat01.tif`, `mgat02.tif`, `mgat03.tif`, `mgat04.tif`, `mgat05.tif`, `mgat06.tif`, `mgat07.tif`, `mgat08.tif`, `mgat10.tif`, `mgat14.tif`, `mgat15.tif`, `nsolid1.tif`, `retain06.tif`, `skym3.tif`
- `data\m9\textures`: `arrow1.tif`, `broad1.tif`, `bwall01.tif`, `bwall02.tif`, `bwall03.tif`, `bwall04.tif`, `bwall05.tif`, `bwall06.tif`, `cavern1.tif`, `cavern2.tif`, `cembar1.tif`, `cemwall6.tif`, `chkpt01.tif`, `chkpt02.tif`, `chkpt03.tif`, `chkpt04.tif`, `chkpt05.tif`, `craterdrt.tif`, `craterroad.tif`, `cratersand.tif`, `dirt1.tif`, `flag0000.tif`, `hay1.tif`, `hay2.tif`, `hbarrl.tif`, `hlite2.tif`, `hway01.tif`, `hway02.tif`, `intun1.tif`, `intun2.tif`, `intun4.tif`, `m1cliff1.tif`, `m1gate12.tif`, `m1rock2.tif`, `m1sky1.tif`, `m9road09.tif`, `mnsand1.tif`, `mnsand2.tif`, `mnsrd10.tif`, `mnsroad2.tif`, `mnsroad9.tif`, `nsand1.tif`, `nsand2.tif`, `nsand3.tif`, `nsand4.tif`, `ocean1.tif`, `rroadb.tif`, `scliftrn1.tif`, `scurve.tif`, `sdirt1.tif`, `surf01.tif`, `surf02.tif`, `surf03.tif`, `surf04.tif`, `surf05.tif`, `surf06.tif`, `surf07.tif`, `surf08.tif`, `surf09.tif`, `surf10.tif`, `surf11.tif`, `surf12.tif`, `surf13.tif`, `woodpost.tif`

</details>

<details><summary>Resources and keyframe scripts by folder</summary>

- `data\common\multi_bft\zrdr`: `bftmulti.zrd`, `deathmulti.zrd`
- `data\common\zrdr`: `anim.zrd`, `bftbubbles.zrd`, `bftsplash.zrd`, `boatwake.zrd`, `briefing.zrd`, `cam_shake.zrd`, `cockpit.zrd`, `detail.zrd`, `dialog.zrd`, `effects.zrd`, `exhaust.zrd`, `fmv.zrd`, `fonts.zrd`, `hud.zrd`, `load_control.zrd`, `pickup.zrd`, `player.zrd`, `regen.zrd`, `shock.zrd`, `sounds.zrd`, `vehicle.zrd`, `vehicle_easy.zrd`, `vehicle_hard.zrd`, `weapons.zrd`, `weather.zrd`
- `data\common\zrdr\enemies`: `amphib.zrd`, `arc.zrd`, `drone.zrd`, `ftank.zrd`, `helicop.zrd`, `hovert.zrd`, `lasdesig.zrd`, `lockon.zrd`, `ltank.zrd`, `minelay.zrd`, `mortar.zrd`, `ntank.zrd`, `rumv.zrd`, `rumv_easy.zrd`, `samsite.zrd`, `scout.zrd`, `scout_easy.zrd`, `sonic.zrd`, `sub.zrd`, `turret.zrd`
- `data\common\zrdr\explosns`: `ash_dirt.zrd`, `blue_sparks.zrd`, `bxcrsh.zrd`, `bxxpld.zrd`, `csinfbfx.zrd`, `deadman1.zrd`, `exp_blue.zrd`, `exp_green.zrd`, `exp_red.zrd`, `exp_yellow.zrd`, `flare_blue.zrd`, `flare_orange.zrd`, `flare_white.zrd`, `frxplsn.zrd`, `generic6.zrd`, `gsxpld.zrd`, `he_dirt.zrd`, `hulk_big.zrd`, `hulk_small.zrd`, `kill_arc.zrd`, `kill_erfpg.zrd`, `kill_lasdes.zrd`, `kill_laser.zrd`, `kill_mine.zrd`, `kill_missile.zrd`, `kill_mortar.zrd`, `kill_napalm.zrd`, `kill_rfpg.zrd`, `kill_sonic.zrd`, `lav_dirt.zrd`, `lgimpact.zrd`, `mdimpact.zrd`, `mine_veh.zrd`, `missed.zrd`, `napalm_veh.zrd`, `parts.zrd`, `parts_blue.zrd`, `parts_green.zrd`, `parts_orange.zrd`, `parts_red.zrd`, `parts_smorange.zrd`, `parts_yellow.zrd`, `prscrate.zrd`, `redsprks.zrd`, `ring_black.zrd`, `ring_fire.zrd`, `ring_red.zrd`, `ring_white.zrd`, `shatter.zrd`, `shatter_veh.zrd`, `smimpact.zrd`, `smoke1.zrd`, `smoke2.zrd`, `smoke_destroy.zrd`, `smxplsn.zrd`, `snow_dirt.zrd`, `sulph_dirt.zrd`, `unwatexp.zrd`, `wtrxplsn.zrd`
- `data\common\zrdr\lighting`: `light_green.zrd`, `light_red.zrd`
- `data\common\zrdr\vtol`: `gen_vtol.zrd`, `pchute.zrd`
- `data\common\zrdr\weapons`: `bftmbrst.zrd`, `bsplsh.zrd`, `erfpg_mzl.zrd`, `fire.zrd`, `firering.zrd`, `miss_launch.zrd`, `muzzleburst.zrd`, `napalm.zrd`, `nuke.zrd`, `rcochet1.zrd`, `rfpg_mzl.zrd`, `ric_blue.zrd`, `ric_green.zrd`, `ric_red.zrd`, `sizzle.zrd`, `smktrl1.zrd`, `splash1.zrd`
- `data\m1\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`, `puppies_easy.zrd`, `puppies_hard.zrd`, `start_single_m1.zrd`, `startanims.zrd`
- `data\m1\zrdr\aipath`: `aiv.zrd`, `aiv_easy.zrd`, `aiv_hard.zrd`, `net_01.zrd`, `net_02.zrd`, `net_03.zrd`, `net_04.zrd`, `net_05.zrd`, `net_06.zrd`, `net_07.zrd`, `net_08.zrd`, `net_09.zrd`, `net_10.zrd`, `net_11.zrd`, `net_12.zrd`, `net_13.zrd`, `net_14.zrd`, `net_15.zrd`, `net_16.zrd`, `net_17.zrd`, `net_18.zrd`, `net_19.zrd`, `net_20.zrd`, `net_21.zrd`, `net_22.zrd`, `net_23.zrd`, `net_24.zrd`, `net_25.zrd`, `net_26.zrd`, `net_27.zrd`, `net_28.zrd`, `net_29.zrd`, `net_30.zrd`, `net_31.zrd`, `net_32.zrd`, `net_33.zrd`, `net_34.zrd`, `net_35.zrd`, `net_36.zrd`, `net_37.zrd`, `net_38.zrd`, `net_39.zrd`, `net_40.zrd`, `net_41.zrd`, `net_42.zrd`, `net_43.zrd`, `net_44.zrd`, `net_45.zrd`, `net_46.zrd`, `net_47.zrd`, `net_48.zrd`, `net_49.zrd`, `net_50.zrd`, `net_51.zrd`, `net_52.zrd`, `net_53.zrd`, `net_54.zrd`, `net_55.zrd`, `net_56.zrd`, `net_57.zrd`, `net_58.zrd`, `net_59.zrd`, `net_60.zrd`, `net_61.zrd`, `net_62.zrd`, `net_63.zrd`, `net_64.zrd`, `net_65.zrd`, `net_66.zrd`, `net_67.zrd`, `net_68.zrd`, `net_69.zrd`, `net_70.zrd`, `net_71.zrd`, `net_72.zrd`, `net_73.zrd`, `net_74.zrd`, `net_75.zrd`, `net_76.zrd`, `net_77.zrd`, `net_78.zrd`, `net_79.zrd`, `net_80.zrd`, `net_81.zrd`, `net_82.zrd`, `net_83.zrd`, `net_84.zrd`, `net_85.zrd`, `net_86.zrd`, `net_87.zrd`, `net_88.zrd`, `net_89.zrd`, `net_90.zrd`, `net_91.zrd`
- `data\m1\zrdr\beach`: `countdown.zrd`, `heligate.zrd`, `helisnds.zrd`, `hitrkt1.zrd`, `rckrang1.zrd`, `rktcount.zrd`, `rocket1.zrd`
- `data\m1\zrdr\bft`: `deathm1.zrd`
- `data\m1\zrdr\choppers`: `m1_chop.zrd`; `m1_chop.zan`
- `data\m1\zrdr\envmodels`: `aztecdr.zrd`, `aztecfire.zrd`, `drain.zrd`, `frcgate.zrd`, `fuel.zrd`, `fueltank.zrd`, `grating.zrd`, `gstrut.zrd`, `liteh.zrd`, `m1objects.zrd`, `nbsign.zrd`, `oldpier.zrd`, `plants.zrd`, `shak.zrd`, `start_north.zrd`, `vwbus.zrd`, `vwrebel.zrd`; `vwbus.zan`
- `data\m1\zrdr\pier`: `dockhous.zrd`, `ocean.zrd`, `prcrane.zrd`, `prwind.zrd`, `seagull.zrd`
- `data\m1\zrdr\ruins`: `holo.zrd`, `holobeam.zrd`, `powbays.zrd`, `ruins.zrd`
- `data\m1\zrdr\semi`: `semi.zrd`, `semichains.zrd`, `semideath.zrd`; `semicab.zan`, `semicabst.zan`, `semitrlr.zan`, `semitrlrst.zan`
- `data\m1\zrdr\vtol`: `m1flufvtol.zrd`, `m1pickup.zrd`, `m1startcam.zrd`, `m1vtols.zrd`, `novtol.zrd`; `m1exit.zan`, `m1exitcam.zan`, `m1flufvtol.zan`, `m1pickup.zan`, `m1puloop.zan`, `m1startcam.zan`
- `data\m2\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`, `puppies_easy.zrd`, `puppies_hard.zrd`, `startanims.zrd`
- `data\m2\zrdr\aipath`: `aiv.zrd`, `aiv_easy.zrd`, `aiv_hard.zrd`, `net_01.zrd`, `net_02.zrd`, `net_03.zrd`, `net_04.zrd`, `net_05.zrd`, `net_06.zrd`, `net_07.zrd`, `net_08.zrd`, `net_09.zrd`, `net_10.zrd`, `net_11.zrd`, `net_12.zrd`, `net_13.zrd`, `net_14.zrd`, `net_15.zrd`, `net_16.zrd`, `net_17.zrd`, `net_18.zrd`, `net_19.zrd`, `net_20.zrd`, `net_21.zrd`, `net_22.zrd`, `net_23.zrd`, `net_24.zrd`, `net_25.zrd`, `net_26.zrd`, `net_27.zrd`, `net_28.zrd`, `net_29.zrd`, `net_30.zrd`, `net_31.zrd`, `net_32.zrd`, `net_33.zrd`, `net_34.zrd`, `net_35.zrd`, `net_36.zrd`, `net_37.zrd`, `net_38.zrd`, `net_39.zrd`, `net_40.zrd`, `net_41.zrd`, `net_42.zrd`, `net_43.zrd`, `net_44.zrd`, `net_45.zrd`, `net_46.zrd`, `net_47.zrd`, `net_48.zrd`, `net_49.zrd`, `net_50.zrd`, `net_51.zrd`, `net_52.zrd`, `net_53.zrd`, `net_54.zrd`, `net_55.zrd`, `net_56.zrd`, `net_57.zrd`, `net_58.zrd`, `net_59.zrd`, `net_60.zrd`, `net_61.zrd`, `net_62.zrd`, `net_63.zrd`, `net_64.zrd`, `net_65.zrd`, `net_66.zrd`, `net_67.zrd`, `net_68.zrd`, `net_69.zrd`, `net_70.zrd`, `net_71.zrd`, `net_72.zrd`, `net_73.zrd`, `net_74.zrd`, `net_75.zrd`, `net_76.zrd`, `net_77.zrd`, `net_78.zrd`, `net_79.zrd`, `net_80.zrd`, `net_81.zrd`, `net_83.zrd`, `net_89.zrd`
- `data\m2\zrdr\bft`: `bftm2.zrd`, `deathm2.zrd`
- `data\m2\zrdr\choppers`: `m2_chop.zrd`; `m2_chop1.zan`, `m2_chop2.zan`, `m2_chop3.zan`
- `data\m2\zrdr\comstat`: `boxes.zrd`, `comdish.zrd`, `comfence.zrd`, `comstat.zrd`
- `data\m2\zrdr\envmodels`: `barrel.zrd`, `bouy.zrd`, `bridge.zrd`, `bunker.zrd`, `canend.zrd`, `litening.zrd`, `maingate.zrd`
- `data\m2\zrdr\factory`: `facbox1.zrd`, `facbox2.zrd`, `facdoor.zrd`, `facheli.zrd`, `fachit.zrd`, `faclock.zrd`, `facpump2.zrd`, `facpump3.zrd`, `facsnd.zrd`, `facturr.zrd`
- `data\m2\zrdr\mine`: `digger.zrd`, `digrgone.zrd`, `machine.zrd`, `mcar.zrd`, `mcaranim.zrd`, `mcartime.zrd`, `morphpit.zrd`, `pipetrig.zrd`; `mcaranim.zan`
- `data\m2\zrdr\power`: `countdown.zrd`, `gflash.zrd`, `powcore.zrd`, `powdoor.zrd`, `powexp.zrd`, `powkey.zrd`, `powsnd.zrd`, `powvoice.zrd`
- `data\m2\zrdr\techlab`: `amchip.zrd`, `cranebox.zrd`, `labdoor.zrd`, `labradio.zrd`, `labsnd.zrd`, `labwall.zrd`, `lfanhit.zrd`, `lfanmov.zrd`, `warning.zrd`
- `data\m2\zrdr\vtol`: `m2dropoff.zrd`, `m2flufvtol.zrd`, `m2pickup.zrd`, `m2vtols.zrd`, `novtol.zrd`; `m2doexit.zan`, `m2doloop.zan`, `m2dropoff.zan`, `m2exit.zan`, `m2exitcam.zan`, `m2flufvtol.zan`, `m2pickup.zan`, `m2puloop.zan`, `m2startcam.zan`
- `data\m3\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`, `puppies_easy.zrd`, `puppies_hard.zrd`, `startanims.zrd`
- `data\m3\zrdr\aipath`: `aiv.zrd`, `aiv_easy.zrd`, `aiv_hard.zrd`, `net_01.zrd`, `net_02.zrd`, `net_03.zrd`, `net_04.zrd`, `net_05.zrd`, `net_06.zrd`, `net_07.zrd`, `net_08.zrd`, `net_09.zrd`, `net_10.zrd`, `net_11.zrd`, `net_12.zrd`, `net_13.zrd`, `net_14.zrd`, `net_15.zrd`, `net_16.zrd`, `net_17.zrd`, `net_18.zrd`, `net_19.zrd`, `net_20.zrd`, `net_21.zrd`, `net_22.zrd`, `net_23.zrd`, `net_24.zrd`, `net_25.zrd`, `net_26.zrd`, `net_27.zrd`, `net_28.zrd`, `net_29.zrd`, `net_30.zrd`, `net_31.zrd`, `net_32.zrd`, `net_33.zrd`, `net_34.zrd`, `net_35.zrd`, `net_36.zrd`, `net_37.zrd`, `net_38.zrd`, `net_39.zrd`, `net_40.zrd`, `net_41.zrd`, `net_42.zrd`, `net_43.zrd`, `net_44.zrd`, `net_45.zrd`, `net_46.zrd`, `net_47.zrd`, `net_48.zrd`, `net_49.zrd`, `net_50.zrd`, `net_51.zrd`, `net_52.zrd`, `net_53.zrd`, `net_54.zrd`, `net_55.zrd`, `net_56.zrd`, `net_57.zrd`, `net_58.zrd`, `net_59.zrd`, `net_60.zrd`, `net_61.zrd`, `net_62.zrd`, `net_63.zrd`, `net_64.zrd`, `net_65.zrd`, `net_66.zrd`, `net_67.zrd`, `net_68.zrd`, `net_69.zrd`, `net_70.zrd`, `net_71.zrd`, `net_72.zrd`, `net_73.zrd`, `net_74.zrd`, `net_75.zrd`, `net_76.zrd`, `net_77.zrd`, `net_78.zrd`, `net_79.zrd`, `net_80.zrd`, `net_81.zrd`, `net_82.zrd`, `net_83.zrd`, `net_84.zrd`, `net_85.zrd`, `net_86.zrd`, `net_87.zrd`, `net_88.zrd`, `net_89.zrd`, `net_90.zrd`, `net_91.zrd`, `net_92.zrd`
- `data\m3\zrdr\bft`: `bftm3.zrd`, `deathm3.zrd`
- `data\m3\zrdr\boiler`: `boilelev.zrd`, `bvalve.zrd`, `elevdown.zrd`
- `data\m3\zrdr\choppers`: `m3_chop.zrd`; `m3_chop1.zan`
- `data\m3\zrdr\envmodels`: `bridgetow.zrd`, `conegate.zrd`, `decks.zrd`, `fogchange.zrd`, `gtherm.zrd`, `hpad.zrd`, `lavbub.zrd`, `lazrcenter.zrd`, `m3doors.zrd`, `mnbridge.zrd`, `reflector.zrd`, `sprknull.zrd`
- `data\m3\zrdr\hover`: `hoverchip.zrd`, `hoverdoor.zrd`, `welder.zrd`
- `data\m3\zrdr\vtol`: `m3dropoff.zrd`, `m3flufvtol.zrd`, `m3pickup.zrd`, `m3vtols.zrd`; `m3doexit.zan`, `m3doloop.zan`, `m3dropoff.zan`, `m3exit.zan`, `m3exitcam.zan`, `m3flufvtol.zan`, `m3pickup.zan`, `m3puloop.zan`, `m3startcam1.zan`, `m3startcam2.zan`
- `data\m4\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`, `puppies_easy.zrd`, `puppies_hard.zrd`, `startanims.zrd`
- `data\m4\zrdr\aipath`: `aiv.zrd`, `aiv_easy.zrd`, `aiv_hard.zrd`, `net_01.zrd`, `net_02.zrd`, `net_03.zrd`, `net_04.zrd`, `net_05.zrd`, `net_06.zrd`, `net_07.zrd`, `net_08.zrd`, `net_09.zrd`, `net_10.zrd`, `net_11.zrd`, `net_12.zrd`, `net_13.zrd`, `net_14.zrd`, `net_15.zrd`, `net_16.zrd`, `net_17.zrd`, `net_18.zrd`, `net_19.zrd`, `net_20.zrd`, `net_21.zrd`, `net_22.zrd`, `net_23.zrd`, `net_24.zrd`, `net_25.zrd`, `net_26.zrd`, `net_27.zrd`, `net_28.zrd`, `net_29.zrd`, `net_30.zrd`, `net_31.zrd`, `net_32.zrd`, `net_33.zrd`, `net_34.zrd`, `net_35.zrd`, `net_36.zrd`, `net_37.zrd`, `net_38.zrd`, `net_39.zrd`, `net_40.zrd`, `net_41.zrd`, `net_42.zrd`, `net_43.zrd`, `net_44.zrd`, `net_45.zrd`, `net_46.zrd`, `net_47.zrd`, `net_48.zrd`, `net_49.zrd`, `net_50.zrd`, `net_51.zrd`, `net_52.zrd`, `net_53.zrd`, `net_54.zrd`, `net_55.zrd`, `net_56.zrd`, `net_57.zrd`, `net_58.zrd`, `net_59.zrd`, `net_60.zrd`, `net_61.zrd`, `net_62.zrd`, `net_63.zrd`, `net_64.zrd`, `net_65.zrd`, `net_66.zrd`, `net_67.zrd`, `net_68.zrd`, `net_69.zrd`
- `data\m4\zrdr\bft`: `bftm4.zrd`, `deathm4.zrd`
- `data\m4\zrdr\cathdrl`: `cathscop.zrd`
- `data\m4\zrdr\chem`: `chmsnd.zrd`, `liquids.zrd`
- `data\m4\zrdr\choppers`: `m4_chop.zrd`; `m4_chop1.zan`
- `data\m4\zrdr\envmodels`: `bigbridge.zrd`, `canalmorph.zrd`, `damgates.zrd`, `fogchange.zrd`, `fountain.zrd`, `fueltank.zrd`, `grating.zrd`, `handcar.zrd`, `hruin.zrd`, `raildam.zrd`, `rockbase.zrd`, `rrties.zrd`, `ruins.zrd`, `signal.zrd`, `st_bridg.zrd`, `train.zrd`, `train_collide.zrd`, `wallturr.zrd`, `warehouse.zrd`; `handcar.zan`, `train_s1.zan`, `train_s2.zan`, `train_s3.zan`
- `data\m4\zrdr\vtol`: `m4dropoff.zrd`, `m4flufvtol.zrd`, `m4pickup.zrd`, `m4vtols.zrd`; `m4doexit.zan`, `m4doloop.zan`, `m4dropoff.zan`, `m4exit.zan`, `m4exitcam.zan`, `m4flufvtol.zan`, `m4pickup.zan`, `m4puloop.zan`, `m4startcam.zan`
- `data\m5\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`, `puppies_easy.zrd`, `puppies_hard.zrd`, `startanims.zrd`, `watexp.zrd`
- `data\m5\zrdr\aipath`: `aiv.zrd`, `aiv_easy.zrd`, `aiv_hard.zrd`, `net_01.zrd`, `net_02.zrd`, `net_03.zrd`, `net_04.zrd`, `net_05.zrd`, `net_06.zrd`, `net_07.zrd`, `net_08.zrd`, `net_09.zrd`, `net_10.zrd`, `net_11.zrd`, `net_12.zrd`, `net_13.zrd`, `net_14.zrd`, `net_15.zrd`, `net_16.zrd`, `net_17.zrd`, `net_18.zrd`, `net_19.zrd`, `net_21.zrd`, `net_22.zrd`, `net_23.zrd`, `net_24.zrd`, `net_25.zrd`, `net_26.zrd`, `net_27.zrd`, `net_28.zrd`, `net_29.zrd`, `net_30.zrd`, `net_31.zrd`, `net_32.zrd`, `net_33.zrd`, `net_34.zrd`, `net_35.zrd`, `net_36.zrd`, `net_37.zrd`, `net_41.zrd`, `net_42.zrd`, `net_44.zrd`, `net_45.zrd`, `net_46.zrd`, `net_47.zrd`, `net_48.zrd`, `net_49.zrd`, `net_50.zrd`, `net_51.zrd`, `net_52.zrd`, `net_53.zrd`, `net_54.zrd`, `net_55.zrd`, `net_56.zrd`, `net_57.zrd`, `net_58.zrd`, `net_59.zrd`, `net_60.zrd`
- `data\m5\zrdr\bft`: `bftm5.zrd`, `deathm5.zrd`
- `data\m5\zrdr\choppers`: `m5_chop.zrd`; `m5_chop1.zan`, `m5_chop2.zan`
- `data\m5\zrdr\envmodels`: `bigham.zrd`, `btundoor.zrd`, `clonemove.zrd`, `cloneship.zrd`, `clonesnd.zrd`, `comstation.zrd`, `fogchange.zrd`, `frcsub.zrd`, `hambay.zrd`, `hamdoors.zrd`, `icecolum.zrd`, `icedoor.zrd`, `powerstat.zrd`, `rtower.zrd`, `scontainer.zrd`
- `data\m5\zrdr\subhut`: `sbarm.zrd`, `sbbelt.zrd`, `sbelev.zrd`, `subchip.zrd`
- `data\m5\zrdr\vtol`: `m5dropoff.zrd`, `m5flufvtol.zrd`, `m5pickup.zrd`, `m5vtols.zrd`; `m5doexit.zan`, `m5doloop.zan`, `m5dropoff.zan`, `m5exit.zan`, `m5exitcam.zan`, `m5flufvtol.zan`, `m5pickup.zan`, `m5puloop.zan`, `m5startcam.zan`
- `data\m6\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`, `puppies_easy.zrd`, `puppies_hard.zrd`, `shakers.zrd`, `startanims.zrd`
- `data\m6\zrdr\aipath`: `aiv.zrd`, `aiv_easy.zrd`, `aiv_hard.zrd`, `net_01.zrd`, `net_02.zrd`, `net_03.zrd`, `net_04.zrd`, `net_05.zrd`, `net_06.zrd`, `net_07.zrd`, `net_08.zrd`, `net_09.zrd`, `net_10.zrd`, `net_11.zrd`, `net_12.zrd`, `net_13.zrd`, `net_14.zrd`, `net_15.zrd`, `net_16.zrd`, `net_17.zrd`, `net_18.zrd`, `net_19.zrd`, `net_20.zrd`, `net_21.zrd`, `net_22.zrd`, `net_23.zrd`, `net_24.zrd`, `net_25.zrd`, `net_26.zrd`, `net_27.zrd`, `net_28.zrd`, `net_29.zrd`, `net_30.zrd`, `net_31.zrd`, `net_32.zrd`, `net_33.zrd`, `net_34.zrd`, `net_35.zrd`, `net_36.zrd`, `net_37.zrd`, `net_38.zrd`, `net_39.zrd`, `net_40.zrd`, `net_41.zrd`, `net_42.zrd`, `net_43.zrd`, `net_44.zrd`, `net_45.zrd`, `net_46.zrd`, `net_47.zrd`, `net_48.zrd`, `net_49.zrd`, `net_50.zrd`, `net_51.zrd`, `net_52.zrd`, `net_53.zrd`, `net_54.zrd`, `net_55.zrd`, `net_56.zrd`, `net_57.zrd`, `net_58.zrd`, `net_59.zrd`, `net_60.zrd`, `net_61.zrd`, `net_62.zrd`, `net_63.zrd`, `net_64.zrd`, `net_65.zrd`, `net_66.zrd`, `net_67.zrd`, `net_68.zrd`, `net_69.zrd`, `net_70.zrd`, `net_71.zrd`, `net_72.zrd`, `net_73.zrd`, `net_74.zrd`, `net_75.zrd`, `net_76.zrd`, `net_77.zrd`, `net_78.zrd`, `net_79.zrd`, `net_80.zrd`
- `data\m6\zrdr\bft`: `bftm6.zrd`, `deathm6.zrd`
- `data\m6\zrdr\brain`: `bcamera1.zrd`, `bpanels.zrd`, `brain01.zrd`, `bturrets.zrd`, `cellblk1.zrd`, `celldest.zrd`, `fallobj.zrd`, `plunger.zrd`, `postcrak.zrd`, `totaldest.zrd`; `plunger01.zan`, `plunger02.zan`, `plunger03.zan`
- `data\m6\zrdr\choppers`: `m6_chop.zrd`; `m6_chop1.zan`, `m6_chop2.zan`
- `data\m6\zrdr\elevator`: `bft_trans.zrd`, `elevdown.zrd`, `elevtow.zrd`, `teleprtr.zrd`, `tpaccess.zrd`
- `data\m6\zrdr\envmodels`: `a1.zrd`, `a2.zrd`, `a3.zrd`, `citywall.zrd`, `cltree.zrd`, `fogchange.zrd`, `horizon.zrd`, `liteflsh.zrd`, `objects.zrd`, `stubdoor.zrd`, `stubmid.zrd`, `xposwall.zrd`
- `data\m6\zrdr\vtol`: `m6dropoff.zrd`, `m6vtols.zrd`; `m6doexit.zan`, `m6doloop.zan`, `m6dropoff.zan`, `m6startcam1.zan`, `m6startcam2.zan`, `m6startcam3.zan`, `m6startcam4.zan`
- `data\m7\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`
- `data\m7\zrdr\aipath`: `aiv.zrd`
- `data\m7\zrdr\envmodels`: `bridge.zrd`, `bunker.zrd`, `canend.zrd`
- `data\m7\zrdr\vtol`: `m7flufvtol.zrd`, `m7vtols.zrd`; `m7flufvtol.zan`
- `data\m8\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`
- `data\m8\zrdr\aipath`: `aiv.zrd`
- `data\m8\zrdr\envmodels`: `bridges.zrd`, `deckramps.zrd`
- `data\m8\zrdr\vtol`: `m8flufvtol.zrd`, `m8vtols.zrd`; `m8flufvtol.zan`
- `data\m9\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`, `race.zrd`
- `data\m9\zrdr\aipath`: `aiv.zrd`
- `data\m9\zrdr\envmodels`: `barriers.zrd`, `countdown.zrd`, `haybale.zrd`, `hbarrel.zrd`, `signs.zrd`, `startgate.zrd`
- `data\m10\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`
- `data\m10\zrdr\aipath`: `aiv.zrd`
- `data\m10\zrdr\envmodels`: `bbdoor1.zrd`, `bbdoor2.zrd`, `bbdoor3.zrd`, `burncity.zrd`, `jumps.zrd`, `objects.zrd`, `signdown.zrd`, `snakesign.zrd`, `snakestart.zrd`, `teeth.zrd`, `towers.zrd`
- `data\m11\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`
- `data\m11\zrdr\aipath`: `aiv.zrd`
- `data\m11\zrdr\envmodels`: `icecolumn.zrd`, `scontainer.zrd`
- `data\m11\zrdr\vtol`: `m11flufvtol.zrd`, `m11vtols.zrd`; `m11flufvtol.zan`
- `data\m12\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`
- `data\m12\zrdr\aipath`: `aiv.zrd`
- `data\m12\zrdr\envmodels`: `vensec.zrd`, `warehouse.zrd`
- `data\m12\zrdr\vtol`: `m12flufvtol.zrd`, `m12vtols.zrd`; `m12flufvtol.zan`
- `data\m13\zrdr`: `ai.zrd`, `anim.zrd`, `declient.zrd`, `location.zrd`, `movers.zrd`, `net.zrd`, `objectives.zrd`, `puppies.zrd`
- `data\m13\zrdr\aipath`: `aiv.zrd`
- `data\m13\zrdr\envmodels`: `bft_trans.zrd`, `citywall.zrd`, `cltree.zrd`, `ruins.zrd`, `teleprtr.zrd`, `xposwall.zrd`

</details>

<details><summary>Sounds</summary>

`aav.wav`, `ammo_out.wav`, `ampeng3.wav`, `amptrn1.wav`, `arc01.wav`, `arc02.wav`, `arc03.wav`, `arc04.wav`, `arc05.wav`, `arc_fire1.wav`, `arcon.wav`, `arcstart.wav`, `bcrush.wav`, `bncehard.wav`, `bncewter.wav`, `boxdrop.wav`, `brain1.wav`, `campaign1.wav`, `campaign2.wav`, `campaign3.wav`, `campaign4.wav`, `campaign5.wav`, `campaign6.wav`, `canhit.wav`, `chainbreak.wav`, `charge.wav`, `chopper.wav`, `clone1.wav`, `collaps1.wav`, `collaps2.wav`, `collaps3.wav`, `comp1.wav`, `deadguy.wav`, `deadscr1.wav`, `deadscr2.wav`, `deadscr3.wav`, `deadscr4.wav`, `deadscr5.wav`, `dmsplat1.wav`, `dmsplat2.wav`, `drill.wav`, `eight.wav`, `elect1.wav`, `elevrun.wav`, `elevstar.wav`, `elevstop.wav`, `enemy_engine01.wav`, `enemy_engine02.wav`, `enemy_engine03.wav`, `enemy_engine04.wav`, `enemy_engine05.wav`, `enemy_engine06.wav`, `enemy_engine07.wav`, `enemy_engine08.wav`, `enemy_engine09.wav`, `enemy_engine10.wav`, `erfpg_fire.wav`, `exp_hit1.wav`, `exp_hit2.wav`, `exp_hit3.wav`, `exp_hit4.wav`, `expl01.wav`, `expl02.wav`, `expl03.wav`, `expl04.wav`, `expl05.wav`, `expl06.wav`, `expl07.wav`, `expl08.wav`, `explring.wav`, `fact1.wav`, `fire1.wav`, `firecrackle.wav`, `five.wav`, `flameon.wav`, `fountain.wav`, `four.wav`, `frcfield.wav`, `frcgen.wav`, `gate_brok.wav`, `glassbreak.wav`, `gull_hit.wav`, `gulldead.wav`, `gundown.wav`, `gunside.wav`, `gunup.wav`, `holo.wav`, `hoveng5.wav`, `hydro1.wav`, `hydro2.wav`, `hydro3.wav`, `hydro4.wav`, `incoming.wav`, `labpipe.wav`, `labsnd.wav`, `lgmech.wav`, `lockon_warn.wav`, `loem_fire.wav`, `lowshield.wav`, `ls_fire.wav`, `ls_on.wav`, `m1base.wav`, `m1hint1.wav`, `m1hint2.wav`, `m1intro.wav`, `m1obj1.wav`, `m1obj2.wav`, `m1obj3.wav`, `m1rockt1.wav`, `m1rockt2.wav`, `m1rockt3.wav`, `m1site1.wav`, `m1site2.wav`, `m1site3.wav`, `m2hint1.wav`, `m2hint2.wav`, `m2hint4.wav`, `m2obj1.wav`, `m2obj2.wav`, `m2obj3.wav`, `m2obj4.wav`, `m2obj5.wav`, `m3hint2.wav`, `m3lava1.wav`, `m3obj1.wav`, `m3obj2.wav`, `m3obj3.wav`, `m3obj4.wav`, `m3obj5.wav`, `m3obj6.wav`, `m4hint3.wav`, `m4obj1.wav`, `m4obj2.wav`, `m4obj3.wav`, `m4obj4.wav`, `m4obj5.wav`, `m5hint5.wav`, `m5obj1.wav`, `m5obj2.wav`, `m5obj3.wav`, `m5obj4.wav`, `m5obj5.wav`, `m5obj6.wav`, `m5subhnt.wav`, `m6hint2.wav`, `m6hint3.wav`, `m6hint4.wav`, `m6obj1.wav`, `m6obj2.wav`, `m6obj3.wav`, `m6obj4.wav`, `m6obj5.wav`, `mapclick.wav`, `mapdown.wav`, `mapup.wav`, `metal1.wav`, `mmachine.wav`, `morph1.wav`, `morph2.wav`, `mort_fire1.wav`, `mort_hit1.wav`, `napalm1.wav`, `nine.wav`, `novtol.wav`, `objecthit.wav`, `objective.wav`, `ocean_gull.wav`, `one.wav`, `pickup2.wav`, `ping.wav`, `pkup_amphib.wav`, `pkup_arc.wav`, `pkup_arc_wep.wav`, `pkup_designator.wav`, `pkup_designator_wep.wav`, `pkup_erfpg.wav`, `pkup_erfpg_wep.wav`, `pkup_freon.wav`, `pkup_freon_wep.wav`, `pkup_hover.wav`, `pkup_md_mine.wav`, `pkup_md_mine_wep.wav`, `pkup_md_mort.wav`, `pkup_md_mort_wep.wav`, `pkup_mine.wav`, `pkup_mine_wep.wav`, `pkup_missile.wav`, `pkup_missile_wep.wav`, `pkup_mortar.wav`, `pkup_mortar_wep.wav`, `pkup_nancan.wav`, `pkup_napalm.wav`, `pkup_napalm_wep.wav`, `pkup_nuke.wav`, `pkup_nuke_wep.wav`, `pkup_rem_md_mine.wav`, `pkup_rem_md_mine_wep.wav`, `pkup_rem_mine.wav`, `pkup_rem_mine_wep.wav`, `pkup_sabre.wav`, `pkup_sabre_wep.wav`, `pkup_sonic.wav`, `pkup_sonic_wep.wav`, `pkup_sub.wav`, `pkup_tg_missile.wav`, `pkup_tg_missile_wep.wav`, `pkup_tg_nuke.wav`, `pkup_tg_nuke_wep.wav`, `pkupnan.wav`, `pla_fire1.wav`, `pla_hit2.wav`, `plunger.wav`, `powerstat.wav`, `powerup.wav`, `racedelay.wav`, `refhydro.wav`, `regen.wav`, `review.wav`, `ric_sizzle1.wav`, `ric_sizzle2.wav`, `ric_sizzle3.wav`, `rocket1.wav`, `rollover.wav`, `rumble.wav`, `run_hit1.wav`, `run_land1.wav`, `sequenceinit.wav`, `sequenceinit20.wav`, `seven.wav`, `six.wav`, `sizzle.wav`, `skid.wav`, `skidhvr.wav`, `skidwtr.wav`, `snakepit.wav`, `sonic1.wav`, `sonic2.wav`, `splashbg.wav`, `splashsm.wav`, `squishon.wav`, `steam1.wav`, `steam2.wav`, `stone.wav`, `subext6.wav`, `subhit2.wav`, `subtrn1.wav`, `ten.wav`, `thnder.wav`, `three.wav`, `tnkeng4.wav`, `train.wav`, `traincar.wav`, `two.wav`, `valve2.wav`, `vtol1.wav`, `warn1.wav`, `warn2.wav`, `warn3.wav`, `water_intake.wav`, `watfal1.wav`, `weador1.wav`, `weapick.wav`, `weapon_lock.wav`, `weld1.wav`, `zero.wav`

</details>
