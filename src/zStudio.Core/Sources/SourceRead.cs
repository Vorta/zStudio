namespace Recoil.Zbd.Core.Sources;

/// <summary>Read the opened file, never a length checked on a different path lookup.</summary>
public static class SourceRead
{
    /// <summary>Compare content through one held handle, without allocating another copy of the file.</summary>
    public static bool Matches(string path, long length, string sha256, CancellationToken token = default)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new JournalDigest(length, sha256).Matches(stream, token);
    }
    /// <summary>Asynchronously reads one held handle, applying the limit before allocating the result.</summary>
    public static async Task<byte[]> AllAsync(string path, long maximum, CancellationToken token = default)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await AllAsync(stream, path, maximum, token).ConfigureAwait(false);
    }
    internal static async Task<byte[]> AllAsync(string path, long maximum, DirectoryLease directories, CancellationToken token = default)
    {
        await using FileStream stream = directories.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await AllAsync(stream, path, maximum, token).ConfigureAwait(false);
    }
    private static async Task<byte[]> AllAsync(FileStream stream, string path, long maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int length = Length(stream, maximum, path);
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        byte[] extra = new byte[1];
        if (await stream.ReadAsync(extra, token).ConfigureAwait(false) != 0 || stream.Length != length)
            throw new IOException($"{JsonData.ShownText(path)} changed while it was read; try again.");
        return bytes;
    }
    /// <summary>Reads at most <paramref name="maximum"/> bytes from one opened handle, refusing growth or truncation.</summary>
    public static byte[] All(string path, long maximum, CancellationToken token = default)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
        return All(stream, maximum, path, token);
    }

    internal static byte[] All(Stream stream, long maximum, string name, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        int length = Length(stream, maximum, name);
        byte[] bytes = new byte[length];
        for (int offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            int read = stream.Read(bytes.AsSpan(offset, Math.Min(64 * 1024, bytes.Length - offset)));
            if (read == 0) throw new IOException($"{JsonData.ShownText(name)} shrank while it was read; try again.");
            offset += read;
        }
        token.ThrowIfCancellationRequested();
        if (stream.ReadByte() != -1 || stream.Length != length)
            throw new IOException($"{JsonData.ShownText(name)} changed while it was read; try again.");
        return bytes;
    }

    private static int Length(Stream stream, long maximum, string name)
    {
        long length = stream.Length;
        if (length < 0 || length > maximum || length > Array.MaxLength)
            throw new InvalidDataException($"{JsonData.ShownText(name)} exceeds {maximum:N0} bytes.");
        return (int)length;
    }
}
