# World editor plan

**Status: proposal, not implemented.** This is the plan for turning zStudio's source-project mode into a full world editor for RECOIL, with a zStudio ↔ Blender workflow for 3D models. It was worked out with an external design review and checked against the code and shipped data. The policy changes in [Decisions needed](#decisions-needed) require the maintainer's approval before implementation starts.

## Goal

A modder opens a reconstructed source project ([source-project.md](source-project.md)) and, without touching ZBD files until export, can:

- see any mission as its sources build it;
- place, move, rotate, duplicate and delete world objects;
- add models from any mission or new ones, including functional actors (an M2 tank that works in M1);
- edit AI vehicles, pickups, AI networks, objectives, animations and triggers, lights, fog, texture cycles, collision flags and soils;
- edit 3D models in Blender and see the result in zStudio;
- export working game files for one mission or all.

Every capability is also available through MCP, on the same commands, undo and saves.

## Principles

1. **Sources stay authoritative.** A world edit expresses intent against an authored object. zStudio turns it into source changes, evaluates them with the existing build (`WorldAssembler`, the animation compiler, the resource compilers) and publishes the new compiled mission. There is no second, editor-only world implementation and no separately saved scene.
2. **Edit the source that owns the concept**, not whichever source is easiest to reach from the rendered object. Moving a placement, editing a shared model and editing an initialization animation are different commands.
3. **No inverse compiler.** A capability-driven planner handles each kind of edit: direct field replacement when ownership is unique, a local source refactor when its effects are established, and an explicit "ambiguous" or "unsupported" result otherwise.
4. **The full rebuild is the correctness reference.** Previews get faster first through gesture overrides and caching; incremental builds come last and must always match a clean build.
5. **No zStudio metadata among the sources.**
   - `data\` and `gamegen\` hold only sources, and builds read nothing else.
   - zStudio's working data lives in the project's `zstudio\` folder. No build reads it, and a model, script or recipe that refers into it is rejected.
   - Editor identities live only in memory. Names, content hashes and line numbers are not identities.

   ```
   <project>\
     data\                   sources: models, terrain recipes, textures, resources, animations, sounds
     gamegen\                sources: build scripts
       build-profiles\       target profiles (original, modern)
     zstudio\                zStudio's working data, created when first needed
       export\               Blender exchange, one folder per checkout
       recovery\             save journal with the previous file contents
       staging\              files being prepared for a save
       cache\                derived data, safe to delete
       diagnostics\          measurements and reports
   ```

   Only `cache\` is always safe to delete. Recovery data from an interrupted save, and Blender exports not yet applied, may be the only copies. Leave `zstudio\` out when sharing a project.
6. **Lossless source editing.** Untouched bytes, comments, ordering, scalar kinds and token spelling survive every edit; normalization is an explicit command.
7. **Preview contexts are labelled.** Authored assembly, mission-start preview and animation sandbox are distinct; the editor never bakes runtime state (startup animations, cleanup, difficulty fallbacks) into sources.

## Architecture

### Four kinds of state

| State | Purpose |
| --- | --- |
| Saved sources | The project on disk; exports read a frozen snapshot of them, as now. |
| Working sources | Accepted, undoable, unsaved edits, shared by every source-mode view. |
| Compiled mission snapshot | The immutable result of building one working-source revision: world, animations, resolved resources, provenance. |
| Presentation state | Camera, selection, gizmo drafts, starting-scene initialization, animation simulation; never written to sources. |

An edit replaces the compiled snapshot of a mission document, not the document: its identity, undo context, pinned Properties target and camera stay. (0.8.0's source worlds replace the document on each rebuild; this plan supersedes that.)

### Components

| Layer | Component | Responsibility |
| --- | --- | --- |
| Core | `SourceSnapshot`, `SourceElementRef` | Immutable source contents and revision-qualified syntax references. |
| Core | `RetailScriptSyntax`, `ZrdTextSyntax`, `ZanSyntax` | Lossless syntax over the existing tokenizer and readers: original bytes, token spans, separators and empty tokens, comments. Patches address elements and are checked against the expected value before bytes change. |
| Core | `WorldBuildTrace`, `MissionProvenance`, `AnimationBindingReport` | What the build did, recorded by an optional trace sink in the existing assembler and compilers. |
| Core | `ISourceEditPlanner` | Intent + provenance → source patches, preconditions, postconditions, affected missions. No disk writes, no undo. |
| Core | Placement semantics | Decoding, difficulty resolution and counterpart links shared by direct mode (binary ZAR patches) and source mode (text token patches); split out of `PickupPlacementEditSession`. |
| Core | Publication primitives | Guarded staging and namespace operations for recoverable multi-file saves. |
| Application | Source command contracts | Typed requests and results, operation handles and errors, shared by GUI and MCP. |
| Desktop | `SourceWorkspace` | One per source project: buffers, saved baselines, claims, one project-wide history, drafts, candidate builds, save and recovery. Source-file editors (for example the ZRD tree) use the same buffers. |
| Desktop | `SourceMissionDocument` | A mission view on the workspace: selection, Properties targets, camera, the accepted compiled snapshot. |
| Desktop, Rendering | Interaction controllers | Gesture drafts and instance-transform overrides for immediate feedback. |

### Provenance

The trace records, for one frozen source snapshot:

- **Instruction occurrences.** Each executed script instruction, with its include stack, execution ordinal, raw and expanded arguments, and the macros it read.
- **Node origins.** How each node came to exist: the creating instruction, the glTF file and node, the load or external reference, and alias groups.
- **Logical nodes and parent edges.** The authored node, and separately each parent relationship through which it is shown. World-cell membership is kept apart from the authored hierarchy.
- **Property writes.** For flags, down to the affected bits: which writer set the value, in what order, and what it depended on.
- **Name resolutions.** Each lookup by name, with its candidates in engine order and the one chosen.
- **Resource record origins.** The source record behind each resource, with the requested difficulty, the effective resource and any fallback.
- **Allocations.** Node, model and material slots, linked to logical identity.
- **Dependency observations.** Files read, lookups that failed, and directory contents, including new or removed files that change what a name resolves to.

A property shows its writers, the effective one, the edit targets available and the consumers affected. When the trace cannot establish ownership, the edit is unavailable rather than guessed.

### Plan, preview, apply

- **Plan:** returns supported, ambiguous or unsupported; the exact scope; source patches; read and write sets; required claims; warnings.
- **Preview:** applies the patches to a private candidate, runs the reference build and validates it. A renderer-only gesture preview does not count.
- **Apply:** accepts only a plan held by zStudio with a successful preview. It rechecks the workspace epoch, revision, file baselines, claims and drafts, then records one history entry. A stale plan is replanned, never forced. Requests carry IDs, so a retried request returns the existing result instead of applying twice.

### Where edits land

| Edit | Source |
| --- | --- |
| Transform or flags of an object inside `mN.gltf` | That glTF node |
| A placement wrapper with `ref` | The wrapper node. Geometry belongs to the referenced file; editing one instance requires an explicit copy. |
| An object created or changed by a script | The responsible instruction, once its exact execution occurrence is established |
| Shared model geometry, LODs, engine attributes | The owning glTF, after showing every affected load and mission |
| AI vehicle placement | Its ordered record in the selected `aiv*.zrd` |
| Pickup placement, amount, type, respawn | Its ordered record in `puppies*.zrd`, keeping linked difficulty variants and deduplicating fallback resources |
| AI network or path | The network resource and the records referencing it; moving a node and changing links are separate operations |
| Animation behaviour or keyframes | The definition `.zrd`, the `.zan` and the mission's `anim.zrd` list. A wildcard edit affects every expansion; isolating one is an explicit refactor. |
| Lights, fog, cameras | Their effective script commands; animated changes in animation definitions |
| Texture cycles | The responsible `tex_fx*.gw` or effect definition |
| Soils and polygon attributes | The material or primitive; a shared material offers an explicit fork |

New script-created objects are ordinary readable blocks in the mission's scripts. Repeated edits update that block instead of appending overrides. Insertion is relative to the executed write point, and the search-path changes it makes are traced.

### Transactions, save and recovery

- **One project-wide history.** Two missions can share a model, definition or resource, so undo is per source project, not per mission; mission views show the relevant part.
- **Save is project-wide at first.** Save in any source-project document saves all accepted source changes in the project and says which files. It never saves half a transaction.
- **Recoverable multi-file publication**, in this order:
  1. Stage and verify every output in `zstudio\staging\`. It is on the same drive as the sources, so originals can be moved aside instead of copied.
  2. Record the preimages and the intent in the journal in `zstudio\recovery\`.
  3. With the files guarded against other writers, move each original aside and install its replacement only if the name is still free.
  4. Mark the save committed.
- **Rollback and startup recovery** only undo what this save provably wrote. They never overwrite an external edit or recreate a file that went missing for unknown reasons. An incomplete rollback leaves the project in *Recovery required*, with source writes and export blocked until it is reconciled.
- **Limit of the guarantee.** Source transactions are atomic in the workspace. On disk, a file name can be briefly absent during publication, and other programs do not see the files change simultaneously.

## Blender workflow

File-based exchange is the baseline; an optional add-on makes it safer and quicker. Both paths enter zStudio through the same validation and plan/apply boundary. zStudio never picks up an export by itself: Blender's changes come in only when you choose *Update from export*.

### Checkout

*Edit model*, *Edit referenced asset* or *Edit terrain selection* creates a checkout in the project's `zstudio\export\` folder:

```
zstudio\export\<checkout-id>\
  manifest.json            the checkout contract (fixed once created)
  baseline\project\…       the frozen source files
  input\                   editable glTF, buffers, textures, referenced assets
  context\                 surrounding mission geometry, read-only
  work\model.blend         optional; zStudio never reads it
  outbox\<generation>\     one folder per export: candidate glTF, buffers, textures, complete.json
  sealed\<generation>\     zStudio's immutable copy of an accepted generation
