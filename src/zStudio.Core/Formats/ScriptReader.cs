using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Formats;

public sealed record ScriptInstruction(Guid Id, IReadOnlyList<string> Tokens, ReadOnlyMemory<byte> Raw, long? SourceOffset)
{
    public ReadOnlyMemory<byte> Padding { get; init; }
    public ScriptInstruction Duplicate() => this with { Id = Guid.NewGuid(), SourceOffset = null };
}
public sealed record PreparedScriptEntry(Guid Id, int? SourceIndex, string Name, uint FileTime,
    ReadOnlyMemory<byte> DirectoryRecord, IReadOnlyList<ScriptInstruction> Instructions, ReadOnlyMemory<byte> FollowingBytes)
{
    public PreparedScriptEntry Duplicate(string name) => this with { Id = Guid.NewGuid(), SourceIndex = null, Name = name,
        Instructions = Instructions.Select(i => i.Duplicate()).ToArray() };
}
public sealed record PreparedScriptPackage(ReadOnlyMemory<byte> Header, ReadOnlyMemory<byte> PrefixGap,
    IReadOnlyList<PreparedScriptEntry> Entries, ReadOnlyMemory<byte> Tail);

internal sealed class ScriptReader : IZbdFormatReader
{
    public FormatFamily Family => FormatFamily.Scripts;
    public void Read(ZbdDocument doc, CancellationToken token)
    {
        BinaryCursor c = new(doc.Bytes); c.Skip(8); int count = c.Count(c.U32(), 128);
        List<(string Name, uint Time, uint Offset, ReadOnlyMemory<byte> Raw)> directory = [];
        for (int i = 0; i < count; i++)
        {
            var raw = doc.Slice(12L + i * 128L, 128);
            directory.Add((c.String(120), c.U32(), c.U32(), raw));
        }
        int tableEnd = checked(12 + count * 128);
        long first = count == 0 ? tableEnd : directory[0].Offset;
        List<PreparedScriptEntry> entries = []; ReadOnlyMemory<byte> tail = count == 0 ? doc.Bytes[tableEnd..] : ReadOnlyMemory<byte>.Empty;
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested(); var e = directory[i]; long end = i + 1 < count ? directory[i + 1].Offset : doc.Bytes.Length;
            try
            {
                if (e.Offset < tableEnd) throw new InvalidDataException("Script overlaps the index.");
                var bytes = doc.Slice(e.Offset, end - e.Offset);
                var (instructions, used) = DecodeRecords(bytes, e.Offset, token);
                var following = bytes[used..];
                if (i == count - 1) { tail = following; following = ReadOnlyMemory<byte>.Empty; }
                entries.Add(new(Guid.NewGuid(), i, e.Name, e.Time, e.Raw, instructions, following));
                var a = doc.Add(AssetKind.Script, i, e.Name, e.Offset, end - e.Offset,
                    new JsonObject { ["file_time"] = (long)e.Time }, Content(instructions));
                a.Summary = $"{instructions.Count:N0} instructions";
            }
            catch (InvalidDataException ex) { doc.Diagnostics.Add(new("Error", $"Script {e.Name}: {ex.Message}", i, e.Offset)); }
        }
        // Intact records remain inspectable with their authored indices. Only a
        // complete lossless package can be edited; never serialize a partial parse.
        if (entries.Count == count) doc.Scripts = new(doc.Bytes[..12], doc.Slice(tableEnd, first - tableEnd), entries, tail);
    }
    public static ScriptContent Decode(ReadOnlyMemory<byte> bytes, CancellationToken token)
        => Content(DecodeRecords(bytes, 0, token).Instructions);
    private static (IReadOnlyList<ScriptInstruction> Instructions, int Used) DecodeRecords(ReadOnlyMemory<byte> bytes, long sourceOffset, CancellationToken token)
    {
        BinaryCursor c = new(bytes); List<ScriptInstruction> instructions = [];
        while (c.Remaining > 0)
        {
            token.ThrowIfCancellationRequested(); int start = bytes.Length - c.Remaining;
            uint size = c.U32(); if (size == 0) return (instructions, bytes.Length - c.Remaining);
            uint count = c.U32(); if (count > size) throw new InvalidDataException("Script argument count exceeds its string block.");
            var strings = c.Take(c.Count(size)); int pos = 0; string[] args = new string[count];
            for (int i = 0; i < count; i++)
            {
                int nul = strings.Span[pos..].IndexOf((byte)0); if (nul < 0) throw new InvalidDataException("Unterminated script argument.");
                args[i] = Encoding.Latin1.GetString(strings.Span.Slice(pos, nul)); pos += nul + 1;
            }
            instructions.Add(new(Guid.NewGuid(), args, bytes.Slice(start, checked((int)size + 8)), sourceOffset + start) { Padding = strings[pos..] });
        }
        throw new InvalidDataException("Missing script terminator.");
    }
    internal static ScriptContent Content(IEnumerable<ScriptInstruction> records)
    {
        var instructions = records.Select(i => i.Tokens.ToArray()).ToArray();
        return new(instructions, string.Join('\n', instructions.Select(a => string.Join(' ', a.Select(Quote)))) + "\n");
    }
    private static string Quote(string s) => s.Length == 0 || s.Any(char.IsWhiteSpace) || s.Contains('"')
        ? "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : s;
}

