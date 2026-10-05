using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// A texture pack a profile builds: its file, texel budget (null: every texture at full size), largest texture side, and
/// the mission folders it is built for (null: every mission).
/// </summary>
public sealed record BuildProfilePack(string File, long? BudgetBytes, int MaximumDimension)
{
    /// <summary>The automatic Direct3D pack: <c>rtexture&lt;N&gt;</c>, named per mission (see <see cref="BuildProfiles.AutomaticPack"/>).</summary>
    public const string AutomaticFile = "rtexture*.zbd";
    public IReadOnlyList<string>? Missions { get; init; }
    /// <summary>Whether this is the automatic Direct3D pack, whose budget is the largest pack it makes.</summary>
    public bool Automatic => File == AutomaticFile;
    public TexturePackVariant Variant => Automatic ? new(File, TexturePackKind.Hardware, BudgetBytes, MaximumDimension)
        : TexturePackVariant.FromFileName(File) is { } named ? named with { BudgetBytes = BudgetBytes, MaximumDimension = MaximumDimension }
        : throw new InvalidDataException($"{File} is not a texture pack name.");
    /// <summary>Whether the pack is built for a mission folder (<c>m6</c>).</summary>
    public bool Builds(string mission) => Missions == null || Missions.Contains(mission, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A target profile: what an export builds for one kind of machine. Profiles are build sources: the project's
/// <c>gamegen/build-profiles/&lt;name&gt;.json</c> files, plus the built-in <see cref="Original"/> and <see cref="Modern"/>.
/// <see cref="Status"/> says whether its numbers were measured in the game or are still a guess.
/// </summary>
public sealed record BuildProfile(string Name, string Description, string Status, IReadOnlyList<BuildProfilePack> TexturePacks, string? Source = null, bool IsDefault = false);

public static class BuildProfiles
{
    public const string Folder = "gamegen/build-profiles", Format = "recoil-build-profile";
    public const int MaximumPacks = 16, MaximumProfiles = 64;
    private const long MiB = 1024 * 1024;

    /// <summary>
    /// The packs each mission shipped with (both releases): Direct3D packs for 2 and 4 MB of texture memory and the software
    /// packs texture2 and texture4 everywhere, texture6 for the campaign missions and m13, and texture8 for m6 alone.
    /// </summary>
    public static BuildProfile Original { get; } = new("original", "The texture packs each mission shipped with: rtexture2 and rtexture4 for 2 and 4 MB cards (256-texel textures), the software packs texture2 and texture4, texture6 for m1–m6 and m13, and texture8 for m6.", "measured",
        [new("rtexture2.zbd", 2 * MiB, 256), new("rtexture4.zbd", 4 * MiB, 256), new("texture2.zbd", 2 * MiB, 1024), new("texture4.zbd", 4 * MiB, 1024),
            new("texture6.zbd", 6 * MiB, 1024) { Missions = ["m1", "m2", "m3", "m4", "m5", "m6", "m13"] }, new("texture8.zbd", 8 * MiB, 1024) { Missions = ["m6"] }]);
    /// <summary>
    /// The automatic pack's default largest size: the game is a 32-bit process, and Direct3D keeps a system-memory copy of
    /// every texture beside its surface, so a pack costs a multiple of its size in the 2 GiB address space (not yet measured).
    /// </summary>
    public const int AutomaticMegabytes = 256;
    /// <summary>
    /// The original packs plus larger tiers for modern cards: the game opens rtexture&lt;its reported texture memory in MB&gt;,
    /// counting down, so rtexture8 and rtexture16 give every texture at up to 1024 texels, and the automatic pack holds them
    /// at full size once they need more. Not yet measured in the game.
    /// </summary>
    public static BuildProfile Modern { get; } = new("modern", $"The original packs in every mission plus rtexture8 and rtexture16 (textures up to 1024 texels) for modern cards, a larger rtexture pack holding every texture at full size when the textures need more than 16 MB (at most {AutomaticMegabytes} MB), and texture8 and texturemax for the software renderer. Experimental until measured in the game.", "experimental",
        [.. Original.TexturePacks.Select(p => p with { Missions = null }).Where(p => p.File != "texture8.zbd"), new("rtexture8.zbd", 8 * MiB, 1024), new("rtexture16.zbd", 16 * MiB, 1024),
            new(BuildProfilePack.AutomaticFile, AutomaticMegabytes * MiB, 1024), new("texture8.zbd", 8 * MiB, 1024), new("texturemax.zbd", null, 1024)]);

    /// <summary>
    /// The automatic Direct3D pack of a mission whose textures need <paramref name="memory"/> bytes of texture memory at full
    /// size (two bytes a texel: Direct3D keeps opaque, colour-keyed and alpha textures as 565, 1555 and 4444):
    /// <c>rtexture&lt;N&gt;</c>, N that memory in MiB rounded up to a power of two and at most the pack's budget. It holds every
    /// texture at full size unless they need more than the budget, and it exists only when it is larger than every fixed
    /// rtexture pack the mission gets (the game opens the largest that the card's reported memory allows).
    /// </summary>
    public static TexturePackVariant? AutomaticPack(BuildProfile profile, string mission, long memory)
    {
        if (profile.TexturePacks.FirstOrDefault(p => p.Automatic && p.Builds(mission)) is not { BudgetBytes: long budget } automatic) return null;
        long largest = profile.TexturePacks.Where(p => !p.Automatic && p.Builds(mission)).Select(p => TexturePackVariant.FromFileName(p.File))
            .Where(v => v is { Kind: TexturePackKind.Hardware }).Select(v => v!.BudgetBytes!.Value / MiB).DefaultIfEmpty(0).Max();
        long needed = Math.Max(1, (memory + MiB - 1) / MiB), megabytes = 1;
        while (megabytes < needed) megabytes *= 2;
        megabytes = Math.Min(megabytes, budget / MiB);
        if (needed <= largest || megabytes <= largest) return null;
        return new($"rtexture{megabytes}.zbd", TexturePackKind.Hardware, memory <= budget ? null : budget, automatic.MaximumDimension);
    }

    /// <summary>Every profile of a project: the built-in ones, replaced or joined by the project's files, in name order.</summary>
    public static IReadOnlyList<BuildProfile> List(string root, Func<string, byte[]?>? read = null, IEnumerable<string>? files = null)
    {
        Dictionary<string, BuildProfile> profiles = new(StringComparer.OrdinalIgnoreCase) { [Original.Name] = Original, [Modern.Name] = Modern };
        foreach (string path in ProfileFiles(root, files))
        {
            var profile = Parse(Path.GetFileNameWithoutExtension(path), ReadProfile(root, path, read), path);
            profiles[profile.Name] = profile;
        }
        var defaults = profiles.Values.Where(p => p.IsDefault).ToArray();
        if (defaults.Length > 1) throw new InvalidDataException($"Several build profiles are the default ({string.Join(", ", defaults.Select(d => d.Source))}); mark only one.");
        if (defaults.Length == 0) profiles[Modern.Name] = profiles[Modern.Name] with { IsDefault = true };
        return profiles.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// The named profile, reading only its own file, so another malformed profile does not block it; the project's
    /// default (which needs every file) when null.
    /// </summary>
    public static BuildProfile Find(string root, string? name, Func<string, byte[]?>? read = null, IEnumerable<string>? files = null)
    {
        if (name == null) return List(root, read, files).Single(p => p.IsDefault);
        var paths = ProfileFiles(root, files);
        if (paths.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase)) is { } own)
            return Parse(Path.GetFileNameWithoutExtension(own), ReadProfile(root, own, read), own);
        if (name.Equals(Original.Name, StringComparison.OrdinalIgnoreCase)) return Original;
        if (name.Equals(Modern.Name, StringComparison.OrdinalIgnoreCase)) return Modern;
        throw new InvalidDataException($"The project has no build profile {name} ({string.Join(", ", paths.Select(Path.GetFileNameWithoutExtension).Append(Original.Name).Append(Modern.Name).Distinct(StringComparer.OrdinalIgnoreCase))}).");
    }
    /// <summary>Whether the project has a profile of that name (built in, or a file of it), however its file reads.</summary>
    public static bool Exists(string root, string name, IEnumerable<string>? files = null) =>
        name.Equals(Original.Name, StringComparison.OrdinalIgnoreCase) || name.Equals(Modern.Name, StringComparison.OrdinalIgnoreCase)
        || (files ?? SourceProject.Files(root, Folder, n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            .Any(p => p.StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase) && !p[(Folder.Length + 1)..].Contains('/') && Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase));
    /// <summary>The profile files: .json files directly in the profiles folder (sub-folders are not profiles).</summary>
    private static IReadOnlyList<string> ProfileFiles(string root, IEnumerable<string>? files)
    {
        var paths = (files ?? SourceProject.Files(root, Folder, n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            .Where(p => p.StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase) && !p[(Folder.Length + 1)..].Contains('/')).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length > MaximumProfiles) throw new InvalidDataException($"{Folder} holds more than {MaximumProfiles} profiles.");
        return paths;
    }
    private static byte[] ReadProfile(string root, string path, Func<string, byte[]?>? read)
    {
        if (read != null) return read(path) ?? throw new InvalidDataException($"{path} does not exist.");
        var info = new FileInfo(SourceProject.Resolve(root, path));
        if (info.Length > 64 * 1024) throw new InvalidDataException($"{path} is larger than 64 KB.");
        return File.ReadAllBytes(info.FullName);
    }

    /// <summary>A profile file: <c>{ "format": "recoil-build-profile", "version": 1, "description", "status", "default", "texturePacks": [ { "file", "budgetMiB", "maximumDimension", "missions" } ] }</c>.</summary>
    public static BuildProfile Parse(string name, ReadOnlySpan<byte> json, string source)
    {
        if (name.Length is 0 or > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new InvalidDataException($"{source}: a profile name has 1–64 letters, digits, - or _.");
        if (json.Length > 64 * 1024) throw new InvalidDataException($"{source} is larger than 64 KB.");
        JsonObject root;
        try { root = JsonNode.Parse(json.ToArray(), documentOptions: new() { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? throw new InvalidDataException($"{source} is not a JSON object."); }
        catch (JsonException ex) { throw new InvalidDataException($"{source} is not valid JSON: {ex.Message}", ex); }
        if (Text(root, "format") != Format) throw new InvalidDataException($"{source}: format must be \"{Format}\".");
        if (root["version"] is not JsonValue v || !v.TryGetValue(out int version) || version != 1) throw new InvalidDataException($"{source}: only version 1 is known.");
        string description = root["description"] is null ? "" : Text(root, "description");
        if (description.Length > 1000) throw new InvalidDataException($"{source}: the description is longer than 1000 characters.");
        string status = root["status"] is null ? "experimental" : Text(root, "status");
        if (status is not ("measured" or "experimental")) throw new InvalidDataException($"{source}: status is measured or experimental.");
        bool isDefault = root["default"] is JsonValue d && d.TryGetValue(out bool flag) ? flag : root["default"] is null ? false : throw new InvalidDataException($"{source}: default is true or false.");
        if (root["texturePacks"] is not JsonArray packs || packs.Count is 0 or > MaximumPacks) throw new InvalidDataException($"{source}: texturePacks lists 1–{MaximumPacks} packs.");
        List<BuildProfilePack> list = [];
        foreach (var entry in packs)
        {
            if (entry is not JsonObject pack) throw new InvalidDataException($"{source}: each texture pack is an object.");
            string file = Text(pack, "file").ToLowerInvariant();
            // rtexture*.zbd is the automatic pack: rtexture<N>, sized per mission, at most budgetMiB (a whole number of MiB).
            bool automatic = file == BuildProfilePack.AutomaticFile;
            var variant = automatic ? new TexturePackVariant(file, TexturePackKind.Hardware, AutomaticMegabytes * MiB, 1024) : TexturePackVariant.FromFileName(file);
            // The game opens the names it formats (rtexture8.zbd, never rtexture08.zbd).
            if (variant == null || variant.Kind == TexturePackKind.Interface || !automatic && !System.Text.RegularExpressions.Regex.IsMatch(file, @"\A(r?texture[1-9][0-9]*|texturemax)\.zbd\z"))
                throw new InvalidDataException($"{source}: {file} is not a mission texture pack (rtexture<N>.zbd, texture<N>.zbd, texturemax.zbd, or rtexture*.zbd for the automatic one).");
            if (list.Any(p => p.File == file)) throw new InvalidDataException($"{source}: {file} is listed twice.");
            // An explicit null (which JsonObject returns as a present key with a null value) keeps every texture at full size.
            long? budget = !pack.TryGetPropertyValue("budgetMiB", out var budgetNode) ? variant.BudgetBytes : budgetNode switch
            {
                JsonValue b when automatic && b.TryGetValue(out int whole) && whole is >= 1 and <= 1024 => whole * MiB,
                _ when automatic => throw new InvalidDataException($"{source}: {file} budgetMiB is the largest pack it makes, a whole number from 1 to 1024."),
                null => null,
                JsonValue b when b.TryGetValue(out double mib) && mib is >= 0.25 and <= 1024 => (long)(mib * MiB),
                _ => throw new InvalidDataException($"{source}: {file} budgetMiB is null (full size) or 0.25–1024."),
            };
            // The software renderer skips textures wider than 1024 texels; Direct3D takes what the device reports.
            int largest = variant.Kind == TexturePackKind.Software ? TexturePackBuilder.SoftwareMaximumDimension : 4096;
            int dimension = pack["maximumDimension"] is null ? variant.MaximumDimension
                : pack["maximumDimension"] is JsonValue m && m.TryGetValue(out int side) && side >= TexturePackBuilder.MinimumDimension && side <= largest && (side & (side - 1)) == 0 ? side
                : throw new InvalidDataException($"{source}: {file} maximumDimension is a power of two from {TexturePackBuilder.MinimumDimension} to {largest}{(variant.Kind == TexturePackKind.Software ? " (the software renderer does not draw wider textures)" : "")}.");
            // Optional: the mission folders that get the pack (["m6"]); without it, every mission does.
            IReadOnlyList<string>? missions = null;
            if (pack.TryGetPropertyValue("missions", out var missionsNode))
            {
                if (missionsNode is not JsonArray array || array.Count is 0 or > 64) throw new InvalidDataException($"{source}: {file} missions lists 1–64 mission folders.");
                missions = [.. array.Select(m => m is JsonValue mv && mv.TryGetValue(out string? folder) && System.Text.RegularExpressions.Regex.IsMatch(folder, @"\Am[1-9][0-9]{0,2}\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    ? folder.ToLowerInvariant() : throw new InvalidDataException($"{source}: {file} missions are mission folders such as \"m6\".")).Distinct()];
            }
            list.Add(new(file, budget, dimension) { Missions = missions });
        }
        // Every mission needs one: without an rtexture pack, Direct3D falls back to the paletted software packs and refuses them.
        if (!list.Any(p => p.Variant.Kind == TexturePackKind.Hardware && p.Missions == null))
            throw new InvalidDataException($"{source}: list at least one rtexture<N>.zbd pack for every mission (without \"missions\"); the game's Direct3D renderer reads only those.");
        return new(name, description, status, list, source, isDefault);
    }
    private static string Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : throw new InvalidDataException($"{key} must be a string.");

    /// <summary>
    /// Texture packs a destination mission folder holds that the export does not build (<paramref name="built"/>: the pack
    /// files it plans for the mission): the game opens the largest rtexture&lt;N&gt; at or below its texture memory, so a
    /// stale pack of any size, such as an automatic pack of an earlier export, can be chosen instead of the exported ones;
    /// the software renderer opens texture&lt;N&gt; or texturemax by its option, so a stale one of those can be too.
    /// </summary>
    public static IReadOnlyList<string> ShadowingPacks(string destination, string mission, IReadOnlyCollection<string> built)
    {
        string folder = Path.Combine(destination, mission);
        if (!Directory.Exists(folder)) return [];
        // Software packs too: the software renderer opens texture<N> or texturemax by its option, whatever the export built.
        return Directory.EnumerateFiles(folder, "*texture*.zbd").Select(Path.GetFileName).OfType<string>()
            .Where(f => TexturePackVariant.FromFileName(f) is { Kind: TexturePackKind.Hardware or TexturePackKind.Software } && !built.Contains(f, StringComparer.OrdinalIgnoreCase))
            .Select(f => $"{mission}/{f.ToLowerInvariant()}").Order(StringComparer.Ordinal).ToArray();
    }
}
