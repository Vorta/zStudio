using System.Text;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A line of a source diff: ' ' kept, '-' only on disk, '+' only in the workspace; numbers are 1-based (0: not on that side).</summary>
public sealed record SourceDiffLine(char Kind, int DiskLine, int WorkingLine, string Text);
/// <summary>A bounded description of how a source file's working content differs from the file on disk.</summary>
public sealed record SourceDiffReport(string File, bool OnDisk, bool InWorkspace, bool Text, int DiskBytes, int WorkingBytes, int ChangedLines, IReadOnlyList<SourceDiffLine> Lines, bool Truncated);

/// <summary>
/// Line differences between two versions of a source text (Myers' O(ND) algorithm), with three lines of context around
/// each change. Binary files and edits too large to describe are summarized rather than listed. The lines both versions
/// begin and end with are skipped as bytes, without being split; only the lines between them (and their context) are
/// indexed and compared, within <see cref="MaximumComparedLines"/> and <see cref="MaximumComparisons"/>, and text is
/// decoded only for the lines returned.
/// </summary>
public static class SourceDiff
{
    public const int MaximumEditDistance = 1000, ContextLines = 3, MaximumLineCharacters = 400;
    /// <summary>The most lines (both versions together) compared between the lines they begin and end with; more are summarized.</summary>
    public const int MaximumComparedLines = 200_000;
    /// <summary>The most steps the edit search takes before the change is summarized instead.</summary>
    public const long MaximumComparisons = 100_000_000;

    public static SourceDiffReport Describe(string file, byte[]? disk, byte[]? working, int maximumLines = 200, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLines);
        bool text = IsText(disk) && IsText(working);
        int diskBytes = disk?.Length ?? 0, workingBytes = working?.Length ?? 0;
        SourceDiffReport Report(int changed, IReadOnlyList<SourceDiffLine> lines, bool truncated) => new(file, disk != null, working != null, true, diskBytes, workingBytes, changed, lines, truncated);
        if (!text || disk == null && working == null) return new(file, disk != null, working != null, text, diskBytes, workingBytes, 0, [], false);
        byte[] a = disk ?? [], b = working ?? [];
        // Identical bytes are identical lines, however many.
        if (a.AsSpan().SequenceEqual(b)) return Report(0, [], false);
        token.ThrowIfCancellationRequested();

        // Whole lines both versions begin with (up to the last line feed of the bytes they share), and, after those, the
        // lines both end with (from a line start inside the bytes they end with) need no comparison; context lines from
        // both stay. The shared end is the same bytes on both sides, so a line start in it lies as far from either end.
        int prefix = a.AsSpan(0, a.AsSpan().CommonPrefixLength(b)).LastIndexOf((byte)'\n') + 1;
        int suffix = 0, limit = Math.Min(a.Length, b.Length) - prefix;
        while (suffix < limit && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
        // int.MaxValue: every line to the end, including the last (unterminated, or empty after a final line feed).
        int start = prefix, end = a.AsSpan(a.Length - suffix).IndexOf((byte)'\n') is var feed and >= 0 ? a.Length - suffix + feed + 1 : int.MaxValue;
        for (int i = 0; i < ContextLines && start > 0; i++) start = a.AsSpan(0, start - 1).LastIndexOf((byte)'\n') + 1;
        for (int i = 0; i < ContextLines && end != int.MaxValue; i++)
            end = end < a.Length && a.AsSpan(end).IndexOf((byte)'\n') is var next and >= 0 ? end + next + 1 : int.MaxValue;
        int skipped = a.AsSpan(0, start).Count((byte)'\n');
        token.ThrowIfCancellationRequested();
        if (Index(a, start, end, MaximumComparedLines, token) is not { } left
            || Index(b, start, end == int.MaxValue ? int.MaxValue : end - a.Length + b.Length, MaximumComparedLines - left.Count, token) is not { } right)
            return Report(-1, [], true);
        var (x, y) = Identify(a, left, b, right, token);
        if (Edits(x, y, token) is not { } script) return Report(-1, [], true);

        // Keep three lines of context around every change; collapse the rest.
        List<SourceDiffLine> shown = []; bool truncated = false; int changed = 0, last = -1;
        for (int i = 0; i < script.Count; i++)
        {
            if (script[i].Kind == ' ') continue;
            changed++;
            for (int j = Math.Max(last + 1, i - ContextLines); j <= Math.Min(script.Count - 1, i + ContextLines) && !truncated; j++)
            {
                if (shown.Count == maximumLines) { truncated = true; break; }
                var (kind, da, dw) = script[j];
                shown.Add(kind == '+' ? new('+', 0, skipped + dw + 1, Clip(b, right, dw)) : new(kind, skipped + da + 1, kind == '-' ? 0 : skipped + dw + 1, Clip(a, left, da)));
                last = j;
            }
        }
        return Report(changed, shown, truncated);
    }

    private static bool IsText(byte[]? bytes) => bytes == null || bytes.Length <= SourceProject.MaximumSourceTextBytes && !bytes.AsSpan(0, Math.Min(bytes.Length, 65536)).Contains((byte)0);

    /// <summary>Where some lines of a text lie: line i is bytes Starts[i] to Ends[i] (its line feed, and a carriage return before it, excluded).</summary>
    private sealed record LineIndex(int[] Starts, int[] Ends)
    {
        public int Count => Starts.Length;
    }

