using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recoil.Zbd.Core.Sources;
/// <summary>A content's length and lowercase SHA-256, as a journal records it.</summary>
internal sealed record JournalDigest(long Length, string Sha256)
{
    public static JournalDigest? Of(byte[]? bytes) => bytes == null ? null : new(bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    /// <summary>
    /// The digest of a stream's content from its position to its end, read in blocks of 1 MiB with <paramref name="token"/>
    /// observed before each, so a large file can be given up part-way.
    /// </summary>
    public static JournalDigest Of(Stream stream, CancellationToken token = default)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] block = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long length = 0;
            for (int read; ; length += read)
            {
                token.ThrowIfCancellationRequested();
                if ((read = stream.Read(block)) == 0) break;
                hash.AppendData(block, 0, read);
            }
            return new(length, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(block); }
    }
    /// <summary>
    /// Whether a stream (at its start) has this content. A stream of another length differs without being read, so a file
    /// another program replaced with gigabytes is not hashed to find that out.
    /// </summary>
    public bool Matches(Stream stream, CancellationToken token = default) => stream.Length == Length && Of(stream, token) == this;
    /// <summary>The digest of bytes held in memory (a document's or a built output's), without copying them.</summary>
    public static JournalDigest OfContent(ReadOnlySpan<byte> bytes) => new(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
}