```

The manifest records:
- the scope, i.e. which source owners may change;
- the source and exchange inventories with hashes;
- a correspondence table of temporary tokens mapped to source elements;
- the semantic constraints (engine names, references, alias groups, LOD membership, attachment roots);
- limits, and the tool versions.

Tokens are temporary and removed before anything reaches the project.

### Flows

- **With the add-on:**
  1. The add-on launches `zStudio.exe` in its existing connector mode and talks MCP over stdio, reaching the visible workspace through the same-user pipe. This requires MCP access that the user enabled.
  2. It calls typed tools such as `blender_checkout_begin`, `blender_candidate_begin` and `blender_candidate_submit`.
  3. It exports into a fresh generation folder and writes `complete.json` last.
  4. zStudio seals and validates that generation and shows the proposed source changes. They are applied only when you choose *Update from export* in zStudio.
- **With plain Blender:**
  1. Import the checkout's glTF in Blender.
  2. Export into a new generation folder.
  3. Choose *Update from export* in zStudio. It captures the export's files, seals and validates them, shows the proposed changes and applies them when you confirm.
- **Both flows:** submission is not acceptance, and acceptance is not Save. A no-op round trip changes nothing, even if Blender reordered its JSON.

### Identity in Blender

| Blender operation | Meaning |
| --- | --- |
| Rename for organization | Changes the Blender label only |
| Rename the engine object | An explicit operation that shows its effect on references, wildcards and truncation |
| Duplicate | A new identity; whether geometry and GameZ nodes are shared is decided explicitly |
| Split, merge, delete | Explicit structural changes with a declared mapping; bound or animated identities are never merged automatically |
| `.001` suffixes, copied tokens | Never identity; duplicated tokens are detected |

External references appear as a placement empty with the referenced asset shown beneath it as context; that context is never exported. *Make local* is an explicit operation. Shared nodes (`extras.recoil.instance`) must agree across copies, or the candidate is rejected. Hidden LOD bands are kept; hiding them is not deleting them.

### Validation before acceptance

1. **Interchange integrity:**
   - sizes and counts within bounds;
   - safe paths;
   - supported glTF features only;
   - finite geometry, valid indices, complete buffers and PNGs.
2. **Authored semantics:**
   - engine names, attachment roots and collision helpers;
   - references closed, LODs complete, shared-node copies consistent;
   - material and polygon attributes, morph correspondence;
   - polygon grouping regenerated after topology changes.
3. **Engine representation:** the importer's rules and limits (polygon splitting, UV quantization, the 1,024-normal buffer), with the compiled resource counts reported.
4. **Mission integration:**
   - rebuild the affected missions;
   - compare the ordered animation bindings;
   - check texture, effect and resource resolution, bounds, grid membership and pool use.

Coordinate conversion (game units, +Y up, −Z forward, winding) happens at one tested boundary.

Whole mission databases can be checked out later as an advanced operation, with other objects shown read-only. Exporting the entire assembled mission to Blender and importing it back is not the main workflow.

### What the add-on never does

- enable MCP access, open another socket or elevate;
- write project or game files, or save or export silently;
- treat Blender labels as identities;
- run commands a `.blend` or manifest supplies;
- compile worlds itself;
- repair unsupported data silently;
- switch workspaces or discard other documents' edits.

## Functional imports

*Add static model*, *Load actor template* and *Place AI vehicle* are separate operations. Importing a functional tank builds a dependency plan and records the reason for each link:
- the AIV placement;
- the vehicle and template;
- the model with its external references, materials and textures;
- the animation definitions and keyframe tracks;
- child animations, effects and sounds;
- AI behaviour and network links;
- runtime texture cycles.

Mission-specific links (objectives, coordinates, triggers) do not travel with the vehicle. Name collisions (a texture the destination mission already has under the same name) need an explicit choice: reuse, rename with reference rewrite, or cancel. *Export mission* becomes a deployment operation that checks compatibility with the destination's unchanged files.

## Content workflows

Maps and vehicles get two complete workflows, not one universal importer:
- **Maps:** unsplit terrain surfaces from Blender, plus gameplay regions painted in zStudio, compiled into pieces.
- **Vehicles:** higher-fidelity upgrades of existing vehicles that keep each vehicle's parts, data and behaviour.

Reconstructed content is preserved by default. Every conversion that would change how it behaves is shown and reviewed first. New maps come only after every terrain capability below works on existing missions.

### How the engine sees a map

Measured on the reconstructed 1999 data and read from the retail executable:

- **Grid.**
  - Every mission is divided into 256 × 256 cells; M1 has 16 × 17.
  - A top-level node joins the cell containing its centre in plan view if it overhangs that cell by at most 3 units; otherwise it goes to the always-considered overflow list.
  - The horizon is a landmark: it is never placed in a cell and never clipped by distance.
- **Pieces.** The mission database holds terrain and static geometry as pieces with world-space vertices and identity transforms.
  - M1 has 337 such pieces, 146 placed groups and the horizon: 4,482 triangles in all (M6: 14,148).
  - The ocean is 93 two-triangle quads, one per cell. Land is cut by hand into smaller pieces.
- **Textures** repeat.
  - Each land piece uses one or two tiling textures (sand, cliff, rock, road), and the ocean texture covers all 93 ocean quads.
  - What sizes and pack budgets are usable depends on the [target profile](#target-profiles).
- **Zones** are the engine's area system.
  - Each polygon carries up to three zone numbers (or *any*), and each node carries a zone number with an on/off gate.
  - Every frame the engine takes the zones of the polygon below the camera as current; each player also has their own zones for collision and line of sight.
  - Nodes and polygons outside the current zones are not drawn, collided with or targeted, and do not run animation callbacks.
  - Transition polygons carry two or three zones. M1 uses zones 0–4, 9, 14 (underwater) and 32 (outer cliffs and sand); M6 uses about 20.
- **Node flags:**
  - altitude surface (vehicles stand on it);
  - intersection (collision), or collision by bounding box;
  - proximity;
  - landmark;
  - CanModify and ClipTo.
- **Craters and quicksand.**
  - At run time, the engine cuts a patch outline into the CanModify surfaces around the impact.
  - Before cutting, it cancels the whole patch if any polygon of a visible ClipTo node overlaps the outline in plan view (retail 0x46B1F0, 0x46B550, 0x46BB90).
  - So **CanModify means "craters can form here" and ClipTo, which the original developers called "no clip", means "no crater may overlap this"**. Neither one affects collision.
  - Because the test is in plan view, a ClipTo surface above can cancel a crater on a floor below it. zStudio reports this; painting cannot change it.
- **Soil** is set per material: water, seafloor, quicksand, lava, fire, or custom.

### Maps

Terrain is authored in Blender as unsplit surfaces, and zStudio does the splitting and the gameplay painting.
- *Unsplit* means not cut for the engine's grid or limits.
- Ground, walls, ceilings, water and seafloor are still separate surfaces, which painting stacked areas and caves requires.
- Shipped maps stay as their reconstructed pieces until converted with [Convert to editable terrain](#convert-to-editable-terrain).

#### The terrain recipe

A terrain is a glTF file of surfaces plus a recipe beside it, for example `data\m1\models\terrain\coast.gltf` and `coast.terrain.json`. The recipe holds everything that is not geometry or texture:
- which surfaces are terrain;
- the gameplay attributes painted on them;
- the version of the splitting rules.

It is a source like the scripts: builds read it, and removing it changes the game. It holds no selection state, editor IDs or caches.

```json
{
  "format": "recoil-terrain",
  "version": 1,
  "compiler": 1,
  "surfaces": [
    { "id": "ground",       "model": "coast.gltf", "node": "ground", "defaults": { "craters": "allowed" } },
    { "id": "tunnel_floor", "model": "coast.gltf", "node": "tunnel_floor" },
    { "id": "tunnel_walls", "model": "coast.gltf", "node": "tunnel_walls", "defaults": { "standable": false } },
    { "id": "sea_floor",    "model": "coast.gltf", "node": "sea_floor", "defaults": { "soil": "seafloor" } }
  ],
  "defaults": { "zones": [0], "nodeZone": "auto", "soil": "default", "collision": true, "standable": true, "craters": "ignored" },
  "regions": [
    { "name": "tunnel", "surfaces": ["tunnel_floor", "tunnel_walls"],
      "set": { "zones": [4], "craters": "blocked" } },
    { "name": "tunnel mouth", "surfaces": ["ground", "tunnel_floor"],
      "shape": { "plane": "xz", "polygons": [ { "outer": [[1200, 2050], [1260, 2050], [1260, 2110], [1200, 2110]], "holes": [] } ] },
      "set": { "zones": [0, 4] } },
    { "name": "road", "surfaces": ["ground"], "shape": { "plane": "xz", "polygons": [ … ] },
      "set": { "craters": "blocked" } },
    { "name": "quicksand pit", "surfaces": ["ground"], "shape": { "plane": "xz", "polygons": [ … ] },
      "set": { "soil": "quicksand" } }
  ]
}
```

- **`surfaces`** names the glTF nodes that are terrain. Each must match exactly one node, and surfaces may not overlap. Each surface can have its own defaults (walls not standable, the seafloor's soil). Everything else in the glTF stays an ordinary object.
- **Order of application:**
  1. the recipe's `defaults`;
  2. the surface's own `defaults`;
  3. the `regions`, in order.

  A region changes only the attributes it sets. Materials and textures come from the glTF unless the recipe sets them.
- **Attributes:**
  - `zones`: one to three zone numbers, or `"any"`.
  - `nodeZone`: a number, `"any"` or `"auto"`.
  - `nodeGate`: the gate, set separately.
  - `soil`: a material soil. Painting `quicksand` sets the soil; it does not create a runtime quicksand patch.
  - `collision`: the intersection flag.
  - `standable`: the altitude flag.
  - `craters`: `allowed` (CanModify), `blocked` (ClipTo, "no clip") or `ignored` (neither).
  - A raw flags form keeps combinations these presets cannot express, so a conversion never drops an existing bit.
- **Shapes** are polygons with holes on a projection plane: plan view (`xz`) by default, or a tilted plane with a depth for walls and slopes, with an optional height range. A region without a shape covers its whole surfaces. Where shapes still cannot separate stacked sheets, the surfaces must be split into separate surfaces in Blender.
- **`nodeZone: "auto"`:**
  - A piece whose polygons all share one zone gets that zone.
  - A transition piece gets *any* with the gate on, validated before use.
  - Zone-specific parts that share a cell stay separate pieces.
- **`compiler`** is the version of the splitting rules: tolerances, cutting, ordering and naming. The same inputs always produce the same pieces. A new version is an explicit, reviewed source change.
- **Inclusion.**
  - One of the mission database's roots names the recipe (`extras.recoil.terrain`), at the place in the root order where the terrain belongs.
  - On import the marker expands into ordinary top-level pieces. It is not a game node, and no new script command is needed.
  - The grid (origin, cell size, tolerance) comes from the mission scripts' state when the world is written.

**Brushes.**
- A stroke edits a named region's shape (adding or subtracting area) on the selected surfaces.
- The preview shows the affected areas and the resulting pieces and budget. Confirming makes one undoable change.
- *Erase* reveals what lies beneath: earlier regions or the defaults. *Reset to default* removes the override instead of writing a fixed value.
- Erasing one attribute leaves the others.

**Update from export.**
- The recipe stays. Its regions are re-evaluated against the new surfaces, and changed coverage is shown.
- Paint is in world space, so it stays in place when geometry moves.
- New surfaces must be classified, and missing or split surfaces remapped. Paint is never dropped silently.
- Generated pieces are not stable identities across remeshing; only the build is deterministic.

#### Convert to editable terrain

For shipped maps:
1. **Merge.** The pieces are merged into logical surfaces, keeping polygons, per-corner UVs, normals and attributes. Welding and smoothing are optional.
2. **Check first.** Bindings, shared models, transforms, LODs, references and roles are checked. The horizon, doors, bridges, destructibles and placed objects stay separate.
3. **Derive regions** from the existing attribute boundaries, using tilted regions or separate surfaces where plan-view regions cannot reproduce them. Ambiguous cases are declined, not approximated.
4. **Acceptance** compares:
   - zone probes;
   - collision;
   - crater blocking per cell and zone;
   - animation bindings;
   - seams;
   - budgets.

- **What is lost:** the original piece layout, some names and order, and possibly how many crater models a crater creates. The geometry is not lost.
- **Reconstruction** from the game files still yields pieces, not recipes, so share the project to keep the recipes.

**Editing a shipped map without converting it:**
- Its pieces are edited in place. A geometry edit checks out the selected pieces, with their neighbours as read-only context.
- Setting a node flag on part of a piece requires an explicit split, previewed first.
- Objects on terrain that changed are reported as floating or buried. They move only through an explicit *Conform to terrain*.

**New map** (once every terrain capability works):
1. **Start from a mission scaffold.** It occupies an existing slot and holds a world, camera, runtime scripts, resources and a player start. Choose the origin, extents and target profile.
2. **Build the surfaces in Blender.**
   - Model the ground, tunnel floors and walls, ceilings, water surface and seafloor as separate surfaces.
   - Keep buildings, bridges, doors and props separate.
   - Paint textures and UVs there.
3. **Import into zStudio** with *Update from export*.
   - Mark which surfaces are terrain.
   - See the dimensions, compiled estimates, materials, missing dependencies and the texture budget.
4. **Paint gameplay in zStudio:**
   - zones;
   - soils;
   - collision;
   - standable;
   - craters;
   - draw priority.

   The compiled pieces can be inspected after each change.
5. **Add gameplay, validate and deploy.**
   - Add starts, pickups, actors, routes and logic.
   - Validate texture packs, animation bindings, grid assignment, queries and capacity.
   - Deploy the mission.

**The splitter:**
- It only processes recipe terrain. It never touches placed references, actors, landmarks or objects that scripts or animations refer to.
- It cuts at the mission's exact cell boundaries. The partition the assembler computes is then checked, not assumed.
- Node attributes (zone, gate, flags) separate pieces into different nodes. Polygon attributes (zones, soils) cut polygons but don't force new nodes. Pieces that differ only in polygon zones share material slots, as the importer already does.
- It counts what the engine stores after merging (921 vertices, 1,024 normals, 57 corners per polygon), not Blender vertices, and splits before a limit is reached.
- Neighbouring pieces share boundary cuts, so there are no cracks or T-junctions.
- It reports how many crater-capable models a typical crater would touch, since every one costs runtime models.
- Output is deterministic and versioned. Generated names are build labels, not identities, and there are no terrain LODs at first.

**Zones:**
- **New maps** start with one explicit zone. *Any* is used only deliberately.
- **Transitions.** A transition polygon's node must admit both sides.
- **Suggestions** come from connected surfaces and enclosed spaces, never from texture names.
- **Validation:**
  - It samples the engine's camera probe along starts, routes, camera offsets and scripted cameras, including bridges, ceilings, water and seafloor.
  - It checks transitions from both sides, places that would need more than three zones, and static objects' full extents.
  - It reports how much was sampled.
- **Views:**
  - polygon zones;
  - node zones and gates;
  - the probe's current hit;
  - excluded geometry, ghosted.
- **Where painting happens.** Gameplay painting happens in zStudio, while Blender owns geometry, UVs and textures. The add-on can offer the same assignments through the same commands.

**Textures.**
- Under the modern profile, tiled, uniquely baked and mixed texturing all work within the pack budget.
- A large painted Blender texture is cut into texture pages that fit the profile's limits. It may also be downsampled, or converted to tiles plus patches, at the artist's choice.
- **Budget view:** source pixels, encoded bytes, estimated runtime memory and, once measured, the real peak.
- **UV precision.** GameZ stores UVs as floats. The 1/256 rounding of the shipped models is what the original build tool did, and reconstructed content keeps it. Whether the modern profile can keep full precision, which large textures need, must be confirmed in the game.

### Target profiles

The target is the unmodified `Recoil.exe` on a measured modern configuration: RTX 3080 class, on Windows 11 with its DirectDraw layer or a wrapper. The executable and the capabilities the wrapper reports decide what works, not the GPU alone.

- **Profiles are build sources**, for example `gamegen\build-profiles\original.json` and `modern.json`.
  - The project has a default, and an export can choose another.
  - Build reports record the profile used.
  - zStudio never derives settings from the GPU it runs on.

| Area | Modern profile |
| --- | --- |
| Texture size | Above 256 px when the game reports and uses it; aspect at most 8 |
| Texture packs | Larger complete tiers: the game opens `rtexture<N>` for N = its reported texture memory in MiB, counting down |
| Geometry | More models; each model keeps the engine limits (921 vertices, 1,024 normals, 57 corners) |
| Pools | Script-set node, model and material capacities can be raised only to tested values, and never automatically |
| Memory | `Recoil.exe` is not large-address-aware: 2 GB of address space for everything |
| Rendering | No new shader, skinning or material features |

- **Packs.**
  - Every tier must hold the whole mission. The game's choice is a size threshold, not a fit test.
  - An older, higher-numbered pack left in the game folder would win, so deployment checks for it.
  - The texture directory holds at most 4,096 textures per world.
- **Original-hardware export** remains a separate, complete deployment. It may need geometry variants without high-detail parts.
- **Measure first,** on the maintainer's machine. Until then the profile is *experimental modern*. The measurements:
  - the reported texture memory and maximum texture size;
  - test packs and textures of increasing size, loaded in the game;
  - address-space peaks while loading, repeating missions, and saving and reloading;
  - runtime growth from craters, clones, effects and deaths;
  - frame time in demanding views.

### Vehicles

The main command is *Upgrade vehicle*.
- **What it keeps:** the vehicle's key, data records, placements, movement type and part hierarchy.
- **What it replaces:** the visual content: model, textures, and some texture animations as moving 3D parts.

The upgrade starts from the `ltank` structure:

```
healthy
  turret                    game-driven yaw
    gun                     game-driven pitch
      firepoint
  l4   LOD band 0–256       chassis, tracks (ltracks, rtracks), shadow
  l5   LOD band 256–1024    chassis_1
  target
  support00 … support03     4 ground support points
  collide00 … collide11     12 collision probe points
