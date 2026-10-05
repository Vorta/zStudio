using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Recoil.Zbd.Core.Formats;

/// <summary>Which renderer a texture pack serves; the engine loads exactly one mission pack plus image.zbd.</summary>
public enum TexturePackKind
{
    /// <summary><c>rtexture&lt;N&gt;.zbd</c>: Direct3D, which rejects palettes, so every texel is RGB565.</summary>
    Hardware,
    /// <summary><c>texture&lt;N&gt;.zbd</c> and <c>texturemax.zbd</c>: the software renderer, which shades only paletted texels.</summary>
    Software,
    /// <summary><c>image.zbd</c>: 2D interface images copied as 16-bit rows, at their authored size.</summary>
    Interface,
}

/// <summary>A pack file and its texel budget (texel plus alpha-plane bytes; null keeps every texture at full size).</summary>
public sealed record TexturePackVariant(string FileName, TexturePackKind Kind, long? BudgetBytes, int MaximumDimension)
{
    /// <summary>
    /// Pack names the engine looks for (zVidTexturePackEnsureBuiltinTexturePacksLoaded, retail 0x46DF50): hardware tries
    /// <c>rtexture&lt;MB of texture memory&gt;</c> and counts down; software starts from its TextureMemory option.
    /// </summary>
    public static TexturePackVariant? FromFileName(string fileName)
    {
        string name = Path.GetFileName(fileName).ToLowerInvariant();
        if (name == "image.zbd") return new(name, TexturePackKind.Interface, null, 1024);
        if (name == "texturemax.zbd") return new(name, TexturePackKind.Software, null, TexturePackBuilder.SoftwareMaximumDimension);
        foreach (var (prefix, kind) in new[] { ("rtexture", TexturePackKind.Hardware), ("texture", TexturePackKind.Software) })
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".zbd", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - 4), NumberStyles.None, CultureInfo.InvariantCulture, out int megabytes) && megabytes is >= 1 and <= 1024)
                // Classic cards take 256-texel textures; the larger hardware packs target modern devices.
                return new(name, kind, megabytes * 1024L * 1024, kind == TexturePackKind.Hardware && megabytes < 8 ? 256 : 1024);
        return null;
    }
}

/// <summary>
/// One texture for a pack: its master image and what cannot be derived from pixels. Addressing is the Direct3D clamp
/// word (bit 0 clamps U, bit 1 clamps V). Direct keeps RGB565 texels in software packs, as the retail packs do for skies
/// (unshaded), damage masks and the player-vehicle textures they stamp (ApplyDamageMaskStampOnHit, retail 0x479660).
/// The sort key orders records like the original build (full source path, lowercase); the engine does not rely on it.
/// </summary>
public sealed record PackTexture(string Name, string SortKey, DecodedImage Master, int Addressing = 0, bool Direct = false);

/// <summary>Result of building a pack: its bytes and, per texture, the stored size.</summary>
public sealed record TexturePackBuild(byte[] Bytes, IReadOnlyList<(string Name, int Width, int Height)> Sizes, int Pages, IReadOnlyList<string> Warnings);

/// <summary>How a texture is transparent: not at all, by a colour key (alpha only 0 or 255) or by an alpha plane.</summary>
public enum TextureTransparency { Opaque, Keyed, Alpha }

/// <summary>Builds complete texture packs from master images: sizing, transparency, palettes and the pack layout.</summary>
public static class TexturePackBuilder
{
    public const int MinimumDimension = 8, MaximumRecords = 4096;
    /// <summary>
    /// The widest texture the software renderer draws: its spans switch on 20 − log2(width) with cases 10–17 only (retail
    /// 0x49bbf0, CalcPow2ScratchFields 0x4902b0), so a wider texture is skipped without drawing.
    /// </summary>
    public const int SoftwareMaximumDimension = 1024;

