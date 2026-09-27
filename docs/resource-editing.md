# ZAR archive and ZRD editing

ZAR containers and typed ZRD resources now have shared GUI/MCP editors. These are the first two priorities toward a complete ZBD editor. They do not make every other ZBD format editable.

## Archive members

1. Open a ZAR archive, such as a mission's `zrdr.zbd`, from **Files**. Its members appear in **Assets** in directory order.
2. Right-click a member and open **Edit archive member**, or use **Edit → Archive members**. The menu can add a file, create an empty ZRD array, replace a member, rename, duplicate, delete, or move a member up/down.
3. Choose a file for Add/Replace. Replacement can change the payload length. Known ZRD/WAV inputs are validated before accepting the edit; other payloads remain opaque. The resulting container must still be recognized unambiguously by the shared format reader.
4. Names are 1–63 Latin-1 characters without NUL. Duplicate names are allowed. The editor tracks the member's identity rather than using its name or current row number.
   A complete bounded structural decode identifies embedded ZRD data even after its `.zrd` extension is removed. Renaming retains the same node identities, and the typed GUI/MCP editor remains available after save/reopen. A plausible first word alone does not classify opaque bytes as ZRD.
5. Use the document's **Undo/Redo** controls to reverse accepted changes. **File → Export selected** exports the edited payload; ZRD export also includes inspection JSON.
6. **Save** verifies and atomically replaces the working file. **Save As** requires a new file and makes it the destination for subsequent saves. Reference datasets `zbd_1998` and `zbd_1999` require a copy elsewhere. The Properties window and MCP state identify the save destination; the open document retains its original source context.

Unchanged payload bytes, original payload padding, and untouched directory metadata are retained. Changed/new payloads are appended before the rebuilt directory. Deleting a member removes its directory entry but does not compact the original payload region. A no-op serialization is byte-identical. Archives remain subject to the 512 MiB document limit.

## Typed ZRD data

1. Select a ZRD member in Assets, or open a standalone `.zrd` file. **Data** shows the actual typed root and ordered array children, labelled with their child indices.
2. Right-click a node and choose **Properties**, or select it and press **Alt+Enter**. This pins Properties to that node. Later selection changes do not retarget the window.
3. Edit an integer, float or string in Properties. **Enter** or an intentional field boundary applies one undoable change; **Escape** restores the committed value. Invalid input stays in place with an inline diagnostic. Navigation that retargets Properties, saving, and closing explicitly resolve pending input.
4. Integers are signed 32-bit values. Floats accept finite invariant decimal text or explicit `0xXXXXXXXX` bits, preserving nonfinite values and signed zero. Strings use JSON quotes/escapes and Latin-1 characters, for example `"text"`, `"line\nnext"`, or `"embedded\u0000byte"`.
5. Use the node's context menu or **Edit → ZRD nodes** to add children, change type, duplicate, delete, reorder, or **Move to array**. Moving selects the destination in a tree and uses a zero-based final child index measured after removing the moved node. Cycles are rejected. The root can change type but cannot be deleted or duplicated.
6. Changing type replaces the old scalar/children. Undo restores the previous data and node identities. Properties shows a deleted-record notice if its pinned node/member is removed, and reconnects when Undo restores it.
7. Save the owning archive or standalone ZRD with the normal document controls. Embedded resource changes share the archive's undo history and save transaction.

ZRD is generic typed data: arrays are ordered containers, not inferred name/value dictionaries. The editor does not automatically repair script references, resource names or game-specific record shapes after structural changes. Successful parse/save verification establishes format integrity, not original-game compatibility.

Properties values longer than 16,384 displayed characters show a read-only prefix, with no editable truncated draft. Use **Change type** with the existing type to replace the complete value, or export the resource for external editing. JSON inspection is limited to 1,024 nodes, 4,096 characters per string and 65,536 string characters in total; truncation markers retain the stored counts. Exports retain complete data. MCP `resource_properties` and `inspect_asset` share these limits; `zrd_edit` can explicitly replace the complete value within the normal request limit. Node paging formats only the returned page when no query is supplied, and filtering runs off the UI thread with cancellation and revision checks.

**Reload / F5** uses the current Save As destination. It retains the existing document if the destination is already open, unavailable, malformed or changed during reload.

## Shared ownership and previews

The first accepted archive edit or pickup move claims its source archives in the visible workspace. That ownership lasts through undo history until the owning document closes, including after saving. Another editor may inspect the data but cannot concurrently mutate the same archive. Save/discard and close the first owner before switching editing paths.

Accepted resource snapshots feed the existing dependency resolver. Dependent mission and animation contexts are invalidated; the visible dependent preview refreshes using its retained camera/playhead. Audio refresh uses the existing asynchronous preparation and warm output. Clean cached pickup sessions are rebuilt when their resource snapshot changes. Original-source **Bytes** and the original half of MCP inspection remain separate from edited data, including after member reordering. New members have no original source byte range.

Preparation and serialization run off the UI thread. Publication checks document lifetime/revision; canceled or superseded preparation cannot publish. History is bounded to 128 snapshots and a 256 MiB serialized-snapshot budget per direction, retaining at least the newest undo step. Saved bytes are reparsed and checked before replacement, and content hashes detect external changes. Save As can recover accepted edits when the old destination has changed externally.

## MCP

All tools use the existing opted-in, same-user workspace connection. Discovery stays windowless. Operations return handles read through `zstudio_operation`.

| Tool | Purpose |
| --- | --- |
| `zstudio_archive_members` | Page edited directory order with member UUID, current index, original index, name and length. |
| `zstudio_archive_edit` | Add/add_zrd/replace/rename/duplicate/delete/move with the current document revision. |
| `zstudio_zrd_nodes` | Read the root or page a node's immediate children; returns stable node IDs, types, float bits and bounded editor text. |
| `zstudio_zrd_edit` | Set/type/add/duplicate/delete/move using member/node UUIDs and current revision. |
| `zstudio_resource_select` | Select a member and optional typed node in the visible viewer. |
| `zstudio_resource_properties` | Read generated fields, open pinned Properties, or edit a current field. |

`zrd_nodes` bounds displayed values to 4096 characters and marks `valueTruncated`; export provides complete data. String prefixes are bounded before JSON escaping, so listing a large stored string does not allocate its complete escaped value. The Data tree uses the same formatter with a 200-character label limit. Truncated previews can end inside a JSON escape and are not complete replacement values. String values supplied for editing are JSON-quoted editor text. A move supplies a destination `parent` node UUID and final `position`. New nodes/members are discovered by listing after the operation. Stable UUIDs belong to one document lifetime; reopening generates new identities.

Existing `drafts`/`resolve_drafts`, `undo_redo`, `save_document`, `inspect_asset`, `source_bytes`, `export`, and close/reload guards are shared. Save ZAR/ZRD in place by omitting `destination`; provide a new `destination` for Save As. `resource_properties` with `action="edit"` requires `revision`, `field`, and `value` from the current fields listing. Pending GUI drafts are never discarded by an unrelated automation edit.