public static class PreparedScriptWriter
{
    public const int MaximumTokens = 16;
    public const int MaximumTokenLength = 16384;
    public static void ValidateName(string name)
    { if (name.Length is < 1 or > 119 || name.Any(c => c == 0 || c > 255)) throw new InvalidDataException("Script names require 1–119 Latin-1 characters without NUL."); }
    public static void ValidateTokens(IReadOnlyList<string> tokens)
    {
        if (tokens.Count is < 1 or > MaximumTokens || string.IsNullOrWhiteSpace(tokens[0])) throw new InvalidDataException("An instruction requires a command and at most 15 arguments (16 tokens total).");
        if (tokens.Any(s => s == null || s.Length > MaximumTokenLength || s.Any(c => c == 0 || c > 255))) throw new InvalidDataException("Each token must be a string of at most 16,384 Latin-1 characters without NUL.");
    }
    public static byte[] Write(PreparedScriptPackage package, CancellationToken token = default)
    {
        long length = 12L + package.Entries.Count * 128L + package.PrefixGap.Length + package.Tail.Length;
        foreach (var e in package.Entries)
        {
            if (e.DirectoryRecord.Length != 128 || BinaryCursor.FixedString(e.DirectoryRecord.Span[..120]) != e.Name) ValidateName(e.Name);
            length += 4L + e.FollowingBytes.Length;
            foreach (var i in e.Instructions)
            {
                token.ThrowIfCancellationRequested();
                if (i.Raw.IsEmpty) ValidateTokens(i.Tokens);
                length += i.Raw.IsEmpty ? 8L + i.Tokens.Sum(s => (long)s.Length + 1) + i.Padding.Length : i.Raw.Length;
            }
        }
        FormatRegistry.ValidateDocumentSize(length);
        byte[] result = new byte[(int)length]; package.Header.Span.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)package.Entries.Count);
        int offset = checked(12 + package.Entries.Count * 128); package.PrefixGap.Span.CopyTo(result.AsSpan(offset)); offset += package.PrefixGap.Length;
        for (int n = 0; n < package.Entries.Count; n++)
        {
            token.ThrowIfCancellationRequested(); var e = package.Entries[n]; var record = result.AsSpan(12 + n * 128, 128);
            if (e.DirectoryRecord.Length != 128) throw new InvalidDataException("Invalid script directory record.");
            e.DirectoryRecord.Span.CopyTo(record);
            if (BinaryCursor.FixedString(record[..120]) != e.Name) { record[..120].Clear(); Encoding.Latin1.GetBytes(e.Name, record); }
            BinaryPrimitives.WriteUInt32LittleEndian(record[120..], e.FileTime); BinaryPrimitives.WriteUInt32LittleEndian(record[124..], (uint)offset);
            foreach (var i in e.Instructions)
            {
                token.ThrowIfCancellationRequested();
                if (!i.Raw.IsEmpty) { i.Raw.Span.CopyTo(result.AsSpan(offset)); offset += i.Raw.Length; continue; }
                int size = checked(i.Tokens.Sum(s => s.Length + 1) + i.Padding.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), (uint)size);
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + 4), (uint)i.Tokens.Count); offset += 8;
                foreach (string s in i.Tokens) { Encoding.Latin1.GetBytes(s, result.AsSpan(offset)); offset += s.Length + 1; }
                i.Padding.Span.CopyTo(result.AsSpan(offset)); offset += i.Padding.Length;
            }
            offset += 4; e.FollowingBytes.Span.CopyTo(result.AsSpan(offset)); offset += e.FollowingBytes.Length;
        }
        package.Tail.Span.CopyTo(result.AsSpan(offset)); return result;
    }
}
