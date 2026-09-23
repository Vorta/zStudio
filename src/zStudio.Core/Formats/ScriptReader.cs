using System.Text;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Formats;

internal sealed class ScriptReader : IZbdFormatReader
{
    public FormatFamily Family => FormatFamily.Scripts;
    public void Read(ZbdDocument doc, CancellationToken token)
    {
        BinaryCursor c = new(doc.Bytes); c.Skip(8); int count = c.Count(c.U32(), 128);
        List<(string Name, uint Time, uint Offset)> entries = [];
        for (int i = 0; i < count; i++) entries.Add((c.String(120), c.U32(), c.U32()));
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested(); var e = entries[i]; long end = i + 1 < count ? entries[i + 1].Offset : doc.Bytes.Length;
            try
            {
                if (e.Offset < 12L + count * 128L) throw new InvalidDataException("Script overlaps the index.");
                var bytes = doc.Slice(e.Offset, end - e.Offset);
                ScriptContent script = Decode(bytes, token);
                var a = doc.Add(AssetKind.Script, i, e.Name, e.Offset, end - e.Offset, new JsonObject { ["file_time"] = (long)e.Time }, script);
                a.Summary = $"{script.Instructions.Count:N0} instructions";
            }
            catch (InvalidDataException ex) { doc.Diagnostics.Add(new("Error", $"Script {e.Name}: {ex.Message}", i, e.Offset)); }
        }
    }
    public static ScriptContent Decode(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        BinaryCursor c = new(bytes); List<string[]> instructions = []; bool terminated = false;
        while (c.Remaining > 0)
        {
            token.ThrowIfCancellationRequested(); uint size = c.U32(); if (size == 0) { terminated = true; break; }
            uint count = c.U32(); if (count > size) throw new InvalidDataException("Script argument count exceeds its string block.");
            var strings = c.Take(c.Count(size)); int pos = 0; string[] args = new string[count];
            for (int i = 0; i < count; i++)
            {
                int nul = strings.Span[pos..].IndexOf((byte)0); if (nul < 0) throw new InvalidDataException("Unterminated script argument.");
                args[i] = Encoding.Latin1.GetString(strings.Span.Slice(pos, nul)); pos += nul + 1;
            }
            instructions.Add(args);
        }
        if (!terminated) throw new InvalidDataException("Missing script terminator.");
        string text = string.Join('\n', instructions.Select(a => string.Join(' ', a.Select(Quote)))) + "\n";
        return new(instructions, text);
    }
    private static string Quote(string s) => s.Length == 0 || s.Any(char.IsWhiteSpace) || s.Contains('"')
        ? "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : s;
}
