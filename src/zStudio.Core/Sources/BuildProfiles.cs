using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A texture pack a profile builds: its file, texel budget (null: every texture at full size) and largest texture side.</summary>
public sealed record BuildProfilePack(string File, long? BudgetBytes, int MaximumDimension)
{
    public TexturePackVariant Variant => TexturePackVariant.FromFileName(File) is { } named ? named with { BudgetBytes = BudgetBytes, MaximumDimension = MaximumDimension }
        : throw new InvalidDataException($"{File} is not a texture pack name.");
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

    /// <summary>The packs the retail game shipped with: Direct3D packs for 2 and 4 MB of texture memory, and the software packs.</summary>
    public static BuildProfile Original { get; } = new("original", "The texture packs the game shipped with: rtexture2 and rtexture4 for 2 and 4 MB cards (256-texel textures) and the software packs.", "measured",
        [new("rtexture2.zbd", 2 * MiB, 256), new("rtexture4.zbd", 4 * MiB, 256), new("texture2.zbd", 2 * MiB, 1024), new("texture4.zbd", 4 * MiB, 1024), new("texture6.zbd", 6 * MiB, 1024)]);
    /// <summary>
    /// The original packs plus larger tiers for modern cards: the game opens rtexture&lt;its reported texture memory in MB&gt;,
    /// counting down, so rtexture8 and rtexture16 give every texture at up to 1024 texels. Not yet measured in the game.
    /// </summary>
    public static BuildProfile Modern { get; } = new("modern", "The original packs plus rtexture8 and rtexture16 (textures up to 1024 texels) for modern cards, and texture8 and texturemax for the software renderer. Experimental until measured in the game.", "experimental",
        [.. Original.TexturePacks, new("rtexture8.zbd", 8 * MiB, 1024), new("rtexture16.zbd", 16 * MiB, 1024), new("texture8.zbd", 8 * MiB, 1024), new("texturemax.zbd", null, 1024)]);

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

    /// <summary>A profile file: <c>{ "format": "recoil-build-profile", "version": 1, "description", "status", "default", "texturePacks": [ { "file", "budgetMiB", "maximumDimension" } ] }</c>.</summary>
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
            var variant = TexturePackVariant.FromFileName(file);
            // The game opens the names it formats (rtexture8.zbd, never rtexture08.zbd).
            if (variant == null || variant.Kind == TexturePackKind.Interface || !System.Text.RegularExpressions.Regex.IsMatch(file, @"\A(r?texture[1-9][0-9]*|texturemax)\.zbd\z"))
                throw new InvalidDataException($"{source}: {file} is not a mission texture pack (rtexture<N>.zbd, texture<N>.zbd or texturemax.zbd).");
            if (list.Any(p => p.File == file)) throw new InvalidDataException($"{source}: {file} is listed twice.");
            // An explicit null (which JsonObject returns as a present key with a null value) keeps every texture at full size.
            long? budget = !pack.TryGetPropertyValue("budgetMiB", out var budgetNode) ? variant.BudgetBytes : budgetNode switch
            {
                null => null,
                JsonValue b when b.TryGetValue(out double mib) && mib is >= 0.25 and <= 1024 => (long)(mib * MiB),
                _ => throw new InvalidDataException($"{source}: {file} budgetMiB is null (full size) or 0.25–1024."),
            };
            int dimension = pack["maximumDimension"] is null ? variant.MaximumDimension
                : pack["maximumDimension"] is JsonValue m && m.TryGetValue(out int side) && side is >= TexturePackBuilder.MinimumDimension and <= 4096 && (side & (side - 1)) == 0 ? side
                : throw new InvalidDataException($"{source}: {file} maximumDimension is a power of two from {TexturePackBuilder.MinimumDimension} to 4096.");
            list.Add(new(file, budget, dimension));
        }
        if (!list.Any(p => TexturePackVariant.FromFileName(p.File)!.Kind == TexturePackKind.Hardware))
            throw new InvalidDataException($"{source}: list at least one rtexture<N>.zbd pack; the game's Direct3D renderer reads only those.");
        return new(name, description, status, list, source, isDefault);
    }
    private static string Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : throw new InvalidDataException($"{key} must be a string.");

    /// <summary>
    /// Hardware packs a destination mission folder holds that the profile does not build: the game opens the largest
    /// rtexture&lt;N&gt; at or below its texture memory, so a stale pack of any size the profile leaves out can be chosen
    /// instead of the exported ones.
    /// </summary>
    public static IReadOnlyList<string> ShadowingPacks(string destination, string mission, BuildProfile profile)
    {
        string folder = Path.Combine(destination, mission);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "rtexture*.zbd").Select(Path.GetFileName).OfType<string>()
            .Where(f => TexturePackVariant.FromFileName(f) is { Kind: TexturePackKind.Hardware } && !profile.TexturePacks.Any(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase)))
            .Select(f => $"{mission}/{f.ToLowerInvariant()}").Order(StringComparer.Ordinal).ToArray();
    }
}
