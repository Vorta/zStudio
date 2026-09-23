using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Formats;

internal sealed class TextureReader : IZbdFormatReader
{
    public FormatFamily Family => FormatFamily.TexturePack;
    public void Read(ZbdDocument doc, CancellationToken token)
    {
        BinaryCursor c = new(doc.Bytes); uint unknown = c.U32(), format = c.U32(), pages = c.U32(), records = c.U32(); c.Skip(8);
        int count = c.Count(records, 40); int pageOffset = checked(24 + count * 40);
        BinaryCursor.CheckRange(doc.Bytes.Length, pageOffset, pages * 512L);
        doc.Metadata["header_raw"] = Convert.ToHexStringLower(doc.Bytes.Span[..24]); doc.Metadata["palette_pages"] = pages;
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            string name = c.String(32); uint offset = c.U32(); int page = c.I32();
            try
            {
                if (offset < pageOffset + pages * 512L) throw new InvalidDataException("Image overlaps the pack tables.");
                BinaryCursor image = new(doc.Slice(offset, 16), offset);
                byte flags = image.U8(); image.Skip(3); int width = image.U16(), height = image.U16(); uint headerFlags = image.U32(); int paletteCount = image.U16(); ushort unknownFooter = image.U16();
                if (width == 0 || height == 0 || (long)width * height > 64 * 1024 * 1024) throw new InvalidDataException("Invalid or excessive texture dimensions.");
                int pixelCount = checked(width * height), bpp = paletteCount > 0 ? 1 : (flags & 1) != 0 ? 2 : 1;
                int pixels = checked((int)offset + 16), pixelBytes = checked(pixelCount * bpp), alpha = (flags & 8) != 0 ? pixels + pixelBytes : -1;
                int end = checked(pixels + pixelBytes + (alpha >= 0 ? pixelCount : 0));
                int paletteOffset = -1, paletteBytes = 0;
                if (paletteCount > 0)
                {
                    if ((flags & 128) != 0) { paletteOffset = end; paletteBytes = checked(paletteCount * 2); end = checked(end + paletteBytes); }
                    else if (page >= 0 && page < pages) { paletteOffset = checked(pageOffset + page * 512); paletteBytes = 512; }
                    else throw new InvalidDataException($"Missing palette page {page}.");
                }
                BinaryCursor.CheckRange(doc.Bytes.Length, offset, end - offset);
                TextureInfo info = new(width, height, flags, paletteCount, page, pixels, pixelBytes, alpha, paletteOffset, paletteBytes);
                JsonObject meta = new() { ["width"] = width, ["height"] = height, ["format_code"] = $"0x{flags:X2}", ["header_flags"] = $"0x{headerFlags:X8}", ["palette_entry_count"] = paletteCount, ["palette_page"] = page, ["header_raw"] = Convert.ToHexStringLower(doc.Bytes.Span.Slice((int)offset, 16)) };
                var asset = doc.Add(AssetKind.Texture, i, name, offset, end - offset, meta, info);
                asset.Summary = $"{width} × {height} · {(paletteCount > 0 ? "Paletted" : "RGB565")}{(alpha >= 0 ? " · Alpha" : "")}";
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            { doc.Diagnostics.Add(new("Error", $"Texture {i} ({name}): {ex.Message}", i, offset)); }
        }
    }
}

public static class TextureDecoder
{
    public static DecodedImage Decode(ZbdDocument doc, AssetRecord asset, CancellationToken token = default)
    {
        if (asset.Content is not TextureInfo t) throw new ArgumentException("Not a texture.", nameof(asset));
        int count = checked(t.Width * t.Height);
        byte[] rgba = new byte[checked(count * 4)];
        var pixels = doc.Bytes.Span.Slice(t.PixelsOffset, t.PixelsLength);
        var palette = t.PaletteOffset >= 0 ? doc.Bytes.Span.Slice(t.PaletteOffset, t.PaletteLength) : ReadOnlySpan<byte>.Empty;
        if (t.PaletteCount == 0 && (t.Flags & 1) == 0) throw new InvalidDataException("Unresolved one-byte direct-color encoding.");
        for (int i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            int offset = t.PaletteCount > 0 ? pixels[i] * 2 : i * 2;
            ReadOnlySpan<byte> colors = t.PaletteCount > 0 ? palette : pixels;
            if (offset + 2 > colors.Length) throw new InvalidDataException($"Pixel {i} references a missing palette entry.");
            ushort rgb = BinaryPrimitives.ReadUInt16LittleEndian(colors[offset..]);
            byte r = (byte)((rgb >> 11) & 31), g = (byte)((rgb >> 5) & 63), b = (byte)(rgb & 31);
            rgba[i * 4] = (byte)((r << 3) | (r >> 2)); rgba[i * 4 + 1] = (byte)((g << 2) | (g >> 4)); rgba[i * 4 + 2] = (byte)((b << 3) | (b >> 2));
            // Retail texture upload (zvid_ddd3d.c ConvertImagePixelsForTexture):
            // transparent textures without an alpha plane key the decoded zero color.
            rgba[i * 4 + 3] = t.AlphaOffset >= 0 ? doc.Bytes.Span[t.AlphaOffset + i] : (t.Flags & 2) != 0 && rgb == 0 ? (byte)0 : (byte)255;
        }
        return new(t.Width, t.Height, rgba);
    }
}
