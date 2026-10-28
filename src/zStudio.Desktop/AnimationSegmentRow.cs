using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>
/// A segmented control's options (the Scenario tab's choices, the start threshold's clock). With room they share one row
/// evenly, or each takes its label and an even share of the rest; narrower, a label of several words wraps (its text block
/// uses <see cref="TextWrapping.WrapWithOverflow"/>, never breaking a word) before any option gets less than its longest word;
/// narrower still, the options continue on another row. No label is ever trimmed or clipped. Left-aligned, the control takes
/// only what its labels need.
/// </summary>
internal sealed class AnimationSegmentRow : Panel
{
    private double[] natural = [], least = [];
    /// <summary>Each option's row and width for the width last laid out.</summary>
    private (int Row, double X, double Width)[] placed = [];
    private double[] rowHeights = [];

    protected override Size MeasureOverride(Size available)
    {
        int count = InternalChildren.Count;
        natural = new double[count]; least = new double[count];
        if (count == 0) { placed = []; rowHeights = []; return default; }
        for (int i = 0; i < count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            natural[i] = least[i] = child.DesiredSize.Width;
            // Its longest word with the option's chrome: a measured size is clipped to the space given, so the words are measured as text.
            if (child is ContentControl { Content: TextBlock { Text.Length: > 0 } label })
            {
                var typeface = new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch);
                double pixels = VisualTreeHelper.GetDpi(label).PixelsPerDip;
                double Width(string text) => new FormattedText(text, CultureInfo.CurrentUICulture, label.FlowDirection, typeface, label.FontSize, Brushes.Black, pixels).WidthIncludingTrailingWhitespace;
                least[i] = Math.Min(natural[i], natural[i] - Width(label.Text) + label.Text.Split(' ').Max(Width) + 1);
            }
        }
        double total = natural.Sum();
        double width = double.IsInfinity(available.Width) ? total : available.Width;
        Place(width);
        rowHeights = new double[placed.Length == 0 ? 0 : placed.Max(p => p.Row) + 1];
        for (int i = 0; i < count; i++)
        {
            InternalChildren[i].Measure(new Size(placed[i].Width, double.PositiveInfinity));
            rowHeights[placed[i].Row] = Math.Max(rowHeights[placed[i].Row], InternalChildren[i].DesiredSize.Height);
        }
        return new(Math.Min(width, total), rowHeights.Sum());
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (placed.Length != InternalChildren.Count || Math.Abs(placed.Sum(p => p.Row == 0 ? p.Width : 0) - final.Width) > .5) Place(final.Width);
        double[] tops = new double[rowHeights.Length];
        for (int row = 1; row < tops.Length; row++) tops[row] = tops[row - 1] + rowHeights[row - 1];
        for (int i = 0; i < InternalChildren.Count && i < placed.Length; i++)
        {
            var (row, x, w) = placed[i];
            InternalChildren[i].Arrange(new Rect(x, row < tops.Length ? tops[row] : 0, w, row < rowHeights.Length ? rowHeights[row] : InternalChildren[i].DesiredSize.Height));
        }
        return final;
    }

    /// <summary>Lays the options out <paramref name="width"/> wide: one row when their longest words fit, else as many rows as they need.</summary>
    private void Place(double width)
    {
        int count = natural.Length;
        placed = new (int, double, double)[count];
        // Rows: as many options as their longest words allow (at least one each).
        List<(int First, int Count)> rows = [];
        for (int i = 0; i < count;)
        {
            int first = i; double used = least[i++];
            while (i < count && used + least[i] <= width) used += least[i++];
            rows.Add((first, i - first));
        }
        for (int row = 0; row < rows.Count; row++)
        {
            var (first, length) = rows[row];
            double[] widths = Distribute(width, natural.AsSpan(first, length), least.AsSpan(first, length));
            double x = 0;
            for (int k = 0; k < length; k++) { placed[first + k] = (row, x, widths[k]); x += widths[k]; }
        }
    }

    /// <summary>Each option's width in a row <paramref name="width"/> wide.</summary>
    private static double[] Distribute(double width, ReadOnlySpan<double> natural, ReadOnlySpan<double> least)
    {
        int count = natural.Length;
        double[] widths = new double[count];
        double total = 0, minimum = 0, widest = 0;
        for (int i = 0; i < count; i++) { total += natural[i]; minimum += least[i]; widest = Math.Max(widest, natural[i]); }
        for (int i = 0; i < count; i++)
            widths[i] = width >= widest * count ? width / count
                : width >= total ? natural[i] + (width - total) / count
                : width >= minimum ? least[i] + (total > minimum ? (width - minimum) * (natural[i] - least[i]) / (total - minimum) : 0)
                : width * least[i] / Math.Max(1e-9, minimum);
        return widths;
    }
}
