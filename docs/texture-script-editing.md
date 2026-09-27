# Texture packs and prepared scripts

These editors use the same accepted snapshots, undo history, ownership and verified saves in the GUI and MCP. They complement the model replacement and ZAR/ZRD editors. They do not execute script commands or establish compatibility of arbitrary edits with the original game.

## Replace or add a texture

1. Open a texture pack from Files, then select a texture in Assets.
2. Choose **Edit → Textures → Replace from PNG…**, or use the asset's **Edit texture** context menu.
3. Select an 8-bit RGB or RGBA, non-interlaced PNG. The file must be at most 16 MiB, each dimension at most 4096, and its dimensions must match the selected texture. Non-power-of-two UI images are supported. The separate model-bundle importer retains its existing 512-pixel limit.
4. The replacement dialog includes **this pack** by default. Optionally check sibling mission packs. A unique matching name selects its record automatically; duplicate names require you to choose a specific record index. Unavailable matches remain excluded. The batch supports at most one texture per pack and 64 packs, with a 512 MiB document limit.
5. Apply. Each selected variant keeps its original dimensions; different sizes use deterministic bilinear resampling with premultiplied alpha. Texture name, index and directory order remain unchanged. Indexed records use a new private palette with deterministic median-cut quantization and no dithering. Existing shared palettes and other records remain intact. Direct records remain direct. RGBA alpha and opaque black are preserved; RGB colors are stored at the format's RGB565 precision. Importing exactly the current decoded RGBA data leaves bytes unchanged.
6. Inspect the image/channels/palette, or open Properties. Ctrl+Z undoes the entire batch. Ctrl+S saves all affected packs; Ctrl+Shift+S chooses a new destination. A batch Save As uses one destination folder and preserves the pack filenames; every destination must be new.

**Add PNG…** adds a new texture to the open pack with a unique name of 1–31 Latin-1 characters. Names that collide after removing the extension are also rejected, matching texture lookup. It does not create material bindings or references. Replacement of an existing texture keeps those bindings because the record identity/name is retained.

The document initiating a batch owns all affected source and save paths until it closes. Other open texture views, including current Save As destinations, follow its pending snapshots and cannot independently edit or save those files. After the owner closes, those views read the files again; Reload establishes a new editing baseline if the saved bytes changed. Conflicting model imports or other resource edits are rejected before accepting a batch.

Current Save As destinations are aliases of the source records in the open batch. Variant discovery excludes these aliases, and explicit import targets must use the original source identities instead of adding a saved copy as another variant.

## Edit a prepared v7 script

1. Open `interp.zbd`, then choose a script from Assets. **Instructions** shows the stored order in a virtualized grid. **Text** retains a bounded read-only reconstruction; export provides the full text. **Bytes** continues to show original source bytes.
2. Double-click an instruction, use its **Properties…** context item, or select the instruction and press Alt+Enter. The separate Properties window stays pinned while you select another script or instruction.
3. Edit the command and arguments. These fields use JSON-quoted strings, so `""`, whitespace, backslashes, quotes and newline escapes have explicit representations. Commit on Enter or an intentional field boundary. Invalid input stays as a draft with validation; Escape restores the accepted value. Navigation, retarget, close and save resolve drafts.
4. Use the argument buttons in Properties to add, remove or reorder arguments. The engine token list holds at most 16 tokens including the command. Each edited token is limited to 16,384 Latin-1 characters without NUL; commands cannot be blank. Unknown command names are retained. Oversized original tokens show read-only prefixes; **Replace instruction…** accepts a complete JSON array of bounded tokens, for example `["Command", "argument", ""]`.
5. Use **Edit → Instructions** or the instruction context menu to insert before the selected row (append with no selection), duplicate, delete or move an instruction. Use **Edit → Scripts** or the Assets context menu to add, rename, duplicate, delete or move whole script entries. Names are 1–119 Latin-1 characters; duplicate stored names are allowed and do not identify a record.
6. Open the script entry's Properties to edit its name or raw unsigned stored file time. New entries default to timestamp zero. Existing timestamps are preserved unless edited. A newer loose source script may override the packed script in the game. Renaming does not rewrite any references.
7. Ctrl+Z/Ctrl+Y use document history. Ctrl+S performs a verified save; Ctrl+Shift+S creates a new file and retargets subsequent saves and Reload.

