# Pickup placement editor

Open a mission's `gamez.zbd`, then **Whole world**. Click the visible pickup to select it and show its bounds. The **Locked** checkbox starts checked for each newly opened map; inspection still works while locked.

Uncheck **Locked** to reveal the three translation arrows: red X, green Y, blue Z. Drag an arrow to move along that world axis. The handles use enlarged shafts and arrowheads for easier clicking and keep a consistent screen size as you zoom. Each complete drag is one undoable action. Escape, losing mouse capture, locking, or leaving the view cancels an unfinished drag. Right drag, middle drag, and the wheel retain their camera controls outside a pickup drag.

The Properties panel's **Pickup placement** section shows the type, record number, XYZ coordinates, affected difficulties, resource and save target. Enter a number and press Enter or leave the field to apply it. Escape restores the last accepted coordinate. There is no clear-field button. If the Properties panel is hidden, enable **View > Properties panel**. Translation does not rotate, resize, snap to terrain, or simulate pickup collection/respawn. An axis aimed directly at the camera may require a different view or numeric entry.

## Difficulty and identity

Moves apply to all uniquely matching difficulty records. Matching uses pickup type and the original position/rotation, not generated object names or record indices. Amounts and respawn values remain unchanged. If several difficulties use the same fallback resource, it is edited only once. The panel lists difficulties that are unmatched or ambiguous; their records remain unchanged. Switching difficulty or LOD retains authored changes and selection when a unique counterpart exists.

The `m1` lists for Easy, Medium and Hard contain the same 64 placements. A Nanite move therefore updates its three matching records. Pickups are loaded from `puppies_easy.zrd`, `puppies.zrd`, or `puppies_hard.zrd` inside the resolved archive. In `m1` that archive is **`zrdr.zbd`**; the surrounding `gamez.zbd` supplies models and world geometry.

## Save and working copies

- **Save / Ctrl+S** updates the owning working archive. Dragging itself never writes files.
- **Save As / Ctrl+Shift+S** writes a new archive and makes it the session's subsequent save target. It keeps the current map context; install the saved archive in your own working game-data folder to use it there. Reopening the untouched original dataset still shows its original placements.
- **File > Create backup on Save** optionally retains a uniquely named `.bak` beside each replaced archive. It defaults off and is remembered. Save As always requires a new destination filename.
- The `zbd_1998` and `zbd_1999` reference datasets remain protected. Save from those directories requests a copy elsewhere. For repeated direct saves, open a separate working copy of your game data.
- Undo/redo use the toolbar or Ctrl+Z/Ctrl+Y. Unsaved changes are marked on the map tab and participate in close/reload/workspace-change prompts.

Saving patches coordinate nodes in the existing archive layout. Member order, directory records, padding, unrelated resources, unknown bytes and untouched coordinates remain intact. An edited integer coordinate is written as a float node of the same length. All outputs are staged and reparsed before replacement; an external file change prevents overwrite. Each file replacement is atomic. If saving several archives stops partway through, the result identifies saved files and retains the remaining changes as unsaved.

This editor handles mission pickup lists. Pickups authored directly in GameZ, vehicles, turrets, scenery, pickup type/amount/rotation changes, and adding/deleting pickups are not editable here. Rendering remains a mission-start approximation. Binary preservation and reopened previews have been checked against the reference datasets; original-game validation is still outstanding.
