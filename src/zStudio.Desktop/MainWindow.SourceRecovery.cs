using System.IO;
using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// Saves of a source project that were interrupted (the program or the computer stopped while the files were being
/// replaced) leave a journal in the project's zstudio/recovery folder. Opening the project reports them, and the user
/// decides: roll the files back to before the save, complete the save, or keep the files as they are and set the journal
/// aside. Until then the project cannot be saved; nothing is decided automatically.
/// </summary>
public partial class MainWindow
{
    private long recoveryCheckGeneration;

    private const string RecoveryProblem = "An interrupted save of the source project needs a decision";
    /// <summary>GUI: Tools → Resolve interrupted save… checks the open project now and offers the decisions.</summary>
    private void SourceRecoveryClick(object sender, RoutedEventArgs e) => _ = RunUi(async () =>
    {
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
        int found = await CheckSourceRecoveryAsync(root, everySave: true);
        ViewModel.Status = found == 0 ? "No save of this source project was interrupted." : found == -1 ? "The project's interrupted saves could not be checked; Problems says why." : ViewModel.Status;
    });

    /// <summary>After a source project opens (or on request): report interrupted saves and offer to resolve them; returns how many there are, -1 when they could not be checked, or -2 when a newer check took over (or closing stopped it).</summary>
    /// <remarks>
    /// On opening, only saves that need a decision ask; a save that finished but was not cleaned up is reported in
    /// Problems, and <paramref name="everySave"/> (Tools → Resolve interrupted save) asks about it too.
    /// </remarks>
    private async Task<int> CheckSourceRecoveryAsync(string root, bool everySave = false)
    {
        long generation = ++recoveryCheckGeneration;
        IReadOnlyList<SourceRecoveryCase> cases;
        // Each file's state reads it whole; closing stops the check.
        try { cases = await Task.Run(() => new SourcePublisher(root).FindInterrupted(shutdownToken), shutdownToken); }
        catch (OperationCanceledException) { return -2; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (generation != recoveryCheckGeneration || SourceProjectRoot != root) return -2;
            ViewModel.AddProblem(Bounded($"Source project recovery could not be checked: {Bounded(ex.Message, 512)}"), "Error", root); return -1;
        }
        if (generation != recoveryCheckGeneration || SourceProjectRoot != root) return -2;
        foreach (var old in ViewModel.Problems.Where(p => p.File == root && p.Message.StartsWith(RecoveryProblem, StringComparison.Ordinal)).ToArray()) ViewModel.Problems.Remove(old);
        if (cases.Count == 0) return 0;
        foreach (var c in cases)
            ViewModel.AddProblem(Bounded($"{RecoveryProblem} ({c.SaveId}, {Bounded(c.Description, 256)}): {RecoveryFilesText(c.Files, detailed: false)}." + (c.Committed ? " It finished; only its journal remains to clean up (Tools → Resolve interrupted save, or zstudio_source_recovery_resolve complete)." : " Use Tools → Resolve interrupted save, or zstudio_source_recovery.")), c.Committed ? "Warning" : "Error", root);
        if (automationCloseRequested || !IsVisible) return cases.Count;
        foreach (var c in cases.Where(c => everySave || !c.Committed))
        {
            // Another check or another root took over: its own dialogs decide.
            if (generation != recoveryCheckGeneration || SourceProjectRoot != root) break;
            string choice = "Later";
            Window dialog = CreateSourceRecoveryDialog(c, selected => choice = selected);
            dialog.ShowDialog();
            if (choice == "Later") continue;
            var action = choice switch { "Roll back" => SourceRecoveryAction.RollBack, "Complete" => SourceRecoveryAction.Complete, _ => SourceRecoveryAction.Abandon };
            try
            {
                ViewModel.Status = "Resolving the interrupted save…";
                var result = await ResolveSourceRecoveryAsync(root, c.SaveId, action, shutdownToken);
                if (shutdownToken.IsCancellationRequested) break;
                ViewModel.Status = result.Resolved ? "The interrupted save was resolved." : "The interrupted save still needs a decision.";
                MessageBox.Show(this, result.Resolved ? $"Done: {result.Changed.Count} files changed." : $"Not finished: {RecoveryConflictsText(result.Conflicts)}", "Interrupted save", MessageBoxButton.OK, result.Resolved ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (StudioCommandException ex) { Report(ex); }
            // Closing (or another root) canceled it between two files; what it changed is in Problems.
            catch (OperationCanceledException) { break; }
        }
        return cases.Count;
    }

    /// <summary>Keep decisions outside the scrollable journal details, including on a small or scaled work area.</summary>
    internal Window CreateSourceRecoveryDialog(SourceRecoveryCase recovery, Action<string> selected, Size? available = null)
    {
        Size work = available ?? RecoveryWorkArea();
        double width = Math.Max(1, work.Width - 32), height = Math.Max(1, work.Height - 32);
        Grid layout = new() { Margin = new(20) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock { Text = recovery.Committed ? "A save of this source project finished but was not cleaned up" : "A save of this source project was interrupted",
            FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        string files = RecoveryFilesText(recovery.Files, detailed: true);
        TextBlock details = new() { Text = recovery.Committed
            ? $"{recovery.Description} ({recovery.CreatedUtc.ToLocalTime():g}) wrote every file:\n{files}\n\nComplete removes its journal and the originals it kept. Keep files sets the journal aside instead."
            : $"{recovery.Description} ({recovery.CreatedUtc.ToLocalTime():g}). The files are now:\n{files}\n\nRoll back restores the files as they were before the save. Complete finishes the save. Keep files leaves them as they are and sets the journal aside. Files changed by another program are never overwritten.", TextWrapping = TextWrapping.Wrap };
        ScrollViewer scroll = new() { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 12, 0, 16) };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttons, 2); layout.Children.Add(buttons);
        Window dialog = new() { Owner = this, Title = "Interrupted save", Width = Math.Min(560, width), Height = Math.Min(520, height), MaxWidth = width, MaxHeight = height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Content = layout };
        foreach (string label in recovery.Committed ? new[] { "Complete", "Keep files", "Later" } : new[] { "Roll back", "Complete", "Keep files", "Later" })
        {
            Button button = new() { Content = label, MinWidth = 95, Margin = new(4), Padding = new(10, 7, 10, 7), IsCancel = label == "Later" };
            button.Click += (_, _) => { selected(label); dialog.Close(); }; buttons.Children.Add(button);
        }
        return dialog;
    }

    private Size RecoveryWorkArea()
    {
        nint handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var monitor = new MonitorBounds { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorBounds>() };
        if (handle != 0 && GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor))
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            return new((monitor.Work.Right - monitor.Work.Left) / dpi.DpiScaleX, (monitor.Work.Bottom - monitor.Work.Top) / dpi.DpiScaleY);
        }
        return SystemParameters.WorkArea.Size;
    }

