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

    /// <summary>After a source project opens (or on request): report interrupted saves and offer to resolve them; returns how many there are, -1 when they could not be checked, or -2 when a newer check took over.</summary>
    /// <remarks>
    /// On opening, only saves that need a decision ask; a save that finished but was not cleaned up is reported in
    /// Problems, and <paramref name="everySave"/> (Tools → Resolve interrupted save) asks about it too.
    /// </remarks>
    private async Task<int> CheckSourceRecoveryAsync(string root, bool everySave = false)
    {
        long generation = ++recoveryCheckGeneration;
        IReadOnlyList<SourceRecoveryCase> cases;
        try { cases = await Task.Run(() => new SourcePublisher(root).FindInterrupted()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { ViewModel.AddProblem(Bounded($"Source project recovery could not be checked: {ex.Message}"), "Error", root); return -1; }
        if (generation != recoveryCheckGeneration || SourceProjectRoot != root) return -2;
        foreach (var old in ViewModel.Problems.Where(p => p.File == root && p.Message.StartsWith(RecoveryProblem, StringComparison.Ordinal)).ToArray()) ViewModel.Problems.Remove(old);
        if (cases.Count == 0) return 0;
        foreach (var c in cases)
            ViewModel.AddProblem(Bounded($"{RecoveryProblem} ({c.SaveId}, {c.Description}): {string.Join(", ", c.Files.Take(8).Select(f => $"{f.Relative} is {f.State}"))}." + (c.Committed ? " It finished; only its journal remains to clean up (Tools → Resolve interrupted save, or zstudio_source_recovery_resolve complete)." : " Use Tools → Resolve interrupted save, or zstudio_source_recovery.")), c.Committed ? "Warning" : "Error", root);
        if (automationCloseRequested || !IsVisible) return cases.Count;
        foreach (var c in cases.Where(c => everySave || !c.Committed))
        {
            // Another check or another root took over: its own dialogs decide.
            if (generation != recoveryCheckGeneration || SourceProjectRoot != root) break;
            string files = string.Join("\n", c.Files.Take(12).Select(f => $"{f.Relative}: {Describe(f.State)}{(f.HeldOriginal ? " (original kept)" : "")}"));
            string choice = "Later";
            StackPanel panel = new() { Margin = new(20) };
            panel.Children.Add(new TextBlock { Text = c.Committed ? "A save of this source project finished but was not cleaned up" : "A save of this source project was interrupted", FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = c.Committed
                ? $"{c.Description} ({c.CreatedUtc.ToLocalTime():g}) wrote every file:\n{files}\n\nComplete removes its journal and the originals it kept. Keep files sets the journal aside instead."
                : $"{c.Description} ({c.CreatedUtc.ToLocalTime():g}). The files are now:\n{files}\n\nRoll back restores the files as they were before the save. Complete finishes the save. Keep files leaves them as they are and sets the journal aside. Files changed by another program are never overwritten.", Margin = new(0, 12, 0, 20), TextWrapping = TextWrapping.Wrap });
            WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
            Window dialog = new() { Owner = this, Title = "Interrupted save", Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
            foreach (string label in c.Committed ? new[] { "Complete", "Keep files", "Later" } : new[] { "Roll back", "Complete", "Keep files", "Later" })
            {
                Button button = new() { Content = label, MinWidth = 95, Margin = new(4), Padding = new(10, 7, 10, 7), IsCancel = label == "Later" };
                button.Click += (_, _) => { choice = label; dialog.Close(); }; buttons.Children.Add(button);
            }
            dialog.ShowDialog();
            if (choice == "Later") continue;
            var action = choice switch { "Roll back" => SourceRecoveryAction.RollBack, "Complete" => SourceRecoveryAction.Complete, _ => SourceRecoveryAction.Abandon };
            try
            {
                var result = ResolveSourceRecovery(root, c.SaveId, action);
                MessageBox.Show(this, result.Resolved ? $"Done: {result.Changed.Count} files changed." : $"Not finished: {string.Join("; ", result.Conflicts.Take(8).Select(x => $"{x.Relative}: {x.Reason}"))}", "Interrupted save", MessageBoxButton.OK, result.Resolved ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (StudioCommandException ex) { Report(ex); }
        }
        return cases.Count;
        static string Describe(SourceRecoveryFileState state) => state switch
        {
            SourceRecoveryFileState.Before => "as before the save", SourceRecoveryFileState.After => "as the save wrote it",
            SourceRecoveryFileState.Missing => "missing", _ => "changed by another program"
        };
    }

    private SourceRecoveryResult ResolveSourceRecovery(string root, string saveId, SourceRecoveryAction action)
    {
        if (sourceWorkspace is { } workspace && workspace.Root.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase))
        {
            if (sourceWorkspaceBusy) throw new StudioCommandException("busy", "Let the project's worlds finish rebuilding before resolving an interrupted save.");
            // Only edits of the save's own files would be overtaken by resolving it.
            IReadOnlyList<string> journal;
            try { journal = new SourcePublisher(root).FindInterrupted().FirstOrDefault(c => c.SaveId == saveId)?.Files.Select(f => f.Relative).ToArray() ?? []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { throw new StudioCommandException("io_failed", ex.Message); }
            if (workspace.DirtyFiles.Intersect(journal, StringComparer.OrdinalIgnoreCase).ToArray() is { Length: > 0 } overlap)
                throw new StudioCommandException("unsaved_changes", $"The project's unsaved edits change {string.Join(", ", overlap.Take(6))}, which the interrupted save also wrote; save or undo those edits first.");
        }
        try
        {
            var result = new SourcePublisher(root).Resolve(saveId, action);
            // A resolved save no longer needs a decision.
            if (result.Resolved)
                foreach (var old in ViewModel.Problems.Where(p => p.Message.StartsWith(RecoveryProblem, StringComparison.Ordinal) && p.Message.Contains($"({saveId},", StringComparison.Ordinal)).ToArray()) ViewModel.Problems.Remove(old);
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FileNotFoundException) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { throw new StudioCommandException("io_failed", ex.Message); }
        finally { ViewModel.CheckExternalChanges(); }
    }

    private void RegisterSourceRecoveryCommands(StudioCommands r)
    {
        Register(r, "source_recovery", "List the open source project's interrupted saves (journals in zstudio/recovery): each with its files and whether each now holds the content from before the save, the content the save wrote, is missing, or was changed by another program. A committed journal only needs cleaning up. While an uncommitted one exists, the project cannot be saved.", false, [], _ =>
        {
            string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
            IReadOnlyList<SourceRecoveryCase> cases;
            try { cases = new SourcePublisher(root).FindInterrupted(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { throw new StudioCommandException("io_failed", ex.Message); }
            return Result(new
            {
                saves = cases.Take(32).Select(c => new
                {
                    id = c.SaveId, description = Bounded(c.Description, 256), created = c.CreatedUtc, committed = c.Committed,
                    files = c.Files.Take(64).Select(f => new { file = f.Relative, state = f.State.ToString(), heldOriginal = f.HeldOriginal }).ToArray(), fileCount = c.Files.Count
                }).ToArray(),
                saveCount = cases.Count
            });
        });
        Register(r, "source_recovery_resolve", "Resolve an interrupted save of the open source project: roll_back restores every file the save had replaced (a file changed by another program since is left alone and reported), complete finishes the save, and abandon keeps the files as they are and moves the journal (with any original it kept) to zstudio/recovery/abandoned. Refused while a world rebuilds, and while the project has unsaved edits of a file the save involves (undo them, or close the worlds discarding them).", true,
            [P("save", "string", "Save id from zstudio_source_recovery.", true), P("action", "string", "What to do.", true, "roll_back", "complete", "abandon")], a =>
        {
            string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
            var action = Text(a, "action") switch { "roll_back" => SourceRecoveryAction.RollBack, "complete" => SourceRecoveryAction.Complete, _ => SourceRecoveryAction.Abandon };
            var result = ResolveSourceRecovery(root, Text(a, "save"), action);
            return Result(new { resolved = result.Resolved, changed = result.Changed.Take(64).ToArray(), changedCount = result.Changed.Count, conflicts = result.Conflicts.Take(64).Select(c => new { file = c.Relative, reason = Bounded(c.Reason, 256) }).ToArray(), conflictCount = result.Conflicts.Count });
        });
    }
}
