using System.Buffers.Binary;
using System.Text;

namespace zStudio.Core.Formats;


public static partial class TexturePackWriter
{
    public static void ValidateTextureName(string name)
    { if (name.Length is < 1 or > 31 || name.Any(c => c == 0 || c > 255)) throw new InvalidDataException("Texture names require 1–31 Latin-1 characters without NUL."); }
    internal static ushort Pack(int r, int g, int b) => (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
    internal static int Channel(int color, int channel) => channel switch { 0 => ((color >> 11) & 31) * 255 / 31, 1 => ((color >> 5) & 63) * 255 / 63, _ => (color & 31) * 255 / 31 };
    private sealed class ColorBox(int[] colors, int[] histogram)
    {
        public int[] Colors { get; } = colors;
        public long Weight { get; } = colors.Sum(c => (long)histogram[c]);
        public int[] Ranges { get; } = Enumerable.Range(0, 3).Select(ch => colors.Max(c => Channel(c, ch)) - colors.Min(c => Channel(c, ch))).ToArray();
        public int Range => Ranges.Max();
    }
    /// <summary>Deterministic weighted median cut of an RGB565 histogram to at most <paramref name="maximumColors"/> colours.</summary>
    internal static ushort[] Quantize(int[] histogram, int maximumColors, CancellationToken token)
    {
        int[] colors = Enumerable.Range(0, histogram.Length).Where(c => histogram[c] > 0).ToArray();
        if (colors.Length <= maximumColors) return colors.Select(c => (ushort)c).ToArray();
        List<ColorBox> boxes = [new(colors, histogram)];
        while (boxes.Count < maximumColors && boxes.Any(b => b.Colors.Length > 1))
        {
            token.ThrowIfCancellationRequested();
            var box = boxes.Where(b => b.Colors.Length > 1).OrderByDescending(b => b.Range).ThenByDescending(b => b.Weight).ThenBy(b => b.Colors.Min()).First();
            int channel = Array.IndexOf(box.Ranges, box.Range);
            int[] sorted = box.Colors.OrderBy(c => Channel(c, channel)).ThenBy(c => c).ToArray();
            long sum = 0; int split = 1;
            for (int i = 0; i < sorted.Length - 1; i++) { sum += histogram[sorted[i]]; split = i + 1; if (sum * 2 >= box.Weight) break; }
            boxes.Remove(box); boxes.Add(new(sorted[..split], histogram)); boxes.Add(new(sorted[split..], histogram));
        }
        return boxes.Select(b => Pack((int)(b.Colors.Sum(c => (long)Channel(c, 0) * histogram[c]) / b.Weight),
            (int)(b.Colors.Sum(c => (long)Channel(c, 1) * histogram[c]) / b.Weight), (int)(b.Colors.Sum(c => (long)Channel(c, 2) * histogram[c]) / b.Weight))).Distinct().Order().ToArray();
    }
}
