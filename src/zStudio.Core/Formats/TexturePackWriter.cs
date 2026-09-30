using System.Buffers.Binary;
using System.Text;

namespace Recoil.Zbd.Core.Formats;

public sealed record TexturePayload(string Name, ReadOnlyMemory<byte> Bytes, int PalettePage = -1);
public sealed record TexturePackEdit(ZbdDocument Source, IReadOnlyDictionary<int, TexturePayload> Replacements, IReadOnlyList<TexturePayload> Added);
public sealed record TextureEncodingResult(TexturePayload Payload, int PaletteColors, int QuantizedColors);

public static partial class TexturePackWriter
{
    public static void ValidateTextureName(string name)
    { if (name.Length is < 1 or > 31 || name.Any(c => c == 0 || c > 255)) throw new InvalidDataException("Texture names require 1–31 Latin-1 characters without NUL."); }
    public static byte[] Write(TexturePackEdit edit, CancellationToken token = default)
    {
        var source = edit.Source; int count = source.Assets.Count, added = edit.Added.Count;
        if (source.Probe.Family != FormatFamily.TexturePack || source.Probe.Version != 1 || source.Diagnostics.Any(d => d.Severity == "Error")) throw new InvalidDataException("An intact v1 texture pack is required.");
        if (count + added > 4096) throw new InvalidDataException("Texture pack is full (4096 records).");
        if (edit.Replacements.Keys.Any(i => i < 0 || i >= count)) throw new InvalidDataException("Replacement texture index is outside the pack.");
        long length = source.Bytes.Length + added * 40L + edit.Replacements.Values.Sum(p => (long)p.Bytes.Length) + edit.Added.Sum(p => (long)p.Bytes.Length);
        FormatRegistry.ValidateDocumentSize(length);
        int tableEnd = 24 + count * 40, shift = added * 40;
        byte[] bytes = new byte[(int)length]; source.Bytes.Span[..tableEnd].CopyTo(bytes);
        source.Bytes.Span[tableEnd..].CopyTo(bytes.AsSpan(tableEnd + shift));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), count + added);
        int offset = source.Bytes.Length + shift;
        for (int i = 0; i < count + added; i++)
        {
            token.ThrowIfCancellationRequested(); var record = bytes.AsSpan(24 + i * 40, 40);
            TexturePayload? replacement = i >= count ? edit.Added[i - count] : edit.Replacements.GetValueOrDefault(i);
            if (replacement == null)
            {
                int old = BinaryPrimitives.ReadInt32LittleEndian(record[32..]);
                BinaryPrimitives.WriteInt32LittleEndian(record[32..], checked(old + shift)); continue;
            }
            if (i >= count)
            {
                ValidateTextureName(replacement.Name); Encoding.Latin1.GetBytes(replacement.Name, record);
            }
            replacement.Bytes.Span.CopyTo(bytes.AsSpan(offset));
            BinaryPrimitives.WriteInt32LittleEndian(record[32..], offset);
            BinaryPrimitives.WriteInt32LittleEndian(record[36..], replacement.PalettePage);
            offset += replacement.Bytes.Length;
        }
        return bytes;
    }
    public static TextureEncodingResult Encode(string name, DecodedImage image, ZbdDocument? source = null, AssetRecord? original = null, CancellationToken token = default)
    {
        if (image.Width is < 1 or > 4096 || image.Height is < 1 or > 4096 || image.Rgba.Length != checked(image.Width * image.Height * 4)) throw new InvalidDataException("Texture imports support dimensions from 1 to 4096.");
        var info = original?.Content as TextureInfo;
        bool indexed = info?.PaletteCount > 0;
        int count = image.Width * image.Height;
        bool alpha = info != null && (info.AlphaOffset >= 0 || (info.Flags & 2) != 0);
        int[] histogram = new int[65536];
        for (int i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            alpha |= image.Rgba[i * 4 + 3] != 255;
            if (indexed) histogram[Pack(image.Rgba[i * 4], image.Rgba[i * 4 + 1], image.Rgba[i * 4 + 2])]++;
        }
        ushort[] palette = indexed ? Quantize(histogram, 256, token) : [];
        byte[] map = indexed ? BuildMap(histogram, palette, token) : [];
        int pixelBytes = count * (indexed ? 1 : 2);
        byte[] bytes = new byte[16 + pixelBytes + (alpha ? count : 0) + palette.Length * 2];
        if (source != null && original != null) source.Slice(original.Offset, 16).Span.CopyTo(bytes);
        // Keep unrelated flags/header/addressing bytes. Store explicit alpha rather than keying opaque black.
        byte flags = (byte)((bytes[0] & ~(0x10 | 0x08 | 0x02)) | 1);
        if (alpha) flags |= 0x0a;
        bytes[0] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)image.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)image.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), (ushort)palette.Length);
        for (int i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested(); int p = i * 4;
            ushort rgb = Pack(image.Rgba[p], image.Rgba[p + 1], image.Rgba[p + 2]);
            if (indexed) bytes[16 + i] = map[rgb]; else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16 + i * 2), rgb);
            if (alpha) bytes[16 + pixelBytes + i] = image.Rgba[p + 3];
        }
        int paletteOffset = 16 + pixelBytes + (alpha ? count : 0);
        for (int i = 0; i < palette.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(paletteOffset + i * 2), palette[i]);
        int reduced = indexed ? histogram.Select((n, i) => n > 0 && palette[map[i]] != i ? 1 : 0).Sum() : 0;
        return new(new(name, bytes), palette.Length, reduced);
    }
    internal static ushort Pack(int r, int g, int b) => (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
    internal static int Channel(int color, int channel) => channel switch { 0 => ((color >> 11) & 31) * 255 / 31, 1 => ((color >> 5) & 63) * 255 / 63, _ => (color & 31) * 255 / 31 };
    private sealed class ColorBox(int[] colors, int[] histogram)
    {
        public int[] Colors { get; } = colors;
        public long Weight { get; } = colors.Sum(c => (long)histogram[c]);
        public int[] Ranges { get; } = Enumerable.Range(0, 3).Select(ch => colors.Max(c => Channel(c, ch)) - colors.Min(c => Channel(c, ch))).ToArray();
        public int Range => Ranges.Max();
    }
    /// <summary>Deterministic weighted median cut of an RGB565 histogram to at most <paramref name="maximumColors"/> colours.</summary>
    internal static ushort[] Quantize(int[] histogram, int maximumColors, CancellationToken token)
    {
        int[] colors = Enumerable.Range(0, histogram.Length).Where(c => histogram[c] > 0).ToArray();
        if (colors.Length <= maximumColors) return colors.Select(c => (ushort)c).ToArray();
        List<ColorBox> boxes = [new(colors, histogram)];
        while (boxes.Count < maximumColors && boxes.Any(b => b.Colors.Length > 1))
        {
            token.ThrowIfCancellationRequested();
            var box = boxes.Where(b => b.Colors.Length > 1).OrderByDescending(b => b.Range).ThenByDescending(b => b.Weight).ThenBy(b => b.Colors.Min()).First();
            int channel = Array.IndexOf(box.Ranges, box.Range);
            int[] sorted = box.Colors.OrderBy(c => Channel(c, channel)).ThenBy(c => c).ToArray();
            long sum = 0; int split = 1;
            for (int i = 0; i < sorted.Length - 1; i++) { sum += histogram[sorted[i]]; split = i + 1; if (sum * 2 >= box.Weight) break; }
            boxes.Remove(box); boxes.Add(new(sorted[..split], histogram)); boxes.Add(new(sorted[split..], histogram));
        }
        return boxes.Select(b => Pack((int)(b.Colors.Sum(c => (long)Channel(c, 0) * histogram[c]) / b.Weight),
            (int)(b.Colors.Sum(c => (long)Channel(c, 1) * histogram[c]) / b.Weight), (int)(b.Colors.Sum(c => (long)Channel(c, 2) * histogram[c]) / b.Weight))).Distinct().Order().ToArray();
    }
    internal static byte[] BuildMap(int[] histogram, ushort[] palette, CancellationToken token)
    {
        byte[] map = new byte[65536];
        for (int c = 0; c < histogram.Length; c++)
        {
            if ((c & 255) == 0) token.ThrowIfCancellationRequested(); if (histogram[c] == 0) continue;
            int best = int.MaxValue;
            for (int p = 0; p < palette.Length; p++)
            {
                int r = Channel(c, 0) - Channel(palette[p], 0), g = Channel(c, 1) - Channel(palette[p], 1), b = Channel(c, 2) - Channel(palette[p], 2);
                int distance = r * r + g * g + b * b; if (distance < best) { best = distance; map[c] = (byte)p; }
            }
        }
        return map;
    }
    public static DecodedImage Resize(DecodedImage image, int width, int height, CancellationToken token = default)
    {
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new InvalidDataException("Target texture dimensions exceed import limits.");
        if (width == image.Width && height == image.Height) return image;
        byte[] output = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested(); double sy = Math.Clamp((y + .5) * image.Height / height - .5, 0, image.Height - 1); int y0 = (int)sy, y1 = Math.Min(y0 + 1, image.Height - 1);
            for (int x = 0; x < width; x++)
            {
                double sx = Math.Clamp((x + .5) * image.Width / width - .5, 0, image.Width - 1); int x0 = (int)sx, x1 = Math.Min(x0 + 1, image.Width - 1);
                double fx = sx - x0, fy = sy - y0;
                double alpha = Sample(3), a = alpha / 255;
                int p = (y * width + x) * 4;
                output[p + 3] = (byte)Math.Clamp((int)Math.Round(alpha), 0, 255);
                for (int c = 0; c < 3; c++) output[p + c] = a <= 0 ? (byte)0 : (byte)Math.Clamp((int)Math.Round(Sample(c) / a), 0, 255);
                double Sample(int c)
                {
                    double Pixel(int px, int py) { int i = (py * image.Width + px) * 4; return image.Rgba[i + c] * (c == 3 ? 1 : image.Rgba[i + 3] / 255.0); }
                    return Pixel(x0, y0) * (1 - fx) * (1 - fy) + Pixel(x1, y0) * fx * (1 - fy) + Pixel(x0, y1) * (1 - fx) * fy + Pixel(x1, y1) * fx * fy;
                }
            }
        }
        return new(width, height, output);
    }
}
