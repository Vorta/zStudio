using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

/// <summary>Read-only presentation; the original JSON remains the copy/export source.</summary>
public sealed class ReadOnlyPropertySheet : UserControl
{
    public void Show(JsonObject value, bool hierarchyInCenter)
    {
        StackPanel panel = new() { Margin = new(10,6,10,8) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        foreach (string name in new[] { "name", "kind", "index" }) if (value.TryGetPropertyValue(name, out var v)) Add(panel, name == "index" ? "Record ID" : Caption(name), v);
        if (value["wave"] is JsonObject wave) foreach (var field in wave.Where(p => !Provenance(p.Key))) Add(panel, Caption(field.Key), field.Value);
        if (value["properties"] is JsonObject properties)
            foreach (var field in properties.Where(p => !Provenance(p.Key))) Add(panel, Caption(field.Key), field.Value);
        foreach (var field in value.Where(p => p.Key is not ("name" or "kind" or "index" or "properties" or "wave") && !Provenance(p.Key)))
        {
            if (hierarchyInCenter && field.Key == "tree") { panel.Children.Add(new TextBlock { Text = "Inspect the ZRD hierarchy in Data. This panel summarizes the source asset.", TextWrapping = TextWrapping.Wrap, Margin = new(0,8,0,8) }); continue; }
            Add(panel, Caption(field.Key), field.Value);
        }
        StackPanel raw = new();
        var provenance = new Expander { Header = "Serialized state / provenance", Content = raw, IsExpanded = false, Margin = new(0,8,0,0) };
        panel.Children.Add(provenance); bool loaded = false;
        provenance.Expanded += (_, _) =>
        {
            if (loaded) return; loaded = true;
            foreach (var field in value.Where(p => Provenance(p.Key))) Add(raw, Caption(field.Key), field.Value);
            if (value["properties"] is JsonObject metadata) foreach (var field in metadata.Where(p => Provenance(p.Key))) Add(raw, Caption(field.Key), field.Value);
            if (value["wave"] is JsonObject audio) foreach (var field in audio.Where(p => Provenance(p.Key))) Add(raw, Caption(field.Key), field.Value);
            raw.Children.Add(new TextBlock { Text = "All original fields are included by Copy asset properties as JSON.", TextWrapping = TextWrapping.Wrap, Margin = new(0,6,0,0) });
        };
    }
    // These additional names are explicitly pointer/offset descriptors in the GameZ layouts.
    private static bool Provenance(string name) => name.StartsWith("source_", StringComparison.Ordinal) || name.Contains("_ptr", StringComparison.Ordinal) || name.Contains("_raw", StringComparison.Ordinal) || name is "offset" or "header_flags" or "data_offset" or "DataOffset" or "data_offset_word" or "environment_data" or "action_callback" or "aux_value";
    private static string Caption(string name) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Regex.Replace(name.Replace('_',' '),"([a-z0-9])([A-Z])","$1 $2"));
    private static void Add(Panel panel, string name, JsonNode? value)
    {
        if (value is JsonObject or JsonArray)
        {
            int count = value is JsonObject obj ? obj.Count : ((JsonArray)value).Count;
            StackPanel children = new(); bool loaded = false;
            Expander group = new() { Header = $"{name} · {count:N0} {(value is JsonObject ? "fields" : "items")}", Content = children, Margin = new(0,3,0,3) };
            group.Expanded += (_, _) =>
            {
                if (loaded) return; loaded = true;
                TreeView tree = new() { MaxHeight = 260, Background = Brushes.Transparent, BorderThickness = new(0), ItemsSource = new InspectorNode(name,value).Children };
                children.Children.Add(tree);
            };
            panel.Children.Add(group); return;
        }
        Grid row = new() { Margin = new(0,2,0,4) };
        row.ColumnDefinitions.Add(new() { Width = new(108) }); row.ColumnDefinitions.Add(new());
        TextBlock label = new() { Text = name, TextWrapping = TextWrapping.Wrap, Margin = new(0,4,6,0) }; row.Children.Add(label);
        TextBox text = new() { Text = value?.ToString() ?? "null", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(2), MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(text, name + " (read-only)"); Grid.SetColumn(text,1); row.Children.Add(text); panel.Children.Add(row);
    }
}
