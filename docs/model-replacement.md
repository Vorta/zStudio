# Native model replacement

Model replacement preparation admits original texture-pack bytes and complete replacement packs together under a 512 MiB allowance, before reading another pack or allocating an appended pack. Cancellation or refusal leaves document history unchanged. This allowance covers those retained raw buffers, not total process memory. Texture editing enforces its existing separate source and output allowances before each cold read and output allocation.

zStudio can export a GameZ node hierarchy for Blender and replace its existing model records with solid textured meshes. The animation continues to refer to the same nodes and model indices. Edits are accepted in the visible document, use Undo/Redo, and remain unsaved until an explicit verified Save.

## Export and prepare in Blender

Select an animation, a GameZ node, or a node-bound model and choose **File → Export referenced models…**. An animation resolves its authored root in the matching mission GameZ; ambiguous roots require an explicit `rootNode` through MCP. The bundle includes:

- `assembled.obj`, MTL and decoded PNG textures for spatial reference.
- `local/model_INDEX.obj` for every distinct model, without node transforms baked into its vertices.
- `manifest.json` with the GameZ SHA-256, model/node indices, parent/child identities and both local and assembled transforms.

The exporter visits all authored descendants, including inactive LOD variants. `bvol` collision helpers are labeled in the manifest and retained in the reference export. This is a root-hierarchy export; procedural effects and separately spawned child animations are not synthesized into its geometry. Existing animation JSON export is unchanged.

A bundle allows at most 10,000 node instances and 1,024 distinct models. Before constructing its manifest or exporting files, it also reserves a 32 MiB metadata allowance and at most 262,144 repeated parent, child and model-node references. Repeated instances retain their complete identities within these limits; choose a smaller subtree if the expanded hierarchy exceeds them. Scene preview and ordinary OBJ assembly independently bound traversal work, including groups without model geometry, and refuse excessive traversal instead of returning partial geometry. Bounded preview warning lists explicitly disclose omitted diagnostics.

Texture resolution discovers candidate packs once per model export and shares bounded lookup work and a 512 MiB cold-read allowance across its textures. Each exported PNG retains its complete decoded pixels; the preview's cumulative RGBA and highlight-mask allowance does not truncate exported images.

OBJ coordinates use game units, **+Y up and −Z forward**. Import/export these axes in Blender. The local origin is significant: the game applies the original node transform after loading the replacement. Export the mesh at its original local origin, with applied modeling transforms, triangle faces, UVs and normals. OBJ texture V is bottom-origin; zStudio reverses V at the interchange boundary.

## Replacement manifest

Create a separate version-1 replacement manifest next to the Blender exports:

```json
{
  "version": 1,
  "sourceSha256": "<64 hexadecimal characters from the bundle manifest>",
  "textureName": "my_shell",
  "texture": "my_shell.png",
  "models": [
    { "modelIndex": 1207, "obj": "shell.obj" },
    { "modelIndex": 1208, "obj": "shell.obj" }
  ]
}
```

Open the matching **GameZ** document and choose **File → Replace models…**. The entire batch is validated before publication. Names are labels: explicit indices and the source SHA-256 identify the target. Export a fresh bundle before a subsequent import against an edited snapshot. A single local OBJ can target several model indices; their existing node placements remain independent.

The importer supports one opaque diffuse texture per batch, power-of-two dimensions up to 512×512, and a new ASCII texture name of 1–19 letters/digits/underscores. PNG input uses the shared decoder, including supported greyscale, indexed and RGB/RGBA formats and Adam7 interlacing, and is converted to RGBA8; the resulting diffuse texture must be fully opaque. Retail textures stop at 256×256, and 512 is supported only on hardware that reports it:
- The retail loader stores 16-bit sizes and allocates by pixel count (`0x46ED70`), and the software renderer's spans cover 512.
- Direct3D texture creation (`0x4AA0F0`) replaces any texture larger than the device's reported maximum with the default texture. The engine forces that maximum to 256 when a driver reports 0.
- dgVoodoo-style wrappers report far larger limits. Original 1999-era hardware generally stopped at 256.

MTL files are optional; any diffuse map they specify must match the manifest texture. Paths must be relative, stay inside the input directory and avoid filesystem links. A batch contains 1–128 unique models; each has at most 65,535 corner vertices and 20,000 triangles. OBJ files are limited to 16 MiB; manifests/MTLs to 1 MiB; PNG input to 4 MiB. Nonfinite values, missing indices, degenerate triangles, non-triangulated faces and missing UVs/normals are rejected.

Repeated mappings to the same OBJ share one validated import. The batch is limited to 64 MiB of mesh/material input and eight distinct material libraries per OBJ.

OBJ and MTL input is scanned incrementally with cancellation checks inside long lines. Ignored OBJ operands do not become token arrays, and diagnostics bound authored text before formatting it. These presentation bounds do not shorten names or paths used for material lookup.

The writer supports intact GameZ v15 with a contiguous free material slot and texture packs v1. Models with authored morphs or lights are rejected. Full/fragmented pools and noncanonical source offsets fail before mutation.

