using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>
/// The Outlines pane's column in ZMAP mode. The header, the Height cutoff section and the Terrain zones header keep their
/// heights; the outlines list and the zone rows share what is left. While both fit, the zone rows show whole and the list
/// takes the rest, so nothing scrolls. When they do not, the zone rows still show whole while that leaves the list half the
/// room or four rows (the list alone scrolls: an outline can also be picked in the view, a zone only here); otherwise each is
/// entitled to half the room and one that needs less leaves the rest to the other. Zone rows that scroll show whole rows,
/// and what the list says while it is empty is never cut.
/// </summary>
internal sealed class ZmapOutlinesLayout : Panel
{
    private readonly FrameworkElement header, empty, cutoff, zonesHeader;
    private readonly ListBox list;
    private readonly ScrollViewer zones;
    private readonly Panel zoneRows;
    private readonly double listRow, zoneRow;
    private double zonesRoom, zonesWhole, zonesRow, grain = 1000;
    private (int Rows, double Scale) zonesMeasured = (-1, 0);
    private (double Height, double Scale) listRowSeen;

    /// <summary>The zone rows never show fewer than this many while they scroll.</summary>
    private const int FewestZoneRows = 2;
    /// <summary>The list keeps this many rows where the zone rows show whole at its cost.</summary>
    private const int FewestListRows = 4;

