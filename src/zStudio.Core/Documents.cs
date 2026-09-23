using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core;

public enum FormatFamily { Unknown, TexturePack, Archive, Scripts, Animation, GameZ, Zrd, Wave }
public enum Recognition { Supported, UnsupportedVersion, Malformed, Unknown }
public enum AssetKind { Raw, Texture, Sound, Zrd, Script, Animation, Model, World, Node, Material, TextureReference }
public sealed record FormatProbe(FormatFamily Family, uint? Version, Recognition Recognition, string Description);
public sealed record Diagnostic(string Severity, string Message, int? AssetIndex = null, long? Offset = null);
public readonly record struct AssetId(string File, AssetKind Kind, int Index);
public sealed record FileStamp(long Length, DateTime LastWriteUtc)
{
    public static FileStamp Read(string path) { FileInfo f = new(path); return new(f.Length, f.LastWriteTimeUtc); }
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
    public ReadOnlyMemory<byte> Bytes { get; }
    public List<AssetRecord> Assets { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
    public JsonObject Metadata { get; } = [];
    public GameScene? Scene { get; set; }
    public Animation.AnimationPackage? Animations { get; set; }
    public ZbdDocument(string path, FileStamp stamp, FormatProbe probe, ReadOnlyMemory<byte> bytes)
    { Path = path; Stamp = stamp; Probe = probe; Bytes = bytes; }
    public ReadOnlyMemory<byte> Slice(long offset, long length)
    { BinaryCursor.CheckRange(Bytes.Length, offset, length); return Bytes.Slice((int)offset, (int)length); }
    public AssetRecord Add(AssetKind kind, int index, string name, long offset, long length, JsonObject? metadata = null, object? content = null)
    {
        AssetRecord asset = new() { Id = new(Path, kind, index), Name = name, Offset = offset, Length = length, Metadata = metadata ?? [], Content = content };
        Assets.Add(asset); return asset;
    }
}

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
public sealed record Polygon(int MaterialIndex, uint Flags, int[] Vertices, int[] Normals, Vector2[] Uvs, JsonObject Metadata);
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
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = true, MaxDepth = 256 };
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
    public static JsonArray Integers(IEnumerable<int> values) => new(values.Select(v => (JsonNode?)JsonValue.Create((long)v)).ToArray());
    public static JsonArray Vectors(IEnumerable<Vector3> values) => new(values.Select(v => (JsonNode?)Vector(v)).ToArray());
}
