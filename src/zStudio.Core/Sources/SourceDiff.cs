using System.Text;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A line of a source diff: ' ' kept, '-' only on disk, '+' only in the workspace; numbers are 1-based (0: not on that side).</summary>
public sealed record SourceDiffLine(char Kind, int DiskLine, int WorkingLine, string Text);
/// <summary>A bounded description of how a source file's working content differs from the file on disk.</summary>
public sealed record SourceDiffReport(string File, bool OnDisk, bool InWorkspace, bool Text, int DiskBytes, int WorkingBytes, int ChangedLines, IReadOnlyList<SourceDiffLine> Lines, bool Truncated);

/// <summary>
/// Line differences between two versions of a source text (Myers' O(ND) algorithm), with three lines of context around
/// each change. Binary files and edits too large to describe are summarized rather than listed.
/// </summary>
public static class SourceDiff
{
    public const int MaximumEditDistance = 1000, ContextLines = 3, MaximumLineCharacters = 400;

    public static SourceDiffReport Describe(string file, byte[]? disk, byte[]? working, int maximumLines = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLines);
        bool text = IsText(disk) && IsText(working);
        int diskBytes = disk?.Length ?? 0, workingBytes = working?.Length ?? 0;
        if (!text || disk == null && working == null) return new(file, disk != null, working != null, text, diskBytes, workingBytes, 0, [], false);
        string[] a = Lines(disk), b = Lines(working);
        var script = Edits(a, b);
        if (script == null) return new(file, disk != null, working != null, true, diskBytes, workingBytes, -1, [], true);
        // Keep three lines of context around every change; collapse the rest.
        bool[] keep = new bool[script.Count];
        for (int i = 0; i < script.Count; i++)
            if (script[i].Kind != ' ')
                for (int j = Math.Max(0, i - ContextLines); j <= Math.Min(script.Count - 1, i + ContextLines); j++) keep[j] = true;
        List<SourceDiffLine> lines = []; bool truncated = false;
        for (int i = 0; i < script.Count; i++)
        {
            if (!keep[i]) continue;
            if (lines.Count == maximumLines) { truncated = true; break; }
            lines.Add(script[i]);
        }
        return new(file, disk != null, working != null, true, diskBytes, workingBytes, script.Count(l => l.Kind != ' '), lines, truncated);
    }

    private static bool IsText(byte[]? bytes) => bytes == null || bytes.Length <= SourceProject.MaximumSourceTextBytes && !bytes.AsSpan(0, Math.Min(bytes.Length, 65536)).Contains((byte)0);
    private static string[] Lines(byte[]? bytes) => bytes == null || bytes.Length == 0 ? [] : Encoding.Latin1.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>The edit script from <paramref name="a"/> to <paramref name="b"/>, or null when more than <see cref="MaximumEditDistance"/> lines differ.</summary>
    private static List<SourceDiffLine>? Edits(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length, max = Math.Min(n + m, MaximumEditDistance), offset = max + 1;
        int[] v = new int[2 * max + 3];
        List<int[]> trace = [];
        int found = -1;
        for (int d = 0; d <= max && found < 0; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || k != d && v[offset + k - 1] < v[offset + k + 1] ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }
        if (found < 0) return null;
        // Walk the recorded frontiers back from the end to recover the path.
        List<SourceDiffLine> script = [];
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            var frontier = trace[d];
            int k = cx - cy;
            int previousK = k == -d || k != d && frontier[offset + k - 1] < frontier[offset + k + 1] ? k + 1 : k - 1;
            int px = frontier[offset + previousK], py = px - previousK;
            while (cx > px && cy > py) { cx--; cy--; script.Add(new(' ', cx + 1, cy + 1, Clip(a[cx]))); }
            if (cx == px) { cy--; script.Add(new('+', 0, cy + 1, Clip(b[cy]))); }
            else { cx--; script.Add(new('-', cx + 1, 0, Clip(a[cx]))); }
        }
        while (cx > 0 && cy > 0) { cx--; cy--; script.Add(new(' ', cx + 1, cy + 1, Clip(a[cx]))); }
        script.Reverse();
        return script;
        static string Clip(string line) => line.Length <= MaximumLineCharacters ? line : line[..MaximumLineCharacters] + "…";
    }
}
