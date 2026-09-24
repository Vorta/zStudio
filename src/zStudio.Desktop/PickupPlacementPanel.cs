using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

public sealed class PickupPlacementPanel : UserControl
{
    private readonly TextBlock title = new() { FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock detail = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0), Opacity = .8 };
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(0, 5, 0, 0) };
    private readonly ValueTextBox[] coordinates = [new(), new(), new()];
    private MissionPickupSource? source;
    private Vector3 position;
    private bool updating, committing;
    public event Action<MissionPickupSource, Vector3>? PositionCommitted;
    public PickupPlacementPanel()
    {
        StackPanel panel = new() { Margin = new(10) };
        panel.Children.Add(new TextBlock { Text = "Pickup placement", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) });
        panel.Children.Add(title);
        Grid fields = new() { Margin = new(0, 8, 0, 0) }; panel.Children.Add(fields);
        for (int i = 0; i < coordinates.Length; i++)
        {
            fields.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            StackPanel field = new() { Margin = new(i == 0 ? 0 : 5, 0, 0, 0) }; Grid.SetColumn(field, i); fields.Children.Add(field);
            string axis = "XYZ"[i].ToString(); field.Children.Add(new TextBlock { Text = axis, Margin = new(2, 0, 0, 2) });
            var box = coordinates[i]; box.SetResourceReference(StyleProperty, typeof(TextBox)); box.MinWidth = 45; box.Padding = new(4, 5, 4, 5); box.ToolTip = axis + " position in game units · Enter to apply · Escape to revert";
            AutomationProperties.SetName(box, "Pickup " + axis); field.Children.Add(box);
            box.LostKeyboardFocus += (_, _) => CommitPending();
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { CommitPending(); e.Handled = true; }
                else if (e.Key == Key.Escape) { RefreshCoordinates(); validation.Text = ""; e.Handled = true; }
            };
        }
        panel.Children.Add(validation); panel.Children.Add(detail);
        Content = new ScrollViewer { Content = panel, MaxHeight = 310, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    public void Show(PickupPlacementRecord record, Vector3 value, PickupPlacementScope scope, string target, bool locked)
    {
        if (source != record.Source) CommitPending();
        source = record.Source; position = value; title.Text = record.Type + " · #" + record.Source.RecordIndex;
        detail.Text = scope.Description + "\n" + System.IO.Path.GetFileName(record.Source.ArchivePath) + " → " + record.Source.ResourceName.ToLowerInvariant()
            + "\nSave target: " + target + (locked ? "\nUnlock the map to move this pickup." : "\nDrag an arrow or enter exact coordinates.");
        foreach (var box in coordinates) box.IsReadOnly = locked;
        RefreshCoordinates(); validation.Text = "";
    }
    public void Preview(Vector3 value) { position = value; RefreshCoordinates(); }
    public void Clear() { source = null; validation.Text = ""; }
    public void CommitPending()
    {
        if (updating || committing || source == null || coordinates[0].IsReadOnly) return;
        Vector3 value = default;
        for (int i = 0; i < 3; i++)
        {
            if (!float.TryParse(coordinates[i].Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float number) || !float.IsFinite(number))
            { validation.Text = "Enter a finite number for " + "XYZ"[i] + ". The previous position was retained."; return; }
            value[i] = number;
        }
        validation.Text = "";
        if (value == position) return;
        committing = true;
        try { PositionCommitted?.Invoke(source, value); }
        finally { committing = false; }
    }
    private void RefreshCoordinates()
    {
        updating = true;
        try { for (int i = 0; i < 3; i++) coordinates[i].Text = position[i].ToString("R", CultureInfo.CurrentCulture); }
        finally { updating = false; }
    }
}
