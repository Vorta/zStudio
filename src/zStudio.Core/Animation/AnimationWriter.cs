using System.Buffers.Binary;

namespace zStudio.Core.Animation;

public static class AnimationWriter
{
    public static byte[] Write(AnimationPackage package, CancellationToken token = default)
    {
        if (package.Entries.Count > ushort.MaxValue) throw new InvalidDataException("Animation entry count exceeds 65535.");
        using MemoryStream output = new(); byte[] prefix = (byte[])package.Prefix.Clone();
        int counts = prefix.Length - (package.Version == 39 ? 68 : 60) + 8; uint original = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(counts));
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(counts), (original & 65535) | ((uint)package.Entries.Count << 16)); output.Write(prefix);
        foreach (var entry in package.Entries) { token.ThrowIfCancellationRequested(); output.Write(WriteEntry(entry)); }
        output.Write(package.Tail); return output.ToArray();
    }
    internal static byte[] WriteEntry(AnimationEntry entry)
    {
        if (entry.Sequences.Count > 255 || entry.References.Any(t => t.Count > 255)) throw new InvalidDataException("Sequence/reference counts are limited to 255.");
        using MemoryStream output = new(); byte[] header = (byte[])entry.Bytes.Clone(); header[entry.CountsOffset] = (byte)entry.Sequences.Count;
        for (int i = 0; i < 8; i++) header[entry.CountsOffset + entry.ReferenceLanes[i]] = (byte)entry.References[i].Count;
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
