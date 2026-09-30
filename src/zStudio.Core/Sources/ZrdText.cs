using System.Globalization;
using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Lossless text form of a zReader resource, used for reconstructed source files. The original compiler's
/// text syntax did not survive, so this syntax is zStudio's; it round-trips every compiled value exactly.
/// </summary>
/// <remarks>
/// A file lists the children of its root array. <c>( … )</c> is an array. Integers are decimal. Floats always
/// contain a decimal point or exponent, or use <c>f32:XXXXXXXX</c> raw bits for NaN payloads and infinities.
/// Strings are bare identifiers when unambiguous, otherwise double-quoted with <c>\\</c>, <c>\"</c> and
/// <c>\xNN</c> escapes. <c>#</c> starts a comment outside strings. Files are written as ASCII and read as Latin-1.
/// </remarks>
public static class ZrdText
{
    public const string Extension = ".zrd";
    private const int MaximumDepth = 128, MaximumNodes = 2_000_000;
    /// <summary>Raw float bits; a colon never appears in an unquoted string.</summary>
    public const string RawFloatPrefix = "f32:";

    public static string Write(ZrdNode root, CancellationToken token = default)
    {
        if (root.Kind != ZrdKind.Array) throw new InvalidDataException("A zReader source file stores a root array.");
        StringBuilder text = new(); int budget = MaximumNodes;
        Children(root.Children, 0);
        return text.ToString();
        // A string followed by an array is written as "KEY ( … )" on one line; whitespace never affects the data.
        void Children(IReadOnlyList<ZrdNode> children, int depth)
        {
            for (int i = 0; i < children.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (depth > MaximumDepth || (budget -= 1) < 0) throw new InvalidDataException("ZRD nesting or node limit exceeded.");
                text.Append(' ', depth * 2);
                var child = children[i];
                if (child.Kind == ZrdKind.String && i + 1 < children.Count && children[i + 1].Kind == ZrdKind.Array)
                {
                    text.Append(Scalar(child)).Append(' '); child = children[++i];
                    if (--budget < 0) throw new InvalidDataException("ZRD node limit exceeded.");
                }
                if (child.Kind != ZrdKind.Array) { text.Append(Scalar(child)).Append('\n'); continue; }
                if (Short(child))
                {
                    text.Append('(');
                    foreach (var item in child.Children) text.Append(' ').Append(Scalar(item));
                    text.Append(child.Children.Count == 0 ? ")\n" : " )\n");
                    continue;
                }
                text.Append("(\n");
                Children(child.Children, depth + 1);
                text.Append(' ', depth * 2).Append(")\n");
            }
        }
        static bool Short(ZrdNode array) => array.Children.All(c => c.Kind != ZrdKind.Array) &&
            (array.Children.Count <= 1 || array.Children.Count <= 8 && array.Children.Sum(c => c.Kind == ZrdKind.String ? c.Text.Length + 3 : 12) <= 100);
    }

    public static string Scalar(ZrdNode node) => node.Kind switch
    {
        ZrdKind.Int => unchecked((int)node.Bits).ToString(CultureInfo.InvariantCulture),
        ZrdKind.Float => Float(node.Bits),
        ZrdKind.String => String(node.Text),
        _ => throw new InvalidDataException("Arrays are not scalars.")
    };

