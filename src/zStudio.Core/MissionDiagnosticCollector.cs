namespace zStudio.Core;

/// <summary>Typed mission-preview context; text diagnostics remain a projection for existing source algorithms.</summary>
internal sealed class MissionDiagnosticCollector
{
    internal const int MaximumDetails = 16 * 1024, MaximumSites = 64;
    private readonly Dictionary<string, Diagnostic> items = new(StringComparer.Ordinal);
    private int retained;
    public void Add(Diagnostic diagnostic)
    {
        string message = JsonData.ShownText(diagnostic.Message, 1024);
        string? details = diagnostic.Details;
        if (details?.Length > MaximumDetails) details = details[..MaximumDetails] + "…";
        int cost = message.Length + (details?.Length ?? 0);
        if (items.TryGetValue(message, out var previous))
        {
            items[message] = previous with { Count = previous.Count + diagnostic.Count };
            return;
        }
        if (items.Count >= 1024 || cost > 256 * 1024 - retained) return;
        retained += cost;
        items.Add(message, diagnostic with { Message = message, Details = details });
    }
    public IReadOnlyList<Diagnostic> Project(IEnumerable<string> messages) => messages.Distinct(StringComparer.Ordinal)
        .Select(message => items.GetValueOrDefault(message) ?? new Diagnostic("Warning", message)).ToArray();
}
