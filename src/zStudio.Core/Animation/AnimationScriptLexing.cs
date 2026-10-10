using System.Text;

namespace Recoil.Zbd.Core.Animation;

/// <summary>Bounded Latin-1 lexical work shared by both keyframe-script dialects; no whole-source line/token arrays.</summary>
internal static class AnimationScriptLexing
{
    internal const int MaximumTokenBytes = 4096, ScanBlock = 4096;

    internal static void Source(ReadOnlySpan<byte> bytes, string source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > Sources.SourceProject.MaximumSourceTextBytes)
            throw new InvalidDataException($"{JsonData.ShownText(source)} exceeds the {Sources.SourceProject.MaximumSourceTextBytes:N0}-byte keyframe source limit.");
    }

    internal static void Token(ReadOnlySpan<byte> word, string source, int line)
    {
        if (word.Length > MaximumTokenBytes)
            throw new InvalidDataException($"{JsonData.ShownText(source)}, line {line}: a keyframe token exceeds {MaximumTokenBytes:N0} bytes. Shorten the object name or numeric/keyword operand.");
    }

    internal static string Shown(ReadOnlySpan<byte> word) => word.Length <= JsonData.ShownCharacters
        ? Encoding.Latin1.GetString(word) : Encoding.Latin1.GetString(word[..JsonData.ShownCharacters]) + "…";

    internal static int IndexOf(ReadOnlySpan<byte> text, byte value, CancellationToken token)
    {
        for (int at = 0; at < text.Length; at += ScanBlock)
        {
            token.ThrowIfCancellationRequested();
            int found = text.Slice(at, Math.Min(ScanBlock, text.Length - at)).IndexOf(value);
            if (found >= 0) return at + found;
        }
        token.ThrowIfCancellationRequested(); return -1;
    }

    // SI uses String.Trim's Latin-1 whitespace; fallback deliberately uses only space, tab and CR separators.
    internal static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> text, CancellationToken token)
    {
        int first = 0, last = text.Length;
        while (first < last && char.IsWhiteSpace((char)text[first])) { if ((first & (ScanBlock - 1)) == 0) token.ThrowIfCancellationRequested(); first++; }
        while (last > first && char.IsWhiteSpace((char)text[last - 1])) { if ((last & (ScanBlock - 1)) == 0) token.ThrowIfCancellationRequested(); last--; }
        token.ThrowIfCancellationRequested(); return text[first..last];
    }

    internal static bool Is(ReadOnlySpan<byte> word, ReadOnlySpan<byte> expected)
    {
        if (word.Length != expected.Length) return false;
        for (int i = 0; i < word.Length; i++)
        {
            byte c = word[i]; if (c is >= (byte)'a' and <= (byte)'z') c -= 32;
            if (c != expected[i]) return false;
        }
        return true;
    }

    internal ref struct Lines(ReadOnlySpan<byte> bytes, CancellationToken token, Action<int>? scanned = null)
    {
        private readonly ReadOnlySpan<byte> bytes = bytes;
        private readonly CancellationToken token = token;
        private readonly Action<int>? scanned = scanned;
        private int next;
        private bool done;
        internal int Number { get; private set; }
        internal ReadOnlySpan<byte> Current { get; private set; }
        internal bool MoveNext()
        {
            token.ThrowIfCancellationRequested();
            if (done) return false;
            int start = next, end = bytes.Length;
            for (int at = start; at < bytes.Length; at += ScanBlock)
            {
                token.ThrowIfCancellationRequested();
                var block = bytes.Slice(at, Math.Min(ScanBlock, bytes.Length - at));
                int found = block.IndexOf((byte)'\n');
                scanned?.Invoke(at + (found >= 0 ? found + 1 : block.Length));
                token.ThrowIfCancellationRequested();
                if (found >= 0) { end = at + found; break; }
            }
            Current = bytes[start..end]; done = end == bytes.Length; next = done ? end : end + 1; Number++; return true;
        }
    }

    internal ref struct Words(ReadOnlySpan<byte> line, bool carriageReturn, string source, int lineNumber, CancellationToken token)
    {
        private readonly ReadOnlySpan<byte> line = line;
        private readonly bool carriageReturn = carriageReturn;
        private readonly string source = source;
        private readonly int lineNumber = lineNumber;
        private readonly CancellationToken token = token;
        private int next;
        private bool Separator(byte c) => c is (byte)' ' or (byte)'\t' || carriageReturn && c == '\r';
        internal bool Next(out ReadOnlySpan<byte> word)
        {
            token.ThrowIfCancellationRequested();
            while (next < line.Length && Separator(line[next])) { if ((next & (ScanBlock - 1)) == 0) token.ThrowIfCancellationRequested(); next++; }
            int start = next;
            while (next < line.Length && !Separator(line[next]))
            {
                next++;
                if (next - start > MaximumTokenBytes) Token(line[start..next], source, lineNumber);
            }
            word = line[start..next]; return next > start;
        }
    }
}
