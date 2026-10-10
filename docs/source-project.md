# Source projects

## Editing sources

Text `.zrd` files open in the shared ZRD viewer and editor (tree, Properties, `zrd_nodes`, `zrd_edit`) and save as text that changes only where the edit does: comments, spacing and the spelling of untouched values stay; new records are written in the canonical layout. Importing a text `.zrd` into an archive compiles it. `.gs`/`.gw` scripts open read-only as token text; edit them in any text editor.

## Text formats

### zReader resources (`.zrd`)

The original compiler's text syntax did not survive, and the retail engine only reads compiled data, so zStudio defines a lossless syntax. A file lists the children of its root array.

- `( … )` is an array. A key followed by its value array is written on one line: `GRAVITY ( -9.8 )`.
- Integers are decimal 32-bit values: `42`, `-7`.
- Floats always contain a decimal point or exponent (`2.0`, `-9.8`, `1E-45`). NaN payloads and infinities use raw bits: `f32:7FC00001`. Values round-trip bit-exactly, including `-0.0` and subnormals. The engine's `GetInt` rejects floats, so the distinction matters.
- Strings are bare when they start with a letter or `_` and contain only letters, digits, `_`, `.` and `-`; otherwise they are quoted with `\"`, `\\` and `\xNN` escapes. Files are written as ASCII and read as Latin-1.
- `#` starts a comment outside strings.

### Gamegen scripts (`.gs`, `.gw`)

Scripts use the engine's own tokenizer (`CZInterp::TokenizeLine`, retail 0x4C13C0): `#` ends a line, tokens are separated by comma, space, tab or newline, and ASCII whitespace after a separator is skipped. Only a separator directly after another produces an empty token, so zStudio writes empty tokens with commas (`a,,b`; a trailing empty token needs `a,,`). A script of more than 1,000,000 lines (as many as the instructions a build runs) or 4,000,000 tokens is refused before its lines are read; the retail scripts have at most a few hundred lines.

Opening and indexing source scripts forwards cancellation through tokenization and instruction-row construction. Replacing the workspace or cancelling the owning operation stops that work as well as preventing an obsolete result from appearing.

## Building worlds

A mission world is built by running its build script with the retail interpreter's command semantics (`WorldAssembler`). `SetModelDirectory`, `SetTextureDirectory` and `RdrSetPath` keep the engine's search-path lists: `zRdrAddSearchPaths` (retail 0x4a5ce0, which `SetTextureDirectory` reaches through `zImageInitMissionResources` 0x46ebd0) adds each `;`-separated folder at the head of the list only when the folder exists and the list holds no entry with exactly the same text (`strcmp`); a listed folder never moves. The gamegen tool's own `SetModelDirectory` is lost; it is taken to use the same zUtil routine. So `weapons.gw` and `bftN.gw`, which name folders `common.gw` has already listed, leave the order as it was: in the original build tree m1 searches its models in `data\m1\models\bft`, `data\common\effects\models`, `data\common\models`, `data\effects\models` and `data\m1\models`, and its textures in `data\common\effects\textures`, `data\common\textures`, `data\effects\textures`, `data\m1\textures\bft` and `data\m1\textures`, for the whole build.

Folders compare by their text as written, as `strcmp` does: another spelling of a listed folder (case, separators) is a new entry at the head, and since Windows finds the folder under any spelling, that folder is searched from there. A folder is listed only when it exists in the project; one that does not (such as `data\effects` in a reconstructed project) would hold nothing to find either.

Finding and reading inputs share one allowance per world build: 131,072 existence probes (each model candidate a `LoadGameGen` tries, each texture folder a textured material tries, each folder a directory command tests, and each script, model and referenced file looked up) and 1 GiB of model, buffer and terrain bytes read (a load reads its files again, declared but unused buffers included). The retail missions make at most 4,710 probes and read at most 12.3 MB (1999 m6). A build past either is refused before the next probe or read, with what to reduce.
