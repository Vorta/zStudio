using System.Buffers.Binary;
using System.Security.Cryptography;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

/// <summary>Compact presentation keys; complete source records remain authoritative for all edits.</summary>
internal sealed class SceneTreeProvenance
{
    private readonly Dictionary<string, byte[]> prefixes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MissionPickupSource, string> sources;
    private readonly Dictionary<IReadOnlyList<MissionPickupSource>, string> scopes = new(ReferenceEqualityComparer.Instance);
    internal long PrefixCharacters { get; private set; }
    internal int PrefixCount => prefixes.Count;

    internal SceneTreeProvenance(IEqualityComparer<MissionPickupSource>? comparer = null)
        => sources = new(comparer ?? ReferenceEqualityComparer.Instance);

    private byte[] Prefix(string value)
    {
        if (prefixes.TryGetValue(value, out var result)) return result;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[512];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value.Length);
        hash.AppendData(buffer[..4]);
        // Frame full UTF-16 code units, including surrogate pairs, without a whole-prefix encoding buffer.
        for (int start = 0; start < value.Length; start += buffer.Length / 2)
        {
            int count = Math.Min(buffer.Length / 2, value.Length - start);
            for (int i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(buffer[(2 * i)..], value[start + i]);
            hash.AppendData(buffer[..(2 * count)]);
        }
        PrefixCharacters += value.Length;
        return prefixes[value] = hash.GetHashAndReset();
    }

    internal string Source(MissionPickupSource source)
    {
        if (sources.TryGetValue(source, out var key)) return key;
        // Preserve the old tree tuple exactly: ResourceName belongs to lookup equality, not retained selection.
        Span<byte> framed = stackalloc byte[44];
        BinaryPrimitives.WriteInt32LittleEndian(framed, 1);
        Prefix(source.ArchivePath).CopyTo(framed[4..]);
        BinaryPrimitives.WriteInt32LittleEndian(framed[36..], source.AssetIndex);
        BinaryPrimitives.WriteInt32LittleEndian(framed[40..], source.RecordIndex);
        return sources[source] = Convert.ToHexString(SHA256.HashData(framed));
    }

    internal string Scope(IReadOnlyList<MissionPickupSource> matches)
    {
        if (scopes.TryGetValue(matches, out var key)) return key;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, 2);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], matches.Count);
        hash.AppendData(header);
        foreach (string source in matches.Select(Source).Order(StringComparer.Ordinal))
            hash.AppendData(Convert.FromHexString(source)); // Fixed 32-byte frames, never authored text.
        return scopes[matches] = Convert.ToHexString(hash.GetHashAndReset());
    }

    internal string Actor(int sourceRoot, string name)
    {
        Span<byte> framed = stackalloc byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(framed, 3);
        Prefix(name).CopyTo(framed[4..]);
        BinaryPrimitives.WriteInt32LittleEndian(framed[36..], sourceRoot);
        return Convert.ToHexString(SHA256.HashData(framed));
    }
}
