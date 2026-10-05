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
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException($"Source text larger than {SourceProject.MaximumSourceTextBytes / (1024 * 1024)} MiB is not supported.");
        return Encoding.Latin1.GetString(bytes);
    }
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
            string[] copy = [.. tokens];
            if (copy.Length > 1 && copy[0] == "LoadGameGen" && !copy[1].Contains('%')) copy[1] = Model(copy[1]);
            else if (copy.Length > 2 && Worlds.ScriptConditions.IsSet(copy[0]) && modelMacros.Contains(copy[1])) copy[2] = Model(copy[2]);
            else if (TextureCommands.Contains(copy[0])) for (int i = 1; i < copy.Length; i++) copy[i] = Image(copy[i]);
            result.Add(copy);
        }
        return result;
    }
}
