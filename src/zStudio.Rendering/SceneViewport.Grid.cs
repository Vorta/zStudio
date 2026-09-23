using System.Windows;
using System.Windows.Media;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    private AxisPlaneGridModel3D? groundGrid;

    /// <summary>Animation-only world XZ reference plane. Simulation is owned by Core.</summary>
    public void SetGroundGrid(bool visible)
    {
        if (animationContext == null) return;
        if (groundGrid == null && visible)
        {
            groundGrid = new()
            {
                UpAxis = Axis.Y, Offset = 0, AutoSpacing = true, AutoSpacingRate = 5,
                GridPattern = GridPattern.Grid, GridSpacing = 1, GridThickness = .025, FadingFactor = .25,
                PlaneColor = Colors.Transparent, GridColor = Color.FromArgb(155, 145, 161, 180),
                RenderShadowMap = false, IsHitTestVisible = false
            };
            viewport.Items.Add(groundGrid);
        }
        if (groundGrid != null) groundGrid.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
