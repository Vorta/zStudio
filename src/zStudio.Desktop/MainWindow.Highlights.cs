using System.Windows;
using zStudio.Core;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private WorldHighlightMode worldHighlightMode;
    private long worldHighlightGeneration;
    private bool synchronizingWorldHighlight;

    private static string WorldHighlightName(WorldHighlightMode mode) => mode switch
    {
        WorldHighlightMode.NonDefaultSoils => "nonDefaultSoils",
        WorldHighlightMode.CanModify => "canModify",
        WorldHighlightMode.ClipTo => "clipTo",
        WorldHighlightMode.CanModifyAndClipTo => "canModifyAndClipTo",
        WorldHighlightMode.Zones => "zones",
        WorldHighlightMode.PolygonZones => "polygonZones",
        WorldHighlightMode.Landmarks => "landmarks",
        _ => "none"
    };

    private void WorldHighlightChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || synchronizingWorldHighlight) return;
        if (sender == HighlightCanModify || sender == HighlightClipTo)
        {
            SetWorldHighlightMode((HighlightCanModify.IsChecked == true, HighlightClipTo.IsChecked == true) switch
            {
                (true, true) => WorldHighlightMode.CanModifyAndClipTo,
                (true, false) => WorldHighlightMode.CanModify,
                (false, true) => WorldHighlightMode.ClipTo,
                _ => WorldHighlightMode.None
            });
            return;
        }
        var mode = sender == HighlightSoils ? WorldHighlightMode.NonDefaultSoils : sender == HighlightZones ? WorldHighlightMode.Zones :
            sender == HighlightPolygonZones ? WorldHighlightMode.PolygonZones : sender == HighlightLandmarks ? WorldHighlightMode.Landmarks : WorldHighlightMode.ClipTo;
        SetWorldHighlightMode(((System.Windows.Controls.Primitives.ToggleButton)sender).IsChecked == true ? mode : WorldHighlightMode.None);
    }

    private void SetWorldHighlightMode(WorldHighlightMode mode)
    {
        ++worldHighlightGeneration;
        worldHighlightMode = mode;
        synchronizingWorldHighlight = true;
        try
        {
            HighlightSoils.IsChecked = mode == WorldHighlightMode.NonDefaultSoils;
            HighlightCanModify.IsChecked = mode is WorldHighlightMode.CanModify or WorldHighlightMode.CanModifyAndClipTo;
            HighlightClipTo.IsChecked = mode is WorldHighlightMode.ClipTo or WorldHighlightMode.CanModifyAndClipTo;
            HighlightZones.IsChecked = mode == WorldHighlightMode.Zones;
            HighlightPolygonZones.IsChecked = mode == WorldHighlightMode.PolygonZones;
            HighlightLandmarks.IsChecked = mode == WorldHighlightMode.Landmarks;
        }
        finally { synchronizingWorldHighlight = false; }
        ApplyWorldHighlight();
    }

    private void ApplyWorldHighlight() => scene?.SetWorldHighlightMode(shownAsset?.Kind == AssetKind.World ? worldHighlightMode : WorldHighlightMode.None);
}