Node bounds follow the retail rules, because the engine loads them verbatim and culls a node with its whole subtree when its box leaves the view:
- Each node referencing a replaced model gets the exact solid-model box as its model box and cached node box. The cached box also includes the node's valid child box.
- When that box no longer fits a parent's child box, the parent's child and node boxes expand, and so on up the hierarchy. Stored ancestor envelopes never shrink, so unchanged hierarchies keep their bytes.
- Geometry may therefore extend past a facade's original box, for example to offset a mesh inside its node.
- A node placed directly in a world partition keeps its stored grid box. Replacement geometry must fit that box, because re-gridding is outside this operation.
- An ancestor without valid child bounds (flag `0x400`) is not expanded by inference.
- The render-time sphere cache at node `+0x64`/`+0x70` stays zero.
- The model header's centre and radius use the engine's own formula. Facade models become ordinary solid models at their original indices. Other model payloads, node payloads, ordering, pointer metadata and unknown bytes are copied unchanged. Only changed geometry/header counts/bounds, necessary absolute offsets and the new material/texture pool links are written.

## Save and preview

The new dedicated texture is appended to every local mission texture-pack variant. The original texture and material remain available to other models. Preview, dependent animations and exports use frozen accepted document snapshots. Model and pickup edits share document undo ordering; Properties drafts and pickup drags must be resolved before an import. One GameZ document at a time may own model imports in a mission because the texture packs are shared.

Model acceptance claims the GameZ file, every prepared texture pack and current save targets through the same workspace ownership service used by resource/pickup editors. Conflicts are rejected before changing the snapshot, revision or undo history. Save As also claims every destination before staging files. Claims remain through undo and saving until the owning document closes. Both model and texture output sizes are checked against the 512 MiB document limit before output allocation.

**Save** stages and reparses all outputs before publishing textures first and GameZ last. Existing targets are checked against their saved bytes before atomic replacement. Per-file results disclose partial commits; remaining changes stay dirty. **Save As** requires new GameZ/texture paths in a chosen directory and retargets subsequent model saves. External-change monitoring follows each successfully saved destination and stops watching its former target, including after repeated Save As operations; files not yet published retain their existing target checks. A complete playable working root also needs its unchanged companion files, including the original animation archive. The protected `zbd_1998` and `zbd_1999` datasets are never save targets. No game assets belong in the repository.

Saving an upgraded pickup's GameZ dependencies does not require changing its animation event bytes. Reopen the saved working root to verify that the animation binds the replaced models. Parsing, source-byte preservation and Studio playback checks do not by themselves establish original-game compatibility.

After Save As, **Reload / F5** opens the saved GameZ destination and its local dependencies. If that destination is already open in another document, close that document first; reload retains the current document instead of creating competing copies.

## MCP

- Assets, Data inspection and Document scene read the accepted model snapshot, including appended materials/texture references. Existing asset rows and selection retain kind/index identity through replacement and undo/redo. Pinned asset Properties refreshes that same identity; a record removed by Undo shows an unavailable notice until Redo restores it.
- Original-source Bytes always uses the original record range. Newly appended records have no original bytes. `zstudio_assets` exposes current `Offset`/`Length` separately from nullable `sourceOffset`/`sourceLength`; use the latter with `zstudio_source_bytes`.
- `zstudio_model_bundle_export(document, kind, index, destination, rootNode?, texturePack?)` returns an operation handle and then the bundle directory/manifest and model/placement counts.
- `zstudio_model_replace(document, revision, manifest)` returns an operation handle. It validates off-thread, rechecks lifetime/revision/drafts, accepts one batch and rebuilds the visible preview.
- `zstudio_inspect_asset` keeps original `source` separate from a frozen `edited` snapshot.
- `zstudio_undo_redo` shares GUI history and awaits the model preview refresh.
- `zstudio_save_document(document, revision, modelDirectory?)` saves model dependencies; `modelDirectory` requests Save As. Existing animation/pickup parameters continue to work.

These tools share the GUI workspace and existing opt-in named-pipe host. Initialization and discovery remain windowless. Model export is cancellable; replacement/save publication uses explicit operation results and is not canceled by the toolbar's export/validation command.

## Format evidence

The material-slot links were historically labeled backwards in inspection. Retail `0x4812C0`, specifically `0x48130E`/`0x481316` and `0x481353`–`0x481381`, verifies **previous at +0x28 and next at +0x2A**. The allocator removes the free head and prepends it to the active list. Writer validation traverses both complete disjoint lists before and after the change. The private reconstruction's `zModel/gmod.h` offsets agree with the inspected retail instructions. Model dynamic ranges and their absolute table offsets follow retail `0x4815C0`; node class-none offset words remain parent indices and are never relocated.

Node bounds, per the private reconstruction's `zClass.h`: `+0x74` is the cached node box, `+0x8C` the model box and `+0xA4` the child box, all in node-local space. Flags `0x100`/`0x200`/`0x400` mark them valid.
- `gwNodeRecalcBBox` (`0x448E90`) sets cached = model ∪ child and marks non-world parents dirty.
- `gwNodeComputeChildBBox` (`0x4491B0`) unions the children's cached boxes, each transformed by its local matrix.
- `CZZbd::ReadNodeTable` (`0x455350`) reads the table raw, without recomputation.
- Render traversal (Object3D `0x44B300`) turns the cached box into the world sphere at `+0x64`/`+0x70` and skips the node's subtree when that sphere is outside the frustum.
- `zDi::RebuildBounds` (`0x483AD0`) sets the model centre to min + half extent. Its radius is the bit approximation `(bits(hx²+hy²+hz²) >> 1) + 0x1FC00000`, which reproduces the stored corpus radii 1.41 (m1 model 1206) and 2.045 (facade 1207). External references are read-only development evidence, not build dependencies.
