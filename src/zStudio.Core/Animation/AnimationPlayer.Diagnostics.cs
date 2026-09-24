namespace Recoil.Zbd.Core.Animation;

public sealed record AnimationDiagnosticContext(int Entry, long? Instance = null, Guid? Sequence = null, Guid? Event = null);
public sealed record AnimationPreviewDiagnostic(string Severity, string Category, string Message, AnimationDiagnosticContext Source, long Count, double FirstTime, double LastTime);

public sealed partial class AnimationPlayer
{
    private readonly record struct DiagnosticKey(string Severity, string Category, string Message, AnimationDiagnosticContext Source);
    private Dictionary<DiagnosticKey, AnimationPreviewDiagnostic> previewIssues = [];
    private AnimationDiagnosticContext diagnosticContext = new(-1);
    private void AddNote(string message, string category = "Preview", string severity = "Warning")
    {
        notes.Add(message); // Preserve the existing text API and duration-analysis contract.
        DiagnosticKey key = new(severity, category, message, diagnosticContext);
        if (previewIssues.Count >= 2000 && !previewIssues.ContainsKey(key))
        {
            key = new("Information","Support","Additional distinct preview diagnostics were omitted after the 2,000-group limit.",new(-1));
            message = key.Message; severity = key.Severity; category = key.Category;
        }
        previewIssues[key] = previewIssues.TryGetValue(key, out var previous)
            ? previous with { Count = previous.Count + 1, LastTime = Time }
            : new(severity, category, message, key.Source, 1, Time, Time);
    }
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
