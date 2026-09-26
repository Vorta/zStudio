using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    private sealed record SurfaceAppearance(DiffuseMaterial Normal, WorldSurfaceKind Kind, TextureModel? AlphaMask, bool Horizon);
    private readonly Dictionary<MeshGeometryModel3D, SurfaceAppearance> surfaceAppearances = [];
    private readonly Dictionary<(DiffuseMaterial Normal, WorldHighlightMode Mode), DiffuseMaterial> highlightMaterials = [];
    public WorldHighlightMode HighlightMode { get; private set; }

    /// <summary>Changes only appearance; mesh/instance identities, picking and camera stay intact.</summary>
    public void SetWorldHighlightMode(WorldHighlightMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        HighlightMode = mode;
        foreach (var (mesh, surface) in surfaceAppearances)
        {
            if (!WorldSurfaceHighlights.Matches(surface.Kind, mode)) { mesh.Material = surface.Normal; continue; }
            if (!highlightMaterials.TryGetValue((surface.Normal, mode), out var material))
            {
                material = PreviewMaterials.Create(surface.Horizon);
                float alpha = surface.Normal.DiffuseColor.Alpha;
                material.DiffuseColor = mode switch
                {
                    WorldHighlightMode.NonDefaultSoils => new Color4(1, 1, 0, alpha),
                    WorldHighlightMode.CanModify => new Color4(0, 1, 0, alpha),
                    _ => new Color4(1, 0, 0, alpha)
                };
                material.EnableUnLit = true;
                // White RGB removes authored texture colors while keeping alpha-zero holes.
                // These maps deliberately do not participate in the normal Textures toggle.
                material.DiffuseMap = surface.AlphaMask;
                highlightMaterials.Add((surface.Normal, mode), material);
            }
            mesh.Material = material;
        }
    }

    private static byte[] WhiteAlphaMask(DecodedImage image, CancellationToken token)
    {
        var mask = new byte[image.Rgba.Length];
        for (int i = 0; i < mask.Length; i += 4)
        {
            if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
            mask[i] = mask[i + 1] = mask[i + 2] = 255;
            mask[i + 3] = image.Rgba[i + 3];
        }
        return mask;
    }

    private void ClearWorldHighlights()
    {
        surfaceAppearances.Clear(); highlightMaterials.Clear(); HighlightMode = WorldHighlightMode.None;
    }
}
