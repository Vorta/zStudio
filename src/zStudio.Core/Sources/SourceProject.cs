using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// A reconstructed RECOIL source tree. <c>data/</c> and <c>gamegen/</c> mirror the original build layout; <c>.zstudio/</c>
/// holds what the sources cannot express (record order, residue bytes, timestamps) so unedited sources pack byte-identically.
/// </summary>
public sealed record SourceProjectManifest
{
    public const string FileName = "zstudio-project.json", FormatName = "zstudio-source-project";
    public const int CurrentVersion = 1;
    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = CurrentVersion;
    public string Game { get; init; } = "RECOIL";
    /// <summary>Unique per reconstruction; pack folders record it so one project never overwrites the output of another.</summary>
    public Guid Id { get; init; }
    public required SourceOrigin Origin { get; init; }
    public required IReadOnlyList<SourceOutput> Outputs { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}
/// <summary>The shipped file set a project was reconstructed from. The name is a label; the fingerprint identifies the bytes.</summary>
public sealed record SourceOrigin(string Name, int Files, long Bytes, string Fingerprint);
/// <summary>One shipped file and how it is rebuilt. Family: archive, scripts or passthrough.</summary>
public sealed record SourceOutput(string Path, string Family, string Sha256, long Bytes, string Layout);

public sealed record ArchiveLayout(IReadOnlyList<ArchiveLayoutRecord> Records);
/// <summary>
/// A ZAR member. Encoding is <c>zrd-text</c> (compiled from a text source), <c>verbatim</c> (source bytes) or
/// <c>derived</c> (a cached variant of its source, valid while the source hash matches). Name/source-path fields keep
/// their exact 64 stored bytes when they are not a plain zero-padded string (sound banks store uninitialized memory).
/// </summary>
public sealed record ArchiveLayoutRecord(string Name, string Encoding, string Source, string SourceSha256, uint Aux, ulong FileTime)
{
    public string? NameField { get; init; }
    public string? SourcePath { get; init; }
    public string? SourceField { get; init; }
    public string? Cache { get; init; }
}

public sealed record ScriptLayout(string Header, string PrefixGap, string Tail, IReadOnlyList<ScriptLayoutEntry> Entries);
/// <summary>A prepared script; its original time and per-instruction padding apply only while the source is unchanged.</summary>
public sealed record ScriptLayoutEntry(string Name, string Source, string SourceSha256, uint FileTime, string DirectoryRecord, string Following)
{
    public IReadOnlyList<ScriptLayoutPadding> Padding { get; init; } = [];
}
public sealed record ScriptLayoutPadding(int Instruction, string Bytes);

public static class SourceProject
{
    public const string DataFolder = "data", GameGenFolder = "gamegen", MetadataFolder = ".zstudio";
    /// <summary>Text sources are bounded before they are decoded; retail sources are at most a few hundred kilobytes.</summary>
    public const int MaximumSourceTextBytes = 16 * 1024 * 1024;
    internal static readonly string[] Families = ["archive", "scripts", "passthrough"];
    internal static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string ManifestPath(string root) => System.IO.Path.Combine(root, SourceProjectManifest.FileName);
    public static bool IsProject(string? root) => root != null && File.Exists(ManifestPath(root));
    public static string CachePath(string root, string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigitLower)) throw new InvalidDataException($"'{sha256}' is not a SHA-256 cache identity.");
        return Resolve(root, $"{MetadataFolder}/cache/{sha256[..2]}/{sha256}");
    }

    /// <summary>Resolve a project-relative path (forward slashes), refusing anything that would escape the project.</summary>
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException($"'{relative}' is not a project-relative path.");
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        string prefix = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"'{relative}' escapes the project.");
        return full;
    }

    public static async Task<SourceProjectManifest> LoadAsync(string root, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(ManifestPath(root));
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("The project manifest exceeds 64 MiB.");
        var manifest = await JsonSerializer.DeserializeAsync<SourceProjectManifest>(stream, Json, token) ?? throw new InvalidDataException("Empty project manifest.");
        if (manifest.Format != SourceProjectManifest.FormatName) throw new InvalidDataException("This folder is not a zStudio source project.");
        if (manifest.Version != SourceProjectManifest.CurrentVersion) throw new InvalidDataException($"Source project version {manifest.Version} is not supported (expected {SourceProjectManifest.CurrentVersion}).");
        // Every field is bounded and every output/layout path stays inside its folder; outputs are unique ignoring case.
        if (manifest.Id == Guid.Empty) throw new InvalidDataException("The project manifest has no project identity.");
        if (manifest.Origin is not { Name.Length: <= 255, Files: >= 0, Bytes: >= 0 } origin || !IsSha256(origin.Fingerprint)) throw new InvalidDataException("The project manifest has an invalid origin.");
        if (manifest.Outputs is not { Count: <= SourceExtractor.MaximumFiles } list) throw new InvalidDataException("The project manifest lists too many or no outputs.");
        if (manifest.Notes is not { Count: <= 100_000 } notes || notes.Any(n => n is not { Length: <= 8192 })) throw new InvalidDataException("The project manifest has invalid notes.");
        HashSet<string> outputs = new(StringComparer.OrdinalIgnoreCase);
        foreach (var output in list)
        {
            if (output.Path is not { Length: <= 400 } || output.Layout is not { Length: <= 500 } || !Families.Contains(output.Family) || !IsSha256(output.Sha256) || output.Bytes < 0)
                throw new InvalidDataException("The project manifest has an invalid output entry.");
            _ = Resolve(root, output.Path); _ = Resolve(root, output.Layout);
            if (!outputs.Add(output.Path)) throw new InvalidDataException($"The project manifest lists {output.Path} twice.");
            if (!output.Layout.StartsWith(MetadataFolder + "/", StringComparison.Ordinal)) throw new InvalidDataException($"Layout {output.Layout} must be inside {MetadataFolder}.");
        }
        return manifest;
    }
    public static T ReadLayout<T>(string root, string relative)
    {
        string path = Resolve(root, relative);
        if (new FileInfo(path).Length > 256 * 1024 * 1024) throw new InvalidDataException($"Layout {relative} exceeds 256 MiB.");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException($"Empty layout {relative}.");
    }
    /// <summary>Refuse links on the way from <paramref name="root"/> to a relative file, including the file itself; nothing is followed.</summary>
    public static void RejectNestedLinks(string root, string relative)
    {
        string current = System.IO.Path.GetFullPath(root); var parts = relative.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            current = System.IO.Path.Combine(current, parts[i]);
            FileSystemInfo info = i < parts.Length - 1 ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{info.FullName} is a link; nothing was written through it.");
        }
    }
    /// <summary>Refuse to write through directory links anywhere above a destination.</summary>
    public static void RejectLinks(string path)
    {
        for (var directory = new DirectoryInfo(System.IO.Path.GetFullPath(path)); directory != null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{directory.FullName} is a link; choose a folder of regular directories.");
    }

    /// <summary>A project or pack destination: never a protected original corpus, never inside or containing the input.</summary>
    public static void ValidateSeparate(string destination, string input, string role)
    {
        string d = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(destination)), i = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(input));
        if (PickupPlacementEditSession.IsProtectedPath(d)) throw new InvalidDataException($"The {role} cannot be inside the protected zbd_1998/zbd_1999 folders.");
        if (d.Equals(i, StringComparison.OrdinalIgnoreCase) || d.StartsWith(i + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || i.StartsWith(d + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {role} must be separate from {input}.");
    }
}

public sealed record SourceProgress(int Completed, int Total, string Item);
