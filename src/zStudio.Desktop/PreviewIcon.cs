using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace zStudio.Desktop;

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
        // The editor's 16-unit icon set (1.5-unit strokes): commands, toggles, tree types and card actions.
        ["Move"] = "M8,1.5 V14.5 M1.5,8 H14.5 M6,3.5 L8,1.5 10,3.5 M6,12.5 L8,14.5 10,12.5 M3.5,6 L1.5,8 3.5,10 M12.5,6 L14.5,8 12.5,10",
        ["Rotate"] = "M13.5,8 A5.5,5.5 0 1 1 11.9,4.1 M13.5,2 V5.5 H10",
        ["Reset"] = "M2.5,8 A5.5,5.5 0 1 0 4.1,4.1 M2.5,2 V5.5 H6",
        ["AiNodes"] = "M1.9,4 A1.6,1.6 0 1 0 5.1,4 A1.6,1.6 0 1 0 1.9,4 M10.9,4 A1.6,1.6 0 1 0 14.1,4 A1.6,1.6 0 1 0 10.9,4 M6.4,12 A1.6,1.6 0 1 0 9.6,12 A1.6,1.6 0 1 0 6.4,12 M5.1,4 H10.9 M4.3,5.4 L7.2,10.6 M11.7,5.4 L8.8,10.6",
        ["AiThrough"] = "M3,2 H9 A1,1 0 0 1 10,3 V9 A1,1 0 0 1 9,10 H3 A1,1 0 0 1 2,9 V3 A1,1 0 0 1 3,2 Z M6,10 V11.2 M6,13 A1,1 0 0 0 7,14 H8.2 M10,14 H11.8 M13,14 A1,1 0 0 0 14,13 V11.8 M14,10 V8.2 M14,7 A1,1 0 0 0 13,6 H11.8",
        ["Parts"] = "M1,8 Q8,0 15,8 Q8,16 1,8 M6,8 A2,2 0 1 0 10,8 A2,2 0 1 0 6,8",
        ["Parachute"] = "M2,7.5 A6,6 0 0 1 14,7.5 Z M2,7.5 L7.2,12.5 M14,7.5 L8.8,12.5 M6,7.5 L7.4,12.5 M10,7.5 L8.6,12.5 M7.2,12.3 H8.8 A0.4,0.4 0 0 1 9.2,12.7 V14.1 A0.4,0.4 0 0 1 8.8,14.5 H7.2 A0.4,0.4 0 0 1 6.8,14.1 V12.7 A0.4,0.4 0 0 1 7.2,12.3 Z",
        ["Frame"] = "M2,5 V2 H5 M11,2 H14 V5 M14,11 V14 H11 M5,14 H2 V11 M4.5,11 L7,7.5 8.5,9.5 10,8 11.5,11",
        ["FrameSelection"] = "M2,5 V2 H5 M11,2 H14 V5 M14,11 V14 H11 M5,14 H2 V11 M6,8 A2,2 0 1 0 10,8 A2,2 0 1 0 6,8",
        ["Fly"] = "M8,1.5 C8.7,1.5 9.1,2.3 9.1,3.3 V6 L14,9 V10.5 L9.1,9 V12 L10.6,13.2 V14.5 L8,13.8 5.4,14.5 V13.2 L6.9,12 V9 L2,10.5 V9 L6.9,6 V3.3 C6.9,2.3 7.3,1.5 8,1.5 Z",
        ["Grid"] = "M1,13 L5,2 H11 L15,13 Z M3,8 H13 M4,5 H12 M6,13 L7,2 M10,13 L9,2",
        ["Collision"] = "M6,3 A2,2 0 1 0 10,3 A2,2 0 1 0 6,3 M8,6 V10 M5.5,8 L8,10.5 10.5,8 M1,13 H15",
        ["Horizon"] = "M1.5,11.5 H14.5 M4,11.5 A4,4 0 0 1 12,11.5 M8,3.5 V5 M3.4,6.2 L4.4,7.2 M12.6,6.2 L11.6,7.2 M3.5,14 H12.5",
        ["Wireframe"] = "M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 M2,8 H14 M8,2 C10,4 10.6,6 10.6,8 C10.6,10 10,12 8,14 M8,2 C6,4 5.4,6 5.4,8 C5.4,10 6,12 8,14",
        ["FaceOrientation"] = "M1.5,12 L7,9.5 14.5,11 9,13.5 Z M8,10.5 V2.5 M6,4.5 L8,2.5 10,4.5",
        ["Textures"] = "M3.5,3 H12.5 A1.5,1.5 0 0 1 14,4.5 V11.5 A1.5,1.5 0 0 1 12.5,13 H3.5 A1.5,1.5 0 0 1 2,11.5 V4.5 A1.5,1.5 0 0 1 3.5,3 Z M4.65,6.5 A1.1,1.1 0 1 0 6.85,6.5 A1.1,1.1 0 1 0 4.65,6.5 M14,10.5 L10.5,7 4,13",
        ["Bounds"] = "M2.5,5 V2.5 H5 M11,2.5 H13.5 V5 M13.5,11 V13.5 H11 M5,13.5 H2.5 V11 M7,2.5 H9 M7,13.5 H9 M2.5,7 V9 M13.5,7 V9",
        ["Soils"] = "M1.5,5.5 C3.1,4.3 4.5,4.3 5.8,5.5 C7.1,6.7 8.7,6.7 10.2,5.5 C11.7,4.3 13.2,4.3 14.5,5.5 M1.5,9.5 C3.1,8.3 4.5,8.3 5.8,9.5 C7.1,10.7 8.7,10.7 10.2,9.5 C11.7,8.3 13.2,8.3 14.5,9.5 M1.5,13.5 H14.5",
        ["CanModify"] = "M1.5,11 H4.7 A3.3,3.3 0 0 0 11.3,11 H14.5 M5,5 L6.2,6.6 M11,5 L9.8,6.6 M8,2.5 V5.1",
        ["ClipTo"] = "M8,1.8 L13,3.8 V7.8 C13,10.8 10.8,13 8,14.2 C5.2,13 3,10.8 3,7.8 V3.8 Z",
        ["Zones"] = "M3.5,2 H12.5 A1.5,1.5 0 0 1 14,3.5 V12.5 A1.5,1.5 0 0 1 12.5,14 H3.5 A1.5,1.5 0 0 1 2,12.5 V3.5 A1.5,1.5 0 0 1 3.5,2 Z M8,2 V14 M2,8 H14",
        ["Isolate"] = "M2,5 V2 H5 M11,2 H14 V5 M14,11 V14 H11 M5,14 H2 V11 M5,8 H11 M8,5 V11",
        ["Save"] = "M3.5,2.5 H10.5 L13.5,5.5 V12.5 A1,1 0 0 1 12.5,13.5 H3.5 A1,1 0 0 1 2.5,12.5 V3.5 A1,1 0 0 1 3.5,2.5 Z M5,2.5 V5.5 H10 V2.5 M5,13.5 V9.5 H11 V13.5",
        ["Undo"] = "M5.5,3.5 L2.5,6.5 5.5,9.5 M2.5,6.5 H9.5 A3.5,3.5 0 0 1 9.5,13.5 H7",
        ["Redo"] = "M10.5,3.5 L13.5,6.5 10.5,9.5 M13.5,6.5 H6.5 A3.5,3.5 0 0 0 6.5,13.5 H9",
        ["Settings"] = "M2.5,4.5 H9.5 M12.5,4.5 H13.5 M2.5,11.5 H3.5 M6.5,11.5 H13.5 M9.5,4.5 A1.5,1.5 0 1 0 12.5,4.5 A1.5,1.5 0 1 0 9.5,4.5 M3.5,11.5 A1.5,1.5 0 1 0 6.5,11.5 A1.5,1.5 0 1 0 3.5,11.5",
        ["CollapseAll"] = "M5,3.5 L8,6.5 11,3.5 M5,12.5 L8,9.5 11,12.5",
        ["NewGroup"] = "M2.5,3.5 H6 L7.5,5 H13.5 A1,1 0 0 1 14.5,6 V12.5 A1,1 0 0 1 13.5,13.5 H2.5 A1,1 0 0 1 1.5,12.5 V4.5 A1,1 0 0 1 2.5,3.5 Z M8,7.5 V11.5 M6,9.5 H10",
        ["Search"] = "M2.5,7 A4.5,4.5 0 1 0 11.5,7 A4.5,4.5 0 1 0 2.5,7 M10.5,10.5 L14,14",
        ["World"] = "M1.5,13 L6,6 9,10.2 11,7.5 14.5,13 Z M10.2,4 A1.3,1.3 0 1 0 12.8,4 A1.3,1.3 0 1 0 10.2,4",
        ["Folder"] = "M2.5,3.5 H6 L7.5,5 H13.5 A1,1 0 0 1 14.5,6 V12.5 A1,1 0 0 1 13.5,13.5 H2.5 A1,1 0 0 1 1.5,12.5 V4.5 A1,1 0 0 1 2.5,3.5 Z",
        ["Geometry"] = "M8,2 L14,13 H2 Z M5,7.5 H11 M5,7.5 L8,13 11,7.5",
        ["Node"] = "M8,2 L14,8 8,14 2,8 Z",
        // The later passes: AI vehicles (a tank), pickups (a power-up) and map animations (a film strip).
        ["Tank"] = "M3.5,9.5 H12.5 A2,2 0 0 1 12.5,13.5 H3.5 A2,2 0 0 1 3.5,9.5 Z M5,9.5 V7.5 A1,1 0 0 1 6,6.5 H9.5 A1,1 0 0 1 10.5,7.5 V9.5 M10.5,7.5 H14.5 M5.5,11.5 H10.5",
        ["Pickup"] = "M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 M8.9,4 L6.2,8.4 H9.8 L7.1,12",
        ["Animation"] = "M3,2.5 H13 A0.5,0.5 0 0 1 13.5,3 V13 A0.5,0.5 0 0 1 13,13.5 H3 A0.5,0.5 0 0 1 2.5,13 V3 A0.5,0.5 0 0 1 3,2.5 Z M5,2.5 V13.5 M11,2.5 V13.5 M2.5,5.5 H5 M2.5,8 H5 M2.5,10.5 H5 M11,5.5 H13.5 M11,8 H13.5 M11,10.5 H13.5",
        ["Info"] = "M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 M8,7.3 V11.2 M8,4.9 V5",
        // An object a mission animation binds to: a moving ball with speed lines.
        ["Animated"] = "M8.5,8 A3,3 0 1 0 14.5,8 A3,3 0 1 0 8.5,8 M1.5,5.5 H6 M1,8 H5.5 M1.5,10.5 H6",
        ["Landmark"] = "M4,14.5 V1.5 M4,2.5 H12.5 L10.5,5.25 12.5,8 H4",
        ["Eye"] = "M1.5,8 C1.5,8 4,3.5 8,3.5 C12,3.5 14.5,8 14.5,8 C14.5,8 12,12.5 8,12.5 C4,12.5 1.5,8 1.5,8 Z M6,8 A2,2 0 1 0 10,8 A2,2 0 1 0 6,8",
        ["EyeOff"] = "M1.5,8 C1.5,8 4,3.5 8,3.5 C12,3.5 14.5,8 14.5,8 C14.5,8 12,12.5 8,12.5 C4,12.5 1.5,8 1.5,8 Z M6,8 A2,2 0 1 0 10,8 A2,2 0 1 0 6,8 M2.5,2.5 L13.5,13.5",
        ["PopOut"] = "M9.5,2.5 H13.5 V6.5 M13.5,2.5 L8,8 M12,9.5 V12.5 A1,1 0 0 1 11,13.5 H3.5 A1,1 0 0 1 2.5,12.5 V5 A1,1 0 0 1 3.5,4 H6.5",
        ["DockRight"] = "M3.5,2.5 H12.5 A1,1 0 0 1 13.5,3.5 V12.5 A1,1 0 0 1 12.5,13.5 H3.5 A1,1 0 0 1 2.5,12.5 V3.5 A1,1 0 0 1 3.5,2.5 Z M9.5,2.5 V13.5",
        ["Pointer"] = "M3.5,2.5 L12.5,7.7 8.5,8.7 6.5,12.5 Z",
        ["Warning"] = "M8,2 L14.5,13.5 H1.5 Z M8,6.5 V9.5 M8,11.5 V11.6",
        // Card glyphs: the object, edit, discard, apply, close, a section chevron and the surface probe.
        ["Cube"] = "M8,1.5 L14,4.75 V11.25 L8,14.5 L2,11.25 V4.75 Z M2,4.75 L8,8 L14,4.75 M8,8 V14.5",
        ["Edit"] = "M10.5,2.5 L13.5,5.5 L5.5,13.5 H2.5 V10.5 Z M9,4 L12,7",
        ["Discard"] = "M5.5,3.5 L2.5,6.5 L5.5,9.5 M2.5,6.5 H9.5 A3.5,3.5 0 0 1 9.5,13.5 H7",
        ["Apply"] = "M3,8.5 L6,11.5 L13,4.5",
        ["Close"] = "M4,4 L12,12 M12,4 L4,12",
        ["Chevron"] = "M4,6 L8,10 L12,6",
        ["Probe"] = "M3,8 A5,5 0 1 0 13,8 A5,5 0 1 0 3,8 M8,1 V4 M8,12 V15 M1,8 H4 M12,8 H15"
    }.ToDictionary(p => p.Key, p => { Geometry g = Geometry.Parse(p.Value); g.Freeze(); return g; });

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var brush = (Brush)GetValue(ForegroundProperty);
        drawing.PushTransform(new ScaleTransform(ActualWidth / 16, ActualHeight / 16));
        string? glyph = Kind switch { "Map" => "\uE81E", "Effects" => "\uE794", "Camera" => "\uE714", "Back" => "\uE72B", "Lock" => IsChecked ? "\uE72E" : "\uE785", "Unlock" => IsChecked ? "\uE785" : "\uE72E", _ => null };
        if (glyph != null)
        {
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Symbols, 16, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(text, new Point((16 - text.Width) / 2, (14 - text.Height) / 2));
        }
        else if (Shapes.TryGetValue(Kind, out var geometry))
        {
            var pen = new Pen(brush, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            drawing.DrawGeometry(null, pen, geometry);
            if (Kind == "Node") drawing.DrawEllipse(brush, null, new Point(8, 8), 1.2, 1.2);
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
