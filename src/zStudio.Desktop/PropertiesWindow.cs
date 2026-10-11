using System.ComponentModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;

namespace Recoil.Zbd.Desktop;

/// <summary>A single explicitly targeted inspector; selection and preview lifetime never own its content.</summary>
public sealed class PropertiesWindow : Window
{
    private readonly MainViewModel preferences;
    private readonly ContentControl body = new();
    private readonly TextBlock heading = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
    private readonly Button undo = HistoryButton("Undo", "\uE7A7", "Ctrl+Z");
    private readonly Button redo = HistoryButton("Redo", "\uE7A6", "Ctrl+Y");
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new(12, 0, 12, 8), Opacity = .7 };
    private JsonObject? snapshot;
    private AssetId? readOnlyAsset;
    private long assetRefreshGeneration;
    internal Task AssetRefreshWork { get; private set; } = Task.CompletedTask;
    private string label = "";
    private bool closingResolved, resolvingClose;
    public DocumentModel? Document { get; private set; }
    public AnimationPropertiesEditor? AnimationFields { get; private set; }
    public PickupPropertiesEditor? PickupFields { get; private set; }
    public ResourcePropertiesEditor? ResourceFields { get; private set; }
    public ScriptPropertiesEditor? ScriptFields { get; private set; }
    internal SourcePropertiesEditor? SourceFields { get; private set; }
    public bool HasPendingDrafts => Editors.Any(e => e.HasPendingDrafts);
    /// <summary>Pending input other than drafts being committed (an edit a draft's commit runs may proceed).</summary>
    public bool HasUncommittedDrafts => Editors.Any(e => e.HasUncommittedDrafts);
    private IEnumerable<FieldEditor> Editors => new FieldEditor?[] { AnimationFields, PickupFields, ResourceFields, ScriptFields, SourceFields }.OfType<FieldEditor>();
    public Func<DocumentModel, bool, Task<bool>>? SaveRequested { get; set; }
    public Action<DocumentModel, bool>? UndoRequested { get; set; }
    public Action<DocumentModel>? Editing { get; set; }
    public JsonObject? CurrentJson => ScriptFields?.Json ?? ResourceFields?.Json ?? AnimationFields?.Json ?? PickupFields?.Json ?? SourceFields?.Json ?? snapshot;

    public PropertiesWindow(Window owner, MainViewModel preferences)
    {
        this.preferences = preferences;
        Owner = owner; ShowInTaskbar = false; Icon = owner.Icon; Title = "Properties";
        MinWidth = 400; MinHeight = 300;
        var bounds = preferences.Settings.GetWorkspace().PropertiesWindow;
        bounds.Normalize(); Width = bounds.Width; Height = bounds.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (bounds.Left is double x && bounds.Top is double y)
        {
            // Reject disconnected-monitor positions; constrain oversized windows to the desktop.
            double left = SystemParameters.VirtualScreenLeft, top = SystemParameters.VirtualScreenTop;
            if (x >= left && y >= top && x + Width <= left + SystemParameters.VirtualScreenWidth && y + Height <= top + SystemParameters.VirtualScreenHeight)
            { WindowStartupLocation = WindowStartupLocation.Manual; Left = x; Top = y; }
        }
        Width = Math.Min(Width, SystemParameters.WorkArea.Width); Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        DockPanel panel = new(); Content = panel;
        Grid header = new() { Margin = new(12, 8, 12, 8) };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(heading);
        StackPanel history = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        history.Children.Add(undo); history.Children.Add(redo); Grid.SetColumn(history, 1); header.Children.Add(history);
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        DockPanel.SetDock(notice, Dock.Top); panel.Children.Add(notice);
        panel.Children.Add(body);
        undo.Click += (_, _) => RunUndo(false); redo.Click += (_, _) => RunUndo(true);
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Closed += (_, _) => { RememberBounds(); Detach(); };
        ApplyAppearance();
    }

    private static Button HistoryButton(string name, string glyph, string shortcut)
    {
        Button button = new()
        {
            Width = 32, Height = 32, Padding = new(0), Margin = new(2, 0, 0, 0),
            ToolTip = $"{name} in this document ({shortcut})",
            Content = new TextBlock { FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16, Text = glyph }
        };
        Style style = new(typeof(Button), Application.Current.TryFindResource(typeof(Button)) as Style);
        Trigger disabled = new() { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .4)); style.Triggers.Add(disabled); button.Style = style;
        AutomationProperties.SetName(button, name); return button;
    }

    public void ApplyAppearance()
    {
        FontSize = preferences.Settings.GetWorkspace().Density == "Comfortable" ? 15 : 13;
        Resources["WorkspaceControlPadding"] = preferences.Settings.GetWorkspace().Density == "Comfortable" ? new Thickness(12, 8, 12, 8) : new Thickness(8, 4, 8, 4);
    }
    public bool SetAnimation(DocumentModel document, int entry, Guid sequence, Guid ev)
    {
        if (!BeginTarget(document)) return false;
        AnimationFields = new(document, entry, sequence, ev, preferences);
        AnimationFields.Changed += Refresh;
        AnimationFields.Editing += () => Editing?.Invoke(document);
        body.Content = AnimationFields; Refresh(); return true;
    }
    public bool SetReadOnly(DocumentModel document, string title, JsonObject json)
    {
        if (!BeginTarget(document)) return false;
        label = title; snapshot = (JsonObject)json.DeepClone();
        ReadOnlyPropertySheet sheet = new(); sheet.Show(snapshot, false); body.Content = sheet; Refresh(); return true;
    }
    internal bool SetAsset(DocumentModel document, AssetRecord asset, JsonObject json)
    {
        if (!SetReadOnly(document, $"{asset.Name} · {asset.Kind} #{asset.Index}", json)) return false;
        readOnlyAsset = asset.Id; return true;
    }
    internal void MarkAiSnapshotStale()
    {
        const string message = "Source resources changed. Reopen this AI node to inspect the current graph.";
        if (snapshot?["ai_snapshot"] == null || snapshot["snapshot_status"]?.GetValue<string>() == message) return;
        snapshot["snapshot_status"] = message;
        if (body.Content is ReadOnlyPropertySheet sheet) sheet.Show(snapshot, false);
        Refresh();
    }
    private void ModelAssetsChanged() => AssetRefreshWork = RefreshAssetAsync();
    private async Task RefreshAssetAsync()
    {
        if (Document is not { } doc || readOnlyAsset is not { } id) return;
        long generation = ++assetRefreshGeneration, revision = doc.Revision;
        var token = doc.Lifetime.Token;
        var current = doc.PreviewDocument;
        var asset = current.Assets.SingleOrDefault(a => a.Id == id);
        try
        {
            var json = asset == null ? new JsonObject { ["unavailable"] = "This record is absent from the current edit. Redo can restore it.", ["kind"] = id.Kind.ToString(), ["index"] = id.Index }
                : await Task.Run(() => ExportService.AssetJson(current, asset, token, boundedZrd: true), token);
            if (generation != assetRefreshGeneration || Document != doc || doc.IsDisposed || doc.Revision != revision || readOnlyAsset != id) return;
            snapshot = json; ReadOnlyPropertySheet sheet = new(); sheet.Show(json, false); body.Content = sheet; Refresh();
        }
        catch (OperationCanceledException) when (doc.IsDisposed) { }
    }
    public bool SetPickup(DocumentModel document, MissionPickupSource source, string title, JsonObject json, Func<MissionPickupSource, System.Numerics.Vector3, Task>? sourceMove = null)
    {
        if (!BeginTarget(document)) return false;
        label = title; PickupFields = new(document, source, title, json, sourceMove);
        PickupFields.Changed += Refresh; body.Content = PickupFields; Refresh(); return true;
    }
    internal bool SetSourceObject(DocumentModel document, SourcePropertiesEditor fields)
    {
        if (!BeginTarget(document)) { fields.Dispose(); return false; }
        label = fields.Title; SourceFields = fields; fields.Changed += Refresh; body.Content = fields; Refresh(); return true;
    }
    public bool SetResource(DocumentModel document, ResourcePropertiesEditor fields)
    {
        if (!BeginTarget(document)) return false;
        ResourceFields = fields; fields.Changed += Refresh; body.Content = fields; Refresh(); return true;
    }
    public bool SetScript(DocumentModel document, ScriptPropertiesEditor fields)
    {
        if (!BeginTarget(document)) return false;
        ScriptFields = fields; fields.Changed += Refresh; body.Content = fields; Refresh(); return true;
    }
    private bool BeginTarget(DocumentModel document)
    {
        if (!ResolvePendingDrafts() || document.IsDisposed) return false;
        Detach(); Document = document;
        Retargeted?.Invoke();
        document.Disposing += DocumentDisposing; document.PropertyChanged += DocumentChanged;
        if (document.AnimationEdits is { } edits) edits.Changed += Refresh;
        document.PickupEditsChanged += Refresh;
        document.ModelEditsChanged += ModelAssetsChanged;
        document.ContentEditsChanged += ModelAssetsChanged;
        return true;
    }
    private void Detach()
    {
        if (Document is { } doc)
        {
            doc.Disposing -= DocumentDisposing; doc.PropertyChanged -= DocumentChanged;
            if (doc.AnimationEdits is { } edits) edits.Changed -= Refresh;
            doc.PickupEditsChanged -= Refresh;
            doc.ModelEditsChanged -= ModelAssetsChanged;
            doc.ContentEditsChanged -= ModelAssetsChanged;
        }
        AnimationFields?.Dispose(); PickupFields?.Dispose(); ResourceFields?.Dispose(); ScriptFields?.Dispose(); SourceFields?.Dispose(); ScriptFields = null; SourceFields = null;
        AnimationFields = null; PickupFields = null; ResourceFields = null; Document = null; snapshot = null; body.Content = null; readOnlyAsset = null; ++assetRefreshGeneration;
    }
    private void DocumentDisposing() => CloseResolved();
    private void DocumentChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    public bool ResolvePendingDrafts() => AnimationFields?.ResolvePendingDrafts() != false && PickupFields?.ResolvePendingDrafts() != false && ResourceFields?.ResolvePendingDrafts() != false && ScriptFields?.ResolvePendingDrafts() != false && SourceFields?.ResolvePendingDrafts() != false;
    public async Task<bool> ResolvePendingDraftsAsync()
    {
        // Each editor commits its drafts (awaiting asynchronous ones, such as a source world's pickup position) or asks.
        foreach (var editor in Editors.ToArray())
            if (!await editor.ResolvePendingDraftsAsync()) return false;
        return true;
    }
    private void Refresh()
    {
        if (Document is not { } doc) return;
        string path = preferences.RootPath.Length > 0 ? Path.GetRelativePath(preferences.RootPath, doc.Path) : doc.Path;
        string target = ScriptFields?.TargetLabel ?? ResourceFields?.TargetLabel ?? AnimationFields?.TargetLabel ?? label;
        Title = "Properties — " + Path.GetFileName(doc.Path) + " — " + target;
        heading.Text = path + " → " + target;
        heading.ToolTip = doc.Path + " → " + target;
        notice.Text = doc.IsStale ? "The source file changed on disk. These properties belong to the open document; reload to read the changed source."
            : doc.AnimationEdits != null || PickupFields != null || ResourceFields != null || ScriptFields != null ? "Edits update this document; Ctrl+S saves it to disk." : "Stored properties of the explicitly opened item.";
        undo.IsEnabled = doc.SourceWorld is { } undoWorld ? undoWorld.Workspace.CanUndo
            : doc.AnimationEdits?.CanUndo == true || doc.CanUndoScene || doc.ResourceEdits?.CanUndo == true || doc.ContentEdits?.CanUndo == true;
        redo.IsEnabled = doc.SourceWorld is { } redoWorld ? redoWorld.Workspace.CanRedo
            : doc.AnimationEdits?.CanRedo == true || doc.CanRedoScene || doc.ResourceEdits?.CanRedo == true || doc.ContentEdits?.CanRedo == true;
        undo.Visibility = redo.Visibility = doc.SourceWorld != null || doc.AnimationEdits != null || doc.PickupEdits != null || doc.ModelEdits != null || doc.ResourceEdits != null || doc.ContentEdits != null ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void RunUndo(bool isRedo)
    { if (Document is { } doc && await ResolvePendingDraftsAsync()) UndoRequested?.Invoke(doc, isRedo); }
    private async Task SaveAsync(bool saveAs)
    {
        if (Document is not { } doc || !await ResolvePendingDraftsAsync() || SaveRequested == null) return;
        IsEnabled = false;
        try { await SaveRequested(doc, saveAs); }
        finally { IsEnabled = true; Refresh(); }
    }
    private async void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.S) { e.Handled = true; await SaveAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0); }
        else if (ctrl && e.Key is Key.Z or Key.Y && e.OriginalSource is not TextBoxBase)
        { e.Handled = true; RunUndo(e.Key == Key.Y); }
        // Escape belongs to field drafts, not to window dismissal.
    }
    /// <summary>The window was dismissed (X, Alt+F4, properties_close), rather than closed by its document's replacement.</summary>
    internal bool ClosedByUser { get; private set; }
    /// <summary>
    /// Decides drafts the owner keeps for this window's content (a map zone draft, which closing discards) before any
    /// close, explicit ones included; false keeps the window open.
    /// </summary>
    internal Func<DocumentModel?, bool>? ResolveClosingDrafts { get; set; }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // A close whose field drafts are being resolved asks nothing more until that finishes.
        if (resolvingClose && !closingResolved) { e.Cancel = true; return; }
        // Declining keeps the window as it was: it still follows edits and resolves its input on the next close.
        if (ResolveClosingDrafts?.Invoke(Document) == false) { e.Cancel = true; closingResolved = ClosedByUser = false; return; }
        if (!closingResolved) ClosedByUser = true;
        if (closingResolved || !HasPendingDrafts) return;
        e.Cancel = true;
        resolvingClose = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(async () =>
        {
            try { if (await ResolvePendingDraftsAsync()) CloseResolved(); else ClosedByUser = false; }
            finally { resolvingClose = false; }
        }));
    }
    internal void CloseResolved() { closingResolved = true; Close(); }
    /// <summary>Whether the content takes no input (a source world rebuilding); set by the owner when that state or the target changes.</summary>
    internal bool InputBlocked { set => body.IsEnabled = !value; }
    /// <summary>Raised when the window shows another document.</summary>
    internal event Action? Retargeted;
    /// <summary>Closes by an explicit request (MCP properties_close): an edit in flight does not reopen it.</summary>
    internal void Dismiss() { ClosedByUser = true; CloseResolved(); }
    private void RememberBounds()
    {
        Rect bounds = WindowState == WindowState.Normal ? new(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        var saved = preferences.Settings.GetWorkspace().PropertiesWindow;
        saved.Left = bounds.Left; saved.Top = bounds.Top; saved.Width = bounds.Width; saved.Height = bounds.Height; saved.Normalize();
        try { preferences.Settings.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
