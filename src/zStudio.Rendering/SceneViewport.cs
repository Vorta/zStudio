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
    // Only immutable static parts enter this cache; animated/morphed geometry owns its bounds separately.
    private readonly Dictionary<MeshGeometry3D, (Vector3 Min, Vector3 Max)> staticMeshBounds = [];
    private readonly Dictionary<DiffuseMaterial, TextureModel> textureMaps = [];
    private readonly TextureMemoryBudget textureMemory = new();
    private PreviewTextureCache? previewTextures;
    private DefaultEffectsManager? effects;
    private SortingGroupModel3D? sceneAlphaGroup;
    private Vector3 sceneMin = new(float.PositiveInfinity), sceneMax = new(float.NegativeInfinity);
    private double minimumClipDistance = 0.001;
    private bool updatingClipping;
    private int generation;
    // One unit per retained surface instance or render object, including bounds.
    // Static instancing and animated meshes share this allowance for the visible context.
    internal long MaximumRenderUnits { get; set; } = 65_536;
    internal Func<Task>? StaticBatchYielded { get; set; }
    // Existing rendered fixture observes actual background scan work without a large or timed input.
    internal Action<long>? StaticBoundsMeasured { get; set; }
    private long staticRenderUnits;
    // Managed geometry working storage, separate from object count and decoded textures.
    // Pending workers retain their reservation until their dispatcher continuation exits.
    internal long MaximumGeometryBytes { get; set; } = 256L * 1024 * 1024;
    private readonly object geometryGate = new();
    private long geometryBytes;
    internal long RetainedGeometryBytes { get { lock (geometryGate) return geometryBytes; } }
    private GeometryReservation? staticGeometry;
    public sealed class RenderLimitException(string message) : IOException(message);
    private static void ReserveRenderUnits(ref long units, long count, long maximum)
    {
        if (count < 0 || count > maximum - units)
            throw new RenderLimitException($"The preview exceeds its {maximum:N0} surface-instance/render-object allowance; select a smaller model or scene.");
        units += count;
    }
    private sealed class GeometryReservation(SceneViewport owner) : IDisposable
    {
        private long bytes;
        private int users = 1;
        private bool disposed;
        internal void Resize(long next)
        {
            lock (owner.geometryGate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(GeometryReservation));
                long available = owner.MaximumGeometryBytes - (owner.geometryBytes - bytes);
                if (next < 0 || next > bytes && next > available)
                    throw new RenderLimitException($"The preview exceeds its {owner.MaximumGeometryBytes:N0}-byte managed geometry allowance; select a smaller model or scene.");
                owner.geometryBytes += next - bytes; bytes = next;
            }
        }
        internal IDisposable Retain() { lock (owner.geometryGate) { users++; return new Use(this); } }
        private void Release() { lock (owner.geometryGate) { if (--users == 0) { owner.geometryBytes -= bytes; bytes = 0; } } }
        public void Dispose() { lock (owner.geometryGate) { if (disposed) return; disposed = true; Release(); } }
        private sealed class Use(GeometryReservation reservation) : IDisposable
        {
            private GeometryReservation? value = reservation;
            public void Dispose() => Interlocked.Exchange(ref value, null)?.Release();
        }
    }
    // GeometryBuilder emits at most one vertex per polygon corner and n-2 triangles.
    // Its growing lists plus final arrays and triangulation scratch fit this conservative
    // pre-build bound. The retained arrays are counted exactly after construction.
    private static long GeometryBuildBytes(GameModel model, CancellationToken token, out long copyUpperBound)
    {
        long vertices = 0, triangles = 0, largest = 0;
        foreach (var polygon in model.Polygons)
        {
            token.ThrowIfCancellationRequested();
            int count = polygon.Vertices.Length;
            if (count < 3) continue;
            vertices += count; triangles += count - 2; largest = Math.Max(largest, count);
        }
        bool colors = model.Polygons.Any(p => p.Colors.Length != 0);
        // A morphed pose retains its current MeshPart (including picking/color
        // sources) beside the Helix copy, unlike immutable shared base parts.
        copyUpperBound = checked(vertices * (colors ? 116L : 68L) + triangles * 28 + model.Polygons.LongLength * 512 + 2048);
        return checked(4 * (vertices * (colors ? 52L : 36L) + triangles * 16 + model.Polygons.LongLength * 256 + 1024) + largest * 128);
    }
    private static long GeometryPartBytes(MeshPart part) => checked(256L + 12L * (part.Positions.LongLength + part.Normals.LongLength)
        + 8L * part.TextureCoordinates.LongLength + 4L * (part.Indices.LongLength + part.TrianglePolygons.LongLength + part.VertexPolygons.LongLength) + 16L * part.Colors.LongLength);
    private static long GeometryCopyBytes(MeshPart part) => checked(256L + 12L * (part.Positions.LongLength + part.Normals.LongLength)
        + 8L * part.TextureCoordinates.LongLength + 4L * part.Indices.LongLength + 32L * part.Colors.LongLength);
    private bool disposed;
    private System.Windows.Threading.DispatcherOperation? renderResize;
    public event Action<int>? NodeSelected;
    public event Action<string>? Information;
    public IReadOnlyList<Diagnostic> PreviewDiagnostics { get; private set; } = [];
    public string PreviewSummary { get; private set; } = "";
    public SceneViewport()
    {
        viewport = new FrameViewport(PrepareCameraFrame, QueueRenderSize)
        {
            Camera = new NavigationPerspectiveCamera { Position = new(10, 8, 15), LookDirection = new(-10, -8, -15), UpDirection = new(0, 1, 0), FarPlaneDistance = 100000, NearPlaneDistance = 0.1 },
            BackgroundColor = Color.FromRgb(26, 31, 38),
            ShowCoordinateSystem = true,
            ShowViewCube = true,
            EnableSwapChainRendering = false,
            IsInertiaEnabled = true,
            UseDefaultGestures = false,
            ZoomAroundMouseDownPoint = false,
            ZoomExtentsWhenLoaded = false
        };
        viewport.OITRenderMode = OITRenderType.None;
        viewport.EnableRenderOrder = true;
        viewport.CameraChanged += (_, _) => CameraChanged();
        ConfigurePickupInput(); ConfigureNavigation();
        viewport.RenderExceptionOccurred += (_, e) => { Information?.Invoke("3D preview unavailable: " + e.Exception.Message); e.Handled = true; };
        ConfigureInspection();
    }
    private void QueueRenderSize()
    {
        if (disposed || renderResize?.Status == System.Windows.Threading.DispatcherOperationStatus.Pending) return;
        // Helix defers the D3DImage resize at Background priority. A large scene's
        // continuous rendering can starve it, stretching the initial tiny buffer
        // across the viewport. Coalesce layout changes in the normal UI queue.
        renderResize = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, () =>
        {
            renderResize = null;
            if (disposed || !viewport.IsLoaded || !viewport.IsVisible || viewport.ActualWidth < 1 || viewport.ActualHeight < 1) return;
            try { viewport.RenderHost?.Resize((int)viewport.ActualWidth, (int)viewport.ActualHeight); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            { Information?.Invoke("3D preview resize unavailable: " + ex.Message); }
        });
    }
    public async Task ShowAsync(ZbdDocument doc, AssetRecord asset, AssetResolver resolver, string? pack, int lod, CancellationToken token, bool showBackdrop = false, MissionSceneContext? mission = null)
    {
        int current = ++generation;
        long maximumRenderUnits = MaximumRenderUnits;
        var boundsMeasured = StaticBoundsMeasured;
        if (asset.Kind == AssetKind.World) mission ??= await MissionSceneLoader.LoadAsync(doc, resolver, token: token);
        token.ThrowIfCancellationRequested(); if (current != generation) return;
        GameScene scene = (asset.Kind == AssetKind.World ? mission?.Scene : null) ?? doc.Scene ?? throw new InvalidDataException("No scene data.");
        var candidateTextures = new PreviewTextureCache(textureMemory, resolver.BeginTextureLookup(doc.Path, pack), asset.Kind == AssetKind.World);
        using var textureUse = candidateTextures.Retain();
        var candidateGeometry = new GeometryReservation(this);
        using var geometryUse = candidateGeometry.Retain();
        bool publishedTextures = false;
        try
        {
        ScenePacket packet = await Task.Run(async () =>
        {
            SceneView view = SceneBuilder.ForAsset(scene, asset, lod, token);
            long renderUnits = 0;
            HashSet<int> backdrop = []; Stack<int> pending = new(asset.Kind == AssetKind.World ? MissionSceneContext.FindHorizons(scene).Select(n => n.Root) : []);
            while (pending.TryPop(out int index)) { token.ThrowIfCancellationRequested(); if (index < 0 || index >= scene.Nodes.Count || !backdrop.Add(index)) continue; foreach (int child in SceneBuilder.Children(scene.Nodes[index])) pending.Push(child); }
            // Horizon geometry follows the active camera in the engine (Camera.c
            // SyncViewContextPositions). Its stored position obscures a map overview.
            if (asset.Kind == AssetKind.World && !showBackdrop)
            {
                view = view with { Placements = view.Placements.Where(p => !backdrop.Contains(p.NodeIndex)).ToArray() };
            }
            ReserveRenderUnits(ref renderUnits, view.Placements.Count, maximumRenderUnits);
            List<Diagnostic> notes = [.. view.Diagnostics, .. mission?.Diagnostics.Select(n => new Diagnostic("Warning", n)) ?? [], .. mission?.AiNetworks.Diagnostics ?? []]; Dictionary<int, IReadOnlyList<MeshPart>> geometry = [];
            Dictionary<int, (Vector3 Min, Vector3 Max)> localBounds = [];
            Dictionary<MeshPart, (Vector3 Min, Vector3 Max)> partBounds = [];
            long retainedGeometry = 0;
            foreach (int index in view.Placements.Select(p => p.ModelIndex).Distinct())
            {
                candidateGeometry.Resize(checked(retainedGeometry + GeometryBuildBytes(scene.Models[index], token, out _)));
                geometry[index] = GeometryBuilder.Build(scene.Models[index], notes, token);
                // Zones, flags and horizon groups share these immutable parts. Measure their
                // model-local bounds once here, never rescan their vertices on the dispatcher.
                Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
                long visited = 0;
                foreach (var part in geometry[index])
                {
                    Vector3 partMin = new(float.PositiveInfinity), partMax = new(float.NegativeInfinity);
                    foreach (var point in part.Positions)
                    {
                        if ((visited++ & 4095) == 0) token.ThrowIfCancellationRequested();
                        partMin = Vector3.Min(partMin, point); partMax = Vector3.Max(partMax, point);
                    }
                    partBounds.Add(part, (partMin, partMax));
                    min = Vector3.Min(min, partMin); max = Vector3.Max(max, partMax);
                }
                token.ThrowIfCancellationRequested();
                boundsMeasured?.Invoke(visited);
                localBounds.Add(index, (min, max));
                retainedGeometry = checked(retainedGeometry + geometry[index].Sum(GeometryPartBytes) + 128 + 256L * geometry[index].Count);
                candidateGeometry.Resize(retainedGeometry);
            }
            Dictionary<int, DecodedImage> textures = [];
            HashSet<int> alphaTextures = []; Dictionary<int, byte[]> masks = [];
            int textureNotes = 0;
            foreach (int index in geometry.Values.SelectMany(p => p).Select(p => p.MaterialIndex).Distinct())
            {
                if (index < 0 || index >= scene.Materials.Count) continue;
                int texture = scene.Materials[index].Int("texture_index", -1); if (texture < 0 || texture >= scene.Textures.Count) continue;
                if (textures.ContainsKey(texture)) continue;
                string name = scene.Textures[texture].Text("name");
                var resolved = await candidateTextures.GetAsync(name, token).ConfigureAwait(false);
                if (resolved != null)
                {
                    textures[texture] = resolved.Image;
                    if (resolved.Alpha) alphaTextures.Add(texture);
                    if (resolved.WhiteAlphaMask != null) masks[texture] = resolved.WhiteAlphaMask;
                    if (resolved.Ambiguous && textureNotes++ < 32) notes.Add(new("Warning", $"Multiple textures named {PreviewTextureCache.Label(name)}; using record {resolved.RecordIndex}."));
                }
                else if (textureNotes++ < 32) notes.Add(new("Warning", $"Missing texture: {PreviewTextureCache.Label(name)}"));
            }
            if (textureNotes > 32) notes.Add(new("Warning", $"{textureNotes - 32} additional texture notices omitted."));
            var groups = view.Placements.GroupBy(p => (Model: p.ModelIndex, Horizon: backdrop.Contains(p.NodeIndex),
                Kind: asset.Kind == AssetKind.World ? WorldSurfaceHighlights.NodeKind(scene, p.NodeIndex) : WorldSurfaceKind.Default,
                Zone: WorldSurfaceHighlights.NodeZone(scene, p.NodeIndex)))
                .OrderByDescending(g => g.Key.Horizon).Select(g => new SceneGroup(g.Key.Model, g.Key.Horizon, g.ToArray())).ToArray();
            // Static batches share one Helix geometry per part, even across transparent
            // placements. Colors grow from an enumerable; reserve its replacement scratch.
            long copies = geometry.Values.Sum(parts => parts.Sum(GeometryCopyBytes));
            long colorScratch = geometry.Values.SelectMany(parts => parts).Select(p => 32L * p.Colors.LongLength).DefaultIfEmpty().Max();
            candidateGeometry.Resize(checked(retainedGeometry + copies + colorScratch + 512L * groups.Length));
            renderUnits = 0;
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                foreach (var part in geometry[group.Model])
                {
                    JsonMaterial(scene, part.MaterialIndex, out var color, out int texture);
                    bool transparent = color.Alpha < 1 || alphaTextures.Contains(texture);
                    ReserveRenderUnits(ref renderUnits, group.Instances.LongLength + (transparent ? group.Instances.LongLength : 1), maximumRenderUnits);
                }
                if (!group.Horizon && geometry[group.Model].Any(part => part.Positions.Length != 0))
                    ReserveRenderUnits(ref renderUnits, group.Instances.LongLength + 1, maximumRenderUnits);
            }
            return new ScenePacket(view, geometry, localBounds, partBounds, textures, alphaTextures, masks, notes, groups, renderUnits);
        }, token);
        token.ThrowIfCancellationRequested(); if (current != generation) return;
        ClearMeshes(); AttachEffects(); QueueRenderSize();
        staticRenderUnits = packet.RenderUnits;
        staticGeometry = candidateGeometry;
        previewTextures = candidateTextures; publishedTextures = true;
        Mission = mission; PreviewScene = scene; InspectionSourcePath = doc.Path;
        if (asset.Kind == AssetKind.World) SetAiNetworks(mission?.AiNetworks ?? AiNetworkSnapshot.Empty);
        if (asset.Kind == AssetKind.World) ConfigureHorizon(scene);
        sceneMin = new(float.PositiveInfinity); sceneMax = new(float.NegativeInfinity);
        Dictionary<(int Material, bool Horizon, bool Colors), DiffuseMaterial> materials = [];
        Dictionary<MeshPart, MeshGeometry3D> geometries = [];
        Dictionary<int, TextureModel> alphaMasks = [];
        Dictionary<byte[], TextureModel> maskModels = [];
        Dictionary<DecodedImage, TextureModel> textureModels = [];
        foreach (var (index, bytes) in packet.AlphaMasks)
        {
            var image = packet.Textures[index];
            if (!maskModels.TryGetValue(bytes, out var mask)) maskModels[bytes] = mask = new TextureModel(bytes, SharpDX.DXGI.Format.R8G8B8A8_UNorm, image.Width, image.Height);
            alphaMasks[index] = mask;
        }
        int created = 0;
        foreach (var group in packet.Groups)
        {
            var instances = group.Instances;
            foreach (var part in packet.Geometry[group.Model])
            {
                token.ThrowIfCancellationRequested(); if (current != generation) return;
                if (!materials.TryGetValue((part.MaterialIndex, group.Horizon, part.Colors.Length != 0), out var material))
                {
                    JsonMaterial(scene, part.MaterialIndex, out Color4 color, out int textureIndex);
                    material = PreviewMaterials.Create(group.Horizon, part.Colors.Length != 0); material.DiffuseColor = color; material.EnableUnLit = true;
                    material.VertexColorBlendingFactor = part.Colors.Length == 0 ? 0 : 1;
                    if (packet.Textures.TryGetValue(textureIndex, out var image))
                    {
                        // Display decoded texture colors without additive preview lighting.
                        if (!textureModels.TryGetValue(image, out var map)) textureModels[image] = map = new TextureModel(image.Rgba, SharpDX.DXGI.Format.R8G8B8A8_UNorm, image.Width, image.Height);
                        material.DiffuseMap = map;
                        material.EnableUnLit = true;
                        textureMaps[material] = material.DiffuseMap;
                    }
                    materials[(part.MaterialIndex, group.Horizon, part.Colors.Length != 0)] = material;
                }
                if (!geometries.TryGetValue(part, out var geometry))
                {
                    geometries[part] = geometry = new() { Positions = new Vector3Collection(part.Positions), Normals = new Vector3Collection(part.Normals), TextureCoordinates = new Vector2Collection(part.TextureCoordinates), Indices = new IntCollection(part.Indices), Colors = VertexColors(part, material.DiffuseColor) };
                    staticMeshBounds.Add(geometry, packet.PartBounds[part]);
                }
                bool transparent = material.DiffuseColor.Alpha < 1 || part.MaterialIndex >= 0 && part.MaterialIndex < scene.Materials.Count && packet.AlphaTextures.Contains(scene.Materials[part.MaterialIndex].Int("texture_index", -1));
                // Sort translucent placements individually; a batch spanning a map
                // has no single correct distance relative to other alpha surfaces.
                IEnumerable<ScenePlacement[]> batches = transparent ? instances.Select(p => new[] { p }) : [instances];
                foreach (var batch in batches)
                {
                    token.ThrowIfCancellationRequested(); if (current != generation) return;
                    MeshGeometryModel3D mesh = new()
                    {
                        Geometry = geometry,
                        Material = material,
                        Instances = batch.Select(i => i.Transform).ToList(),
                        CullMode = CullMode.None,
                        IsThrowingShadow = false,
                        IsTransparent = transparent && !group.Horizon,
                        RenderOrder = group.Horizon ? 0 : 1,
                        IsDepthClipEnabled = !group.Horizon,
                    };
                    mesh.MouseDown3D += (_, e) =>
                    {
                        if (InspectionSelectionEnabled && !IsFlyActive && !IsPickupDragging && e is MouseDown3DEventArgs { OriginalInputEventArgs: MouseButtonEventArgs { ChangedButton: MouseButton.Left } } args && args.HitTestResult is { } hit && visiblePlacements.TryGetValue(mesh, out var found))
                        {
                            int at = hit.Tag is int instance ? instance : 0;
                            if (at >= 0 && at < found.Length && found[at].NodeIndex >= 0)
                            { SelectFramingNode(PickupAt(found[at].NodeIndex)?.Root ?? found[at].NodeIndex); NodeSelected?.Invoke(found[at].NodeIndex); }
                        }
                    };
                    meshes.Add(mesh); placements[mesh] = visiblePlacements[mesh] = batch;
                    RegisterInspectionMesh(mesh, part.MaterialIndex);
                    inspectionPolygons[mesh] = part.VertexPolygons;
                    if (asset.Kind == AssetKind.World)
                    {
                        JsonMaterial(scene, part.MaterialIndex, out _, out int texture);
                        surfaceAppearances[mesh] = new(material, WorldSurfaceHighlights.Classify(scene, batch[0].NodeIndex, part.MaterialIndex),
                            alphaMasks.GetValueOrDefault(texture), group.Horizon, WorldSurfaceHighlights.NodeZone(scene, batch[0].NodeIndex));
                    }
                    if (mesh.IsTransparent)
                    {
                        if (sceneAlphaGroup == null) { sceneAlphaGroup = new() { EnableSorting = true, SortTransparentOnly = true, SortingInterval = 0 }; viewport.Items.Add(sceneAlphaGroup); }
                        sceneAlphaGroup.Children.Add(mesh);
                    }
                    else viewport.Items.Add(mesh);
                    // A retained world may render continuously while its replacement
                    // is built. Background-priority continuations can starve behind
                    // that render queue indefinitely; resume in the normal UI queue.
                    if (++created % 30 == 0)
                    {
                        await Task.Yield();
                        if (StaticBatchYielded is { } yielded) await yielded();
                        token.ThrowIfCancellationRequested(); if (current != generation) return;
                    }
                }
            }
            token.ThrowIfCancellationRequested(); if (current != generation) return;
            if (group.Horizon) continue;
            var (min, max) = packet.LocalBounds[group.Model];
            if (float.IsFinite(min.X))
            {
                Vector3[] corners = [new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z), new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z)];
                foreach (var instance in instances) foreach (var corner in corners) { var world = Vector3.Transform(corner, instance.Transform); sceneMin = Vector3.Min(sceneMin, world); sceneMax = Vector3.Max(sceneMax, world); }
                LineGeometryModel3D box = new() { Geometry = new LineGeometry3D { Positions = new Vector3Collection(corners), Indices = new IntCollection([0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7]) }, Color = Colors.Gold, Thickness = 1, Instances = instances.Select(i => i.Transform).ToList(), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
                bounds.Add(box); boundsPlacements[box] = instances; viewport.Items.Add(box);
            }
        }
        token.ThrowIfCancellationRequested(); if (current != generation) return;
        if (asset.Kind == AssetKind.World) ConfigurePickups();
        FrameAll();
        RefreshHorizon();
        PreviewDiagnostics = packet.Notes.ToArray();
        PreviewSummary = $"{packet.View.Placements.Count:N0} instance{(packet.View.Placements.Count == 1 ? "" : "s")} · {meshes.Count:N0} mesh batch{(meshes.Count == 1 ? "" : "es")}";
        Information?.Invoke(PreviewSummary + $" · {packet.Notes.Count} preview notes" + (packet.Notes.Count > 0 ? "\n" + string.Join('\n', packet.Notes.Select(d => d.Message).Distinct().Take(30)) : ""));
        }
        catch
        {
            // A failed upload can already own materials. Remove those references
            // before releasing the cache; a newer publication owns its own cleanup.
            if (publishedTextures && current == generation && ReferenceEquals(previewTextures, candidateTextures)) Clear();
            throw;
        }
        finally { if (!publishedTextures) { candidateTextures.Dispose(); candidateGeometry.Dispose(); } }
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
        TryFrame("all", manual: false);
    }
    private void UpdateClipPlanes()
    {
        if (updatingClipping || !float.IsFinite(sceneMin.X) || !float.IsFinite(sceneMax.X) || viewport.Camera is not ProjectionCamera camera) return;
        var direction = camera.LookDirection;
        if (direction.LengthSquared <= 0 || !double.IsFinite(direction.LengthSquared)) return;
        direction.Normalize();
        double radius = Math.Max(1, (sceneMax - sceneMin).Length() / 2);
        minimumClipDistance = Math.Clamp(radius / 1000000, 0.001, 0.05);
        var range = new VisibleDepthRange(camera.Position, direction, camera.UpDirection, CaptureView().FieldOfView,
            Aspect, minimumClipDistance, (camera as OrthographicCamera)?.Width);
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
            if (element.Visibility != Visibility.Visible || !element.IsRendering || element is TopMostGroup3D or TransformManipulator3D or AiOverlayGroup) continue;
            if (element is MeshGeometryModel3D mesh && mesh.IsDepthClipEnabled) yield return mesh;
            else if (element is GroupModel3D group) foreach (var child in DepthMeshes(group.Children)) yield return child;
        }
    }
    /// <summary>Orbit zoom using standard mouse-wheel deltas. Captured Fly uses speed adjustment instead.</summary>
    public void ZoomAt(Point position, int wheelDelta)
    {
        ZoomBy(wheelDelta / 120.0, position);
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
        ++inspectionSerial;
        HashSet<int> included = [];
        if (node is int root)
        {
            Stack<int> pending = new([root]);
            while (pending.TryPop(out int current))
            {
                if (!included.Add(current) || PreviewScene == null || current < 0 || current >= PreviewScene.Nodes.Count) continue;
                foreach (int child in SceneBuilder.Children(PreviewScene.Nodes[current])) pending.Push(child);
            }
        }
        foreach (var mesh in meshes) { var shown = placements[mesh].Where(p => node == null || included.Contains(p.NodeIndex) || pickupRoots.GetValueOrDefault(p.NodeIndex, -1) == node).ToArray(); visiblePlacements[mesh] = shown; mesh.Instances = shown.Select(p => p.Transform).ToList(); mesh.Visibility = shown.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
        foreach (var box in bounds) box.Visibility = Visibility.Collapsed;
        RecalculateSceneBounds(); RefreshPickupSelection(); FrameAll();
    }
    private void RecalculateSceneBounds()
    {
        sceneMin = new(float.PositiveInfinity); sceneMax = new(float.NegativeInfinity);
        foreach (var mesh in meshes.Where(m => m.Visibility == Visibility.Visible && !placements[m].Any(p => IsHorizon(p.NodeIndex))))
        {
            if (mesh.Geometry is not MeshGeometry3D geometry || !staticMeshBounds.TryGetValue(geometry, out var local) || !float.IsFinite(local.Min.X)) continue;
            var (min, max) = local;
            foreach (var placement in visiblePlacements[mesh]) for (int corner = 0; corner < 8; corner++) { var p = Vector3.Transform(new((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z), placement.Transform); sceneMin = Vector3.Min(sceneMin, p); sceneMax = Vector3.Max(sceneMax, p); }
        }
    }
    private void ClearMeshes()
    {
        ClearInspection();
        CancelNavigation(); FramingSelection = null;
        orbitPivot = null; navigationReferenceDistance = null;
        SetFly(false); flySpeedInitialized = false;
        ClearPickupEditing();
        groundGrid?.Dispose(); groundGrid = null;
        rotationVelocity = default; rotationPoint = null; cameraPoseDirty = true; authoredCameraPose = false;
        ClearAnimationResources();
        ClearWorldHighlights();
        ClearAi();
        horizonNodes.Clear(); Mission = null; PreviewScene = null; InspectionNodes = null;
        sceneAlphaGroup = null;
        sceneMin = new(float.PositiveInfinity); sceneMax = new(float.NegativeInfinity);
        viewport.Items.Clear(); foreach (var mesh in meshes) mesh.Dispose(); foreach (var box in bounds) box.Dispose(); bounds.Clear(); meshes.Clear(); placements.Clear(); visiblePlacements.Clear(); staticMeshBounds.Clear(); textureMaps.Clear();
        staticRenderUnits = 0;
        staticGeometry?.Dispose(); staticGeometry = null;
        previewTextures?.Dispose(); previewTextures = null;
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
    private void AttachEffects() => PreviewResourceLifetime.Attach(ref effects, PreviewMaterials.CreateEffects, manager => viewport.EffectsManager = manager);
    public void Dispose() { if (disposed) return; disposed = true; renderResize?.Abort(); Clear(); AttachNavigationWindow(null); viewport.Dispose(); effects?.Dispose(); effects = null; GC.SuppressFinalize(this); }
    private sealed record SceneGroup(int Model, bool Horizon, ScenePlacement[] Instances);
    private sealed record ScenePacket(SceneView View, Dictionary<int, IReadOnlyList<MeshPart>> Geometry, Dictionary<int, (Vector3 Min, Vector3 Max)> LocalBounds, Dictionary<MeshPart, (Vector3 Min, Vector3 Max)> PartBounds, Dictionary<int, DecodedImage> Textures, HashSet<int> AlphaTextures, Dictionary<int, byte[]> AlphaMasks, List<Diagnostic> Notes, SceneGroup[] Groups, long RenderUnits);
}
