using System.Collections.ObjectModel;

namespace zStudio.Desktop;

public partial class AnimationEditor
{
    private readonly List<ProblemRow> localProblems = [];
    private ObservableCollection<StudioProblem>? operationDiagnostics;
    /// <summary>The window's file problems, listed after the preview's own (animation_runtime problems; Diagnostics shows them).</summary>
    public void SetOperationDiagnostics(ObservableCollection<StudioProblem> messages) => operationDiagnostics = messages;
    /// <summary>The preview's problems: the run's issues, preview failures, sound preparation and the file's problems (animation_runtime problems; the notices chip and Diagnostics show them).</summary>
    internal IReadOnlyList<ProblemRow> CurrentProblems()
    {
        List<ProblemRow> rows = [];
        if (frame != null)
            rows.AddRange(frame.Issues.Select(d => new ProblemRow(d.Severity, d.Category, $"Entry #{d.Source.Entry}" + (d.Source.Instance is long instance ? $" / instance {instance}" : ""), d.Message, d.Count, d.Source.Entry, d.Source.Sequence, d.Source.Event,
                $"First {d.FirstTime:0.000}s · last {d.LastTime:0.000}s · count is diagnostic emissions")));
        rows.AddRange(localProblems);
        foreach (var message in audioDiagnostics.Distinct(StringComparer.Ordinal)) rows.Add(new("Warning", "Resource", "Audio", message, 1, entryIndex, null, null, "Audio preparation/output diagnostic"));
        if (operationDiagnostics != null) rows.AddRange(operationDiagnostics.Select(m => new ProblemRow(m.Severity, m.Category, m.Scope, m.Message, 1, -1, null, null, m.Details) { FileProblem = m }));
        return rows;
    }
    private void RecordPreviewError(string message)
    {
        if (!localProblems.Any(p => p.Message == message)) localProblems.Add(new("Error", "Preview", $"Entry #{entryIndex}", message, 1, entryIndex, null, null, "Preview operation failed; stored animation data is unchanged."));
        Note(message);
    }
    internal sealed record ProblemRow(string Severity, string Category, string Scope, string Message, long Count, int Entry, Guid? Sequence, Guid? Event, string Details) { public StudioProblem? FileProblem { get; init; } }
}