    /// <summary>Presentation only: retain full recovery identities, and disclose omitted rows before the bounded summary.</summary>
    internal static string RecoveryFilesText(IReadOnlyList<SourceRecoveryFile> files, bool detailed)
    {
        int limit = detailed ? 12 : 8;
        string separator = detailed ? "\n" : ", ";
        string count = $"{files.Count:N0} files" + (files.Count > limit ? $" ({files.Count - limit:N0} more not shown)" : "");
        return count + ": " + string.Join(separator, files.Take(limit).Select(f => detailed
            ? $"{Bounded(f.Relative, 256)}: {Describe(f.State)}{(f.HeldOriginal ? " (original kept)" : "")}" : $"{Bounded(f.Relative, 256)} is {f.State}"));
        static string Describe(SourceRecoveryFileState state) => state switch
        {
            SourceRecoveryFileState.Before => "as before the save", SourceRecoveryFileState.After => "as the save wrote it",
            SourceRecoveryFileState.Missing => "missing", _ => "changed by another program"
        };
    }

    internal static string RecoveryConflictsText(IReadOnlyList<SourceRecoveryConflict> conflicts) =>
        $"{conflicts.Count:N0} conflicts" + (conflicts.Count > 8 ? $" ({conflicts.Count - 8:N0} more not shown)" : "") + ": "
        + string.Join("; ", conflicts.Take(8).Select(c => $"{Bounded(c.Relative, 256)}: {Bounded(c.Reason, 256)}"));

