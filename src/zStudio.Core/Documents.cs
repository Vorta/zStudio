using System.Numerics;
using System.Runtime.InteropServices;
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
    /// between two stamps); <see cref="Unverified"/> when another program changed or removed it meanwhile. The read shares
    /// the file with the seal that may still hold it (<see cref="SealedFile"/>), which lets no other program write it.
    /// </summary>
    public static FileStamp ReadHolding(string path, ReadOnlySpan<byte> bytes) => ReadHolding(path, bytes, null);
    internal static FileStamp ReadHolding(string path, ReadOnlySpan<byte> bytes, DirectoryLease? directories)
    {
        try
        {
            using DirectoryLease? own = directories == null ? new() : null;
            using FileStream stream = (directories ?? own!).OpenFile(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
            FileStamp stamp = new(stream.Length, File.GetLastWriteTimeUtc(stream.SafeFileHandle));
            if (stamp.Length != bytes.Length) return Unverified;
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(buffer, 0, Math.Min(65536, bytes.Length - offset));
                    if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(bytes.Slice(offset, read))) return Unverified;
                    offset += read;
                }
                return stream.Length == stamp.Length && File.GetLastWriteTimeUtc(stream.SafeFileHandle) == stamp.LastWriteUtc
                    ? stamp : Unverified;
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
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
public sealed record ScriptContent
{
    public IReadOnlyList<string[]> Instructions { get; }
    private readonly string? sourceText;
    /// <summary>Complete text for explicit export. Prepared scripts format it only when requested.</summary>
    public string Text => GetText();
    public string PreviewText { get; }
    public bool TextTruncated { get; }
    public ScriptContent(IReadOnlyList<string[]> instructions, string text)
    {
        Instructions = instructions; sourceText = text;
        TextTruncated = text.Length > Formats.PreparedScriptText.PreviewCharacters;
        PreviewText = TextTruncated ? text[..Formats.PreparedScriptText.PreviewCharacters] : text;
    }
    internal ScriptContent(IReadOnlyList<string[]> instructions, CancellationToken token)
    {
        Instructions = instructions;
        (PreviewText, TextTruncated) = Formats.PreparedScriptText.Format(instructions, Formats.PreparedScriptText.PreviewCharacters, token);
    }
    public string GetText(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return sourceText ?? Formats.PreparedScriptText.Format(Instructions, int.MaxValue, token).Text;
    }
}
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
    /// <summary>How many characters <see cref="Shown"/> and <see cref="ShownText"/> keep of a value for a message.</summary>
    public const int ShownCharacters = 64;
    private static readonly JsonSerializerOptions ShownOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// A JSON value for a message, at most <paramref name="characters"/> characters and then "…": as JSON writes it (a
    /// string quoted), or with <paramref name="asText"/> a string as its text. Only what is shown is read, copied or
    /// escaped (a parsed value's text as written, an object's or list's first entries), so a large malformed value costs
    /// no more than its preview, however it would serialize.
    /// </summary>
    public static string Shown(JsonNode? node, bool asText = false, int characters = ShownCharacters)
    {
        System.Text.StringBuilder text = new();
        bool whole = asText && node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? Text(value) : Write(node);
        return whole ? text.ToString() : text.Append('…').ToString();

        // Each returns false once the preview is full (the value goes on).
        bool Add(ReadOnlySpan<char> part)
        {
            int room = characters - text.Length;
            if (part.Length <= room) { text.Append(part); return true; }
            text.Append(Cut(part, room)); return false;
        }
        bool Write(JsonNode? item)
        {
            switch (item)
            {
                case null: return Add("null");
                case JsonObject o:
                    if (!Add("{")) return false;
                    bool first = true;
                    foreach (var (key, entry) in o)
                    {
                        if (!first && !Add(",")) return false;
                        first = false;
                        if (!Quoted(key) || !Add(":") || !Write(entry)) return false;
                    }
                    return Add("}");
                case JsonArray a:
                    if (!Add("[")) return false;
                    for (int i = 0; i < a.Count; i++)
                        if (i > 0 && !Add(",") || !Write(a[i])) return false;
                    return Add("]");
            }
            var scalar = (JsonValue)item;
            if (scalar.TryGetValue(out JsonElement element)) return Raw(JsonMarshal.GetRawUtf8Value(element));
            if (scalar.TryGetValue(out string? s)) return Quoted(s);
            // Values built in memory are numbers, switches and the like, which write short.
            return Add(scalar.ToJsonString());
        }
        bool Quoted(string s)
        {
            int room = Math.Max(characters - text.Length, 0);
            return Add(JsonSerializer.Serialize(s.Length > room ? Cut(s, room).ToString() : s, ShownOptions)) && s.Length <= room;
        }
        // A parsed value as written: at most three UTF-8 bytes a character are read, cut at a whole character.
        bool Raw(ReadOnlySpan<byte> raw)
        {
            int length = (int)Math.Min(raw.Length, Math.Max(characters - text.Length, 0) * 3L);
            while (length > 0 && length < raw.Length && (raw[length] & 0xC0) == 0x80) length--;
            return Add(System.Text.Encoding.UTF8.GetString(raw[..length])) && length == raw.Length;
        }
        bool Text(JsonValue item)
        {
            if (!item.TryGetValue(out JsonElement element)) return item.TryGetValue(out string? s) ? Add(s) : Write(item);
            var raw = JsonMarshal.GetRawUtf8Value(element);
            // A short string is read as its text; a longer one is shown as written, escapes and all, without its quotes.
            return raw.Length <= 1024 ? Add(element.GetString()) : Raw(raw[1..^1]);
        }
    }

    /// <summary>An authored text (a name, a path) for a message: at most <paramref name="characters"/> characters and then "…".</summary>
    public static string ShownText(string text, int characters = ShownCharacters) =>
        text.Length <= characters ? text : string.Concat(Cut(text, characters), "…");

    /// <summary>The first <paramref name="length"/> characters, one fewer when that would split a surrogate pair.</summary>
    private static ReadOnlySpan<char> Cut(ReadOnlySpan<char> text, int length)
    {
        if (length <= 0) return [];
        if (length >= text.Length) return text;
        return char.IsHighSurrogate(text[length - 1]) ? text[..(length - 1)] : text[..length];
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
