using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using zStudio.Core;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

internal sealed class SourcePickupDialog : Window
{
    internal SourcePickupAddition? Result { get; private set; }
    private readonly ComboBox type = new() { ItemsSource = MissionPickupType.Catalog, DisplayMemberPath = "Name", SelectedIndex = 0 };
    private readonly ComboBox difficulty = new() { ItemsSource = new[] { "All", "Easy", "Medium", "Hard" }, SelectedIndex = 0 };
    private readonly TextBox x = new(), y = new(), z = new(), rx = new() { Text = "0" }, ry = new() { Text = "0" }, rz = new() { Text = "0" };
    private readonly TextBox amount = new() { Text = "30" }, respawn = new() { Text = "0" };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.IndianRed };

    internal SourcePickupDialog(Window owner, string mission)
    {
        Owner = owner; Title = $"Add pickup to {mission}"; Width = 450; MinWidth = 400;
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        StackPanel panel = new() { Margin = new(16) };
        Field("Type", type); Field("Difficulty", difficulty);
        Row("Position", [("X", x), ("Y", y), ("Z", z)]);
        Row("Rotation (degrees)", [("X", rx), ("Y", ry), ("Z", rz)]);
        Field("Amount (0 uses the game default)", amount); Field("Respawn delay", respawn);
        panel.Children.Add(error);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Button add = new() { Content = "Add", MinWidth = 80, IsDefault = true, Margin = new(0, 0, 8, 0) };
        buttons.Children.Add(add); buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
        panel.Children.Add(buttons); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        type.SelectionChanged += (_, _) => amount.Text = ((MissionPickupType)type.SelectedItem).DefaultAmount.ToString(CultureInfo.InvariantCulture);
        add.Click += (_, _) => Accept();
        Loaded += (_, _) => x.Focus();

        void Field(string label, Control control)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 8, 0, 4) });
            System.Windows.Automation.AutomationProperties.SetName(control, label);
            if (control is TextBox box) box.MaxLength = 64;
            panel.Children.Add(control);
        }
        void Row(string label, (string Name, TextBox Box)[] fields)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 8, 0, 4) });
            Grid row = new();
            for (int i = 0; i < fields.Length; i++)
            {
                row.ColumnDefinitions.Add(new());
                var (name, box) = fields[i]; box.MaxLength = 64;
                StackPanel column = new() { Margin = new(0, 0, 8, 0) };
                column.Children.Add(new TextBlock { Text = name }); column.Children.Add(box);
                System.Windows.Automation.AutomationProperties.SetName(box, $"{label} {name}");
                Grid.SetColumn(column, i); row.Children.Add(column);
            }
            panel.Children.Add(row);
        }
    }

    private void Accept()
    {
        try
        {
            float Number(TextBox box, string field) => float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
                float.IsFinite(value) && Math.Abs(value) <= SourceWorlds.MaximumCoordinate ? value :
                throw new InvalidDataException($"Enter a finite {field} between {-SourceWorlds.MaximumCoordinate} and {SourceWorlds.MaximumCoordinate}.");
            if (!int.TryParse(amount.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 0)
                throw new InvalidDataException("Enter an amount from 0 to 2147483647.");
            var result = new SourcePickupAddition(((MissionPickupType)type.SelectedItem).Name,
                new(Number(x, "X"), Number(y, "Y"), Number(z, "Z")), (string)difficulty.SelectedItem,
                new(Number(rx, "rotation X"), Number(ry, "rotation Y"), Number(rz, "rotation Z")), count, Number(respawn, "respawn delay"));
            SourcePickupEdits.Validate(result);
            Result = result; DialogResult = true;
        }
        catch (InvalidDataException ex) { error.Text = ex.Message; }
    }
}
