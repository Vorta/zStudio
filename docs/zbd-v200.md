# Version 200 worlds and animations

zStudio writes each mission's world (`gamez.zbd`) and animations (`anim.zbd`) for one of two targets. For Recoil and reRECOIL v1 it writes world version 15 and animation format 28, the formats of the shipped game. For reRECOIL v2 it can write world version 200 and animation format 200 instead: the same formats with the fields that capped content widened in their places, so that a map can hold more materials, more nodes in a grid cell, more craters, more animations and longer animation lists. This page specifies the two version-200 formats and how they relate to version 15 and format 28, and when an AI network is written as version 200.

Recoil and reRECOIL v1 cannot load version 200 or format 200. reRECOIL v2 reads both versions. The number 200 keeps clear of every version Zipper's games used (worlds 13, 15 and 27, animations 26, 27, 28 and 39).

## Choosing the target

An export is for one game, its target: the **Export for** switch of the Export ZBD files window (**Recoil and reRECOIL v1**, **reRECOIL v2**), and MCP `zstudio_source_export`'s required `target`. The target chooses the formats of the worlds and animations, and also the packs and sound banks:

| Target | Game | World, animations | Texture and image packs | Sound banks |
| --- | --- | --- | --- | --- |
| `v1` | Recoil 1.x and reRECOIL v1 | version 15, format 28 | the 16-bit packs: Direct3D (`rtexture<N>.zbd`, `image.zbd`) and software (`texture<N>.zbd`, `texturemax.zbd`) | `soundsh.zbd`, `soundsm.zbd`, `soundsl.zbd` |
| `v2` | reRECOIL v2 | version 200, format 200 | the [32-bit packs](rgba-packs.md) (`rgbatexture<N>.zbd`, `rgbaimage.zbd`) and the 16-bit Direct3D packs, which reRECOIL v2 draws until its new renderer; no software packs, whose paletted images it cannot use | `soundsh.zbd`, the only bank it reads |

The file names stay `gamez.zbd` and `anim.zbd`, and the resource archives (`zrdr.zbd`) and `interp.zbd` are the same for both targets, except that only target `v2` writes an AI network past Recoil's limits ([AI networks](#ai-networks-version-200)). `zstudio_source_status` lists the targets of each output. Without `outputs` an export builds every file of its target; a listed file of the other target is refused, naming the target that builds it. zStudio's code callers default to `v1`. The window shows only the files of the game it exports for, keeps each game's choices and destination apart, opens on the game it showed last (v1 for a new profile), and **Check source project** checks that game.

After reRECOIL v2's phase 7 (its new renderer) reRECOIL v2 needs no 16-bit pack. The zStudio release that follows it removes the 16-bit Direct3D packs (`rtexture<N>.zbd`, `image.zbd`) and the texture dithering setting from target `v2`, which then writes the version-200 worlds and animations, the 32-bit packs, the resource archives, `interp.zbd` and `soundsh.zbd`; texture dithering then applies to target `v1` only.

## The twin rule

A version-200 world is its version-15 twin with wider fields: the same sections in the same order, the same texture directory, material and model pools with the same free slots, the same node table with the same freed and never-used slots, the same records with the same values, and the same stale pointer words. Only the widened fields below differ, and the file offsets that follow from the larger material slots: the header's model and node table offsets and each live display instance's data offset (the last word of its 88-byte slot, which the crater code seeks). A format-200 animation file is its format-28 twin in the same way: the same entries, lists, sequences and events in the same order.

zStudio writes both world versions from one world model, reads either version back into the same model, and a world read from either file writes both files again byte for byte. It compiles both animation formats from the same definitions; a compiled or read animation package keeps its own format.

The bytes that version 200 widens over hold 0 in every file zStudio writes and in every shipped file: a world's three bytes after its crater limit, a grid cell's byte before its crater count, the animation state's 16-bit word before the entry count and an entry's two bytes after its counts. A world whose grid cell holds another value there cannot be written as version 200.

Content that only version 200 holds has no version-15 twin. Version 15 keeps the limits the original build tool applied:

