using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using zStudio.Core;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

// The renderer seam for the Desktop tests and PreviewCheck. Only WPF, System.Numerics and
// Core types cross it, so its callers never depend on the renderer's own types.
public sealed partial class SceneViewport
{
    /// <summary>What a rendered mesh is: a static scene batch, an animation pose's mesh, or an overlay or helper.</summary>
    internal enum RenderMeshRole { Scene, Animation, Overlay }

    // Only SceneViewport can name this interface, so tests and tools cannot reach the render node behind a handle.
    private interface IRenderNodeAccess { GeometryNode Node { get; } }
    private static GeometryNode RenderNode(RenderMeshHandle handle) => ((IRenderNodeAccess)handle).Node;

    /// <summary>A live view of one rendered mesh or line node; two views are equal when they show the same node.</summary>
    internal sealed class RenderMeshHandle : IEquatable<RenderMeshHandle>, IRenderNodeAccess
    {
        private readonly GeometryNode node;
        internal RenderMeshHandle(GeometryNode node, RenderMeshRole role) { this.node = node; Role = role; }
        GeometryNode IRenderNodeAccess.Node => node;
        public RenderMeshRole Role { get; }
        public bool IsLine => node is LineNode;
        public bool Visible { get => node.Visibility == Visibility.Visible; set => node.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
        public bool IsRendering { get => node.IsRendering; set => node.IsRendering = value; }
        public bool IsHitTestVisible { get => node.IsHitTestVisible; set => node.IsHitTestVisible = value; }
        public bool IsTransparent
        {
            get => node is MeshNode { IsTransparent: true };
            set { if (node is MeshNode mesh) mesh.IsTransparent = value; }
        }
        public bool IsDepthClipEnabled { get => node.IsDepthClipEnabled; set => node.IsDepthClipEnabled = value; }
        public int RenderOrder => node.RenderOrder;
        public IReadOnlyList<Matrix4x4>? Instances
        {
            get => node.Instances;
            set => node.Instances = value?.ToArray();
        }
        /// <summary>The node's transform (identity when it has none).</summary>
        public Matrix4x4? Transform
        {
            get => node.Transform ?? Matrix4x4.Identity;
            set => node.Transform = value;
        }
        private MeshData? MeshGeometry => (node as MeshNode)?.Geometry;
        /// <summary>Identity of the geometry object; unchanged while only its colours are replaced.</summary>
        public object? GeometryIdentity => node switch { MeshNode mesh => mesh.Geometry, LineNode line => line.Geometry, _ => null };
        /// <summary>The live position list (same object while the geometry keeps it).</summary>
        public IReadOnlyList<Vector3> Positions => node switch { MeshNode { Geometry: { } mesh } => mesh.Positions, LineNode { Geometry: { } line } => line.Positions, _ => [] };
        public IReadOnlyList<int> Indices => node switch { MeshNode { Geometry: { } mesh } => mesh.Indices, LineNode { Geometry: { } line } => line.Indices, _ => [] };
        /// <summary>The vertex colours as RGBA; the same view object while the geometry keeps the same colour list.</summary>
        public IReadOnlyList<Vector4>? Colors => MeshGeometry?.Colors is { } colors ? ColorView.Of(colors) : null;
        public object? MaterialIdentity => Material;
        private PreviewMaterial? Material => (node as MeshNode)?.Material;
        public Vector4 DiffuseColor => Material is { } material ? material.DiffuseColor.ToVector4() : default;
        public bool HasDiffuseMap => Material?.DiffuseMap != null;
        public bool MaterialUnlit
        {
            get => Material?.Unlit == true;
            set { if (Material is { } material) material.Unlit = value; }
        }
        /// <summary>The preview material's diagnostic overlay tint (alpha 0 when none).</summary>
        public Vector4 OverlayColor => Material is { } material ? PreviewMaterials.OverlayColor(material).ToVector4() : default;
        /// <summary>Replaces the material's texture with these RGBA8 pixels (fixtures only).</summary>
        public void SetTexture(byte[] rgba, int width, int height)
            => (Material ?? throw new InvalidOperationException("The mesh has no diffuse material.")).DiffuseMap = RenderTexture.FromRgba(rgba, width, height);
        public bool Equals(RenderMeshHandle? other) => other != null && ReferenceEquals(node, other.node);
        public override bool Equals(object? obj) => obj is RenderMeshHandle other && Equals(other);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(node);
    }

