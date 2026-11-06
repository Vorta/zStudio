using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

/// <summary>
/// A 16-bit texture pack the plan has for each mission: its file, texel budget (null: every texture at full size) and
/// largest texture side. An export for one target builds the packs of that target (<see cref="ExportTargets.Builds"/>).
/// </summary>
public sealed record BuildProfilePack(string File, long? BudgetBytes, int MaximumDimension)
{
    /// <summary>The automatic Direct3D pack: <c>rtexture&lt;N&gt;</c>, named per mission (see <see cref="BuildProfiles.AutomaticPack"/>).</summary>
    public const string AutomaticFile = "rtexture*.zbd";
    /// <summary>Whether this is the automatic Direct3D pack, whose budget is the largest pack it makes.</summary>
    public bool Automatic => File == AutomaticFile;
    public TexturePackVariant Variant => Automatic ? new(File, TexturePackKind.Hardware, BudgetBytes, MaximumDimension)
        : TexturePackVariant.FromFileName(File) is { } named ? named with { BudgetBytes = BudgetBytes, MaximumDimension = MaximumDimension }
        : throw new InvalidDataException($"{File} is not a texture pack name.");
}

/// <summary>
/// The 16-bit texture packs the plan has in every mission (owner decision, 2026-10-30: one pack set), and the 32-bit packs
/// beside them for reRECOIL v2 (<see cref="RgbaTiers"/>, <see cref="RgbaImages"/>). An export for one target builds the packs
/// of that target (<see cref="ExportTargets.Builds"/>).
/// </summary>
public static class BuildProfiles
{
    private const long MiB = 1024 * 1024;

    /// <summary>
    /// The automatic pack's largest size: the game is a 32-bit process, and Direct3D keeps a system-memory copy of every
    /// texture beside its surface, so a pack costs a multiple of its size in the 2 GiB address space (not yet measured).
    /// </summary>
    public const int AutomaticMegabytes = 256;
    /// <summary>
    /// Every pack a mission shipped with (rtexture2 and rtexture4 at 256 texels for 2 and 4 MB cards, texture2, texture4,
    /// texture6 and texture8 for the software renderer), at the shipped budgets and sides, plus larger tiers for modern
    /// cards: the game opens rtexture&lt;its reported texture memory in MB&gt;, counting down, so rtexture8 and rtexture16
    /// give every texture at up to 1024 texels, the automatic pack holds them at full size once they need more, and
    /// texturemax keeps them at full size for the software renderer. The larger tiers are not yet measured in the game.
    /// </summary>
    public static IReadOnlyList<BuildProfilePack> TexturePacks { get; } =
    [
        new("rtexture2.zbd", 2 * MiB, 256), new("rtexture4.zbd", 4 * MiB, 256), new("texture2.zbd", 2 * MiB, 1024), new("texture4.zbd", 4 * MiB, 1024),
        new("texture6.zbd", 6 * MiB, 1024), new("rtexture8.zbd", 8 * MiB, 1024), new("rtexture16.zbd", 16 * MiB, 1024),
        new(BuildProfilePack.AutomaticFile, AutomaticMegabytes * MiB, 1024), new("texture8.zbd", 8 * MiB, 1024), new("texturemax.zbd", null, 1024),
    ];

