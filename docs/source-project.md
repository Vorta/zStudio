# Source projects

## Editing sources

Text `.zrd` files open in the shared ZRD viewer and editor (tree, Properties, `zrd_nodes`, `zrd_edit`) and save as text that changes only where the edit does: comments, spacing and the spelling of untouched values stay; new records are written in the canonical layout. Importing a text `.zrd` into an archive compiles it. `.gs`/`.gw` scripts open read-only as token text; edit them in any text editor.

## Text formats

### zReader resources (`.zrd`) and animation definitions (`.zad`)

The original compiler's text syntax did not survive, and the retail engine only reads compiled data, so zStudio defines a lossless syntax, the same for resources and animation definitions. A file lists the children of its root array.

- `( … )` is an array. A key followed by its value array is written on one line: `GRAVITY ( -9.8 )`.
- Integers are decimal 32-bit values: `42`, `-7`.
- Floats always contain a decimal point or exponent (`2.0`, `-9.8`, `1E-45`). NaN payloads and infinities use raw bits: `f32:7FC00001`. Values round-trip bit-exactly, including `-0.0` and subnormals. The engine's `GetInt` rejects floats, so the distinction matters.
- Strings are bare when they start with a letter or `_` and contain only letters, digits, `_`, `.` and `-`; otherwise they are quoted with `\"`, `\\` and `\xNN` escapes. Files are written as ASCII and read as Latin-1.
- `#` starts a comment outside strings.

### Keyframe scripts (`.zan`)

The original scripts were Softimage "SI Animation Script" exports, compiled by a lost tool. Reconstruction writes them again in that format, and exports compile them as that tool did: the shipped keyframes of the 1998 and 1999 releases come back bit for bit (pad floats aside).

```
SI Animation Script
FRAMES: 521
OBJECTS: 1
Warning, file version 3.7 is later than DKit release version 3
Attempt to read: An error may occur...
Frame: 1
Object: copter01
Scaling:     1.000000 1.000000 1.000000
Rotation:    0.304886 -0.111792 -0.811317
Translation: 570.880615 48.131424 168.158310
Warning, file version 3.7 is later than DKit release version 3
Attempt to read: An error may occur...
Frame: 6
...
```

Each frame block gives every object's scaling, rotation (radians about X, Y and Z, applied Z, then Y, then X; values beyond ±π are allowed) and translation; a definition's `NAME` picks the object. `Frame: n` is frame n − 1 at the definition's `SCRIPT_FRAME_RATE`, and frames may go back (the engine plays reversed segments as authored). The header, blank lines, `#` comment lines and the Softimage DKit messages are not keys; a frame repeated immediately adds nothing.

The compiler turns consecutive frames into keyframe segments: a channel is kept in a segment when it changes (a position or scale component by more than 0.00001, a rotation by a half-angle above 0.00001 radians); the first and last segments keep every channel; a segment with no change is left out. Times are frame × the float of 1/rate; rotations go through the engine's own matrix, Euler and quaternion routines in single precision; rates are the change over the segment, with the spin taken from the engine's quaternion log and its fast square root.

Reconstruction writes the frames the compiler kept, with grid frames on the script's step where every object stood still. Keyed values are the six-decimal values the stored floats came from, and rotations the Euler angles that compile to the stored quaternions. A channel that stops before its next key ends at the value its stored rate reaches; between such points the compiler recorded nothing, and the held frames take the next key's value (as surviving fragments of the original texts show). Scripts of the shipped files also get the DKit messages their exporter wrote: before every frame (after every frame in the `m5doexit.zan` of 20 May 1998), with the Softimage version of the script's stamp date (3.5001 before July 1997, 3.7 until 4 May 1998, then 3.71). A script with no stamp date holds only its frames. The exit scripts list their objects as `vtol1`, `lengine`, `rengine`, `cargodoor`, as the fragments show. Keyframes that no SI script reproduces exactly (two poses at one frame, a first segment that does not move every channel, a value no six-decimal number reads back as, a rotation whose angles take too long to find, or a script larger than a project reads) are written in zStudio's keyframe format instead, with a note. A track whose keyframes are not on the frame grid of its `SCRIPT_FRAME_RATE` is not reconstructed at all, with a note. Translations and scalings of negative zero are written `-0.000000`, which reads back as negative zero. Rotation angles are never written as negative zero: keyframes only such an angle reproduces are written in zStudio's format, with a note (none of the shipped ones need it).