- a material pool of more than 32,767 slots is stored as 32,767 slots, and a world that uses more materials is refused;
- a crater limit above 255 is stored as 255;
- a grid cell holds at most 32,767 nodes: the build lists the further nodes of the cell in the world's own children, and a world that has more in a cell is refused.

## World, version 200

Every number is little-endian. Offsets are from the start of the record. The version-15 records are those zStudio's reader decodes (`src/zStudio.Core/Formats/field-layouts.json`) and [engine evidence](agents/engine-evidence.md#gamez-world-format) describes; the pool headers and the order of the class data and its lists after the node table are those `GameZWriter.Write` writes (`src/zStudio.Core/Worlds/GameZWriter.cs`).

| Record | Field | Version 15 | Version 200 |
| --- | --- | --- | --- |
| Header (36 bytes: nine 32-bit words: magic, version, texture count, the texture directory's, material pool's and model pool's offsets, node table capacity, the node free list's head, the node table's offset) | version at +4 | 15 | 200 |
| Material slot (44 bytes in version 15, 48 in version 200) | the material's previous and next slot in its list | 16-bit signed at +40 and +42 | 32-bit signed at +40 and +44 |
| Polygon (28 bytes) | flags at +0 | the low byte holds the corner count | the low byte is 0 |
| Polygon | +8 | stale vertex index pointer (zStudio writes 1) | corner count, 32-bit |
| Node slot (196 bytes in version 15, 200 in version 200) | free list | a 32-bit tag at +192: the slot's flags in the top byte, the next free slot (24 bits, all ones for none) below, or for a live node its class data's offset | the next free slot at +192, 32-bit signed: -1 for none and for every slot that is not on the free list (live nodes included); the slot's flags at +196, 32-bit, with bits 0 to 23 clear |
| World class data (172 bytes) | crater limit (partitionMaxDecFeatureCount) at +76 | 8-bit, then three zero bytes | 32-bit signed, 0 or more |
| Grid cell (64 bytes) | +56 to +63 | an unknown byte (0), the crater count (8-bit), the node count (16-bit), a stale node list pointer (0) | the crater count (32-bit signed) at +56, the node count (32-bit signed) at +60 |

The free list is the one the game walks: from the header's free-list head through each listed slot's next free slot to -1. The other fields, the zone sets (three zones a set), the names and the class data keep their version-15 widths and places.

## Animations, format 200

| Record | Field | Format 28 | Format 200 |
| --- | --- | --- | --- |
| Prefix (12 bytes, at the start) | format marker at +4 | 28 (`0x1c`) | 200 (`0xc8`) |
| State block (60 bytes, after the prefix and its source-file stamps, 84 bytes each, counted at +8 of the prefix) | +8 | an unused 16-bit word, then the entry count (16-bit signed, the blank first entry included) | the entry count, 32-bit signed |
| Entry header (308 bytes in format 28, 336 in format 200) | counts from +260 | ten 8-bit counts, then two zero bytes | ten 32-bit signed counts |
| Entry header | list pointers | nine stale words at +272 | the same nine words at +300 |

An entry header is followed by its reference tables in this order, each record of a fixed size: tracked nodes (96 bytes), node references (40), light references (44), sound references (44), sample references (36), effect template references (36), activation prerequisites (48) and child animations (72); then the reset sequence and the entry's sequences, each a 64-byte header with the length of its events at +60, then its event records. zStudio's reader decodes the format-28 records with the `ANIM_*` layouts of `src/zStudio.Core/Formats/field-layouts.json`.

The ten counts are, in order: the sequences, the tracked nodes, the node references, the light references, the sound references, the sample references, the effect template references, the activation prerequisites, the prerequisites to satisfy (MINIMUM_TO_SATISFY) and the child animations. The lists, the sequences and the event records that follow an entry header are format 28's. An event keeps its 16-bit and 32-bit fields: where it names an element of a list in 16 bits, it names only indices up to 32,767 (every table but the prerequisites and the child animations starts with a blank element 0). Events 19 and 24 keep the entry they call in a 16-bit field as well; zStudio stores 0 there (the game finds the entry by name), and reRECOIL v2 keeps an entry there only when its index fits.

## AI networks, version 200

An AI network is a resource-archive member `net_<n>.zrd`: a zReader file of tags and values ([engine evidence](agents/engine-evidence.md#net_nnzrd)). Recoil and reRECOIL v1 read version 105 (or a file without a version tag): the members `net_01.zrd` to `net_99.zrd`, the nodes `node_00` to `node_98` and three link slots a node. reRECOIL v2 reads version 105 and version 200 alike, with a network number of two or more digits up to 2,147,483,647, any node number and any number of link slots. Version 200 changes only the version tag: it marks a network that Recoil would read wrongly, and Recoil drops a network of a version other than 105.

zStudio writes the version a network needs:

- Within Recoil's limits, for both targets: version 105 (a file without a version tag keeps none), with three link slots a node: empty slots (`-1`) are added to fewer, and empty slots past the third are removed. The shipped networks build byte for byte as before.
- Past them (a network number above 99 or a name with more than two digits, a node tag such as `node_99`, `node_150` or `node_5`, a link in the fourth slot or later): version 200, with the link slots as authored, for target `v2` only. Target `v1` refuses the resource archive and names the network, as it refuses a world that version 15 cannot store.

A network file of another version (MechWarrior 3's 106) is written as it is.

## Limits

| Content | Version 15, format 28 | Version 200, format 200 |
| --- | --- | --- |
| Material slots | 32,767 | 65,536 (zStudio's table limit) |
| Nodes in a grid cell | 32,767 | no format limit |
| Crater limit | 255 | 2,147,483,647 |
| Corners of a polygon | 3 to 255 | 3 to 255 (reRECOIL v2 loads polygons of at most 57 corners) |
| Animation entries, the blank first one included | 32,767 | no format limit |
| Sequences of an animation | 255 | 32,767: event 10 names the sequence it wakes in 16 bits, and reRECOIL v2 refuses more |
| References of an animation's table | 254, or 255 for prerequisites and child animations | no format limit; an event names a sample, an effect template, a child animation or (in most event types) a node in a 16-bit field, which holds indices up to 32,767 |
| MINIMUM_TO_SATISFY | 255 | 2,147,483,647 |

zStudio's own limits apply to both versions: at most 65,536 node and model slots (and 65,536 material slots in version 200), 262,144 node references in a world and files of at most 512 MiB.

The source map editor previews and checks a map's grid cells and animations as Recoil stores them: it lists a grid cell's nodes past 32,767 in the world's own children, and it refuses animations that only format 200 holds (more than 255 sequences, more than 254 references in a table (255 for prerequisites and child animations), more than 32,766 animations, a MINIMUM_TO_SATISFY above 255): their preview is unavailable and edits that check the mission's animations, such as a model rename, are refused, while an export for target v2 still builds them. It keeps a world's material pool and crater limit as the build script sets them and previews a pool only version 200 stores; version 15 clamps both when it is written.

An export for target v1 that writes a mission's `gamez.zbd` or `anim.zbd` notes the other of the two when the destination keeps it as version 200 or format 200 (or cannot read it): Recoil and reRECOIL v1 cannot load that mission. Before the export, the Export ZBD files window cautions under its destination about such a version-200 file, and for reRECOIL v2 about a folder that is also the one it exports to for Recoil and reRECOIL v1 while the export writes worlds or animations.

## Reading

zStudio reads version 200 and format 200 wherever it reads version 15 and format 28: compiled folders open them for inspection and preview, Compare worlds compares a version-200 world with a version-15 one, and an export reads a destination's version-200 files for its lookup report. Inspection shows a version-200 node slot's `free_next` and `slot_flags`, a polygon's `vertex_count`, a grid cell's `feature_count` and the animation state's `entry_count`. Reconstruction works only from the shipped files, which are version 15 and format 28. zStudio's world model keeps a grid cell's stored crater count in one byte (every world zStudio writes and every shipped world stores 0), so Compare worlds refuses a version-200 world whose grid cell stores more than 255.
