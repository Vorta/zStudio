using System.Numerics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using zStudio.Core;
using zStudio.Rendering.Direct3D;
using Color = System.Windows.Media.Color;

namespace zStudio.Rendering;

/// <summary>The 3D preview: the only application component that drives the renderer (zStudio.Rendering.Direct3D).</summary>
public sealed partial class SceneViewport : UserControl, IDisposable
{
    private readonly RenderView view;
    // The view's input surface: focus, capture, cursor and pointer positions.
    private readonly FrameworkElement viewport;
    private readonly List<MeshNode> meshes = [];
    private readonly FaceOrientation faceOrientation = new();
    private bool wireframe;
    public bool FaceOrientationEnabled => faceOrientation.Enabled;
    private readonly List<LineNode> bounds = [];
    private readonly Dictionary<MeshNode, ScenePlacement[]> placements = [];
    private readonly Dictionary<MeshNode, ScenePlacement[]> visiblePlacements = [];
    // Only immutable static parts enter this cache; animated/morphed geometry owns its bounds separately.
    private readonly Dictionary<MeshData, (Vector3 Min, Vector3 Max)> staticMeshBounds = [];
    private readonly Dictionary<PreviewMaterial, RenderTexture> textureMaps = [];
    private readonly TextureMemoryBudget textureMemory = new();
    private PreviewTextureCache? previewTextures;
    private RenderDevice? device;
    private SortingGroupNode? sceneAlphaGroup;
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
        // sources) beside the renderer's copy, unlike immutable shared base parts.
        // MeshPart: 36 bytes per vertex (+16 with colours), 16 per triangle. The renderer keeps only the tinted colour array
        // (+16 per vertex) and, once picked, its hierarchy (about 20 per triangle).
        copyUpperBound = checked(vertices * (colors ? 68L : 36L) + triangles * 16 + MeshBvh.RetainedBytes(triangles) + model.Polygons.LongLength * 512 + 2048);
        return checked(4 * (vertices * (colors ? 52L : 36L) + triangles * 16 + model.Polygons.LongLength * 256 + 1024) + largest * 128);
    }
    private static long GeometryPartBytes(MeshPart part) => checked(256L + 12L * (part.Positions.LongLength + part.Normals.LongLength)
        + 8L * part.TextureCoordinates.LongLength + 4L * (part.Indices.LongLength + part.TrianglePolygons.LongLength + part.VertexPolygons.LongLength) + 16L * part.Colors.LongLength);
    // The managed copy a renderer mesh keeps of a part: the tinted colour array and, once picked, a hierarchy over the
    // triangles (device buffers are not managed memory).
    private static long GeometryCopyBytes(MeshPart part) => checked(256L + 16L * part.Colors.LongLength + MeshBvh.RetainedBytes(part.Indices.LongLength / 3));
    // A replacement colour stream while the old one is still held: the new tinted array.
    private static long ColorReplacementBytes(MeshPart part) => 16L * part.Colors.LongLength;
    private bool disposed;
    public event Action<int>? NodeSelected;
    public event Action<string>? Information;
    /// <summary>A frame rendered again after a reported render or resize failure: the normal status can return.</summary>
    public event Action? PreviewRestored;
    /// <summary>A render or resize failure was reported (with its text, which <see cref="Information"/> also carries).</summary>
    public event Action<string>? PreviewFailed;
    /// <summary>The render or resize failure reported since the last good frame; null while the view renders.</summary>
    public string? RenderFailure { get; private set; }
    // The failure message last reported: retries that fail the same way (as a render or a resize) are not reported again
    // until a good frame or a load.
    private string? reportedFailureMessage;
    private bool reportNextFailure;
    public IReadOnlyList<Diagnostic> PreviewDiagnostics { get; private set; } = [];
    public string PreviewSummary { get; private set; } = "";
    public SceneViewport()
    {
        view = new RenderView
        {
            Camera = new PerspectiveRenderCamera { Position = new(10, 8, 15), LookDirection = new(-10, -8, -15), UpDirection = new(0, 1, 0), FarPlaneDistance = 100000, NearPlaneDistance = 0.1 },
            BackgroundColor = Color.FromRgb(26, 31, 38),
            FrameUpdate = PrepareCameraFrame,
        };
        viewport = view.Element;
        view.LayoutChanged += QueueRenderSize;
        view.CameraChanged += CameraChanged;
        ConfigurePickupInput(); ConfigureNavigation();
        view.RenderFailed += error => ReportRenderFailure("3D preview unavailable: ", error.Message);
        view.ResizeFailed += error => ReportRenderFailure("3D preview resize unavailable: ", error.Message);
        view.ObservePresentedFrames(RenderRestored);
        ConfigureInspection();
    }
    private void ReportRenderFailure(string prefix, string message)
    {
        if (message == reportedFailureMessage && !reportNextFailure) return;
        reportNextFailure = false; reportedFailureMessage = message;
        RenderFailure = prefix + message;
        Information?.Invoke(RenderFailure);
        PreviewFailed?.Invoke(RenderFailure);
    }
    private void RenderRestored()
    {
        if (RenderFailure == null) return;
        RenderFailure = null; reportedFailureMessage = null;
        PreviewRestored?.Invoke();
    }
    private void QueueRenderSize()
    {
        RebasePresentationPointers();
        if (!disposed) view.QueueResize();
    }
    public async Task ShowAsync(ISceneDocument doc, AssetRecord asset, AssetResolver resolver, string? pack, int lod, CancellationToken token, bool showBackdrop = false, MissionSceneContext? mission = null)
    {
        int current = ++generation;
        var nodeVisibility = NodeVisibility;
        long maximumRenderUnits = MaximumRenderUnits;
        var boundsMeasured = StaticBoundsMeasured;
        if (asset.Kind == AssetKind.World) mission ??= await MissionSceneLoader.LoadAsync(doc, resolver, token: token);
        token.ThrowIfCancellationRequested(); if (current != generation) return;
        GameScene scene = (asset.Kind == AssetKind.World ? mission?.Scene : null) ?? doc.Scene ?? throw new InvalidDataException("No scene data.");
        var candidateTextures = doc is zStudio.Core.Sources.SourceSceneSnapshot source
            ? new PreviewTextureCache(textureMemory, source, false)
            : new PreviewTextureCache(textureMemory, resolver.BeginTextureLookup(doc.Path, pack), false);
        using var textureUse = candidateTextures.Retain();
        var candidateGeometry = new GeometryReservation(this);
        using var geometryUse = candidateGeometry.Retain();
        bool publishedTextures = false;
        try
        {
        ScenePacket packet = await Task.Run(async () =>
        {
            SceneView view = SceneBuilder.ForAsset(scene, asset, lod, token, nodeVisibility);
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
                geometry[index] = GeometryBuilder.Build(scene.Models[index], notes, token, respectBackFaces: doc.Game == GameVariant.Recoil);
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
            HashSet<int> alphaTextures = [];
            int textureNotes = 0; HashSet<int> attemptedTextures = [];
            foreach (int index in geometry.Values.SelectMany(p => p).Select(p => p.MaterialIndex).Distinct())
            {
                if (index < 0 || index >= scene.Materials.Count) continue;
                int texture = scene.Materials[index].Int("texture_index", -1); if (texture < 0 || texture >= scene.Textures.Count) continue;
                if (!attemptedTextures.Add(texture)) continue;
                string name = scene.Textures[texture].Text("name");
                LoadedPreviewTexture? resolved;
                try { resolved = await candidateTextures.GetAsync(name, token).ConfigureAwait(false); }
                catch (InvalidDataException error) when (doc is zStudio.Core.Sources.SourceSceneSnapshot)
                {
                    if (textureNotes++ < 32) notes.Add(new("Warning", $"Texture {PreviewTextureCache.Label(name)}: {PreviewTextureCache.Label(error.Message)}"));
                    continue;
                }
                if (resolved != null)
                {
                    textures[texture] = resolved.Image;
                    if (resolved.Alpha) alphaTextures.Add(texture);
                    if (resolved.Ambiguous && textureNotes++ < 32) notes.Add(new("Warning", $"Multiple textures named {PreviewTextureCache.Label(name)}; using record {resolved.RecordIndex}."));
                }
                else if (textureNotes++ < 32) notes.Add(new("Warning", $"Missing texture: {PreviewTextureCache.Label(name)}"));
            }
            if (textureNotes > 32) notes.Add(new("Warning", $"{textureNotes - 32} additional texture notices omitted."));
            var groups = view.Placements.GroupBy(p => (Model: p.ModelIndex, Horizon: backdrop.Contains(p.NodeIndex),
                Kind: asset.Kind == AssetKind.World ? WorldSurfaceHighlights.NodeKind(scene, p.NodeIndex) : WorldSurfaceKind.Default,
                Zone: WorldSurfaceHighlights.NodeZone(scene, p.NodeIndex)))
                .OrderByDescending(g => g.Key.Horizon).Select(g => new SceneGroup(g.Key.Model, g.Key.Horizon, g.ToArray())).ToArray();
            // Static batches share one render geometry per part, even across transparent placements.
            long copies = geometry.Values.Sum(parts => parts.Sum(GeometryCopyBytes));
            // The transient is the picking hierarchy built for one part at a time.
            long pickingScratch = geometry.Values.SelectMany(parts => parts).Select(p => MeshBvh.BuildBytes(p.Indices.LongLength / 3)).DefaultIfEmpty().Max();
            candidateGeometry.Resize(checked(retainedGeometry + copies + pickingScratch + 512L * groups.Length));
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
            return new ScenePacket(view, geometry, localBounds, partBounds, textures, alphaTextures, notes, groups, renderUnits);
        }, token);
        token.ThrowIfCancellationRequested(); if (current != generation) return;
        ClearMeshes(); AttachEffects(); QueueRenderSize();
        staticRenderUnits = packet.RenderUnits;
        staticGeometry = candidateGeometry;
        previewTextures = candidateTextures; publishedTextures = true;
        Mission = mission; PreviewScene = scene; InspectionSourcePath = doc.Path; VisibleSceneNodes = packet.View.VisibleNodes;
        if (asset.Kind == AssetKind.World) SetAiNetworks(mission?.AiNetworks ?? AiNetworkSnapshot.Empty);
        if (asset.Kind == AssetKind.World) ConfigureHorizon(scene);
        sceneMin = new(float.PositiveInfinity); sceneMax = new(float.NegativeInfinity);
        Dictionary<(int Material, bool Horizon, bool Colors, WorldSurfaceKind Kind, int Zone), PreviewMaterial> materials = [];
        Dictionary<MeshPart, MeshData> geometries = [];
        Dictionary<DecodedImage, RenderTexture> textureModels = [];
        int created = 0;
        foreach (var group in packet.Groups)
        {
            var instances = group.Instances;
            foreach (var part in packet.Geometry[group.Model])
            {
                token.ThrowIfCancellationRequested(); if (current != generation) return;
                var kind = asset.Kind == AssetKind.World ? WorldSurfaceHighlights.Classify(scene, instances[0].NodeIndex, part.MaterialIndex) : WorldSurfaceKind.Default;
                int zone = asset.Kind == AssetKind.World ? WorldSurfaceHighlights.NodeZone(scene, instances[0].NodeIndex) : 0xFF;
                var materialKey = (part.MaterialIndex, group.Horizon, part.Colors.Length != 0, kind, zone);
                if (!materials.TryGetValue(materialKey, out var material))
                {
                    JsonMaterial(scene, part.MaterialIndex, out Color4 color, out int textureIndex);
                    material = PreviewMaterials.Create(group.Horizon, part.Colors.Length != 0, faceOrientation); material.DiffuseColor = color; material.Unlit = true;
                    material.VertexColorBlendingFactor = part.Colors.Length == 0 ? 0 : 1;
                    if (packet.Textures.TryGetValue(textureIndex, out var image))
                    {
                        // Display decoded texture colors without additive preview lighting.
                        if (!textureModels.TryGetValue(image, out var map)) textureModels[image] = map = RenderTexture.FromRgba(image.Rgba, image.Width, image.Height);
                        material.DiffuseMap = map;
                        material.Unlit = true;
                        textureMaps[material] = map;
                    }
                    materials[materialKey] = material;
                }
                if (!geometries.TryGetValue(part, out var geometry))
                {
                    geometries[part] = geometry = new(part.Positions, part.Indices, part.Normals, part.TextureCoordinates, VertexColors(part, material.DiffuseColor));
                    staticMeshBounds.Add(geometry, packet.PartBounds[part]);
                }
                bool transparent = material.DiffuseColor.Alpha < 1 || part.MaterialIndex >= 0 && part.MaterialIndex < scene.Materials.Count && packet.AlphaTextures.Contains(scene.Materials[part.MaterialIndex].Int("texture_index", -1));
                // Sort translucent placements individually; a batch spanning a map
                // has no single correct distance relative to other alpha surfaces.
                IEnumerable<ScenePlacement[]> batches = transparent ? instances.Select(p => new[] { p }) : [instances];
                foreach (var batch in batches)
                {
                    token.ThrowIfCancellationRequested(); if (current != generation) return;
                    MeshNode mesh = new()
                    {
                        Geometry = geometry,
                        Material = material,
                        Instances = batch.Select(i => i.Transform).ToList(),
                        CullMode = part.ShowBackFace ? CullMode.None : CullMode.Back,
                        IsTransparent = transparent && !group.Horizon,
                        RenderOrder = group.Horizon ? 0 : 1,
                        IsDepthClipEnabled = !group.Horizon,
                    };
                    // Clicks select through HandleScenePointerDown's inspection probe only, which
                    // excludes the horizon backdrop: clicking the sky selects nothing.
                    meshes.Add(mesh); placements[mesh] = visiblePlacements[mesh] = batch;
                    if (!group.Horizon) RegisterPolygonSource(geometry, part, scene.Models, group.Model);
                    RegisterInspectionMesh(mesh, part.MaterialIndex);
                    inspectionPolygons[mesh] = part.VertexPolygons;
                    if (asset.Kind == AssetKind.World)
                    {
                        surfaceAppearances[mesh] = new(material, kind, zone, group.Horizon);
                    }
                    if (mesh.IsTransparent)
                    {
                        if (sceneAlphaGroup == null) { sceneAlphaGroup = new() { SortingEnabled = true }; view.Items.Add(sceneAlphaGroup); }
                        sceneAlphaGroup.Children.Add(mesh);
                    }
                    else view.Items.Add(mesh);
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
                LineNode box = new() { Geometry = new LineData(corners, [0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7]), Color = Colors.Gold, Thickness = 1, Instances = instances.Select(i => i.Transform).ToList(), Visibility = Visibility.Collapsed, IsHitTestVisible = false, IsGuide = true };
                bounds.Add(box); boundsPlacements[box] = instances; view.Items.Add(box);
            }
        }
        token.ThrowIfCancellationRequested(); if (current != generation) return;
        if (asset.Kind == AssetKind.World) ConfigurePickups();
        if (wireframe) ShowPolygonEdges(true);
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
        if (updatingClipping || !float.IsFinite(sceneMin.X) || !float.IsFinite(sceneMax.X) || view.Camera is not RenderCamera camera) return;
        var direction = camera.LookDirection;
        if (direction.LengthSquared <= 0 || !double.IsFinite(direction.LengthSquared)) return;
        direction.Normalize();
        double radius = Math.Max(1, (sceneMax - sceneMin).Length() / 2);
        minimumClipDistance = Math.Clamp(radius / 1000000, 0.001, 0.05);
        var range = new VisibleDepthRange(camera.Position, direction, camera.UpDirection, CaptureView().FieldOfView,
            Aspect, minimumClipDistance, (camera as OrthographicRenderCamera)?.Width);
        foreach (var mesh in DepthMeshes(view.Items))
        {
            if (mesh.Geometry is not { Positions: var positions, Indices: var indices }) continue;
            var world = ToWpf(mesh.Transform ?? Matrix4x4.Identity);
            // Null instances draw once; empty instances draw nothing.
            if (mesh.Instances is { } instances)
                foreach (var instance in instances) { var transform = ToWpf(instance); transform.Append(world); range.Include(positions, indices, transform); }
            else range.Include(positions, indices, world);
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
    private IEnumerable<MeshNode> DepthMeshes(IEnumerable<Node> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Visibility != Visibility.Visible || !node.IsRendering || node.IsGuide && !view.GuidesVisible || node is TopMostGroupNode or TransformGizmo or AiOverlayGroup) continue;
            if (node is MeshNode mesh && mesh.IsDepthClipEnabled) yield return mesh;
            else if (node is GroupNode group) foreach (var child in DepthMeshes(group.Children)) yield return child;
        }
    }
    /// <summary>Orbit zoom using standard mouse-wheel deltas. Captured Fly uses speed adjustment instead.</summary>
    public void ZoomAt(Point position, int wheelDelta)
    {
        ZoomBy(wheelDelta / 120.0, position);
    }
    /// <summary>Outlines each polygon over the normal appearance (textures, overlays and face orientation stay).</summary>
    public void SetWireframe(bool enabled) { wireframe = enabled; ShowPolygonEdges(enabled); }
    public bool Wireframe => wireframe;
    /// <summary>How many meshes currently carry a polygon outline.</summary>
    public int PolygonOutlineCount => polygonEdges.Count;
    /// <summary>Temporary winding visualization; authored materials and preview choices remain intact.</summary>
    public void SetFaceOrientation(bool enabled)
    {
        faceOrientation.Enabled = enabled;
        ++inspectionSerial; inspectionStamp = null;
        view.InvalidateRender();
    }
    private bool texturesEnabled = true;
    public void SetTextured(bool enabled)
    {
        texturesEnabled = enabled;
        foreach (var (material, texture) in textureMaps)
        {
            material.DiffuseMap = enabled ? texture : null;
            material.Unlit = enabled;
        }
        SyncZoneGhosts();
        if (worldAnimation != null && animationFrame != null) UpdateAnimationFrame(animationFrame, false, animationEffectLighting);
    }
    private bool boundsEnabled;
    public void SetBounds(bool enabled) { boundsEnabled = enabled; RefreshBoundsBoxes(); }
    /// <summary>Each box outlines the instances of its model the view shows: an isolated or hidden object's box goes with it.</summary>
    private void RefreshBoundsBoxes()
    {
        foreach (var box in bounds)
        {
            var shown = (boundsPlacements.GetValueOrDefault(box) ?? []).Where(IsShownPlacement).ToArray();
            box.Instances = shown.Select(p => AdjustPickupPlacement(p).Transform).ToList();
            box.Visibility = boundsEnabled && shown.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        // Outlined objects follow what the view shows too.
        if (OutlinedNodes.Count > 0) RefreshOutlines();
    }
    private int? isolatedRoot;
    private int[] isolatedMembers = [];
    private int[] hiddenRoots = [];
    /// <summary>The objects hidden in the view (signed selection owners or node indices), drawn again by <see cref="Isolate"/>(null).</summary>
    public IReadOnlyList<int> HiddenNodes => hiddenRoots;
    /// <summary>
    /// Shows only <paramref name="node"/> and its descendants (less what is hidden), or everything not hidden again for null;
    /// <paramref name="members"/> are shown with it (the objects a source group holds, which owns no node of its own).
    /// </summary>
    public void Isolate(int? node, IReadOnlyCollection<int>? members = null)
    {
        isolatedRoot = node; isolatedMembers = node == null || members == null ? [] : [.. members];
        ApplyNodeVisibility();
    }
    /// <summary>Hides these objects (and their descendants) in the view only; an empty list shows them again.</summary>
    public void SetHiddenNodes(IEnumerable<int> nodes)
    {
        var next = nodes.Distinct().ToArray();
        if (next.SequenceEqual(hiddenRoots)) return;
        hiddenRoots = next;
        ApplyNodeVisibility();
    }
    private HashSet<int> Subtrees(IReadOnlyCollection<int> roots)
    {
        HashSet<int> included = [];
        if (roots.Count == 0) return included;
        // Signed selection owners stand for every node they own: one pass over the scene finds them all.
        HashSet<int> owners = roots.Where(r => r < 0).ToHashSet();
        IEnumerable<int> owned = owners.Count == 0 || PreviewScene == null ? [] : PreviewScene.Nodes.Where(n => owners.Contains(SelectionNode(n.Index))).Select(n => n.Index);
        Stack<int> pending = new(roots.Where(r => r >= 0).Concat(owned));
        while (pending.TryPop(out int current))
        {
            if (!included.Add(current) || PreviewScene == null || current < 0 || current >= PreviewScene.Nodes.Count) continue;
            foreach (int child in SceneBuilder.Children(PreviewScene.Nodes[current])) pending.Push(child);
        }
        return included;
    }
    private HashSet<int> isolatedNodes = [], hiddenNodes = [];
    private bool IsShownPlacement(ScenePlacement p) => !replacedNodes.Contains(p.NodeIndex) && IsShownNode(p.NodeIndex);
    public bool IsNodeShown(int node) => IsShownNode(node);
    private bool IsShownNode(int node) => (isolatedRoot == null || isolatedNodes.Contains(node) || pickupRoots.TryGetValue(node, out int pickup) && (pickup == isolatedRoot || isolatedNodes.Contains(pickup)))
        && !hiddenNodes.Contains(node) && !hiddenNodes.Contains(pickupRoots.GetValueOrDefault(node, -1));
    private void ApplyNodeVisibility()
    {
        ++inspectionSerial;
        isolatedNodes = isolatedRoot is int root ? Subtrees([root, .. isolatedMembers]) : [];
        hiddenNodes = Subtrees(hiddenRoots);
        foreach (var mesh in meshes) { var shown = placements[mesh].Where(IsShownPlacement).ToArray(); visiblePlacements[mesh] = shown; mesh.Instances = shown.Select(p => p.Transform).ToList(); mesh.Visibility = shown.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
        RefreshBoundsBoxes();
        // The camera stays where it is; Frame all is a separate, explicit command. The horizon follows it again next frame.
        RecalculateSceneBounds(); RefreshPickupSelection(); RefreshZoneVisibility();
        if (worldAnimation != null && animationFrame != null)
        { levelMin = sceneMin; levelMax = sceneMax; UpdateAnimationFrame(animationFrame, false, animationEffectLighting); }
        cameraPoseDirty = true; view.InvalidateRender();
    }
    private void RecalculateSceneBounds()
    {
        sceneMin = new(float.PositiveInfinity); sceneMax = new(float.NegativeInfinity);
        foreach (var mesh in meshes.Where(m => m.Visibility == Visibility.Visible && !placements[m].Any(p => IsHorizon(p.NodeIndex))))
        {
            if (mesh.Geometry is not { } geometry || !staticMeshBounds.TryGetValue(geometry, out var local) || !float.IsFinite(local.Min.X)) continue;
            var (min, max) = local;
            foreach (var placement in visiblePlacements[mesh]) for (int corner = 0; corner < 8; corner++) { var p = Vector3.Transform(new((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z), placement.Transform); sceneMin = Vector3.Min(sceneMin, p); sceneMax = Vector3.Max(sceneMax, p); }
        }
    }
    private void ClearMeshes()
    {
        ++worldSceneGeneration;
        DiscardWorldAnimation();
        TerrainBrushActive = false; TerrainBrushWaiting = false;
        ClearInspection();
        CancelNavigation(); FramingSelection = null;
        orbitPivot = null; navigationReferenceDistance = null;
        SetFly(false); flySpeedInitialized = false;
        ClearPickupEditing();
        groundGrid?.Dispose(); groundGrid = null;
        rotationVelocity = default; rotationPoint = null; cameraPoseDirty = true; authoredCameraPose = false;
        ClearAnimationResources();
        ClearWorldHighlights(); ClearPolygonOverlays();
        ClearAi(); ClearParachutes(); ClearAnimationPaths(); ClearOutlines();
        isolatedRoot = null; isolatedMembers = []; hiddenRoots = []; isolatedNodes = []; hiddenNodes = [];
        horizonNodes.Clear(); Mission = null; PreviewScene = null; InspectionNodes = null; VisibleSceneNodes = new HashSet<int>();
        objectPreviewMeshes = null; objectPreviewBounds = null; objectPreviewOriginals = null; ObjectTransformPreviewNode = null; objectPreviewToken = 0;
        sceneAlphaGroup = null;
        sceneMin = new(float.PositiveInfinity); sceneMax = new(float.NegativeInfinity);
        view.Items.Clear(); foreach (var mesh in meshes) mesh.Dispose(); foreach (var box in bounds) box.Dispose(); bounds.Clear(); meshes.Clear(); placements.Clear(); visiblePlacements.Clear(); staticMeshBounds.Clear();
        // Untextured materials (Textures off) no longer hold their maps: release each upload here.
        foreach (var texture in textureMaps.Values) texture.ReleaseDeviceResources();
        textureMaps.Clear();
        DisposeFixtures();
        staticRenderUnits = 0;
        staticGeometry?.Dispose(); staticGeometry = null;
        previewTextures?.Dispose(); previewTextures = null;
    }
    public void Clear() { generation++; ClearMeshes(); }
    public System.Windows.Media.Imaging.BitmapSource RenderImage(int width, int height, bool preserveAspect = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        // Capture at the live size, then scale the image: resizing the back buffer for a capture would change the viewport,
        // the projection and, at non-100% DPI, its own size.
        var image = view.RenderBitmap() ?? throw new InvalidOperationException($"The graphics device did not produce a frame ({view.CaptureRefusal ?? "no frame was read"}).");
        double scaleX = (double)width / image.PixelWidth, scaleY = (double)height / image.PixelHeight;
        if (preserveAspect) scaleX = scaleY = Math.Min(scaleX, scaleY);
        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(image, new ScaleTransform(scaleX, scaleY));
        scaled.Freeze(); return scaled;
    }
    // Loading a preview also retries a view whose device failed (same device, so attaching alone changes nothing); a failure
    // of that retry is reported again.
    private void AttachEffects()
    {
        PreviewResourceLifetime.Attach(ref device, RenderDevice.Create, created => view.Device = created);
        reportNextFailure = true; view.RetryRendering();
    }
    public void Dispose() { if (disposed) return; disposed = true; view.CancelResize(); Clear(); AttachNavigationWindow(null); view.Dispose(); device?.Dispose(); device = null; GC.SuppressFinalize(this); }
    private sealed record SceneGroup(int Model, bool Horizon, ScenePlacement[] Instances);
    private sealed record ScenePacket(SceneView View, Dictionary<int, IReadOnlyList<MeshPart>> Geometry, Dictionary<int, (Vector3 Min, Vector3 Max)> LocalBounds, Dictionary<MeshPart, (Vector3 Min, Vector3 Max)> PartBounds, Dictionary<int, DecodedImage> Textures, HashSet<int> AlphaTextures, List<Diagnostic> Notes, SceneGroup[] Groups, long RenderUnits);
}
