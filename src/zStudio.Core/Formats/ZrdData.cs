using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Formats;

public enum ZrdKind { Int = 1, Float = 2, String = 3, Array = 4 }

/// <summary>Authored data, never runtime memory. Copies retain identity unless explicitly duplicated.</summary>
public sealed record ZrdNode(Guid Id, ZrdKind Kind, uint Bits, string Text, IReadOnlyList<ZrdNode> Children, long SourceOffset = -1)
{
    public string Value => Kind switch
    {
        ZrdKind.Int => unchecked((int)Bits).ToString(CultureInfo.InvariantCulture),
        ZrdKind.Float => float.IsFinite(BitConverter.UInt32BitsToSingle(Bits)) ? BitConverter.UInt32BitsToSingle(Bits).ToString("R", CultureInfo.InvariantCulture) : $"0x{Bits:X8}",
        ZrdKind.String => JsonSerializer.Serialize(Text),
        _ => $"{Children.Count} item{(Children.Count == 1 ? "" : "s")}"
    };
    /// <summary>Bound string input before JSON escaping, so preview allocations do not scale with stored text.</summary>
    public (string Value, bool Truncated) PreviewValue(int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        string value = Kind == ZrdKind.String
            ? JsonSerializer.Serialize(Text.Length > maximumCharacters ? Text[..maximumCharacters] : Text)
            : Value;
        bool truncated = value.Length > maximumCharacters || Kind == ZrdKind.String && Text.Length > maximumCharacters;
        return (value.Length > maximumCharacters ? value[..maximumCharacters] : value, truncated);
    }
    public ZrdNode? Find(Guid id) => Id == id ? this : Children.Select(c => c.Find(id)).FirstOrDefault(n => n != null);
    public ZrdNode Duplicate() => this with { Id = Guid.NewGuid(), SourceOffset = -1, Children = Children.Select(c => c.Duplicate()).ToArray() };
    public static ZrdNode Create(ZrdKind kind, string value = "") => Set(new(Guid.NewGuid(), kind, 0, "", []), kind, value);
    public static ZrdNode Set(ZrdNode node, ZrdKind kind, string value)
    {
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown ZRD type.");
        uint bits = 0; string text = "";
        if (kind == ZrdKind.Int) bits = unchecked((uint)int.Parse(value.Length == 0 ? "0" : value, CultureInfo.InvariantCulture));
        if (kind == ZrdKind.Float)
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) bits = uint.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            else { float number = float.Parse(value.Length == 0 ? "0" : value, CultureInfo.InvariantCulture); if (!float.IsFinite(number)) throw new InvalidDataException("Use finite decimal floats or explicit 0xXXXXXXXX bits."); bits = BitConverter.SingleToUInt32Bits(number); }
        }
        if (kind == ZrdKind.String)
        {
            try { text = JsonSerializer.Deserialize<string>(value.Length == 0 ? "\"\"" : value) ?? throw new InvalidDataException("Enter a JSON-quoted string."); }
            catch (JsonException ex) { throw new InvalidDataException("Enter a JSON-quoted string (for example \"text\").", ex); }
            if (text.Any(c => c > 255)) throw new InvalidDataException("ZRD strings must be representable as Latin-1.");
        }
        return node with { Kind = kind, Bits = bits, Text = text, Children = kind == ZrdKind.Array && node.Kind == kind ? node.Children : [] };
    }
    public JsonObject ToJson(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        JsonObject result = new() { ["offset"] = SourceOffset < 0 ? null : $"0x{SourceOffset:X}", ["type"] = Kind.ToString().ToLowerInvariant() };
        if (Kind == ZrdKind.Array) result["children"] = JsonData.Array(Children, n => n.ToJson(token), token);
        else if (Kind == ZrdKind.String) result["value"] = Text;
        else if (Kind == ZrdKind.Int) result["value"] = (long)unchecked((int)Bits);
        else { result["value"] = JsonData.Number(BitConverter.UInt32BitsToSingle(Bits)); result["raw_bits"] = $"0x{Bits:X8}"; }
        return result;
    }
    /// <summary>Inspection uses a bounded tree; full exports continue to use ToJson.</summary>
    public JsonObject ToPreviewJson(CancellationToken token = default, int maximumNodes = 1024, int maximumCharacters = 65536, int maximumDepth = 24, int maximumString = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDepth); ArgumentOutOfRangeException.ThrowIfNegative(maximumString);
        int nodes = Math.Max(1, maximumNodes), characters = Math.Max(0, maximumCharacters);
        return Visit(this, 0);
        JsonObject Visit(ZrdNode node, int depth)
        {
            token.ThrowIfCancellationRequested(); nodes--;
            if (node.Kind is not (ZrdKind.String or ZrdKind.Array)) return node.ToJson(token);
            JsonObject result = new() { ["offset"] = node.SourceOffset < 0 ? null : $"0x{node.SourceOffset:X}", ["type"] = node.Kind.ToString().ToLowerInvariant() };
            if (node.Kind == ZrdKind.String)
            {
                int count = Math.Min(node.Text.Length, Math.Min(characters, maximumString)); characters -= count;
                result["value"] = node.Text[..count];
                if (count != node.Text.Length) { result["value_truncated"] = true; result["stored_characters"] = node.Text.Length; }
            }
            else
            {
                JsonArray children = []; result["children"] = children;
                foreach (var child in node.Children) { if (nodes == 0 || depth >= maximumDepth) break; children.Add(Visit(child, depth + 1)); }
                if (children.Count != node.Children.Count) { result["children_truncated"] = true; result["stored_children"] = node.Children.Count; }
            }
            return result;
        }
    }
}

