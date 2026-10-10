using System.Text;

namespace Recoil.Zbd.Core.Formats;

/// <summary>Bounded presentation and explicit complete export of stored script tokens.</summary>
internal static class PreparedScriptText
{
    internal const int PreviewCharacters = 65_536;
    internal static (string Text, bool Truncated) Format(IReadOnlyList<string[]> instructions, int maximumCharacters, CancellationToken token)
    {
        StringBuilder text = new(Math.Min(maximumCharacters, 4096));
        bool Add(char c)
        {
            if (text.Length == maximumCharacters) return false;
            text.Append(c); return true;
        }
        foreach (var words in instructions)
        {
            token.ThrowIfCancellationRequested();
            for (int i = 0; i < words.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (i > 0 && !Add(' ')) return (text.ToString(), true);
                if (text.Length == maximumCharacters) return (text.ToString(), true);
                string word = words[i]; bool quoted = word.Length == 0;
                for (int n = 0; !quoted && n < word.Length; n++)
                {
                    if ((n & 4095) == 0) token.ThrowIfCancellationRequested();
                    quoted = char.IsWhiteSpace(word[n]) || word[n] == '"';
                }
                if (quoted && !Add('"')) return (text.ToString(), true);
                for (int n = 0; n < word.Length; n++)
                {
                    if ((n & 4095) == 0) token.ThrowIfCancellationRequested();
                    char c = word[n];
                    if (quoted && (c is '\\' or '"') && !Add('\\')) return (text.ToString(), true);
                    if (!Add(c)) return (text.ToString(), true);
                }
                if (quoted && !Add('"')) return (text.ToString(), true);
            }
            if (!Add('\n')) return (text.ToString(), true);
        }
        // Retain the original empty-script text convention.
        if (instructions.Count == 0 && !Add('\n')) return (text.ToString(), true);
        return (text.ToString(), false);
    }
}
