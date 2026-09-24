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
    private string label = "";
    private bool closingResolved, resolvingClose;
    public DocumentModel? Document { get; private set; }
    public AnimationPropertiesEditor? AnimationFields { get; private set; }
    public PickupPropertiesEditor? PickupFields { get; private set; }
    public bool HasPendingDrafts => AnimationFields?.HasPendingDrafts == true || PickupFields?.HasPendingDrafts == true;
    public Func<DocumentModel, bool, Task<bool>>? SaveRequested { get; set; }
    public Action<DocumentModel, bool>? UndoRequested { get; set; }
    public Action<DocumentModel>? Editing { get; set; }
    public JsonObject? CurrentJson => AnimationFields?.Json ?? PickupFields?.Json ?? snapshot;

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
    public bool SetPickup(DocumentModel document, MissionPickupSource source, string title, JsonObject json)
    {
        if (!BeginTarget(document)) return false;
        label = title; PickupFields = new(document, source, title, json);
        PickupFields.Changed += Refresh; body.Content = PickupFields; Refresh(); return true;
    }
    private bool BeginTarget(DocumentModel document)
    {
        if (!ResolvePendingDrafts() || document.IsDisposed) return false;
        Detach(); Document = document;
        document.Disposing += DocumentDisposing; document.PropertyChanged += DocumentChanged;
        if (document.AnimationEdits is { } edits) edits.Changed += Refresh;
        document.PickupEditsChanged += Refresh;
        return true;
    }
    private void Detach()
    {
        if (Document is { } doc)
        {
            doc.Disposing -= DocumentDisposing; doc.PropertyChanged -= DocumentChanged;
            if (doc.AnimationEdits is { } edits) edits.Changed -= Refresh;
            doc.PickupEditsChanged -= Refresh;
        }
        AnimationFields?.Dispose(); PickupFields?.Dispose();
        AnimationFields = null; PickupFields = null; Document = null; snapshot = null; body.Content = null;
    }
    private void DocumentDisposing() => CloseResolved();
    private void DocumentChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    public bool ResolvePendingDrafts() => AnimationFields?.ResolvePendingDrafts() != false && PickupFields?.ResolvePendingDrafts() != false;
    private void Refresh()
    {
        if (Document is not { } doc) return;
        string path = preferences.RootPath.Length > 0 ? Path.GetRelativePath(preferences.RootPath, doc.Path) : doc.Path;
        string target = AnimationFields?.TargetLabel ?? label;
        Title = "Properties — " + Path.GetFileName(doc.Path) + " — " + target;
        heading.Text = path + " → " + target;
        heading.ToolTip = doc.Path + " → " + target;
        notice.Text = doc.IsStale ? "The source file changed on disk. These properties belong to the open document; reload to read the changed source."
            : doc.AnimationEdits != null || PickupFields != null ? "Edits update this document; Ctrl+S saves it to disk." : "Stored properties of the explicitly opened item.";
        undo.IsEnabled = doc.AnimationEdits?.CanUndo == true || doc.PickupEdits?.CanUndo == true;
        redo.IsEnabled = doc.AnimationEdits?.CanRedo == true || doc.PickupEdits?.CanRedo == true;
        undo.Visibility = redo.Visibility = doc.AnimationEdits != null || doc.PickupEdits != null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RunUndo(bool isRedo)
    { if (Document is { } doc && ResolvePendingDrafts()) UndoRequested?.Invoke(doc, isRedo); }
    private async Task SaveAsync(bool saveAs)
    {
        if (Document is not { } doc || !ResolvePendingDrafts() || SaveRequested == null) return;
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
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closingResolved || !HasPendingDrafts) return;
        e.Cancel = true;
        if (resolvingClose) return;
        resolvingClose = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            try { if (ResolvePendingDrafts()) CloseResolved(); }
            finally { resolvingClose = false; }
        });
    }
    internal void CloseResolved() { closingResolved = true; Close(); }
    private void RememberBounds()
    {
        Rect bounds = WindowState == WindowState.Normal ? new(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        var saved = preferences.Settings.GetWorkspace().PropertiesWindow;
        saved.Left = bounds.Left; saved.Top = bounds.Top; saved.Width = bounds.Width; saved.Height = bounds.Height; saved.Normalize();
        try { preferences.Settings.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
