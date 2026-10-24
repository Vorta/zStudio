using System.Globalization;
using zStudio.Core.Formats;
using zStudio.Core.Sources;

namespace zStudio.Core.Language;

/// <summary>Read-only language features over the same parser used by source builds. No editor or renderer dependency.</summary>
public static partial class RecoilLanguage
{
    public const int MaximumDiagnostics = 200, MaximumResults = 200, MaximumTokens = 250_000;
    public static LanguageDocument Analyze(string path, string text, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new LanguageDocument(path, text);
        if (text.Length > SourceProject.MaximumSourceTextBytes)
        { result.Report("RECOIL_LIMIT", "information", "Document exceeds the 16 MiB analysis limit.", new(0, 0)); return result; }
        if (text.Contains('\0'))
        { result.Report("RECOIL_BINARY", "information", "This appears to be compiled binary data. GameZ Script analyzes text source files.", new(0, 0)); return result; }
        int errorAt = 0; string? errorText = null;
        try
        {
            result.Root = ZrdText.Parse(text, token, (node, span) =>
            {
                if (result.spans.Count >= MaximumTokens) throw new InvalidDataException("Editor token limit exceeded; analysis is incomplete.");
                result.spans.Add(node.Id, span);
                if (node.Kind != ZrdKind.Array) result.tokens.Add(new(span, node.Kind == ZrdKind.String ? node.Text : node.Value, node.Kind.ToString().ToLowerInvariant()));
            }, issue: (at, message) => { errorAt = at; errorText = message; },
            delimiter: (kind, at) =>
            {
                if (result.tokens.Count >= MaximumTokens) throw new InvalidDataException("Editor token limit exceeded; analysis is incomplete.");
                result.tokens.Add(new(new(at, 1), kind.ToString(), "delimiter"));
            });
            result.Complete = true;
        }
        catch (InvalidDataException ex)
        {
            int start = Math.Clamp(errorAt, 0, text.Length);
            result.Report(errorText == null ? "RECOIL_LIMIT" : "RECOIL_SYNTAX", errorText == null ? "information" : "error",
                errorText ?? ex.Message, new(start, start < text.Length ? 1 : 0));
        }
        result.tokens.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
        // Recover only unfinished closing delimiters for editor context. The original syntax error
        // remains, and the compiler/deep validator still receives the original source bytes.
        if (result.Root == null && errorText == "Missing ')'.")
        {
            int depth = result.tokens.Where(t => t.Kind == "delimiter").Sum(t => t.Text == "(" ? 1 : -1);
            if (depth > 0 && text.Length + depth + 1 <= SourceProject.MaximumSourceTextBytes)
            {
                string completed = text + "\n" + new string(')', depth);
                try
                {
                    result.Root = ZrdText.Parse(completed, token, (node, span) => result.spans[node.Id] = span);
                    result.CompletionText = completed;
                }
                catch (InvalidDataException) { }
            }
        }
        result.Family = PartialFamily(result.tokens);
        int unsupported = text.AsSpan().IndexOfAnyExceptInRange('\0', '\xFF');
        if (unsupported >= 0) result.Report("RECOIL_ENCODING", "error", "Source files use Latin-1. This character cannot be saved in the source encoding.", new(unsupported, 1));
        if (result.Root != null)
        {
            var root = result.Root;
            while (root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array) root = root.Children[0];
            Collect(result, root, null, token);
            result.Family = Classify(result);
            if (result.Complete) Validate(result, token);
            int remaining = MaximumResults;
            result.Symbols = Symbols(result, null, ref remaining);
        }
        return result;
    }

    private static void Collect(LanguageDocument doc, ZrdNode array, LanguageRecord? parent, CancellationToken token)
    {
        var children = array.Children;
        for (int i = 0; i < children.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var key = children[i];
            if (key.Kind == ZrdKind.Array) { Collect(doc, key, parent, token); continue; }
            if (key.Kind != ZrdKind.String) continue;
            var value = i + 1 < children.Count ? children[i + 1] : null;
            // Animation flags and positional resource strings are not forced into key/value pairs.
            bool paired = value?.Kind == ZrdKind.Array || (parent?.Key.StartsWith("MISSION", StringComparison.Ordinal) == true && value?.Kind != ZrdKind.Array)
                || key.Text is "HIGH" or "MED" or "LOW" && Ancestors(parent).Contains("SETS") && value?.Kind == ZrdKind.String;
            var record = new LanguageRecord(key.Text, key, paired ? value : null, parent);
            doc.Records.Add(record);
            if (paired && value != null)
            { i++; if (value.Kind == ZrdKind.Array) Collect(doc, value, record, token); }
        }
    }

    private static IReadOnlyList<LanguageSymbol> Symbols(LanguageDocument doc, LanguageRecord? parent, ref int remaining)
    {
        List<LanguageSymbol> result = [];
        foreach (var record in doc.Records.Where(r => ReferenceEquals(r.Parent, parent)))
        {
            if (remaining-- <= 0) break;
            if (record.Value == null) continue;
            string name = record.Key;
            if (record.Key is "ANIMATION_DEFINITION" or "SEQUENCE_DEFINITION")
                name += " · " + (doc.Records.FirstOrDefault(r => ReferenceEquals(r.Parent, record) && r.Key == (record.Key == "ANIMATION_DEFINITION" ? "ANIMATION_NAME" : "NAME"))?.FirstText ?? "unnamed");
            var key = doc.Span(record.KeyNode); var end = doc.Span(record.Value).End;
            result.Add(new(Shown(name), record.Key.Contains("DEFINITION", StringComparison.Ordinal) ? "class" : "property",
                new(key.Start, Math.Max(key.Length, end - key.Start)), key, Symbols(doc, record, ref remaining)));
        }
        return result;
    }
    internal static string Shown(string text) => text.Length > 256 ? text[..256] + "…" : text;
    private static string PartialFamily(IReadOnlyList<LanguageToken> tokens)
    {
        int depth = 0;
        foreach (var item in tokens)
        {
            if (item.Kind == "delimiter") { depth += item.Text == "(" ? 1 : -1; continue; }
            if (depth > 1) continue;
            string? family = item.Text switch { "ANIMATION_DEFINITIONS" => "animation", "BALLISTICS" => "weapons", "PICKUP_DATA" => "pickups", "EFFECTS" => "effects", "SOUND_PATH" => "sounds", "camera_zone" => "player", "NEW_GAME_START" => "startanims", "READ_TIME" => "objectives", _ => null };
            if (family != null) return family;
        }
        return "resource";
    }

