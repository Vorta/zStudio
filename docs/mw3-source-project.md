# MechWarrior 3 source projects

New glTF files use `extras.zstudio`. Core and the Blender extension also read legacy `extras.recoil`; when both keys are present, `zstudio` wins without merging. Import does not rewrite source files. Blender operator IDs, property handles, saved-state names and the extension ID remain unchanged.

Initialize reconstructs an original MechWarrior 3 ZBD folder into a separate `data/` and `gamegen/` source tree. Check and Export compile those sources for original MW3 1.2. The original game folder is read-only and is not needed after reconstruction.

The in-app MW3 map editor, the RECOIL Blender checkout/update workflow, new campaign scaffolding and Pirate's Moon are deferred. Terrain recipes and terrain conversion are not supported for MW3. Source files can be edited on disk. Opening compiled game files remains inspection only.

## Initialize, check and export

1. Choose **Initialize** on the welcome screen, the original `zbd` folder, and a new or empty project folder outside it.
2. Open the project and use **Export → Check source project** to compile and validate without writing game files.
3. Use **Export → Export ZBD files** to choose outputs and a separate destination. Existing outputs require replacement consent. A failed selected output prevents publication of the whole selection.

Original resource archives must carry the animation definitions their compiled programs were built from. Rebuilt files whose definitions were deliberately left in the source project are not initialization inputs. Obsolete projects containing `gamegen/mw3.project.json` must be reconstructed from the original files.

## Recovered source tree

The archive directory records compiler input paths. Resources return to those folders with their member names: `data/common/zrdr`, `data/common/zrdr/mechs`, `data/c1/zrdr/envmodels`, `data/c1/zrdr/aipath/m1`, and the corresponding campaign folders. Animation definitions become `.zad` files; their lists name `.zad` files as well. Other resource data remains `.zrd` text.

Prepared scripts retain their entry paths and file times: `gamegen/c1.gs`, `gamegen/Support/common.gw` and the other archived scripts. File operands use `.gltf` and `.png` in the source project. Macro operands such as `%DB%.flt` become `%DB%.gltf`.

Models use the shared full source-format-4 glTF codec with external binary buffers and PNGs. The model files retain the original FLT boundaries and references recovered by reversing the build scripts over the shipped worlds. They include hierarchy, transforms, geometry, material assignments, normals, UVs, morph data and corner colours. Library archive paths recover `data/lab/models` and `data/common/mechs/models`; a scene's `extras.zstudio.mechLibraryMember` identifies an original library member. The source profile identifies `game: "mw3"` without a separate manifest.

The campaign masters retain source groups and objects that were not registered into the rendered world. Those records are recovered too, including animation roots outside the map graph. Their top-level subtrees carry `extras.zstudio.registerWithWorld: false` so a build keeps them below the master. Registered objects whose original group attachment was removed remain in the master in record order; surviving group structure is preserved. Both JSON `.gltf` and binary `.glb` containers are supported. Native polygons may contain up to 255 corners.

The native animation compiler reads `.zad` definitions and the `.zan` scripts they name. Campaign keyframe scripts return to the paths recorded by animation source stamps, using the original `SI Animation Script` layout: `FRAMES:`, `OBJECTS:`, then `Frame:`, `Object:`, `Scaling:`, `Rotation:` and `Translation:` blocks with blank lines and Windows line endings. Values use exactly six decimal places. Initialization verifies identical channel flags and frame times, with small bounded differences in base values and rates; exported animations need not reproduce the original binary bits. A base component may differ by 0.000001, and a rate difference may accumulate to 0.000002 over its segment, with four float ULPs also allowed at large magnitudes. Initialization refuses a script outside those limits instead of substituting another grammar. Native rotation rates use the rounding behavior measured in the later official 1.2 animation rebuild. Animation compilation depends on its ZAN and ZAD sources, with no precision setting in glTF. Reconstruction refuses cubic SI interpolation rather than discard its coefficients. Unused cubic slots of linear events contain compiler residue and are initialized on export. Definitions that differ from the compiled game program are repaired only when recompilation verifies their complete expanded entries; reconstruction notes describe the repair. Existing projects are not rewritten automatically, and the shared `OBJECT`/`FRAME` syntax remains readable.

Version-4 sampled mech motion members become named `.zan` files under `data/common/mechs/motions`. Their original authoring folder cannot be recovered: the archive records compiler temporary files under the runtime folder. These files use a distinct sampled-motion grammar, with `MOTION`, `LOOP_SECONDS`, `FRAME_COUNT`, named `PART` tracks and `SAMPLES`. Each sample lists translation XYZ followed by quaternion WXYZ. The closing sample is retained separately from the playback frame count.

Textures become named PNGs. Model file locations and the scripts' texture search paths identify model texture folders; `IMAGE_PATH` resources identify interface image folders. Unreferenced campaign textures remain in that campaign's `textures` folder. Reconstruction notes disclose these inferences: a pack's name directory alone does not recover every original loose-file subfolder. WAVs use `data/common/sounds`.

