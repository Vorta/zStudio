# MechWarrior 3 source projects

Initialize accepts a MechWarrior 3 ZBD folder and reconstructs a separate `data/` and `gamegen/` source project. The target is original MechWarrior 3 1.2. The game files remain read-only; exporting is an explicit operation into a separate folder.

This first version provides the complete source pipeline. The in-app MW3 map editor, new-mission authoring, the RECOIL Blender extension workflow and Pirate's Moon are not supported.

## Initialize, check and export

1. Choose **Initialize** on the welcome screen. Select the game's `zbd` folder and a new or empty project folder outside it.
2. Open the reconstructed project. Its landing page explains the MW3 editing limits. Use **Export → Check source project** to build and validate the sources without publishing files.
3. Use **Export → Export ZBD files** to select outputs and a destination. Existing files require replacement consent. A failed output prevents publication of the whole selection.

The pipeline rebuilds the supplied world and animation files in `c1`, `c2`, `c3`, `c4`, `c4b` and `t1`, resource archives and mission variants, prepared scripts, texture libraries, sound banks, mech libraries and motion data. Optional quality variants are reconstructed when supplied. Other input files are listed as not reconstructed. The project does not need the original ZBD folder after initialization.

## Sources

`gamegen/mw3.project.json` identifies the game and lists the outputs and their build recipes. Recipes refer only to project-relative files under `data/` or `gamegen/`. Files are named by record index, so duplicate engine names remain distinct; recipes retain their original names, order and archive membership.

Each ZBD has a corresponding `data/<relative-output>.sources/` folder. It holds text ZRD resources, text interpreter scripts, WAV sounds, PNG images, position glTF files, and JSON parts containing named fixed records or typed scalar arrays. World and animation versions remain 27 and 39; motion remains version 4. Names, hierarchy, shared model indices, class data, free slots, material cycles, corner UVs/colors and unknown values remain in the record sources. Negative zero and nonfinite preserved float words use explicit `bits:` values. These are versioned build inputs, not the application's inspection JSON exports.

The geometry glTF profile uses the shared source-format-4 codec and an embedded buffer. It represents each model's indexed positions. Engine polygon topology, materials, normals, UVs and hierarchy stay in the named recipe records. It is not a complete scene checkout.

## Supported edits and limits

- Resource ZRD text, interpreter instructions and WAV samples can be edited on disk. Each sound's original bank format is a ceiling for edited WAV conversion. Unchanged original WAVs remain byte-identical, including any authored cues outside their sample data (reported without inferring MW3 playback semantics). For edited WAVs, export validates supported PCM and clamps out-of-range cues with a warning without modifying the source WAV.
- PNG pixels can change while dimensions remain the same. Original opaque, keyed and full-alpha modes and indexed/global-palette encoding are retained where possible. New colors outside an indexed image's original palette rebuild an inline palette within its color budget; adding transparency promotes the alpha mode when needed. Dithering is **None**; other dithering choices are refused for MW3.
- Position glTF edits keep vertex count and triangulation and stay within the recorded original envelope. Growing or moving geometry beyond that envelope requires rebuilding cached bounds and partitions and is refused. Node transforms, material assignment, morph weights and new mesh attributes on the position glTF are also refused.
- Typed normal, UV, corner-color and motion-sample arrays can be edited while preserving their element counts. Motion quaternions are stored WXYZ. Fixed world structure, cached spatial data, indices, opaque animation tracks and other records marked `preserved` are retained. Their fingerprints detect accidental unsupported edits; removing those checks does not make such edits supported.
- Full MW3 spatial compilation, topology editing, activation/reference restructuring and authoring new animations are later work. Reconstructing and preserving stored spline coefficients does not establish an inverse SI compiler.

Always check the project after external edits. Parsing and byte-preservation checks do not prove gameplay compatibility; test edited exports in a separate MW3 installation before replacing a playable copy.

## MCP

Use the existing `source_reconstruct`, `source_status`, `source_export_selection` and `source_export` tools. Reconstruction, status and export reports identify `game: "mw3"`; MW3 output families have an `mw3-` prefix. Omit the export destination to check only. `source_dithering` refuses non-None modes; Blender checkout/update is unavailable. These commands use the same source protection, snapshot, staging and publication services as the GUI.

## Evidence

Format and runtime evidence comes from [`telltaleatheist/mw3-re`](https://github.com/telltaleatheist/mw3-re/tree/dce9757b8b49d9a04a0bad8187741ca1bc12b62c), pinned to commit `dce9757b8b49d9a04a0bad8187741ca1bc12b62c`, and the user's read-only game data. The external project is not a build or runtime dependency. Historical authoring inputs that did not survive are represented by explicit source records; no original GameGen reconstruction is implied.
