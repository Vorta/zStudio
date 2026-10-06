using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Where texture sources belong. The original build sorted pack records by their full source path, so each mission pack
/// is a sequence of sorted runs, one per texture folder: common effects, common, the mission (with its <c>bft\</c>
/// subfolder sorting inline), or, for multiplayer missions, common effects, the shared multiplayer vehicle, common and
/// the mission. Interface images in image.zbd are placed by the folders their resources name.
/// </summary>
public static class TextureSources
{
    public const string EffectsTextures = "data/common/effects/textures", CommonTextures = "data/common/textures", MultiBftTextures = "data/common/multi_bft/textures";
    public const string Fonts = "data/common/fonts", Images = "data/common/images", HudImages = "data/common/images/hud";
    public const string Extension = ".png";
    private const int MaximumCachedSizes = 65536;
    /// <summary>The largest texture side a build decodes (<c>SourceBuilder.DecodeTexture</c>).</summary>
    internal const int MaximumSide = 4096;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime Written, DateTime Created, int Width, int Height)> Sizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A PNG's size from its header, cached by the file's length and time, so planning an export reads each image's
    /// header once; null when the file is missing, unreadable, does not start with a PNG header or is larger than a
    /// build decodes (<see cref="MaximumSide"/>; the build reports it).
    /// </summary>
    internal static (int Width, int Height)? PngSize(string path) => PngSize(path, true);
    /// <summary>
    /// <see cref="PngSize(string)"/>, optionally without the cache: a build reads the header of each image it decodes
    /// anyway, which is not what planning costs (<see cref="HeaderRead"/>).
    /// </summary>
    internal static (int Width, int Height)? PngSize(string path, bool cache)
    {
        Span<byte> header = stackalloc byte[24];
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
            // A copy keeps the write time but gets a new creation time, so both identify the file's content.
            if (cache && Sizes.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc && cached.Created == info.CreationTimeUtc) return (cached.Width, cached.Height);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            if (stream.ReadAtLeast(header, header.Length, false) < header.Length) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (PngSize((ReadOnlySpan<byte>)header) is not var (width, height)) return null;
        if (!cache) return (width, height);
        if (Sizes.Count >= MaximumCachedSizes) Sizes.Clear();
        Sizes[path] = (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, width, height);
        return (width, height);
    }
    /// <summary>A PNG's size from the header at the start of <paramref name="png"/>, as <see cref="PngSize(string)"/> reads it.</summary>
    internal static (int Width, int Height)? PngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !png[..8].SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]) || !png[12..16].SequenceEqual("IHDR"u8)) return null;
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[16..]), height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[20..]);
        if (width is < 1 or > MaximumSide || height is < 1 or > MaximumSide) return null;
        return (width, height);
    }

    /// <summary>Whether <see cref="PngSize"/> read the header of <paramref name="path"/> (what planning cost, for tests).</summary>
    internal static bool HeaderRead(string path) => Sizes.ContainsKey(path);

    /// <summary>Folders searched for a mission's textures, as <c>support\common.gw</c> sets them.</summary>
    public static IReadOnlyList<string> MissionFolders(string mission, bool multiplayer) => multiplayer
        ? [EffectsTextures, MultiBftTextures, CommonTextures, $"data/{mission}/textures"]
        : [EffectsTextures, CommonTextures, $"data/{mission}/textures", $"data/{mission}/textures/bft"];

    /// <summary>The key the original build sorted pack records by: the lowercase source path.</summary>
    public static string SortKey(string relativePath) => relativePath.Replace('/', '\\').ToLowerInvariant();

    /// <summary>Consecutive runs of ascending names (lowercase, ordinal) in pack record order.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Runs(IReadOnlyList<string> names)
    {
        List<List<string>> runs = [];
        foreach (string name in names.Select(n => n.ToLowerInvariant()))
        {
            if (runs.Count == 0 || string.CompareOrdinal(name, runs[^1][^1]) < 0) runs.Add([]);
            runs[^1].Add(name);
        }
        return runs;
    }

    /// <summary>
    /// Folder of each texture in a mission pack, by run position: [effects, common, mission+bft, mission] for campaign
    /// missions and [effects, multiplayer vehicle, common, mission] for multiplayer ones. A campaign mission's third run
    /// holds the mission textures that sort before <c>bft\</c> and then the <c>bft\</c> folder. Packs whose runs do not
    /// follow the layout place everything in the mission folder and say so.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PlaceMission(string mission, IReadOnlyList<string> names, bool multiplayer, List<string> notes)
    {
        var runs = Runs(names); Dictionary<string, string> folders = new(StringComparer.OrdinalIgnoreCase);
        string missionFolder = $"data/{mission}/textures";
        if (runs.Count != 4)
        {
            notes.Add($"{mission}: texture records form {runs.Count} sorted runs instead of 4; all were placed in {missionFolder}.");
            foreach (string name in names) folders.TryAdd(name, missionFolder);
            return folders;
        }
        for (int r = 0; r < 4; r++)
            foreach (string name in runs[r])
            {
                string folder = (multiplayer, r) switch
                {
                    (_, 0) => EffectsTextures,
                    (true, 1) => MultiBftTextures,
                    (true, 2) => CommonTextures,
                    (false, 1) => CommonTextures,
                    // data\mN\textures\name.tif sorts before data\mN\textures\bft\ exactly when "name." < "bft\".
                    (false, 2) => string.CompareOrdinal(name + ".", "bft\\") < 0 ? missionFolder : missionFolder + "/bft",
                    _ => missionFolder
                };
                folders.TryAdd(name, folder);
            }
        return folders;
    }

    /// <summary>
    /// Folder of each interface image, from <paramref name="names"/> in pack order: the IMAGE_PATH active where a resource
    /// names it (the most frequent), else the first mission whose objectives name it, else fonts (fonts.zrd) or HUD
    /// (hud.zrd, pickup.zrd). An image no resource names takes the folder most of its sorted run has, since the original
    /// build ordered records by source path; otherwise it stays in the images root recorded by fmv.zrd. The fonts and
    /// HUD folder names are inferred; the others are recorded.
    /// </summary>
    /// <remarks>A name stored more than once (image.zbd has eight) was a file in more than one folder: each copy takes its run's folder.</remarks>
    public static IReadOnlyList<string> PlaceImages(IReadOnlyList<string> names, IReadOnlyList<(string Archive, string Member, ZrdNode Tree)> resources)
    {
        HashSet<string> wanted = new(names.Select(n => n.ToLowerInvariant()));
        Dictionary<string, Dictionary<string, int>> paths = [];
        Dictionary<string, int> objectiveMission = [];
        HashSet<string> fonts = [], hud = [];
        foreach (var (archive, member, tree) in resources.OrderBy(r => MissionNumber(r.Archive)).ThenBy(r => r.Archive, StringComparer.Ordinal).ThenBy(r => r.Member, StringComparer.Ordinal))
        {
            string file = member.ToLowerInvariant(); int mission = MissionNumber(archive);
            Walk(tree, null, 0);
            void Walk(ZrdNode node, string? imagePath, int depth)
            {
                if (depth > 64) return;
                var children = node.Children;
                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    if (child.Kind == ZrdKind.String && child.Text == "IMAGE_PATH" && i + 1 < children.Count && children[i + 1] is { Kind: ZrdKind.Array, Children: [{ Kind: ZrdKind.String } path, ..] })
                        imagePath = RecordedFolder(path.Text) ?? imagePath;
                    else if (child.Kind == ZrdKind.String && wanted.Contains(child.Text.ToLowerInvariant()))
                    {
                        string name = child.Text.ToLowerInvariant();
                        if (imagePath != null) { var counts = paths.TryGetValue(name, out var c) ? c : paths[name] = []; counts[imagePath] = counts.GetValueOrDefault(imagePath) + 1; }
                        if (file == "objectives.zrd" && mission > 0) objectiveMission.TryAdd(name, mission);
                        if (file == "fonts.zrd") fonts.Add(name);
                        if (file is "hud.zrd" or "pickup.zrd") hud.Add(name);
                    }
                    else if (child.Kind == ZrdKind.Array) Walk(child, imagePath, depth + 1);
                }
            }
        }
        Dictionary<string, string> evidence = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in wanted)
        {
            string? folder = paths.TryGetValue(name, out var counts) ? counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).First().Key
                : objectiveMission.TryGetValue(name, out int m) ? $"data/m{m}/images"
                : fonts.Contains(name) ? Fonts : hud.Contains(name) ? HudImages : null;
            if (folder != null) evidence[name] = folder;
        }
        var repeated = names.GroupBy(n => n.ToLowerInvariant()).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        List<string> folders = [];
        foreach (var run in Runs(names))
        {
            string runFolder = run.Where(n => evidence.ContainsKey(n) && !repeated.Contains(n)).GroupBy(n => evidence[n]).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault()?.Key ?? Images;
            foreach (string name in run) folders.Add(repeated.Contains(name) ? runFolder : evidence.GetValueOrDefault(name) ?? runFolder);
        }
        return folders;
    }

    /// <summary><c>..\data\common\images\dialog\briefing</c> → <c>data/common/images/dialog/briefing</c>; null unless it is a relative data path.</summary>
    internal static string? RecordedFolder(string recorded)
    {
        string path = recorded.Replace('\\', '/').TrimEnd('/');
        while (path.StartsWith("../", StringComparison.Ordinal)) path = path[3..];
        if (!path.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        return "data/" + string.Join('/', parts[1..]);
    }

    /// <summary>N for an <c>mN/…</c> path, 0 otherwise.</summary>
    public static int MissionNumber(string relative)
    {
        string first = relative.Replace('\\', '/').Split('/')[0];
        return first.Length > 1 && (first[0] is 'm' or 'M') && int.TryParse(first.AsSpan(1), out int n) && n > 0 ? n : 0;
    }
}
