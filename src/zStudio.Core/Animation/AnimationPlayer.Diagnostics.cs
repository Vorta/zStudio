namespace zStudio.Core.Animation;

public sealed record AnimationDiagnosticContext(int Entry, long? Instance = null, Guid? Sequence = null, Guid? Event = null);
public sealed record AnimationPreviewDiagnostic(string Severity, string Category, string Message, AnimationDiagnosticContext Source, long Count, double FirstTime, double LastTime);

public sealed partial class AnimationPlayer
{
    private readonly record struct DiagnosticKey(string Severity, string Category, string Message, AnimationDiagnosticContext Source);
    private Dictionary<DiagnosticKey, AnimationPreviewDiagnostic> previewIssues = [];
    private AnimationDiagnosticContext diagnosticContext = new(-1);
    /// <summary>The latest warning, which a refused mode change names.</summary>
    private string? lastWarning;
    /// <summary>The most distinct diagnostics a run keeps; later distinct ones are counted in <see cref="OmittedDiagnostics"/>'s group.</summary>
    public const int MaximumDiagnosticGroups = 2000;
    /// <summary>The message of the group that counts the distinct diagnostics left out after <see cref="MaximumDiagnosticGroups"/> (entry −1).</summary>
    public const string OmittedDiagnostics = "Additional distinct preview diagnostics were omitted after the 2,000-group limit.";
    /// <summary>
    /// A note about how the preview runs a kind of event at all ("Approximate: …", nested activation order, collision surfaces):
    /// one group per entry and message, whichever instance, sequence or event raised it. The cards fold them into one line.
    /// </summary>
    public static bool IsGenericNote(string category, string severity, string message) =>
        category == "Support" && severity == "Information" && GenericNotes.Any(prefix => message.StartsWith(prefix, StringComparison.Ordinal));
    private static readonly string[] GenericNotes =
    [
        "Approximate:", "Engine-based", "Nested animation activation uses", "Preview collision", "Flat-ground preview collision", "Ground collision is disabled",
        "Inherited gameplay launch velocity", "Beam activation points use", "Animation checkpoint storage", "Additional distinct preview diagnostics",
    ];
    private void AddNote(string message, string category = "Preview", string severity = "Warning")
    {
        notes.Add(message); // Preserve the existing text API and duration-analysis contract.
        if (severity == "Warning") lastWarning = message;
        // Groups are kept per entry, sequence and event, never per instance (a call cycle starts new instances without end): the
        // first occurrence's instance is kept for display. A generic note is one group per entry. A call left out because its
        // path is too deep is always kept (its groups are bounded by the package's call events).
        var source = diagnosticContext;
        bool generic = IsGenericNote(category, severity, message);
        DiagnosticKey key = new(severity, category, message, generic ? new(source.Entry) : source with { Instance = null });
        bool exempt = message.Contains(TooDeepNote, StringComparison.Ordinal);
        if (previewIssues.Count >= MaximumDiagnosticGroups && !exempt && !previewIssues.ContainsKey(key))
        {
            key = new("Information", "Support", OmittedDiagnostics, new(-1));
            message = key.Message; severity = key.Severity; category = key.Category; generic = true;
        }
        previewIssues[key] = previewIssues.TryGetValue(key, out var previous)
            ? previous with { Count = previous.Count + 1, LastTime = Time }
            : new(severity, category, message, generic ? key.Source : source, 1, Time, Time);
    }
    /// <summary>What the note of a call left out for a too-deep call path says (it is never omitted by the group limit).</summary>
    internal const string TooDeepNote = "its call path is too deep for the preview";
    private DiagnosticScope PushDiagnostic(AnimationDiagnosticContext source)
    {
        var previous = diagnosticContext; diagnosticContext = source;
        return new(() => diagnosticContext = previous);
    }
    private sealed class DiagnosticScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
