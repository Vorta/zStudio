# zStudio RECOIL for Blender

This extension imports and exports zStudio source-format-4 glTF graphs in **Blender 5.2 LTS on Windows x64**. It uses Blender's bundled glTF importer/exporter and the installed zStudio format library through a windowless helper. No zStudio window, MCP permission or network connection is needed.

## Install

1. In Blender, open **Edit → Preferences → Get Extensions** and choose **Install from Disk** from the upper-right menu.
2. Select `dependencies/blender/zstudio_recoil-0.9.0.zip` from the portable zStudio folder and enable it.
3. In **Preferences → Add-ons**, find **zStudio RECOIL**, expand its entry, and set **zStudio executable** to that portable folder's `zStudio.exe`.

The helper is chosen in your preferences. Imported files cannot choose an executable. The extension and zStudio share version **0.9.0**; update them together. Keep the portable folder's `dependencies` alongside `zStudio.exe`.

## Import and edit

Use **File → Import → RECOIL glTF**. Select a source project's master map or a model, or the input file of a zStudio checkout. Each imported graph gets its own Blender scene; switch between imports with Blender's scene selector. Each physical glTF has one shared collection. Reference holders retain their own transforms, flags and zone assignment; collection instances display the referenced file. Reference spellings such as `part.gltf` and `./part.gltf` stay distinct in the exported graph.

Select an object and open **Object Properties → RECOIL** to edit its engine name, seven Boolean node flags, optional Can Modify / Clip To crater mode, and zone (**Inherit**, **Any**, or an ID from 0 to 254). Hover over each field for a description of its role in the game. Zone ID 0 is an ordinary explicit zone; **Inherit** takes the parent zone. Omitted Boolean flags mean false. Unset crater mode writes no `declient` property. The unresolved node bits are not exposed.

The **RECOIL** sidebar shows the same editing values alongside the import, export and reference-workspace actions. **File Load Root** in that sidebar edits the file's root attributes. Source identities, original names, metadata and workspace bookkeeping are saved internally and hidden from ordinary Custom Properties. Use the RECOIL fields and advanced dialogs to edit engine properties; your own Custom Properties remain available. Import the glTF again to create a scene for this extension; scenes without the required internal state cannot be exported.

Select a reference holder and choose **Edit Shared Model** to edit its source collection. Geometry and source properties there affect every reference to that file. Ordinary child collections may organize that file; viewport hiding and selection locks do not remove objects from export. Keep physical collection instance offsets at zero and move reference holders to place instances. Use **Return to World** when finished. New objects belong in that source collection. Physical source filenames and existing reference links are retained; the extension does not invent a separate glTF for every mesh.

Keep reference instancing enabled and its collection target intact. A holder that also has its own mesh uses an `↳` child to display the referenced file: move the holder, and leave that child parented to it with identity transforms. Export refuses removed, duplicated, moved or retargeted reference helpers. Undo those helper edits or reimport to restore the reference display.

Imported objects and resources belong to their original graph. Export refuses copies from a separate RECOIL import, even when both graphs use the same filenames. Undo the cross-graph copy and create the replacement within the intended graph. Duplicating objects or making meshes single-user within that graph remains supported.

**Edit Placement Zones** opens a separate zone workspace. Node zones, altitude gates and face zones there affect only that reference occurrence, including nested occurrences. **Accept Placement Zones** keeps these assignments; **Cancel Placement Zones** discards that workspace's draft. Geometry and hierarchy changes are refused in a zone workspace: use shared-model editing for those. Nested placement edits remain inside the outer draft: accepting a nested workspace keeps its changes in that draft, and canceling the outer workspace discards them. Finish or cancel every open zone workspace before exporting.

Objects using the same mesh inside that physical file also share its face assignments. To give them independent faces, first make the mesh single-user in **Edit Shared Model**, then edit the placement zones.

In Mesh Edit Mode select faces, then choose **Assign Polygon Zones**. Enter an ordered comma-separated list of up to three zone IDs, with `any` allowed; an empty list clears explicit face zones. **Select Faces by Zones** selects faces with that exact assignment. The face attributes survive ordinary Blender geometry editing. If a modifier mixes incompatible identities or assignments, export refuses instead of guessing which placement zones should follow them.

**Mesh Properties**, **Material Properties** and **Object Appearance** expose their separate stored flag words as checkboxes, with the remaining values as JSON. These are advanced engine attributes, not additional node flags. Object transform bookkeeping is represented by Blender's transforms and is excluded from appearance flags. Source validation runs before an export is offered to zStudio. Blender's viewport is an editing view, not a simulation of the game's collision, crater or altitude queries.

To make an imported textured material untextured, disconnect its image and remove `texture` from **Material Properties → Other values**. Export refuses a removed image while a recorded engine texture name remains, including missing-image checkers. Materials originally authored with only a named runtime texture retain that behavior.

## Export and update zStudio

Use **File → Export → RECOIL glTF graph** or **Export RECOIL glTF** in the sidebar. Choose a folder outside the imported sources; a checkout defaults to its outbox. A successful export creates a new generation containing all linked glTFs, buffers and textures, with a completion receipt. Source properties and zones remain embedded in glTF. The receipt and provenance are temporary workflow data, not authoritative project metadata.

For a zStudio project, use **Tools → Edit in Blender…** to choose a source file and create a checkout first. After exporting from Blender, open the mission world and choose **Tools → Update from Blender export…**. The whole graph is validated and accepted as one undoable source edit. Existing placement assignments in other source files are remapped through the edit. Conflicting source changes and shared texture replacements require an explicit decision. Save in zStudio to publish source changes; only a project export writes compiled game files.

Use the RECOIL import/export commands for this workflow. Ordinary Blender glTF exports lack the graph provenance needed for safe updates. Old flattened checkouts must be replaced by a new checkout.

Saving a `.blend` retains the exact imported baseline, reference collection identities, editing properties and packed images. Reopening can restore its temporary baseline cache. Export still validates all current geometry and metadata. Polygon boundaries are retained when they represent the exported surface; faces with differing placement zones, normals, UVs or non-affine shape-key deformation remain separate when merging them would change that surface.

## Limits

The initial compatibility range is Blender 5.2.x, with glTF `.gltf` files and external PNG textures. Normal zStudio source-format restrictions apply, including supported material channels, engine names, transforms and model sizes. Assign new surfaces a material with Metallic set to zero. Missing source images (including names supplied by runtime texture cycles) appear as magenta checkers; unchanged checkers are editor placeholders and never become source textures. Replacing a checker with an actual image exports that authored image normally. A graph is bounded to 4,096 files and 512 MiB of source bytes. Cyclic or escaping references are refused. No operation writes the protected retail corpora or the original imported files.
