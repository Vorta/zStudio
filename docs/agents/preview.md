# Preview semantics

Rules for model, Whole world, mission and animation previews. Engine facts and addresses: [engine-evidence](engine-evidence.md). Source data and serialized records never change through a preview; mark game-dependent approximations explicitly.

## Mission starting scene

- Mission previews show a separate single-player starting scene for the chosen difficulty, built in this order: load-time flagged cleanup and automatic activations, per-instance turret cleanup, AIV vehicle instances and poses, pickup placements, the immediate NEW_GAME_START animation frontier, then the [player tank](#player-tank).
- Load initialization runs every flagged cleanup in entry order before the zero-time events of the ON_STARTUP entries. A launch at either step does not restart an animation the load or the NEW_GAME_START list has started. A range-gated animation (EXECUTION_BY_RANGE) runs only if its attach node is in range of the player's spawn: the spawn of the first vehicle of the chosen AIV layout that the vehicle resource defines.
- Approximations against retail ([animation runtime](engine-evidence.md#animation-runtime)): the game runs `ON_STARTUP` events only in the first frame, by priority, the preview in activation order; the startup frontiers apply the range test once to every started entry, children included, before its first events (no gated entry runs without an AIV spawn; cleanups are not gated); `EXECUTION_BY_ZONE` entries run their zero-time events at start; a clone's nodes come from the prepared scene, where load cleanup already ran (a cleanup outlasting zero time, or a clone bound at nodes changed since load, would differ); gaps between same-flag segments, the morph clamp and event 0x0B (evaluated absolutely, not incrementally) differ.
- Never advance the mission context time or play startup audio to recover a pose (the player tank's mode change runs on its own clock: [player tank](#player-tank)). Background actors stay at their starting layout; AI, combat, inventory, terrain settling, saved-game state and mission progression are not simulated.
- Unplaced scripted actors stay hidden in world context, with a diagnostic, until placed; explicit individual inspection may use a clearly labeled stored pose. No generic offset on static terrain or ordinary zero transforms.
- Names never identify instances; cloned vehicles keep their source-node provenance. Shared world animation bindings share state and checkpoint aliases; copied animation roots and effects keep separate identities.
- Edits invalidate the frozen preview context and rebuild the baseline.

## Player tank

- The local player's tank is the first vehicle the chosen AIV layout spawns (its placement record, never the name `bft_00`). Every mission preview draws it as the game shows it settled after a fresh start in a state: master type, primary and secondary weapon ([vehicles](engine-evidence.md#vehicles)). Without a choice it draws the mission's start, which the data decides: tracked, the first listed weapon the mission allows as secondary and the second as primary (RFPG and HE Mortar in every shipped mission; the single-player threshold rule, with the folder number standing in for the mission on m7–m13); compiled worlds and animation previews always draw that. A source map's view takes a choice for the open map (the tank's Properties card, MCP `scene_options` `bftType`/`bftPrimary`/`bftSecondary`): every opening starts at the mission's start; rebuilds, undo, save, reload and difficulty keep it; it is never saved and makes no edit, draft or undo step. What the view draws, when not the start, is in the context's labels (`MissionLayoutSelection.PlayerTank`); a change rebuilds the view as a difficulty change does, after a pending edit is applied or discarded.
- Weapons: every listed weapon's model under `gun` is switched off, then the primary pair and the secondary's model on; a single-mount secondary rests at its stored position at scale 1 with the turret doors collapsed. Approximations: the loaded mine, missile or nuke model is not hung on the launcher, the primary's scroll is not animated, and ownership and ammo are not simulated (every weapon can be chosen).
- Master type: the end of the vehicle resource's mode change from track (sub: amphibious first), run after the NEW_GAME_START frontier on the tank only, its entries in a private player with its own work allowance ticking at 60 Hz until they finish (at most 10 s per list), with no mission clock, audio, effect or other actor; a flag-0x20 entry runs no cleanup first, since the load stopped it. Its flags and poses are published only when every list finished and all its writes were admitted, otherwise the tank stays tracked and the reason goes to Problems; a tank that cannot be prepared at all is reported as such, not as missing. A part the change leaves shown in a morphed shape draws a morphed copy of its model. Not simulated: the code's height changes (sub dive, floating), trails, wakes and bubbles, the props' speed and the hover shadow's clearance test.
- A type is offered when the tank's track block names its list and the map's animations hold every entry it starts; one an edit or a difficulty change removes returns to Tracked, and a missing tank keeps the choice for when one spawns. In sub a forbidden secondary is replaced by the weapon the game switches to (every weapon owned and loaded, another bank on its first side).
- An opening that hides the tank (the m2, m4 and m5 drop-offs) keeps it hidden, whatever the choice.

## Difficulty

- Easy/Medium/Hard is one shared, saved app preference, default Medium; opening a source project restores the difficulty its `.zstudio/settings.json` remembers (a project seen for the first time remembers the current one). It stays enabled when Map is hidden because animation bindings use the mission context.
- Easy selects `aiv_easy.zrd` and `vehicle_easy.zrd`, Medium `aiv.zrd` and `vehicle.zrd`, Hard `aiv_hard.zrd` and `vehicle_hard.zrd`; each absent variant falls back independently to its default file. Empty valid variants stay empty; a malformed selected resource reports an error and keeps the last valid preview. Never merge layouts or infer difficulty from actor name suffixes.
- Requested difficulty and effective resources are part of the context/cache identity and the labels. Authored AIV variant selection is a preview capability, not engine parity ([resource archives](engine-evidence.md#resource-archives)).
- Changing difficulty keeps the playhead and playback intent, free or followed camera, grid and height, LOD, custom range and authored selection; a followed camera is reapplied at the reconstructed playhead. Bindings and inspection remap only through unambiguous actor identity and source provenance, never old clone indices; absent or ambiguous instances clear the binding. Audio resources and the output stay warm. Superseded requests never publish; a request's cancellation token never becomes the lifetime of a successfully loaded animated viewport. No edit or undo entry is created.

## Turrets

- Turrets initialize from `ai.zrd` for each matching scene instance, with its pattern and destruction-animation rules ([`ai.zrd`](engine-evidence.md#aizrd)).
- Apply the selected primary cleanup to the actual instance, with reset components scoped to that turret; keep destroyed geometry for later animation playback. Hide initialization-only fire-point and effect helpers without deleting source records.

## Pickups

- Pickups use the engine's 40-type catalog and `puNNN` model templates. Select `puppies_easy.zrd`/`puppies.zrd`/`puppies_hard.zrd` independently, falling back to the default only when the variant is absent. These resources are ordered placement lists, not name/value pairs.
- Keep XYZ, radian Euler rotation, template scale, amount/default amount and respawn metadata; hide `bvol` collision geometry. A copy without a `bvol` is destroyed as the game destroys it, and the next placement takes its name. Approximation: the preview looks for `bvol` among the copy's direct children, the game's `FindSubNodeByName` searches the whole subtree ([pickups](engine-evidence.md#pickups)); every shipped pickup keeps it as a direct child. Show every authored pickup regardless of player inventory.
- Bind clones by archive/resource/record identity and template provenance; different pickup resources never remap by generated name or clone index. Invalid individual placements get resource/record diagnostics without discarding valid siblings.
- Never simulate collection, respawn or terrain snapping to invent a starting placement.

## LOD

- LOD 0 is the nearest, highest-detail authored band of each parent group, not one global map distance; higher ranks clamp independently per object.
- Animation LOD selection changes only visible poses and keeps inactive variants' simulation state.
- Model previews and exports use the enclosing LOD group where present. Map, model and animation pickers behave alike.

## Materials and texture cycles

- Texture cycles follow the runtime creation order ([textures](engine-evidence.md#textures-and-the-rendering-target)); keep effect-setup material bindings separate from serialized GameZ data. Preload cycle maps; cycle sampling is deterministic when seeking.
- Transparent textures without an alpha plane key decoded RGB565 zero when flag 2 is set ([colour keys](engine-evidence.md#textures-and-the-rendering-target)).
- Alpha blending keeps opaque depth writes but writes no depth for transparent surfaces, including alpha-zero texels. Transparent surfaces draw through the same explicit mesh input layout as opaque ones.
- Opacity follows the SetLitFlag-named inherited alpha override ([animation runtime](engine-evidence.md#animation-runtime)); never render stored opacity unconditionally.

## Horizon

- The horizon follows the active viewport camera: XYZ for the Player horizon fallback, or an explicit XZ-only binding. Render it before scene geometry without depth writes or scene near/far clipping; exclude it from framing and depth bounds.
- Animation world poses obey the Horizon toggle; it follows the camera while paused and while Follow camera is active. Whole world keeps its own Horizon checkbox.

## Cameras

- Finalize preview cameras after the per-frame inertia and navigation updates and before the frame's camera matrices are captured. Camera notifications only invalidate; they never recursively reapply animation poses.
- Frame pipeline, depth bounds, clipping and the navigation floor: [architecture](architecture.md#rendering-pipeline).
- Free orbit, Fly and authored preview cameras stay upright around +Y with ±89° pitch, keeping the orbit target or the Fly/authored position. Named top/bottom views are exactly axis-aligned, with a stable basis when free orbit leaves the pole.
- No built-in keyboard camera control (owner decision 2026-10-22): with the 3D view focused, arrows and Page Up/Down are consumed without moving the camera (focus stays in the view) and W/A/S/D/Z/Q, Backspace and the mouse's Back/Forward buttons do not move the camera (outside Fly); only a left click navigates with the view cube; touch acts as the mouse (a tap is a left click, press-and-hold a right click); the wheel does nothing during an AI path transform, a drop-window move or a box selection; the keys are Home and the numpad shortcuts ([3D navigation](../3d-navigation.md)) and Fly's.
- Shared Blender-style navigation runs before any picking of the press: middle orbit, Shift+middle pan, Ctrl+middle pointer-directed zoom (orthographic pointer anchoring), Ctrl+Shift+middle dolly, with decaying inertia. Resolve held Shift/Ctrl throughout a middle drag regardless of press order; defer pivot picking until orbit movement begins; clear the previous mode's inertia on transitions.
- The orbit pivot is the nearest eligible rendered triangle under the first unmodified middle press (transformed instances and runtime animation meshes included; helpers and horizons excluded); orbit rotates eye offset and basis together about that world-space pivot without recentering. Zoom travel speed comes from the camera-to-hit distance along the pointer ray, refreshed between substeps of at most one wheel step and during inertia, keeping the last reference on a miss. Surface queries use the current camera without mutating the pivot or depending on cached clipping planes.
- Projection changes keep the target plane's apparent scale. Clipping uses perspective side planes or parallel orthographic planes; picking, framing, billboards, horizon and constant-pixel pickup handles support both. Frame uses visible transformed geometry with stable source-node/descendant selection, excluding horizon, grid and handles.
- Fly: [workbench](workbench.md#fly-camera); its motion integrates before camera finalization. Terrain collision is not simulated for any camera.
- Axis views enter orthographic and return to perspective on orbit; an explicitly chosen orthographic projection persists. Camera snapshots keep projection, width and axis state through refreshes. Manual navigation exits Follow camera without changing playback or serialized data.
- Camera animation channels follow the engine's camera semantics ([cameras](engine-evidence.md#animation-runtime)): never treat Euler angles as eye coordinates or translation as a look-at target; the radian horizontal FOV converts to degrees at the preview-frame boundary. Camera keyframes keep parent transforms and deterministic seeking. Follow camera toggles apply immediately while paused.
- Idle regression captures read the presented back buffer without forcing a render or resize.

## Ground grid, collision and height

- Grid and Ground collision are independent controls, both on by default; Core callers opt into `GroundPlaneEnabled`.
- Grid visibility changes only rendering. The grid shader is independent of the scene far distance, antialiased, depth-tested without depth writes; only its pass disables depth clipping and clamps output depth. The grid never takes part in picking, framing or near/far bounds.
- Collision is a world Y=0 plane, not terrain or camera collision. Mesh-based contact includes morph, scale and spin and every authored LOD variant; independently moving descendants are excluded. Keep the landed support latch in checkpoints and through shrink/sink, with no extra impact cues; initial overlap correction alone emits no impact; missing geometry uses a diagnosed origin fallback. Release and impact audio are both gated on contact ([ground contact](engine-evidence.md#animation-runtime)). Preload collision samples; keep cue gain on seek and resume.
- Height defaults to 0, accepts −999 to 999 game units and is preview-only; negative values lower the animation; grid and collision plane stay at Y=0. Event and simulation coordinates stay authored: apply the world-Y offset once to output poses, effects, lights, cameras and audio and to ground-support measurements. Never translate serialized records or the static map.
- Grid, collision and Height changes rebuild at the current playhead, keep playback and custom range, and invalidate duration and checkpoints.

## Animation audio

- Audio is unmuted by default; volume starts at 50 %. A device failure shows a diagnostic and checks Mute (unchecking retries asynchronously); an unavailable sample is reported without disabling other sounds. Nonspatial approximation: no original attenuation, pitch or mixing.
- Prepare audio from a frozen dependency snapshot, including child animations, branches, cleanup and procedural impact samples. Decode only reachable samples off the UI thread; reuse one warm shared-mode WASAPI output (50 ms requested buffer, up to 32 voices) through pause, mute and stop. Sound cues, pause, mute and seek never initialize or dispose native audio devices on the UI thread. Scrubbing is silent; Play after seeking resumes still-active sounds without replaying finished ones.
- Keep active one-shot sounds in seek snapshots until their WAV duration ends, so mute and pause/resume do not drop them. Keep independent voice cursors and generation checks across cancellation, seek and asynchronous shutdown. Closing the preview cancels preparation and releases its output asynchronously.

## Animation runtime and duration

- Runtime simulation state stays separate from serialized data. The primary sequence is cleanup, not an extra runtime sequence. Events, procedural launch and gravity follow [animation runtime](engine-evidence.md#animation-runtime) (0x16 releases, 0x17 stops; launch bytes 32/40/48/56 are yaw, pitch, speed, radial acceleration).
- Approximation: starting an animation manually in the preview bypasses its activation prerequisites (a Support note says so).
- Playback, seeking and duration analysis advance in 60 Hz ticks; cancellation is observed only between ticks, so a canceled seek leaves the player where an uninterrupted run passes and resuming gives the same frames. A tick refused part-way is reported and never continued; frames are refused until a seek, step or playback replays from a checkpoint or a reset.
- Duration analysis runs an independent simulation on a snapshot (current phase, seed, conditions, ground collision, height, activation points) and never mutates source data, advances the visible player or emits sound. Finite ranges are calculated; programs that loop, wait for release or depend on gameplay are labelled Looping, Open-ended or Partial, never a known total. The suggested window is at least 5 s and includes observed finite tails; analysis is bounded to one hour of simulation and two seconds of work. Settings → Range in seconds overrides the range (rounded up to a 60 Hz frame); Auto restores it. Never assume one fixed duration for every asset.
- Transport: Space on a selected animation in Assets or passive preview content plays or pauses without moving focus or stealing native text, button or slider behaviour; during loading it queues playback (a second press cancels; a selection change cancels); modifiers and key repeat never toggle. The readout shows 60 Hz frame boundaries (frame 0 is the initial pose). Frame steps pause and clamp to the displayed range; Stop returns to frame 0 of the selected phase; Play at the endpoint restarts; Replay preview repeats the displayed range independently of authored loop events. The time slider and event trace scrub silently and pause.
- Bind root chooser: indexes each actor root once and lists the first 500 matching scene nodes in scene order with complete labels and node identities.
- Preparation limits refuse new work with a diagnostic before anything is published, never a partial context: up to 256 simultaneous animation instances; 10,000 dispatches per tick; binding/initialization work 96 Mi units per operation (6 Ki per node in scenes over 16,384 nodes) and frame traversal 64 Mi; 256 MiB of retained player state per player (optional checkpoints dropped first, so a seek may replay from earlier); 256 MiB each for decoded textures and for prepared meshes per viewport; 512 MiB of retained audio PCM; a 1,000,000-unit lighting allowance per frame. These are preview limits, not limits on authored game data.

## Whole world overlays

- AI networks and VTOL routes are presentation: they never change source data, document revisions or history, invent links or snap positions. Overlays, handles, the horizon and the grid stay out of framing and depth bounds. AI picking is off during a pickup drag or Fly; pickup handles take priority.