    /// <summary>
    /// The automatic Direct3D pack of a mission whose textures need <paramref name="memory"/> bytes of texture memory at full
    /// size, counted as every pack's budget is (<see cref="TexturePackBuilder.StoredBytes"/>): <c>rtexture&lt;N&gt;</c>, N
    /// that memory in MiB rounded up to a power of two and at most <see cref="AutomaticMegabytes"/>. It exists only when it is
    /// larger than every fixed rtexture pack (the game opens the largest that the card's reported memory allows). Its budget
    /// is N MiB, so the build reduces the textures only when they need more: above the automatic pack's budget, or with what
    /// an estimate from PNG headers cannot count (textures the world brings from other folders, alpha planes; see
    /// <see cref="SourceBuilder.TextureMemory"/> and <see cref="SourceBuilder.PackMemory"/>).
    /// </summary>
    public static TexturePackVariant? AutomaticPack(long memory)
    {
        if (TexturePacks.FirstOrDefault(p => p.Automatic) is not { BudgetBytes: long budget } automatic) return null;
        long largest = TexturePacks.Where(p => !p.Automatic).Select(p => TexturePackVariant.FromFileName(p.File))
            .Where(v => v is { Kind: TexturePackKind.Hardware }).Select(v => v!.BudgetBytes!.Value / MiB).DefaultIfEmpty(0).Max();
        long needed = Math.Max(1, (memory + MiB - 1) / MiB), megabytes = 1;
        while (megabytes < needed) megabytes *= 2;
        megabytes = Math.Min(megabytes, budget / MiB);
        if (needed <= largest || megabytes <= largest) return null;
        return new($"rtexture{megabytes}.zbd", TexturePackKind.Hardware, megabytes * MiB, automatic.MaximumDimension);
    }

    /// <summary>The smallest 32-bit tier, in MB: reRECOIL v2 has far more graphics memory than the 16-bit tiers serve.</summary>
    public const int RgbaSmallestMegabytes = 64;
    /// <summary>The 32-bit interface image pack, written wherever image.zbd is.</summary>
    public const string RgbaImageFile = "rgbaimage.zbd";
    private const string RgbaTexturePrefix = "rgbatexture";
    /// <summary>The 32-bit texture pack of the tier of <paramref name="megabytes"/> MB.</summary>
    public static string RgbaTextureFile(long megabytes) => $"{RgbaTexturePrefix}{megabytes}.zbd";
    /// <summary>The interface image pack's variant: every image at its authored size, no budget.</summary>
    public static TexturePackVariant RgbaImages { get; } = new(RgbaImageFile, TexturePackKind.RgbaInterface, null, RgbaPack.MaximumSide);

