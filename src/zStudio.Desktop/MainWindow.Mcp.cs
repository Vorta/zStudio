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
    internal StudioCommands Commands => studioCommands ??= CreateCommands();
    internal void InitializeMcp()
    {
        if (!ViewModel.Settings.McpEnabled || mcpHost != null) return;
        try
        {
            mcpHost = new(Commands, typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown");
            mcpHost.Activity += message => Dispatcher.BeginInvoke(() => { mcpActivity.Add($"{DateTime.Now:T} {message}"); while (mcpActivity.Count > 200) mcpActivity.RemoveAt(0); });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ViewModel.AddProblem("MCP: " + ex.Message); }
    }
    private async Task StopMcpAsync()
    {
        stoppingAutomation = true;
        var host = mcpHost; mcpHost = null;
        try
        {
            foreach (var job in automationOperations.Values) job.Cancellation.Cancel();
            if (host != null) await host.DisposeAsync();
            await Task.WhenAll(automationOperations.Values.Select(j => j.Work));
        }
        finally { stoppingAutomation = false; }
    }
    private void McpIntegrationClick(object sender, RoutedEventArgs e)
    {
        if (mcpWindow != null) { mcpWindow.Activate(); return; }
        StackPanel panel = new() { Margin = new(18) };
        CheckBox enabled = new() { Content = "Enable local MCP access", IsChecked = ViewModel.Settings.McpEnabled, Margin = new(0,0,0,12) };
        panel.Children.Add(enabled);
        TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0,0,0,12) }; panel.Children.Add(status);
        void Refresh() => status.Text = mcpHost == null ? "Disabled. Enable MCP before connecting an agent." : $"Ready · {mcpHost.ConnectionCount} clients\nInstance: {mcpHost.Instance.Id}\nAgents share this workspace and its undo history. Discovery stays in the background; the first workspace request can open zStudio. No network port is used.";
        enabled.Click += async (_, _) => { ViewModel.Settings.McpEnabled = enabled.IsChecked == true; ViewModel.Settings.Save(); if (enabled.IsChecked == true) InitializeMcp(); else await StopMcpAsync(); Refresh(); };
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
    private static StudioParameter P(string name, string type, string description, bool required = false, params string[] choices) => new(name, type, description, required, choices.Length == 0 ? null : choices);
    private static StudioParameter DocumentParameter => P("document", "string", "Document lifetime ID from zstudio_state.", true);
    private static StudioParameter RevisionParameter => P("revision", "integer", "Expected current document revision. Read before editing.", true);
    private static string Text(JsonObject a, string key, string fallback = "") => a[key]?.GetValue<string>() ?? fallback;
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
    private void RequireNoDrafts(DocumentModel? doc = null)
    {
        if ((doc == null || propertiesWindow?.Document == doc) && propertiesWindow?.HasPendingDrafts == true || (doc == null || shownDocument == doc) && animation?.HasAutomationDrafts == true)
            throw new StudioCommandException("pending_drafts", "Unfinished GUI input is retained. Inspect and explicitly resolve drafts before continuing.");
        if (scene?.IsPickupDragging == true) throw new StudioCommandException("busy", "A pickup drag is in progress.");
    }
    private static object DocumentState(DocumentModel d) => new { id = d.SessionId, d.Path, d.Revision, d.IsDirty, d.IsStale, d.PickupsLocked, format = d.Document.Probe, assetCount = d.Assets.Count, selected = d.SelectedAsset?.Record.Id, d.LastSavedCopy };
    private void Register(StudioCommands registry, string name, string description, bool mutates, StudioParameter[] parameters, Func<JsonObject, CancellationToken, Task<StudioResult>> action)
    {
        registry.Add(new("zstudio_" + name, description, mutates, parameters, async (args, token) =>
        {
            if (mutates) await automationGate.WaitAsync(token);
            try { return await Dispatcher.InvokeAsync(() => action(args, token)).Task.Unwrap(); }
            finally { if (mutates) automationGate.Release(); }
        }));
    }
    private void Register(StudioCommands registry, string name, string description, bool mutates, StudioParameter[] parameters, Func<JsonObject, StudioResult> action)
        => Register(registry, name, description, mutates, parameters, (a, _) => Task.FromResult(action(a)));
    private StudioCommands CreateCommands()
    {
        StudioCommands registry = new();
        Register(registry, "state", "Read the visible workspace, document identities, revisions and current preview.", false, [], _ => Result(new
        {
            version = typeof(MainWindow).Assembly.GetName().Version?.ToString(), ViewModel.HasRoot, ViewModel.RootPath, ViewModel.Status, ViewModel.IsBusy,
            documents = ViewModel.Documents.Select(DocumentState).ToArray(), activeDocument = ViewModel.SelectedDocument?.SessionId,
            preview = previewId, selectedAsset = shownAsset?.Id, animationTime = animation?.CurrentFrame?.Time, animationPlaying = animation?.IsPlaying,
            pendingPropertiesDrafts = propertiesWindow?.HasPendingDrafts == true, pendingPreviewDrafts = animation?.HasAutomationDrafts == true
        }));
        Register(registry, "capabilities", "Read all capability schemas. Unsupported formats remain read-only; MCP does not add binary patching.", false, [], _ => new(registry.Describe()));
        RegisterWorkspaceCommands(registry);
        RegisterPreviewCommands(registry);
        RegisterEditCommands(registry);
        return registry;
    }
}
