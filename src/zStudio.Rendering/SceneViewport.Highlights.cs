using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    private sealed record SurfaceAppearance(DiffuseMaterial Normal, WorldSurfaceKind Kind, TextureModel? AlphaMask, bool Horizon, int Zone = 0xFF);
    private readonly Dictionary<MeshGeometryModel3D, SurfaceAppearance> surfaceAppearances = [];
    private readonly Dictionary<(DiffuseMaterial Normal, WorldHighlightMode Mode, int Zone), DiffuseMaterial> highlightMaterials = [];
    public WorldHighlightMode HighlightMode { get; private set; }

    /// <summary>Changes only appearance; mesh/instance identities, picking and camera stay intact.</summary>
    public void SetWorldHighlightMode(WorldHighlightMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        HighlightMode = mode;
        foreach (var (mesh, surface) in surfaceAppearances)
        {
            if (!WorldSurfaceHighlights.Matches(surface.Kind, mode)) { mesh.Material = surface.Normal; continue; }
            int zone = mode == WorldHighlightMode.Zones ? surface.Zone : -1;
            if (!highlightMaterials.TryGetValue((surface.Normal, mode, zone), out var material))
            {
                material = PreviewMaterials.Create(surface.Horizon);
                float alpha = surface.Normal.DiffuseColor.Alpha;
                var (r, g, b) = WorldSurfaceHighlights.ZoneColor(surface.Zone);
                material.DiffuseColor = mode switch
                {
                    WorldHighlightMode.NonDefaultSoils => new Color4(1, 1, 0, alpha),
                    WorldHighlightMode.CanModify => new Color4(0, 1, 0, alpha),
                    WorldHighlightMode.Zones => new Color4(r, g, b, alpha),
                    _ => new Color4(1, 0, 0, alpha)
                };
                material.EnableUnLit = true;
                // White RGB removes authored texture colors while keeping alpha-zero holes.
                // These maps deliberately do not participate in the normal Textures toggle.
                material.DiffuseMap = surface.AlphaMask;
                highlightMaterials.Add((surface.Normal, mode, zone), material);
            }
            mesh.Material = material;
        }
    }

    private void ClearWorldHighlights()
    {
        surfaceAppearances.Clear(); highlightMaterials.Clear(); HighlightMode = WorldHighlightMode.None;
    }
}
