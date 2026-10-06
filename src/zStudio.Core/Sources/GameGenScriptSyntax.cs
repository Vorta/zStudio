using System.Text;

namespace Recoil.Zbd.Core.Sources;

/// <summary>One physical line of a gamegen script: its 1-based number, where it lies in the text, and its tokens with their places.</summary>
public sealed record GameGenScriptLine(int Number, int Start, int Length, IReadOnlyList<string> Tokens, IReadOnlyList<TextSpan> Spans)
{
    /// <summary>Whether the line holds an instruction (blank and comment-only lines hold none).</summary>
    public bool IsInstruction => Tokens.Count > 0;
}

/// <summary>
/// A gamegen script (<c>.gs</c>/<c>.gw</c>) kept as written: every line with the tokens the engine reads from it (see
/// <see cref="GameGenScriptText"/>) and where each token is. Edits replace tokens or insert and comment out whole lines, so
/// comments, spacing, separators and line endings elsewhere stay exactly as they were.
/// </summary>
public sealed class GameGenScriptSyntax
{
    private static readonly char[] Separators = [',', ' ', '\t', '\n'];
    public string Text { get; }
    /// <summary>The line ending most of the file uses; inserted lines use it.</summary>
    public string Newline { get; }
    public IReadOnlyList<GameGenScriptLine> Lines { get; }

    /// <remarks>A text past <see cref="GameGenScriptText.CheckBounds"/> is refused before its lines are made.</remarks>
    private GameGenScriptSyntax(string text)
    {
        GameGenScriptText.CheckBounds(text);
        Text = text;
        int crlf = 0, lf = 0;
        List<GameGenScriptLine> lines = []; int start = 0, number = 0;
        while (start <= text.Length)
        {
            int end = text.IndexOf('\n', start); bool last = end < 0; if (last) end = text.Length;
            int length = end - start;
            if (!last) { if (length > 0 && text[end - 1] == '\r') { crlf++; length--; } else lf++; }
            number++;
            // A file's final newline does not start another line.
            if (last && length == 0 && number > 1) break;
            var (tokens, spans) = Tokenize(text, start, length);
            lines.Add(new(number, start, length, tokens, spans));
            if (last) break;
            start = end + 1;
        }
        Lines = lines; Newline = crlf > lf ? "\r\n" : "\n";
    }

    public static GameGenScriptSyntax Parse(ReadOnlySpan<byte> bytes) => new(GameGenScriptText.Decode(bytes));
    public static GameGenScriptSyntax Parse(string text) => new(text);
    public byte[] Encode() => Encoding.Latin1.GetBytes(Text);
    /// <summary>The line with a 1-based number.</summary>
    public GameGenScriptLine Line(int number) => number >= 1 && number <= Lines.Count ? Lines[number - 1] : throw new InvalidDataException($"The script has no line {number}.");

    /// <summary>
    /// The text with tokens of one line replaced (token index → new text). The line must still read as the same number of
    /// tokens with the new values; nothing else in the file changes.
    /// </summary>
    public string ReplaceTokens(int number, IReadOnlyDictionary<int, string> replacements)
    {
        var line = Line(number);
        if (!line.IsInstruction) throw new InvalidDataException($"Line {number} holds no instruction.");
        List<string> expected = [.. line.Tokens];
        StringBuilder text = new(Text.Length + 64); int at = 0;
        foreach (var (index, value) in replacements.OrderBy(r => r.Key))
        {
            if (index < 0 || index >= line.Tokens.Count) throw new InvalidDataException($"Line {number} has no token {index}.");
            if (value.Length == 0 || value.Any(c => c > 255 || c == '\0' || c == '#' || c == '\r' || Separators.Contains(c))) throw new InvalidDataException($"'{value}' cannot be one script token.");
            var span = line.Spans[index];
            text.Append(Text, at, span.Start - at).Append(value); at = span.End;
            expected[index] = value;
        }
        text.Append(Text, at, Text.Length - at);
        string result = text.ToString();
        var check = new GameGenScriptSyntax(result);
        if (check.Lines.Count != Lines.Count || !check.Line(number).Tokens.SequenceEqual(expected)) throw new InvalidDataException($"Line {number} would not read back as intended.");
        return result;
    }

    /// <summary>The text with whole instruction lines inserted before line <paramref name="number"/> (one past the last line appends).</summary>
    public string InsertLines(int number, IReadOnlyList<IReadOnlyList<string>> instructions)
    {
        if (number < 1 || number > Lines.Count + 1) throw new InvalidDataException($"The script has no line {number}.");
        StringBuilder inserted = new();
        foreach (var tokens in instructions)
            inserted.Append(GameGenScriptText.WriteLine(tokens) ?? throw new InvalidDataException("An instruction cannot be written as script text.")).Append(Newline);
        int at = number <= Lines.Count ? Lines[number - 1].Start : Text.Length;
        // Appending after a last line without a newline starts a new line first.
        string prefix = number > Lines.Count && Text.Length > 0 && !Text.EndsWith('\n') ? Newline : "";
        string result = Text[..at] + prefix + inserted + Text[at..];
        // Each inserted line must read back as the tokens asked for (a token ending in a carriage return would not).
        var check = new GameGenScriptSyntax(result);
        int first = 1 + result.AsSpan(0, at + prefix.Length).Count('\n');
        for (int i = 0; i < instructions.Count; i++)
            if (first + i > check.Lines.Count || !check.Line(first + i).Tokens.SequenceEqual(instructions[i])) throw new InvalidDataException("An inserted instruction would not read back as intended.");
        return result;
    }

    /// <summary>The text with an instruction line turned into a comment, which keeps it readable and lets it be restored by hand.</summary>
    public string CommentOut(int number)
    {
        var line = Line(number);
        if (!line.IsInstruction) throw new InvalidDataException($"Line {number} holds no instruction.");
        return Text[..line.Start] + "# " + Text[line.Start..];
    }

    /// <summary>CZInterp::TokenizeLine (see <see cref="GameGenScriptText.TokenizeLine(string)"/>), with each token's place in the text.</summary>
    private static (IReadOnlyList<string>, IReadOnlyList<TextSpan>) Tokenize(string text, int start, int length)
    {
        // Blank and comment lines share empty lists.
        if (GameGenScriptText.TokenizeLine(text, start, start + length, null, null) == 0) return ([], []);
        List<string> tokens = []; List<TextSpan> spans = [];
        GameGenScriptText.TokenizeLine(text, start, start + length, tokens, spans);
        return (tokens, spans);
    }
}
