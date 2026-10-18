using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// The progress of a GUI export or check of the source project: modal, so the workspace below cannot be used while it runs.
/// The work runs while the dialog is open; it says what is being built and how far the run is, and Cancel (or Escape, or
/// closing it) stops the work. The dialog closes when the work ends; its result or failure goes back to the caller.
/// </summary>
internal sealed class SourceExportDialog : Window
{
    private readonly Func<IProgress<SourceProgress>, CancellationToken, Task> work;
    private readonly bool writes;
    private readonly TextBlock stage = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly TextBlock count = new() { Opacity = 0.7, Margin = new(0, 4, 0, 0) };
    private readonly ProgressBar bar = new() { IsIndeterminate = true, Height = 4, Margin = new(0, 10, 0, 0) };
    private readonly Button cancel = new() { Content = "Cancel", MinWidth = 80, IsCancel = false };
    private readonly CancellationTokenSource stopping = new();
    private Exception? failure;
    private bool finished;

    /// <param name="writes">An export writes the game files at its end; a check only builds them.</param>
    public SourceExportDialog(Window owner, string title, string heading, bool writes, Func<IProgress<SourceProgress>, CancellationToken, Task> work)
    {
        this.work = work; this.writes = writes;
        Owner = owner; Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        System.Windows.Automation.AutomationProperties.SetName(bar, "Progress");
        StackPanel panel = new();
        panel.Children.Add(new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(stage); panel.Children.Add(bar); panel.Children.Add(count);
        WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        Content = DialogLayout.WithActions(panel, buttons);
        DialogLayout.Constrain(this, new(520, 230));
        stage.Text = "Planning the game files…";
        cancel.Click += (_, _) => Stop();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Stop(); } };
        // Closing stops the work first; the dialog closes once it has ended.
        Closing += (_, e) => { if (!finished) { e.Cancel = true; Stop(); } };
        Loaded += async (_, _) => await RunAsync();
    }

    /// <summary>Shows the dialog until the work ends, then rethrows its failure (a cancellation included).</summary>
    public void Run()
    {
        ShowDialog();
        // Closed before the work ended (its owner closing): the run was stopped, as by Cancel.
        if (!finished) { Stop(); throw new OperationCanceledException("The export window closed before the run ended."); }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task RunAsync()
    {
        try { await work(new Progress<SourceProgress>(Show), stopping.Token); }
        catch (Exception ex) { failure = ex; }
        finished = true; stopping.Dispose();
        Close();
    }

    /// <summary>What the run is doing: each game file as it is built, then the checks before anything is written, then writing.</summary>
    private void Show(SourceProgress progress)
    {
        if (finished || stopping.IsCancellationRequested) return;
        if (progress.Total <= 0) return;
        bool items = progress.Completed < progress.Total;
        stage.Text = items ? $"Building {progress.Item}" : progress.Item switch
        {
            "Checking lookups by name" => "Checking what the missions look up by name…",
            "Publishing" => "Checking the sources are unchanged, then writing the game files…",
            "Checked" => "Checking the sources are unchanged…",
            _ => progress.Item,
        };
        bar.IsIndeterminate = !items;
        bar.Maximum = progress.Total; bar.Value = progress.Completed;
        count.Text = items ? $"{progress.Completed + 1:N0} of {progress.Total:N0} game files" : $"{progress.Total:N0} game files built" + (writes ? "" : ", nothing written");
    }

    private void Stop()
    {
        if (finished || stopping.IsCancellationRequested) return;
        stopping.Cancel(); cancel.IsEnabled = false;
        stage.Text = writes ? "Canceling… game files already being replaced are finished or restored first." : "Canceling…";
    }
}
