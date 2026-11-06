using System.Buffers.Binary;

namespace zStudio.Core.Animation;

/// <summary>
/// Serializes an <see cref="AnimationPackage"/> in its own format (<see cref="AnimationPackage.Format"/>): the prefix with
/// the entry count, then each entry's header with its counts, its reference tables and its sequences. Format 28 (RECOIL)
/// counts in bytes and 16 bits, format 200 (reRECOIL v2) in 32 bits; the records are the same in both.
/// </summary>
public static class AnimationWriter
{
    /// <summary>
    /// The most sequences a format-200 entry holds: event 10 keeps the sequence it wakes in a 16-bit field, so reRECOIL v2
    /// refuses an entry of more.
    /// </summary>
    public const int MaximumWideSequences = short.MaxValue;

    public static byte[] Write(AnimationPackage package, CancellationToken token = default)
    {
        uint format = package.Format;
        // The game reads format 28's count as a signed 16-bit value; MechWarrior 3's is kept unsigned, format 200's is 32-bit.
        int maximum = format switch { 28 => AnimationCompiler.MaximumEntries, 39 => ushort.MaxValue, _ => int.MaxValue };
        if (package.Entries.Count > maximum) throw new InvalidDataException($"A format-{format} animation file holds at most {maximum:N0} entries, including the blank first one.");
        using MemoryStream output = new(); byte[] prefix = (byte[])package.Prefix.Clone();
        int counts = prefix.Length - (format == 39 ? 68 : 60) + 8;
        if (format == AnimationPackage.WideFormat) BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(counts), package.Entries.Count);
        else
        {
            uint original = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(counts));
            BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(counts), (original & 65535) | ((uint)package.Entries.Count << 16));
        }
        output.Write(prefix);
        foreach (var entry in package.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Format != format) throw new InvalidDataException($"Animation entry {entry.Index} has a format-{entry.Format} header in a format-{format} file.");
            output.Write(WriteEntry(entry));
        }
        output.Write(package.Tail); return output.ToArray();
    }
    internal static byte[] WriteEntry(AnimationEntry entry)
    {
        bool wide = entry.Format == AnimationPackage.WideFormat;
        if (wide ? entry.Sequences.Count > MaximumWideSequences : entry.Sequences.Count > 255 || entry.References.Any(t => t.Count > 255))
            throw new InvalidDataException(wide ? $"A format-200 animation holds at most {MaximumWideSequences:N0} sequences." : "Sequence/reference counts are limited to 255.");
        using MemoryStream output = new(); byte[] header = (byte[])entry.Bytes.Clone();
        AnimationEntry.SetStoredCount(header, entry.Format, entry.CountsOffset, 0, entry.Sequences.Count);
        for (int i = 0; i < 8; i++) AnimationEntry.SetStoredCount(header, entry.Format, entry.CountsOffset, entry.ReferenceLanes[i], entry.References[i].Count);
        byte[] primary = SequenceBytes(entry.Primary);
        // The loader overwrites the inline copy. Preserve it on a no-op save, but
        // update both copies when the canonical header or payload length changes.
        if (!primary.AsSpan(0, 64).SequenceEqual(entry.OriginalPrimaryHeader)) primary.AsSpan(0, 64).CopyTo(header.AsSpan(entry.Version == 39 ? 200 : 196));
        if (entry.Version == 39) header[268] = checked((byte)entry.Puffers.Count);
        output.Write(header);
        for (int table = 0; table < 8; table++)
        {
            if (entry.Version == 39 && table == 3) foreach (var puffer in entry.Puffers) { if (puffer.Bytes.Length != 44) throw new InvalidDataException("Invalid puffer reference size."); output.Write(puffer.Bytes); }
            foreach (var reference in entry.References[table])
        {
            if (reference.Bytes.Length != AnimationPackage.ReferenceSizes[table]) throw new InvalidDataException("Invalid reference record size.");
            output.Write(reference.Bytes);
        }
        }
        output.Write(primary); foreach (var sequence in entry.Sequences) output.Write(SequenceBytes(sequence)); return output.ToArray();
    }
    private static byte[] SequenceBytes(AnimationSequence sequence)
    {
        using MemoryStream payload = new();
        foreach (var ev in sequence.Events)
        {
            if (ev.Bytes.Length < 12) throw new InvalidDataException("An event header requires 12 bytes.");
            byte[] bytes = (byte[])ev.Bytes.Clone(); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length); payload.Write(bytes);
        }
        payload.Write(sequence.OpaqueTail); byte[] header = (byte[])sequence.Bytes.Clone(); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60), checked((int)payload.Length));
        using MemoryStream result = new(); result.Write(header); payload.Position = 0; payload.CopyTo(result); return result.ToArray();
    }
}