public static partial class ZrdDecoder
{
    /// <summary>Ordinary archive consumers honor the shared decoder's refusal; only explicit raw export may bypass it.</summary>
    internal static ZrdNode ReadAsset(ZbdDocument document, AssetRecord asset, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (asset.Metadata["typed_decode_limited"]?.GetValue<bool>() == true)
            throw new InvalidDataException("This archive member exceeds the shared typed-decoding budget; use raw inspection, exact member export or replacement.");
        return asset.Content as ZrdNode ?? Read(document.Slice(asset.Offset, asset.Length), token);
    }

    internal static ZrdNode? TryRead(ReadOnlyMemory<byte> bytes, CancellationToken token, ArchiveZrdBudget? allocation = null)
    {
        if (bytes.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span) is < 1 or > 4) return null;
        try { return Read(bytes, token, allocation); }
        catch (InvalidDataException) { return null; }
    }
    public static ZrdNode Read(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        => Read(bytes, token, null);
    internal static ZrdNode Read(ReadOnlyMemory<byte> bytes, CancellationToken token, ArchiveZrdBudget? allocation)
    {
        BinaryCursor cursor = new(bytes); int budget = 2_000_000;
        var root = ReadNode(0);
        if (cursor.Remaining != 0) throw new InvalidDataException($"Trailing ZRD bytes at 0x{cursor.Position:X}.");
        return root;
        ZrdNode ReadNode(int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 128 || --budget < 0) throw new InvalidDataException("ZRD nesting or node limit exceeded.");
            allocation?.Node();
            long offset = cursor.AbsolutePosition; var kind = (ZrdKind)cursor.U32();
            uint bits = 0; string text = ""; List<ZrdNode> children = [];
            switch (kind)
            {
                case ZrdKind.Int: case ZrdKind.Float: bits = cursor.U32(); break;
                case ZrdKind.String:
                    int length = cursor.Count(cursor.U32()); allocation?.Text(length);
                    text = Encoding.Latin1.GetString(cursor.Take(length).Span); break;
                case ZrdKind.Array:
                    int count = cursor.I32(); if (count < 1) throw new InvalidDataException("Invalid ZRD array count."); cursor.Count((uint)(count - 1), 4);
                    allocation?.Children(count - 1);
                    for (int i = 1; i < count; i++) children.Add(ReadNode(depth + 1)); break;
                default: throw new InvalidDataException($"Unknown ZRD type {(uint)kind} at 0x{offset:X}.");
            }
            return new(Guid.NewGuid(), kind, bits, text, children.ToArray(), offset);
        }
    }
}

public static class ZrdWriter
{
    public static byte[] Write(ZrdNode root, CancellationToken token = default)
    {
        using MemoryStream stream = new(); int budget = 2_000_000; HashSet<Guid> identities = [];
        WriteNode(root, 0); return stream.ToArray();
        void Word(uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); stream.Write(bytes); }
        void WriteNode(ZrdNode node, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 128 || --budget < 0 || !identities.Add(node.Id)) throw new InvalidDataException("ZRD nesting, node count or identity limit exceeded.");
            if (stream.Length + 8L + (node.Kind == ZrdKind.String ? node.Text.Length : 0) > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException("ZRD exceeds the document size limit.");
            Word((uint)node.Kind);
            switch (node.Kind)
            {
                case ZrdKind.Int: case ZrdKind.Float: Word(node.Bits); break;
                case ZrdKind.String:
                    if (node.Text.Any(c => c > 255)) throw new InvalidDataException("ZRD strings must be Latin-1.");
                    Word((uint)node.Text.Length); stream.Write(Encoding.Latin1.GetBytes(node.Text)); break;
                case ZrdKind.Array:
                    Word(checked((uint)node.Children.Count + 1)); foreach (var child in node.Children) WriteNode(child, depth + 1); break;
                default: throw new InvalidDataException("Unknown ZRD type.");
            }
            if (stream.Length > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException("ZRD exceeds the document size limit.");
        }
    }
}
