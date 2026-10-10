namespace Recoil.Zbd.Core.Export;

/// <summary>Scans the already bounded interchange text without a string or array per line/word.</summary>
internal sealed class ObjText(CancellationToken token, Action<string>? checkpoint = null)
{
    internal void Check(string phase)
    {
        token.ThrowIfCancellationRequested();
        checkpoint?.Invoke(phase);
        token.ThrowIfCancellationRequested();
    }

    internal Lines ReadLines(string text, string phase) => new(text, this, phase);

    internal ref struct Lines(string text, ObjText owner, string phase)
    {
        private int position;
        private bool finished;
        internal bool MoveNext(out Range range)
        {
            owner.Check(phase);
            if (finished) { range = default; return false; }
            int start = position;
            while (position < text.Length)
            {
                owner.Check(phase);
                int count = Math.Min(1024, text.Length - position);
                int newline = text.AsSpan(position, count).IndexOf('\n');
                if (newline >= 0)
                {
                    range = start..(position + newline); position += newline + 1; return true;
                }
                position += count;
            }
            range = start..text.Length; finished = true; return true;
        }
    }

    internal Range Trim(ReadOnlySpan<char> text, string phase)
    {
        int start = 0, end = text.Length;
        while (start < end && char.IsWhiteSpace(text[start]))
        { if ((start & 1023) == 0) Check(phase); start++; }
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        { if ((end & 1023) == 0) Check(phase); end--; }
        return start..end;
    }

    // An OBJ comment starts at '#', including directly after an operand. Return at most
    // destination.Length words, or one more to report excess operands without scanning/materializing them.
    internal int Fields(ReadOnlySpan<char> line, Span<Range> destination)
    {
        int offset = 0, count = 0;
        while (offset < line.Length)
        {
            while (offset < line.Length && char.IsWhiteSpace(line[offset]))
            { if ((offset & 1023) == 0) Check("obj"); offset++; }
            if (offset == line.Length || line[offset] == '#') break;
            if (count == destination.Length) return count + 1;
            int start = offset;
            while (offset < line.Length && !char.IsWhiteSpace(line[offset]) && line[offset] != '#')
            { if ((offset & 1023) == 0) Check("obj"); offset++; }
            destination[count++] = start..offset;
        }
        return count;
    }

    internal bool Equal(ReadOnlySpan<char> left, ReadOnlySpan<char> right, string phase)
    {
        if (left.Length != right.Length) return false;
        for (int offset = 0; offset < left.Length; offset += 1024)
        {
            Check(phase); int count = Math.Min(1024, left.Length - offset);
            if (!left.Slice(offset, count).SequenceEqual(right.Slice(offset, count))) return false;
        }
        return true;
    }
}