Fallback keyframe scripts allow 65,536 keys per object track and 262,144 keys across one file, within the 16 MiB source-text limit. Compilation shares an allowance of 1,048,576 retained fallback keys across its scripts, reserved before allocating each key. Reconstruction checks the writer's limits before appending tracks, then reparses and recompiles every fallback track to verify its meaningful keyframe values exactly. A fallback that cannot be represented is refused before reconstruction publishes the animation sources. These limits do not change SI compilation arithmetic. Both keyframe dialects read lines and tokens incrementally, observing cancellation during long scans. Each source is limited to 16 MiB and each significant token to 4,096 Latin-1 bytes; long comments and blank lines need no retained line arrays. Accepted object names remain complete, and writers enforce the same token limit before emission.

zStudio's keyframe format lists a track per object; exports compile both formats:

```
OBJECT copter01
FRAME 0 POSITION 1429.22 56.43 3107.5 VELOCITY 35.18 -8.75 -45.85 ROTATION 0.929 -0.128 -0.319 0.136 SPIN -0.003 0.130 0.020 SCALE 1 1 1 GROWTH 0 0 0
FRAME 5 POSITION 1440.95 53.52 3092.22 ROTATION 0.941 -0.121 -0.279 0.148
FRAME 2599
```

A key's channels start a segment that runs to the next key; the last key's frame ends the track. Frames count at the definition's `SCRIPT_FRAME_RATE` and may go back. POSITION and SCALE are XYZ, ROTATION a quaternion W X Y Z (not zero). A channel's rate may follow it: VELOCITY and GROWTH per second, SPIN as a rotation vector (half-angle radians per second). Without a rate, the channel moves steadily to its value at the next key that lists it, through any keys in between (their segments carry it on, since the engine holds a channel a segment does not key); with a rate, it moves only until the next key and then holds unless that key lists it. Two keys at the same frame are a cut, which jumps to the second value. A file without OBJECT lines is one track any node may use.

### Gamegen scripts (`.gs`, `.gw`)

Scripts use the engine's own tokenizer (`CZInterp::TokenizeLine`, retail 0x4C13C0): `#` ends a line, tokens are separated by comma, space, tab or newline, and ASCII whitespace after a separator is skipped. Only a separator directly after another produces an empty token, so zStudio writes empty tokens with commas (`a,,b`; a trailing empty token needs `a,,`). A script of more than 1,000,000 lines (as many as the instructions a build runs) or 4,000,000 tokens is refused before its lines are read; the retail scripts have at most a few hundred lines.

Opening and indexing source scripts forwards cancellation through tokenization and instruction-row construction. Replacing the workspace or cancelling the owning operation stops that work as well as preventing an obsolete result from appearing.

## Building worlds

A mission world is built by running its build script with the retail interpreter's command semantics (`WorldAssembler`). `SetModelDirectory`, `SetTextureDirectory` and `RdrSetPath` keep the engine's search-path lists (`RdrSetPath` empties the zReader list first, `zRdrSetPath` 0x48cca0): `zRdrAddSearchPaths` (retail 0x4a5ce0, which `SetTextureDirectory` reaches through `zImageInitMissionResources` 0x46ebd0) adds each `;`-separated folder at the head of the list only when the folder exists and the list holds no entry with exactly the same text (`strcmp`); a listed folder never moves. The gamegen tool's own `SetModelDirectory` is lost; it is taken to use the same zUtil routine. So `weapons.gw` and `bftN.gw`, which name folders `common.gw` has already listed, leave the order as it was: in the original build tree m1 searches its models in `data\m1\models\bft`, `data\common\effects\models`, `data\common\models`, `data\effects\models` and `data\m1\models`, and its textures in `data\common\effects\textures`, `data\common\textures`, `data\effects\textures`, `data\m1\textures\bft` and `data\m1\textures`, for the whole build.

Folders compare by their text as written, as `strcmp` does: another spelling of a listed folder (case, separators) is a new entry at the head, and since Windows finds the folder under any spelling, that folder is searched from there. A folder is listed only when it exists in the project; one that does not (such as `data\effects` in a reconstructed project) would hold nothing to find either.

Finding and reading inputs share one allowance per world build: 131,072 existence probes (each model candidate a `LoadGameGen` tries, each texture folder a textured material tries, each folder a directory command tests, and each script, model and referenced file looked up) and 1 GiB of model, buffer and terrain bytes read (a load reads its files again, declared but unused buffers included). The retail missions make at most 4,710 probes and read at most 12.3 MB (1999 m6). A build past either is refused before the next probe or read, with what to reduce.
