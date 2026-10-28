using System.Windows;
using System.Windows.Media;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>
/// The Structure view's colours, pens, fonts and glyphs (design/animation-redesign.md §8; the dark and light token sets of the
/// mockup). One frozen palette per theme; the view asks for the current one when it draws.
/// </summary>
internal sealed class AnimationGraphPalette
{
    private static AnimationGraphPalette? dark, light;
    internal static AnimationGraphPalette For(bool isDark) => isDark ? dark ??= new(true) : light ??= new(false);
    /// <summary>The theme an element shows: dark when its primary text is light.</summary>
    internal static bool IsDark(FrameworkElement element) =>
        element.TryFindResource("TextFillColorPrimaryBrush") is not SolidColorBrush { Color: var text } || text.R + text.G + text.B > 382;

    public bool Dark { get; }
    public Brush Node, NodeBorder, Frame, FrameBorder, HeaderHover, Well, WellBorder, Selected, T1, T2, T3, T4, Accent, OnAccent, Run, Caution, CautionBack,
        Bad, Hatch, Divider, Control, Pane;
    public Brush State, Motion, Camera, Sound, Effect, Call, Release, Stop, Prerequisite;
    public Pen NodePen, FramePen, CleanupPen, WellPen, DividerPen, AccentOutline, RunOutline;
    public Typeface Text { get; } = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public Typeface Strong { get; } = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    public Typeface Italic { get; } = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    public Typeface Mono { get; } = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public DrawingBrush Dots { get; }
    public DrawingBrush HatchBrush { get; }

    private AnimationGraphPalette(bool isDark)
    {
        Dark = isDark;
        Brush B(uint argb) { var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)); brush.Freeze(); return brush; }
        Brush W(double alpha) => B(((uint)Math.Round(alpha * 255) << 24) | 0xFFFFFF);
        Brush K(double alpha) => B((uint)Math.Round(alpha * 255) << 24);
        if (isDark)
        {
            Node = B(0xFF2F2F2F); NodeBorder = W(.09); Frame = W(.025); FrameBorder = W(.09); HeaderHover = W(.04); Well = K(.20); WellBorder = W(.06);
            Selected = W(.0837); T1 = B(0xFFFFFFFF); T2 = W(.786); T3 = W(.6); T4 = W(.45); Accent = B(0xFFFF9A4D); OnAccent = B(0xFF1A1A1A);
            Run = B(0xFF7BD88F); Caution = B(0xFFFCE100); CautionBack = B(0xFF433519); Bad = B(0xFFFF99A4); Hatch = W(.10); Divider = W(.0837);
            Control = W(.0605); Pane = B(0xFF272727);
            State = B(0xFF9DB4C8); Motion = B(0xFF6FC8D6); Camera = B(0xFFF5A742); Sound = B(0xFFB9A6FF); Effect = B(0xFFF2C46D); Call = W(.72);
            Release = B(0xFF7BD88F); Stop = B(0xFFFF99A4); Prerequisite = B(0xFFC9A2FF);
        }
        else
        {
            Node = B(0xFFFFFFFF); NodeBorder = K(.12); Frame = K(.022); FrameBorder = K(.12); HeaderHover = K(.037); Well = K(.035); WellBorder = K(.0578);
            Selected = K(.045); T1 = K(.896); T2 = K(.66); T3 = K(.52); T4 = K(.42); Accent = B(0xFFB85406); OnAccent = B(0xFFFFFFFF);
            Run = B(0xFF2F8A47); Caution = B(0xFF9D5D00); CautionBack = B(0xFFFFF4CE); Bad = B(0xFFC42B1C); Hatch = K(.09); Divider = K(.0803);
            Control = B(0xB3FFFFFF); Pane = B(0xFFFAFAFA);
            State = B(0xFF4D6478); Motion = B(0xFF1F7F8C); Camera = B(0xFFB25E00); Sound = B(0xFF6E56C2); Effect = B(0xFF8A6200); Call = K(.62);
            Release = B(0xFF2F8A47); Stop = B(0xFFC42B1C); Prerequisite = B(0xFF7A4FC4);
        }
        Pen P(Brush brush, double thickness, DashStyle? dash = null) { var pen = new Pen(brush, thickness) { DashStyle = dash ?? DashStyles.Solid, StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round }; pen.Freeze(); return pen; }
        NodePen = P(NodeBorder, 1); FramePen = P(FrameBorder, 1); CleanupPen = P(isDark ? W(.22) : K(.28), 1, new DashStyle([3, 2], 0));
        WellPen = P(WellBorder, 1); DividerPen = P(Divider, 1); AccentOutline = P(Accent, 2); RunOutline = P(Run, 1);
        var dot = new GeometryDrawing(isDark ? W(.055) : K(.07), null, new EllipseGeometry(new Point(1, 1), .6, .6));
        Dots = new DrawingBrush(dot) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 14, 14), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 14, 14), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        Dots.Freeze();
        var line = new GeometryDrawing(null, P(Hatch, 1), Geometry.Parse("M0,6 L6,0 M-1,1 L1,-1 M5,7 L7,5"));
        HatchBrush = new DrawingBrush(line) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 6, 6), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        HatchBrush.Freeze();
    }

    /// <summary>The colour of an event family (shape tells the family too, so colour is never the only cue).</summary>
    public Brush Family(AnimationEventFamily family) => family switch
    {
        AnimationEventFamily.State => State, AnimationEventFamily.Motion => Motion, AnimationEventFamily.Camera => Camera, AnimationEventFamily.Sound => Sound,
        AnimationEventFamily.Effect => Effect, AnimationEventFamily.Release => Release, AnimationEventFamily.Stop => Stop, AnimationEventFamily.Call => Call, _ => T2,
    };
    public Brush Relation(AnimationGraphRelation relation) => relation switch
    {
        AnimationGraphRelation.Release or AnimationGraphRelation.ContactRelease => Release,
        AnimationGraphRelation.StopSequence or AnimationGraphRelation.StopAnimation or AnimationGraphRelation.ResetAnimation or AnimationGraphRelation.InvalidateAnimation => Stop,
        AnimationGraphRelation.Prerequisite => Prerequisite,
        _ => T2,
    };
}