    /// <summary>The resolution of an interrupted save running off the UI thread; closing waits for it (canceled, it stops between two files).</summary>
    private Task sourceRecoveryWork = Task.CompletedTask;
    /// <summary>Runs the resolution itself off the UI thread (tests hold it to cancel or close while it runs).</summary>
    internal Func<string, string, SourceRecoveryAction, CancellationToken, SourceRecoveryResult> ResolveSourceSave { get; set; } = static (root, saveId, action, token) => new SourcePublisher(root).Resolve(saveId, action, token);

    /// <summary>
    /// Resolves an interrupted save (GUI and MCP). The journal is read and the files are hashed and moved off the UI thread
    /// while the workspace is disabled as during a save, so no edit, save or other MCP change overtakes it; only the result
    /// is published here. <paramref name="token"/>, a change of root and closing cancel it between two files: the journal
    /// then records what it did, the save still needs a decision, and a cancellation after files changed says which (also
    /// in Problems).
    /// </summary>
    private async Task<SourceRecoveryResult> ResolveSourceRecoveryAsync(string root, string saveId, SourceRecoveryAction action, CancellationToken token)
    {
        var workspace = sourceWorkspace is { } open && open.Root.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase) ? open : null;
        if (workspace != null && sourceWorkspaceBusy) throw new StudioCommandException("busy", "Let the project's worlds finish rebuilding before resolving an interrupted save.");
        if (!sourceRecoveryWork.IsCompleted) throw new StudioCommandException("busy", "Another interrupted save is being resolved.");
        using var exclusion = BeginDocumentSave();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ViewModel.WorkspaceToken, shutdownToken);
        try
        {
            if (workspace != null)
            {
                // Only edits of the save's own files would be overtaken by resolving it.
                var journal = await Task.Run(() => new SourcePublisher(root).SaveFiles(saveId), cancellation.Token);
                if (workspace.DirtyFiles.Intersect(journal, StringComparer.OrdinalIgnoreCase).ToArray() is { Length: > 0 } overlap)
                    throw new StudioCommandException("unsaved_changes", $"The project's unsaved edits change {overlap.Length:N0} files{(overlap.Length > 6 ? $" ({overlap.Length - 6:N0} more not shown)" : "")}: {string.Join(", ", overlap.Take(6).Select(f => Bounded(f, 256)))}, which the interrupted save also wrote; save or undo those edits first.");
            }
            var resolve = ResolveSourceSave;
            var work = Task.Run(() => resolve(root, saveId, action, cancellation.Token), cancellation.Token);
            sourceRecoveryWork = work;
            var result = await work;
            // A resolved save no longer needs a decision.
            if (result.Resolved)
                foreach (var old in ViewModel.Problems.Where(p => p.Message.StartsWith(RecoveryProblem, StringComparison.Ordinal) && p.Message.Contains($"({saveId},", StringComparison.Ordinal)).ToArray()) ViewModel.Problems.Remove(old);
            return result;
        }
        // Canceled after it changed files: it stays a cancellation, and says what it left (MCP returns the same message).
        catch (OperationCanceledException ex) when (ex.InnerException is OperationCanceledException) { if (!shutdownToken.IsCancellationRequested) ViewModel.AddProblem(Bounded(ex.Message), "Warning", root); throw; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FileNotFoundException) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { throw new StudioCommandException("io_failed", ex.Message); }
        finally { if (!shutdownToken.IsCancellationRequested) ViewModel.CheckExternalChanges(); }
    }

    private void RegisterSourceRecoveryCommands(StudioCommands r)
    {
        Register(r, "source_recovery", "List the open source project's interrupted saves (journals in zstudio/recovery): each with its files and whether each now holds the content from before the save, the content the save wrote, is missing, or was changed by another program. Returns at most 32 saves with 64 files each and total counts; file paths keep 256 characters with fileTruncated. The scan bounds all journal manifests and logs together to 64 MiB and 50,000 file rows. A committed journal only needs cleaning up. While an uncommitted one exists, the project cannot be saved. A project replacement during inspection returns context_changed; retry for the current project.", false, [], async (_, token) =>
        {
            string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
            long generation = ViewModel.WorkspaceGeneration;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ViewModel.WorkspaceToken, shutdownToken);
            token = cancellation.Token;
            var reading = SourceProjectReading;
            IReadOnlyList<SourceRecoveryCase> cases;
            // File hashing stays off the UI thread and stops when the request or its workspace ends.
            try { cases = await Task.Run(() => { reading?.Invoke("source_recovery", token); return new SourcePublisher(root).FindInterrupted(token); }, token); }
            catch (OperationCanceledException) { RequireSourceRead(root, generation, token); throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { throw new StudioCommandException("io_failed", ex.Message); }
            RequireSourceRead(root, generation, token);
            return Result(new
            {
                saves = cases.Take(32).Select(c => new
                {
                    id = c.SaveId, description = Bounded(c.Description, 256), created = c.CreatedUtc, committed = c.Committed,
                    files = c.Files.Take(64).Select(f => new { file = Bounded(f.Relative, 256), fileTruncated = f.Relative.Length > 256, state = f.State.ToString(), heldOriginal = f.HeldOriginal }).ToArray(), fileCount = c.Files.Count
                }).ToArray(),
                saveCount = cases.Count
            });
        });
        RegisterJob(r, "source_recovery_resolve", "Resolve an interrupted save of the open source project: roll_back restores every file the save had replaced (a file changed by another program since is left alone and reported), complete finishes the save, and abandon keeps the files as they are and moves the journal (with any original it kept) to zstudio/recovery/abandoned. It runs off the UI thread with the workspace disabled as during a save. Cancelling stops it between two files, never during one: the journal records what it did, the save still needs a decision (any action resolves it from there), and a cancellation after files changed says which. Returns at most 64 changed paths and conflicts with total counts. Paths keep 256 characters, with pathsTruncated for changed paths and fileTruncated on conflicts. Refused while a world rebuilds, and while the project has unsaved edits of a file the save involves (undo them, or close the worlds discarding them).",
            [P("save", "string", "Save id from zstudio_source_recovery.", true), P("action", "string", "What to do.", true, "roll_back", "complete", "abandon")], true, async (a, token) =>
        {
            string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
            var action = Text(a, "action") switch { "roll_back" => SourceRecoveryAction.RollBack, "complete" => SourceRecoveryAction.Complete, _ => SourceRecoveryAction.Abandon };
            var result = await ResolveSourceRecoveryAsync(root, Text(a, "save"), action, token);
            // Resolved (or left with conflicts): the job completes with the result, even when a cancel arrives as it ends.
            CommitRunningJob();
            return Result(new { resolved = result.Resolved, changed = result.Changed.Take(64).Select(f => Bounded(f, 256)).ToArray(), changedCount = result.Changed.Count,
                pathsTruncated = result.Changed.Take(64).Any(f => f.Length > 256),
                conflicts = result.Conflicts.Take(64).Select(c => new { file = Bounded(c.Relative, 256), fileTruncated = c.Relative.Length > 256, reason = Bounded(c.Reason, 256) }).ToArray(), conflictCount = result.Conflicts.Count });
        });
    }
}