    /// <summary>
    /// The lines of <paramref name="bytes"/> from the line start <paramref name="from"/> up to the line start
    /// <paramref name="before"/> (int.MaxValue: to the end), as the text splits at line feeds with CR LF read as LF: n line
    /// feeds make n + 1 lines, an empty file none. Null when more than <paramref name="budget"/> lines would be indexed;
    /// they are counted before anything is allocated for them.
    /// </summary>
    private static LineIndex? Index(byte[] bytes, int from, int before, int budget, CancellationToken token)
    {
        if (bytes.Length == 0) return new([], []);
        long count = bytes.AsSpan(from, Math.Min(before, bytes.Length) - from).Count((byte)'\n') + (before == int.MaxValue ? 1L : 0);
        if (count > budget) return null;
        int[] starts = new int[count], ends = new int[count];
        for (int i = 0, at = from; i < count; i++)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            int feed = at < bytes.Length ? bytes.AsSpan(at).IndexOf((byte)'\n') : -1, finish = feed < 0 ? bytes.Length : at + feed;
            starts[i] = at;
            ends[i] = feed >= 0 && finish > at && bytes[finish - 1] == '\r' ? finish - 1 : finish;
            at = finish + 1;
        }
        return new(starts, ends);
    }

    private static ReadOnlySpan<byte> Line(byte[] bytes, LineIndex index, int i) => bytes.AsSpan(index.Starts[i], index.Ends[i] - index.Starts[i]);
    /// <summary>A line's text (Latin-1, as sources are read), cut to <see cref="MaximumLineCharacters"/> before it is decoded.</summary>
    private static string Clip(byte[] bytes, LineIndex index, int i)
    {
        var line = Line(bytes, index, i);
        return line.Length <= MaximumLineCharacters ? Encoding.Latin1.GetString(line) : Encoding.Latin1.GetString(line[..MaximumLineCharacters]) + "…";
    }

    /// <summary>Numbers the indexed lines of both texts so that equal lines (as bytes) have equal numbers; each line is hashed once.</summary>
    private static (int[] Left, int[] Right) Identify(byte[] a, LineIndex left, byte[] b, LineIndex right, CancellationToken token)
    {
        // A key is a left line (k ≥ 0) or a right line (~k).
        ReadOnlySpan<byte> Of(int key) => key >= 0 ? Line(a, left, key) : Line(b, right, ~key);
        Dictionary<int, int> ids = new(new KeyComparer(Of));
        int[] Number(int count, bool isLeft)
        {
            int[] numbers = new int[count];
            for (int i = 0; i < count; i++)
            {
                if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                int key = isLeft ? i : ~i;
                if (!ids.TryGetValue(key, out numbers[i])) ids[key] = numbers[i] = ids.Count;
            }
            return numbers;
        }
        return (Number(left.Count, true), Number(right.Count, false));
    }
    private delegate ReadOnlySpan<byte> LineOf(int key);
    private sealed class KeyComparer(LineOf line) : IEqualityComparer<int>
    {
        public bool Equals(int x, int y) => line(x).SequenceEqual(line(y));
        public int GetHashCode(int key) { HashCode hash = new(); hash.AddBytes(line(key)); return hash.ToHashCode(); }
    }

    /// <summary>
    /// The edit script from line numbers <paramref name="a"/> to <paramref name="b"/> (kind and positions on both sides),
    /// or null when more than <see cref="MaximumEditDistance"/> lines differ or the search would take more than
    /// <see cref="MaximumComparisons"/> steps.
    /// </summary>
    private static List<(char Kind, int A, int B)>? Edits(int[] a, int[] b, CancellationToken token)
    {
        int n = a.Length, m = b.Length, max = Math.Min(n + m, MaximumEditDistance), offset = max + 1;
        int[] v = new int[2 * max + 3];
        // The frontier before each round d, on diagonals −d to d.
        List<int[]> trace = [];
        int found = -1; long work = 0;
        for (int d = 0; d <= max && found < 0; d++)
        {
            token.ThrowIfCancellationRequested();
            trace.Add(v[(offset - d)..(offset + d + 1)]);
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || k != d && v[offset + k - 1] < v[offset + k + 1] ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k, from = x;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[offset + k] = x; work += x - from + 1;
                if (x >= n && y >= m) { found = d; break; }
            }
            if (work > MaximumComparisons) return null;
        }
        if (found < 0) return null;
        // Walk the recorded frontiers back from the end to recover the path.
        List<(char, int, int)> script = [];
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            var frontier = trace[d];
            int k = cx - cy;
            int previousK = k == -d || k != d && frontier[k - 1 + d] < frontier[k + 1 + d] ? k + 1 : k - 1;
            int px = frontier[previousK + d], py = px - previousK;
            while (cx > px && cy > py) { cx--; cy--; script.Add((' ', cx, cy)); }
            if (cx == px) { cy--; script.Add(('+', cx, cy)); }
            else { cx--; script.Add(('-', cx, cy)); }
        }
        while (cx > 0 && cy > 0) { cx--; cy--; script.Add((' ', cx, cy)); }
        script.Reverse();
        return script;
    }
}
