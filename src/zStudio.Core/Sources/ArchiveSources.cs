using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>ZAR archives: zReader resources (<c>zrdr.zbd</c>) and sound banks (<c>sounds{h,m,l}.zbd</c>).</summary>
internal static class ArchiveSources
{
    internal const int RecordSize = 148;
    /// <summary>
    /// The longest source field a record holds: 63 Latin-1 characters and the terminating zero. RECOIL's compiler recorded
    /// its temporary files there (at most 55 characters in the shipped archives).
    /// </summary>
    internal const int MaximumSourceField = 63;
    internal sealed record Member(int Index, string Name, uint Aux, string? SourceField, ulong FileTime, ReadOnlyMemory<byte> Payload, long Offset = -1);
    /// <summary>
    /// A member to write. The engine looks members up by name; the source field records where it was built from, whole
    /// (see <see cref="FitsSourceField"/>), or is empty.
    /// </summary>
    internal sealed record Entry(string Name, string SourceField, byte[] Payload);
    /// <summary>Whether <paramref name="field"/> is stored whole: what reads it back (placement edits) gets exactly this path.</summary>
    internal static bool FitsSourceField(string field) => field.Length <= MaximumSourceField && !field.Any(c => c == 0 || c > 255);

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
                BinaryPrimitives.ReadUInt64LittleEndian(r[140..]), bytes.Slice((int)offset, (int)size), offset));
        }
        return members;
    }
    private static string? PlainField(ReadOnlySpan<byte> field)
    {
        int nul = field.IndexOf((byte)0);
        return nul >= 0 && !field[nul..].ContainsAnyExcept((byte)0) ? Encoding.Latin1.GetString(field[..nul]) : null;
    }

    /// <summary>
    /// The time every written member records: the DOS epoch, 1 January 1980 00:00 UTC. The original compiler stored when it
    /// packed the archive, as a local DOS time (with bit 1 set) and a FILETIME; the engine reads neither. A fixed time keeps
    /// exports deterministic: the same sources build the same bytes at any time, in any time zone.
    /// </summary>
    internal static readonly DateTime MemberTime = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Contiguous payloads in member order, then the directory and the {1, count} footer, as the engine expects.</summary>
    internal static byte[] Write(IReadOnlyList<Entry> members)
    {
        long length = members.Sum(m => (long)m.Payload.Length) + members.Count * (long)RecordSize + 8;
        FormatRegistry.ValidateDocumentSize(length);
        byte[] result = new byte[length]; int offset = 0;
        foreach (var m in members) { m.Payload.CopyTo(result, offset); offset += m.Payload.Length; }
        // Both fields hold MemberTime, the DOS time in the original form (bit 1 set).
        uint dosTime = (uint)(MemberTime.Hour << 11 | MemberTime.Minute << 5 | MemberTime.Second / 2) | 2;
        ulong fileTime = (ulong)MemberTime.ToFileTimeUtc();
        int position = 0; var span = result.AsSpan();
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i]; var r = span.Slice(offset + i * RecordSize, RecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)position); BinaryPrimitives.WriteUInt32LittleEndian(r[4..], (uint)m.Payload.Length); position += m.Payload.Length;
            if (m.Name.Length is < 1 or > 63 || m.Name.Any(c => c == 0 || c > 255)) throw new InvalidDataException($"'{m.Name}' is not a valid archive member name (1–63 Latin-1 characters).");
            Encoding.Latin1.GetBytes(m.Name, r[8..72]);
            BinaryPrimitives.WriteUInt32LittleEndian(r[72..], dosTime);
            // Never shortened or altered: a partial path could name another source, which an edit would then change.
            if (!FitsSourceField(m.SourceField)) throw new InvalidDataException($"The source path of archive member '{m.Name}' ({m.SourceField}) does not fit the {MaximumSourceField} Latin-1 characters a member records.");
            Encoding.Latin1.GetBytes(m.SourceField, r[76..140]);
            BinaryPrimitives.WriteUInt64LittleEndian(r[140..], fileTime);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(span[^8..], 1); BinaryPrimitives.WriteUInt32LittleEndian(span[^4..], (uint)members.Count);
        return result;
    }
}
