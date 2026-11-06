# 32-bit texture and image packs

Every export for reRECOIL v2 (the Export ZBD files window's **reRECOIL v2** game, or MCP `zstudio_source_export` with target `v2`) writes 32-bit copies of the texture and image packs, which reRECOIL v2 uses in place of RECOIL's 16-bit textures. They hold each texture's pixels as RGBA8, exactly as zStudio decodes its PNG, beside the 16-bit packs, which stay as they are. This page specifies the files: what they hold, their layout and how their pixels relate to the PNG sources.

The retail game never reads them: their names are none of those it opens (`rtexture<N>`, `texture<N>`, `texturemax`, `texture`, `image`, `rimage`).

## Files

| File | Holds |
| --- | --- |
| `mN\rgbatexture<N>.zbd` | every texture of the mission's 16-bit packs, for N MB of texture memory |
| `rgbaimage.zbd` | the interface images of `image.zbd`, beside it |

### Texture tiers

A mission's texture packs are tiered by texture memory, as its `rtexture<N>` packs are: N is the pack's budget in MB (1 MB = 1,048,576 bytes) of texels at four bytes a texel, written in decimal without leading zeros, and the game opens the largest N at or below the texture memory it has.

- The tiers are the powers of two from 64: 64, 128, 256, 512.
- Each mission gets its **full-size tier**: the smallest N whose budget holds every texture at its authored size (the size of its PNG). It is the mission's largest tier.
- Each smaller tier from 64 MB is written too. It holds every texture, reducing them to fit its budget (see [Reduced textures](#reduced-textures)). A smaller tier exists only below the full-size one, so each reduces at least one texture and none is a copy of another.
- A mission whose textures fit in 64 MB has `rgbatexture64.zbd` alone.
- zStudio writes pack files of at most 512 MiB, header and record table included, so it refuses to export a mission whose full-size tier would be larger, naming its largest textures.

Each pack holds every texture of the mission's 16-bit packs under the same names, in the same record order and with the same edge modes: the textures of the mission's texture folders and those its world brings from other folders, names with numbered mipmap endings (`_1`, `_2`) included.

### Interface images

A project that builds `image.zbd` also builds `rgbaimage.zbd` (for reRECOIL v2: an export for Recoil and reRECOIL v1 leaves it out): the same images, under the same names and in the same order, each at its authored size (no tiers, no budget). A name `image.zbd` holds more than once (an image in two folders) is stored once, as its first record, the one the game finds; the export warns about the others.

## Layout

Little-endian throughout; no padding, alignment, reserved or unknown fields.

### Header (24 bytes)

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 8 | magic: `RCLRGBA` followed by a NUL byte (`52 43 4C 52 47 42 41 00`) |
| 8 | 4 | version: 1 |
| 12 | 4 | kind: 1 for a mission texture pack, 2 for the interface image pack |
| 16 | 4 | budget: N in MB for a texture pack (the N of its name), 0 for the image pack |
| 20 | 4 | record count |

### Records

The record table follows the header, one record after another, in record order:

| Size | Field |
| --- | --- |
| 2 | name length in bytes: 1–255 |
| length | name: Latin-1, no NUL; unique within the pack ignoring case (zStudio writes names in lower case) |
| 4 | width: 1–16,384, the width stored |
| 4 | height: 1–16,384, the height stored |
| 4 | addressing: bit 0 clamps U, bit 1 clamps V (repeats when clear); other bits 0; 0 for interface images |
| 8 | data offset: where the record's pixels start, from the start of the file |
| 8 | data size: width × height × 4 |

The addressing word is the one the 16-bit image header stores: the edge mode of the glTF samplers that use the texture, or for a texture only a texture cycle shows, of the material it is shown on.

### Pixels

Each record's pixels follow the record table in record order: the first record's start straight after the table, each next one straight after the previous one's, and the file ends at the last pixel. A record's pixels are its rows from top to bottom, each from left to right, four bytes a texel: red, green, blue, alpha. Alpha is straight (colour not multiplied by alpha).

No transparency class, colour key or palette is stored. They follow from the pixels, as zStudio classifies textures: opaque when every alpha is 255, colour-keyed when alpha is only 0 or 255, otherwise with an alpha plane.

### What a reader checks

A file is a valid pack only when all of these hold:

- the magic, version 1 and kind 1 or 2; a texture pack's budget is at least 1, the image pack's 0;
- the record table fits the file, with every name length 1–255;
- every name is Latin-1 without NUL and unique ignoring case;
- every width and height is 1–16,384 and every data size their product times four;
- addressing sets no bit but 0 and 1, and none in the image pack;
- every record's data offset is where the previous record's pixels end (the first's, where the table ends), and the file ends at the last record's last pixel.

zStudio refuses to export a pack that breaks any of them: every export is read back through the same checks before anything is written. Offsets and sizes are 64-bit, so the format holds files past 4 GiB; zStudio writes pack files of at most 512 MiB.

## Pixel values

### At authored size

Every texture of a full-size tier, every texture a smaller tier did not reduce, and every interface image holds exactly the RGBA8 that zStudio decodes from its master PNG, including the colour of fully transparent pixels, with no quantizing, palette or dithering. The decoding:

- **Colour types:** greyscale (1, 2, 4, 8 or 16 bits), RGB (8 or 16 bits a channel), palette (1, 2, 4 or 8 bits an index), greyscale with alpha and RGBA (8 or 16 bits a channel), interlaced (Adam7) or not.
- **Greyscale** sets red, green and blue to the grey value; below 8 bits a value v of d bits becomes v × 255 / (2^d − 1) (1 bit: 0 or 255; 2 bits: × 85; 4 bits: × 17).
- **16-bit channels** keep their high byte (v >> 8), alpha included: truncated, not rounded.
- **Palette** images take each entry's colour; alpha comes from `tRNS` for the entries it lists and is 255 for the others.
- **`tRNS` in greyscale and RGB images** names one transparent value, compared at the image's bit depth (the value's low d bits for d-bit samples, all 16 bits in a 16-bit image): a pixel whose stored samples equal it gets alpha 0 and keeps its decoded colour; every other pixel gets alpha 255.
- **Ignored:** `gAMA`, `cHRM`, `sRGB`, `iCCP` and every other ancillary chunk; the stored values are used as they are, with no gamma or colour-space conversion.
- **Refused** (the export fails and names the file): PNG files decoders could read differently, such as chunks out of the specification's order or repeated, a `tRNS` of the wrong length, or image data that does not fill the image exactly. zStudio decodes PNG sources up to 4,096 × 4,096.

### Reduced textures

A tier below the full-size one reduces textures in rounds until they fit its budget:

- Each round takes every texture whose longer side is still longer than 8 texels, those with the most texels (at the round's start) first, record order breaking ties.
- Each such texture has its longer side halved, rounding down but keeping at least 8 texels; a square texture halves its width. Its other side stays.
- The reduction stops at the first texture that brings the textures within the budget.

Sides need not be powers of two, and the aspect ratio is not limited. The stored width and height are the reduced ones. (The 16-bit tiers halve in rounds too, but keep powers of two and their own limits.)

A reduced texture is resampled from its master: an area average on premultiplied alpha, then each channel rounded to the nearest 8-bit value (halves to even), with no dithering. A texel all of whose source texels are fully transparent has colour 0; a texel whose averaged alpha rounds to 0 can keep a colour. The export lists, for each such pack, the textures it reduced with their authored and stored sizes.

## Determinism

The same sources export to byte-identical packs: records in a fixed order, sizes from the sources, and resampling that does not depend on how the work is divided.
