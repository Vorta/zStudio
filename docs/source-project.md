# Source projects

RECOIL's shipped ZBD files are build outputs. The studio originally built them with a gamegen tool from a source tree (`data\` beside a tool folder of `.gs`/`.gw` scripts), repackaging shared textures, models, animations and resources for every mission. zStudio reconstructs that source tree from shipped files, lets you work on the sources, and builds the game files from them again.

A source project is separate from editing ZBD files directly. Opening a ZBD file still edits that file. In a source project, the sources are what you edit; game files change only when you export them. Exported files must work in the game; they are not byte-identical to the shipped files.

## Reconstruct, check and export

- **Tools → Reconstruct source project…** asks for the shipped data folder (the folder containing `interp.zbd`, `zrdr.zbd` and `m1\`) and a new or empty destination outside it, then optionally opens the project as the workspace root. MCP: `zstudio_source_reconstruct`.
- **Tools → Check source project** builds every game file in memory and reports failures and warnings without writing. MCP: `zstudio_source_export` without `destination`.
- **Tools → Export all ZBD files…** builds every game file into a folder outside the project. MCP: `zstudio_source_export` with `destination`.
- **Tools → Export ZBD file** lists the game files the project can build; choosing one exports only that file. MCP: `zstudio_source_export` with `outputs`, for example `["m1/zrdr.zbd"]`.
- `zstudio_source_status` lists the game files the project can build and the sources of each.

The export commands appear when the open folder is a source project, which is any folder with both `data` and `gamegen` subfolders. The project holds no zStudio files: what it can build is derived from its folders.

When the destination already has some of the selected game files, the GUI asks before replacing them; MCP needs `overwrite`. Other files in the destination are left alone, so a game installation can be the destination. All outputs are built, reopened through the shared readers and staged first; if any output fails, nothing is written. Publication moves replaced files aside and restores them if a later step fails. Unsaved edits to project files must be saved or discarded before exporting, because exports read the files on disk.

Exports read every source file once and check that none changed before anything is written; an edit made while an export runs fails the export rather than mixing two states. Opening another folder cancels a running export.

Reconstruction supports RECOIL data and requires RECOIL evidence (prepared scripts, a version-15 world or a version-28 animation program); MechWarrior 3 folders are refused. A canceled or failed reconstruction removes everything it wrote, so the same folder can be used again. Projects and export folders can never be the protected `zbd_1998`/`zbd_1999` corpora, overlap their input, or pass through links. Text sources larger than 16 MiB are refused before they are decoded.

## Layout

```
<project>\
  gamegen\                     the original tool folder: *.gs and support\*.gw, recovered from interp.zbd
  data\                        the original source tree
    common\zrdr\{enemies,explosns,lighting,vtol,weapons}\*.zrd
    common\multi_bft\zrdr\     common\sounds\*.wav
    mN\zrdr\{aipath,envmodels,vtol,bft,choppers,…}\*.zrd
```

Directory placement is recovered from evidence in the shipped files. Every ZAR member records the temporary file its compiler created in the source directory (for example `D:\battlesportdev\data\m1\zrdr\envmodels\fueE3B0.TMP`), so each resource returns to its original folder, including subfolders that member names do not carry. Sound banks carry no source paths; their WAVs go to `data\common\sounds`, the `SOUND_PATH` set by `sounds.zrd`. The prepared-script index names each script (`support\common.gw`, `m1.gs`) and its modification time, which the reconstructed file keeps.

Exported archives record each member's project path (`data\m1\zrdr\envmodels\fuel.zrd`) where the original compiler recorded its temporary file, so reconstructing exported files restores the same tree.

## What is built

| Game file | Sources | Build |
| --- | --- | --- |
| `zrdr.zbd` | `.zrd` files in every `zrdr` folder under `data\common` | compiled, ordered by source path |
| `mN\zrdr.zbd` | `.zrd` files under `data\mN\zrdr` | compiled, ordered by source path |
| `interp.zbd` | `gamegen\*.gs`, `gamegen\support\*.gw` | tokenized; each script records its file's modification time |
| `soundsh.zbd`, `soundsm.zbd`, `soundsl.zbd` | `data\common\sounds\*.wav` | converted to the HIGH/MED/LOW formats of `sounds.zrd` |
| `gamez.zbd`, `anim.zbd`, texture packs, `image.zbd` | not reconstructed yet | not built |

Archive members are found by name, so two sources with the same file name in one archive are refused. A `.zrd` source may be text or compiled data.

Each sound's source is its best-quality version across the three shipped banks. `sounds.zrd` declares a rate, sample size and channel count for each bank; the declaration is a ceiling. As in every retail bank, each of the three values is the lower of the source's and the declaration's, so a sound is never raised in quality. Conversion mixes channels, reduces sample size and resamples with a windowed-sinc low-pass filter; cue markers, which the engine turns into playback times, move with the samples. A WAV that `sounds.zrd` does not declare goes into every bank unchanged, with a warning. Only 8- and 16-bit PCM can be converted.

On the 1999 retail data, reconstructing, exporting and reconstructing again gives the same tree. The exported archives contain the same members with identical compiled data, `interp.zbd` the same scripts and tokens, `soundsh.zbd` the shipped sounds unchanged, and the medium/low banks the shipped formats with the same frame counts (within two frames) and cues.

Reconstruction lists shipped files whose family it does not reconstruct yet. They are not copied into the project. Textures (PNG made from the best-quality variant of each texture, scaled and converted per pack on export, including packs the engine supports but the game did not ship), GameZ worlds with glTF models, and an `anim.zbd` compiler from `.zrd`/`.zan` sources are the next steps. The 1999 data shares 1.4× of its texture-pack records, 3.8× of its world content (after removing 42.8 MB of preallocated empty slots) and 2× of its animation entries across missions; those families carry most of the redundancy.

## Editing sources

Text `.zrd` files open in the shared ZRD viewer and editor (tree, Properties, `zrd_nodes`, `zrd_edit`) and save as text; comments and formatting you add are replaced by the canonical layout when zStudio saves the file. Importing a text `.zrd` into an archive compiles it. `.gs`/`.gw` scripts open read-only as token text; edit them in any text editor. WAV sources can be replaced with any 8- or 16-bit PCM file.

## Text formats

### zReader resources (`.zrd`)

The original compiler's text syntax did not survive, and the retail engine only reads compiled data, so zStudio defines a lossless syntax. A file lists the children of its root array.

- `( … )` is an array. A key followed by its value array is written on one line: `GRAVITY ( -9.8 )`.
- Integers are decimal 32-bit values: `42`, `-7`.
- Floats always contain a decimal point or exponent (`2.0`, `-9.8`, `1E-45`). NaN payloads and infinities use raw bits: `f32:7FC00001`. Values round-trip bit-exactly, including `-0.0` and subnormals. The engine's `GetInt` rejects floats, so the distinction matters.
- Strings are bare when they start with a letter or `_` and contain only letters, digits, `_`, `.` and `-`; otherwise they are quoted with `\"`, `\\` and `\xNN` escapes. Files are written as ASCII and read as Latin-1.
- `#` starts a comment outside strings.

### Gamegen scripts (`.gs`, `.gw`)

Scripts use the engine's own tokenizer (`CZInterp::TokenizeLine`, retail 0x4C13C0): `#` ends a line, tokens are separated by comma, space, tab or newline, and ASCII whitespace after a separator is skipped. Only a separator directly after another produces an empty token, so zStudio writes empty tokens with commas (`a,,b`; a trailing empty token needs `a,,`).

## Keep sources away from a retail install

Do not place a source tree inside a game folder. The engine rejects `anim.zbd` when a stamped source exists with a different time, loose `support\*.gw`/`*.gs` files override `interp.zbd`, and loose `*_easy`/`*_hard` resources change difficulty selection. Export into the game folder (or a separate folder) instead.
