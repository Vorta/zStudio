using System.Windows;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

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
        WorldHighlightMode.Zones => "zones",
        _ => "none"
    };

    private void WorldHighlightChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || synchronizingWorldHighlight) return;
        var mode = sender == HighlightSoils ? WorldHighlightMode.NonDefaultSoils :
            sender == HighlightCanModify ? WorldHighlightMode.CanModify : sender == HighlightZones ? WorldHighlightMode.Zones : WorldHighlightMode.ClipTo;
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
            HighlightCanModify.IsChecked = mode == WorldHighlightMode.CanModify;
            HighlightClipTo.IsChecked = mode == WorldHighlightMode.ClipTo;
            HighlightZones.IsChecked = mode == WorldHighlightMode.Zones;
        }
        finally { synchronizingWorldHighlight = false; }
        ApplyWorldHighlight();
    }

    private void ApplyWorldHighlight() => scene?.SetWorldHighlightMode(shownAsset?.Kind == AssetKind.World ? worldHighlightMode : WorldHighlightMode.None);
}