    private sealed class ColorView(Color4[] colors) : IReadOnlyList<Vector4>
    {
        private static readonly ConditionalWeakTable<Color4[], ColorView> views = new();
        internal static ColorView Of(Color4[] colors) => views.GetValue(colors, static c => new(c));
        public Vector4 this[int index] => colors[index].ToVector4();
        public int Count => colors.Length;
        public IEnumerator<Vector4> GetEnumerator() { foreach (var color in colors) yield return color.ToVector4(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A ground grid node as the tools check it.</summary>
    internal sealed class GroundGridHandle(GroundGridNode grid)
    {
        public double Offset => grid.Offset;
        public bool IsHitTestVisible => grid.IsHitTestVisible;
        public bool Visible { get => grid.Visibility == Visibility.Visible; set => grid.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    }

    /// <summary>The live projection camera: reads and writes go straight to it, as navigation does.</summary>
    internal sealed class DebugCameraAccess(SceneViewport owner)
    {
        private RenderCamera Camera => owner.view.Camera ?? throw new InvalidOperationException("No projection camera.");
        public Point3D Position { get => Camera.Position; set => Camera.Position = value; }
        public Vector3D LookDirection { get => Camera.LookDirection; set => Camera.LookDirection = value; }
        public Vector3D UpDirection { get => Camera.UpDirection; set => Camera.UpDirection = value; }
        public bool IsOrthographic => Camera is OrthographicRenderCamera;
        /// <summary>Vertical field of view in degrees (NaN for an orthographic camera).</summary>
        public double FieldOfView => Camera is PerspectiveRenderCamera perspective ? perspective.FieldOfView : double.NaN;
        public double NearPlaneDistance { get => Camera.NearPlaneDistance; set => Camera.NearPlaneDistance = value; }
        public double FarPlaneDistance { get => Camera.FarPlaneDistance; set => Camera.FarPlaneDistance = value; }
        /// <summary>The view matrix the renderer uses for this camera.</summary>
        public Matrix4x4 CreateViewMatrix() => Camera.CreateViewMatrix();
        /// <summary>The view matrix of a copy of the camera (the copy must keep the renderer's view construction).</summary>
        public Matrix4x4 CreateClonedViewMatrix() => Camera.Clone().CreateViewMatrix();
        public Matrix4x4 CreateProjectionMatrix(double aspect) => Camera.CreateProjectionMatrix(aspect);
    }

    /// <summary>One ray hit, nearest first: <see cref="Instance"/> is the hit instance (instanced meshes) or the triangle ordinal.</summary>
    internal sealed record SceneHit(RenderMeshHandle? Mesh, Vector3 Point, int? Instance);

    /// <summary>A test mesh added to this view. <see cref="Placements"/> registers it as a static scene batch.</summary>
    internal sealed record FixtureMesh(Vector3[] Positions, int[] Indices)
    {
        public Vector3[]? Normals { get; init; }
        public Vector2[]? TextureCoordinates { get; init; }
        public Matrix4x4[]? Instances { get; init; }
        public Matrix4x4? Transform { get; init; }
        /// <summary>Diffuse colour; null keeps the default material colour.</summary>
        public Vector4? Color { get; init; }
        public bool Unlit { get; init; }
        /// <summary>Starts from a copy of this mesh's material instead of a new one.</summary>
        public RenderMeshHandle? MaterialFrom { get; init; }
        public (byte[] Rgba, int Width, int Height)? Texture { get; init; }
        /// <summary>Registers the mesh as a static scene batch of these placements (bounds from <see cref="Positions"/>).</summary>
        public ScenePlacement[]? Placements { get; init; }
        /// <summary>Adds the mesh to the rendered scene; false keeps it as bookkeeping only.</summary>
        public bool Rendered { get; init; } = true;
    }

    private DebugCameraAccess? debugCamera;
    private readonly List<Node> fixtureNodes = [];

    internal DebugCameraAccess DebugCamera => debugCamera ??= new(this);

    /// <summary>The displayed back buffer, read without rendering, resizing or moving the camera.</summary>
    internal BitmapSource CapturePresentedFrame() => view.CapturePresented();
    /// <summary>Renders a frame and reads the view as WPF composes it right away (see <c>RenderView.RenderAndReadAsComposed</c>; 96 DPI).</summary>
    internal BitmapSource? RenderFrameReadAsComposed() => view.RenderAndReadAsComposed();
    /// <summary>Runs the view's reaction to WPF reporting its front buffer lost (false) or available again (true).</summary>
    internal void SimulateFrontBufferAvailability(bool available) => view.SimulateFrontBufferAvailability(available);
    /// <summary>Whether WPF reports the view's front buffer available (null before the view has a surface).</summary>
    internal bool? FrontBufferAvailable => view.FrontBufferAvailable;

    /// <summary>Applies <paramref name="change"/> and waits until the next frame is presented.</summary>
    internal async Task WaitForPresentedFrameAsync(Action change, TimeSpan? timeout = null)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var presented = view.ObservePresentedFrames(() => frame.TrySetResult());
        change();
        try { await frame.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10)); }
        catch (TimeoutException)
        {
            throw new TimeoutException($"No presented frame after the change: {view.RenderStatus}, Visible={viewport.IsVisible}, Loaded={viewport.IsLoaded}, WindowActive={Window.GetWindow(viewport)?.IsActive}.");
        }
    }

