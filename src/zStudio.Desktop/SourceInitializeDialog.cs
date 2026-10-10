using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// Chooses the retail ZBD folder and a new or empty folder, then initializes a source project there; the caller opens it
/// once the dialog has closed. The work runs while the dialog is open, so its progress and any refusal show here and Cancel
/// stops it (nothing stays written). A Cancel that comes once the project is written leaves it unopened in its folder.
/// </summary>
internal sealed class SourceInitializeDialog : Window
{
    public delegate Task<SourceReconstructionReport> Initialize(string retail, string project, IProgress<string> progress, CancellationToken token);

    /// <summary>What the initialization wrote, once it has finished.</summary>
    public SourceReconstructionReport? Report { get; private set; }
    private readonly Initialize initialize;
    private readonly TextBox retail = new(), project = new();
    private readonly Button browseRetail = new() { Content = "Browse…", MinWidth = 80, Margin = new(8, 0, 0, 0) }, browseProject = new() { Content = "Browse…", MinWidth = 80, Margin = new(8, 0, 0, 0) };
    private readonly Button start = new() { Content = "Initialize and open", IsDefault = true, IsEnabled = false, MinWidth = 80, Margin = new(0, 0, 8, 0), Padding = new(12, 4, 12, 4) }, cancel = new() { Content = "Cancel", MinWidth = 80 };
    private readonly TextBlock progressText = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }, error = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.IndianRed };
    private readonly ProgressBar progressBar = new() { IsIndeterminate = true, Height = 4, Margin = new(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private CancellationTokenSource? running;
    /// <summary>The window was asked to close while initializing; it closes once the work has stopped.</summary>
    private bool closeWhenStopped;

    public SourceInitializeDialog(Window owner, string? retailFolder, Initialize initialize, Size? available = null)
    {
        this.initialize = initialize;
        Owner = owner; Title = "Initialize source project";
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        System.Windows.Automation.AutomationProperties.SetName(retail, "Retail ZBD folder");
        System.Windows.Automation.AutomationProperties.SetName(project, "Source project folder");
        System.Windows.Automation.AutomationProperties.SetName(browseRetail, "Browse for the retail ZBD folder");
        System.Windows.Automation.AutomationProperties.SetName(browseProject, "Browse for the source project folder");
        retail.Text = retailFolder ?? "";

        StackPanel panel = new();
        panel.Children.Add(new TextBlock { Text = "Unpack the original game files once into RECOIL's source tree (data and gamegen). You then edit the sources and export new game files from them.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) });
        panel.Children.Add(Label("Retail ZBD folder"));
        panel.Children.Add(Row(retail, browseRetail));
        panel.Children.Add(Hint("The game's original files: the folder with interp.zbd, zrdr.zbd and the mission folders."));
        panel.Children.Add(Label("Source project folder"));
        panel.Children.Add(Row(project, browseProject));
        panel.Children.Add(Hint("A new or empty folder outside the retail folder."));
        panel.Children.Add(new StackPanel { Margin = new(0, 12, 0, 0), Children = { progressText, progressBar, error } });
        WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 14, 0, 0) };
        buttons.Children.Add(start); buttons.Children.Add(cancel);
        Content = DialogLayout.WithActions(panel, buttons);
        DialogLayout.Constrain(this, new(600, 520), available);

        retail.TextChanged += (_, _) => Changed(); project.TextChanged += (_, _) => Changed();
        browseRetail.Click += (_, _) => Browse(retail, "Choose the retail ZBD folder (contains interp.zbd and m1)");
        browseProject.Click += (_, _) => Browse(project, "Choose a new or empty folder for the source project");
        start.Click += async (_, _) => await StartAsync();
        cancel.Click += (_, _) => Stop();
        // Escape cancels like the button; closing while initializing stops the work first.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Stop(); } };
        Closing += (_, e) => { if (running != null) { e.Cancel = true; closeWhenStopped = true; running.Cancel(); progressText.Text = "Canceling…"; } };
        Loaded += (_, _) => (retail.Text.Length == 0 ? retail : project).Focus();
        Changed();
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 4) };
    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new(0, 4, 0, 0) };
    private static DockPanel Row(TextBox box, Button browse)
    {
        DockPanel row = new();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse); row.Children.Add(box);
        return row;
    }

    private bool Ready => retail.Text.Trim().Length > 0 && project.Text.Trim().Length > 0;
    private void Changed() { start.IsEnabled = running == null && Ready; error.Text = ""; }

    private void Browse(TextBox box, string title)
    {
        OpenFolderDialog dialog = new() { Title = title };
        // Start at the typed folder, or at its nearest existing parent when it is a new folder.
        try
        {
            for (string? folder = box.Text.Trim().Length > 0 ? Path.GetFullPath(box.Text.Trim()) : null; folder != null; folder = Path.GetDirectoryName(folder))
                if (Directory.Exists(folder)) { dialog.InitialDirectory = folder; break; }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { }
        if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
    }

    private async Task StartAsync()
    {
        if (running != null || !Ready) return;
        using CancellationTokenSource cancellation = new(); running = cancellation;
        Busy(true); error.Text = ""; progressText.Text = "Checking the folders…";
        SourceReconstructionReport? report = null;
        try { report = await initialize(retail.Text.Trim(), project.Text.Trim(), new Progress<string>(text => { if (running == cancellation && !cancellation.IsCancellationRequested) progressText.Text = text; }), cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        // A refusal (for example a folder that is not the original game files) stays in the dialog so the folders can be changed.
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { error.Text = ex.Message; }
        finally { running = null; }
        // Cancel came after the work's last check: the project is written, but it is not opened (as source_reconstruct leaves
        // a project cancelled while it opens), and the dialog says where it is.
        if (report != null && cancellation.IsCancellationRequested)
            error.Text = $"Canceled once the project was written: it stays in {report.Project} and was not opened. Open it from the welcome screen, or delete the folder.";
        else if (report != null) { Report = report; DialogResult = true; return; }
        // Closing waits for the work to stop; a refusal it ends with (such as a folder it could not remove) stays visible.
        if (closeWhenStopped && error.Text.Length == 0) { Close(); return; }
        closeWhenStopped = false;
        Busy(false); progressText.Text = "";
    }
    private void Stop()
    {
        if (running == null) { Close(); return; }
        running.Cancel(); closeWhenStopped = true; progressText.Text = "Canceling…"; cancel.IsEnabled = false;
    }
    private void Busy(bool busy)
    {
        retail.IsReadOnly = project.IsReadOnly = busy;
        browseRetail.IsEnabled = browseProject.IsEnabled = !busy;
        start.IsEnabled = !busy && Ready; cancel.IsEnabled = true;
        progressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}