    private static string Float(uint bits)
    {
        float value = BitConverter.UInt32BitsToSingle(bits);
        if (!float.IsFinite(value)) return RawFloatPrefix + bits.ToString("X8", CultureInfo.InvariantCulture);
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('.') && !text.Contains('E')) text += ".0";
        // "R" is round-trip exact for finite floats, including -0 and subnormals; verify rather than assume.
        return BitConverter.SingleToUInt32Bits(float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)) == bits ? text : RawFloatPrefix + bits.ToString("X8", CultureInfo.InvariantCulture);
    }

    private static string String(string value)
    {
        if (IsBare(value)) return value;
        StringBuilder text = new("\"");
        foreach (char c in value)
        {
            if (c > 255) throw new InvalidDataException("ZRD strings must be Latin-1.");
            if (c == '"') text.Append("\\\"");
            else if (c == '\\') text.Append("\\\\");
            else if (c is >= ' ' and <= '~') text.Append(c);
            else text.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
        }
        return text.Append('"').ToString();
    }

    /// <summary>Unquoted strings must start like an identifier so they can never read as numbers or syntax.</summary>
    private static bool IsBare(string value)
    {
        if (value.Length == 0 || !(char.IsAsciiLetter(value[0]) || value[0] == '_')) return false;
        foreach (char c in value) if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')) return false;
        return true;
    }

    public static ZrdNode Parse(string text, CancellationToken token = default)
    {
        int position = 0, line = 1, budget = MaximumNodes;
        List<ZrdNode> children = [];
        while (true)
        {
            SkipTrivia();
            if (position == text.Length) break;
            children.Add(ReadNode(0));
        }
        return new(Guid.NewGuid(), ZrdKind.Array, 0, "", children.ToArray());

        ZrdNode ReadNode(int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > MaximumDepth || --budget < 0) throw Error("ZRD nesting or node limit exceeded.");
            char c = text[position];
            if (c == ')') throw Error("Unexpected ')'.");
            if (c == '(')
            {
                position++; List<ZrdNode> items = [];
                while (true)
                {
                    SkipTrivia();
                    if (position == text.Length) throw Error("Missing ')'.");
                    if (text[position] == ')') { position++; break; }
                    items.Add(ReadNode(depth + 1));
                }
                return new(Guid.NewGuid(), ZrdKind.Array, 0, "", items.ToArray());
            }
            if (c == '"') return new(Guid.NewGuid(), ZrdKind.String, 0, ReadQuoted(), []);
            string word = Word(position);
            if (word.Length == 0) throw Error($"Unexpected character '{c}'.");
            position += word.Length;
            if (word.StartsWith(RawFloatPrefix, StringComparison.Ordinal))
            {
                string hex = word[RawFloatPrefix.Length..];
                if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint raw)) throw Error($"Raw float bits need exactly eight hex digits after {RawFloatPrefix}.");
                return new(Guid.NewGuid(), ZrdKind.Float, raw, "", []);
            }
            if (char.IsAsciiLetter(word[0]) || word[0] == '_')
            {
                if (!IsBare(word)) throw Error($"'{word}' must be quoted.");
                return new(Guid.NewGuid(), ZrdKind.String, 0, word, []);
            }
            if (word.Contains('.') || word.Contains('e') || word.Contains('E'))
            {
                if (!float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value)) throw Error($"Invalid float '{word}'.");
                return new(Guid.NewGuid(), ZrdKind.Float, BitConverter.SingleToUInt32Bits(value), "", []);
            }
            if (!int.TryParse(word, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int integer)) throw Error($"Invalid integer '{word}'.");
            return new(Guid.NewGuid(), ZrdKind.Int, unchecked((uint)integer), "", []);
        }
        string ReadQuoted()
        {
            StringBuilder value = new(); position++;
            while (true)
            {
                if (position == text.Length || text[position] == '\n') throw Error("Unterminated string.");
                char c = text[position++];
                if (c == '"') return value.ToString();
                if (c != '\\') { if (c > 255) throw Error("ZRD strings must be Latin-1."); value.Append(c); continue; }
                if (position == text.Length) throw Error("Unterminated escape.");
                char escape = text[position++];
                if (escape is '"' or '\\') value.Append(escape);
                else if (escape == 'x' && position + 2 <= text.Length && byte.TryParse(text.AsSpan(position, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)) { value.Append((char)b); position += 2; }
                else throw Error($"Unknown escape '\\{escape}'.");
            }
        }
        string Word(int start)
        {
            int end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('(' or ')' or '"' or '#')) end++;
            return text[start..end];
        }
        void SkipTrivia()
        {
            while (position < text.Length)
            {
                char c = text[position];
                if (c == '\n') { line++; position++; }
                else if (char.IsWhiteSpace(c)) position++;
                else if (c == '#') { while (position < text.Length && text[position] != '\n') position++; }
                else break;
            }
        }
        InvalidDataException Error(string message) => new($"Line {line}: {message}");
    }

    public static ZrdNode Parse(ReadOnlySpan<byte> bytes, CancellationToken token = default)
    {
        if (bytes.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException($"Source text larger than {SourceProject.MaximumSourceTextBytes / (1024 * 1024)} MiB is not supported.");
        return Parse(Encoding.Latin1.GetString(bytes), token);
    }
    public static byte[] Encode(ZrdNode root, CancellationToken token = default) => Encoding.ASCII.GetBytes(Write(root, token));

    /// <summary>A text source rather than compiled zReader data: compiled files begin with a type word of 1–4.</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> prefix) => prefix.Length < 4 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(prefix) is < 1 or > 4;
}