New MW3 initializations name each available, referenced WAV after its first declared sound ID, including files whose original names were already descriptive. For example, `0001.wav` becomes `mech_powerup.wav`. Every declaration sharing that original audio points to the same renamed WAV; sound IDs and playback settings stay intact. The common `sounds.zrd` determines names first, followed by other sound-definition resources in source-path order. Unreferenced audio keeps its stored filename; references to audio absent from the supplied banks stay unresolved and unchanged, with a reconstruction note. Conflicting or unsavable final filenames refuse initialization. Existing projects are not renamed automatically.

Assets absent from the supplied files cannot be reconstructed. For example a supplied mech library may refer to lab images whose texture library was not supplied. The model keeps those texture names and Check/Export warns about their missing PNGs.

## Native builds and edits

The Export ZBD files window selects **MechWarrior 3 1.2** for a native project, with its own remembered file choices and destination. RECOIL projects retain their v1/v2 selector. MCP `source_export` requires `target: "mw3"`; `source_export_selection` defaults to that native target and `source_status` lists `targets: ["mw3"]`. A target belonging to the other game is refused. Core callers pass `ExportTarget.Mw3` to `SourceBuilder.CheckAsync` or `ExportAsync`; their default remains RECOIL v1.

GameGen scripts assemble version-27 worlds and recalculate bounds and partitions. MW3 model material switches, texture cycles, vertex colours and native node attributes are carried by the glTF profile. Texture cycles store authored speed, looping and frame names; runtime cursor values are initialized by the compiler. Mutable materials belong to each model, as required for independent animation.

glTF vertex colours use the standard 0–1 scale; the compiler converts them to MW3's 0–255 channels. RGB (`VEC3`) and fully opaque RGBA (`VEC4`) colours are accepted, as floats or normalized unsigned bytes or shorts, following the [glTF colour accessor types](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#meshes). Vertex alpha other than 1 is refused because the native corner stream cannot preserve it. Meshes without vertex colours compile with white corners. Native `LoadGameGen` options `altitude_off` and `intersect_off` affect the loaded root; unknown option spellings have no effect. `Object3DSetScroll` and `Object3DSetScrollAlways` apply script-authored scroll rates, including the detached holder used by the always-scroll path.

Texture wrap and clamp modes come from glTF samplers and are recovered separately for each campaign. The master's `extras.zstudio.mw3TextureAddressing` holds fallback modes for textures used only by scripts: 0 repeats both axes, 1 clamps U, 2 clamps V, and 3 clamps both. A model's sampler takes precedence when that texture is used in geometry. Scroll commands with `OFF` are refused because they crash the original 1.2 interpreter; remove that command and disable the model's scrolling property instead.

Mech-library material cycles are refused because the library's material stream has no implemented cycle payload. Campaign world material cycles are supported. Assigning a PNG in another source folder includes its resolved image in the corresponding texture library; a PNG URI takes precedence over a retained engine-name alias.

Resource folder membership determines the resource outputs: common resources build `reader.zbd`, campaign resources build `c1/reader.zbd` and equivalents, and `aipath/m1` builds `readerm1.zbd`. Prepared scripts build `interp.zbd`. Native model membership builds `mechlib.zbd`; sampled motions build `motion.zbd`; image and texture folders build `rimage.zbd`, `rmechtex.zbd` and campaign `rtexture.zbd`. Animation roots build each campaign's `anim.zbd`. Sound declarations and WAV sources build `soundsH.zbd`.

Edit resources, script instructions, full glTF scenes, animation definitions, keyframe or sampled motion tracks, PNG pixels and supported PCM WAVs, then check the project. Geometry may change topology and extent within the shared model compiler's supported limits; bounds and partitions are rebuilt. There is no fixed-position-envelope recipe. Animation names and roots must resolve against the rebuilt world. Reconstruction checks six-decimal keyframe accuracy separately from small, bounded rounding differences in procedural animation arithmetic.

Texture exports use RGB565 and the appropriate transparency representation at the source image dimensions, preserving native six-bit green precision in keyed images. A keyed source with opaque black uses an alpha plane to preserve both black and transparency. Exports can normalize palettes and encoding rather than reproduce an original library byte for byte. Dithering is **None**; other settings are refused for MW3. Sound exports follow `sounds.zrd` declarations when present and clamp out-of-range cues with a warning. Sources remain unchanged.

Check verifies parsing and source compilation. Compatibility with the original game's runtime still requires testing exports in a separate MW3 installation before replacing playable files.

## MCP

Use `source_reconstruct`, `source_status`, `source_export_selection` and `source_export`. Reconstruction, status and export identify `game: "mw3"`. Native library output families have an `mw3-` prefix; resources, scripts, worlds and animations use the shared families. Omit the export destination for a check. GUI and MCP use the same source snapshots, protection, staging and publication services.

## Evidence

The pinned reference is [`telltaleatheist/mw3-re` at dce9757b8b49d9a04a0bad8187741ca1bc12b62c](https://github.com/telltaleatheist/mw3-re/tree/dce9757b8b49d9a04a0bad8187741ca1bc12b62c), together with the read-only original game data. The external project is evidence only and is not a build or runtime dependency. Engine mechanisms are recorded in [engine evidence](agents/engine-evidence.md).
