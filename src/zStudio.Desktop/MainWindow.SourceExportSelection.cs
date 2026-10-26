using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    internal Action<CancellationToken>? SourceExportPlanning { get; set; }

    private async Task<SourceExportSelection> LoadExportSelectionAsync(CancellationToken token)
    {
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project first.");
        long generation = ViewModel.WorkspaceGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ViewModel.WorkspaceToken);
        string? name = await SourceProfileForAsync(root, cancellation.Token);
        var choice = sourceProfileChoice;
        var profile = await Task.Run(() => BuildProfiles.Find(root, name, token: cancellation.Token), cancellation.Token);
        var plan = await Task.Run(() =>
        {
            SourceExportPlanning?.Invoke(cancellation.Token);
            return SourceBuilder.Plan(root, null, profile, token: cancellation.Token);
        }, cancellation.Token);
        bool Current() => !shutdownToken.IsCancellationRequested && generation == ViewModel.WorkspaceGeneration && root == SourceProjectRoot && choice == sourceProfileChoice;
        cancellation.Token.ThrowIfCancellationRequested();
        if (!Current()) throw new StudioCommandException("context_changed", "The project or build profile changed; choose the export again.");
        return new(plan, ViewModel.Settings, profile.Name, Current);
    }

    internal SourceExportSelectionDialog CreateExportSelectionDialog() => new(this, LoadExportSelectionAsync, model =>
    {
        try { model.Remember(ViewModel.Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { ViewModel.AddProblem("Could not remember the export selection: " + Bounded(ex.Message)); }
    });

    private async void ExportSourceFilesClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        var dialog = CreateExportSelectionDialog();
        if (dialog.ShowDialog() != true || dialog.Selection is not { } selection) return;
        if (!selection.IsCurrent()) throw new StudioCommandException("context_changed", "The project or build profile changed; choose the export again.");
        await ExportSourceInteractiveAsync(selection.SelectedPaths, selection);
    });

    private void RegisterSourceExportSelectionCommands(StudioCommands registry)
    {
        RegisterJob(registry, "source_export_selection", "Inspect or remember the Export ZBD files checkbox selection. Choices are shared across projects and restarts by relative output path; new paths start selected. This command never exports files. Use source_export with the selected output paths to export.",
            [P("action", "string", "Get choices, select files, or clear files; default get.", false, "get", "select", "clear"),
             new("outputs", "array", "Exact output paths to select or clear; omitted applies to every output in the current plan.", Items: new("", "string", "Relative game file path."), MinItems: 1, MaxItems: 256),
             .. PageParameters], true, async (args, token) =>
            {
                string action = Text(args, "action", "get");
                var model = await LoadExportSelectionAsync(token);
                if (action != "get")
                {
                    if (action is not ("select" or "clear")) throw new StudioCommandException("invalid_argument", "Action is get, select, or clear.");
                    var paths = OutputArguments(args);
                    if (paths == null) model.SetAll(action == "select");
                    else
                    {
                        var byPath = model.Files.ToDictionary(f => SourceExportSelection.Normalize(f.Path), StringComparer.OrdinalIgnoreCase);
                        var rows = paths.Select(path => byPath.GetValueOrDefault(SourceExportSelection.Normalize(path)) ?? throw new StudioCommandException("invalid_argument", $"This source project cannot build {Bounded(path, 260)}.")).ToArray();
                        foreach (var row in rows) row.Set(action == "select"); model.Refresh();
                    }
                    token.ThrowIfCancellationRequested();
                    if (!model.IsCurrent()) throw new StudioCommandException("context_changed", "The project or profile changed.");
                }
                else if (args.ContainsKey("outputs")) throw new StudioCommandException("invalid_argument", "Outputs applies only to select or clear.");
                var result = Result(new { profile = model.Profile, selectedCount = model.SelectedCount, total = model.Files.Count,
                    outputs = Page(model.Files, args, f => f.Path, f => new { path = f.Path, group = f.Group, selected = f.IsChecked == true }, maximumRowBytes: f => 256 + 12L * f.Path.Length).Data });
                if (action != "get")
                {
                    // A dialog may have opened while the plan was loading; its snapshot must remain authoritative.
                    RequireAutomationMutationAvailable();
                    model.Remember(ViewModel.Settings); CommitRunningJob();
                }
                return result;
            });
    }
}
