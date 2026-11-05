# Version 200 worlds and animations

zStudio writes each mission's world (`gamez.zbd`) and animations (`anim.zbd`) for one of two targets. For Recoil and reRECOIL v1 it writes world version 15 and animation format 28, the formats of the shipped game. For reRECOIL v2 it can write world version 200 and animation format 200 instead: the same formats with the fields that capped content widened in their places, so that a map can hold more materials, more nodes in a grid cell, more craters, more animations and longer animation lists. This page specifies the two version-200 formats and how they relate to version 15 and format 28.

Recoil and reRECOIL v1 cannot load version 200 or format 200. reRECOIL v2 reads both versions. The number 200 keeps clear of every version Zipper's games used (worlds 13, 15 and 27, animations 28 and 39).

## Choosing the target

The target applies to a whole export. MCP `zstudio_source_export` takes `target`: `recoil` (the default) or `rerecoil-v2`. The file names stay `gamez.zbd` and `anim.zbd`, and every other output (archives, scripts, sounds, texture and image packs, the [32-bit packs](rgba-packs.md)) is the same for both targets. The Export ZBD files window always exports for Recoil.

## The twin rule

A version-200 world is its version-15 twin with wider fields: the same sections in the same order, the same texture directory, material and model pools with the same free slots, the same node table with the same freed and never-used slots, the same records with the same values, and the same stale pointer words. Only the widened fields below differ, and the sections' offsets in the header move with the larger material and node slots. A format-200 animation file is its format-28 twin in the same way: the same entries, lists, sequences and events in the same order.

zStudio writes both versions from one world model and one compiled animation package. It reads either version back into the same model, and a world read from either file writes both files again byte for byte.

The bytes that version 200 widens over hold 0 in every file zStudio writes and in every shipped file: a world's three bytes after its crater limit, a grid cell's byte before its crater count, the animation state's 16-bit word before the entry count and an entry's two bytes after its counts. A world whose grid cell holds another value there cannot be written as version 200.

Content that only version 200 holds has no version-15 twin. Version 15 keeps the limits the original build tool applied:

- a material pool of more than 32,767 slots is stored as 32,767 slots, and a world that uses more materials is refused;
- a crater limit above 255 is stored as 255;
- a grid cell holds at most 32,767 nodes: the build lists the further nodes of the cell in the world's own children, and a world that has more in a cell is refused.

## World, version 200

Every number is little-endian. Offsets are from the start of the record.

| Record | Field | Version 15 | Version 200 |
| --- | --- | --- | --- |
| Header (36 bytes) | version at +4 | 15 | 200 |
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
| Prefix | format marker at +4 | 28 (`0x1c`) | 200 (`0xc8`) |
| State block (60 bytes) | +8 | an unused 16-bit word, then the entry count (16-bit signed, the blank first entry included) | the entry count, 32-bit signed |
| Entry header (308 bytes in format 28, 336 in format 200) | counts from +260 | ten 8-bit counts, then two zero bytes | ten 32-bit signed counts |
| Entry header | list pointers | nine stale words at +272 | the same nine words at +300 |

The ten counts are, in order: the sequences, the tracked nodes, the node references, the light references, the sound references, the sample references, the effect template references, the activation prerequisites, the prerequisites to satisfy (MINIMUM_TO_SATISFY) and the child animations. The lists, the sequences and the event records that follow an entry header are format 28's. An event keeps its 16-bit and 32-bit fields: where it names an element of a list in 16 bits, it names at most the first 32,768 elements.

## Limits

| Content | Version 15, format 28 | Version 200, format 200 |
| --- | --- | --- |
| Material slots | 32,767 | 65,536 (zStudio's table limit) |
| Nodes in a grid cell | 32,767 | no format limit |
| Crater limit | 255 | 2,147,483,647 |
| Corners of a polygon | 3 to 255 | 3 to 255; the game loads polygons of up to 57 corners in both versions |
| Animation entries, the blank first one included | 32,767 | no format limit |
| Sequences of an animation | 255 | 32,767: event 10 names the sequence it wakes in 16 bits, and reRECOIL v2 refuses more |
| References of an animation's table | 254, or 255 for prerequisites and child animations | no format limit; an event names a sample, an effect template, a child animation or (in most event types) a node in 16 bits, so at most 32,767 of those it names |
| MINIMUM_TO_SATISFY | 255 | 2,147,483,647 |

zStudio's own limits apply to both versions: at most 65,536 node, model and material slots, 262,144 node references in a world and files of at most 512 MiB.

## Reading

zStudio reads version 200 and format 200 wherever it reads version 15 and format 28: compiled folders open them for inspection and preview, Compare worlds compares a version-200 world with a version-15 one, and an export reads a destination's version-200 files for its lookup report. Inspection shows a version-200 node slot's `free_next` and `slot_flags`, a polygon's `vertex_count`, a grid cell's `feature_count` and the animation state's `entry_count`. Reconstruction works only from the shipped files, which are version 15 and format 28. zStudio's world model keeps a grid cell's stored crater count in one byte (every world zStudio writes and every shipped world stores 0), so Compare worlds refuses a version-200 world whose grid cell stores more than 255.
