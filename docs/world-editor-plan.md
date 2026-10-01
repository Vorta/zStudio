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
5. **No zStudio metadata in the project.** Editor identities live in memory and in application-owned state outside the project (recovery journals, Blender checkouts). Names, content hashes and line numbers are not identities.
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
  1. Stage and verify every output.
  2. Record the preimages and the intent in a journal outside the project (`%LOCALAPPDATA%\RecoilZbdStudio\SourceRecovery\v1\…`).
  3. With the files guarded against other writers, move each original aside and install its replacement only if the name is still free.
  4. Mark the save committed.
- **Rollback and startup recovery** only undo what this save provably wrote. They never overwrite an external edit or recreate a file that went missing for unknown reasons. An incomplete rollback leaves the project in *Recovery required*, with source writes and export blocked until it is reconciled.
- **Limit of the guarantee.** Source transactions are atomic in the workspace. On disk, a file name can be briefly absent during publication, and other programs do not see the files change simultaneously.

## Blender workflow

File-based exchange is the baseline; an optional add-on makes it safer and quicker. Both paths enter zStudio through the same validation and plan/apply boundary.

### Checkout

*Edit model*, *Edit referenced asset* or *Edit terrain selection* creates a checkout outside the project:

```
BlenderCheckouts\v1\<checkout-id>\
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
  4. zStudio seals and validates that generation and shows the proposed source changes.
- **With plain Blender:**
  1. Import the checkout's glTF in Blender.
  2. Export into a new generation folder.
  3. Choose *Accept changes* in zStudio. It captures the export's files, seals and validates them, and asks for acceptance.
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
- **Maps:** terrain surfaces plus explicit gameplay regions, compiled into pieces.
- **Vehicles:** rigid parts with engine roles plus explicit behaviour, compiled into actors.

Reconstructed content is preserved by default. Every conversion that would change how it behaves is shown and reviewed first.

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
  - Hardware textures are RGB565, a power of two and 8–256 px; 512 px works only where the device reports it. Aspect is at most 8, and names are at most 19 characters.
  - A 256² texture costs 128 KiB. One unique texture per M1 cell would need 34 MiB, more than any texture pack holds.
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
  - Before cutting, it cancels the patch if any polygon of a visible ClipTo node overlaps the outline in plan view (retail 0x46B1F0, 0x46B550, 0x46BB90).
  - So **CanModify means "craters can form here" and ClipTo means "no crater may overlap this"**. Neither one affects collision.
- **Soil** is set per material: water, seafloor, quicksand, lava, fire, or custom.

### Maps

Maps have two source representations, built by one assembler:

| Representation | In the project | Build |
| --- | --- | --- |
| Preserved pieces | Today's `mN.gltf` pieces with their node and material attributes | Kept as authored and repartitioned by their bounds; geometry changes only through explicit edits |
| Generated terrain (new) | Unchunked Blender surfaces (glTF + PNG) plus a terrain recipe, `*.terrain.json`. The recipe holds the surface references, the painted regions with their attributes, defaults and a named compiler profile; it has no selection state, editor IDs or caches. | Cut into ordinary top-level pieces when the mission database is imported, then built by the normal update and write |

- **Existing missions** stay as preserved pieces and are not converted automatically. A later *Promote to generated terrain* checks a selection for script, animation and resource bindings, references and sharing first.
- **The recipe is a new file format defined by zStudio** (decision [H](#decisions-needed)).
- **Fixed point:**
  - Reconstructed projects keep the exact reconstruct → export → reconstruct fixed point.
  - For generated terrain, the guarantee is that export → reconstruct → export produces the same game content. Reconstructing from the game files yields pieces, not the recipe, so authors share the source project to keep their recipes.

**New map:**
1. **Start from a mission scaffold.** It occupies an existing slot and holds a world, camera, runtime scripts, resources and a player start. Choose the origin, extents and target profile (decision [I](#decisions-needed)).
2. **Build the surfaces in Blender.**
   - Model the ground, tunnel floors and walls, water surface and seafloor as separate components where that helps.
   - Keep buildings, bridges, doors and props separate.
   - Paint textures and UVs there.
3. **Import the sealed export into zStudio.**
   - Mark which components are terrain, static objects or references.
   - See the dimensions, the compiled estimates, materials, missing dependencies and the texture budget.
4. **Paint gameplay in zStudio:**
   - zones;
   - soils;
   - collision (intersection, altitude);
   - crater behaviour (CanModify, ClipTo);
   - draw priority.

   The compiled pieces are generated and can be inspected after each accepted change.
5. **Add gameplay, validate and deploy.**
   - Add starts, pickups, actors, routes and logic.
   - Validate texture packs, animation bindings, grid assignment, queries and capacity.
   - Deploy the mission.

**Editing a shipped map:**
- Its pieces are edited in place. A geometry edit checks out the selected pieces, with their neighbours as read-only context.
- Setting a node flag on part of a piece requires an explicit split, previewed first.
- Named doors, bridges, destructibles and the horizon stay separate authored objects.
- Objects on terrain that changed are reported as floating or buried. They move only through an explicit *Conform to terrain*.

**Paint channels.** Each channel is independent and has one owner:

| Channel | Meaning | Stored on |
| --- | --- | --- |
| Zones | Up to three zone numbers, or a deliberate *any* | Polygon |
| Soil | Default, water, seafloor, quicksand, lava, fire, custom | Material |
| Priority, back faces, colour | Rendering | Polygon and material |
| Altitude, intersection | Whether vehicles stand on it and collide with it | Node |
| Bounding-box collision, proximity | Separate query behaviour | Node |
| CanModify, ClipTo | Craters may form; craters may not overlap | Node |
| Node zone and gate | Which zones the whole node belongs to | Node |

- **Storage.** Painted regions are categorical regions: vector areas or volumes, local to a surface component. They are not RGB masks or triangle indices, because a top-down bitmap cannot tell a bridge from the floor beneath it.
- **After a remesh,** the regions are re-evaluated against the new geometry. zStudio reports uncovered surfaces, conflicts and changed boundaries for explicit resolution.
- **On preserved pieces,** attributes transfer from the checkout's starting copy by surface correspondence. Ambiguous cases stay unresolved; zStudio does not guess the nearest triangle.
- **"No clip"** is offered as separate controls: *no collision* (intersection off), *not standable* (altitude off) and *no craters* (ClipTo). Each changes only that one behaviour.

**The chunker:**
- It only processes generated terrain. It never touches placed references, actors, landmarks or objects that scripts or animations refer to.
- It cuts at the mission's exact cell boundaries, using its origin, cell size and tolerance. The partition the assembler computes is then checked, not assumed.
- Node attributes (zone, gate, flags) separate pieces into different nodes. Polygon attributes (zones, soils) cut polygons but don't force new nodes. Pieces that differ only in polygon zones share their material slots; the importer already keeps zones on polygons.
- It counts what GameZ stores (vertices, normals, polygons) against tested limits and splits before a limit is reached.
- Neighbouring pieces share boundary cuts, so there are no cracks or T-junctions. UVs are interpolated before the 1/256 tile shift.
- Output is deterministic and versioned. Generated names (component, cell, piece) are build labels, not identities.
- There are no terrain LODs at first.

**Textures.**
- Tiled materials are the default, with a limited amount of unique detail.
- A large painted Blender texture gets three explicit choices:
  - downsample it to one texture, previewed first;
  - convert it to tiled materials plus a few unique patches (recommended);
  - cut it into budgeted texture pages and remap the UVs, refused if it does not fit.
- **Budget view:** for each pack, the texture count, sizes, cost, reductions, cycle frames and remaining capacity, with a preview at that pack's quality.
- The engine has no splat maps or shader graphs, so Blender shader effects do not carry over.

**Zones:**
- **New maps** start with one explicit zone. *Any* is used only deliberately, and unpainted surfaces never become *any*.
- **Transitions.** A transition polygon's node must admit both sides, so its node zone is *any* or the gate is off. Otherwise the transition disappears from one side.
- **Suggestions** come from connected surfaces and enclosed spaces, never from texture names.
- **Validation:**
  - It samples the engine's camera probe along starts, routes, camera offsets and scripted cameras, including bridges, ceilings, water and seafloor.
  - It checks transitions from both sides, places that would need more than three zones, and static objects' full extents.
  - It reports how much was sampled; sampling is not proof.
- **Views:**
  - polygon zones;
  - node zones and gates;
  - the probe's current hit and zones;
  - excluded geometry, ghosted.
- **Where painting happens.** Gameplay painting is primary in zStudio. Blender owns geometry, UVs and texture painting. The add-on can offer the same assignments through the same commands.

### Vehicles

A tank is a rigid hierarchy of named parts plus data records and behaviour programs; the world grid plays no part in it. The first workflow is *New tracked enemy from template*, starting from `ltank`:

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

The game finds these parts by name: a missing collision point fails the vehicle, and the helper points are hidden once read.

**Roles and checks:**

| Role | Check |
| --- | --- |
| `healthy` | The template's lifecycle and attachment structure are kept |
| `turret`, `gun` | Pivots and rest transforms are explicit; yaw and pitch belong to the game |
| `firepoint` | Under the gun; firing ray shown, −Z forward |
| `target` | An explicit aim point, never the mesh centre by default |
| `collide00`–`collide11` | Each exactly once; missing, duplicate, non-finite or coincident points rejected |
| `support00`–`support03` | Footprint shown against the chassis |
| `shadow` | The quad and its activation |
| `ltracks`, `rtracks` | Separate parts with scrolling UVs |

- **Helper rules.** These counts are the tracked-vehicle contract. Hover, flying and swimming vehicles get their own after study.
  - Helper positions suggested from the bounding box must be reviewed.
  - Scaling a vehicle scales its helpers and animations too.
  - Hidden helpers and LOD bands are exported with it.
- **Geometry over the limits.** A part used only as a transform (such as `turret`) may be split into mesh children under it. A part whose model the game reads directly (the tracks scroll their UVs) is reduced or rejected, never restructured silently. The total cost is reported.
- **LODs** are authored in Blender with RECOIL's LOD groups; generated LODs come later as a reviewed operation.
- **Destruction** is either the template's death behaviour (generic hulk, fire and smoke animations) or a custom wreck sequence authored explicitly.
- **Animation.** Blender actions are inputs; once accepted, the `.zan` tracks and definitions are the source.
  - **Flow:** action → node motion → role mapping → conversion and check with zStudio's evaluator → `.zan` → definition events → the mission's `anim.zrd` → `anim.zbd`.
  - **Game-driven channels** (AI movement, turret yaw, gun pitch) refuse keyframes. Hatches, doors and rotors are keyframed, and full spins survive key reduction.
  - **Events** (sounds, effects, visibility) are authored in the definition editor, not inferred from action names.
  - **Morphs** are accepted only as two shapes with the same topology.
  - **Skinned meshes** are rejected, except parts attached rigidly to one bone.
- **Files.** New vehicle files go where the source layout puts similar content:

  | Content | Where |
  | --- | --- |
  | Geometry | `data/mN/models/bft/name.gltf` |
  | Textures | `data/mN/textures/bft/` |
  | Vehicle entries | `data/common/zrdr/vehicle.zrd`, plus every variant that exists |
  | Reusable behaviour | `data/common/zrdr/enemies/name.zrd` |
  | Mission-only behaviour | The mission's `zrdr/bft/` |
  | Model load | `support/bftN.gw` |
  | Animation list | The mission's `anim.zrd` |
  | Placements | `aipath/aiv*.zrd` |

  - Records are cloned from a template, keeping unknown fields; values are never estimated from the mesh.
  - New vehicle names need their dispatch verified first. New movement modes are engine work, not an editor feature (decision [J](#decisions-needed)).
- **Preview.** A vehicle test view offers turret and gun sliders, the firing ray, collision and support points, track scroll, forced LOD bands and death and reset playback, all preview-only.
- **In-game acceptance** covers:
  - spawn;
  - drive and turn;
  - aim and fire;
  - slopes and soils;
  - LOD changes;
  - death and repeated spawns;
  - save and reload.
- **The player's multi-mode vehicle** is a later, separate contract.

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
- **MCP.** Every paint and preparation operation has semantic commands: explicit regions, categorical assignments, bounded brush geometry, preview and apply. MCP never synthesizes mouse strokes.

## Roadmap

| Phase | Scope | Acceptance |
| --- | --- | --- |
| 0. Semantic baseline | Ordered animation-binding reports, dependency-resolution traces, an evidence ledger, preview-context labels; target profiles, zone and probe tracing, vehicle role-consumer audit, static versus runtime geometry limits | Retail reconstruction tests report binding relationships and known exceptions (for example `smoke1`); traced and untraced builds agree; reports reproduce the measured cases and name unknown behaviour instead of guessing |
| 1. Source transactions | `SourceWorkspace`, lossless ZRD/script syntax, project-wide undo, recoverable multi-file save; vertical slice: moving pickups (with linked difficulty variants) in text `puppies*.zrd` | GUI and MCP produce identical patches; undo restores exact bytes; failure injection at every publication step leaves no partial transaction; a moved pickup works in the game |
| 2. World objects | Move, rotate, duplicate, delete, hierarchy, flags, script-owned environment properties, asset-versus-placement scope; guarded file-based Blender checkout; geometry and texture preflight, editing preserved terrain pieces, explicit chunk creation for static geometry, node and polygon attribute assignment, vehicle role inspection | Database and script objects survive edit → save → reopen → export; unrelated sources and bindings unchanged; a small ground area and a separate static object survive Blender exchange, build, reconstruction and an in-game drive |
| 3. Functional actors | Create and delete pickups and AIV actors, AI networks and paths, dependency-aware cross-mission import, mission export; *New tracked enemy from template* with helper assignment, role-safe geometry preparation, catalogue variants and AIV placement | The M2 tank works in M1 with its behaviour and resources; a custom enemy works in M1; collisions and missing dependencies are actionable |
| 4. Mission logic | Source-backed animation editor, triggers, prerequisites, destruction, objectives, starts, cameras, audio cues; Blender action conversion, death and cleanup editing, animated doors and hatches | One encounter with a custom enemy, an animated or destructible object, an objective and an exit works in the game |
| 5. Terrain and advanced Blender | Generated-terrain recipes, region painting, remesh reconciliation, multi-zone and stacked-surface diagnostics, characterized craters; the optional add-on; whole-database exchange | New playable terrain with transitions, collision, soils, craters and reload checked in the game |
| 6. Scale | Incremental builds, multiplayer workflows, and new mission slots only with evidence | Incremental and clean builds agree; budgets survive repeated runtime activity; multiplayer checked with several clients |

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
| D | Recovery journals, staging, checkout manifests and temporary correspondence IDs may live in application-owned folders outside the project; none are build inputs, and unresolved ones are never expired silently. | Recoverability and controlled interchange without project metadata. | Open |
| E | Source saves are recoverable, not simultaneously visible; a file name may briefly be absent; publication never replaces an unexpected file; incomplete rollback blocks writes until reconciled. | Preserving external work is preferred over an unqualified atomic-save claim. | Open |
| F | Blender edits enter only through sealed candidate generations and the shared plan/apply path; the add-on uses the existing user-enabled MCP connector and never writes project or game files. | One acceptance boundary; the existing MCP policy stays intact. | Open |
| G | Evidence names the operation it establishes: runtime routines do not define the missing build tool's behaviour; animation-binding reports keep logical targets and resolution order, not names or slots, as identity. | See [Verified corrections](#verified-corrections). | Open |
| H | Documented terrain recipes (`*.terrain.json`) are allowed as project sources: versioned build inputs with no editor state. Reconstruction from game files yields pieces, not recipes. | Generated terrain needs its painted regions stored somewhere; the alternative is chunking as an explicit one-time edit and painting the chunks. | Open |
| I | The first target profile is the original executable on period hardware (256 px textures, shipped pack budgets); larger textures and packs are a separate, tested profile. | Budgets and the texture workflow depend on it. | Open |
| J | "New vehicle" means a reskin or replacement, or a new vehicle entry using an existing movement mode; new movement modes are out of scope. | New movement behaviour is engine work. | Open |
| K | The first new-map release supports single-level outdoor terrain; caves, stacked floors and underwater traversal follow, but the region format supports them from the start. | Zones and the camera probe make those cases the hardest to validate. | Open |

## Verified corrections

The review found conflicts between earlier research notes and the source-project rules, and the shipped data and retail code settle them.

- **ClipTo blocks craters.** The reconstruction calls its test "fully inside", but the retail code (0x46B1F0 and 0x46B550, with the byte-matched 0x46BB90) cancels a crater or quicksand patch when any polygon of a visible ClipTo node overlaps it in plan view. Because any one overlapping polygon cancels the patch, splitting a ClipTo node into several does not change the result, as long as the pieces stay visible in the same crater cell. Splitting CanModify surfaces does change how many crater models a crater creates.

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
- **Textures.** Device limits on real hardware, pack selection, and alpha and mipmap costs. This gates texture budgets and quality presets.
- **Test fixtures.**
  - A four-cell terrain: offset origin, two soils, a bridge over another floor, a two-zone transition, separate altitude and intersection cases, and a crater crossing cell and ClipTo boundaries, also built with different subdivisions of the same surface.
  - A tracked vehicle: an oversized cosmetic part, an oversized track that must be rejected, asymmetric track UVs, missing and duplicate helpers, a hatch animation, and repeated death and reset.
- **New mission slots.** Whether an `m14` can be registered, selected, saved and played is unknown. Until it is established, new missions replace existing slots.
- **Typed schemas.** AI paths and objectives need typed, evidence-backed schemas; the generic ZRD tree editor remains the escape hatch.
- **Game acceptance** runs on a separate disposable installation, through the normal runtime scripts (`mN_zbd.gs`), never the build script that writes `gamez.zbd`. Studio preview, parsing and reconstruction fixed points remain separate acceptance labels from passing in the game.