    public static TexturePackBuild Build(IReadOnlyList<PackTexture> textures, TexturePackVariant variant, CancellationToken token = default)
    {
        if (textures.Count > MaximumRecords) throw new InvalidDataException($"A texture pack holds at most {MaximumRecords:N0} textures.");
        List<string> warnings = [];
        var ordered = textures.OrderBy(t => t.SortKey, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToArray();
        foreach (var t in ordered)
        {
            TexturePackWriter.ValidateTextureName(t.Name);
            if (t.Master.Width < 1 || t.Master.Height < 1 || t.Master.Rgba.Length != checked(t.Master.Width * t.Master.Height * 4)) throw new InvalidDataException($"{t.Name}: invalid image.");
        }
        foreach (var duplicate in ordered.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            warnings.Add($"{duplicate.Key} appears {duplicate.Count()} times; the engine uses the first ({duplicate.First().SortKey}).");
        bool threeD = variant.Kind != TexturePackKind.Interface;
        var modes = ordered.Select(t => Classify(t.Master)).ToArray();
        bool Paletted(int i) => variant.Kind == TexturePackKind.Software && !ordered[i].Direct;
        long Cost(int i, int w, int h) => (long)w * h * (Paletted(i) ? 1 : 2) + (modes[i] == TextureTransparency.Alpha ? (long)w * h : 0);
        var sizes = ordered.Select(t => threeD ? Normalize(t.Master.Width, t.Master.Height, variant) : (t.Master.Width, t.Master.Height)).ToArray();
        if (variant.BudgetBytes is { } budget) Fit(sizes, Cost, budget, variant, token, warnings);

        // Resample every texture from its master, then quantize the paletted ones into shared pages.
        var images = new DecodedImage[ordered.Length];
        for (int i = 0; i < ordered.Length; i++) { token.ThrowIfCancellationRequested(); images[i] = Resample(ordered[i].Master, sizes[i].Width, sizes[i].Height, token); }
        int[] pageOf = Enumerable.Repeat(-1, ordered.Length).ToArray(); List<ushort[]> pages = [];
        var paletted = Enumerable.Range(0, ordered.Length).Where(Paletted).ToArray();
        if (paletted.Length > 0) BuildPages(paletted, images, modes, pageOf, pages, token);

        using MemoryStream output = new(); using BinaryWriter w = new(output, Encoding.Latin1);
        w.Write(0); w.Write(1); w.Write(pages.Count); w.Write(ordered.Length); w.Write(0L);
        long tableStart = output.Position; output.Position += ordered.Length * 40L;
        foreach (var page in pages) for (int i = 0; i < 256; i++) w.Write(i < page.Length ? page[i] : (ushort)0);
        List<(string, int, int)> stored = [];
        for (int i = 0; i < ordered.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            long offset = output.Position; if (offset > int.MaxValue) throw new InvalidDataException("The texture pack exceeds 2 GiB.");
            WriteImage(w, images[i], modes[i], ordered[i].Addressing, pageOf[i] >= 0 ? pages[pageOf[i]] : null, token);
            long end = output.Position; output.Position = tableStart + i * 40L;
            byte[] name = new byte[32]; Encoding.Latin1.GetBytes(ordered[i].Name.ToLowerInvariant()).CopyTo(name, 0);
            w.Write(name); w.Write((int)offset); w.Write(pageOf[i]); output.Position = end;
            stored.Add((ordered[i].Name, images[i].Width, images[i].Height));
        }
        FormatRegistry.ValidateDocumentSize(output.Length);
        return new(output.ToArray(), stored, pages.Count, warnings);
    }

    /// <summary>Opaque when every alpha is 255; colour-keyed when alpha is only 0 or 255 (1555 on hardware keeps more colour); else an alpha plane.</summary>
    public static TextureTransparency Classify(DecodedImage image)
    {
        bool translucent = false, transparent = false;
        for (int i = 3; i < image.Rgba.Length; i += 4)
        {
            byte a = image.Rgba[i];
            if (a == 0) transparent = true; else if (a != 255) { translucent = true; break; }
        }
        return translucent ? TextureTransparency.Alpha : transparent ? TextureTransparency.Keyed : TextureTransparency.Opaque;
    }

    /// <summary>3D textures are powers of two from 8 up to the variant's limit; hardware also needs aspect ≤ 8 (always enforced by CreateTextureRecord).</summary>
    internal static (int Width, int Height) Normalize(int width, int height, TexturePackVariant variant)
    {
        // Nearest power of two, at most 2^30 (shifting further would overflow and never end).
        static int Pow2(int v) { int p = 1; while (p < v && p < 1 << 30) p <<= 1; return p > v && p - v > v - p / 2 ? p / 2 : p; }
        int w = Math.Clamp(Pow2(width), MinimumDimension, variant.MaximumDimension), h = Math.Clamp(Pow2(height), MinimumDimension, variant.MaximumDimension);
        while (w > h * 8) w /= 2;
        while (h > w * 8) h /= 2;
        return (w, h);
    }

    /// <summary>
    /// Halves textures in rounds, each round halving the longer axis of every texture still above the minimum (largest
    /// cost first), until the pack fits. Every texture keeps its share of detail; the result is deterministic.
    /// </summary>
    private static void Fit((int Width, int Height)[] sizes, Func<int, int, int, long> cost, long budget, TexturePackVariant variant, CancellationToken token, List<string> warnings)
    {
        long total = 0; for (int i = 0; i < sizes.Length; i++) total += cost(i, sizes[i].Width, sizes[i].Height);
        while (total > budget)
        {
            token.ThrowIfCancellationRequested();
            var round = Enumerable.Range(0, sizes.Length).Where(i => Math.Max(sizes[i].Width, sizes[i].Height) > MinimumDimension)
                .OrderByDescending(i => cost(i, sizes[i].Width, sizes[i].Height)).ThenBy(i => i).ToArray();
            if (round.Length == 0) { warnings.Add($"The textures exceed the {variant.FileName} budget even at {MinimumDimension} × {MinimumDimension}."); return; }
            foreach (int i in round)
            {
                var (w, h) = sizes[i]; long before = cost(i, w, h);
                if (w >= h) w /= 2; else h /= 2;
                if (w < MinimumDimension || h < MinimumDimension) continue;
                // Keep hardware aspect ≤ 8 by halving the other axis too.
                if (variant.Kind == TexturePackKind.Hardware && (w * 8 < h || h * 8 < w)) { if (w > h) w /= 2; else h /= 2; }
                sizes[i] = (Math.Max(w, MinimumDimension), Math.Max(h, MinimumDimension)); total += cost(i, sizes[i].Width, sizes[i].Height) - before;
                if (total <= budget) return;
            }
        }
    }

    /// <summary>Area-average downscaling and linear upscaling per axis, on premultiplied alpha so transparent texels do not bleed colour.</summary>
    public static DecodedImage Resample(DecodedImage image, int width, int height, CancellationToken token = default)
    {
        if (width == image.Width && height == image.Height) return image;
        float[] source = new float[image.Width * image.Height * 4];
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            float a = image.Rgba[i * 4 + 3] / 255f;
            for (int c = 0; c < 3; c++) source[i * 4 + c] = image.Rgba[i * 4 + c] * a;
            source[i * 4 + 3] = image.Rgba[i * 4 + 3];
        }
        var horizontal = Weights(image.Width, width); var vertical = Weights(image.Height, height);
        float[] rows = new float[width * image.Height * 4];
        for (int y = 0; y < image.Height; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
                foreach (var (index, weight) in horizontal[x])
                    for (int c = 0; c < 4; c++) rows[(y * width + x) * 4 + c] += source[(y * image.Width + index) * 4 + c] * weight;
        }
        byte[] rgba = new byte[width * height * 4]; Span<float> sum = stackalloc float[4];
        for (int y = 0; y < height; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                sum.Clear();
                foreach (var (index, weight) in vertical[y]) for (int c = 0; c < 4; c++) sum[c] += rows[(index * width + x) * 4 + c] * weight;
                int o = (y * width + x) * 4; float alpha = Math.Clamp(sum[3], 0, 255);
                rgba[o + 3] = (byte)MathF.Round(alpha);
                for (int c = 0; c < 3; c++) rgba[o + c] = alpha <= 0 ? (byte)0 : (byte)Math.Clamp(MathF.Round(sum[c] / (alpha / 255f)), 0, 255);
            }
        }
        return new(width, height, rgba);

        static (int Index, float Weight)[][] Weights(int from, int to)
        {
            var result = new (int, float)[to][];
            for (int t = 0; t < to; t++)
            {
                if (to <= from)
                {
                    // Each target texel averages the source texels it covers, weighted by coverage.
                    double start = (double)t * from / to, end = (double)(t + 1) * from / to; List<(int, float)> list = [];
                    for (int s = (int)Math.Floor(start); s < Math.Min(from, (int)Math.Ceiling(end)); s++)
                    {
                        double cover = Math.Min(end, s + 1) - Math.Max(start, s);
                        if (cover > 0) list.Add((s, (float)(cover * to / from)));
                    }
                    result[t] = list.ToArray();
                }
                else
                {
                    double centre = Math.Clamp((t + 0.5) * from / to - 0.5, 0, from - 1); int s0 = (int)centre, s1 = Math.Min(s0 + 1, from - 1); float f = (float)(centre - s0);
                    result[t] = s1 == s0 ? [(s0, 1f)] : [(s0, 1 - f), (s1, f)];
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Groups paletted textures by mean colour (deterministic k-means) into up to 16 shared pages, as the retail software
    /// packs do (12–18 pages); every page and palette is expanded per fog/shade recipe at load, so pages are shared.
    /// Entry 0 is black and reserved: keyed and fully transparent texels use it, opaque texels never do.
    /// </summary>
    private static void BuildPages(int[] members, DecodedImage[] images, TextureTransparency[] modes, int[] pageOf, List<ushort[]> pages, CancellationToken token)
    {
        int k = Math.Clamp((members.Length + 23) / 24, 1, 16);
        var means = members.ToDictionary(i => i, i => Mean(images[i]));
        // Seeds evenly spaced by luminance; assignments converge within a few iterations.
        var byLuma = members.OrderBy(i => Luma(means[i])).ThenBy(i => i).ToArray();
        var centres = Enumerable.Range(0, k).Select(c => means[byLuma[(int)((c + 0.5) * byLuma.Length / k)]]).ToArray();
        int[] assignment = new int[members.Length];
        for (int iteration = 0; iteration < 24; iteration++)
        {
            token.ThrowIfCancellationRequested(); bool changed = false;
            for (int m = 0; m < members.Length; m++)
            {
                var v = means[members[m]]; int best = 0; double bestDistance = double.MaxValue;
                for (int c = 0; c < k; c++) { double d = Distance(v, centres[c]); if (d < bestDistance) { bestDistance = d; best = c; } }
                if (assignment[m] != best || iteration == 0) { changed |= assignment[m] != best; assignment[m] = best; }
            }
            for (int c = 0; c < k; c++)
            {
                var group = Enumerable.Range(0, members.Length).Where(m => assignment[m] == c).ToArray();
                if (group.Length > 0) centres[c] = (group.Average(m => means[members[m]].R), group.Average(m => means[members[m]].G), group.Average(m => means[members[m]].B));
            }
            if (!changed && iteration > 0) break;
        }
        for (int c = 0; c < k; c++)
        {
            var group = Enumerable.Range(0, members.Length).Where(m => assignment[m] == c).Select(m => members[m]).ToArray();
            if (group.Length == 0) continue;
            int[] histogram = new int[65536];
            foreach (int i in group)
                for (int p = 0; p < images[i].Width * images[i].Height; p++)
                    if (images[i].Rgba[p * 4 + 3] >= (modes[i] == TextureTransparency.Keyed ? 128 : 1)) histogram[Rgb565(images[i].Rgba, p)]++;
            histogram[0] = 0;
            var colors = TexturePackWriter.Quantize(histogram, 255, token);
            ushort[] palette = [0, .. colors.Where(color => color != 0)];
            if (palette.Length == 1) palette = [0, 0x0020];
            int page = pages.Count; pages.Add(palette);
            foreach (int i in group) pageOf[i] = page;
        }
        static (double R, double G, double B) Mean(DecodedImage image)
        {
            double r = 0, g = 0, b = 0, weight = 0;
            for (int p = 0; p < image.Width * image.Height; p++)
            {
                double a = image.Rgba[p * 4 + 3] / 255.0; r += image.Rgba[p * 4] * a; g += image.Rgba[p * 4 + 1] * a; b += image.Rgba[p * 4 + 2] * a; weight += a;
            }
            return weight <= 0 ? (0, 0, 0) : (r / weight, g / weight, b / weight);
        }
        static double Luma((double R, double G, double B) c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        static double Distance((double R, double G, double B) a, (double R, double G, double B) b) => (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);
    }

    /// <summary>Exact inverse of the engine's bit-replicating expansion: round(c·31/255) and round(c·63/255).</summary>
    internal static ushort Rgb565(byte[] rgba, int pixel) =>
        (ushort)(((rgba[pixel * 4] * 31 + 127) / 255 << 11) | ((rgba[pixel * 4 + 1] * 63 + 127) / 255 << 5) | ((rgba[pixel * 4 + 2] * 31 + 127) / 255));

    private static void WriteImage(BinaryWriter w, DecodedImage image, TextureTransparency mode, int addressing, ushort[]? palette, CancellationToken token)
    {
        int count = image.Width * image.Height;
        // Format bits: 0x01 16-bit colour words, 0x02 transparent, 0x04 opaque marker, 0x08 alpha plane, 0x10 page palette.
        byte flags = (byte)((palette == null ? 0x01 : 0x11) | mode switch { TextureTransparency.Opaque => 0x04, TextureTransparency.Keyed => 0x02, _ => 0x0A });
        w.Write(flags); w.Write((byte)0); w.Write((ushort)0);
        w.Write((ushort)image.Width); w.Write((ushort)image.Height); w.Write(0u);
        w.Write((ushort)(palette == null ? 0 : 256)); w.Write((ushort)(addressing & 3));
        bool Opaque(int p) => mode switch { TextureTransparency.Opaque => true, TextureTransparency.Keyed => image.Rgba[p * 4 + 3] >= 128, _ => image.Rgba[p * 4 + 3] > 0 };
        if (palette == null)
            for (int p = 0; p < count; p++)
            {
                if ((p & 4095) == 0) token.ThrowIfCancellationRequested();
                // Keyed textures store the key; alpha-plane textures keep the colour under any alpha.
                ushort color = mode == TextureTransparency.Alpha || Opaque(p) ? Rgb565(image.Rgba, p) : (ushort)0;
                // Opaque black would read as a hole in a keyed texture.
                if (mode == TextureTransparency.Keyed && color == 0 && Opaque(p)) color = 0x0020;
                w.Write(color);
            }
        else
        {
            Dictionary<ushort, byte> cache = [];
            for (int p = 0; p < count; p++)
            {
                if ((p & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!Opaque(p)) { w.Write((byte)0); continue; }
                ushort color = Rgb565(image.Rgba, p);
                if (!cache.TryGetValue(color, out byte index)) cache[color] = index = Nearest(palette, color);
                w.Write(index);
            }
        }
        if (mode == TextureTransparency.Alpha) w.Write(image.Rgba.Where((_, i) => i % 4 == 3).ToArray());
    }
    private static byte Nearest(ushort[] palette, ushort color)
    {
        int best = 1, bestDistance = int.MaxValue;
        for (int i = 1; i < palette.Length; i++)
        {
            int r = TexturePackWriter.Channel(color, 0) - TexturePackWriter.Channel(palette[i], 0), g = TexturePackWriter.Channel(color, 1) - TexturePackWriter.Channel(palette[i], 1), b = TexturePackWriter.Channel(color, 2) - TexturePackWriter.Channel(palette[i], 2);
            int d = 3 * r * r + 4 * g * g + 2 * b * b; if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return (byte)best;
    }
}
