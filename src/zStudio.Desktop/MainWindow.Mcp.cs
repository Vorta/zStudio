using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Mcp;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private LocalMcpHost? mcpHost;
    private Window? mcpWindow;
    private readonly ObservableCollection<string> mcpActivity = [];
    private StudioCommands? studioCommands;
    private readonly SemaphoreSlim automationGate = new(1);
    private Task? mcpStopTask;
    // Only an accepted source-world replacement can suppress a direct request's late cancellation.
    // Async-local ownership keeps unrelated GUI edits and concurrent read commands out of this result.
    private sealed class DirectSourcePublication(CancellationToken token)
    {
        internal CancellationToken Token { get; } = token;
        internal DocumentModel? Document;
    }
    private readonly AsyncLocal<DirectSourcePublication?> directSourcePublication = new();
    private void ThrowIfSourceRequestCanceled(CancellationToken token)
    {
        if (directSourcePublication.Value?.Document == null) token.ThrowIfCancellationRequested();
    }
    internal StudioCommands Commands => studioCommands ??= CreateCommands();
    internal void InitializeMcp()
    {
        if (!ViewModel.Settings.McpEnabled || mcpHost != null || stoppingAutomation || shutdownToken.IsCancellationRequested) return;
        try
        {
            mcpHost = new(Commands, typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown");
            mcpHost.Activity += message => Dispatcher.BeginInvoke(() => { mcpActivity.Add($"{DateTime.Now:T} {message}"); while (mcpActivity.Count > 200) mcpActivity.RemoveAt(0); });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ViewModel.AddProblem("MCP: " + ex.Message); }
    }
    internal Task StopMcpAsync()
    {
        if (mcpStopTask is { IsCompleted: false }) return mcpStopTask;
        stoppingAutomation = true;
        return mcpStopTask = StopMcpCoreAsync();
    }
    private async Task StopMcpCoreAsync()
    {
        var host = mcpHost; mcpHost = null;
        try
        {
            foreach (var job in automationOperations.Values) job.Cancellation.Cancel();
            if (host != null) await host.DisposeAsync();
            await Task.WhenAll(automationOperations.Values.Select(j => j.Work));
        }
        finally { stoppingAutomation = false; }
    }
    private async void McpIntegrationClick(object sender, RoutedEventArgs e)
    {
        if (stoppingAutomation) await StopMcpAsync();
        if (shutdownToken.IsCancellationRequested) return;
        if (mcpWindow != null) { mcpWindow.Activate(); return; }
        StackPanel panel = new() { Margin = new(18) };
        CheckBox enabled = new() { Content = "Enable local MCP access", IsChecked = ViewModel.Settings.McpEnabled, Margin = new(0,0,0,12) };
        panel.Children.Add(enabled);
        TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0,0,0,12) }; panel.Children.Add(status);
        void Refresh() => status.Text = mcpHost == null ? "Disabled. Enable MCP before connecting an agent." : $"Ready · {mcpHost.ConnectionCount} clients\nInstance: {mcpHost.Instance.Id}\nAgents share this workspace and its undo history. Discovery stays in the background; the first workspace request can open zStudio. No network port is used.";
        enabled.Click += async (_, _) =>
        {
            if (shutdownToken.IsCancellationRequested || !enabled.IsEnabled) return;
            enabled.IsEnabled = false;
            try
            {
                ViewModel.Settings.McpEnabled = enabled.IsChecked == true; ViewModel.Settings.Save();
                if (enabled.IsChecked == true) InitializeMcp(); else await StopMcpAsync();
            }
            finally { enabled.IsEnabled = !shutdownToken.IsCancellationRequested; Refresh(); }
        };
        TextBox config = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 95, Text = JsonSerializer.Serialize(new { mcpServers = new { zStudio = new { command = Environment.ProcessPath, args = new[] { "--mcp" } } } }, new JsonSerializerOptions { WriteIndented = true }) }; panel.Children.Add(config);
        WrapPanel buttons = new(); panel.Children.Add(buttons);
        foreach (var (label, action) in new (string, Action)[] { ("Copy configuration", () => Clipboard.SetText(config.Text)), ("Disconnect clients", () => mcpHost?.DisconnectClients()), ("Refresh", Refresh) })
        { Button b = new() { Content = label, Margin = new(0,8,8,8) }; b.Click += (_, _) => { action(); Refresh(); }; buttons.Children.Add(b); }
        panel.Children.Add(new TextBlock { Text = "Recent MCP activity (last 200 entries)" });
        panel.Children.Add(new ListBox { ItemsSource = mcpActivity, Height = 220 }); Refresh();
        mcpWindow = new() { Owner = this, Title = "MCP integration", Width = 670, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        mcpWindow.Closed += (_, _) => mcpWindow = null; mcpWindow.Show();
    }
    private static StudioResult Result(object value)
    {
        var options = new JsonSerializerOptions { IncludeFields = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        JsonNode data = value as JsonNode ?? JsonSerializer.SerializeToNode(value, options)!;
        return new(data is JsonObject ? data : new JsonObject { ["items"] = data });
    }
    private static StudioParameter P(string name, string type, string description, bool required = false, params string[] choices)
        => new(name, type, description, required, choices.Length == 0 ? null : choices,
            Minimum: type == "integer" ? int.MinValue : null, Maximum: type == "integer" ? int.MaxValue : null);
    private static StudioParameter DocumentParameter => P("document", "string", "Document lifetime ID from zstudio_state.", true);
    private static StudioParameter RevisionParameter => new("revision", "integer", "Expected current document revision. Read before editing.", true, Minimum: 0, Maximum: long.MaxValue);
    private static string Text(JsonObject a, string key, string fallback = "") => a[key]?.GetValue<string>() ?? fallback;
    /// <summary>A file or folder argument (empty when absent): only a full path, since a relative one would resolve against zStudio's own folder, which a new build replaces.</summary>
    private static string FullPath(JsonObject a, string key) => FullPath(Text(a, key), key);
    private static string FullPath(string path, string name) => path.Length == 0 || Path.IsPathFullyQualified(path) ? path
        : throw new StudioCommandException("invalid_argument", $"Give {name} as a full path; a relative one would resolve against zStudio's own folder.");
    private static int Int(JsonObject a, string key, int fallback = 0) => a[key]?.GetValue<int>() ?? fallback;
    private static double Number(JsonObject a, string key, double fallback = 0) => a[key]?.GetValue<double>() ?? fallback;
    private static bool Flag(JsonObject a, string key, bool fallback = false) => a[key]?.GetValue<bool>() ?? fallback;
    private DocumentModel TargetDocument(JsonObject args, bool write = false)
    {
        var doc = ViewModel.Documents.FirstOrDefault(d => d.SessionId.ToString() == Text(args, "document") && !d.IsDisposed) ?? throw new StudioCommandException("stale_document", "Document is no longer open. Read zstudio_state.");
        if (write && args["revision"]?.GetValue<long>() != doc.Revision) throw new StudioCommandException("revision_conflict", "Document changed. Read its current revision before retrying.");
        if (write) RequireNoDrafts(doc);
        return doc;
    }
    /// <remarks>With <paramref name="committing"/>, drafts whose commit is running (and called this) do not count.</remarks>
    private void RequireNoDrafts(DocumentModel? doc = null, bool committing = false, SceneInspectionCard? committingCard = null, ZoneDraft? committingZones = null)
    {
        if (HasZoneDraft && zoneDraft != committingZones && (doc == null || zoneDraft!.Document == doc))
            throw new StudioCommandException("pending_drafts", "Apply or cancel the map zone draft before continuing.");
        // Resource/content edits elsewhere refresh the shown preview, whose scene-card draft would otherwise need a modal decision mid-request.
        if (HasInspectionDraft && inspectionDraft != committingCard && (doc == null || inspectionDraft!.DraftDocument == doc || doc != shownDocument && inspectionDraft.DraftDocument == shownDocument && (doc.ResourceEdits != null || doc.ContentEdits != null)))
            throw new StudioCommandException("pending_drafts", "Resolve the scene card draft explicitly before continuing.");
        if ((doc == null || propertiesWindow?.Document == doc) && (committing ? propertiesWindow?.HasUncommittedDrafts : propertiesWindow?.HasPendingDrafts) == true || (doc == null || shownDocument == doc) && animation?.HasAutomationDrafts == true)
            throw new StudioCommandException("pending_drafts", "Unfinished GUI input is retained. Inspect and explicitly resolve drafts before continuing.");
        if (scene?.IsPickupDragging == true) throw new StudioCommandException("busy", "A pickup drag is in progress.");
    }
    private static object DocumentState(DocumentModel d) => DocumentState(d, includeContentDetails: true);
    internal static object DocumentState(DocumentModel d, bool includeContentDetails) => new { id = d.SessionId, d.Path, d.Revision, d.IsDirty, d.IsStale, d.PickupsLocked, game = d.PreviewDocument.Game.ToString(), format = d.Document.Probe, assetCount = d.Assets.Count, selected = d.SelectedAsset?.Record.Id, d.LastSavedCopy, sourceWorld = SourceWorldState(d),
        contentEdits = ContentState(d, includeContentDetails) };
    private static object? ContentState(DocumentModel d, bool includeDetails)
    {
        if (d.ContentEdits == null) return null;
        var files = FileResultPreview.Content(d.ContentEdits, includeDetails: includeDetails);
        return new { d.IsContentMirror, files = files.Values, fileCount = files.Count, filesTruncated = files.Truncated };
    }
    private void Register(StudioCommands registry, string name, string description, bool mutates, StudioParameter[] parameters, Func<JsonObject, CancellationToken, Task<StudioResult>> action)
    {
        registry.Add(new("zstudio_" + name, description, mutates, parameters, async (args, token) =>
        {
            if (mutates) await automationGate.WaitAsync(token);
            try
            {
                return await Dispatcher.InvokeAsync(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    automationRequest.Value = true;
                    if (mutates) RequireAutomationMutationAvailable();
                    using var scope = PreviewOperation.Begin(token);
                    var previous = directSourcePublication.Value;
                    directSourcePublication.Value = new(token);
                    try
                    {
                        var result = await action(args, token);
                        ThrowIfSourceRequestCanceled(token);
                        return result;
                    }
                    finally { directSourcePublication.Value = previous; }
                }, System.Windows.Threading.DispatcherPriority.Normal, token).Task.Unwrap();
            }
            finally { if (mutates) automationGate.Release(); }
        }));
    }
    private void Register(StudioCommands registry, string name, string description, bool mutates, StudioParameter[] parameters, Func<JsonObject, StudioResult> action)
        => Register(registry, name, description, mutates, parameters, (a, _) => Task.FromResult(action(a)));
    private StudioCommands CreateCommands()
    {
        StudioCommands registry = new();
        Register(registry, "state", "Read the visible workspace and page document identities/revisions, preserving complete paths. Offset defaults to 0; limit defaults to 64, maximum 64. Documents have a 1 MiB pre-projection metadata allowance, so a page may be shorter; follow nextOffset, with total and offset. A single unsupported oversized row returns too_large. Status is a 512-character preview with statusTruncated. Source document workspace summaries contain counts and undo/redo state; read source_changes for the shared dirty-file and history details. Global state contentEdits summaries return fileCount with empty files and filesTruncated; per-document command results preview at most 64 file paths/targets of 512 characters, with per-row PathTruncated/destinationTruncated.", false,
            [new("offset", "integer", "Zero-based document offset; default 0.", Minimum: 0, Maximum: int.MaxValue), new("limit", "integer", "Document page size 1–64; default 64. Follow nextOffset for shorter metadata-bounded pages.", Minimum: 1, Maximum: 64)], a =>
        {
            var page = StateDocumentPage.Select(ViewModel.Documents, Int(a, "offset"), Int(a, "limit", 64));
            // The root is an identity, so refuse an unsupported envelope rather than clip it.
            if (6L * (ViewModel.RootPath.Length + (long)(shownAsset?.Id.File.Length ?? 0)) > StateDocumentPage.MaximumBytes)
                throw new StudioCommandException("too_large", "The workspace identities exceed the supported state metadata size.");
            return Result(new
            {
                version = typeof(MainWindow).Assembly.GetName().Version?.ToString(), ViewModel.HasRoot, ViewModel.RootPath, Status = ViewModel.Status.Length <= 512 ? ViewModel.Status : Bounded(ViewModel.Status, 511), statusTruncated = ViewModel.Status.Length > 512, ViewModel.IsBusy,
                documents = page.Documents.Select(d => DocumentState(d, includeContentDetails: false)).ToArray(), total = page.Total, offset = page.Offset, nextOffset = page.NextOffset, activeDocument = ViewModel.SelectedDocument?.SessionId,
                preview = previewId, selectedAsset = shownAsset?.Id, animationTime = animation?.CurrentFrame?.Time, animationPlaying = animation?.IsPlaying,
                pendingPropertiesDrafts = propertiesWindow?.HasPendingDrafts == true, pendingPreviewDrafts = animation?.HasAutomationDrafts == true, pendingSceneDrafts = HasInspectionDraft,
                // A map zone draft with targets blocks other writes until source_zone_draft applies or cancels it.
                pendingZoneDrafts = HasZoneDraft
            });
        });
        Register(registry, "capabilities", "Read all capability schemas. Unsupported formats remain read-only; MCP does not add binary patching.", false, [], _ => new(registry.Describe()));
        RegisterWorkspaceCommands(registry);
        RegisterPreviewCommands(registry);
        RegisterAiCommands(registry);
        RegisterValveCommands(registry);
        RegisterInspectionCommands(registry);
        RegisterSceneTreeCommand(registry);
        RegisterEditCommands(registry);
        RegisterModelCommands(registry);
        RegisterResourceCommands(registry);
        RegisterMotionCommands(registry); RegisterMissionCommands(registry); RegisterMechCommands(registry);
        RegisterContentCommands(registry);
        RegisterSourceCommands(registry);
        RegisterSourceWorldCommands(registry);
        RegisterSourceObjectCommands(registry);
        RegisterSourceBlenderCommands(registry);
        RegisterSourceRecoveryCommands(registry); RegisterSourceTerrainCommands(registry); RegisterSourceZoneCommands(registry);
        RegisterWorldCompareCommands(registry);
        return registry;
    }
}
