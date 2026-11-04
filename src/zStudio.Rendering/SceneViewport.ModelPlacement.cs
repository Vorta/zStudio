using System.Numerics;
using System.Windows;
using zStudio.Core;
using zStudio.Core.Sources;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private SortingGroupNode? modelGhost;
    private GeometryReservation? modelGhostGeometry;
    private PreviewTextureCache? modelGhostTextures;
    private readonly List<RenderTexture> modelGhostMaps = [];
    private Point? modelPlacementPoint;
    private bool modelPlacementFrozen;
    public Vector3? ModelPlacementPosition { get; private set; }
    public event Action? ModelPlacementChanged;
    public bool HasModelPlacement => modelGhost != null;

    /// <summary>Draw only the added root from the real candidate scene, independently of the map's batches.</summary>
    public async Task BeginModelPlacementAsync(SourceSceneSnapshot source, GameScene candidate, IReadOnlyList<int> roots, int lod, CancellationToken token)
    {
        ClearModelPlacement();
        int expected = generation;
        var reservation = new GeometryReservation(this);
        var textures = new PreviewTextureCache(textureMemory, source, false);
        using var textureUse = textures.Retain();
        var group = new SortingGroupNode { SortingEnabled = true, IsHitTestVisible = false, Visibility = Visibility.Hidden };
        try
        {
            var packet = await Task.Run(async () =>
            {
                var rows = roots.SelectMany(root => SceneBuilder.Assemble(candidate, lod, token: token, rootIndex: root).Placements);
                long bytes = 0, units = staticRenderUnits;
                List<(MeshPart Part, ScenePlacement Row)> parts = [];
                Dictionary<int, IReadOnlyList<MeshPart>> geometry = [];
                Dictionary<int, LoadedPreviewTexture> images = [];
                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    if (!geometry.TryGetValue(row.ModelIndex, out var built))
                    {
                        reservation.Resize(checked(bytes + GeometryBuildBytes(candidate.Models[row.ModelIndex], token, out _)));
                        built = GeometryBuilder.Build(candidate.Models[row.ModelIndex], [], token, respectBackFaces: true);
                        geometry.Add(row.ModelIndex, built);
                        bytes += built.Sum(p => GeometryPartBytes(p) + GeometryCopyBytes(p));
                        reservation.Resize(bytes);
                    }
                    foreach (var part in built)
                    {
                        ReserveRenderUnits(ref units, 2, MaximumRenderUnits);
                        parts.Add((part, row));
                        if (part.MaterialIndex < 0 || part.MaterialIndex >= candidate.Materials.Count) continue;
                        int image = SceneMaterialSemantics.EffectiveTextureIndex(candidate.Materials[part.MaterialIndex]);
                        if (image < 0 || image >= candidate.Textures.Count || images.ContainsKey(image)) continue;
                        if (await textures.GetAsync(candidate.Textures[image].Text("name"), token).ConfigureAwait(false) is { } decoded) images.Add(image, decoded);
                    }
                }
                return (parts, images);
            }, token);
            token.ThrowIfCancellationRequested();
            if (expected != generation || disposed) throw new OperationCanceledException("The view changed during placement preparation.");
            Dictionary<MeshPart, MeshData> geometryCache = [];
            Dictionary<int, RenderTexture> maps = [];
            foreach (var (part, row) in packet.parts)
            {
                token.ThrowIfCancellationRequested();
                JsonMaterial(candidate, part.MaterialIndex, out var color, out int image);
                var material = PreviewMaterials.Create(vertexTint: part.Colors.Length != 0);
                material.DiffuseColor = color; material.Unlit = true;
                material.VertexColorBlendingFactor = part.Colors.Length == 0 ? 0 : 1;
                PreviewMaterials.SetFade(material, .45f);
                if (packet.images.TryGetValue(image, out var decoded))
                {
                    if (!maps.TryGetValue(image, out var map)) { maps[image] = map = RenderTexture.FromRgba(decoded.Image.Rgba, decoded.Image.Width, decoded.Image.Height); modelGhostMaps.Add(map); }
                    material.DiffuseMap = map;
                }
                if (!geometryCache.TryGetValue(part, out var geometry)) geometryCache[part] = geometry = new(part.Positions, part.Indices, part.Normals, part.TextureCoordinates, VertexColors(part, color), part.TriangleBackFaces);
                group.Children.Add(new MeshNode { Geometry = geometry, Material = material, Instances = [row.Transform], IsTransparent = true,
                    IsHitTestVisible = false, CullMode = part.ShowBackFace ? CullMode.None : CullMode.Back, RenderOrder = 1 });
            }
            modelGhost = group; modelGhostGeometry = reservation; modelGhostTextures = textures;
            view.Items.Add(group); view.InvalidateRender();
        }
        catch { DisposeModelGhost(group); textures.Dispose(); reservation.Dispose(); throw; }
    }

    public void SetModelPlacementPoint(Point? point)
    {
        if (modelPlacementFrozen) return;
        modelPlacementPoint = point; RefreshModelPlacement();
    }
    /// <summary>Keep the accepted world point fixed while its source transaction is prepared, including camera motion.</summary>
    public void FreezeModelPlacement() => modelPlacementFrozen = true;
    private void RefreshModelPlacement()
    {
        if (modelGhost == null || modelPlacementFrozen) return;
        Vector3? position = modelPlacementPoint is { } point && !NavigationCubeBounds.Contains(point) && TryNavigationSurface(point, out var hit)
            && Math.Abs(hit.X) <= SourceWorlds.MaximumCoordinate && Math.Abs(hit.Y) <= SourceWorlds.MaximumCoordinate && Math.Abs(hit.Z) <= SourceWorlds.MaximumCoordinate
            ? new((float)hit.X, (float)hit.Y, (float)hit.Z) : null;
        if (position == ModelPlacementPosition) return;
        ModelPlacementPosition = position;
        modelGhost.Visibility = position == null ? Visibility.Hidden : Visibility.Visible;
        modelGhost.Transform = position is { } p ? Matrix4x4.CreateTranslation(p) : Matrix4x4.Identity;
        ModelPlacementChanged?.Invoke();
    }
    public void ClearModelPlacement()
    {
        if (modelGhost != null) { view.Items.Remove(modelGhost); DisposeModelGhost(modelGhost); }
        modelGhost = null; modelPlacementPoint = null; ModelPlacementPosition = null; modelPlacementFrozen = false;
        modelGhostTextures?.Dispose(); modelGhostTextures = null;
        modelGhostGeometry?.Dispose(); modelGhostGeometry = null;
    }
    private void DisposeModelGhost(SortingGroupNode group)
    {
        foreach (var mesh in group.Children) mesh.Dispose();
        group.Children.Clear(); group.Dispose();
        foreach (var map in modelGhostMaps) map.ReleaseDeviceResources();
        modelGhostMaps.Clear();
    }
}
