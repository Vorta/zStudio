using System.Numerics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using SharpDX.Direct3D11;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;
using Color = System.Windows.Media.Color;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;

namespace Recoil.Zbd.Rendering;

/// <summary>The only application component that exposes Helix/Direct3D types.</summary>
public sealed partial class SceneViewport : UserControl, IDisposable
{
    private readonly Viewport3DX viewport;
    private readonly List<MeshGeometryModel3D> meshes = [];
    private readonly List<LineGeometryModel3D> bounds = [];
    private readonly Dictionary<MeshGeometryModel3D, ScenePlacement[]> placements = [];
    private readonly Dictionary<MeshGeometryModel3D, ScenePlacement[]> visiblePlacements = [];
    private readonly Dictionary<DiffuseMaterial, TextureModel> textureMaps = [];
    private DefaultEffectsManager? effects;
    private SortingGroupModel3D? sceneAlphaGroup;
    private Vector3 sceneMin = new(float.PositiveInfinity), sceneMax = new(float.NegativeInfinity);
    private double minimumClipDistance = 0.001;
    private bool updatingClipping;
    private int generation;
    public event Action<int>? NodeSelected;
    public event Action<string>? Information;
    public IReadOnlyList<Diagnostic> PreviewDiagnostics { get; private set; } = [];
    public string PreviewSummary { get; private set; } = "";
    public SceneViewport()
    {
        viewport = new FrameViewport(PrepareCameraFrame)
        {
            Camera = new HCamera { Position = new(10, 8, 15), LookDirection = new(-10, -8, -15), UpDirection = new(0, 1, 0), FarPlaneDistance = 100000, NearPlaneDistance = 0.1 },
            BackgroundColor = Color.FromRgb(26, 31, 38),
            ShowCoordinateSystem = true,
            ShowViewCube = true,
            EnableSwapChainRendering = false,
            IsInertiaEnabled = true,
            ZoomAroundMouseDownPoint = true,
            ZoomExtentsWhenLoaded = false
        };
        viewport.OITRenderMode = OITRenderType.None;
        viewport.EnableRenderOrder = true;
        viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Pan, new MouseGesture(MouseAction.MiddleClick)));
        viewport.PreviewMouseWheel += (_, e) => { if (!IsPickupDragging) ZoomAt(e.GetPosition(viewport), e.Delta); e.Handled = true; };
        viewport.CameraChanged += (_, _) => CameraChanged();
        ConfigurePickupInput(); ConfigureUprightRotation();
        viewport.RenderExceptionOccurred += (_, e) => { Information?.Invoke("3D preview unavailable: " + e.Exception.Message); e.Handled = true; };
        Content = viewport;
    }
    public async Task ShowAsync(ZbdDocument doc, AssetRecord asset, AssetResolver resolver, string? pack, int lod, CancellationToken token, bool showBackdrop = false, MissionSceneContext? mission = null)
    {
        int current = ++generation;
        if (asset.Kind == AssetKind.World) mission ??= await MissionSceneLoader.LoadAsync(doc, resolver, token: token);
        GameScene scene = (asset.Kind == AssetKind.World ? mission?.Scene : null) ?? doc.Scene ?? throw new InvalidDataException("No scene data.");
        ScenePacket packet = await Task.Run(async () =>
        {
            SceneView view = SceneBuilder.ForAsset(scene, asset, lod, token);
            // Horizon geometry follows the active camera in the engine (Camera.c
            // SyncViewContextPositions). Its stored position obscures a map overview.
            if (asset.Kind == AssetKind.World && !showBackdrop)
            {
                HashSet<int> backdrop = []; Stack<int> pending = new(MissionSceneContext.FindHorizons(scene).Select(n => n.Root));
                while (pending.TryPop(out int index)) { if (index < 0 || index >= scene.Nodes.Count || !backdrop.Add(index)) continue; foreach (int child in SceneBuilder.Children(scene.Nodes[index])) pending.Push(child); }
                view = view with { Placements = view.Placements.Where(p => !backdrop.Contains(p.NodeIndex)).ToArray() };
            }
            List<Diagnostic> notes = [.. view.Diagnostics, .. mission?.Diagnostics.Select(n => new Diagnostic("Warning", n)) ?? []]; Dictionary<int, IReadOnlyList<MeshPart>> geometry = [];
            foreach (int index in view.Placements.Select(p => p.ModelIndex).Distinct()) geometry[index] = GeometryBuilder.Build(scene.Models[index], notes, token);
            Dictionary<int, DecodedImage> textures = [];
            foreach (int index in geometry.Values.SelectMany(p => p).Select(p => p.MaterialIndex).Distinct())
            {
                if (index < 0 || index >= scene.Materials.Count) continue;
                int texture = scene.Materials[index].Int("texture_index", -1); if (texture < 0 || texture >= scene.Textures.Count) continue;
                if (textures.ContainsKey(texture)) continue;
                string name = scene.Textures[texture].Text("name");
                var resolved = await resolver.ResolveTextureAsync(doc.Path, name, pack, token).ConfigureAwait(false);
                if (resolved != null) { textures[texture] = TextureDecoder.Decode(resolved.Document, resolved.Asset, token); if (resolved.Ambiguous) notes.Add(new("Warning", $"Multiple textures named {name}; using record {resolved.Asset.Index}.")); }
                else notes.Add(new("Warning", $"Missing texture: {name}"));
            }
            HashSet<int> alphaTextures = textures.Where(p => HasAlpha(p.Value)).Select(p => p.Key).ToHashSet();
            return new ScenePacket(view, geometry, textures, alphaTextures, notes);
        }, token);
        token.ThrowIfCancellationRequested(); if (current != generation) return;
        ClearMeshes(); effects ??= PreviewMaterials.CreateEffects(); viewport.EffectsManager = effects;
        Mission = mission; PreviewScene = scene;
        if (asset.Kind == AssetKind.World) ConfigureHorizon(scene);
        sceneMin = new(float.PositiveInfinity); sceneMax = new(float.NegativeInfinity);
        Dictionary<(int Material, bool Horizon), DiffuseMaterial> materials = [];
        int created = 0;
        foreach (var group in packet.View.Placements.GroupBy(p => (Model: p.ModelIndex, Horizon: IsHorizon(p.NodeIndex))).OrderByDescending(g => g.Key.Horizon))
        {
            var instances = group.ToArray();
            foreach (var part in packet.Geometry[group.Key.Model])
            {
                token.ThrowIfCancellationRequested(); if (current != generation) return;
                if (!materials.TryGetValue((part.MaterialIndex, group.Key.Horizon), out var material))
                {
                    JsonMaterial(scene, part.MaterialIndex, out Color4 color, out int textureIndex);
                    material = PreviewMaterials.Create(group.Key.Horizon); material.DiffuseColor = color; material.EnableUnLit = true;
                    if (packet.Textures.TryGetValue(textureIndex, out var image))
                    {
                        // Display decoded texture colors without additive preview lighting.
                        material.DiffuseMap = new TextureModel(image.Rgba, SharpDX.DXGI.Format.R8G8B8A8_UNorm, image.Width, image.Height);
                        material.EnableUnLit = true;
                        textureMaps[material] = material.DiffuseMap;
                    }
                    materials[(part.MaterialIndex, group.Key.Horizon)] = material;
                }
                MeshGeometry3D geometry = new() { Positions = new Vector3Collection(part.Positions), Normals = new Vector3Collection(part.Normals), TextureCoordinates = new Vector2Collection(part.TextureCoordinates), Indices = new IntCollection(part.Indices) };
                bool transparent = material.DiffuseColor.Alpha < 1 || part.MaterialIndex >= 0 && part.MaterialIndex < scene.Materials.Count && packet.AlphaTextures.Contains(scene.Materials[part.MaterialIndex].Int("texture_index", -1));
                // Sort translucent placements individually; a batch spanning a map
                // has no single correct distance relative to other alpha surfaces.
                IEnumerable<ScenePlacement[]> batches = transparent ? instances.Select(p => new[] { p }) : [instances];
                foreach (var batch in batches)
                {
                    MeshGeometryModel3D mesh = new()
                    {
                        Geometry = geometry,
                        Material = material,
                        Instances = batch.Select(i => i.Transform).ToList(),
                        CullMode = CullMode.None,
                        IsThrowingShadow = false,
                        IsTransparent = transparent && !group.Key.Horizon,
                        RenderOrder = group.Key.Horizon ? 0 : 1,
                        IsDepthClipEnabled = !group.Key.Horizon,
                    };
                    mesh.MouseDown3D += (_, e) =>
                    {
                        if (!IsFlyActive && !IsPickupDragging && e is MouseDown3DEventArgs { OriginalInputEventArgs: MouseButtonEventArgs { ChangedButton: MouseButton.Left } } args && args.HitTestResult is { } hit && visiblePlacements.TryGetValue(mesh, out var found))
                        {
                            int at = hit.Tag is int instance ? instance : 0;
                            if (at >= 0 && at < found.Length && found[at].NodeIndex >= 0) NodeSelected?.Invoke(found[at].NodeIndex);
                        }
                    };
                    meshes.Add(mesh); placements[mesh] = visiblePlacements[mesh] = batch;
                    if (mesh.IsTransparent)
                    {
                        if (sceneAlphaGroup == null) { sceneAlphaGroup = new() { EnableSorting = true, SortTransparentOnly = true, SortingInterval = 0 }; viewport.Items.Add(sceneAlphaGroup); }
                        sceneAlphaGroup.Children.Add(mesh);
                    }
                    else viewport.Items.Add(mesh);
                    if (++created % 30 == 0) await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            if (group.Key.Horizon) continue;
            var points = packet.Geometry[group.Key.Model].SelectMany(g => g.Positions).ToArray();
            if (points.Length > 0)
            {
                Vector3 min = points.Aggregate(Vector3.Min), max = points.Aggregate(Vector3.Max);
                Vector3[] corners = [new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z), new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z)];
                foreach (var instance in instances) foreach (var corner in corners) { var world = Vector3.Transform(corner, instance.Transform); sceneMin = Vector3.Min(sceneMin, world); sceneMax = Vector3.Max(sceneMax, world); }
                LineGeometryModel3D box = new() { Geometry = new LineGeometry3D { Positions = new Vector3Collection(corners), Indices = new IntCollection([0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7]) }, Color = Colors.Gold, Thickness = 1, Instances = instances.Select(i => i.Transform).ToList(), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
                bounds.Add(box); boundsPlacements[box] = instances; viewport.Items.Add(box);
            }
        }
        if (asset.Kind == AssetKind.World) ConfigurePickups();
        FrameAll();
        RefreshHorizon();
        PreviewDiagnostics = packet.Notes.ToArray();
        PreviewSummary = $"{packet.View.Placements.Count:N0} instance{(packet.View.Placements.Count == 1 ? "" : "s")} · {meshes.Count:N0} mesh batch{(meshes.Count == 1 ? "" : "es")}";
        Information?.Invoke(PreviewSummary + $" · {packet.Notes.Count} preview notes" + (packet.Notes.Count > 0 ? "\n" + string.Join('\n', packet.Notes.Select(d => d.Message).Distinct().Take(30)) : ""));
    }
    private static void JsonMaterial(GameScene scene, int index, out Color4 color, out int texture)
    {
        if (index < 0 || index >= scene.Materials.Count) { color = new Color4(0.75f, 0.75f, 0.75f, 1); texture = -1; return; }
        var material = scene.Materials[index]; var c = material["color"]; texture = material.Int("texture_index", -1);
        float alpha = Math.Clamp(material.Float("alpha", 255) / 255, 0, 1);
        color = new Color4(Math.Clamp(c.Float("r", 255) / 255, 0, 1), Math.Clamp(c.Float("g", 255) / 255, 0, 1), Math.Clamp(c.Float("b", 255) / 255, 0, 1), alpha);
        if (texture >= 0) color = new Color4(1, 1, 1, alpha);
    }
    public void FrameAll()
    {
        rotationVelocity = default;
        if (!float.IsFinite(sceneMin.X) || viewport.Camera is not HCamera camera) return;
        Vector3 center = (sceneMin + sceneMax) / 2; double radius = Math.Max(1, (sceneMax - sceneMin).Length() / 2);
        Vector3 direction = Vector3.Normalize(new Vector3(1, 1.6f, 1.5f)); double distance = radius * 2.6;
        camera.Position = new(center.X + direction.X * distance, center.Y + direction.Y * distance, center.Z + direction.Z * distance);
        camera.LookDirection = new(-direction.X * distance, -direction.Y * distance, -direction.Z * distance);
        camera.UpDirection = new(0, 1, 0);
        UpdateClipPlanes();
    }
    private void UpdateClipPlanes()
    {
        if (updatingClipping || !float.IsFinite(sceneMin.X) || !float.IsFinite(sceneMax.X) || viewport.Camera is not HCamera camera) return;
        var direction = camera.LookDirection;
        if (direction.LengthSquared <= 0 || !double.IsFinite(direction.LengthSquared)) return;
        direction.Normalize();
        double radius = Math.Max(1, (sceneMax - sceneMin).Length() / 2);
        minimumClipDistance = Math.Clamp(radius / 1000000, 0.001, 0.05);
        var range = new VisibleDepthRange(camera.Position, direction, camera.UpDirection, camera.FieldOfView,
            Math.Max(1, viewport.ActualWidth) / Math.Max(1, viewport.ActualHeight), minimumClipDistance);
        foreach (var mesh in DepthMeshes(viewport.Items))
        {
            if (mesh.Geometry is not MeshGeometry3D geometry) continue;
            var world = mesh.Transform?.Value ?? Matrix3D.Identity;
            if (mesh.Instances is { Count: > 0 } instances)
                foreach (var instance in instances) { var transform = ToWpf(instance); transform.Append(world); range.Include(geometry, transform); }
            else range.Include(geometry, world);
        }
        double closest = range.Near, farthest = range.Far;
        // Empty views still need a valid projection while navigating back to geometry.
        if (!double.IsFinite(closest) || !double.IsFinite(farthest))
        {
            closest = double.PositiveInfinity; farthest = double.NegativeInfinity;
            for (int corner = 0; corner < 8; corner++)
            {
                Point3D point = new((corner & 1) == 0 ? sceneMin.X : sceneMax.X,
                    (corner & 2) == 0 ? sceneMin.Y : sceneMax.Y, (corner & 4) == 0 ? sceneMin.Z : sceneMax.Z);
                double depth = Vector3D.DotProduct(point - camera.Position, direction);
                closest = Math.Min(closest, depth); farthest = Math.Max(farthest, depth);
            }
        }
        if (!double.IsFinite(closest) || !double.IsFinite(farthest)) return;
        // Navigation clearance is independent of the projection near plane.
        double near = Math.Max(minimumClipDistance, closest * 0.25);
        double padding = Math.Max(minimumClipDistance * 8, (farthest - closest) * 0.05);
        double far = Math.Max(near * 2, farthest + padding);
        updatingClipping = true;
        try
        {
            // Keep a valid projection even during the individual property notifications.
            if (near >= camera.FarPlaneDistance) camera.FarPlaneDistance = far;
            camera.NearPlaneDistance = near;
            camera.FarPlaneDistance = far;
        }
        finally { updatingClipping = false; }
    }
    private static IEnumerable<MeshGeometryModel3D> DepthMeshes(IEnumerable<Element3D> elements)
    {
        foreach (var element in elements)
        {
            if (element.Visibility != Visibility.Visible || !element.IsRendering || element is TopMostGroup3D or TransformManipulator3D) continue;
            if (element is MeshGeometryModel3D mesh && mesh.IsDepthClipEnabled) yield return mesh;
            else if (element is GroupModel3D group) foreach (var child in DepthMeshes(group.Children)) yield return child;
        }
    }
    /// <summary>Orbit zoom using standard mouse-wheel deltas. Captured Fly uses speed adjustment instead.</summary>
    public void ZoomAt(Point position, int wheelDelta)
    {
        if (IsFlyActive || wheelDelta == 0 || !viewport.IsZoomEnabled || viewport.Camera is not HCamera camera) return;
        var direction = camera.LookDirection;
        double distance = direction.Length;
        if (distance <= 0 || !double.IsFinite(distance)) return;
        direction.Normalize();
        Point3D origin = camera.Position + camera.LookDirection;
        bool hasSurface = viewport.FindNearest(new Vector2((float)position.X, (float)position.Y), out var hit, out _, out _);
        if (hasSurface)
        {
            origin = new(hit.X, hit.Y, hit.Z);
            distance = (origin - camera.Position).Length;
        }
        // Refresh the focus distance so an old orbit target above the map cannot stall zoom.
        distance = Math.Max(distance, minimumClipDistance * 4);
        camera.LookDirection = direction * distance;
        if (!hasSurface) origin = camera.Position + camera.LookDirection;
        viewport.AddZoomForce(-wheelDelta * 0.001, origin);
    }
    public void SetWireframe(bool enabled) { foreach (var mesh in meshes) mesh.FillMode = enabled ? FillMode.Wireframe : FillMode.Solid; }
    public void SetTextured(bool enabled)
    {
        foreach (var (material, texture) in textureMaps)
        {
            material.DiffuseMap = enabled ? texture : null;
            material.EnableUnLit = enabled;
        }
    }
    public void SetBounds(bool enabled) { foreach (var box in bounds) box.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed; }
    public void Isolate(int? node)
    {
        foreach (var mesh in meshes) { var shown = placements[mesh].Where(p => node == null || p.NodeIndex == node || pickupRoots.GetValueOrDefault(p.NodeIndex, -1) == node).ToArray(); visiblePlacements[mesh] = shown; mesh.Instances = shown.Select(p => p.Transform).ToList(); mesh.Visibility = shown.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
        foreach (var box in bounds) box.Visibility = Visibility.Collapsed;
        RecalculateSceneBounds(); RefreshPickupSelection(); FrameAll();
    }
    private void RecalculateSceneBounds()
    {
        sceneMin = new(float.PositiveInfinity); sceneMax = new(float.NegativeInfinity);
        foreach (var mesh in meshes.Where(m => m.Visibility == Visibility.Visible && !placements[m].Any(p => IsHorizon(p.NodeIndex))))
        {
            if (mesh.Geometry?.Positions is not { Count: > 0 } positions) continue; Vector3 min = positions.Aggregate(Vector3.Min), max = positions.Aggregate(Vector3.Max);
            foreach (var placement in visiblePlacements[mesh]) for (int corner = 0; corner < 8; corner++) { var p = Vector3.Transform(new((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z), placement.Transform); sceneMin = Vector3.Min(sceneMin, p); sceneMax = Vector3.Max(sceneMax, p); }
        }
    }
    private void ClearMeshes()
    {
        SetFly(false); flySpeedInitialized = false;
        ClearPickupEditing();
        groundGrid?.Dispose(); groundGrid = null;
        rotationVelocity = default; rotationPoint = null; cameraPoseDirty = true; authoredCameraPose = false;
        ClearAnimationResources();
        horizonNodes.Clear(); Mission = null; PreviewScene = null;
        sceneAlphaGroup = null;
        sceneMin = new(float.PositiveInfinity); sceneMax = new(float.NegativeInfinity);
        viewport.Items.Clear(); foreach (var mesh in meshes) mesh.Dispose(); foreach (var box in bounds) box.Dispose(); bounds.Clear(); meshes.Clear(); placements.Clear(); visiblePlacements.Clear(); textureMaps.Clear();
    }
    public void Clear() { generation++; ClearMeshes(); }
    public System.Windows.Media.Imaging.BitmapSource RenderImage(int width, int height, bool preserveAspect = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        // Helix's sized capture restores physical dimensions as logical pixels,
        // growing the back buffer on every call at non-100% DPI. Capture the live
        // size, then scale the image without changing the viewport or projection.
        var image = viewport.RenderBitmap() ?? throw new InvalidOperationException("The graphics device did not produce a frame.");
        double scaleX = (double)width / image.PixelWidth, scaleY = (double)height / image.PixelHeight;
        if (preserveAspect) scaleX = scaleY = Math.Min(scaleX, scaleY);
        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(image, new ScaleTransform(scaleX, scaleY));
        scaled.Freeze(); return scaled;
    }
    public void Dispose() { Clear(); viewport.Dispose(); effects?.Dispose(); effects = null; GC.SuppressFinalize(this); }
    private static bool HasAlpha(DecodedImage image) { for (int i = 3; i < image.Rgba.Length; i += 4) if (image.Rgba[i] != 255) return true; return false; }
    private sealed record ScenePacket(SceneView View, Dictionary<int, IReadOnlyList<MeshPart>> Geometry, Dictionary<int, DecodedImage> Textures, HashSet<int> AlphaTextures, List<Diagnostic> Notes);
}
