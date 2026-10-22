using System.Windows;
using System.Windows.Media;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private GroundGridNode? groundGrid;

    /// <summary>Animation-only world XZ reference plane. Simulation is owned by Core.</summary>
    public void SetGroundGrid(bool visible)
    {
        if (animationContext == null) return;
        if (groundGrid == null && visible)
        {
            groundGrid = new()
            {
                Offset = 0, AutoSpacing = true, AutoSpacingRate = 5,
                GridSpacing = 1, GridThickness = .025, FadingFactor = .25,
                PlaneColor = Colors.Transparent, GridColor = Color.FromArgb(155, 145, 161, 180),
                IsHitTestVisible = false, IsGuide = true
            };
            view.Items.Add(groundGrid);
        }
        if (groundGrid != null) groundGrid.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