    /// <summary>The presented back buffer's size in physical pixels (0 × 0 before the first frame).</summary>
    internal (int Width, int Height) PresentedPixelSize => view.PresentedPixelSize;
    /// <summary>Physical pixels per DIP the renderer applies to its back buffer, lines and shader widths.</summary>
    internal double RenderDpiScale => view.DpiScale;
    /// <summary>A world point on the render surface, in DIPs.</summary>
    internal Point ProjectToView(Point3D point) => view.Project(point);
    internal Exception? RenderException => view.RenderException;
    /// <summary>Renders one frame now at the current size (unlike <see cref="CapturePresentedFrame"/>).</summary>
    internal BitmapSource RenderCurrentFrame() => view.RenderBitmap() ?? throw new InvalidOperationException($"The graphics device did not produce a frame ({view.CaptureRefusal ?? "no frame was read"}).");

    /// <summary>Every mesh node in the rendered scene, in submission order (lines excluded).</summary>
    internal IReadOnlyList<RenderMeshHandle> EnumerateRenderMeshes()
    {
        HashSet<MeshNode> scene = [.. meshes], animation = [.. animatedMeshes.Values.SelectMany(items => items).Select(item => item.Mesh)];
        List<RenderMeshHandle> result = [];
        void Add(MeshNode mesh) => result.Add(new(mesh, scene.Contains(mesh) ? RenderMeshRole.Scene : animation.Contains(mesh) ? RenderMeshRole.Animation : RenderMeshRole.Overlay));
        void Walk(IEnumerable<Node> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is MeshNode mesh) Add(mesh);
                else if (node is GroupNode group) Walk(group.Children);
                else if (node is TransformGizmo gizmo) foreach (var handle in gizmo.Handles) Add(handle);
            }
        }
        Walk(view.Items);
        return result;
    }

    /// <summary>Triangles the visible scene and animation meshes draw, counting every instance.</summary>
    internal int RenderedTriangleCount => EnumerateRenderMeshes().Where(m => m.Role != RenderMeshRole.Overlay && m.Visible)
        .Sum(m => m.Indices.Count / 3 * (m.Instances?.Count ?? 1));

