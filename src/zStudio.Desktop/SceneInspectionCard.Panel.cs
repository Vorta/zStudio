using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Recoil.Zbd.Desktop;

internal sealed partial class SceneInspectionCard
{
    private const double HoverHeight = 90;
    private readonly TextBlock hoverName = new() { Name = "InspectionHoverName", FontWeight = FontWeights.SemiBold,
        FontSize = 12, Height = 20, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock[] hoverSurface = HoverComponents("InspectionSurface"), hoverOrigin = HoverComponents("InspectionOrigin");

    private static TextBlock[] HoverComponents(string name) => Enumerable.Range(0, 3).Select(i => new TextBlock
    { Name = name + "XYZ"[i], FontSize = 12, Height = 18, Margin = new(0, 0, i == 2 ? 0 : 10, 0) }).ToArray();

    private Border CreatePanel()
    {
        StackPanel readout = new(); readout.Children.Add(hoverName);
        AddVector("Surface", "Surface world XYZ", hoverSurface); AddVector("Origin", "Object origin XYZ", hoverOrigin);
        var hoverBox = Box(readout); hoverBox.Name = "InspectionHover"; hoverBox.Height = HoverHeight;
        Grid contents = new(); contents.RowDefinitions.Add(new() { Height = GridLength.Auto });
        contents.RowDefinitions.Add(new() { Height = GridLength.Auto });
        contents.Children.Add(hoverBox); SetRow(card, 1); contents.Children.Add(card);
        Border result = new() { Name = "InspectionPanel", Child = contents, CornerRadius = new(6),
            BorderBrush = card.BorderBrush, BorderThickness = new(1), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new(10), ClipToBounds = true };
        result.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        // Let native fields/buttons/scrolling run first; unused panel space and
        // scroll limits still consume input instead of acting on the scene behind.
        result.MouseWheel += (_, e) => e.Handled = true;
        result.MouseDown += (_, e) => e.Handled = true;
        return result;

        void AddVector(string label, string tooltip, TextBlock[] components)
        {
            Grid row = new() { Height = 24, ToolTip = tooltip };
            row.ColumnDefinitions.Add(new() { Width = new(46) });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, Opacity = .75, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            StackPanel coordinates = new() { Orientation = Orientation.Horizontal };
            foreach (var component in components) coordinates.Children.Add(component);
            // Keep full round-trip numbers visible even in a constrained panel;
            // ordinary widths retain the normal font size without stretching.
            Viewbox fit = new() { Child = coordinates, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            SetColumn(fit, 1); row.Children.Add(fit);
            readout.Children.Add(row);
        }
    }

    private void RefreshHover()
    {
        var hovered = viewport.HoverInspection;
        hoverName.Text = hovered == null ? "Point at geometry or an AI node" : Display(describe(hovered)["Node"]);
        hoverName.ToolTip = hoverName.Text;
        SetVector(hoverSurface, hovered?.Surface); SetVector(hoverOrigin, hovered?.Origin);

        static void SetVector(TextBlock[] components, Vector3? value)
        {
            for (int i = 0; i < components.Length; i++)
            {
                string text = "XYZ"[i] + " " + (value?[i].ToString("R", CultureInfo.InvariantCulture) ?? "—");
                components[i].Text = text; components[i].ToolTip = text;
            }
        }
    }

    private void ArrangePanel()
    {
        panel.Width = Math.Max(0, Math.Min(350, ActualWidth - 20));
        double available = Math.Max(0, ActualHeight - 20);
        var cube = viewport.NavigationCubeHomeBounds;
        if (!cube.IsEmpty && cube.Right > ActualWidth - 10 - panel.Width)
        {
            double aboveCube = Math.Max(0, cube.Top - 20);
            // Never reserve the cube's vertical strip at the cost of clipping
            // the actions or leaving no usable details viewport. At short
            // heights reserve a side slot instead; this depends on viewport
            // size, not selection/edit state, so the header/fields stay stable.
            if (aboveCube >= HoverHeight + 2 + 160) available = Math.Min(available, aboveCube);
            else panel.Width = Math.Max(0, Math.Min(panel.Width, ActualWidth - 30 - Math.Min(cube.Width, 60)));
        }
        panel.MaxHeight = available;
        card.Height = Math.Max(0, Math.Min(340, available - HoverHeight - 2));
        viewport.ReserveInspectionPanel(new Rect(Math.Max(0, ActualWidth - 10 - panel.Width), 10, panel.Width,
            Math.Min(available, HoverHeight + 2 + (card.Visibility == Visibility.Visible ? card.Height : 0))));
    }
}