    /// <summary>
    /// The 32-bit texture packs of a mission whose textures take <paramref name="memory"/> bytes at their authored sizes, four
    /// bytes a texel (<see cref="RgbaPack.TexelBytes"/>). The tiers are powers of two from <see cref="RgbaSmallestMegabytes"/>
    /// MB, each <c>rgbatexture&lt;N&gt;.zbd</c> with a budget of N MiB of texels; reRECOIL v2 opens the largest N at or below
    /// its texture memory. The last is the full-size tier, the smallest N that holds every texture at its authored size; the
    /// others are every smaller tier, each of which reduces textures to fit its budget (see <see cref="RgbaPackBuilder.Fit"/>).
    /// No smaller tier is an identical copy: its budget is below what the textures need, so it reduces at least one of them,
    /// and two smaller tiers differ, since halving one texture of at most 4096 × 4096 texels (64 MiB) never takes the
    /// textures from above one tier's budget to within the next smaller one's.
    /// </summary>
    public static IReadOnlyList<(TexturePackVariant Pack, bool FullSize)> RgbaTiers(long memory)
    {
        long full = RgbaSmallestMegabytes;
        while (full * MiB < memory && full < 1L << 31) full *= 2;
        List<(TexturePackVariant, bool)> tiers = [];
        for (long megabytes = RgbaSmallestMegabytes; megabytes < full; megabytes *= 2) tiers.Add((RgbaVariant(megabytes), false));
        tiers.Add((RgbaVariant(full), true));
        return tiers;
    }
    private static TexturePackVariant RgbaVariant(long megabytes) => new(RgbaTextureFile(megabytes), TexturePackKind.Rgba, megabytes * MiB, RgbaPack.MaximumSide);
    /// <summary>N of a file named <c>rgbatexture&lt;N&gt;.zbd</c> (ignoring case; N decimal, 1–4,294,967,295, without leading zeros), else null.</summary>
    public static long? RgbaTextureMegabytes(string fileName)
    {
        string name = Path.GetFileName(fileName);
        if (!name.StartsWith(RgbaTexturePrefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zbd", StringComparison.OrdinalIgnoreCase)) return null;
        var digits = name.AsSpan(RgbaTexturePrefix.Length, name.Length - RgbaTexturePrefix.Length - 4);
        return digits.Length is >= 1 and <= 10 && digits[0] != '0' && digits.IndexOfAnyExceptInRange('0', '9') < 0
            && long.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) is var megabytes and <= uint.MaxValue ? megabytes : null;
    }

    /// <summary>
    /// Texture packs a destination mission folder holds that the export does not build (<paramref name="built"/>: the pack
    /// files it plans for the mission): the game opens the largest rtexture&lt;N&gt; at or below its texture memory, so a
    /// stale pack of any size, such as an automatic pack of an earlier export, can be chosen instead of the exported ones;
    /// the software renderer opens texture&lt;N&gt; or texturemax by its option, so a stale one of those can be too. With
    /// <paramref name="rgba"/>, the 32-bit packs instead: reRECOIL v2 opens the largest rgbatexture&lt;N&gt; at or below its
    /// texture memory likewise. Every entry of the folder counts towards <paramref name="maximumEntries"/>
    /// (<see cref="SourceProject.MaximumScannedEntries"/>; beyond that the listing is refused with <see cref="IOException"/>),
    /// and <paramref name="token"/> is observed at each.
    /// </summary>
    public static IReadOnlyList<string> ShadowingPacks(string destination, string mission, IReadOnlyCollection<string> built, CancellationToken token = default, bool rgba = false)
        => ShadowingPacks(destination, mission, built, SourceProject.MaximumScannedEntries, token, rgba: rgba);
    internal static IReadOnlyList<string> ShadowingPacks(string destination, string mission, IReadOnlyCollection<string> built, int maximumEntries, CancellationToken token,
        InventoryBudget? inventory = null, bool rgba = false)
        // Software packs too: the software renderer opens texture<N> or texturemax by its option, whatever the export built.
        => Packs(destination, mission, built, maximumEntries, token, inventory,
            f => rgba ? RgbaTextureMegabytes(f) != null : TexturePackVariant.FromFileName(f) is { Kind: TexturePackKind.Hardware or TexturePackKind.Software });

    /// <summary>
    /// The rtexture&lt;N&gt; and rgbatexture&lt;N&gt; packs a destination mission folder holds that <paramref name="built"/> does not
    /// name, from one listing of the folder bounded as <see cref="ShadowingPacks(string, string, IReadOnlyCollection{string}, CancellationToken, bool)"/>
    /// bounds it: the packs either game opens by its texture memory.
    /// </summary>
    public static IReadOnlyList<string> LeftPacks(string destination, string mission, IReadOnlyCollection<string> built, CancellationToken token)
        => Packs(destination, mission, built, SourceProject.MaximumScannedEntries, token, null,
            f => RgbaTextureMegabytes(f) != null || TexturePackVariant.FromFileName(f) is { Kind: TexturePackKind.Hardware });

    private static IReadOnlyList<string> Packs(string destination, string mission, IReadOnlyCollection<string> built, int maximumEntries, CancellationToken token,
        InventoryBudget? inventory, Func<string, bool> pack)
    {
        string folder = Path.Combine(destination, mission);
        if (!Directory.Exists(folder)) return [];
        SourceProject.ScanBudget budget = new(maximumEntries, maximum => new IOException($"{folder} holds more than {maximum:N0} files and folders."), token, inventory);
        return SourceProject.Entries(folder, budget).OfType<FileInfo>().Select(f => f.Name)
            .Where(f => f.Contains("texture", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".zbd", StringComparison.OrdinalIgnoreCase))
            .Where(f => pack(f) && !built.Contains(f, StringComparer.OrdinalIgnoreCase))
            .Select(f => $"{mission}/{f.ToLowerInvariant()}").Order(StringComparer.Ordinal).ToArray();
    }
}
