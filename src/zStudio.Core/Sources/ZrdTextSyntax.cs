using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A range of source text: the index of its first character and its length, both in characters.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    /// <summary>The index just past the last character of the span.</summary>
    public int End => Start + Length;
}

/// <summary>
/// A text zReader source that keeps the exact text of every node, so an edit changes only the characters it must.
/// </summary>
/// <remarks>
/// <para>Parsing uses <see cref="ZrdText"/>'s reader, so it accepts exactly the same files and builds the same tree. Nodes are
/// located by <see cref="ZrdNode.Id"/>: a scalar spans its token (quotes included), an array spans <c>(</c> through <c>)</c>
/// and the root spans the whole text.</para>
/// <para><see cref="ReplaceScalars"/> and <see cref="Rewrite"/> keep comments, blank lines, line endings and the spelling of
/// untouched values, and verify their result by parsing it again. Source text round-trips through Latin-1, so write it with
/// <see cref="Encode"/> rather than the ASCII <see cref="ZrdText.Encode"/>.</para>
/// </remarks>
public sealed class ZrdTextSyntax
{
    private readonly record struct Entry(ZrdNode Node, TextSpan Span);
    private readonly Dictionary<Guid, Entry> nodes;

    private ZrdTextSyntax(string text, ZrdNode root, Dictionary<Guid, Entry> nodes) { Text = text; Root = root; this.nodes = nodes; }

    /// <summary>The exact source text.</summary>
    public string Text { get; }
    /// <summary>The parsed tree, identical in meaning to <see cref="ZrdText.Parse(string, CancellationToken)"/> of <see cref="Text"/>.</summary>
    public ZrdNode Root { get; }

    /// <summary>Parses <paramref name="text"/> with the same acceptance, limits and errors as <see cref="ZrdText.Parse(string, CancellationToken)"/>.</summary>
    public static ZrdTextSyntax Parse(string text, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        Dictionary<Guid, Entry> nodes = [];
        var root = ZrdText.Parse(text, token, (node, span) => nodes.Add(node.Id, new(node, span)));
        return new(text, root, nodes);
    }

    /// <summary>Parses a Latin-1 source file, refusing one larger than <see cref="SourceProject.MaximumSourceTextBytes"/> before it is decoded.</summary>
    public static ZrdTextSyntax Parse(ReadOnlySpan<byte> bytes, CancellationToken token = default) => Parse(ZrdText.Decode(bytes), token);

