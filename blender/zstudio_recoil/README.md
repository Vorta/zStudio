# zStudio RECOIL for Blender

New glTF files use `extras.zstudio`. Core and the Blender extension also read legacy `extras.recoil`; when both keys are present, `zstudio` wins without merging. Import does not rewrite source files. Blender operator IDs, property handles, saved-state names and the extension ID remain unchanged.

This extension imports and exports zStudio source glTF graphs in **Blender 5.2 LTS on Windows x64**. It uses Blender's glTF importer and exporter and the zStudio format library through a windowless helper; it needs no zStudio window, MCP access or network connection.

Both RECOIL and MechWarrior 3 source projects use this workflow. MW3 world, mech and lab graphs retain their native node words, corner colours, material switches and cycles. The source game and library member are fixed identities; an existing base mech's named top-level root must stay available to AIV actors. Use fresh version-3 checkouts and the matching extension/helper (graph protocol 2); source glTF remains format 4. The existing RECOIL menu and property names refer to these shared tools.

## Install

1. In Blender, open **Edit → Preferences → Get Extensions** and choose **Install from Disk** in the upper-right menu.
2. Select `zStudio-Blender-extension-<version>.zip` from your zStudio folder, beside `zStudio.exe` (the version is zStudio's), and enable it. Blender lists it as **zStudio RECOIL**.
3. In **Preferences → Add-ons**, expand **zStudio RECOIL** and set **zStudio executable** to that folder's `zStudio.exe`.

The extension and zStudio share one version: the extension works only with the `zStudio.exe` of its own version, so update them together. The ZIP need not stay beside `zStudio.exe` once the extension is installed.

## Import and edit

In zStudio, open the project's map and choose **Tools → Edit in Blender…** to check out a source model or master map with every file it references. In Blender, choose **File → Import → RECOIL glTF** and select the checkout's input file: Blender's file browser hides the project's `.zstudio` folder, so paste the path zStudio shows. Each import gets its own Blender scene; each physical glTF file is one shared collection, and reference holders display the file they reference through collection instances.

- **Object Properties → RECOIL** (also in the **RECOIL** sidebar) edits an object's engine name, its node flags, its crater mode (Can Modify or Clip To) and its zone (**Inherit**, **Any**, or 0–254). Hover over a field for its role in the game. **File Load Root** in the sidebar edits the file's root attributes.
- **Edit Shared Model** on a reference holder edits the referenced file's collection: changes there affect every reference to that file. Add new objects there. Choose **Return to World** when done. Move reference holders, not the collection instances, to place copies.
- **Edit Placement Zones** opens a zone workspace for that one occurrence of a referenced file: node zones, altitude gates and face zones there affect only that placement. **Accept Placement Zones** keeps them, **Cancel Placement Zones** discards them. Finish every zone workspace before exporting.
- In Mesh Edit Mode, **Assign Polygon Zones** sets an ordered list of up to three zone IDs (or `any`; empty clears) on the selected faces, and **Select Faces by Zones** selects faces with a given list. Objects sharing a mesh share its face zones; make the mesh single-user in **Edit Shared Model** to give them their own.
- **Mesh Properties**, **Material Properties** and **Object Appearance** show their stored flag words as checkboxes and the other engine values as JSON.
- To make a textured material untextured, disconnect its image and remove `texture` from **Material Properties → Other values**.

Export refuses edits it can't map back to the sources, and says what to undo:
- removing, duplicating, moving or retargeting a reference's display helper (the `↳` child of a holder that has its own mesh);
- objects copied from another RECOIL import, or faces joined (**Ctrl+J**) or Boolean-merged from another RECOIL mesh;
- modifiers that remove face identities or mix incompatible zone assignments (apply them first), and modifiers on a mesh with shape keys (remove the modifiers or the shape keys; other modifiers export as their result);
- parent-relative transforms that require shear, including Keep Transform parenting below nonuniform scale (apply nonuniform scale before parenting, or adjust the parenting or constraints);
- hidden library-linked constraint or modifier targets (make them visible in their library or use a local visible target);
- geometry or hierarchy changes inside a zone workspace.

## Export and update zStudio

**Add Model Reference** requires Object Mode; finish mesh edits before creating a placement.

To add a placement, open the **RECOIL** sidebar and choose **Add Model Reference**. Enter a model source path already present in this imported graph (for example `data/common/models/pturret.gltf`), the placement and reference-child names, and choose **Active object** or **Source root** as the parent. **Sibling position** is zero-based. The command creates fresh objects referencing the existing shared model; it does not copy its geometry.

The dialog accepts an exact glTF source transform as JSON: either `{"matrix":[16 column-major values]}` or an object containing `translation`, `rotation` (XYZW quaternion) and/or `scale`. These are glTF coordinates, not Blender's Z-up coordinates. Empty `{}` means identity. Placement and reference properties are separate RECOIL JSON objects; ordinary flags and zones can also be edited in the existing panels afterward. Subsequent Blender transform edits supersede the exact source transform when the pose changes. Sheared/non-affine poses are refused.

The selected model determines the reference path. Insertions are anchored to the following sibling; if that sibling is later removed or reparented, undo that edit or recreate the new placement at the intended position. Insertions within an inline source prefix are refused: choose a position after that section. To recreate a deleted placement exactly, supply its complete original transform, names, metadata, parent and sibling position; a model file alone does not contain those placement values.

1. Choose **File → Export → RECOIL glTF graph** (or **Export RECOIL glTF** in the sidebar); a checkout exports into its own outbox. Each export is a complete new generation of the linked files with a receipt. Hidden objects export too.
   Object constraints export their evaluated pose at the current frame, preserving each object's transform relative to its parent.
2. In zStudio, with a map of the project open, choose **Tools → Update from Blender export…**. The whole graph is validated and applied as one undoable edit; placement zones elsewhere in the project follow it. Source edits made in zStudio since the checkout, and shared texture changes, need your confirmation.
3. **Save** in zStudio writes the source files; only a project export builds game files.

An export is a Blender undo step. After undoing past an export that zStudio applied, redo it (**Ctrl+Shift+Z**) or undo that update in zStudio before exporting again, or start a fresh checkout. Use the RECOIL commands for this workflow: ordinary Blender glTF exports lack the provenance an update needs. Saving the `.blend` keeps the imported baseline and images.

MCP: `zstudio_source_blender_checkout`, `zstudio_source_blender_checkouts`, `zstudio_source_blender_update`, `zstudio_source_blender_extension`.

## Limits

- To continue editing an export in a new Blender session, import its root glTF from the complete generation folder; keep the dependencies and `recoil-export.json` together. This starts a fresh import, without inheriting the previous session's update history.
- Ordinary translation-only edits retain the original matrix or TRS representation and untouched transform values. Blender still computes edits with float precision: an inverse move is not guaranteed to restore every source number at every coordinate, particularly small fractional coordinates after a large move.
- Blender 5.2.x, `.gltf` files with external PNG textures, and the zStudio source format's rules for materials, names, transforms and model sizes. Give new surfaces a material with Metallic set to zero.
- Missing images show as magenta checkers and never become source textures; replace one with an actual image to export it.
- A graph holds at most 4,096 files and 512 MiB of source data; cyclic or escaping references are refused.
- Blender's view is an editing view, not the game's collision, crater or zone queries. A pickup's collision volume (`bvol`) is hidden by its material, so it still counts in framing and shows in wireframe; keep it, because the game registers a pickup only when its model has one.
- Each import copies its files to a `zstudio-blender-…` folder in Windows' temporary folder, which isn't deleted automatically; delete old ones when Blender is closed.
- The extension never changes the files it imported. Exports go to the checkout's outbox, which is the only folder **Update from Blender export…** reads; export elsewhere only for your own copies.
