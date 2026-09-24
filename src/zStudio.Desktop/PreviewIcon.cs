using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Recoil.Zbd.Desktop;

/// <summary>Small scalable viewport symbols; inherits the Fluent content foreground in every state.</summary>
public sealed class PreviewIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(PreviewIcon), new FrameworkPropertyMetadata("Frame", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(PreviewIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(PreviewIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool IsChecked { get => (bool)GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
    private static readonly Typeface Symbols = new("Segoe Fluent Icons");
    private static readonly IReadOnlyDictionary<string, Geometry> Shapes = new Dictionary<string, string>
    {
        ["Frame"] = "M1,5 V1 H5 M11,1 H15 V5 M15,11 V15 H11 M5,15 H1 V11 M5,6 L8,4 11,6 11,10 8,12 5,10 Z M5,6 L8,8 11,6 M8,8 V12",
        ["Grid"] = "M1,13 L5,2 H11 L15,13 Z M3,8 H13 M4,5 H12 M6,13 L7,2 M10,13 L9,2",
        ["Collision"] = "M6,3 A2,2 0 1 0 10,3 A2,2 0 1 0 6,3 M8,6 V10 M5.5,8 L8,10.5 10.5,8 M1,13 H15",
        ["Horizon"] = "M1,11 H15 M4,10 A4,4 0 0 1 12,10 M8,2 V3 M2,5 L3,6 M13,6 L14,5",
        ["Wireframe"] = "M2,5 L8,1 14,5 14,11 8,14 2,11 Z M2,5 L8,8 14,5 M8,8 V14 M8,1 L8,8 M2,5 L8,14 M14,5 L8,14",
        ["Textures"] = "M2,1 H14 V13 H2 Z M8,1 V13 M2,7 H14",
        ["Bounds"] = "M2,4 V2 H4 M6,2 H10 M12,2 H14 V4 M14,6 V9 M14,11 V13 H12 M10,13 H6 M4,13 H2 V11 M2,9 V6"
    }.ToDictionary(p => p.Key, p => { Geometry g = Geometry.Parse(p.Value); g.Freeze(); return g; });

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var brush = (Brush)GetValue(ForegroundProperty);
        drawing.PushTransform(new ScaleTransform(ActualWidth / 16, ActualHeight / 16));
        string? glyph = Kind switch { "Map" => "\uE81E", "Effects" => "\uE794", "Camera" => "\uE714", "Fly" => "\uE709", "Lock" => IsChecked ? "\uE72E" : "\uE785", _ => null };
        if (glyph != null)
        {
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Symbols, 16, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(text, new Point((16 - text.Width) / 2, (14 - text.Height) / 2));
        }
        else if (Shapes.TryGetValue(Kind, out var geometry))
        {
            var pen = new Pen(brush, 1.15) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            drawing.DrawGeometry(null, pen, geometry);
            if (Kind == "Textures") { drawing.DrawRectangle(brush, null, new Rect(2, 1, 6, 6)); drawing.DrawRectangle(brush, null, new Rect(8, 7, 6, 6)); }
        }
        // A geometric checked marker accompanies the accent fill, including in high contrast.
        if (IsChecked) drawing.DrawLine(new Pen(brush, 1.5), new Point(5, 15.5), new Point(11, 15.5));
        drawing.Pop();
    }
}

public sealed class PreviewToggleTooltip : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        $"{values[0]} — {(values[1] is true ? "On" : "Off")}\n{values[2]}";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ShortLodLabel : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string text ? text.Split('·')[0].Trim() : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LodItemTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        Application.Current.TryFindResource(container is ContentPresenter { TemplatedParent: ComboBox } ? "LodShortTemplate" : "LodFullTemplate") as DataTemplate;
}
