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
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

public partial class FieldEditor : UserControl
{
    protected bool refreshingFields, committingDraft, resolvingDrafts, disposed;
    private protected readonly List<DraftInput> draftInputs = [];
    protected readonly List<Action> valueRefresh = [];
    protected readonly List<Action> referenceRefresh = [];
    protected string inputScope = "properties";
    public bool HasPendingDrafts => draftInputs.Any(d => d.Draft.IsPending);
    private protected sealed record DraftInput(FieldDraft Draft, FrameworkElement Control, Action Display, string Scope);
    protected virtual void RefreshProperties() { }
    protected static void Label(StackPanel panel,string text,bool title = false) => panel.Children.Add(new TextBlock { Text = text,TextWrapping = TextWrapping.Wrap,FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal,Opacity = title ? 1 : .75,Margin = new(0,3,0,6) });
    protected static void ReadOnlyText(StackPanel panel,string text) => panel.Children.Add(new TextBox { Text = text,IsReadOnly = true,TextWrapping = TextWrapping.Wrap,BorderThickness = new(0),Background = Brushes.Transparent,Margin = new(0,3,0,6) });
    protected void Input(StackPanel panel,string label,string value,Action<string> commit,bool readOnly = false,string? hint = null,Func<string>? getter = null,string[]? components = null,string separator = ", ",int? componentColumns = null)
    {
        AddAutomationField(label, components == null ? "text" : "components", () => getter?.Invoke() ?? value, readOnly ? null : commit, components, hint);
        // A vector's components share one draft and one commit boundary.
        Grid row = new() { Margin = new(0,2,0,5) };
        row.ColumnDefinitions.Add(new() { Width = new(112) }); row.ColumnDefinitions.Add(new());
        row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
        TextBlock caption = new() { Text = label + (readOnly ? " (read-only)" : ""),TextWrapping = TextWrapping.Wrap,Margin = new(0,6,6,0) }; row.Children.Add(caption);
        Grid values = new(); Grid.SetColumn(values,1); row.Children.Add(values);
        if (components != null) { Grid.SetRow(values,1); Grid.SetColumn(values,0); Grid.SetColumnSpan(values,2); Grid.SetColumnSpan(caption,2); row.RowDefinitions.Add(new() { Height = GridLength.Auto }); }
        var parts = components == null ? new[] { value } : SplitComponents(value,separator);
        var boxes = new List<ValueTextBox>();
        int columns = componentColumns ?? components?.Length ?? 1;
        for (int i = 0; i < columns; i++) values.ColumnDefinitions.Add(new());
        for (int i = 0; i < Math.Ceiling((components?.Length ?? 1) / (double)columns); i++) values.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (int i = 0; i < (components?.Length ?? 1); i++)
        {
            StackPanel component = new() { Margin = new(i % columns == 0 ? 0 : 3,0,0,3) }; Grid.SetColumn(component,i % columns); Grid.SetRow(component,i / columns); values.Children.Add(component);
            if (components != null) component.Children.Add(new TextBlock { Text = components[i],Opacity = .7,FontSize = 11 });
            ValueTextBox box = new() { Text = parts.ElementAtOrDefault(i) ?? "",IsReadOnly = readOnly,MinWidth = 20,ToolTip = hint ?? label }; AutomationProperties.SetName(box,label + (components != null ? " " + components[i] : ""));
            boxes.Add(box); component.Children.Add(box);
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
        FieldDraft draft = new(value,commit);
        bool displaying = false;
        void Display()
        {
            displaying = true; var text = components == null ? new[] { draft.Text } : SplitComponents(draft.Text,separator);
            for (int i = 0; i < boxes.Count; i++) { string next = text.ElementAtOrDefault(i) ?? ""; if (boxes[i].Text != next) boxes[i].Text = next; }
            error.Text = draft.Error ?? ""; error.Visibility = draft.Error == null ? Visibility.Collapsed : Visibility.Visible; displaying = false;
        }
        var input = new DraftInput(draft,row,Display,inputScope); if (!readOnly) draftInputs.Add(input);
        if (getter != null && inputScope is "properties" or "references")
            (inputScope == "properties" ? valueRefresh : referenceRefresh).Add(() => { draft.Refresh(getter()); Display(); });
        foreach (var box in boxes)
        {
            box.TextChanged += (_,_) => { if (!displaying) draft.Text = components == null ? box.Text : string.Join(separator,boxes.Select(b => b.Text)); };
            box.LostKeyboardFocus += (_,_) => { if (CanCommitFocus(row) && !readOnly) CommitInput(input); };
            box.PreviewKeyDown += (_,e) =>
            {
                if (readOnly) return;
                if (e.Key == Key.Enter) { CommitInput(input); e.Handled = true; }
                else if (e.Key == Key.Escape) { draft.Discard(); Display(); e.Handled = true; }
            };
            if (components != null)
            {
                DataObject.AddPastingHandler(box,(_,e) =>
                {
                    if (readOnly || e.DataObject.GetData(DataFormats.UnicodeText) is not string text) return;
                    var incoming = SplitComponents(text,separator);
                    if (incoming.Length == boxes.Count) { draft.Text = string.Join(separator,incoming); Display(); e.CancelCommand(); }
                });
            }
        }
        if (components != null)
        {
            ContextMenu menu = new(); MenuItem copy = new() { Header = "Copy complete value" }; copy.Click += (_,_) => Clipboard.SetText(draft.Text); menu.Items.Add(copy); values.ContextMenu = menu;
        }
    }
    protected static string[] SplitComponents(string value,string separator) => separator == "|" ? value.Split('|') : value.Split([',',' ','\t'],StringSplitOptions.RemoveEmptyEntries);
    protected bool CanCommitFocus(FrameworkElement group)
    {
        if (refreshingFields || disposed || committingDraft || !group.IsVisible || group.IsKeyboardFocusWithin || (Window.GetWindow(this) is MainWindow { IsChangingLayout: true } || Window.GetWindow(this)?.Owner is MainWindow { IsChangingLayout: true })) return false;
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
    public virtual bool ResolvePendingDrafts()
    {
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
    protected sealed record ChoiceValue(int Value,string Label);
}
