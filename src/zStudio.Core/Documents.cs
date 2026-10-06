using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core;

public enum FormatFamily { Unknown, TexturePack, Archive, Scripts, Animation, GameZ, Zrd, Wave }
public enum Recognition { Supported, UnsupportedVersion, Malformed, Unknown }
public enum GameVariant { Shared, Recoil, MechWarrior3 }
public enum AssetKind { Raw, Texture, Sound, Zrd, Script, Animation, Model, World, Node, Material, TextureReference, Motion }
public sealed record FormatProbe(FormatFamily Family, uint? Version, Recognition Recognition, string Description);
public sealed record Diagnostic(string Severity, string Message, int? AssetIndex = null, long? Offset = null);
public readonly record struct AssetId(string File, AssetKind Kind, int Index);
public sealed record FileStamp(long Length, DateTime LastWriteUtc)
{
    public static FileStamp Read(string path) { FileInfo f = new(path); return new(f.Length, f.LastWriteTimeUtc); }
    /// <summary>A stamp no file has: a file another program changed before its stamp could be taken reads as changed.</summary>
    public static readonly FileStamp Unverified = new(-1, DateTime.MinValue);
    /// <summary>
    /// The stamp of a file just saved with <paramref name="bytes"/>, taken while it still holds exactly them (read back
    /// between two stamps); <see cref="Unverified"/> when another program changed or removed it meanwhile.
    /// </summary>
    public static FileStamp ReadHolding(string path, ReadOnlySpan<byte> bytes)
    {
        try
        {
            var stamp = Read(path);
            return Sources.SourceProject.FileEquals(path, bytes) && Read(path) == stamp ? stamp : Unverified;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Unverified; }
    }
}

public sealed class AssetRecord
{
    public required AssetId Id { get; init; }
    public required string Name { get; set; }
    public AssetKind Kind => Id.Kind;
    public int Index => Id.Index;
    public long Offset { get; init; }
    public long Length { get; set; }
    public string Summary { get; set; } = "";
    public JsonObject Metadata { get; init; } = [];
    public object? Content { get; set; }
    public override string ToString() => Name;
}

