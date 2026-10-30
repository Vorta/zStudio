using System.Globalization;
using System.Text;
using zStudio.Core.Animation;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public static partial class SourceAnimationEdits
{
    public sealed record SourceExcerpt(string File, int FirstLine, string Text, bool Truncated);
    public sealed record SourceContext(string Record, SourceExcerpt Definition, SourceExcerpt? Script, string? Note);

    /// <summary>Read-only source context from the same snapshot/resolver as compilation, bounded before copying text.</summary>
    public static SourceContext ReadContext(IProjectFiles files, string root, int definitionIndex, string recordId,
        double? scriptTime = null, CancellationToken token = default, string? boundScript = null)
    {
        var set = AnimationDefinitionSet.Load(files, root, token);
        if (definitionIndex < 0 || definitionIndex >= set.Definitions.Count) throw new InvalidDataException("The definition is unavailable.");
        var definition = set.Definitions[definitionIndex];
        var inspection = Inspect(definition.File, definition.Ordinal, definitionIndex,
            files.Read(definition.File, token, ProjectReadLimits.Resource(SourceProject.MaximumSourceTextBytes)), token);
        var record = inspection.Description.Records.FirstOrDefault(r => r.Id == recordId)
            ?? throw new InvalidDataException("The source record is unavailable.");
        var field = record.Fields.FirstOrDefault(f => inspection.Locations.ContainsKey(f.Id))
            ?? throw new InvalidDataException("This record has no source context.");
        var location = inspection.Locations[field.Id];
        var span = inspection.Syntax.SpanOf(location.Parent.Id);
        var excerpt = Excerpt(definition.File, inspection.Syntax.Text, span.Start, span.End, token);
        SourceExcerpt? script = null; string? note = null;
        string? name = record.Fields.FirstOrDefault(f => f.Key == "SCRIPT_FILENAME")?.Values.FirstOrDefault();
        if (name != null && boundScript != null) name = boundScript;
        if (name != null)
        {
            if (name.Contains('*')) note = "The script name is binding-dependent; definition context is shown.";
            else if (set.ReadScript(name, definition.File) is { } file)
            {
                if (file.Bytes.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException("Script context exceeds the source text allowance.");
                string text = Encoding.Latin1.GetString(file.Bytes); int start = 0;
                if (scriptTime is double time && double.IsFinite(time))
                {
                    string? rateText = record.Fields.FirstOrDefault(f => f.Key == "SCRIPT_FRAME_RATE")?.Values.FirstOrDefault();
                    if (double.TryParse(rateText ?? "30", NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) && rate > 0)
                    {
                        double frame = Math.Round(time * rate) + 1;
                        // Context navigation only; never interprets or recompiles channel values.
                        string label = "Frame: " + frame.ToString("0", CultureInfo.InvariantCulture);
                        int at = 0;
                        while (at < text.Length)
                        {
                            token.ThrowIfCancellationRequested(); int end = text.IndexOf('\n', at); if (end < 0) end = text.Length;
                            if (text.AsSpan(at, end - at).Trim().SequenceEqual(label)) { start = at; break; }
                            at = end + 1;
                        }
                        if (start == 0) note = "Script context starts at the file header; no exact source frame was located.";
                    }
                }
                script = Excerpt(file.Path, text, start, text.Length, token);
            }
            else note = "The referenced script is unavailable.";
        }
        return new(recordId, excerpt, script, note);
    }

    private static SourceExcerpt Excerpt(string file, string text, int start, int end, CancellationToken token)
    {
        int line = 1;
        for (int i = 0; i < start; i++) { if ((i & 4095) == 0) token.ThrowIfCancellationRequested(); if (text[i] == '\n') line++; }
        int stop = Math.Min(end, start + 8192), lines = 0, cursor = start;
        for (; cursor < stop; cursor++) if (text[cursor] == '\n' && ++lines == 80) { cursor++; break; }
        return new(file, line, text.Substring(start, cursor - start), cursor < end);
    }
}
