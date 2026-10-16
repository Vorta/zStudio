using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using zStudio.Core;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

public partial class FieldEditor : UserControl
{
    protected bool refreshingFields, committingDraft, resolvingDrafts, disposed;
    protected bool ManualDraftMode;
    /// <summary>Card layout: a narrow label beside the value, and X/Y/Z components side by side with their axis letter inside each box.</summary>
    protected bool CompactRows;
    /// <summary>The colours of the X, Y and Z axis letters (red, green, blue, readable on light and dark themes).</summary>
    internal static readonly Brush[] AxisBrushes = [Frozen(0xE8, 0x60, 0x5F), Frozen(0x4C, 0xAF, 0x63), Frozen(0x3E, 0x9B, 0xE0)];
    private static Brush Frozen(byte r, byte g, byte b) { SolidColorBrush brush = new(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    protected event Action? ManualDraftChanged;
    private protected readonly List<DraftInput> draftInputs = [];
    protected readonly List<Action> valueRefresh = [];
    protected readonly List<Action> referenceRefresh = [];
    protected string inputScope = "properties";
    public bool HasPendingDrafts => draftInputs.Any(d => d.Draft.IsPending || d.Draft.IsCommitting);
    /// <summary>Drafts with input no commit is applying yet (a committing draft is excluded).</summary>
    public bool HasUncommittedDrafts => draftInputs.Any(d => d.Draft.IsPending && !d.Draft.IsCommitting);
    private protected sealed record DraftInput(FieldDraft Draft, FrameworkElement Control, Action Display, string Scope);
    protected virtual void RefreshProperties() { }
    protected static void Label(StackPanel panel,string text,bool title = false) => panel.Children.Add(new TextBlock { Text = text,TextWrapping = TextWrapping.Wrap,FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal,Opacity = title ? 1 : .75,Margin = new(0,3,0,6) });
    protected static void ReadOnlyText(StackPanel panel,string text) => panel.Children.Add(new TextBox { Text = text,IsReadOnly = true,TextWrapping = TextWrapping.Wrap,BorderThickness = new(0),Background = Brushes.Transparent,Margin = new(0,3,0,6) });
    protected void Input(StackPanel panel,string label,string value,Action<string> commit,bool readOnly = false,string? hint = null,Func<string>? getter = null,string[]? components = null,string separator = ", ",int? componentColumns = null,Func<string,Task>? asyncCommit = null)
    {
        if (components != null && separator != "|")
        {
            var write = commit; commit = text => { ComponentText.CheckNumeric(text); write(text); };
            if (asyncCommit != null) { var writeAsync = asyncCommit; asyncCommit = text => { ComponentText.CheckNumeric(text); return writeAsync(text); }; }
        }
        AddAutomationField(label, components == null ? "text" : "components", () => getter?.Invoke() ?? value, readOnly ? null : commit, components, hint, asyncWrite: readOnly ? null : asyncCommit);
        // A vector's components share one draft and one commit boundary.
        Grid row = new() { Margin = new(0,2,0,5) };
        row.ColumnDefinitions.Add(new() { Width = new(CompactRows ? 76 : 112) }); row.ColumnDefinitions.Add(new());
        row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
        TextBlock caption = new() { Text = label + (readOnly ? " (read-only)" : ""),TextWrapping = TextWrapping.Wrap,Margin = new(0,6,6,0) }; row.Children.Add(caption);
        if (CompactRows) { caption.FontSize = 13; caption.VerticalAlignment = VerticalAlignment.Center; caption.Margin = new(0,0,6,0); caption.SetResourceReference(TextBlock.ForegroundProperty,"TextFillColorSecondaryBrush"); }
        Grid values = new(); Grid.SetColumn(values,1); row.Children.Add(values);
        if (components != null && !CompactRows) { Grid.SetRow(values,1); Grid.SetColumn(values,0); Grid.SetColumnSpan(values,2); Grid.SetColumnSpan(caption,2); row.RowDefinitions.Add(new() { Height = GridLength.Auto }); }
        var parts = components == null ? new[] { value } : ComponentText.Display(value,separator,components.Length);
        var boxes = new List<ValueTextBox>();
        int columns = componentColumns ?? components?.Length ?? 1;
        for (int i = 0; i < columns; i++) values.ColumnDefinitions.Add(new());
        for (int i = 0; i < Math.Ceiling((components?.Length ?? 1) / (double)columns); i++) values.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (int i = 0; i < (components?.Length ?? 1); i++)
        {
            StackPanel component = new() { Margin = new(i % columns == 0 ? 0 : 3,0,0,3) }; Grid.SetColumn(component,i % columns); Grid.SetRow(component,i / columns); values.Children.Add(component);
            if (components != null && !CompactRows) component.Children.Add(new TextBlock { Text = components[i],Opacity = .7,FontSize = 11 });
            ValueTextBox box = new() { Text = parts.ElementAtOrDefault(i) ?? "",IsReadOnly = readOnly,MinWidth = 20,ToolTip = hint ?? label }; AutomationProperties.SetName(box,label + (components != null ? " " + components[i] : ""));
            if (ManualDraftMode) box.MaxLength = 4096;
            boxes.Add(box);
            if (components != null && CompactRows)
            {
                // The axis letter sits inside the box, coloured like the 3D handles.
                Grid inline = new();
                box.Padding = new(components[i].Length == 1 ? 20 : 8,box.Padding.Top,box.Padding.Right,box.Padding.Bottom);
                inline.Children.Add(box);
                if (components[i].Length == 1)
                {
                    // The letter is also a scrub handle: drag it sideways to change the value.
                    Border axisLetter = new() { Padding = new(8,0,4,0),IsHitTestVisible = !readOnly,Background = Brushes.Transparent,Cursor = Cursors.SizeWE,Focusable = !readOnly,FocusVisualStyle = null,
                        ToolTip = readOnly ? null : "Drag to change · Shift ×10 · Ctrl ×0.1",HorizontalAlignment = HorizontalAlignment.Left,
                        Child = new TextBlock { Text = components[i],FontSize = 12,FontWeight = FontWeights.SemiBold,VerticalAlignment = VerticalAlignment.Center,
                            Foreground = "XYZ".IndexOf(components[i][0]) is int axis and >= 0 ? AxisBrushes[axis] : Brushes.Gray } };
                    KeyboardNavigation.SetIsTabStop(axisLetter,false);
                    if (!readOnly) ScrubHandle(axisLetter,box,label);
                    inline.Children.Add(axisLetter);
                }
                component.Children.Add(inline);
            }
            else component.Children.Add(box);
        }
        if (components?.Length == 4 && componentColumns == null)
        {
            // Reflow existing controls so drafts, caret, focus order and one atomic edit survive.
            values.SizeChanged += (_,_) =>
            {
                int count = values.ActualWidth >= 480 * FontSize / 13 ? 4 : 2;
                if (values.ColumnDefinitions.Count == count) return;
                values.ColumnDefinitions.Clear(); values.RowDefinitions.Clear();
                for (int i = 0; i < count; i++) values.ColumnDefinitions.Add(new());
                for (int i = 0; i < 4 / count; i++) values.RowDefinitions.Add(new() { Height = GridLength.Auto });
                for (int i = 0; i < 4; i++) { Grid.SetColumn(values.Children[i],i % count); Grid.SetRow(values.Children[i],i / count); }
            };
        }
        TextBlock error = new() { TextWrapping = TextWrapping.Wrap,Visibility = Visibility.Collapsed,Margin = new(0,3,0,0) }; Grid.SetRow(error,components == null ? 1 : 2); Grid.SetColumnSpan(error,2); row.Children.Add(error); panel.Children.Add(row);
        FieldDraft draft = new(value,commit,asyncCommit);
        bool displaying = false; string? composedText = null;
        void Display()
        {
            displaying = true;
            // A rejected/native input already lives in its component boxes. Redistributing that same pending draft
            // can move pasted text to another component, reset its caret and copy the whole input unnecessarily.
            if (!draft.IsPending || !ReferenceEquals(draft.Text,composedText))
            {
                var text = components == null ? new[] { draft.Text } : ComponentText.Display(draft.Text,separator,components.Length);
                for (int i = 0; i < boxes.Count; i++) { string next = text.ElementAtOrDefault(i) ?? ""; if (boxes[i].Text != next) boxes[i].Text = next; }
            }
            error.Text = draft.Error ?? ""; error.Visibility = draft.Error == null ? Visibility.Collapsed : Visibility.Visible; displaying = false;
        }
        var input = new DraftInput(draft,row,Display,inputScope); if (!readOnly) draftInputs.Add(input);
        if (getter != null && inputScope is "properties" or "references")
            (inputScope == "properties" ? valueRefresh : referenceRefresh).Add(() => { draft.Refresh(getter()); Display(); });
        foreach (var box in boxes)
        {
            box.TextChanged += (_,_) => { if (!displaying) { composedText = draft.Text = components == null ? box.Text : string.Join(separator,boxes.Select(b => b.Text)); if (ManualDraftMode) ManualDraftChanged?.Invoke(); } };
            box.LostKeyboardFocus += async (_,_) => { if (!ManualDraftMode && CanCommitFocus(row) && !readOnly) await CommitInputAsync(input); };
            box.PreviewKeyDown += async (_,e) =>
            {
                if (readOnly) return;
                if (e.Key == Key.Enter) { e.Handled = true; if (!ManualDraftMode) await CommitInputAsync(input); }
                else if (e.Key == Key.Escape && !draft.IsCommitting) { draft.Discard(); Display(); if (ManualDraftMode) ManualDraftChanged?.Invoke(); e.Handled = true; }
            };
            if (components != null)
            {
                DataObject.AddPastingHandler(box,(_,e) =>
                {
                    if (readOnly || e.DataObject.GetData(DataFormats.UnicodeText) is not string text) return;
                    if (ComponentText.TrySplit(text,separator,boxes.Count,out var incoming) && incoming.Length == boxes.Count)
                    { draft.Text = string.Join(separator,incoming); Display(); if (ManualDraftMode) ManualDraftChanged?.Invoke(); e.CancelCommand(); }
                });
            }
        }
        if (components != null)
        {
            ContextMenu menu = new(); MenuItem copy = new() { Header = "Copy complete value" }; copy.Click += (_,_) => Clipboard.SetText(draft.Text); menu.Items.Add(copy); values.ContextMenu = menu;
        }
    }
    /// <summary>Dragging <paramref name="handle"/> sideways steps <paramref name="box"/>'s number: 0.1 per pixel (rotation 0.5°, scale 0.01).</summary>
    private static void ScrubHandle(FrameworkElement handle, TextBox box, string label)
    {
        double step = label.StartsWith("Rotation", StringComparison.Ordinal) ? .5 : label.StartsWith("Scale", StringComparison.Ordinal) ? .01 : .1;
        Point start = default; double origin = 0; string originText = ""; bool dragging = false;
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (box.IsReadOnly || !box.IsEnabled || !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out origin) || !double.IsFinite(origin)) return;
            start = e.GetPosition(handle); originText = box.Text; dragging = handle.CaptureMouse(); e.Handled = true;
            if (dragging) handle.Focus();
        };
        handle.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            double factor = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? .1 : 1;
            double pixels = Math.Round(e.GetPosition(handle).X - start.X);
            string text = pixels == 0 ? originText : Math.Round(origin + pixels * step * factor, 4).ToString("0.####", CultureInfo.InvariantCulture);
            if (box.Text != text) box.Text = text;
        };
        // After the drag, typing and Escape belong to the field again.
        handle.MouseLeftButtonUp += (_, e) => { if (!dragging) return; dragging = false; handle.ReleaseMouseCapture(); e.Handled = true; box.Focus(); };
        // Escape during a drag restores the value it started from.
        handle.PreviewKeyDown += (_, e) => { if (dragging && e.Key == Key.Escape) { box.Text = originText; dragging = false; handle.ReleaseMouseCapture(); e.Handled = true; } };
        handle.LostMouseCapture += (_, _) => dragging = false;
    }
    protected bool CanCommitFocus(FrameworkElement group)
    {
        if (refreshingFields || disposed || committingDraft || !group.IsVisible || !group.IsEnabled || group.IsKeyboardFocusWithin || (Window.GetWindow(this) is MainWindow { IsChangingLayout: true } || Window.GetWindow(this)?.Owner is MainWindow { IsChangingLayout: true })) return false;
        // Navigating presentation chrome is not an implicit source edit. Commands that
        // need current stored values explicitly resolve drafts before running.
        for (var target = Keyboard.FocusedElement as DependencyObject; target != null; target = target is Visual ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target))
            if (target is MenuItem or TabItem or System.Windows.Controls.Primitives.Thumb || target is FrameworkElement { Tag: "Layout" }) return false;
        return Keyboard.FocusedElement is DependencyObject focused && Window.GetWindow(focused) == Window.GetWindow(group);
    }
    private protected bool CommitInput(DraftInput input)
    {
        if (committingDraft || disposed) return true;
        committingDraft = true;
        bool success;
        try { success = input.Draft.Commit(); input.Display(); }
        finally { committingDraft = false; }
        if (success) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!disposed && !HasPendingDrafts) RefreshProperties(); });
        return success;
    }
    private protected async Task<bool> CommitInputAsync(DraftInput input)
    {
        if (!input.Draft.IsAsync) return CommitInput(input);
        if (disposed) return false;
        committingDraft = true;
        // A source world's edit blocks Properties input while it rebuilds (the owner sets that), which takes focus; focus
        // returns where it was, while the window is active.
        var focused = Keyboard.FocusedElement as UIElement;
        try { bool result = await input.Draft.CommitAsync(); if (!disposed) input.Display(); return result; }
        finally
        {
            committingDraft = false;
            if (!disposed)
            {
                if (focused is { IsVisible: true, IsEnabled: true } && IsAncestorOf(focused) && Window.GetWindow(this)?.IsActive == true) focused.Focus();
                if (!HasPendingDrafts) RefreshProperties();
            }
        }
    }
    public async Task<bool> ResolvePendingDraftsAsync()
    {
        if (!draftInputs.Any(d => d.Draft.IsAsync)) return ResolvePendingDrafts();
        foreach (var input in draftInputs.Where(d => d.Draft.IsPending || d.Draft.IsCommitting).ToArray())
            if (!await CommitInputAsync(input))
            {
                var choice = MessageBox.Show(Window.GetWindow(this), (input.Draft.Error ?? "Invalid input.") + "\n\nDiscard this draft?", "Unfinished property input", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice != MessageBoxResult.Yes) return false;
                input.Draft.Discard(); input.Display();
            }
        return !HasPendingDrafts;
    }
    public virtual bool ResolvePendingDrafts()
    {
        if (draftInputs.Any(d => d.Draft.IsAsync && (d.Draft.IsPending || d.Draft.IsCommitting))) return false;
        if (committingDraft || resolvingDrafts || disposed) return true;
        resolvingDrafts = true;
        try
        {
            foreach (var input in draftInputs.Where(d => d.Draft.IsPending).ToArray())
            {
                if (CommitInput(input)) continue;
                string choice = "Keep editing";
                StackPanel panel = new() { Margin = new(20) };
                panel.Children.Add(new TextBlock { Text = "Unfinished property input",FontWeight = FontWeights.SemiBold,FontSize = 18 });
                panel.Children.Add(new TextBlock { Text = input.Draft.Error,TextWrapping = TextWrapping.Wrap,Margin = new(0,12,0,12) });
                WrapPanel buttons = new(); panel.Children.Add(buttons);
                Window dialog = new() { Owner = Window.GetWindow(this),Title = "Resolve property input",Width = 450,SizeToContent = SizeToContent.Height,ResizeMode = ResizeMode.NoResize,WindowStartupLocation = WindowStartupLocation.CenterOwner,Content = panel };
                foreach (string text in new[] { "Keep editing","Discard draft" }) { Button button = new() { Content = text,Margin = new(4),IsCancel = text == "Keep editing" }; button.Click += (_,_) => { choice = text; dialog.Close(); }; buttons.Children.Add(button); }
                dialog.ShowDialog();
                if (choice == "Keep editing") { input.Control.BringIntoView(); input.Control.MoveFocus(new(FocusNavigationDirection.First)); return false; }
                input.Draft.Discard(); input.Display();
            }
            return true;
        }
        finally { resolvingDrafts = false; }
    }
    protected void Choice(StackPanel panel,string label,IEnumerable<ChoiceValue> values,int value,Action<int> commit,bool readOnly = false,Func<int>? getter = null,bool searchable = false,bool fullWidth = false)
    {
        var items = values.ToArray();
        if (readOnly) { Input(panel,label,items.FirstOrDefault(c => c.Value == value)?.Label ?? value.ToString(CultureInfo.InvariantCulture),_ => { },true); return; }
        AddAutomationField(label, "choice", () => (getter?.Invoke() ?? value).ToString(CultureInfo.InvariantCulture), text =>
        {
            int selected = int.Parse(text, CultureInfo.InvariantCulture);
            if (!items.Any(i => i.Value == selected)) throw new InvalidDataException("Unknown choice value.");
            commit(selected);
        }, choices: items.Select(i => new AutomationChoice(i.Value, i.Label)).ToArray());
        ComboBox choice = new() { ItemsSource = items,DisplayMemberPath = nameof(ChoiceValue.Label),SelectedItem = items.FirstOrDefault(c => c.Value == value),IsEnabled = !readOnly,IsEditable = searchable,IsReadOnly = !searchable,IsTextSearchEnabled = true,Margin = new(0,0,0,5),MaxDropDownHeight = 300 };
        AutomationProperties.SetName(choice,label);
        Grid row = new() { Margin = new(0,2,0,5) }; row.ColumnDefinitions.Add(new() { Width = new(112) }); row.ColumnDefinitions.Add(new());
        row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new(0,6,6,0) }); Grid.SetColumn(choice,1); row.Children.Add(choice); panel.Children.Add(row); bool syncing = false;
        if (fullWidth)
        {
            row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetColumnSpan(row.Children[0],2); Grid.SetRow(choice,1); Grid.SetColumn(choice,0); Grid.SetColumnSpan(choice,2);
        }
        if (getter != null) valueRefresh.Add(() => { syncing = true; value = getter(); choice.SelectedItem = items.FirstOrDefault(c => c.Value == value); syncing = false; });
        choice.SelectionChanged += (_,_) =>
        {
            if (syncing || refreshingFields || disposed || choice.SelectedItem is not ChoiceValue selected || selected.Value == value) return;
            if (!ResolvePendingDrafts()) { syncing = true; choice.SelectedItem = items.FirstOrDefault(c => c.Value == value); syncing = false; return; }
            commit(selected.Value); value = getter?.Invoke() ?? selected.Value;
        };
        if (searchable)
        {
            choice.IsTextSearchEnabled = false;
            choice.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,new TextChangedEventHandler((_,_) =>
            {
                if (syncing || !choice.IsKeyboardFocusWithin || choice.SelectedItem is ChoiceValue selected && selected.Label == choice.Text) return;
                string query = choice.Text; syncing = true;
                choice.ItemsSource = items.Where(i => i.Label.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
                choice.Text = query; choice.IsDropDownOpen = true; syncing = false;
            }));
        }
    }
    protected void TextChoice(StackPanel panel,string label,string value,IEnumerable<string> choices,Action<string> commit,bool readOnly,Func<string>? getter = null)
    {
        Input(panel,label,value,commit,readOnly,getter:getter);
        var suggestions = choices.Distinct().Order().ToArray();
        if (readOnly || suggestions.Length == 0) return;
        ComboBox picker = new() { ItemsSource = suggestions,IsEditable = true,Text = "Choose a name…",MaxDropDownHeight = 220,Margin = new(0,0,0,4) }; AutomationProperties.SetName(picker,label + " suggestions"); panel.Children.Add(picker);
        picker.SelectionChanged += (_,_) => { if (!refreshingFields && picker.SelectedItem is string text && ResolvePendingDrafts()) { commit(text); RefreshProperties(); } };
    }
    protected void Button(Panel panel,string text,Action action) { AddAutomationAction(text, action); Button button = new() { Content = text,Margin = new(2),Padding = new(6,3,6,3) }; button.Click += (_,_) => action(); panel.Children.Add(button); }
    protected void AsyncButton(Panel panel, string text, Func<Task> action)
    {
        AddAutomationAsyncAction(text, action);
        Button button = new() { Content = text, Margin = new(2), Padding = new(6,3,6,3) };
        button.Click += async (_, _) =>
        {
            // A draft whose commit rebuilt a source world closed this editor; the action belongs to the old document.
            if (!await ResolvePendingDraftsAsync() || disposed) return;
            button.IsEnabled = false; committingDraft = true;
            try { await action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Edit properties"); }
            finally { committingDraft = false; button.IsEnabled = true; if (!disposed) RefreshProperties(); }
        };
        panel.Children.Add(button);
    }
    protected sealed record ChoiceValue(int Value,string Label);
}