public sealed class ZbdDocument
{
    public string Path { get; }
    public FileStamp Stamp { get; }
    public FormatProbe Probe { get; }
    public GameVariant Game { get; internal set; }
    public ReadOnlyMemory<byte> Bytes { get; }
    public List<AssetRecord> Assets { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
    public JsonObject Metadata { get; } = [];
    public GameScene? Scene { get; set; }
    /// <summary>Reconstructed source text (<c>zrd-text</c> or <c>gamegen-script</c>) rather than a compiled game file.</summary>
    public string? SourceSyntax { get; internal set; }
    public GameZSourceLayout? GameZLayout { get; internal set; }
    public long? ArchiveDirectoryOffset { get; internal set; }
    public Animation.AnimationPackage? Animations { get; set; }
    public Formats.PreparedScriptPackage? Scripts { get; internal set; }
    public ZbdDocument(string path, FileStamp stamp, FormatProbe probe, ReadOnlyMemory<byte> bytes)
    {
        Path = path; Stamp = stamp; Probe = probe; Bytes = bytes;
        Game = (probe.Family, probe.Version) switch
        { (FormatFamily.GameZ, 27) or (FormatFamily.Animation, 39) => GameVariant.MechWarrior3,
          (FormatFamily.GameZ, 15 or 13) or (FormatFamily.Animation, 28) => GameVariant.Recoil, _ => GameVariant.Shared };
    }
    public ReadOnlyMemory<byte> Slice(long offset, long length)
    { BinaryCursor.CheckRange(Bytes.Length, offset, length); return Bytes.Slice((int)offset, (int)length); }
    public AssetRecord Add(AssetKind kind, int index, string name, long offset, long length, JsonObject? metadata = null, object? content = null)
    {
        AssetRecord asset = new() { Id = new(Path, kind, index), Name = name, Offset = offset, Length = length, Metadata = metadata ?? [], Content = content };
        Assets.Add(asset); return asset;
    }
}

/// <summary>Physical ranges recorded by the shared reader; stored pointer words remain metadata.</summary>
public sealed record GameZSourceLayout(int TextureOffset, int MaterialOffset, int ModelOffset, int NodeOffset,
    int MaterialCapacity, int ModelCapacity, int NodeCapacity, IReadOnlyList<long> NodeDataOffsets);

public sealed record TextureInfo(int Width, int Height, byte Flags, int PaletteCount, int PalettePage,
    int PixelsOffset, int PixelsLength, int AlphaOffset, int PaletteOffset, int PaletteLength);
public sealed record DecodedImage(int Width, int Height, byte[] Rgba);
public sealed record ScriptContent(IReadOnlyList<string[]> Instructions, string Text);
public sealed record WaveCue(uint Id, uint SampleOffset);
public sealed record WaveInfo(ushort Encoding, ushort Channels, uint SampleRate, ushort BitsPerSample,
    ushort BlockAlign, int DataOffset, int DataLength, IReadOnlyList<WaveCue> Cues)
{
    public double Duration => SampleRate == 0 || BlockAlign == 0 ? 0 : (double)DataLength / BlockAlign / SampleRate;
}
public sealed record Polygon(int MaterialIndex, uint Flags, int[] Vertices, int[] Normals, Vector2[] Uvs, JsonObject Metadata)
{ public Vector3[] Colors { get; init; } = []; }
public sealed record GameModel(int Index, Vector3[] Vertices, Vector3[] Normals, Vector3[] Morphs, Polygon[] Polygons, JsonObject Metadata);
public sealed record GameNode(int Index, string Name, string Class, int? ModelIndex, int[] Parents, int[] Children, JsonObject Metadata, JsonObject Data);
public sealed class GameScene
{
    public List<JsonObject> Textures { get; } = [];
    public List<JsonObject> Materials { get; } = [];
    public List<GameModel> Models { get; } = [];
    public List<GameNode> Nodes { get; } = [];
}
public static class JsonData
{
    // A supported 128-level ZRD uses both an object and a children array per level, plus export wrappers.
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = true, MaxDepth = 512 };
    public static long Integer(JsonNode? node, long fallback = 0)
    {
        if (node is not JsonValue v) return fallback;
        if (v.TryGetValue<long>(out long a)) return a;
        if (v.TryGetValue<int>(out int b)) return b;
        if (v.TryGetValue<uint>(out uint c)) return c;
        if (v.TryGetValue<ushort>(out ushort d)) return d;
        return fallback;
    }
    public static int Int(this JsonNode? node, string key, int fallback = 0) => checked((int)Integer(node?[key], fallback));
    public static uint UInt(this JsonNode? node, string key) => checked((uint)Integer(node?[key]));
    public static float Scalar(JsonNode? node, float fallback = 0)
    {
        if (node is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out double n)) return (float)n;
        if (v.TryGetValue<float>(out float f)) return f;
        if (v.TryGetValue<long>(out long a)) return a;
        if (v.TryGetValue<int>(out int b)) return b;
        if (v.TryGetValue<uint>(out uint c)) return c;
        return fallback;
    }
    public static float Float(this JsonNode? node, string key, float fallback = 0) => Scalar(node?[key], fallback);
    public static string Text(this JsonNode? node) => node is JsonObject o ? o["text"]?.GetValue<string>() ?? "" : node?.ToString() ?? "";
    public static string Text(this JsonNode? node, string key) => node?[key].Text() ?? "";
    public static JsonNode Number(float value) => float.IsFinite(value) ? JsonValue.Create((double)value)! : JsonValue.Create($"0x{BitConverter.SingleToUInt32Bits(value):X8}")!;
    public static JsonObject Vector(Vector3 v) => new() { ["x"] = Number(v.X), ["y"] = Number(v.Y), ["z"] = Number(v.Z) };
    public static JsonArray Integers(IEnumerable<int> values, CancellationToken token = default) => Array(values, v => JsonValue.Create((long)v), token);
    public static JsonArray Vectors(IEnumerable<Vector3> values, CancellationToken token = default) => Array(values, v => Vector(v), token);
    public static JsonArray Array<T>(IEnumerable<T> values, Func<T, JsonNode?> convert, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        JsonArray result = [];
        foreach (T value in values)
        {
            token.ThrowIfCancellationRequested();
            result.Add(convert(value));
        }
        token.ThrowIfCancellationRequested();
        return result;
    }
    public static JsonNode? Clone(JsonNode? node, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (node is JsonObject source)
        {
            JsonObject result = new(source.Options);
            foreach (var (key, value) in source) result.Add(key, Clone(value, token));
            return result;
        }
        if (node is JsonArray array)
        {
            JsonArray result = new(array.Options);
            foreach (var value in array) result.Add(Clone(value, token));
            return result;
        }
        return node?.DeepClone();
    }
    public static JsonObject PreviewObject(JsonObject source, int nodes = 512, int characters = 8192, CancellationToken token = default)
    {
        var preview = Preview(source, nodes, characters, token);
        var result = (JsonObject)preview.Value!;
        if (preview.Truncated) result["inspection_truncated"] = true;
        return result;
    }
    /// <summary>Bound metadata before copying/serializing, including nested collections and escaped text.</summary>
    public static (JsonNode? Value, bool Truncated) Preview(JsonNode? source, int nodes = 512, int characters = 8192, CancellationToken token = default)
    {
        bool truncated = false;
        var result = Visit(source, 0); return (result, truncated);
        JsonNode? Visit(JsonNode? node, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (nodes-- <= 0 || depth > 12) { truncated = true; return null; }
            if (node is JsonObject obj)
            {
                JsonObject copy = new();
                foreach (var (key, value) in obj)
                {
                    if (nodes <= 0 || key.Length > characters) { truncated = true; break; }
                    characters -= key.Length; copy[key] = Visit(value, depth + 1);
                }
                return copy;
            }
            if (node is JsonArray array)
            {
                JsonArray copy = [];
                foreach (var value in array) { if (nodes <= 0) { truncated = true; break; } copy.Add(Visit(value, depth + 1)); }
                return copy;
            }
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                int count = Math.Min(text.Length, Math.Min(characters, 1024)); characters -= count;
                truncated |= count != text.Length; return JsonValue.Create(text[..count]);
            }
            return node?.DeepClone();
        }
    }
    public static string Hex(byte[] bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        System.Text.StringBuilder result = new(checked(bytes.Length * 2));
        for (int offset = 0; offset < bytes.Length; offset += 4096)
        {
            token.ThrowIfCancellationRequested();
            result.Append(Convert.ToHexStringLower(bytes.AsSpan(offset, Math.Min(4096, bytes.Length - offset))));
        }
        token.ThrowIfCancellationRequested();
        return result.ToString();
    }
}
