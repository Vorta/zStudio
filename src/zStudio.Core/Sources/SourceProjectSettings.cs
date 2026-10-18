using System.Text.Json;
using System.Text.Json.Nodes;
using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

/// <summary>How the project was last viewed: Diagnostics shown (and its height) and the mission difficulty; null when not remembered.</summary>
public sealed record SourceProjectView(bool? Diagnostics, double? DiagnosticsHeight, MissionDifficulty? Difficulty);

/// <summary>
/// What zStudio remembers for a source project (user decision): <c>.zstudio/settings.json</c>, a hidden folder beside
/// <c>data</c> and <c>gamegen</c>, holding the export's texture dithering and how the project was last viewed. Only the
/// workspace reads it, passing the dithering to exports and checks; builds never read the folder. A write keeps the
/// file's other keys.
/// </summary>
public static class SourceProjectSettings
{
    public const string Folder = ".zstudio", File = Folder + "/settings.json";
    private const string DitheringKey = "textureDithering", DiagnosticsKey = "diagnostics", DiagnosticsHeightKey = "diagnosticsHeight", DifficultyKey = "difficulty";
    /// <summary>The largest settings file read.</summary>
    internal const int MaximumFileBytes = 64 * 1024;

    /// <summary>The project's texture dithering: None when the file or its value is absent; a malformed file is refused.</summary>
    public static TextureDithering ReadDithering(string root)
    {
        JsonObject? settings;
        // Reads wait for a write the workspace is making: its replacing move must not meet an open reader.
        lock (Gate) settings = Read(root);
        if (settings?[DitheringKey] is not { } value) return TextureDithering.None;
        return value is JsonValue text && text.TryGetValue(out string? name) && Parse(name) is { } dithering ? dithering
            : throw new InvalidDataException($"{File}: {DitheringKey} is none, ordered or diffusion.");
    }

    /// <summary>Remembers the project's texture dithering.</summary>
    public static void WriteDithering(string root, TextureDithering dithering) => Update(root, settings => settings[DitheringKey] = Name(dithering), replaceDamaged: true);

    /// <summary>How the project was last viewed. A missing or unreadable file, or a value of another kind, is simply not remembered.</summary>
    public static SourceProjectView ReadView(string root)
    {
        JsonObject? settings;
        try { lock (Gate) settings = Read(root); }
        catch (InvalidDataException) { settings = null; }
        bool? diagnostics = settings?[DiagnosticsKey] is JsonValue shown && shown.TryGetValue(out bool b) ? b : null;
        double? height = settings?[DiagnosticsHeightKey] is JsonValue size && size.TryGetValue(out double h) && double.IsFinite(h) ? h : null;
        MissionDifficulty? difficulty = settings?[DifficultyKey] is JsonValue level && level.TryGetValue(out string? name)
            && Enum.TryParse<MissionDifficulty>(name, ignoreCase: false, out var d) && Enum.IsDefined(d) ? d : null;
        return new(diagnostics, height, difficulty);
    }

    /// <summary>Remembers how the project is viewed: Diagnostics shown or hidden, with its height.</summary>
    public static void WriteDiagnostics(string root, bool shown, double? height) => Update(root, settings =>
    {
        settings[DiagnosticsKey] = shown;
        if (height is double h && double.IsFinite(h)) settings[DiagnosticsHeightKey] = h;
    });

    /// <summary>Remembers the mission difficulty the project was last viewed at.</summary>
    public static void WriteDifficulty(string root, MissionDifficulty difficulty) => Update(root, settings => settings[DifficultyKey] = difficulty.ToString());

    /// <summary>The name a setting or MCP uses for a dithering mode.</summary>
    public static string Name(TextureDithering dithering) => dithering switch
    {
        TextureDithering.Ordered => "ordered",
        TextureDithering.Diffusion => "diffusion",
        _ => "none",
    };
    public static TextureDithering? Parse(string? name) => name?.ToLowerInvariant() switch
    {
        "none" => TextureDithering.None,
        "ordered" => TextureDithering.Ordered,
        "diffusion" => TextureDithering.Diffusion,
        _ => null,
    };

    // Read-modify-write is serialized: two changes (a view setting and the dithering) never lose one another.
    private static readonly Lock Gate = new();
    /// <summary>
    /// Changes the settings file, keeping its other keys, in the hidden folder. A file that cannot be read is replaced only by
    /// an explicit choice (<paramref name="replaceDamaged"/>, the dithering); remembering the view leaves it as it is.
    /// </summary>
    /// <summary>
    /// Hides the project's .zstudio folder that holds <paramref name="path"/> (working data is created there before any
    /// setting may be): where the file system allows it, and never failing the work that created it.
    /// </summary>
    internal static void HideFolderOf(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
        {
            if (!directory.Name.Equals(Folder, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (OperatingSystem.IsWindows() && directory.Exists && !directory.Attributes.HasFlag(FileAttributes.Hidden)) directory.Attributes |= FileAttributes.Hidden; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return;
        }
    }
    private static void Update(string root, Action<JsonObject> change, bool replaceDamaged = false)
    {
        lock (Gate) UpdateLocked(root, change, replaceDamaged);
    }
    private static void UpdateLocked(string root, Action<JsonObject> change, bool replaceDamaged)
    {
        JsonObject settings;
        try { settings = Read(root) ?? []; }
        catch (InvalidDataException) when (replaceDamaged) { settings = []; }
        change(settings);
        string path = Path(root), folder = System.IO.Path.GetDirectoryName(path)!;
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"{root} no longer exists; its settings were not written.");
        SourceProject.RejectLinks(folder);
        var created = Directory.CreateDirectory(folder);
        // Hidden where the file system allows it; a share that refuses the attribute still keeps the settings.
        try { if (OperatingSystem.IsWindows() && !created.Attributes.HasFlag(FileAttributes.Hidden)) created.Attributes |= FileAttributes.Hidden; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            System.IO.File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            System.IO.File.Move(temporary, path, overwrite: true);
        }
        catch { try { System.IO.File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
    }

    private static string Path(string root) => System.IO.Path.Combine(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)), File);
    private static JsonObject? Read(string root)
    {
        string path = Path(root);
        SourceProject.RejectLinks(System.IO.Path.GetDirectoryName(path)!);
        if (!System.IO.File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{path} is a link; zStudio reads its settings only from a regular file.");
        if (info.Length > MaximumFileBytes) throw new InvalidDataException($"{File} is larger than {MaximumFileBytes / 1024} KB.");
        try
        {
            // A file saved by an editor may start with a UTF-8 byte order mark.
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            var text = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes.AsSpan(3) : bytes;
            var settings = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException($"{File} is not a JSON object.");
            // Duplicate keys surface only when the object is first used.
            _ = settings.Count;
            return settings;
        }
        catch (JsonException ex) { throw new InvalidDataException($"{File} is not valid JSON: {ex.Message}", ex); }
        catch (ArgumentException ex) { throw new InvalidDataException($"{File} names a setting twice: {ex.Message}", ex); }
    }
}
