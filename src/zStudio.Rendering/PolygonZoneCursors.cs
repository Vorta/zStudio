using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace zStudio.Rendering;

/// <summary>
/// The polygon zone tool's cursors, drawn once: a ring at the hotspot with a badge (an accent + to paint, a white − to
/// erase), and an eyedropper to pick a polygon's zone.
/// </summary>
internal static class PolygonZoneCursors
{
    private const int Size = 32;
    private static readonly Color Accent = Color.FromRgb(0xFF, 0x9A, 0x4D);
    private static Cursor? paint, erase, pick;
    internal static Cursor Paint => paint ??= Create(context => Badge(context, Accent, plus: true), new(12, 12));
    internal static Cursor Erase => erase ??= Create(context => Badge(context, Colors.White, plus: false), new(12, 12));
    internal static Cursor Pick => pick ??= Create(Eyedropper, new(3, 28));

    private static void Ring(DrawingContext context)
    {
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 3.5), new(12, 12), 6, 6);
        context.DrawEllipse(null, new Pen(Brushes.White, 1.5), new(12, 12), 6, 6);
        context.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1), new(12, 12), 1.2, 1.2);
    }
    private static void Badge(DrawingContext context, Color fill, bool plus)
    {
        Ring(context);
        context.DrawEllipse(new SolidColorBrush(fill), new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1), new(23, 23), 6.5, 6.5);
        var mark = new Pen(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), 1.8);
        context.DrawLine(mark, new(19.5, 23), new(26.5, 23));
        if (plus) context.DrawLine(mark, new(23, 19.5), new(23, 26.5));
    }
    private static void Eyedropper(DrawingContext context)
    {
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 3.5);
        var body = new Pen(Brushes.White, 1.6);
        foreach (var pen in new[] { outline, body })
        {
            context.DrawLine(pen, new(3, 28), new(8, 23));
            context.DrawLine(pen, new(8, 23), new(19, 12));
            context.DrawLine(pen, new(16, 9), new(22, 15));
            context.DrawLine(pen, new(21, 8), new(26, 3));
        }
        context.DrawEllipse(Brushes.White, outline, new(23.5, 6.5), 3, 3);
    }

    /// <summary>A 32×32 cursor (32-bit with alpha) from a drawing, with its hotspot.</summary>
    private static Cursor Create(Action<DrawingContext> draw, Point hotspot)
    {
        DrawingVisual visual = new();
        using (var context = visual.RenderOpen()) draw(context);
        RenderTargetBitmap bitmap = new(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] pixels = new byte[Size * Size * 4];
        bitmap.CopyPixels(pixels, Size * 4, 0);
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int mask = Size * 4, image = 40 + Size * Size * 4 + mask;
            writer.Write((short)0); writer.Write((short)2); writer.Write((short)1);
            writer.Write((byte)Size); writer.Write((byte)Size); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((short)hotspot.X); writer.Write((short)hotspot.Y); writer.Write(image); writer.Write(22);
            writer.Write(40); writer.Write(Size); writer.Write(Size * 2); writer.Write((short)1); writer.Write((short)32);
            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            // Bottom-up rows of straight (not premultiplied) BGRA, then an empty AND mask.
            for (int y = Size - 1; y >= 0; y--)
                for (int x = 0; x < Size; x++)
                {
                    int at = (y * Size + x) * 4; byte a = pixels[at + 3];
                    for (int c = 0; c < 3; c++) writer.Write(a == 0 ? (byte)0 : (byte)Math.Min(255, pixels[at + c] * 255 / a));
                    writer.Write(a);
                }
            writer.Write(new byte[mask]);
        }
        stream.Position = 0;
        return new Cursor(stream);
    }
}