    /// <summary>Ray hits against every hit-testable node of the rendered scene (horizon backdrop and overlays included), nearest first.</summary>
    internal IReadOnlyList<SceneHit> HitTestScene(Point3D origin, Vector3D direction)
    {
        if (!view.IsReady || direction.LengthSquared <= 0) return [];
        direction.Normalize();
        var hits = view.HitTest(new Vector3((float)origin.X, (float)origin.Y, (float)origin.Z), new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z));
        return hits.OrderBy(h => h.Distance).Select(h => new SceneHit(h.Node is GeometryNode node ? Handle(node) : null, h.Point, h.Tag)).ToArray();
    }

    /// <summary>Hits under a render-surface point (DIPs) along the current camera's ray from the eye, not from the near plane.</summary>
    internal IReadOnlyList<SceneHit> HitTestScene(Point point) => TryCameraRay(point, out var origin, out var direction, true) ? HitTestScene(origin, direction) : [];

    private RenderMeshHandle Handle(GeometryNode node) => new(node, node is MeshNode mesh && meshes.Contains(mesh) ? RenderMeshRole.Scene
        : animatedMeshes.Values.Any(items => items.Any(item => ReferenceEquals(item.Mesh, node))) ? RenderMeshRole.Animation : RenderMeshRole.Overlay);

    internal RenderMeshHandle AddFixtureMesh(FixtureMesh fixture)
    {
        // Fixtures use plain materials (the plain diffuse pass); a template's material is copied as a plain one.
        var material = fixture.MaterialFrom is { } template && RenderNode(template) is MeshNode { Material: { } source } ? source.ClonePlain() : PreviewMaterial.CreatePlain();
        if (fixture.Color is { } color) material.DiffuseColor = new Color4(color.X, color.Y, color.Z, color.W);
        if (fixture.Unlit) material.Unlit = true;
        if (fixture.Texture is { } texture) material.DiffuseMap = RenderTexture.FromRgba(texture.Rgba, texture.Width, texture.Height);
        MeshData? geometry = fixture.Positions.Length == 0 ? null : new(fixture.Positions, fixture.Indices, fixture.Normals, fixture.TextureCoordinates);
        var mesh = new MeshNode { Geometry = geometry, Material = material, CullMode = CullMode.None, Instances = fixture.Instances };
        if (fixture.Transform is { } transform) mesh.Transform = transform;
        if (fixture.Placements is { } placed)
        {
            meshes.Add(mesh); placements[mesh] = visiblePlacements[mesh] = placed;
            if (geometry != null) staticMeshBounds[geometry] = (fixture.Positions.Aggregate(Vector3.Min), fixture.Positions.Aggregate(Vector3.Max));
        }
        else fixtureNodes.Add(mesh);
        if (fixture.Rendered)
        {
            if (view.Device == null) AttachEffects();
            view.Items.Add(mesh);
        }
        return new(mesh, fixture.Placements != null ? RenderMeshRole.Scene : RenderMeshRole.Overlay);
    }

    /// <summary>Takes a fixture mesh out of the scene and every registration, and releases it.</summary>
    internal void RemoveFixtureMesh(RenderMeshHandle handle)
    {
        var node = RenderNode(handle);
        view.Items.Remove(node);
        if (node is MeshNode mesh)
        {
            meshes.Remove(mesh); placements.Remove(mesh); visiblePlacements.Remove(mesh);
            inspectionMeshes.Remove(mesh); inspectionPolygons.Remove(mesh);
            if (mesh.Geometry is { } geometry) staticMeshBounds.Remove(geometry);
        }
        fixtureNodes.Remove(node); node.Dispose();
    }

    /// <summary>Empties the rendered scene (registrations stay); fixtures added afterwards render alone.</summary>
    internal void ClearRenderItems() => view.Items.Clear();

    internal void RegisterFixtureInspection(RenderMeshHandle mesh, int material, long? runtime = null, int node = -1, int model = -1)
        => RegisterInspectionMesh((MeshNode)RenderNode(mesh), material, runtime, node, model);

    /// <summary>Shows only these placements of a fixture batch, as isolation does.</summary>
    internal void SetFixtureVisiblePlacements(RenderMeshHandle handle, ScenePlacement[] shown)
    {
        var mesh = (MeshNode)RenderNode(handle);
        visiblePlacements[mesh] = shown; mesh.Instances = shown.Select(p => p.Transform).ToArray();
    }

    /// <summary>Whether this instance of the mesh can supply an orbit pivot or a selection hit.</summary>
    internal bool OrbitSurfaceEligible(RenderMeshHandle mesh, int instance) => RenderNode(mesh) is MeshNode node && IsOrbitSurface(node, instance);

    /// <summary>Puts a pressed move handle along <paramref name="axisTransform"/>'s X axis (hit at <paramref name="hit"/>) and a fresh handle target, without input capture.</summary>
    internal void BeginFixtureHandleGesture(Matrix4x4 axisTransform, Vector3 hit)
    {
        var arrow = new MeshNode { Transform = axisTransform };
        fixtureNodes.Add(arrow);
        activePickupHandle = new(arrow, hit);
        pickupTarget = new GroupNode();
    }

    /// <summary>Where the transform handles are placed (their target's transform).</summary>
    internal Matrix4x4? TransformHandleTargetTransform => pickupTarget == null ? null : pickupTarget.Transform ?? Matrix4x4.Identity;
    /// <summary>The handles' world size scale (one unit of handle geometry).</summary>
    internal double? TransformHandleSizeScale => pickupManipulator?.SizeScale;

    internal RenderMeshHandle? SelectionBox => selectionBox is { } box ? new(box, RenderMeshRole.Overlay) : null;
    internal RenderMeshHandle? ParachuteLine => parachuteLine is { } line ? new(line, RenderMeshRole.Overlay) : null;
    internal IReadOnlyList<(RenderMeshHandle Box, ScenePlacement[] Placements)> BoundsBoxes => [.. boundsPlacements.Select(p => (new RenderMeshHandle(p.Key, RenderMeshRole.Overlay), p.Value))];
    internal IReadOnlyList<(RenderMeshHandle Mesh, ScenePlacement[] Placements)> StaticBatches => [.. placements.Select(p => (new RenderMeshHandle(p.Key, RenderMeshRole.Scene), p.Value))];
    /// <summary>Static scene batches built for the shown scene (none after Clear or a superseded or canceled load).</summary>
    internal int StaticBatchCount => meshes.Count;
    /// <summary>Bounds-box line nodes built for the shown scene.</summary>
    internal int BoundsBoxCount => bounds.Count;
    /// <summary>Cached model-local bounds of static geometry (animated and morphed geometry never enters it).</summary>
    internal int StaticBoundsCacheCount => staticMeshBounds.Count;
    internal IReadOnlyList<(RenderMeshHandle Mesh, int[] Polygons)> InspectionPolygonMaps => [.. inspectionPolygons.Select(p => (Handle(p.Key), p.Value))];
    internal int[]? InspectionPolygons(RenderMeshHandle mesh) => RenderNode(mesh) is MeshNode node ? inspectionPolygons.GetValueOrDefault(node) : null;
    internal IReadOnlyList<RenderMeshHandle> AiLinkMeshes => [.. aiLinks.Keys.Select(m => new RenderMeshHandle(m, RenderMeshRole.Overlay))];
    internal IReadOnlyList<GroundGridHandle> GroundGrids => [.. view.Items.OfType<GroundGridNode>().Select(g => new GroundGridHandle(g))];

    /// <summary>Shows or hides the view cube and the coordinate axes directly (fixtures keep pixels free of them).</summary>
    internal void SetNavigationAids(bool viewCube, bool axes) { view.ShowViewCube = viewCube; view.ShowCoordinateSystem = axes; }
    internal bool InertiaEnabled { get => navigationInertia; set => navigationInertia = value; }
    /// <summary>Orbit, pan and zoom input are enabled (Fly suspends them).</summary>
    internal bool OrbitControlsEnabled => panEnabled && zoomEnabled && rotationEnabled;
    /// <summary>Back-to-front sorting of transparent meshes (on by default; fixtures turn it off to force a draw order).</summary>
    internal void SetTransparentSorting(bool enabled) { foreach (var group in view.Items.OfType<SortingGroupNode>()) group.SortingEnabled = enabled; }
    /// <summary>The graphics interop's own exception type (first-chance evidence in tests).</summary>
    internal static bool IsGraphicsInteropException(Exception error) => RenderDevice.IsInteropException(error);

    private static Matrix4x4 ToNumerics(Matrix3D m) => new((float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14, (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
        (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34, (float)m.OffsetX, (float)m.OffsetY, (float)m.OffsetZ, (float)m.M44);
    private void DisposeFixtures() { foreach (var node in fixtureNodes) node.Dispose(); fixtureNodes.Clear(); }
}

/// <summary>Device-recovery guard on a software device, as the preview's graphics recovery uses it.</summary>
internal sealed class PreviewRecoveryHarness : IDisposable
{
    private readonly RenderDevice owned = RenderDevice.CreateSoftware();
    public void DisposeAllResources() => owned.DisposeAllResources();
    public void Reinitialize() => owned.Reinitialize();
    /// <summary>Raised after the device resources were reinitialized.</summary>
    public event EventHandler<EventArgs>? Reinitialized { add => owned.Reinitialized += value; remove => owned.Reinitialized -= value; }
    /// <summary>The current device's identity (null without one).</summary>
    public object? Device => owned.DeviceIdentity;
    public bool DeviceDisposed => owned.DeviceDisposed;
    public void Dispose() => owned.Dispose();
}
