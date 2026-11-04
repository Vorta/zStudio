namespace zStudio.Core.Formats;

/// <summary>Result of building a 32-bit pack: its bytes and, per record, the stored size.</summary>
public sealed record RgbaPackBuild(byte[] Bytes, IReadOnlyList<(string Name, int Width, int Height)> Sizes, IReadOnlyList<string> Warnings)
{
    /// <summary>What the textures take at their authored sizes, four bytes a texel: what a budget is compared with.</summary>
    public long FullSizeBytes { get; init; }
    /// <summary>The textures the budget stored smaller than authored, in record order, with both sizes.</summary>
    public IReadOnlyList<(string Name, int Width, int Height, int StoredWidth, int StoredHeight)> Reduced { get; init; } = [];
}

/// <summary>
/// Builds 32-bit packs (<see cref="RgbaPack"/>) from master images. A texture is stored at its authored size, as exactly the
/// RGBA8 its master decodes to, unless the pack's budget reduces it; then it is resampled from its master to the reduced
/// size (area average on premultiplied alpha, <see cref="TexturePackBuilder.Resample"/>), with no quantizing, palette or
/// dithering. Records follow the 16-bit packs' order. Deterministic: the same masters give the same bytes.
/// </summary>
public static class RgbaPackBuilder
{
    /// <summary>A budget halves a texture's longer side only while it is longer than this, and never below it.</summary>
    public const int MinimumReducedSide = 8;
    private const long MiB = 1024 * 1024;

    /// <summary>
    /// Builds a pack of <paramref name="kind"/> holding <paramref name="textures"/>; a mission texture pack fits them to
    /// <paramref name="budgetMegabytes"/> MiB of texels (an interface image pack, whose budget word is 0, keeps every image at
    /// its authored size). Records are ordered as the 16-bit packs order them (source path, then name); a name repeated
    /// ignoring case keeps its first record only (the one the game finds), with a warning. Sizes come from the sources and the
    /// layout is fixed before anything is decoded, so a pack whose file would exceed what a pack file may hold
    /// (<see cref="FormatRegistry.MaximumDocumentBytes"/>) is refused first; then each master is decoded, resampled when the
    /// budget reduced it, copied into place and released, so building holds the pack file, one master and its resampled copy.
    /// </summary>
    public static RgbaPackBuild Build(IReadOnlyList<PackSource> textures, RgbaPackKind kind, uint budgetMegabytes, string fileName, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (kind == RgbaPackKind.MissionTextures ? budgetMegabytes == 0 : budgetMegabytes != 0)
            throw new ArgumentOutOfRangeException(nameof(budgetMegabytes), "A mission texture pack has a budget; an interface image pack has none.");
        if (textures.Count > TexturePackBuilder.MaximumRecords) throw new InvalidDataException($"A texture pack holds at most {TexturePackBuilder.MaximumRecords:N0} textures.");
        BoundedDiagnostics warnings = new();
        // The 16-bit packs' record order (TexturePackBuilder.BuildFromSources), so both hold the textures in the same order.
        var ordered = textures.OrderBy(t => t.SortKey, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToArray();
        List<PackSource> kept = []; Dictionary<string, (PackSource First, int Count)> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in ordered)
        {
            token.ThrowIfCancellationRequested();
            string name = texture.Name.ToLowerInvariant();
            RgbaPack.ValidateName(name);
            if (texture.Width is < 1 or > RgbaPack.MaximumSide || texture.Height is < 1 or > RgbaPack.MaximumSide)
                throw new InvalidDataException($"{JsonData.ShownText(texture.Name, 64)}{From(texture)}: {texture.Width} × {texture.Height} texels; a 32-bit pack stores 1–{RgbaPack.MaximumSide:N0} texels a side.");
            if (seen.TryGetValue(name, out var first)) { seen[name] = (first.First, first.Count + 1); continue; }
            seen.Add(name, (texture, 1)); kept.Add(texture);
        }
        foreach (var (name, (first, count)) in seen.Where(s => s.Value.Count > 1).OrderBy(s => s.Key, StringComparer.Ordinal))
            warnings.Add($"{JsonData.ShownText(name, 64)} appears {count} times; {JsonData.ShownText(fileName, 128)} names each texture once and holds the first ({JsonData.ShownText(first.SortKey, 192)}), the one the game uses.");

        var sizes = kept.Select(t => (t.Width, t.Height)).ToArray();
        long fullSize = sizes.Sum(s => RgbaPack.TexelBytes(s.Width, s.Height));
        List<(string, int, int, int, int)> reduced = [];
        if (kind == RgbaPackKind.MissionTextures && fullSize > budgetMegabytes * MiB)
        {
            if (!Fit(sizes, budgetMegabytes * MiB, token))
                warnings.Add($"The textures exceed the {JsonData.ShownText(fileName, 128)} budget of {budgetMegabytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} MB even with every side reduced to {MinimumReducedSide} texels.");
            for (int i = 0; i < kept.Count; i++)
                if (sizes[i] != (kept[i].Width, kept[i].Height)) reduced.Add((kept[i].Name.ToLowerInvariant(), kept[i].Width, kept[i].Height, sizes[i].Width, sizes[i].Height));
        }

        var entries = kept.Select((t, i) => new RgbaPack.Entry(t.Name.ToLowerInvariant(), (uint)t.Addressing & RgbaPack.AddressingBits, sizes[i].Width, sizes[i].Height)).ToArray();
        long total = RgbaPack.FileBytes(entries);
        if (total > FormatRegistry.MaximumDocumentBytes)
        {
            string advice = kind == RgbaPackKind.InterfaceImages ? "Use fewer or smaller images" : "Use fewer or smaller textures";
            var largest = Enumerable.Range(0, kept.Count).OrderByDescending(i => (long)sizes[i].Width * sizes[i].Height).ThenBy(i => i).Take(8)
                .Select(i => $"{JsonData.ShownText(kept[i].Name, 64)} ({sizes[i].Width} × {sizes[i].Height}{From(kept[i])})");
            throw new InvalidDataException($"{JsonData.ShownText(fileName, 128)} would take {(total + MiB - 1) / MiB:N0} MiB with its textures at the sizes it stores, more than the "
                + $"{FormatRegistry.MaximumDocumentBytes / MiB} MiB a pack file can hold. {advice}. The largest it stores: {string.Join(", ", largest)}.");
        }
        byte[] file = new byte[total];
        long[] offsets = RgbaPack.WriteTables(file, kind, budgetMegabytes, entries);
        for (int i = 0; i < kept.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var master = Master(kept[i], token);
            // At its authored size a texture is its master's pixels exactly; only a reduced one is resampled.
            var image = TexturePackBuilder.Resample(master, sizes[i].Width, sizes[i].Height, token);
            if (image.Rgba.LongLength != RgbaPack.TexelBytes(sizes[i].Width, sizes[i].Height)) throw new InvalidDataException($"{kept[i].Name}: invalid image.");
            image.Rgba.CopyTo(file, offsets[i]);
        }
        FormatRegistry.ValidateDocumentSize(file.LongLength);
        var messages = warnings.Snapshot();
        // Result consumers preview only the first few warnings. Keep an aggregate omission visible there too.
        if (messages.Remove(BoundedDiagnostics.OmissionNotice)) messages.Insert(0, BoundedDiagnostics.OmissionNotice);
        return new(file, [.. entries.Select(e => (e.Name, e.Width, e.Height))], messages) { FullSizeBytes = fullSize, Reduced = reduced };

        static string From(PackSource source) => source.File is { } path ? $", {JsonData.ShownText(path, 192)}" : "";
    }

