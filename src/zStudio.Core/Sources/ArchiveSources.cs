using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>ZAR archives: zReader resources (<c>zrdr.zbd</c>) and sound banks (<c>sounds{h,m,l}.zbd</c>).</summary>
internal static class ArchiveSources
{
    internal const int RecordSize = 148;
    internal sealed record Member(int Index, string Name, ReadOnlyMemory<byte> NameField, uint Aux, ReadOnlyMemory<byte> SourceField, ulong FileTime, ReadOnlyMemory<byte> Payload);

    /// <summary>Records in stored order. Payloads must be contiguous in record order, as in every retail archive.</summary>
    internal static IReadOnlyList<Member> Read(ReadOnlyMemory<byte> bytes)
    {
        var span = bytes.Span;
        if (bytes.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(span[^8..]) != 1) throw new InvalidDataException("Missing ZAR footer.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(span[^4..]); FormatRegistry.CheckEntries("Archive member", count);
        long table = bytes.Length - 8L - count * (long)RecordSize;
        if (table < 0) throw new InvalidDataException("ZAR directory exceeds the file.");
        List<Member> members = []; long expected = 0;
        for (int i = 0; i < count; i++)
        {
            var record = bytes.Slice((int)(table + i * RecordSize), RecordSize); var r = record.Span;
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(r), size = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
            if (offset != expected || offset + (long)size > table) throw new InvalidDataException($"Member {i} is not stored contiguously; this layout is not supported for source reconstruction.");
            expected = offset + (long)size;
            members.Add(new(i, BinaryCursor.FixedString(r[8..72]), record[8..72], BinaryPrimitives.ReadUInt32LittleEndian(r[72..]), record[76..140],
                BinaryPrimitives.ReadUInt64LittleEndian(r[140..]), bytes.Slice((int)offset, (int)size)));
        }
        if (expected != table) throw new InvalidDataException("Unreferenced bytes precede the ZAR directory.");
        return members;
    }

    internal static byte[] Write(IReadOnlyList<(ArchiveLayoutRecord Record, byte[] Payload)> members)
    {
        long payloads = members.Sum(m => (long)m.Payload.Length);
        long length = payloads + members.Count * (long)RecordSize + 8;
        FormatRegistry.ValidateDocumentSize(length);
        byte[] result = new byte[length]; int offset = 0;
        foreach (var (_, payload) in members) { payload.CopyTo(result, offset); offset += payload.Length; }
        int position = 0; var span = result.AsSpan();
        for (int i = 0; i < members.Count; i++)
        {
            var (record, payload) = members[i]; var r = span.Slice(offset + i * RecordSize, RecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)position); BinaryPrimitives.WriteUInt32LittleEndian(r[4..], (uint)payload.Length); position += payload.Length;
            Field(r[8..72], record.NameField, record.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(r[72..], record.Aux);
            Field(r[76..140], record.SourceField, record.SourcePath ?? "");
            BinaryPrimitives.WriteUInt64LittleEndian(r[140..], record.FileTime);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(span[^8..], 1); BinaryPrimitives.WriteUInt32LittleEndian(span[^4..], (uint)members.Count);
        return result;
    }
    private static void Field(Span<byte> target, string? raw, string text)
    {
        if (raw != null) { byte[] bytes = Convert.FromBase64String(raw); if (bytes.Length != target.Length) throw new InvalidDataException("Invalid stored archive field."); bytes.CopyTo(target); return; }
        if (text.Length >= target.Length || text.Any(c => c == 0 || c > 255)) throw new InvalidDataException($"'{text}' does not fit a {target.Length}-byte Latin-1 field.");
        Encoding.Latin1.GetBytes(text, target);
    }
    /// <summary>A plain zero-padded Latin-1 string, or null when the stored bytes must be kept verbatim.</summary>
    internal static string? PlainField(ReadOnlySpan<byte> field)
    {
        int nul = field.IndexOf((byte)0);
        return nul >= 0 && !field[nul..].ContainsAnyExcept((byte)0) ? Encoding.Latin1.GetString(field[..nul]) : null;
    }
}
