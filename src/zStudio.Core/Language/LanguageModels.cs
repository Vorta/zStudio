using zStudio.Core.Formats;
using zStudio.Core.Sources;

namespace zStudio.Core.Language;

public sealed record LanguageDiagnostic(string Code, string Severity, string Message, TextSpan Span, string? Mission = null);
public sealed record LanguageToken(TextSpan Span, string Text, string Kind);
public sealed record LanguageSymbol(string Name, string Kind, TextSpan Span, TextSpan Selection, IReadOnlyList<LanguageSymbol> Children);
public sealed record LanguageCompletion(string Label, string Detail, string InsertText, string Kind = "keyword", string? Documentation = null, TextSpan? ReplacementSpan = null);
public sealed record LanguageLocation(string Path, TextSpan Span, string? Context = null);
public sealed record LanguageHover(string Markdown, TextSpan Span);
public sealed record LanguageField(string Name, string Description, string Shape = "", string[]? Choices = null, string? Reference = null,
    string? Arguments = null, string? Notes = null, string? Example = null, bool Suggested = true,
    string? UsageWarning = null, bool ValidateValues = true);

/// <summary>One immutable editor buffer. Offsets are UTF-16, matching LSP's negotiated default.</summary>
public sealed class LanguageDocument
{
    public string Path { get; }
    public string Text { get; }
    public string Family { get; internal set; } = "resource";
    public bool Complete { get; internal set; }
    public IReadOnlyList<LanguageDiagnostic> Diagnostics => diagnostics;
    public IReadOnlyList<LanguageToken> Tokens => tokens;
    public IReadOnlyList<LanguageSymbol> Symbols { get; internal set; } = [];
    internal List<LanguageDiagnostic> diagnostics = [];
    internal List<LanguageToken> tokens = [];
    internal Dictionary<Guid, TextSpan> spans = [];
    internal ZrdNode? Root;
    internal string? CompletionText;
    internal List<LanguageRecord> Records = [];
    internal LanguageDocument(string path, string text) { Path = path; Text = text; }
    internal TextSpan Span(ZrdNode node)
    {
        var span = spans.GetValueOrDefault(node.Id);
        int start = Math.Min(span.Start, Text.Length);
        return new(start, Math.Min(span.End, Text.Length) - start);
    }
    internal void Report(string code, string severity, string message, TextSpan span, string? mission = null)
    {
        if (diagnostics.Count < RecoilLanguage.MaximumDiagnostics)
            diagnostics.Add(new(code, severity, message.Length > 2048 ? message[..2048] + "…" : message, span, mission));
        else if (diagnostics.Count == RecoilLanguage.MaximumDiagnostics)
            diagnostics.Add(new("RECOIL_LIMIT", "information", "Further diagnostics omitted; analysis is incomplete.", new(0, 0)));
    }
}

internal sealed record LanguageRecord(string Key, ZrdNode KeyNode, ZrdNode? Value, LanguageRecord? Parent)
{
    internal IReadOnlyList<ZrdNode> Values => Value?.Kind == ZrdKind.Array ? Value.Children : Value == null ? [] : [Value];
    internal IEnumerable<ZrdNode> Scalars => Values.Where(n => n.Kind != ZrdKind.Array);
    internal string? FirstText => Scalars.FirstOrDefault() is { Kind: ZrdKind.String } n ? n.Text : null;
}
