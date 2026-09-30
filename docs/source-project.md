# Source projects

RECOIL's shipped ZBD files are build outputs. The studio originally built them with a gamegen tool from a source tree (`data\` beside a tool folder of `.gs`/`.gw` scripts), repackaging shared textures, models, animations and resources for every mission. zStudio reconstructs that source tree from shipped files, lets you work on the sources, and packs the game files back.

## Reconstruct and pack

- **Tools → Reconstruct source project…** asks for the shipped data folder (the folder containing `interp.zbd`, `zrdr.zbd` and `m1\`) and a new or empty destination outside it, then optionally opens the project as the workspace root. MCP: `zstudio_source_reconstruct`.
- **Tools → Verify source project** rebuilds every game file in memory and compares it with the shipped file. MCP: `zstudio_source_pack` without `destination`.
- **Tools → Pack ZBD files…** writes every game file into a new, empty or previously packed folder outside the project. All outputs are staged and reopened through the shared readers first; if any fails, nothing is written. MCP: `zstudio_source_pack` with `destination`.
- `zstudio_source_status` summarizes the open project.

Reconstruction supports RECOIL data; MechWarrior 3 folders are refused. It verifies that every output packs back byte-identically before it is accepted; an output that would not is kept verbatim and reported as a note. On the 1999 retail data (58 files) every output reconstructs and packs back exactly. Packing reports each output as `identical`, `changed` (with the edited sources) or `failed` (with the error). Unsaved edits to project files must be saved or discarded before packing, because packing reads the files on disk.

Reconstruction requires RECOIL evidence (prepared scripts, a version-15 world or a version-28 animation program). A canceled or failed reconstruction removes everything it wrote, so the same folder can be used again.

Packing reads every project file once and checks that none changed before anything is written; an edit made while a pack runs fails the pack rather than mixing two states. Publication moves replaced outputs aside and restores them if any later step fails, so a pack folder always holds one complete pack. Opening another folder cancels a running pack.

Projects and pack folders can never be the protected `zbd_1998`/`zbd_1999` corpora, overlap their input, or pass through links, including links inside a previously packed folder. A pack folder is marked with `zstudio-pack.json` and the project's identity: only the same project can pack into it again, and re-packing replaces only outputs this project produces. Text sources larger than 16 MiB are refused before they are decoded.

## Layout

```
<project>\
  zstudio-project.json         manifest: origin fingerprint and one entry per shipped file
  gamegen\                     the original tool folder: *.gs and support\*.gw, recovered from interp.zbd
  data\                        the original source tree
    common\zrdr\{enemies,explosns,lighting,vtol,weapons}\*.zrd
    common\multi_bft\zrdr\     common\sounds\*.wav
    mN\zrdr\{aipath,envmodels,vtol,bft,choppers,…}\*.zrd
  .zstudio\                    what sources cannot express (not original data)
    layouts\<output>.json      record order, residue bytes and original timestamps
    cache\                     stored variants that zStudio cannot regenerate yet
    passthrough\<output>       shipped files whose family is not reconstructed yet
```

Directory placement is recovered from evidence in the shipped files. Every ZAR member records the temporary file its compiler created in the source directory (for example `D:\battlesportdev\data\m1\zrdr\envmodels\fueE3B0.TMP`), so each resource returns to its original folder, including subfolders that member names do not carry. Sound banks carry no source paths; their WAVs go to `data\common\sounds`, the `SOUND_PATH` set by `sounds.zrd`. The prepared-script index names each script (`support\common.gw`, `m1.gs`) and its modification time, which the reconstructed file keeps.

## What is reconstructed

| Shipped files | Sources | Packing |
| --- | --- | --- |
| `zrdr.zbd`, `mN\zrdr.zbd` | text `.zrd` files | compiled from text |
| `interp.zbd` | `gamegen\*.gs`, `gamegen\support\*.gw` | tokenized from text |
| `soundsh/m/l.zbd` | `data\common\sounds\*.wav` from the high-quality bank | medium/low variants are stored and used while their source is unchanged |
| `gamez.zbd`, `anim.zbd`, texture packs, `image.zbd` | not yet (kept verbatim) | copied |

GameZ worlds with OpenFlight `.flt` models, `anim.zbd` with its `.zrd`/`.zan` sources, and texture packs/`image.zbd` with `.tif` images are the next reconstruction steps. The 1999 data shares 1.4× of its texture-pack records, 3.8× of its world content (after removing 42.8 MB of preallocated empty slots) and 2× of its animation entries across missions; those families carry most of the redundancy.

## Editing sources

Text `.zrd` files open in the shared ZRD viewer and editor (tree, Properties, `zrd_nodes`, `zrd_edit`) and save as text; comments and formatting you add are replaced by the canonical layout when zStudio saves the file. Importing a text `.zrd` into an archive compiles it. `.gs`/`.gw` scripts open read-only as token text; edit them in any text editor. An edited script is re-encoded and takes its file's modification time; an unedited one keeps its original padding and time.

Stored variants are tied to their source's content: a medium/low sound bank entry derived from an edited WAV cannot be regenerated yet, so packing fails with a message naming the source. Record metadata that the engine ignores (temporary compile paths, DOS times, archive file times) is kept from the original build.

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

Do not place a reconstructed tree beside a retail game folder. The engine rejects `anim.zbd` when a stamped source exists with a different time, loose `support\*.gw`/`*.gs` files override `interp.zbd`, and loose `*_easy`/`*_hard` resources change difficulty selection. Pack into a separate folder and copy the packed files into a game installation.
