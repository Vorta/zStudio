using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

/// <summary>
/// A texture pack every export builds for each mission: its file, texel budget (null: every texture at full size) and
/// largest texture side.
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

/// <summary>The texture packs exports and checks build in every mission (owner decision, 2026-10-30: one pack set).</summary>
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

    /// <summary>
    /// Texture packs a destination mission folder holds that the export does not build (<paramref name="built"/>: the pack
    /// files it plans for the mission): the game opens the largest rtexture&lt;N&gt; at or below its texture memory, so a
    /// stale pack of any size, such as an automatic pack of an earlier export, can be chosen instead of the exported ones;
    /// the software renderer opens texture&lt;N&gt; or texturemax by its option, so a stale one of those can be too. Every entry
    /// of the folder counts towards <paramref name="maximumEntries"/> (<see cref="SourceProject.MaximumScannedEntries"/>; beyond
    /// that the listing is refused with <see cref="IOException"/>), and <paramref name="token"/> is observed at each.
    /// </summary>
    public static IReadOnlyList<string> ShadowingPacks(string destination, string mission, IReadOnlyCollection<string> built, CancellationToken token = default)
        => ShadowingPacks(destination, mission, built, SourceProject.MaximumScannedEntries, token);
    internal static IReadOnlyList<string> ShadowingPacks(string destination, string mission, IReadOnlyCollection<string> built, int maximumEntries, CancellationToken token,
        InventoryBudget? inventory = null)
    {
        string folder = Path.Combine(destination, mission);
        if (!Directory.Exists(folder)) return [];
        SourceProject.ScanBudget budget = new(maximumEntries, maximum => new IOException($"{folder} holds more than {maximum:N0} files and folders."), token, inventory);
        // Software packs too: the software renderer opens texture<N> or texturemax by its option, whatever the export built.
        return SourceProject.Entries(folder, budget).OfType<FileInfo>().Select(f => f.Name)
            .Where(f => f.Contains("texture", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".zbd", StringComparison.OrdinalIgnoreCase))
            .Where(f => TexturePackVariant.FromFileName(f) is { Kind: TexturePackKind.Hardware or TexturePackKind.Software } && !built.Contains(f, StringComparer.OrdinalIgnoreCase))
            .Select(f => $"{mission}/{f.ToLowerInvariant()}").Order(StringComparer.Ordinal).ToArray();
    }
}