    internal ZmapOutlinesLayout(FrameworkElement header, ListBox list, FrameworkElement empty, FrameworkElement cutoff, FrameworkElement zonesHeader, ScrollViewer zones,
        double listRow, double zoneRow)
    {
        (this.header, this.list, this.empty, this.cutoff, this.zonesHeader, this.zones, this.listRow, this.zoneRow) = (header, list, empty, cutoff, zonesHeader, zones, listRow, zoneRow);
        zoneRows = (Panel)zones.Content;
        ClipToBounds = true;
        // In reading order, which is the tab order.
        foreach (var part in new UIElement[] { header, list, empty, cutoff, zonesHeader, zones }) Children.Add(part);
        // A list that already scrolls keeps its size when a row comes or goes, yet what it asks for changes.
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, _) => InvalidateMeasure();
        // A scrolled list that grows until every row shows keeps its scrollbar's gutter (the bar is decided before the
        // rows return to the top): with nothing left to scroll, it is measured once more.
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is ScrollViewer { ComputedVerticalScrollBarVisibility: Visibility.Visible, ScrollableHeight: 0 } scroll) scroll.InvalidateMeasure();
        }));
    }

    protected override Size MeasureOverride(Size available)
    {
        Size open = new(available.Width, double.PositiveInfinity);
        foreach (var part in new[] { header, cutoff, zonesHeader, empty }) part.Measure(open);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        // Heights are compared as whole numbers of device pixels, which is what the layout rounds every part to (without
        // layout rounding, of thousandths): in DIP an exact fit can come out a hair short (507.99999999999994 for 508,
        // or rows of 34 pixels at 115 DPI), and that would cost a row that fits.
        grain = UseLayoutRounding ? scale : 1000;
        bool listed = empty.Visibility != Visibility.Visible;
        MeasureZoneRows(available.Width, scale);
        double zonesWanted = Grains(zonesWhole), row = Math.Max(1, Grains(zonesRow)), edges = Math.Max(0, zonesWanted - zoneRows.Children.Count * row);
        double room = Room(available.Height), listEdges = Grains(list.Margin.Top + list.Margin.Bottom), listWanted, zonesNow;
        for (bool again = false; ; again = true)
        {
            // The list is virtualized, so its whole height is counted from a row's, not measured.
            double listRowNow = Grains(ListRow(scale));
            listWanted = listed ? list.Items.Count * listRowNow + listEdges : Grains(empty.DesiredSize.Height);
            zonesNow = zonesWanted;
            if (listWanted + zonesWanted > room)
            {
                double half = Math.Floor(room / 2);
                double listKept = FewestListRows * listRowNow + listEdges;
                double share = !listed ? room - listWanted : zonesWanted <= Math.Max(half, room - listKept) ? zonesWanted : Math.Max(half, room - listWanted);
                if (share < zonesWanted)
                    share = Math.Min(zonesWanted, Math.Max(Math.Floor((share - edges) / row), FewestZoneRows) * row + edges);
                zonesNow = Math.Min(room, share);
            }
            zonesRoom = zonesNow / grain;
            double listRoom = (double.IsInfinity(room) ? listWanted : room - zonesNow) / grain;
            // The lists count the rows that fit by subtracting one after another, and heights that are no binary
            // fractions (a row rounded to pixels at 115 DPI) can leave them a rounding error short of the last row, which
            // would then scroll. So each is measured a pixel higher than it is arranged (the layout rounds less away);
            // the list only while its rows fit, since a list that scrolls would be cut by that pixel.
            double spare = UseLayoutRounding ? 1 / scale : .01;
            zones.Measure(new(available.Width, zonesRoom + spare));
            list.Measure(new(available.Width, listRoom + (listed && listWanted <= room - zonesNow ? spare : 0)));
            // The list's rows are made in that measure. Where they were not there before (on entering the mode) or were
            // measured for another scale, their height shows only now: if it is not the one counted with, the room is
            // shared once more.
            if (again || !listed || Grains(ListRow(scale)) == listRowNow) break;
        }
        double width = double.IsInfinity(available.Width) ? Children.Cast<UIElement>().Max(part => part.DesiredSize.Width) : available.Width;
        return new(width, double.IsInfinity(available.Height) ? (Kept + listWanted + zonesNow) / grain : available.Height);
    }

    /// <summary>
    /// The zone rows' whole height and one row's, as they are laid out: at a display scale that is no multiple of 25 % a
    /// row is rounded to whole pixels and is no longer the height it was given. The scroller is measured at its whole
    /// height for that, which returns it to its first row, so not while it is scrolled unless the rows changed.
    /// </summary>
    private void MeasureZoneRows(double width, double scale)
    {
        if (zones.Visibility != Visibility.Visible) { (zonesWhole, zonesRow, zonesMeasured) = (0, zoneRow, (-1, 0)); return; }
        var now = (zoneRows.Children.Count, scale);
        double scrolled = zones.VerticalOffset;
        if (scrolled > 0 && now == zonesMeasured) return;
        zones.Measure(new(width, double.PositiveInfinity));
        // Scrolled rows whose count or scale changed (the window moved to another monitor) return to their row.
        if (scrolled > 0) zones.ScrollToVerticalOffset(scrolled);
        zonesWhole = zones.DesiredSize.Height;
        zonesRow = zoneRows.Children.Count > 0 && zoneRows.Children[0].DesiredSize.Height > 0 ? zoneRows.Children[0].DesiredSize.Height : zoneRow;
        zonesMeasured = now;
    }

    /// <summary>
    /// A list row's height: a row that is laid out says it (rounded to whole pixels as the zone rows are). While none is
    /// (the list's rows are replaced when an outline is added, removed or moved), the last one seen at this scale stands,
    /// and before any was seen the height the rows are given.
    /// </summary>
    private double ListRow(double scale)
    {
        for (int i = 0, count = Math.Min(list.Items.Count, 64); i < count; i++)
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is UIElement { DesiredSize.Height: > 0 } row)
            {
                listRowSeen = (row.DesiredSize.Height, scale);
                return row.DesiredSize.Height;
            }
        return listRowSeen.Scale == scale ? listRowSeen.Height : listRow;
    }

    /// <summary>A height in the whole units heights are compared in: device pixels, or thousandths without layout rounding.</summary>
    private double Grains(double height) => Math.Round(height * grain);

    private double Kept => Grains(header.DesiredSize.Height) + Grains(cutoff.DesiredSize.Height) + Grains(zonesHeader.DesiredSize.Height);

    /// <summary>What the two lists share of a height, in those units; a pixel that is not whole is no room.</summary>
    private double Room(double height) => Math.Max(0, Math.Floor(height * grain + .001) - Kept);

    protected override Size ArrangeOverride(Size final)
    {
        double top = 0;
        double listRoom = Math.Max(0, Room(final.Height) - Grains(zonesRoom)) / grain;
        Place(header, header.DesiredSize.Height);
        // The list and what it says while it is empty take the same place.
        empty.Arrange(new(0, top, final.Width, listRoom));
        Place(list, listRoom);
        Place(cutoff, cutoff.DesiredSize.Height);
        Place(zonesHeader, zonesHeader.DesiredSize.Height);
        Place(zones, zonesRoom);
        return final;

        void Place(UIElement part, double height)
        {
            part.Arrange(new(0, top, final.Width, height));
            top += height;
        }
    }
}