```

- **The checkout** holds:
  - the whole hierarchy, helpers and LODs;
  - texture-cycle bindings and behaviour dependencies;
  - every mission load of the vehicle, including aliases such as `ltank_2`.
- **Kept as they are:** role names and helper numbering.
- **Gameplay may change with the new model.** Collision and support points, the firepoint, the aim target and the track UV scale can be fitted to the new mesh: suggested from it, then reviewed. The upgrade lists every change that affects gameplay so that each one gets tested.
- **Splitting by role:**

  | Part | Treatment |
  | --- | --- |
  | Cosmetic rigid geometry under a kept pivot | Split into child meshes |
  | A role whose model the game reads directly | Keeps one compliant model on the role node |
  | `ltracks`, `rtracks` | One model each, within the limits, because the game scrolls them |
  | Morph-bearing part | Keeps both shapes in correspondence |
  | Helpers (`collide`, `support`, `firepoint`, `target`) | Never split |

- **LODs.** One functional skeleton with visual bands. The original model becomes the far band and the original-profile asset, without duplicating role names.
- **Textures.** Full-quality PNG sources are packed per profile, keeping cycle frames, skins and damage masks. Shared materials are flagged.
- **Animated textures become 3D parts:**
  - **Propellers** (a texture cycle on `props`) become a looping spin, written the way helicopter rotors are: `OBJECT_MOTION` with an `XYZ_ROTATION` rate and `LOOP_COUNT -1`.
  - **Starting the spin.** A vehicle entry in `vehicle.zrd` can name `start_anims ( name )`. The game starts that animation at the vehicle's root when it sets the vehicle up, for the player and for AI vehicles alike. The shipped amphibious enemy spins its radar this way (`radar_spin`).
  - **Stopping it.** The death sequence stops the spin, as helicopter destruction stops its rotors.
  - **Still to verify:** mode changes, saved-game restore and repeated spawns.
  - **Tracks stay texture-scrolled.** The game scrolls them with speed, and nothing in the engine spins wheels with speed.
  - **The VTOL's heat shimmer** stays a texture effect.
  - Only the affected texture-cycle binding is replaced.
- **Death behaviour** is unchanged by default. Upgrading shared assets such as `hulk_small` is a separate, scoped upgrade.
- **Animation from Blender:**
  - Actions are inputs. Once accepted, the `.zan` tracks and definitions are the source.
  - Game-driven channels (AI movement, turret yaw, gun pitch) refuse keyframes.
  - Morphs are accepted only as two shapes with the same topology; skinned meshes are rejected.
- **Preview.** A vehicle test view offers turret and gun sliders, the firing ray, collision and support points, track scroll, forced LOD bands and death and reset playback.
- **In-game acceptance:**
  - startup;
  - two live copies, where one dying leaves the other unaffected;
  - later spawns;
  - movement, reversing and turning;
  - slopes and soils;
  - aiming and firing;
  - LOD changes;
  - death and repeated spawns;
  - save and reload;
  - the player vehicle's mode changes.

### Other workflows

| Workflow | Scope | Priority |
| --- | --- | --- |
| Buildings, doors, bridges, destructibles | Blender hierarchy → static, moving and destruction roles → collision and zones → activation, health and cleanup definitions → placement → destruction and reload test | Early; shares terrain and vehicle infrastructure |
| AI paths and encounters | Typed networks, connected nodes, compatible actors, activation and pursuit, traversability tests | Phase 3 |
| Mission logic and objectives | Starts, prerequisites, triggers, objectives, failure and completion, exit, save state; reference-safe rename and delete | Needed for a playable new mission |
| Water, seafloor, special soils | Layers, zone transitions, soils, movement modes, underwater effects and camera | Before general terrain authoring |
| Effects and attachment points | Geometry and sprites → cycles → effect definition → animation calls → lifetime and budget | After template reuse works |
| Sounds | PCM source → sound catalogue and bank variants → loop or one-shot → attachment → preview and deployment | Reuse early; new sounds with mission logic |
| Environment | Horizon, fog, lights, cameras, texture cycles; build-time versus runtime setup | Basic in Phase 2, animated in Phase 4 |
| Packaging and multiplayer | Existing-slot templates, spawns, mode resources, shared dependencies, per-player zones, multi-client tests | Packaging early, multiplayer later |

A playable deployment (ZBD files) and an editable source package (the project) are different deliverables, especially once terrain recipes exist.

### Architecture additions

- **Asset preparation.** A Core stage runs before models are allocated, with two strategies: terrain lowering and rigid-part preparation. They share clipping and attribute utilities, but each has its own rules.
- **Many-to-many provenance.** A generated polygon traces back to a source triangle, a region and a cell cut. Editing a generated piece's flag edits its region or source piece, never a hidden override on the output.
- **Three validation levels:** a well-formed asset, an asset the engine can represent, and content a mission can deploy. A work-in-progress model can exist in the project before it is a complete vehicle.
- **Artist master files** (`.blend`) live in the artist's own storage; checkouts are only for exchange.
- **Target profiles are compiler inputs.** The profile used is recorded in build reports and cache keys. Script pool sizes stay authoritative; a profile only validates them.
- **MCP.** Every paint and preparation operation has semantic commands: explicit regions, categorical assignments, bounded brush geometry, preview and apply. MCP never synthesizes mouse strokes.

## Roadmap

| Phase | Scope | Acceptance |
| --- | --- | --- |
| 0. Engine and target contracts | Ordered animation-binding reports, dependency-resolution traces, an evidence ledger, preview-context labels; crater and zone-probe characterization, engine limits, measurements of the modern target, vehicle lifecycle hooks (`start_anims`, death, restore) | Executable-qualified evidence and small deterministic characterization tests; traced and untraced builds agree; unknown behaviour is named, not guessed |
| 1. Source foundation | `SourceWorkspace`, lossless ZRD/script syntax, project-wide undo, recoverable multi-file save in `zstudio\`, *Update from export*; vertical slice: moving pickups (with linked difficulty variants) in text `puppies*.zrd` | GUI and MCP produce identical patches; undo restores exact bytes; failure injection at every publication step leaves no partial transaction; a moved pickup works in the game |
| 2. World objects and profiles | Move, rotate, duplicate, delete, hierarchy, flags, script-owned environment properties; Blender checkout; target profiles in `gamegen\build-profiles\`; texture and geometry budgets | Database and script objects survive edit → save → reopen → export; unrelated sources and bindings unchanged; test packs load as the profile predicts |
| 3. Terrain source and splitter | Recipe format, shapes, surface selection, brushes, inclusion, deterministic splitting; *Convert to editable terrain* | Converted M1 terrain reproduces its geometry and attributes before any artistic change |
| 4. Complete terrain behaviour | Caves, stacked surfaces, underwater layers, multi-zone transitions, CanModify and ClipTo, quicksand, crater reload | M1, then the M5/M6 underwater and zone cases, pass source editing, Blender update, export and in-game tests, one change at a time |
| 5. Vehicle upgrades and actors | *Upgrade vehicle*, role-safe splitting, LODs, textures, cosmetic animations; AIV placement, AI paths, cross-mission import, mission export | An upgraded tank passes two-copy, death, respawn and save/reload tests, with its listed gameplay changes checked; the M2 tank works in M1 |
| 6. Mission logic | Source-backed animation editor, triggers, prerequisites, destruction, objectives, starts, cameras, audio cues; Blender action conversion | One encounter with an upgraded enemy, an animated or destructible object, an objective and an exit works in the game |
| 7. Integrated rebuild | An existing mission rebuilt from unsplit terrain, with gameplay edits, on the measured modern profile | The full capability matrix passes; only then are new maps supported |
| 8. Scale | Incremental builds, multiplayer workflows, and new mission slots only with evidence | Incremental and clean builds agree; budgets survive repeated runtime activity; multiplayer checked with several clients |

Each phase includes GUI/MCP parity, protocol tests, source-protection tests and documented limitations. The first deliverable is deliberately small: one complete source edit that proves ownership, provenance, undo, validation, saving and MCP together.

Phase 1 is complete when all three hold:
- Source-slice tests pass:
  - placement and difficulty linking (ambiguous and many-to-one counterparts, fallback deduplication, repeated moves);
  - lossless text (only the changed coordinate tokens differ, untouched float bits preserved);
  - workspace and command behaviour (one project-wide order, a shared buffer with the ZRD tree editor, stale plans rejected, idempotent MCP apply);
  - multi-file save with failure injection and startup recovery (a crash between steps, external edits during a vacancy, two processes recovering).
- The direct-mode editors are unchanged.
- An in-game check confirms the moved pickup.

## Decisions needed

| | Change | Why | Status |
| --- | --- | --- | --- |
| A | Python is allowed in an optional Blender add-on (and optional validation); Core, the application, builds and tests stay C#-only and never need Python or Blender. | The current rule allows Python only for optional export validation. | Approved |
| B | One `SourceWorkspace` and one project-wide undo history per source project; Save in source mode saves all accepted source changes and says which files. Direct-mode documents are unchanged. | Shared files and multi-file edits cannot be owned by per-mission histories. | Approved |
| C | Text `.zrd`, `.gs`, `.gw` and `.zan` edits preserve untouched bytes, comments, order, scalar kinds and spelling; formatting is an explicit command. | Today a text `.zrd` save replaces comments and formatting with the canonical layout. | Approved |
| D | zStudio's working data (recovery journal, save staging, Blender exchange in `zstudio\export\`) lives in the project's `zstudio\` folder beside `data\` and `gamegen\`. Builds never read it, and unfinished recovery data or Blender work is never deleted silently. | Recoverability and controlled interchange without metadata among the sources. | Approved |
| E | Source saves are recoverable, not simultaneously visible; a file name may briefly be absent; publication never replaces an unexpected file; incomplete rollback blocks writes until reconciled. | Preserving external work is preferred over an unqualified atomic-save claim. | Approved |
| F | Blender changes come in only when the user chooses *Update from export*, through sealed candidate generations and the shared plan/apply path. The add-on uses the existing user-enabled MCP connector and never writes project or game files. | One acceptance boundary; the existing MCP policy stays intact. | Approved |
| G | Evidence names the operation it establishes: runtime routines do not define the missing build tool's behaviour; animation-binding reports keep logical targets and resolution order, not names or slots, as identity. | See [Verified corrections](#verified-corrections). | Adopted |
| H | Terrain is authored as unsplit Blender surfaces; splitting and zoning happen in zStudio. The gameplay paint is kept in a terrain recipe (`*.terrain.json`) beside the surfaces: a versioned build input with no editor state. Reconstruction from game files yields pieces, not recipes. | Painted regions must survive re-exports from Blender. | Approved |
| I | The target is modern hardware (RTX 3080 class) running the unmodified `Recoil.exe`, expressed as measured build profiles; an original-hardware profile remains. | Texture sizes, pack budgets and geometry budgets depend on it. | Approved |
| J | "New tank" means a higher-fidelity upgrade of an existing vehicle: new model and textures, some texture animations as 3D parts, same key and movement type. Gameplay may change with the new model (collision, firepoint, size). | New movement modes would be engine work. | Approved |
| K | New maps are supported only once every terrain capability works: caves, stacked floors, underwater areas, zone transitions, craters. | A reduced first release would ship maps the later format must replace. | Approved |
| L | "No clip" means no craters: the ClipTo flag, as the original developers used the term. | Collision and standing are separate flags. | Resolved |

## Verified corrections

The review found conflicts between earlier research notes and the source-project rules, and the shipped data and retail code settle them.

- **ClipTo blocks craters.** The reconstruction calls its test "fully inside", but the retail code (0x46B1F0 and 0x46B550, with the byte-matched 0x46BB90) cancels a crater or quicksand patch when any polygon of a visible ClipTo node overlaps it in plan view. Because any one overlapping polygon cancels the patch, splitting a ClipTo node into several does not change the result, as long as the pieces stay visible in the same crater cell. Splitting CanModify surfaces does change how many crater models a crater creates.
- **UV rounding belongs to the build tool.** GameZ stores UVs as floats. The 1/256 rounding seen in the shipped models is what the original build did, so reconstructed content keeps it; it is not a stated engine limit.
- **Colinear corners.** The runtime's `zDi::AddPolygonEx` (retail 0x483650) removes colinear corners, fans non-planar polygons and extrapolates UVs. The shipped models repeat corners, keep non-planar polygons and keep non-affine UVs, so the original build tool did none of that, and the source importer follows the shipped data.
- **Animation wildcards match one decimal digit.** Four shipped patterns have world nodes that match with one arbitrary character but not with a digit, and none of those became animations. For example, m4's `stgwin*` produced `stgwin1` and `stgwin2` but not `stgwing` or `stgwinb`; the same holds for m5 `tower**`, m1 `pu***` and m10 `barrel**`.

## Open investigations

- **Same-name animation binding.** Rebuilt worlds do not reproduce the original node slot order, so an animation can bind a different same-named node (`smoke1`, the M2 radar). The fix is either reproducing the allocation order or an explicit source refactor that disambiguates the names; never a silent rename.
- **Runtime capacity.** The configured node, model and material pools are not the same as runtime headroom: copied actors, lights, effects and destruction also consume them.
- **Zone probe.** Its origin, direction and range, which surfaces it accepts, which of stacked hits wins, initialization and no-hit behaviour, and per-player differences. This gates zone validation and general new-map support.
- **Craters.** How the crater feature grid relates to the world cells, its limits and eviction, overlapping and repeated craters, crater reloading, and how splitting CanModify surfaces changes the number of crater models created. This gates automatic rechunking of crater-capable terrain.
- **Model limits.** Static loading and drawing limits versus the runtime polygon routine's limits (about 921 vertices), normals, and growth while clipping craters. This gates the splitter profiles.
- **Vehicle roles.** Which roles need their own model (the tracks do), lookup scope and order, scroll and morph sharing, helper numbering and pivots, and other movement modes' helpers. This gates part splitting and custom rigs.
- **Animation conversion.** Engine interpolation, spin winding, morph control, reset behaviour and conflicts with game-driven channels. This gates Blender action import.
- **Modern target measurements.**
  - On the maintainer's machine: the reported texture memory and maximum texture size, test packs and textures loaded in steps, and address-space peaks.
  - Runtime growth from craters, clones and effects, and frame time.
  - Whether full-precision UVs render correctly on the hardware path.

  This gates the modern profile, texture budgets and quality presets.
- **Vehicle animation lifecycle.** A `start_anims` loop: what happens on player mode changes, saved-game restore and repeated spawns, and that the death sequence stops it on each clone. This gates propellers and other texture animations becoming 3D parts.
- **Crater visibility.** Which nodes the crater test sees (the crater cell and the camera's list), and its interaction with zones and stacked surfaces. This gates complete cave and stacked-floor support.
- **Test fixtures.**
  - A four-cell terrain: offset origin, two soils, a bridge over another floor, a two-zone transition, separate altitude and intersection cases, and a crater crossing cell and ClipTo boundaries, also built with different subdivisions of the same surface.
  - A tracked vehicle: an oversized cosmetic part, an oversized track that must be rejected, asymmetric track UVs, missing and duplicate helpers, a hatch animation, and repeated death and reset.
- **New mission slots.** Whether an `m14` can be registered, selected, saved and played is unknown. Until it is established, new missions replace existing slots.
- **Typed schemas.** AI paths and objectives need typed, evidence-backed schemas; the generic ZRD tree editor remains the escape hatch.
- **Game acceptance** runs on a separate disposable installation, through the normal runtime scripts (`mN_zbd.gs`), never the build script that writes `gamez.zbd`. Studio preview, parsing and reconstruction fixed points remain separate acceptance labels from passing in the game.
