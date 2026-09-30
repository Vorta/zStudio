using System.Text;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Text form of prepared gamegen scripts (<c>.gs</c>/<c>.gw</c>). Tokenization follows the engine's
/// CZInterp::TokenizeLine (retail 0x4C13C0): <c>#</c> removes the rest of a line, tokens are separated by
/// comma, space, tab or newline, and C-locale whitespace after a separator is skipped (token bytes are
/// sign-extended, so only ASCII whitespace qualifies). Only a separator directly after another creates an empty token.
/// </summary>
public static class GameGenScriptText
{
    private static readonly char[] Separators = [',', ' ', '\t', '\n'];
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    /// <summary>Instructions of a script file read in text mode (CRLF becomes LF); blank and comment lines are skipped.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Tokenize(string text)
    {
        List<IReadOnlyList<string>> lines = [];
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var tokens = TokenizeLine(raw);
            if (tokens.Count > 0) lines.Add(tokens);
        }
        return lines;
    }

    public static IReadOnlyList<string> TokenizeLine(string line)
    {
        int comment = line.IndexOf('#');
        if (comment >= 0) line = line[..comment];
        int cursor = 0; List<string> tokens = [];
        while (cursor < line.Length && IsSpace(line[cursor])) cursor++;
        while (true)
        {
            int separator = line.IndexOfAny(Separators, cursor);
            if (separator < 0) break;
            tokens.Add(line[cursor..separator]);
            cursor = separator + 1;
            while (cursor < line.Length && IsSpace(line[cursor])) cursor++;
        }
        if (cursor < line.Length) tokens.Add(line[cursor..]);
        return tokens;
    }

    /// <summary>Encode one instruction so that <see cref="TokenizeLine"/> returns exactly <paramref name="tokens"/>, or null if impossible.</summary>
    public static string? WriteLine(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return null;
        StringBuilder text = new();
        for (int i = 0; i < tokens.Count; i++)
        {
            // Commas join empty tokens to their neighbors; whitespace would be skipped.
            if (i > 0) text.Append(tokens[i].Length == 0 || tokens[i - 1].Length == 0 ? ',' : ' ');
            text.Append(tokens[i]);
        }
        if (tokens[^1].Length == 0) text.Append(',');
        string line = text.ToString();
        return line.Any(c => c > 255 || c == '\0') || !TokenizeLine(line).SequenceEqual(tokens) ? null : line;
    }

    public static string Write(IEnumerable<IReadOnlyList<string>> instructions)
    {
        StringBuilder text = new();
        foreach (var tokens in instructions)
            text.Append(WriteLine(tokens) ?? throw new InvalidDataException("A script instruction cannot be represented as text.")).Append('\n');
        return text.ToString();
    }
}