/// <summary>The graph's glyphs: 16×16 stroked outlines (the mockup's set), drawn at any size.</summary>
internal static class AnimationGraphGlyphs
{
    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["state"] = "M5,4.5 A4.5,4.5 0 1 0 11,4.5 M8,2 V7.5", ["motion"] = "M3,13 L13,3 M7.5,3 H13 V8.5", ["camera"] = "M2,5 H10 V12 H2 Z M10,7 L14,5 V12 L10,10",
        ["sound"] = "M2,6 H5 L9,3 V13 L5,10 H2 Z M11.5,6 A3,3 0 0 1 11.5,10", ["effect"] = "M8,1.5 L9.3,6.7 14.5,8 9.3,9.3 8,14.5 6.7,9.3 1.5,8 6.7,6.7 Z",
        ["call"] = "M2,8 H11 M8,5 L11,8 8,11 M13.5,3.5 V12.5", ["release"] = "M4,3 L12,8 4,13 Z", ["stop"] = "M4,4 H12 V12 H4 Z",
        ["invalidate"] = "M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 M3.8,3.8 L12.2,12.2", ["loop"] = "M13,8 A5,5 0 1 1 11.5,4.5 M13,2 V5 H10",
        ["condition"] = "M8,1.5 L14.5,8 8,14.5 1.5,8 Z", ["game"] = "M2.5,4.5 H13.5 V11.5 H2.5 Z M5,8 H7 M6,7 V9 M10,7.5 H10.1 M11.5,8.5 H11.6",
        ["key"] = "M2,8 A3,3 0 1 0 8,8 A3,3 0 1 0 2,8 M8,8 H14 M12,8 V11", ["back"] = "M13,5 H6 A3,3 0 0 0 6,11 H10 M8.5,2.5 L6,5 8.5,7.5",
        ["pose"] = "M3,13 V3 H13 V13 Z M3,8 H13", ["attach"] = "M3,8 A5,5 0 1 0 13,8 A5,5 0 1 0 3,8 M8,1 V4 M8,12 V15 M1,8 H4 M12,8 H15",
        ["hit"] = "M8,1.5 L9.3,6.7 14.5,8 9.3,9.3 8,14.5 6.7,9.3 1.5,8 6.7,6.7 Z", ["cleanup"] = "M2.5,8 A5.5,5.5 0 1 0 4.1,4.1 M2.5,2 V5.5 H6",
        ["start"] = "M4,3 L12,8 4,13 Z M2,2 V14", ["range"] = "M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 M8,8 H14", ["definition"] = "M4,1.5 H9.5 L12.5,4.5 V14 A0.5,0.5 0 0 1 12,14.5 H4 A0.5,0.5 0 0 1 3.5,14 V2 A0.5,0.5 0 0 1 4,1.5 Z M9.5,1.5 V4.5 H12.5 M6.5,7.5 V12.5 L10.5,10 Z",
        ["chevron"] = "M4,6 L8,10 L12,6", ["chevronRight"] = "M6,4 L10,8 L6,12", ["open"] = "M5,3 L12.5,8 5,13 Z", ["warning"] = "M8,2 L14.5,13.5 H1.5 Z M8,6.5 V9.5 M8,11.5 V11.6",
        ["clock"] = "M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 M8,4.5 V8 L10.5,9.5", ["fit"] = "M2,5 V2 H5 M11,2 H14 V5 M14,11 V14 H11 M5,14 H2 V11",
        ["zoomIn"] = "M2.5,7 A4.5,4.5 0 1 0 11.5,7 A4.5,4.5 0 1 0 2.5,7 M10.5,10.5 L14,14 M5,7 H9 M7,5 V9", ["zoomOut"] = "M2.5,7 A4.5,4.5 0 1 0 11.5,7 A4.5,4.5 0 1 0 2.5,7 M10.5,10.5 L14,14 M5,7 H9",
        ["legend"] = "M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 M8,7.3 V11.2 M8,4.9 V5", ["pause"] = "M5.5,3.5 V12.5 M10.5,3.5 V12.5", ["check"] = "M3,8.5 L6.5,12 L13,4.5",
    };
    public static Geometry Get(string name)
    {
        if (Cache.TryGetValue(name, out var geometry)) return geometry;
        geometry = Geometry.Parse(Paths.GetValueOrDefault(name) ?? Paths["state"]); geometry.Freeze();
        return Cache[name] = geometry;
    }
    public static string Family(AnimationGraphRow row) => row.Type switch
    {
        30 => "loop", 31 or 33 => "condition", 32 or 34 => "condition", 35 => "game", 12 => "key", 27 => "invalidate", 26 => "cleanup",
        _ => row.Family switch
        {
            AnimationEventFamily.State => "state", AnimationEventFamily.Motion => "motion", AnimationEventFamily.Camera => "camera", AnimationEventFamily.Sound => "sound",
            AnimationEventFamily.Effect => "effect", AnimationEventFamily.Call => "call", AnimationEventFamily.Release => "release", AnimationEventFamily.Stop => "stop",
            AnimationEventFamily.Game => "game", _ => "state",
        },
    };
    public static string Trigger(string kind) => kind switch
    {
        "whenCalled" => "call", "onStartup" or "newGameStart" => "start", "weaponHit" or "collisionHit" or "weaponOrCollisionHit" => "hit", "rangeGate" => "range",
        "zoneGate" => "range", "loadCleanup" or "cleansUp" => "cleanup", "poseKept" or "oneShot" => "pose", "attach" => "attach", _ => "start",
    };

    /// <summary>Draws a glyph with its top left at <paramref name="at"/>, <paramref name="size"/> wide, stroked in <paramref name="brush"/>.</summary>
    public static void Draw(DrawingContext dc, string name, Point at, double size, Brush brush)
    {
        // The viewBox's 1.5 stroke scales with the glyph, as in the mockup's SVG.
        double scale = size / 16;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, at.X, at.Y));
        dc.DrawGeometry(null, new Pen(brush, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Get(name));
        dc.Pop();
    }
}