    /// <summary>One texture of a pack as planned: its source path's sort key, its name and its authored size.</summary>
    public readonly record struct Planned(string SortKey, string Name, int Width, int Height);

    /// <summary>
    /// What a pack of <paramref name="textures"/> will be (<paramref name="budgetMegabytes"/> 0: no budget), laid out and fitted
    /// as <see cref="Build"/> lays them out and fits them (the same order, the first of a repeated name, <see cref="Fit"/>), from
    /// their sizes alone: for textures whose masters have those sizes, the exact size of the file the build writes.
    /// </summary>
    public static PackEstimate Estimate(IReadOnlyList<Planned> textures, uint budgetMegabytes, CancellationToken token = default)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        var kept = textures.OrderBy(t => t.SortKey, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).Where(t => names.Add(t.Name.ToLowerInvariant())).ToArray();
        var sizes = kept.Select(t => (t.Width, t.Height)).ToArray();
        long authored = sizes.Sum(s => RgbaPack.TexelBytes(s.Width, s.Height));
        if (budgetMegabytes > 0 && authored > budgetMegabytes * MiB) Fit(sizes, budgetMegabytes * MiB, token);
        long bytes = RgbaPack.HeaderBytes;
        for (int i = 0; i < kept.Length; i++) bytes += RgbaPack.RecordFixedBytes + kept[i].Name.Length + RgbaPack.TexelBytes(sizes[i].Width, sizes[i].Height);
        return new(bytes, kept.Length, Enumerable.Range(0, kept.Length).Count(i => sizes[i] != (kept[i].Width, kept[i].Height)), authored);
    }

    /// <summary>
    /// Reduces textures to fit <paramref name="budget"/> bytes at four a texel (docs/rgba-packs.md, Reduced textures): in rounds,
    /// each halving the longer side (the width of a square) of every texture still longer than <see cref="MinimumReducedSide"/>,
    /// those with the most texels at the round's start first and record order breaking ties, until the textures fit. A halved
    /// side rounds down but keeps at least <see cref="MinimumReducedSide"/> texels; sides need not be powers of two and the
    /// aspect is not limited (unlike the 16-bit packs' fitting). Deterministic. False when the textures still exceed the budget
    /// with no side left to halve.
    /// </summary>
    internal static bool Fit((int Width, int Height)[] sizes, long budget, CancellationToken token)
    {
        long total = 0; foreach (var (w, h) in sizes) total += RgbaPack.TexelBytes(w, h);
        while (total > budget)
        {
            token.ThrowIfCancellationRequested();
            var round = Enumerable.Range(0, sizes.Length).Where(i => Math.Max(sizes[i].Width, sizes[i].Height) > MinimumReducedSide)
                .OrderByDescending(i => RgbaPack.TexelBytes(sizes[i].Width, sizes[i].Height)).ThenBy(i => i).ToArray();
            if (round.Length == 0) return false;
            foreach (int i in round)
            {
                var (w, h) = sizes[i]; long before = RgbaPack.TexelBytes(w, h);
                if (w >= h) w = Math.Max(MinimumReducedSide, w / 2); else h = Math.Max(MinimumReducedSide, h / 2);
                sizes[i] = (w, h); total += RgbaPack.TexelBytes(w, h) - before;
                if (total <= budget) return true;
            }
        }
        return true;
    }

    /// <summary>Decodes a texture's master, which must have the size its source gave.</summary>
    private static DecodedImage Master(PackSource source, CancellationToken token)
    {
        var master = source.Decode(token);
        if (master.Width != source.Width || master.Height != source.Height)
            throw new InvalidDataException($"{source.Name}: the image is {master.Width} × {master.Height}, not the {source.Width} × {source.Height} its source gave.");
        if (master.Rgba.LongLength != RgbaPack.TexelBytes(master.Width, master.Height)) throw new InvalidDataException($"{source.Name}: invalid image.");
        return master;
    }
}
