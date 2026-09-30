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

## Roadmap

| Phase | Scope | Acceptance |
| --- | --- | --- |
| 0. Semantic baseline | Ordered animation-binding reports, dependency-resolution traces, an evidence ledger, preview-context labels | Retail reconstruction tests report binding relationships and known exceptions (for example `smoke1`); traced and untraced builds agree |
| 1. Source transactions | `SourceWorkspace`, lossless ZRD/script syntax, project-wide undo, recoverable multi-file save; vertical slice: moving pickups (with linked difficulty variants) in text `puppies*.zrd` | GUI and MCP produce identical patches; undo restores exact bytes; failure injection at every publication step leaves no partial transaction; a moved pickup works in the game |
| 2. World objects | Move, rotate, duplicate, delete, hierarchy, flags, script-owned environment properties, asset-versus-placement scope; guarded file-based Blender checkout | Database and script objects survive edit → save → reopen → export; unrelated sources and bindings unchanged |
| 3. Functional actors | Create and delete pickups and AIV actors, AI networks and paths, dependency-aware cross-mission import, mission export | The M2 tank works in M1 with its behaviour and resources; collisions and missing dependencies are actionable |
| 4. Mission logic | Source-backed animation editor, triggers, prerequisites, destruction, objectives, starts, cameras, audio cues | A small scenario (encounter, trigger, objective, exit) can be authored and completed |
| 5. Terrain and advanced Blender | Optional add-on, terrain and subassembly editing, whole-database exchange, LOD/soil/reference tools | New playable content in an existing mission slot, with terrain, collision, destruction and reload checked in the game |
| 6. Scale | Incremental builds, multiplayer workflows, and new mission slots only with evidence | Incremental and clean builds agree; multiplayer checked with several clients |

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

| | Change | Why |
| --- | --- | --- |
| A | Python is allowed in an optional Blender add-on (and optional validation); Core, the application, builds and tests stay C#-only and never need Python or Blender. | The current rule allows Python only for optional export validation. |
| B | One `SourceWorkspace` and one project-wide undo history per source project; Save in source mode saves all accepted source changes and says which files. Direct-mode documents are unchanged. | Shared files and multi-file edits cannot be owned by per-mission histories. |
| C | Text `.zrd`, `.gs`, `.gw` and `.zan` edits preserve untouched bytes, comments, order, scalar kinds and spelling; formatting is an explicit command. | Today a text `.zrd` save replaces comments and formatting with the canonical layout. |
| D | Recovery journals, staging, checkout manifests and temporary correspondence IDs may live in application-owned folders outside the project; none are build inputs, and unresolved ones are never expired silently. | Recoverability and controlled interchange without project metadata. |
| E | Source saves are recoverable, not simultaneously visible; a file name may briefly be absent; publication never replaces an unexpected file; incomplete rollback blocks writes until reconciled. | Preserving external work is preferred over an unqualified atomic-save claim. |
| F | Blender edits enter only through sealed candidate generations and the shared plan/apply path; the add-on uses the existing user-enabled MCP connector and never writes project or game files. | One acceptance boundary; the existing MCP policy stays intact. |
| G | Evidence names the operation it establishes: runtime routines do not define the missing build tool's behaviour; animation-binding reports keep logical targets and resolution order, not names or slots, as identity. | See [Verified corrections](#verified-corrections). |

## Verified corrections

The review found two conflicts between earlier research notes and the source-project rules; the shipped data settles both.

- **Colinear corners.** The runtime's `zDi::AddPolygonEx` (retail 0x483650) removes colinear corners, fans non-planar polygons and extrapolates UVs. The shipped models repeat corners, keep non-planar polygons and keep non-affine UVs, so the original build tool did none of that, and the source importer follows the shipped data.
- **Animation wildcards match one decimal digit.** Four shipped patterns have world nodes that match with one arbitrary character but not with a digit, and none of those became animations. For example, m4's `stgwin*` produced `stgwin1` and `stgwin2` but not `stgwing` or `stgwinb`; the same holds for m5 `tower**`, m1 `pu***` and m10 `barrel**`.

## Open investigations

- **Same-name animation binding.** Rebuilt worlds do not reproduce the original node slot order, so an animation can bind a different same-named node (`smoke1`, the M2 radar). The fix is either reproducing the allocation order or an explicit source refactor that disambiguates the names; never a silent rename.
- **Runtime capacity.** The configured node, model and material pools are not the same as runtime headroom: copied actors, lights, effects and destruction also consume them.
- **Terrain.** Terrain contact, altitude and intersection flags, CanModify/ClipTo, soils, destruction and crater reloading must be characterized before terrain editing.
- **New mission slots.** Whether an `m14` can be registered, selected, saved and played is unknown. Until it is established, new missions replace existing slots.
- **Typed schemas.** AI paths and objectives need typed, evidence-backed schemas; the generic ZRD tree editor remains the escape hatch.
- **Game acceptance** runs on a separate disposable installation, through the normal runtime scripts (`mN_zbd.gs`), never the build script that writes `gamez.zbd`. Studio preview, parsing and reconstruction fixed points remain separate acceptance labels from passing in the game.
