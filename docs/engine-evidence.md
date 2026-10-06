# Engine evidence

What the retail engine does that source projects and the world editor rely on, with the evidence for each fact. It settles the open investigations of the [world editor plan](world-editor-plan.md#open-investigations) as far as the code and the shipped data can. What only the running game can settle is listed per topic and collected in [In-game tests](#in-game-tests).

Researched 2026-10-03 against the 1999 retail executable (1998 where noted) and the reconstructed source. Labels:

- **[B]**: byte-matched in the reconstruction.
- **[BN]**: confirmed in the executable.
- **[D]**: measured on the shipped 1998/1999 data.
- **[U]**: reconstruction only or inference, still to confirm.

Addresses are retail. The zone probes are documented in the plan, under [How the engine sees a map](world-editor-plan.md#how-the-engine-sees-a-map), and modelled by `ZoneProbe` in Core.

## Common rules

- **Errors are silent.** `zError::ReportOld` (0x404e80) is a single `ret` [BN]. Messages such as "GameZ node buffer is full" or "Database intersections array is full" never appear or reach a log. A failure shows only as its effect: a default texture, a missing object, a crash.
- **Resource keys.** `zRdrGetNode` / `zRdrFindNode` (0x48cf70, 0x48cec0) [B] search depth-first through nested arrays. They return the item after the first string equal to the key anywhere, including strings inside values.
  - Of duplicate keys, the first wins.
  - A string value can shadow a key that is read later.
  - A key that ends its array makes the reader read past the array.
- **Script commands match by case-sensitive prefix** (`strncmp` over the command's length): the built-ins (`CZInterp::HandleBuiltinCommand` 0x4c1c50 [B]) and the engine commands (`DispatchCoreCommand` 0x4c20a0 [B]). `FindNodes` runs `FindNode`, `findnode` runs nothing, and `Quit` alone must match exactly; it ends its own script, and the script that sourced it goes on.
- **No type checks.** Gameplay loaders read the raw value union and never call the checked accessors.
  - An integer where a float belongs reads as a denormal: `10` becomes about 1.4e−45.
  - A float where an integer belongs reads as a huge integer: `8.0` becomes 1090519040.
  - A missing item or a wrong shape dereferences garbage. zStudio treats every one of these as an error, not as tolerated input.

## Name lookups and node slot order

- **The node list runs highest slot first.**
  - Loading a world inserts every live node at the head of the Object3D list in ascending slot order: `ReadNodeTable` 0x455350 [B], `ReadSingleNodeClassData` 0x454c60 [BN], `CZTypeList::Insert` 0x44ed90 [B, BN; 1998 0x447c70 BN].
  - So every lookup by name (`FindByTypeAndName` 0x44ecf0) finds the **highest slot** of a name first. Nodes made later go to the head too: the light and sound nodes `LoadZbd` makes for each entry come before the world's, so a later entry's whole-world lookup of such a name finds that node (no shipped name collides).
  - The loading order is `InitMissionGameplaySystems` 0x417a00 [BN]: no node is created between the world load and animation binding.
- **Animation roots** (`zEffect_Anim::LoadZbd` 0x45efb0, binding loop 0x45f5bf–0x45f689 [BN]):
  - Consecutive entries with the same root name take the following same-named nodes, in list order.
  - A new name, or running out of matches, starts again at the head of the list.
  - Entry 0 and entries in state 5 are skipped without breaking the chain.
- **Names inside an animation** (`ResolveNodeByName` 0x45e5c0 [B]) are searched in this order:
  1. the callback node's subtree, preorder, children first to last;
  2. the bound root's subtree;
  3. the entry's own light and sound nodes;
  4. the whole world, highest slot first.
  - There is no shortcut for the root's name, and names compare exactly (`strcmp`): a reference by the root's name finds a node of that name in the callback's subtree first.
  - The callback node is the entry's attach node, itself looked up this way when the animations load (before it is set and with the entry's lights and sounds not yet counted, so the bound root's subtree, then the whole world; a failure fails the whole `anim.zbd`: `LoadZbd` 0x45f6d3–0x45f6e5 [BN]). 1999 m4, m6, m12 and m13 (and 1998 m4 and m6) have hit entries attached to a node outside their root (`hit_wall_102`, root `dwall02`, attaches to one of 66 `wall1`), so their references resolve under that node. A runtime copy of an entry (`CloneEntryForNode` 0x45e730 [U]) and an entry bound again to another node (`RebindEntryToNode` 0x45ed80 [BN]) take the new node as the callback when the loaded callback was the bound root, and otherwise look the attach node up only inside the new node; without it, the entry is disabled (state 5).
  - Binding again takes the new node's name as the root name and looks every tracked node and node reference up again this way; the names themselves stay. A reference by the old root's name therefore finds the new node only when it has that name. 1999 and 1998 m5 start `under_water_exp` (root `watexp.flt`) at pipes' `destroyed` nodes, so its `watexp.flt` references find the world's `watexp.flt`, not the pipe's node [D].
  - The game binds again: a child animation started at a node (`ActivateRuntime` 0x45d930 [B]), even at the node it is bound to; a stop at a node (`StopAndCleanup` 0x45d570 [B], which turrets and vehicles call through `NodeActionCallback` 0x45d6b0 [B]) only at another node; and a root flagged 0x8000, which `LoadAndInstantiate` 0x45fb30 copies and binds to the copy (`EnsureCopiedRootTree` 0x45e6d0 [B]). No shipped entry has the flag [D].
  - What the whole world holds depends on when the lookup is made. As the mission loads (`LoadZbd`, the copies, the turrets' stops) it is the world file's nodes and the animations' own light, sound and copied nodes; AI vehicles and pickups are placed afterwards (`InitMissionGameplaySystems` 0x417a00: animations, turrets, `Player::InitMissionRuntimeFromWorldAndCamera`, then the pickups), so only lookups made while the mission runs find their copies. The preview resolves names as the game does at each of these points; the animations' own light and sound nodes are not scene nodes there.
  - Vehicle code looks parts up differently (see [Vehicles](#vehicles)).
- **The allocator** (`gwNodeNew` 0x4478c0 [BN], `FreeNodeToFreeList` 0x447a70 [B], `DestroyNodeRecursive` 0x451a60 [B]):
  - It is one free list for every node class, last in, first out. A freed slot keeps its old name.
  - `DeleteTree` frees post-order. Models (0x482080, 0x4820f0) and materials (0x480dc0) are also last in, first out.
  - No lookup by name depends on model or material slots.
- **The allocator in detail** [B]:
  - `DestroyNodeRecursive` returns at once while the node still has a parent (+0x54). Otherwise it removes `children[0]` until none is left, destroying each child left without a parent, then frees the node (`DeleteNodeByType`). A node under two parents is freed with the last of them; world objects survive `DeleteTree %dbName%` because the world is their second parent.
  - `AddChildGeneric` 0x4484d0 appends to the child and parent lists; `RemoveChildGeneric` 0x448660 removes keeping the order.
  - `TryFreeNode` 0x447b60 queues a free (`CZNodeList::Insert` 0x44ed60, at the head) while `g_CZClass_DeferredProcessingEnabled` (0x4dded8, initially 1) is 0; the runtime update passes clear it (`CZTypeList::UpdateBucket` 0x44eaa0, `UpdateSequences` 0x44ebe0, `UpdateAnimations` 0x44ec30 [U]). Until a queued free runs, the node's type-list link is only marked (`MarkPendingRemoval` 0x44eed0), and `FindByTypeAndName` does not skip marked links, so a lookup during those passes can still find a node being freed. `ProcessPendingFrees` 0x44eea0 frees the queue from its head. A build frees at once.
- **How the lost build tool loaded a file** (`LoadGameGen`; not in any shipped executable) [D, from the shipped slots and free lists; checked independently by a second model's allocator replays, see below]:
  1. Each file the file's records reference is loaded once into a cache, in the order of its first reference (also among the records a reference has of its own): the file loaded on its own (its own caches, its instance definitions, a root, its records), kept until the end. A file named by two different paths is cached twice: in mission databases (1999 m8 `frntbrdg.flt`; m1 `veg1`, `veg2`, `gullfly`, `pier`), and inside model files (`comanche.flt` names `hturret.flt` three times by two paths; 1999 m4 `chemplnt.flt` names `chempipl.flt` by two). No shipped file references a file without nodes; zStudio's build caches one like any other (a root alone), as the rule implies, and its empty copy waits for the next record. Reconstruction reads such a reference the same way (an object3d without geometry named for its file, without children) and writes it as a reference, so a rebuilt world caches it where the built one did.
  2. Its instance definitions: a node the records reach along more than one edge (an OpenFlight instance definition, which has no name of its own) is made once with its subtree; its instance references attach it without making nodes. 1999 m9's unnamed node under `sgate1`…`sgate8` is one.
  3. The load's root.
  4. The records in order, a node and then its children. A reference's content is copied from its cache after the node of the next record is made. A reference that has records of its own is copied at once, before them. When a reference is the file's last record, an end node is made for the copy and freed after it. A copy expands instances along every edge, so a cached file's identical unnamed subtrees (alike in their names, models and transforms all the way down) are one definition again (1999 m1 `rktpad.flt`'s `r_blst1`…`6` and `smkring.flt`'s `s1`…`s8`).
  5. A cached file's own caches are freed when its load ends; the caches of the outermost load are freed when it ends, in the order they were made, each as `DestroyNodeRecursive` frees it (a shared node with its last parent).
  - The root and first records take the slots the caches' own loads freed and nothing took again, last freed first, before fresh slots: 1999 m13's database begins on the slots `walldes1.flt` and `walldes2.flt`'s caches freed inside its last part's load, m5's on its last caches' slots although no record lies below its root, m2's on slots several caches left.
  - A copy shows which records a file had, but not always where its groups closed: a group's last objects may have followed it. The cache frees them in the order its groups held them, which settles it (1999 m1, m4, m13).
  - Two later loads of one file under one name cannot be told apart in the world; the database's caches decide which made which nodes (1999 and 1998 m1 `rfpg_mzl.flt`).
  - Models are made when a file is read, by a cache's load or by the load of the file itself, and a copy uses the models of the cache it copies [D: every 1999 and 1998 world rebuilt with these rules shares each model among exactly the shipped nodes]:
    - Copies of one cache share its models. So do the AI clones the game makes from a template, which share the template's scroll, morph and texture-cycle state (below).
    - Each cache has models of its own, also for a file another file's load reads again: 1999 m1's palms are read once by every part that names them, and m4's and m12's warehouse pieces 28 times.
    - Each object read has a model of its own, even when its values equal another's (1999 `redsprks.flt`'s four pieces).
    - A world stores its models in the order the loader made them: caches before records, a node's model when the node is read. A few nodes with children got theirs after their children's (turret guns, some groups: 2–28 nodes per mission), which the face order of the lost files decided; and 1999 m3's `rfpg_mzl.flt` model comes before the database's records although a later load reads the file. Neither is reproduced; the order affects nothing in the game.
  - Where the slots fit several references as the one that made a second cache, the models decide: 1999 m5 references `btundr1.flt` three times, and the copies under `tun_door_01` and `tun_door_02` share models while the one under `tun_door_03` has its own (its polygons are in zone 11, the others' in zone 6), so the third reference, not the second, named the file by its second path. m3's `lturret.flt` and `pturret.flt` are the same case (both releases). The scripts set no zones, so the two caches' polygon zones came from the files themselves. The slots show only which reference made a cache; the models also show which later references copied it: 1999 m1's `fgull08` and `fgull09` copy the cache `fgull07` made, and m4's `chemplnt.flt` names `chempipl.flt` by its second path four times.
  - What earlier looked like the tool attaching records elsewhere, reusing nodes by name, or databases beginning on freed slots is explained by these rules: in 1999 m1 the large part's reference is the group in slot 2065 (2066 is the next record, which triggers the copy), and slot 3987 is a group whose making triggers the last `samsite1.flt` copy; m3's `conegate.flt` cache fits once `comanche.flt`'s second path is known; m13's `wall_a1` makes all its children in one go across the change from freed to fresh slots.
- **The mission databases were several files** [D]. The database file referenced other files holding parts of the world: groups and objects, often with references of their own, and sometimes parts of their own (1999 m5's first part holds a group with a reference to a part of `comctr` and `tunl`, then the two radio towers). The reference is a group the build deleted; its part is cached and copied like a model. Examples: 1999 m1 `dock1`, `dock2` and `dock_house` (copied after `lighthouse`, between it and its own child), `g307` and `g306` (between `rocket11` and its pad), a 302-node part, and m13's teleport and terrain parts, which hold groups, objects and levels of detail but no model references.
  - Names the build deleted are lost, except where slots stayed free: every shipped world but m6 is dense, and 1999 m6 keeps 287 freed slots with names such as `g219`, `street`, `hole01`, `phone.flt` and `db`.
  - Reconstruction (see [source-project.md](source-project.md)) infers these files and replays the build to check each mission. Every node takes its shipped slot when built, for every mission of both releases. For 1999 m2, m4 and m5 (both releases) and m13 a second model (ChatGPT Pro, consulted 2026-10-03 with these slot tables) found complete witnesses with the same rules, for m3 a prefix, and on 2026-10-05 for m6 (both releases).
  - m2 needed three of the same rules, found by search rather than per mission: the slots its records start on come from several caches' leftovers (553–573, 726–756 and 914, below the caches' high-water mark 916), so where the caches end in the free list is the boundary whose caches' simulation ends there and gives back its mark; its factory part's last ten objects are the next group's records, with `nturret1.flt` named by a second path in the shorter part; and its last record is a part reference whose copy, after the end node, holds `labradio` alone. The free list that fits is the one where `comanche.flt` names `hturret.flt` by a second path. Child order follows, so `world1` lists `mcar_01` … `mcar_06` first, as shipped.
  - m6 needed no new loader rule [D, matching the second model's complete witnesses]:
    - Its free list is the reading where `comanche.flt` names `hturret.flt` by a second path; it differs from the other reading in 86 entries, from entry 546 on.
    - Its database load made 20 caches, 11 of them parts (the aztec, beach and lava parts hold a part each). The city part, cached first, names `phone.flt` once by a second path; the brain part names `braintur.flt` once by a second path and closes a group before its last objects; the lava part also closes a group early.
    - Its records begin on the 126 slots the last part's own load freed: its inner part's cache, `dr02.flt`'s cache and its end node. The first records are `horizon` and its children, then the city part's reference and the group after it, which triggers the city copy. That copy uses up the freed slots and continues into the fresh ones (1804 in 1999, 1799 in 1998), so no walk of the fresh slots alone can find where the records start.
    - m6 left its first cache's slots free with their names, so the bottom of its free list holds that cache whole (402 nodes from slot 46, made from fresh slots). Reconstruction reads it from there, names the references whose slots were taken again (and lost their names) from the copy in the database, simulates the caches from it and finds the 126 slots.
    - `db` (4787 in 1999, 4777 in 1998), the first slot freed, is the end node the database's last part reference needed.
  - Repeated names that bind to another node in a rebuilt world (the highest slot of each name compared with the shipped world): none of 2,021 in 1999 (14 before m6 was rebuilt in its record order, 47 before m3's and m5's second paths followed their models, 94 before m2 was rebuilt in its record order, 260 before m2's and m6's databases kept their objects in slot order with groups at the freed slots, 472 before these rules, 702 before the parts), none of 1,427 in 1998 (was 15, 49, 96, 256, and 417 before the rules). Every lookup the game makes by name finds the shipped node in both releases: animation roots, attach nodes, names inside animations (subtrees in child order, then the whole world), node prerequisites, texture-effect `FindNode`, `effects.zrd` templates and `ai.zrd` turret targets and deactivations. These are Compare worlds' counts, which pair nodes by structure; an earlier count identified nodes by their ancestors' names and gave 250 and 246, missing ten m6 objects directly under the world (such as `g234` and `lavascroll05`) that share their name with another one there.
  - The parent-child structure of every rebuilt world is the shipped one (both releases, a fresh reconstruction compared with Compare worlds): no node is changed, missing or extra.
- **Lookups that resolved differently before the parts** [D] (exported worlds numbered nodes in creation order):
  - m1: 4 entries;
  - m2: 10, including `smoke1` and the com-centre dish of `comdish_rotate`, which then bound the amphibian template's `radar`;
  - m4: 36 (`hit_warehous_*`, `fill_4`/`fill_5`);
  - m5: 5;
  - m6: 40 (`hit_wall_1xx`×31 and others);
  - m12: 7;
  - m13: 36;
  - m3 and m7–m11: none (1998 m3: 1).
  - The game's own `tex_fx` `FindNode` also changed target 21 times across m1–m6. 16 of those targets have models of their own.
- **Even shipped data is inconsistent.** Which `smoke1` binds differs between shipped missions. Several cross-links come from wildcard definitions applied to roots that lack the named node, which looks accidental.
- **For zStudio:**
  - The animation preview takes the highest live slot (freed slots keep their names but are not nodes): `AnimationPreviewContext.ResolveRoot` with the binding loop's chain (`NameLookups.RootPositions`, entry state at +0x98), its whole-world fallback (the most recently created node), effect templates, the texture-cycle lookup in `Textures.cs`, and the mission's AI vehicles. Lookups made as the mission loads see only the world file's nodes.
  - Check and export report every lookup with more than one candidate (`WorldLookups`), and the lookups an export or an edit changes.
  - An explicit refactor (renaming the dish, one `smoke1`, restricting wildcards) changes shipped behaviour and must be stated, never silent.

## Craters and quicksand

- **Pipeline** [B, BN]:
  - **Hit:** a weapon's `CRATER (min max)` hits a node with CanModify (0x10000). The radius is uniform in [min, max]; `MAX_CRATER_RADIUS` is never applied on this path, and shipped radii are 10–39.
  - **Template:** `declient.zrd` `CRATER` gives `POINTS 6` and `DEPTH 1.7` in every mission.
  - **Moving inward** (`InitFeatureFromEventTemplate` 0x456c80 [BN]): the centre moves so the circle lies at least 1 unit inside the impact cell.
  - **Clipping:** the outline is clipped into the touched surfaces (`ClipPatch` 0x46b1f0 [BN]).
  - **Bowl:** `n` quads and `n` triangles, its centre 2 × DEPTH below the impact (`CreateFeature` 0x457140 [BN]).
- **Grid, limit, eviction.**
  - The feature grid is the world's cell grid. Each 64-byte area record counts its craters at byte +0x39, which is 0 in every shipped cell.
  - A cell refuses a crater once its count reaches world byte +0x4C [BN]: 16 in every shipped world, at most 255 (`WorldPartitionMaxDECFeatureCount`). Craters and quicksand share the count.
  - Nothing is evicted. A refused crater plays the normal impact effect.
- **No stacking.** A crater is refused when its outline box, with a 5-unit margin, overlaps an existing feature in the cell [BN]. The bowl node is also ClipTo (`gwNodeSetFlag17` 0x447d70 [B]), so later outlines over it are cancelled.
- **What the test sees** (`ProcessNodePolygonSetXY` 0x46b550 [BN], `ClipPatch`):
  - only the impact cell's top-level list (clamped to the edge cell) and the world overflow list, overflow first;
  - only active (0x04) nodes with their own model, and every ClipTo node before any CanModify node; a node with both counts as ClipTo;
  - no zone, gate, height, LOD or child test;
  - the model's own vertices with **no transform**.
  - So a transformed or nested CanModify node never craters as placed; M2 `g502`/`g503` are such cases [D]. A ClipTo group without its own model blocks nothing; M2 has 30 such bunker groups [D]. Every overlapping CanModify layer is cut at any height; one shipped spot is M4 `g659`/`g660` over `g671`/`g672` [D].
- **Each accepted crater creates:**
  - one bowl node, ClipTo, gated, with the zone of the first touched CanModify node whose zone is not 0xFF [BN 0x46af40]; its polygons carry no zones (count 0);
  - one bowl model;
  - one replacement model for every touched node, holding a full copy of its polygons (`ApplyNodeDiPairs` 0x46ae40 [B]). Quicksand creates 2 nodes and 2 models.
  - At radius 25, 88–100% of craters touch a single node; at most 3 nodes in M2 and 4 in M4 [D].
- **Model limits while clipping.**
  - A replacement model is rebuilt polygon by polygon (`AddPolygonEx` 0x483650 [BN]). The polygon that would need vertex 923 is dropped, and so is everything after it, while the crater still succeeds.
  - Shipped growth is 6.5–11 vertices per crater per node (at most 26), so 16 craters reach 291 vertices at worst [D].
  - Crater models carry no normals.
  - Clipped pieces get UVs from a flat projection of their first three corners and are rounded to 1/256.
  - A crater inside one polygon goes through `TriangulatePolygonWithHole` 0x46c070 [BN], whose 32-entry edge stack has no bounds check.
- **Saved games.**
  - Saving writes each crater's original template (`WriteFeatureSectionsToZAR` 0x457b40 [B]).
  - Loading reloads the modified nodes' models from `gamez.zbd` by slot, deletes the bowls, resets their cells' counts and replays the craters (0x457c10, 0x457750 [B]).
  - [U] A road crater may come back with the default crater material after a restart, because the material is matched by address.
  - Animation states are saved with their nodes' slots (`NodePtrToValidatedIndex`, `zeff_anim_save.c` [U]), so a save made before a world's slots changed restores them onto other nodes; saves made with the retail files keep working with exports that give every node its shipped slot.
- **Multiplayer** [U, from the reconstruction]: a crater event names its surface's material by its index in the world's material table (`craterTypeId`, `IndexFromPtrOrMinus1`); the receiver looks the index up in its own table without a range check, and nothing compares the players' files. Players need the same exported worlds: another material order shows other craters' textures. Rebuilt worlds order their used materials differently from the shipped ones and omit the shipped worlds' unused colour materials (made by the original tool for point entries such as lens flares), so they are not crater-compatible with the shipped files; the user decided that is not needed (2026-10-05).
- **Retail bugs** [BN]:
  - A clip that fails partway leaks the models it already built.
  - `ClipPatch` sizes its result array by the cell's node count but also clips overflow-list CanModify nodes, so more touched nodes than the cell holds overruns the heap. No shipped CanModify node is in the overflow list [D].
- **For zStudio (crater-capable terrain):**
  - Use only top-level nodes with their own model and an identity transform, in a cell, never in the overflow list.
  - Keep at most about 450 vertices per node, so 16 craters fit in the 922-vertex budget.
  - Use convex, upward, UV-mapped polygons of at most about 30 corners.
  - Make cells at least 2·Rmax + 2 wide (80 for the shipped weapons).
  - Keep overhang into neighbouring cells under 1 unit.
  - Allow one CanModify layer at any point.
  - The plan's crater test means: active top-level ClipTo nodes with their own model, in the impact cell or the overflow list, tested in model space.

## Runtime capacity

- **Pools are fixed arrays with free lists.** Nothing grows during play.
  - `initmN.gw` sizes only the first allocation: 16000/6000/5000 in every shipped script; the defaults are 8250 / 1750 / 2500 (0x451900, 0x475e70, 0x480ae0 [B]).
  - The world file's own pool headers and free chains then replace them (0x455350, 0x481fa0, 0x4808c0 [B]).
  - So the usable capacity is the exported file's free chain. Every shipped world stores 16000 nodes, 6000 models and 5000 materials [D].
  - The engine has no capacity maximum except 32767 materials. zStudio's 65,536-node clamp is its own choice.
- **What copies cost** (`CopyNodeDispatch` 0x452400, `CopyNodeDisplayInstance` 0x451b20 [B]):
  - **Single-player vehicle clones:** nodes only, sharing the model.
  - **Pickups:** nodes, plus a model per node that has one.
  - **Effect instances and network vehicles:** nodes, models and materials.
  - **Light, sound, animate, sequence, switch and world nodes:** never copied. Under a LOD, one failed child aborts the copy and leaks the nodes already made.
- **Consumers.**
  - **At load:**
    - vehicle clones: 25–35 nodes each, 4,342 nodes for m6's 143 [D];
    - pickups;
    - one runtime node per animation entry, plus extra light and sound nodes;
    - the projectile pool: Σ(trunc(range × rate / speed) + 1), plus 9; 455 nodes in every mission [D] (0x4b1190 [BN]);
    - effects.
  - **During play,** each kept until the mission unloads:
    - effect instances at peak concurrency;
    - extra concurrent animation instances, never freed;
    - pickups dropped by killed vehicles (collected pickups are only deactivated);
    - craters (1 node and 1 model each, and replacement models);
    - material clones, which are never freed (`zDi::FreeContents` 0x482160 [B]).
- **Running out is silent** [BN]:
  - **Nodes:** `gwNodeNew` returns 0, and objects, effects and animation instances are skipped.
  - **Models:** a model shortage also leaks the node already made for the copy.
  - **Materials:** they fall back to the default white material.
- **Other fixed tables:**
  - **Texture directory:** 4,096 entries. Runtime appends are not bounds-checked (0x46d810 [B]), so overflowing it corrupts memory.
  - **Lights:** 64 active per frame (0x487a30 [BN]).
  - **Animation entries:** at most 254 light and 254 sound references each.
  - **Cells:** 32,767 nodes each.
  - **Craters:** 16 per cell.
  - **Rendering queues:** 256 transparent polygons per frame; overflow is dropped.
- **Shipped headroom after load** [D]:
  - **Nodes:** the tightest pool is m6 with 5,091 free (32%); m1 has 7,595 and m8 14,342.
  - **Models:** at least 3,970 free.
  - **Materials:** at least 4,327 free.
  - **Texture directory:** at least 3,283 entries free.
- **Budget.** Required node capacity ≥ the sum of:
  - the stored live nodes;
  - the animation entries' nodes;
  - the projectile pool, plus about 6 fixed nodes;
  - the AI vehicle clones and pickup placements, minus the turret helpers freed at load;
  - a reserve for effects, concurrent animations, drops and craters (cells × min(16, expected)).
  - Models and materials follow the same pattern.
- **For zStudio:**
  - Validate the three free chains.
  - Report load-time demand per pool as an error, and the reserve against headroom as a warning; the fallback is the shipped minimum above.
  - Report the texture directory total against 4,096.
  - Flag light, sound, animate, sequence and switch nodes inside vehicle and pickup templates.

## Vehicles

- **Lookups.**
  - **Vehicle code** (`FindSubNodeByName` 0x452770 [BN]): only the vehicle's own subtree, the root first, then children last to first.
  - **Animations** (`ResolveNodeByName` 0x45e5c0 [B]): first to last, then the whole world.
  - A repeated role name can therefore resolve differently for each, and a missing part can bind a node elsewhere in the world. No shipped vehicle repeats a role name [D].
- **Roles the code drives:**
  - `rtracks` and `ltracks` must be separate textured models.
    - Each scrolls its whole model along **V** at 1.72 × the side speed (forward speed ± yaw × 2.25) [BN 0x42704e]. The reconstruction's U is wrong.
    - `rtracks`' first material switches its cycle frame by `track_switch` thresholds. An `rtracks` without a model crashes.
  - `chassis` rotation is overwritten every tick with pitch and roll in track mode [BN].
  - `turret` (yaw) and `gun` (pitch) have their whole rotation overwritten every tick (`UpdateGunAndTurretAimNodes` 0x43a4f0 [BN]). The pivots are their origins, and authored rotation is lost.
- **Fire points.**
  - They are `fpnt_c`, `fpnt_l` and `fpnt_r` under `gun`, used as offsets in the aim basis; any model on them is detached.
  - Without `gun` or `turret`, shots leave from the root plus (0,1,0).
  - The string `firepoint` does not exist in the executable; only turrets name fire points, through `ai.zrd`.
- **Helpers.**
  - `collide00`–`collide11` and `support00`–`support03` give only their local position, and are then deactivated (0x421ed0, 0x4220f0).
  - Collide points are read only when `vehicle.zrd` has no `collision` list, and only from the first clone.
  - Supports are read for every clone and override a `platform` list.
  - Shipped numbering:
    - collide 00–02 front, 03–05 right, 06–08 rear, 09–11 left;
    - supports front-left, front-right, back-right, back-left.
  - Every shipped helper is an untransformed direct child of `healthy` [D].
  - `target` adds its local position without the vehicle's rotation.
- **Mode parts:**
  - **Track:** `chassis`, the tracks, `dust_l`/`dust_r`.
  - **Amphibious:** `wake`, `splash_l`/`splash_r` [U].
  - **Submarine:** `props` cycle speed and `caustic1` cycle.
  - **Hover:** `shadow` only.
  - **Morphs** are driven by the mode-change animations.
- **Clones.**
  - The template is the AI vehicle name up to its first `_` followed by a digit (0x423150 [BN]). So `ltank_2_05` spawns `ltank`, and the `*_2` templates are never used.
  - Single-player clones share the template's model, so they share track scroll, morph and texture-cycle state. Only transforms and active flags are per copy.
- **Animation lifecycle.**
  - **Start:** `start_anims` runs once per created vehicle, as its own instance on that clone (0x420d10 [BN], `ActivateRuntime` 0x45d930 [B]).
  - **Mode changes** never touch it.
  - **Death** never stops it: the death sequences hide `healthy`, so a loop under `healthy` keeps running invisibly.
  - **Stopping by name:** `STOP_ANIMATION` by name stops only the base entry, which is the first-spawned copy's instance (`zEffectAnim::Stop` 0x45c040 [BN]).
  - **Respawn** never re-runs `start_anims`.
  - **Saved games:** sibling instances are restored onto the base entry (0x461040 [U]). A constant spin survives this; timed loops may not.
- **For zStudio (vehicle upgrades):**
  - Require unique role names across the vehicle subtree, and textured `rtracks` and `ltracks` with the tread laid out along V.
  - Give `turret`, `gun` and `chassis` identity rotation and the pivot at the origin.
  - Make helpers geometry-free, unrotated direct children of `healthy`, numbered as shipped.
  - Put `fpnt_*` under `gun`.
  - Put spinning parts under `healthy`, keep loops constant-rate, and never stop a cloned vehicle's loop by name.
  - Keep `props` as an empty pivot or its cycled model.
  - Upgrading `ltank` changes every `ltank_*` placement.

## Animation runtime

- **A keyframe segment** (event 0x0C: `AdvanceKeyframe` 0x45b120, `AnimateKeyframeSample` 0x45ae90 [B]) is evaluated from its own base every frame. With τ = min(elapsed, t1) − entry:
  - position P0 + V·τ, scale S0 + G·τ (no clamp; event 0x0B clamps scale at 0.001);
  - rotation exp(Ω·τ) ⊗ q0 (`zMathQuatFromRotationVector` 0x475b80 [BN]: a true square root and sin/cos of |Ω·τ|, so Ω is a half-angle rate);
  - then the quaternion becomes a matrix and Euler angles (0x475a80, 0x474e10 [BN]), and the local matrix is rebuilt from translation, rotation and scale.
  - Nothing is normalised, and the node keeps no winding.
- **Between segments** the runtime reads only the next segment's channel flags, never its values. A channel the next segment does not key holds the current end value. Continuity is the compiler's job.
- **Shipped segments** [D]:
  - 176 events and 14,747 segments; the largest event has 520.
  - Shortest segment 0.04 s; the largest rotation in one segment is 159°.
  - Jumps of up to 1.8° between segments come from the original compiler's approximate spin, which zStudio's SI compiler reproduces.
- **Time.**
  - The frame step comes from `GetTickCount`, capped at 0.125 s (`Time::Tick` 0x4a56d0 [BN]).
  - An event starts with the frame's remaining time, so its pose at τ = 0 is never shown (`RunSequenceEvents` 0x45cc00 [BN]).
  - A loop drops up to one frame per iteration.
  - Animations paused by range or zone gating do not catch up afterwards.
  - `SCRIPT_FRAME_RATE` matters only to the compiler.
- **Morph** is driven only by event 0x0B (`AnimateNodeOverTime` 0x45a9d0 [BN]): the morph factor advances at its rate and snaps to the end.
  - Above 1 it is clamped to 1. Below 1e−5 the morph is switched off, but the value is kept and can go negative.
  - A LOD with a range fade overwrites its descendants' factor every frame (0x44b8c0 [BN]). No shipped morph sits under one [D].
- **Reset.**
  - The rest pose is captured at every activation (`CaptureNodeStates` 0x45d240 [B]), not taken from the world file.
  - It is restored only by stop-with-cleanup after a `RESET_TIME` ≥ 0 (0x45d310, 0x45d570 [B]). A negative `RESET_TIME` keeps the final pose.
  - Opacity, texture variant, parenting and camera values are not restored.
- **Hit activation.** `WEAPON_HIT`, `COLLIDE_HIT` and `WEAPON_OR_COLLIDE_HIT` entries count down a value at +0xB0 through the callbacks of their attach node, and start when it reaches 0 (weapon hits: `TickResetDelayOnHit` 0x458bb0; collisions: `TickResetDelayOnTimer` 0x458b50; reconstruction names [B]).
  - The loader keeps the stored countdown (`LoadZbd` 0x45efb0 reads each entry whole and clears only its pointers [U]). Only a stop copies the `HEALTH` at +0xAC into it (`FinalizeStop` 0x45d3d0 [B]), and at load only entries with a single `RESET_TIME` (flag 0x20) are stopped.
  - Every shipped entry stores its `HEALTH` in both fields [D]. With 0 stored, an entry without load cleanup starts on its first hit: in 1999 the VTOLs' `destroy_vtol1` and `vtol2` (m1–m5) and m6's `vtol1`, which need 15 damage.
- **Game-driven nodes.** Callbacks run by priority 0–5, and the last writer in a frame wins.
  - Vehicles tick at priority 2 (0x420057 [BN]). Animations default to 4, which overrides them visually; the 34 shipped definitions at 1 are overwritten.
  - Turrets aim at priority 1.
  - Rotating or scaling a node with an authored matrix discards the matrix.
- **For zStudio** (Blender action import):
  - Sample Blender's evaluated local transforms; only linear position and scale and a constant-rate fixed-axis rotation can be represented.
  - Compute exact rates (V = ΔP/Δt, G = ΔS/Δt, Ω = log(q1·q0⁻¹)/Δt) with a continuous quaternion hemisphere, and never use `SiMath`'s spin for new content.
  - Use SPIN for turns of π or more.
  - Store unit quaternions.
  - Make segments contiguous and key every moving channel in each segment.
  - Never key `INPUT_NODE`.
  - Morph: one shape key per model, as 0x0B events in [0, 1], never under a range-fade LOD.
  - Make loops start and end on the same pose.
  - Do not key nodes the game drives, or set the priority on purpose.
  - Avoid pitch at exactly ±90° with |roll| > 90°.
  - The preview differs from retail in three ways, to label or fix: gaps between same-flag segments, the morph clamp, and 0x0B evaluated absolutely rather than incrementally.

## Missions and slots

- **No mission table.**
  - Every per-mission input is formatted from the mission number without a range check: `support\initm%d.gw`, `m%d_zbd.gs` and the sound set `M%d` (`LoadMissionCoreResources` 0x417810 [BN]), and `zbd\m%d\zrdr.zbd` with its search path (0x42ecb0 [BN]).
  - `maps\m%d.zmap`, `briefing.zrd` `CAMPAIGN%d` and `Weather.zrd` `MISSION%d` are optional.
  - Everything else (world, animations, packs) is named by the mission's scripts. Scripts not in `interp.zbd` are read loose from the game folder (`RunScriptFile` 0x4c1500 [B]).
  - Saved games store the number as a plain integer, with no range check on load (0x4174f0 [BN]).
- **Choosing a mission.**
  - **New Game** always starts mission 1 (0x41c525 [BN]).
  - **Completing a mission** queues the next number unless the mission's `objectives.zrd` sets `FINAL_MISSION` (`inc edx` at 0x418fdf [BN]). Only m6 sets it.
  - **Multiplayer worlds** come from `dialog.zrd` `MP_NEW_GAME/WORLD/CYCLE`, which holds up to 20 entries; entry *i* is mission *i* + 7 (0x41a5b0 [BN]).
- **Per-number rules fixed in the executable:**
  - **Weapons in network play:** a 13-row table (`CheckMissionWeaponAvailability` 0x43ca90 [B, BN]). Mission 14 reads past it into its own return address and allows every weapon; 15 and up read undefined stack data.
  - **Amphibious mode** online only in missions 9, 11, 12 and 13 (0x423380 [B, BN]).
  - **Single player:** amphibious from mission 3, hover from 4, submarine in 6 only, carried forward by saves.
  - **Mission films:** looked up as `'M'` plus the character `'0' + number` (0x42edb0 [BN]). Missions 10 and up never match, and the shipped `M10`–`M13` entries are dead.
- **For zStudio:**
  - Any existing slot can be replaced as pure data.
  - A new multiplayer arena m14 is pure data: its files, scripts and a `dialog.zrd` entry. Arenas 15 and up need a patch.
  - A new single-player mission is reached only through the chain: clearing m6's `FINAL_MISSION` makes m7 next, and that slot is also arena 7. Otherwise New Game and the next-mission step need patches (0x41c526, 0x418fdf).

## Resource schemas

These are the keys the game reads. Anything else in a file is ignored. Types are exact (see [Common rules](#common-rules)).

- **`net_NN.zrd`** (`AINet::LoadFromZrd` 0x403040 [B, BN]), loaded for NN 01–99 in single player only:
  - **Identity:** the network ID is the file number.
  - **`version`:** must be the integer 105 or be absent, otherwise the whole network is dropped silently.
  - **`name`:** copied unchecked into 20 bytes.
  - **`type`:** the first two letters ST/HI/FI/DE; anything else is standard.
  - **Floats:** `path_width` (default 10), `activate_rad`, `attack_rad` (0 → radius about 38.7), `attack_dwell` (0 → 10 s), `not_pursuit_dwell`, `return_range`.
  - **`pursuit_params`** (or `pursuit_range`): two floats.
  - **`hide_times`:** 8/4 only when absent.
  - **`attack_strategy`:** the first three letters FOL/CIR/HEA/BAC/ZIG/SIT. **Missing or unrecognised means head-on (HEA).**
  - **`attack_buddy`:** a flag; vehicles with the same network ID alert each other. `activate_buddy` is never read.
  - **`node_00`–`node_98`** are read in index order: an integer (never read), XYZ and three links. **A link to a missing node crashes at mission load** (0x403550 [B]). Shipped: 470 networks, none with a dangling link or a self-link [D].
- **`aiv.zrd`** (0x41fe90 [BN]; only `aiv.zrd`, never `aiv_easy` or `aiv_hard`) is a list of name and record pairs: `name ( netId:int ( x y z :float ) yawDegrees:float )`.
  - **Names:** at most 27 characters; the template is derived as in [Vehicles](#vehicles).
  - **Lookup:** `CreateFromNamesAtPose` 0x421ab0 [B] finds the name, else copies the template, with `FindByTypeAndName` among all live nodes, the most recently created first, and uses what it finds as the vehicle whatever its class. A light or sound of that name would be placed as the vehicle; no shipped name collides [D].
  - **Spawning:** the entry spawns only when its template occurs in the selected `vehicle*.zrd`.
  - **The local player** is the first entry that spawns (`bft_00` in every shipped file).
  - **Network ID:** 0 means no AI; an unknown network leaves the vehicle inert.
  - Shipped: 1,838 entries, all well typed; one duplicate, `bft_00` in m2 `aiv_easy.zrd` [D].
- **`ai.zrd`** (turrets, 0x437ac0 [BN], 0x4367a0 [B]) has the root keys `DESTROY_ANIM` and `TURRET` (pattern and definition pairs). Per turret:
  - **`PARTS`:** 2–4 names, bound only under a `healthy` node.
  - **Behaviour:** `DEACTIVATE`, `EFFECT`, `ACTIVATE_ON_HIT`, `ALWAYS_LOOK_AT`, `DAMAGE_PART`, `DESTROY_ANIM`, `FIRE_ANIM`.
  - **Defaults:** `HEALTH` 100, `INTERSECT_BVOL` 1, `LOS` 0.
  - **`TARGETS`:** at most 8, each the highest slot of its name in the whole world (exact name, no wildcards), aimed at through its local matrix while active (`InitFromReaderNode` 0x4367a0 [B], `Tick` 0x436e40 [U]). Shipped `TARGETS vtol` finds a `vtol` child at its parent's origin, which only its parent `vtol1`/`vtol2` moves, so the SAM sites aim at a fixed point; `hel_*1` and `braintur_*` find nothing.
  - **Names:** `DEACTIVATE` finds its first name in the whole world, then each further name in that node's subtree; `PARTS`, `EFFECT` and `DAMAGE_PART` search the turret's subtree, preorder.
  - **`MSL_LOCK`:** presence only.
  - **`WEAPON`:** `NAME` is required (a missing or unknown weapon crashes), plus `AMMO` 50, `BASE_MOVES`, `DAMAGE_MODIFIER` 1, `DETECTION_RANGE` 200, `FIRE_DWELL`, `FIRE_RATE` 1, `FIRE_LIMITS`.
  - Shipped: 130 definitions [D].
- **`objectives.zrd`** (0x417f90, 0x418230, flow 0x418d40 [BN]):
  - **Timing:** `READ_TIME` and `REVIEW_DELAY` are integers, default 4 s. A float is misread.
  - **`FINAL_MISSION`:** the fifth completion exactly ends the game.
  - **Sounds:** `REVIEW_SOUND` and `OBJECTIVE_SOUND`.
  - **`OBJECTIVE1`, `OBJECTIVE2`, …:** contiguous; loading stops at the first gap. More than 10 overflows the slot array.
  - **Each objective holds:**
    - the image name, title key, text key and completion key, in that order;
    - `ACTIVE ( node [child …] )`: completes when that node becomes active;
    - `INACTIVE ( … )`: completes when it becomes inactive (`ACTIVE` takes precedence). An unresolved path never completes;
    - `READ_SOUND ( sample [delay] )`;
    - `AUTOPLAY ()`.
  - `HINT_*` keys do not exist in the executable.
  - Shipped: m3 objective 5 lacks its completion key, so it shows "INACTIVE". m7–m13 carry an unused m2-style copy [D].
  - Map markers come from `maps\mN.zmap` (version 5: RGB colour, outline points, objective index).
- **`startanims.zrd`** (0x4192d0 [BN]): `NEW_GAME_START` and `LOAD_GAME_START` list animation names to restart; missing ones are skipped. Single player only.
- **For zStudio:**
  - Typed editors for networks (header and node graph), AI vehicles, turrets, objectives (at most 10, with node-path pickers resolved against the world after vehicles spawn) and start animations, keeping unknown keys. The generic ZRD tree stays the escape hatch.
  - Validation classes: fatal (dangling links, missing weapon names, more than 10 objectives, wrong shapes, overlong names), silently ignored, and behaviour-changing.
  - [AI networks](ai-networks.md) should show missing or unrecognised strategies as head-on.

## Textures and the rendering target

- **Choosing the pack** (`zVidTexturePackEnsureBuiltinTexturePacksLoaded` 0x46df50 [B, BN]):
  - **Direct3D:** N = total texture memory (`GetAvailableVidMem` with DDSCAPS_TEXTURE, 0x4a9a30 [BN]) in MiB. The game tries `rtexture<N>.zbd` down to 1, then `texturemax.zbd` and `texture.zbd`.
  - **Software:** the `TextureMemory` option 1–4 chooses `texture8/6/4/2`; anything else chooses `texturemax`. A missing pack falls back from `texture<size>` (8 when `texturemax` was chosen) down to `texture1`, then `texturemax` and `texture.zbd`.
  - The option does nothing under Direct3D. The choice is made again for every mission.
  - So `texture8` and `texturemax` serve only the software renderer: under Direct3D the game reaches `texturemax` only when no `rtexture` pack exists, and then refuses its paletted textures.
- **Software texture size:** spans are drawn for textures 8–1024 texels wide; the span routines switch on 20 − log2(width) with cases 10–17 and skip anything else without drawing (0x49bbf0 and its siblings [BN]; `CalcPow2ScratchFields` 0x4902b0 [B]). No shipped pack holds a texture above 256 [D].
- **Creating textures** (`CreateTextureRecord` 0x4aa0f0 [BN]).
  - **Default texture instead:** a side above the device's reported maximum (0 counts as 256), a non-power-of-two size where the device requires powers of two, an aspect ratio above 8, or a palette.
  - **No size limit of its own.**
  - **Formats:** opaque textures as the display format (565), alpha planes as ARGB4444, colour keys as 1555.
  - **No mipmaps.**
  - **Memory:** each texture keeps a system-memory copy beside the video-memory surface. All textures load when the mission loads; there is no streaming.
- **Address space.** No executable is large-address-aware, so the game, the wrapper and the driver share 2 GiB. Expect 2–3 times the pack's size in the process [U].
- **UVs.** Shipped models are drawn with their stored float UVs (`RenderNodeHardware` 0x477b30, `SubmitPolygon` 0x4abb20 [BN]). Geometry built during play (craters, quicksand, clipped CanModify pieces) is rounded to 1/256 by `AddPolygonEx` 0x483650 [BN], up to 2 texels on a 1024 texture.
- **Frame time.** Each opaque polygon is its own draw call, and transform, lighting and clipping run on the CPU. Frame time follows polygon count rather than texture size [U].

The measurements that remain are in [In-game tests](#in-game-tests) under T1–T7.

## In-game tests

Run on a disposable copy of the game (for example `D:\RecoilTest`), exporting into that copy only, never into a protected corpus. Tools: Process Monitor, VMMap, `typeperf`, PresentMon, and optionally the dgVoodoo debug build with DebugView.

**Rendering target**

- **T1. Pack choice.**
  - Set dgVoodoo VRAM to 8, 16, 64, 256, 1024 MB and 2 and 4 GB in turn. Process Monitor shows the first `rtexture<N>` probed and the one that opens.
  - Pass: N follows VRAM and no setting ends on `texturemax` or `texture.zbd`.
- **T2. Maximum texture size.**
  - Replace textures visible at the M1 start with labelled grids of 512² to 4096², plus 1024×128 and 1024×64, using a profile with `maximumDimension` 4096 and no budget.
  - Pass per size: the grid renders instead of the default texture. 1024×64 must show the default.
- **T3. Memory tiers.**
  - Build packs from upscaled textures at 32–512 MiB and select each tier through VRAM.
  - Log virtual bytes with `typeperf` and the largest free block with VMMap over load, play, three restarts, save, load and a mission change.
  - Pass: peak at most 1.5 GiB, largest free block at least 256 MiB, restarts within 32 MiB, and no default textures.
- **T4. Runtime growth.** Make 50, 100 and 200 craters, kills and effects on the chosen tier. Pass: growth recorded, peak at most 1.5 GiB over 30 minutes.
- **T5. Frame time.** PresentMon for 60 s at three repeatable views with the original packs, the `modern` packs and the top tier. Pass: modern p99 within 10% of original and at most 16.7 ms.
- **T6. UV precision.** A 1024 grid on two quads with UVs off the 1/256 grid.
  - Pass: it matches the zStudio preview within 0.5 texel.
  - Then crater it: the code predicts jumps of up to 2 texels at the patch edge.
- **T7. Mipmaps.** Repeat T5 with dgVoodoo's mipmapping `appdriven` and `autogen_bilinear`, judging shimmer and frame time.

**Craters**

- **T8.** In M1, 17 separated craters in one cell. Predicted: the 17th is refused.
- **T9.** In M2, fire at `g502` (x ≈ 2900–3070, z ≈ 885–1010). Predicted: no crater.
- **T10.** In M4, a crater at x ≈ 2206, z ≈ 2800, where layers are stacked. Check the rim heights.
- **T11.** Crater a road, save, restart and load. Predicted: the crater may come back with the sand crater material.

**Vehicles and animation**

- **T12.** Two `amphi` copies. Kill the second.
  - With `STOP_ANIMATION radar_spin` in its death sequence: predicted, the first copy's radar stops.
  - Then save and load with one copy dead.
- **T13.** A three-turn spin in one segment, and 600 iterations of a 1 s loop against a stopwatch: about half a frame of drift per iteration.

**Missions**

- **T14. Multiplayer m14, no patch.**
  - Copy `zbd\m7` to `zbd\m14`, add `m14_zbd.gs`, `support\initm14.gw`, `commonm14.gw` and `tex_fxm14.gw`, and a `dialog.zrd` world entry.
  - Host, choose world 8, join with a second client. Predicted: every weapon is available.
- **T15. Single-player save retargeted to m14.** A save from m2 with its mission number changed to 14, and m2's files copied as m14. It should load and round-trip.

**Resources**

- **T16.** Remove `activate_rad` from one M1 network: does its vehicle move before being shot?
- **T17.** Complete M3's objective 5 and open the objectives panel. Predicted heading: "INACTIVE".
- **T18.** Delete `READ_SOUND` from M1 objective 3, complete objective 3 before objective 2, then review. Predicted: a crash.

**Zones** (see the plan)

- **T19.** Walk M1 across zone transitions with the camera low and high: drawn geometry follows the camera's zones, not the vehicle's.
