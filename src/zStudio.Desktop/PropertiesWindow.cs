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
using zStudio.Core;

namespace zStudio.Desktop;

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
    private string label = "";
    private bool closingResolved, resolvingClose, hasBeenShown;
    public DocumentModel? Document { get; private set; }
    public PickupPropertiesEditor? PickupFields { get; private set; }
    internal SourcePropertiesEditor? SourceFields { get; private set; }
    public bool HasPendingDrafts => Editors.Any(e => e.HasPendingDrafts);
    /// <summary>Pending input other than drafts being committed (an edit a draft's commit runs may proceed).</summary>
    public bool HasUncommittedDrafts => Editors.Any(e => e.HasUncommittedDrafts);
    private IEnumerable<FieldEditor> Editors => new FieldEditor?[] { PickupFields, SourceFields }.OfType<FieldEditor>();
    public Func<DocumentModel, Task<bool>>? SaveRequested { get; set; }
    public Action<DocumentModel, bool>? UndoRequested { get; set; }
    public JsonObject? CurrentJson => PickupFields?.Json ?? SourceFields?.Json ?? snapshot;

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
        PreviewKeyDown += OnKey; PreviewKeyUp += (_, e) => MainWindow.PresentationKeyUp(e);
        Loaded += (_, _) => hasBeenShown = true;
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
        FontSize = 13;
        Resources["WorkspaceControlPadding"] = new Thickness(8, 4, 8, 4);
    }
    public bool SetReadOnly(DocumentModel document, string title, JsonObject json)
    {
        if (!BeginTarget(document)) return false;
        label = title; snapshot = (JsonObject)json.DeepClone();
        ReadOnlyPropertySheet sheet = new(); sheet.Show(snapshot, false); body.Content = sheet; Refresh(); return true;
    }
    internal bool SetAsset(DocumentModel document, AssetRecord asset, JsonObject json)
    {
        return SetReadOnly(document, $"{asset.Name} · {asset.Kind} #{asset.Index}", json);
    }
    internal void MarkAiSnapshotStale()
    {
        const string message = "Source resources changed. Reopen this AI node to inspect the current graph.";
        if (snapshot?["ai_snapshot"] == null || snapshot["snapshot_status"]?.GetValue<string>() == message) return;
        snapshot["snapshot_status"] = message;
        if (body.Content is ReadOnlyPropertySheet sheet) sheet.Show(snapshot, false);
        Refresh();
    }
    /// <summary>A source map's pickup, whose position edits its sources (<paramref name="sourceMove"/>).</summary>
    public bool SetPickup(DocumentModel document, MissionPickupSource source, string title, JsonObject json, Func<MissionPickupSource, System.Numerics.Vector3, Task> sourceMove)
    {
        if (!BeginTarget(document)) return false;
        label = title; PickupFields = new(document, source, json, sourceMove);
        PickupFields.Changed += Refresh; body.Content = PickupFields; Refresh(); return true;
    }
    internal bool SetSourceObject(DocumentModel document, SourcePropertiesEditor fields)
    {
        if (!BeginTarget(document)) { fields.Dispose(); return false; }
        label = fields.Title; SourceFields = fields; fields.Changed += Refresh; body.Content = fields; Refresh(); return true;
    }
    private bool BeginTarget(DocumentModel document)
    {
        if (!ResolvePendingDrafts() || document.IsDisposed) return false;
        Detach(); Document = document;
        Retargeted?.Invoke();
        document.Disposing += DocumentDisposing; document.PropertyChanged += DocumentChanged;
        return true;
    }
    private void Detach()
    {
        if (Document is { } doc)
        {
            doc.Disposing -= DocumentDisposing; doc.PropertyChanged -= DocumentChanged;
        }
        PickupFields?.Dispose(); SourceFields?.Dispose(); SourceFields = null;
        PickupFields = null; Document = null; snapshot = null; body.Content = null;
    }
    private void DocumentDisposing() => CloseResolved();
    private void DocumentChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    public bool ResolvePendingDrafts() => PickupFields?.ResolvePendingDrafts() != false && SourceFields?.ResolvePendingDrafts() != false;
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
        string target = label;
        Title = "Properties — " + Path.GetFileName(doc.Path) + " — " + target;
        heading.Text = path + " → " + target;
        heading.ToolTip = doc.Path + " → " + target;
        notice.Text = doc.IsStale ? "The source file changed on disk. These properties belong to the open document; reload to read the changed source."
            : doc.SourceWorld != null && body.Content is not ReadOnlyPropertySheet ? "Edits update the source project; Ctrl+S saves it to disk." : "Stored properties of the explicitly opened item (read only).";
        undo.IsEnabled = doc.SourceWorld?.Workspace.CanUndo == true;
        redo.IsEnabled = doc.SourceWorld?.Workspace.CanRedo == true;
        undo.Visibility = redo.Visibility = doc.SourceWorld != null ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void RunUndo(bool isRedo)
    { if (Document is { } doc && await ResolvePendingDraftsAsync()) UndoRequested?.Invoke(doc, isRedo); }
    private async Task SaveAsync()
    {
        if (Document is not { } doc || !await ResolvePendingDraftsAsync() || SaveRequested == null) return;
        IsEnabled = false;
        try { await SaveRequested(doc); }
        finally { IsEnabled = true; Refresh(); }
    }
    private async void OnKey(object sender, KeyEventArgs e)
    {
        if (Owner is MainWindow main && main.PresentationKey(e)) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.S) { e.Handled = true; await SaveAsync(); }
        else if (ctrl && e.Key is Key.Z or Key.Y && e.OriginalSource is not TextBoxBase)
        { e.Handled = true; RunUndo(e.Key == Key.Y); }
        // Escape belongs to field drafts, not to window dismissal.
    }
    /// <summary>The window was dismissed (X, Alt+F4, properties_close), rather than closed by its document's replacement.</summary>
    internal bool ClosedByUser { get; private set; }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // A close whose field drafts are being resolved asks nothing more until that finishes.
        if (resolvingClose && !closingResolved) { e.Cancel = true; return; }
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
        // A Properties window can be pinned to a document without ever being shown.
        // Its uninitialized HWND coordinates must not replace the last useful popup bounds.
        if (!hasBeenShown) return;
        Rect bounds = WindowState == WindowState.Normal ? new(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        var saved = preferences.Settings.GetWorkspace().PropertiesWindow;
        saved.Left = bounds.Left; saved.Top = bounds.Top; saved.Width = bounds.Width; saved.Height = bounds.Height; saved.Normalize();
        try { preferences.Settings.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
