using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

public partial class AnimationEditor
{
    private readonly ObservableCollection<LogRow> logRows = [];
    private readonly ObservableCollection<ProblemRow> problemRows = [];
    private ObservableCollection<StudioProblem>? operationDiagnostics;
    private readonly List<ProblemRow> localProblems = [];
    private bool followingLog;
    private AnimationPlayer? logPlayer;
    public void SetOperationDiagnostics(ObservableCollection<StudioProblem> messages)
    {
        operationDiagnostics = messages; messages.CollectionChanged += OperationDiagnosticsChanged;
        Problems.ItemsSource = problemRows; EventLog.ItemsSource = logRows; EventLog.IsVisibleChanged += (_,_) => { if (EventLog.IsVisible) UpdateRuntimeTools(); }; Problems.IsVisibleChanged += (_,_) => { if (Problems.IsVisible) RefreshProblems(); }; EventLog.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(LogScrollChanged));
        RefreshProblems();
    }
    private void OperationDiagnosticsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshProblems();
    private void UpdateRuntimeTools()
    {
        if (frame == null) return;
        if (!ReferenceEquals(logPlayer,player)) { logRows.Clear(); logPlayer = player; }
        var rootSequences = frame.Sequences.Where(s => s.Instance == 1).ToArray();
        RuntimeSequences.ItemsSource = rootSequences;
        foreach (var sequence in program.SelectMany(p => p.Children))
        {
            var status = rootSequences.FirstOrDefault(s => s.Sequence == sequence.Sequence);
            sequence.RuntimeMarker = status?.State == "Running" ? "▶" : status?.State == "Waiting for release" ? "Ⅱ" : "";
            for (int i = 0; i < sequence.Children.Count; i++) sequence.Children[i].RuntimeMarker = status?.EventIndex == i && status.State != "Complete" ? "▸" : "";
        }
        // Reconcile by occurrence identity. A status refresh never replaces a selected row or
        // merges repeated dispatches of the same source event at the same time.
        if (logRows.Count > 0 && (frame.Trace.Count == 0 || logRows[0].Occurrence < frame.Trace[0].Occurrence || logRows[^1].Occurrence > frame.Trace[^1].Occurrence))
        {
            var retained = frame.Trace.Select(t => t.Occurrence).ToHashSet();
            for (int i = logRows.Count - 1; i >= 0; i--) if (!retained.Contains(logRows[i].Occurrence)) logRows.RemoveAt(i);
        }
        long latest = logRows.LastOrDefault()?.Occurrence ?? 0;
        foreach (var trace in frame.Trace)
        {
            if (trace.Occurrence <= latest) continue;
            var entry = edits.Package.Entries.ElementAtOrDefault(trace.Entry);
            string sequence = entry?.AllSequences.FirstOrDefault(s => s.Id == trace.Sequence)?.Name ?? trace.Sequence.ToString();
            logRows.Add(new(trace.Occurrence,trace.Start,trace.Instance,trace.Entry,trace.Sequence,trace.Event,sequence,trace.Name,trace.Status));
        }
        LogLimit.Text = $"{frame.Trace.Count:N0} retained · {frame.TraceDropped:N0} earlier records discarded";
        int laneCount = rootSequences.Select(s => s.Sequence).Distinct().Count();
        TraceHint.Text = $"{Math.Min(6,laneCount)} of {laneCount} root sequence lanes · considers up to the latest 1,200 retained root events · child instances excluded\n{frame.TraceDropped:N0} earlier records discarded from event-log retention · △ approximate / trace-only · selected lane outlined · Ctrl+wheel zoom";
        if (FollowLatest.IsChecked == true && logRows.Count > 0 && EventLog.IsVisible)
        {
            followingLog = true; EventLog.ScrollIntoView(logRows[^1]);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => followingLog = false);
        }
        RefreshProblems();
    }
    private void RefreshProblems()
    {
        if (Problems == null || disposed || !Problems.IsVisible) return;
        var rows = CurrentProblems();
        string filter = (ProblemFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        var selected = Problems.SelectedItem as ProblemRow;
        var view = rows.Where(r => filter == "All" || r.Category == filter).ToArray();
        for (int i = 0; i < view.Length; i++)
        {
            if (i == problemRows.Count) problemRows.Add(view[i]);
            else if (problemRows[i] != view[i]) problemRows[i] = view[i];
        }
        while (problemRows.Count > view.Length) problemRows.RemoveAt(problemRows.Count - 1);
        if (selected != null) Problems.SelectedItem = problemRows.FirstOrDefault(r => r.Entry == selected.Entry && r.Sequence == selected.Sequence && r.Event == selected.Event && r.Message == selected.Message && r.Scope == selected.Scope);
    }
    private List<ProblemRow> CurrentProblems()
    {
        List<ProblemRow> rows = [];
        if (frame != null)
            rows.AddRange(frame.Issues.Select(d => new ProblemRow(d.Severity,d.Category,$"Entry #{d.Source.Entry}" + (d.Source.Instance is long instance ? $" / instance {instance}" : ""),d.Message,d.Count,d.Source.Entry,d.Source.Sequence,d.Source.Event,$"First {d.FirstTime:0.000}s · last {d.LastTime:0.000}s · count is diagnostic emissions")));
        rows.AddRange(localProblems);
        foreach (var message in audioDiagnostics.Distinct(StringComparer.Ordinal)) rows.Add(new("Warning","Resource","Audio",message,1,entryIndex,null,null,"Audio preparation/output diagnostic"));
        // File/operation source context is supplied by its emitter, independently of preview selection.
        if (operationDiagnostics != null) rows.AddRange(operationDiagnostics.Select(m => new ProblemRow(m.Severity,m.Category,m.Scope,m.Message,1,-1,null,null,m.Details) { FileProblem = m }));
        return rows;
    }
    private void RecordPreviewError(string message)
    {
        if (!localProblems.Any(p => p.Message == message)) localProblems.Add(new("Error","Preview",$"Entry #{entryIndex}",message,1,entryIndex,null,null,"Preview operation failed; stored animation data is unchanged."));
        Note(message); RefreshProblems();
    }
    private void ProblemFilterChanged(object sender, SelectionChangedEventArgs e) { if (ready) RefreshProblems(); }
    private void CopyProblemClick(object sender, RoutedEventArgs e)
    {
        if (Problems.SelectedItem is ProblemRow row) Clipboard.SetText($"{row.Severity}\t{row.Category}\t{row.Scope}\t{row.Message}\t{row.Count}\n{row.Details}");
    }
    private void LogDetailsClick(object sender, RoutedEventArgs e)
    {
        if (EventLog.SelectedItem is LogRow row) ShowDiagnosticDetails("Event details", $"Event: {row.Name}\nSequence: {row.SequenceName}\nEntry: #{row.Entry} · instance {row.Instance}\nOccurrence: {row.Occurrence}\nTime: {row.Start:R} seconds\nSupport / result: {row.Status}\n\nSequence ID: {row.Sequence}\nEvent ID: {row.Event}");
        else Note("Select an event-log row to inspect its full details.");
    }
    private void ProblemDetailsClick(object sender, RoutedEventArgs e)
    {
        if (Problems.SelectedItem is ProblemRow row) ShowDiagnosticDetails("Problem details", $"{row.Severity} · {row.Category}\n{row.Scope}\n\n{row.Message}\n\nCount: {row.Count}\n{row.Details}");
        else Note("Select a problem to inspect its full details.");
    }
    private void RuntimeDetailsClick(object sender, RoutedEventArgs e)
    {
        if (RuntimeSequences.SelectedItem is AnimationSequenceStatus row) ShowDiagnosticDetails("Runtime sequence", $"{row.Name}\n{row.State}\nEvent index: {row.EventIndex}\nLoop: {row.Iteration}\nInstance: {row.Instance}\nSequence ID: {row.Sequence}");
        else Note("Select a runtime sequence to inspect its full details.");
    }
    private void ShowDiagnosticDetails(string title, string text)
    {
        DetailDialog.Show(Window.GetWindow(this),title,text);
    }
    private void ProblemNavigate(object sender, MouseButtonEventArgs e)
    {
        if (Problems.SelectedItem is not ProblemRow row) return;
        if (row.FileProblem is { } problem) FileProblemSelected?.Invoke(problem);
        else if (row.Sequence is Guid sequence)
        {
            if (row.Entry == entryIndex) SelectSource(sequence,row.Event ?? Guid.Empty);
            else SourceNavigationRequested?.Invoke(row.Entry,sequence,row.Event ?? Guid.Empty);
        }
    }
    private void LogNavigate(object sender, MouseButtonEventArgs e)
    {
        if (EventLog.SelectedItem is LogRow row)
        {
            if (row.Entry == entryIndex) SelectSource(row.Sequence,row.Event);
            else SourceNavigationRequested?.Invoke(row.Entry,row.Sequence,row.Event);
        }
    }
    private void LogWheel(object sender, MouseWheelEventArgs e) { if (e.Delta > 0) FollowLatest.IsChecked = false; }
    private void LogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!followingLog && e.VerticalChange < 0 && e.ExtentHeightChange == 0) FollowLatest.IsChecked = false;
    }
    private static string LogText(IEnumerable<LogRow> rows) => "Occurrence\tTime(s)\tInstance\tEntry\tSequence\tEvent\tSupport / result\n" + string.Join("\n",rows.Select(r => $"{r.Occurrence}\t{r.Start:R}\t{r.Instance}\t{r.Entry}\t{r.SequenceName} ({r.Sequence})\t{r.Name} ({r.Event})\t{r.Status}"));
    private void CopyLogSelectedClick(object sender, RoutedEventArgs e) { if (EventLog.SelectedItems.Count > 0) Clipboard.SetText(LogText(EventLog.SelectedItems.OfType<LogRow>().OrderBy(r => r.Occurrence))); }
    private void CopyLogVisibleClick(object sender, RoutedEventArgs e) => Clipboard.SetText(LogText(EventLog.Items.OfType<LogRow>()));
    private sealed record LogRow(long Occurrence,double Start,long Instance,int Entry,Guid Sequence,Guid Event,string SequenceName,string Name,string Status);
    private sealed record ProblemRow(string Severity,string Category,string Scope,string Message,long Count,int Entry,Guid? Sequence,Guid? Event,string Details) { public StudioProblem? FileProblem { get; init; } }
    public event Action<StudioProblem>? FileProblemSelected;
}
