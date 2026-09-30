using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

internal enum SceneInspectionBinding { None, AuthoredPosition, AuthoredRotation, AuthoredHeading }

/// <summary>A persistent readout which can participate in the card's explicit draft.</summary>
internal sealed class SceneInspectionField : Grid
{
    internal SceneInspectionBinding Binding { get; }
    internal ValueTextBox[] Inputs { get; }
    private bool editing;

    internal SceneInspectionField(string label, bool vector, SceneInspectionBinding binding, Button copy)
    {
        Binding = binding;
        Margin = new(0, 4, 0, 4);
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid contents = new();
        contents.RowDefinitions.Add(new() { Height = GridLength.Auto });
        contents.RowDefinitions.Add(new() { Height = GridLength.Auto });
        contents.Children.Add(new TextBlock { Text = label, Opacity = .75, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new(0, 0, 0, 3) });
        StackPanel inputs = new();
        SetRow(inputs, 1);
        contents.Children.Add(inputs);
        Inputs = Enumerable.Range(0, vector ? 3 : 1).Select(_ => new ValueTextBox
        {
            IsReadOnly = true, Opacity = .75, Padding = new(6, 2, 6, 2), MinWidth = 0,
            MinHeight = 30, MaxHeight = vector ? 30 : 54, FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = vector ? TextWrapping.NoWrap : TextWrapping.Wrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        }).ToArray();
        for (int i = 0; i < Inputs.Length; i++)
        {
            string name = binding == SceneInspectionBinding.AuthoredPosition ? "Authored position" : label;
            AutomationProperties.SetName(Inputs[i], name + (vector ? " " + "XYZ"[i] : ""));
            if (binding != SceneInspectionBinding.None) Inputs[i].MaxLength = 64;
            if (!vector) { inputs.Children.Add(Inputs[i]); continue; }
            Grid component = new() { Margin = new(0, i == 0 ? 0 : 2, 0, 0) };
            component.ColumnDefinitions.Add(new() { Width = new(18) });
            component.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            component.Children.Add(new TextBlock { Text = "XYZ"[i].ToString(), Opacity = .75, VerticalAlignment = VerticalAlignment.Center });
            SetColumn(Inputs[i], 1); component.Children.Add(Inputs[i]); inputs.Children.Add(component);
        }
        Children.Add(contents);
        copy.VerticalAlignment = VerticalAlignment.Top; copy.Margin = new(4, 17, 0, 0);
        SetColumn(copy, 1); Children.Add(copy);
    }

    internal void SetEditing(bool value)
    {
        editing = value && Binding != SceneInspectionBinding.None;
        foreach (var input in Inputs) { input.IsReadOnly = !editing; input.Opacity = editing ? 1 : .75; }
    }

    internal void Refresh(JsonNode? value, string display)
    {
        // Playback/hover refresh must not overwrite incomplete input, text selection or the caret.
        if (editing) return;
        for (int i = 0; i < Inputs.Length; i++)
        {
            string text = Inputs.Length == 3 && value is JsonObject vector
                ? Binding == SceneInspectionBinding.AuthoredRotation
                    ? vector["xyz"[i].ToString()]!.GetValue<double>().ToString("R", CultureInfo.InvariantCulture)
                    : JsonData.Scalar(vector["xyz"[i].ToString()], float.NaN).ToString("R", CultureInfo.InvariantCulture) : display;
            if (Inputs[i].Text != text) Inputs[i].Text = text;
            Inputs[i].ToolTip = text;
        }
    }
}