    public static IReadOnlyList<LanguageCompletion> Complete(LanguageDocument document, int offset)
    {
        offset = Math.Clamp(offset, 0, document.Text.Length);
        if (ScalarOperand(document, offset) is { } scalarRecord)
        {
            var scalarField = Field(document.Family, scalarRecord.Key, scalarRecord.Parent?.Key ?? "", Ancestors(scalarRecord.Parent));
            return scalarField?.Choices?.Select(c => new LanguageCompletion(c, scalarField.Description, c, "enumMember", FieldDocumentation(scalarField))).ToArray() ?? [];
        }
        var context = Context(document, offset).Where(p => p.Length > 0).ToArray();
        string parent = context.Length > 1 ? context[^2] : "";
        var field = Field(document.Family, context.LastOrDefault() ?? "", parent, context[..Math.Max(0, context.Length - 1)]);
        int argument = ArgumentIndex(document, offset);
        if (document.Family == "animation" && field?.Name == "VALUE" && parent == "CALLBACK" && argument == 0)
            return CallbackIds.Select(pair => new LanguageCompletion(pair.Key.ToString(CultureInfo.InvariantCulture), pair.Value, pair.Key.ToString(CultureInfo.InvariantCulture), "enumMember", CallbackHelp(pair.Key))).ToArray();
        if (field?.Choices is { Length: > 0 } choices)
            return argument == 0 ? choices.Select(c => new LanguageCompletion(c, field.Description, c, "enumMember", FieldDocumentation(field))).ToArray() : [];
        if (document.Family == "animation" && field?.Name is "OPACITY_FROM" or "OPACITY_TO" && argument == 1)
            return new[] { "ON", "OFF" }.Select(c => new LanguageCompletion(c, "Alpha override at this endpoint", c, "enumMember", FieldDocumentation(field))).ToArray();
        return Fields(document.Family, context.LastOrDefault() ?? "", context).Where(f => f.Suggested).Take(MaximumResults)
            .Select(f => new LanguageCompletion(f.Name, f.Description, f.Name, Documentation: FieldDocumentation(f))).ToArray();
    }

    public static LanguageHover? Hover(LanguageDocument document, int offset)
    {
        var item = document.Tokens.FirstOrDefault(t => t.Span.Start <= offset && t.Span.End > offset);
        if (item == null) return null;
        var record = document.Records.FirstOrDefault(r => document.Span(r.KeyNode) == item.Span);
        var context = Context(document, item.Span.Start).Where(p => p.Length > 0).ToArray();
        var field = Field(document.Family, item.Text, record?.Parent?.Key ?? context.LastOrDefault() ?? "", record == null ? context : Ancestors(record.Parent));
        if (field != null) return new(FieldDocumentation(field), item.Span);
        if (ScalarOperand(document, item.Span.Start) is { } scalarRecord &&
            Field(document.Family, scalarRecord.Key, scalarRecord.Parent?.Key ?? "", Ancestors(scalarRecord.Parent)) is { } scalarField)
            return new(FieldDocumentation(scalarField), item.Span);
        if (document.Family == "animation" && context.Length >= 2 && context[^1] == "VALUE" && context[^2] == "CALLBACK" && int.TryParse(item.Text, out int callback))
            return new(CallbackHelp(callback), item.Span);
        if (context.Length > 0)
        {
            var owner = Field(document.Family, context[^1], context.Length > 1 ? context[^2] : "", context[..^1]);
            if (owner?.Choices?.Contains(item.Text, StringComparer.Ordinal) == true) return new(FieldDocumentation(owner), item.Span);
        }
        return null;
    }
    private static bool IsWeatherScalar(string key) => key is "TYPE" or "PARTICLES" or "WIND_DIR" or "WIND_VEL" or "GRAVITY";
    private static LanguageRecord? ScalarOperand(LanguageDocument document, int offset)
    {
        if (document.Family != "weather") return null;
        return document.Records.FirstOrDefault(r => IsWeatherScalar(r.Key) && r.Parent?.Key.StartsWith("MISSION", StringComparison.Ordinal) == true &&
            (r.Value is { Kind: not ZrdKind.Array } value && document.Span(r.KeyNode).End < offset && offset <= document.Span(value).End ||
             r.Value == null && document.Span(r.KeyNode).End < offset &&
             !document.Tokens.Any(t => t.Span.Start >= document.Span(r.KeyNode).End && t.Span.Start < offset)));
    }
    // Delimiters and scalar tokens come from the strict parser even when a final ')' is still being typed.
    internal static List<string> Context(LanguageDocument doc, int offset)
    {
        List<string> stack = []; string previous = "";
        foreach (var token in doc.Tokens)
        {
            if (token.Span.Start >= offset) break;
            if (token.Kind == "delimiter")
            {
                if (token.Text == "(") stack.Add(previous);
                else if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                previous = "";
            }
            else previous = token.Kind == "string" ? token.Text : "";
        }
        return stack;
    }
}
