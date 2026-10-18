# VTOL drop windows

Open a mission in **Whole world**, then choose **Parachute** above the viewport in a compiled world, or **VTOL route** in a source map's Show group. Editing compiled ZBD assets is being retired in favour of [source projects](source-project.md). A compiled root opens only through MCP `open_root` (the GUI opens only source projects); a source map has no editing lock. The flight selector identifies an animation entry and its bound root. The blue line shows one flight cycle with the initial segment from the pre-flight pose omitted; start markers identify existing drop windows, and release markers show eligible callback positions. These are authored release positions, not parachute landing predictions. Range conditions and other game state still determine whether an item is released in the game.

Analysis uses an independent, frozen instance of the existing animation runtime. It does not move the visible player, play audio, alter the mission's starting scene or change source data. A root without a visible mesh is sampled at its own transform, including its parents. Unsupported programs and exhausted analysis limits report why the flight cannot be analyzed; an incomplete path is never presented as complete. A flight that cannot be analyzed keeps its refusal message and the flight selector available, so another discovered flight can still be opened.

## Move a window

1. In a compiled world, unlock editing first. Select a drop window in the list or at its start marker.
2. Choose **Move along path** to follow the pointer along adjoining path segments, or **Set start time** to enter a local cycle time in seconds.
3. Inspect the proposed release markers. **Confirm** accepts one undo step; **Cancel** restores the original markers. Enter and Escape perform these actions from the viewport; native text controls retain their own keyboard behavior.

A move changes only the existing call's start threshold. The stop duration, callback cadence, conditions, event order and all other operands are retained. It stays within the cycle and neighboring windows. Runtime verification refuses a move that changes another window's releases. Some authored windows overlap in time and depend on a preceding stop; a predecessor that cannot move independently remains visible with an explicit refusal reason.

The path retains every ordered sample and time identity, including the undrawn initial segment. Release positions and edit calculations are unchanged. At a crossing, pointer movement follows adjoining segments from the selected window's current phase instead of jumping to a different visit to the same position. Use the numeric start control, or an exact MCP segment and fraction, to select another phase deliberately. Adjoining stationary intervals can be crossed without jumping to an unrelated visit. If both exits from a hover project to the same path and the intended phase is ambiguous, use the numeric start time. An ambiguous click on overlapping start markers requires choosing the window from the list.

Other editing modes and Fly cannot overlap a move. Camera changes are blocked during pointer movement; a numeric draft allows ordinary camera inspection. Document changes, replacement and resource changes invalidate old analyses. Unfinished numeric input remains a draft until explicitly confirmed or canceled. A failed source rebuild rolls back the accepted source change and retains a still-wanted draft for retry; explicit cancellation does not restore it.

While a new start is being analyzed or input is invalid, the viewport keeps the last valid draft preview. Invalid input shows its reason and disables Confirm. MCP samples/releases mirror that displayed preview; retrying valid input updates it, and Cancel restores the original markers.

## Ownership and saves

In a compiled world, the actual companion `anim.zbd` document opens in Files while the world remains visible. The Parachute panel identifies that animation owner. Its Undo, Redo and Save controls target that document; the title-bar commands continue to target the displayed world. Animation Save produces a verified **new copy outside the source root**, using the existing animation Save As protection. It does not overwrite the original archive or automatically install the copy into a mission.

If mission-coordinate inspection attaches a coordinate edit session to the animation document, ordinary document Save still writes the animation copy and also saves any modified coordinates. Save-and-close keeps the document open if any changes remain unsaved. Ordinary Undo/Redo use the animation's available steps first, then coordinate history when the animation has no step in that direction. The dedicated Parachute history controls always target animation history.

In a source-project world, the edit changes one `START_TIME` scalar in the owning `.zad` definition. Existing compiler provenance and a complete before/after compilation verify that only the selected threshold changed. A shared definition that would also alter other entries cannot be edited independently through this panel. Acceptance uses the existing project transaction, world rebuild, rollback and undo. Save writes the project's sources; no `anim.zbd` is created or edited (only export compiles one). A successful rebuild closes the old analysis; reopen **VTOL route** in the replacement world to continue.

## MCP parity

`zstudio_parachute` exposes the same analysis, selection, drafts, history and saves through cancellable operation handles. Every request names the displayed preview, world document and revision. Except for `show`, it also names the returned Parachute snapshot. Compiled `begin`, `set`, `confirm`, `undo`, `redo` and `save` require the returned `ownerRevision`. Draft updates and confirmation require their token.

- `show`, `hide`, `analyze` and `select` open/close the overlay and choose exact entry/window identities.
- `begin`, `set`, `confirm` and `cancel` operate on the shared GUI draft. `set` takes either `start` seconds or `segment` plus `fraction`.
- `samples` and `releases` page the ordered path and eligible release positions using `offset` and `limit`; they have no text-query filter. `preview_state.parachute` gives bounded summary, identity and draft state, including `analysisRefusal` for an unavailable selected flight.
- `undo`, `redo` and `save` target the explicitly identified owner. Compiled `save` requires `destination`; source `save` rejects a destination and saves the source project.

Discovery remains windowless. These are workspace operations and use the existing opt-in transport and visible workspace; no command captures physical input.
