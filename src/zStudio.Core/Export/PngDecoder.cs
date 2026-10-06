using System.Buffers.Binary;
using System.IO.Compression;

namespace Recoil.Zbd.Core.Export;

/// <summary>
/// PNG decoder for images saved by common editors: greyscale, RGB, palette, grey+alpha and RGBA at every legal bit depth,
/// <c>tRNS</c> transparency and Adam7 interlacing, converted to RGBA8. Chunk CRCs are checked and the decoded size is
/// bounded before anything is allocated. Gamma and colour-space chunks are ignored, as game textures carry raw values.
/// A file other decoders would read differently is refused as <see cref="InvalidDataException"/>: chunks out of the
/// specification's order or repeated, a <c>tRNS</c> chunk of the wrong length for its colour type or palette, and pixel
/// data that does not fill or overfills the image.
/// </summary>
public static class PngDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static DecodedImage Decode(ReadOnlySpan<byte> png, int maximumDimension = 4096, CancellationToken token = default)
    {
        if (maximumDimension is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        if (png.Length < 33 || !png[..8].SequenceEqual(Signature)) throw new InvalidDataException("Expected PNG.");
        int width = 0, height = 0, depth = 0, colorType = 0, offset = 8; bool interlaced = false, header = false, ended = false;
        // The image data has begun (IDAT), and has ended (another chunk after it): its chunks must be consecutive.
        bool imageData = false, imageEnded = false;
        byte[]? palette = null, transparency = null;
        using MemoryStream compressed = new();
        while (offset < png.Length)
        {
            token.ThrowIfCancellationRequested();
            if (offset > png.Length - 12) throw new InvalidDataException("Truncated PNG chunk.");
            int length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            if (length < 0 || length > png.Length - offset - 12) throw new InvalidDataException("Invalid PNG chunk length.");
            var type = png.Slice(offset + 4, 4); var data = png.Slice(offset + 8, length);
            if (Crc(png.Slice(offset + 4, length + 4)) != BinaryPrimitives.ReadUInt32BigEndian(png[(offset + 8 + length)..])) throw new InvalidDataException("PNG checksum mismatch.");
            // The order the PNG specification sets, where decoders differ on files that break it (the palette or transparency
            // a streaming decoder has not seen when the pixels arrive, a second one replacing the first or ignored, image data
            // split by other chunks read as one stream or cut at the first): such a file is refused rather than read one way.
            if (imageData && !type.SequenceEqual("IDAT"u8)) imageEnded = true;
            if (type.SequenceEqual("IHDR"u8))
            {
                if (header || offset != 8 || length != 13) throw new InvalidDataException("Invalid PNG header.");
                width = BinaryPrimitives.ReadInt32BigEndian(data); height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                depth = data[8]; colorType = data[9];
                if (width < 1 || height < 1 || width > maximumDimension || height > maximumDimension) throw new InvalidDataException($"PNG images may be at most {maximumDimension} × {maximumDimension}.");
                bool valid = colorType switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 3 => depth is 1 or 2 or 4 or 8, 2 or 4 or 6 => depth is 8 or 16, _ => false };
                if (!valid || data[10] != 0 || data[11] != 0 || data[12] > 1) throw new InvalidDataException("Unsupported PNG format.");
                interlaced = data[12] == 1; header = true;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (!header || length % 3 != 0 || length == 0 || length > 768) throw new InvalidDataException("Invalid PNG palette.");
                if (palette != null) throw new InvalidDataException("The PNG has two palettes (PLTE chunks); it may have one.");
                if (imageData) throw new InvalidDataException("The PNG's palette (PLTE) follows its image data; it must come before it.");
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                if (!header) throw new InvalidDataException("Missing PNG header.");
                if (colorType is 4 or 6) throw new InvalidDataException("Invalid PNG transparency: an image with an alpha channel has no tRNS chunk.");
                if (transparency != null) throw new InvalidDataException("The PNG has two transparency (tRNS) chunks; it may have one.");
                if (imageData) throw new InvalidDataException("The PNG's transparency (tRNS) follows its image data; it must come before it.");
                // Greyscale states one 2-byte grey value, RGB three 2-byte values, a palette image an alpha value for each of
                // its first palette entries (after the palette, and not more than it has): anything else gives no transparency
                // a reader could agree on, and reading it as opaque would drop the transparency the file meant.
                if (colorType == 0 && length != 2 || colorType == 2 && length != 6)
                    throw new InvalidDataException($"Invalid PNG transparency: a{(colorType == 0 ? " greyscale" : "n RGB")} image's tRNS chunk holds {(colorType == 0 ? 2 : 6)} bytes, not {length:N0}.");
                if (colorType == 3 && palette == null) throw new InvalidDataException("The PNG's transparency (tRNS) comes before its palette (PLTE); it must follow it.");
                if (colorType == 3 && length > palette!.Length / 3)
                    throw new InvalidDataException($"Invalid PNG transparency: the tRNS chunk holds {length:N0} alpha values for a palette of {palette.Length / 3:N0} entries.");
                transparency = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (!header) throw new InvalidDataException("Missing PNG header.");
                if (imageEnded) throw new InvalidDataException("The PNG's image data (IDAT chunks) is split by other chunks; its chunks must be consecutive.");
                if (compressed.Length + length > int.MaxValue / 2) throw new InvalidDataException("PNG data is too large.");
                compressed.Write(data); imageData = true;
            }
            else if (type.SequenceEqual("IEND"u8)) { ended = true; break; }
            // Ancillary chunks (lowercase first letter) such as gAMA, pHYs and tEXt carry nothing a texture needs.
            else if ((type[0] & 32) == 0) throw new InvalidDataException($"Unsupported critical PNG chunk {System.Text.Encoding.ASCII.GetString(type)}.");
            offset += length + 12;
        }
        if (!header || !ended) throw new InvalidDataException("Incomplete PNG.");
        if (colorType == 3 && palette == null) throw new InvalidDataException("Palette PNG without a palette.");
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bitsPerPixel = channels * depth, bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        var passes = interlaced ? Adam7(width, height) : [(0, 0, 1, 1, width, height)];
        long expected = passes.Sum(p => p.W == 0 || p.H == 0 ? 0 : (long)p.H * (1 + ((long)p.W * bitsPerPixel + 7) / 8));
        if (expected > 1L << 30) throw new InvalidDataException("PNG image is too large.");
        byte[] raw = new byte[expected]; compressed.Position = 0;
        using (ZLibStream z = new(compressed, CompressionMode.Decompress))
        {
            int excess;
            // The rows, and then the end of the data: pixels beyond the image make it malformed (decoders differ on them).
            try { z.ReadExactly(raw); excess = z.ReadByte(); }
            catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated PNG pixels.", ex); }
            catch (InvalidDataException ex) { throw new InvalidDataException("Corrupt PNG pixel data.", ex); }
            if (excess != -1) throw new InvalidDataException("Excess PNG pixels: the image data holds more than its rows.");
        }
        byte[] rgba = new byte[checked(width * height * 4)]; int position = 0;
        // The transparent grey or RGB value: below 16 bits a sample uses the value's low bits, and the PNG specification has
        // decoders mask the others (an encoder may leave them set), as libpng does.
        int[]? key = null;
        if (transparency != null && colorType is 0 or 2)
        {
            int mask = depth == 16 ? 0xFFFF : (1 << depth) - 1;
            key = [.. Enumerable.Range(0, transparency.Length / 2).Select(c => BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(c * 2)) & mask)];
        }
        PixelFormat format = new(colorType, depth, channels, palette, colorType == 3 ? transparency : null, key);
        foreach (var (x0, y0, dx, dy, w, h) in passes)
        {
            if (w == 0 || h == 0) continue;
            int stride = (w * bitsPerPixel + 7) / 8; byte[] previous = new byte[stride], current = new byte[stride];
            for (int row = 0; row < h; row++)
            {
                token.ThrowIfCancellationRequested();
                int filter = raw[position++]; raw.AsSpan(position, stride).CopyTo(current); position += stride;
                Unfilter(filter, current, previous, bytesPerPixel);
                ConvertRow(format, current, w, rgba, (y0 + row * dy) * width + x0, dx);
                (previous, current) = (current, previous);
            }
        }
        return new(width, height, rgba);
    }

    /// <summary>How to read a row: <paramref name="Transparency"/> holds a palette image's alpha values, <paramref name="Key"/> a grey or RGB image's transparent sample values.</summary>
    private sealed record PixelFormat(int ColorType, int Depth, int Channels, byte[]? Palette, byte[]? Transparency, int[]? Key);

    /// <summary>
    /// One unfiltered row of <paramref name="w"/> pixels into RGBA, from pixel <paramref name="target"/> in steps of
    /// <paramref name="dx"/>.
    /// </summary>
    private static void ConvertRow(PixelFormat f, byte[] line, int w, byte[] rgba, int target, int dx)
    {
        for (int index = 0; index < w; index++)
        {
            int o = (target + index * dx) * 4;
            switch (f.ColorType)
            {
                case 0:
                    {
                        byte g = (byte)Sample(f, line, index, 0); rgba[o] = rgba[o + 1] = rgba[o + 2] = g;
                        rgba[o + 3] = f.Key is [var grey] && Raw(f, line, index, 0) == grey ? (byte)0 : (byte)255;
                        break;
                    }
                case 2:
                    rgba[o] = (byte)Sample(f, line, index, 0); rgba[o + 1] = (byte)Sample(f, line, index, 1); rgba[o + 2] = (byte)Sample(f, line, index, 2);
                    rgba[o + 3] = f.Key is [var keyR, var keyG, var keyB] && Raw(f, line, index, 0) == keyR && Raw(f, line, index, 1) == keyG && Raw(f, line, index, 2) == keyB ? (byte)0 : (byte)255;
                    break;
                case 3:
                    {
                        byte[] palette = f.Palette!;
                        int entry = Sample(f, line, index, 0); if (entry * 3 + 2 >= palette.Length) throw new InvalidDataException("PNG palette index out of range.");
                        rgba[o] = palette[entry * 3]; rgba[o + 1] = palette[entry * 3 + 1]; rgba[o + 2] = palette[entry * 3 + 2];
                        rgba[o + 3] = f.Transparency != null && entry < f.Transparency.Length ? f.Transparency[entry] : (byte)255;
                        break;
                    }
                case 4: { byte g = (byte)Sample(f, line, index, 0); rgba[o] = rgba[o + 1] = rgba[o + 2] = g; rgba[o + 3] = (byte)Sample(f, line, index, 1); break; }
                default: rgba[o] = (byte)Sample(f, line, index, 0); rgba[o + 1] = (byte)Sample(f, line, index, 1); rgba[o + 2] = (byte)Sample(f, line, index, 2); rgba[o + 3] = (byte)Sample(f, line, index, 3); break;
            }
        }
    }
    /// <summary>A channel as 8 bits: 16-bit samples keep their high byte, lower depths scale up, palette indices stay.</summary>
    private static int Sample(PixelFormat f, byte[] line, int index, int channel)
    {
        if (f.Depth == 16) return line[(index * f.Channels + channel) * 2];
        if (f.Depth == 8) return line[index * f.Channels + channel];
        int bit = index * f.Depth; int value = (line[bit >> 3] >> (8 - f.Depth - (bit & 7))) & ((1 << f.Depth) - 1);
        return f.ColorType == 3 ? value : value * 255 / ((1 << f.Depth) - 1);
    }
    /// <summary>A channel's stored value, as <c>tRNS</c> compares it.</summary>
    private static int Raw(PixelFormat f, byte[] line, int index, int channel)
    {
        if (f.Depth == 16) return BinaryPrimitives.ReadUInt16BigEndian(line.AsSpan((index * f.Channels + channel) * 2));
        if (f.Depth == 8) return line[index * f.Channels + channel];
        int bit = index * f.Depth; return (line[bit >> 3] >> (8 - f.Depth - (bit & 7))) & ((1 << f.Depth) - 1);
    }

    private static void Unfilter(int filter, byte[] line, byte[] previous, int bpp)
    {
        switch (filter)
        {
            case 0: break;
            case 1: for (int i = bpp; i < line.Length; i++) line[i] += line[i - bpp]; break;
            case 2: for (int i = 0; i < line.Length; i++) line[i] += previous[i]; break;
            case 3: for (int i = 0; i < line.Length; i++) line[i] += (byte)(((i >= bpp ? line[i - bpp] : 0) + previous[i]) / 2); break;
            case 4:
                for (int i = 0; i < line.Length; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                    int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
                    line[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                break;
            default: throw new InvalidDataException("Invalid PNG filter.");
        }
    }

    private static (int X, int Y, int Dx, int Dy, int W, int H)[] Adam7(int width, int height)
    {
        (int X, int Y, int Dx, int Dy)[] p = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
        return p.Select(q => (q.X, q.Y, q.Dx, q.Dy, width > q.X ? (width - q.X + q.Dx - 1) / q.Dx : 0, height > q.Y ? (height - q.Y + q.Dy - 1) / q.Dy : 0)).ToArray();
    }

    internal static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320U ^ (c >> 1) : c >> 1; table[n] = c; }
        return table;
    }
}