Script entry and instruction UUIDs survive edits/reordering/undo for the life of the document. Duplicates receive new IDs. Deleted pinned targets display an unavailable notice and return through Undo. Source indices/offsets continue to identify original data. The shared writer preserves unedited instruction blocks, instruction padding, unchanged directory bytes, header fields, gaps, terminators and the package tail; a no-op write is byte-identical.

If an individual script is malformed, Problems identifies its name, authored index and source offset. Valid records remain available in Assets, Text, inspection and export under their original indices, including records after the damaged entry. The entire pack remains read-only: no partial editing session or Save is enabled. Invalid pack headers/directories can still prevent record recovery.

Saved and pending script snapshots feed the existing mission resource/texture-cycle preview consumers. Editing does not run arbitrary commands or implement general gameplay scripting. Preview approximations remain as documented in the animation guide.

## Save protections and limits

Source datasets named `zbd_1998` or `zbd_1999` require Save As. Save As never overwrites an existing file. Working-file saves check the expected disk bytes, stage and flush output, reparse it with the shared format reader, and verify bytes before atomic per-file replacement. File/directory links are rejected for saves. Exports retain their existing source-tree and destination protections.

All batch files pass staging/verification before publication starts. Publication is atomic per file, not across a directory: an external change, access failure or shutdown during publication may leave a partial batch. The save result lists successfully saved paths, errors and remaining unpublished paths; successful targets are tracked individually, remaining changes stay dirty, and retry saves the remaining files. History and source inspection stay available until the document closes.

After a partial Save As, every requested destination remains attached to the batch. Ordinary Save retries only unpublished copies and never falls back to an original source path. Pending copies keep the document dirty even when their bytes match an earlier saved snapshot. A file that appears at a pending destination is an external conflict; it is never overwritten. Invalid directory destinations are rejected during preflight.

Retail references used for the format constraints are `0x46EE69` (external palette flag `0x10`; `0x80` is runtime ownership), `0x4C1160` (prepared instruction blocks/terminators), and `0x4C5740` (loose-file timestamp precedence). The private reconstruction's `zInterp` layout corroborates the 16-pointer token list. These sources are evidence, not build dependencies, and are not distributed.

## MCP equivalents

All names below have the `zstudio_` prefix. Mutations require the document lifetime ID and current revision. Operations return handles read with `operation`.

| Tool | Purpose |
| --- | --- |
| `texture_targets` | Discover/page matching sibling records, dimensions, ambiguity and problems for a selected index. |
| `texture_import` | `path` is the PNG; `index` replaces a record, or omit it and provide `name` to add. Optional `targets: [{path,index}, …]` is the explicit complete batch, including the selected pack. |
| `texture_palette` | Read the current edited or mirrored header and paged RGB565 palette, matching the GUI inspector through replacement, addition, undo/redo and Save As. |
| `script_records` | Page script entries, or provide `script` UUID to page instructions. Token previews are bounded to 16 tokens of 64 characters each, with truncation disclosed. |
| `script_entry_edit` | `add`, `duplicate`, `rename`, `delete`, `move`, `timestamp`; `fileTime` is an unsigned 32-bit value. |
| `script_instruction_edit` | `add`, `set`, `duplicate`, `delete`, `move`; `tokens` contains the literal command and ordered arguments. |
| `script_select` | Select a script and optional instruction by UUID in the visible workspace, keeping Properties pinned. |
| `script_properties` | `fields`, `open`, `edit`, or `invoke`; generated argument actions use `propertyAction`. Field edits use the GUI's quoted editor text. |
| `drafts`, `resolve_drafts`, `properties_state` | Read/resolve the same visible drafts and pinned fields/actions. |
| `undo_redo`, `save_document` | Shared history/save. A single-file Save As uses `destination`; a batch uses `destinations` mapping every source path to a distinct new destination. Omit both to save working files. Results disclose partial saves. |

Discovery remains windowless and uses the generated shared command catalog. Texture/script operations never enable MCP or capture input. GUI dynamic menus, grid targeting and generated property actions are mapped in `mcp-capabilities.json` and exercised through real named-pipe tests.
