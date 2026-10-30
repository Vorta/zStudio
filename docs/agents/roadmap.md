# Roadmap

Open work for the world editor. Every item ships with GUI/MCP parity, protocol and source-protection tests and documented limits; [in-game acceptance](testing.md#in-game-acceptance) runs on a disposable installation.

## Scope decisions (owner decisions)

- Target: modern hardware (RTX 3080 class) running the unmodified `Recoil.exe`, through one measured pack set that keeps the shipped packs for original hardware (owner decision, 2026-10-30: one modern pack set, no build profiles).
- "New tank" means a higher-fidelity upgrade of an existing vehicle: new model and textures, some texture animations as 3D parts, same key and movement type. Gameplay may change with the new model (collision, fire points, size); new movement modes would be engine work.
- New maps are supported only once every terrain capability works: caves, stacked floors, underwater areas, zone transitions, craters.

## Phases

| Phase | Scope | Acceptance |
| --- | --- | --- |
| Complete terrain behaviour | Caves, stacked surfaces, underwater layers, multi-zone transitions, CanModify and ClipTo, quicksand, crater reload; tilted region planes for walls; zone probe validation; texture page cutting for large painted textures | M1, then the M5/M6 underwater and zone cases, pass source editing, Blender update, export and in-game tests, one change at a time |
| Vehicle upgrades and actors | Upgrade vehicle (below), role-safe splitting, LODs, textures, cosmetic animations; cross-mission actor import; mission export | An upgraded tank passes two-copy, death, respawn and save/reload tests with its listed gameplay changes checked; the M2 tank works in M1 |
| Mission logic | Source-backed animation authoring beyond existing fields (Animation mode edits operands, waits, activation, prerequisites and start thresholds): event insertion, deletion and reordering, `.zan` keyframes and `anim.zad` lists (a wildcard edit affects every expansion, isolating one is an explicit refactor), triggers, prerequisites, destruction, objectives, starts, cameras, audio cues; Blender action conversion (requirements: [engine evidence](engine-evidence.md#animation-runtime), Blender action import); resource validation classes (fatal, silently ignored, behaviour-changing; [resource schemas](engine-evidence.md#resource-schemas)) | One encounter with an upgraded enemy, an animated or destructible object, an objective and an exit works in the game |
| Integrated rebuild | An existing mission rebuilt from unsplit terrain with gameplay edits on the measured pack set | The full capability matrix passes; only then new maps |
| Scale | Incremental builds, multiplayer workflows, new mission slots only with evidence | Incremental and clean builds agree; budgets survive repeated runtime activity; multiplayer checked with several clients |

## Where edits still need to land

Implemented edits are in [sources](sources.md#edit-semantics).

| Edit | Source |
| --- | --- |
| Animated light, fog and camera changes | Their animation definitions |
| Texture cycles | The responsible `tex_fx*.gw` or effect definition |
| Soils and other material or polygon attributes (polygon zones are implemented) | The material or primitive; a shared material offers an explicit fork |

## Open gaps

- AI path Edit mode on source maps is reachable only through MCP (`zstudio_ai_path` `enter`); the GUI panel and G/R/S keys work only after that. Add a GUI entry, then the absolute-coordinate operation for a multi-node selection (one undo step, one evaluation).
- Runtime capacity validation (requirement): check the three free chains, report load-time demand per pool as an error and the play reserve against headroom as a warning, textures appended at run time against the 4,096-entry texture directory (the stored directory is already capped), and light/sound/animate/sequence/switch nodes inside vehicle and pickup templates ([runtime capacity](engine-evidence.md#runtime-capacity)).

## Upgrade vehicle

- Keeps the vehicle's key, data records, placements, movement type and part hierarchy; replaces the visual content. Authoring requirements: [vehicles](engine-evidence.md#vehicles). Starts from the `ltank` structure: `healthy` → `turret` (game-driven yaw) → `gun` (game-driven pitch) → `fpnt_c`/`fpnt_l`/`fpnt_r`; `l4` (LOD 0–256: chassis, `ltracks`, `rtracks`, shadow); `l5` (LOD 256–1024: `chassis_1`); `target`; `support00`–`support03`; `collide00`–`collide11`.
- The checkout holds the whole hierarchy, helpers, LODs, texture-cycle bindings, behaviour dependencies and every mission load including aliases (`ltank_2`). Role names and helper numbering stay. Collision/support points, fire points, aim target and track UV scale may be fitted to the new mesh (suggested, then reviewed); every gameplay-affecting change is listed for testing.
- Splitting: cosmetic rigid geometry under a kept pivot → child meshes; a role whose model the game reads directly keeps one compliant model; `ltracks`/`rtracks` one model each within limits (the game scrolls them); morph parts keep both shapes in correspondence; helpers never split.
- One functional skeleton with visual LOD bands; the original model becomes the far band. Full-quality PNG sources packed into every tier, keeping cycle frames, skins and damage masks; shared materials flagged.
- Propellers (a texture cycle on `props`) become a looping `OBJECT_MOTION` `XYZ_ROTATION` with `LOOP_COUNT -1`, started through the vehicle's `start_anims` in `vehicle.zrd` and parented under the healthy geometry (death hides it; never stop a cloned loop by name; respawn does not rerun `start_anims`). Still to verify: loops through mode changes, saved-game restore and repeated spawns. Tracks stay texture-scrolled; the VTOL heat shimmer stays a texture effect; only the affected cycle binding is replaced.
- Death behaviour unchanged by default; shared assets such as `hulk_small` are a separate scoped upgrade. Blender actions are inputs (accepted `.zan` tracks and definitions become the source); game-driven channels refuse keyframes; morphs only as two same-topology shapes; skinned meshes rejected.
- Vehicle test view: turret and gun sliders, firing ray, collision and support points, track scroll, forced LOD bands, death and reset playback.

## Other workflows

| Workflow | Scope |
| --- | --- |
| Buildings, doors, bridges, destructibles | Blender hierarchy → static, moving and destruction roles → collision and zones → activation, health and cleanup definitions → placement → destruction and reload test |
| AI paths and encounters | Typed networks, connected nodes, compatible actors, activation and pursuit, traversability tests |
| Mission logic and objectives | Starts, prerequisites, triggers, objectives, failure and completion, exit, save state; reference-safe rename and delete |
| Water, seafloor, special soils | Layers, zone transitions, soils, movement modes, underwater effects and camera |
| Effects and attachment points | Geometry and sprites → cycles → effect definition → animation calls → lifetime and budget |
| Sounds | PCM source → sound catalogue and bank variants → loop or one-shot → attachment → preview and deployment |
| Environment | Horizon, fog, lights, cameras, texture cycles; build-time versus runtime setup |
| Packaging and multiplayer | Existing-slot templates, spawns, mode resources, shared dependencies, per-player zones, multi-client tests |

A playable deployment (ZBD files) and an editable source package (the project) are different deliverables.

## Architecture additions

- Asset preparation stage before model allocation: terrain lowering and rigid-part preparation, sharing clipping and attribute utilities.
- Many-to-many provenance: a generated polygon traces to its source triangle, region and cell cut; editing a generated piece edits its region or source piece, never a hidden override.
- Three validation levels: well-formed asset, engine-representable asset, deployable mission content.
- The pack set is a compiler input recorded in build reports and cache keys; script pool sizes stay authoritative.
- Every paint and preparation operation has semantic MCP commands (explicit regions, categorical assignments, bounded brush geometry, preview, apply); MCP never synthesizes mouse strokes.

## Test fixture backlog

- A four-cell terrain: offset origin, two soils, a bridge over another floor, a two-zone transition, separate altitude and intersection cases, a crater crossing cell and ClipTo boundaries, also built with different subdivisions of the same surface.
- A tracked vehicle: an oversized cosmetic part, an oversized track that must be rejected, asymmetric track UVs, missing and duplicate helpers, a hatch animation, repeated death and reset.
