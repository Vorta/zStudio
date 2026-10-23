# zStudio RECOIL for Blender

This extension imports and exports zStudio source glTF graphs in **Blender 5.2 LTS on Windows x64**. It uses Blender's glTF importer and exporter and the zStudio format library through a windowless helper; it needs no zStudio window, MCP access or network connection.

## Install

1. In Blender, open **Edit → Preferences → Get Extensions** and choose **Install from Disk** in the upper-right menu.
2. Select `zStudio-Blender-extension-<version>.zip` from your zStudio folder, beside `zStudio.exe` (the version is zStudio's), and enable it. Blender lists it as **zStudio RECOIL**.
3. In **Preferences → Add-ons**, expand **zStudio RECOIL** and set **zStudio executable** to that folder's `zStudio.exe`.

The extension and zStudio share one version; update them together and keep the extension's ZIP beside `zStudio.exe`.

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
- geometry or hierarchy changes inside a zone workspace.

## Export and update zStudio

1. Choose **File → Export → RECOIL glTF graph** (or **Export RECOIL glTF** in the sidebar); a checkout exports into its own outbox. Each export is a complete new generation of the linked files with a receipt. Hidden objects export too.
2. In zStudio, with a map of the project open, choose **Tools → Update from Blender export…**. The whole graph is validated and applied as one undoable edit; placement zones elsewhere in the project follow it. Source edits made in zStudio since the checkout, and shared texture changes, need your confirmation.
3. **Save** in zStudio writes the source files; only a project export builds game files.

An export is a Blender undo step. After undoing past an export that zStudio applied, redo it (**Ctrl+Shift+Z**) or undo that update in zStudio before exporting again, or start a fresh checkout. Use the RECOIL commands for this workflow: ordinary Blender glTF exports lack the provenance an update needs. Saving the `.blend` keeps the imported baseline and images.

MCP: `zstudio_source_blender_checkout`, `zstudio_source_blender_checkouts`, `zstudio_source_blender_update`, `zstudio_source_blender_extension`.

## Limits

- Blender 5.2.x, `.gltf` files with external PNG textures, and the zStudio source format's rules for materials, names, transforms and model sizes. Give new surfaces a material with Metallic set to zero.
- Missing images show as magenta checkers and never become source textures; replace one with an actual image to export it.
- A graph holds at most 4,096 files and 512 MiB of source data; cyclic or escaping references are refused.
- Blender's view is an editing view, not the game's collision, crater or zone queries. A pickup's collision volume (`bvol`) is hidden by its material, so it still counts in framing and shows in wireframe; keep it, because the game registers a pickup only when its model has one.
- Each import copies its files to a `zstudio-blender-…` folder in Windows' temporary folder, which isn't deleted automatically; delete old ones when Blender is closed.
- The extension never changes the files it imported. Exports go to the checkout's outbox, which is the only folder **Update from Blender export…** reads; export elsewhere only for your own copies.
