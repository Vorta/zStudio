using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>ZAR archives: zReader resources (<c>zrdr.zbd</c>) and sound banks (<c>sounds{h,m,l}.zbd</c>).</summary>
internal static class ArchiveSources
{
    internal const int RecordSize = 148;
    internal sealed record Member(int Index, string Name, uint Aux, string? SourceField, ulong FileTime, ReadOnlyMemory<byte> Payload);
    /// <summary>A member to write. The engine looks members up by name; the source field records where it was built from.</summary>
    internal sealed record Entry(string Name, string SourceField, byte[] Payload);

    /// <summary>Records in stored order with their payloads (possibly shared). Source fields are null unless a zero-terminated string.</summary>
    internal static IReadOnlyList<Member> Read(ReadOnlyMemory<byte> bytes)
    {
        var span = bytes.Span;
        if (bytes.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(span[^8..]) != 1) throw new InvalidDataException("Missing ZAR footer.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(span[^4..]); FormatRegistry.CheckEntries("Archive member", count);
        long table = bytes.Length - 8L - count * (long)RecordSize;
        if (table < 0) throw new InvalidDataException("ZAR directory exceeds the file.");
        List<Member> members = [];
        for (int i = 0; i < count; i++)
        {
            var r = span.Slice((int)(table + i * RecordSize), RecordSize);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(r), size = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
            BinaryCursor.CheckRange(table, offset, size);
            members.Add(new(i, BinaryCursor.FixedString(r[8..72]), BinaryPrimitives.ReadUInt32LittleEndian(r[72..]), PlainField(r[76..140]),
                BinaryPrimitives.ReadUInt64LittleEndian(r[140..]), bytes.Slice((int)offset, (int)size)));
        }
        return members;
    }
    private static string? PlainField(ReadOnlySpan<byte> field)
    {
        int nul = field.IndexOf((byte)0);
        return nul >= 0 && !field[nul..].ContainsAnyExcept((byte)0) ? Encoding.Latin1.GetString(field[..nul]) : null;
    }

    /// <summary>Contiguous payloads in member order, then the directory and the {1, count} footer, as the engine expects.</summary>
    internal static byte[] Write(IReadOnlyList<Entry> members, DateTime builtUtc)
    {
        long length = members.Sum(m => (long)m.Payload.Length) + members.Count * (long)RecordSize + 8;
        FormatRegistry.ValidateDocumentSize(length);
        byte[] result = new byte[length]; int offset = 0;
        foreach (var m in members) { m.Payload.CopyTo(result, offset); offset += m.Payload.Length; }
        // The original compiler stored a DOS time (with bit 1 set) and a FILETIME; the engine reads neither.
        var local = builtUtc.ToLocalTime(); uint dosTime = (uint)(local.Hour << 11 | local.Minute << 5 | local.Second / 2) | 2;
        ulong fileTime = (ulong)builtUtc.ToFileTimeUtc();
        int position = 0; var span = result.AsSpan();
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i]; var r = span.Slice(offset + i * RecordSize, RecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)position); BinaryPrimitives.WriteUInt32LittleEndian(r[4..], (uint)m.Payload.Length); position += m.Payload.Length;
            if (m.Name.Length is < 1 or > 63 || m.Name.Any(c => c == 0 || c > 255)) throw new InvalidDataException($"'{m.Name}' is not a valid archive member name (1–63 Latin-1 characters).");
            Encoding.Latin1.GetBytes(m.Name, r[8..72]);
            BinaryPrimitives.WriteUInt32LittleEndian(r[72..], dosTime);
            string source = m.SourceField.Length > 63 ? m.SourceField[^63..] : m.SourceField;
            Encoding.Latin1.GetBytes(source.Select(c => c > 255 ? '?' : c).ToArray(), r[76..140]);
            BinaryPrimitives.WriteUInt64LittleEndian(r[140..], fileTime);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(span[^8..], 1); BinaryPrimitives.WriteUInt32LittleEndian(span[^4..], (uint)members.Count);
        return result;
    }
}
