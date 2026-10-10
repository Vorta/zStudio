namespace Recoil.Zbd.Core.Sources;

/// <summary>Chunked searches shared by source lexers; no substring is allocated while searching.</summary>
internal static class SourceTextScan
{
    internal const int Chunk = 4096;

    internal static int Find(string text, char value, int start, int end, CancellationToken token, Action? checkpoint = null)
    {
        while (start < end)
        {
            checkpoint?.Invoke();
            token.ThrowIfCancellationRequested();
            int length = Math.Min(Chunk, end - start), found = text.IndexOf(value, start, length);
            token.ThrowIfCancellationRequested();
            if (found >= 0) return found;
            start += length;
        }
        token.ThrowIfCancellationRequested();
        return -1;
    }

    internal static int FindAny(string text, char[] values, int start, int end, CancellationToken token, Action? checkpoint = null)
    {
        while (start < end)
        {
            checkpoint?.Invoke();
            token.ThrowIfCancellationRequested();
            int length = Math.Min(Chunk, end - start), found = text.IndexOfAny(values, start, length);
            token.ThrowIfCancellationRequested();
            if (found >= 0) return found;
            start += length;
        }
        token.ThrowIfCancellationRequested();
        return -1;
    }

    internal static int Count(string text, char value, int length, CancellationToken token)
    {
        int count = 0;
        for (int at = 0; at < length; at += Chunk)
        {
            token.ThrowIfCancellationRequested();
            count += text.AsSpan(at, Math.Min(Chunk, length - at)).Count(value);
        }
        token.ThrowIfCancellationRequested();
        return count;
    }
}
