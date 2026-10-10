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

    /// <summary>A script file's text, bounded before it is decoded.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes) => Decode(bytes, CancellationToken.None);
    internal static string Decode(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException($"Source text larger than {SourceProject.MaximumSourceTextBytes / (1024 * 1024)} MiB is not supported.");
        string text = Encoding.Latin1.GetString(bytes);
        token.ThrowIfCancellationRequested();
        return text;
    }
    /// <summary>
    /// The most lines a script may have: as many as the instructions a build runs at most
    /// (<see cref="Worlds.WorldAssembler.MaximumInstructions"/>). The retail scripts have at most a few hundred.
    /// </summary>
    public const int MaximumLines = Worlds.WorldAssembler.MaximumInstructions;
    /// <summary>The most tokens a script may have: four for each line it may have (the retail scripts have fewer than three per instruction).</summary>
    public const int MaximumTokens = 4 * MaximumLines;

    /// <summary>
    /// Refuses a script's text with more than <see cref="MaximumLines"/> lines or <see cref="MaximumTokens"/> tokens
    /// before anything is made of it: a 16 MiB file of short lines or empty tokens would otherwise become millions of
    /// line, token and position objects. Counting allocates nothing.
    /// </summary>
    public static void CheckBounds(string text) => CheckBounds(text, CancellationToken.None);
    internal static void CheckBounds(string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int lines = SourceTextScan.Count(text, '\n', text.Length, token) + (text.Length == 0 || text[^1] != '\n' ? 1 : 0);
        if (lines > MaximumLines) throw new InvalidDataException($"The script has {lines:N0} lines; scripts of more than {MaximumLines:N0} lines are not supported.");
        // Each token ends at a separator or at the end of its line, so only longer texts can hold too many.
        if ((long)text.Length + lines <= MaximumTokens) return;
        long tokens = 0;
        foreach (var (start, length) in Lines(text, token))
            if ((tokens += TokenizeLine(text, start, start + length, null, null, token)) > MaximumTokens)
                throw new InvalidDataException($"The script has more than {MaximumTokens:N0} tokens, which is not supported.");
    }

    /// <summary>
    /// The physical lines of a script's text as the engine reads them in text mode: each ends at a newline, a carriage
    /// return before that newline is not part of it, and a final newline does not start another line.
    /// </summary>
    internal static IEnumerable<(int Start, int Length)> Lines(string text, CancellationToken token = default)
    {
        int start = 0;
        while (true)
        {
            int end = SourceTextScan.Find(text, '\n', start, text.Length, token);
            if (end < 0) { if (start < text.Length || start == 0) yield return (start, text.Length - start); yield break; }
            yield return (start, end > start && text[end - 1] == '\r' ? end - 1 - start : end - start);
            start = end + 1;
        }
    }

    /// <summary>Instructions of a script file read in text mode (CRLF becomes LF); blank and comment lines are skipped. Refused past <see cref="CheckBounds"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Tokenize(string text)
        => TokenizeCancellable(text, CancellationToken.None);

    internal static IReadOnlyList<IReadOnlyList<string>> TokenizeCancellable(string text, CancellationToken token)
    {
        CheckBounds(text, token);
        List<IReadOnlyList<string>> lines = [];
        foreach (var (start, length) in Lines(text, token))
        {
            token.ThrowIfCancellationRequested();
            List<string> tokens = [];
            if (TokenizeLine(text, start, start + length, tokens, null, token) > 0) lines.Add(tokens);
        }
        token.ThrowIfCancellationRequested();
        return lines;
    }

    /// <summary>Count source tokens without allocating their strings, for a shared cache budget before tokenization.</summary>
    internal static long CountTokens(string text, CancellationToken token)
    {
        long count = 0;
        foreach (var (start, length) in Lines(text, token))
        {
            token.ThrowIfCancellationRequested();
            count += TokenizeLine(text, start, start + length, null, null, token);
            if (count > MaximumTokens) throw new InvalidDataException($"The script has more than {MaximumTokens:N0} tokens, which is not supported.");
        }
        token.ThrowIfCancellationRequested();
        return count;
    }

    public static IReadOnlyList<string> TokenizeLine(string line) => TokenizeLine(line, CancellationToken.None);
    internal static IReadOnlyList<string> TokenizeLine(string line, CancellationToken token)
    {
        List<string> tokens = [];
        TokenizeLine(line, 0, line.Length, tokens, null, token);
        return tokens;
    }

    /// <summary>
    /// CZInterp::TokenizeLine over the line <paramref name="text"/>[<paramref name="start"/>..<paramref name="end"/>):
    /// adds its tokens (and where each lies in the text) to the lists given, and returns how many there are.
    /// </summary>
    internal static int TokenizeLine(string text, int start, int end, List<string>? tokens, List<TextSpan>? spans, CancellationToken token = default, Action? scanCheckpoint = null)
    {
        token.ThrowIfCancellationRequested();
        int comment = SourceTextScan.Find(text, '#', start, end, token, scanCheckpoint);
        if (comment >= 0) end = comment;
        int count = 0, cursor = start;
        while (cursor < end && IsSpace(text[cursor])) { if ((cursor & 4095) == 0) token.ThrowIfCancellationRequested(); cursor++; }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int separator = SourceTextScan.FindAny(text, Separators, cursor, end, token, scanCheckpoint);
            if (separator < 0) break;
            Add(cursor, separator);
            cursor = separator + 1;
            while (cursor < end && IsSpace(text[cursor])) { if ((cursor & 4095) == 0) token.ThrowIfCancellationRequested(); cursor++; }
        }
        if (cursor < end) Add(cursor, end);
        token.ThrowIfCancellationRequested();
        return count;
        void Add(int from, int to) { count++; tokens?.Add(text[from..to]); spans?.Add(new(from, to - from)); }
    }

    /// <summary>Encode one instruction so that <see cref="TokenizeLine"/> returns exactly <paramref name="tokens"/>, or null if impossible.</summary>
    public static string? WriteLine(IReadOnlyList<string> tokens) => WriteLine(tokens, CancellationToken.None);
    internal static string? WriteLine(IReadOnlyList<string> tokens, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (tokens.Count == 0) return null;
        StringBuilder text = new();
        for (int i = 0; i < tokens.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            // Commas join empty tokens to their neighbors; whitespace would be skipped.
            if (i > 0) text.Append(tokens[i].Length == 0 || tokens[i - 1].Length == 0 ? ',' : ' ');
            text.Append(tokens[i]);
        }
        if (tokens[^1].Length == 0) text.Append(',');
        string line = text.ToString();
        for (int i = 0; i < line.Length; i++) { if ((i & 4095) == 0) token.ThrowIfCancellationRequested(); if (line[i] > 255 || line[i] == '\0') return null; }
        var parsed = TokenizeLine(line, token);
        bool equal = TokensEqual(parsed, tokens, token);
        token.ThrowIfCancellationRequested();
        return equal ? line : null;
    }

    internal static bool TokensEqual(IReadOnlyList<string> left, IReadOnlyList<string> right, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        }
        token.ThrowIfCancellationRequested();
        return true;
    }

    public static string Write(IEnumerable<IReadOnlyList<string>> instructions)
    {
        StringBuilder text = new();
        foreach (var tokens in instructions)
            text.Append(WriteLine(tokens) ?? throw new InvalidDataException("A script instruction cannot be represented as text.")).Append('\n');
        return text.ToString();
    }

    /// <summary>Commands whose arguments name texture image files (the shipped scripts name <c>.tif</c> files).</summary>
    private static readonly HashSet<string> TextureCommands = new(["CycleTextureSetMap", "WriteTextureSetMap", "LensFlareTexture", "TextureAdd"], StringComparer.Ordinal);
    private static readonly HashSet<string> ImageExtensions = new([".tif", ".tiff", ".tga", ".bmp", ".rgb", ".rgba", ".sgi", ".int", ".inta", ".pcx", ".jpg", ".jpeg", ".gif"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The macros scripts use as the model file of <c>LoadGameGen</c> (the shipped <c>dbName</c>).</summary>
    public static IReadOnlySet<string> ModelMacros(IEnumerable<IReadOnlyList<IReadOnlyList<string>>> scripts)
    {
        HashSet<string> macros = new(StringComparer.Ordinal);
        foreach (var tokens in scripts.SelectMany(s => s))
            if (tokens.Count > 1 && tokens[0] == "LoadGameGen" && tokens[1].Length > 2 && tokens[1][0] == '%' && tokens[1][^1] == '%') macros.Add(tokens[1][1..^1]);
        return macros;
    }

    /// <summary>
    /// A script as a source project spells its files: models are glTF and textures PNG, so the model file of
    /// <c>LoadGameGen</c> (and a macro set to one, such as <c>dbName</c>) names a <c>.gltf</c> file where the shipped
    /// scripts name OpenFlight <c>.flt</c> files, and the texture commands name <c>.png</c> files. Node names keep their
    /// spelling, also where they end in <c>.flt</c> (LoadGameGen's node name, FindNode, AddChild): the world and its
    /// resources use them. A macro the scripts also use as a node name (<c>LoadGameGen %dbName% %dbName%</c>) renames that
    /// node with it (the database root becomes <c>m1.gltf</c>); every use changes alike and the build deletes the node. The build finds a model by its name with either extension, and the game finds a texture by
    /// its name without the extension (zImage::TexDirFindOrAppendByPath, retail 0x46D810), so nothing else changes.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ProjectFileNames(IReadOnlyList<IReadOnlyList<string>> instructions, IReadOnlySet<string> modelMacros)
    {
        static string Model(string file) => file.EndsWith(".flt", StringComparison.OrdinalIgnoreCase) ? file[..^4] + ".gltf" : file;
        static string Image(string file) => ImageExtensions.Contains(Path.GetExtension(file)) ? file[..^Path.GetExtension(file).Length] + ".png" : file;
        List<IReadOnlyList<string>> result = new(instructions.Count);
        foreach (var tokens in instructions)
        {
            // A prepared record may retain a nonempty raw block without any tokens. It stays lossless in the
            // binary package, but has no command to normalize or represent as a source instruction.
            if (tokens.Count == 0) throw new InvalidDataException("A script instruction without a command cannot be represented as text.");
            string[] copy = [.. tokens];
            if (copy.Length > 1 && copy[0] == "LoadGameGen" && !copy[1].Contains('%')) copy[1] = Model(copy[1]);
            else if (copy.Length > 2 && Worlds.ScriptConditions.IsSet(copy[0]) && modelMacros.Contains(copy[1])) copy[2] = Model(copy[2]);
            else if (TextureCommands.Contains(Worlds.ScriptCommands.Core(copy[0]))) for (int i = 1; i < copy.Length; i++) copy[i] = Image(copy[i]);
            result.Add(copy);
        }
        return result;
    }
}