    /// <summary>The Latin-1 bytes of a source text, the inverse of <see cref="Parse(ReadOnlySpan{byte}, CancellationToken)"/>.</summary>
    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.AsSpan().IndexOfAnyExceptInRange('\0', '\xFF') >= 0) throw new InvalidDataException("ZRD source text must be Latin-1.");
        return Encoding.Latin1.GetBytes(text);
    }

    /// <summary>The text of node <paramref name="nodeId"/>: a scalar's token (quotes included), an array's <c>(</c> through <c>)</c>, or the whole text for the root.</summary>
    public TextSpan SpanOf(Guid nodeId) => TryGetSpan(nodeId, out var span) ? span : throw new ArgumentException($"Node {nodeId} is not part of this source.", nameof(nodeId));

    /// <summary>The span of node <paramref name="nodeId"/>, when the node belongs to <see cref="Root"/>.</summary>
    public bool TryGetSpan(Guid nodeId, out TextSpan span)
    {
        bool found = nodes.TryGetValue(nodeId, out var entry);
        span = entry.Span;
        return found;
    }

    /// <summary>Equal kinds and values throughout: bits for integers and floats, text for strings, children in order. Ids and source offsets are ignored.</summary>
    public static bool StructurallyEqual(ZrdNode a, ZrdNode b)
    {
        ArgumentNullException.ThrowIfNull(a); ArgumentNullException.ThrowIfNull(b);
        Stack<(ZrdNode A, ZrdNode B)> pending = new(); pending.Push((a, b));
        while (pending.TryPop(out var pair))
        {
            var (x, y) = pair;
            if (ReferenceEquals(x, y)) continue;
            if (x.Kind != y.Kind) return false;
            switch (x.Kind)
            {
                case ZrdKind.Int or ZrdKind.Float: if (x.Bits != y.Bits) return false; break;
                case ZrdKind.String: if (!string.Equals(x.Text, y.Text, StringComparison.Ordinal)) return false; break;
                case ZrdKind.Array:
                    if (x.Children.Count != y.Children.Count) return false;
                    for (int i = 0; i < x.Children.Count; i++) pending.Push((x.Children[i], y.Children[i]));
                    break;
                default: return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The text with scalar values replaced in place. Keys are ids of scalar nodes of <see cref="Root"/>; values are the new
    /// scalars, of any scalar kind. Only the replaced tokens change, written as <see cref="ZrdText.Scalar"/> spells them; a
    /// replacement equal in kind and value to the original keeps the original spelling. A space is added only where a
    /// quoted token becomes a bare one directly next to another bare token.
    /// </summary>
    /// <exception cref="ArgumentException">A key is not a scalar of <see cref="Root"/>, or a value is not a scalar.</exception>
    /// <exception cref="InvalidDataException">A value cannot be written, the text would exceed <paramref name="maximumCharacters"/>, or the result does not read back as the replaced tree.</exception>
    public string ReplaceScalars(IReadOnlyDictionary<Guid, ZrdNode> replacements, CancellationToken token = default, int maximumCharacters = SourceProject.MaximumSourceTextBytes)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        List<(TextSpan Span, string Text)> edits = []; int replacementCharacters = 0;
        foreach (var (id, value) in replacements)
        {
            token.ThrowIfCancellationRequested();
            if (!nodes.TryGetValue(id, out var entry) || entry.Node.Kind == ZrdKind.Array || ReferenceEquals(entry.Node, Root)) throw new ArgumentException($"Node {id} is not a scalar of this source.", nameof(replacements));
            if (value is null || value.Kind is not (ZrdKind.Int or ZrdKind.Float or ZrdKind.String)) throw new ArgumentException($"The replacement for node {id} is not a scalar.", nameof(replacements));
            if (SameScalar(entry.Node, value)) continue;
            if (value.Kind == ZrdKind.String && value.Text.Length > maximumCharacters) throw TooLong(maximumCharacters);
            string scalar = ZrdText.Scalar(value, maximumCharacters - replacementCharacters);
            if (scalar.Length > maximumCharacters - replacementCharacters) throw TooLong(maximumCharacters);
            replacementCharacters += scalar.Length;
            edits.Add((entry.Span, scalar));
        }
        if (edits.Count == 0) return Text.Length <= maximumCharacters ? Text : throw TooLong(maximumCharacters);
        edits.Sort((x, y) => x.Span.Start.CompareTo(y.Span.Start));
        StringBuilder text = new(); int copied = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var (span, value) = edits[i];
            text.Append(Text, copied, span.Start - copied);
            // Bare tokens are delimited only by whitespace and syntax, so a quoted string turning bare may need a separator.
            if (text.Length > 0 && IsWord(text[^1]) && IsWord(value[0])) text.Append(' ');
            text.Append(value); copied = span.End;
            if (IsWord(value[^1]) && copied < Text.Length && IsWord(Text[copied]) && !(i + 1 < edits.Count && edits[i + 1].Span.Start == copied)) text.Append(' ');
            if (text.Length > maximumCharacters) throw TooLong(maximumCharacters);
        }
        if (Text.Length - copied > maximumCharacters - text.Length) throw TooLong(maximumCharacters);
        string result = text.Append(Text, copied, Text.Length - copied).ToString();
        if (!StructurallyEqual(ZrdText.Parse(result, token), Apply(Root))) throw new InvalidDataException("Replacing ZRD values would change other data; the source was left unchanged.");
        return result;

        ZrdNode Apply(ZrdNode node) => replacements.TryGetValue(node.Id, out var value) ? value
            : node.Kind == ZrdKind.Array ? node with { Children = node.Children.Select(Apply).ToArray() } : node;
    }

    /// <summary>
    /// The text of <paramref name="edited"/>, an edited version of <see cref="Root"/> in which retained nodes keep their ids.
    /// </summary>
    /// <remarks>
    /// Unchanged subtrees keep their exact text and a changed scalar replaces only its token. An array whose children changed
    /// keeps its own trivia and copies each retained child with the comments above it and the rest of its line. A removed
    /// child takes its comments and line with it; a comment block separated from it by a blank line stays. New children are
    /// written in the <see cref="ZrdText.Write"/> layout on their own lines, indented like their siblings, with the file's line
    /// endings; an array right after a string continues that string's line. Arrays laid out on one line stay on one line.
    /// The result must read back as <paramref name="edited"/>; otherwise, or when the text would exceed
    /// <paramref name="maximumCharacters"/>, the canonical <see cref="ZrdText.Write"/> text is returned with <c>Lossless</c> false.
    /// </remarks>
    /// <exception cref="InvalidDataException"><paramref name="edited"/> cannot be written in either form.</exception>
    public (string Text, bool Lossless) Rewrite(ZrdNode edited, CancellationToken token = default, int maximumCharacters = SourceProject.MaximumSourceTextBytes)
    {
        ArgumentNullException.ThrowIfNull(edited);
        if (edited.Kind != ZrdKind.Array) throw new InvalidDataException("A zReader source file stores a root array.");
        if (ReferenceEquals(edited, Root) && Text.Length <= maximumCharacters) return (Text, true);
        string? text;
        try
        {
            text = new Rewriter(this, token, maximumCharacters).Write(edited);
            if (!StructurallyEqual(ZrdText.Parse(text, token), edited)) text = null;
        }
        catch (InvalidDataException) { text = null; }
        return text != null ? (text, true) : (ZrdText.Write(edited, token, maximumCharacters), false);
    }

    private static bool SameScalar(ZrdNode original, ZrdNode value) => original.Kind == value.Kind &&
        (original.Kind == ZrdKind.String ? string.Equals(original.Text, value.Text, StringComparison.Ordinal) : original.Bits == value.Bits);
    /// <summary>A character that continues a bare token.</summary>
    private static bool IsWord(char c) => !char.IsWhiteSpace(c) && c is not ('(' or ')' or '"' or '#');
    private static InvalidDataException TooLong(int maximumCharacters) => new($"The text form exceeds {maximumCharacters:N0} characters.");

    /// <summary>
    /// Writes an edited tree over the original text. Each array's interior is laid out as an opening line (the rest of the
    /// <c>(</c> line), then per child its lead trivia (from the previous child's line end), its text and its trail (the rest
    /// of its line, through the newline), then the closing trivia before <c>)</c>.
    /// </summary>
    private sealed class Rewriter(ZrdTextSyntax source, CancellationToken token, int maximumCharacters)
    {
        private readonly string text = source.Text;
        private readonly string newline = NewlineOf(source.Text);
        private readonly StringBuilder output = new();
        private int budget = ZrdText.MaximumNodes;
        // The current output line: where it starts, its indentation and whether it holds only whitespace or an unfinished comment.
        private int lineStart, indentLength;
        private bool indenting = true, blank = true, commentOpen;
        private char last;

        public string Write(ZrdNode edited)
        {
            Children(source.Root, edited, 0, text.Length, isRoot: true, depth: 0, defaultIndent: "");
            return output.ToString();
        }

        private void Node(ZrdNode node, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > ZrdText.MaximumDepth || --budget < 0) throw new InvalidDataException("ZRD nesting or node limit exceeded.");
            if (source.nodes.TryGetValue(node.Id, out var entry) && !ReferenceEquals(entry.Node, source.Root))
            {
                var original = entry.Node; var span = entry.Span;
                if (ReferenceEquals(original, node) || original.Kind != ZrdKind.Array && SameScalar(original, node)) { Token(text.AsSpan(span.Start, span.Length)); return; }
                if (original.Kind == ZrdKind.Array && node.Kind == ZrdKind.Array)
                {
                    int start = span.Start + 1, end = span.End - 1;
                    // An empty array has no layout worth keeping unless it spans lines (and so may hold comments).
                    if (original.Children.Count == 0 && node.Children.Count > 0 && text.AsSpan(start, end - start).IndexOf('\n') < 0) { Fresh(node, depth); return; }
                    string indent = output.ToString(lineStart, indentLength);
                    Token("(");
                    Children(original, node, start, end, isRoot: false, depth + 1, indent + "  ");
                    Token(")");
                    return;
                }
            }
            if (node.Kind == ZrdKind.Array) { Fresh(node, depth); return; }
            if (node.Kind == ZrdKind.String && node.Text.Length > maximumCharacters - output.Length) throw TooLong(maximumCharacters);
            Token(ZrdText.Scalar(node, maximumCharacters - output.Length));
        }

        /// <summary>Writes a new array in the <see cref="ZrdText.Write"/> layout, indented from the current line; children that come from the original keep their text.</summary>
        private void Fresh(ZrdNode array, int depth)
        {
            string indent = output.ToString(lineStart, indentLength);
            Token("(");
            if (array.Children.Count == 0) { Token(")"); return; }
            bool lines = !ZrdText.IsShort(array);
            Insert(array.Children, 0, array.Children.Count, depth + 1, lines, indent + "  ");
            Append(lines ? indent : " ", trivia: true);
            Token(")");
        }

        /// <summary>Emits the interior [<paramref name="start"/>, <paramref name="end"/>) of <paramref name="original"/> with <paramref name="edited"/>'s children.</summary>
        private void Children(ZrdNode original, ZrdNode edited, int start, int end, bool isRoot, int depth, string defaultIndent)
        {
            var before = original.Children; var after = edited.Children; int n = before.Count, m = after.Count;
            var spans = new TextSpan[n]; int[] leadStart = new int[n], trailEnd = new int[n];
            // A root without children keeps all of its text (a header, say) ahead of anything added.
            int openEnd = n == 0 && isRoot ? end : LineEnd(start), previous = openEnd;
            Dictionary<Guid, int> index = new(n);
            for (int i = 0; i < n; i++)
            {
                spans[i] = source.nodes[before[i].Id].Span; leadStart[i] = previous;
                previous = trailEnd[i] = LineEnd(spans[i].End);
                index.TryAdd(before[i].Id, i);
            }
            int closeStart = previous;
            // Retained children are matched by id; the longest run that kept its order stays in place, the rest move.
            int[] matched = new int[m]; bool[] retained = new bool[n];
            for (int j = 0; j < m; j++)
            {
                matched[j] = -1;
                if (index.TryGetValue(after[j].Id, out int k) && !retained[k]) { matched[j] = k; retained[k] = true; }
            }
            bool[] anchor = Anchors(matched);
            bool multiline = isRoot || text.AsSpan(start, end - start).IndexOf('\n') >= 0;
            string? anyIndent = null;

            Append(text.AsSpan(start, openEnd - start), trivia: true);
            int next = 0, nextOriginal = 0, previousAnchor = -1;
            while (true)
            {
                int stay = next; while (stay < m && !anchor[stay]) stay++;
                int stayOriginal = stay < m ? matched[stay] : n;
                // Originals before the next anchor left this place: a comment block above them that a blank line separates stays.
                bool lineBreak = false;
                for (; nextOriginal < stayOriginal; nextOriginal++)
                {
                    int k = nextOriginal, detached = DetachedEnd(leadStart[k], spans[k].Start);
                    var kept = text.AsSpan(leadStart[k], detached - leadStart[k]);
                    if (kept.Contains('#')) { if (!blank || commentOpen) Newline(); Append(kept, trivia: true); }
                    lineBreak |= trailEnd[k] > spans[k].End && text[trailEnd[k] - 1] == '\n';
                }
                while (next < stay)
                {
                    token.ThrowIfCancellationRequested();
                    int moved = matched[next];
                    if (moved >= 0)
                    {
                        // A sibling that moved takes the comments directly above it and the rest of its line.
                        int detached = DetachedEnd(leadStart[moved], spans[moved].Start);
                        Append(text.AsSpan(detached, spans[moved].Start - detached), trivia: true);
                        Node(after[next], depth);
                        Append(text.AsSpan(spans[moved].End, trailEnd[moved] - spans[moved].End), trivia: true);
                        next++; continue;
                    }
                    int run = next; while (run < stay && matched[run] < 0) run++;
                    Insert(after, next, run, depth, multiline, multiline ? Indent(previousAnchor, stayOriginal < n ? stayOriginal : -1) : "");
                    next = run;
                }
                // Removing the end of a line must not join the lines around it.
                if (lineBreak && !blank) Newline();
                if (stay == m) break;
                int s = stayOriginal;
                Append(text.AsSpan(leadStart[s], spans[s].Start - leadStart[s]), trivia: true);
                Node(after[stay], depth);
                Append(text.AsSpan(spans[s].End, trailEnd[s] - spans[s].End), trivia: true);
                previousAnchor = s; next = stay + 1; nextOriginal = s + 1;
            }
            Append(text.AsSpan(closeStart, end - closeStart), trivia: true);

            string Indent(int previousStay, int nextStay)
            {
                if (previousStay >= 0 && LineIndent(spans[previousStay].Start) is { } previousIndent) return previousIndent;
                if (nextStay >= 0 && LineIndent(spans[nextStay].Start) is { } nextIndent) return nextIndent;
                if (anyIndent == null)
                {
                    anyIndent = defaultIndent;
                    for (int k = 0; k < n; k++) if (LineIndent(spans[k].Start) is { } own) { anyIndent = own; break; }
                }
                return anyIndent;
            }
        }

        /// <summary>Writes new children in the <see cref="ZrdText.Write"/> layout: each on its own line, or spaced on one line.</summary>
        private void Insert(IReadOnlyList<ZrdNode> siblings, int from, int to, int depth, bool multiline, string indent)
        {
            for (int j = from; j < to; j++)
            {
                var child = siblings[j];
                if (!multiline)
                {
                    if (output.Length > 0 && !char.IsWhiteSpace(last)) Append(" ", trivia: true);
                    Node(child, depth);
                    continue;
                }
                if (child.Kind == ZrdKind.Array && j > 0 && siblings[j - 1].Kind == ZrdKind.String && !blank && !commentOpen) Append(" ", trivia: true);
                else
                {
                    if (!blank || commentOpen) Newline();
                    if (output.Length == lineStart) Append(indent, trivia: true);
                }
                Node(child, depth);
                if (!(child.Kind == ZrdKind.String && j + 1 < to && siblings[j + 1].Kind == ZrdKind.Array)) Newline();
            }
        }

        private void Token(ReadOnlySpan<char> value)
        {
            if (commentOpen) Newline();
            else if (output.Length > 0 && IsWord(last) && IsWord(value[0])) Append(" ", trivia: true);
            Append(value, trivia: false);
        }

        private void Newline() => Append(newline, trivia: true);

        /// <summary>The file's line ending, taken from its first line; LF when it has none.</summary>
        private static string NewlineOf(string text)
        {
            int first = text.IndexOf('\n');
            return first > 0 && text[first - 1] == '\r' ? "\r\n" : "\n";
        }

        private void Append(ReadOnlySpan<char> piece, bool trivia)
        {
            if (piece.IsEmpty) return;
            // Text after an unfinished comment would become part of it.
            if (commentOpen && piece[0] != '\n' && !piece.StartsWith("\r\n", StringComparison.Ordinal)) Newline();
            if (piece.Length > maximumCharacters - output.Length) throw TooLong(maximumCharacters);
            int at = output.Length; output.Append(piece); last = piece[^1];
            int lineBreak = piece.LastIndexOf('\n');
            if (lineBreak >= 0)
            {
                lineStart = at + lineBreak + 1; indentLength = 0; indenting = blank = true; commentOpen = false;
                piece = piece[(lineBreak + 1)..];
            }
            foreach (char c in piece)
            {
                if (!indenting && !blank) break;
                if (indenting) { if (c is ' ' or '\t') indentLength++; else indenting = false; }
                if (!char.IsWhiteSpace(c)) blank = false;
            }
            // Trivia holds only whitespace and comments, so a '#' on its last line leaves that comment open.
            if (trivia && piece.Contains('#')) commentOpen = true;
        }

        /// <summary>The end of the trivia that finishes the line at <paramref name="from"/>: through its newline or to the end of the text, or <paramref name="from"/> when a token follows on the line.</summary>
        private int LineEnd(int from)
        {
            int p = from;
            while (p < text.Length && text[p] != '\n' && char.IsWhiteSpace(text[p])) p++;
            if (p < text.Length && text[p] == '#') { int lineBreak = text.IndexOf('\n', p); p = lineBreak < 0 ? text.Length : lineBreak; }
            return p == text.Length ? p : text[p] == '\n' ? p + 1 : from;
        }

        /// <summary>The end of a lead's last whitespace-only line: what precedes it is separated from the child by a blank line.</summary>
        private int DetachedEnd(int lead, int child)
        {
            int detached = lead, line = lead;
            for (int p = lead; p < child; p++)
            {
                if (text[p] != '\n') continue;
                if (text.AsSpan(line, p - line).IsWhiteSpace()) detached = p + 1;
                line = p + 1;
            }
            return detached;
        }

        /// <summary>The indentation before a token that begins its line, or null when something precedes it on the line.</summary>
        private string? LineIndent(int at)
        {
            int p = at;
            while (p > 0 && text[p - 1] is ' ' or '\t') p--;
            return p == 0 || text[p - 1] == '\n' ? text[p..at] : null;
        }

        /// <summary>The longest strictly increasing run of matched original indices: the children that keep their order.</summary>
        private static bool[] Anchors(int[] matched)
        {
            int m = matched.Length, length = 0; int[] tails = new int[m], previous = new int[m];
            for (int j = 0; j < m; j++)
            {
                if (matched[j] < 0) continue;
                int low = 0, high = length;
                while (low < high) { int middle = (low + high) >>> 1; if (matched[tails[middle]] < matched[j]) low = middle + 1; else high = middle; }
                previous[j] = low > 0 ? tails[low - 1] : -1; tails[low] = j;
                if (low == length) length++;
            }
            bool[] anchors = new bool[m];
            for (int j = length > 0 ? tails[length - 1] : -1; j >= 0; j = previous[j]) anchors[j] = true;
            return anchors;
        }
    }
}
