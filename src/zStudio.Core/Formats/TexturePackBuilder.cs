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

/// <summary>
/// A <see cref="PackTexture"/> whose master is decoded only when the pack needs its pixels: its size (from the image's
/// header, which the decoded master must have) is known before. <see cref="Transparency"/>, when already known (from an
/// earlier decode of the same image), spares the decode that only classifies it. A pack decodes one master at a time and
/// keeps only the texture at its stored size (see <see cref="TexturePackBuilder.BuildFromSources"/>).
/// </summary>
public sealed record PackSource(string Name, string SortKey, int Width, int Height, Func<CancellationToken, DecodedImage> Decode, int Addressing = 0, bool Direct = false)
{
    public TextureTransparency? Transparency { get; init; }
    /// <summary>The file the texture comes from, named in refusals (a texture can be stored under another name).</summary>
    public string? File { get; init; }
}

/// <summary>Result of building a pack: its bytes and, per texture, the stored size.</summary>
public sealed record TexturePackBuild(byte[] Bytes, IReadOnlyList<(string Name, int Width, int Height)> Sizes, int Pages, IReadOnlyList<string> Warnings)
{
    /// <summary>What the textures need of the budget at the largest sizes the pack stores (texels plus alpha planes), before fitting.</summary>
    public long FullSizeBytes { get; init; }
}

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

    public static TexturePackBuild Build(IReadOnlyList<PackTexture> textures, TexturePackVariant variant, CancellationToken token = default) =>
        BuildFromSources([.. textures.Select(t => new PackSource(t.Name, t.SortKey, t.Master.Width, t.Master.Height, _ => t.Master, t.Addressing, t.Direct))], variant, token);

    /// <summary>
    /// How much of the textures a build keeps decoded at their stored sizes between its passes, at four bytes a texel
    /// (<see cref="DecodedImage"/>): a texture beyond it is decoded again when its pixels are needed.
    /// </summary>
    internal const long RetainedImageBytes = 256L << 20;

    /// <summary>
    /// Builds a pack from textures decoded one at a time. Each texture's stored size follows from its own size, the pack's
    /// largest side and, under a budget, every texture's transparency (alpha planes count against it). So a master whose
    /// transparency is not known is decoded to classify it and kept only reduced to the size the pack stores without a
    /// budget, while the textures so kept fit the budget and a pack file; one that the budget then reduces further, or
    /// that was not kept, is decoded again and resampled to its final size. The shared palettes need every paletted
    /// texture's mean colour and then its colours, and the pack is written after them, so the textures at their final sizes
    /// are kept for those passes while they fit <see cref="RetainedImageBytes"/> (four bytes a texel, although a paletted
    /// texel takes one byte of the pack) and the others are decoded again for each pass; every decode resamples the same
    /// master to the same size, so the pack is the same either way. Building thus holds at most that much, one master, the
    /// texture being resampled from it (with a few rows of workspace, see <see cref="Resample"/>) and the pack file. A pack
    /// whose file would exceed what a pack file may hold (<see cref="FormatRegistry.MaximumDocumentBytes"/>) is refused
    /// before its textures are resampled to their final sizes: before anything is decoded when its texels alone exceed it.
    /// </summary>
    public static TexturePackBuild BuildFromSources(IReadOnlyList<PackSource> textures, TexturePackVariant variant, CancellationToken token = default)
        => BuildFromSources(textures, variant, RetainedImageBytes, token);

    /// <summary><see cref="BuildFromSources(IReadOnlyList{PackSource}, TexturePackVariant, CancellationToken)"/> keeping at most <paramref name="retainedBytes"/> of decoded textures.</summary>
    internal static TexturePackBuild BuildFromSources(IReadOnlyList<PackSource> textures, TexturePackVariant variant, long retainedBytes, CancellationToken token)
    {
        if (textures.Count > MaximumRecords) throw new InvalidDataException($"A texture pack holds at most {MaximumRecords:N0} textures.");
        List<string> warnings = [];
        var ordered = textures.OrderBy(t => t.SortKey, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToArray();
        foreach (var t in ordered)
        {
            TexturePackWriter.ValidateTextureName(t.Name);
            if (t.Width < 1 || t.Height < 1) throw new InvalidDataException($"{t.Name}: invalid image.");
        }
        foreach (var duplicate in ordered.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            warnings.Add($"{duplicate.Key} appears {duplicate.Count()} times; the engine uses the first ({duplicate.First().SortKey}).");
        bool threeD = variant.Kind != TexturePackKind.Interface;
        var modes = new TextureTransparency[ordered.Length];
        bool Paletted(int i) => variant.Kind == TexturePackKind.Software && !ordered[i].Direct;
        long Cost(int i, int w, int h) => (long)w * h * (Paletted(i) ? 1 : 2) + (modes[i] == TextureTransparency.Alpha ? (long)w * h : 0);
        var sizes = ordered.Select(t => threeD ? Normalize(t.Width, t.Height, variant) : (t.Width, t.Height)).ToArray();
        // Without a budget every texture is stored at that size, so the pack's texels are known before anything is decoded.
        if (variant.BudgetBytes == null) CheckOutput(variant, ordered, sizes, Paletted, _ => false, true);

        // Textures are kept only resampled from their masters, at most at the size the pack stores without a budget; a master
        // is never kept. What is kept fits the budget and a pack file, so it never exceeds what the pack itself holds, and
        // what the kept images take (four bytes a texel) fits retainedBytes.
        var kept = new DecodedImage?[ordered.Length];
        long keeping = Math.Min(variant.BudgetBytes ?? long.MaxValue, FormatRegistry.MaximumDocumentBytes), keptCost = 0, retained = 0;
        long Retains(int i) => 4L * sizes[i].Width * sizes[i].Height;
        for (int i = 0; i < ordered.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (ordered[i].Transparency is { } known) { modes[i] = known; continue; }
            var master = Master(ordered[i], token);
            modes[i] = Classify(master);
            long cost = Cost(i, sizes[i].Width, sizes[i].Height);
            if (keptCost + cost <= keeping && retained + Retains(i) <= retainedBytes) { kept[i] = Resample(master, sizes[i].Width, sizes[i].Height, token); keptCost += cost; retained += Retains(i); }
        }
        long fullSize = 0; for (int i = 0; i < sizes.Length; i++) fullSize += Cost(i, sizes[i].Width, sizes[i].Height);
        if (variant.BudgetBytes is { } budget)
        {
            var full = ((int Width, int Height)[])sizes.Clone();
            Fit(sizes, Cost, budget, variant, token, warnings);
            // A texture the budget reduced is resampled from its master again, as every texture is resampled once from it.
            for (int i = 0; i < sizes.Length; i++) if (sizes[i] != full[i] && kept[i] != null) { kept[i] = null; retained -= 4L * full[i].Width * full[i].Height; }
        }
        CheckOutput(variant, ordered, sizes, Paletted, i => modes[i] == TextureTransparency.Alpha, false);
        // Every texture at its final size: kept while retainedBytes holds it, else decoded again for each pass that needs it.
        DecodedImage Final(int i) => Resample(Master(ordered[i], token), sizes[i].Width, sizes[i].Height, token);
        var images = new DecodedImage?[ordered.Length];
        var means = new (double R, double G, double B)[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var image = kept[i] ?? Final(i);
            if (Paletted(i)) means[i] = Mean(image);
            if (kept[i] != null) images[i] = image;
            else if (retained + Retains(i) <= retainedBytes) { images[i] = image; retained += Retains(i); }
            kept[i] = null;
        }

        // Quantize the paletted textures into shared pages.
        int[] pageOf = Enumerable.Repeat(-1, ordered.Length).ToArray(); List<ushort[]> pages = [];
        var paletted = Enumerable.Range(0, ordered.Length).Where(Paletted).ToArray();
        if (paletted.Length > 0) BuildPages(paletted, means, i => images[i] ?? Final(i), modes, pageOf, pages, token);

        // Sized for the whole file, which is then returned without a copy.
        long expected = OutputBytes(sizes, Paletted, i => modes[i] == TextureTransparency.Alpha) + pages.Count * 512L;
        using MemoryStream output = new((int)Math.Min(expected, FormatRegistry.MaximumDocumentBytes)); using BinaryWriter w = new(output, Encoding.Latin1);
        w.Write(0); w.Write(1); w.Write(pages.Count); w.Write(ordered.Length); w.Write(0L);
        long tableStart = output.Position; output.Position += ordered.Length * 40L;
        foreach (var page in pages) for (int i = 0; i < 256; i++) w.Write(i < page.Length ? page[i] : (ushort)0);
        List<(string, int, int)> stored = [];
        for (int i = 0; i < ordered.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            long offset = output.Position; if (offset > int.MaxValue) throw new InvalidDataException("The texture pack exceeds 2 GiB.");
            var image = images[i] ?? Final(i);
            // Written: no longer kept.
            images[i] = null;
            WriteImage(w, image, modes[i], ordered[i].Addressing, pageOf[i] >= 0 ? pages[pageOf[i]] : null, token);
            long end = output.Position; output.Position = tableStart + i * 40L;
            byte[] name = new byte[32]; Encoding.Latin1.GetBytes(ordered[i].Name.ToLowerInvariant()).CopyTo(name, 0);
            w.Write(name); w.Write((int)offset); w.Write(pageOf[i]); output.Position = end;
            stored.Add((ordered[i].Name, image.Width, image.Height));
        }
        FormatRegistry.ValidateDocumentSize(output.Length);
        byte[] bytes = output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
        return new(bytes, stored, pages.Count, warnings) { FullSizeBytes = fullSize };
    }

    /// <summary>Decodes a texture's master, which must have the size its source gave.</summary>
    private static DecodedImage Master(PackSource source, CancellationToken token)
    {
        var master = source.Decode(token);
        if (master.Width != source.Width || master.Height != source.Height)
            throw new InvalidDataException($"{source.Name}: the image is {master.Width} × {master.Height}, not the {source.Width} × {source.Height} its source gave.");
        if (master.Rgba.Length != checked(master.Width * master.Height * 4)) throw new InvalidDataException($"{source.Name}: invalid image.");
        return master;
    }

    /// <summary>
    /// The pack file's size for these stored sizes, without its palette pages (at most 16 of 512 bytes): the header, and per
    /// texture a 40-byte record, a 16-byte image header, its texels (a byte each when paletted, else two) and its alpha plane.
    /// </summary>
    internal static long OutputBytes((int Width, int Height)[] sizes, Func<int, bool> paletted, Func<int, bool> alpha)
    {
        long total = 24;
        for (int i = 0; i < sizes.Length; i++)
        {
            long texels = (long)sizes[i].Width * sizes[i].Height;
            total += 56 + texels * (paletted(i) ? 1 : 2) + (alpha(i) ? texels : 0);
        }
        return total;
    }

    /// <summary>
    /// Refuses a pack whose file would hold more than a pack file may (<see cref="FormatRegistry.MaximumDocumentBytes"/>),
    /// before its textures are resampled to their final sizes. <paramref name="texelsOnly"/>: alpha planes are not known
    /// yet (nothing is decoded), so the size is the least the pack can take.
    /// </summary>
    private static void CheckOutput(TexturePackVariant variant, PackSource[] ordered, (int Width, int Height)[] sizes, Func<int, bool> paletted, Func<int, bool> alpha, bool texelsOnly)
    {
        // With the palette pages paletted textures share (at most 16 of 512 bytes).
        long total = OutputBytes(sizes, paletted, alpha) + (Enumerable.Range(0, sizes.Length).Any(paletted) ? 16 * 512 : 0);
        if (total <= FormatRegistry.MaximumDocumentBytes) return;
        string advice = variant.Kind == TexturePackKind.Interface ? "Use fewer or smaller images"
            : variant.BudgetBytes is { } budget ? $"Give the pack a budget below {FormatRegistry.MaximumDocumentBytes >> 20} MiB (it has {budget >> 20} MiB) in its build profile"
            : "Give the pack a budget or a smaller largest side in a build profile, or use fewer or smaller textures";
        var largest = Enumerable.Range(0, sizes.Length).OrderByDescending(i => (long)sizes[i].Width * sizes[i].Height).ThenBy(i => i).Take(8)
            .Select(i => $"{ordered[i].Name} ({sizes[i].Width} × {sizes[i].Height}{(ordered[i].File is { } file ? $", {file}" : "")})");
        throw new InvalidDataException($"{variant.FileName} would take {(texelsOnly ? "at least " : "")}{(total + (1 << 20) - 1) >> 20} MiB with its textures at the sizes it stores, "
            + $"more than the {FormatRegistry.MaximumDocumentBytes >> 20} MiB a pack file can hold. {advice}. The largest it stores: {string.Join(", ", largest)}.");
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

    /// <summary>
    /// Area-average downscaling and linear upscaling per axis, on premultiplied alpha so transparent texels do not bleed colour.
    /// Each output row sums the source rows it covers, each first resampled across, in the order of its weights; the source
    /// rows are resampled as the output rows reach them. Besides the result it holds one premultiplied source row, the two
    /// source rows last resampled across (consecutive output rows share at most those) and one output row's sums, never the
    /// image as floats, so a large master costs a few rows of workspace. Every texel takes the same arithmetic in the same
    /// order as the whole-image passes would, so the result does not depend on how the work is divided.
    /// </summary>
    public static DecodedImage Resample(DecodedImage image, int width, int height, CancellationToken token = default)
    {
        if (width == image.Width && height == image.Height) return image;
        int sourceWidth = image.Width;
        var horizontal = Weights(sourceWidth, width); var vertical = Weights(image.Height, height);
        float[] premultiplied = new float[sourceWidth * 4];
        // Source rows resampled across, by source row: the output rows ask for rows in increasing order, going back at most
        // to the last two, so the older of these two is the one replaced.
        float[][] across = [new float[width * 4], new float[width * 4]];
        int[] held = [-1, -1];
        int resampled = 0;
        float[] Across(int row)
        {
            if (held[0] == row) return across[0];
            if (held[1] == row) return across[1];
            if ((resampled++ & 63) == 0) token.ThrowIfCancellationRequested();
            int slot = held[0] < held[1] ? 0 : 1; float[] result = across[slot];
            int start = row * sourceWidth * 4;
            for (int x = 0; x < sourceWidth; x++)
            {
                int i = start + x * 4;
                float a = image.Rgba[i + 3] / 255f;
                for (int c = 0; c < 3; c++) premultiplied[x * 4 + c] = image.Rgba[i + c] * a;
                premultiplied[x * 4 + 3] = image.Rgba[i + 3];
            }
            Array.Clear(result);
            for (int x = 0; x < width; x++)
                foreach (var (index, weight) in horizontal[x])
                    for (int c = 0; c < 4; c++) result[x * 4 + c] += premultiplied[index * 4 + c] * weight;
            held[slot] = row;
            return result;
        }
        byte[] rgba = new byte[width * height * 4]; float[] sums = new float[width * 4];
        for (int y = 0; y < height; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            Array.Clear(sums);
            foreach (var (index, weight) in vertical[y])
            {
                float[] row = Across(index);
                for (int k = 0; k < sums.Length; k++) sums[k] += row[k] * weight;
            }
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4; float alpha = Math.Clamp(sums[x * 4 + 3], 0, 255);
                rgba[o + 3] = (byte)MathF.Round(alpha);
                for (int c = 0; c < 3; c++) rgba[o + c] = alpha <= 0 ? (byte)0 : (byte)Math.Clamp(MathF.Round(sums[x * 4 + c] / (alpha / 255f)), 0, 255);
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
    private static void BuildPages(int[] members, (double R, double G, double B)[] meanColours, Func<int, DecodedImage> images, TextureTransparency[] modes, int[] pageOf, List<ushort[]> pages, CancellationToken token)
    {
        int k = Math.Clamp((members.Length + 23) / 24, 1, 16);
        var means = members.ToDictionary(i => i, i => meanColours[i]);
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
            {
                token.ThrowIfCancellationRequested();
                var image = images(i);
                for (int p = 0; p < image.Width * image.Height; p++)
                    if (image.Rgba[p * 4 + 3] >= (modes[i] == TextureTransparency.Keyed ? 128 : 1)) histogram[Rgb565(image.Rgba, p)]++;
            }
            histogram[0] = 0;
            var colors = TexturePackWriter.Quantize(histogram, 255, token);
            ushort[] palette = [0, .. colors.Where(color => color != 0)];
            if (palette.Length == 1) palette = [0, 0x0020];
            int page = pages.Count; pages.Add(palette);
            foreach (int i in group) pageOf[i] = page;
        }
        static double Luma((double R, double G, double B) c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        static double Distance((double R, double G, double B) a, (double R, double G, double B) b) => (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);
    }

    /// <summary>A texture's mean colour weighted by alpha, which groups it with textures of like colour into a palette page.</summary>
    private static (double R, double G, double B) Mean(DecodedImage image)
    {
        double r = 0, g = 0, b = 0, weight = 0;
        for (int p = 0; p < image.Width * image.Height; p++)
        {
            double a = image.Rgba[p * 4 + 3] / 255.0; r += image.Rgba[p * 4] * a; g += image.Rgba[p * 4 + 1] * a; b += image.Rgba[p * 4 + 2] * a; weight += a;
        }
        return weight <= 0 ? (0, 0, 0) : (r / weight, g / weight, b / weight);
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
        if (mode != TextureTransparency.Alpha) return;
        // The alpha plane, through a small buffer rather than a copy of the plane.
        Span<byte> plane = stackalloc byte[4096];
        for (int p = 0; p < count; p += plane.Length)
        {
            token.ThrowIfCancellationRequested();
            int n = Math.Min(plane.Length, count - p);
            for (int k = 0; k < n; k++) plane[k] = image.Rgba[(p + k) * 4 + 3];
            w.Write(plane[..n]);
        }
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
